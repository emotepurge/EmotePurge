using Xunit;
using EmotePurge.Core.Services;
using EmotePurge.Worker;

namespace EmotePurge.Worker.Tests;

public class TwitchJoinNoticeParserTests
{
    // Recorded 2026-09-08 (join probe); the channel name is invented.
    private const string RecordedLimitLine =
        "@msg-id=msg_concurrent_channel_limit_reached :tmi.twitch.tv NOTICE #somechannel :You are connected to too many chat channels. Please close some existing chat connections to join a new chat.";

    [Fact]
    public void RecordedLimitLine_IsParsed()
    {
        Assert.True(TwitchJoinNoticeParser.IsNotice(RecordedLimitLine));
        Assert.True(TwitchJoinNoticeParser.TryParse(RecordedLimitLine, out var reason, out var channel));
        Assert.Equal(TwitchJoinFailureReasons.ConcurrentChannelLimit, reason);
        Assert.Equal("somechannel", channel);
    }

    [Fact]
    public void BannedLine_IsParsed()
    {
        const string line = "@msg-id=msg_banned :tmi.twitch.tv NOTICE #somechannel :You are permanently banned from talking in somechannel.";

        Assert.True(TwitchJoinNoticeParser.TryParse(line, out var reason, out var channel));
        Assert.Equal(TwitchJoinFailureReasons.Banned, reason);
        Assert.Equal("somechannel", channel);
    }

    [Theory]
    [InlineData("@msg-id=msg_banned;room-id=1 :tmi.twitch.tv NOTICE #c :x")]
    [InlineData("@room-id=1;msg-id=msg_banned;other=2 :tmi.twitch.tv NOTICE #c :x")]
    [InlineData("@room-id=1;other=2;msg-id=msg_banned :tmi.twitch.tv NOTICE #c :x")]
    public void MsgId_AtAnyPosition_IsFound(string line)
    {
        Assert.True(TwitchJoinNoticeParser.TryParse(line, out var reason, out _));
        Assert.Equal(TwitchJoinFailureReasons.Banned, reason);
    }

    [Fact]
    public void EscapedTagValues_DoNotBreakTheMsgIdLookup()
    {
        const string line = @"@system-msg=a\sb\:c\\d;msg-id=msg_banned;x=\s :tmi.twitch.tv NOTICE #c :x";

        Assert.True(TwitchJoinNoticeParser.TryParse(line, out var reason, out var channel));
        Assert.Equal(TwitchJoinFailureReasons.Banned, reason);
        Assert.Equal("c", channel);
    }

    [Fact]
    public void ChannelWithoutHash_NoticeToAsterisk_IsRejected()
    {
        const string line = "@msg-id=msg_banned :tmi.twitch.tv NOTICE * :x";

        Assert.True(TwitchJoinNoticeParser.IsNotice(line));
        Assert.False(TwitchJoinNoticeParser.TryParse(line, out _, out _));
    }

    [Fact]
    public void NoticeWithoutTagBlock_IsANoticeButNotParsed()
    {
        const string line = ":tmi.twitch.tv NOTICE #c :You are banned";

        Assert.True(TwitchJoinNoticeParser.IsNotice(line));
        Assert.False(TwitchJoinNoticeParser.TryParse(line, out _, out _));
    }

    [Fact]
    public void SuspendedLine_IsParsed()
    {
        const string line = "@msg-id=msg_channel_suspended :tmi.twitch.tv NOTICE #SomeChannel :This channel does not exist or has been suspended.";

        Assert.True(TwitchJoinNoticeParser.TryParse(line, out var reason, out var channel));
        Assert.Equal(TwitchJoinFailureReasons.ChannelSuspended, reason);
        Assert.Equal("somechannel", channel);
    }

    [Theory]
    [InlineData("@msg-id=msg_channel_suspended_extra :tmi.twitch.tv NOTICE #c :x")]
    [InlineData("@msg-id=msg_suspended :tmi.twitch.tv NOTICE #c :x")]
    [InlineData("@msg-id=bad_ban_admin;target-user-id=3 :tmi.twitch.tv NOTICE #c :nope")]
    [InlineData("@msg-id= :tmi.twitch.tv NOTICE #c :x")]
    [InlineData("@msg-id=msg_banned_extra :tmi.twitch.tv NOTICE #c :x")]
    public void OtherMsgIds_AreRejected(string line)
    {
        Assert.True(TwitchJoinNoticeParser.IsNotice(line));
        Assert.False(TwitchJoinNoticeParser.TryParse(line, out _, out _));
    }

    [Fact]
    public void Privmsg_MentioningNoticeInItsText_IsNotANotice()
    {
        const string line = "@msg-id=msg_banned :u!u@u.tmi.twitch.tv PRIVMSG #c :NOTICE #c msg_banned";

        Assert.False(TwitchJoinNoticeParser.IsNotice(line));
        Assert.False(TwitchJoinNoticeParser.TryParse(line, out _, out _));
    }

    [Fact]
    public void Usernotice_IsNotANotice()
    {
        const string line = "@msg-id=msg_banned :tmi.twitch.tv USERNOTICE #c :x";

        Assert.False(TwitchJoinNoticeParser.IsNotice(line));
        Assert.False(TwitchJoinNoticeParser.TryParse(line, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("@")]
    [InlineData("@msg-id=msg_banned")]
    [InlineData("@msg-id=msg_banned ")]
    [InlineData("@msg-id=msg_banned :tmi.twitch.tv")]
    [InlineData("@msg-id=msg_banned :tmi.twitch.tv NOTICE")]
    [InlineData("@msg-id=msg_banned :tmi.twitch.tv NOTICE #")]
    [InlineData(":")]
    [InlineData("NOTICE")]
    public void TruncatedOrEmptyLines_NeverThrow_AndAreRejected(string line)
    {
        Assert.False(TwitchJoinNoticeParser.TryParse(line, out _, out _));
        _ = TwitchJoinNoticeParser.IsNotice(line);
    }

    [Fact]
    public void ChannelAtEndOfLine_WithoutTrailing_IsParsed()
    {
        Assert.True(TwitchJoinNoticeParser.TryParse(
            "@msg-id=msg_banned :tmi.twitch.tv NOTICE #c\r\n", out _, out var channel));
        Assert.Equal("c", channel);
    }

    [Fact]
    public void UppercaseChannel_IsLowercased()
    {
        Assert.True(TwitchJoinNoticeParser.TryParse(
            "@msg-id=msg_banned :tmi.twitch.tv NOTICE #HandOfBlood :x", out _, out var channel));
        Assert.Equal("handofblood", channel);
    }

    [Fact]
    public void UppercaseMsgIdOrLowercaseCommand_IsRejected()
    {
        Assert.False(TwitchJoinNoticeParser.TryParse(
            "@msg-id=MSG_BANNED :tmi.twitch.tv NOTICE #c :x", out _, out _));
        Assert.False(TwitchJoinNoticeParser.IsNotice("@msg-id=msg_banned :tmi.twitch.tv notice #c :x"));
    }
}
