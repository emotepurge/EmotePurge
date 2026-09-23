using Xunit;

namespace EmotePurge.Worker.Tests;

public class IrcLineSpliceRuleTests
{
    // Everything a real Twitch reply line carries before reply-parent-msg-body. Synthetic
    // (invented ids/logins/text), never recorded chat — same rule as everywhere else in this repo.
    private const string ReplyTagsBeforeParentBody =
        "@badge-info=;badges=;client-nonce=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;color=#1E90FF;"
        + "display-name=TestUser1;emotes=;first-msg=0;flags=;id=aaaa1111-1111-1111-1111-111111111111;mod=0;"
        + "reply-parent-display-name=TestUser0;";

    private const string ReplyTagsAfterParentBody =
        ";reply-parent-msg-id=eeee5555-5555-5555-5555-555555555555;reply-parent-user-id=555555555;"
        + "reply-parent-user-login=testuser0;reply-thread-parent-display-name=TestUser0;"
        + "reply-thread-parent-msg-id=eeee5555-5555-5555-5555-555555555555;"
        + "reply-thread-parent-user-login=testuser0;returning-chatter=0;room-id=111111111;subscriber=0;"
        + "tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
        + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :@TestUser0 alles klar";

    [Fact]
    public void IsSpliced_NullLine_ReturnsFalse() => Assert.False(IrcLineSpliceRule.IsSpliced(null));

    [Fact]
    public void IsSpliced_EmptyLine_ReturnsFalse() => Assert.False(IrcLineSpliceRule.IsSpliced(string.Empty));

    [Fact]
    public void IsSpliced_SplicedTagBlockWithSecondAt_ReturnsTrue()
    {
        // Synthetic: the splice lands inside the value of a typed tag ("subscriber=0"), so the
        // second line's tag block starts mid-value instead of after a ';'.
        var line = "@badge-info=;badges=;color=#0000FF;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "mod=0;room-id=111111111;subscriber=0@badge-info=;badges=;display-name=TestUser2;"
            + "id=bbbb2222-2222-2222-2222-222222222222;mod=0;room-id=111111111;subscriber=0;"
            + "tmi-sent-ts=1694000000000;turbo=0;user-id=222222222;user-type= "
            + ":testuser2!testuser2@testuser2.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.True(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_OrdinaryLineWithMentionInMessageText_ReturnsFalse()
    {
        var line = "@badge-info=;badges=;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "mod=0;room-id=111111111;subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello @othertestuser1 how are you";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_OrdinaryLineWithHostmaskPrefix_ReturnsFalse()
    {
        var line = "@badge-info=;badges=;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "mod=0;room-id=111111111;subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=444444444;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_LineWithoutTags_ReturnsFalse()
    {
        var line = ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_ServerPing_ReturnsFalse() => Assert.False(IrcLineSpliceRule.IsSpliced("PING :tmi.twitch.tv"));

    [Fact]
    public void IsSpliced_NoSpaceWithSecondAt_ReturnsTrue()
    {
        // Synthetic: no space anywhere in the line, so the whole line is the tag block. A second
        // '@' here can only mean a splice landed with no trailing IRC command/params at all.
        var line = "@badge-info=;badges=;id=aaaa1111-1111-1111-1111-111111111111@badge-info=;id=bbbb2222-2222-2222-2222-222222222222";

        Assert.True(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_NoSpaceWithoutSecondAt_ReturnsFalse()
    {
        // Synthetic: no space anywhere in the line and only the leading '@' — the whole line is
        // the tag block and it is not spliced.
        var line = "@badge-info=;badges=;id=aaaa1111-1111-1111-1111-111111111111";

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void TagBlockForLog_NullLine_ReturnsEmptyString() => Assert.Equal(string.Empty, IrcLineSpliceRule.TagBlockForLog(null));

    [Fact]
    public void TagBlockForLog_EmptyLine_ReturnsEmptyString() => Assert.Equal(string.Empty, IrcLineSpliceRule.TagBlockForLog(string.Empty));

    [Fact]
    public void TagBlockForLog_LineWithSpace_ReturnsSegmentUpToFirstSpace()
    {
        var line = "@badge-info=;id=aaaa1111-1111-1111-1111-111111111111 :testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        // badge-info is an identifying tag (#246) and is redacted even though its value happens to
        // be empty here; id is not and survives untouched.
        Assert.Equal("@badge-info=<entfernt>;id=aaaa1111-1111-1111-1111-111111111111", IrcLineSpliceRule.TagBlockForLog(line));
    }

    [Fact]
    public void TagBlockForLog_LineWithoutSpace_ReturnsWholeLine()
    {
        var line = "@badge-info=;id=aaaa1111-1111-1111-1111-111111111111";

        Assert.Equal("@badge-info=<entfernt>;id=aaaa1111-1111-1111-1111-111111111111", IrcLineSpliceRule.TagBlockForLog(line));
    }

    [Fact]
    public void TagBlockForLog_TagBlockLongerThanMaxLength_TruncatesToMaxLength()
    {
        var tagBlock = new string('a', 20);
        var line = tagBlock + " rest of line ignored";

        Assert.Equal(new string('a', 10), IrcLineSpliceRule.TagBlockForLog(line, maxLength: 10));
    }

    [Fact]
    public void TagBlockForLog_TagBlockShorterThanMaxLength_ReturnsUnchanged()
    {
        var tagBlock = new string('a', 5);
        var line = tagBlock + " rest of line ignored";

        Assert.Equal(tagBlock, IrcLineSpliceRule.TagBlockForLog(line, maxLength: 10));
    }

    [Fact]
    public void IsSpliced_MentionInsideReplyParentBody_ReturnsFalse()
    {
        // The case that made the first version of this rule unusable: Twitch's reply UI prepends
        // "@nutzername " to every reply, reply-parent-msg-body carries the parent verbatim, and the
        // IRCv3 escaping leaves '@' alone. A bare second '@' therefore fires on every reply.
        var line = ReplyLine(@"@TestUser0\shallo\sdu");

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_ReplyParentBodyImitatingATagBlock_ReturnsFalse()
    {
        // Chat text under a stranger's control, shaped exactly like a spliced tag block. The ';'
        // is escaped as "\:", so the whole thing stays inside the one reply-parent-msg-body tag,
        // whose value the rule skips. Anything else would hand every chatter a sentinel trigger.
        var line = ReplyLine(@"@badge-info=\:badges=\shaha");

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_ReplyParentBodyEndingInATagBlockStart_ReturnsFalse()
    {
        // Same imitation, this time as the entire parent body and therefore at the very end of the
        // tag's value — the position where a real splice would sit.
        var line = ReplyLine("@badge-info=");

        Assert.False(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_SpliceIntoAReplyLineOutsideTheParentBody_ReturnsTrue()
    {
        // A reply line — so the parent body carries its usual mention — cut open inside the value
        // of a typed tag. Skipping free-text values must not blind the rule to this.
        var line = ReplyLine(@"@TestUser0\shallo\sdu")
            .Replace("room-id=111111111;subscriber=0;", "room-id=111111111;subscriber=0@badge-info=;badges=;", StringComparison.Ordinal);

        Assert.True(IrcLineSpliceRule.IsSpliced(line));
    }

    [Fact]
    public void IsSpliced_SecondAtFollowedByKeyWithoutEquals_ReturnsFalse()
    {
        // A login is followed by a separator, never by '=' — that is the whole distinction.
        var line = "@badge-info=;display-name=TestUser1;id=aaaa1111-1111-1111-1111-111111111111;"
            + "reply-parent-msg-id=eeee5555-5555-5555-5555-555555555555;room-id=111111111;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        Assert.False(IrcLineSpliceRule.IsSpliced(line.Replace("room-id=111111111", "room-id=111111111@testuser0", StringComparison.Ordinal)));
    }

    [Fact]
    public void TagBlockForLog_ReplyParentBody_ValueIsRedacted()
    {
        var line = ReplyLine(@"@TestUser0\shallo\sdu");

        // Every "<entfernt>" replaces what used to be a short or empty value, so redacting this many
        // tags pushes the block past the default 512-char cap before the tags this test checks even
        // appear — raise it here to verify the redaction itself, independent of the truncation
        // covered by the dedicated TagBlockForLog_TagBlockLongerThanMaxLength_* tests.
        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line, maxLength: 2000);

        // The promise the sentinel's log comment makes: no message text, not this line's and not a
        // stranger's — and, since #246, no identity either, sender's or parent-message-author's.
        Assert.Contains("reply-parent-msg-body=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("TestUser0\\shallo", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-parent-display-name=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-parent-user-login=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-parent-user-id=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-thread-parent-display-name=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-thread-parent-user-login=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.Contains(";display-name=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.Contains(";user-id=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("TestUser0;", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("testuser0;", tagBlock, StringComparison.Ordinal);
        // Not a blanket "555555555" check — that digit run also occurs, legitimately, inside the
        // untouched reply-parent-msg-id GUID a few tags later.
        Assert.DoesNotContain("user-id=555555555", tagBlock, StringComparison.Ordinal);
        Assert.StartsWith("@badge-info=<entfernt>;badges=<entfernt>;", tagBlock, StringComparison.Ordinal);
        // Non-identifying tags in the same block still survive untouched.
        Assert.Contains(";room-id=111111111;", tagBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TagBlockForLog_ThreadParentBody_ValueIsRedactedToo()
    {
        // The key is matched by its "msg-body" suffix, so a sibling tag Twitch may add later is
        // redacted from its first appearance instead of leaking once.
        var line = @"@badge-info=;reply-parent-msg-body=@TestUser0\shallo;"
            + @"reply-thread-parent-msg-body=@TestUser0\sanfang;room-id=111111111;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line);

        // badge-info is now also redacted (#246, identifying tag), on top of the two free-text
        // msg-body tags this test originally covered.
        Assert.Equal(
            "@badge-info=<entfernt>;reply-parent-msg-body=<entfernt>;reply-thread-parent-msg-body=<entfernt>;room-id=111111111;user-type=",
            tagBlock);
    }

    [Fact]
    public void TagBlockForLog_IdentifyingTags_ValuesAreRedactedButKeysSurvive()
    {
        // A representative tag block with every category #246 asks for: the sender's own identity,
        // badges, and a Shared Chat (#73) source-* badge pair — plus a couple of non-identifying
        // tags (room-id, subscriber) that must stay exactly as they are so the diagnostic shape of
        // the block is still readable.
        var line = "@badge-info=subscriber/12;badges=subscriber/12,moderator/1;display-name=TestUser1;"
            + "login=testuser1;user-id=333333333;source-badges=vip/1;source-badge-info=;room-id=111111111;"
            + "subscriber=1 :testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line);

        Assert.Equal(
            "@badge-info=<entfernt>;badges=<entfernt>;display-name=<entfernt>;login=<entfernt>;"
            + "user-id=<entfernt>;source-badges=<entfernt>;source-badge-info=<entfernt>;room-id=111111111;subscriber=1",
            tagBlock);
    }

    [Fact]
    public void TagBlockForLog_SourceStyleUserIdAndLoginTags_AreRedactedByPattern()
    {
        // Twitch does not send these two today (#73's SharedChatRule only reads source-room-id,
        // source-id, source-badges, source-badge-info) — this pins the forward-looking pattern
        // match from the issue itself ("source-user-id/source-login-style shared-chat tags").
        var line = "@source-room-id=222222222;source-user-id=444444444;source-login=sourceuser "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line);

        Assert.Equal(
            "@source-room-id=222222222;source-user-id=<entfernt>;source-login=<entfernt>",
            tagBlock);
    }

    [Fact]
    public void TagBlockForLog_SplicedLineWithoutFreeTextTag_NonIdentifyingTagsSurviveVerbatim()
    {
        var line = "@badge-info=;room-id=111111111;subscriber=0@badge-info=;room-id=222222222;user-type= "
            + ":testuser2!testuser2@testuser2.tmi.twitch.tv PRIVMSG #targetchannel_test :hello";

        // The leading badge-info tag is redacted as an identifying tag (#246); the spliced-in second
        // "@badge-info=" is embedded inside the *value* of "subscriber" (a non-identifying tag) and
        // is not recognised as its own tag by this line-based redaction, same as before #246 — it is
        // the splice defect itself that made it invisible to the tag parser (E6), not a gap here.
        Assert.Equal(
            "@badge-info=<entfernt>;room-id=111111111;subscriber=0@badge-info=;room-id=222222222;user-type=",
            IrcLineSpliceRule.TagBlockForLog(line));
    }

    [Fact]
    public void TagBlockForLog_FreeTextValueRedactedBeforeTruncation()
    {
        // Redaction must not be something a long parent body can push past the length cap.
        var line = "@reply-parent-msg-body=" + new string('x', 4000) + ";room-id=111111111 "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hi";

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line);

        Assert.Equal("@reply-parent-msg-body=<entfernt>;room-id=111111111", tagBlock);
    }

    private static string ReplyLine(string parentBody) =>
        ReplyTagsBeforeParentBody + "reply-parent-msg-body=" + parentBody + ReplyTagsAfterParentBody;
}
