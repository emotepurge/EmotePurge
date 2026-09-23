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
        // command. IsLikelyIrcCommand exists exactly to catch this: "this" is not all upper-case
        // letters/digits, so it is replaced with the unparsed marker instead of logged verbatim.
        var line = "@reply-parent-msg-body=this has spaces;room-id=111111111 PRIVMSG #targetchannel_test :hi";

        var redacted = TwitchLibRawLineRedaction.Redact(line);

        Assert.Equal("command=<unparsed>, tagKeys=[reply-parent-msg-body]", redacted);
        Assert.DoesNotContain("this", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("has", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("spaces", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_RealCommandWord_IsKeptVerbatim()
    {
        // The guard must not reject genuine IRC commands, including Twitch's numeric replies.
        Assert.Equal("command=JOIN, tagKeys=[]", TwitchLibRawLineRedaction.Redact("JOIN #targetchannel_test"));
        Assert.Equal("command=372, tagKeys=[]", TwitchLibRawLineRedaction.Redact(":tmi.twitch.tv 372 :- test"));
    }

    [Fact]
    public void Redact_CommandLongerThanCap_IsTruncated()
    {
        var command = new string('X', 100);
        var line = command + " rest";

        var redacted = TwitchLibRawLineRedaction.Redact(line);

        Assert.Equal($"command={new string('X', 32)}, tagKeys=[]", redacted);
    }
}
