using System.Data.Common;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// T-C (#201) Task 2: the sync's leave observations and entry stamps (spec 5.5 rule 5, E33, E34),
// against real Postgres — the ON CONFLICT … GREATEST upsert, the explicit transactions and the
// row-lock interleavings are exactly what a fake would hide. A file of its own with one service
// factory, so the sync's constructor has a single call site here.
[Collection("Postgres")]
public class SevenTvSyncServiceLeaveObservationTests(PostgresFixture fixture)
{
    // Real 7TV set ids are 26-character ULIDs; the observation upsert drops anything that is not
    // 1–32 alphanumeric characters, so every id in this file is.
    private const string SetId = "01J94NYQR0000D15QN0BDGN85E";
    private const string SwitchedSetId = "01J94NYQR0000D15QN0BDGN99Z";

    // The set of the one case that runs the Api's set-centric report: that report hits every channel
    // with the set active, so no other case may share it.
    private const string ReportSetId = "01J94NYQR0000D15QN0BDGNAPI";

    private static readonly AuditActor Actor = new("100", "leaveobstester");

    [Theory]
    [InlineData(null)]
    [InlineData(31)]
    public async Task SyncChannel_RowMissingFromTheLiveSet_EnteredUnknownOrBeforeTheWindow_IsArchivedAndRecordedAgainstTheActiveSet(int? enteredMinutesAgo)
    {
        var name = $"leaveobs_b1_{enteredMinutesAgo?.ToString() ?? "null"}";
        var channel = await SeedChannelAsync(name,
            ("lokeep1", false, null),
            ("logone1", false, enteredMinutesAgo is null ? null : DateTime.UtcNow.AddMinutes(-enteredMinutesAgo.Value)));
        var before = DateTime.UtcNow;

        await SyncAsync(channel, SetId, Live("lokeep1"));

        await using var verify = fixture.CreateDbContext();
        Assert.True((await LoadEmoteAsync(verify, channel, "logone1")).IsArchived);
        var observation = Assert.Single(await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).ToListAsync());
        Assert.Equal(("logone1", SetId), (observation.SevenTvEmoteId, observation.SevenTvEmoteSetId));
        Assert.True(observation.LastObservedAtUtc >= before.AddMilliseconds(-1));
        // A channel without a single tag gets the observation all the same: the sync cannot know
        // which tag report is still on its way.
        Assert.False(await verify.EmoteTags.AnyAsync(t => t.ChannelId == channel.Id));
    }

    [Fact]
    public async Task SyncChannel_RowMissingFromTheLiveSet_EnteredInsideTheWindow_IsArchivedWithoutAnObservation()
    {
        var channel = await SeedChannelAsync("leaveobs_b1_inside",
            ("lokeep1", false, null),
            ("lofresh1", false, DateTime.UtcNow.AddMinutes(-5)));

        await SyncAsync(channel, SetId, Live("lokeep1"));

        await using var verify = fixture.CreateDbContext();
        Assert.True((await LoadEmoteAsync(verify, channel, "lofresh1")).IsArchived);
        Assert.False(await verify.EmoteSetLeaveObservations.AnyAsync(o => o.ChannelId == channel.Id));
    }

    [Fact]
    public async Task SyncChannel_ArchivedRowEnteredBeforeTheWindowWithoutObservation_IsRecordedOnce_NotOnEveryResync()
    {
        var channel = await SeedChannelAsync("leaveobs_b2_once",
            ("lokeep1", false, null),
            ("loarch1", true, DateTime.UtcNow.AddMinutes(-31)));

        await SyncAsync(channel, SetId, Live("lokeep1"));
        var first = await LatestAsync(channel, SetId, "loarch1");
        Assert.NotNull(first);

        await SyncAsync(channel, SetId, Live("lokeep1"));
        Assert.Equal(first, await LatestAsync(channel, SetId, "loarch1"));
    }

    [Fact]
    public async Task SyncChannel_ArchivedRowWithAnObservationYoungerThanItsEntry_GetsNoNewObservation()
    {
        var entered = DateTime.UtcNow.AddMinutes(-40);
        var observed = DateTime.UtcNow.AddMinutes(-35);
        var channel = await SeedChannelAsync("leaveobs_b2_seen",
            ("lokeep1", false, null),
            ("loarch1", true, entered));
        await using (var seed = fixture.CreateDbContext())
        {
            await EmoteSetLeaveObservations.RecordAsync(seed, channel.Id, SetId, ["loarch1"], observed, CancellationToken.None);
        }

        await SyncAsync(channel, SetId, Live("lokeep1"));

        var latest = await LatestAsync(channel, SetId, "loarch1");
        Assert.NotNull(latest);
        Assert.True(Math.Abs((latest.Value - observed).TotalMilliseconds) < 1, "the seeded observation must stay as it was");
    }

    [Fact]
    public async Task SyncChannel_ArchivedRowWithUnknownEntryAndAnObservation_KeepsTheObservationAsItWas()
    {
        var observed = DateTime.UtcNow.AddMinutes(-50);
        var channel = await SeedChannelAsync("leaveobs_b2_nullentry",
            ("lokeep1", false, null),
            ("loarch1", true, null));
        await using (var seed = fixture.CreateDbContext())
        {
            await EmoteSetLeaveObservations.RecordAsync(seed, channel.Id, SetId, ["loarch1"], observed, CancellationToken.None);
        }

        await SyncAsync(channel, SetId, Live("lokeep1"));

        var latest = await LatestAsync(channel, SetId, "loarch1");
        Assert.NotNull(latest);
        Assert.True(Math.Abs((latest.Value - observed).TotalMilliseconds) < 1, "an unknown entry must not trigger a new observation");
    }

    [Fact]
    public async Task SyncChannel_ArchivedRowWithAnObservationOlderThanItsEntry_GetsOneNewObservation_NotOnTheNextPass()
    {
        var entered = DateTime.UtcNow.AddMinutes(-31);
        var observed = DateTime.UtcNow.AddMinutes(-60);
        var channel = await SeedChannelAsync("leaveobs_b2_stale",
            ("lokeep1", false, null),
            ("loarch1", true, entered));
        await using (var seed = fixture.CreateDbContext())
        {
            await EmoteSetLeaveObservations.RecordAsync(seed, channel.Id, SetId, ["loarch1"], observed, CancellationToken.None);
        }

        await SyncAsync(channel, SetId, Live("lokeep1"));
        var first = await LatestAsync(channel, SetId, "loarch1");
        Assert.NotNull(first);
        Assert.True(first.Value > entered, "the new observation must be younger than the entry");

        await SyncAsync(channel, SetId, Live("lokeep1"));
        Assert.Equal(first, await LatestAsync(channel, SetId, "loarch1"));
    }

    [Fact]
    public async Task SyncChannel_StampsTheEntryOnCreateAndOnUnarchive_ButNotOnARename()
    {
        var old = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var channel = await SeedChannelAsync("leaveobs_stamp",
            ("loren1", false, old),
            ("loback1", true, old));
        var before = DateTime.UtcNow;

        await SyncAsync(channel, SetId,
            new SevenTvEmote("loren1", "Renamed", ImageUrl("loren1")),
            Live("loback1"),
            Live("lonew1"));

        await using var verify = fixture.CreateDbContext();
        var renamed = await LoadEmoteAsync(verify, channel, "loren1");
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(old, renamed.LastEnteredSetAtUtc);
        var unarchived = await LoadEmoteAsync(verify, channel, "loback1");
        Assert.False(unarchived.IsArchived);
        Assert.True(unarchived.LastEnteredSetAtUtc >= before.AddMilliseconds(-1));
        Assert.True((await LoadEmoteAsync(verify, channel, "lonew1")).LastEnteredSetAtUtc >= before.AddMilliseconds(-1));
    }

    [Fact]
    public async Task SyncChannel_ArchivingARowWithAPlacement_LeavesThePlacementUntouched()
    {
        var channel = await SeedChannelAsync("leaveobs_placement",
            ("lokeep1", false, null),
            ("loplaced1", false, null));
        // Whole seconds, so the stored anchor compares exactly after Postgres' microsecond truncation.
        var registeredAtUtc = DateTime.UtcNow.AddHours(-1);
        registeredAtUtc = registeredAtUtc.AddTicks(-(registeredAtUtc.Ticks % TimeSpan.TicksPerSecond));
        var (tagId, operationId) = await SeedPlacementAsync(channel, "loplaced1", SetId, registeredAtUtc);

        await SyncAsync(channel, SetId, Live("lokeep1"));

        await using var verify = fixture.CreateDbContext();
        Assert.NotNull(await LatestAsync(channel, SetId, "loplaced1"));
        var placement = await verify.EmoteTagPlacements.AsNoTracking().SingleAsync(p => p.TagId == tagId);
        Assert.Equal(("loplaced1", SetId, operationId, registeredAtUtc),
            (placement.SevenTvEmoteId, placement.SevenTvEmoteSetId, placement.OperationId, placement.RegisteredAtUtc));
    }

    [Fact]
    public async Task SyncChannel_SetSwitchWithLeavingRows_ClosesTheIntervalThenArchivesAndRecordsAgainstTheNewSet()
    {
        var channel = await SeedChannelAsync("leaveobs_switch",
            ("lokeep1", false, null),
            ("loold1", false, null));
        // The first sync opens the observation interval for the old set.
        await SyncAsync(channel, SetId, Live("lokeep1"), Live("loold1"));

        var result = await SyncAsync(channel, SwitchedSetId, Live("lokeep1"));

        Assert.NotNull(result);
        await using var verify = fixture.CreateDbContext();
        var intervals = await verify.ChannelEmoteSetObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).OrderBy(o => o.ObservedFromUtc).ToListAsync();
        Assert.Equal([(SetId, ChannelEmoteSetObservationClosedBy.SetSwitch), (SwitchedSetId, (string?)null)],
            intervals.Select(o => (o.SevenTvEmoteSetId, o.ClosedBy)));
        Assert.True((await LoadEmoteAsync(verify, channel, "loold1")).IsArchived);
        var observation = Assert.Single(await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).ToListAsync());
        Assert.Equal(("loold1", SwitchedSetId), (observation.SevenTvEmoteId, observation.SevenTvEmoteSetId));
    }

    [Fact]
    public async Task SyncChannel_ConcurrentEmoteInsertRetry_RunsInANewTransaction_AndLeavesExactlyOneObservation()
    {
        var channel = await SeedChannelAsync("leaveobs_retry",
            ("lokeep1", false, null),
            ("logone1", false, null));
        var interceptor = new SevenTvSyncServiceTests.ConcurrentEmoteInsertInterceptor(fixture, maxInsertions: 1);
        var cache = new EmoteMatchCache();

        await using (var db = fixture.CreateDbContext([interceptor]))
        {
            var service = CreateService(db, cache, RestAnswering(channel, SetId, Live("lokeep1"), Live("loracex1")));
            Assert.NotNull(await service.SyncChannelAsync(channel.ChannelName));
        }

        Assert.Equal("loracex1", Assert.Single(interceptor.InsertedRows).SevenTvEmoteId);
        await using var verify = fixture.CreateDbContext();
        Assert.True((await LoadEmoteAsync(verify, channel, "logone1")).IsArchived);
        var observation = Assert.Single(await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).ToListAsync());
        Assert.Equal("logone1", observation.SevenTvEmoteId);
        // The retry adopted the Api's archived row: an un-archive, so it is stamped as an entry.
        Assert.NotNull((await LoadEmoteAsync(verify, channel, "loracex1")).LastEnteredSetAtUtc);
    }

    // The observation and the archive are one commit: when the save after the upsert fails for good
    // (a second concurrent insert inside the retry), neither is left behind.
    [Fact]
    public async Task SyncChannel_SaveFailsAfterTheUpsertInBothAttempts_LeavesNoObservationBehind()
    {
        var channel = await SeedChannelAsync("leaveobs_rollback",
            ("lokeep1", false, null),
            ("logone1", false, null));
        var interceptor = new SevenTvSyncServiceTests.ConcurrentEmoteInsertInterceptor(fixture, maxInsertions: 2);

        await using (var db = fixture.CreateDbContext([interceptor]))
        {
            var service = CreateService(db, new EmoteMatchCache(),
                RestAnswering(channel, SetId, Live("lokeep1"), Live("loracea1"), Live("loraceb1")));
            await Assert.ThrowsAsync<DbUpdateException>(() => service.SyncChannelAsync(channel.ChannelName));
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False((await LoadEmoteAsync(verify, channel, "logone1")).IsArchived);
        Assert.False(await verify.EmoteSetLeaveObservations.AnyAsync(o => o.ChannelId == channel.Id));
    }

    [Fact]
    public async Task SyncChannel_ChannelPurgedRightBeforeTheObservationUpsert_IsAbandonedLikeAVanishedRow()
    {
        var channel = await SeedChannelAsync("leaveobs_purged",
            ("lokeep1", false, null),
            ("logone1", false, null));
        var purge = new PurgeChannelBeforeObservationUpsert(fixture, channel.Id);
        var cache = new EmoteMatchCache();

        SevenTvSyncResult? result;
        await using (var db = fixture.CreateDbContext([purge]))
        {
            result = await CreateService(db, cache, RestAnswering(channel, SetId, Live("lokeep1"))).SyncChannelAsync(channel.ChannelName);
        }

        Assert.True(purge.Fired);
        Assert.Null(result);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == channel.Id));
        Assert.False(await verify.EmoteSetLeaveObservations.AnyAsync(o => o.ChannelId == channel.Id));
        Assert.DoesNotContain(channel.ChannelName, cache.GetCachedChannelNames());
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_PulledActiveRow_IsArchivedAndRecorded()
    {
        var channel = await SeedChannelAsync("leaveobs_a_active",
            ("lokeep1", false, null),
            ("logone1", false, DateTime.UtcNow));

        var result = await ApplyAsync(channel, Delta(pulledIds: ["logone1"]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        await using var verify = fixture.CreateDbContext();
        Assert.True((await LoadEmoteAsync(verify, channel, "logone1")).IsArchived);
        // Always credible, the window does not apply: the row entered the set just now.
        Assert.NotNull(await LatestAsync(channel, SetId, "logone1"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_PulledIdsOfAnArchivedRowAndOfAnUnknownId_RecordBoth_AndStayNoChange()
    {
        var channel = await SeedChannelAsync("leaveobs_a_nochange",
            ("lokeep1", false, null),
            ("loarch1", true, DateTime.UtcNow));
        var cache = new EmoteMatchCache();
        cache.ReplaceChannel(channel.ChannelName, SetId, new Dictionary<string, string> { ["Sentinel"] = "sentinel" });

        var result = await ApplyAsync(channel, Delta(pulledIds: ["loarch1", "lonorow1"]), cache);

        Assert.Equal(SevenTvDeltaOutcome.NoChange, result.Outcome);
        Assert.NotNull(await LatestAsync(channel, SetId, "loarch1"));
        Assert.NotNull(await LatestAsync(channel, SetId, "lonorow1"));
        // NoChange means no refresh: the sentinel a refresh would have replaced is still there.
        Assert.Equal(["Sentinel"], cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.Keys);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_TheSameIdTwiceInPulledIds_WritesOneRowWithoutError()
    {
        var channel = await SeedChannelAsync("leaveobs_a_twice",
            ("lokeep1", false, null),
            ("lodup1", false, null));

        var result = await ApplyAsync(channel, Delta(pulledIds: ["lodup1", "lodup1"]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        await using var verify = fixture.CreateDbContext();
        Assert.Equal("lodup1", Assert.Single(await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).ToListAsync()).SevenTvEmoteId);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_ASecondDeltaForTheSameId_KeepsOneRowWithTheLaterTime()
    {
        var channel = await SeedChannelAsync("leaveobs_a_second",
            ("lokeep1", false, null),
            ("logone1", false, null));

        await ApplyAsync(channel, Delta(pulledIds: ["logone1"]));
        var first = await LatestAsync(channel, SetId, "logone1");
        await ApplyAsync(channel, Delta(pulledIds: ["logone1"]));

        await using var verify = fixture.CreateDbContext();
        var observation = Assert.Single(await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).ToListAsync());
        Assert.True(observation.LastObservedAtUtc > first);
    }

    [Fact]
    public async Task RecordAsync_AnOlderObservationThanTheStoredOne_KeepsTheStoredValue()
    {
        var channel = await SeedChannelAsync("leaveobs_greatest");
        var later = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

        await using (var db = fixture.CreateDbContext())
        {
            await EmoteSetLeaveObservations.RecordAsync(db, channel.Id, SetId, ["logone1"], later, CancellationToken.None);
            await EmoteSetLeaveObservations.RecordAsync(db, channel.Id, SetId, ["logone1"], later.AddMinutes(-10), CancellationToken.None);
        }

        Assert.Equal(later, await LatestAsync(channel, SetId, "logone1"));
    }

    [Fact]
    public async Task RecordAsync_DropsMalformedIdsAndEverythingUnderAMalformedSetId_WithoutFailing()
    {
        var channel = await SeedChannelAsync("leaveobs_malformed");
        var now = DateTime.UtcNow;
        var tooLong = new string('a', SevenTvEmoteIdValidation.MaxLength + 1);

        await using (var db = fixture.CreateDbContext())
        {
            await EmoteSetLeaveObservations.RecordAsync(db, channel.Id, SetId, ["logood1", tooLong, "bad-id", ""], now, CancellationToken.None);
            await EmoteSetLeaveObservations.RecordAsync(db, channel.Id, new string('S', 33), ["logood2"], now, CancellationToken.None);
            await EmoteSetLeaveObservations.RecordAsync(db, channel.Id, "set-with-dash", ["logood3"], now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var observation = Assert.Single(await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).ToListAsync());
        Assert.Equal(("logood1", SetId), (observation.SevenTvEmoteId, observation.SevenTvEmoteSetId));
    }

    // Spec 5.5 rule 5 and Codex finding 2, as one sequence: entry → a stale REST resync archives inside
    // the window without an observation → the REMOVE dispatch for the archived row still records the
    // leave, later than the play-in's registration → the PUSH re-enters the row. The read-time rule
    // that turns "observation later than RegisteredAtUtc" into "placement no longer valid" is Task 3's;
    // here the timestamps it compares are asserted, and that the sync deleted no placement.
    [Fact]
    public async Task CodexFinding2_StaleRestArchive_ThenRemoveDelta_ThenPush_LeavesAnObservationLaterThanTheRegistration()
    {
        var channel = await SeedChannelAsync("leaveobs_codex2", ("lokeep1", false, null));

        Assert.Equal(SevenTvDeltaOutcome.Applied, (await ApplyAsync(channel, Delta(pushed: [Live("lotag1")]))).Outcome);
        var registeredAtUtc = DateTime.UtcNow;
        var (tagId, _) = await SeedPlacementAsync(channel, "lotag1", SetId, registeredAtUtc);

        await SyncAsync(channel, SetId, Live("lokeep1"));
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.True((await LoadEmoteAsync(verify, channel, "lotag1")).IsArchived);
        }
        Assert.Null(await LatestAsync(channel, SetId, "lotag1"));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, (await ApplyAsync(channel, Delta(pulledIds: ["lotag1"]))).Outcome);
        var observedAt = await LatestAsync(channel, SetId, "lotag1");
        Assert.NotNull(observedAt);
        Assert.True(observedAt > registeredAtUtc);

        Assert.Equal(SevenTvDeltaOutcome.Applied, (await ApplyAsync(channel, Delta(pushed: [Live("lotag1")]))).Outcome);
        await using (var verify = fixture.CreateDbContext())
        {
            var row = await LoadEmoteAsync(verify, channel, "lotag1");
            Assert.False(row.IsArchived);
            Assert.True(row.LastEnteredSetAtUtc > observedAt);
            Assert.True(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tagId));
        }
        Assert.Equal(observedAt, await LatestAsync(channel, SetId, "lotag1"));
    }

    // The same sequence end to end (T-C Task 3), read the way the tags page reads it: the real sync
    // drives every step and EmoteTagService applies the read-time rule. The placement survives the
    // stale REST archive (no observation inside the window — counterexample 8), and drops out of
    // every placement field once the REMOVE has recorded a leave later than its registration — even
    // though the PUSH has put the emote back and the placement row itself is still there.
    [Fact]
    public async Task CodexFinding2_EndToEnd_APlacementRegisteredBeforeTheRemove_NoLongerCountsAsPlacedByTheTag()
    {
        var channel = await SeedChannelAsync("leaveobs_codex2_read", ("lokeep1", false, null));
        Assert.Equal(SevenTvDeltaOutcome.Applied, (await ApplyAsync(channel, Delta(pushed: [Live("lotag1")]))).Outcome);
        var (tagId, operationId) = await SeedPlacementAsync(channel, "lotag1", SetId, DateTime.UtcNow);

        var placed = await ReadTagAsync(channel, tagId);
        Assert.Equal((true, (Guid?)operationId), (placed.Entry.PlacedByThisTag, placed.Entry.PlacementOperationId));

        await SyncAsync(channel, SetId, Live("lokeep1"));
        var afterStaleRest = await ReadTagAsync(channel, tagId);
        Assert.Equal((false, true, 1), (afterStaleRest.Entry.InSet, afterStaleRest.Entry.PlacedByThisTag, afterStaleRest.PlacedCount));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, (await ApplyAsync(channel, Delta(pulledIds: ["lotag1"]))).Outcome);
        Assert.Equal(SevenTvDeltaOutcome.Applied, (await ApplyAsync(channel, Delta(pushed: [Live("lotag1")]))).Outcome);

        var afterReAdd = await ReadTagAsync(channel, tagId);
        Assert.Equal((true, false, 0), (afterReAdd.Entry.InSet, afterReAdd.Entry.PlacedByThisTag, afterReAdd.PlacedCount));
        Assert.Equal(((DateTime?)null, (Guid?)null), (afterReAdd.Entry.PlacedAtUtc, afterReAdd.Entry.PlacementOperationId));
        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.EmoteTagPlacements.AnyAsync(p => p.TagId == tagId && p.OperationId == operationId));
    }

    // The same sequence with the EventAPI off: no REMOVE ever arrives. Once the window after the
    // entry is over, the next REST resync's post-check records the leave exactly once.
    [Fact]
    public async Task CodexFinding2_WithoutRemoveDelta_ThePostCheckRecordsTheLeaveOnceTheWindowIsOver_ExactlyOnce()
    {
        var channel = await SeedChannelAsync("leaveobs_codex2_rest", ("lokeep1", false, null));
        await ApplyAsync(channel, Delta(pushed: [Live("lotag1")]));
        var registeredAtUtc = DateTime.UtcNow;

        await SyncAsync(channel, SetId, Live("lokeep1"));
        Assert.Null(await LatestAsync(channel, SetId, "lotag1"));

        // The window passes: moved back rather than waited for.
        await using (var clock = fixture.CreateDbContext())
        {
            await clock.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "lotag1")
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastEnteredSetAtUtc, DateTime.UtcNow.AddMinutes(-31)));
        }

        await SyncAsync(channel, SetId, Live("lokeep1"));
        var observedAt = await LatestAsync(channel, SetId, "lotag1");
        Assert.NotNull(observedAt);
        Assert.True(observedAt > registeredAtUtc);

        await SyncAsync(channel, SetId, Live("lokeep1"));
        Assert.Equal(observedAt, await LatestAsync(channel, SetId, "lotag1"));
    }

    // Counterexample 8 (T-C Task 4), the Stronghold standard case, end to end with the real sync and
    // the real report: the rows have been archived since last week's stream; the play-in is
    // registered; the PUSH un-archives and stamps each row; the next REST resync reads 7TV's stale
    // cache and archives them again — inside the window, so without an observation. The (late) report
    // then finds no leave after its registration and places every emote, and the placements hold
    // through the resync that finally sees them.
    [Fact]
    public async Task CounterExample8_StaleRestArchiveRightAfterThePlayIn_DoesNotCostThePlacements()
    {
        var lastWeek = DateTime.UtcNow.AddDays(-7);
        var channel = await SeedChannelAsync("leaveobs_ce8",
            ("lokeep1", false, null),
            ("losh1", true, lastWeek),
            ("losh2", true, lastWeek));
        var tagId = await SeedTagWithEntriesAsync(channel, "losh1", "losh2");
        var operationId = Guid.NewGuid();
        await using (var db = fixture.CreateDbContext())
        {
            var registration = await new EmoteTagService(db).RegisterOperationAsync(
                channel.ChannelName, tagId, new RegisterTagOperationRequest(operationId, EmoteTagOperationKind.PlayIn, SetId));
            Assert.Equal(TagOperationRegistrationStatus.Ok, registration.Status);
        }

        Assert.Equal(SevenTvDeltaOutcome.Applied,
            (await ApplyAsync(channel, Delta(pushed: [Live("losh1"), Live("losh2")]))).Outcome);
        await SyncAsync(channel, SetId, Live("lokeep1"));
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.True((await LoadEmoteAsync(verify, channel, "losh1")).IsArchived);
        }
        Assert.Null(await LatestAsync(channel, SetId, "losh1"));
        Assert.Null(await LatestAsync(channel, SetId, "losh2"));

        TagPlacementReportResult report;
        await using (var db = fixture.CreateDbContext())
        {
            report = await new EmoteTagService(db).ReportPlacementsAsync(
                channel.ChannelName, tagId, new TagPlacementReport(operationId, SetId, ["losh1", "losh2"]), Actor);
        }

        Assert.Equal((TagReportStatus.Ok, 2, 0), (report.Status, report.RecordedCount, report.DiscardedStaleIds.Count));
        await SyncAsync(channel, SetId, Live("lokeep1"), Live("losh1"), Live("losh2"));
        await using (var verify = fixture.CreateDbContext())
        {
            var tags = new EmoteTagService(verify);
            var entries = (await tags.ListEntriesAsync(channel.ChannelName, tagId, null)).Entries;
            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.Equal((true, true, (Guid?)operationId), (e.InSet, e.PlacedByThisTag, e.PlacementOperationId)));
            Assert.Equal(2, Assert.Single((await tags.ListAsync(channel.ChannelName, null)).Tags).PlacedCount);
        }

        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channel.Id);
    }

    // Two contexts, (i): two first inserts of the same key. B blocks on A's uncommitted row, then
    // takes the ON CONFLICT branch once A commits — both commits succeed, one row.
    [Fact]
    public async Task RecordAsync_TwoContextsInsertTheSameRowConcurrently_BothCommit_OneRow()
    {
        var channel = await SeedChannelAsync("leaveobs_two_insert");
        var observedAt = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

        await using var contextA = fixture.CreateTaggedDbContext("leaveobs-insert-a");
        await using var contextB = fixture.CreateTaggedDbContext("leaveobs-insert-b");
        await using var transactionA = await contextA.Database.BeginTransactionAsync();
        await EmoteSetLeaveObservations.RecordAsync(contextA, channel.Id, SetId, ["lorace1"], observedAt, CancellationToken.None);

        var contenderB = Task.Run(async () =>
        {
            await using var transactionB = await contextB.Database.BeginTransactionAsync();
            await EmoteSetLeaveObservations.RecordAsync(contextB, channel.Id, SetId, ["lorace1"], observedAt.AddSeconds(1), CancellationToken.None);
            await transactionB.CommitAsync();
        });
        await fixture.WaitUntilBlockedOnLockAsync("leaveobs-insert-b", contenderB);
        await transactionA.CommitAsync();
        await contenderB;

        await using var verify = fixture.CreateDbContext();
        var observation = Assert.Single(await verify.EmoteSetLeaveObservations.AsNoTracking()
            .Where(o => o.ChannelId == channel.Id).ToListAsync());
        Assert.Equal(observedAt.AddSeconds(1), observation.LastObservedAtUtc);
    }

    // Two contexts, (ii): A stamps t₂, B stamps t₁ < t₂ and commits after A. GREATEST keeps t₂ — a
    // last-writer-wins upsert would have turned the observation back.
    [Fact]
    public async Task RecordAsync_AnOlderStampCommittingAfterANewerOne_LeavesTheNewerOne()
    {
        var channel = await SeedChannelAsync("leaveobs_two_order");
        var t2 = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var t1 = t2.AddMinutes(-3);
        await using (var seed = fixture.CreateDbContext())
        {
            await EmoteSetLeaveObservations.RecordAsync(seed, channel.Id, SetId, ["lorace1"], t1.AddMinutes(-10), CancellationToken.None);
        }

        await using var contextA = fixture.CreateTaggedDbContext("leaveobs-order-a");
        await using var contextB = fixture.CreateTaggedDbContext("leaveobs-order-b");
        await using var transactionA = await contextA.Database.BeginTransactionAsync();
        await EmoteSetLeaveObservations.RecordAsync(contextA, channel.Id, SetId, ["lorace1"], t2, CancellationToken.None);

        var contenderB = Task.Run(async () =>
        {
            await using var transactionB = await contextB.Database.BeginTransactionAsync();
            await EmoteSetLeaveObservations.RecordAsync(contextB, channel.Id, SetId, ["lorace1"], t1, CancellationToken.None);
            await transactionB.CommitAsync();
        });
        await fixture.WaitUntilBlockedOnLockAsync("leaveobs-order-b", contenderB);
        await transactionA.CommitAsync();
        await contenderB;

        Assert.Equal(t2, await LatestAsync(channel, SetId, "lorace1"));
    }

    // The Api's set-centric delete report and the worker's delta stamp the same row overlapping: the
    // report takes its timestamp first, is held just before its upsert, and the worker's later stamp
    // commits meanwhile. The report then commits last with the older value — and the later one stays.
    [Fact]
    public async Task MarkDeletedInSetAndAPulledDelta_Overlapping_TheLaterStampWins()
    {
        var channel = await SeedChannelAsync("leaveobs_api_worker", ReportSetId,
            ("lokeep1", false, null),
            ("logone1", false, null));
        var pause = new PauseBeforeObservationUpsert();

        await using var apiDb = fixture.CreateDbContext([pause]);
        var emoteService = new EmoteService(
            apiDb, NullLogger<EmoteService>.Instance,
            new ExcludedChannelFilter(new ConfigurationBuilder().Build(), NullLogger<ExcludedChannelFilter>.Instance));
        var report = emoteService.MarkDeletedInSetAsync(
            ReportSetId, "owner-seven-tv-id", "setowner", "owner-twitch-id", ["logone1"], channel.ChannelName, Actor);
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var workerStartedAt = DateTime.UtcNow;
        Assert.Equal(SevenTvDeltaOutcome.Applied, (await ApplyAsync(channel, Delta(pulledIds: ["logone1"]), emoteSetId: ReportSetId)).Outcome);
        var workerStamp = await LatestAsync(channel, ReportSetId, "logone1");

        pause.Release.SetResult();
        var reported = await report.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, Assert.Single(reported.Channels).Count);
        Assert.True(workerStamp >= workerStartedAt.AddMilliseconds(-1));
        Assert.Equal(workerStamp, await LatestAsync(channel, ReportSetId, "logone1"));
    }

    private static SevenTvSyncService CreateService(AppDbContext db, EmoteMatchCache cache, ISevenTvApiClient? apiClient = null) =>
        new(db, apiClient ?? Substitute.For<ISevenTvApiClient>(), cache, new DuplicateEmoteNameTracker(),
            new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            NullLogger<SevenTvSyncService>.Instance);

    private static ISevenTvApiClient RestAnswering(Channel channel, string emoteSetId, params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes))));
        return apiClient;
    }

    // Every step on a fresh context, like separate worker ticks: a long-lived context would hand back
    // its tracked rows and hide what another context wrote in between.
    private async Task<SevenTvSyncResult?> SyncAsync(Channel channel, string emoteSetId, params SevenTvEmote[] liveEmotes)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db, new EmoteMatchCache(), RestAnswering(channel, emoteSetId, liveEmotes))
            .SyncChannelAsync(channel.ChannelName);
    }

    private async Task<SevenTvDeltaResult> ApplyAsync(
        Channel channel, SevenTvEmoteSetDelta delta, EmoteMatchCache? cache = null, string emoteSetId = SetId)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db, cache ?? new EmoteMatchCache()).ApplyEmoteSetUpdateAsync(channel.ChannelName, emoteSetId, delta);
    }

    // The tag read on a fresh context, for the channel's active set, as the tags page asks for it.
    private async Task<(EmoteTagEntryDto Entry, int PlacedCount)> ReadTagAsync(Channel channel, long tagId)
    {
        await using var db = fixture.CreateDbContext();
        var tags = new EmoteTagService(db);
        var entries = await tags.ListEntriesAsync(channel.ChannelName, tagId, null);
        var list = await tags.ListAsync(channel.ChannelName, null);
        return (Assert.Single(entries.Entries), Assert.Single(list.Tags, t => t.Id == tagId).PlacedCount);
    }

    private async Task<DateTime?> LatestAsync(Channel channel, string emoteSetId, string sevenTvEmoteId)
    {
        await using var db = fixture.CreateDbContext();
        var latest = await EmoteSetLeaveObservations.LoadLatestAsync(db, channel.Id, emoteSetId, [sevenTvEmoteId], CancellationToken.None);
        return latest.TryGetValue(sevenTvEmoteId, out var observedAt) ? observedAt : null;
    }

    private static Task<Emote> LoadEmoteAsync(AppDbContext db, Channel channel, string sevenTvEmoteId) =>
        db.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == sevenTvEmoteId);

    private static SevenTvEmoteSetDelta Delta(IReadOnlyList<SevenTvEmote>? pushed = null, IReadOnlyList<string>? pulledIds = null) =>
        new(pushed ?? [], [], pulledIds ?? []);

    private static string ImageUrl(string sevenTvId) => $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp";

    private static SevenTvEmote Live(string sevenTvId) => new(sevenTvId, sevenTvId, ImageUrl(sevenTvId));

    private Task<Channel> SeedChannelAsync(string name, params (string SevenTvId, bool Archived, DateTime? EnteredAtUtc)[] emotes) =>
        SeedChannelAsync(name, SetId, emotes);

    private async Task<Channel> SeedChannelAsync(
        string name, string activeEmoteSetId, params (string SevenTvId, bool Archived, DateTime? EnteredAtUtc)[] emotes)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = activeEmoteSetId, IsBotActive = true };
        db.Channels.Add(channel);
        foreach (var (sevenTvId, archived, enteredAtUtc) in emotes)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                SevenTvEmoteId = sevenTvId,
                Name = sevenTvId,
                ImageUrl = ImageUrl(sevenTvId),
                IsArchived = archived,
                LastEnteredSetAtUtc = enteredAtUtc,
            });
        }

        await db.SaveChangesAsync();
        return channel;
    }

    private async Task<(long TagId, Guid OperationId)> SeedPlacementAsync(
        Channel channel, string sevenTvEmoteId, string emoteSetId, DateTime registeredAtUtc)
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
            ImageUrl = ImageUrl(sevenTvEmoteId),
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
        return (tag.Id, operationId);
    }

    // A tag with entries for the given emotes and nothing else — no operation, placement or activation.
    private async Task<long> SeedTagWithEntriesAsync(Channel channel, params string[] sevenTvEmoteIds)
    {
        await using var db = fixture.CreateDbContext();
        var tag = new EmoteTag { ChannelId = channel.Id, Name = "Stronghold", NormalizedName = "stronghold", CreatedAtUtc = DateTime.UtcNow };
        db.EmoteTags.Add(tag);
        await db.SaveChangesAsync();
        db.EmoteTagEntries.AddRange(sevenTvEmoteIds.Select(id => new EmoteTagEntry
        {
            TagId = tag.Id,
            SevenTvEmoteId = id,
            Alias = id,
            ImageUrl = ImageUrl(id),
            AddedAtUtc = DateTime.UtcNow,
        }));
        await db.SaveChangesAsync();
        return tag.Id;
    }

    private static bool IsObservationUpsert(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO \"EmoteSetLeaveObservations\"", StringComparison.Ordinal);

    // Deletes the channel from a second context right before the sync's observation upsert is sent —
    // the purge-or-merge interleaving the row gate does not hold off, at the one point where it
    // surfaces as a bare PostgresException (23503) rather than a failed save.
    private sealed class PurgeChannelBeforeObservationUpsert(PostgresFixture fixture, string channelId) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && IsObservationUpsert(command))
            {
                Fired = true;
                await using var purge = fixture.CreateDbContext();
                await purge.Channels.Where(c => c.Id == channelId).ExecuteDeleteAsync(cancellationToken);
            }

            return result;
        }
    }

    // Holds the first observation upsert of its context until released, with the caller's
    // transaction already open and its timestamp already taken.
    private sealed class PauseBeforeObservationUpsert : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (IsObservationUpsert(command) && Reached.TrySetResult())
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
