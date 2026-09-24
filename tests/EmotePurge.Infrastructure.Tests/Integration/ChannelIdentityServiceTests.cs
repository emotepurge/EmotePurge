using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// Integration rather than unit tests on purpose: the two things that can actually go wrong here —
/// a rename handing its name over to the row that is being deleted in the same transaction, and a
/// merge moving live-day rows onto a channel that already has some of those days — are both
/// decided by the real unique indexes (<c>IX_Channels_ChannelName</c>,
/// <c>IX_ChannelLiveDays_ChannelId_Date</c>). No in-memory provider and no mocked
/// <see cref="AppDbContext"/> would ever see either of them.
/// <para>
/// The channel names are prefixed per test because the fixture's database is shared across the
/// whole Postgres collection and <c>ReconcileActiveChannelsAsync</c> scans *every* active channel.
/// Rows left behind by other tests are inert here — the substituted Helix client answers nothing
/// for them, so they land in the "Twitch does not know this" branch that writes nothing — which is
/// also why the counters asserted exactly are the writing ones and <c>Checked</c>/
/// <c>LoginsMissing</c> are only asserted as lower bounds.
/// </para>
/// </summary>
[Collection("Postgres")]
public class ChannelIdentityServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheLoginIsUnchanged_TouchesNothing()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "identityunchanged", "10001");
        // Mixed case on purpose (Regel 9): Helix's login is compared against the stored, normalized
        // name, so a capitalized answer must not read as a rename.
        var harness = CreateHarness(db, [new TwitchUserIdentity("10001", "IdentityUnchanged")]);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(0, summary.Merged);
        Assert.Equal(0, summary.IdsBackfilled);
        Assert.Empty(harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.ChannelName == "identityunchanged");
        Assert.Null(channel.TrackingResumedAt);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenHelixReportsANewLogin_RenamesTheRowAndPublishesLeaveThenJoin()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityrenameold", "10002");
        var createdAt = seeded.CreatedAt;
        var harness = CreateHarness(db, [new TwitchUserIdentity("10002", "IdentityRenameNew")]);
        var before = DateTime.UtcNow;

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Renamed);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.Equal("identityrenamenew", channel.ChannelName);
        // The rename is exactly the tracking gap TrackingResumedAt exists for: between the rename on
        // Twitch and this reconcile the IRC join was pointed at a channel that no longer answered.
        // Asserted as "stamped during this call", not merely "not null" — a value copied from
        // CreatedAt or left over from an earlier join would pass the weaker check.
        Assert.NotNull(channel.TrackingResumedAt);
        Assert.InRange(
            channel.TrackingResumedAt.Value,
            before.AddMilliseconds(-1),
            DateTime.UtcNow.AddMilliseconds(1));
        // Tolerance, not equality: Postgres stores the column at microsecond resolution, so a
        // DateTime round-trip loses the sub-microsecond ticks the seeded value carried.
        Assert.Equal(createdAt, channel.CreatedAt, TimeSpan.FromMilliseconds(1));
        Assert.Empty(await verify.Channels.AsNoTracking().Where(c => c.ChannelName == "identityrenameold").ToListAsync());

        var entry = await verify.AuditLogEntries.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.ChannelRename && e.ChannelName == "identityrenamenew");
        Assert.Equal("system", entry.ActorLogin);
        Assert.Equal("system", entry.ActorTwitchUserId);
        Assert.NotNull(entry.DetailsJson);
        // Parsed, not substring-matched: the column is jsonb, so Postgres hands the payload back
        // reordered and reformatted — an assertion on the raw text tests the wrong thing.
        using var details = JsonDocument.Parse(entry.DetailsJson);
        Assert.Equal("identityrenameold", details.RootElement.GetProperty("oldLogin").GetString());
        Assert.Equal("identityrenamenew", details.RootElement.GetProperty("newLogin").GetString());
        Assert.Equal("10002", details.RootElement.GetProperty("twitchChannelId").GetString());

        // Order is the contract: the worker resolves the row by name when it handles the JOIN, and
        // the LEAVE is what drops the old name's match cache and EventAPI subscription.
        Assert.Equal(
            ["channel:bot:commands|LEAVE:identityrenameold", "channel:bot:commands|JOIN:identityrenamenew"],
            harness.Redis.Messages);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenRenaming_ClosesTheOpenObservationInterval()
    {
        // Spec 4.3, F9, AK 17: the periodic-reconcile RenameAsync is one of the observation log's
        // closing sites. Asserts the call into the service, not a row in the table — same as the
        // merge case above.
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityrenameobs1old", "10015");
        var emoteSetObservationService = Substitute.For<IChannelEmoteSetObservationService>();
        var harness = CreateHarness(
            db,
            [new TwitchUserIdentity("10015", "IdentityRenameObs1New")],
            emoteSetObservationService: emoteSetObservationService);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Renamed);
        await emoteSetObservationService.Received(1).CloseOpenIntervalAsync(
            seeded.Id, ChannelEmoteSetObservationClosedBy.Rename, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheTargetNameIsHeldByARowWithItsOwnDifferentId_SkipsBothRows()
    {
        await using var db = fixture.CreateDbContext();
        var renamed = await SeedChannelAsync(db, "identityblockedold", "10003");
        var blocker = await SeedChannelAsync(db, "identityblockednew", "19003");
        var harness = CreateHarness(db, [new TwitchUserIdentity("10003", "IdentityBlockedNew")]);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();
        // Second pass on the same warning state: this pair converges once the blocking row is itself
        // reconciled, but if that row is unreconcilable it never does — and then an undeduplicated
        // warning repeats hourly for the life of the process.
        await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(0, summary.Merged);
        Assert.Equal(0, summary.MergesRefused);
        Assert.Empty(harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("identityblockedold", (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == renamed.Id)).ChannelName);
        Assert.Equal("identityblockednew", (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == blocker.Id)).ChannelName);
        Assert.Single(
            harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("identityblockedold") && e.Message.Contains("19003"));
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheTargetNameIsHeldByAnIdLessRow_MergesItIntoTheIdRow()
    {
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, "identitymergeold", "10004");
        var loser = await SeedChannelAsync(db, "identitymergenew", twitchChannelId: null);
        // The survivor keeps emotes — the guard is about the *loser*, not about merging into an
        // empty channel.
        db.Emotes.Add(new Emote { ChannelId = survivor.Id, SevenTvEmoteId = "aaaaaaaaaaaaaaaaaaaaaaa4", Name = "identityKeep" });
        db.ChannelLiveDays.AddRange(
            new ChannelLiveDay { ChannelId = survivor.Id, Date = new DateOnly(2026, 8, 1), LiveMinutes = 10 },
            new ChannelLiveDay { ChannelId = survivor.Id, Date = new DateOnly(2026, 8, 2), LiveMinutes = 5 },
            // 2026-08-01 exists on both sides: the constructed collision the MAX rule is for.
            new ChannelLiveDay { ChannelId = loser.Id, Date = new DateOnly(2026, 8, 1), LiveMinutes = 30 },
            new ChannelLiveDay { ChannelId = loser.Id, Date = new DateOnly(2026, 8, 3), LiveMinutes = 7 });
        db.VoteSessions.AddRange(
            new VoteSession { ChannelId = survivor.Id, Title = "identity survivor session" },
            new VoteSession { ChannelId = loser.Id, Title = "identity loser session" });
        await db.SaveChangesAsync();
        var harness = CreateHarness(db, [new TwitchUserIdentity("10004", "IdentityMergeNew")]);
        var before = DateTime.UtcNow;

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Merged);
        Assert.Equal(0, summary.MergesRefused);
        Assert.Equal(0, summary.Renamed);

        await using var verify = fixture.CreateDbContext();
        var merged = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id);
        Assert.Equal("identitymergenew", merged.ChannelName);
        Assert.NotNull(merged.TrackingResumedAt);
        Assert.InRange(
            merged.TrackingResumedAt.Value,
            before.AddMilliseconds(-1),
            DateTime.UtcNow.AddMilliseconds(1));
        Assert.Empty(await verify.Channels.AsNoTracking().Where(c => c.Id == loser.Id).ToListAsync());

        var days = await verify.ChannelLiveDays.AsNoTracking()
            .Where(d => d.ChannelId == survivor.Id)
            .OrderBy(d => d.Date)
            .ToListAsync();
        Assert.Equal(3, days.Count);
        Assert.Equal(30, days[0].LiveMinutes); // MAX(10, 30) on the colliding day
        Assert.Equal(5, days[1].LiveMinutes);
        Assert.Equal(7, days[2].LiveMinutes);
        Assert.Empty(await verify.ChannelLiveDays.AsNoTracking().Where(d => d.ChannelId == loser.Id).ToListAsync());

        Assert.Equal(2, await verify.VoteSessions.AsNoTracking().CountAsync(s => s.ChannelId == survivor.Id));
        Assert.Equal(0, await verify.VoteSessions.AsNoTracking().CountAsync(s => s.ChannelId == loser.Id));

        var entry = await verify.AuditLogEntries.AsNoTracking()
            .SingleAsync(e => e.Action == AuditActions.ChannelMerge && e.ChannelName == "identitymergenew");
        Assert.Equal("system", entry.ActorLogin);
        Assert.NotNull(entry.DetailsJson);
        using var details = JsonDocument.Parse(entry.DetailsJson);
        Assert.Equal(survivor.Id, details.RootElement.GetProperty("survivorChannelId").GetString());
        Assert.Equal(loser.Id, details.RootElement.GetProperty("loserChannelId").GetString());
        Assert.Equal("identitymergeold", details.RootElement.GetProperty("oldLogin").GetString());
        // Moved and collapsed counted apart: the loser brought two days, one moved across and one was
        // folded into the survivor's existing 2026-08-01. A single "2 moved" would not reconcile with
        // the survivor going from two rows to three.
        Assert.Equal(1, details.RootElement.GetProperty("movedLiveDays").GetInt32());
        Assert.Equal(1, details.RootElement.GetProperty("collapsedLiveDays").GetInt32());
        Assert.Equal(1, details.RootElement.GetProperty("movedVoteSessions").GetInt32());

        Assert.Equal(
            ["channel:bot:commands|LEAVE:identitymergeold", "channel:bot:commands|JOIN:identitymergenew"],
            harness.Redis.Messages);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenMerging_ClosesTheSurvivorsOpenObservationInterval()
    {
        // Spec 4.3, F9: the surviving channel of a merge is one of the observation log's five
        // closing sites. Asserts the call into the service, not a row in the table — the loser needs
        // no call at all (its row cascades away with db.Channels.Remove), which this also proves by
        // never expecting a call for the loser's id.
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, "identitymergeobs1old", "10014");
        var loser = await SeedChannelAsync(db, "identitymergeobs1new", twitchChannelId: null);
        await db.SaveChangesAsync();
        var emoteSetObservationService = Substitute.For<IChannelEmoteSetObservationService>();
        var harness = CreateHarness(
            db,
            [new TwitchUserIdentity("10014", "IdentityMergeObs1New")],
            emoteSetObservationService: emoteSetObservationService);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Merged);
        await emoteSetObservationService.Received(1).CloseOpenIntervalAsync(
            survivor.Id, ChannelEmoteSetObservationClosedBy.Merge, Arg.Any<CancellationToken>());
        await emoteSetObservationService.DidNotReceive().CloseOpenIntervalAsync(
            loser.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_ForAnIdLessRowHelixKnows_BackfillsTheTwitchId()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identitybackfill", twitchChannelId: null);
        var harness = CreateHarness(db, [new TwitchUserIdentity("10005", "IdentityBackfill")]);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.IdsBackfilled);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(0, summary.Merged);
        Assert.Empty(harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.Equal("10005", channel.TwitchChannelId);
        Assert.Equal("identitybackfill", channel.ChannelName);
        // A backfill is not a coverage event — nothing was missed, so the tracking clock stays put.
        Assert.Null(channel.TrackingResumedAt);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenAnIdLessRowsLoginBelongsToAnotherRowsId_MergesWithTheIdRowSurviving()
    {
        // The duplicate case with the roles swapped: the row carrying the id is inactive (it was
        // left, or the rename happened while it was), so only the id-less row under the *new* login
        // is in the projection. The id row must still be the survivor — it owns the history.
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, "identityswapold", "10006", isBotActive: false);
        var loser = await SeedChannelAsync(db, "identityswapnew", twitchChannelId: null);
        // Set directly on the already-saved row: the survivor was deactivated some time ago, and the
        // merge making it active again must clear this the same way CompleteJoinAsync's reactivation
        // branch does.
        survivor.DeactivatedAtUtc = DateTime.UtcNow.AddDays(-10);
        db.ChannelLiveDays.Add(new ChannelLiveDay { ChannelId = loser.Id, Date = new DateOnly(2026, 8, 4), LiveMinutes = 12 });
        await db.SaveChangesAsync();
        var harness = CreateHarness(db, [new TwitchUserIdentity("10006", "IdentitySwapNew")]);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Merged);

        await using var verify = fixture.CreateDbContext();
        var merged = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id);
        Assert.Equal("identityswapnew", merged.ChannelName);
        // survivor.IsBotActive |= loser.IsBotActive — the merged channel is the one the bot is in.
        Assert.True(merged.IsBotActive);
        // The merge is what reactivated the survivor, so its retention clock must stop the same way
        // a reactivating join's does.
        Assert.Null(merged.DeactivatedAtUtc);
        Assert.Empty(await verify.Channels.AsNoTracking().Where(c => c.Id == loser.Id).ToListAsync());
        Assert.Equal(1, await verify.ChannelLiveDays.AsNoTracking().CountAsync(d => d.ChannelId == survivor.Id));

        Assert.Equal(
            ["channel:bot:commands|LEAVE:identityswapold", "channel:bot:commands|JOIN:identityswapnew"],
            harness.Redis.Messages);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheLoserStillHasEmotes_RefusesTheMergeAndLeavesBothRowsAlone()
    {
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, "identityrefuseold", "10007");
        var loser = await SeedChannelAsync(db, "identityrefusenew", twitchChannelId: null);
        db.Emotes.Add(new Emote { ChannelId = loser.Id, SevenTvEmoteId = "aaaaaaaaaaaaaaaaaaaaaaa7", Name = "identityRefuse" });
        await db.SaveChangesAsync();
        var harness = CreateHarness(db, [new TwitchUserIdentity("10007", "IdentityRefuseNew")]);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        // Exactly one, although the pass meets this duplicate pair from both ends — the id row
        // wanting the name and the id-less row holding it. Counting the same refusal twice would
        // make the worker's log line report twice the problems that exist.
        Assert.Equal(1, summary.MergesRefused);
        Assert.Equal(0, summary.Merged);
        Assert.Equal(0, summary.Renamed);
        Assert.Empty(harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("identityrefuseold", (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id)).ChannelName);
        var untouchedLoser = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == loser.Id);
        Assert.Equal("identityrefusenew", untouchedLoser.ChannelName);
        // The sharpest probe on "nothing was written": TrackingResumedAt and IsBotActive are the two
        // fields MergeAsync touches immediately after the guard, before the name it is named for.
        var untouchedSurvivor = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id);
        Assert.Null(untouchedSurvivor.TrackingResumedAt);
        Assert.True(untouchedSurvivor.IsBotActive);
        Assert.True(untouchedLoser.IsBotActive);
        Assert.Equal(1, await verify.Emotes.AsNoTracking().CountAsync(e => e.ChannelId == loser.Id));
        Assert.Empty(await verify.AuditLogEntries.AsNoTracking().Where(e => e.ChannelName == "identityrefusenew").ToListAsync());
        Assert.Contains(harness.Logger.Entries, e => e.Message.Contains(survivor.Id) && e.Message.Contains(loser.Id));
    }

    // GDPR Art. 21 objection gate (issue #252, revised #260): before this revision a merge was the
    // one place this pass could flip an inactive row active again
    // (`survivor.IsBotActive |= loser.IsBotActive`), so a blocked id refused it. Since this revision
    // the id-less duplicate is deactivated directly, the moment its login resolves to the blocked
    // id — before a merge is ever considered — so the row that would have been the merge's loser
    // simply stops being observed instead of staying an active, permanently-refused duplicate.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheChannelIdIsExcluded_DeactivatesTheActiveDuplicateAndDoesNotReactivateTheSurvivor()
    {
        await using var db = fixture.CreateDbContext();
        // The survivor is inactive — exactly the state a merge could otherwise reactivate, and
        // inactive rows are outside the reconcile's active-rows snapshot, so this row is never even
        // looked at this pass. The loser is the active id-less duplicate a rejoin during a Twitch
        // outage could have created before the id was known to be blocked (see the class remark on
        // MergeAsync).
        var survivor = await SeedChannelAsync(db, "identityexcludedold", "10099", isBotActive: false);
        var loser = await SeedChannelAsync(db, "identityexcludednew", twitchChannelId: null);
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10099").Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10099", "IdentityExcludedNew")], excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Deactivated);
        Assert.Equal(0, summary.MergesRefused);
        Assert.Equal(0, summary.Merged);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(["channel:bot:commands|LEAVE:identityexcludednew"], harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var untouchedSurvivor = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id);
        Assert.False(untouchedSurvivor.IsBotActive);
        Assert.Null(untouchedSurvivor.TrackingResumedAt);
        var deactivatedLoser = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == loser.Id);
        Assert.Equal("identityexcludednew", deactivatedLoser.ChannelName);
        Assert.False(deactivatedLoser.IsBotActive);
        Assert.NotNull(deactivatedLoser.DeactivatedAtUtc);
        // The survivor already holds the id, so the unique index leaves no room to write it onto the
        // duplicate too — the deactivation must still go through without it.
        Assert.Null(deactivatedLoser.TwitchChannelId);
        await AssertAnonymousExclusionLeaveAsync(verify, "identityexcludednew");
    }

    // P2 Codex finding (issue #260, revised further in this same revision): when *both* the id-bearing
    // row and its id-less duplicate are active, the projection carries both, and the pass reaches the
    // pair from each end — the id row via its own known id (ReconcileKnownIdRowAsync) and the id-less
    // row via its login resolving to that same id (ReconcileIdLessRowAsync). Both independently
    // deactivate themselves now: this is not the "one refusal reached from both ends" case that needs
    // deduplication (that dedup logic — settledChannelIds — still exists for MergeAsync's own
    // loser-has-emotes refusal, untouched), because two real rows really do change state here, so two
    // counted deactivations and two log lines are correct, not a double-count of one event.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheChannelIdIsExcluded_AndBothRowsAreActive_DeactivatesBothIndependently()
    {
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, "identityexcludedbothold", "10098");
        var loser = await SeedChannelAsync(db, "identityexcludedbothnew", twitchChannelId: null);
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10098").Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10098", "IdentityExcludedBothNew")], excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(2, summary.Deactivated);
        Assert.Equal(0, summary.MergesRefused);
        Assert.Equal(0, summary.Merged);
        Assert.Equal(0, summary.Renamed);
        // Order between the two is not part of the contract — the rows are visited in whatever order
        // the active-rows scan returns them in — but both LEAVEs must have happened.
        Assert.Equal(2, harness.Redis.Messages.Count);
        Assert.Contains("channel:bot:commands|LEAVE:identityexcludedbothold", harness.Redis.Messages);
        Assert.Contains("channel:bot:commands|LEAVE:identityexcludedbothnew", harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var deactivatedSurvivor = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id);
        Assert.Equal("identityexcludedbothold", deactivatedSurvivor.ChannelName);
        Assert.False(deactivatedSurvivor.IsBotActive);
        var deactivatedLoser = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == loser.Id);
        Assert.Equal("identityexcludedbothnew", deactivatedLoser.ChannelName);
        Assert.False(deactivatedLoser.IsBotActive);
        await AssertAnonymousExclusionLeaveAsync(verify, "identityexcludedbothold", "identityexcludedbothnew");
        // Two distinct rows deactivating is two events, not one repeated — unlike the merge-refusal
        // dedup case, this line is expected to appear twice.
        Assert.Equal(2, harness.Logger.Entries.Count(e => e.Message.Contains("excluded-channel list")));
    }

    // P1 Codex finding (issue #260, this revision): the scenario that motivated the general rule.
    // Both rows start active — a known-id row under its old login and its id-less duplicate under
    // the current one — exactly the state the previous two tests already cover for "both active".
    // What this test adds is the operator's documented next step (Operations.md: purge after
    // blocking) and the tick after it: before this fix, purging the duplicate freed its name for
    // RenameAsync to put the surviving blocked row onto — and publish a JOIN for. After the fix, the
    // surviving row is already deactivated (by the first reconcile, independently of the purge) and
    // stays that way; the purge only deletes data, it does not resurrect observation.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheActiveDuplicateIsPurgedAfterBothRowsWereDeactivated_NeverRenamesOrJoinsTheSurvivor()
    {
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, "identitypurgedupold", "10097");
        var loser = await SeedChannelAsync(db, "identitypurgedupnew", twitchChannelId: null);
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10097").Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10097", "IdentityPurgeDupNew")], excludedChannelFilter: excludedChannelFilter);

        var first = await harness.Service.ReconcileActiveChannelsAsync();
        Assert.NotNull(first);
        Assert.Equal(2, first.Deactivated);
        Assert.Equal(0, first.Renamed);
        Assert.Equal(0, first.Merged);

        // The operator's documented next step: purge the (now-inactive) duplicate to actually delete
        // its data. Done directly against the context here — PurgeAsync itself belongs to
        // ChannelService, not this service, and this test only needs the row gone.
        await using (var purgeDb = fixture.CreateDbContext())
        {
            var loserRow = await purgeDb.Channels.SingleAsync(c => c.Id == loser.Id);
            purgeDb.Channels.Remove(loserRow);
            await purgeDb.SaveChangesAsync();
        }

        var second = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(second);
        // The survivor is already inactive, so the second pass finds nothing left to touch for this
        // pair — in particular no RenameAsync onto the now-vacant name and no JOIN, which is exactly
        // the gap this fix closes.
        Assert.Equal(0, second.Renamed);
        Assert.Equal(0, second.Merged);
        Assert.DoesNotContain(harness.Redis.Messages, m => m.Contains("|JOIN:"));

        await using var verify = fixture.CreateDbContext();
        var survivorRow = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id);
        Assert.False(survivorRow.IsBotActive);
        Assert.Equal("identitypurgedupold", survivorRow.ChannelName);
        Assert.NotNull(survivorRow.DeactivatedAtUtc);
    }

    // The simplest shape of the general rule (issue #260, this revision): a single active row whose
    // already-known Twitch id turns out to be excluded is deactivated on sight, before Helix's
    // answer about the login is even consulted — not just when a rename or a merge would otherwise
    // follow.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheKnownIdIsExcluded_DeactivatesTheRowInsteadOfActingOnItsLogin()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityexcludedknown", "10096");
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10096").Returns(true);
        // Helix still answers with the *unchanged* login — Case 1's fast path — to prove the gate
        // fires before that fast path even gets a chance to run.
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10096", "IdentityExcludedKnown")], excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Deactivated);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(0, summary.Merged);
        Assert.Equal(["channel:bot:commands|LEAVE:identityexcludedknown"], harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.False(channel.IsBotActive);
        Assert.NotNull(channel.DeactivatedAtUtc);
        await AssertAnonymousExclusionLeaveAsync(verify, "identityexcludedknown");
    }

    // The objection gate's deactivation is a leave (it writes channel.leave), so it closes the open
    // emote-set observation interval in the same save, exactly like ChannelService.LeaveAsync (spec
    // 4.3). Without it an inactive row kept an open interval, breaking the "no open row implies
    // inactive" invariant RecordObservedSetAsync relies on.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheKnownIdIsExcluded_ClosesTheOpenObservationInterval()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityexcludedobs1", "10089");
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10089").Returns(true);
        var emoteSetObservationService = Substitute.For<IChannelEmoteSetObservationService>();
        var harness = CreateHarness(
            db,
            [new TwitchUserIdentity("10089", "IdentityExcludedObs1")],
            emoteSetObservationService: emoteSetObservationService,
            excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Deactivated);
        await emoteSetObservationService.Received(1).CloseOpenIntervalAsync(
            seeded.Id, ChannelEmoteSetObservationClosedBy.Leave, Arg.Any<CancellationToken>());
    }

    // The BackfillIdAsync residual gap the #252 DECISIONS entry used to document explicitly, closed
    // by this revision: an id-less active row whose login now resolves to an excluded id must not be
    // backfilled into observation under that id — it is deactivated instead, and the id is
    // deliberately left unwritten (Assert.Null below), because the row is being told to stop being
    // observed, not brought into observation under a name that could be found again.
    // Fourth Codex review: the deactivated row now also keeps the id it resolved to — without it, a
    // later join by its login while Twitch answered Unavailable or NotFound found the row by name,
    // saw no id to check and reactivated it (see the join-path test further down).
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenAnIdLessRowsLoginResolvesToAnExcludedId_DeactivatesItAndWritesTheIdDown()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityexcludedbackfill", twitchChannelId: null);
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10095").Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10095", "IdentityExcludedBackfill")], excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Deactivated);
        Assert.Equal(0, summary.IdsBackfilled);
        Assert.Equal(["channel:bot:commands|LEAVE:identityexcludedbackfill"], harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.False(channel.IsBotActive);
        Assert.Equal("10095", channel.TwitchChannelId);
        Assert.NotNull(channel.DeactivatedAtUtc);
    }

    // The end-to-end shape of the fourth review's second P1: once the reconcile has deactivated an
    // id-less row for an excluded id, a join by that row's login must stay refused whatever Twitch
    // answers — Unavailable (no identity at all) and NotFound (the login is unknown right now) are
    // the two answers that bypass the join path's identity check and fall back to the row by name.
    [Theory]
    [InlineData(TwitchUserLookupStatus.Unavailable)]
    [InlineData(TwitchUserLookupStatus.NotFound)]
    public async Task ReconcileActiveChannelsAsync_AfterDeactivatingAnIdLessExcludedRow_AJoinByItsLoginStaysRefusedWhateverTwitchAnswers(
        TwitchUserLookupStatus joinLookupStatus)
    {
        await using var db = fixture.CreateDbContext();
        var login = $"identityexcludedrejoin{(int)joinLookupStatus}";
        var twitchChannelId = $"1009{(int)joinLookupStatus}0";
        var seeded = await SeedChannelAsync(db, login, twitchChannelId: null);
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded(twitchChannelId).Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity(twitchChannelId, login)], excludedChannelFilter: excludedChannelFilter);
        Assert.Equal(1, (await harness.Service.ReconcileActiveChannelsAsync())?.Deactivated);

        await using var joinDb = fixture.CreateDbContext();
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TwitchUserLookup.Failed(joinLookupStatus));
        var channelService = new ChannelService(
            joinDb,
            Substitute.For<IRedisPublisher>(),
            identityService,
            new ChannelEmoteSetObservationService(joinDb),
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            excludedChannelFilter,
            NullLogger<ChannelService>.Instance);

        var result = await channelService.JoinAsync(login, new AuditActor("4711", "sensitron"));

        Assert.Equal(ChannelJoinStatus.ChannelExcluded, result.Status);
        await using var verify = fixture.CreateDbContext();
        Assert.False((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id)).IsBotActive);
    }

    // P1 Codex finding (issue #260, third review round): the two early returns below (no app token,
    // Helix unreachable) used to skip the entire pass, including the known-id exclusion gate above —
    // an active row whose STORED Twitch id is excluded therefore kept being observed for as long as
    // an outage lasted, exactly the observation the block list exists to stop. Deactivating such a
    // row needs no Helix answer at all, so it now runs before either early return, and a token
    // outage can no longer buy an excluded channel continued observation.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WithoutAnAppToken_StillDeactivatesARowWhoseStoredIdIsExcluded()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityexcludednotoken", "10094");
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10094").Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10094", "IdentityExcludedNoToken")], token: null, excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        // Not the null the plain "no token" case returns (see the test above without an exclusion):
        // something real was written despite the outage, and that is worth the worker's log line.
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Deactivated);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(0, summary.Merged);
        await harness.Helix.DidNotReceive().GetUsersAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(["channel:bot:commands|LEAVE:identityexcludednotoken"], harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.False(channel.IsBotActive);
        Assert.NotNull(channel.DeactivatedAtUtc);
    }

    // The Helix-unreachable counterpart of the test above — same gap, the other early return.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenHelixIsUnavailable_StillDeactivatesARowWhoseStoredIdIsExcluded()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityexcludedhelixdown", "10093");
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10093").Returns(true);
        var harness = CreateHarness(db, identities: null, excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(1, summary.Deactivated);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(0, summary.Merged);
        Assert.Equal(["channel:bot:commands|LEAVE:identityexcludedhelixdown"], harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.False(channel.IsBotActive);
        Assert.NotNull(channel.DeactivatedAtUtc);
    }

    // P1 Codex finding (issue #260, third review round): a thrown LEAVE publish inside
    // ChannelDeactivation.DeactivateAsync used to escape DeactivateExcludedRowAsync uncaught — the
    // deactivation itself commits before the publish, so nothing here could roll it back, but the
    // exception nonetheless propagated straight out of ReconcileActiveChannelsAsync, taking every
    // row behind it in the pass (and the whole tick's summary) down with it. Two independently
    // excluded rows prove the row after the failing one still gets processed rather than the tick
    // aborting on the first publish failure.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheExcludedRowsLeavePublishFails_StillDeactivatesEveryExcludedRow()
    {
        await using var db = fixture.CreateDbContext();
        var first = await SeedChannelAsync(db, "idexcludedpubfaila", "10092");
        var second = await SeedChannelAsync(db, "idexcludedpubfailb", "10091");
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10092").Returns(true);
        excludedChannelFilter.IsExcluded("10091").Returns(true);
        var harness = CreateHarness(
            db,
            [
                new TwitchUserIdentity("10092", "IdentityExcludedPublishFailA"),
                new TwitchUserIdentity("10091", "IdentityExcludedPublishFailB")
            ],
            excludedChannelFilter: excludedChannelFilter,
            failPublishes: true);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(2, summary.Deactivated);
        Assert.Contains(
            harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("LEAVE announcement could not be published"));
        // Neither channel's name leaked into the warning, same restraint as the success log line.
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("idexcludedpubfaila"));
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("idexcludedpubfailb"));

        await using var verify = fixture.CreateDbContext();
        Assert.False((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == first.Id)).IsBotActive);
        Assert.False((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == second.Id)).IsBotActive);
        Assert.NotNull((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == first.Id)).DeactivatedAtUtc);
        Assert.NotNull((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == second.Id)).DeactivatedAtUtc);
    }

    // P1 Codex finding (issue #260, third review round): DeactivateExcludedRowAsync used to reload
    // by row.ChannelName, not by the row's own primary key. A concurrent purge of the exact row this
    // decision was made for, followed by a fresh join under the same login, replaces it with an
    // unrelated row before the write — a name-based reload would find and deactivate *that* row
    // instead, silently pulling an active, unexcluded channel back out of observation. Simulated
    // without real concurrency by piggy-backing on the one call every row makes before its own
    // deactivation (IExcludedChannelFilter.IsExcluded): the substitute performs the "concurrent"
    // purge-then-join right there, between the snapshot ReconcileActiveChannelsAsync already took
    // and the reload DeactivateExcludedRowAsync is about to do for that same row.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheRowIsPurgedAndReplacedUnderTheSameLoginDuringTheTick_SkipsTheReplacementInsteadOfDeactivatingIt()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identitypurgeracereplaced", "10090");
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        var raced = false;
        excludedChannelFilter.IsExcluded(Arg.Any<string?>()).Returns(callInfo =>
        {
            var id = (string?)callInfo[0];
            if (id != "10090" || raced)
            {
                return id == "10090";
            }

            raced = true;
            // Two round trips, not one batched Remove+Add, so the unique index on ChannelName is
            // never even momentarily double-claimed.
            using (var purgeDb = fixture.CreateDbContext())
            {
                purgeDb.Channels.Remove(purgeDb.Channels.Single(c => c.Id == seeded.Id));
                purgeDb.SaveChanges();
            }

            using (var joinDb = fixture.CreateDbContext())
            {
                joinDb.Channels.Add(new Channel
                {
                    ChannelName = "identitypurgeracereplaced",
                    TwitchChannelId = "20099",
                    IsBotActive = true
                });
                joinDb.SaveChanges();
            }

            return true;
        });
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10090", "IdentityPurgeRaceReplaced")], excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        // Nothing was deactivated: the original row is gone (purging its data is not this pass's
        // concern) and the replacement never carried the excluded id, so reloading by primary key
        // finds nothing left to act on rather than silently deactivating the wrong, unrelated row.
        Assert.Equal(0, summary.Deactivated);
        Assert.Empty(harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var replacement = await verify.Channels.AsNoTracking().SingleAsync(c => c.ChannelName == "identitypurgeracereplaced");
        Assert.True(replacement.IsBotActive);
        Assert.Equal("20099", replacement.TwitchChannelId);
        Assert.NotEqual(seeded.Id, replacement.Id);
        Assert.Null(replacement.DeactivatedAtUtc);
    }

    // Fourth Codex review, finding 3: a failed deactivation write on either exclusion path — the
    // known-id pass ahead of the Helix call, and the id-less path inside the main loop — used to reach
    // a catch that logged the row's login and internal id. Injected at the save, where a real
    // failure happens; the row stays active for the next pass to retry.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileActiveChannelsAsync_WhenAnExcludedRowsDeactivationFails_WarnsWithoutNamingIt(bool idLess)
    {
        await using var db = fixture.CreateDbContext();
        var login = idLess ? "identityexcludedfailnoid" : "identityexcludedfailknown";
        var twitchChannelId = idLess ? "10281" : "10282";
        var seeded = await SeedChannelAsync(db, login, idLess ? null : twitchChannelId);
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded(twitchChannelId).Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity(twitchChannelId, login)], excludedChannelFilter: excludedChannelFilter);
        db.SavingChanges += (_, _) => throw new DbUpdateException("injected save failure");

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Deactivated);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("excluded-channel list"));
        foreach (var identifier in new[] { login, seeded.Id, twitchChannelId })
        {
            Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains(identifier, StringComparison.Ordinal));
        }

        Assert.Empty(harness.Redis.Messages);
        await using var verify = fixture.CreateDbContext();
        Assert.True((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id)).IsBotActive);
    }

    // Fourth Codex review: case 3 ("the new login is held by a row with a different id") names both
    // rows and the blocking row's id. When that row is the blocked channel's, all three tie the block
    // to it — the new login is the name the blocked channel last had.
    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenTheNewLoginIsHeldByAnExcludedRow_SkipsAndNamesNothing()
    {
        await using var db = fixture.CreateDbContext();
        var row = await SeedChannelAsync(db, "identitycase3excludedold", "10291");
        var blocked = await SeedChannelAsync(db, "identitycase3excludednew", "10292", isBotActive: false);
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("10292").Returns(true);
        var harness = CreateHarness(
            db, [new TwitchUserIdentity("10291", "IdentityCase3ExcludedNew")], excludedChannelFilter: excludedChannelFilter);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Renamed);
        Assert.Equal(0, summary.Merged);
        Assert.Contains(harness.Logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("excluded-channel list"));
        foreach (var identifier in new[] { "identitycase3excluded", "10291", "10292", row.Id, blocked.Id })
        {
            Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains(identifier, StringComparison.Ordinal));
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("identitycase3excludedold", (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == row.Id)).ChannelName);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenAMergeStaysRefused_WarnsOncePerProcessRun()
    {
        // The refusal is the one state in this service that *cannot* resolve by itself — it waits
        // for a person to move or delete the loser's emotes. Undeduplicated it would therefore warn
        // every tick forever, which is exactly what the blocked case is deduplicated against.
        await using var db = fixture.CreateDbContext();
        var survivor = await SeedChannelAsync(db, "identityrefusededupold", "10011");
        var loser = await SeedChannelAsync(db, "identityrefusededupnew", twitchChannelId: null);
        db.Emotes.Add(new Emote { ChannelId = loser.Id, SevenTvEmoteId = "aaaaaaaaaaaaaaaaaaaaaab1", Name = "identityRefuseDedup" });
        await db.SaveChangesAsync();
        var warningState = new ChannelIdentityWarningState();
        var harness = CreateHarness(db, [new TwitchUserIdentity("10011", "IdentityRefuseDedupNew")], warningState: warningState);

        var first = await harness.Service.ReconcileActiveChannelsAsync();
        var second = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        // The counter keeps reporting on every tick — that is what keeps the state visible in the
        // worker's summary line once the individual warning has fallen silent.
        Assert.Equal(1, first.MergesRefused);
        Assert.Equal(1, second.MergesRefused);
        Assert.Single(
            harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains(loser.Id));

        // Same second half as the dead-login and dead-id cases: a fresh process reports the still
        // unresolved pair once more rather than inheriting the silence.
        var restarted = CreateHarness(
            db,
            [new TwitchUserIdentity("10011", "IdentityRefuseDedupNew")],
            warningState: new ChannelIdentityWarningState());
        await restarted.Service.ReconcileActiveChannelsAsync();
        Assert.Single(
            restarted.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains(loser.Id));

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("identityrefusededupold", (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Id)).ChannelName);
        Assert.Equal(1, await verify.Emotes.AsNoTracking().CountAsync(e => e.ChannelId == loser.Id));
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WithoutAnAppToken_SkipsTheTickWithoutAskingHelix()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identitynotoken", "10008");
        var harness = CreateHarness(db, [new TwitchUserIdentity("10008", "IdentityNoTokenNew")], token: null);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.Null(summary);
        await harness.Helix.DidNotReceive().GetUsersAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Empty(harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal("identitynotoken", (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id)).ChannelName);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenHelixIsUnavailable_WritesNothingAndReturnsNull()
    {
        // The distinction this rests on: a null answer is "we could not ask", never "nobody exists".
        // Treating it as the latter would rename or merge on an empty result set.
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identityhelixdown", "10009");
        var harness = CreateHarness(db, identities: null);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.Null(summary);
        Assert.Empty(harness.Redis.Messages);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.Equal("identityhelixdown", channel.ChannelName);
        Assert.Equal("10009", channel.TwitchChannelId);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_ForALoginHelixDoesNotKnow_WarnsOncePerProcessRun()
    {
        await using var db = fixture.CreateDbContext();
        await SeedChannelAsync(db, "identitymissinglogin", twitchChannelId: null);
        var warningState = new ChannelIdentityWarningState();
        var harness = CreateHarness(db, [], warningState: warningState);

        var first = await harness.Service.ReconcileActiveChannelsAsync();
        var second = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.LoginsMissing >= 1);
        Assert.True(second.LoginsMissing >= 1);
        Assert.Empty(harness.Redis.Messages);
        // The level is part of the contract, not decoration: a downgrade to Debug would silence this
        // in production while every message assertion stayed green.
        Assert.Single(
            harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("identitymissinglogin"));

        // A fresh process (a fresh warning state) must report it again — that is the other half of
        // the bar: no flood, but never silence.
        var restarted = CreateHarness(db, [], warningState: new ChannelIdentityWarningState());
        await restarted.Service.ReconcileActiveChannelsAsync();
        Assert.Single(
            restarted.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("identitymissinglogin"));
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_ForAnIdHelixDoesNotKnow_WarnsOncePerProcessRun()
    {
        // A deleted or banned account: the id resolves to nothing in an otherwise successful Helix
        // response. Nothing may be written — the row is the only record that channel ever existed.
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identitymissingid", "10010");
        var harness = CreateHarness(db, []);

        var first = await harness.Service.ReconcileActiveChannelsAsync();
        var second = await harness.Service.ReconcileActiveChannelsAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.LoginsMissing >= 1);
        Assert.Single(
            harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("10010"));

        // Same second half as its case-5 twin: a restart must report the still-dead account again.
        var restarted = CreateHarness(db, [], warningState: new ChannelIdentityWarningState());
        await restarted.Service.ReconcileActiveChannelsAsync();
        Assert.Single(
            restarted.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("10010"));

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id);
        Assert.Equal("identitymissingid", channel.ChannelName);
        Assert.Equal("10010", channel.TwitchChannelId);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenOneRowsWriteFails_SkipsItAndFinishesTheTick()
    {
        await using var db = fixture.CreateDbContext();
        var first = await SeedChannelAsync(db, "identityracea", "10031");
        var second = await SeedChannelAsync(db, "identityraceb", "10032");
        var harness = CreateHarness(db, [
            new TwitchUserIdentity("10031", "IdentityRaceAlpha"),
            new TwitchUserIdentity("10032", "IdentityRaceBeta")]);

        // The race the snapshot cannot close, injected where it actually happens: between the "is the
        // target name free?" check and the write. SavingChanges fires inside SaveChangesAsync, so the
        // blocking row is committed on its own connection while the rename is already in flight —
        // exactly what a parallel join does. Only the first save is interfered with, so the second
        // row must still get through.
        var interfered = false;
        db.SavingChanges += (_, _) =>
        {
            if (interfered)
            {
                return;
            }

            interfered = true;
            var pendingName = db.ChangeTracker.Entries<Channel>()
                .Single(e => e.State == EntityState.Modified).Entity.ChannelName;
            using var blocker = fixture.CreateDbContext();
            blocker.Channels.Add(new Channel { ChannelName = pendingName, IsBotActive = false });
            blocker.SaveChanges();
        };

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        // The tick survives: before the per-row catch, the DbUpdateException left every unvisited row
        // unprocessed and threw the summary away with it.
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Renamed);
        Assert.True(interfered);
        Assert.Contains(
            harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("fehlgeschlagen"));

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.Channels.AsNoTracking()
            .Where(c => c.Id == first.Id || c.Id == second.Id)
            .ToListAsync();
        // Which of the two loses the race depends on the scan order and does not matter — what
        // matters is that exactly one was renamed and the other kept its old name untouched.
        Assert.Single(rows, r => r.ChannelName is "identityracealpha" or "identityracebeta");
        Assert.Single(rows, r => r.ChannelName is "identityracea" or "identityraceb");
        Assert.Equal(2, harness.Redis.Messages.Count);
    }

    [Fact]
    public async Task ReconcileActiveChannelsAsync_WhenPublishingFails_KeepsTheCommittedRenameAndWarns()
    {
        await using var db = fixture.CreateDbContext();
        var seeded = await SeedChannelAsync(db, "identitypublishfail", "10041");
        var harness = CreateHarness(db, [new TwitchUserIdentity("10041", "IdentityPublishFailNew")], failPublishes: true);

        var summary = await harness.Service.ReconcileActiveChannelsAsync();

        // The row is committed before the publish, so letting the exception escape could not undo it
        // — it would only cost the rest of the tick. And it cannot be retried: the next pass sees the
        // stored name already matching Helix (case 1) and never publishes again.
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Renamed);
        Assert.Contains(
            harness.Logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("identitypublishfail"));

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(
            "identitypublishfailnew",
            (await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Id)).ChannelName);
    }

    [Fact]
    public async Task LookupByLoginAsync_ReturnsFoundAndNormalizesTheLoginItAsksFor()
    {
        await using var db = fixture.CreateDbContext();
        var harness = CreateHarness(db, [new TwitchUserIdentity("10020", "IdentityLookup")]);

        var lookup = await harness.Service.LookupByLoginAsync("  IdentityLookup  ");

        Assert.Equal(TwitchUserLookupStatus.Found, lookup.Status);
        Assert.Equal("10020", lookup.User!.Id);
        await harness.Helix.Received(1).GetUsersAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 0),
            Arg.Is<IReadOnlyCollection<string>>(logins => logins.Single() == "identitylookup"),
            "identity-app-token",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LookupByLoginAsync_ReturnsNotFoundForAnEmptyButSuccessfulResponse()
    {
        await using var db = fixture.CreateDbContext();
        var harness = CreateHarness(db, []);

        var lookup = await harness.Service.LookupByLoginAsync("identitylookupmissing");

        Assert.Equal(TwitchUserLookupStatus.NotFound, lookup.Status);
        Assert.Null(lookup.User);
    }

    [Fact]
    public async Task LookupByLoginAsync_ReturnsUnavailableWithoutAnAppToken()
    {
        await using var db = fixture.CreateDbContext();
        var harness = CreateHarness(db, [new TwitchUserIdentity("10021", "identitylookuptoken")], token: null);

        var lookup = await harness.Service.LookupByLoginAsync("identitylookuptoken");

        // Unavailable, not NotFound: the join path in Task 7 must keep today's behaviour instead of
        // rejecting a channel because we could not reach Twitch.
        Assert.Equal(TwitchUserLookupStatus.Unavailable, lookup.Status);
        Assert.Null(lookup.User);
        // Fourth Codex review: the join path can still refuse a row with a blocked stored id after
        // this, and a line naming the login right before that refusal would tie the block to it.
        Assert.Contains(harness.Logger.Entries, e => e.Message.Contains("No app token"));
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("identitylookuptoken"));
    }

    [Fact]
    public async Task LookupByLoginAsync_ReturnsUnavailableWhenHelixFails()
    {
        await using var db = fixture.CreateDbContext();
        var harness = CreateHarness(db, identities: null);

        var lookup = await harness.Service.LookupByLoginAsync("identitylookupdown");

        Assert.Equal(TwitchUserLookupStatus.Unavailable, lookup.Status);
        Assert.Null(lookup.User);
        Assert.Contains(harness.Logger.Entries, e => e.Message.Contains("Helix not reachable"));
        Assert.DoesNotContain(harness.Logger.Entries, e => e.Message.Contains("identitylookupdown"));
    }

    // Fourth Codex review: a channel.leave by the system actor is written for nothing but the
    // objection gate, so an entry naming the channel would tell every admin reading the audit log
    // which channel the objection concerns. The entry exists, but carries only the reason.
    private static async Task AssertAnonymousExclusionLeaveAsync(AppDbContext verify, params string[] channelNames)
    {
        Assert.False(await verify.AuditLogEntries.AsNoTracking().AnyAsync(e => channelNames.Contains(e.ChannelName)));
        var anonymousSystemLeaves = await verify.AuditLogEntries.AsNoTracking()
            .Where(e => e.Action == AuditActions.ChannelLeave && e.ActorLogin == "system" && e.ChannelName == null)
            .Select(e => e.DetailsJson)
            .ToListAsync();
        Assert.Contains(anonymousSystemLeaves, details => details is not null && details.Contains("\"excluded\"", StringComparison.Ordinal));
    }

    private static async Task<Channel> SeedChannelAsync(
        AppDbContext db, string channelName, string? twitchChannelId, bool isBotActive = true)
    {
        var channel = new Channel
        {
            ChannelName = ChannelName.Normalize(channelName),
            TwitchChannelId = twitchChannelId,
            IsBotActive = isBotActive
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    // identities: null = Helix could not be reached; an empty list = a successful response that
    // knows none of the ids/logins asked for. Those two must never be conflated, which is why the
    // harness makes the difference a single argument.
    private static Harness CreateHarness(
        AppDbContext db,
        IReadOnlyList<TwitchUserIdentity>? identities,
        string? token = "identity-app-token",
        ChannelIdentityWarningState? warningState = null,
        bool failPublishes = false,
        IChannelEmoteSetObservationService? emoteSetObservationService = null,
        IExcludedChannelFilter? excludedChannelFilter = null)
    {
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetUsersAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(identities);

        var appTokenProvider = Substitute.For<ITwitchAppTokenProvider>();
        appTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns(token);

        var publisher = new RecordingPublisher(failPublishes);
        var logger = new RecordingLogger<ChannelIdentityService>();
        var state = warningState ?? new ChannelIdentityWarningState();
        var emoteSetObservations = emoteSetObservationService ?? Substitute.For<IChannelEmoteSetObservationService>();

        return new Harness(
            helix,
            publisher,
            logger,
            emoteSetObservations,
            new ChannelIdentityService(
                db, helix, appTokenProvider, publisher, emoteSetObservations, state,
                excludedChannelFilter ?? Substitute.For<IExcludedChannelFilter>(), logger));
    }

    private sealed record Harness(
        ITwitchHelixClient Helix,
        RecordingPublisher Redis,
        RecordingLogger<ChannelIdentityService> Logger,
        IChannelEmoteSetObservationService EmoteSetObservations,
        ChannelIdentityService Service);

    // Order matters here in a way NSubstitute's Received() cannot express as clearly: LEAVE has to
    // precede JOIN, so the fake keeps the sequence rather than a set of calls.
    private sealed class RecordingPublisher(bool fails = false) : IRedisPublisher
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public Task PublishAsync(string channel, string message, CancellationToken cancellationToken = default)
        {
            // Recorded before it throws: a Redis outage happens on the wire, so the call was made.
            _messages.Add($"{channel}|{message}");
            if (fails)
            {
                throw new InvalidOperationException("Redis nicht erreichbar (Test).");
            }

            return Task.CompletedTask;
        }
    }
}
