using Microsoft.Extensions.Configuration;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Pure and TwitchLib-free like TwitchWatchdogPolicy/TwitchReconnectBackoffPolicy — chatter id and badges arrive
// as plain BCL values, IConfiguration is the only external dependency. Never throws: IsBot runs in
// TwitchChatManager.OnMessageReceived, the hot path (see the class comment on BotChatterDetector).
public class BotChatterDetectorTests
{
    private const string NightbotId = "19264788";
    private const string StreamElementsId = "100135110";
    private const string FossabotId = "237719657";
    private const string MoobotId = "1564983";
    private const string StreamlabsId = "105166207";
    private const string SeryBotId = "402337290";

    [Fact]
    public void IsBot_BotBadge_ReturnsTrue_EvenWithoutAKnownAccountId()
    {
        var detector = CreateDetector();

        var result = detector.IsBot("999999999", [new("bot-badge", "1")]);

        Assert.True(result);
    }

    [Theory]
    [InlineData(NightbotId)]
    [InlineData(StreamElementsId)]
    [InlineData(FossabotId)]
    [InlineData(MoobotId)]
    [InlineData(StreamlabsId)]
    [InlineData(SeryBotId)]
    public void IsBot_StaticAccountId_ReturnsTrue(string accountId)
    {
        var detector = CreateDetector();

        var result = detector.IsBot(accountId, badges: null);

        Assert.True(result);
    }

    [Fact]
    public void IsBot_AccountIdFromScalarConfig_ReturnsTrue()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Twitch:AdditionalBotAccountIds"] = "555555555"
        });
        var detector = new BotChatterDetector(configuration);

        var result = detector.IsBot("555555555", badges: null);

        Assert.True(result);
    }

    [Fact]
    public void IsBot_AccountIdFromIndexedArrayConfig_ReturnsTrue()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Twitch:AdditionalBotAccountIds:0"] = "555555555",
            ["Twitch:AdditionalBotAccountIds:1"] = "666666666"
        });
        var detector = new BotChatterDetector(configuration);

        Assert.True(detector.IsBot("555555555", badges: null));
        Assert.True(detector.IsBot("666666666", badges: null));
    }

    [Fact]
    public void IsBot_UnknownAccountIdWithoutBadge_ReturnsFalse()
    {
        var detector = CreateDetector();

        var result = detector.IsBot("123456789", badges: null);

        Assert.False(result);
    }

    [Fact]
    public void IsBot_NullChatterIdAndNullBadges_ReturnsFalse_NoException()
    {
        var detector = CreateDetector();

        var result = detector.IsBot(chatterId: null, badges: null);

        Assert.False(result);
    }

    [Fact]
    public void IsBot_EmptyChatterIdAndEmptyBadges_ReturnsFalse_NoException()
    {
        var detector = CreateDetector();

        var result = detector.IsBot(chatterId: "", badges: []);

        Assert.False(result);
    }

    [Fact]
    public void IsBot_ConfigMissing_StaticListStillWorks()
    {
        var detector = new BotChatterDetector(new ConfigurationBuilder().Build());

        var result = detector.IsBot(NightbotId, badges: null);

        Assert.True(result);
    }

    [Fact]
    public void IsBot_ConfigEmpty_StaticListStillWorks()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Twitch:AdditionalBotAccountIds"] = ""
        });
        var detector = new BotChatterDetector(configuration);

        var result = detector.IsBot(NightbotId, badges: null);

        Assert.True(result);
    }

    [Fact]
    public void IsBot_ConfigOnlyCommas_StaticListStillWorks_AndNoUnknownIdMatches()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Twitch:AdditionalBotAccountIds"] = ",,,"
        });
        var detector = new BotChatterDetector(configuration);

        Assert.True(detector.IsBot(NightbotId, badges: null));
        Assert.False(detector.IsBot("123456789", badges: null));
    }

    [Fact]
    public void IsBot_ConfigValueWithSurroundingWhitespace_IsRecognized()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Twitch:AdditionalBotAccountIds"] = "  555555555 , 666666666  "
        });
        var detector = new BotChatterDetector(configuration);

        Assert.True(detector.IsBot("555555555", badges: null));
        Assert.True(detector.IsBot("666666666", badges: null));
    }

    [Fact]
    public void IsBot_ConfigDuplicatesAStaticId_NoErrorAndStillDetected()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Twitch:AdditionalBotAccountIds"] = NightbotId
        });
        var detector = new BotChatterDetector(configuration);

        var result = detector.IsBot(NightbotId, badges: null);

        Assert.True(result);
    }

    [Fact]
    public void IsBot_BadgeWithDifferentKey_ReturnsFalse()
    {
        var detector = CreateDetector();

        var result = detector.IsBot("123456789", [new("moderator", "1")]);

        Assert.False(result);
    }

    private static BotChatterDetector CreateDetector() => new(new ConfigurationBuilder().Build());

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
