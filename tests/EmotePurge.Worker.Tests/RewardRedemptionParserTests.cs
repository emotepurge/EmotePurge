using EmotePurge.Core.Chat;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Worker.WishCapture;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The pure half of the reward capture (plan #486 2.1, AK47): plain values in, an observation or nothing out. Which
// TwitchLib field feeds which value is RewardRedemptionTwitchLibTests' subject.
public class RewardRedemptionParserTests
{
    private const string RoomId = "111111111";
    private const string RewardId = "aaaa1111-2222-3333-4444-555555555555";
    private const string MessageId = "bbbb1111-2222-3333-4444-666666666666";
    private const string UserId = "333333333";
    private const string Login = "testviewer1";

    private static readonly DateTime SentAtUtc = new(2026, 11, 19, 18, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void OwnRoomWithEveryField_YieldsTheSevenFields()
    {
        var observation = Parse(text: "7tv.app/emotes/01ARZ3NDEKTSV4RRFFQ69G5FAV please");

        Assert.Equal(
            new RewardObservation(RoomId, RewardId, MessageId, UserId, Login, "7tv.app/emotes/01ARZ3NDEKTSV4RRFFQ69G5FAV please", SentAtUtc),
            observation);
    }

    [Fact]
    public void NoRewardId_YieldsNothing()
    {
        Assert.Null(Parse(rewardId: null));
    }

    [Theory]
    [InlineData(MessageOrigin.Foreign)]
    [InlineData(MessageOrigin.Indeterminate)]
    public void RoomOtherThanOwn_YieldsNothing(MessageOrigin origin)
    {
        Assert.Null(Parse(origin: origin));
    }

    [Fact]
    public void EmptyText_StillYieldsAnObservation()
    {
        Assert.Equal(string.Empty, Parse(text: "")!.Text);
        Assert.Equal(string.Empty, Parse(text: null)!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaa1111;badges=")]
    [InlineData("aaaa 1111")]
    [InlineData("aaaa@1111")]
    [InlineData("aaaa_1111")]
    public void RewardIdOutsideItsShape_YieldsNothing(string rewardId)
    {
        Assert.Null(Parse(rewardId: rewardId));
    }

    [Fact]
    public void RewardIdAndMessageId_AreBoundedAtSixtyFourCharacters()
    {
        var atLimit = new string('a', WishLimits.RewardIdMaxLength);
        var overLimit = new string('a', WishLimits.RewardIdMaxLength + 1);

        Assert.NotNull(Parse(rewardId: atLimit, messageId: atLimit));
        Assert.Null(Parse(rewardId: overLimit));
        Assert.Null(Parse(messageId: overLimit));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bbbb=1111")]
    public void MessageIdOutsideItsShape_YieldsNothing(string? messageId)
    {
        Assert.Null(Parse(messageId: messageId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12a45")]
    [InlineData("123456789012345678901")]
    [InlineData("1111@badge-info=")]
    public void RoomIdOrUserIdOutsideItsShape_YieldsNothing(string? id)
    {
        Assert.Null(Parse(roomId: id));
        Assert.Null(Parse(userId: id));
    }

    [Fact]
    public void TwitchIds_AreBoundedAtTwentyDigits()
    {
        var twenty = new string('9', RewardRedemptionParser.MaxTwitchIdLength);

        Assert.NotNull(Parse(roomId: twenty, userId: twenty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test viewer")]
    [InlineData("testviewer!x")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    public void LoginOutsideItsShape_YieldsNothing(string? login)
    {
        Assert.Null(Parse(login: login));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("Test_Viewer_1")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    public void LoginWithinItsShape_IsKeptVerbatim(string login)
    {
        Assert.Equal(login, Parse(login: login)!.Login);
    }

    [Fact]
    public void SendTimeMissingOrAtTheEpoch_YieldsNothing()
    {
        Assert.Null(Parse(sentAtUtc: default(DateTimeOffset).UtcDateTime));
        Assert.Null(Parse(sentAtUtc: DateTime.UnixEpoch));
        Assert.NotNull(Parse(sentAtUtc: DateTime.UnixEpoch.AddMilliseconds(1)));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void SendTimeNotInUtc_YieldsNothing(DateTimeKind kind)
    {
        Assert.Null(Parse(sentAtUtc: DateTime.SpecifyKind(SentAtUtc, kind)));
    }

    [Fact]
    public void ControlCharacters_AreRemovedFromTheText()
    {
        Assert.Equal("ACTION sadge pls", Parse(text: "\u0001ACTION sadge\t pls\u0001")!.Text);
        Assert.Equal("ab", Parse(text: "a\u0000\u001f\u007f\u0085b")!.Text);
    }

    [Fact]
    public void TextOverFiveHundredCodePoints_IsCutAtFiveHundred()
    {
        var text = new string('x', WishLimits.RequestTextMaxLength + 25);

        Assert.Equal(new string('x', WishLimits.RequestTextMaxLength), Parse(text: text)!.Text);
    }

    [Fact]
    public void TextCut_CountsCodePoints_AndNeverSplitsASurrogatePair()
    {
        // U+1F600 is two UTF-16 units; 499 letters plus two emoji keep 499 + 1 code points.
        var text = new string('x', WishLimits.RequestTextMaxLength - 1) + "\U0001F600\U0001F600";

        var cut = Parse(text: text)!.Text;

        Assert.Equal(new string('x', WishLimits.RequestTextMaxLength - 1) + "\U0001F600", cut);
    }

    [Fact]
    public void ControlCharacters_DoNotCountTowardsTheLimit()
    {
        var text = new string('\u0001', 50) + new string('x', WishLimits.RequestTextMaxLength);

        Assert.Equal(new string('x', WishLimits.RequestTextMaxLength), Parse(text: text)!.Text);
    }

    [Fact]
    public void LoneSurrogate_BecomesTheReplacementCharacter()
    {
        Assert.Equal("a�b", Parse(text: "a\uD800b")!.Text);
    }

    private static RewardObservation? Parse(
        string? rewardId = RewardId,
        MessageOrigin origin = MessageOrigin.Own,
        string? roomId = RoomId,
        string? messageId = MessageId,
        string? userId = UserId,
        string? login = Login,
        string? text = "sadge",
        DateTime? sentAtUtc = null) =>
        RewardRedemptionParser.Parse(rewardId, origin, roomId, messageId, userId, login, text, sentAtUtc ?? SentAtUtc);
}
