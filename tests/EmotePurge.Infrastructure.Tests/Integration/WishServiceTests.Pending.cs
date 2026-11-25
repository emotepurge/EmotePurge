using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using EmotePurge.Infrastructure.Tests.Fakes;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 7 (K6, plan #486 T6, contract 2.6): approve, reject and unlink on a captured wish, the K6 parts of
// adopt and PATCH, the order of view=open and who sees the redeemer data (2.4a). Every test runs against the migrated
// schema, so the placeholder CHECK of plan 2.8a guards every save. The races wait on Postgres (pg_stat_activity).
public partial class WishServiceTests
{
    // ------------------------------------------------------------------------------------ approve -> approved

    [Fact]
    public async Task Approve_ALinkWish_BecomesApproved_WithTheBodysAlias_AndAuditsOutcomeOnly()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var wishId = await SeedCapturedAsync(channel, emoteId, WishMatchKind.Link, w => w.DurationDays = 9);

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "catJAM", null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Null(result.ExtendedExpiresAtUtc);
        var row = await LoadWishAsync(wishId);
        Assert.Equal(
            (WishStatus.Approved, emoteId, "catJAM", "catJAM", 9, (DateTime?)Now, (string?)null, 1),
            (row.Status, row.SevenTvEmoteId, row.Alias, row.EmoteName, row.DurationDays, row.ApprovedAtUtc, row.StatusReason, row.DecisionVersion));
        Assert.Equal(Now, row.StatusChangedAtUtc);
        var details = await SingleAuditDetailsAsync(channel, AuditActions.WishApprove);
        Assert.Equal(["mergedIntoWishId", "outcome", "wishId"], details.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((wishId, "approved", JsonValueKind.Null),
            (details.GetProperty("wishId").GetInt64(), details.GetProperty("outcome").GetString(), details.GetProperty("mergedIntoWishId").ValueKind));
        AssertPublishedOnce(channel);
    }

    // Plan 2.8a: a name wish without an emote takes the body's id; for a caller confirmed on the row's Twitch id the
    // requested name pre-fills the alias, the emote name follows the alias, all three in the same update. The duration
    // of the body wins.
    [Fact]
    public async Task Approve_ANameWishWithoutAnEmote_TakesTheBodysId_AndTheRequestedNameAsAlias()
    {
        var twitchId = Guid.NewGuid().ToString("N")[..12];
        var channel = await SeedChannelAsync(twitchId: twitchId);
        var wishId = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.Name, w => w.RequestedName = "peepoHey");
        var emoteId = UlidId();

        var result = await Sut().ApproveAsync(
            channel.Name, wishId, new ApproveWishRequest(emoteId, null, null, 12), Actor, new WishCaptureAccess(channel.Id, twitchId, IdConfirmed: true));

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Approved, emoteId, "peepoHey", "peepoHey", 12), (row.Status, row.SevenTvEmoteId, row.Alias, row.EmoteName, row.DurationDays));
    }

    // Plan 2.4a: the requested name is redeemer data. Alias and emote name are shown to every manager, so a caller
    // not confirmed on the row's Twitch id must not get it copied there: without a name in the body the approval is
    // wish_alias_invalid and nothing changes; with one, the body's name is taken and the answer carries no redeemer data.
    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("other-id")]
    [InlineData("none")]
    public async Task Approve_ANameWish_ByACallerNotConfirmedOnTheId_NeverCopiesTheRequestedName(string access)
    {
        var twitchId = Guid.NewGuid().ToString("N")[..12];
        var channel = await SeedChannelAsync(twitchId: twitchId);
        var wishId = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.Name, w => w.RequestedName = "peepoHey");
        var emoteId = UlidId();
        WishCaptureAccess? grant = access switch
        {
            "unconfirmed" => new WishCaptureAccess(channel.Id, twitchId, IdConfirmed: false),
            "other-id" => new WishCaptureAccess(channel.Id, "other" + twitchId, IdConfirmed: true),
            _ => null,
        };
        var before = await SnapshotAsync(wishId);

        var refused = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(emoteId, null, null, null), Actor, grant);

        Assert.Equal(WishOutcome.AliasInvalid, refused.Outcome);
        await AssertUntouchedAsync(channel, wishId, before);

        var named = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(emoteId, null, "catJAM", null), Actor, grant);

        Assert.Equal(WishOutcome.Success, named.Outcome);
        Assert.Equal(("catJAM", "catJAM", (string?)null, (string?)null), (named.Wish!.Alias, named.Wish.EmoteName, named.Wish.RequestedName, named.Wish.RequestText));
        var row = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Approved, "catJAM", "catJAM"), (row.Status, row.Alias, row.EmoteName));
    }

    [Fact]
    public async Task Approve_ALinkWishWithoutAnyName_IsAliasInvalid_AndChangesNothing()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedCapturedAsync(channel, EmoteId(), WishMatchKind.Link);
        var before = await SnapshotAsync(wishId);

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, null, null), Actor);

        Assert.Equal(WishOutcome.AliasInvalid, result.Outcome);
        await AssertUntouchedAsync(channel, wishId, before);
    }

    [Fact]
    public async Task Approve_WithoutAnyEmote_IsEmoteRequired_AndChangesNothing()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.None);
        var before = await SnapshotAsync(wishId);

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "Alias", null), Actor);

        Assert.Equal(WishOutcome.EmoteRequired, result.Outcome);
        await AssertUntouchedAsync(channel, wishId, before);
    }

    [Theory]
    [InlineData("", null, null, WishOutcome.EmoteIdInvalid)]
    [InlineData("not-an-id!", null, null, WishOutcome.EmoteIdInvalid)]
    [InlineData(null, "has space", null, WishOutcome.AliasInvalid)]
    [InlineData(null, null, 0, WishOutcome.DurationOutOfRange)]
    [InlineData(null, null, 366, WishOutcome.DurationOutOfRange)]
    public async Task Approve_RefusesAMalformedBody(string? emoteId, string? alias, int? days, WishOutcome expected)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedCapturedAsync(channel, EmoteId(), WishMatchKind.Link);
        var before = await SnapshotAsync(wishId);

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(emoteId, null, alias, days), Actor);

        Assert.Equal(expected, result.Outcome);
        await AssertUntouchedAsync(channel, wishId, before);
    }

    // (2): the emote is in the set and no wish runs for it: the way out is "take over from the set".
    [Fact]
    public async Task Approve_OfAnEmoteInTheSetWithoutARunningWish_IsAlreadyInSet_WithTheSlots()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-3));
        var wishId = await SeedCapturedAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link);
        var before = await SnapshotAsync(wishId);

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "Kappa", null), Actor);

        Assert.Equal((WishOutcome.EmoteAlreadyInSet, (long?)null), (result.Outcome, result.ConflictingWishId));
        Assert.Equal([slot], result.SlotIds);
        await AssertUntouchedAsync(channel, wishId, before);
    }

    // ------------------------------------------------------------------------------------ approve -> merged

    // AK58: the running wish is extended exactly like ExtendAsync, the open one ends merged with the target's emote
    // fields (a name wish without an emote passes the CHECK), both audit entries carry their own details.
    [Fact]
    public async Task Approve_WithAnActiveRunningWish_MergesIntoIt_AndExtendsItByTheChannelDefault()
    {
        var channel = await SeedChannelAsync();
        await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 7, null), Actor);
        _publisher.ClearReceivedCalls();
        var emoteId = UlidId();
        var expires = Now.AddDays(5);
        var target = await SeedRowAsync(channel, WishStatus.Active, w => (w.SevenTvEmoteId, w.Alias, w.ExpiresAtUtc) = (emoteId, "RunningAlias", expires));
        var wishId = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.Name, w => w.RequestedName = "whatever");

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(emoteId, null, null, null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(expires.AddDays(14), result.ExtendedExpiresAtUtc);
        Assert.Equal((WishStatus.Merged, (long?)target), (result.Wish!.Status, result.Wish.MergedIntoWishId));
        var merged = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Merged, (long?)target, emoteId, "RunningAlias", "Emote"), (merged.Status, merged.MergedIntoWishId, merged.SevenTvEmoteId, merged.Alias, merged.EmoteName));
        Assert.Equal(((DateTime?)Now, Now, 1), (merged.EndedAtUtc, merged.StatusChangedAtUtc, merged.DecisionVersion));
        var running = await LoadWishAsync(target);
        Assert.Equal((WishStatus.Active, (DateTime?)expires.AddDays(14), 1, SeededDecisionVersion + 1), (running.Status, running.ExpiresAtUtc, running.ExtensionCount, running.DecisionVersion));
        Assert.Null(running.RemovalQueuedAtUtc);

        var approve = await SingleAuditDetailsAsync(channel, AuditActions.WishApprove);
        Assert.Equal((wishId, "merged", target), (approve.GetProperty("wishId").GetInt64(), approve.GetProperty("outcome").GetString(), approve.GetProperty("mergedIntoWishId").GetInt64()));
        var extend = await SingleAuditDetailsAsync(channel, AuditActions.WishExtend);
        Assert.Equal(["durationDays", "sevenTvEmoteId", "wishId"], extend.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((target, 14), (extend.GetProperty("wishId").GetInt64(), extend.GetProperty("durationDays").GetInt32()));
        AssertPublishedOnce(channel);
    }

    // The running wish is presented and overdue: the extension counts from now, returns it to active, and its
    // wish.extend records the expiry it replaced (contract 2.7).
    [Fact]
    public async Task Approve_WithAPresentedOverdueRunningWish_ReturnsItToActive_AndRecordsThePreviousExpiry()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var overdue = Now.AddDays(-1);
        var target = await SeedRowAsync(channel, WishStatus.Presented, w =>
            (w.SevenTvEmoteId, w.StatusReason, w.ExpiresAtUtc) = (emoteId, WishStatusReason.Expired, overdue));
        var wishId = await SeedCapturedAsync(channel, emoteId, WishMatchKind.Link);

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, null, null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var running = await LoadWishAsync(target);
        Assert.Equal((WishStatus.Active, (string?)null, (DateTime?)Now.AddDays(30)), (running.Status, running.StatusReason, running.ExpiresAtUtc));
        var extend = await SingleAuditDetailsAsync(channel, AuditActions.WishExtend);
        Assert.Equal(overdue, extend.GetProperty("previousExpiresAtUtc").GetDateTime());
    }

    // Sol-R2-P2-7: a running wish without an expiry cannot book an extension. Nothing is written, the open wish stays
    // decidable; once the running wish is in the set with an expiry, the same approval merges.
    [Theory]
    [InlineData(WishStatus.Approved)]
    [InlineData(WishStatus.Unconfirmed)]
    public async Task Approve_WithARunningWishWithoutExpiry_IsRunningNotExtendable_ThenMergesOnceItRuns(string runningStatus)
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var target = await SeedRowAsync(channel, runningStatus, w => w.SevenTvEmoteId = emoteId);
        var wishId = await SeedCapturedAsync(channel, emoteId, WishMatchKind.Link, w => w.StatusReason = WishStatusReason.AlreadyRunning);
        var before = await SnapshotAsync(wishId);
        var targetBefore = await SnapshotAsync(target);

        var refused = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "Alias", null), Actor);

        Assert.Equal((WishOutcome.RunningNotExtendable, (long?)target), (refused.Outcome, refused.ConflictingWishId));
        await AssertUntouchedAsync(channel, wishId, before);
        Assert.Equal(targetBefore, await SnapshotAsync(target));
        Assert.Equal(WishStatusReason.AlreadyRunning, (await LoadWishAsync(wishId)).StatusReason);

        await using (var db = fixture.CreateDbContext())
        {
            await db.Wishes.Where(w => w.Id == target).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, WishStatus.Active)
                .SetProperty(w => w.StartsAtUtc, Now.AddDays(-1))
                .SetProperty(w => w.ExpiresAtUtc, Now.AddDays(29)));
        }

        var merged = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "Alias", null), Actor);
        Assert.Equal((WishOutcome.Success, WishStatus.Merged), (merged.Outcome, merged.Wish!.Status));
        Assert.Equal(Now.AddDays(59), merged.ExtendedExpiresAtUtc);
    }

    // Sol-R2-P2-3: the hex ObjectID of an old link names the same emote as the running ULID.
    [Fact]
    public async Task Approve_WithTheHexFormOfARunningUlid_MergesIntoIt_InsteadOfASecondRunningWish()
    {
        var channel = await SeedChannelAsync();
        var target = await SeedRowAsync(channel, WishStatus.Active, w => w.SevenTvEmoteId = "01F6MZGCNG000255K4X1K7NTHR");
        var wishId = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.None);

        var result = await Sut().ApproveAsync(channel.Name, wishId, new ApproveWishRequest("60ae958e229664e8667aea38", null, "X", null), Actor);

        Assert.Equal((WishOutcome.Success, (long?)target), (result.Outcome, result.Wish!.MergedIntoWishId));
        Assert.Equal("01F6MZGCNG000255K4X1K7NTHR", (await LoadWishAsync(wishId)).SevenTvEmoteId);
    }

    // Both rows and both audit entries are one transaction: a failure at the audit write leaves no trace anywhere.
    [Fact]
    public async Task Approve_AsMerge_FailingAtTheAuditWrite_RollsBackBothWishes()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var target = await SeedRowAsync(channel, WishStatus.Active, w => w.SevenTvEmoteId = emoteId);
        var wishId = await SeedCapturedAsync(channel, emoteId, WishMatchKind.Link);
        var before = await SnapshotAsync(wishId);
        var targetBefore = await SnapshotAsync(target);
        var hook = new CommandHook(
            c => c.Contains("INSERT INTO \"AuditLogEntries\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException("forced failure at the audit write"));
        await using var failing = fixture.CreateTaggedDbContext("wish-approve-audit-fails", [hook]);

        await Assert.ThrowsAnyAsync<Exception>(() => Sut(failing).ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, null, null), Actor));

        await AssertUntouchedAsync(channel, wishId, before);
        Assert.Equal(targetBefore, await SnapshotAsync(target));
    }

    // ------------------------------------------------------------------------------------ approve: races

    // AK59: two approvals at once meet at the channel row; one decides, the other sees the decided wish.
    [Fact]
    public async Task Approve_TwoAtOnce_OneSucceeds_TheOtherIsAStateConflict()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedCapturedAsync(channel, EmoteId(), WishMatchKind.Link);
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var a = fixture.CreateTaggedDbContext("wish-approve-twice-a");
        await using var b = fixture.CreateTaggedDbContext("wish-approve-twice-b");
        var first = Sut(a).ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "First", null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-approve-twice-a", first);
        var second = Sut(b).ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "Second", null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-approve-twice-b", second);
        await holderTransaction.CommitAsync();

        var outcomes = (await Task.WhenAll(first, second)).Select(r => r.Outcome).Order().ToList();
        Assert.Equal([WishOutcome.Success, WishOutcome.StateConflict], outcomes);
        Assert.Equal(1, await CountAuditsAsync(channel, AuditActions.WishApprove));
    }

    // Sol-P2-9: an approval and an extension of the running wish at once, in both orders at its row lock. Both
    // extensions count: two defaults on, two extensions, two wish.extend.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approve_AndExtendOfTheRunningWishAtOnce_BothExtensionsCount(bool extendQueuesFirst)
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var expires = Now.AddDays(4);
        var target = await SeedRowAsync(channel, WishStatus.Active, w => (w.SevenTvEmoteId, w.ExpiresAtUtc) = (emoteId, expires));
        var wishId = await SeedCapturedAsync(channel, emoteId, WishMatchKind.Link);
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockWishesByIdsAsync([target], CancellationToken.None);

        await using var extendDb = fixture.CreateTaggedDbContext("wish-approve-race-extend");
        await using var approveDb = fixture.CreateTaggedDbContext("wish-approve-race-approve");
        Task<WishCommandResult> Extend() => Sut(extendDb).ExtendAsync(channel.Name, target, Actor);
        Task<WishCommandResult> Approve() => Sut(approveDb).ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, null, null), Actor);
        Task<WishCommandResult> extend, approve;
        if (extendQueuesFirst)
        {
            extend = Extend();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-race-extend", extend);
            approve = Approve();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-race-approve", approve);
        }
        else
        {
            approve = Approve();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-race-approve", approve);
            extend = Extend();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-race-extend", extend);
        }

        await holderTransaction.CommitAsync();
        Assert.Equal(WishOutcome.Success, (await extend).Outcome);
        Assert.Equal(WishOutcome.Success, (await approve).Outcome);

        var running = await LoadWishAsync(target);
        Assert.Equal(((DateTime?)expires.AddDays(60), 2, SeededDecisionVersion + 2), (running.ExpiresAtUtc, running.ExtensionCount, running.DecisionVersion));
        Assert.Equal(2, await CountAuditsAsync(channel, AuditActions.WishExtend));
        Assert.Equal(WishStatus.Merged, (await LoadWishAsync(wishId)).Status);
    }

    // The running wish ends between the read and the lock: the order starts over at (2) and the approval becomes an
    // ordinary approved wish (no entry in the set).
    [Fact]
    public async Task Approve_WhenTheRunningWishEndsWhileItWaits_BecomesAnOrdinaryApproval()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var target = await SeedRowAsync(channel, WishStatus.Active, w => w.SevenTvEmoteId = emoteId);
        var wishId = await SeedCapturedAsync(channel, emoteId, WishMatchKind.Link);
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockWishesByIdsAsync([target], CancellationToken.None);
        await holder.Wishes.Where(w => w.Id == target).ExecuteUpdateAsync(u => u
            .SetProperty(w => w.Status, WishStatus.Removed)
            .SetProperty(w => w.EndedAtUtc, Now));

        await using var contender = fixture.CreateTaggedDbContext("wish-approve-target-ends");
        var approve = Sut(contender).ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "Fresh", null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-approve-target-ends", approve);
        await holderTransaction.CommitAsync();
        var result = await approve;

        Assert.Equal((WishOutcome.Success, WishStatus.Approved), (result.Outcome, result.Wish!.Status));
        Assert.Null(result.ExtendedExpiresAtUtc);
        Assert.Equal(0, await CountAuditsAsync(channel, AuditActions.WishExtend));
    }

    // R7: the reconcile and the approval meet at the channel row. Reconcile first: the wish is active, the approval a
    // conflict. Approval first: the entry is in the set, so the approval is refused and the reconcile links.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approve_AgainstAConcurrentReconcile_TheChannelRowDecides(bool reconcileQueuesFirst)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedCapturedAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddHours(-1));
        var slot = await SeedSlotAsync(emote, "InTheSet", Now.AddMinutes(-5));
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var approveDb = fixture.CreateTaggedDbContext("wish-approve-vs-reconcile-a");
        Task Reconcile() => ReconcileSut(() => fixture.CreateTaggedDbContext("wish-approve-vs-reconcile-r")).ReconcileChannelAsync(channel.Id);
        Task<WishCommandResult> Approve() => Sut(approveDb).ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, "X", null), Actor);
        Task reconcile;
        Task<WishCommandResult> approve;
        if (reconcileQueuesFirst)
        {
            reconcile = Reconcile();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-vs-reconcile-r", reconcile);
            approve = Approve();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-vs-reconcile-a", approve);
        }
        else
        {
            approve = Approve();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-vs-reconcile-a", approve);
            reconcile = Reconcile();
            await fixture.WaitUntilBlockedOnLockAsync("wish-approve-vs-reconcile-r", reconcile);
        }

        await holderTransaction.CommitAsync();
        await reconcile;
        var result = await approve;

        Assert.Equal(reconcileQueuesFirst ? WishOutcome.StateConflict : WishOutcome.EmoteAlreadyInSet, result.Outcome);
        var row = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot), (row.Status, row.SlotId));
        Assert.Equal(0, await CountAuditsAsync(channel, AuditActions.WishApprove));
    }

    // ------------------------------------------------------------------------------------ reject

    [Fact]
    public async Task Reject_EndsTheOpenWish_KeepsTheEmoteFields_AndAudits()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.None);

        var result = await Sut().RejectAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Rejected, (DateTime?)Now, (string?)null, string.Empty), (row.Status, row.EndedAtUtc, row.StatusReason, row.SevenTvEmoteId));
        var details = await SingleAuditDetailsAsync(channel, AuditActions.WishReject);
        Assert.Equal(["durationDays", "sevenTvEmoteId", "wishId"], details.EnumerateObject().Select(p => p.Name).Order());
        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ unlink

    // AK61 and W9: back to pending in one update, the generation fields cleared, the first activation kept, a queued
    // removal dropped; a link wish keeps its id. The pair goes into the exclusion list, the audit names no alias.
    [Fact]
    public async Task Unlink_ReleasesTheAutoLink_ResetsTheFields_WritesTheExclusion_AndAudits()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddHours(-2));
        var wishId = await SeedAutoLinkedAsync(channel, emote, slot, WishMatchKind.Link);

        var result = await Sut().UnlinkAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Pending, (string?)null, Now, (string?)null), (row.Status, row.StatusReason, row.StatusChangedAtUtc, row.SlotId));
        Assert.Equal((emote.SevenTvEmoteId, string.Empty, string.Empty), (row.SevenTvEmoteId, row.EmoteName, row.Alias));
        Assert.Equal(((DateTime?)null, (DateTime?)null, (DateTime?)null, (DateTime?)null), (row.StartsAtUtc, row.ExpiresAtUtc, row.SlotAddedAtUtc, row.SlotConfirmedAtUtc));
        Assert.Equal(((string?)null, 0, (DateTime?)Now.AddHours(-1)), (row.LinkOrigin, row.ExtensionCount, row.FirstActivatedAtUtc));
        Assert.Equal(((DateTime?)null, (int?)null, SeededDecisionVersion + 1), (row.RemovalQueuedAtUtc, row.RemovalQueuedVersion, row.DecisionVersion));

        await using var db = fixture.CreateDbContext();
        Assert.Equal([slot], await db.WishSlotExclusions.Where(x => x.WishId == wishId).Select(x => x.SlotId).ToListAsync());
        var details = await SingleAuditDetailsAsync(channel, AuditActions.WishUnlink);
        Assert.Equal(["fromStatus", "slotId", "wishId"], details.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((wishId, WishStatus.Active, slot), (details.GetProperty("wishId").GetInt64(), details.GetProperty("fromStatus").GetString(), details.GetProperty("slotId").GetString()));
        AssertPublishedOnce(channel);

        // A second unlink finds pending: a conflict, the exclusion stays one row.
        Assert.Equal(WishOutcome.StateConflict, (await Sut().UnlinkAsync(channel.Name, wishId, Actor)).Outcome);
        Assert.Equal(1, await db.WishSlotExclusions.CountAsync(x => x.WishId == wishId));
    }

    [Fact]
    public async Task Unlink_OfANameWish_ClearsTheIdToo()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddHours(-2));
        var wishId = await SeedAutoLinkedAsync(channel, emote, slot, WishMatchKind.Name);

        Assert.Equal(WishOutcome.Success, (await Sut().UnlinkAsync(channel.Name, wishId, Actor)).Outcome);

        var row = await LoadWishAsync(wishId);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (row.SevenTvEmoteId, row.EmoteName, row.Alias));
    }

    // Only active with source irc and origin auto. presented with unconfirmed_start is the date correction's row of
    // the table, never the manager's.
    [Theory]
    [InlineData("adopted-origin")]
    [InlineData("manual-source")]
    [InlineData("presented-unconfirmed-start")]
    public async Task Unlink_RefusesEverythingButAnActiveAutoLinkedCapture(string variant)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddHours(-2));
        var wishId = await SeedAutoLinkedAsync(channel, emote, slot, WishMatchKind.Link, w =>
        {
            switch (variant)
            {
                case "adopted-origin":
                    w.LinkOrigin = WishLinkOrigin.Adopted;
                    break;
                case "manual-source":
                    w.Source = WishSource.Manual;
                    break;
                default:
                    (w.Status, w.StatusReason) = (WishStatus.Presented, WishStatusReason.UnconfirmedStart);
                    break;
            }
        });
        var before = await SnapshotAsync(wishId);

        Assert.Equal(WishOutcome.StateConflict, (await Sut().UnlinkAsync(channel.Name, wishId, Actor)).Outcome);

        await AssertUntouchedAsync(channel, wishId, before);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WishSlotExclusions.AnyAsync(x => x.WishId == wishId));
    }

    // AK61: after the commit the reconcile runs; another open wish of the emote takes the entry, the released one
    // never again.
    [Fact]
    public async Task Unlink_StartsTheReconcile_AndAnotherOpenWishOfTheEmoteTakesTheEntry()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddHours(-2));
        var released = await SeedAutoLinkedAsync(channel, emote, slot, WishMatchKind.Link);
        var other = await SeedCapturedAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddHours(-3));
        var trigger = new WishReconcileTrigger(
            ReconcileSut(fixture.CreateDbContext), new WishOptions { Enabled = true }, NullLogger<WishReconcileTrigger>.Instance);

        var result = await SutWith(trigger).UnlinkAsync(channel.Name, released, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal((WishStatus.Active, slot), ((await LoadWishAsync(other)).Status, (await LoadWishAsync(other)).SlotId));
        var after = await LoadWishAsync(released);
        Assert.Equal((WishStatus.Pending, (string?)null), (after.Status, after.SlotId));
    }

    // ------------------------------------------------------------------------------------ adopt and PATCH (K6)

    // The manager's pick lifts the exclusion of the pair: a name wish on an entry it was released from.
    [Fact]
    public async Task Adopt_OfAnOpenWishOnAnExcludedSlot_LinksIt_AndRemovesTheExclusion()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel, "catJAM");
        var slot = await SeedSlotAsync(emote, "catJAMslot", Now.AddHours(-1));
        var wishId = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.Name, w => w.RequestedName = "catJAMslot");
        await using (var db = fixture.CreateDbContext())
        {
            db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wishId, SlotId = slot, CreatedAtUtc = Now.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        var result = await AdoptAsync(channel, slot, wishId: wishId);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var row = await LoadWishAsync(wishId);
        Assert.Equal((WishStatus.Active, emote.SevenTvEmoteId, "catJAM", "catJAMslot", (DateTime?)null), (row.Status, row.SevenTvEmoteId, row.EmoteName, row.Alias, row.ApprovedAtUtc));
        await using var check = fixture.CreateDbContext();
        Assert.False(await check.WishSlotExclusions.AnyAsync(x => x.WishId == wishId));
    }

    // Plan 2.8a, also on the K1 path: an emote row without a name gives the wish the entry's name.
    [Fact]
    public async Task Adopt_FromAnEmoteRowWithoutAName_TakesTheSlotsName_ForANewAndAnOpenWish()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel, string.Empty);
        var first = await SeedSlotAsync(emote, "FirstSlot", Now.AddHours(-1));
        var second = await SeedSlotAsync(emote, "SecondSlot", Now.AddHours(-1));
        var open = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.None);

        var created = await AdoptAsync(channel, first);
        Assert.Equal(WishOutcome.Success, created.Outcome);
        Assert.Equal("FirstSlot", (await LoadWishAsync(created.Wish!.Id)).EmoteName);

        await using (var db = fixture.CreateDbContext())
        {
            await db.Wishes.Where(w => w.Id == created.Wish.Id).ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WishStatus.Kept).SetProperty(w => w.EndedAtUtc, Now));
        }

        Assert.Equal(WishOutcome.Success, (await AdoptAsync(channel, second, wishId: open)).Outcome);
        Assert.Equal(("SecondSlot", "SecondSlot"), ((await LoadWishAsync(open)).EmoteName, (await LoadWishAsync(open)).Alias));
    }

    // P3-12: without an emote an alias means nothing; the duration alone goes through. With an emote both do, and a
    // change of an open wish starts the reconcile.
    [Fact]
    public async Task Patch_OfAnOpenWish_TakesOnlyTheDurationWithoutAnEmote_AndReconcilesAfterwards()
    {
        var channel = await SeedChannelAsync();
        var without = await SeedCapturedAsync(channel, string.Empty, WishMatchKind.None);
        var with = await SeedCapturedAsync(channel, EmoteId(), WishMatchKind.Link);
        var before = await SnapshotAsync(without);
        var trigger = Substitute.For<IWishReconcileTrigger>();

        Assert.Equal(WishOutcome.StateConflict, (await SutWith(trigger).UpdateAsync(channel.Name, without, new UpdateWishRequest("Alias", null), Actor)).Outcome);
        Assert.Equal(before, await SnapshotAsync(without));
        await trigger.DidNotReceiveWithAnyArgs().TriggerAsync(default!, default);

        var duration = await SutWith(trigger).UpdateAsync(channel.Name, without, new UpdateWishRequest(null, 3), Actor);
        Assert.Equal((WishOutcome.Success, 3, string.Empty), (duration.Outcome, duration.Wish!.DurationDays, duration.Wish.Alias));
        var alias = await SutWith(trigger).UpdateAsync(channel.Name, with, new UpdateWishRequest("Picked", null), Actor);
        Assert.Equal((WishOutcome.Success, "Picked"), (alias.Outcome, alias.Wish!.Alias));
        await trigger.Received(2).TriggerAsync(channel.Id, Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------------------------ reads

    // W22: open wishes lead the open view, oldest first; the rest keeps the order of the other views.
    [Fact]
    public async Task List_Open_PutsTheOpenWishesFirst_OldestFirst()
    {
        var channel = await SeedChannelAsync();
        var failed = await SeedRowAsync(channel, WishStatus.Failed);
        var young = await SeedCapturedAsync(channel, EmoteId(), WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddMinutes(-1));
        var presented = await SeedRowAsync(channel, WishStatus.Presented, w => w.ExpiresAtUtc = Now.AddDays(1));
        var old = await SeedCapturedAsync(channel, EmoteId(), WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddDays(-2));

        var ids = (await ListAsync(channel, WishListView.Open)).Items.Select(w => w.Id).ToList();

        Assert.Equal([old, young, presented, failed], ids);
    }

    // Plan 2.4a: the redeemer data of a captured wish only for a caller confirmed on the row's current Twitch id; a
    // manual wish keeps its requester login (K1 data). The same rule for the list, the detail and a command answer.
    [Theory]
    [InlineData("confirmed", true)]
    [InlineData("unconfirmed", false)]
    [InlineData("other-id", false)]
    [InlineData("none", false)]
    public async Task RedeemerData_LeavesOnlyForACallerConfirmedOnTheRowsId(string access, bool shown)
    {
        var twitchId = Guid.NewGuid().ToString("N")[..12];
        var channel = await SeedChannelAsync(twitchId: twitchId);
        var rewardId = await SeedRewardAsync(channel, "Wunsch-Emote");
        var captured = await SeedCapturedAsync(channel, EmoteId(), WishMatchKind.Name, w => (w.RequestedName, w.RewardId) = ("peepoHey", rewardId));
        var manual = await SeedWishAsync(channel, EmoteId(), WishStatus.Approved);
        await using (var db = fixture.CreateDbContext())
        {
            await db.Wishes.Where(w => w.Id == manual).ExecuteUpdateAsync(u => u.SetProperty(w => w.RequesterLogin, "handviewer"));
        }

        WishCaptureAccess? grant = access switch
        {
            "confirmed" => new WishCaptureAccess(channel.Id, twitchId, IdConfirmed: true),
            "unconfirmed" => new WishCaptureAccess(channel.Id, twitchId, IdConfirmed: false),
            "other-id" => new WishCaptureAccess(channel.Id, "other" + twitchId, IdConfirmed: true),
            _ => null,
        };

        var listed = (await Sut().ListAsync(channel.Name, WishListView.Open, 1, 50, grant))!.Items;
        var detail = (await Sut().GetAsync(channel.Name, captured, grant))!;
        var patched = (await Sut().UpdateAsync(channel.Name, captured, new UpdateWishRequest(null, 5), Actor, grant)).Wish!;

        foreach (var dto in new[] { listed.Single(w => w.Id == captured), detail, patched })
        {
            Assert.Equal(WishMatchKind.Name, dto.MatchKind);
            if (shown)
            {
                Assert.Equal(("please", "peepoHey", "viewer", "Wunsch-Emote"), (dto.RequestText, dto.RequestedName, dto.RequesterLogin, dto.RewardLabel));
            }
            else
            {
                Assert.Equal(((string?)null, (string?)null, (string?)null, (string?)null), (dto.RequestText, dto.RequestedName, dto.RequesterLogin, dto.RewardLabel));
            }
        }

        Assert.Equal("handviewer", listed.Single(w => w.Id == manual).RequesterLogin);
    }

    // A row without a Twitch id is never confirmed; and the access names the row it was decided on: renamed in
    // between, the answer still comes from that row, not from the one that took the name.
    [Fact]
    public async Task RedeemerData_FollowTheCheckedRow_ThroughARename_AndNeverLeaveAnUnboundRow()
    {
        var unbound = await SeedChannelAsync();
        await UpdateChannelAsync(unbound, c => c.TwitchChannelId = null);
        var open = await SeedCapturedAsync(unbound, EmoteId(), WishMatchKind.Link);
        var unboundList = (await Sut().ListAsync(unbound.Name, WishListView.Open, 1, 50, new WishCaptureAccess(unbound.Id, null, false)))!;
        Assert.Null(unboundList.Items.Single(w => w.Id == open).RequestText);

        var checkedId = Guid.NewGuid().ToString("N")[..12];
        var checkedRow = await SeedChannelAsync(twitchId: checkedId);
        var wishId = await SeedCapturedAsync(checkedRow, EmoteId(), WishMatchKind.Link);
        var grant = new WishCaptureAccess(checkedRow.Id, checkedId, IdConfirmed: true);
        var oldName = checkedRow.Name;
        await UpdateChannelAsync(checkedRow, c => c.ChannelName = $"{oldName}x");
        await using (var db = fixture.CreateDbContext())
        {
            db.Channels.Add(new Channel { ChannelName = oldName, TwitchChannelId = Guid.NewGuid().ToString("N")[..12] });
            await db.SaveChangesAsync();
        }

        var listed = (await Sut().ListAsync(oldName, WishListView.Open, 1, 50, grant))!;
        Assert.Equal("please", Assert.Single(listed.Items).RequestText);
        Assert.Equal(wishId, listed.Items[0].Id);
        var rejected = await Sut().RejectAsync(oldName, wishId, Actor, grant);
        Assert.Equal((WishOutcome.Success, "please"), (rejected.Outcome, rejected.Wish!.RequestText));
    }

    // ------------------------------------------------------------------------------------ helpers

    // A 7TV id already in its canonical form (an upper-case ULID), for tests that send it in a body: EmoteId() is a
    // 24-character hex string, which the approval would canonicalize into another string.
    private static string UlidId() => "01" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();

    private WishService SutWith(IWishReconcileTrigger trigger, AppDbContext? db = null) =>
        new(
            db ?? fixture.CreateDbContext(),
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            NullLogger<WishService>.Instance, BotChannelStates.Unavailable(),
            trigger);

    // A wish the capture created: pending, source irc, the placeholder in name and alias, redeemer data set.
    private async Task<long> SeedCapturedAsync(Seeded channel, string sevenTvEmoteId, string matchKind, Action<Wish>? configure = null)
    {
        var wish = NewWish(channel.Id, sevenTvEmoteId, WishStatus.Pending, slotId: null);
        wish.Source = WishSource.Irc;
        wish.MatchKind = matchKind;
        wish.StatusReason = matchKind == WishMatchKind.None ? WishStatusReason.Unparsed : null;
        wish.EmoteName = string.Empty;
        wish.Alias = string.Empty;
        wish.CreatedAtUtc = Now.AddMinutes(-10);
        wish.StatusChangedAtUtc = Now.AddMinutes(-10);
        wish.RequesterTwitchUserId = "777";
        wish.RequesterLogin = "viewer";
        wish.RequestText = "please";
        configure?.Invoke(wish);
        await using var db = fixture.CreateDbContext();
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    // An active wish the reconcile linked from a capture, with an extension, a queue and its first activation.
    private Task<long> SeedAutoLinkedAsync(Seeded channel, Emote emote, string slotId, string matchKind, Action<Wish>? configure = null) =>
        SeedRowAsync(channel, WishStatus.Active, w =>
        {
            w.SevenTvEmoteId = emote.SevenTvEmoteId;
            w.Source = WishSource.Irc;
            w.MatchKind = matchKind;
            w.LinkOrigin = WishLinkOrigin.Auto;
            w.ApprovedAtUtc = null;
            w.SlotId = slotId;
            w.SlotAddedAtUtc = Now.AddHours(-2);
            w.SlotConfirmedAtUtc = Now.AddMinutes(-5);
            w.FirstActivatedAtUtc = Now.AddHours(-1);
            w.ExtensionCount = 1;
            w.RequesterTwitchUserId = "777";
            w.RequesterLogin = "viewer";
            w.RequestText = "please";
            configure?.Invoke(w);
        });

    private async Task<long> SeedRewardAsync(Seeded channel, string label)
    {
        await using var db = fixture.CreateDbContext();
        var reward = new ChannelWishReward { ChannelId = channel.Id, RewardId = Guid.NewGuid().ToString(), Label = label, CreatedAtUtc = Now };
        db.ChannelWishRewards.Add(reward);
        await db.SaveChangesAsync();
        return reward.Id;
    }

    private async Task<Wish> LoadWishAsync(long wishId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == wishId);
    }

    private async Task<JsonElement> SingleAuditDetailsAsync(Seeded channel, string action)
    {
        await using var db = fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.AsNoTracking().SingleAsync(a => a.ChannelName == channel.Name && a.Action == action);
        return JsonDocument.Parse(entry.DetailsJson!).RootElement.Clone();
    }

    private async Task<int> CountAuditsAsync(Seeded channel, string action)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name && a.Action == action);
    }

    private async Task AssertUntouchedAsync(Seeded channel, long wishId, string before)
    {
        Assert.Equal(before, await SnapshotAsync(wishId));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name && a.Action != AuditActions.WishSettings));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }
}
