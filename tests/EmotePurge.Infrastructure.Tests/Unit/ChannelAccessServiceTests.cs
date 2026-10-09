using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The authorization decision core, pinned down without a container: every collaborator is an
// interface, and IConfiguration comes from an in-memory collection. Separate from
// ChannelAccessServiceAdminTests, which covers only the allow-list parsing of IsGlobalAdmin.
//
// The single most valuable assertion in this file is
// CanViewUsageStatsAsync_DeniesAccess_WhenSevenTvCannotAnswer: a "?? true" instead of the "?? false"
// on the grants branch is one character, produces no build error and no failing test anywhere else,
// and would open every channel's usage statistics to every logged-in user.
public class ChannelAccessServiceTests
{
    [Fact]
    public async Task CanManageChannelAsync_AllowsGlobalAdmin_WithoutConsultingTwitch()
    {
        var moderatorCheck = Substitute.For<IModeratorCheckService>();
        var channels = Substitute.For<IChannelService>();
        var service = CreateService(
            moderatorCheck: moderatorCheck,
            channelService: channels,
            settings: new() { ["Auth:AdminTwitchUserIds"] = "42" });

        Assert.True(await service.CanManageChannelAsync(Principal("sensitron"), "somechannel"));

        // The admin branch short-circuits before the channel lookup — an admin must stay in
        // control even for a channel row that does not exist yet.
        await channels.DidNotReceive().GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await moderatorCheck.DidNotReceive().IsModeratorAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanManageChannelAsync_AllowsAnAdminLogin_OnTheTransitionalLoginList_WhenNoIdListIsConfigured()
    {
        var service = CreateService(
            channelService: Substitute.For<IChannelService>(),
            settings: new() { ["Auth:AdminTwitchLogins"] = "HandOfBlood" });

        Assert.True(await service.CanManageChannelAsync(new TwitchPrincipalInfo("999", "handofblood", null), "somechannel"));
    }

    [Fact]
    public async Task CanManageChannelAsync_DoesNotTreatAnAdminLoginAsAdmin_OnceAnIdListIsConfigured()
    {
        var service = CreateService(settings: new()
        {
            ["Auth:AdminTwitchLogins"] = "HandOfBlood",
            ["Auth:AdminTwitchUserIds"] = "1",
        });

        Assert.False(await service.CanManageChannelAsync(new TwitchPrincipalInfo("999", "handofblood", null), "somechannel"));
    }

    [Fact]
    public async Task CanManageChannelAsync_AllowsBroadcaster_MatchedOnTheImmutableTwitchId()
    {
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var moderatorCheck = Substitute.For<IModeratorCheckService>();
        var service = CreateService(moderatorCheck: moderatorCheck, channelService: channels);

        // Login deliberately differs from the channel name: the id is what decides, so a renamed
        // broadcaster keeps access to their own channel row.
        Assert.True(await service.CanManageChannelAsync(new TwitchPrincipalInfo("1001", "renamed_streamer", null), "streamer"));
        await moderatorCheck.DidNotReceive().IsModeratorAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanManageChannelAsync_FallsBackToTheLogin_WhenTheChannelHasNoTwitchIdYet()
    {
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = null });
        var service = CreateService(channelService: channels);

        Assert.True(await service.CanManageChannelAsync(Principal("streamer"), "streamer"));
    }

    [Fact]
    public async Task CanManageChannelAsync_DeniesBroadcaster_WhenTheLoginMatchesButTheTwitchIdDoesNot()
    {
        // The rename-squatting guard: Twitch releases freed-up logins for re-registration, so a
        // pure login comparison would hand the channel to whoever grabbed the old name next.
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var moderatorCheck = Substitute.For<IModeratorCheckService>();
        moderatorCheck.IsModeratorAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService(moderatorCheck: moderatorCheck, channelService: channels);

        Assert.False(await service.CanManageChannelAsync(new TwitchPrincipalInfo("9999", "streamer", null), "streamer"));
    }

    [Fact]
    public async Task CanManageChannelAsync_FallsThroughToTheModeratorCheck_ForEveryoneElse()
    {
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var moderatorCheck = Substitute.For<IModeratorCheckService>();
        moderatorCheck.IsModeratorAsync(Arg.Any<TwitchPrincipalInfo>(), "streamer", Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(moderatorCheck: moderatorCheck, channelService: channels);

        Assert.True(await service.CanManageChannelAsync(Principal("somemod"), "streamer"));
    }

    [Fact]
    public async Task CanManageChannelAsync_NormalizesTheChannelName_BeforeEveryLookup()
    {
        // Twitch logins are typed with capitals ("HandOfBlood"); the database holds them lowercase.
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var moderatorCheck = Substitute.For<IModeratorCheckService>();
        moderatorCheck.IsModeratorAsync(Arg.Any<TwitchPrincipalInfo>(), "streamer", Arg.Any<CancellationToken>())
            .Returns(true);
        var service = CreateService(moderatorCheck: moderatorCheck, channelService: channels);

        Assert.True(await service.CanManageChannelAsync(Principal("somemod"), "  Streamer  "));
        await channels.Received().GetByNameAsync("streamer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CanViewUsageStatsAsync_DeniesAccess_WhenSevenTvCannotAnswer()
    {
        // The one-character trap from the report: a non-Ok status means "7TV could not tell us", and
        // the only safe reading of that is "no grant". Answering true here would open every channel's
        // usage statistics to every logged-in user, and a 7TV outage would be the trigger.
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync("42", Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.Unavailable));
        var service = CreateService(sevenTvEditorService: editors, channelService: channels);

        Assert.False(await service.CanViewUsageStatsAsync(Principal("stranger"), "streamer"));
    }

    [Fact]
    public async Task CanViewUsageStatsAsync_DeniesAccess_WhenTheUserHasNoSevenTvAccountAtAll()
    {
        // Issue #37 regression guard: NoSevenTvAccount is not an outage, it is the common case of a
        // user who never signed up for 7TV — but this is still an authorization check, so it must
        // fail closed exactly like Unavailable does. Loosening this to "only Unavailable denies"
        // would open every channel's usage statistics to every 7TV-account-less logged-in user.
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync("42", Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        var service = CreateService(sevenTvEditorService: editors, channelService: channels);

        Assert.False(await service.CanViewUsageStatsAsync(Principal("stranger"), "streamer"));
    }

    [Fact]
    public async Task CanViewUsageStatsAsync_DeniesAccess_WhenSevenTvAnswersWithNoGrants()
    {
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync("42", Arg.Any<CancellationToken>()).Returns(SevenTvEditorGrantsLookupResult.Ok(Grants()));
        var service = CreateService(sevenTvEditorService: editors, channelService: channels);

        Assert.False(await service.CanViewUsageStatsAsync(Principal("stranger"), "streamer"));
    }

    [Fact]
    public async Task CanViewUsageStatsAsync_AllowsSevenTvEditor_MatchedOnTheTwitchChannelId()
    {
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync("42", Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(Grants(logins: ["someoneelse"], twitchIds: ["1001"])));
        var service = CreateService(sevenTvEditorService: editors, channelService: channels);

        // The login set deliberately does not contain "streamer": with an id on the row, the id is
        // the only thing that may decide — same reasoning as the broadcaster check.
        Assert.True(await service.CanViewUsageStatsAsync(Principal("editor"), "streamer"));
    }

    [Fact]
    public async Task CanViewUsageStatsAsync_DeniesSevenTvEditorOfAnotherChannel_EvenWhenTheLoginSetMatches()
    {
        // Multi-tenant isolation on the 7TV axis: an editor grant for channel B must not unlock
        // channel A just because A's login happens to sit in the login set.
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = "1001" });
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync("42", Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(Grants(logins: ["streamer"], twitchIds: ["2002"])));
        var service = CreateService(sevenTvEditorService: editors, channelService: channels);

        Assert.False(await service.CanViewUsageStatsAsync(Principal("editor"), "streamer"));
    }

    [Fact]
    public async Task CanViewUsageStatsAsync_FallsBackToTheLoginSet_WhenTheChannelHasNoTwitchIdYet()
    {
        var channels = Substitute.For<IChannelService>();
        channels.GetByNameAsync("streamer", Arg.Any<CancellationToken>())
            .Returns(new Channel { ChannelName = "streamer", TwitchChannelId = null });
        var editors = Substitute.For<ISevenTvEditorService>();
        editors.GetEditorGrantsAsync("42", Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(Grants(logins: ["streamer"], twitchIds: ["2002"])));
        var service = CreateService(sevenTvEditorService: editors, channelService: channels);

        Assert.True(await service.CanViewUsageStatsAsync(Principal("editor"), "streamer"));
    }

    [Fact]
    public async Task CanViewUsageStatsAsync_ShortCircuits_WithoutCalling7Tv_WhenTheCallerCanManageTheChannel()
    {
        // Not cosmetic: the 7TV path is the most expensive authorization branch in the app, and
        // these endpoints are pollable by a viewer.
        var editors = Substitute.For<ISevenTvEditorService>();
        var service = CreateService(
            sevenTvEditorService: editors,
            settings: new() { ["Auth:AdminTwitchUserIds"] = "42" });

        Assert.True(await service.CanViewUsageStatsAsync(Principal("sensitron"), "streamer"));
        await editors.DidNotReceive().GetEditorGrantsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static TwitchPrincipalInfo Principal(string login) => new("42", login, AccessToken: null);

    private static SevenTvEditorGrants Grants(string[]? logins = null, string[]? twitchIds = null) =>
        new(new HashSet<string>(logins ?? []), new HashSet<string>(twitchIds ?? []));

    private static ChannelAccessService CreateService(
        IModeratorCheckService? moderatorCheck = null,
        ISevenTvEditorService? sevenTvEditorService = null,
        IChannelService? channelService = null,
        Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var allowlist = new GlobalAdminAllowlist(configuration, NullLogger<GlobalAdminAllowlist>.Instance);

        return new ChannelAccessService(
            moderatorCheck ?? Substitute.For<IModeratorCheckService>(),
            sevenTvEditorService ?? Substitute.For<ISevenTvEditorService>(),
            channelService ?? Substitute.For<IChannelService>(),
            allowlist,
            NullLogger<ChannelAccessService>.Instance);
    }
}
