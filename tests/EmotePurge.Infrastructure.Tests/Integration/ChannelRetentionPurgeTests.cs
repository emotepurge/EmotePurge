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

// The retention purge (IChannelService.PurgeIfInactiveSinceAsync) and the row lock it shares with the
// join and the identity merge (ChannelQueries.LoadChannelForUpdateAsync). Against real Postgres: what
// is asserted is FK cascades and row-lock waits, neither of which a mocked context has.
//
// The races are made deterministic the way AccountDeletionServiceTests does it (PostgresLockProbe):
// every contender runs on its own tagged context, and the test waits until pg_stat_activity reports it
// waiting on a lock. To keep the first contender inside its transaction *after* it has taken the
// channel lock, the test holds the audit log in SHARE mode — every path under test writes an audit
// entry right before its commit, so it parks exactly there, lock in hand.
[Collection("Postgres")]
public class ChannelRetentionPurgeTests(PostgresFixture fixture)
{
    private static readonly AuditActor Moderator = new("retention-mod", "retentionmod");

    [Fact]
    public async Task PurgeIfInactiveSince_DueChannel_DeletesItsWholeHistory_AndAuditsTheRetentionReason()
    {
        var cutoff = Cutoff();
        var seeded = await SeedChannelWithHistoryAsync("retentiondue", isBotActive: false, deactivatedAtUtc: cutoff.AddMinutes(-1));

        await using var db = fixture.CreateDbContext();
        // Mixed case on purpose (Regel 9): the lookup normalizes.
        var result = await CreateService(db).PurgeIfInactiveSinceAsync("RetentionDue", cutoff, AuditActor.System);

        Assert.Equal(ChannelRetentionPurgeResult.Purged, result);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == seeded.Channel.Id));
        Assert.False(await verify.Emotes.AnyAsync(e => e.ChannelId == seeded.Channel.Id));
        Assert.False(await verify.UsageStats.AnyAsync(u => u.EmoteId == seeded.EmoteId));
        Assert.False(await verify.ChannelLiveDays.AnyAsync(d => d.ChannelId == seeded.Channel.Id));
        Assert.False(await verify.VoteSessions.AnyAsync(s => s.ChannelId == seeded.Channel.Id));
        Assert.False(await verify.VoteSessionEmotes.AnyAsync(b => b.VoteSessionId == seeded.SessionId));
        Assert.False(await verify.Votes.AnyAsync(v => v.VoteSessionId == seeded.SessionId));
        // The voter is not channel data and stays.
        Assert.True(await verify.Users.AnyAsync(u => u.Id == seeded.VoterId));

        var entry = Assert.Single(await LoadAuditEntriesAsync("retentiondue"), e => e.Action == AuditActions.ChannelPurge);
        Assert.Equal(AuditActor.System.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(AuditActor.System.Login, entry.ActorLogin);
        Assert.NotNull(entry.DetailsJson);
        using var details = JsonDocument.Parse(entry.DetailsJson);
        Assert.Equal("retention", details.RootElement.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PurgeIfInactiveSince_DeactivatedAtOrAfterTheCutoff_IsNotDue(int minutesAfterCutoff)
    {
        // The cutoff is exclusive: a channel deactivated exactly at it has not been inactive for the
        // full period yet.
        var cutoff = Cutoff();
        var name = $"retentionnotdue{minutesAfterCutoff}";
        var seeded = await SeedChannelWithHistoryAsync(name, isBotActive: false, deactivatedAtUtc: cutoff.AddMinutes(minutesAfterCutoff));

        await AssertNotPurgedAsync(name, cutoff, seeded.Channel.Id, ChannelRetentionPurgeResult.StillActive);
    }

    [Fact]
    public async Task PurgeIfInactiveSince_InactiveRowWithoutAStamp_IsNeverDue()
    {
        // Rows the job has not stamped yet (left under an image that predates the column) must never
        // compare as due — the job stamps them first, and the period starts there.
        var seeded = await SeedChannelWithHistoryAsync("retentionnostamp", isBotActive: false, deactivatedAtUtc: null);

        await AssertNotPurgedAsync("retentionnostamp", Cutoff(), seeded.Channel.Id, ChannelRetentionPurgeResult.StillActive);
    }

    [Fact]
    public async Task PurgeIfInactiveSince_ActiveRow_IsNeverPurged_EvenWithAStaleStamp()
    {
        // Not a state the code produces (every activation nulls the stamp), which is exactly why the
        // purge checks IsBotActive on its own rather than trusting the stamp alone.
        var cutoff = Cutoff();
        var seeded = await SeedChannelWithHistoryAsync("retentionactive", isBotActive: true, deactivatedAtUtc: cutoff.AddDays(-30));

        await AssertNotPurgedAsync("retentionactive", cutoff, seeded.Channel.Id, ChannelRetentionPurgeResult.StillActive);
    }

    [Fact]
    public async Task PurgeIfInactiveSince_ChannelRejoinedAfterItsCandidateSelection_IsNotPurged()
    {
        var cutoff = Cutoff();
        var seeded = await SeedChannelWithHistoryAsync("retentionrejoined", isBotActive: false, deactivatedAtUtc: cutoff.AddDays(-1));
        await using (var joinDb = fixture.CreateDbContext())
        {
            var join = await CreateService(joinDb).JoinAsync("retentionrejoined", Moderator);
            Assert.Equal(ChannelJoinStatus.Joined, join.Status);
        }

        await AssertNotPurgedAsync("retentionrejoined", cutoff, seeded.Channel.Id, ChannelRetentionPurgeResult.StillActive);
    }

    [Fact]
    public async Task PurgeIfInactiveSince_UnknownChannel_ReturnsNotFound_AndWritesNothing()
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).PurgeIfInactiveSinceAsync("retentionunknown", Cutoff(), AuditActor.System);

        Assert.Equal(ChannelRetentionPurgeResult.NotFound, result);
        Assert.Empty(await LoadAuditEntriesAsync("retentionunknown"));
    }

    [Fact]
    public async Task PurgeIfInactiveSince_RejectsANonUtcCutoff()
    {
        await using var db = fixture.CreateDbContext();
        var local = DateTime.SpecifyKind(Cutoff(), DateTimeKind.Local);

        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateService(db).PurgeIfInactiveSinceAsync("retentionlocalcutoff", local, AuditActor.System));
    }

    [Fact]
    public async Task ChannelRowLocks_RefuseToRunOutsideATransaction()
    {
        // A FOR UPDATE in an autocommit statement is released the moment it returns.
        await using var db = fixture.CreateDbContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LoadChannelForUpdateAsync("retentionnotx", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LoadChannelByTwitchIdForUpdateAsync("retention-notx", CancellationToken.None));
    }

    [Fact]
    public async Task ChannelRowLock_RereadsARowTheContextAlreadyTracked()
    {
        // A tracking query does not overwrite an instance the context already tracks — without the
        // reload, locking a row read earlier would hand back exactly the stale state the lock is for.
        await SeedChannelAsync("retentionstale", twitchChannelId: null, isBotActive: false, deactivatedAtUtc: Cutoff());
        await using var db = fixture.CreateDbContext();
        var stale = await db.LoadChannelAsync("retentionstale", CancellationToken.None);
        Assert.NotNull(stale);
        await using (var other = fixture.CreateDbContext())
        {
            await other.Channels.Where(c => c.ChannelName == "retentionstale")
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.IsBotActive, true));
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var locked = await db.LoadChannelForUpdateAsync("retentionstale", CancellationToken.None);

        Assert.NotNull(locked);
        Assert.Same(stale, locked);
        Assert.True(locked.IsBotActive);
    }

    [Fact]
    public async Task ChannelRowLock_RefusesARowChangedBeforeItWasLocked()
    {
        // Reloading would silently discard the change; the caller has its order wrong and is told so.
        await SeedChannelAsync("retentiondirty", twitchChannelId: null, isBotActive: false, deactivatedAtUtc: Cutoff());
        await using var db = fixture.CreateDbContext();
        var channel = await db.LoadChannelAsync("retentiondirty", CancellationToken.None);
        Assert.NotNull(channel);
        channel.IsBotActive = true;

        await using var transaction = await db.Database.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.LoadChannelForUpdateAsync("retentiondirty", CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeHoldingTheLock_MakesAJoinWait_AndTheJoinThenCreatesAFreshRow(bool joinResolvesTheTwitchId)
    {
        // The purge has taken the row lock and parks before its commit; a join of the same channel
        // arrives and must wait (by name, or by Twitch id when Helix answers). Once the purge commits,
        // the join finds no row and creates the channel afresh — no concurrency exception, no 500.
        var name = joinResolvesTheTwitchId ? "retentionpurgefirstid" : "retentionpurgefirst";
        var twitchId = joinResolvesTheTwitchId ? "retention-770001" : null;
        var cutoff = Cutoff();
        var seeded = await SeedChannelWithHistoryAsync(name, isBotActive: false, deactivatedAtUtc: cutoff.AddDays(-1), twitchChannelId: twitchId);

        await using var auditHold = await HoldTheAuditLogAsync();

        var purgeTag = $"{name}-purge";
        await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
        var purge = CreateService(purgeDb).PurgeIfInactiveSinceAsync(name, cutoff, AuditActor.System);
        await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);

        var joinTag = $"{name}-join";
        await using var joinDb = fixture.CreateTaggedDbContext(joinTag);
        var identity = twitchId is null ? Unavailable() : Found(twitchId, name);
        var join = CreateService(joinDb, identity).JoinAsync(name, Moderator);
        await fixture.WaitUntilBlockedOnLockAsync(joinTag, join);

        await auditHold.ReleaseAsync();

        Assert.Equal(ChannelRetentionPurgeResult.Purged, await purge);
        var joined = await join;
        Assert.Equal(ChannelJoinStatus.Joined, joined.Status);

        await using var verify = fixture.CreateDbContext();
        var fresh = await verify.Channels.AsNoTracking().SingleAsync(c => c.ChannelName == name);
        Assert.NotEqual(seeded.Channel.Id, fresh.Id);
        Assert.True(fresh.IsBotActive);
        Assert.Equal(twitchId, fresh.TwitchChannelId);
        // A new channel, honestly: no resumed-tracking marker, no retention stamp, no history.
        Assert.Null(fresh.TrackingResumedAt);
        Assert.Null(fresh.DeactivatedAtUtc);
        Assert.False(await verify.Emotes.AnyAsync(e => e.ChannelId == fresh.Id));
        Assert.False(await verify.Emotes.AnyAsync(e => e.ChannelId == seeded.Channel.Id));

        var actions = (await LoadAuditEntriesAsync(name)).Select(e => e.Action).ToList();
        Assert.Equal([AuditActions.ChannelPurge, AuditActions.ChannelJoin], actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoinHoldingTheLock_MakesThePurgeWait_AndThePurgeThenFindsTheChannelActive(bool joinResolvesTheTwitchId)
    {
        // The mirror order: the join has locked the inactive, due row and parks before its commit; the
        // purge arrives and must wait. Once the join commits, the purge rechecks under the lock, sees an
        // active row and leaves it — history intact, no purge entry.
        var name = joinResolvesTheTwitchId ? "retentionjoinfirstid" : "retentionjoinfirst";
        var twitchId = joinResolvesTheTwitchId ? "retention-770002" : null;
        var cutoff = Cutoff();
        var seeded = await SeedChannelWithHistoryAsync(name, isBotActive: false, deactivatedAtUtc: cutoff.AddDays(-1), twitchChannelId: twitchId);

        await using var auditHold = await HoldTheAuditLogAsync();

        var joinTag = $"{name}-join";
        await using var joinDb = fixture.CreateTaggedDbContext(joinTag);
        var identity = twitchId is null ? Unavailable() : Found(twitchId, name);
        var join = CreateService(joinDb, identity).JoinAsync(name, Moderator);
        await fixture.WaitUntilBlockedOnLockAsync(joinTag, join);

        var purgeTag = $"{name}-purge";
        await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
        var purge = CreateService(purgeDb).PurgeIfInactiveSinceAsync(name, cutoff, AuditActor.System);
        await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);

        await auditHold.ReleaseAsync();

        Assert.Equal(ChannelJoinStatus.Joined, (await join).Status);
        Assert.Equal(ChannelRetentionPurgeResult.StillActive, await purge);

        await using var verify = fixture.CreateDbContext();
        var channel = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == seeded.Channel.Id);
        Assert.True(channel.IsBotActive);
        Assert.Null(channel.DeactivatedAtUtc);
        Assert.True(await verify.Emotes.AnyAsync(e => e.ChannelId == seeded.Channel.Id));
        Assert.True(await verify.Votes.AnyAsync(v => v.VoteSessionId == seeded.SessionId));

        var actions = (await LoadAuditEntriesAsync(name)).Select(e => e.Action).ToList();
        Assert.Equal([AuditActions.ChannelJoin], actions);
    }

    [Fact]
    public async Task PurgeHoldingTheSurvivorsLock_MakesTheMergeWait_AndTheMergeThenSkipsWithoutFailing()
    {
        // Case 4 of the identity reconciliation: an inactive, due row holds the Twitch id, an active
        // id-less row holds its current login. The merge would reactivate the id row — the retention
        // purge is deleting it. Purge first: the merge waits on the survivor, finds it gone and skips
        // cleanly (the next pass backfills the id onto the remaining row) instead of failing its save.
        var cutoff = Cutoff();
        var survivor = await SeedChannelWithHistoryAsync(
            "retentionmergeold", isBotActive: false, deactivatedAtUtc: cutoff.AddDays(-1), twitchChannelId: "retention-770003");
        var loser = await SeedChannelAsync("retentionmergenew", twitchChannelId: null, isBotActive: true, deactivatedAtUtc: null);

        await using var auditHold = await HoldTheAuditLogAsync();

        const string purgeTag = "retentionmerge1-purge";
        await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
        var purge = CreateService(purgeDb).PurgeIfInactiveSinceAsync("retentionmergeold", cutoff, AuditActor.System);
        await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);

        const string mergeTag = "retentionmerge1-merge";
        await using var mergeDb = fixture.CreateTaggedDbContext(mergeTag);
        var logger = new RecordingLogger<ChannelIdentityService>();
        var reconcile = CreateIdentityService(mergeDb, new TwitchUserIdentity("retention-770003", "retentionmergenew"), logger)
            .ReconcileActiveChannelsAsync();
        await fixture.WaitUntilBlockedOnLockAsync(mergeTag, reconcile);

        await auditHold.ReleaseAsync();

        Assert.Equal(ChannelRetentionPurgeResult.Purged, await purge);
        var summary = await reconcile;
        Assert.NotNull(summary);
        Assert.Equal(0, summary.Merged);
        // Skipped, not failed: a failed save is logged as a warning naming the row. (Warnings about
        // other tests' leftover rows, which Helix does not know, are expected and filtered out.)
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("retentionmerge"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("retention-770003"));

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == survivor.Channel.Id));
        var remaining = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == loser.Id);
        Assert.Equal("retentionmergenew", remaining.ChannelName);
        Assert.True(remaining.IsBotActive);
    }

    [Fact]
    public async Task MergeHoldingTheSurvivorsLock_MakesThePurgeWait_AndThePurgeThenLeavesItAlone()
    {
        // The mirror order: the merge has locked the survivor and parks before its commit. The purge
        // waits; afterwards the row is active and answers to the new login, so the purge by its old
        // name finds nothing to delete — the history the merge just reactivated survives.
        var cutoff = Cutoff();
        var survivor = await SeedChannelWithHistoryAsync(
            "retentionmerge2old", isBotActive: false, deactivatedAtUtc: cutoff.AddDays(-1), twitchChannelId: "retention-770004");
        var loser = await SeedChannelAsync("retentionmerge2new", twitchChannelId: null, isBotActive: true, deactivatedAtUtc: null);

        await using var auditHold = await HoldTheAuditLogAsync();

        const string mergeTag = "retentionmerge2-merge";
        await using var mergeDb = fixture.CreateTaggedDbContext(mergeTag);
        var reconcile = CreateIdentityService(
                mergeDb, new TwitchUserIdentity("retention-770004", "retentionmerge2new"), new RecordingLogger<ChannelIdentityService>())
            .ReconcileActiveChannelsAsync();
        await fixture.WaitUntilBlockedOnLockAsync(mergeTag, reconcile);

        const string purgeTag = "retentionmerge2-purge";
        await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
        var purge = CreateService(purgeDb).PurgeIfInactiveSinceAsync("retentionmerge2old", cutoff, AuditActor.System);
        await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);

        await auditHold.ReleaseAsync();

        var summary = await reconcile;
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Merged);
        Assert.Equal(ChannelRetentionPurgeResult.NotFound, await purge);

        await using var verify = fixture.CreateDbContext();
        var merged = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == survivor.Channel.Id);
        Assert.Equal("retentionmerge2new", merged.ChannelName);
        Assert.True(merged.IsBotActive);
        Assert.Null(merged.DeactivatedAtUtc);
        Assert.True(await verify.Emotes.AnyAsync(e => e.ChannelId == survivor.Channel.Id));
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == loser.Id));
        Assert.DoesNotContain(await LoadAuditEntriesAsync("retentionmerge2old"), e => e.Action == AuditActions.ChannelPurge);
    }

    // 180 days back, as the job would compute it once per run. The tests only need "some fixed cutoff";
    // the period itself is RetentionPolicy's business (T6). Whole seconds, because Postgres keeps
    // microseconds and .NET ticks: an unrounded cutoff stored as a stamp would come back a fraction
    // *before* itself, and the exactly-at-the-cutoff case would test the rounding instead of the rule.
    private static DateTime Cutoff()
    {
        var cutoff = DateTime.UtcNow.AddDays(-180);
        return cutoff.AddTicks(-(cutoff.Ticks % TimeSpan.TicksPerSecond));
    }

    private static ChannelService CreateService(AppDbContext db, IChannelIdentityService? identity = null) =>
        new(
            db,
            Substitute.For<IRedisPublisher>(),
            identity ?? Unavailable(),
            // Uncapped: the shared collection database accumulates active channels across tests.
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            Substitute.For<IExcludedChannelFilter>(),
            NullLogger<ChannelService>.Instance);

    private static IChannelIdentityService Unavailable() =>
        Lookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable));

    private static IChannelIdentityService Found(string twitchChannelId, string login) =>
        Lookup(TwitchUserLookup.Found(new TwitchUserIdentity(twitchChannelId, login)));

    private static IChannelIdentityService Lookup(TwitchUserLookup lookup)
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(lookup);
        return identityService;
    }

    // Helix answers with this one identity for every call; every other active row in the shared
    // database is unknown to it and therefore inert (the "Twitch does not know this" branches write
    // nothing — see ChannelIdentityServiceTests).
    private static ChannelIdentityService CreateIdentityService(
        AppDbContext db, TwitchUserIdentity identity, ILogger<ChannelIdentityService> logger)
    {
        var helix = Substitute.For<ITwitchHelixClient>();
        helix.GetUsersAsync(
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns([identity]);
        var appTokenProvider = Substitute.For<ITwitchAppTokenProvider>();
        appTokenProvider.GetTokenAsync(Arg.Any<CancellationToken>()).Returns("retention-app-token");

        return new ChannelIdentityService(
            db, helix, appTokenProvider, Substitute.For<IRedisPublisher>(), new ChannelIdentityWarningState(),
            Substitute.For<IExcludedChannelFilter>(), logger);
    }

    private async Task AssertNotPurgedAsync(string channelName, DateTime cutoff, string channelId, ChannelRetentionPurgeResult expected)
    {
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db).PurgeIfInactiveSinceAsync(channelName, cutoff, AuditActor.System);

        Assert.Equal(expected, result);
        await using var verify = fixture.CreateDbContext();
        Assert.True(await verify.Channels.AnyAsync(c => c.Id == channelId));
        Assert.True(await verify.Emotes.AnyAsync(e => e.ChannelId == channelId));
        Assert.DoesNotContain(await LoadAuditEntriesAsync(channelName), e => e.Action == AuditActions.ChannelPurge);
    }

    /// <summary>
    /// Takes the audit log in SHARE mode, which blocks every insert into it until released — the point
    /// where the join, the purge and the merge each park right before their commit, with the channel
    /// lock already in hand. Released by rolling back.
    /// </summary>
    private async Task<AuditLogHold> HoldTheAuditLogAsync()
    {
        var db = fixture.CreateDbContext();
        await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""LOCK TABLE "AuditLogEntries" IN SHARE MODE""");
        return new AuditLogHold(db);
    }

    private async Task<Channel> SeedChannelAsync(string name, string? twitchChannelId, bool isBotActive, DateTime? deactivatedAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel
        {
            ChannelName = name,
            TwitchChannelId = twitchChannelId,
            IsBotActive = isBotActive,
            DeactivatedAtUtc = deactivatedAtUtc
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    /// <summary>
    /// A channel with one row on every cascade edge the purge takes down: an emote with a usage row,
    /// a live day, and an ended vote session with a ballot row and a vote.
    /// </summary>
    private async Task<SeededChannel> SeedChannelWithHistoryAsync(
        string name, bool isBotActive, DateTime? deactivatedAtUtc, string? twitchChannelId = null)
    {
        var channel = await SeedChannelAsync(name, twitchChannelId, isBotActive, deactivatedAtUtc);

        await using var db = fixture.CreateDbContext();
        var voter = new User
        {
            Id = $"{name}-voter",
            TwitchUsername = $"{name}voter",
            DisplayName = $"{name}voter",
            LastLogin = DateTime.UtcNow
        };
        db.Users.Add(voter);
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "RetentionEmote",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 1, 15), UseCount = 3 });
        db.ChannelLiveDays.Add(new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 1, 15), LiveMinutes = 60 });
        var session = new VoteSession { ChannelId = channel.Id, Title = "Retention", IsActive = false, EndedAt = DateTime.UtcNow.AddDays(-200) };
        db.VoteSessions.Add(session);
        await db.SaveChangesAsync();

        db.VoteSessionEmotes.Add(new VoteSessionEmote { VoteSessionId = session.Id, EmoteId = emote.Id });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = voter.Id, Type = VoteType.Keep });
        await db.SaveChangesAsync();

        return new SeededChannel(channel, emote.Id, session.Id, voter.Id);
    }

    private async Task<IReadOnlyList<AuditLogEntry>> LoadAuditEntriesAsync(string channelName)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.ChannelName == channelName)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    private sealed record SeededChannel(Channel Channel, string EmoteId, long SessionId, string VoterId);

    private sealed class AuditLogHold(AppDbContext db) : IAsyncDisposable
    {
        public async Task ReleaseAsync() => await db.Database.RollbackTransactionAsync();

        public async ValueTask DisposeAsync()
        {
            // Idempotent: a test that failed before ReleaseAsync must not leave the audit log locked for
            // every test after it.
            if (db.Database.CurrentTransaction is not null)
            {
                await db.Database.RollbackTransactionAsync();
            }

            await db.DisposeAsync();
        }
    }
}
