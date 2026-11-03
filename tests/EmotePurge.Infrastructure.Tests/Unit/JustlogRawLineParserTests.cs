using EmotePurge.Infrastructure.ChatLogArchive;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// All lines below are hand-written and synthetic: invented logins (e.g. alice_test), invented
// numeric ids (e.g. 100000001), invented message text. No recorded IRC line, real user-id or real
// chat message ever goes into this repo (Fixture-Regel, design doc Eng-Review 5A).
public class JustlogRawLineParserTests
{
    private const string ActionMarker = "\u0001";

    [Fact]
    public void TryParse_OrdinaryPrivmsgWithThreeBadges_ReturnsMessageWithBadgesInOrder()
    {
        var line = "@badge-info=;badges=broadcaster/1,subscriber/12,vip/1;room-id=200000001;"
            + "tmi-sent-ts=1700000000000;user-id=100000001 "
            + ":alice_test!alice_test@alice_test.tmi.twitch.tv PRIVMSG #faketown_test :hey everyone";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out var ircCommand);

        Assert.True(ok);
        Assert.Null(ircCommand);
        Assert.Equal("100000001", message.UserId);
        Assert.Equal("200000001", message.RoomId);
        Assert.Null(message.SourceRoomId);
        Assert.Equal("hey everyone", message.Text);
        Assert.Equal(
            [
                new KeyValuePair<string, string>("broadcaster", "1"),
                new KeyValuePair<string, string>("subscriber", "12"),
                new KeyValuePair<string, string>("vip", "1"),
            ],
            message.Badges);
    }

    [Fact]
    public void TryParse_PrivmsgWithoutBadges_ReturnsEmptyBadgeList()
    {
        var line = "@badges=;room-id=200000001;tmi-sent-ts=1700000005000;user-id=100000002 "
            + ":bob_test!bob_test@bob_test.tmi.twitch.tv PRIVMSG #faketown_test :hi there";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.Empty(message.Badges);
    }

    [Fact]
    public void TryParse_PrivmsgWithBotBadge_IncludesBotBadgeSetId()
    {
        var line = "@badges=bot-badge/1;room-id=200000001;tmi-sent-ts=1700000030000;user-id=100000007 "
            + ":purgebot_test!purgebot_test@purgebot_test.tmi.twitch.tv PRIVMSG #faketown_test :purge stats";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.Contains(new KeyValuePair<string, string>("bot-badge", "1"), message.Badges);
    }

    [Fact]
    public void TryParse_ActionMessage_UnpacksActionFraming()
    {
        var line = "@badges=;room-id=200000001;tmi-sent-ts=1700000010000;user-id=100000003 "
            + ":carol_test!carol_test@carol_test.tmi.twitch.tv PRIVMSG #faketown_test "
            + $":{ActionMarker}ACTION waves hello{ActionMarker}";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.Equal("waves hello", message.Text);
    }

    [Fact]
    public void TryParse_TextWithColonAndSpaces_StaysIntact()
    {
        var line = "@badges=;room-id=200000001;tmi-sent-ts=1700000030000;user-id=100000007 "
            + ":purgebot_test!purgebot_test@purgebot_test.tmi.twitch.tv PRIVMSG #faketown_test "
            + ":purge stats: 0 emotes removed";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.Equal("purge stats: 0 emotes removed", message.Text);
    }

    [Fact]
    public void TryParse_WithEscapedSpaceInAnUnrelatedTag_StillParsesTheMessage()
    {
        // display-name isn't part of ChatLogMessage, but a PRIVMSG line always carries it, so this
        // proves an escaped tag value elsewhere in the same tag list doesn't break parsing (the
        // naive split-on-';' approach is only safe because IRCv3 escapes a literal ';' as "\:" —
        // this line checks the '\s' -> space case specifically).
        var line = "@display-name=Jimmy\\sBob;badges=;room-id=200000001;tmi-sent-ts=1700000000000;user-id=100000001 "
            + ":jimmybob_test!jimmybob_test@jimmybob_test.tmi.twitch.tv PRIVMSG #faketown_test :hello";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out var ircCommand);

        Assert.True(ok);
        Assert.Null(ircCommand);
        Assert.Equal("100000001", message.UserId);
        Assert.Equal("hello", message.Text);
    }

    [Fact]
    public void TryParse_SharedChatWithDifferentSourceRoomId_SetsBothRoomIds()
    {
        var line = "@badges=;room-id=200000001;source-room-id=200000009;tmi-sent-ts=1700000025000;user-id=100000006 "
            + ":frank_test!frank_test@frank_test.tmi.twitch.tv PRIVMSG #faketown_test :hello from another channel";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.Equal("200000001", message.RoomId);
        Assert.Equal("200000009", message.SourceRoomId);
        Assert.NotEqual(message.RoomId, message.SourceRoomId);
    }

    [Fact]
    public void TryParse_LineWithOtherSourceMarker_SetsHasOtherSourceMarkersTrue()
    {
        var line = "@badges=;room-id=200000001;source-id=cccccccc-cccc-cccc-cccc-cccccccccccc;"
            + "tmi-sent-ts=1700000035000;user-id=100000008 "
            + ":grace_test!grace_test@grace_test.tmi.twitch.tv PRIVMSG #faketown_test :hello via a session";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.True(message.HasOtherSourceMarkers);
    }

    [Fact]
    public void TryParse_OrdinaryLineWithoutSourceTags_SetsHasOtherSourceMarkersFalse()
    {
        var line = "@badges=;room-id=200000001;tmi-sent-ts=1700000040000;user-id=100000009 "
            + ":heidi_test!heidi_test@heidi_test.tmi.twitch.tv PRIVMSG #faketown_test :hello";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.False(message.HasOtherSourceMarkers);
    }

    [Fact]
    public void TryParse_EmptySourceRoomIdWithOtherMarkerSet_SourceRoomIdIsNullAndHasOtherSourceMarkersTrue()
    {
        // The combination that carries the undecidable case: an empty source-room-id normalizes
        // to null exactly like an absent one, but the other marker's presence still has to survive
        // into the flag so SharedChatRule.Classify can tell it apart from an ordinary message.
        var line = "@badges=;room-id=200000001;source-badges=;source-room-id=;"
            + "tmi-sent-ts=1700000045000;user-id=100000010 "
            + ":ivan_test!ivan_test@ivan_test.tmi.twitch.tv PRIVMSG #faketown_test :hello ambiguous session";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.Null(message.SourceRoomId);
        Assert.True(message.HasOtherSourceMarkers);
    }

    [Fact]
    public void TryParse_Clearchat_ReturnsFalseWithCommandSet()
    {
        var line = "@ban-duration=600;room-id=200000001;target-user-id=100000004;tmi-sent-ts=1700000015000 "
            + ":tmi.twitch.tv CLEARCHAT #faketown_test :dave_test";

        var ok = JustlogRawLineParser.TryParse(line, out _, out var ircCommand);

        Assert.False(ok);
        Assert.Equal("CLEARCHAT", ircCommand);
    }

    [Fact]
    public void TryParse_UsernoticeWithTrailingText_ReturnsFalseWithCommandSet()
    {
        // The live worker never counts USERNOTICE either (TwitchChatManager.OnMessageReceived only
        // wires up PRIVMSG) — a trailing message on a sub/raid notice must not be mistaken for chat.
        var line = "@msg-id=sub;room-id=200000001;tmi-sent-ts=1700000020000;user-id=100000005 "
            + ":tmi.twitch.tv USERNOTICE #faketown_test :Thanks for subbing!";

        var ok = JustlogRawLineParser.TryParse(line, out _, out var ircCommand);

        Assert.False(ok);
        Assert.Equal("USERNOTICE", ircCommand);
    }

    [Fact]
    public void TryParse_PrivmsgLineWithoutTrailing_ReturnsFalseWithoutCommand()
    {
        var line = "@room-id=200000001;tmi-sent-ts=1700000000000;user-id=100000001 "
            + ":alice_test!alice_test@alice_test.tmi.twitch.tv PRIVMSG #faketown_test";

        var ok = JustlogRawLineParser.TryParse(line, out _, out var ircCommand);

        Assert.False(ok);
        Assert.Null(ircCommand);
    }

    [Fact]
    public void TryParse_EmptyLine_ReturnsFalseWithoutCommand()
    {
        var ok = JustlogRawLineParser.TryParse(string.Empty, out _, out var ircCommand);

        Assert.False(ok);
        Assert.Null(ircCommand);
    }

    [Fact]
    public void TryParse_LineWithoutLeadingTagsOrPrefix_ReturnsFalseWithoutCommand()
    {
        var ok = JustlogRawLineParser.TryParse("this is not an irc line at all", out _, out var ircCommand);

        Assert.False(ok);
        Assert.Null(ircCommand);
    }

    [Fact]
    public void TryParse_UnparsableTimestamp_ReturnsFalseWithoutCommand()
    {
        var line = "@room-id=200000001;tmi-sent-ts=not-a-number;user-id=100000001 "
            + ":alice_test!alice_test@alice_test.tmi.twitch.tv PRIVMSG #faketown_test :hey";

        var ok = JustlogRawLineParser.TryParse(line, out _, out var ircCommand);

        Assert.False(ok);
        Assert.Null(ircCommand);
    }

    [Fact]
    public void TryParse_TimestampAboveDateTimeOffsetRange_ReturnsFalseWithoutCommand()
    {
        // long.MaxValue parses fine as a long but is far past what FromUnixTimeMilliseconds can
        // turn into a DateTimeOffset — that call throws unless the range is checked first, and an
        // uncaught throw here would abort the whole backfill block for one broken foreign line.
        var line = $"@room-id=200000001;tmi-sent-ts={long.MaxValue};user-id=100000001 "
            + ":alice_test!alice_test@alice_test.tmi.twitch.tv PRIVMSG #faketown_test :hey";

        var ok = JustlogRawLineParser.TryParse(line, out _, out var ircCommand);

        Assert.False(ok);
        Assert.Null(ircCommand);
    }

    [Fact]
    public void TryParse_TimestampBelowDateTimeOffsetRange_ReturnsFalseWithoutCommand()
    {
        var line = $"@room-id=200000001;tmi-sent-ts={long.MinValue};user-id=100000001 "
            + ":alice_test!alice_test@alice_test.tmi.twitch.tv PRIVMSG #faketown_test :hey";

        var ok = JustlogRawLineParser.TryParse(line, out _, out var ircCommand);

        Assert.False(ok);
        Assert.Null(ircCommand);
    }

    [Fact]
    public void TryParse_ValidTimestamp_ProducesUtcKindDateTime()
    {
        var line = "@room-id=200000001;tmi-sent-ts=1700000000000;user-id=100000001 "
            + ":alice_test!alice_test@alice_test.tmi.twitch.tv PRIVMSG #faketown_test :hey";

        var ok = JustlogRawLineParser.TryParse(line, out var message, out _);

        Assert.True(ok);
        Assert.Equal(DateTimeKind.Utc, message.SentAtUtc.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime, message.SentAtUtc);
    }
}
