using EmotePurge.Core.Entities;
using EmotePurge.Core.Matching;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// EventAPI deltas per (id, name) (plan #374 7.3; spec §7.2, D17, AK 19, AK 20): the nine spec cases (AK
// 20's in-place rename on one slot, operator decision R4) and the counter-examples of the adversarial
// review end to end — map, flush, REST afterwards. The planner's rule table itself is unit-tested
// (EmoteSlotDeltaPlannerTests); here the service's half is pinned: what is read behind the lock, what is
// written, what stays byte-equal, the leaves and the outcomes.
[Collection("Postgres")]
public class SevenTvSyncServiceDeltaSlotTests(PostgresFixture fixture)
{
    private const string AmbiguousLine = "is ambiguous per slot";
    private static readonly AuditActor Actor = new("tw_delta_admin", "delta_admin");

    // ------------------------------------------------------------------ the nine spec cases

    // (1) AK 19: a push of a second name adds a provisional slot; the emote keeps its older name. The map
    // is refreshed with the outcome, so the push slot counts at once (plan #373 E7), and a flush writes
    // onto exactly that slot.
    [Fact]
    public async Task Push_ASecondName_AddsAProvisionalSlot_KeepsTheEmoteName_AndCountsAtOnce()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_push2");
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        var probeA = await SeedSlotAsync(db, x, "probeA", T1);
        var cache = new EmoteMatchCache();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await ApplyAsync(db, channel, cache, Delta(pushed: [Push("x", "probeB")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var slots = await SlotsAsync(db, x.Id);
        Assert.Equal(["probeA", "probeB"], slots.Select(s => s.Name));
        Assert.Equal(Columns(probeA), Columns(slots[0]));
        var probeB = slots[1];
        Assert.Equal((false, false, false), (probeB.AddedAtFromRest, probeB.IsArchived, probeB.SeedUnsettled));
        Assert.True(probeB.AddedAt >= before);
        Assert.Equal(("probeA", (DateTime?)T1), ((await EmoteAsync(db, channel, "x")).Name, (await EmoteAsync(db, channel, "x")).FirstSeenAt));

        var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.Equal((probeA.Id, probeB.Id), (map["probeA"], map["probeB"]));
        await FlushAsync(db, map, "probeB");
        Assert.Equal((1, 1), (await UsageBySlotAsync(db, x.Id, SetA))[probeB.Id]);
        Assert.False((await UsageBySlotAsync(db, x.Id, SetA)).ContainsKey(probeA.Id));
    }

    // (2) AK 19: REST was faster — a push of a name that already has an active own slot is a no-op.
    [Fact]
    public async Task Push_AnActiveName_IsANoOp_AndLeavesEveryRowAsItWas()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_pushdup");
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "probeA", T1);
        await SeedSlotAsync(db, x, "probeB", T2);
        var before = await StateAsync(db, channel);

        var result = await ApplyAsync(db, channel, null, Delta(pushed: [Push("x", "probeB")]));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
    }

    // (3) AK 19 / D7: a push under the name of an archived slot writes nothing and asks for REST; REST
    // then resumes the same slot with the new date and its history (AK 47).
    [Fact]
    public async Task Push_UnderAnArchivedName_IsAmbiguous_AndRestResumesTheSameSlotWithItsHistory()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_pushd7");
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "probeA", T1);
        var probeB = await SeedSlotAsync(db, x, "probeB", T2, archived: true, archivedAt: T5);
        await SeedUsageAsync(db, x, probeB, SetA, 4, 6);
        var before = await StateAsync(db, channel);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(db, channel, null, Delta(pushed: [Push("x", "probeB")]), logger);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(channel.ChannelName, result.ChannelName);
        Assert.Equal(before, await StateAsync(db, channel));
        AssertAmbiguousLine(logger, SlotDeltaAmbiguity.ArchivedNamesake, "x");

        await SyncOnAsync(db, channel, Live("x", "probeA", T1), Live("x", "probeB", T3));

        var resumed = await SlotAsync(db, probeB.Id);
        Assert.Equal(("probeB", (DateTime?)T3, true, false, (DateTime?)null), (resumed.Name, resumed.AddedAt, resumed.AddedAtFromRest, resumed.IsArchived, resumed.ArchivedAt));
        Assert.Equal(2, (await SlotsAsync(db, x.Id)).Count);
        Assert.Equal((2, 10), (await UsageBySlotAsync(db, x.Id, SetA))[probeB.Id]);
    }

    // (4) and (5) AK 19 / D17: a pull of one of two slots archives only that slot, the emote stays and no
    // leave is observed; the pull of the last slot archives the emote and observes the leave exactly once.
    [Fact]
    public async Task Pull_OneOfTwoSlots_ArchivesOnlyIt_ThenTheLastOne_ArchivesTheEmoteWithOneLeave()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_pull");
        await SeedKeeperAsync(db, channel);
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        var probeA = await SeedSlotAsync(db, x, "probeA", T1);
        var probeB = await SeedSlotAsync(db, x, "probeB", T2);

        var first = await ApplyAsync(db, channel, null, Delta(pulled: [Pull("x", "probeA")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, first.Outcome);
        Assert.True((await SlotAsync(db, probeA.Id)).IsArchived);
        Assert.NotNull((await SlotAsync(db, probeA.Id)).ArchivedAt);
        Assert.False((await SlotAsync(db, probeB.Id)).IsArchived);
        var emote = await EmoteAsync(db, channel, "x");
        // D13: the remaining slot names the emote.
        Assert.Equal((false, "probeB", (DateTime?)T2), (emote.IsArchived, emote.Name, emote.FirstSeenAt));
        Assert.Equal(0, await LeaveRowsAsync(db, channel, "x"));

        var second = await ApplyAsync(db, channel, null, Delta(pulled: [Pull("x", "probeB")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, second.Outcome);
        emote = await EmoteAsync(db, channel, "x");
        Assert.Equal((true, false, "probeB"), (emote.IsArchived, emote.IsPlaceholder, emote.Name));
        Assert.NotNull(emote.ArchivedAt);
        Assert.True((await SlotAsync(db, probeB.Id)).IsArchived);
        Assert.Equal(1, await LeaveRowsAsync(db, channel, "x"));
    }

    // (6) AK 20 with operator decision R4: a rename of an emote with exactly one active own slot renames
    // that slot in place — same id, same date and date origin — and the emote follows D13.
    [Fact]
    public async Task Rename_WithExactlyOneActiveSlot_RenamesItInPlace()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_rename1");
        var x = await SeedEmoteAsync(db, channel, "x", "probeB", firstSeenAt: T2);
        var probeB = await SeedSlotAsync(db, x, "probeB", T2);
        var cache = new EmoteMatchCache();

        var result = await ApplyAsync(db, channel, cache, Delta(updated: [Rename("x", "probeB", "probeC")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var slot = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal((probeB.Id, "probeC", (DateTime?)T2, true, false), (slot.Id, slot.Name, slot.AddedAt, slot.AddedAtFromRest, slot.IsArchived));
        Assert.Equal("probeC", (await EmoteAsync(db, channel, "x")).Name);
        Assert.Equal(probeB.Id, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId["probeC"]);
    }

    // (7) AK 20 with operator decision R4 (E10, Sol-P1-1): with two active slots any rename is ambiguous
    // — onto the sibling's name (7TV's quirk) as onto a free name — and writes nothing.
    [Theory]
    [InlineData("probeA")]
    [InlineData("probeC")]
    public async Task Rename_WithTwoActiveSlots_IsAmbiguous_AndWritesNothing(string newName)
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, newName == "probeA" ? "dslot_rename2_quirk" : "dslot_rename2_free");
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "probeA", T1);
        await SeedSlotAsync(db, x, "probeB", T2);
        var before = await StateAsync(db, channel);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(db, channel, null, Delta(updated: [Rename("x", "probeB", newName)]), logger);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
        AssertAmbiguousLine(logger, SlotDeltaAmbiguity.RenameWithSiblings, "x");
    }

    // (8) AK 20: a pull without a name while two slots are active is ambiguous — no slot, no leave.
    [Fact]
    public async Task NamelessPull_WithTwoActiveSlots_IsAmbiguous_WithoutALeave()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_nameless");
        await SeedKeeperAsync(db, channel);
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "probeA", T1);
        await SeedSlotAsync(db, x, "probeB", T2);
        var before = await StateAsync(db, channel);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(db, channel, null, Delta(pulled: [Pull("x", null)]), logger);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
        AssertAmbiguousLine(logger, SlotDeltaAmbiguity.NamelessPullWithSiblings, "x");
    }

    // (9) AK 20 / D12: any delta on an emote that still carries an unsettled seed of the active set is
    // ambiguous; the seed and its rows stay as they are until REST settles it.
    [Fact]
    public async Task Delta_OnAnEmoteWithASeedOfTheActiveSet_IsAmbiguous()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_seed");
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        var seed = await SeedSeedAsync(db, x);
        await SeedUsageAsync(db, x, seed, SetA, 3);
        var before = await StateAsync(db, channel);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(db, channel, null, Delta(pushed: [Push("x", "probeB")]), logger);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
        AssertAmbiguousLine(logger, SlotDeltaAmbiguity.SeedOfActiveSet, "x");
    }

    // Review 13.10. (P2-2): a malformed dispatch that pulls every active emote is ambiguous because one of
    // them (c) carries a seed of the active set. The plausibility guard still comes first: no leaves for a
    // and b, ImplausibleSkipped.
    [Fact]
    public async Task AmbiguousDispatch_ThatPullsEveryActiveEmote_IsSkippedAsImplausible_WithoutLeaves()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_wipe_amb");
        foreach (var id in new[] { "a", "b", "c" })
        {
            var emote = await SeedEmoteAsync(db, channel, id, id.ToUpperInvariant(), firstSeenAt: T1);
            if (id == "c")
            {
                var seed = await SeedSeedAsync(db, emote);
                await SeedUsageAsync(db, emote, seed, SetA, 3);
            }
            else
            {
                await SeedSlotAsync(db, emote, id.ToUpperInvariant(), T1);
            }
        }

        var before = await StateAsync(db, channel);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(
            db, channel, null, Delta(pulled: [Pull("a", "A"), Pull("b", "B"), Pull("c", "C")]), logger);

        Assert.Equal(SevenTvDeltaOutcome.ImplausibleSkipped, result.Outcome);
        Assert.Equal((0, 0, 0), (await LeaveRowsAsync(db, channel, "a"), await LeaveRowsAsync(db, channel, "b"), await LeaveRowsAsync(db, channel, "c")));
        Assert.Equal(before, await StateAsync(db, channel));
        Assert.Single(Lines(logger, "skipped as implausible"));
    }

    // ------------------------------------------------------------------ adversarial review, end to end

    // Sol-P1-1 (case A): A(t1), B(t2); 7TV renames the first entry A → C (ambiguous, two slots), a fresh
    // REST renames t1 to C, a stale REST turns it back to A, then 7TV renames the second entry and the
    // quirk sends o = B, n = C. Ambiguous — so "C" never counts on t2: not before REST and not after.
    [Fact]
    public async Task QuirkRenameAfterAStaleRest_IsAmbiguous_AndNoCountLandsOnTheWrongSlot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_sol_p1_1");
        await SeedKeeperAsync(db, channel);
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        var t1 = await SeedSlotAsync(db, x, "A", T1);
        var t2 = await SeedSlotAsync(db, x, "B", T2);
        var cache = new EmoteMatchCache();
        var keeper = Live("k", "Keeper", T0);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, (await ApplyAsync(db, channel, cache, Delta(updated: [Rename("x", "A", "C")]))).Outcome);
        await SyncAsync(db, channel, cache, keeper, Live("x", "C", T1), Live("x", "B", T2));
        Assert.Equal("C", (await SlotAsync(db, t1.Id)).Name);
        await SyncAsync(db, channel, cache, keeper, Live("x", "A", T1), Live("x", "B", T2));
        Assert.Equal("A", (await SlotAsync(db, t1.Id)).Name);
        var before = await StateAsync(db, channel);

        var quirk = await ApplyAsync(db, channel, cache, Delta(updated: [Rename("x", "B", "C")]));

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, quirk.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
        var map = cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId;
        Assert.False(map.ContainsKey("C"));
        await FlushAsync(db, map, "C");
        Assert.Empty(await UsageBySlotAsync(db, x.Id, SetA));

        // The current REST: t1 is C, t2 is D. "C" now counts on t1, never on t2.
        await SyncAsync(db, channel, cache, keeper, Live("x", "C", T1), Live("x", "D", T2));
        await FlushAsync(db, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId, "C");
        var usage = await UsageBySlotAsync(db, x.Id, SetA);
        Assert.Equal((1, 1), usage[t1.Id]);
        Assert.False(usage.ContainsKey(t2.Id));
        Assert.Equal(("C", "D"), ((await SlotAsync(db, t1.Id)).Name, (await SlotAsync(db, t2.Id)).Name));
    }

    // Sol-P2-1 (case B): a D4 tie left an active A(t) next to an archived B(t), both authoritative; a rename
    // A → B would violate the authoritative unique index. Ambiguous, no 23505, nothing written.
    [Fact]
    public async Task Rename_OntoAnArchivedTieSlot_IsAmbiguous_WithoutAUniqueViolation()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_sol_p2_1");
        var x = await SeedEmoteAsync(db, channel, "x", "A", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "A", T1);
        await SeedSlotAsync(db, x, "B", T1, archived: true, archivedAt: T5);
        var before = await StateAsync(db, channel);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(db, channel, null, Delta(updated: [Rename("x", "A", "B")]), logger);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
        AssertAmbiguousLine(logger, SlotDeltaAmbiguity.AuthoritativeKeyCollision, "x");
    }

    // Plan 3.4, the belt behind E12: a slot key conflict that still reaches the save (here a concurrent
    // writer inserts the colliding authoritative slot right before it) is not retried and not thrown —
    // the transaction rolls back, the dispatch is answered as ambiguous, and the certain last-slot leave
    // of the same dispatch (Y) is recorded in a new transaction.
    [Fact]
    public async Task SlotKeyConflictAtSave_IsAnsweredAsAmbiguous_AndKeepsTheCertainLeave()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "dslot_belt");
        await SeedKeeperAsync(setup, channel);
        var x = await SeedEmoteAsync(setup, channel, "x", "A", firstSeenAt: T1);
        var a = await SeedSlotAsync(setup, x, "A", T1);
        var y = await SeedEmoteAsync(setup, channel, "y", "Yak", firstSeenAt: T1);
        var yak = await SeedSlotAsync(setup, y, "Yak", T1);
        var hook = new SaveHook(async (_, call) =>
        {
            if (call != 1)
            {
                return;
            }

            await using var other = fixture.CreateDbContext();
            other.EmoteSlots.Add(new EmoteSlot { EmoteId = x.Id, Name = "C", AddedAt = T1, AddedAtFromRest = true, IsArchived = true, ArchivedAt = T5 });
            await other.SaveChangesAsync();
        });
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var db = fixture.CreateDbContext([hook]);

        var result = await CreateService(db, ApiReturning(channel, SetA), logger: logger)
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, SetA, Delta(updated: [Rename("x", "A", "C")], pulled: [Pull("y", "Yak")]));

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(1, hook.Calls);
        Assert.Equal(Columns(a), Columns(await SlotAsync(setup, a.Id)));
        Assert.Equal(Columns(yak), Columns(await SlotAsync(setup, yak.Id)));
        Assert.False((await EmoteAsync(setup, channel, "y")).IsArchived);
        Assert.Equal(1, await LeaveRowsAsync(setup, channel, "y"));
        var warning = Assert.Single(Lines(logger, "authoritative slot key at save time"));
        Assert.Contains(SlotDeltaAmbiguity.AuthoritativeKeyCollision, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Yak", warning, StringComparison.Ordinal);
    }

    // Sol-P1-2 (case C, operator decision R5): an ambiguous X and a last-slot pull of Y, which entered the
    // set five minutes ago, in one dispatch. No inventory is written, but Y's leave is observed — the stale
    // REST that archives Y inside the window observes none, so without it the leave would be lost, and the
    // tag placement registered before the removal would still hold after Y's return.
    [Fact]
    public async Task AmbiguousX_AndALastSlotPullOfAYoungY_ObserveYsLeave_SoItsOldPlacementNoLongerHolds()
    {
        Channel channel;
        await using (var setup = fixture.CreateDbContext())
        {
            channel = await SeedChannelAsync(setup, "dslot_sol_p1_2");
            await SeedKeeperAsync(setup, channel);
            var x = await SeedEmoteAsync(setup, channel, "x", "A", firstSeenAt: T1);
            await SeedSlotAsync(setup, x, "A", T1);
            await SeedSlotAsync(setup, x, "B", T2);
            var y = await SeedEmoteAsync(setup, channel, "y", "Yak", firstSeenAt: T1);
            y.LastEnteredSetAtUtc = DateTime.UtcNow.AddMinutes(-5);
            await setup.SaveChangesAsync();
            await SeedSlotAsync(setup, y, "Yak", T1);
        }

        // Whole seconds, so the stored anchor compares exactly after Postgres' microsecond truncation.
        var registeredAtUtc = DateTime.UtcNow.AddMinutes(-4);
        registeredAtUtc = registeredAtUtc.AddTicks(-(registeredAtUtc.Ticks % TimeSpan.TicksPerSecond));
        var tagId = await SeedPlacementAsync(channel, "y", SetA, registeredAtUtc);
        Assert.Equal((true, 1), await ReadTagAsync(channel, tagId));

        await using var db = fixture.CreateDbContext();
        var inventoryBefore = await InventoryAsync(db, channel);
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(db, channel, null, Delta(updated: [Rename("x", "A", "C")], pulled: [Pull("y", "Yak")]), logger);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(inventoryBefore, await InventoryAsync(db, channel));
        var observedAt = await LatestLeaveAsync(db, channel, "y");
        Assert.NotNull(observedAt);
        Assert.True(observedAt > registeredAtUtc);
        Assert.Single(Lines(logger, "1 certain leaves recorded despite the ambiguity"));

        // A stale REST without Y archives it inside the credibility window: no second observation.
        var keeper = Live("k", "Keeper", T0);
        await SyncAsync(db, channel, null, keeper, Live("x", "A", T1), Live("x", "B", T2));
        Assert.True((await EmoteAsync(db, channel, "y")).IsArchived);
        Assert.Equal(observedAt, await LatestLeaveAsync(db, channel, "y"));

        // Y returns: the observation from the dispatch stays, and the placement from before it no longer holds.
        await SyncAsync(db, channel, null, keeper, Live("x", "A", T1), Live("x", "B", T2), Live("y", "Yak", T1));
        Assert.False((await EmoteAsync(db, channel, "y")).IsArchived);
        Assert.Equal(observedAt, await LatestLeaveAsync(db, channel, "y"));
        Assert.Equal((false, 0), await ReadTagAsync(channel, tagId));
    }

    // Sol-P2-2 (case D, virtual state E5): a set-centric delete and restore without entries (plan #376
    // 4.2a) leave the emote active with its slot archived — the virtual state with a D7 candidate. A push
    // of the old name is ambiguous; the REST resync resumes the same slot (D7), and the map counts on it.
    [Fact]
    public async Task PushInTheVirtualStateAfterARestoreWithoutEntries_IsAmbiguous_AndRestResumesTheSlot()
    {
        var set = "01DSL" + Guid.NewGuid().ToString("N")[..21].ToUpperInvariant();
        Channel channel;
        EmoteSlot slot;
        Emote x;
        await using (var setup = fixture.CreateDbContext())
        {
            channel = await SeedChannelAsync(setup, "dslot_sol_p2_2", set);
            await SeedEmoteAsync(setup, channel, "k", "Keeper", firstSeenAt: T0);
            x = await SeedEmoteAsync(setup, channel, "x", "A", firstSeenAt: T1);
            slot = await SeedSlotAsync(setup, x, "A", T1);
            await SeedUsageAsync(setup, x, slot, set, 2, 3);
        }

        await using (var reportDb = fixture.CreateDbContext())
        {
            await ReportService(reportDb).MarkDeletedInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", ["x"], null, Actor);
        }

        await using (var reportDb = fixture.CreateDbContext())
        {
            await ReportService(reportDb).MarkRestoredInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", ["x"], null, Actor);
        }

        await using var db = fixture.CreateDbContext();
        Assert.False((await EmoteAsync(db, channel, "x")).IsArchived);
        Assert.True((await SlotAsync(db, slot.Id)).IsArchived);
        var before = await StateAsync(db, channel);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await CreateService(db, ApiReturning(channel, set), cache, logger)
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, set, Delta(pushed: [Push("x", "A")]));

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
        AssertAmbiguousLine(logger, SlotDeltaAmbiguity.ArchivedNamesake, "x");

        await CreateService(db, ApiReturning(channel, set, Live("k", "Keeper", T0), Live("x", "A", T3)), cache)
            .SyncChannelAsync(channel.ChannelName);
        var resumed = await SlotAsync(db, slot.Id);
        Assert.Equal(("A", (DateTime?)T3, true, false), (resumed.Name, resumed.AddedAt, resumed.AddedAtFromRest, resumed.IsArchived));
        Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal(slot.Id, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId["A"]);
        Assert.Equal((2, 5), (await UsageBySlotAsync(db, x.Id, set))[slot.Id]);
    }

    // Case D, plain virtual state (an active emote without any slot, left from before the slot work): a push
    // is ambiguous (E5) — a no-op would leave the emote uncounted, the match map has no virtual entries —
    // and the REST resync creates the slot, which the map then counts.
    [Fact]
    public async Task PushInTheVirtualStateWithoutSlots_IsAmbiguous_AndRestCreatesTheCountedSlot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_virtual");
        var x = await SeedEmoteAsync(db, channel, "x", "Xen", firstSeenAt: T1);
        var before = await StateAsync(db, channel);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();

        var result = await ApplyAsync(db, channel, cache, Delta(pushed: [Push("x", "Xen")]), logger);

        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
        AssertAmbiguousLine(logger, SlotDeltaAmbiguity.VirtualSlot, "x");

        await SyncAsync(db, channel, cache, Live("x", "Xen", T1));
        var slot = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal(slot.Id, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId["Xen"]);
    }

    // ------------------------------------------------------------------ further cases of plan 7.3 and section 5

    // AK 13 via the delta: the provisional push slot is bound by name by the next dated REST and becomes
    // authoritative under the same id.
    [Fact]
    public async Task PushSlot_IsAuthorizedByTheNextDatedRest_UnderTheSameId()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_provisional");
        await SeedKeeperAsync(db, channel);

        Assert.Equal(SevenTvDeltaOutcome.Applied, (await ApplyAsync(db, channel, null, Delta(pushed: [Push("x", "probeA")]))).Outcome);
        var x = await EmoteAsync(db, channel, "x");
        var pushSlot = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.False(pushSlot.AddedAtFromRest);
        Assert.Equal(("probeA", false, false), (x.Name, x.IsArchived, x.IsPlaceholder));
        Assert.NotNull(x.LastEnteredSetAtUtc);

        await SyncAsync(db, channel, null, Live("k", "Keeper", T0), Live("x", "probeA", T5));

        var authorized = Assert.Single(await SlotsAsync(db, x.Id));
        Assert.Equal((pushSlot.Id, (DateTime?)T5, true), (authorized.Id, authorized.AddedAt, authorized.AddedAtFromRest));
    }

    // AK 18/19, 7TV's remove without alias: one pulled item per slot of the id — both slots archived, the
    // emote archived, one leave.
    [Fact]
    public async Task TwoPullsOfOneId_ArchiveBothSlotsAndTheEmote_WithOneLeave()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_twopulls");
        await SeedKeeperAsync(db, channel);
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "probeA", T1);
        await SeedSlotAsync(db, x, "probeB", T2);

        var result = await ApplyAsync(db, channel, null, Delta(pulled: [Pull("x", "probeB"), Pull("x", "probeA")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        Assert.All(await SlotsAsync(db, x.Id), s => Assert.True(s.IsArchived));
        Assert.True((await EmoteAsync(db, channel, "x")).IsArchived);
        Assert.Equal(1, await LeaveRowsAsync(db, channel, "x"));
    }

    // Section 5: a return under a new name after the archive un-archives the emote with a fresh entry and
    // a new provisional slot at zero (D4: a new entry); the old slot stays archived.
    [Fact]
    public async Task PushUnderANewName_OfAnArchivedEmote_UnarchivesIt_WithANewSlot()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_return_new");
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", archived: true, firstSeenAt: T1, archivedAt: T5);
        x.IsPlaceholder = true;
        await db.SaveChangesAsync();
        var old = await SeedSlotAsync(db, x, "probeA", T1, archived: true, archivedAt: T5);
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await ApplyAsync(db, channel, null, Delta(pushed: [Push("x", "probeB")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var emote = await EmoteAsync(db, channel, "x");
        Assert.Equal((false, (DateTime?)null, false, "probeB"), (emote.IsArchived, emote.ArchivedAt, emote.IsPlaceholder, emote.Name));
        Assert.True(emote.LastEnteredSetAtUtc >= before);
        Assert.Equal(Columns(old), Columns(await SlotAsync(db, old.Id)));
        var added = Assert.Single(await SlotsAsync(db, x.Id), s => s.Id != old.Id);
        Assert.Equal(("probeB", false, false), (added.Name, added.AddedAtFromRest, added.IsArchived));
    }

    // Section 5: a shared set, one channel ambiguous (two slots), the other not (one slot): each channel
    // decides for itself, only the ambiguous one asks for REST.
    [Fact]
    public async Task SharedSet_OneChannelAmbiguous_TheOtherApplies()
    {
        await using var db = fixture.CreateDbContext();
        var two = await SeedChannelAsync(db, "dslot_shared_two", SetB);
        var one = await SeedChannelAsync(db, "dslot_shared_one", SetB);
        var xTwo = await SeedEmoteAsync(db, two, "x", "A", firstSeenAt: T1);
        await SeedSlotAsync(db, xTwo, "A", T1);
        await SeedSlotAsync(db, xTwo, "B", T2);
        var xOne = await SeedEmoteAsync(db, one, "x", "A", firstSeenAt: T1);
        var slotOne = await SeedSlotAsync(db, xOne, "A", T1);
        var twoBefore = await StateAsync(db, two);
        var delta = Delta(updated: [Rename("x", "A", "C")]);

        var onTwo = await CreateService(db, ApiReturning(two, SetB)).ApplyEmoteSetUpdateAsync(two.ChannelName, SetB, delta);
        var onOne = await CreateService(db, ApiReturning(one, SetB)).ApplyEmoteSetUpdateAsync(one.ChannelName, SetB, delta);

        Assert.Equal((SevenTvDeltaOutcome.AmbiguousDelta, SevenTvDeltaOutcome.Applied), (onTwo.Outcome, onOne.Outcome));
        Assert.Equal(twoBefore, await StateAsync(db, two));
        Assert.Equal("C", (await SlotAsync(db, slotOne.Id)).Name);
    }

    // Section 5: a pull of every active slot of the set's only emote is an implausible wipe — skipped,
    // nothing written.
    [Fact]
    public async Task PullOfTheLastActiveEmote_IsSkippedAsImplausible()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_wipe");
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: T1);
        await SeedSlotAsync(db, x, "probeA", T1);
        var before = await StateAsync(db, channel);

        var result = await ApplyAsync(db, channel, null, Delta(pulled: [Pull("x", "probeA")]));

        Assert.Equal(SevenTvDeltaOutcome.ImplausibleSkipped, result.Outcome);
        Assert.Equal(before, await StateAsync(db, channel));
    }

    // Section 5 / E8 (F5): a pull of an archived emote or of an id without a row writes nothing and
    // observes the leave — NoChange.
    [Fact]
    public async Task PullOfAnArchivedEmoteAndOfAnUnknownId_RecordsBothLeaves_AndStaysNoChange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dslot_pull_archived");
        await SeedKeeperAsync(db, channel);
        var x = await SeedEmoteAsync(db, channel, "x", "probeA", archived: true, firstSeenAt: T1, archivedAt: T5);
        await SeedSlotAsync(db, x, "probeA", T1, archived: true, archivedAt: T5);
        var before = await InventoryAsync(db, channel);

        var result = await ApplyAsync(db, channel, null, Delta(pulled: [Pull("x", "probeA"), Pull("u", "Unknown")]));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, result.Outcome);
        Assert.Equal(before, await InventoryAsync(db, channel));
        Assert.Equal((1, 1), (await LeaveRowsAsync(db, channel, "x"), await LeaveRowsAsync(db, channel, "u")));
    }

    // ------------------------------------------------------------------ helpers

    private static SevenTvEmote Push(string id, string name) => new(id, name, Url(id));

    private static SevenTvEmoteRename Rename(string id, string? oldName, string? newName) => new(id, oldName, newName, Url(id));

    private static SevenTvPulledEntry Pull(string id, string? name) => new(id, name);

    private static SevenTvEmoteSetDelta Delta(
        IReadOnlyList<SevenTvEmote>? pushed = null,
        IReadOnlyList<SevenTvEmoteRename>? updated = null,
        IReadOnlyList<SevenTvPulledEntry>? pulled = null) =>
        new(pushed ?? [], updated ?? [], pulled ?? []);

    private static Task<SevenTvDeltaResult> ApplyAsync(
        AppDbContext db, Channel channel, EmoteMatchCache? cache, SevenTvEmoteSetDelta delta, RecordingLogger<SevenTvSyncService>? logger = null) =>
        CreateService(db, ApiReturning(channel, SetA), cache, logger)
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, SetA, delta);

    private static async Task SyncAsync(AppDbContext db, Channel channel, EmoteMatchCache? cache, params SevenTvEmote[] live) =>
        Assert.NotNull(await CreateService(db, ApiReturning(channel, SetA, live), cache).SyncChannelAsync(channel.ChannelName));

    private static EmoteService ReportService(AppDbContext db) =>
        new(db, NullLogger<EmoteService>.Instance, Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db), new SlotReconcileOptions { Enabled = true });

    // A second active emote with a slot, so pulling every slot of x is no implausible wipe.
    private static async Task SeedKeeperAsync(AppDbContext db, Channel channel)
    {
        var keeper = await SeedEmoteAsync(db, channel, "k", "Keeper", firstSeenAt: T0);
        await SeedSlotAsync(db, keeper, "Keeper", T0);
    }

    private static async Task FlushAsync(AppDbContext db, IReadOnlyDictionary<string, string> map, string message)
    {
        var counts = EmoteNameMatching.MatchEmoteIds(message, map)
            .ToDictionary(slotKey => new UsageCounterKey(slotKey, SetA), _ => new EmoteUsageCounts(1, 0, 0));
        await new UsageStatFlushService(db, NullLogger<UsageStatFlushService>.Instance).FlushAsync(counts);
    }

    // The ambiguity line names the reason code and the 7TV id, never an alias (plan 4.3).
    private static void AssertAmbiguousLine(RecordingLogger<SevenTvSyncService> logger, string reason, string sevenTvId)
    {
        var line = Assert.Single(Lines(logger, AmbiguousLine));
        Assert.Contains($"({reason}, 7TV emote {sevenTvId})", line, StringComparison.Ordinal);
        Assert.DoesNotContain("probe", line, StringComparison.Ordinal);
    }

    private static async Task<int> LeaveRowsAsync(AppDbContext db, Channel channel, string sevenTvId) =>
        await db.EmoteSetLeaveObservations.AsNoTracking().CountAsync(o => o.ChannelId == channel.Id && o.SevenTvEmoteId == sevenTvId);

    private static async Task<DateTime?> LatestLeaveAsync(AppDbContext db, Channel channel, string sevenTvId)
    {
        var latest = await EmoteSetLeaveObservations.LoadLatestAsync(db, channel.Id, SetA, [sevenTvId], CancellationToken.None);
        return latest.TryGetValue(sevenTvId, out var observedAt) ? observedAt : null;
    }

    // Every column of every emote and slot row of the channel, plus its leave rows.
    private static async Task<string> StateAsync(AppDbContext db, Channel channel)
    {
        var leaves = await db.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id)
            .Select(o => o.SevenTvEmoteId + "@" + o.SevenTvEmoteSetId + "@" + o.LastObservedAtUtc.Ticks)
            .ToListAsync();
        return await InventoryAsync(db, channel) + "#" + string.Join("|", leaves.Order(StringComparer.Ordinal));
    }

    // Every column of every emote and slot row of the channel.
    private static async Task<string> InventoryAsync(AppDbContext db, Channel channel)
    {
        var emotes = await db.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).ToListAsync();
        var emoteText = emotes.Select(e => string.Join(
            "|", e.Id, e.SevenTvEmoteId, e.Name, e.ImageUrl, e.IsArchived, e.ArchivedAt?.Ticks, e.FirstSeenAt?.Ticks,
            e.LastSyncedAt.Ticks, e.LastEnteredSetAtUtc?.Ticks, e.IsPlaceholder));
        return string.Join("\n", emoteText.Order(StringComparer.Ordinal)) + "#" + await SlotColumnsAsync(db, channel);
    }

    private static async Task<string> SlotColumnsAsync(AppDbContext db, Channel channel)
    {
        var slots = await db.EmoteSlots.AsNoTracking().Where(s => s.Emote.ChannelId == channel.Id).ToListAsync();
        return string.Join("\n", slots.Select(Columns).Order(StringComparer.Ordinal));
    }

    // A play-in placement of the tag "Stronghold" for one emote, registered at registeredAtUtc (as in
    // SevenTvSyncServiceLeaveObservationTests).
    private async Task<long> SeedPlacementAsync(Channel channel, string sevenTvEmoteId, string emoteSetId, DateTime registeredAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        var tag = new EmoteTag { ChannelId = channel.Id, Name = "Stronghold", NormalizedName = "stronghold", CreatedAtUtc = registeredAtUtc };
        db.EmoteTags.Add(tag);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        db.EmoteTagEntries.Add(new EmoteTagEntry
        {
            TagId = tag.Id,
            SevenTvEmoteId = sevenTvEmoteId,
            Alias = sevenTvEmoteId,
            ImageUrl = Url(sevenTvEmoteId),
            AddedAtUtc = registeredAtUtc,
        });
        db.EmoteTagOperations.Add(new EmoteTagOperation
        {
            OperationId = operationId,
            TagId = tag.Id,
            Kind = EmoteTagOperationKind.PlayIn,
            SevenTvEmoteSetId = emoteSetId,
            RegisteredAtUtc = registeredAtUtc,
        });
        db.EmoteTagPlacements.Add(new EmoteTagPlacement
        {
            TagId = tag.Id,
            SevenTvEmoteId = sevenTvEmoteId,
            SevenTvEmoteSetId = emoteSetId,
            PlacedAtUtc = registeredAtUtc,
            OperationId = operationId,
            RegisteredAtUtc = registeredAtUtc,
        });
        await db.SaveChangesAsync();
        return tag.Id;
    }

    // The tags page's verdict (EmoteTagService, read-time rule Holds): placed by this tag, and the tag's count.
    private async Task<(bool PlacedByThisTag, int PlacedCount)> ReadTagAsync(Channel channel, long tagId)
    {
        await using var db = fixture.CreateDbContext();
        var tags = new EmoteTagService(db, Substitute.For<ITrackedEmoteSetMembershipService>(), Substitute.For<IForeignEmoteSetService>());
        var entries = await tags.ListEntriesAsync(channel.ChannelName, tagId, null);
        var list = await tags.ListAsync(channel.ChannelName, null);
        return (Assert.Single(entries.Entries).PlacedByThisTag, Assert.Single(list.Tags, t => t.Id == tagId).PlacedCount);
    }
}
