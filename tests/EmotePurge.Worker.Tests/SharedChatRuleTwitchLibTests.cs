using EmotePurge.Core.Chat;
using TwitchLib.Client.Models;
using TwitchLib.Client.Parsing;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Deliberately TwitchLib-bound — the one exception to "Policies TwitchLib-free" in this project.
// SharedChatRule.FromTags relies on a fact about the installed TwitchLib.Client 4.0.1 that no pure
// test can see: source-room-id, source-id, source-badges and source-badge-info are all left
// undocumented (i.e. only reachable through ChatMessage.UndocumentedTags, never as a typed
// property), so they only show up at all when a message carries at least one such tag. That is the
// library behaviour under test here, not our own code — hence real IRC lines run through TwitchLib's
// own IrcParser and ChatMessage constructor instead of a hand-built fake. Lines are synthetic
// (invented ids/logins/text), never recorded chat, same rule as everywhere else in this repo.
// There is no Dependabot freeze on TwitchLib.* any more (DECISIONS.md, 2026-11-03): these tests are
// the only signal left that an update typed one of the markers away, so they must stay red then.
public class SharedChatRuleTwitchLibTests
{
    private const string OwnRoomId = "111111111";
    private const string ForeignRoomId = "222222222";

    [Fact]
    public void FromTags_SharedChatLineWithFullSourceSet_ReturnsForeign_AndSourceRoomIdIsUndocumented()
    {
        var line = "@badge-info=;badges=;client-nonce=abc123;color=#0000FF;display-name=TestUser1;"
            + "emotes=;first-msg=0;flags=;id=aaaa1111-1111-1111-1111-111111111111;mod=0;"
            + "returning-chatter=0;room-id=" + OwnRoomId + ";source-badge-info=;source-badges=;"
            + "source-id=bbbb2222-2222-2222-2222-222222222222;source-room-id=" + ForeignRoomId + ";"
            + "subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
            + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #targetchannel_test :hello from shared chat";

        var message = ParseLine(line);

        // Proves the contract this rule depends on and that a TwitchLib update could break
        // silently: 4.0.1 does not type source-room-id, so it only shows up in UndocumentedTags.
        Assert.NotNull(message.UndocumentedTags);
        Assert.True(message.UndocumentedTags.ContainsKey(SharedChatRule.SourceRoomIdTag));

        var result = SharedChatRule.FromTags(message.RoomId, message.UndocumentedTags);

        Assert.Equal(MessageOrigin.Foreign, result);
    }

    [Fact]
    public void FromTags_OrdinaryLineWithOnlyTypedTags_ReturnsOwn_AndUndocumentedTagsIsNull()
    {
        // Only the typed tags TwitchLib.Client 4.0.1 models as ChatMessage properties: badges,
        // color, display-name, id, room-id, tmi-sent-ts, user-id, user-type, mod, subscriber,
        // turbo. Deliberately no client-nonce, no flags — either would already land in
        // UndocumentedTags and defeat the null probe below.
        var line = "@badges=;color=#FF0000;display-name=TestUser2;"
            + "id=cccc3333-3333-3333-3333-333333333333;mod=0;room-id=" + OwnRoomId + ";"
            + "subscriber=0;tmi-sent-ts=1694000000000;turbo=0;user-id=444444444;user-type= "
            + ":testuser2!testuser2@testuser2.tmi.twitch.tv PRIVMSG #targetchannel_test :hello normal message";

        var message = ParseLine(line);

        // If this fails, the fixture line above picked up an undocumented tag by accident and the
        // "no exception, no lookup" branch of FromTags is not actually being exercised.
        Assert.Null(message.UndocumentedTags);

        var result = SharedChatRule.FromTags(message.RoomId, message.UndocumentedTags);

        Assert.Equal(MessageOrigin.Own, result);
    }

    [Fact]
    public void FromTags_LineWithOtherSourceMarkersButNoSourceRoomId_ReturnsIndeterminate()
    {
        var line = "@badges=;color=#00FF00;display-name=TestUser3;"
            + "id=dddd4444-4444-4444-4444-444444444444;mod=0;room-id=" + OwnRoomId + ";"
            + "source-badges=;source-id=eeee5555-5555-5555-5555-555555555555;subscriber=0;"
            + "tmi-sent-ts=1694000000000;turbo=0;user-id=555555555;user-type= "
            + ":testuser3!testuser3@testuser3.tmi.twitch.tv PRIVMSG #targetchannel_test :hello indeterminate";

        var message = ParseLine(line);

        var result = SharedChatRule.FromTags(message.RoomId, message.UndocumentedTags);

        Assert.Equal(MessageOrigin.Indeterminate, result);
    }

    [Theory]
    [InlineData(SharedChatRule.SourceIdTag, "bbbb2222-2222-2222-2222-222222222222")]
    [InlineData(SharedChatRule.SourceBadgesTag, "")]
    [InlineData(SharedChatRule.SourceBadgeInfoTag, "")]
    public void FromTags_EachOtherSourceMarkerAlone_IsUndocumented_AndReturnsIndeterminate(string marker, string value)
    {
        // One marker per line: if a TwitchLib update types exactly this one away, the key leaves
        // UndocumentedTags (the first assertion names which marker) and the live side would count
        // the message as Own instead of Indeterminate. The combined cases above cannot see that
        // for source-badge-info at all, and for the others only when every sibling vanishes too.
        var line = "@badges=;color=#00FF00;display-name=TestUser3;"
            + "id=dddd4444-4444-4444-4444-444444444444;mod=0;room-id=" + OwnRoomId + ";"
            + marker + "=" + value + ";subscriber=0;"
            + "tmi-sent-ts=1694000000000;turbo=0;user-id=555555555;user-type= "
            + ":testuser3!testuser3@testuser3.tmi.twitch.tv PRIVMSG #targetchannel_test :hello single marker";

        var message = ParseLine(line);

        Assert.NotNull(message.UndocumentedTags);
        Assert.True(message.UndocumentedTags.ContainsKey(marker), marker + " no longer reaches UndocumentedTags");
        Assert.Equal(MessageOrigin.Indeterminate, SharedChatRule.FromTags(message.RoomId, message.UndocumentedTags));
    }

    private static ChatMessage ParseLine(string line)
    {
        var ircMessage = IrcParser.ParseMessage(line);
        return new ChatMessage("purgebot_test", ircMessage, new MessageEmoteCollection(), false, "prefix", "suffix");
    }
}
