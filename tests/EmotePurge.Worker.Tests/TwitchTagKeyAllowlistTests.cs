using Xunit;

namespace EmotePurge.Worker.Tests;

// Pins the shared tag-key allowlist (#246) and its effect on both redaction sites. All lines use
// invented names and ids. The recorded-line shapes follow the examples and tag tables of Twitch's
// IRC reference (dev.twitch.tv/docs/chat/irc, checked 2026-10-14) and the shapes already seen in
// this repo's fixtures; only names, ids and text were replaced.
public class TwitchTagKeyAllowlistTests
{
    public static TheoryData<string> FixedKeyCases()
    {
        var data = new TheoryData<string>();
        foreach (var key in TwitchTagKeyAllowlist.FixedKeys)
        {
            data.Add(key);
        }

        return data;
    }

    public static TheoryData<string> PrefixCases()
    {
        var data = new TheoryData<string>();
        foreach (var prefix in TwitchTagKeyAllowlist.Prefixes)
        {
            data.Add(prefix);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixedKeyCases))]
    public void AllAllowlistKeysAndPrefixes_PassThrough_FixedKeys(string key) =>
        Assert.True(TwitchTagKeyAllowlist.IsAllowed(key));

    [Theory]
    [MemberData(nameof(PrefixCases))]
    public void AllAllowlistKeysAndPrefixes_PassThrough_Prefixes(string prefix)
    {
        Assert.True(TwitchTagKeyAllowlist.IsAllowed(prefix + "some-key-9"));

        // 48 is the rest limit, but the whole key is also capped at 64 by the grammar.
        Assert.True(TwitchTagKeyAllowlist.IsAllowed(prefix + new string('a', Math.Min(48, 64 - prefix.Length))));
        Assert.False(TwitchTagKeyAllowlist.IsAllowed(prefix));
        Assert.False(TwitchTagKeyAllowlist.IsAllowed(prefix + new string('a', 49)));
        Assert.False(TwitchTagKeyAllowlist.IsAllowed(prefix + "UPPER"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("chatwort")]
    [InlineData("SomeDisplayName")]
    [InlineData("hello world")]
    [InlineData("id ")]
    [InlineData("ID")]
    [InlineData("msg-param")]
    [InlineData("display_name")]
    [InlineData("+")]
    [InlineData("vendor/")]
    [InlineData("+vendor.example/id")]
    [InlineData("id@x")]
    public void NonAllowlistedOrGrammarViolatingKeys_AreRejected(string key) =>
        Assert.False(TwitchTagKeyAllowlist.IsAllowed(key));

    [Fact]
    public void TotalLengthCap_AppliesOnTopOfThePrefixRestLimit()
    {
        const string prefix = "reply-thread-parent-";

        // Rest of 44 gives exactly 64 characters in total; a rest of 45 is within the 48 rest limit but exceeds 64.
        Assert.True(TwitchTagKeyAllowlist.IsAllowed(prefix + new string('a', 44)));
        Assert.False(TwitchTagKeyAllowlist.IsAllowed(prefix + new string('a', 45)));
    }

    // The agreed fixed names (A30 plus the documented additions), spelled out independently of the constant
    // so deleting a name from FixedKeys fails here.
    [Theory]
    [InlineData("badge-info")]
    [InlineData("badges")]
    [InlineData("bits")]
    [InlineData("client-nonce")]
    [InlineData("color")]
    [InlineData("display-name")]
    [InlineData("emote-only")]
    [InlineData("emote-sets")]
    [InlineData("emotes")]
    [InlineData("first-msg")]
    [InlineData("flags")]
    [InlineData("followers-only")]
    [InlineData("id")]
    [InlineData("login")]
    [InlineData("message-id")]
    [InlineData("mod")]
    [InlineData("msg-id")]
    [InlineData("r9k")]
    [InlineData("returning-chatter")]
    [InlineData("room-id")]
    [InlineData("slow")]
    [InlineData("subs-only")]
    [InlineData("subscriber")]
    [InlineData("system-msg")]
    [InlineData("target-msg-id")]
    [InlineData("target-user-id")]
    [InlineData("ban-duration")]
    [InlineData("thread-id")]
    [InlineData("tmi-sent-ts")]
    [InlineData("turbo")]
    [InlineData("user-id")]
    [InlineData("user-type")]
    [InlineData("vip")]
    [InlineData("animation-id")]
    [InlineData("custom-reward-id")]
    [InlineData("gifs")]
    [InlineData("msg-param-displayName")]
    [InlineData("msg-param-viewerCount")]
    [InlineData("msg-param-profileImageURL")]
    [InlineData("msg-param-copoReward")]
    public void AgreedFixedNames_AreAllowed(string key) =>
        Assert.True(TwitchTagKeyAllowlist.IsAllowed(key));

    [Fact]
    public void KeyLongerThanGrammarLimit_IsRejected() =>
        Assert.False(TwitchTagKeyAllowlist.IsAllowed("msg-param-" + new string('a', 60)));

    // --- Redact (TwitchLibRawLineRedaction) -------------------------------------------------

    [Fact]
    public void Redact_BareFragmentAfterKey_IsWithheldAndCounted()
    {
        var redacted = TwitchLibRawLineRedaction.Redact("@reply-parent-msg-body=hello;SECRET PRIVMSG #room :more");

        Assert.Equal("command=PRIVMSG, tagKeys=[reply-parent-msg-body], withheldTagKeys=1", redacted);
        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_DisplayNameAsBareFragment_IsWithheld()
    {
        var redacted = TwitchLibRawLineRedaction.Redact("@badges=;SomeDisplayName;color=#fff :x!x@x.tmi.twitch.tv PRIVMSG #room :hi");

        Assert.Equal("command=PRIVMSG, tagKeys=[badges,color], withheldTagKeys=1", redacted);
        Assert.DoesNotContain("SomeDisplayName", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_UnknownKeyWithEquals_IsWithheld()
    {
        var redacted = TwitchLibRawLineRedaction.Redact("@chatwort=x;id=1 PRIVMSG #room :hi");

        Assert.Equal("command=PRIVMSG, tagKeys=[id], withheldTagKeys=1", redacted);
        Assert.DoesNotContain("chatwort", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_GrammarViolation_IsWithheld()
    {
        var redacted = TwitchLibRawLineRedaction.Redact("@ID=1;msg-param-UPPER=1;a_b=2;id=1 PRIVMSG #room :hi");

        Assert.Equal("command=PRIVMSG, tagKeys=[id], withheldTagKeys=3", redacted);
    }

    [Fact]
    public void Redact_EmptyFragments_AreNotCounted() =>
        Assert.Equal("command=PRIVMSG, tagKeys=[id]", TwitchLibRawLineRedaction.Redact("@id=1;; PRIVMSG #room :hi"));

    // --- TagBlockForLog (IrcLineSpliceRule) -------------------------------------------------

    [Fact]
    public void TagBlockForLog_BareFragmentAfterKey_IsReplacedByWithheldMarker()
    {
        var tagBlock = IrcLineSpliceRule.TagBlockForLog("@reply-parent-msg-body=hello;SECRET PRIVMSG #room :more");

        Assert.Equal("@reply-parent-msg-body=<entfernt>;<withheld>", tagBlock);
        Assert.DoesNotContain("SECRET", tagBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TagBlockForLog_DisplayNameAsBareFragment_IsWithheld()
    {
        var tagBlock = IrcLineSpliceRule.TagBlockForLog("@badges=;SomeDisplayName;color=#fff :x!x@x.tmi.twitch.tv PRIVMSG #room :hi");

        Assert.Equal("@badges=<entfernt>;<withheld>;color=<entfernt>", tagBlock);
        Assert.DoesNotContain("SomeDisplayName", tagBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TagBlockForLog_UnknownKeyWithEquals_IsWithheldWithItsValue()
    {
        var tagBlock = IrcLineSpliceRule.TagBlockForLog("@chatwort=secretvalue;id=1 PRIVMSG #room :hi");

        Assert.Equal("@<withheld>;id=<entfernt>", tagBlock);
        Assert.DoesNotContain("chatwort", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue", tagBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void TagBlockForLog_GrammarViolation_IsWithheld() =>
        Assert.Equal("@<withheld>;<withheld>;id=<entfernt>", IrcLineSpliceRule.TagBlockForLog("@ID=1;a_b=2;id=1 PRIVMSG #room :hi"));

    [Fact]
    public void TagBlockForLog_EmbeddedUnknownKey_IsWithheldWithItsValue()
    {
        var tagBlock = IrcLineSpliceRule.TagBlockForLog("@room-id=111111111@EvilName=secret;mod=0 PRIVMSG #room :hi");

        Assert.Equal("@room-id=111111111@<withheld>;mod=0", tagBlock);
        Assert.DoesNotContain("EvilName", tagBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", tagBlock, StringComparison.Ordinal);
    }

    // --- R1: no value survives except the format-checked exceptions --------------------------

    public static TheoryData<string, string> ValueBearingTags() => new()
    {
        { "system-msg", @"SomeUser\ssubscribed\swith\sPrime." },
        { "msg-param-recipient-display-name", "SomeDisplayName" },
        { "msg-param-sender-login", "someone" },
        { "msg-param-login", "someone" },
        { "display-name", "X" },
        { "reply-parent-display-name", "Y" },
        { "color", "#ABCDEF" },
        { "id", "aaaa1111-1111-1111-1111-111111111111" },
    };

    [Theory]
    [MemberData(nameof(ValueBearingTags))]
    public void TagBlockForLog_TopLevelValue_IsRemovedButKeyStays(string key, string value)
    {
        var tagBlock = IrcLineSpliceRule.TagBlockForLog($"@mod=0;{key}={value};room-id=111111111 PRIVMSG #room :hi");

        Assert.Equal($"@mod=0;{key}=<entfernt>;room-id=111111111", tagBlock);
    }

    [Theory]
    [MemberData(nameof(ValueBearingTags))]
    public void TagBlockForLog_EmbeddedValue_IsRemovedButKeyStays(string key, string value)
    {
        var tagBlock = IrcLineSpliceRule.TagBlockForLog($"@mod=0@{key}={value};room-id=111111111 PRIVMSG #room :hi");

        Assert.Equal($"@mod=0@{key}=<entfernt>;room-id=111111111", tagBlock);
    }

    [Theory]
    [InlineData("tmi-sent-ts", "1694000000000", true)]
    [InlineData("tmi-sent-ts", "12345678901234567890", true)]
    [InlineData("tmi-sent-ts", "123456789012345678901", false)]
    [InlineData("tmi-sent-ts", "17a", false)]
    [InlineData("tmi-sent-ts", "", false)]
    [InlineData("room-id", "111111111", true)]
    [InlineData("room-id", "abc", false)]
    [InlineData("subscriber", "0", true)]
    [InlineData("subscriber", "1", true)]
    [InlineData("subscriber", "2", false)]
    [InlineData("subscriber", "01", false)]
    [InlineData("mod", "1", true)]
    [InlineData("turbo", "0", true)]
    [InlineData("vip", "1", true)]
    [InlineData("first-msg", "0", true)]
    [InlineData("returning-chatter", "1", true)]
    [InlineData("emote-only", "1", true)]
    [InlineData("emote-only", "yes", false)]
    public void TagBlockForLog_FormatCheckedValues_AreKeptOnlyInExactFormat(string key, string value, bool kept)
    {
        var expected = kept ? $"{key}={value}" : $"{key}=<entfernt>";

        // Top level, then embedded after a '@'. A value that itself carries a '@key=' start cannot
        // be expressed embedded (it would nest), so the embedded form skips it.
        Assert.Equal("@" + expected, IrcLineSpliceRule.TagBlockForLog($"@{key}={value} PRIVMSG #room :hi"));
        if (!value.Contains('@', StringComparison.Ordinal))
        {
            Assert.Equal("@mod=0@" + expected, IrcLineSpliceRule.TagBlockForLog($"@mod=0@{key}={value} PRIVMSG #room :hi"));
        }
    }

    [Fact]
    public void TagBlockForLog_RoomIdCarryingASplice_RedactsTheEmbeddedPart()
    {
        var tagBlock = IrcLineSpliceRule.TagBlockForLog("@room-id=111111111@display-name=EvilName@user-id=444444444;mod=0 PRIVMSG #room :hi");

        Assert.Equal("@room-id=111111111@display-name=<entfernt>@user-id=<entfernt>;mod=0", tagBlock);
    }

    // --- The allowlist must not be too narrow: real Twitch line shapes -----------------------

    public static TheoryData<string> RecordedLines() => new()
    {
        // PRIVMSG (reply, bits, shared chat source tags)
        "@badge-info=subscriber/8;badges=subscriber/6,bits/100;bits=100;client-nonce=abc123;color=#1E90FF;"
        + "display-name=TestUser1;emotes=;first-msg=0;flags=;id=aaaa1111-1111-1111-1111-111111111111;mod=0;"
        + "reply-parent-display-name=TestUser0;reply-parent-msg-body=hi;reply-parent-msg-id=eeee5555;"
        + "reply-parent-user-id=555555555;reply-parent-user-login=testuser0;reply-thread-parent-msg-id=eeee5555;"
        + "reply-thread-parent-user-login=testuser0;returning-chatter=0;room-id=111111111;subscriber=1;"
        + "source-badge-info=;source-badges=;source-id=bbbb;source-only=0;source-room-id=222222222;"
        + "tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type=;vip=1 "
        + ":testuser1!testuser1@testuser1.tmi.twitch.tv PRIVMSG #target_test :hello",
        // USERNOTICE: resub
        "@badge-info=subscriber/8;badges=subscriber/6;color=#008000;display-name=TestUser1;emotes=;flags=;"
        + @"id=cccc3333;login=testuser1;mod=0;msg-id=resub;msg-param-cumulative-months=8;msg-param-months=0;"
        + @"msg-param-should-share-streak=1;msg-param-streak-months=3;msg-param-sub-plan-name=Tier\s1;"
        + @"msg-param-sub-plan=1000;msg-param-was-gifted=false;room-id=111111111;subscriber=1;"
        + @"system-msg=TestUser1\ssubscribed;tmi-sent-ts=1694000000000;turbo=0;user-id=333333333;user-type= "
        + ":tmi.twitch.tv USERNOTICE #target_test :thanks",
        // USERNOTICE: raid (camel-case msg-param keys)
        "@badge-info=;badges=;color=#FF0000;display-name=TestRaider;emotes=;id=dddd4444;login=testraider;mod=0;"
        + "msg-id=raid;msg-param-displayName=TestRaider;msg-param-login=testraider;msg-param-profileImageURL=https://example.invalid/x.png;"
        + @"msg-param-viewerCount=42;room-id=111111111;subscriber=0;system-msg=42\sraiders;tmi-sent-ts=1694000000000;"
        + "turbo=0;user-id=666666666;user-type= :tmi.twitch.tv USERNOTICE #target_test",
        // USERNOTICE: submysterygift / subgift / viewermilestone
        "@badge-info=;badges=;color=;display-name=TestGifter;emotes=;id=eeee5555;login=testgifter;mod=0;"
        + "msg-id=subgift;msg-param-gift-months=1;msg-param-months=2;msg-param-origin-id=ab\\sc;"
        + "msg-param-recipient-display-name=TestRecipient;msg-param-recipient-id=777;msg-param-recipient-user-name=testrecipient;"
        + "msg-param-sender-count=0;msg-param-sub-plan-name=Tier\\s1;msg-param-sub-plan=1000;room-id=111111111;"
        + "subscriber=0;system-msg=x;tmi-sent-ts=1694000000000;turbo=0;user-id=888;user-type= :tmi.twitch.tv USERNOTICE #target_test",
        "@badge-info=;badges=;color=;display-name=TestUser1;emotes=;id=ffff6666;login=testuser1;mod=0;"
        + "msg-id=viewermilestone;msg-param-category=watch-streak;msg-param-copoReward=450;msg-param-id=gggg;"
        + "msg-param-value=5;room-id=111111111;subscriber=0;system-msg=x;tmi-sent-ts=1694000000000;turbo=0;"
        + "user-id=333333333;user-type= :tmi.twitch.tv USERNOTICE #target_test",
        // NOTICE
        "@msg-id=msg_channel_suspended :tmi.twitch.tv NOTICE #target_test :This channel does not exist",
        "@msg-id=bad_ban_admin;target-user-id=333333333 :tmi.twitch.tv NOTICE #target_test :nope",
        // CLEARCHAT
        "@ban-duration=350;room-id=111111111;target-user-id=333333333;tmi-sent-ts=1694000000000 :tmi.twitch.tv CLEARCHAT #target_test :testuser1",
        // CLEARMSG
        "@login=testuser1;room-id=;target-msg-id=aaaa1111;tmi-sent-ts=1694000000000 :tmi.twitch.tv CLEARMSG #target_test :hi",
        // ROOMSTATE
        "@emote-only=0;followers-only=-1;r9k=0;room-id=111111111;slow=0;subs-only=0 :tmi.twitch.tv ROOMSTATE #target_test",
        // USERSTATE
        "@badge-info=;badges=;color=#0000FF;display-name=PurgeBotTest;emote-sets=0,33;id=hhhh7777;mod=0;subscriber=0;turbo=0;user-type= :tmi.twitch.tv USERSTATE #target_test",
        // GLOBALUSERSTATE
        "@badge-info=;badges=;color=#0000FF;display-name=PurgeBotTest;emote-sets=0;turbo=0;user-id=999;user-type= :tmi.twitch.tv GLOBALUSERSTATE",
        // WHISPER
        "@badges=;color=#0000FF;display-name=TestUser1;emotes=;message-id=1;thread-id=1_2;turbo=0;user-id=333;user-type= :testuser1!testuser1@testuser1.tmi.twitch.tv WHISPER purgebot_test :hi",
    };

    [Theory]
    [MemberData(nameof(RecordedLines))]
    public void RecordedTwitchLines_HaveNoWithheldKeys(string line)
    {
        var redacted = TwitchLibRawLineRedaction.Redact(line);
        var tagBlock = IrcLineSpliceRule.TagBlockForLog(line, maxLength: 10_000);

        Assert.DoesNotContain("withheldTagKeys", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("<withheld>", tagBlock, StringComparison.Ordinal);
    }
}
