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
    }

    [Fact]
    public async Task LookupByLoginAsync_ReturnsUnavailableWhenHelixFails()
    {
        await using var db = fixture.CreateDbContext();
        var harness = CreateHarness(db, identities: null);

        var lookup = await harness.Service.LookupByLoginAsync("identitylookupdown");

        Assert.Equal(TwitchUserLookupStatus.Unavailable, lookup.Status);
        Assert.Null(lookup.User);
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
        IChannelEmoteSetObservationService? emoteSetObservationService = null)
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
            new ChannelIdentityService(db, helix, appTokenProvider, publisher, emoteSetObservations, state, logger));
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
