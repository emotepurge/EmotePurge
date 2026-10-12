using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The slot-aware REST reconcile through the real SyncChannelAsync with the flag on (plan #372, 6.3),
// plus the flag-off proof on slot data (6.4). The decision rules themselves are unit-tested in
// EmoteSlotReconcilePlannerTests; these cases prove the service applies a plan to real rows: slots,
// usage rebinds of the settle, D13 on the emote row, HasChanges and the per-channel log lines.
[Collection("Postgres")]
public class SevenTvSyncServiceSlotTests(PostgresFixture fixture)
{
    private const string SettleLine = "settle deferred: v4 dates missing for";
    private const string SummaryLine = "slot reconcile created";
    private const string MergeLine = "settle merged";

    // 1 — AK 5: the active set's seed settles onto the oldest dated entry and keeps only its own rows.
    [Fact]
    public async Task Settle_TwoDatedEntries_SeedBecomesTheOldestSlotWithOnlyTheActiveSetsRows()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_ak5");
        var x = await SeedEmoteAsync(db, channel, "x", "ome44");
        var seedA = await SeedSeedAsync(db, x);
        var seedB = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seedA, SetA, 5, 7);
        await SeedUsageAsync(db, x, seedB, SetB, 11);
        var seedBBefore = Columns(await SlotAsync(db, seedB.Id));
        var logger = new RecordingLogger<SevenTvSyncService>();
        var live = new[] { Live("x", "ome44", T1), Live("x", "hob44", T2) };

        var first = await SyncAsync(db, channel, true, SetA, logger, live);
        var second = await SyncAsync(db, channel, true, SetA, logger, live);

        var slots = await SlotsAsync(db, x.Id);
        var ome = Assert.Single(slots, s => s.Name == "ome44" && !s.SeedUnsettled && !s.IsArchived);
        Assert.Equal(seedA.Id, ome.Id);
        Assert.Equal(T1, ome.AddedAt);
        Assert.True(ome.AddedAtFromRest);
        var hob = Assert.Single(slots, s => s.Name == "hob44");
        Assert.Equal(T2, hob.AddedAt);
        Assert.True(hob.AddedAtFromRest);
        Assert.False(hob.IsArchived);
        Assert.Equal(seedBBefore, Columns(await SlotAsync(db, seedB.Id)));

        var usage = await UsageBySlotAsync(db, x.Id, SetA);
        Assert.Equal((2, 12), usage[seedA.Id]);
        Assert.False(usage.ContainsKey(hob.Id));
        Assert.Equal((1, 11), (await UsageBySlotAsync(db, x.Id, SetB))[seedB.Id]);

        var emote = await EmoteAsync(db, channel, "x");
        Assert.Equal("ome44", emote.Name);
        Assert.Equal(T1, emote.FirstSeenAt);
        Assert.True(first!.HasChanges);
        Assert.False(second!.HasChanges);
        Assert.Single(Lines(logger, SummaryLine));
    }

    // 2 — AK 6: a multi-entry group with a missing date waits; the next dated pass is AK 5.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settle_MultiEntryGroupWithAnUndatedEntry_IsDeferredUntilDatesArrive(bool oneEntryDated)
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, $"slot_ak6_{oneEntryDated}".ToLowerInvariant());
        var x = await SeedEmoteAsync(db, channel, "x", "ome44");
        var seed = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seed, SetA, 3);
        var seedBefore = Columns(await SlotAsync(db, seed.Id));
        var logger = new RecordingLogger<SevenTvSyncService>();

        await SyncAsync(db, channel, true, SetA, logger, Live("x", "hob44", oneEntryDated ? T2 : null), Live("x", "ome44", null));

        Assert.Equal(seedBefore, Columns(Assert.Single(await SlotsAsync(db, x.Id))));
        Assert.Equal($"{channel.ChannelName}: settle deferred: v4 dates missing for 1 emotes", Assert.Single(Lines(logger, SettleLine)));
        Assert.Empty(Lines(logger, SummaryLine));

        await SyncAsync(db, channel, true, SetA, logger, Live("x", "hob44", T2), Live("x", "ome44", T1));

        var slots = await SlotsAsync(db, x.Id);
        Assert.Equal(2, slots.Count);
        var settled = Assert.Single(slots, s => s.Id == seed.Id);
        Assert.Equal(("ome44", T1, true, false), (settled.Name, settled.AddedAt, settled.AddedAtFromRest, settled.SeedUnsettled));
        Assert.Single(Lines(logger, SettleLine));
    }

    // 3 — AK 7, swap: the date is the identity, the names move.
    [Fact]
    public async Task Swap_NamesMoveBetweenTheDatedSlotsAndUsageStays()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_swap");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var s1 = await SeedSlotAsync(db, x, "A", T1);
        var s2 = await SeedSlotAsync(db, x, "B", T2);
        await SeedUsageAsync(db, x, s1, SetA, 4, 4);

        var result = await SyncOnAsync(db, channel, Live("x", "C", T1), Live("x", "A", T2));

        Assert.Equal(("C", T1), ((await SlotAsync(db, s1.Id)).Name, (await SlotAsync(db, s1.Id)).AddedAt));
        Assert.Equal(("A", T2), ((await SlotAsync(db, s2.Id)).Name, (await SlotAsync(db, s2.Id)).AddedAt));
        Assert.Equal(2, (await SlotsAsync(db, x.Id)).Count);
        Assert.Equal((2, 8), (await UsageBySlotAsync(db, x.Id, SetA))[s1.Id]);
        Assert.Equal("C", (await EmoteAsync(db, channel, "x")).Name);
        Assert.True(result!.HasChanges);
    }

    // 4 — AK 7, remove + add, then the return under the old name (D7).
    [Fact]
    public async Task RemoveAndAdd_ThenReturnUnderTheOldName_ResumesTheSameSlot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_remove_add");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var s1 = await SeedSlotAsync(db, x, "A", T1);
        await SeedUsageAsync(db, x, s1, SetA, 9);

        await SyncOnAsync(db, channel, Live("x", "B", T2));

        var afterSwap = await SlotsAsync(db, x.Id);
        var archived = Assert.Single(afterSwap, s => s.Id == s1.Id);
        Assert.True(archived.IsArchived);
        Assert.NotNull(archived.ArchivedAt);
        var b = Assert.Single(afterSwap, s => s.Name == "B");
        Assert.False(b.IsArchived);
        Assert.Equal(T2, b.AddedAt);

        await SyncOnAsync(db, channel, Live("x", "A", T3));

        var resumed = await SlotAsync(db, s1.Id);
        Assert.Equal(("A", T3, true, false), (resumed.Name, resumed.AddedAt, resumed.AddedAtFromRest, resumed.IsArchived));
        Assert.Null(resumed.ArchivedAt);
        Assert.True((await SlotAsync(db, b.Id)).IsArchived);
        Assert.Equal(2, (await SlotsAsync(db, x.Id)).Count);
        Assert.Equal((1, 9), (await UsageBySlotAsync(db, x.Id, SetA))[s1.Id]);
    }

    // 5 — #371 edge case: a seed next to a REST slot hands its active-set rows to that slot.
    [Fact]
    public async Task SeedNextToRestSlot_RowsMoveToTheRestSlot_SeedIsArchivedWithoutDate()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_seed_next_rest");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var rest = await SeedSlotAsync(db, x, "A", T1);
        var seed = await SeedSeedAsync(db, x);
        var seedB = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seed, SetA, 2, 3);
        await SeedUsageAsync(db, x, seedB, SetB, 6);
        var totalsBefore = await UsageTotalsAsync(db, x.Id, SetA);
        var rowIdsBefore = await UsageRowIdsAsync(db, seed.Id);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await SyncAsync(db, channel, true, SetA, logger, Live("x", "A", T1));

        var settled = await SlotAsync(db, seed.Id);
        Assert.False(settled.SeedUnsettled);
        Assert.True(settled.IsArchived);
        Assert.Null(settled.ArchivedAt);
        var usage = await UsageBySlotAsync(db, x.Id, SetA);
        Assert.Equal((2, 5), Assert.Single(usage).Value);
        Assert.True(usage.ContainsKey(rest.Id));
        Assert.Equal(totalsBefore, await UsageTotalsAsync(db, x.Id, SetA));
        Assert.Equal((1, 6), (await UsageBySlotAsync(db, x.Id, SetB))[seedB.Id]);
        Assert.True((await SlotAsync(db, seedB.Id)).SeedUnsettled);
        Assert.False(result!.HasChanges);
        Assert.Contains("moved 2 usage rows", Assert.Single(Lines(logger, SummaryLine)), StringComparison.Ordinal);
        // E10 without a colliding cell: the rows are re-keyed, not merged or re-inserted.
        Assert.Equal(rowIdsBefore, await UsageRowIdsAsync(db, rest.Id));
        Assert.Empty(Lines(logger, MergeLine));
    }

    // 5a — plan #373 E10: a seed settling onto an existing target whose (set, day) cell is taken. The
    // colliding rows are added onto the target's (all three counters; Source live if either row is
    // live, imported only if both are) and deleted, the free day is re-keyed with its row id, the
    // target's own day and the seed's other set stay; no unique violation, the seed ends settled and
    // archived without a date exactly as without the collision. Mutation probe: a plain re-key fails
    // the save with 23505.
    [Fact]
    public async Task SeedNextToRestSlot_CollidingCells_AreMergedOntoTheRestSlot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_seed_merge");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var rest = await SeedSlotAsync(db, x, "A", T1);
        var seed = await SeedSeedAsync(db, x);
        var seedB = await SeedSeedAsync(db, x);
        var d1 = new DateOnly(2026, 8, 1);
        const UsageStatSource live = UsageStatSource.Live;
        const UsageStatSource imported = UsageStatSource.ChatLogArchive;
        var freeDay = AddRow(db, x, seed, SetA, d1, (2, 1, 1), live);
        AddRow(db, x, seed, SetA, d1.AddDays(1), (3, 1, 2), imported);
        AddRow(db, x, rest, SetA, d1.AddDays(1), (10, 4, 5), live);
        AddRow(db, x, seed, SetA, d1.AddDays(2), (5, 0, 1), live);
        AddRow(db, x, rest, SetA, d1.AddDays(2), (7, 2, 0), imported);
        AddRow(db, x, seed, SetA, d1.AddDays(3), (4, 1, 0), imported);
        AddRow(db, x, rest, SetA, d1.AddDays(3), (6, 0, 3), imported);
        AddRow(db, x, rest, SetA, d1.AddDays(4), (9, 9, 9), live);
        AddRow(db, x, seedB, SetB, d1.AddDays(1), (8, 0, 0), live);
        await db.SaveChangesAsync();
        var totalsBefore = await UsageTotalsAsync(db, x.Id, SetA);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await SyncAsync(db, channel, true, SetA, logger, Live("x", "A", T1));

        Assert.NotNull(result);
        var settled = await SlotAsync(db, seed.Id);
        Assert.Equal((false, true, (DateTime?)null), (settled.SeedUnsettled, settled.IsArchived, settled.ArchivedAt));
        var rows = await db.UsageStats.AsNoTracking()
            .Where(u => u.EmoteId == x.Id && u.EmoteSetId == SetA)
            .OrderBy(u => u.Date)
            .ToListAsync();
        Assert.All(rows, r => Assert.Equal(rest.Id, r.SlotId));
        Assert.Equal(
            new (DateOnly, int, int, int, UsageStatSource)[]
            {
                (d1, 2, 1, 1, live),
                (d1.AddDays(1), 13, 5, 7, live),
                (d1.AddDays(2), 12, 2, 1, live),
                (d1.AddDays(3), 10, 1, 3, imported),
                (d1.AddDays(4), 9, 9, 9, live),
            },
            rows.Select(r => (r.Date, r.UseCount, r.BotUseCount, r.SharedChatUseCount, r.Source)));
        Assert.Equal(freeDay.Id, rows[0].Id);
        var totalsAfter = await UsageTotalsAsync(db, x.Id, SetA);
        Assert.Equal((totalsBefore.Uses, totalsBefore.Bot, totalsBefore.Shared), (totalsAfter.Uses, totalsAfter.Bot, totalsAfter.Shared));
        Assert.Equal((1, 8), (await UsageBySlotAsync(db, x.Id, SetB))[seedB.Id]);
        Assert.True((await SlotAsync(db, seedB.Id)).SeedUnsettled);
        Assert.Contains("moved 4 usage rows", Assert.Single(Lines(logger, SummaryLine)), StringComparison.Ordinal);
        Assert.Contains("merged 3 usage rows", Assert.Single(Lines(logger, MergeLine)), StringComparison.Ordinal);

        // The next tick finds nothing left to settle or merge.
        var second = new RecordingLogger<SevenTvSyncService>();
        await SyncAsync(db, channel, true, SetA, second, Live("x", "A", T1));
        Assert.Empty(Lines(second, MergeLine));
        Assert.Equal(5, await db.UsageStats.CountAsync(u => u.SlotId == rest.Id));
    }

    // 6 — AK 4 half: a foreign seed stays byte-identical; switching to its set settles it.
    [Fact]
    public async Task ForeignSeed_StaysUntouched_UntilTheChannelSwitchesToItsSet()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_foreign_seed");
        var x = await SeedEmoteAsync(db, channel, "x", "X");
        var seedA = await SeedSeedAsync(db, x);
        var seedB = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seedA, SetA, 1, 2);
        await SeedUsageAsync(db, x, seedB, SetB, 10, 20, 30);
        var seedBBefore = Columns(await SlotAsync(db, seedB.Id));
        var totalsA = await UsageTotalsAsync(db, x.Id, SetA);
        var totalsB = await UsageTotalsAsync(db, x.Id, SetB);

        await SyncOnAsync(db, channel, Live("x", "X", T1));

        Assert.Equal(seedBBefore, Columns(await SlotAsync(db, seedB.Id)));
        Assert.False((await SlotAsync(db, seedA.Id)).SeedUnsettled);

        await SyncAsync(db, channel, true, SetB, null, Live("x", "X", T5));

        var b = await SlotAsync(db, seedB.Id);
        Assert.Equal(("X", T5, true, false, false), (b.Name, b.AddedAt, b.AddedAtFromRest, b.IsArchived, b.SeedUnsettled));
        var a = await SlotAsync(db, seedA.Id);
        Assert.True(a.IsArchived);
        Assert.NotNull(a.ArchivedAt);
        Assert.Equal(totalsA, await UsageTotalsAsync(db, x.Id, SetA));
        Assert.Equal(totalsB, await UsageTotalsAsync(db, x.Id, SetB));
        Assert.Equal((3, 60), (await UsageBySlotAsync(db, x.Id, SetB))[seedB.Id]);
        Assert.Equal((2, 3), (await UsageBySlotAsync(db, x.Id, SetA))[seedA.Id]);
        Assert.Equal(T5, (await EmoteAsync(db, channel, "x")).FirstSeenAt);
    }

    // 7 — AK 8: the same entry returning after a REST lag is un-archived, not duplicated.
    [Fact]
    public async Task ReturnAfterRestLag_SameDate_UnarchivesTheSlot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_rest_lag");
        var x = await SeedEmoteAsync(db, channel, "x", "A", archived: true, firstSeenAt: T1);
        var s1 = await SeedSlotAsync(db, x, "A", T1, archived: true, archivedAt: DateTime.UtcNow.AddMinutes(-5));

        await SyncOnAsync(db, channel, Live("x", "A", T1));

        var slot = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal(s1.Id, slot.Id);
        Assert.False(slot.IsArchived);
        Assert.Null(slot.ArchivedAt);
        Assert.False((await EmoteAsync(db, channel, "x")).IsArchived);
    }

    // 8 — AK 8, tie: two entries of one date bind by name only.
    [Fact]
    public async Task Tie_EqualDates_BindByNameOnly()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_tie");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var a = await SeedSlotAsync(db, x, "A", T1);
        var b = await SeedSlotAsync(db, x, "B", T1);

        var unchanged = await SyncOnAsync(db, channel, Live("x", "A", T1), Live("x", "B", T1));
        var renamed = await SyncOnAsync(db, channel, Live("x", "A", T1), Live("x", "C", T1));

        Assert.False(unchanged!.HasChanges);
        Assert.True(renamed!.HasChanges);
        var slots = await SlotsAsync(db, x.Id);
        Assert.Equal(3, slots.Count);
        Assert.False(slots.Single(s => s.Id == a.Id).IsArchived);
        Assert.True(slots.Single(s => s.Id == b.Id).IsArchived);
        var c = Assert.Single(slots, s => s.Name == "C");
        Assert.Equal((T1, true, false), (c.AddedAt, c.AddedAtFromRest, c.IsArchived));
    }

    // 9 — AK 9: the last slot going takes the emote with it, and only then a leave is recorded.
    [Fact]
    public async Task LastSlotGoes_EmoteIsArchivedAndLeaveRecorded_NotBefore()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_last_goes");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        await SeedEmoteAsync(db, channel, "y", "Y", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "A", T1);
        var b = await SeedSlotAsync(db, x, "B", T2);

        await SyncOnAsync(db, channel, Live("x", "A", T1), Live("y", "Y", T1));

        Assert.True((await SlotAsync(db, b.Id)).IsArchived);
        Assert.False((await EmoteAsync(db, channel, "x")).IsArchived);
        Assert.False(await LeaveRecordedAsync(db, channel, "x"));

        await SyncOnAsync(db, channel, Live("y", "Y", T1));

        Assert.All(await SlotsAsync(db, x.Id), s => Assert.True(s.IsArchived));
        Assert.True((await EmoteAsync(db, channel, "x")).IsArchived);
        Assert.True(await LeaveRecordedAsync(db, channel, "x"));
    }

    // 10 — AK 10: Emote.Name follows the oldest active slot (ties and all-undated ordinal).
    [Fact]
    public async Task DerivedName_FollowsTheOldestActiveSlot_TiesOrdinal()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_derived_name");

        await SyncOnAsync(
            db,
            channel,
            Live("old", "Zeta", T1), Live("old", "Alpha", T2),
            Live("tie", "b", T3), Live("tie", "a", T3),
            Live("nul", "beta", null), Live("nul", "Alpha", null));

        var old = await EmoteAsync(db, channel, "old");
        Assert.Equal(("Zeta", (DateTime?)T1), (old.Name, old.FirstSeenAt));
        Assert.Equal("a", (await EmoteAsync(db, channel, "tie")).Name);
        var nul = await EmoteAsync(db, channel, "nul");
        Assert.Equal(("Alpha", (DateTime?)null), (nul.Name, nul.FirstSeenAt));
    }

    // 11 — AK 12 (replaces SevenTvSyncServiceDuplicateEmoteIdTests with the flag on).
    [Fact]
    public async Task SameIdUnderTwoNames_TwoSlots_SecondRunReportsNoChange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_dup_alias");
        var live = new[] { Live("dup1", "inkorrekt", T1), Live("dup1", "Nerdge", T2) };

        var first = await SyncOnAsync(db, channel, live);
        var stamps = (await SlotsAsync(db, (await EmoteAsync(db, channel, "dup1")).Id)).Select(s => s.LastSyncedAt).ToList();
        var second = await SyncOnAsync(db, channel, live);

        var emote = await EmoteAsync(db, channel, "dup1");
        var slots = await SlotsAsync(db, emote.Id);
        Assert.Equal(["Nerdge", "inkorrekt"], slots.Select(s => s.Name));
        Assert.Equal(stamps, slots.Select(s => s.LastSyncedAt));
        Assert.True(first!.HasChanges);
        Assert.False(second!.HasChanges);
        Assert.Equal("inkorrekt", emote.Name);
    }

    // 12 — AK 13: a provisional slot gets its date authorized silently; a different name replaces it.
    [Fact]
    public async Task ProvisionalSlot_SameName_IsAuthorizedWithoutChange_OtherName_IsReplaced()
    {
        await using var db = fixture.CreateDbContext();
        var authorized = await SeedChannelAsync(db, "slot_provisional_auth");
        var xa = await SeedEmoteAsync(db, authorized, "x", "C", firstSeenAt: DateTime.UtcNow);
        var pushA = await SeedSlotAsync(db, xa, "C", DateTime.UtcNow, fromRest: false);
        var replaced = await SeedChannelAsync(db, "slot_provisional_repl");
        var xr = await SeedEmoteAsync(db, replaced, "x", "C", firstSeenAt: DateTime.UtcNow);
        var pushR = await SeedSlotAsync(db, xr, "C", DateTime.UtcNow, fromRest: false);

        var same = await SyncOnAsync(db, authorized, Live("x", "C", T5));
        var other = await SyncOnAsync(db, replaced, Live("x", "D", T5));

        var c = await SlotAsync(db, pushA.Id);
        Assert.Equal(("C", T5, true, false), (c.Name, c.AddedAt, c.AddedAtFromRest, c.IsArchived));
        Assert.False(same!.HasChanges);
        Assert.Equal(T5, (await EmoteAsync(db, authorized, "x")).FirstSeenAt);

        Assert.True((await SlotAsync(db, pushR.Id)).IsArchived);
        var d = Assert.Single(await SlotsAsync(db, xr.Id), s => s.Name == "D");
        Assert.Equal((T5, true, false), (d.AddedAt, d.AddedAtFromRest, d.IsArchived));
        Assert.True(other!.HasChanges);
        Assert.Equal("D", (await EmoteAsync(db, replaced, "x")).Name);
    }

    // 13 — AK 47: external resumption re-keys the archived slot onto the new date.
    [Fact]
    public async Task ExternalResumption_ArchivedSlotIsReKeyedOntoTheNewDate()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_ak47");
        var x = await SeedEmoteAsync(db, channel, "x", "A", archived: true, firstSeenAt: T1);
        var s1 = await SeedSlotAsync(db, x, "A", T1, archived: true, archivedAt: T2);
        await SeedUsageAsync(db, x, s1, SetA, 8);

        await SyncOnAsync(db, channel, Live("x", "A", T3));

        var slot = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal((s1.Id, T3, true, false), (slot.Id, slot.AddedAt, slot.AddedAtFromRest, slot.IsArchived));
        Assert.Null(slot.ArchivedAt);
        Assert.Equal((1, 8), (await UsageBySlotAsync(db, x.Id, SetA))[s1.Id]);
        Assert.Equal(T3, (await EmoteAsync(db, channel, "x")).FirstSeenAt);
    }

    // 14 — AK 48: an active slot of the same name blocks the resumption.
    [Fact]
    public async Task ActiveSameNameSlot_BlocksResumption_NewEntryStartsAtZero()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_ak48");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T2);
        var old = await SeedSlotAsync(db, x, "A", T1, archived: true, archivedAt: T2);
        var current = await SeedSlotAsync(db, x, "A", T2);

        await SyncOnAsync(db, channel, Live("x", "A", T3));

        var slots = await SlotsAsync(db, x.Id);
        Assert.Equal(3, slots.Count);
        Assert.Equal((T1, true), (slots.Single(s => s.Id == old.Id).AddedAt, slots.Single(s => s.Id == old.Id).IsArchived));
        Assert.True(slots.Single(s => s.Id == current.Id).IsArchived);
        var fresh = Assert.Single(slots, s => s.AddedAt == T3);
        Assert.False(fresh.IsArchived);
    }

    // 15 — AK 49: of two archived namesakes the most recently archived one resumes.
    [Fact]
    public async Task TwoArchivedNamesakes_TheMostRecentlyArchivedResumes()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_ak49");
        var x = await SeedEmoteAsync(db, channel, "x", "A", archived: true, firstSeenAt: T2);
        var s1 = await SeedSlotAsync(db, x, "A", T0, archived: true, archivedAt: T1);
        var s2 = await SeedSlotAsync(db, x, "A", T1, archived: true, archivedAt: T2);
        await SeedUsageAsync(db, x, s2, SetA, 4);

        await SyncOnAsync(db, channel, Live("x", "A", T3));

        var resumed = await SlotAsync(db, s2.Id);
        Assert.Equal((T3, false), (resumed.AddedAt, resumed.IsArchived));
        Assert.Equal((T0, true), ((await SlotAsync(db, s1.Id)).AddedAt, (await SlotAsync(db, s1.Id)).IsArchived));
        Assert.Equal((1, 4), (await UsageBySlotAsync(db, x.Id, SetA))[s2.Id]);
    }

    // 16 — AK 50: date matches win over a resumption in the same pass.
    [Fact]
    public async Task SwapNextToAnArchivedNamesake_DateMatchesWin()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_ak50");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var a0 = await SeedSlotAsync(db, x, "A", T0, archived: true, archivedAt: T1);
        var s1 = await SeedSlotAsync(db, x, "A", T1);
        var s2 = await SeedSlotAsync(db, x, "B", T2);

        await SyncOnAsync(db, channel, Live("x", "C", T1), Live("x", "A", T2));

        Assert.Equal("C", (await SlotAsync(db, s1.Id)).Name);
        Assert.Equal("A", (await SlotAsync(db, s2.Id)).Name);
        var archived = await SlotAsync(db, a0.Id);
        Assert.Equal((T0, true), (archived.AddedAt, archived.IsArchived));
        Assert.Equal(3, (await SlotsAsync(db, x.Id)).Count);
    }

    // 17 — E18: the first own slot replaces the virtual one without HasChanges.
    [Fact]
    public async Task FirstOwnSlot_ReplacesTheVirtualSlot_WithoutHasChanges()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_virtual");
        var x = await SeedEmoteAsync(db, channel, "x", "Foo", firstSeenAt: T1);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await SyncAsync(db, channel, true, SetA, logger, Live("x", "Foo", T1));

        var slot = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal(("Foo", T1, true, false), (slot.Name, slot.AddedAt, slot.AddedAtFromRest, slot.IsArchived));
        Assert.False(result!.HasChanges);
        Assert.Contains("created 1 and updated 0 slots", Assert.Single(Lines(logger, SummaryLine)), StringComparison.Ordinal);
    }

    // 18 — an overlay outage without rename changes no slot, no date and no FirstSeenAt.
    [Fact]
    public async Task OverlayOutage_WithoutRename_KeepsSlotDatesAndFirstSeenAt()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_overlay_outage");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var slot = await SeedSlotAsync(db, x, "A", T1);
        var before = Columns(slot);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await SyncAsync(db, channel, true, SetA, logger, Live("x", "A", null));

        Assert.Equal(before, Columns(await SlotAsync(db, slot.Id)));
        Assert.Equal(T1, (await EmoteAsync(db, channel, "x")).FirstSeenAt);
        Assert.False(result!.HasChanges);
        Assert.Empty(Lines(logger, SummaryLine));
    }

    // 19 — D13 strictly: an undated replacement of a dated slot does not inherit its date.
    [Fact]
    public async Task UndatedReplacementOfADatedSlot_FirstSeenAtBecomesNull()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_undated_repl");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var a = await SeedSlotAsync(db, x, "A", T1);

        await SyncOnAsync(db, channel, Live("x", "B", null));

        Assert.True((await SlotAsync(db, a.Id)).IsArchived);
        var b = Assert.Single(await SlotsAsync(db, x.Id), s => s.Name == "B");
        Assert.Equal(((DateTime?)null, false, false), (b.AddedAt, b.AddedAtFromRest, b.IsArchived));
        var emote = await EmoteAsync(db, channel, "x");
        Assert.Equal(("B", (DateTime?)null), (emote.Name, emote.FirstSeenAt));
    }

    // 20 — an archived emote with usage in the active set and no live entry: seed untouched, no deferral.
    [Fact]
    public async Task ArchivedEmoteWithActiveSetUsage_WithoutLiveEntry_SeedStaysAndNothingIsDeferred()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_archived_usage");
        var gone = await SeedEmoteAsync(db, channel, "gone", "Gone", archived: true, archivedAt: T2);
        var seed = await SeedSeedAsync(db, gone);
        await SeedUsageAsync(db, gone, seed, SetA, 3, 3);
        var before = Columns(await SlotAsync(db, seed.Id));
        var logger = new RecordingLogger<SevenTvSyncService>();

        await SyncAsync(db, channel, true, SetA, logger, Live("other", "Other", T1));

        Assert.Equal(before, Columns(await SlotAsync(db, seed.Id)));
        Assert.Equal((2, 6), (await UsageBySlotAsync(db, gone.Id, SetA))[seed.Id]);
        Assert.Empty(Lines(logger, SettleLine));
    }

    // 22 — on → off (rename in 7TV) → on: the date binds the same slot back and renames it.
    [Fact]
    public async Task OnOffOn_RenameWhileOff_SameSlotIsRenamed()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_on_off_on");

        await SyncOnAsync(db, channel, Live("x", "A", T1));
        var x = await EmoteAsync(db, channel, "x");
        var slot = Assert.Single(await SlotsAsync(db, x.Id));
        await SyncAsync(db, channel, false, SetA, null, Live("x", "C", T1));
        Assert.Equal("A", (await SlotAsync(db, slot.Id)).Name);
        Assert.Equal("C", (await EmoteAsync(db, channel, "x")).Name);

        await SyncOnAsync(db, channel, Live("x", "C", T1));

        var after = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal((slot.Id, "C", T1), (after.Id, after.Name, after.AddedAt));
    }

    // 6.4 (1) — flag off over seeds and a multi-alias emote: no slot write at all, K1 as before.
    [Fact]
    public async Task FlagOff_SeedsAndMultiAliasEmote_SlotsAndSlotIdsStayByteIdentical()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_flag_off");
        var x = await SeedEmoteAsync(db, channel, "x", "ome44");
        var seedA = await SeedSeedAsync(db, x);
        var seedB = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seedA, SetA, 5);
        await SeedUsageAsync(db, x, seedB, SetB, 6);
        var y = await SeedEmoteAsync(db, channel, "y", "Y", firstSeenAt: T1);
        await SeedSlotAsync(db, y, "Y", T1);
        var slotsBefore = await AllSlotColumnsAsync(db, channel);
        var slotIdsBefore = await UsageSlotIdsAsync(db, channel);

        var result = await SyncAsync(db, channel, false, SetA, null, Live("x", "ome44", T1), Live("x", "hob44", T2), Live("y", "Z", T1));

        Assert.Equal(slotsBefore, await AllSlotColumnsAsync(db, channel));
        Assert.Equal(slotIdsBefore, await UsageSlotIdsAsync(db, channel));
        // K1: the last alias of the duplicated id wins, and the emote takes the last entry's date.
        var emote = await EmoteAsync(db, channel, "x");
        Assert.Equal(("hob44", (DateTime?)T2), (emote.Name, emote.FirstSeenAt));
        Assert.Equal("Z", (await EmoteAsync(db, channel, "y")).Name);
        Assert.True(result!.HasChanges);
    }

    // 6.7 tracker — flag on: a collision through a second alias is reported although Emote.Name differs.
    [Fact]
    public async Task Tracker_On_CollisionThroughASecondAlias_LogsTheWarning()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_tracker_on");
        var logger = new RecordingLogger<SevenTvSyncService>();

        await SyncAsync(db, channel, true, SetA, logger, Live("a", "One", T1), Live("a", "Shared", T2), Live("b", "Shared", T3));

        var line = Assert.Single(Lines(logger, "shared by more than one emote"));
        Assert.Contains("Shared", line);
    }

    // Two aliases of one emote are no collision.
    [Fact]
    public async Task Tracker_On_TwoAliasesOfOneEmote_LogsNothing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_tracker_alias");
        var logger = new RecordingLogger<SevenTvSyncService>();

        await SyncAsync(db, channel, true, SetA, logger, Live("a", "One", T1), Live("a", "Two", T2));

        Assert.Empty(Lines(logger, "shared by more than one emote"));
    }

    // Flag off: the tracker reads Emote.Name as before.
    [Fact]
    public async Task Tracker_Off_TwoEmotesWithTheSameName_LogsTheWarning()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "slot_tracker_off");
        var logger = new RecordingLogger<SevenTvSyncService>();

        await SyncAsync(db, channel, false, SetA, logger, Live("a", "Same", T1), Live("b", "Same", T2));

        Assert.Single(Lines(logger, "shared by more than one emote"));
    }

    private static async Task<bool> LeaveRecordedAsync(AppDbContext db, Channel channel, string sevenTvId) =>
        await db.EmoteSetLeaveObservations.AsNoTracking()
            .AnyAsync(o => o.ChannelId == channel.Id && o.SevenTvEmoteId == sevenTvId && o.SevenTvEmoteSetId == SetA);

    private static async Task<List<string>> AllSlotColumnsAsync(AppDbContext db, Channel channel)
    {
        var slots = await db.EmoteSlots.AsNoTracking().Where(s => s.Emote.ChannelId == channel.Id).ToListAsync();
        return [.. slots.Select(Columns).Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> UsageSlotIdsAsync(AppDbContext db, Channel channel)
    {
        var rows = await db.UsageStats.AsNoTracking()
            .Where(u => u.Emote.ChannelId == channel.Id)
            .Select(u => u.Id + ":" + u.SlotId)
            .ToListAsync();
        return [.. rows.Order(StringComparer.Ordinal)];
    }

    private static UsageStat AddRow(
        AppDbContext db, Emote emote, EmoteSlot slot, string setId, DateOnly date, (int Uses, int Bot, int Shared) counts, UsageStatSource source)
    {
        var row = new UsageStat
        {
            EmoteId = emote.Id,
            SlotId = slot.Id,
            EmoteSetId = setId,
            Date = date,
            UseCount = counts.Uses,
            BotUseCount = counts.Bot,
            SharedChatUseCount = counts.Shared,
            Source = source
        };
        db.UsageStats.Add(row);
        return row;
    }

    private static async Task<List<long>> UsageRowIdsAsync(AppDbContext db, string slotId) =>
        [.. (await db.UsageStats.AsNoTracking().Where(u => u.SlotId == slotId).Select(u => u.Id).ToListAsync()).Order()];
}
