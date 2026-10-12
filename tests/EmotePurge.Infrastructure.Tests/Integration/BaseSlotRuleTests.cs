using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The base slot rule of plan #373 (E5/E6/E6a) against real Postgres: rule (a) read-only for the map,
// (a)–(c) get-or-create for flush and enqueue, and the lock contract that keeps two creators of the
// same pair at one seed. The ordinal tie on Name under a linguistic column collation has its own
// database in BaseSlotRuleOrdinalOrderTests.
[Collection("Postgres")]
public class BaseSlotRuleTests(PostgresFixture fixture)
{
    private const string SetA = "SETA";
    private const string SetB = "SETB";
    private const string SetC = "SETC";
    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Find_ReturnsTheOldestSlotCarryingRowsOfTheSet_AndNothingForAPairWithoutRows_WritingNothing()
    {
        var (_, emoteId) = await SeedEmoteAsync("baseslot_find");
        var dated = await SeedSlotAsync(emoteId, "Dated", addedAt: T1, fromRest: true);
        var seed = await SeedSlotAsync(emoteId, "Emote", seedUnsettled: true);
        var later = await SeedSlotAsync(emoteId, "Zed");
        await SeedRowsAsync((dated, emoteId, SetB, 1), (seed, emoteId, SetA, 1), (later, emoteId, SetA, 2));
        var slotsBefore = await SlotCountAsync(emoteId);

        await using var db = fixture.CreateDbContext();
        var found = await BaseSlotRule.FindAsync(
            db, [new(emoteId, SetA), new(emoteId, SetB), new(emoteId, SetC), new(emoteId, SetA)], CancellationToken.None);

        // SETA: two undated slots, the ordinal smaller name wins; the dated slot carries only SETB rows.
        Assert.Equal(2, found.Count);
        Assert.Equal(seed, found[new(emoteId, SetA)]);
        Assert.Equal(dated, found[new(emoteId, SetB)]);
        Assert.False(found.ContainsKey(new(emoteId, SetC)));
        Assert.Equal(slotsBefore, await SlotCountAsync(emoteId));
        Assert.Empty(await BaseSlotRule.FindAsync(db, [], CancellationToken.None));
    }

    [Fact]
    public async Task GetOrCreate_AndLockAndGetOrCreate_RequireAnOpenTransaction()
    {
        await using var db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => BaseSlotRule.GetOrCreateAsync(db, [new("e", SetA)], Now, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BaseSlotRule.LockAndGetOrCreateAsync(db, [new("e", SetA)], Now, CancellationToken.None));
        // Even with nothing to resolve: a caller outside a transaction is a bug either way.
        await Assert.ThrowsAsync<InvalidOperationException>(() => BaseSlotRule.GetOrCreateAsync(db, [], Now, CancellationToken.None));
    }

    [Fact]
    public async Task GetOrCreate_RuleA_TakesTheDatedSlotBeforeTheSeed_AndCreatesNothing()
    {
        var (_, emoteId) = await SeedEmoteAsync("baseslot_rule_a");
        var seed = await SeedSlotAsync(emoteId, "Aaa", seedUnsettled: true);
        var dated = await SeedSlotAsync(emoteId, "Zzz", addedAt: T1, fromRest: true);
        await SeedRowsAsync((seed, emoteId, SetA, 1), (dated, emoteId, SetA, 2));

        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await BaseSlotRule.LockAndGetOrCreateAsync(db, [new(emoteId, SetA)], Now, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(dated, result.SlotIds[new(emoteId, SetA)]);
        Assert.Empty(result.Created);
        Assert.Equal(2, await SlotCountAsync(emoteId));
    }

    [Fact]
    public async Task GetOrCreate_RuleB_ReusesTheSeedOfARunOfTheSameSetOnly_AndNeverASettledSlot()
    {
        var (channelId, emoteId) = await SeedEmoteAsync("baseslot_rule_b");
        var seedOfA = await SeedSlotAsync(emoteId, "Aaa", seedUnsettled: true);
        var youngerSeedOfA = await SeedSlotAsync(emoteId, "Bbb", seedUnsettled: true);
        var seedOfB = await SeedSlotAsync(emoteId, "Ccc", seedUnsettled: true);
        var settledOfC = await SeedSlotAsync(emoteId, "Ddd", addedAt: T1, fromRest: true);
        await SeedSnapshotAsync(channelId, SetA, emoteId, youngerSeedOfA);
        await SeedSnapshotAsync(channelId, SetA, emoteId, seedOfA);
        await SeedSnapshotAsync(channelId, SetB, emoteId, seedOfB);
        await SeedSnapshotAsync(channelId, SetC, emoteId, settledOfC);

        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await BaseSlotRule.LockAndGetOrCreateAsync(
            db, [new(emoteId, SetA), new(emoteId, SetB), new(emoteId, SetC)], Now, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(seedOfA, result.SlotIds[new(emoteId, SetA)]);
        Assert.Equal(seedOfB, result.SlotIds[new(emoteId, SetB)]);
        // (b) only binds unsettled seeds: SETC gets a new seed next to the settled slot its run names.
        var newSeed = result.SlotIds[new(emoteId, SetC)];
        Assert.NotEqual(settledOfC, newSeed);
        Assert.Equal([new BaseSlotPair(emoteId, SetC)], result.Created);
        Assert.True((await SlotAsync(newSeed)).SeedUnsettled);
    }

    [Fact]
    public async Task GetOrCreate_RuleB_SkipsASeedCarryingRowsOfAnotherSet_SoNoSeedEverSpansTwoSets()
    {
        var (channelId, emoteId) = await SeedEmoteAsync("baseslot_rule_b_crossset");
        var seed = await SeedSlotAsync(emoteId, "Emote", seedUnsettled: true);
        await SeedRowsAsync((seed, emoteId, SetB, 1));
        await SeedSnapshotAsync(channelId, SetA, emoteId, seed);

        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await BaseSlotRule.LockAndGetOrCreateAsync(db, [new(emoteId, SetA), new(emoteId, SetB)], Now, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(seed, result.SlotIds[new(emoteId, SetB)]);
        Assert.NotEqual(seed, result.SlotIds[new(emoteId, SetA)]);
        Assert.Equal([new BaseSlotPair(emoteId, SetA)], result.Created);
    }

    [Fact]
    public async Task GetOrCreate_RuleB_StillBindsTheSeedWhenAFlushPutRowsOfTheSameSetOnItBetweenRuleAAndRuleB()
    {
        // Flush phase 1 writes real slot keys without the emote lock (only the foreign-key KEY SHARE),
        // so rows of the run's set can land on the row-less seed after statement (a) read nothing and
        // before statement (b) runs. (b) must still bind that seed: its guard is "no rows of another
        // set", not "no rows" — the latter would create a second seed for the pair right here.
        var (channelId, emoteId) = await SeedEmoteAsync("baseslot_rule_b_sameset");
        var seed = await SeedSlotAsync(emoteId, "Emote", seedUnsettled: true);
        await SeedSnapshotAsync(channelId, SetA, emoteId, seed);

        var hook = new CommandHook(
            sql => sql.Contains("u.\"EmoteSetId\" = input.\"EmoteSetId\"", StringComparison.Ordinal)
                   && !sql.Contains("ChatLogBackfillRunEmotes", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence == 1)
                {
                    await SeedRowsAsync((seed, emoteId, SetA, 1));
                }
            });
        await using var db = fixture.CreateDbContext([hook]);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await BaseSlotRule.LockAndGetOrCreateAsync(db, [new(emoteId, SetA)], Now, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(1, hook.Count(sql => sql.Contains("ChatLogBackfillRunEmotes", StringComparison.Ordinal)));
        Assert.Equal(seed, result.SlotIds[new(emoteId, SetA)]);
        Assert.Empty(result.Created);
        Assert.Equal(1, await SlotCountAsync(emoteId));
    }

    [Fact]
    public async Task GetOrCreate_RuleC_CreatesOneSeedPerPairWithTheM1Fields_InOneCall()
    {
        var (_, liveId) = await SeedEmoteAsync("baseslot_rule_c", name: "LiveName");
        var archivedAt = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var archivedId = await AddEmoteAsync(liveId, "ArchivedName", archived: true, archivedAt: archivedAt);

        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        BaseSlotPair[] pairs = [new(liveId, SetA), new(liveId, SetB), new(archivedId, SetA), new(liveId, SetA)];
        var result = await BaseSlotRule.LockAndGetOrCreateAsync(db, pairs, Now, CancellationToken.None);
        await transaction.CommitAsync();

        Assert.Equal(3, result.SlotIds.Count);
        Assert.Equal(result.SlotIds.Keys.ToHashSet(), result.Created.ToHashSet());
        Assert.Equal(3, result.SlotIds.Values.Distinct().Count());

        var live = await SlotAsync(result.SlotIds[new(liveId, SetA)]);
        Assert.Equal((liveId, "LiveName", false, (DateTime?)null), (live.EmoteId, live.Name, live.IsArchived, live.ArchivedAt));
        var archived = await SlotAsync(result.SlotIds[new(archivedId, SetA)]);
        Assert.Equal((archivedId, "ArchivedName", true, (DateTime?)archivedAt), (archived.EmoteId, archived.Name, archived.IsArchived, archived.ArchivedAt));
        foreach (var slot in new[] { live, archived, await SlotAsync(result.SlotIds[new(liveId, SetB)]) })
        {
            Assert.Null(slot.AddedAt);
            Assert.False(slot.AddedAtFromRest);
            Assert.True(slot.SeedUnsettled);
            Assert.Equal(Now, slot.LastSyncedAt);
        }
    }

    [Fact]
    public async Task GetOrCreate_ASeedBoundByItsFirstRow_IsFoundByTheNextCall()
    {
        var (_, emoteId) = await SeedEmoteAsync("baseslot_bound");
        string seed;
        await using (var first = fixture.CreateDbContext())
        await using (var transaction = await first.Database.BeginTransactionAsync())
        {
            seed = (await BaseSlotRule.LockAndGetOrCreateAsync(first, [new(emoteId, SetA)], Now, CancellationToken.None)).SlotIds[new(emoteId, SetA)];
            first.UsageStats.Add(Row(seed, emoteId, SetA, 1));
            await first.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var second = fixture.CreateDbContext();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var again = await BaseSlotRule.LockAndGetOrCreateAsync(second, [new(emoteId, SetA)], Now, CancellationToken.None);

        Assert.Equal(seed, again.SlotIds[new(emoteId, SetA)]);
        Assert.Empty(again.Created);
        Assert.Equal(1, await SlotCountAsync(emoteId));
    }

    [Fact]
    public async Task GetOrCreate_APairOfAMissingEmote_IsMissingFromTheResult()
    {
        var (_, emoteId) = await SeedEmoteAsync("baseslot_missing");

        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.LockEmotesByIdsAsync([emoteId], CancellationToken.None);
        var direct = await BaseSlotRule.GetOrCreateAsync(db, [new("no-such-emote", SetA), new(emoteId, SetA)], Now, CancellationToken.None);
        var locked = await BaseSlotRule.LockAndGetOrCreateAsync(db, [new("no-such-emote", SetB)], Now, CancellationToken.None);

        Assert.Equal([new BaseSlotPair(emoteId, SetA)], direct.SlotIds.Keys);
        Assert.Equal([new BaseSlotPair(emoteId, SetA)], direct.Created);
        Assert.Empty(locked.SlotIds);
        Assert.Empty(locked.Created);
    }

    [Fact]
    public async Task LockAndGetOrCreate_DropsThePairsOfAChannelPurgedWhileItWaited_AndResolvesTheOthers()
    {
        var (purgedChannel, purgedEmote) = await SeedEmoteAsync("baseslot_purged");
        var (_, otherEmote) = await SeedEmoteAsync("baseslot_purged_other");

        await using var purge = fixture.CreateDbContext();
        await using var purgeTransaction = await purge.Database.BeginTransactionAsync();
        await purge.Database.ExecuteSqlAsync($"""DELETE FROM "Channels" WHERE "Id" = {purgedChannel}""");

        const string tag = "baseslot-purged-caller";
        await using var caller = fixture.CreateTaggedDbContext(tag);
        await using var callerTransaction = await caller.Database.BeginTransactionAsync();
        var resolving = BaseSlotRule.LockAndGetOrCreateAsync(caller, [new(purgedEmote, SetA), new(otherEmote, SetA)], Now, CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync(tag, resolving);
        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync(tag));

        await purgeTransaction.CommitAsync();
        var result = await resolving;
        await callerTransaction.CommitAsync();

        Assert.Equal([new BaseSlotPair(otherEmote, SetA)], result.SlotIds.Keys);
        Assert.Equal([new BaseSlotPair(otherEmote, SetA)], result.Created);
    }

    [Fact]
    public async Task LockAndGetOrCreate_HoldsTheChannelRowWhileItWaitsForTheEmoteRow()
    {
        // E6 order: channel rows, then emote rows. While the caller waits at the emote, it already holds
        // the channel — a NOWAIT probe on the channel fails.
        var (channelId, emoteId) = await SeedEmoteAsync("baseslot_order");

        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockEmotesByIdsAsync([emoteId], CancellationToken.None);

        const string tag = "baseslot-order-caller";
        await using var caller = fixture.CreateTaggedDbContext(tag);
        await using var callerTransaction = await caller.Database.BeginTransactionAsync();
        var resolving = BaseSlotRule.LockAndGetOrCreateAsync(caller, [new(emoteId, SetA)], Now, CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync(tag, resolving);
        Assert.Contains("\"Emotes\"", await fixture.WaitingQueryAsync(tag));

        await using (var probe = fixture.CreateDbContext())
        await using (var probeTransaction = await probe.Database.BeginTransactionAsync())
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => probe.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "Channels" WHERE "Id" = {channelId} FOR NO KEY UPDATE NOWAIT"""));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, failure.SqlState);
        }

        await holderTransaction.CommitAsync();
        Assert.Single((await resolving).Created);
    }

    [Fact]
    public async Task TwoCreatorsOfTheSamePair_EndWithOneSeed_WhenTheFirstBindsItWithAUsageRow()
    {
        var (_, emoteId) = await SeedEmoteAsync("baseslot_race_row");

        await using var first = fixture.CreateDbContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        var created = await BaseSlotRule.LockAndGetOrCreateAsync(first, [new(emoteId, SetA)], Now, CancellationToken.None);
        var seed = created.SlotIds[Assert.Single(created.Created)];
        first.UsageStats.Add(Row(seed, emoteId, SetA, 1));
        await first.SaveChangesAsync();

        const string tag = "baseslot-race-row-second";
        await using var second = fixture.CreateTaggedDbContext(tag);
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var resolving = BaseSlotRule.LockAndGetOrCreateAsync(second, [new(emoteId, SetA)], Now, CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync(tag, resolving);

        await firstTransaction.CommitAsync();
        var result = await resolving;
        await secondTransaction.CommitAsync();

        Assert.Equal(seed, result.SlotIds[new(emoteId, SetA)]);
        Assert.Empty(result.Created);
        Assert.Equal(1, await SlotCountAsync(emoteId));
    }

    [Fact]
    public async Task TwoCreatorsOfTheSamePair_EndWithOneSeed_WhenTheFirstBindsItWithASnapshotRowOfTheSet()
    {
        // The enqueue's shape: its own locks, then GetOrCreate, then a snapshot row naming the seed. The
        // second creator (flush phase 2) waits and then finds the row-less seed through rule (b).
        var (channelId, emoteId) = await SeedEmoteAsync("baseslot_race_snapshot");
        var runId = await SeedRunAsync(channelId, SetA);

        await using var first = fixture.CreateDbContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await first.LockChannelsByIdsAsync([channelId], CancellationToken.None);
        await first.LockEmotesByIdsAsync([emoteId], CancellationToken.None);
        var created = await BaseSlotRule.GetOrCreateAsync(first, [new(emoteId, SetA)], Now, CancellationToken.None);
        var seed = created.SlotIds[Assert.Single(created.Created)];
        first.ChatLogBackfillRunEmotes.Add(new ChatLogBackfillRunEmote
        {
            RunId = runId,
            EmoteId = emoteId,
            SevenTvEmoteId = "7tv",
            Name = "Emote",
            SlotId = seed,
            CreatedSlot = true
        });
        await first.SaveChangesAsync();

        const string tag = "baseslot-race-snapshot-second";
        await using var second = fixture.CreateTaggedDbContext(tag);
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var resolving = BaseSlotRule.LockAndGetOrCreateAsync(second, [new(emoteId, SetA)], Now, CancellationToken.None);
        await fixture.WaitUntilBlockedOnLockAsync(tag, resolving);

        await firstTransaction.CommitAsync();
        var result = await resolving;
        await secondTransaction.CommitAsync();

        Assert.Equal(seed, result.SlotIds[new(emoteId, SetA)]);
        Assert.Empty(result.Created);
        Assert.Equal(1, await SlotCountAsync(emoteId));
    }

    private static UsageStat Row(string slotId, string emoteId, string setId, int day) =>
        new() { EmoteId = emoteId, EmoteSetId = setId, SlotId = slotId, Date = new DateOnly(2026, 9, day), UseCount = day };

    private async Task<(string ChannelId, string EmoteId)> SeedEmoteAsync(string channelName, string name = "Emote")
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        var emote = new Emote { ChannelId = channel.Id, SevenTvEmoteId = "7tv-" + channelName, Name = name, ImageUrl = "i" };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return (channel.Id, emote.Id);
    }

    private async Task<string> AddEmoteAsync(string siblingEmoteId, string name, bool archived, DateTime? archivedAt)
    {
        await using var db = fixture.CreateDbContext();
        var channelId = await db.Emotes.Where(e => e.Id == siblingEmoteId).Select(e => e.ChannelId).SingleAsync();
        var emote = new Emote { ChannelId = channelId, SevenTvEmoteId = "7tv-" + name, Name = name, ImageUrl = "i", IsArchived = archived, ArchivedAt = archivedAt };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote.Id;
    }

    private async Task<string> SeedSlotAsync(string emoteId, string name, DateTime? addedAt = null, bool fromRest = false, bool seedUnsettled = false)
    {
        await using var db = fixture.CreateDbContext();
        var slot = new EmoteSlot { EmoteId = emoteId, Name = name, AddedAt = addedAt, AddedAtFromRest = fromRest, SeedUnsettled = seedUnsettled };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private async Task SeedRowsAsync(params (string SlotId, string EmoteId, string SetId, int Day)[] rows)
    {
        await using var db = fixture.CreateDbContext();
        db.UsageStats.AddRange(rows.Select(r => Row(r.SlotId, r.EmoteId, r.SetId, r.Day)));
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedRunAsync(string channelId, string setId)
    {
        await using var db = fixture.CreateDbContext();
        var run = new ChatLogBackfillRun
        {
            ChannelId = channelId,
            Status = ChatLogBackfillRunStatus.Completed,
            RequestedMonths = 1,
            WindowFrom = new DateOnly(2026, 8, 1),
            WindowTo = new DateOnly(2026, 9, 1),
            WeeksTotal = 5,
            EmoteSetId = setId,
            ArchiveBaseUrl = "https://logs.cyex.app/",
            RequestedByTwitchUserId = "42",
            RequestedByLogin = "baseslot",
            RequestedAtUtc = Now
        };
        db.ChatLogBackfillRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task SeedSnapshotAsync(string channelId, string setId, string emoteId, string slotId)
    {
        var runId = await SeedRunAsync(channelId, setId);
        await using var db = fixture.CreateDbContext();
        db.ChatLogBackfillRunEmotes.Add(new ChatLogBackfillRunEmote
        {
            RunId = runId,
            EmoteId = emoteId,
            SevenTvEmoteId = "7tv",
            Name = "Emote",
            SlotId = slotId,
            CreatedSlot = true
        });
        await db.SaveChangesAsync();
    }

    private async Task<EmoteSlot> SlotAsync(string slotId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.EmoteSlots.AsNoTracking().SingleAsync(s => s.Id == slotId);
    }

    private async Task<int> SlotCountAsync(string emoteId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.EmoteSlots.CountAsync(s => s.EmoteId == emoteId);
    }
}
