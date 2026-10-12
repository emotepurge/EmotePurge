using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Emote slots plan 376, child 6 (section 4, 5.5): the set-centric reports with entries and the
// arbitration on B3 (the origin of an entry is the channel its slot id belongs to). The flag is on unless a case says otherwise; every
// case runs its report, sync and reads on fresh contexts, like the api and the worker do. Set and
// emote ids are alphanumeric and unique per case: the observation upsert drops anything else, and a
// report hits every channel of the shared database that has its set active.
public partial class EmoteServiceTests
{
    private static readonly DateTime T9 = new(2026, 10, 9, 10, 0, 0, 999, DateTimeKind.Utc);
    private static readonly DateOnly UsageFrom = new(2026, 8, 1);
    private static readonly DateOnly UsageTo = new(2026, 8, 31);

    // ---------------------------------------------------------------- AK 41: partial delete, retry, last slot

    [Fact]
    public async Task Entries_Ak41_PartialDelete_ArchivesOnlyThatSlot_RetryIsANoOp_TheLastSlotArchivesTheEmoteWithOneLeave()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var (channel, emote, ome44, hob44) = await SeedTwoSlotEmoteAsync("slotrpt_ak41", set, x);

        var first = await DeleteAsync(set, [x], [Entry(x, "hob44", hob44.Id, T2)]);

        Assert.Null(first.Rejection);
        var hit = Assert.Single(first.Channels);
        Assert.Equal((1, 1), (hit.Count, hit.NewlyChangedCount));
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.True((await SlotAsync(verify, hob44.Id)).IsArchived);
            Assert.NotNull((await SlotAsync(verify, hob44.Id)).ArchivedAt);
            Assert.False((await SlotAsync(verify, ome44.Id)).IsArchived);
            var row = await EmoteAsync(verify, channel, x);
            Assert.False(row.IsArchived);
            Assert.Equal(("ome44", (DateTime?)T1), (row.Name, row.FirstSeenAt));
            Assert.Equal(0, await LeaveRowsAsync(verify, channel, set));
        }

        var retry = await DeleteAsync(set, [x], [Entry(x, "hob44", hob44.Id, T2)]);

        Assert.Equal((1, 0), (Assert.Single(retry.Channels).Count, Assert.Single(retry.Channels).NewlyChangedCount));
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.False((await EmoteAsync(verify, channel, x)).IsArchived);
            Assert.Equal(0, await LeaveRowsAsync(verify, channel, set));
        }

        await DeleteAsync(set, [x], [Entry(x, "ome44", ome44.Id, T1)]);
        DateTime observedAt;
        await using (var verify = fixture.CreateDbContext())
        {
            var row = await EmoteAsync(verify, channel, x);
            Assert.True(row.IsArchived);
            Assert.NotNull(row.ArchivedAt);
            Assert.All(await SlotsAsync(verify, emote.Id), s => Assert.True(s.IsArchived));
            observedAt = Assert.Single(await LeaveRowListAsync(verify, channel, set)).LastObservedAtUtc;
        }

        // The repeated last report finds nothing active: no second leave, its timestamp stays.
        await DeleteAsync(set, [x], [Entry(x, "ome44", ome44.Id, T1)]);
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.Equal(observedAt, Assert.Single(await LeaveRowListAsync(verify, channel, set)).LastObservedAtUtc);
        }
    }

    // D13 through the planner's derivation: removing the oldest slot moves the emote's name and date to
    // the next oldest active slot.
    [Fact]
    public async Task Entries_DeletingTheOldestSlot_DerivesNameAndDateFromTheNextOldest()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var (channel, _, ome44, _) = await SeedTwoSlotEmoteAsync("slotrpt_d13", set, x);

        await DeleteAsync(set, [x], [Entry(x, "ome44", ome44.Id, T1)]);

        await using var verify = fixture.CreateDbContext();
        var row = await EmoteAsync(verify, channel, x);
        Assert.Equal((false, "hob44", (DateTime?)T2), (row.IsArchived, row.Name, row.FirstSeenAt));
        Assert.Equal(0, await LeaveRowsAsync(verify, channel, set));
    }

    // ---------------------------------------------------------------- AK 52: a late report after D7

    // AK 52: the slot was deleted, REST resumed it per D7 on t3 before the report arrived; the late
    // report with t1 is a stale no-op without a leave.
    [Fact]
    public async Task Entries_Ak52_LateDeleteAfterAD7Resumption_IsANoOpWithoutALeave()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var channel = await SeedOnAsync("slotrpt_ak52", set);
        var s1 = await SeedSingleSlotEmoteAsync(channel, x, "aaa", T1, archived: true);

        await SyncOnSetAsync(channel, set, Live(x, "aaa", T3));
        await using (var check = fixture.CreateDbContext())
        {
            var resumed = await SlotAsync(check, s1.Id);
            Assert.Equal((false, (DateTime?)T3, true), (resumed.IsArchived, resumed.AddedAt, resumed.AddedAtFromRest));
        }

        var result = await DeleteAsync(set, [x], [Entry(x, "aaa", s1.Id, T1)]);

        Assert.Equal((1, 0), (Assert.Single(result.Channels).Count, Assert.Single(result.Channels).NewlyChangedCount));
        await using var verify = fixture.CreateDbContext();
        var slot = await SlotAsync(verify, s1.Id);
        Assert.Equal((false, (DateTime?)T3), (slot.IsArchived, slot.AddedAt));
        Assert.False((await EmoteAsync(verify, channel, x)).IsArchived);
        Assert.Equal(0, await LeaveRowsAsync(verify, channel, set));
    }

    // ---------------------------------------------------------------- AK 42, 46, 55: restore

    // AK 42: the matching archived generation is reactivated once, with a provisional date; its usage
    // stays on it, and the emote row comes back with a fresh entry stamp and D13 from the slot.
    [Fact]
    public async Task Entries_Ak42_Restore_ReactivatesTheMatchingArchivedGeneration_Provisionally_UsageStays()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var channel = await SeedOnAsync("slotrpt_ak42", set);
        var s1 = await SeedSingleSlotEmoteAsync(channel, x, "aaa", T1, archived: true, usage: [3, 4]);
        var before = DateTime.UtcNow;

        var result = await RestoreAsync(set, [x], [Entry(x, "aaa", s1.Id, T1)]);

        Assert.Equal((1, 1), (Assert.Single(result.Channels).Count, Assert.Single(result.Channels).NewlyChangedCount));
        await using var verify = fixture.CreateDbContext();
        var slot = await SlotAsync(verify, s1.Id);
        Assert.Equal((false, (DateTime?)null, false), (slot.IsArchived, slot.ArchivedAt, slot.AddedAtFromRest));
        Assert.True(slot.AddedAt >= before.AddMilliseconds(-1));
        var row = await EmoteAsync(verify, channel, x);
        Assert.False(row.IsArchived);
        Assert.Null(row.ArchivedAt);
        Assert.True(row.LastEnteredSetAtUtc >= before.AddMilliseconds(-1));
        Assert.Equal(("aaa", slot.AddedAt), (row.Name, row.FirstSeenAt));
        Assert.Equal((2, 7), (await UsageBySlotAsync(verify, row.Id, set))[s1.Id]);
    }

    // AK 46: delete and undo with entries, then REST: the same slot id becomes authoritative on the new
    // date, and /totals shows one row with the old and the new usage.
    [Fact]
    public async Task Entries_Ak46_DeleteThenRestore_ThenRest_KeepsTheSlotId_AndTotalsShowOldAndNewUsage()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var channel = await SeedOnAsync("slotrpt_ak46", set);
        var s1 = await SeedSingleSlotEmoteAsync(channel, x, "aaa", T1, usage: [3, 4]);

        await DeleteAsync(set, [x], [Entry(x, "aaa", s1.Id, T1)]);
        await RestoreAsync(set, [x], [Entry(x, "aaa", s1.Id, T1)]);
        await SyncOnSetAsync(channel, set, Live(x, "aaa", T9));
        await AddUsageAsync(s1, set, new DateOnly(2026, 8, 20), 10);

        await AssertResumedOnTheSameSlotAsync(channel, set, x, s1.Id, expectedUses: 17);
    }

    // AK 55 (a): the restore was saved but its answer was lost; the identical retry changes nothing, and
    // REST t9 afterwards makes the same slot authoritative on t9.
    [Fact]
    public async Task Entries_Ak55a_RestoreRetry_IsANoOp_ThenRestAuthorizesTheNewDateOnTheSameSlot()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var channel = await SeedOnAsync("slotrpt_ak55a", set);
        var s1 = await SeedSingleSlotEmoteAsync(channel, x, "aaa", T1, archived: true, usage: [2]);
        await RestoreAsync(set, [x], [Entry(x, "aaa", s1.Id, T1)]);
        string restored;
        await using (var check = fixture.CreateDbContext())
        {
            restored = Columns(await SlotAsync(check, s1.Id));
        }

        var retry = await RestoreAsync(set, [x], [Entry(x, "aaa", s1.Id, T1)]);

        Assert.Equal((1, 0), (Assert.Single(retry.Channels).Count, Assert.Single(retry.Channels).NewlyChangedCount));
        await using (var check = fixture.CreateDbContext())
        {
            Assert.Equal(restored, Columns(await SlotAsync(check, s1.Id)));
        }

        await SyncOnSetAsync(channel, set, Live(x, "aaa", T9));
        await AssertResumedOnTheSameSlotAsync(channel, set, x, s1.Id, expectedUses: 2);
    }

    // AK 55 (b): REST resumed the slot per D7 on t9 first; the restore report with the old generation t1
    // answers without touching the date.
    [Fact]
    public async Task Entries_Ak55b_RestResumedFirst_TheRestoreWithTheOldGeneration_KeepsT9()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var channel = await SeedOnAsync("slotrpt_ak55b", set);
        var s1 = await SeedSingleSlotEmoteAsync(channel, x, "aaa", T1, archived: true, usage: [2]);
        await SyncOnSetAsync(channel, set, Live(x, "aaa", T9));

        var result = await RestoreAsync(set, [x], [Entry(x, "aaa", s1.Id, T1)]);

        Assert.Equal((1, 0), (Assert.Single(result.Channels).Count, Assert.Single(result.Channels).NewlyChangedCount));
        await AssertResumedOnTheSameSlotAsync(channel, set, x, s1.Id, expectedUses: 2);
    }

    // ---------------------------------------------------------------- R3 a: the flag off

    // R3 a: with the flag off, entries are ignored — the report runs at the emote level and writes no
    // slot, and its audit entry is the one from before (no entryCount).
    [Fact]
    public async Task Entries_FlagOff_AreIgnored_TheReportRunsAsWithoutThem()
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var (channel, xRow, _, hob44) = await SeedTwoSlotEmoteAsync("slotrpt_flagoff", set, x);

        var result = await DeleteAsync(set, [x], [Entry(x, "hob44", hob44.Id, T2)], on: false);

        Assert.Equal((1, 1), (Assert.Single(result.Channels).Count, Assert.Single(result.Channels).NewlyChangedCount));
        await using var verify = fixture.CreateDbContext();
        Assert.True((await EmoteAsync(verify, channel, x)).IsArchived);
        Assert.All(await SlotsAsync(verify, xRow.Id), s => Assert.False(s.IsArchived));
        Assert.Equal(1, await LeaveRowsAsync(verify, channel, set));
        var entry = Assert.Single(await AuditDetailsAsync(verify, set));
        Assert.False(entry.TryGetProperty("entryCount", out _));
    }

    // ---------------------------------------------------------------- AK 54 and the arbitration on B3

    // AK 54: a shared set with local slot ids; the origin slot of channel A and the fan-out slot of
    // channel B (same 7TV date) are archived and restored each locally — no 400 for B's slot id.
    [Fact]
    public async Task Entries_Ak54_SharedSet_DeleteAndRestoreActOnEachLocalSlot()
    {
        var shared = await SeedSharedAsync("slotrpt_ak54");

        var deleted = await DeleteAsync(shared.Set, [shared.X], [Entry(shared.X, "aaa", shared.SlotA.Id, T1)], expected: shared.A.ChannelName);

        Assert.Null(deleted.Rejection);
        Assert.Equal([(shared.A.ChannelName, 1, 1), (shared.B.ChannelName, 1, 1)], deleted.Channels.Select(c => (c.ChannelName, c.Count, c.NewlyChangedCount)));
        await using (var check = fixture.CreateDbContext())
        {
            Assert.True((await SlotAsync(check, shared.SlotA.Id)).IsArchived);
            Assert.True((await SlotAsync(check, shared.SlotB.Id)).IsArchived);
            Assert.Equal(1, await LeaveRowsAsync(check, shared.A, shared.Set));
            Assert.Equal(1, await LeaveRowsAsync(check, shared.B, shared.Set));
        }

        var restored = await RestoreAsync(shared.Set, [shared.X], [Entry(shared.X, "aaa", shared.SlotA.Id, T1)], expected: shared.A.ChannelName);

        Assert.Equal([1, 1], restored.Channels.Select(c => c.NewlyChangedCount));
        await using var verify = fixture.CreateDbContext();
        Assert.False((await SlotAsync(verify, shared.SlotA.Id)).IsArchived);
        Assert.False((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
        Assert.False((await EmoteAsync(verify, shared.A, shared.X)).IsArchived);
        Assert.False((await EmoteAsync(verify, shared.B, shared.X)).IsArchived);
    }

    // Arbitration (a): the undo of a shared-set delete expects the owner's channel B but carries the slot
    // id of channel A. A is the origin (generation checked there), B resolves its own slot by date, and
    // the report succeeds — before, "origin = expectedChannelName" would have refused it after the 7TV
    // write. The origin's generation check still applies: a stale date leaves A untouched.
    [Fact]
    public async Task Entries_ArbitrationA_ExpectedChannelB_SlotIdOfA_OriginIsA_BFansOut()
    {
        var shared = await SeedSharedAsync("slotrpt_arba");

        var result = await DeleteAsync(shared.Set, [shared.X], [Entry(shared.X, "aaa", shared.SlotA.Id, T1)], expected: shared.B.ChannelName);

        Assert.Null(result.Rejection);
        Assert.Null(result.UnresolvedChannel);
        Assert.Equal([1, 1], result.Channels.Select(c => c.NewlyChangedCount));
        await using var verify = fixture.CreateDbContext();
        Assert.True((await SlotAsync(verify, shared.SlotA.Id)).IsArchived);
        Assert.True((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
    }

    [Fact]
    public async Task Entries_ArbitrationA_StaleGenerationInTheOrigin_LeavesTheOriginAlone_WhileBStillFansOut()
    {
        var shared = await SeedSharedAsync("slotrpt_arbastale");
        await using (var db = fixture.CreateDbContext())
        {
            // The origin's slot was re-keyed (D7) to t3 meanwhile; B still holds t1.
            await db.EmoteSlots.Where(s => s.Id == shared.SlotA.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AddedAt, T3));
        }

        var result = await DeleteAsync(shared.Set, [shared.X], [Entry(shared.X, "aaa", shared.SlotA.Id, T1)], expected: shared.B.ChannelName);

        Assert.Equal([(shared.A.ChannelName, 0), (shared.B.ChannelName, 1)], result.Channels.Select(c => (c.ChannelName, c.NewlyChangedCount)));
        await using var verify = fixture.CreateDbContext();
        Assert.False((await SlotAsync(verify, shared.SlotA.Id)).IsArchived);
        Assert.True((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
    }

    // Arbitration (b): the channel the slot id belongs to is no hit (broadcaster lock, block list), so
    // the entry has no origin: only B is written, by date, and A stays untouched.
    [Theory]
    [InlineData("locked")]
    [InlineData("blocked")]
    public async Task Entries_ArbitrationB_TheSlotsChannelIsNoHit_OnlyTheOtherChannelFansOut(string reason)
    {
        var shared = await SeedSharedAsync($"slotrpt_arbb{reason[0]}");
        var filter = Substitute.For<IExcludedChannelFilter>();
        if (reason == "blocked")
        {
            filter.IsExcluded(shared.A.TwitchChannelId).Returns(true);
        }

        await using var lockScope = reason == "locked" ? await BroadcasterLockScope.CreateAsync(fixture, shared.A.TwitchChannelId!) : null;
        try
        {
            var result = await DeleteAsync(
                shared.Set, [shared.X], [Entry(shared.X, "aaa", shared.SlotA.Id, T1)], expected: shared.B.ChannelName, filter: filter);

            Assert.Null(result.Rejection);
            Assert.Equal([(shared.B.ChannelName, 1)], result.Channels.Select(c => (c.ChannelName, c.NewlyChangedCount)));
            await using var verify = fixture.CreateDbContext();
            Assert.False((await SlotAsync(verify, shared.SlotA.Id)).IsArchived);
            Assert.False((await EmoteAsync(verify, shared.A, shared.X)).IsArchived);
            Assert.Equal(0, await LeaveRowsAsync(verify, shared.A, shared.Set));
            Assert.True((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
        }
        finally
        {
            await DeactivateAsync(shared.A.Id);
        }
    }

    // Arbitration (c): an unknown slot id (its row is gone) is no error — the entry fans out to every
    // hit by date, the report succeeds, and its audit entries carry the entries they resolved.
    [Fact]
    public async Task Entries_ArbitrationC_UnknownSlotId_FansOut_AndAuditsWithoutARejection()
    {
        var shared = await SeedSharedAsync("slotrpt_arbc");

        var result = await DeleteAsync(shared.Set, [shared.X], [Entry(shared.X, "aaa", Guid.NewGuid().ToString(), T1)], expected: shared.B.ChannelName);

        Assert.Null(result.Rejection);
        Assert.Equal([1, 1], result.Channels.Select(c => c.NewlyChangedCount));
        await using var verify = fixture.CreateDbContext();
        Assert.True((await SlotAsync(verify, shared.SlotA.Id)).IsArchived);
        Assert.True((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
        var audit = await AuditDetailsAsync(verify, shared.Set);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, details => Assert.Equal(1, details.GetProperty("entryCount").GetInt32()));
    }

    // Arbitration (d): a slot id of another 7TV emote is a contract violation — the whole report is
    // refused before any write: no row, slot, leave or audit entry.
    [Fact]
    public async Task Entries_ArbitrationD_SlotIdOfAnotherEmote_RefusesTheWholeReport_AndWritesNothing()
    {
        var shared = await SeedSharedAsync("slotrpt_arbd");
        var z = NewSevenTvId();
        var zSlot = await SeedSingleSlotEmoteAsync(shared.A, z, "zzz", T1);
        var y = NewSevenTvId();
        await SeedSingleSlotEmoteAsync(shared.A, y, "yyy", T1);

        var result = await DeleteAsync(
            shared.Set, [shared.X, y], [Entry(shared.X, "aaa", zSlot.Id, T1)], expected: shared.A.ChannelName);

        Assert.Equal(SyncInSetRejection.SlotOfAnotherEmote, result.Rejection);
        Assert.Empty(result.Channels);
        Assert.Null(result.UnresolvedChannel);
        await using var verify = fixture.CreateDbContext();
        Assert.False((await SlotAsync(verify, shared.SlotA.Id)).IsArchived);
        Assert.False((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
        Assert.False((await SlotAsync(verify, zSlot.Id)).IsArchived);
        Assert.False((await EmoteAsync(verify, shared.A, y)).IsArchived);
        Assert.Equal(0, await LeaveRowsAsync(verify, shared.A, shared.Set));
        Assert.Empty(await AuditDetailsAsync(verify, shared.Set));
    }

    // In the origin the slot id decides, not the date: two slots of the emote share t1 there, the entry
    // carries no alias (it may be missing, plan 376 4.1), and still exactly the reported slot is archived.
    // A fan-out channel with the same tie would write nothing.
    [Fact]
    public async Task Entries_InTheOrigin_TheSlotIdDecides_EvenOnADateTieWithoutAlias()
    {
        var shared = await SeedSharedAsync("slotrpt_origintie");
        EmoteSlot twinA;
        EmoteSlot twinB;
        await using (var db = fixture.CreateDbContext())
        {
            twinA = await SeedSlotAsync(db, await EmoteAsync(db, shared.A, shared.X), "bbb", T1);
            twinB = await SeedSlotAsync(db, await EmoteAsync(db, shared.B, shared.X), "bbb", T1);
        }

        var result = await DeleteAsync(shared.Set, [shared.X], [Entry(shared.X, alias: null, twinA.Id, T1)], expected: shared.B.ChannelName);

        Assert.Equal([(shared.A.ChannelName, 1), (shared.B.ChannelName, 0)], result.Channels.Select(c => (c.ChannelName, c.NewlyChangedCount)));
        await using var verify = fixture.CreateDbContext();
        Assert.True((await SlotAsync(verify, twinA.Id)).IsArchived);
        Assert.False((await SlotAsync(verify, shared.SlotA.Id)).IsArchived);
        Assert.False((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
        Assert.False((await SlotAsync(verify, twinB.Id)).IsArchived);
    }

    // D4 tie in the fan-out: two authoritative slots of the emote share the date in channel B, so the
    // alias decides; without a slot of that name nothing is written there (REST reconciles).
    [Fact]
    public async Task Entries_FanOutTie_TheAliasDecides_ANonMatchingAliasWritesNothing()
    {
        var shared = await SeedSharedAsync("slotrpt_tie");
        EmoteSlot twin;
        await using (var db = fixture.CreateDbContext())
        {
            var bRow = await EmoteAsync(db, shared.B, shared.X);
            twin = await SeedSlotAsync(db, bRow, "bbb", T1);
        }

        await DeleteAsync(shared.Set, [shared.X], [Entry(shared.X, "bbb", shared.SlotA.Id, T1)], expected: shared.A.ChannelName);

        await using (var verify = fixture.CreateDbContext())
        {
            Assert.True((await SlotAsync(verify, twin.Id)).IsArchived);
            Assert.False((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
        }

        await DeleteAsync(shared.Set, [shared.X], [Entry(shared.X, "ccc", Guid.NewGuid().ToString(), T1)], expected: shared.A.ChannelName);

        await using (var verify = fixture.CreateDbContext())
        {
            // B still has two authoritative slots on t1 and none of them is named ccc.
            Assert.False((await SlotAsync(verify, shared.SlotB.Id)).IsArchived);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string NewSet() => "01RPT" + Guid.NewGuid().ToString("N")[..21].ToUpperInvariant();

    private static string NewSevenTvId() => "s" + Guid.NewGuid().ToString("N")[..23];

    private static SyncInSetEntryDto Entry(string sevenTvEmoteId, string? alias, string? slotId, DateTime? addedAt) =>
        new(sevenTvEmoteId, alias, slotId, addedAt);

    private static EmoteService SlotService(AppDbContext db, bool on, IExcludedChannelFilter? filter = null) =>
        new(
            db, NullLogger<EmoteService>.Instance, filter ?? Substitute.For<IExcludedChannelFilter>(),
            new BroadcasterChannelLockService(db), new SlotReconcileOptions { Enabled = on });

    private async Task<SyncDeletedInSetResultDto> DeleteAsync(
        string set, IReadOnlyList<string> ids, IReadOnlyList<SyncInSetEntryDto>? entries,
        bool on = true, string? expected = null, IExcludedChannelFilter? filter = null)
    {
        await using var db = fixture.CreateDbContext();
        return await SlotService(db, on, filter).MarkDeletedInSetAsync(
            set, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ids, expected, Actor, entries);
    }

    private async Task<SyncRestoredInSetResultDto> RestoreAsync(
        string set, IReadOnlyList<string> ids, IReadOnlyList<SyncInSetEntryDto>? entries, bool on = true, string? expected = null)
    {
        await using var db = fixture.CreateDbContext();
        return await SlotService(db, on).MarkRestoredInSetAsync(
            set, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, ids, expected, Actor, entries);
    }

    private async Task SyncOnSetAsync(Channel channel, string set, params SevenTvEmote[] live)
    {
        await using var db = fixture.CreateDbContext();
        Assert.NotNull(await SyncAsync(db, channel, slotsEnabled: true, set, logger: null, live));
    }

    private async Task<Channel> SeedOnAsync(string name, string set)
    {
        await using var db = fixture.CreateDbContext();
        return await SeedChannelAsync(db, name, set);
    }

    // An emote of the channel with one authoritative slot; the emote's name, date and state follow the
    // slot. enteredMinutesAgo stamps LastEnteredSetAtUtc (null = unknown, i.e. long ago).
    private async Task<EmoteSlot> SeedSingleSlotEmoteAsync(
        Channel channel, string sevenTvId, string name, DateTime addedAt,
        bool archived = false, int? enteredMinutesAgo = null, int[]? usage = null)
    {
        await using var db = fixture.CreateDbContext();
        var emote = await SeedEmoteAsync(db, channel, sevenTvId, name, archived, firstSeenAt: addedAt, archivedAt: archived ? T5 : null);
        if (enteredMinutesAgo is { } minutes)
        {
            emote.LastEnteredSetAtUtc = DateTime.UtcNow.AddMinutes(-minutes);
            await db.SaveChangesAsync();
        }

        var slot = await SeedSlotAsync(db, emote, name, addedAt, archived: archived, archivedAt: archived ? T5 : null);
        if (usage is not null)
        {
            await SeedUsageAsync(db, emote, slot, channel.ActiveEmoteSetId!, usage);
        }

        return slot;
    }

    private async Task<EmoteSlot> SeedSeedOfAsync(Channel channel, string sevenTvId)
    {
        await using var db = fixture.CreateDbContext();
        return await SeedSeedAsync(db, await EmoteAsync(db, channel, sevenTvId));
    }

    // AK 41's emote: one 7TV id under two aliases, ome44 (t1, the oldest, so the emote's name) and
    // hob44 (t2), both authoritative and active.
    private async Task<(Channel Channel, Emote Emote, EmoteSlot Ome44, EmoteSlot Hob44)> SeedTwoSlotEmoteAsync(string name, string set, string x)
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, name, set);
        var emote = await SeedEmoteAsync(db, channel, x, "ome44", firstSeenAt: T1);
        var ome44 = await SeedSlotAsync(db, emote, "ome44", T1);
        var hob44 = await SeedSlotAsync(db, emote, "hob44", T2);
        return (channel, emote, ome44, hob44);
    }

    // AK 54's shared set: channels A and B have the same set active, and the same 7TV emote under the
    // same alias and date — each with its own local slot.
    private async Task<SharedSet> SeedSharedAsync(string stem)
    {
        var set = NewSet();
        var x = NewSevenTvId();
        var a = await SeedOnAsync($"{stem}_a", set);
        var b = await SeedOnAsync($"{stem}_b", set);
        var slotA = await SeedSingleSlotEmoteAsync(a, x, "aaa", T1);
        var slotB = await SeedSingleSlotEmoteAsync(b, x, "aaa", T1);
        return new SharedSet(set, x, a, b, slotA, slotB);
    }

    private async Task AddUsageAsync(EmoteSlot slot, string set, DateOnly date, int useCount)
    {
        await using var db = fixture.CreateDbContext();
        db.UsageStats.Add(new UsageStat { EmoteId = slot.EmoteId, SlotId = slot.Id, EmoteSetId = set, Date = date, UseCount = useCount });
        await db.SaveChangesAsync();
    }

    // The slot is authoritative on t9 again, no second slot was created, and /totals (flag on) shows one
    // card for it that carries all of its usage.
    private async Task AssertResumedOnTheSameSlotAsync(Channel channel, string set, string x, string slotId, int expectedUses)
    {
        await using var db = fixture.CreateDbContext();
        var emote = await EmoteAsync(db, channel, x);
        var slot = Assert.Single(await SlotsAsync(db, emote.Id), s => !s.SeedUnsettled);
        Assert.Equal((slotId, false, (DateTime?)T9, true), (slot.Id, slot.IsArchived, slot.AddedAt, slot.AddedAtFromRest));
        var totals = await new UsageStatQueryService(db, new SlotReconcileOptions { Enabled = true })
            .GetUsageContextAsync(channel.ChannelName, UsageFrom, UsageTo, set);
        var row = Assert.Single(totals, t => t.EmoteId == emote.Id);
        Assert.Equal((slotId, expectedUses), (row.SlotId, row.TotalUseCount));
    }

    private static Task<int> LeaveRowsAsync(AppDbContext db, Channel channel, string set) =>
        db.EmoteSetLeaveObservations.AsNoTracking().CountAsync(o => o.ChannelId == channel.Id && o.SevenTvEmoteSetId == set);

    private static Task<List<EmoteSetLeaveObservation>> LeaveRowListAsync(AppDbContext db, Channel channel, string set) =>
        db.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id && o.SevenTvEmoteSetId == set)
            .ToListAsync();

    // Parsed, because a no-tracking read returns Postgres' jsonb rendering.
    private static async Task<List<JsonElement>> AuditDetailsAsync(AppDbContext db, string set)
    {
        var details = await db.AuditLogEntries.AsNoTracking()
            .Where(a => a.TargetType == "emoteSet" && a.TargetId == set)
            .Select(a => a.DetailsJson)
            .ToListAsync();
        return details.ConvertAll(json => JsonDocument.Parse(json!).RootElement.Clone());
    }

    private sealed record SharedSet(string Set, string X, Channel A, Channel B, EmoteSlot SlotA, EmoteSlot SlotB);
}
