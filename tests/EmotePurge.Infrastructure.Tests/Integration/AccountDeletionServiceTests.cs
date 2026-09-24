using System.Security.Cryptography;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Against real Postgres and Redis: what is worth asserting here is row locks, FK behaviour, jsonb
// round trips and real Redis keys — a mocked context would prove none of it. Redis is a class fixture
// (its own container), because the rate-limit telemetry's last-rejection slot is one global key and
// the tests below assert on its exact content.
//
// The concurrency cases are made deterministic by waiting on Postgres itself rather than on a clock:
// every contender runs on a context with its own application_name, and WaitUntilBlockedOnLockAsync
// (PostgresLockProbe) polls pg_stat_activity until that backend is actually waiting on a lock. No test
// sleeps and hopes.
[Collection("Postgres")]
public class AccountDeletionServiceTests(PostgresFixture fixture, RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    private static readonly AuditActor Admin = new("acctdel-admin", "acctdeladmin");

    [Fact]
    public async Task Delete_AdminRequest_RemovesTheRowAndEveryVote_AndKeepsOtherVoters()
    {
        var user = await SeedUserAsync("acctdel-votes");
        var other = await SeedUserAsync("acctdel-votes-other");
        var (channel, openSession, endedSession, emote) = await SeedChannelWithSessionsAsync("acctdelvotes");
        await SeedVotesAsync(
            (openSession.Id, emote.Id, user.Id, VoteType.Keep),
            (endedSession.Id, emote.Id, user.Id, VoteType.Delete),
            (openSession.Id, emote.Id, other.Id, VoteType.Keep));
        await using (var tokenDb = fixture.CreateDbContext())
        {
            await new UserService(tokenDb, CreateCipher(), CreateRoleCache())
                .StoreTwitchTokensAsync(user.Id, "access", DateTime.UtcNow.AddHours(1), "refresh", "scopes");
        }

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, onlyIfInactiveBeforeUtc: null);

        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
        Assert.Equal(2, result.VotesDeleted);
        Assert.Equal(1, result.VotesInOpenSessionsDeleted);

        await using var verifyDb = fixture.CreateDbContext();
        // The row carried the encrypted tokens; there is no separate token store to check.
        Assert.False(await verifyDb.Users.AnyAsync(u => u.Id == user.Id));
        Assert.False(await verifyDb.Votes.AnyAsync(v => v.UserId == user.Id));
        Assert.Equal(1, await verifyDb.Votes.CountAsync(v => v.UserId == other.Id));
        // No channel is touched — there is no user -> channel edge, and channel data has its own period.
        Assert.True(await verifyDb.Channels.AnyAsync(c => c.Id == channel.Id));
    }

    [Fact]
    public async Task Delete_OpenSessionLosesTheVote_AndItsScoreDrops()
    {
        var user = await SeedUserAsync("acctdel-score");
        var other = await SeedUserAsync("acctdel-score-other");
        var (channel, openSession, _, emote) = await SeedChannelWithSessionsAsync("acctdelscore");
        await SeedVotesAsync(
            (openSession.Id, emote.Id, user.Id, VoteType.Keep),
            (openSession.Id, emote.Id, other.Id, VoteType.Keep));

        await using (var beforeDb = fixture.CreateDbContext())
        {
            var before = await new VoteSessionQueryService(beforeDb, new UsageStatQueryService(beforeDb))
                .GetResultsAsync(channel.ChannelName, openSession.Id);
            Assert.Equal(2, Assert.Single(before!.Emotes).Score);
        }

        await using (var db = fixture.CreateDbContext())
        {
            await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);
        }

        await using var afterDb = fixture.CreateDbContext();
        var after = await new VoteSessionQueryService(afterDb, new UsageStatQueryService(afterDb))
            .GetResultsAsync(channel.ChannelName, openSession.Id);
        Assert.Equal(1, Assert.Single(after!.Emotes).Score);
        Assert.Equal(1, after.VoterCount);
    }

    [Fact]
    public async Task Delete_PseudonymisesEntriesWhereTheUserWasActor_AndLeavesTheRestOfThemAlone()
    {
        var user = await SeedUserAsync("acctdel-actor");
        var entryId = await SeedAuditEntryAsync(new AuditLogEntry
        {
            ActorTwitchUserId = user.Id,
            ActorLogin = user.TwitchUsername,
            Action = AuditActions.ChannelLeave,
            ChannelName = "acctdelactorchan",
            DetailsJson = """{"reason":"manual"}"""
        });

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        Assert.Equal(1, result.AuditEntriesPseudonymisedAsActor);
        Assert.Equal(0, result.AuditEntriesPseudonymisedAsTarget);
        Assert.Equal(1, result.AuditEntriesPseudonymised);

        await using var verifyDb = fixture.CreateDbContext();
        var entry = await verifyDb.AuditLogEntries.AsNoTracking().SingleAsync(e => e.Id == entryId);
        Assert.Equal(AuditActor.DeletedUser.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(AuditActor.DeletedUser.Login, entry.ActorLogin);
        Assert.Equal(AuditActions.ChannelLeave, entry.Action);
        Assert.Equal("acctdelactorchan", entry.ChannelName);
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal("manual", details.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Delete_PseudonymisesUserTargetEntries_IncludingTheLoginDetail_AndKeepsOtherDetails()
    {
        // Both entries written by the real service methods, so the test follows their actual shape.
        var user = await SeedUserAsync("acctdel-target");
        await using (var seedDb = fixture.CreateDbContext())
        {
            var users = new UserService(seedDb, CreateCipher(), CreateRoleCache());
            await users.RevokeSessionsAsync(user.Id, Admin);
            await users.InvalidateRoleCacheAsync(user.Id, Admin);
        }

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        Assert.Equal(2, result.AuditEntriesPseudonymisedAsTarget);
        Assert.Equal(0, result.AuditEntriesPseudonymisedAsActor);

        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.AuditLogEntries.AnyAsync(e => e.TargetType == "user" && e.TargetId == user.Id));
        var invalidation = await verifyDb.AuditLogEntries.AsNoTracking()
            .Where(e => e.Action == AuditActions.UserInvalidateRoleCache
                && e.TargetId == AuditActor.DeletedUser.TwitchUserId
                && e.ActorTwitchUserId == Admin.TwitchUserId)
            .OrderByDescending(e => e.Id)
            .FirstAsync();
        using var details = JsonDocument.Parse(invalidation.DetailsJson!);
        Assert.Equal(AuditActor.DeletedUser.Login, details.RootElement.GetProperty("login").GetString());
        // Only the identifying key is rewritten; the rest of the payload survives.
        Assert.Equal(0, details.RootElement.GetProperty("removedEntries").GetInt32());
        // The admin who acted is not the deleted user and stays named.
        Assert.Equal(Admin.Login, invalidation.ActorLogin);
    }

    [Fact]
    public async Task Delete_LeavesChannelEntriesUntouched_EvenWhenTheyCarryTheSameLoginAndId()
    {
        // A broadcaster's Twitch user id is their channel id, and their login is the channel name — so
        // channel history repeats both. It is channel data, not account data, and must survive.
        var user = await SeedUserAsync("acctdel-chan");
        var renameId = await SeedAuditEntryAsync(new AuditLogEntry
        {
            ActorTwitchUserId = AuditActor.System.TwitchUserId,
            ActorLogin = AuditActor.System.Login,
            Action = AuditActions.ChannelRename,
            ChannelName = user.TwitchUsername,
            DetailsJson = JsonSerializer.Serialize(new { twitchChannelId = user.Id, oldLogin = "acctdelchanold", newLogin = user.TwitchUsername })
        });
        var channelTargetId = await SeedAuditEntryAsync(new AuditLogEntry
        {
            ActorTwitchUserId = Admin.TwitchUserId,
            ActorLogin = Admin.Login,
            Action = AuditActions.ChannelPurge,
            ChannelName = user.TwitchUsername,
            TargetType = "channel",
            TargetId = user.Id,
            DetailsJson = JsonSerializer.Serialize(new { login = user.TwitchUsername })
        });
        var before = await LoadEntriesAsync(renameId, channelTargetId);

        await using var db = fixture.CreateDbContext();
        await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        var after = await LoadEntriesAsync(renameId, channelTargetId);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Delete_WritesAUserDeleteEntry_ThatCarriesNoIdentity()
    {
        var user = await SeedUserAsync("acctdel-entry");
        var (_, openSession, _, emote) = await SeedChannelWithSessionsAsync("acctdelentry");
        await SeedVotesAsync((openSession.Id, emote.Id, user.Id, VoteType.Delete));
        await SeedAuditEntryAsync(new AuditLogEntry
        {
            ActorTwitchUserId = user.Id,
            ActorLogin = user.TwitchUsername,
            Action = AuditActions.ChannelJoin,
            ChannelName = "acctdelentry"
        });
        var watermark = await AuditWatermarkAsync();

        await using var db = fixture.CreateDbContext();
        await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        var entry = await SingleUserDeleteEntryAfterAsync(watermark);
        Assert.Equal(Admin.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(Admin.Login, entry.ActorLogin);
        Assert.Equal("user", entry.TargetType);
        Assert.Equal(AuditActor.DeletedUser.TwitchUserId, entry.TargetId);
        Assert.Null(entry.ChannelName);
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal("adminRequest", details.RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, details.RootElement.GetProperty("votesDeleted").GetInt32());
        Assert.Equal(1, details.RootElement.GetProperty("auditEntriesPseudonymised").GetInt32());
        Assert.Equal(3, details.RootElement.EnumerateObject().Count());
        await AssertNoEntryNamesAsync(user);
    }

    [Fact]
    public async Task Delete_SelfDeletion_RecordsTheMarkerAsActor_AndCountsAnEntryNamingThemTwiceOnce()
    {
        // An admin deleting themself from the admin list. Their earlier revocation of their own sessions
        // named them as actor *and* target — one entry, counted once in the total.
        var user = await SeedUserAsync("acctdel-self");
        var self = new AuditActor(user.Id, user.TwitchUsername);
        await using (var seedDb = fixture.CreateDbContext())
        {
            await new UserService(seedDb, CreateCipher(), CreateRoleCache()).RevokeSessionsAsync(user.Id, self);
        }

        var watermark = await AuditWatermarkAsync();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).DeleteAsync(user.Id, self, AccountDeletionReason.AdminRequest, null);

        Assert.Equal(1, result.AuditEntriesPseudonymisedAsActor);
        Assert.Equal(1, result.AuditEntriesPseudonymisedAsTarget);
        Assert.Equal(1, result.AuditEntriesPseudonymised);
        var entry = await SingleUserDeleteEntryAfterAsync(watermark);
        Assert.Equal(AuditActor.DeletedUser.TwitchUserId, entry.ActorTwitchUserId);
        Assert.Equal(AuditActor.DeletedUser.Login, entry.ActorLogin);
        await AssertNoEntryNamesAsync(user);
    }

    [Fact]
    public async Task Delete_CalledTwice_ReturnsNotFound_AndWritesNothingTheSecondTime()
    {
        var user = await SeedUserAsync("acctdel-twice");
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(AccountDeletionOutcome.Deleted, (await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null)).Outcome);
        }

        var watermark = await AuditWatermarkAsync();
        await using var secondDb = fixture.CreateDbContext();
        var second = await CreateService(secondDb).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        Assert.Equal(new AccountDeletionResult(AccountDeletionOutcome.NotFound), second);
        Assert.Equal(watermark, await AuditWatermarkAsync());
    }

    [Fact]
    public async Task Delete_UnknownUser_ReturnsNotFound_AndLeavesRedisAlone()
    {
        // The id is unverified input when no row backs it — it must not reach the SCAN pattern.
        const string unknown = "acctdel-nobody";
        await redisFixture.Connection.GetDatabase().StringSetAsync($"modlist:{unknown}", "[]");

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).DeleteAsync(unknown, Admin, AccountDeletionReason.AdminRequest, null);

        Assert.Equal(AccountDeletionOutcome.NotFound, result.Outcome);
        Assert.True(await redisFixture.Connection.GetDatabase().KeyExistsAsync($"modlist:{unknown}"));
    }

    [Theory]
    [InlineData(-400, -400, AccountDeletionOutcome.Deleted)]
    [InlineData(-400, -10, AccountDeletionOutcome.StillActive)]
    [InlineData(-10, -400, AccountDeletionOutcome.StillActive)]
    [InlineData(-400, null, AccountDeletionOutcome.Deleted)]
    public async Task Delete_Inactivity_ComparesTheLaterOfLoginAndLastSeenWithTheCutoff(
        int lastLoginDaysAgo, int? lastSeenDaysAgo, AccountDeletionOutcome expected)
    {
        var now = DateTime.UtcNow;
        var id = $"acctdel-inact{lastLoginDaysAgo}{lastSeenDaysAgo?.ToString() ?? "n"}";
        var user = await SeedUserAsync(id, now.AddDays(lastLoginDaysAgo), lastSeenDaysAgo is null ? null : now.AddDays(lastSeenDaysAgo.Value));
        var watermark = await AuditWatermarkAsync();

        await using var db = fixture.CreateDbContext();
        var result = await CreateService(db).DeleteAsync(user.Id, AuditActor.System, AccountDeletionReason.Inactivity, now.AddDays(-365));

        Assert.Equal(expected, result.Outcome);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.Equal(expected == AccountDeletionOutcome.StillActive, await verifyDb.Users.AnyAsync(u => u.Id == user.Id));
        if (expected == AccountDeletionOutcome.StillActive)
        {
            Assert.Equal(watermark, await AuditWatermarkAsync());
        }
        else
        {
            var entry = await SingleUserDeleteEntryAfterAsync(watermark);
            Assert.Equal(AuditActor.System.Login, entry.ActorLogin);
            using var details = JsonDocument.Parse(entry.DetailsJson!);
            Assert.Equal("inactivity", details.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task Delete_Inactivity_RechecksUnderTheLock_AndLosesToAStampCommittedWhileItWaited()
    {
        // The job selected this user as a candidate (activity 400 days ago). Before its deletion gets
        // the row, a request stamps LastSeenAtUtc — still uncommitted when the deletion arrives. The
        // deletion must wait for it and then see the fresh stamp, not the stale candidate state.
        var now = DateTime.UtcNow;
        var user = await SeedUserAsync("acctdel-recheck", now.AddDays(-400), now.AddDays(-400));
        var (_, openSession, _, emote) = await SeedChannelWithSessionsAsync("acctdelrecheck");
        await SeedVotesAsync((openSession.Id, emote.Id, user.Id, VoteType.Keep));

        await using var stampDb = fixture.CreateDbContext();
        await using var stampTransaction = await stampDb.Database.BeginTransactionAsync();
        await stampDb.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAtUtc, now));

        const string deletionTag = "acctdel-recheck-deletion";
        await using var deletionDb = fixture.CreateTaggedDbContext(deletionTag);
        var deletion = CreateService(deletionDb).DeleteAsync(user.Id, AuditActor.System, AccountDeletionReason.Inactivity, now.AddDays(-365));
        await fixture.WaitUntilBlockedOnLockAsync(deletionTag, deletion);

        await stampTransaction.CommitAsync();

        Assert.Equal(AccountDeletionOutcome.StillActive, (await deletion).Outcome);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.True(await verifyDb.Users.AnyAsync(u => u.Id == user.Id));
        Assert.Equal(1, await verifyDb.Votes.CountAsync(v => v.UserId == user.Id));
    }

    [Fact]
    public async Task Delete_RejectsACutoffMismatchedWithTheReason()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DeleteAsync("acctdel-args", AuditActor.System, AccountDeletionReason.Inactivity, onlyIfInactiveBeforeUtc: null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DeleteAsync("acctdel-args", Admin, AccountDeletionReason.AdminRequest, DateTime.UtcNow));
    }

    [Fact]
    public async Task Delete_ClearsTheRoleCacheKeysOfTheUser()
    {
        var user = await SeedUserAsync("acctdel-rolecache");
        var redis = redisFixture.Connection.GetDatabase();
        await redis.StringSetAsync($"modlist:{user.Id}", "[]");
        await redis.StringSetAsync($"7tveditor:{user.Id}", "{}");
        await CreateRoleCache().SetIsSubscriberAsync(user.Id, "9001", isSubscriber: true);

        await using var db = fixture.CreateDbContext();
        await CreateService(db).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        Assert.False(await redis.KeyExistsAsync($"modlist:{user.Id}"));
        Assert.False(await redis.KeyExistsAsync($"7tveditor:{user.Id}"));
        Assert.False(await redis.KeyExistsAsync($"subcheck:{user.Id}:9001"));
    }

    [Theory]
    [InlineData("acctdel-slot", true)]
    [InlineData("acctdel-slot:42", true)]
    [InlineData("acctdel-slot-stranger", false)]
    public async Task Delete_ForgetsTheTelemetrySlot_OnlyWhenItCarriesThisUser(string storedPartition, bool forgotten)
    {
        // The limiter partitions by the bare user id, and the voting policy by "{id}:{sessionId}";
        // another user whose id merely starts with this one must keep their slot.
        var store = CreateTelemetryStore();
        await store.RecordPolicyDecisionAsync(new RateLimitPolicyDecision("voting", Accepted: false, "POST", "/api/x", storedPartition, 5));
        var user = await SeedUserAsync("acctdel-slot");

        await using var db = fixture.CreateDbContext();
        await CreateService(db, telemetry: store).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        var remaining = (await store.ReadAsync()).LastLocalRejection;
        if (forgotten)
        {
            Assert.Null(remaining);
        }
        else
        {
            Assert.Equal(storedPartition, remaining?.Partition);
        }
    }

    [Fact]
    public async Task Delete_RetriesAFailedRedisStepOnce_AndLogsOnlyACountWhenTheRetryFailsToo()
    {
        var user = await SeedUserAsync("acctdel-retry");
        var roleCache = new FlakyRoleCache(failures: 1);
        var telemetry = new FailingTelemetry();
        var logger = new RecordingLogger<AccountDeletionService>();

        await using var db = fixture.CreateDbContext();
        var result = await new AccountDeletionService(db, roleCache, telemetry, logger)
            .DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);

        // A Redis failure never undoes the committed deletion.
        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
        Assert.Equal(2, roleCache.Calls);
        Assert.Equal(2, telemetry.Calls);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("1 Redis cleanup step", warning.Message);
        Assert.DoesNotContain(user.Id, warning.Message);
    }

    [Fact]
    public async Task LateAuditWriterHoldingTheSharedLock_IsWaitedFor_AndItsEntryIsPseudonymised()
    {
        // Writer A (a role-cache invalidation) holds FOR SHARE on the user row and has not inserted its
        // audit entry yet; deletion B starts and must wait. When A commits, B runs and rewrites A's
        // fresh entry like every older one.
        var user = await SeedUserAsync("acctdel-overlap-a");
        var gate = new GatedRoleCache();

        await using var writerDb = fixture.CreateDbContext();
        var writer = new UserService(writerDb, CreateCipher(), gate).InvalidateRoleCacheAsync(user.Id, Admin);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        const string deletionTag = "acctdel-overlap-a-deletion";
        await using var deletionDb = fixture.CreateTaggedDbContext(deletionTag);
        var deletion = CreateService(deletionDb).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);
        await fixture.WaitUntilBlockedOnLockAsync(deletionTag, deletion);

        gate.Release.SetResult();
        Assert.Equal(0, await writer);
        var result = await deletion;

        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
        Assert.Equal(1, result.AuditEntriesPseudonymisedAsTarget);
        await AssertNoEntryNamesAsync(user);
    }

    [Fact]
    public async Task LateAuditWriterArrivingWhileTheDeletionHoldsTheLock_FindsNoRow_AndWritesNothing()
    {
        // Deletion B already holds FOR UPDATE on the user row. To keep it inside its transaction long
        // enough, the test itself locks one of the user's audit entries, which B has to rewrite. Writer A
        // then asks for FOR SHARE and must wait; once B commits, A finds no row and writes nothing.
        var user = await SeedUserAsync("acctdel-overlap-b");
        var entryId = await SeedAuditEntryAsync(new AuditLogEntry
        {
            ActorTwitchUserId = Admin.TwitchUserId,
            ActorLogin = Admin.Login,
            Action = AuditActions.UserRevokeSessions,
            TargetType = "user",
            TargetId = user.Id,
            DetailsJson = JsonSerializer.Serialize(new { login = user.TwitchUsername })
        });
        var watermark = await AuditWatermarkAsync();

        await using var blockerDb = fixture.CreateDbContext();
        await using var blocker = await blockerDb.Database.BeginTransactionAsync();
        await blockerDb.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AuditLogEntries" WHERE "Id" = {entryId} FOR UPDATE""");

        const string deletionTag = "acctdel-overlap-b-deletion";
        await using var deletionDb = fixture.CreateTaggedDbContext(deletionTag);
        var deletion = CreateService(deletionDb).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);
        await fixture.WaitUntilBlockedOnLockAsync(deletionTag, deletion);

        const string writerTag = "acctdel-overlap-b-writer";
        await using var writerDb = fixture.CreateTaggedDbContext(writerTag);
        var writer = new UserService(writerDb, CreateCipher(), CreateRoleCache()).InvalidateRoleCacheAsync(user.Id, Admin);
        await fixture.WaitUntilBlockedOnLockAsync(writerTag, writer);

        await blocker.RollbackAsync();

        Assert.Equal(AccountDeletionOutcome.Deleted, (await deletion).Outcome);
        Assert.Null(await writer);
        await using var verifyDb = fixture.CreateDbContext();
        // Exactly one new entry — B's own user.delete; A wrote nothing.
        var newEntries = await verifyDb.AuditLogEntries.AsNoTracking().Where(e => e.Id > watermark).ToListAsync();
        Assert.Equal(AuditActions.UserDelete, Assert.Single(newEntries).Action);
        await AssertNoEntryNamesAsync(user);
    }

    [Fact]
    public async Task VoteArrivingWhileTheDeletionHoldsTheLock_FailsItsForeignKey_AndTheDeletionCommits()
    {
        // A vote insert checks its FK with a key-share lock on the user row, which conflicts with the
        // deletion's FOR UPDATE: the vote waits, and after the commit its FK check fails. The deletion
        // itself is never the one to fail.
        var user = await SeedUserAsync("acctdel-vote-race");
        var (channel, openSession, _, emote) = await SeedChannelWithSessionsAsync("acctdelvoterace");
        var entryId = await SeedAuditEntryAsync(new AuditLogEntry
        {
            ActorTwitchUserId = user.Id,
            ActorLogin = user.TwitchUsername,
            Action = AuditActions.ChannelJoin,
            ChannelName = channel.ChannelName
        });

        await using var blockerDb = fixture.CreateDbContext();
        await using var blocker = await blockerDb.Database.BeginTransactionAsync();
        await blockerDb.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AuditLogEntries" WHERE "Id" = {entryId} FOR UPDATE""");

        const string deletionTag = "acctdel-vote-race-deletion";
        await using var deletionDb = fixture.CreateTaggedDbContext(deletionTag);
        var deletion = CreateService(deletionDb).DeleteAsync(user.Id, Admin, AccountDeletionReason.AdminRequest, null);
        await fixture.WaitUntilBlockedOnLockAsync(deletionTag, deletion);

        const string voteTag = "acctdel-vote-race-vote";
        await using var voteDb = fixture.CreateTaggedDbContext(voteTag);
        var vote = new VoteSessionService(voteDb, Substitute.For<IForeignEmoteSetService>(), Substitute.For<ISevenTvEmoteSetListService>()).CastVoteAsync(channel.ChannelName, openSession.Id, emote.Id, user.Id, VoteType.Keep);
        await fixture.WaitUntilBlockedOnLockAsync(voteTag, vote);

        await blocker.RollbackAsync();

        Assert.Equal(AccountDeletionOutcome.Deleted, (await deletion).Outcome);
        await Assert.ThrowsAsync<DbUpdateException>(() => vote);
        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.Votes.AnyAsync(v => v.UserId == user.Id));
    }

    private static AesGcmTokenCipher CreateCipher()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Twitch:TokenEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            })
            .Build();
        return new AesGcmTokenCipher(configuration);
    }

    private ModRoleCache CreateRoleCache() =>
        new(redisFixture.Connection, new ConfigurationBuilder().Build(), NullLogger<ModRoleCache>.Instance);

    private RateLimitTelemetryStore CreateTelemetryStore() =>
        new(redisFixture.Connection, TimeProvider.System, NullLogger<RateLimitTelemetryStore>.Instance);

    private AccountDeletionService CreateService(AppDbContext db, IRateLimitTelemetry? telemetry = null) =>
        new(db, CreateRoleCache(), telemetry ?? CreateTelemetryStore(), NullLogger<AccountDeletionService>.Instance);

    private async Task<User> SeedUserAsync(string id, DateTime? lastLogin = null, DateTime? lastSeen = null)
    {
        await using var db = fixture.CreateDbContext();
        var user = new User
        {
            Id = id,
            TwitchUsername = id.Replace("-", string.Empty),
            DisplayName = id,
            LastLogin = lastLogin ?? DateTime.UtcNow,
            LastSeenAtUtc = lastSeen
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<(Channel Channel, VoteSession Open, VoteSession Ended, Emote Emote)> SeedChannelWithSessionsAsync(string channelName)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = channelName, IsBotActive = true };
        db.Channels.Add(channel);
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "Emote",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        var open = new VoteSession { ChannelId = channel.Id, Title = "Open", IsActive = true };
        var ended = new VoteSession { ChannelId = channel.Id, Title = "Ended", IsActive = false, EndedAt = DateTime.UtcNow };
        db.VoteSessions.AddRange(open, ended);
        await db.SaveChangesAsync();
        return (channel, open, ended, emote);
    }

    private async Task SeedVotesAsync(params (long SessionId, string EmoteId, string UserId, VoteType Type)[] votes)
    {
        await using var db = fixture.CreateDbContext();
        db.Votes.AddRange(votes.Select(v => new Vote { VoteSessionId = v.SessionId, EmoteId = v.EmoteId, UserId = v.UserId, Type = v.Type }));
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedAuditEntryAsync(AuditLogEntry entry)
    {
        await using var db = fixture.CreateDbContext();
        db.AuditLogEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<long> AuditWatermarkAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.MaxAsync(e => (long?)e.Id) ?? 0;
    }

    private async Task<AuditLogEntry> SingleUserDeleteEntryAfterAsync(long watermark)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking().SingleAsync(e => e.Id > watermark && e.Action == AuditActions.UserDelete);
    }

    private async Task<IReadOnlyList<string>> LoadEntriesAsync(params long[] ids)
    {
        await using var db = fixture.CreateDbContext();
        var entries = await db.AuditLogEntries.AsNoTracking().Where(e => ids.Contains(e.Id)).OrderBy(e => e.Id).ToListAsync();
        return entries.Select(e => JsonSerializer.Serialize(e)).ToList();
    }

    /// <summary>
    /// Nothing in the whole audit log names the deleted user any more — id or login, in any column.
    /// Checked client-side: the details are jsonb, and the point is to catch the user in *any* key.
    /// Only valid for users whose test seeds no channel history (that legitimately keeps the name).
    /// </summary>
    private async Task AssertNoEntryNamesAsync(User user)
    {
        await using var db = fixture.CreateDbContext();
        var entries = await db.AuditLogEntries.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(entries, e =>
            e.ActorTwitchUserId == user.Id
            || e.ActorLogin == user.TwitchUsername
            || e.TargetId == user.Id
            || (e.DetailsJson?.Contains(user.Id, StringComparison.Ordinal) ?? false)
            || (e.DetailsJson?.Contains(user.TwitchUsername, StringComparison.Ordinal) ?? false));
    }

    /// <summary>
    /// A role cache whose invalidation parks until released — how the test holds a late audit writer
    /// inside its transaction, after the FOR SHARE lock and before the audit insert.
    /// </summary>
    private sealed class GatedRoleCache : IModRoleCache
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> InvalidateUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return 0;
        }

        public Task<SevenTvEditorGrants?> TryGetSevenTvEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetSevenTvEditorGrantsAsync(string twitchUserId, SevenTvEditorGrants grants, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool?> TryGetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, bool isSubscriber, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A role cache whose invalidation throws the first <c>failures</c> times.</summary>
    private sealed class FlakyRoleCache(int failures) : IModRoleCache
    {
        public int Calls { get; private set; }

        public Task<int> InvalidateUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Calls <= failures
                ? throw new InvalidOperationException($"redis down for {twitchUserId}")
                : Task.FromResult(0);
        }

        public Task<SevenTvEditorGrants?> TryGetSevenTvEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetSevenTvEditorGrantsAsync(string twitchUserId, SevenTvEditorGrants grants, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool?> TryGetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetIsSubscriberAsync(string twitchUserId, string broadcasterTwitchId, bool isSubscriber, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>Telemetry whose forget call always reports the store as unreachable.</summary>
    private sealed class FailingTelemetry : IRateLimitTelemetry
    {
        public int Calls { get; private set; }

        public Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(false);
        }
    }
}
