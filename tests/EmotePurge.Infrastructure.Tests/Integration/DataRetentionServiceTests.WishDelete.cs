using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// K7a (plan #500 T2, AK112; spec E32, E35): deleting an ended wish against the real retention pass, in this suite's own
// database because the pass works on every row. The pass deletes ended wishes (4c) and clears requester data (4d) in
// unordered batches; the delete takes the channel row, then the wish row waiting, then the wishes merged into it
// without waiting, so it never waits for a wish row while it holds another and cannot deadlock with the pass.
//
// Deterministic through a test-only barrier, not through the order of ids: an AFTER row trigger on exactly one wish row
// waits on an advisory lock the test holds. When it runs, the pass's statement has already changed that row and holds
// its row lock until the statement's transaction ends (a BEFORE trigger would run before the row lock and prove
// nothing). A NOWAIT probe from a third connection proves the row lock before the delete is started. After the barrier
// opens, the pass must finish without an exception and with every later category done (capture gap, deactivated
// channel, system load): a pass that was the deadlock victim would have thrown out of the wish categories.
public partial class DataRetentionServiceTests
{
    private const long WishBarrierKey = 500_112;
    private const string WishBarrierTrigger = "retention_test_wish_barrier";

    private static readonly AuditActor WishDeleteActor = new("wish-delete-mod", "wishdeletemod");

    // AK112 (a): the pass holds exactly one merged child, once while deleting it after 365 days (4c), once while clearing
    // its requester data (4d). The delete gives way at once with a state conflict, and the pass completes.
    [Theory]
    [InlineData("DELETE")]
    [InlineData("UPDATE")]
    public async Task Delete_WhileTheRealRetentionPassHoldsAMergedChild_GivesWayAtOnce_AndThePassCompletesEveryLaterCategory(string operation)
    {
        var deleting = operation == "DELETE";
        var channel = await SeedWishDeleteChannelAsync("wishdelchild");
        var target = await SeedRetentionWishAsync(channel.Id, WishStatus.Removed, endedDaysAgo: 1.0 / 24, firstActivatedDaysAgo: 40);
        var child = deleting
            ? await SeedRetentionWishAsync(
                channel.Id, WishStatus.Merged, endedDaysAgo: RetentionPolicy.EndedWish.TotalDays + 1, createdDaysAgo: 500,
                firstActivatedDaysAgo: 450, mergedInto: target)
            : await SeedRetentionWishAsync(
                channel.Id, WishStatus.Merged, endedDaysAgo: RetentionPolicy.WishRequester.TotalDays + 1, firstActivatedDaysAgo: 45,
                mergedInto: target);
        var later = await SeedLaterRetentionCategoriesAsync(channel.Id);
        var ended = (await LoadWishAsync(target)).EndedAtUtc!.Value;
        var targetBefore = await WishRowSnapshotAsync(target);
        const string passTag = "retention-wish-delete-child";

        await using var barrier = await WishBarrier.InstallAsync(this, operation, child);
        var pass = barrier.Track(RunAsync(enforce: true, applicationName: passTag));
        await fixture.WaitUntilBlockedOnLockAsync(passTag, pass);
        Assert.Equal("advisory", await WaitEventAsync(passTag));
        Assert.Contains($"{operation} {(deleting ? "FROM " : string.Empty)}\"Wishes\"", await fixture.WaitingQueryAsync(passTag), StringComparison.Ordinal);
        Assert.True(await IsWishRowLockedAsync(child));

        // Without NOWAIT on the children the delete would wait here for the pass; it gives way within a short bound (which
        // cancels the statement rather than abandoning a task that still uses the context).
        await using (var db = CreateDbContext())
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var refused = await WishDeleteService(db)
                .DeleteAsync(channel.ChannelName, target, ended, WishDeleteActor, AccessOf(channel), bound.Token);
            Assert.Equal(WishOutcome.StateConflict, refused.Outcome);
        }

        Assert.Equal(targetBefore, await WishRowSnapshotAsync(target));
        Assert.Equal(0, await CountWishDeleteAuditsAsync(channel));
        Assert.False(pass.IsCompleted);

        await barrier.ReleaseAsync();
        var summary = await pass.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(deleting ? 1 : 0, summary.EndedWishesDeleted);
        Assert.Equal(deleting ? 0 : 1, summary.WishRequestersCleared);
        await AssertLaterRetentionCategoriesDoneAsync(later, summary);
        await using (var verify = CreateDbContext())
        {
            var childRow = await verify.Wishes.AsNoTracking().SingleOrDefaultAsync(w => w.Id == child);
            if (deleting)
            {
                Assert.Null(childRow);
            }
            else
            {
                Assert.Equal((target, (string?)null), (childRow!.MergedIntoWishId, childRow.RequesterTwitchUserId));
            }
        }

        // The conflict was the lock and nothing else: the retry the client offers deletes.
        await using var retryDb = CreateDbContext();
        var retried = await WishDeleteService(retryDb).DeleteAsync(channel.ChannelName, target, ended, WishDeleteActor, AccessOf(channel));
        Assert.Equal(WishOutcome.Success, retried.Outcome);
        Assert.Equal(1, await CountWishDeleteAuditsAsync(channel));
    }

    // AK112 (a), the other direction: the delete holds channel, target and child when the pass starts. The pass waits on
    // the child, the delete commits, the pass then deletes the child (now without its reference) and completes.
    [Fact]
    public async Task Delete_HoldingTheMergedChildWhenTheRetentionPassStarts_Succeeds_AndThePassWaitsThenCompletes()
    {
        var channel = await SeedWishDeleteChannelAsync("wishdelfirst");
        var target = await SeedRetentionWishAsync(channel.Id, WishStatus.Removed, endedDaysAgo: 1.0 / 24, firstActivatedDaysAgo: 40);
        var child = await SeedRetentionWishAsync(
            channel.Id, WishStatus.Merged, endedDaysAgo: RetentionPolicy.EndedWish.TotalDays + 1, createdDaysAgo: 500,
            firstActivatedDaysAgo: 450, mergedInto: target);
        var later = await SeedLaterRetentionCategoriesAsync(channel.Id);
        var ended = (await LoadWishAsync(target)).EndedAtUtc!.Value;
        const string passTag = "retention-wish-delete-second";
        Task<RetentionRunSummary>? pass = null;
        var hook = new CommandHook(
            command => command.Contains("NOWAIT", StringComparison.Ordinal),
            async _ =>
            {
                Assert.True(await IsWishRowLockedAsync(child));
                pass = RunAsync(enforce: true, applicationName: passTag);
                await fixture.WaitUntilBlockedOnLockAsync(passTag, pass);
                Assert.Contains("DELETE FROM \"Wishes\"", await fixture.WaitingQueryAsync(passTag), StringComparison.Ordinal);
            });

        try
        {
            await using var db = fixture.CreateTaggedDbContext("wish-delete-before-retention", [hook], databaseName: DatabaseName);
            var result = await WishDeleteService(db)
                .DeleteAsync(channel.ChannelName, target, ended, WishDeleteActor, AccessOf(channel))
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(WishOutcome.Success, result.Outcome);
        }
        catch
        {
            await DrainAsync(pass);
            throw;
        }

        Assert.Equal(1, hook.Count(command => command.Contains("NOWAIT", StringComparison.Ordinal)));
        var summary = await pass!.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, summary.EndedWishesDeleted);
        await AssertLaterRetentionCategoriesDoneAsync(later, summary);
        await using var verify = CreateDbContext();
        Assert.False(await verify.Wishes.AnyAsync(w => w.Id == target || w.Id == child));
        Assert.Equal(1, await CountWishDeleteAuditsAsync(channel));
    }

    // AK112 (b): the pass holds the wish itself. The delete waits at the wish row (behind its channel lock) and then
    // answers by what the pass did: not found after the 365-day deletion, a success after clearing the requester data.
    [Theory]
    [InlineData("DELETE", WishOutcome.WishNotFound)]
    [InlineData("UPDATE", WishOutcome.Success)]
    public async Task Delete_WhileTheRealRetentionPassHoldsTheWishItself_Waits_ThenAnswersByWhatThePassDid(string operation, WishOutcome expected)
    {
        var deleting = operation == "DELETE";
        var channel = await SeedWishDeleteChannelAsync("wishdeltarget");
        var target = deleting
            ? await SeedRetentionWishAsync(
                channel.Id, WishStatus.Removed, endedDaysAgo: RetentionPolicy.EndedWish.TotalDays + 1, createdDaysAgo: 500,
                firstActivatedDaysAgo: 450)
            : await SeedRetentionWishAsync(
                channel.Id, WishStatus.Removed, endedDaysAgo: RetentionPolicy.WishRequester.TotalDays + 1, firstActivatedDaysAgo: 45);
        var later = await SeedLaterRetentionCategoriesAsync(channel.Id);
        var ended = (await LoadWishAsync(target)).EndedAtUtc!.Value;
        const string passTag = "retention-wish-delete-target";
        const string deleteTag = "wish-delete-behind-retention";

        await using var barrier = await WishBarrier.InstallAsync(this, operation, target);
        var pass = barrier.Track(RunAsync(enforce: true, applicationName: passTag));
        await fixture.WaitUntilBlockedOnLockAsync(passTag, pass);
        Assert.Equal("advisory", await WaitEventAsync(passTag));
        Assert.True(await IsWishRowLockedAsync(target));

        await using var deleteDb = fixture.CreateTaggedDbContext(deleteTag, DatabaseName);
        var delete = WishDeleteService(deleteDb).DeleteAsync(channel.ChannelName, target, ended, WishDeleteActor, AccessOf(channel));
        await fixture.WaitUntilBlockedOnLockAsync(deleteTag, delete);
        var waitingAt = await fixture.WaitingQueryAsync(deleteTag);
        Assert.Contains("FROM \"Wishes\"", waitingAt, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", waitingAt, StringComparison.Ordinal);
        Assert.DoesNotContain("NOWAIT", waitingAt, StringComparison.Ordinal);

        await barrier.ReleaseAsync();
        var summary = await pass.WaitAsync(TimeSpan.FromSeconds(30));
        var result = await delete.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(deleting ? 1 : 0, summary.EndedWishesDeleted);
        Assert.Equal(deleting ? 0 : 1, summary.WishRequestersCleared);
        await AssertLaterRetentionCategoriesDoneAsync(later, summary);
        await using var verify = CreateDbContext();
        Assert.False(await verify.Wishes.AnyAsync(w => w.Id == target));
        Assert.Equal(deleting ? 0 : 1, await CountWishDeleteAuditsAsync(channel));
    }

    // ------------------------------------------------------------------------------------ helpers (wish delete)

    private async Task<Channel> SeedWishDeleteChannelAsync(string name)
    {
        await using var db = CreateDbContext();
        var channel = new Channel { ChannelName = name, IsBotActive = true, TwitchChannelId = $"{name}-twitch" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    // The access the wish filter decides for a manager confirmed on the channel row's Twitch id.
    private static WishCaptureAccess AccessOf(Channel channel) => new(channel.Id, channel.TwitchChannelId, IdConfirmed: true);

    private WishService WishDeleteService(AppDbContext db) => new(
        db,
        new WishChangedPublisher(Substitute.For<IRedisPublisher>(), NullLogger<WishChangedPublisher>.Instance),
        new HandWoundTimeProvider(new DateTimeOffset(_now)),
        NullLogger<WishService>.Instance);

    // One due row of each category the pass reaches after the wish categories (4e, 5, 6).
    private async Task<LaterRetentionRows> SeedLaterRetentionCategoriesAsync(string wishChannelId)
    {
        long gapId;
        await using (var db = CreateDbContext())
        {
            var gap = Gap(wishChannelId, endedDaysAgo: RetentionPolicy.WishCaptureGap.TotalDays + 1);
            db.WishCaptureGaps.Add(gap);
            await db.SaveChangesAsync();
            gapId = gap.Id;
        }

        var deactivated = await SeedChannelAsync("wishdelgone", isBotActive: false, deactivatedAtUtc: ChannelCutoff.AddDays(-1));
        var sampleId = await SeedOldSystemLoadSampleAsync();
        return new LaterRetentionRows(gapId, deactivated.Id, sampleId);
    }

    private async Task AssertLaterRetentionCategoriesDoneAsync(LaterRetentionRows later, RetentionRunSummary summary)
    {
        Assert.Equal(1, summary.WishCaptureGapsDeleted);
        Assert.Equal((1, 0), (summary.Channels.Purged, summary.Channels.Failed));
        Assert.Equal(1, summary.SystemLoadRawSamplesDeleted);
        Assert.False(summary.SystemLoadRawSamplesFailed);
        Assert.False(summary.SystemLoadHourlyRowsFailed);
        await using var db = CreateDbContext();
        Assert.False(await db.WishCaptureGaps.AnyAsync(g => g.Id == later.GapId));
        Assert.False(await db.Channels.AnyAsync(c => c.Id == later.DeactivatedChannelId));
        Assert.False(await db.SystemLoadSamples.AnyAsync(s => s.Id == later.SystemLoadSampleId));
    }

    private async Task<int> CountWishDeleteAuditsAsync(Channel channel)
    {
        await using var db = CreateDbContext();
        return await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.ChannelName && a.Action == AuditActions.WishDelete);
    }

    private async Task<string> WishRowSnapshotAsync(long wishId)
    {
        await using var db = CreateDbContext();
        return JsonSerializer.Serialize(await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == wishId));
    }

    // Whether another transaction holds the wish row: NOWAIT fails at once with 55P03 instead of waiting.
    private async Task<bool> IsWishRowLockedAsync(long wishId)
    {
        await using var probe = CreateDbContext();
        await using var transaction = await probe.Database.BeginTransactionAsync();
        try
        {
            await probe.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Wishes" WHERE "Id" = {wishId} FOR UPDATE NOWAIT""");
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return true;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private async Task<string> WaitEventAsync(string applicationName)
    {
        await using var probe = CreateDbContext();
        return await probe.Database
            .SqlQuery<string>($"""SELECT wait_event AS "Value" FROM pg_stat_activity WHERE application_name = {applicationName} AND wait_event_type = 'Lock'""")
            .SingleAsync();
    }

    // A pass a failed test left running would work on the next test's rows; it is awaited before the test ends.
    private static async Task DrainAsync(Task? pass)
    {
        if (pass is null)
        {
            return;
        }

        try
        {
            await pass.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception)
        {
            // Cleanup only: the test has failed already, and its own exception is the one to report.
        }
    }

    private sealed record LaterRetentionRows(long GapId, string DeactivatedChannelId, long SystemLoadSampleId);

    /// <summary>
    /// The test-only barrier: an <c>AFTER</c> row trigger on one wish row that waits on an advisory lock the test holds
    /// in an open transaction. <see cref="ReleaseAsync"/> opens it; disposing opens it as well and removes trigger and
    /// function, so a failed test leaves nothing behind for the next one.
    /// </summary>
    private sealed class WishBarrier : IAsyncDisposable
    {
        private readonly DataRetentionServiceTests _suite;
        private readonly AppDbContext _holder;
        private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _held;
        private Task? _pass;

        private WishBarrier(DataRetentionServiceTests suite, AppDbContext holder)
        {
            _suite = suite;
            _holder = holder;
        }

        public static async Task<WishBarrier> InstallAsync(DataRetentionServiceTests suite, string operation, long wishId)
        {
            if (operation is not ("DELETE" or "UPDATE"))
            {
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "The barrier holds a DELETE or an UPDATE.");
            }

            await using (var db = suite.CreateDbContext())
            {
                // Identifiers and the operation from the test's own constants; the id goes in as a literal because a
                // trigger condition cannot take parameters.
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync(
                    $"""
                    DROP TRIGGER IF EXISTS {WishBarrierTrigger} ON "Wishes";
                    CREATE OR REPLACE FUNCTION {WishBarrierTrigger}() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN
                        PERFORM pg_advisory_xact_lock({WishBarrierKey});
                        RETURN NULL;
                    END $$;
                    CREATE TRIGGER {WishBarrierTrigger} AFTER {operation} ON "Wishes"
                    FOR EACH ROW WHEN (OLD."Id" = {wishId}) EXECUTE FUNCTION {WishBarrierTrigger}();
                    """);
#pragma warning restore EF1002
            }

            var barrier = new WishBarrier(suite, suite.CreateDbContext());
            barrier._held = await barrier._holder.Database.BeginTransactionAsync();
            await barrier._holder.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({WishBarrierKey})");
            return barrier;
        }

        /// <summary>The pass started behind this barrier; disposing waits for it, so it never outlives the test.</summary>
        public Task<T> Track<T>(Task<T> pass)
        {
            _pass = pass;
            return pass;
        }

        public async Task ReleaseAsync()
        {
            if (_held is not null)
            {
                await _held.RollbackAsync();
                await _held.DisposeAsync();
                _held = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await _holder.DisposeAsync();
            await DrainAsync(_pass);
            await using var db = _suite.CreateDbContext();
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync(
                $"""
                DROP TRIGGER IF EXISTS {WishBarrierTrigger} ON "Wishes";
                DROP FUNCTION IF EXISTS {WishBarrierTrigger}();
                """);
#pragma warning restore EF1002
        }
    }
}
