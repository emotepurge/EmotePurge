using TwitchLib.Client.Models;
using TwitchLib.Client.Parsing;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Deliberately TwitchLib-bound — same exception as SharedChatRuleTwitchLibTests. IrcLineSpliceRule
// (E6, docs/superpowers/plans/2026-09-08-worker-erfassungsfehler.md) depends on a fact about the
// installed TwitchLib.Client 4.0.1 that no pure test can see: when a splice lands inside the value
// of a *typed* tag (e.g. "subscriber=0@badge-info="), the library's tag parser scans for the next
// ';' or ' ' to end that tag's value — so the embedded "@badge-info=" never becomes its own
// dictionary entry and therefore never surfaces via ChatMessage.UndocumentedTags. Only the raw,
// unparsed line (ChatMessage.RawIrcMessage) still shows the second '@'. That is the library
// behaviour under test here, not our own code — hence a real IRC line runs through TwitchLib's own
// IrcParser and ChatMessage constructor instead of a hand-built fake. The line is synthetic
// (invented ids/logins/text), never recorded chat, same rule as everywhere else in this repo.
// The second fact pinned here is what the first version of the rule got wrong: an ordinary reply
// line reaches ChatMessage.RawIrcMessage with an unescaped '@' inside the reply-parent-msg-body
// tag, because Twitch's reply UI prepends "@nutzername " to every reply and IRCv3 tag escaping
// leaves '@' alone. Rule and pure test shared that wrong assumption and were both green — only a
// line pushed through the library's own parser settles it, so every case below does exactly that.
// The TwitchLib.* Dependabot freeze (see .github/dependabot.yml) exists so an update cannot flip
// either contract without this test going red first.
public class IrcLineSpliceRuleTwitchLibTests
{
    private const string RoomId = "111111111";

    [Fact]
    public void SplicedLineWithSecondAtInsideTypedTagValue_RawIrcMessageShowsIt_ButUndocumentedTagsDoesNot()
    {
        // The splice sits right after "subscriber=0" (first line's tag block ends mid-value) and
        // is immediately followed by the second line's tag block starting with "@badge-info=" —
        // exactly the shape reported for issue #114. Every key on both sides (badges, color,
        // display-name, id, mod, room-id, subscriber, tmi-sent-ts, turbo, user-id, user-type) is a
        // typed ChatMessage property, deliberately excluding badge-info as a real key — it only
        // ever appears embedded inside the corrupted subscriber value, never as its own tag.
        var line = "@badges=;color=#FF0000;display-name=TestUser1;id=cccc3333-3333-3333-3333-333333333333;"
            + "mod=0;room-id=" + RoomId + ";subscriber=0@badge-info=;badges=;color=#00FF00;"
            + "display-name=TestUser2;id=dddd4444-4444-4444-4444-444444444444;mod=0;room-id=" + RoomId + ";"
            + "subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=444444444;user-type= "
            + ":testuser2!testuser2@testuser2.tmi.twitch.tv PRIVMSG #targetchannel_test :hello spliced";

        var message = ParseLine(line);

        // (a) The raw line is preserved verbatim (ChatMessage.RawIrcMessage = ircMessage.ToString(),
        // which returns the cached original raw string, not a re-serialization of the parsed tags)
        // and still carries the second '@', so the sentinel fires on it.
        Assert.Equal(line, message.RawIrcMessage);
        Assert.True(IrcLineSpliceRule.IsSpliced(message.RawIrcMessage));

        // (b) This is the proof for E6: the corruption never becomes a separate dictionary entry,
        // so UndocumentedTags — built only from unrecognised keys — does not show it at all.
        Assert.Null(message.UndocumentedTags);
    }

    [Fact]
    public void OrdinaryReplyLine_MentionInsideParentBody_DoesNotFire()
    {
        // The regression this file exists for. Twitch's reply UI prepends "@nutzername " to the
        // reply, reply-parent-msg-body carries the parent message verbatim, and IRCv3 tag escaping
        // only touches ';', space, '\', CR and LF — '@' survives. The first version of the rule
        // looked for a bare second '@' in the tag block and therefore reported every single reply
        // as a splice. Both the rule and its pure test shared that assumption and were green;
        // running the line through the installed library is what breaks the tie.
        var line = ReplyLine(@"@TestUser0\shallo\sdu");

        var message = ParseLine(line);

        Assert.Equal(line, message.RawIrcMessage);
        Assert.False(IrcLineSpliceRule.IsSpliced(message.RawIrcMessage));
    }

    [Fact]
    public void ReplyLineWhoseParentBodyImitatesATagBlock_DoesNotFire()
    {
        // Worst case for a text-shape rule: a chatter writes something that looks like the start of
        // a second tag block. The ';' arrives escaped as "\:", so it stays inside the one
        // reply-parent-msg-body value — which is exactly why that value is excluded wholesale.
        var line = ReplyLine(@"@badge-info=\:badges=\shaha");

        var message = ParseLine(line);

        Assert.Equal(line, message.RawIrcMessage);
        Assert.False(IrcLineSpliceRule.IsSpliced(message.RawIrcMessage));
    }

    [Fact]
    public void SpliceIntoAReplyLineOutsideTheParentBody_StillFires()
    {
        // Excluding free-text values must not cost the detection everywhere else on the line.
        var line = ReplyLine(@"@TestUser0\shallo\sdu")
            .Replace("room-id=" + RoomId + ";subscriber=0;", "room-id=" + RoomId + ";subscriber=0@badge-info=;badges=;", StringComparison.Ordinal);

        var message = ParseLine(line);

        Assert.Equal(line, message.RawIrcMessage);
        Assert.True(IrcLineSpliceRule.IsSpliced(message.RawIrcMessage));
    }

    [Fact]
    public void ReplyLine_TagBlockForLog_CarriesNoForeignMessageText()
    {
        var line = ReplyLine(@"@TestUser0\shallo\sdu");

        var tagBlock = IrcLineSpliceRule.TagBlockForLog(ParseLine(line).RawIrcMessage);

        Assert.DoesNotContain("hallo", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-parent-msg-body=<entfernt>;", tagBlock, StringComparison.Ordinal);

        // #246: the same call also redacts the chatter's and the parent message author's identity
        // — verified through the real TwitchLib parser, not a hand-built line, same as the rest of
        // this file.
        Assert.DoesNotContain("TestUser1", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("testuser0", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("333333333", tagBlock, StringComparison.Ordinal);
        Assert.Contains("display-name=<entfernt>;", tagBlock, StringComparison.Ordinal);
        Assert.Contains("reply-parent-user-login=<entfernt>;", tagBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full Twitch reply line with <paramref name="parentBody"/> as the (already IRCv3-escaped)
    /// value of <c>reply-parent-msg-body</c>. Synthetic throughout, never recorded chat.
    /// </summary>
    private static string ReplyLine(string parentBody) =>
        "@badge-info=;badges=;client-nonce=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;color=#1E90FF;"
        + "display-name=TestUser1;emotes=;first-msg=0;flags=;id=aaaa1111-1111-1111-1111-111111111111;mod=0;"
        + "reply-parent-display-name=TestUser0;reply-parent-msg-body=" + parentBody + ";"
        + "reply-parent-msg-id=eeee5555-5555-5555-5555-555555555555;reply-parent-user-id=555555555;"
        + "reply-parent-user-login=testuser0;reply-thread-parent-display-name=TestUser0;"
        + "reply-thread-parent-msg-id=eeee5555-5555-5555-5555-555555555555;"
        + "reply-thread-parent-user-login=testuser0;returning-chatter=0;room-id=" + RoomId + ";subscriber=0;"
        + "tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
        + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :@TestUser0 alles klar";

    private static ChatMessage ParseLine(string line)
    {
        var ircMessage = IrcParser.ParseMessage(line);
        return new ChatMessage("purgebot_test", ircMessage, new MessageEmoteCollection(), false, "prefix", "suffix");
    }
}
