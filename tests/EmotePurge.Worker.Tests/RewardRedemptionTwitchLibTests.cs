using EmotePurge.Core.Chat;
using EmotePurge.Core.Services;
using EmotePurge.Worker.WishCapture;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TwitchLib.Client.Models;
using TwitchLib.Client.Parsing;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Deliberately TwitchLib-bound, the same exception as SharedChatRuleTwitchLibTests (plan #486 T10, AK47, AK48, W1).
// The reward capture depends on a fact about the installed TwitchLib.Client 4.0.1 that no pure test can see: the
// ChatMessage constructor has its own branch for custom-reward-id and sets the typed ChatMessage.CustomRewardId, so
// the tag never reaches UndocumentedTags. A TwitchLib update that drops that branch would move the tag there and
// leave CustomRewardId null — the capture would go silent without an error anywhere. These tests run lines through
// TwitchLib's own IrcParser and ChatMessage constructor and then through RewardCapture.Read, the very field reads the
// chat handler runs. Lines are synthetic in the shape of the live probe P1 (the tag set of a redemption with text
// input, without source-room-id); every id, login and text is invented, never recorded chat (plan W3).
public class RewardRedemptionTwitchLibTests
{
    private const string RoomId = "111111111";
    private const string ForeignRoomId = "222222222";
    private const string RewardId = "aaaa1111-2222-3333-4444-555555555555";
    private const string MessageId = "bbbb1111-2222-3333-4444-666666666666";
    private const string UserId = "333333333";
    private const string Login = "testviewer1";
    private const long SentAtUnixMs = 1763500000123;

    private static readonly DateTime SentAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(SentAtUnixMs).UtcDateTime;

    [Fact]
    public void ProbeShapedLine_TypesTheRewardTag_AndReadsAllSevenFields()
    {
        var message = ParseLine(RedemptionLine("7tv.app/emotes/01ARZ3NDEKTSV4RRFFQ69G5FAV"));

        // The contract: the typed property carries the value, and the key is not in UndocumentedTags (the dictionary
        // itself exists here because flags and returning-chatter are untyped).
        Assert.Equal(RewardId, message.CustomRewardId);
        Assert.NotNull(message.UndocumentedTags);
        Assert.False(message.UndocumentedTags.ContainsKey("custom-reward-id"));
        Assert.Equal(MessageId, message.Id);
        Assert.Equal(SentAtUnixMs, message.TmiSent.ToUnixTimeMilliseconds());
        Assert.Equal(RoomId, message.RoomId);
        Assert.Equal(UserId, message.UserId);
        Assert.Equal(Login, message.Username);
        Assert.Equal("7tv.app/emotes/01ARZ3NDEKTSV4RRFFQ69G5FAV", message.Message);

        var observation = RewardCapture.Read(message);

        Assert.Equal(
            new RewardObservation(RoomId, RewardId, MessageId, UserId, Login, "7tv.app/emotes/01ARZ3NDEKTSV4RRFFQ69G5FAV", SentAtUtc),
            observation);
        Assert.Equal(DateTimeKind.Utc, observation!.SentAtUtc.Kind);
    }

    [Fact]
    public void LineWithOnlyTypedTagsAndTheRewardTag_LeavesUndocumentedTagsNull()
    {
        // Typed tags only, plus custom-reward-id: if the reward tag were untyped, it alone would create the dictionary.
        var line = "@badges=;color=#FF0000;custom-reward-id=" + RewardId + ";display-name=TestViewer1;"
            + "id=" + MessageId + ";mod=0;room-id=" + RoomId + ";subscriber=0;tmi-sent-ts=" + SentAtUnixMs + ";"
            + "turbo=0;user-id=" + UserId + ";user-type= "
            + ":" + Login + "!" + Login + "@" + Login + ".tmi.twitch.tv PRIVMSG #probechannel_test :sadge";

        var message = ParseLine(line);

        Assert.Null(message.UndocumentedTags);
        Assert.Equal(RewardId, message.CustomRewardId);
        Assert.NotNull(RewardCapture.Read(message));
    }

    [Fact]
    public void LineWithoutTheRewardTag_HasNoRewardId_AndYieldsNothing()
    {
        var message = ParseLine(RedemptionLine("hello", rewardTag: null));

        Assert.Null(message.CustomRewardId);
        Assert.Null(RewardCapture.Read(message));
    }

    [Fact]
    public void RewardTagWithAnEmptyValue_IsTheEmptyString_AndYieldsNothing()
    {
        // TwitchLib sets the property to "" for "custom-reward-id=": the handler's null test lets the line through,
        // the parser's shape check stops it.
        var message = ParseLine(RedemptionLine("hello", rewardTag: ""));

        Assert.Equal(string.Empty, message.CustomRewardId);
        Assert.Null(RewardCapture.Read(message));
    }

    [Fact]
    public void SourceRoomIdEqualToTheRoom_YieldsAnObservation()
    {
        var message = ParseLine(RedemptionLine("hello", extraTags: "source-badge-info=;source-badges=;"
            + "source-id=" + MessageId + ";source-room-id=" + RoomId + ";"));

        Assert.Equal(MessageOrigin.Own, SharedChatRule.FromTags(message.RoomId, message.UndocumentedTags));
        Assert.NotNull(RewardCapture.Read(message));
    }

    [Fact]
    public void SourceRoomIdOfAnotherRoom_YieldsNothing()
    {
        var message = ParseLine(RedemptionLine("hello", extraTags: "source-badge-info=;source-badges=;"
            + "source-id=cccc1111-2222-3333-4444-777777777777;source-room-id=" + ForeignRoomId + ";"));

        Assert.Equal(RewardId, message.CustomRewardId);
        Assert.Null(RewardCapture.Read(message));
    }

    // The marker lines of SharedChatRuleTwitchLibTests (a marker without source-room-id, one per line, with and
    // without a value), extended by the reward tag (AK48): no observation, for learning and redemption alike — the
    // parser has no separate learning path, both are this one read.
    [Theory]
    [InlineData(SharedChatRule.SourceIdTag, "cccc1111-2222-3333-4444-777777777777")]
    [InlineData(SharedChatRule.SourceIdTag, "")]
    [InlineData(SharedChatRule.SourceBadgesTag, "")]
    [InlineData(SharedChatRule.SourceBadgesTag, "subscriber/1")]
    [InlineData(SharedChatRule.SourceBadgeInfoTag, "")]
    [InlineData(SharedChatRule.SourceBadgeInfoTag, "subscriber/1")]
    public void SourceMarkerWithoutSourceRoomId_YieldsNothing(string marker, string value)
    {
        var message = ParseLine(RedemptionLine("hello", extraTags: marker + "=" + value + ";"));

        Assert.Equal(RewardId, message.CustomRewardId);
        Assert.Equal(MessageOrigin.Indeterminate, SharedChatRule.FromTags(message.RoomId, message.UndocumentedTags));
        Assert.Null(RewardCapture.Read(message));
    }

    [Fact]
    public void ActionLine_YieldsTheTextWithoutTheActionWrapper()
    {
        var message = ParseLine(RedemptionLine("\u0001ACTION sadge please\u0001"));

        Assert.True(message.IsMe);
        Assert.Equal("sadge please", RewardCapture.Read(message)!.Text);
    }

    [Fact]
    public void SplicedLineWithTheRewardTag_IsFlagged_AndTheRouteDoesNotObserveIt()
    {
        // The splice shape of IrcLineSpliceRuleTwitchLibTests (second tag block inside a typed value), with the reward
        // tag in the second half: TwitchLib still fills CustomRewardId, only the raw line shows the splice (W23).
        var line = "@badges=;color=#FF0000;display-name=TestUser1;id=cccc3333-3333-3333-3333-333333333333;"
            + "mod=0;room-id=" + RoomId + ";subscriber=0@badge-info=;badges=;color=#00FF00;custom-reward-id=" + RewardId + ";"
            + "display-name=TestViewer1;id=" + MessageId + ";mod=0;room-id=" + RoomId + ";"
            + "subscriber=0;tmi-sent-ts=" + SentAtUnixMs + ";turbo=0;user-id=" + UserId + ";user-type= "
            + ":" + Login + "!" + Login + "@" + Login + ".tmi.twitch.tv PRIVMSG #probechannel_test :sadge";

        var message = ParseLine(line);
        var isSpliced = IrcLineSpliceRule.IsSpliced(message.RawIrcMessage);
        var route = ChatMessageRoute.Decide(
            isExcluded: false,
            captureEnabled: true,
            hasRewardTag: message.CustomRewardId is not null,
            isSpliced,
            snapshotIsEmpty: false);

        Assert.Equal(RewardId, message.CustomRewardId);
        Assert.True(isSpliced);
        Assert.False(route.Observe);
        Assert.True(route.CountSplicedRedemption);
        Assert.True(route.MatchEmotes);
    }

    [Fact]
    public void Capture_QueuesTheObservation_AndCountsAnEmptySnapshot()
    {
        var stats = new WorkerStats();
        var queue = new RewardObservationQueue(stats);
        var message = ParseLine(RedemptionLine("sadge"));

        RewardCapture.Capture(message, snapshotIsEmpty: true, queue, stats);
        RewardCapture.Capture(ParseLine(RedemptionLine("sadge", messageId: "dddd1111-2222-3333-4444-888888888888")), snapshotIsEmpty: false, queue, stats);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out var first));
        Assert.Equal(MessageId, first.MessageId);
        Assert.Equal(1, stats.TakeRewardCaptureCounters().ObservedAtEmptySnapshot);
    }

    [Fact]
    public void Capture_LineThatYieldsNothing_NeverCallsTheSink()
    {
        var stats = new WorkerStats();
        var sink = Substitute.For<IRewardObservationSink>();

        RewardCapture.Capture(ParseLine(RedemptionLine("hello", rewardTag: "")), snapshotIsEmpty: true, sink, stats);

        sink.DidNotReceiveWithAnyArgs().TryEnqueue(default!);
        Assert.True(stats.TakeRewardCaptureCounters().IsZero);
    }

    [Fact]
    public void Capture_SinkThrows_CountsTheFault_AndDoesNotThrow()
    {
        var stats = new WorkerStats();
        var sink = Substitute.For<IRewardObservationSink>();
        sink.TryEnqueue(Arg.Any<RewardObservation>()).Throws(new InvalidOperationException("sink broke"));

        var exception = Record.Exception(() =>
            RewardCapture.Capture(ParseLine(RedemptionLine("sadge")), snapshotIsEmpty: true, sink, stats));

        Assert.Null(exception);
        var counters = stats.TakeRewardCaptureCounters();
        Assert.Equal(1, counters.CaptureFaults);
        Assert.Equal(0, counters.ObservedAtEmptySnapshot);
    }

    private static string RedemptionLine(
        string text,
        string? rewardTag = RewardId,
        string messageId = MessageId,
        string extraTags = "")
    {
        var reward = rewardTag is null ? string.Empty : "custom-reward-id=" + rewardTag + ";";
        return "@badge-info=subscriber/3;badges=subscriber/3;color=#1E90FF;" + reward
            + "display-name=TestViewer1;emotes=;first-msg=0;flags=;id=" + messageId + ";mod=0;returning-chatter=0;"
            + "room-id=" + RoomId + ";" + extraTags + "subscriber=1;tmi-sent-ts=" + SentAtUnixMs + ";turbo=0;"
            + "user-id=" + UserId + ";user-type= "
            + ":" + Login + "!" + Login + "@" + Login + ".tmi.twitch.tv PRIVMSG #probechannel_test :" + text;
    }

    private static ChatMessage ParseLine(string line)
    {
        var ircMessage = IrcParser.ParseMessage(line);
        return new ChatMessage("purgebot_test", ircMessage, new MessageEmoteCollection(), false, "prefix", "suffix");
    }
}
