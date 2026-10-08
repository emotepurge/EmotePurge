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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The broadcaster's own purge (#245, IChannelService.PurgeByBroadcasterAsync) and its data summary.
// Against real Postgres: what is asserted is the FK cascade into every channel-bound table, the row
// locks the purge shares with the join, and a real deadlock against a sync-shaped transaction.
//
// Races are made deterministic as in ChannelRetentionPurgeTests (PostgresLockProbe): each contender
// runs on its own tagged context, and the test waits until pg_stat_activity reports it waiting on a
// lock. Holding the audit log in SHARE mode parks the purge right before its commit, row lock in hand.
[Collection("Postgres")]
public class ChannelBroadcasterPurgeTests(PostgresFixture fixture)
{
    // 26 characters, like real 7TV set ULIDs.
    private const string SetA = "01HZBPSETAAAAAAAAAAAAAAAAA";
    private const string SetB = "01HZBPSETBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task PurgeByBroadcaster_OwnRow_DeletesEveryChannelBoundTable_AuditsLocksAndLeavesAfterTheCommit()
    {
        var actor = Broadcaster("bp-100001", "bpown");
        var seeded = await SeedChannelWithFullHistoryAsync("bpown", actor.TwitchUserId);
        var bystander = await SeedChannelWithFullHistoryAsync("bpbystander", "bp-100002");
        var identity = Substitute.For<IChannelIdentityService>();
        var publisher = Substitute.For<IRedisPublisher>();
        var rowVisibleAtPublish = new List<bool>();
        publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                // Looked up from a second context: the LEAVE goes out only after the commit, so the
                // row must already be gone for everybody else.
                await using var probe = fixture.CreateDbContext();
                rowVisibleAtPublish.Add(await probe.Channels.AnyAsync(c => c.Id == seeded.ChannelId));
            });

        await using var db = fixture.CreateDbContext();
        // Mixed case on purpose (Regel 9): the lookup normalizes.
        var result = await CreateService(db, identity, publisher).PurgeByBroadcasterAsync("BpOwn", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.Purged, result);
        // A stored id that matches is proof enough; Twitch is not asked.
        await identity.DidNotReceiveWithAnyArgs().LookupByLoginAsync(default!, default);
        await AssertChannelDataGoneAsync(seeded);
        await AssertChannelDataPresentAsync(bystander);

        var entry = Assert.Single(await LoadAuditEntriesAsync("bpown"));
        Assert.Equal(AuditActions.ChannelPurge, entry.Action);
        Assert.Equal(actor.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(actor.Login, entry.ActorLogin);
        AssertBroadcasterReason(entry);

        Assert.NotNull(await LoadLockedAtAsync(actor.TwitchUserId));
        await publisher.Received(1).PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}bpown", Arg.Any<CancellationToken>());
        Assert.Equal([false], rowVisibleAtPublish);
    }

    [Fact]
    public async Task PurgeByBroadcaster_RowWithAForeignId_IsNotTheCallers_AndNothingIsWritten()
    {
        var seeded = await SeedChannelWithFullHistoryAsync("bpforeign", "bp-200001");
        var identity = Substitute.For<IChannelIdentityService>();
        var publisher = Substitute.For<IRedisPublisher>();
        var actor = Broadcaster("bp-200002", "bpforeign");

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, identity, publisher).PurgeByBroadcasterAsync("bpforeign", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.NotBroadcaster, result);
        await identity.DidNotReceiveWithAnyArgs().LookupByLoginAsync(default!, default);
        await AssertNothingWrittenAsync(seeded, "bpforeign", actor.TwitchUserId, publisher);
    }

    [Fact]
    public async Task PurgeByBroadcaster_IdLessRow_TwitchConfirmsTheCaller_PurgesAndLocksTheCallersId()
    {
        var actor = Broadcaster("bp-300001", "bpidless");
        var seeded = await SeedChannelWithFullHistoryAsync("bpidless", twitchChannelId: null);
        var publisher = Substitute.For<IRedisPublisher>();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, Found(actor.TwitchUserId, "bpidless"), publisher)
            .PurgeByBroadcasterAsync("bpidless", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.Purged, result);
        await AssertChannelDataGoneAsync(seeded);
        AssertBroadcasterReason(Assert.Single(await LoadAuditEntriesAsync("bpidless")));
        Assert.NotNull(await LoadLockedAtAsync(actor.TwitchUserId));
        await publisher.Received(1).PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}bpidless", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeByBroadcaster_IdLessRow_TwitchNamesSomeoneElse_IsNotTheCallers()
    {
        var actor = Broadcaster("bp-400001", "bpidlessforeign");
        var seeded = await SeedChannelWithFullHistoryAsync("bpidlessforeign", twitchChannelId: null);
        var publisher = Substitute.For<IRedisPublisher>();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, Found("bp-400002", "bpidlessforeign"), publisher)
            .PurgeByBroadcasterAsync("bpidlessforeign", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.NotBroadcaster, result);
        await AssertNothingWrittenAsync(seeded, "bpidlessforeign", actor.TwitchUserId, publisher);
    }

    [Theory]
    [InlineData(TwitchUserLookupStatus.Unavailable)]
    [InlineData(TwitchUserLookupStatus.NotFound)]
    public async Task PurgeByBroadcaster_IdLessRow_TwitchCannotConfirm_IsUnresolved(TwitchUserLookupStatus status)
    {
        var name = $"bpunresolved{status.ToString().ToLowerInvariant()}";
        var actor = Broadcaster($"bp-500{(int)status}", name);
        var seeded = await SeedChannelWithFullHistoryAsync(name, twitchChannelId: null);
        var publisher = Substitute.For<IRedisPublisher>();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, Lookup(TwitchUserLookup.Failed(status)), publisher)
            .PurgeByBroadcasterAsync(name, actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.IdentityUnresolved, result);
        await AssertNothingWrittenAsync(seeded, name, actor.TwitchUserId, publisher);
    }

    [Fact]
    public async Task PurgeByBroadcaster_IdLessDuplicateUnderTheRoutedLogin_TakesTheIdRowWithIt()
    {
        // The rename leftover: the main row holds the id under its old login, an id-less duplicate
        // (created by a join during a Helix outage) holds the current one and carries a tag — since
        // #201 the reconcile refuses to merge a row with tags, so such duplicates live longer. Twitch
        // confirms the routed login is the caller's: both rows are the caller's channel.
        var actor = Broadcaster("bp-600001", "bpdupnew");
        var main = await SeedChannelWithFullHistoryAsync("bpdupold", actor.TwitchUserId);
        var duplicate = await SeedChannelWithFullHistoryAsync("bpdupnew", twitchChannelId: null);
        var publisher = Substitute.For<IRedisPublisher>();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, Found(actor.TwitchUserId, "bpdupnew"), publisher)
            .PurgeByBroadcasterAsync("bpdupnew", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.Purged, result);
        await AssertChannelDataGoneAsync(main);
        await AssertChannelDataGoneAsync(duplicate);
        AssertBroadcasterReason(Assert.Single(await LoadAuditEntriesAsync("bpdupold")));
        AssertBroadcasterReason(Assert.Single(await LoadAuditEntriesAsync("bpdupnew")));
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.Equal(1, await verify.BroadcasterChannelLocks.CountAsync(l => l.TwitchChannelId == actor.TwitchUserId));
        }

        await publisher.Received(1).PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}bpdupold", Arg.Any<CancellationToken>());
        await publisher.Received(1).PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}bpdupnew", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeByBroadcaster_RoutedToTheIdRow_LeavesAnIdLessDuplicateUnderAnotherLogin()
    {
        // The proof boundary: a stored id proves the row it sits on, not some other login. Nobody asked
        // Twitch about the duplicate's login, so it stays.
        var actor = Broadcaster("bp-700001", "bpboundmain");
        var main = await SeedChannelWithFullHistoryAsync("bpboundmain", actor.TwitchUserId);
        var duplicate = await SeedChannelWithFullHistoryAsync("bpboundold", twitchChannelId: null);
        var identity = Substitute.For<IChannelIdentityService>();
        var publisher = Substitute.For<IRedisPublisher>();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, identity, publisher).PurgeByBroadcasterAsync("bpboundmain", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.Purged, result);
        await AssertChannelDataGoneAsync(main);
        await AssertChannelDataPresentAsync(duplicate);
        Assert.Empty(await LoadAuditEntriesAsync("bpboundold"));
        await identity.DidNotReceiveWithAnyArgs().LookupByLoginAsync(default!, default);
        await publisher.DidNotReceive().PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}bpboundold", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeByBroadcaster_RoutedToAnIdLessDuplicateTwitchGivesToSomeoneElse_IsNotTheCallers()
    {
        var actor = Broadcaster("bp-710001", "bpbound2main");
        var main = await SeedChannelWithFullHistoryAsync("bpbound2main", actor.TwitchUserId);
        var duplicate = await SeedChannelWithFullHistoryAsync("bpbound2old", twitchChannelId: null);
        var publisher = Substitute.For<IRedisPublisher>();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, Found("bp-710002", "bpbound2old"), publisher)
            .PurgeByBroadcasterAsync("bpbound2old", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.NotBroadcaster, result);
        await AssertNothingWrittenAsync(duplicate, "bpbound2old", actor.TwitchUserId, publisher);
        await AssertChannelDataPresentAsync(main);
        Assert.Empty(await LoadAuditEntriesAsync("bpbound2main"));
    }

    [Fact]
    public async Task PurgeByBroadcaster_UnknownName_IsNotFound_WithoutAskingTwitch()
    {
        var identity = Substitute.For<IChannelIdentityService>();
        var publisher = Substitute.For<IRedisPublisher>();
        var actor = Broadcaster("bp-800001", "bpunknown");

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, identity, publisher).PurgeByBroadcasterAsync("bpunknown", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.NotFound, result);
        await identity.DidNotReceiveWithAnyArgs().LookupByLoginAsync(default!, default);
        Assert.Empty(await LoadAuditEntriesAsync("bpunknown"));
        Assert.Null(await LoadLockedAtAsync(actor.TwitchUserId));
        Assert.Empty(publisher.ReceivedCalls());
    }

    // The purge has locked the row and parks before its commit; a join of the same channel must wait
    // (by Twitch id when Helix answers, by name when it does not) and decide only once the purge has
    // committed — on the lock the purge wrote in the same transaction (#245, plan T4 test 11).
    //  - a moderator with a resolved identity is refused: no row is created, the lock stays;
    //  - the broadcaster with a resolved identity lifts the lock and creates the channel afresh;
    //  - a moderator during a Helix outage has no id to check, so the fresh row is created id-less and
    //    the lock stays — the accepted gap the 7TV sync's gate and the identity reconcile close.
    [Theory]
    [InlineData("moderator")]
    [InlineData("broadcaster")]
    [InlineData("moderatorDuringOutage")]
    public async Task PurgeHoldingTheLock_MakesAConcurrentJoinWait_AndTheJoinThenSeesTheLock(string joiner)
    {
        var name = $"bprace{joiner.Length}";
        var actor = Broadcaster($"bp-9001{joiner.Length:D2}", name);
        var seeded = await SeedChannelWithFullHistoryAsync(name, actor.TwitchUserId);

        await using var auditHold = await HoldTheAuditLogAsync();

        var purgeTag = $"{name}-purge";
        await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
        var purge = CreateService(purgeDb, Substitute.For<IChannelIdentityService>()).PurgeByBroadcasterAsync(name, actor);
        await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);

        var joinTag = $"{name}-join";
        await using var joinDb = fixture.CreateTaggedDbContext(joinTag);
        var joinIdentity = joiner == "moderatorDuringOutage"
            ? Lookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable))
            : Found(actor.TwitchUserId, name);
        var joinActor = joiner == "broadcaster" ? actor : new AuditActor("bp-mod", "bpmod");
        var join = CreateService(joinDb, joinIdentity).JoinAsync(name, joinActor);
        await fixture.WaitUntilBlockedOnLockAsync(joinTag, join);

        await auditHold.ReleaseAsync();

        Assert.Equal(ChannelBroadcasterPurgeResult.Purged, await purge);
        var joinResult = await join;

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Emotes.AnyAsync(e => e.ChannelId == seeded.ChannelId));
        var fresh = await verify.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.ChannelName == name);
        var actions = (await LoadAuditEntriesAsync(name)).Select(e => e.Action).ToList();
        var lockedAt = await LoadLockedAtAsync(actor.TwitchUserId);
        try
        {
            switch (joiner)
            {
                case "moderator":
                    Assert.Equal(ChannelJoinStatus.LockedByBroadcaster, joinResult.Status);
                    Assert.Equal(lockedAt, joinResult.LockedAtUtc);
                    Assert.Null(fresh);
                    Assert.Equal([AuditActions.ChannelPurge], actions);
                    break;
                case "broadcaster":
                    Assert.Equal(ChannelJoinStatus.Joined, joinResult.Status);
                    Assert.NotNull(fresh);
                    Assert.NotEqual(seeded.ChannelId, fresh.Id);
                    Assert.Equal(actor.TwitchUserId, fresh.TwitchChannelId);
                    Assert.Null(lockedAt);
                    Assert.Equal([AuditActions.ChannelPurge, AuditActions.ChannelJoin], actions);
                    break;
                default:
                    Assert.Equal(ChannelJoinStatus.Joined, joinResult.Status);
                    Assert.NotNull(fresh);
                    Assert.Null(fresh.TwitchChannelId);
                    Assert.NotNull(lockedAt);
                    Assert.Equal([AuditActions.ChannelPurge, AuditActions.ChannelJoin], actions);
                    break;
            }
        }
        finally
        {
            // Neither a fresh row nor the lock may outlive this case in the shared database.
            await verify.Channels.Where(c => c.ChannelName == name).ExecuteUpdateAsync(c => c.SetProperty(x => x.IsBotActive, false));
            await verify.BroadcasterChannelLocks.Where(l => l.TwitchChannelId == actor.TwitchUserId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task PurgeByBroadcaster_ASecondCall_IsNotFound()
    {
        var actor = Broadcaster("bp-110001", "bptwice");
        await SeedChannelWithFullHistoryAsync("bptwice", actor.TwitchUserId);

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(ChannelBroadcasterPurgeResult.Purged, await CreateService(db).PurgeByBroadcasterAsync("bptwice", actor));
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(ChannelBroadcasterPurgeResult.NotFound, await CreateService(db).PurgeByBroadcasterAsync("bptwice", actor));
        }

        Assert.Single(await LoadAuditEntriesAsync("bptwice"));
    }

    [Fact]
    public async Task PurgeByBroadcaster_ALeavePublishThatFails_IsLoggedWithoutNamingTheChannel_AndThePurgeStands()
    {
        var actor = Broadcaster("bp-120001", "bppublishfails");
        var seeded = await SeedChannelWithFullHistoryAsync("bppublishfails", actor.TwitchUserId);
        var publisher = Substitute.For<IRedisPublisher>();
        publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis is down"));
        var logger = new RecordingLogger<ChannelService>();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db, Substitute.For<IChannelIdentityService>(), publisher, logger)
            .PurgeByBroadcasterAsync("bppublishfails", actor);

        Assert.Equal(ChannelBroadcasterPurgeResult.Purged, result);
        await AssertChannelDataGoneAsync(seeded);
        Assert.NotNull(await LoadLockedAtAsync(actor.TwitchUserId));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain("bppublishfails", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(actor.TwitchUserId, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDataSummary_CountsEmotesVoteSessionsLiveDaysAndTags_OfThisChannelOnly()
    {
        await SeedChannelWithFullHistoryAsync("bpsummary", "bp-130001");
        await SeedChannelWithFullHistoryAsync("bpsummaryother", "bp-130002");
        await using (var db = fixture.CreateDbContext())
        {
            var channelId = await db.Channels.Where(c => c.ChannelName == "bpsummary").Select(c => c.Id).SingleAsync();
            db.Emotes.Add(NewEmote(channelId, "SecondEmote"));
            db.ChannelLiveDays.Add(new ChannelLiveDay { ChannelId = channelId, Date = new DateOnly(2026, 1, 16), LiveMinutes = 30 });
            db.EmoteTags.Add(new EmoteTag { ChannelId = channelId, Name = "Second", NormalizedName = "second", CreatedAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateDbContext();
        var summary = await CreateService(read).GetDataSummaryAsync("BpSummary");

        Assert.Equal(new ChannelDataSummary(EmoteCount: 2, VoteSessionCount: 1, LiveDayCount: 2, TagCount: 2), summary);
    }

    [Fact]
    public async Task GetDataSummary_WithoutARow_IsNull()
    {
        await using var db = fixture.CreateDbContext();

        Assert.Null(await CreateService(db).GetDataSummaryAsync("bpsummarymissing"));
    }

    [Fact]
    public async Task PurgeByBroadcaster_DeadlockedByASyncHoldingTheLeaveObservation_RetriesAndSucceeds()
    {
        // R5: the full 7TV sync upserts an existing EmoteSetLeaveObservation row (row lock) and then
        // updates Channels; the purge locks Channels and then cascades into that observation row — the
        // reverse order, so Postgres reports 40P01. Forced deterministically here:
        //   1. the "sync" upserts the observation row with the sync's own statement and raises its
        //      deadlock_timeout to 60 s, so it can never be the one that detects the cycle;
        //   2. the audit log is held, so the purge parks with the channel row lock in hand;
        //   3. the "sync" updates the channel row and waits on the purge;
        //   4. the audit hold is released: the purge's cascade waits on the observation row and closes
        //      the cycle. Its own deadlock check (default 1 s) is the only one due — the purge is the victim.
        // Its first attempt rolls back, which lets the sync's update through; the sync commits, and the
        // retry (fresh change tracker, new transaction) deletes the channel.
        var actor = Broadcaster("bp-140001", "bpdeadlock");
        var seeded = await SeedChannelWithFullHistoryAsync("bpdeadlock", actor.TwitchUserId);
        var publisher = Substitute.For<IRedisPublisher>();
        var logger = new RecordingLogger<ChannelService>();

        const string syncTag = "bpdeadlock-sync";
        await using var syncDb = fixture.CreateTaggedDbContext(syncTag);
        await using var syncTransaction = await syncDb.Database.BeginTransactionAsync();
        await syncDb.Database.ExecuteSqlRawAsync("SET LOCAL deadlock_timeout = '60s'");
        await EmoteSetLeaveObservations.RecordAsync(
            syncDb, seeded.ChannelId, SetA, [seeded.LeaveObservationEmoteId], DateTime.UtcNow, CancellationToken.None);

        await using var auditHold = await HoldTheAuditLogAsync();

        const string purgeTag = "bpdeadlock-purge";
        await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
        var purge = CreateService(purgeDb, Substitute.For<IChannelIdentityService>(), publisher, logger)
            .PurgeByBroadcasterAsync("bpdeadlock", actor);
        await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);

        var syncChannelUpdate = syncDb.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Channels" SET "LastSyncedAtUtc" = {DateTime.UtcNow} WHERE "Id" = {seeded.ChannelId}""");
        await fixture.WaitUntilBlockedOnLockAsync(syncTag, syncChannelUpdate);

        await auditHold.ReleaseAsync();

        // Only the purge's rollback releases the channel row: the sync's update finishing proves the
        // first attempt was the deadlock victim.
        Assert.Equal(1, await syncChannelUpdate);
        await syncTransaction.CommitAsync();

        Assert.Equal(ChannelBroadcasterPurgeResult.Purged, await purge);
        var retry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("deadlock", retry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bpdeadlock", retry.Message, StringComparison.Ordinal);
        await AssertChannelDataGoneAsync(seeded);
        Assert.NotNull(await LoadLockedAtAsync(actor.TwitchUserId));
        AssertBroadcasterReason(Assert.Single(await LoadAuditEntriesAsync("bpdeadlock")));
        await publisher.Received(1).PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}bpdeadlock", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeByBroadcaster_APersistentDeadlock_GivesUpAfterThreeAttempts_AndWritesNothing()
    {
        var actor = Broadcaster("bp-150001", "bpdeadlockforever");
        var seeded = await SeedChannelWithFullHistoryAsync("bpdeadlockforever", actor.TwitchUserId);
        var publisher = Substitute.For<IRedisPublisher>();
        var interceptor = new FailEverySave(PostgresErrorCodes.DeadlockDetected);

        await using var db = fixture.CreateDbContext([interceptor]);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreateService(db, Substitute.For<IChannelIdentityService>(), publisher).PurgeByBroadcasterAsync("bpdeadlockforever", actor));

        Assert.Equal(PostgresErrorCodes.DeadlockDetected, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        Assert.Equal(3, interceptor.Attempts);
        // Every attempt starts from an empty change tracker: the third saw one audit entry, one lock
        // and one deletion — not three of each piled up from the attempts before it.
        Assert.All(interceptor.PendingChangesPerAttempt, pending => Assert.Equal(3, pending));
        await AssertNothingWrittenAsync(seeded, "bpdeadlockforever", actor.TwitchUserId, publisher);
    }

    [Fact]
    public async Task PurgeByBroadcaster_AnyOtherDatabaseError_IsNotRetried()
    {
        var actor = Broadcaster("bp-160001", "bpothererror");
        var seeded = await SeedChannelWithFullHistoryAsync("bpothererror", actor.TwitchUserId);
        var publisher = Substitute.For<IRedisPublisher>();
        var interceptor = new FailEverySave(PostgresErrorCodes.SerializationFailure);

        await using var db = fixture.CreateDbContext([interceptor]);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => CreateService(db, Substitute.For<IChannelIdentityService>(), publisher).PurgeByBroadcasterAsync("bpothererror", actor));

        Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        Assert.Equal(1, interceptor.Attempts);
        await AssertNothingWrittenAsync(seeded, "bpothererror", actor.TwitchUserId, publisher);
    }

    private static AuditActor Broadcaster(string twitchUserId, string login) => new(twitchUserId, login);

    private static ChannelService CreateService(
        AppDbContext db,
        IChannelIdentityService? identity = null,
        IRedisPublisher? publisher = null,
        ILogger<ChannelService>? logger = null) =>
        new(
            db,
            publisher ?? Substitute.For<IRedisPublisher>(),
            identity ?? Lookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable)),
            new ChannelEmoteSetObservationService(db),
            new BroadcasterChannelLockService(db),
            // Uncapped: the shared collection database accumulates active channels across tests.
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            Substitute.For<IExcludedChannelFilter>(),
            logger ?? new RecordingLogger<ChannelService>());

    private static IChannelIdentityService Found(string twitchChannelId, string login) =>
        Lookup(TwitchUserLookup.Found(new TwitchUserIdentity(twitchChannelId, login)));

    private static IChannelIdentityService Lookup(TwitchUserLookup lookup)
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(lookup);
        return identityService;
    }

    private static Emote NewEmote(string channelId, string name) => new()
    {
        ChannelId = channelId,
        Name = name,
        SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
        ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
    };

    private static EmoteTagEntry NewEntry(long tagId) => new()
    {
        TagId = tagId,
        SevenTvEmoteId = Guid.NewGuid().ToString("N")[..26],
        Alias = "KEKW",
        ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp",
        AddedAtUtc = DateTime.UtcNow
    };

    private static void AssertBroadcasterReason(AuditLogEntry entry)
    {
        Assert.Equal(AuditActions.ChannelPurge, entry.Action);
        Assert.NotNull(entry.DetailsJson);
        using var details = JsonDocument.Parse(entry.DetailsJson);
        Assert.Equal("broadcasterRequest", details.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>
    /// A channel with one row in each of the thirteen tables that hang off it by cascade (R3.2 a):
    /// Emotes, UsageStats, ChannelLiveDays, VoteSessions, VoteSessionEmotes, Votes, an open and a
    /// closed ChannelEmoteSetObservation, an EmoteSetLeaveObservation, and a tag with an entry, a
    /// placement, an activation and an operation.
    /// </summary>
    private async Task<SeededChannel> SeedChannelWithFullHistoryAsync(string name, string? twitchChannelId)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, TwitchChannelId = twitchChannelId, IsBotActive = true, ActiveEmoteSetId = SetA };
        db.Channels.Add(channel);
        var voter = new User
        {
            Id = $"{name}-voter",
            TwitchUsername = $"{name}voter",
            DisplayName = $"{name}voter",
            LastLogin = DateTime.UtcNow
        };
        db.Users.Add(voter);
        await db.SaveChangesAsync();

        var emote = NewEmote(channel.Id, "PurgeEmote");
        db.Emotes.Add(emote);
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = new DateOnly(2026, 1, 15), UseCount = 3 });
        db.ChannelLiveDays.Add(new ChannelLiveDay { ChannelId = channel.Id, Date = new DateOnly(2026, 1, 15), LiveMinutes = 60 });
        var session = new VoteSession { ChannelId = channel.Id, Title = "Purge", IsActive = false, EndedAt = DateTime.UtcNow.AddDays(-1) };
        db.VoteSessions.Add(session);
        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = channel.Id,
            SevenTvEmoteSetId = SetB,
            ObservedFromUtc = DateTime.UtcNow.AddDays(-10),
            ObservedToUtc = DateTime.UtcNow.AddDays(-5),
            ClosedBy = ChannelEmoteSetObservationClosedBy.SetSwitch
        });
        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = channel.Id,
            SevenTvEmoteSetId = SetA,
            ObservedFromUtc = DateTime.UtcNow.AddDays(-5)
        });
        var tag = new EmoteTag { ChannelId = channel.Id, Name = "Funny", NormalizedName = "funny", CreatedAtUtc = DateTime.UtcNow };
        db.EmoteTags.Add(tag);
        await db.SaveChangesAsync();

        db.VoteSessionEmotes.Add(new VoteSessionEmote { VoteSessionId = session.Id, EmoteId = emote.Id });
        db.Votes.Add(new Vote { VoteSessionId = session.Id, EmoteId = emote.Id, UserId = voter.Id, Type = VoteType.Keep });
        var entry = NewEntry(tag.Id);
        db.EmoteTagEntries.Add(entry);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        db.EmoteTagOperations.Add(new EmoteTagOperation
        {
            OperationId = operationId,
            TagId = tag.Id,
            Kind = EmoteTagOperationKind.PlayIn,
            SevenTvEmoteSetId = SetA,
            RegisteredAtUtc = DateTime.UtcNow
        });
        db.EmoteTagActivations.Add(new EmoteTagActivation
        {
            TagId = tag.Id,
            SevenTvEmoteSetId = SetA,
            ActivatedAtUtc = DateTime.UtcNow,
            OperationId = operationId
        });
        db.EmoteTagPlacements.Add(new EmoteTagPlacement
        {
            TagId = tag.Id,
            SevenTvEmoteId = entry.SevenTvEmoteId,
            SevenTvEmoteSetId = SetA,
            PlacedAtUtc = DateTime.UtcNow,
            OperationId = operationId,
            RegisteredAtUtc = DateTime.UtcNow
        });
        db.EmoteSetLeaveObservations.Add(new EmoteSetLeaveObservation
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = entry.SevenTvEmoteId,
            SevenTvEmoteSetId = SetA,
            LastObservedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        return new SeededChannel(channel.Id, emote.Id, session.Id, tag.Id, entry.SevenTvEmoteId);
    }

    private async Task AssertChannelDataGoneAsync(SeededChannel seeded) =>
        Assert.Equal(0, (await CountChannelDataAsync(seeded)).Sum());

    private async Task AssertChannelDataPresentAsync(SeededChannel seeded) =>
        Assert.All(await CountChannelDataAsync(seeded), count => Assert.NotEqual(0, count));

    /// <summary>The channel row and its thirteen dependent tables, one count each.</summary>
    private async Task<int[]> CountChannelDataAsync(SeededChannel seeded)
    {
        await using var verify = fixture.CreateDbContext();
        return
        [
            await verify.Channels.CountAsync(c => c.Id == seeded.ChannelId),
            await verify.Emotes.CountAsync(e => e.ChannelId == seeded.ChannelId),
            await verify.UsageStats.CountAsync(u => u.EmoteId == seeded.EmoteId),
            await verify.ChannelLiveDays.CountAsync(d => d.ChannelId == seeded.ChannelId),
            await verify.VoteSessions.CountAsync(s => s.ChannelId == seeded.ChannelId),
            await verify.VoteSessionEmotes.CountAsync(b => b.VoteSessionId == seeded.SessionId),
            await verify.Votes.CountAsync(v => v.VoteSessionId == seeded.SessionId),
            await verify.ChannelEmoteSetObservations.CountAsync(o => o.ChannelId == seeded.ChannelId && o.ObservedToUtc == null),
            await verify.ChannelEmoteSetObservations.CountAsync(o => o.ChannelId == seeded.ChannelId && o.ObservedToUtc != null),
            await verify.EmoteSetLeaveObservations.CountAsync(o => o.ChannelId == seeded.ChannelId),
            await verify.EmoteTags.CountAsync(t => t.ChannelId == seeded.ChannelId),
            await verify.EmoteTagEntries.CountAsync(e => e.TagId == seeded.TagId),
            await verify.EmoteTagPlacements.CountAsync(p => p.TagId == seeded.TagId),
            await verify.EmoteTagActivations.CountAsync(a => a.TagId == seeded.TagId),
            await verify.EmoteTagOperations.CountAsync(o => o.TagId == seeded.TagId),
        ];
    }

    private async Task AssertNothingWrittenAsync(SeededChannel seeded, string channelName, string actorTwitchUserId, IRedisPublisher publisher)
    {
        await AssertChannelDataPresentAsync(seeded);
        Assert.Empty(await LoadAuditEntriesAsync(channelName));
        Assert.Null(await LoadLockedAtAsync(actorTwitchUserId));
        Assert.Empty(publisher.ReceivedCalls());
    }

    private async Task<DateTime?> LoadLockedAtAsync(string twitchChannelId)
    {
        await using var db = fixture.CreateDbContext();
        return await new BroadcasterChannelLockService(db).GetLockedAtUtcAsync(twitchChannelId);
    }

    private async Task<IReadOnlyList<AuditLogEntry>> LoadAuditEntriesAsync(string channelName)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking()
            .Where(e => e.ChannelName == channelName)
            .OrderBy(e => e.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Takes the audit log in SHARE mode, which blocks every insert into it until released — where the
    /// purge parks right before its commit, channel lock in hand. Released by rolling back.
    /// </summary>
    private async Task<AuditLogHold> HoldTheAuditLogAsync()
    {
        var db = fixture.CreateDbContext();
        await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("""LOCK TABLE "AuditLogEntries" IN SHARE MODE""");
        return new AuditLogHold(db);
    }

    private sealed record SeededChannel(string ChannelId, string EmoteId, long SessionId, long TagId, string LeaveObservationEmoteId);

    private sealed class AuditLogHold(AppDbContext db) : IAsyncDisposable
    {
        public async Task ReleaseAsync() => await db.Database.RollbackTransactionAsync();

        public async ValueTask DisposeAsync()
        {
            // Idempotent: a test that failed before ReleaseAsync must not leave the audit log locked.
            if (db.Database.CurrentTransaction is not null)
            {
                await db.Database.RollbackTransactionAsync();
            }

            await db.DisposeAsync();
        }
    }

    /// <summary>
    /// Fails every save with a Postgres error of the given SQLSTATE, in the shape EF surfaces a failed
    /// statement (a <see cref="DbUpdateException"/> around the <see cref="PostgresException"/>), and
    /// records how many entries each attempt was about to write.
    /// </summary>
    private sealed class FailEverySave(string sqlState) : SaveChangesInterceptor
    {
        private readonly List<int> _pendingChangesPerAttempt = [];

        public int Attempts => _pendingChangesPerAttempt.Count;

        public IReadOnlyList<int> PendingChangesPerAttempt => _pendingChangesPerAttempt;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var pending = eventData.Context!.ChangeTracker.Entries()
                .Count(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            _pendingChangesPerAttempt.Add(pending);
            throw new DbUpdateException(
                "Injected failure.",
                new PostgresException("injected", "ERROR", "ERROR", sqlState));
        }
    }
}
