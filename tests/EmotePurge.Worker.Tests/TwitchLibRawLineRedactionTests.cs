using Xunit;

namespace EmotePurge.Worker.Tests;

public class TwitchLibRawLineRedactionTests
{
    [Fact]
    public void Redact_NullLine_ReturnsNoLineMarker() =>
        Assert.Equal("command=<none>, tagKeys=[]", TwitchLibRawLineRedaction.Redact(null));

    [Fact]
    public void Redact_EmptyLine_ReturnsNoLineMarker() =>
        Assert.Equal("command=<none>, tagKeys=[]", TwitchLibRawLineRedaction.Redact(string.Empty));

    [Fact]
    public void Redact_OrdinaryPrivmsgLine_KeepsCommandAndTagKeysOnly()
    {
        // Synthetic, never recorded chat — same rule as the rest of this repo.
        var line = "@badge-info=;badges=;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "mod=0;room-id=111111111;subscriber=0;user-id=333333333 "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello there everyone";

        var redacted = TwitchLibRawLineRedaction.Redact(line);

        Assert.Equal(
            "command=PRIVMSG, tagKeys=[badge-info,badges,display-name,id,mod,room-id,subscriber,user-id]",
            redacted);
        Assert.DoesNotContain("TestUser1", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("333333333", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("#targetchannel_test", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_LineWithoutTags_KeepsCommandOnly()
    {
        var line = ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.Equal("command=PRIVMSG, tagKeys=[]", TwitchLibRawLineRedaction.Redact(line));
    }

    [Fact]
    public void Redact_ServerPing_KeepsCommandOnly() =>
        Assert.Equal("command=PING, tagKeys=[]", TwitchLibRawLineRedaction.Redact("PING :tmi.twitch.tv"));

    [Fact]
    public void Redact_LineWithNoSpaceAtAll_HasNoCommand()
    {
        // Exactly the shape a truncated/malformed line can take — the whole point of this class is
        // to tolerate it instead of throwing.
        var line = "@badge-info=;id=aaaa1111-1111-1111-1111-111111111111";

        Assert.Equal("command=<none>, tagKeys=[badge-info,id]", TwitchLibRawLineRedaction.Redact(line));
    }

    [Fact]
    public void Redact_TagBlockWithNoValues_KeysOnlyNoEquals()
    {
        // A splice or truncation can leave a bare key with no '=' at all.
        var line = "@badge-info;id=aaaa1111 PRIVMSG #targetchannel_test :hi";

        Assert.Equal("command=PRIVMSG, tagKeys=[badge-info,id]", TwitchLibRawLineRedaction.Redact(line));
    }

    [Fact]
    public void Redact_TagValueContainingUnescapedSpace_DoesNotLeakItAsCommand()
    {
        // A well-formed line never has a raw space inside a tag value (IRCv3 escapes it as "\s"),
        // but a line that reaches here failed to parse — so a corrupted/spliced (#114) line can. The
        // naive "cut the tag block at the first space" would then land mid-value, and everything
        // after it — a fragment of that value, here "this" — would otherwise be reported as the IRC
        // command. IsLikelyIrcCommand exists exactly to catch this: "this" is not a token TwitchLib
        // itself recognises as a command, so it is replaced with the unparsed marker instead of
        // logged verbatim.
        var line = "@reply-parent-msg-body=this has spaces;room-id=111111111 PRIVMSG #targetchannel_test :hi";

        var redacted = TwitchLibRawLineRedaction.Redact(line);

        Assert.Equal("command=<unparsed>, tagKeys=[reply-parent-msg-body]", redacted);
        Assert.DoesNotContain("this", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("has", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("spaces", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_UppercaseChatWordInCommandPosition_IsNotMistakenForACommand()
    {
        // Codex Sol review finding (P2): the previous guard accepted *any* all-upper-case word, so
        // an unescaped space inside a free-text tag's value — the same corruption shape as the test
        // above — could put an arbitrary upper-case chat-text fragment exactly where the command
        // goes, and it would sail through unredacted because "SECRET" is, shape-wise, just as valid
        // an all-caps token as "PRIVMSG". The allow-list closes that: "SECRET" is not a command
        // TwitchLib itself recognises (TwitchLib.Client.Parsing.IrcParser.ParseCommand), so it is
        // replaced with the unparsed marker instead.
        var line = "@reply-parent-msg-body=hello SECRET :nick!nick@nick.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        var redacted = TwitchLibRawLineRedaction.Redact(line);

        Assert.Equal("command=<unparsed>, tagKeys=[reply-parent-msg-body]", redacted);
        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RealCommandWord_IsKeptVerbatim()
    {
        // The guard must not reject genuine IRC commands, including Twitch's numeric replies and
        // every named command TwitchLib.Client 4.0.1's IrcParser.ParseCommand switch recognises.
        Assert.Equal("command=JOIN, tagKeys=[]", TwitchLibRawLineRedaction.Redact("JOIN #targetchannel_test"));
        Assert.Equal("command=372, tagKeys=[]", TwitchLibRawLineRedaction.Redact(":tmi.twitch.tv 372 :- test"));

        foreach (var command in new[]
                 {
                     "PRIVMSG", "NOTICE", "PING", "PONG", "CLEARCHAT", "CLEARMSG", "USERSTATE",
                     "GLOBALUSERSTATE", "NICK", "JOIN", "PART", "PASS", "CAP", "WHISPER",
                     "SERVERCHANGE", "RECONNECT", "ROOMSTATE", "USERNOTICE", "MODE",
                 })
        {
            Assert.Equal($"command={command}, tagKeys=[]", TwitchLibRawLineRedaction.Redact(command + " rest"));
        }
    }

    [Fact]
    public void Redact_UnknownAllUppercaseToken_IsNotMistakenForACommand()
    {
        // Distinct from the SECRET case above: this is a *standalone* line whose first word merely
        // looks command-shaped (all upper-case) without any tag-value corruption involved — the
        // allow-list must reject it purely on not being a token TwitchLib recognises.
        Assert.Equal(
            "command=<unparsed>, tagKeys=[]",
            TwitchLibRawLineRedaction.Redact("GIBBERISH #targetchannel_test :hi"));
    }

    [Fact]
    public void Redact_CommandLongerThanCap_IsUnparsedNotTruncatedLiteral()
    {
        // No real IRC/Twitch command is anywhere near 32 characters (the longest,
        // GLOBALUSERSTATE, is 15), so anything that needs truncating can never match the allow-list
        // — the cap exists only to bound the work done on a pathological input, not to produce a
        // truncated-but-still-logged command word. Pins that the truncated garbage itself never
        // reaches the output.
        var command = new string('X', 100);
        var line = command + " rest";

        var redacted = TwitchLibRawLineRedaction.Redact(line);

        Assert.Equal("command=<unparsed>, tagKeys=[]", redacted);
        Assert.DoesNotContain("X", redacted, StringComparison.Ordinal);
    }
}
