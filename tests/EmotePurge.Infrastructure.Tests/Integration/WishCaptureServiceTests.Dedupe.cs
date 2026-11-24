using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The dedupe receipt (Sol-P2-6), the first redemption right after a command (Sol-P2-5) and the binding decided
// behind the channel lock (Sol-P1-1), with real Postgres and, for the races, a second connection.
public partial class WishCaptureServiceTests
{
    // ------------------------------------------------------------------------------------ dedupe

    [Fact]
    public async Task TheSameLineTwice_IsOneWish_TheSecondIsDuplicate_WithoutAnEvent()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));
        _publisher.ClearReceivedCalls();
        _trigger.ClearReceivedCalls();

        Assert.Equal(WishCaptureOutcome.Duplicate, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", text: "something else")));

        Assert.Equal("KEKW", (await SingleWishAsync(channel)).RequestText);
        AssertNothingPublished();
        await _trigger.DidNotReceive().TriggerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // TwitchLib's doubled read loop (#114) delivers the same line twice at once: the channel lock serializes the two,
    // and the second sees the first's committed receipt.
    [Fact]
    public async Task TheSameLineTwiceAtOnce_IsOneWish()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        var outcomes = await Task.WhenAll(
            Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")),
            Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));

        Assert.Equal([WishCaptureOutcome.WishCreated, WishCaptureOutcome.Duplicate], outcomes.Order());
        Assert.Single(await WishesAsync(channel));
    }

    // Plan 2.4: the receipt is per channel; the same message id in another channel is another redemption.
    [Fact]
    public async Task TheSameMessageIdInAnotherChannel_IsNoDuplicate()
    {
        var one = await SeedChannelAsync();
        var two = await SeedChannelAsync();
        await SeedRewardAsync(one, "tag-a");
        await SeedRewardAsync(two, "tag-a");

        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(one, "tag-a", "msg-1")));
        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(two, "tag-a", "msg-1")));
    }

    // Order 1: the line was the learning candidate; after the confirm the reward is known, and the same line coming
    // back (buffered, doubled) must not become a wish (plan 2.4: the redemption of the learning never is one).
    [Fact]
    public async Task TheCandidatesLine_AfterTheConfirm_IsDuplicate_AndNoWish()
    {
        var channel = await SeedChannelAsync();
        var session = await StartLearningAsync(channel);
        Assert.Equal(WishCaptureOutcome.CandidateStored, await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1")));
        var candidate = (await LearningRowAsync(channel))!.CandidateId;
        var confirmed = await RewardService().ConfirmLearningAsync(
            channel.Access, new ConfirmWishRewardLearningRequest(session, candidate, "Wish", 7), Actor);
        Assert.Equal(WishRewardOutcome.Success, confirmed.Outcome);

        Assert.Equal(WishCaptureOutcome.Duplicate, await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1")));

        Assert.Empty(await WishesAsync(channel));
        Assert.Null(await LearningRowAsync(channel));
    }

    // Order 2: the confirm runs before the doubled line is decided. The buffered line is the candidate's, so it is a
    // duplicate as well; a fresh redemption of the learned reward right after the confirm is a wish at once (Sol-P2-5).
    [Fact]
    public async Task AFreshRedemption_RightAfterTheConfirm_IsAWish_TheCandidatesLineIsNot()
    {
        var channel = await SeedChannelAsync();
        var session = await StartLearningAsync(channel);
        await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1"));
        var candidate = (await LearningRowAsync(channel))!.CandidateId;
        await RewardService().ConfirmLearningAsync(channel.Access, new ConfirmWishRewardLearningRequest(session, candidate, "Wish", 7), Actor);

        var fresh = await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-2"));
        var buffered = await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1"));

        Assert.Equal((WishCaptureOutcome.WishCreated, WishCaptureOutcome.Duplicate), (fresh, buffered));
        Assert.Equal(7, (await SingleWishAsync(channel)).DurationDays);
    }

    // Account deletion before the buffered duplicate: the deletion clears the requester fields, the receipt stays, so
    // the line coming back creates no second wish with the deleted account's data.
    [Fact]
    public async Task ABufferedDuplicate_AfterTheAccountDeletion_IsDuplicate_AndTheRequesterStaysGone()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var userId = await SeedUserAsync();
        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", userId: userId)));

        await DeleteAccountAsync(userId);
        var again = await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", userId: userId));

        Assert.Equal(WishCaptureOutcome.Duplicate, again);
        var wish = await SingleWishAsync(channel);
        Assert.Equal(((string?)null, (string?)null), (wish.RequesterTwitchUserId, wish.RequesterLogin));
        Assert.Equal(((string?)null, (string?)null, (string?)null), (wish.RequestText, wish.RequestedName, wish.IrcMessageId));
        Assert.True(await ReceiptExistsAsync(channel, "msg-1"));
    }

    // The other order: the duplicate is decided first, then the account goes. Still one wish, and it carries no
    // requester afterwards.
    [Fact]
    public async Task ADuplicate_BeforeTheAccountDeletion_LeavesOneWish_WhichTheDeletionClears()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var userId = await SeedUserAsync();
        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", userId: userId));

        Assert.Equal(WishCaptureOutcome.Duplicate, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", userId: userId)));
        await DeleteAccountAsync(userId);

        var wish = await SingleWishAsync(channel);
        Assert.Equal(((string?)null, (string?)null), (wish.RequesterTwitchUserId, wish.RequesterLogin));
        Assert.Equal(((string?)null, (string?)null, (string?)null), (wish.RequestText, wish.RequestedName, wish.IrcMessageId));
        Assert.True(await ReceiptExistsAsync(channel, "msg-1"));
    }

    // K7a (spec E35, AK110): the receipt hangs on no wish, so deleting the captured wish keeps it, and the same line
    // within the receipt's lifetime does not bring the wish back.
    [Fact]
    public async Task ADeletedCapturedWish_IsNotCreatedAgain_ByTheSameLine()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));
        var captured = await SingleWishAsync(channel);
        Assert.True((await Wishes().RejectAsync(channel.Name, captured.Id, Actor, channel.Access)).IsSuccess);
        var rejected = await SingleWishAsync(channel);

        var deleted = await Wishes().DeleteAsync(channel.Name, rejected.Id, rejected.EndedAtUtc!.Value, Actor, channel.Access);
        Assert.Equal(WishOutcome.Success, deleted.Outcome);

        Assert.Equal(WishCaptureOutcome.Duplicate, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));
        Assert.Empty(await WishesAsync(channel));
        Assert.True(await ReceiptExistsAsync(channel, "msg-1"));

        WishService Wishes() => new(
            fixture.CreateDbContext(), new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance), _clock, NullLogger<WishService>.Instance);
    }

    // ------------------------------------------------------------------------------------ first redemption after a command (Sol-P2-5)

    [Fact]
    public async Task TheFirstRedemption_RightAfterTheStart_IsTheCandidate()
    {
        var channel = await SeedChannelAsync();

        await StartLearningAsync(channel);
        var outcome = await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1"));

        Assert.Equal(WishCaptureOutcome.CandidateStored, outcome);
    }

    // ------------------------------------------------------------------------------------ binding behind the lock

    // Plan T5: a second connection holds the channel lock and lets the identity reconcile's kind of change happen while
    // the capture waits; the capture decides on the state after it.
    [Theory]
    [InlineData("unchanged", WishCaptureOutcome.WishCreated)]
    [InlineData("changed", WishCaptureOutcome.ChannelUnbound)]
    [InlineData("cleared", WishCaptureOutcome.ChannelUnbound)]
    [InlineData("deleted", WishCaptureOutcome.ChannelUnbound)]
    [InlineData("paused", WishCaptureOutcome.Discarded)]
    [InlineData("left", WishCaptureOutcome.Discarded)]
    public async Task Binding_ChangesWhileTheCaptureWaitsOnTheChannelLock_TheCaptureDecidesOnTheNewState(
        string change, WishCaptureOutcome expected)
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var tag = $"wish-capture-binding-{change}";
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext(tag);
        var waiting = Sut(contender).ProcessAsync(Observation(channel, "tag-a", "msg-1"));
        await fixture.WaitUntilBlockedOnLockAsync(tag, waiting);

        var rows = holder.Channels.Where(c => c.Id == channel.Id);
        _ = change switch
        {
            "changed" => await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.TwitchChannelId, Guid.NewGuid().ToString("N")[..12])),
            "cleared" => await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.TwitchChannelId, (string?)null)),
            "deleted" => await rows.ExecuteDeleteAsync(),
            "paused" => await holder.ChannelWishRewards.Where(r => r.ChannelId == channel.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.IsActive, false)),
            "left" => await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.IsBotActive, false)),
            _ => await rows.ExecuteUpdateAsync(set => set.SetProperty(c => c.IsBotActive, true))
        };
        await holderTransaction.CommitAsync();

        Assert.Equal(expected, await waiting);
        Assert.Equal(expected == WishCaptureOutcome.WishCreated, (await WishesAsync(channel)).Count == 1);
        Assert.Equal(expected == WishCaptureOutcome.WishCreated, await ReceiptExistsAsync(channel, "msg-1"));
    }

    // The reward counts from the confirm's commit on. A line decided while the confirm is still open (candidate set,
    // reward not yet visible) is dropped without a receipt, so the same line delivered again after the commit, like
    // every later one, is a wish: nothing waits for a snapshot to be refreshed.
    [Fact]
    public async Task ALineDecidedWhileTheConfirmIsOpen_IsDropped_AndTheSameLineAfterTheCommitIsAWish()
    {
        var channel = await SeedChannelAsync();
        var session = await StartLearningAsync(channel);
        await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1"));
        var candidate = (await LearningRowAsync(channel))!.CandidateId;

        // The confirm's writes, held open behind the channel lock.
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);
        holder.ChannelWishRewards.Add(new ChannelWishReward
        {
            ChannelId = channel.Id,
            RewardId = "tag-new",
            Label = "Wish",
            DefaultDurationDays = 7,
            CreatedAtUtc = Now
        });
        await holder.SaveChangesAsync();
        await holder.WishRewardLearnings.Where(l => l.ChannelId == channel.Id && l.SessionId == session && l.CandidateId == candidate)
            .ExecuteDeleteAsync();

        Assert.Equal(WishCaptureOutcome.Discarded, await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-2")));
        Assert.False(await ReceiptExistsAsync(channel, "msg-2"));
        await holderTransaction.CommitAsync();

        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-2")));
    }

    // The candidate side of the same deadline (Sol review round 2): a line seen a minute before the session expires
    // waits on the channel lock until after it. The locked decision reads the clock behind the lock, so the expired
    // session takes no candidate and no receipt is kept.
    [Fact]
    public async Task ALineWaitingOnTheChannelLockPastTheSessionsExpiry_TakesNoCandidate()
    {
        var channel = await SeedChannelAsync();
        await StartLearningAsync(channel);
        _clock.Advance(WishLimits.LearningWindow - TimeSpan.FromMinutes(1));
        await using var holder = fixture.CreateDbContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.LockChannelsByIdsAsync([channel.Id], CancellationToken.None);

        await using var contender = fixture.CreateTaggedDbContext("wish-capture-candidate-deadline");
        var waiting = Sut(contender).ProcessAsync(Observation(channel, "tag-new", "msg-1", sentAt: Now));
        await fixture.WaitUntilBlockedOnLockAsync("wish-capture-candidate-deadline", waiting);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await holderTransaction.CommitAsync();

        Assert.Equal(WishCaptureOutcome.Discarded, await waiting);
        Assert.Null((await LearningRowAsync(channel))!.CandidateId);
        Assert.False(await ReceiptExistsAsync(channel, "msg-1"));
    }

    // ------------------------------------------------------------------------------------ helpers

    private async Task<string> SeedUserAsync()
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        await using var db = fixture.CreateDbContext();
        db.Users.Add(new User { Id = id, TwitchUsername = $"u{id}", DisplayName = id, LastLogin = Now });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task DeleteAccountAsync(string twitchUserId)
    {
        await using var db = fixture.CreateDbContext();
        var service = new AccountDeletionService(
            db, Substitute.For<IModRoleCache>(), Substitute.For<IRateLimitTelemetry>(), NullLogger<AccountDeletionService>.Instance);
        var result = await service.DeleteAsync(twitchUserId, Actor, AccountDeletionReason.AdminRequest, null);
        Assert.Equal(AccountDeletionOutcome.Deleted, result.Outcome);
    }
}
