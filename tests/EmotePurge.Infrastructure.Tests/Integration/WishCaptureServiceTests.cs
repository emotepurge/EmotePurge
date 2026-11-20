using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishCaptureService (wish emotes K6, plan #486 T5) against real Postgres with the migrated schema: one observation
// from the read without a lock to the wish or the candidate, the binding, the limits and what a captured wish
// carries. Dedupe, the first redemption after a command and the races live in WishCaptureServiceTests.Dedupe; the
// housekeeping, the gaps and the life stamp in WishCaptureServiceTests.Housekeeping. Every test seeds its own channel.
[Collection("Postgres")]
public partial class WishCaptureServiceTests(PostgresFixture fixture)
{
    private const string Ulid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly AuditActor Actor = new("4242", "wishmod");

    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly IWishReconcileTrigger _trigger = Substitute.For<IWishReconcileTrigger>();
    private readonly HandWoundTimeProvider _clock = new();

    private DateTime Now => _clock.Now.UtcDateTime;

    // ------------------------------------------------------------------------------------ a known reward

    [Fact]
    public async Task KnownReward_CreatesAPendingWish_WithEverythingTheRedemptionCarries()
    {
        var channel = await SeedChannelAsync();
        var reward = await SeedRewardAsync(channel, "tag-a", days: 21);
        var sentAt = Now.AddSeconds(-30);

        var outcome = await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", text: "KEKW bitte", sentAt: sentAt, login: "SomeViewer"));

        Assert.Equal(WishCaptureOutcome.WishCreated, outcome);
        var wish = await SingleWishAsync(channel);
        Assert.Equal((WishStatus.Pending, WishSource.Irc, (string?)null), (wish.Status, wish.Source, wish.StatusReason));
        Assert.Equal((21, reward, "msg-1"), (wish.DurationDays, wish.RewardId, wish.IrcMessageId));
        Assert.Equal(("KEKW bitte", "KEKW", WishMatchKind.Name), (wish.RequestText, wish.RequestedName, wish.MatchKind));
        Assert.Equal((string.Empty, string.Empty, string.Empty), (wish.SevenTvEmoteId, wish.EmoteName, wish.Alias));
        Assert.Equal(("1001", "someviewer"), (wish.RequesterTwitchUserId, wish.RequesterLogin));
        Assert.Equal((sentAt, Now), (wish.CreatedAtUtc, wish.StatusChangedAtUtc));
        Assert.Equal(
            ((DateTime?)null, (DateTime?)null, (DateTime?)null, (string?)null, false, 0),
            (wish.ApprovedAtUtc, wish.EndedAtUtc, wish.StartsAtUtc, wish.SlotId, wish.BotOwned, wish.DecisionVersion));
        Assert.Equal(sentAt, (await RewardAsync(reward)).LastRedemptionSeenAtUtc);
        Assert.True(await ReceiptExistsAsync(channel, "msg-1"));
        await AssertNoAuditAsync(channel);
        AssertPublishedOnce(channel);
        await _trigger.Received(1).TriggerAsync(channel.Id, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://7tv.app/emotes/01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("bitte das hier 7tv.app/emotes/01arz3ndektsv4rrffq69g5fav danke")]
    public async Task ALink_CarriesTheCanonicalId_ButNoNameYet(string text)
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", text: text));

        var wish = await SingleWishAsync(channel);
        Assert.Equal((Ulid, WishMatchKind.Link, (string?)null), (wish.SevenTvEmoteId, wish.MatchKind, wish.RequestedName));
        Assert.Equal((string.Empty, string.Empty, (string?)null), (wish.EmoteName, wish.Alias, wish.StatusReason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("!!! ???")]
    public async Task NothingUsableInTheText_IsStillAWish_WithTheHintUnparsed(string text)
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", text: text)));

        var wish = await SingleWishAsync(channel);
        Assert.Equal((WishMatchKind.None, WishStatusReason.Unparsed, text), (wish.MatchKind, wish.StatusReason, wish.RequestText));
    }

    [Fact]
    public async Task ATextOf500CodePoints_IsStoredWhole_ALongerOneIsCutTo500()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var exact = new string('a', 500);
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 600));

        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", userId: "1", text: exact));
        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-2", userId: "2", text: emoji));

        var wishes = await WishesAsync(channel);
        Assert.Equal(exact, wishes[0].RequestText);
        Assert.Equal(500, wishes[1].RequestText!.EnumerateRunes().Count());
        Assert.StartsWith(wishes[1].RequestText!, emoji, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASendTimeInTheFuture_IsCappedAtNow()
    {
        var channel = await SeedChannelAsync();
        var reward = await SeedRewardAsync(channel, "tag-a");

        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", sentAt: Now.AddMinutes(5)));

        var wish = await SingleWishAsync(channel);
        Assert.Equal((Now, Now), (wish.CreatedAtUtc, wish.StatusChangedAtUtc));
        Assert.Equal(Now, (await RewardAsync(reward)).LastRedemptionSeenAtUtc);
    }

    // AK52: the wish takes the duration of its reward, not the channel default.
    [Fact]
    public async Task TwoRewards_With7And30Days_GiveTheirWishesTheirOwnDuration()
    {
        var channel = await SeedChannelAsync();
        await SeedSettingsAsync(channel, defaultDurationDays: 14);
        await SeedRewardAsync(channel, "tag-7", days: 7);
        await SeedRewardAsync(channel, "tag-30", days: 30);

        await Sut().ProcessAsync(Observation(channel, "tag-7", "msg-1", userId: "1"));
        await Sut().ProcessAsync(Observation(channel, "tag-30", "msg-2", userId: "2"));

        Assert.Equal([7, 30], (await WishesAsync(channel)).Select(w => w.DurationDays));
    }

    [Fact]
    public async Task LastSeen_NeverMovesBack_ForALineDrainedLate()
    {
        var channel = await SeedChannelAsync();
        var reward = await SeedRewardAsync(channel, "tag-a");

        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1", userId: "1", sentAt: Now.AddSeconds(-5)));
        await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-2", userId: "2", sentAt: Now.AddSeconds(-50)));

        Assert.Equal(Now.AddSeconds(-5), (await RewardAsync(reward)).LastRedemptionSeenAtUtc);
        Assert.Equal(2, (await WishesAsync(channel)).Count);
    }

    // A known reward is a wish also while a learning session waits for its candidate, and the session stays as it is.
    [Fact]
    public async Task AKnownReward_DuringLearning_BecomesAWish_NotTheCandidate()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var session = await StartLearningAsync(channel);

        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));

        var row = (await LearningRowAsync(channel))!;
        Assert.Equal((session, (Guid?)null), (row.SessionId, row.CandidateId));
        Assert.Single(await WishesAsync(channel));
    }

    // Plan 2.4 step 6: the reconcile runs right after the commit, so a slot already in the window links at once
    // (the real trigger and reconcile, not a substitute).
    [Fact]
    public async Task ASlotAlreadyInTheWindow_LinksRightAfterTheCapture()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var emote = await SeedEmoteAsync(channel, Ulid);
        var slotAddedAt = Now.AddMinutes(-1);
        var slot = await SeedSlotAsync(emote, "Wanted", slotAddedAt);

        var outcome = await Sut(trigger: RealTrigger()).ProcessAsync(
            Observation(channel, "tag-a", "msg-1", text: $"https://7tv.app/emotes/{Ulid}", sentAt: Now.AddMinutes(-5)));

        Assert.Equal(WishCaptureOutcome.WishCreated, outcome);
        var wish = await SingleWishAsync(channel);
        Assert.Equal((WishStatus.Active, slot, WishLinkOrigin.Auto), (wish.Status, wish.SlotId, wish.LinkOrigin));
        Assert.Equal((Ulid, "Wanted", "Emote"), (wish.SevenTvEmoteId, wish.Alias, wish.EmoteName));
        Assert.Equal(slotAddedAt, wish.StartsAtUtc);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.AuditLogEntries.CountAsync(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishAutoLink));
    }

    [Fact]
    public async Task AFailingReconcileAfterTheCommit_DoesNotChangeTheAnswer()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var failing = new WishReconcileTrigger(
            ThrowingReconcile(), new WishOptions { Enabled = true }, NullLogger<WishReconcileTrigger>.Instance);

        var outcome = await Sut(trigger: failing).ProcessAsync(Observation(channel, "tag-a", "msg-1"));

        Assert.Equal(WishCaptureOutcome.WishCreated, outcome);
        Assert.Single(await WishesAsync(channel));
    }

    // ------------------------------------------------------------------------------------ what is dropped (AK51)

    [Fact]
    public async Task AnUnknownReward_WithoutLearning_LeavesNoRowInAnyTable()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-known");

        var outcome = await Sut().ProcessAsync(Observation(channel, "tag-unknown", "msg-1"));

        Assert.Equal(WishCaptureOutcome.Discarded, outcome);
        await AssertNoTraceAsync(channel);
        Assert.Null(await LearningRowAsync(channel));
        Assert.Single(await RewardsAsync(channel));
        AssertNothingPublished();
    }

    [Fact]
    public async Task APausedReward_IsDiscarded_AndAReactivatedOneCountsAtOnce()
    {
        var channel = await SeedChannelAsync();
        var reward = await SeedRewardAsync(channel, "tag-a", isActive: false);

        Assert.Equal(WishCaptureOutcome.Discarded, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));
        await AssertNoTraceAsync(channel);

        var resumed = await RewardService().UpdateRewardAsync(channel.Access, reward, new UpdateWishRewardRequest(null, null, true), Actor);
        Assert.Equal(WishRewardOutcome.Success, resumed.Outcome);

        // Sol-P2-5: no snapshot to wait for, the very next redemption (even the same line) is a wish.
        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ADeactivatedChannel_IsDiscarded(bool isBotActive, bool deactivatedStamp)
    {
        var channel = await SeedChannelAsync(isBotActive: isBotActive, deactivatedAt: deactivatedStamp ? Now.AddDays(-1) : null);
        await SeedRewardAsync(channel, "tag-a");

        Assert.Equal(WishCaptureOutcome.Discarded, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-1")));
        await AssertNoTraceAsync(channel);
    }

    [Theory]
    [InlineData("", "msg-1", "1001", "viewer")]
    [InlineData("tag-a", "", "1001", "viewer")]
    [InlineData("tag-a", "msg-1", "", "viewer")]
    [InlineData("tag-a", "msg-1", "1001", "")]
    [InlineData("tag-a", "msg-1", "1001", "aaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task AMalformedObservation_IsDiscarded_WithoutTouchingTheDatabase(string rewardId, string messageId, string userId, string login)
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        var outcome = await Sut().ProcessAsync(new RewardObservation(channel.TwitchId!, rewardId, messageId, userId, login, "KEKW", Now));

        Assert.Equal(WishCaptureOutcome.Discarded, outcome);
        await AssertNoTraceAsync(channel);
    }

    // ------------------------------------------------------------------------------------ binding (Sol-P1-1)

    [Fact]
    public async Task AnUnboundChannel_CarryingTheIrcName_IsChannelUnbound_AndKeepsNothing()
    {
        var channel = await SeedChannelAsync(bound: false);
        await SeedRewardAsync(channel, "tag-a");
        await SeedLearningAsync(channel);

        var outcome = await Sut().ProcessAsync(new RewardObservation("31337", "tag-a", "msg-1", "1001", "viewer", "KEKW", Now));

        Assert.Equal(WishCaptureOutcome.ChannelUnbound, outcome);
        await AssertNoTraceAsync(channel);
        Assert.Null((await LearningRowAsync(channel))!.CandidateId);
        AssertNothingPublished();
    }

    // Row A (name x, id 1) was renamed; a new row B carries the name x without an id. A redemption from room 2 in the
    // IRC channel x reaches neither: A is not room 2 and B is not bound.
    [Fact]
    public async Task AReassignedLogin_ReachesNeitherTheRenamedRowNorTheNewUnboundOne()
    {
        var oldRow = await SeedChannelAsync();
        await SeedRewardAsync(oldRow, "tag-a");
        await using (var db = fixture.CreateDbContext())
        {
            await db.Channels.Where(c => c.Id == oldRow.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.ChannelName, oldRow.Name + "_old"));
        }

        var newRow = await SeedChannelAsync(bound: false, name: oldRow.Name);
        await SeedRewardAsync(newRow, "tag-a");
        var otherRoom = Guid.NewGuid().ToString("N")[..12];

        var outcome = await Sut().ProcessAsync(new RewardObservation(otherRoom, "tag-a", "msg-1", "1001", "viewer", "KEKW", Now));

        Assert.Equal(WishCaptureOutcome.ChannelUnbound, outcome);
        await AssertNoTraceAsync(oldRow);
        await AssertNoTraceAsync(newRow);
    }

    // ------------------------------------------------------------------------------------ limits

    // AK53, second half: the fourth open wish of one viewer at limit 3 is refused without a receipt; once one of the
    // three is no longer open, the same line is a wish.
    [Fact]
    public async Task TheFourthOpenWishOfAViewer_IsRequesterLimit_AndAWishThatTurnedActiveNoLongerCounts()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", $"msg-{i}", userId: "77")));
        }

        // Another viewer is not held back by the first one's limit.
        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-other", userId: "78")));

        Assert.Equal(WishCaptureOutcome.RequesterLimit, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-4", userId: "77")));
        Assert.False(await ReceiptExistsAsync(channel, "msg-4"));
        Assert.Equal(4, (await WishesAsync(channel)).Count);

        var first = (await WishesAsync(channel))[0];
        await using (var db = fixture.CreateDbContext())
        {
            await db.Wishes.Where(w => w.Id == first.Id).ExecuteUpdateAsync(set => set
                .SetProperty(w => w.Status, WishStatus.Active)
                .SetProperty(w => w.SevenTvEmoteId, Ulid)
                .SetProperty(w => w.EmoteName, "Emote")
                .SetProperty(w => w.Alias, "Emote"));
        }

        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-4", userId: "77")));
    }

    [Fact]
    public async Task TheLimitPerViewer_FollowsTheConfiguration()
    {
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");

        Assert.Equal(WishCaptureOutcome.WishCreated, await Sut(maxPerRequester: 1).ProcessAsync(Observation(channel, "tag-a", "msg-1")));
        Assert.Equal(WishCaptureOutcome.RequesterLimit, await Sut(maxPerRequester: 1).ProcessAsync(Observation(channel, "tag-a", "msg-2")));
    }

    [Fact]
    public async Task The201stOpenWishOfAChannel_IsChannelLimit_WithoutAReceipt()
    {
        var channel = await SeedChannelAsync();
        var reward = await SeedRewardAsync(channel, "tag-a");
        await using (var db = fixture.CreateDbContext())
        {
            db.Wishes.AddRange(Enumerable.Range(0, WishLimits.MaxPendingPerChannel).Select(i => new Wish
            {
                ChannelId = channel.Id,
                Status = WishStatus.Pending,
                Source = WishSource.Irc,
                RewardId = reward,
                RequesterTwitchUserId = $"viewer-{i}",
                CreatedAtUtc = Now,
                StatusChangedAtUtc = Now
            }));
            await db.SaveChangesAsync();
        }

        var outcome = await Sut().ProcessAsync(Observation(channel, "tag-a", "msg-201", userId: "new-viewer"));

        Assert.Equal(WishCaptureOutcome.ChannelLimit, outcome);
        Assert.False(await ReceiptExistsAsync(channel, "msg-201"));
        Assert.Equal(WishLimits.MaxPendingPerChannel, (await WishesAsync(channel)).Count);
        AssertNothingPublished();
    }

    // ------------------------------------------------------------------------------------ learning (AK50)

    [Fact]
    public async Task TheFirstUnknownRewardDuringLearning_BecomesTheCandidate_ASecondDoesNotReplaceIt()
    {
        var channel = await SeedChannelAsync();
        var session = await StartLearningAsync(channel);
        var sentAt = Now.AddSeconds(2);
        _clock.Advance(TimeSpan.FromSeconds(3));

        var first = await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1", text: "please KEKW", login: "Viewer", sentAt: sentAt));
        var second = await Sut().ProcessAsync(Observation(channel, "tag-other", "msg-2", text: "other", login: "other"));

        Assert.Equal((WishCaptureOutcome.CandidateStored, WishCaptureOutcome.Discarded), (first, second));
        var row = (await LearningRowAsync(channel))!;
        Assert.Equal(session, row.SessionId);
        Assert.NotNull(row.CandidateId);
        Assert.Equal(("tag-new", "please KEKW", "viewer", sentAt), (row.CandidateRewardId, row.CandidateText, row.CandidateLogin, row.CandidateSeenAtUtc));
        Assert.True(await ReceiptExistsAsync(channel, "msg-1"));
        Assert.False(await ReceiptExistsAsync(channel, "msg-2"));
        Assert.Empty(await WishesAsync(channel));
        await AssertNoAuditAsync(channel);
        AssertPublishedOnce(channel);
        await _trigger.DidNotReceive().TriggerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The read model shows exactly this candidate.
        var read = await RewardService().GetAsync(channel.Access, "1", false);
        Assert.Equal(row.CandidateId, read.Rewards!.Learning!.Candidate!.CandidateId);
    }

    // Plan 2.4 step 4: a line sent more than 10 seconds before the start is not the candidate; one within is.
    [Theory]
    [InlineData(-11, WishCaptureOutcome.Discarded)]
    [InlineData(-10, WishCaptureOutcome.CandidateStored)]
    public async Task ARedemptionSentBeforeTheStart_CountsOnlyWithinTenSeconds(int secondsBeforeStart, WishCaptureOutcome expected)
    {
        var channel = await SeedChannelAsync();
        await StartLearningAsync(channel);
        var startedAt = Now;
        _clock.Advance(TimeSpan.FromSeconds(1));

        var outcome = await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1", sentAt: startedAt.AddSeconds(secondsBeforeStart)));

        Assert.Equal(expected, outcome);
        Assert.Equal(expected == WishCaptureOutcome.CandidateStored, (await LearningRowAsync(channel))!.CandidateId is not null);
    }

    [Fact]
    public async Task AnExpiredSessionThatStillLies_TakesNoCandidate()
    {
        var channel = await SeedChannelAsync();
        await StartLearningAsync(channel);
        _clock.Advance(WishLimits.LearningWindow);

        Assert.Equal(WishCaptureOutcome.Discarded, await Sut().ProcessAsync(Observation(channel, "tag-new", "msg-1", sentAt: Now)));

        Assert.Null((await LearningRowAsync(channel))!.CandidateId);
        Assert.False(await ReceiptExistsAsync(channel, "msg-1"));
    }

    // ------------------------------------------------------------------------------------ logs (AK64)

    [Fact]
    public async Task NoLogLine_CarriesText_Login_OrAUserId()
    {
        var provider = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([provider], new LoggerFilterOptions { MinLevel = LogLevel.Trace });
        var logger = factory.CreateLogger<WishCaptureService>();
        var channel = await SeedChannelAsync();
        await SeedRewardAsync(channel, "tag-a");
        var unbound = await SeedChannelAsync(bound: false);
        const string text = "secret-text-SnoopyKEKW";
        const string login = "secretlogin";
        const string userId = "987654321";

        await StartLearningAsync(channel);
        await Sut(logger: logger).ProcessAsync(Observation(channel, "tag-new", "msg-0", text: text, login: login, userId: userId));
        for (var i = 1; i <= 4; i++)
        {
            await Sut(logger: logger, maxPerRequester: 3).ProcessAsync(Observation(channel, "tag-a", $"msg-{i}", text: text, login: login, userId: userId));
        }

        await Sut(logger: logger).ProcessAsync(Observation(channel, "tag-a", "msg-1", text: text, login: login, userId: userId));
        await Sut(logger: logger).ProcessAsync(new RewardObservation("424242", "tag-a", "msg-9", userId, login, text, Now));
        await Sut(logger: logger).ProcessAsync(Observation(unbound, "tag-a", "msg-9", text: text, login: login, userId: userId));

        Assert.NotEmpty(provider.Lines);
        Assert.All(provider.Lines, line =>
        {
            Assert.DoesNotContain(text, line.AllText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(login, line.AllText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(userId, line.AllText, StringComparison.Ordinal);
            Assert.DoesNotContain("424242", line.AllText, StringComparison.Ordinal);
        });
    }

    // ------------------------------------------------------------------------------------ helpers

    private WishCaptureService Sut(
        AppDbContext? db = null,
        int maxPerRequester = 3,
        IWishReconcileTrigger? trigger = null,
        ILogger<WishCaptureService>? logger = null,
        WishOptions? options = null) =>
        new(
            db ?? fixture.CreateDbContext(),
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            trigger ?? _trigger,
            options ?? new WishOptions { Enabled = true, Capture = { Enabled = true, MaxPendingPerRequester = maxPerRequester } },
            _clock,
            logger ?? NullLogger<WishCaptureService>.Instance);

    private WishRewardService RewardService(AppDbContext? db = null) =>
        new(db ?? fixture.CreateDbContext(), new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance), _clock);

    private WishReconcileTrigger RealTrigger()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext());
        var provider = services.BuildServiceProvider();
        var reconcile = new WishReconcileService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            NullLogger<WishReconcileService>.Instance);
        return new WishReconcileTrigger(reconcile, new WishOptions { Enabled = true }, NullLogger<WishReconcileTrigger>.Instance);
    }

    private static IWishReconcileService ThrowingReconcile()
    {
        var reconcile = Substitute.For<IWishReconcileService>();
        reconcile.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("reconcile down"));
        return reconcile;
    }

    private async Task<Guid> StartLearningAsync(Seeded channel)
    {
        var started = await RewardService().StartLearningAsync(channel.Access, Actor);
        Assert.Equal(WishRewardOutcome.Success, started.Outcome);
        _publisher.ClearReceivedCalls();
        return started.Learning!.SessionId;
    }

    private RewardObservation Observation(
        Seeded channel, string rewardTag, string messageId, string userId = "1001", string login = "viewer", string text = "KEKW", DateTime? sentAt = null) =>
        new(channel.TwitchId ?? "none", rewardTag, messageId, userId, login, text, sentAt ?? Now);

    private List<NSubstitute.Core.ICall> PublishCalls() =>
        _publisher.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync)).ToList();

    private void AssertPublishedOnce(Seeded channel)
    {
        var call = Assert.Single(PublishCalls());
        Assert.Equal(LiveEvents.Channel, call.GetArguments()[0]);
        Assert.Equal(new LiveEvent(LiveEvents.WishChanged, channel.Name), LiveEvent.TryParse((string?)call.GetArguments()[1]));
    }

    private void AssertNothingPublished() => Assert.Empty(PublishCalls());

    // Neither a wish, nor a receipt, nor a gap, nor an audit entry for the channel.
    private async Task AssertNoTraceAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.Wishes.AnyAsync(w => w.ChannelId == channel.Id));
        Assert.False(await db.WishCaptureReceipts.AnyAsync(r => r.ChannelId == channel.Id));
        Assert.False(await db.WishCaptureGaps.AnyAsync(g => g.ChannelId == channel.Id));
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        Assert.False(await db.WishRewardLearnings.AnyAsync(l => l.ChannelId == channel.Id && l.CandidateId != null));
    }

    private async Task AssertNoAuditAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
    }

    private async Task<Wish> SingleWishAsync(Seeded channel) => Assert.Single(await WishesAsync(channel));

    private async Task<List<Wish>> WishesAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Wishes.AsNoTracking().Where(w => w.ChannelId == channel.Id).OrderBy(w => w.Id).ToListAsync();
    }

    private async Task<ChannelWishReward> RewardAsync(long rowId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ChannelWishRewards.AsNoTracking().SingleAsync(r => r.Id == rowId);
    }

    private async Task<List<ChannelWishReward>> RewardsAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ChannelWishRewards.AsNoTracking().Where(r => r.ChannelId == channel.Id).ToListAsync();
    }

    private async Task<bool> ReceiptExistsAsync(Seeded channel, string messageId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WishCaptureReceipts.AnyAsync(r => r.ChannelId == channel.Id && r.IrcMessageId == messageId);
    }

    private async Task<WishRewardLearning?> LearningRowAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WishRewardLearnings.AsNoTracking().SingleOrDefaultAsync(l => l.ChannelId == channel.Id);
    }

    private async Task<Seeded> SeedChannelAsync(
        bool bound = true, bool isBotActive = true, DateTime? deactivatedAt = null, string? name = null)
    {
        name ??= $"c{Guid.NewGuid():N}"[..18];
        var twitchId = bound ? Guid.NewGuid().ToString("N")[..12] : null;
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, TwitchChannelId = twitchId, IsBotActive = isBotActive, DeactivatedAtUtc = deactivatedAt };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return new Seeded(channel.Id, name, twitchId, new WishCaptureAccess(channel.Id, twitchId, IdConfirmed: bound));
    }

    private async Task SeedSettingsAsync(Seeded channel, int defaultDurationDays = 30, int pendingExpiryDays = 14)
    {
        await using var db = fixture.CreateDbContext();
        db.ChannelWishSettings.Add(new ChannelWishSettings
        {
            ChannelId = channel.Id,
            DefaultDurationDays = defaultDurationDays,
            PendingExpiryDays = pendingExpiryDays
        });
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedRewardAsync(Seeded channel, string tag, int days = 30, bool isActive = true)
    {
        await using var db = fixture.CreateDbContext();
        var reward = new ChannelWishReward
        {
            ChannelId = channel.Id,
            RewardId = tag,
            Label = "Reward",
            DefaultDurationDays = days,
            IsActive = isActive,
            CreatedAtUtc = Now
        };
        db.ChannelWishRewards.Add(reward);
        await db.SaveChangesAsync();
        return reward.Id;
    }

    // A learning row as the start leaves it, written past the binding check (for an unbound row).
    private async Task SeedLearningAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        db.WishRewardLearnings.Add(new WishRewardLearning
        {
            ChannelId = channel.Id,
            SessionId = Guid.NewGuid(),
            StartedAtUtc = Now,
            ExpiresAtUtc = Now + WishLimits.LearningWindow
        });
        await db.SaveChangesAsync();
    }

    private async Task<Emote> SeedEmoteAsync(Seeded channel, string sevenTvEmoteId)
    {
        await using var db = fixture.CreateDbContext();
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "Emote",
            SevenTvEmoteId = sevenTvEmoteId,
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private async Task<string> SeedSlotAsync(Emote emote, string alias, DateTime addedAt)
    {
        await using var db = fixture.CreateDbContext();
        var slot = new EmoteSlot { EmoteId = emote.Id, Name = alias, AddedAt = addedAt, AddedAtFromRest = true };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private sealed record Seeded(string Id, string Name, string? TwitchId, WishCaptureAccess Access);
}
