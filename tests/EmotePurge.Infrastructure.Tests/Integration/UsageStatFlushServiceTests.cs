using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// FlushAsync writes through hand-written SQL (INSERT ... ON CONFLICT), so a real postgres:16-alpine
// container is the only thing that can verify it at all: the arbiter inference against the unique
// slot index — which carries an INCLUDE column — has no equivalent in any in-memory provider.
//
// Since M2 (plan #373, E9) a key names a slot, or — with the flag off, for a pair without a slot — is a
// pending key. Phase 1 writes the slot keys, phase 2 resolves the pending keys in its own transaction.
[Collection("Postgres")]
public class UsageStatFlushServiceTests(PostgresFixture fixture)
{
    // Most batches below use a single, arbitrary emote set id — the composite (SlotId, EmoteSetId)
    // key does not otherwise matter to them. OtherSetId is reserved for the tests that exercise two
    // different sets sharing a day (AK 11): the flush writes the set id with the row instead of summing
    // across it.
    private const string SetId = "set-1";
    private const string OtherSetId = "set-2";

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task FlushAsync_InsertsNewRow_ForFirstCountOfTheDay()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest1");

        await CreateService(db).FlushAsync(Batch((Key(slot), new(3, 0, 0))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(3, stat.UseCount);
        Assert.Equal(Today, stat.Date);
        Assert.Equal(slot, stat.SlotId);
    }

    [Fact]
    public async Task FlushAsync_InsertsNewRow_WithAllThreeValues()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest-allthree");

        await CreateService(db).FlushAsync(Batch((Key(slot), new(3, 1, 2))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(3, stat.UseCount);
        Assert.Equal(1, stat.BotUseCount);
        Assert.Equal(2, stat.SharedChatUseCount);
    }

    [Fact]
    public async Task FlushAsync_AddsToExistingRow_InsteadOfCreatingASecondOne()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest2");

        // Two flushes in the same UTC day must accumulate on one row — this is the ON CONFLICT
        // DO UPDATE path, and the assertion that the arbiter actually matched the unique index
        // rather than raising a duplicate-key error.
        await CreateService(db).FlushAsync(Batch((Key(slot), new(4, 0, 0))));
        await CreateService(db).FlushAsync(Batch((Key(slot), new(6, 0, 0))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(10, stat.UseCount);
    }

    [Fact]
    public async Task FlushAsync_AddsToExistingRow_AccumulatesBothColumnsSeparately()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest2b");

        // Same conflict path as above, but with both columns populated — the DO UPDATE must add
        // "UseCount" and "BotUseCount" independently, not cross-add or drop one of them.
        await CreateService(db).FlushAsync(Batch((Key(slot), new(4, 1, 0))));
        await CreateService(db).FlushAsync(Batch((Key(slot), new(6, 3, 0))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(10, stat.UseCount);
        Assert.Equal(4, stat.BotUseCount);
    }

    [Fact]
    public async Task FlushAsync_AddsToExistingRow_AccumulatesAllThreeColumnsSeparately()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest2c");

        // Same conflict path as above, now with all three columns populated.
        await CreateService(db).FlushAsync(Batch((Key(slot), new(4, 1, 2))));
        await CreateService(db).FlushAsync(Batch((Key(slot), new(6, 3, 5))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(10, stat.UseCount);
        Assert.Equal(4, stat.BotUseCount);
        Assert.Equal(7, stat.SharedChatUseCount);
    }

    [Fact]
    public async Task FlushAsync_WritesTwoRows_ForTwoSetIdsOfTheSameSlotOnTheSameDay()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest-twosets");

        // A mid-day set switch splits one slot's usage into two keys (spec section 5, rule 1). The
        // flush must write two rows, not sum them.
        await CreateService(db).FlushAsync(Batch(
            (new UsageCounterKey(slot, SetId), new(3, 0, 0)),
            (new UsageCounterKey(slot, OtherSetId), new(5, 0, 0))));

        var stats = await ReadStatsAsync(fixture, emote.Id);
        Assert.Equal(2, stats.Count);
        Assert.Equal(3, stats.Single(s => s.EmoteSetId == SetId).UseCount);
        Assert.Equal(5, stats.Single(s => s.EmoteSetId == OtherSetId).UseCount);
    }

    [Fact]
    public async Task FlushAsync_AddsToExistingRow_OnlyForTheMatchingSetId()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest-setconflict");

        // The ON CONFLICT target is (SlotId, EmoteSetId, Date): the second flush must add only to the
        // matching set's row and leave the other set's row of the same slot untouched.
        await CreateService(db).FlushAsync(Batch(
            (new UsageCounterKey(slot, SetId), new(4, 0, 0)),
            (new UsageCounterKey(slot, OtherSetId), new(9, 0, 0))));
        await CreateService(db).FlushAsync(Batch((new UsageCounterKey(slot, SetId), new(6, 0, 0))));

        var stats = await ReadStatsAsync(fixture, emote.Id);
        Assert.Equal(2, stats.Count);
        Assert.Equal(10, stats.Single(s => s.EmoteSetId == SetId).UseCount);
        Assert.Equal(9, stats.Single(s => s.EmoteSetId == OtherSetId).UseCount);
    }

    [Fact]
    public async Task FlushAsync_KeepsADeferredBatchsSetId_SeparateFromANewerFlush()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest-deferredset");

        // Models UsageFlushWorker requeuing a failed batch (spec section 5, rule 4): the key preserves
        // the set id the batch was counted under, so a batch that failed under the old set lands as a
        // separate row next to a newer flush under the current set.
        await CreateService(db).FlushAsync(Batch((new UsageCounterKey(slot, SetId), new(7, 0, 0))));
        await CreateService(db).FlushAsync(Batch((new UsageCounterKey(slot, OtherSetId), new(2, 0, 0))));

        var stats = await ReadStatsAsync(fixture, emote.Id);
        Assert.Equal(2, stats.Count);
        Assert.Equal(7, stats.Single(s => s.EmoteSetId == SetId).UseCount);
        Assert.Equal(2, stats.Single(s => s.EmoteSetId == OtherSetId).UseCount);
    }

    [Fact]
    public async Task FlushAsync_SkipsUnknownSlotIds_AndStillWritesTheRest()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest3");

        // A count buffered for a slot that no longer exists would violate the FK; it must not take the
        // rest of the batch down with it.
        var result = await CreateService(db).FlushAsync(Batch(
            (Key(slot), new(2, 0, 0)),
            (new UsageCounterKey(Guid.NewGuid().ToString(), SetId), new(99, 0, 0))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(2, stat.UseCount);
        Assert.Empty(result.Requeued);
    }

    [Fact]
    public async Task FlushAsync_WritesEverySlot_OfAMultiSlotBatch()
    {
        await using var db = fixture.CreateDbContext();
        var (first, firstSlot) = await SeedEmoteWithSlotAsync(db, "flushtest4");
        var (second, secondSlot) = await SeedEmoteWithSlotAsync(db, "flushtest5");

        // Guards the UNNEST array pairing: a mismatch between the id arrays and the count array would
        // silently attribute counts to the wrong emote.
        await CreateService(db).FlushAsync(Batch((Key(firstSlot), new(11, 0, 0)), (Key(secondSlot), new(22, 0, 0))));

        Assert.Equal(11, Assert.Single(await ReadStatsAsync(fixture, first.Id)).UseCount);
        Assert.Equal(22, Assert.Single(await ReadStatsAsync(fixture, second.Id)).UseCount);
    }

    [Fact]
    public async Task FlushAsync_PairsAllArraysCorrectly_AcrossMultipleSlots()
    {
        await using var db = fixture.CreateDbContext();
        var (first, firstSlot) = await SeedEmoteWithSlotAsync(db, "flushtest4b");
        var (second, secondSlot) = await SeedEmoteWithSlotAsync(db, "flushtest5b");

        // Six UNNEST arrays: a swap between any two would attribute counts to the wrong emote, slot or
        // column. All values are pairwise distinct, so any transposition shows up.
        await CreateService(db).FlushAsync(Batch((Key(firstSlot), new(11, 5, 2)), (Key(secondSlot), new(3, 22, 9))));

        var firstStat = Assert.Single(await ReadStatsAsync(fixture, first.Id));
        Assert.Equal((11, 5, 2, firstSlot), (firstStat.UseCount, firstStat.BotUseCount, firstStat.SharedChatUseCount, firstStat.SlotId));

        var secondStat = Assert.Single(await ReadStatsAsync(fixture, second.Id));
        Assert.Equal((3, 22, 9, secondSlot), (secondStat.UseCount, secondStat.BotUseCount, secondStat.SharedChatUseCount, secondStat.SlotId));
    }

    [Fact]
    public async Task FlushAsync_WritesBotOnlyRow_WithZeroHumanCount()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest-botonly");

        // E1: bot usage is preserved, not dropped, even when an emote had no human usage at all in
        // the batch — no "only write rows with Human > 0" filter exists.
        await CreateService(db).FlushAsync(Batch((Key(slot), new(0, 7, 0))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(0, stat.UseCount);
        Assert.Equal(7, stat.BotUseCount);
    }

    [Fact]
    public async Task FlushAsync_WritesSharedChatOnlyRow_WithZeroHumanAndBotCount()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, slot) = await SeedEmoteWithSlotAsync(db, "flushtest-sharedonly");

        // B6 negative probe (#73 design): a batch where an emote came only from a foreign room during
        // a Shared Chat session still gets a row.
        await CreateService(db).FlushAsync(Batch((Key(slot), new(0, 0, 6))));

        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal((0, 0, 6), (stat.UseCount, stat.BotUseCount, stat.SharedChatUseCount));
    }

    [Fact]
    public async Task FlushAsync_DoesNothing_ForEmptyBatch()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).FlushAsync(new Dictionary<UsageCounterKey, EmoteUsageCounts>());

        Assert.Empty(result.WrittenChannels);
        Assert.Empty(result.Requeued);
    }

    [Fact]
    public async Task FlushAsync_ReturnsEachAffectedChannelOnce()
    {
        await using var db = fixture.CreateDbContext();
        var (first, second) = await SeedTwoEmotesInOneChannelAsync(db, "flushchannels1");
        var firstSlot = await db.SeedUsageSlotAsync(first.Id, UsageSlotKind.Settled);
        var (elsewhere, elsewhereSlot) = await SeedEmoteWithSlotAsync(db, "flushchannels2");

        // The caller announces one live event per channel — two emotes of the same channel must not
        // produce two announcements, whichever phase wrote them.
        var result = await CreateService(db).FlushAsync(Batch(
            (Key(firstSlot), new(1, 0, 0)),
            (new UsageCounterKey(PendingSlotKey.For(second.Id), SetId), new(2, 0, 0)),
            (Key(elsewhereSlot), new(3, 0, 0))));

        Assert.Equal(["flushchannels1", "flushchannels2"], result.WrittenChannels.Order(StringComparer.Ordinal));
        Assert.Single(await ReadStatsAsync(fixture, elsewhere.Id));
    }

    [Fact]
    public async Task FlushAsync_ExcludesChannelsOfSkippedSlots()
    {
        await using var db = fixture.CreateDbContext();
        var (_, slot) = await SeedEmoteWithSlotAsync(db, "flushchannels3");

        // An id with no row has no channel to announce, and must not smuggle a null into the result.
        var result = await CreateService(db).FlushAsync(Batch(
            (Key(slot), new(5, 0, 0)),
            (new UsageCounterKey(Guid.NewGuid().ToString(), SetId), new(9, 0, 0))));

        Assert.Equal("flushchannels3", Assert.Single(result.WrittenChannels));
    }

    [Fact]
    public async Task FlushAsync_ReturnsEmpty_WhenEverySlotIsGone()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).FlushAsync(Batch(
            (new UsageCounterKey(Guid.NewGuid().ToString(), SetId), new(4, 0, 0)),
            (new UsageCounterKey(PendingSlotKey.For(Guid.NewGuid().ToString()), SetId), new(1, 0, 0))));

        Assert.Empty(result.WrittenChannels);
        Assert.Empty(result.Requeued);
    }

    // ------------------------------------------------------------------ spec AK 14, 16, 17

    // AK 14: two slots of one emote (ome44 / hob44) write two rows of the same day, with the same
    // EmoteId and each on its own slot; a second flush adds onto each. Two rows of one emote, set and
    // day violate nothing since M2.
    [Fact]
    public async Task FlushAsync_TwoSlotsOfOneEmote_WriteTwoRowsWithTheSameEmoteId_AndASecondFlushAdds()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, ome44) = await SeedEmoteWithSlotAsync(db, "flush-ak14");
        var hob44 = await db.SeedUsageSlotAsync(emote.Id, UsageSlotKind.Provisional);

        await CreateService(db).FlushAsync(Batch((Key(ome44), new(2, 0, 0)), (Key(hob44), new(1, 0, 0))));
        await CreateService(db).FlushAsync(Batch((Key(ome44), new(1, 0, 0)), (Key(hob44), new(4, 0, 0))));

        var stats = await ReadStatsAsync(fixture, emote.Id);
        Assert.Equal(2, stats.Count);
        Assert.All(stats, s => Assert.Equal((emote.Id, SetId, Today), (s.EmoteId, s.EmoteSetId, s.Date)));
        Assert.Equal(3, stats.Single(s => s.SlotId == ome44).UseCount);
        Assert.Equal(5, stats.Single(s => s.SlotId == hob44).UseCount);
    }

    // AK 16: a key of a slot deleted while its count was buffered is dropped with one warning line;
    // the rest of the batch is written and its channel is not announced.
    [Fact]
    public async Task FlushAsync_DropsAKeyOfADeletedSlot_WithAWarning_WritesTheRest_AndDoesNotAnnounceItsChannel()
    {
        await using var db = fixture.CreateDbContext();
        var (kept, keptSlot) = await SeedEmoteWithSlotAsync(db, "flush-ak16-kept");
        var (gone, goneSlot) = await SeedEmoteWithSlotAsync(db, "flush-ak16-gone");
        await db.EmoteSlots.Where(s => s.Id == goneSlot).ExecuteDeleteAsync();
        var logger = new RecordingLogger<UsageStatFlushService>();

        var result = await new UsageStatFlushService(db, logger).FlushAsync(Batch(
            (Key(keptSlot), new(3, 0, 0)),
            (Key(goneSlot), new(8, 0, 0))));

        Assert.Equal(3, Assert.Single(await ReadStatsAsync(fixture, kept.Id)).UseCount);
        Assert.Empty(await ReadStatsAsync(fixture, gone.Id));
        Assert.Equal("flush-ak16-kept", Assert.Single(result.WrittenChannels));
        Assert.Empty(result.Requeued);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 buffered usage counts dropped", warning.Message, StringComparison.Ordinal);
    }

    // AK 17 (flush half): after an in-place settle the slot that carried the seed's history is the
    // slot the map hands out, and the flush writes onto it; the seed rows of another set stay put.
    [Fact]
    public async Task FlushAsync_AfterAnInPlaceSettle_WritesOntoTheSlotCarryingTheSeedHistory_AndLeavesTheOtherSetsSeed()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flush-ak17");
        db.UsageStats.AddRange(
            new UsageStat { EmoteId = emote.Id, EmoteSetId = SetId, Date = new DateOnly(2026, 8, 1), UseCount = 5 },
            new UsageStat { EmoteId = emote.Id, EmoteSetId = OtherSetId, Date = new DateOnly(2026, 8, 1), UseCount = 9 });
        await db.AssignUsageSlotsAsync();
        await db.SaveChangesAsync();
        var seedA = (await ReadStatsAsync(fixture, emote.Id)).Single(s => s.EmoteSetId == SetId).SlotId;
        var seedB = (await ReadStatsAsync(fixture, emote.Id)).Single(s => s.EmoteSetId == OtherSetId).SlotId;
        // The settle of child 2, in place: same id, now authoritative and named after the oldest entry.
        await db.EmoteSlots.Where(s => s.Id == seedA).ExecuteUpdateAsync(set => set
            .SetProperty(s => s.SeedUnsettled, false)
            .SetProperty(s => s.Name, "ome44")
            .SetProperty(s => s.AddedAt, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc))
            .SetProperty(s => s.AddedAtFromRest, true));

        await CreateService(db).FlushAsync(Batch((new UsageCounterKey(seedA, SetId), new(4, 0, 0))));

        var stats = await ReadStatsAsync(fixture, emote.Id);
        Assert.Equal([5, 4], stats.Where(s => s.SlotId == seedA).OrderBy(s => s.Date).Select(s => s.UseCount));
        var otherSet = stats.Single(s => s.EmoteSetId == OtherSetId);
        Assert.Equal((seedB, 9), (otherSet.SlotId, otherSet.UseCount));
        Assert.Equal(2, await db.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
    }

    // ------------------------------------------------------------------ pending keys (flag off, E4/E5)

    [Fact]
    public async Task FlushAsync_PendingKeyWithoutAPairSlot_CreatesExactlyOneSeedCarryingExactlyThatRow()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flush-pending-new", archivedAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = DateTime.UtcNow.AddSeconds(-5);

        var result = await CreateService(db).FlushAsync(Batch((Pending(emote), new(3, 1, 2))));

        var seed = Assert.Single(await db.EmoteSlots.AsNoTracking().Where(s => s.EmoteId == emote.Id).ToListAsync());
        // M1 field rules: name and archive state from the emote, no date, unsettled.
        Assert.Equal(
            (emote.Name, true, emote.ArchivedAt, (DateTime?)null, false, true),
            (seed.Name, seed.IsArchived, seed.ArchivedAt, seed.AddedAt, seed.AddedAtFromRest, seed.SeedUnsettled));
        Assert.True(seed.LastSyncedAt >= before);
        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal((seed.Id, SetId, Today, 3, 1, 2), (stat.SlotId, stat.EmoteSetId, stat.Date, stat.UseCount, stat.BotUseCount, stat.SharedChatUseCount));
        Assert.Equal("flush-pending-new", Assert.Single(result.WrittenChannels));
        Assert.Empty(result.Requeued);
    }

    [Fact]
    public async Task FlushAsync_PendingKeyWhosePairGotASlotMeanwhile_WritesThereWithoutANewSeed()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flush-pending-existing");
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, EmoteSetId = SetId, Date = new DateOnly(2026, 8, 1), UseCount = 5 });
        await db.AssignUsageSlotsAsync();
        await db.SaveChangesAsync();
        var slot = Assert.Single(await ReadStatsAsync(fixture, emote.Id)).SlotId;

        await CreateService(db).FlushAsync(Batch((Pending(emote), new(2, 0, 0))));

        Assert.Equal(1, await db.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
        Assert.Equal([slot, slot], (await ReadStatsAsync(fixture, emote.Id)).Select(s => s.SlotId));
    }

    // E9 / E6a: the old slot key of a pair and its pending key in one batch (a refresh between two
    // messages turned the pair into a pending key; the old slot had no row yet). Phase 1 writes the old
    // key first, and phase 2's rule (a) finds it: one slot, one row, the sum.
    [Fact]
    public async Task FlushAsync_PendingKeyFindsTheRowPhaseOneJustWrote_OneSlotOneRowTheSum()
    {
        await using var db = fixture.CreateDbContext();
        var (emote, oldSlot) = await SeedEmoteWithSlotAsync(db, "flush-phase-order", UsageSlotKind.UnsettledSeed);

        await CreateService(db).FlushAsync(Batch((Key(oldSlot), new(2, 1, 0)), (Pending(emote), new(3, 0, 1))));

        Assert.Equal(1, await db.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
        var stat = Assert.Single(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal((oldSlot, 5, 1, 1), (stat.SlotId, stat.UseCount, stat.BotUseCount, stat.SharedChatUseCount));
    }

    [Fact]
    public async Task FlushAsync_PendingKeysOfTwoSetsOfOneEmote_CreateTwoSeeds_EachWithOnlyItsRow()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flush-pending-twosets");

        await CreateService(db).FlushAsync(Batch(
            (Pending(emote), new(3, 0, 0)),
            (Pending(emote, OtherSetId), new(7, 0, 0))));

        var stats = await ReadStatsAsync(fixture, emote.Id);
        Assert.Equal(2, stats.Count);
        Assert.NotEqual(stats[0].SlotId, stats[1].SlotId);
        Assert.Equal(2, await db.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id && s.SeedUnsettled));
    }

    // Rule (b): a row-less seed an enqueue of the same set bound is reused; a pending key of another set
    // gets a seed of its own.
    [Fact]
    public async Task FlushAsync_PendingKey_ReusesTheRowlessEnqueueSeedOfTheSameSet_NotOfAnotherSet()
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, "flush-pending-enqseed");
        var enqueueSeed = await db.SeedUsageSlotAsync(emote.Id);
        var run = await BackfillRunSeed.AddRunAsync(db, emote.ChannelId, ChatLogBackfillRunStatus.Completed);
        db.ChatLogBackfillRunEmotes.Add(new ChatLogBackfillRunEmote
        {
            RunId = run.Id,
            EmoteId = emote.Id,
            SevenTvEmoteId = emote.SevenTvEmoteId,
            Name = emote.Name,
            SlotId = enqueueSeed,
            CreatedSlot = true
        });
        await db.SaveChangesAsync();

        await CreateService(db).FlushAsync(Batch(
            (Pending(emote, run.EmoteSetId), new(3, 0, 0)),
            (Pending(emote, OtherSetId), new(4, 0, 0))));

        var stats = await ReadStatsAsync(fixture, emote.Id);
        Assert.Equal(enqueueSeed, stats.Single(s => s.EmoteSetId == run.EmoteSetId).SlotId);
        Assert.NotEqual(enqueueSeed, stats.Single(s => s.EmoteSetId == OtherSetId).SlotId);
        Assert.Equal(2, await db.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
    }

    [Fact]
    public async Task FlushAsync_PendingKeyOfADeletedEmote_IsDropped_NotRequeued()
    {
        await using var db = fixture.CreateDbContext();
        var (kept, keptSlot) = await SeedEmoteWithSlotAsync(db, "flush-pending-gone");
        var logger = new RecordingLogger<UsageStatFlushService>();

        var result = await new UsageStatFlushService(db, logger).FlushAsync(Batch(
            (Key(keptSlot), new(1, 0, 0)),
            (new UsageCounterKey(PendingSlotKey.For(Guid.NewGuid().ToString()), SetId), new(6, 0, 0))));

        Assert.Single(await ReadStatsAsync(fixture, kept.Id));
        Assert.Empty(result.Requeued);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // ------------------------------------------------------------------ phase 2 fails (E9 return contract)

    // Another session holds the pending key's emote row past phase 2's lock_timeout: phase 1 is
    // committed, the result hands back exactly the pending key with its counts, and no seed exists.
    [Fact]
    public async Task FlushAsync_WhenPhaseTwoHitsTheLockTimeout_PhaseOneIsCommitted_AndOnlyThePendingKeysComeBack()
    {
        await using var db = fixture.CreateDbContext();
        var (other, otherSlot) = await SeedEmoteWithSlotAsync(db, "flush-timeout-other");
        var emote = await SeedEmoteAsync(db, "flush-timeout");
        var pending = Pending(emote);

        UsageFlushResult result;
        await using (var holder = new NpgsqlConnection(db.Database.GetConnectionString()))
        {
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand("""SELECT 1 FROM "Emotes" WHERE "Id" = @id FOR UPDATE""", holder, transaction))
            {
                lockCommand.Parameters.AddWithValue("id", emote.Id);
                await lockCommand.ExecuteNonQueryAsync();
            }

            result = await CreateService(db).FlushAsync(Batch((Key(otherSlot), new(2, 0, 0)), (pending, new(3, 1, 4))));
            await transaction.RollbackAsync();
        }

        Assert.Equal(2, Assert.Single(await ReadStatsAsync(fixture, other.Id)).UseCount);
        Assert.Equal("flush-timeout-other", Assert.Single(result.WrittenChannels));
        var requeued = Assert.Single(result.Requeued);
        Assert.Equal((pending, new EmoteUsageCounts(3, 1, 4)), (requeued.Key, requeued.Value));
        Assert.Empty(await ReadStatsAsync(fixture, emote.Id));
        Assert.Equal(0, await db.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
    }

    // Second variant: a deadlock (40P01) after phase 2 created its seed rolls the seed back with it.
    [Fact]
    public async Task FlushAsync_WhenPhaseTwoIsADeadlockVictim_RollsBackItsSeed_AndHandsThePendingKeyBack()
    {
        Emote emote;
        await using (var setup = fixture.CreateDbContext())
        {
            emote = await SeedEmoteAsync(setup, "flush-deadlock");
        }

        var hook = new CommandHook(
            sql => sql.Contains("INSERT INTO \"EmoteSlots\"", StringComparison.Ordinal),
            _ => throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected));
        await using var db = fixture.CreateTaggedDbContext("flush-deadlock", [hook]);
        var pending = Pending(emote);

        var result = await CreateService(db).FlushAsync(Batch((pending, new(5, 0, 0))));

        Assert.Empty(result.WrittenChannels);
        var requeued = Assert.Single(result.Requeued);
        Assert.Equal((pending, new EmoteUsageCounts(5, 0, 0)), (requeued.Key, requeued.Value));
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.EmoteSlots.CountAsync(s => s.EmoteId == emote.Id));
        Assert.Empty(await ReadStatsAsync(fixture, emote.Id));
    }

    private static UsageCounterKey Key(string slotId) => new(slotId, SetId);

    private static UsageCounterKey Pending(Emote emote, string setId = SetId) => new(PendingSlotKey.For(emote.Id), setId);

    private static Dictionary<UsageCounterKey, EmoteUsageCounts> Batch(params (UsageCounterKey Key, EmoteUsageCounts Counts)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Counts);

    private static UsageStatFlushService CreateService(AppDbContext db) =>
        new(db, NullLogger<UsageStatFlushService>.Instance);

    // Reads through a fresh context: the rows were written by raw SQL, so anything the original
    // context has tracked is irrelevant to what actually landed in the table.
    private static async Task<List<UsageStat>> ReadStatsAsync(PostgresFixture fixture, string emoteId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UsageStats.AsNoTracking().Where(u => u.EmoteId == emoteId).OrderBy(u => u.Id).ToListAsync();
    }

    private static async Task<(Emote Emote, string SlotId)> SeedEmoteWithSlotAsync(
        AppDbContext db, string channelName, UsageSlotKind kind = UsageSlotKind.Settled)
    {
        var emote = await SeedEmoteAsync(db, channelName);
        return (emote, await db.SeedUsageSlotAsync(emote.Id, kind));
    }

    private static async Task<Emote> SeedEmoteAsync(AppDbContext db, string channelName, DateTime? archivedAt = null)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        var emote = NewEmote(channel.Id, "PogChamp");
        emote.IsArchived = archivedAt is not null;
        emote.ArchivedAt = archivedAt;
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private static async Task<(Emote First, Emote Second)> SeedTwoEmotesInOneChannelAsync(AppDbContext db, string channelName)
    {
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        var first = NewEmote(channel.Id, "PogChamp");
        var second = NewEmote(channel.Id, "Kappa");
        db.Emotes.AddRange(first, second);
        await db.SaveChangesAsync();
        return (first, second);
    }

    private static Emote NewEmote(string channelId, string name) => new()
    {
        ChannelId = channelId,
        Name = name,
        SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
        ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
    };
}
