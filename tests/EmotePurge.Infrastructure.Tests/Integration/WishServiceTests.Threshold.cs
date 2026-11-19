using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 4 (wish emotes K2, plan #484 T2b, contract 2.6): a change of the protection threshold is a
// decision. It locks the channel's active wishes after the channel row, bumps their DecisionVersion and drops a
// queued removal in the same commit as the settings. The lock races wait on Postgres itself (pg_stat_activity),
// never on a clock.
public partial class WishServiceTests
{
    private const int SeededVersion = 2;

    // Off to on, on to off, one value to another: every active wish is superseded, nothing else is.
    [Theory]
    [InlineData(null, "2.5")]
    [InlineData("2.5", null)]
    [InlineData("2.5", "3")]
    public async Task ThresholdChange_SupersedesTheActiveWishesOnly_InOneCommitWithOneAuditAndOneEvent(string? from, string? to)
    {
        var channel = await SeedChannelAsync();
        if (from is not null)
        {
            Assert.True((await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, decimal.Parse(from)), Actor)).IsSuccess);
        }

        var queued = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: true);
        var plain = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: false);
        var presented = await SeedDecidedWishAsync(channel, WishStatus.Presented, queued: true);
        var approved = await SeedDecidedWishAsync(channel, WishStatus.Approved, queued: false);
        var ended = await SeedDecidedWishAsync(channel, WishStatus.Removed, queued: false);
        var foreignChannel = await SeedChannelAsync();
        var foreign = await SeedDecidedWishAsync(foreignChannel, WishStatus.Active, queued: true);
        var untouched = new[] { presented, approved, ended, foreign };
        var before = await Task.WhenAll(untouched.Select(WishStateAsync));
        var auditsBefore = await CountSettingsAuditsAsync(channel);
        _publisher.ClearReceivedCalls();

        var result = await Sut().UpdateSettingsAsync(
            channel.Name, new UpdateWishSettingsRequest(30, 7, to is null ? null : decimal.Parse(to)), Actor);

        Assert.True(result.IsSuccess);
        foreach (var id in new[] { queued, plain })
        {
            var state = await WishStateAsync(id);
            Assert.Equal((WishStatus.Active, SeededVersion + 1, (DateTime?)null, (int?)null), (state.Status, state.DecisionVersion, state.QueuedAt, state.QueuedVersion));
            Assert.Equal(Now.AddHours(-1), state.StatusChangedAt);
        }

        Assert.Equal(before, await Task.WhenAll(untouched.Select(WishStateAsync)));
        Assert.Equal(auditsBefore + 1, await CountSettingsAuditsAsync(channel));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(1, await db.ChannelWishSettings.CountAsync(s => s.ChannelId == channel.Id));
            Assert.Equal(to is null ? null : decimal.Parse(to), (await db.ChannelWishSettings.SingleAsync(s => s.ChannelId == channel.Id)).ProtectionThresholdPerDay);
        }

        AssertPublishedOnce(channel);
    }

    // The wishes, the settings row and the audit entry commit together: an error after the wish update ran rolls
    // all of it back.
    [Fact]
    public async Task ThresholdChange_FailingAfterTheWishUpdate_RollsBackWishesSettingsAndAudit()
    {
        var channel = await SeedChannelAsync();
        Assert.True((await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, 1m), Actor)).IsSuccess);
        var wishId = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: true);
        var before = await WishStateAsync(wishId);
        _publisher.ClearReceivedCalls();
        var hook = new CommandHook(
            c => c.Contains("UPDATE \"Wishes\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException("forced failure after the wish update"));

        await using var failing = fixture.CreateTaggedDbContext("wish-threshold-rollback", [hook]);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Sut(failing).UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(10, 3, 5m), Actor));

        Assert.Equal(before, await WishStateAsync(wishId));
        await using var db = fixture.CreateDbContext();
        var settings = await db.ChannelWishSettings.SingleAsync(s => s.ChannelId == channel.Id);
        Assert.Equal((30, 7, (decimal?)1m), (settings.DefaultDurationDays, settings.ProbationLeadDays, settings.ProtectionThresholdPerDay));
        Assert.Equal(1, await CountSettingsAuditsAsync(channel));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // Only the duration or the lead changes (or the same threshold again, also after rounding, also "off" without
    // a settings row): no wish is locked or written.
    [Theory]
    [InlineData(null, null)]
    [InlineData("2.5", "2.5")]
    [InlineData("2.5", "2.504")]
    [InlineData("off-without-row", null)]
    public async Task UnchangedThreshold_LocksAndWritesNoWish(string? from, string? to)
    {
        var channel = await SeedChannelAsync();
        if (from is not null && from != "off-without-row")
        {
            Assert.True((await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, decimal.Parse(from)), Actor)).IsSuccess);
        }
        else if (from is null)
        {
            Assert.True((await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, null), Actor)).IsSuccess);
        }

        var wishId = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: true);
        var before = await WishStateAsync(wishId);
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);

        await using var tracked = fixture.CreateTaggedDbContext("wish-threshold-unchanged", [hook]);
        var result = await Sut(tracked).UpdateSettingsAsync(
            channel.Name, new UpdateWishSettingsRequest(12, 4, to is null ? null : decimal.Parse(to)), Actor);

        Assert.True(result.IsSuccess);
        Assert.Equal((12, 4), (result.Settings!.DefaultDurationDays, result.Settings.ProbationLeadDays));
        Assert.Equal(before, await WishStateAsync(wishId));
        Assert.Equal(0, hook.Count(c => c.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && c.Contains("FOR UPDATE", StringComparison.Ordinal)));
        Assert.Equal(0, hook.Count(c => c.Contains("UPDATE \"Wishes\"", StringComparison.Ordinal)));
    }

    // Plan #485 T0b (Sol-P2-4): a write that does not send the threshold keeps the stored one and is no decision:
    // no wish is locked, versioned or unqueued, whatever else the write changes. Also without a settings row.
    [Theory]
    [InlineData("2.5")]
    [InlineData("off-without-row")]
    public async Task SettingsWithoutAThreshold_KeepTheStoredOne_AndSupersedeNothing(string from)
    {
        var channel = await SeedChannelAsync();
        if (from != "off-without-row")
        {
            Assert.True((await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, decimal.Parse(from)), Actor)).IsSuccess);
        }

        var wishId = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: true);
        var before = await WishStateAsync(wishId);
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);

        await using var tracked = fixture.CreateTaggedDbContext("wish-threshold-kept", [hook]);
        var result = await Sut(tracked).UpdateSettingsAsync(
            channel.Name, new UpdateWishSettingsRequest(12, 4, null, KeepProtectionThreshold: true), Actor);

        Assert.True(result.IsSuccess);
        decimal? expected = from == "off-without-row" ? null : decimal.Parse(from);
        Assert.Equal((12, 4, expected), (result.Settings!.DefaultDurationDays, result.Settings.ProbationLeadDays, result.Settings.ProtectionThresholdPerDay));
        Assert.Equal(before, await WishStateAsync(wishId));
        Assert.Equal(0, hook.Count(c => c.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && c.Contains("FOR UPDATE", StringComparison.Ordinal)));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(expected, (await db.ChannelWishSettings.SingleAsync(s => s.ChannelId == channel.Id)).ProtectionThresholdPerDay);
    }

    // Keeping the threshold ignores whatever the request carries in its place (the route sends null then).
    [Fact]
    public async Task SettingsWithoutAThreshold_IgnoreTheValueField()
    {
        var channel = await SeedChannelAsync();
        Assert.True((await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, 2m), Actor)).IsSuccess);

        var result = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, -1m, KeepProtectionThreshold: true), Actor);

        Assert.Equal((WishOutcome.Success, (decimal?)2m), (result.Outcome, result.Settings!.ProtectionThresholdPerDay));
    }

    // A channel without an active wish: the change goes through as before, without a single wish lock.
    [Fact]
    public async Task ThresholdChange_WithoutActiveWishes_TakesNoWishLock()
    {
        var channel = await SeedChannelAsync();
        var approved = await SeedDecidedWishAsync(channel, WishStatus.Approved, queued: false);
        var before = await WishStateAsync(approved);
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);

        await using var tracked = fixture.CreateTaggedDbContext("wish-threshold-no-active", [hook]);
        var result = await Sut(tracked).UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, 2m), Actor);

        Assert.True(result.IsSuccess);
        Assert.Equal(2m, result.Settings!.ProtectionThresholdPerDay);
        Assert.Equal(before, await WishStateAsync(approved));
        Assert.Equal(0, hook.Count(c => c.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && c.Contains("FOR UPDATE", StringComparison.Ordinal)));
    }

    // Order A: the expiry run holds the wish row and queues the removal. The settings command passes the channel
    // row, blocks at the wish row (not the channel row) and, after the run committed, drops what it queued.
    [Fact]
    public async Task OrderA_ARunHoldingTheWishRow_MakesTheSettingsWaitThere_ThenItsQueueIsDropped()
    {
        const string tag = "wish-threshold-a-contender";
        var channel = await SeedChannelAsync();
        var wishId = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: false);

        await using var run = fixture.CreateTaggedDbContext("wish-threshold-a-run");
        await using var runTransaction = await run.Database.BeginTransactionAsync();
        Assert.Single(await run.LockWishesByIdsAsync([wishId], CancellationToken.None));
        var held = await run.Wishes.SingleAsync(w => w.Id == wishId);
        held.RemovalQueuedAtUtc = Now;
        held.RemovalQueuedVersion = held.DecisionVersion;
        await run.SaveChangesAsync();

        await using var contender = fixture.CreateTaggedDbContext(tag);
        var waiting = Sut(contender).UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, 4m), Actor);
        await fixture.WaitUntilBlockedOnLockAsync(tag, waiting);

        var query = await fixture.WaitingQueryAsync(tag);
        Assert.Contains("\"Wishes\"", query);
        Assert.DoesNotContain("\"Channels\"", query);
        await AssertNoSettingsOrAuditAsync(channel);

        await runTransaction.CommitAsync();
        Assert.True((await waiting).IsSuccess);

        var state = await WishStateAsync(wishId);
        Assert.Equal((SeededVersion + 1, (DateTime?)null, (int?)null), (state.DecisionVersion, state.QueuedAt, state.QueuedVersion));
    }

    // Order B: a writer holds the channel row. The settings command waits there and holds no wish row yet, so a
    // third connection locks the wish with NOWAIT, queues a removal and commits; once the channel is free the
    // command reads behind its own wish lock and drops that queue. Proves "channel before wish".
    [Fact]
    public async Task OrderB_AHeldChannelRow_MakesTheSettingsWaitBeforeAnyWishLock()
    {
        const string tag = "wish-threshold-b-contender";
        var channel = await SeedChannelAsync();
        var wishId = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: false);

        await using var holder = fixture.CreateTaggedDbContext("wish-threshold-b-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        Assert.Single(await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None));

        await using var contender = fixture.CreateTaggedDbContext(tag);
        var waiting = Sut(contender).UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, 4m), Actor);
        await fixture.WaitUntilBlockedOnLockAsync(tag, waiting);
        Assert.Contains("\"Channels\"", await fixture.WaitingQueryAsync(tag));

        await using (var third = fixture.CreateDbContext())
        {
            await using var thirdTransaction = await third.Database.BeginTransactionAsync();
            // NOWAIT fails at once (55P03) if anyone holds the row: the command must not.
            Assert.Equal(
                [wishId],
                await third.Database.SqlQuery<long>($"""SELECT "Id" AS "Value" FROM "Wishes" WHERE "Id" = {wishId} FOR UPDATE NOWAIT""").ToListAsync());
            await third.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.RemovalQueuedAtUtc, Now)
                .SetProperty(w => w.RemovalQueuedVersion, SeededVersion));
            await thirdTransaction.CommitAsync();
        }

        Assert.False(waiting.IsCompleted);
        await holderTransaction.CommitAsync();
        Assert.True((await waiting).IsSuccess);

        var state = await WishStateAsync(wishId);
        Assert.Equal((SeededVersion + 1, (DateTime?)null, (int?)null), (state.DecisionVersion, state.QueuedAt, state.QueuedVersion));
    }

    // The reconcile also takes the channel row before the wish rows: whichever holds its wish locks first, the
    // other waits at the channel row, and both end without a deadlock (40P01).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThresholdChangeAgainstTheReconcile_EitherOrder_BothWaitAtTheChannelRow_AndNoneDeadlocks(bool reconcileFirst)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedDecidedWishAsync(channel, WishStatus.Active, queued: true);
        var request = new UpdateWishSettingsRequest(30, 7, 3m);

        static bool IsWishLock(string c) => c.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && c.Contains("FOR UPDATE", StringComparison.Ordinal);

        string? waitedAt = null;
        if (reconcileFirst)
        {
            Task<WishCommandResult>? settings = null;
            var hook = new CommandHook(IsWishLock, async _ =>
            {
                settings = Sut(fixture.CreateTaggedDbContext("wish-threshold-settings-waits")).UpdateSettingsAsync(channel.Name, request, Actor);
                await fixture.WaitUntilBlockedOnLockAsync("wish-threshold-settings-waits", settings);
                waitedAt = await fixture.WaitingQueryAsync("wish-threshold-settings-waits");
            });

            await ReconcileSut(() => fixture.CreateTaggedDbContext("wish-threshold-reconcile-first", [hook])).ReconcileChannelAsync(channel.Id);
            Assert.True((await settings!).IsSuccess);
        }
        else
        {
            Task? reconcile = null;
            var hook = new CommandHook(IsWishLock, async _ =>
            {
                reconcile = ReconcileSut(() => fixture.CreateTaggedDbContext("wish-threshold-reconcile-waits")).ReconcileChannelAsync(channel.Id);
                await fixture.WaitUntilBlockedOnLockAsync("wish-threshold-reconcile-waits", reconcile);
                waitedAt = await fixture.WaitingQueryAsync("wish-threshold-reconcile-waits");
            });

            await using var first = fixture.CreateTaggedDbContext("wish-threshold-settings-first", [hook]);
            Assert.True((await Sut(first).UpdateSettingsAsync(channel.Name, request, Actor)).IsSuccess);
            await reconcile!;
        }

        Assert.Contains("\"Channels\"", waitedAt);
        var state = await WishStateAsync(wishId);
        Assert.Equal((SeededVersion + 1, (DateTime?)null), (state.DecisionVersion, state.QueuedAt));
    }

    // ------------------------------------------------------------------------------------ helpers (threshold)

    private WishReconcileService ReconcileSut(Func<AppDbContext> contextFactory)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => contextFactory());
        var provider = services.BuildServiceProvider();
        return new WishReconcileService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            NullLogger<WishReconcileService>.Instance);
    }

    // A wish with an earlier decision (DecisionVersion 2), its state set an hour ago and, when asked, a removal
    // queued at that version.
    private async Task<long> SeedDecidedWishAsync(Seeded channel, string status, bool queued)
    {
        await using var db = fixture.CreateDbContext();
        var wish = NewWish(channel.Id, EmoteId(), status, slotId: null);
        wish.StatusChangedAtUtc = Now.AddHours(-1);
        wish.DecisionVersion = SeededVersion;
        wish.ExpiresAtUtc = Now.AddDays(-1);
        wish.EndedAtUtc = WishStatus.IsEnded(status) ? Now.AddHours(-1) : null;
        if (queued)
        {
            wish.RemovalQueuedAtUtc = Now.AddMinutes(-30);
            wish.RemovalQueuedVersion = SeededVersion;
        }

        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    private async Task<WishState> WishStateAsync(long wishId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Wishes.AsNoTracking()
            .Where(w => w.Id == wishId)
            .Select(w => new WishState(w.Status, w.DecisionVersion, w.RemovalQueuedAtUtc, w.RemovalQueuedVersion, w.StatusChangedAtUtc, w.ExpiresAtUtc))
            .SingleAsync();
    }

    private async Task<int> CountSettingsAuditsAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishSettings);
    }

    private async Task AssertNoSettingsOrAuditAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.ChannelWishSettings.AnyAsync(s => s.ChannelId == channel.Id));
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
    }

    private sealed record WishState(
        string Status, int DecisionVersion, DateTime? QueuedAt, int? QueuedVersion, DateTime StatusChangedAt, DateTime? ExpiresAt);
}
