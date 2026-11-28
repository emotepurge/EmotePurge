using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 2 (wish emotes, T5): the transition commands on an existing wish against real Postgres.
// The matrix below is written out independently of WishTransitions: every (state, command) pair it does not
// list must be refused with a state conflict and leave the row, the audit log and the live stream untouched.
public partial class WishServiceTests
{
    private const int SeededDecisionVersion = 3;

    private static readonly string[] Commands =
        ["patch", "extend", "keep", "close", "retry", "readd", "complete-removal", "recheck", "bot-remove", "approve", "reject", "unlink"];

    // Refusals other than the state conflict, in the manual channel every row of this matrix lives in (#487 T7): the one
    // state recheck accepts needs the bot to act for the channel. Nothing is written either way.
    private static readonly Dictionary<(string State, string Command), WishOutcome> RefusedOtherwise = new()
    {
        [("presented", "recheck")] = WishOutcome.BotUnavailable,
    };

    // (state, command) -> state after success. Seeded rows have no slot, except for complete-removal, which runs on
    // a row bound to a slot generation and reports exactly that (plan #485 2.1a); presented carries reason
    // "unconfirmed" (the one reason recheck would need); removed_externally ended one hour ago.
    private static readonly Dictionary<(string State, string Command), string> Allowed = new()
    {
        [("pending", "patch")] = "pending",
        [("approved", "patch")] = "approved",
        [("failed", "patch")] = "failed",
        [("active", "extend")] = "active",
        [("presented", "extend")] = "active",
        [("active", "keep")] = "kept",
        [("presented", "keep")] = "kept",
        [("approved", "close")] = "closed",
        [("failed", "close")] = "closed",
        [("presented", "close")] = "closed",
        [("failed", "retry")] = "approved",
        [("removed", "readd")] = "approved",
        [("removed_externally", "readd")] = "approved",
        [("active", "complete-removal")] = "removed",
        [("presented", "complete-removal")] = "removed",
        [("removed_externally", "complete-removal")] = "removed",
        // K6 (plan #486 2.6). unlink runs on a row seeded as an automatic capture link to a slot.
        [("pending", "approve")] = "approved",
        [("pending", "reject")] = "rejected",
        [("active", "unlink")] = "pending",
    };

    private static readonly Dictionary<string, string> AuditActionOf = new()
    {
        ["patch"] = "wish.update",
        ["extend"] = "wish.extend",
        ["keep"] = "wish.keep",
        ["close"] = "wish.close",
        ["retry"] = "wish.retry",
        ["readd"] = "wish.readd",
        ["complete-removal"] = "wish.remove",
        ["approve"] = "wish.approve",
        ["reject"] = "wish.reject",
        ["unlink"] = "wish.unlink",
    };

    // Succeed without any trace: a repeated removal report of a wish already removed (row 5 of plan #485 2.1a).
    private static readonly HashSet<(string State, string Command)> SucceedWithoutTrace = [("removed", "complete-removal")];

    public static TheoryData<string, string> StateCommandPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var state in new[]
                 {
                     "pending", "approved", "unconfirmed", "failed", "active", "presented", "rejected", "expired",
                     "removed", "removed_externally", "kept", "closed", "merged",
                 })
        {
            foreach (var command in Commands)
            {
                data.Add(state, command);
            }
        }

        return data;
    }

    // AK2 over the service layer, E25 on every success.
    [Theory]
    [MemberData(nameof(StateCommandPairs))]
    public async Task Matrix_EveryStateAndCommand_SucceedsExactlyWhereListed_ElseConflictsWithoutATrace(string state, string command)
    {
        var channel = await SeedChannelAsync();
        var wishId = command switch
        {
            "complete-removal" => (await SeedBoundRowAsync(channel, state)).WishId,
            "unlink" => await SeedCaptureLinkedRowAsync(channel, state),
            _ => await SeedRowAsync(channel, state),
        };
        var before = await SnapshotAsync(wishId);

        var result = await RunAsync(command, channel, wishId);

        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == wishId);
        var audit = await db.AuditLogEntries.Where(a => a.ChannelName == channel.Name).ToListAsync();
        if (SucceedWithoutTrace.Contains((state, command)))
        {
            Assert.Equal(WishOutcome.Success, result.Outcome);
            Assert.Equal(state, result.Wish!.Status);
            Assert.Equal(before, JsonSerializer.Serialize(row));
            Assert.Empty(audit);
            _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
        }
        else if (Allowed.TryGetValue((state, command), out var target))
        {
            Assert.Equal(WishOutcome.Success, result.Outcome);
            Assert.Equal(target, row.Status);
            Assert.Equal(target, result.Wish!.Status);
            Assert.Equal(SeededDecisionVersion + 1, row.DecisionVersion);
            Assert.Null(row.RemovalQueuedAtUtc);
            Assert.Null(row.RemovalQueuedVersion);
            Assert.Equal(AuditActionOf[command], Assert.Single(audit).Action);
            Assert.Equal(WishStatus.IsEnded(target), row.EndedAtUtc is not null);
            if (WishStatus.IsEnded(state) && WishStatus.IsEnded(target))
            {
                // From one end state into another the retention clock stays (plan #485 2.1a).
                Assert.Equal(Now.AddHours(-1), row.EndedAtUtc);
            }

            if (target != state)
            {
                Assert.Equal(Now, row.StatusChangedAtUtc);
                Assert.Null(row.StatusReason);
            }
            else
            {
                // PATCH in approved/failed/pending, extend on active: no state change, so the clock of the state stays.
                Assert.Equal(Now.AddDays(-1), row.StatusChangedAtUtc);
            }

            AssertPublishedOnce(channel);
        }
        else
        {
            Assert.Equal(RefusedOtherwise.GetValueOrDefault((state, command), WishOutcome.StateConflict), result.Outcome);
            Assert.Null(result.Wish);
            Assert.Equal(before, JsonSerializer.Serialize(row));
            Assert.Empty(audit);
            _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
        }
    }

    [Fact]
    public async Task Close_OfAPresentedWishWithASlot_IsAConflict_WithoutOne_Succeeds()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-3));
        var linked = await SeedRowAsync(channel, WishStatus.Presented, w => w.SlotId = slot);
        var before = await SnapshotAsync(linked);

        Assert.Equal(WishOutcome.StateConflict, (await Sut().CloseAsync(channel.Name, linked, Actor)).Outcome);
        Assert.Equal(before, await SnapshotAsync(linked));

        var unlinked = await SeedRowAsync(channel, WishStatus.Presented);
        Assert.Equal(WishOutcome.Success, (await Sut().CloseAsync(channel.Name, unlinked, Actor)).Outcome);
    }

    [Fact]
    public async Task Commands_OnAWishOfAnotherChannel_OrAnUnknownOne_AreWishNotFound_AnUnknownChannelIsChannelNotFound()
    {
        var channel = await SeedChannelAsync();
        var other = await SeedChannelAsync();
        var foreign = await SeedRowAsync(other, WishStatus.Active);
        var before = await SnapshotAsync(foreign);

        foreach (var command in Commands)
        {
            Assert.Equal(WishOutcome.WishNotFound, (await RunAsync(command, channel, foreign)).Outcome);
            Assert.Equal(WishOutcome.WishNotFound, (await RunAsync(command, channel, 987654321)).Outcome);
            Assert.Equal(WishOutcome.ChannelNotFound, (await RunAsync(command, new Seeded("x", "no_such_channel_x"), foreign)).Outcome);
        }

        Assert.Equal(before, await SnapshotAsync(foreign));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name || a.ChannelName == other.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // ------------------------------------------------------------------------------------ extend

    [Fact]
    public async Task Extend_CountsFromTheCurrentExpiry_ByTheChannelDefault_AndRecordsTheExtensionLength()
    {
        var channel = await SeedChannelAsync();
        await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(14, 7, null), Actor);
        var expires = Now.AddDays(3);
        var wishId = await SeedRowAsync(channel, WishStatus.Active, w => w.ExpiresAtUtc = expires);

        var result = await Sut().ExtendAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        var dto = result.Wish!;
        Assert.Equal(expires.AddDays(14), dto.ExpiresAtUtc);
        Assert.Equal((1, 30), (dto.ExtensionCount, dto.DurationDays));

        await using var db = fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishExtend);
        using var json = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal(14, json.RootElement.GetProperty("durationDays").GetInt32());
    }

    [Fact]
    public async Task Extend_APresentedWishPastItsExpiry_CountsFromNow_AndReturnsToActiveWithoutReason()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.Expired;
            w.ExpiresAtUtc = Now.AddDays(-2);
            w.ExtensionCount = 2;
        });

        var result = await Sut().ExtendAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal((WishStatus.Active, (string?)null), (result.Wish!.Status, result.Wish.StatusReason));
        Assert.Equal(Now.AddDays(30), result.Wish.ExpiresAtUtc);
        Assert.Equal(3, result.Wish.ExtensionCount);
        Assert.Null(result.Wish.EndedAtUtc);
    }

    // A wish without an expiry (e.g. presented while unconfirmed or already in the set) is not extended into an
    // active wish without start and slot; its duration changes through PATCH (spec 5).
    [Theory]
    [InlineData(WishStatus.Presented, WishStatusReason.Unconfirmed)]
    [InlineData(WishStatus.Presented, WishStatusReason.AlreadyInSet)]
    [InlineData(WishStatus.Active, null)]
    public async Task Extend_AWishWithoutExpiry_IsAConflict_AndLeavesNoTrace(string status, string? reason)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, status, w =>
        {
            w.StatusReason = reason;
            w.StartsAtUtc = null;
            w.ExpiresAtUtc = null;
        });
        var before = await SnapshotAsync(wishId);

        var result = await Sut().ExtendAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(wishId));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // AK6: manual entry for a running emote → conflict with its id; while it is approved, extend conflicts and
    // PATCH changes the duration.
    [Fact]
    public async Task AK6_ARunningApprovedWish_CannotBeExtended_ButItsDurationIsPatched()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var first = await CreateAsync(channel, emoteId, "One");
        var running = first.Wish!.Id;

        var again = await CreateAsync(channel, emoteId, "Two");
        Assert.Equal((WishOutcome.AlreadyRunning, (long?)running), (again.Outcome, again.ConflictingWishId));

        var extend = await Sut().ExtendAsync(channel.Name, running, Actor);
        Assert.Equal(WishOutcome.StateConflict, extend.Outcome);

        var patch = await Sut().UpdateAsync(channel.Name, running, new UpdateWishRequest(null, 60), Actor);
        Assert.Equal(WishOutcome.Success, patch.Outcome);
        Assert.Equal((60, "One"), (patch.Wish!.DurationDays, patch.Wish.Alias));
        Assert.Null(patch.Wish.ExpiresAtUtc);
    }

    // ------------------------------------------------------------------------------------ patch

    [Theory]
    [InlineData(null, 0, WishOutcome.DurationOutOfRange)]
    [InlineData(null, 366, WishOutcome.DurationOutOfRange)]
    [InlineData("has space", null, WishOutcome.AliasInvalid)]
    [InlineData("", null, WishOutcome.AliasInvalid)]
    public async Task Patch_RejectsInvalidValues_AndWritesNothing(string? alias, int? days, WishOutcome expected)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Approved);
        var before = await SnapshotAsync(wishId);

        var result = await Sut().UpdateAsync(channel.Name, wishId, new UpdateWishRequest(alias, days), Actor);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(wishId));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
    }

    [Fact]
    public async Task Patch_ChangesTheAliasOfAFailedWish_AndKeepsItsReason()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Failed, w => w.StatusReason = WishStatusReason.AliasConflict);

        var result = await Sut().UpdateAsync(channel.Name, wishId, new UpdateWishRequest(" Renamed ", null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(("Renamed", 30, WishStatus.Failed, WishStatusReason.AliasConflict),
            (result.Wish!.Alias, result.Wish.DurationDays, result.Wish.Status, result.Wish.StatusReason));
    }

    // ------------------------------------------------------------------------------------ keep, retry

    [Fact]
    public async Task Keep_EndsBotOwnership_AndStartsTheRetentionClock()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Active, w =>
        {
            w.BotOwned = true;
            w.BotOwnedAddedAtUtc = Now.AddDays(-5);
        });

        var result = await Sut().KeepAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.SingleAsync(w => w.Id == wishId);
        Assert.Equal((WishStatus.Kept, false, (DateTime?)null, (DateTime?)Now), (row.Status, row.BotOwned, row.BotOwnedAddedAtUtc, row.EndedAtUtc));
    }

    [Fact]
    public async Task Retry_IsANewApproval_AndClearsTheFailureReason()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Failed, w =>
        {
            w.StatusReason = WishStatusReason.SetFull;
            w.ApprovedAtUtc = Now.AddDays(-4);
        });

        var result = await Sut().RetryAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal((WishStatus.Approved, (string?)null, (DateTime?)Now), (result.Wish!.Status, result.Wish.StatusReason, result.Wish.ApprovedAtUtc));
    }

    // ------------------------------------------------------------------------------------ re-add

    [Fact]
    public async Task ReAdd_Timed_WithoutADuration_TakesTheChannelDefault_ClearsTheRun_AndKeepsTheHistory()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-20), archived: true);
        await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(21, 7, null), Actor);
        var firstActivated = Now.AddDays(-20);
        var wishId = await SeedRowAsync(channel, WishStatus.RemovedExternally, w =>
        {
            w.SevenTvEmoteId = emote.SevenTvEmoteId;
            w.SlotId = slot;
            w.LinkOrigin = WishLinkOrigin.Auto;
            w.StartsAtUtc = Now.AddDays(-20);
            w.StartsAtAuthoritative = true;
            w.ExpiresAtUtc = Now.AddDays(10);
            w.ExtensionCount = 2;
            w.SlotAddedAtUtc = Now.AddDays(-20);
            w.SlotConfirmedAtUtc = Now.AddHours(-2);
            w.FirstActivatedAtUtc = firstActivated;
            w.Permanent = true;
        });

        var result = await Sut().ReAddAsync(channel.Name, wishId, new ReAddWishRequest("timed", null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await using var db = fixture.CreateDbContext();
        var row = await db.Wishes.SingleAsync(w => w.Id == wishId);
        Assert.Equal((WishStatus.Approved, 21, false, (DateTime?)Now), (row.Status, row.DurationDays, row.Permanent, row.ApprovedAtUtc));
        Assert.Null(row.StartsAtUtc);
        Assert.False(row.StartsAtAuthoritative);
        Assert.Null(row.ExpiresAtUtc);
        Assert.Null(row.SlotAddedAtUtc);
        Assert.Null(row.SlotConfirmedAtUtc);
        Assert.Null(row.EndedAtUtc);
        Assert.Equal(0, row.ExtensionCount);
        // The slot and the first activation are history and stay; the link origin goes with the old link (#487 plan 2.1
        // point 2), the next link sets it again.
        Assert.Equal((slot, (DateTime?)firstActivated, (string?)null), (row.SlotId, row.FirstActivatedAtUtc, row.LinkOrigin));
    }

    // AK12, command part: permanent → approved with Permanent; the reconcile half is T7's.
    [Fact]
    public async Task ReAdd_Permanent_IsApprovedWithPermanent_AndKeepsTheDuration()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Removed, w => w.DurationDays = 45);

        var result = await Sut().ReAddAsync(channel.Name, wishId, new ReAddWishRequest("permanent", null), Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal((WishStatus.Approved, true, 45), (result.Wish!.Status, result.Wish.Permanent, result.Wish.DurationDays));

        var timed = await SeedRowAsync(channel, WishStatus.Removed);
        var withDuration = await Sut().ReAddAsync(channel.Name, timed, new ReAddWishRequest("timed", 9), Actor);
        Assert.Equal((false, 9), (withDuration.Wish!.Permanent, withDuration.Wish.DurationDays));
    }

    [Theory]
    [InlineData(null, null, WishOutcome.ModeInvalid)]
    [InlineData("Timed", null, WishOutcome.ModeInvalid)]
    [InlineData("forever", null, WishOutcome.ModeInvalid)]
    [InlineData("timed", 0, WishOutcome.DurationOutOfRange)]
    [InlineData("permanent", 366, WishOutcome.DurationOutOfRange)]
    public async Task ReAdd_RejectsAnInvalidModeOrDuration_AndWritesNothing(string? mode, int? days, WishOutcome expected)
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Removed);
        var before = await SnapshotAsync(wishId);

        var result = await Sut().ReAddAsync(channel.Name, wishId, new ReAddWishRequest(mode, days), Actor);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(before, await SnapshotAsync(wishId));
    }

    [Fact]
    public async Task ReAdd_WhileAnotherWishForTheEmoteRuns_IsAlreadyRunning_WithItsId()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var removed = await SeedRowAsync(channel, WishStatus.Removed, w => w.SevenTvEmoteId = emoteId);
        var running = await SeedRowAsync(channel, WishStatus.Active, w => w.SevenTvEmoteId = emoteId);
        var before = await SnapshotAsync(removed);

        var result = await Sut().ReAddAsync(channel.Name, removed, new ReAddWishRequest("timed", null), Actor);

        Assert.Equal((WishOutcome.AlreadyRunning, (long?)running), (result.Outcome, result.ConflictingWishId));
        Assert.Equal(before, await SnapshotAsync(removed));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // The belt: a manual entry committed without our row lock. The re-add's update waits on the running index,
    // then fails on it, and the failure is the conflict outcome with the winner's id.
    [Fact]
    public async Task ReAdd_LosingARaceAtTheRunningIndex_IsAlreadyRunning_WithTheWinnersId()
    {
        var channel = await SeedChannelAsync();
        var emoteId = EmoteId();
        var removed = await SeedRowAsync(channel, WishStatus.Removed, w => w.SevenTvEmoteId = emoteId);
        var before = await SnapshotAsync(removed);
        await using var holder = fixture.CreateTaggedDbContext("wish-readd-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        var winner = NewWish(channel.Id, emoteId, WishStatus.Approved, slotId: null);
        holder.Wishes.Add(winner);
        await holder.SaveChangesAsync();

        await using var contender = fixture.CreateTaggedDbContext("wish-readd-contender");
        var waiting = Sut(contender).ReAddAsync(channel.Name, removed, new ReAddWishRequest("timed", null), Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-readd-contender", waiting);

        await holderTransaction.CommitAsync();
        var result = await waiting;

        Assert.Equal((WishOutcome.AlreadyRunning, (long?)winner.Id), (result.Outcome, result.ConflictingWishId));
        Assert.Equal(before, await SnapshotAsync(removed));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // ------------------------------------------------------------------------------------ concurrency

    // AK3: two keeps at once on separate contexts: exactly one wins, one audit entry.
    [Fact]
    public async Task AK3_TwoConcurrentKeeps_ExactlyOneSucceeds_WithOneAuditEntry()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Active);
        await using var first = fixture.CreateDbContext();
        await using var second = fixture.CreateDbContext();

        var results = await Task.WhenAll(
            Sut(first).KeepAsync(channel.Name, wishId, Actor),
            Sut(second).KeepAsync(channel.Name, wishId, Actor));

        Assert.Single(results, r => r.Outcome == WishOutcome.Success);
        Assert.Single(results, r => r.Outcome == WishOutcome.StateConflict);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name));
        var row = await db.Wishes.SingleAsync(w => w.Id == wishId);
        Assert.Equal((WishStatus.Kept, SeededDecisionVersion + 1), (row.Status, row.DecisionVersion));
    }

    // Sol-P2-2, the command half: a command that waits on the wish row lock decides on what the holder committed.
    // Here the holder corrects the expiry (as the reconcile's date correction will); the extension counts from
    // the corrected value, never from the one the command could have read before the lock.
    [Fact]
    public async Task Extend_WaitsOnTheWishRowLock_AndExtendsTheValueCommittedMeanwhile()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Active, w => w.ExpiresAtUtc = Now.AddDays(5));
        var corrected = Now.AddDays(8);
        await using var holder = fixture.CreateTaggedDbContext("wish-extend-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockWishesByIdsAsync([wishId], CancellationToken.None);
        await holder.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ExpiresAtUtc, corrected));

        await using var contender = fixture.CreateTaggedDbContext("wish-extend-contender");
        var waiting = Sut(contender).ExtendAsync(channel.Name, wishId, Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-extend-contender", waiting);

        Assert.Contains("\"Wishes\"", await fixture.WaitingQueryAsync("wish-extend-contender"));
        await using (var probe = fixture.CreateDbContext())
        {
            Assert.False(await probe.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        }

        await holderTransaction.CommitAsync();
        var result = await waiting;

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(corrected.AddDays(30), result.Wish!.ExpiresAtUtc);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(corrected.AddDays(30), (await db.Wishes.SingleAsync(w => w.Id == wishId)).ExpiresAtUtc);
    }

    [Fact]
    public async Task Extend_WaitingBehindAKeep_SeesKept_AndConflictsWithoutATrace()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Presented);
        await using var holder = fixture.CreateTaggedDbContext("wish-keep-holder");
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockWishesByIdsAsync([wishId], CancellationToken.None);
        await holder.Wishes.Where(w => w.Id == wishId).ExecuteUpdateAsync(s => s
            .SetProperty(w => w.Status, WishStatus.Kept)
            .SetProperty(w => w.EndedAtUtc, Now));

        await using var contender = fixture.CreateTaggedDbContext("wish-keep-contender");
        var waiting = Sut(contender).ExtendAsync(channel.Name, wishId, Actor);
        await fixture.WaitUntilBlockedOnLockAsync("wish-keep-contender", waiting);

        await holderTransaction.CommitAsync();
        var committed = await SnapshotAsync(wishId);
        var result = await waiting;

        Assert.Equal(WishOutcome.StateConflict, result.Outcome);
        Assert.Equal(committed, await SnapshotAsync(wishId));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    // ------------------------------------------------------------------------------------ audit

    // AK19 for the transition commands: no alias, emote name or login in the raw JSON, only the three keys.
    [Fact]
    public async Task TransitionAuditDetails_CarryOnlyIdsAndNumbers()
    {
        var channel = await SeedChannelAsync();
        void Secret(Wish w)
        {
            w.Alias = "SecretAlias";
            w.EmoteName = "SecretName";
            w.RequesterLogin = "secretviewer";
        }

        var approved = await SeedRowAsync(channel, WishStatus.Approved, Secret);
        var active = await SeedRowAsync(channel, WishStatus.Active, Secret);
        var presented = await SeedRowAsync(channel, WishStatus.Presented, Secret);
        var failed = await SeedRowAsync(channel, WishStatus.Failed, Secret);
        var removed = await SeedRowAsync(channel, WishStatus.Removed, Secret);
        var external = (await SeedBoundRowAsync(channel, WishStatus.RemovedExternally, Secret)).WishId;

        Assert.True((await Sut().UpdateAsync(channel.Name, approved, new UpdateWishRequest("SecretPatched", 10), Actor)).IsSuccess);
        Assert.True((await Sut().ExtendAsync(channel.Name, active, Actor)).IsSuccess);
        Assert.True((await Sut().KeepAsync(channel.Name, presented, Actor)).IsSuccess);
        Assert.True((await Sut().RetryAsync(channel.Name, failed, Actor)).IsSuccess);
        Assert.True((await Sut().CloseAsync(channel.Name, approved, Actor)).IsSuccess);
        Assert.True((await Sut().ReAddAsync(channel.Name, removed, new ReAddWishRequest("timed", null), Actor)).IsSuccess);
        Assert.True((await ReportRemovalAsync(channel, external)).IsSuccess);

        await using var db = fixture.CreateDbContext();
        var entries = await db.AuditLogEntries.Where(a => a.ChannelName == channel.Name).ToListAsync();
        Assert.Equal(7, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.DoesNotContain("Secret", e.DetailsJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("viewer", e.DetailsJson!, StringComparison.OrdinalIgnoreCase);
            using var json = JsonDocument.Parse(e.DetailsJson!);
            // A booked removal also names the reported entry (plan #485 2.1a): ids and one instant.
            string[] keys = e.Action == AuditActions.WishRemove
                ? ["durationDays", "emoteSetId", "sevenTvEmoteId", "slotAddedAtUtc", "slotId", "wishId"]
                : ["durationDays", "sevenTvEmoteId", "wishId"];
            Assert.Equal(keys, json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(("wish", e.TargetId), (e.TargetType, json.RootElement.GetProperty("wishId").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        });
    }

    // ------------------------------------------------------------------------------------ helpers

    private Task<WishCommandResult> RunAsync(string command, Seeded channel, long wishId, AppDbContext? db = null)
    {
        if (command == "complete-removal")
        {
            return ReportRemovalAsync(channel, wishId, db: db);
        }

        var sut = Sut(db);
        return command switch
        {
            "patch" => sut.UpdateAsync(channel.Name, wishId, new UpdateWishRequest("NewAlias", 12), Actor),
            "extend" => sut.ExtendAsync(channel.Name, wishId, Actor),
            "keep" => sut.KeepAsync(channel.Name, wishId, Actor),
            "close" => sut.CloseAsync(channel.Name, wishId, Actor),
            "retry" => sut.RetryAsync(channel.Name, wishId, Actor),
            "readd" => sut.ReAddAsync(channel.Name, wishId, new ReAddWishRequest("timed", null), Actor),
            "recheck" => sut.RecheckAsync(channel.Name, wishId, Actor),
            "bot-remove" => sut.RequestBotRemovalAsync(channel.Name, wishId, Actor),
            "approve" => sut.ApproveAsync(channel.Name, wishId, new ApproveWishRequest(null, null, null, null), Actor),
            "reject" => sut.RejectAsync(channel.Name, wishId, Actor),
            "unlink" => sut.UnlinkAsync(channel.Name, wishId, Actor),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }

    // The row of SeedRowAsync as the capture's automatic link to a slot of its own (unlink needs source irc, origin
    // auto and a slot; plan #486 2.6). In every state, so the matrix shows that only active is accepted.
    private async Task<long> SeedCaptureLinkedRowAsync(Seeded channel, string state)
    {
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-10));
        return await SeedRowAsync(channel, state, w =>
        {
            w.SevenTvEmoteId = emote.SevenTvEmoteId;
            w.Source = WishSource.Irc;
            w.MatchKind = WishMatchKind.Link;
            w.LinkOrigin = WishLinkOrigin.Auto;
            w.SlotId = slot;
        });
    }

    private async Task<string> SnapshotAsync(long wishId)
    {
        await using var db = fixture.CreateDbContext();
        return JsonSerializer.Serialize(await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == wishId));
    }

    // A row as it would look in the state: a run with start and expiry where the state has one, an end time
    // in the end states, a reason where the state carries one, and a queued bot removal that every successful
    // command must drop (E25).
    private async Task<long> SeedRowAsync(Seeded channel, string status, Action<Wish>? configure = null)
    {
        var wish = NewWish(channel.Id, EmoteId(), status, slotId: null);
        wish.StatusChangedAtUtc = Now.AddDays(-1);
        wish.CreatedAtUtc = Now.AddDays(-12);
        wish.ApprovedAtUtc = status == WishStatus.Pending ? null : Now.AddDays(-11);
        wish.StatusReason = status switch
        {
            WishStatus.Presented => WishStatusReason.Unconfirmed,
            WishStatus.Failed => WishStatusReason.Unknown,
            _ => null,
        };
        if (status is WishStatus.Active or WishStatus.Presented || WishStatus.IsEnded(status))
        {
            wish.StartsAtUtc = Now.AddDays(-10);
            wish.StartsAtAuthoritative = true;
            wish.ExpiresAtUtc = Now.AddDays(20);
        }

        wish.EndedAtUtc = WishStatus.IsEnded(status) ? Now.AddHours(-1) : null;
        wish.DecisionVersion = SeededDecisionVersion;
        wish.RemovalQueuedAtUtc = Now.AddMinutes(-1);
        wish.RemovalQueuedVersion = SeededDecisionVersion;
        configure?.Invoke(wish);

        await using var db = fixture.CreateDbContext();
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }
}
