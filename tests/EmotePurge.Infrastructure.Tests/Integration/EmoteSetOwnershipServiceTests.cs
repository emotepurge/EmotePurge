using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class EmoteSetOwnershipServiceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CheckAsync_ReturnsOtherTrackedChannel_WhenActiveSetIdMatches()
    {
        // Tier 2: two channels we already track share the same ActiveEmoteSetId — the cheap,
        // no-network DB cross-check must catch this regardless of what 7TV says.
        await using var db = fixture.CreateDbContext();

        var channelA = new Channel { ChannelName = "sharedsettest_a", TwitchChannelId = "1001", ActiveEmoteSetId = "set-shared" };
        var channelB = new Channel { ChannelName = "sharedsettest_b", TwitchChannelId = "1002", ActiveEmoteSetId = "set-shared" };
        db.Channels.AddRange(channelA, channelB);
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync("1001", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user-a", "set-shared")));
        sevenTv.GetEmoteSetOwnerIdAsync("set-shared", Arg.Any<CancellationToken>())
            .Returns("7tv-user-a");

        var service = CreateService(db, sevenTv);

        var result = await service.CheckAsync("sharedsettest_a", caller: null);

        Assert.True(result.Available);
        Assert.True(result.IsOwnSet);
        Assert.Contains("sharedsettest_b", result.OtherTrackedChannelsSharingSet);
        Assert.Empty(result.OtherModeratedChannelsSharingSet);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnavailable_WhenChannelNeverSynced()
    {
        await using var db = fixture.CreateDbContext();

        db.Channels.Add(new Channel { ChannelName = "neversynctest", TwitchChannelId = null, ActiveEmoteSetId = "" });
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        var service = CreateService(db, sevenTv);

        var result = await service.CheckAsync("neversynctest", caller: null);

        Assert.False(result.Available);
        Assert.False(result.IsOwnSet);
        Assert.Empty(result.OtherTrackedChannelsSharingSet);
        Assert.Empty(result.OtherModeratedChannelsSharingSet);
        await sevenTv.DidNotReceive().ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_ReturnsModeratedChannelMatch_ForUntrackedChannel()
    {
        // Tier 3: the caller moderates a channel we've never joined/synced at all (no DB row) —
        // Tier 2 alone can never see this, only a live 7TV lookup against the moderated list can.
        await using var db = fixture.CreateDbContext();

        db.Channels.Add(new Channel { ChannelName = "tier3test_owner", TwitchChannelId = "2001", ActiveEmoteSetId = "set-tier3" });
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync("2001", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user-owner", "set-tier3")));
        sevenTv.GetEmoteSetOwnerIdAsync("set-tier3", Arg.Any<CancellationToken>())
            .Returns("7tv-user-owner");
        // The untracked moderated channel's broadcaster id resolves to the SAME active set.
        sevenTv.ResolveSevenTvIdentityAsync("3002", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user-untracked", "set-tier3")));

        var service = CreateService(db, sevenTv, Moderated(("tier3test_untracked", "3002")));

        var result = await service.CheckAsync("tier3test_owner", Caller());

        Assert.Contains("tier3test_untracked", result.OtherModeratedChannelsSharingSet);
        Assert.Empty(result.OtherTrackedChannelsSharingSet);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNoModeratedMatches_WhenTheCallerModeratesNothing()
    {
        // An empty list is a real answer: there is simply no Tier-3 candidate to resolve, so 7TV is
        // asked exactly once — for the channel under test itself.
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "tier3empty_owner", TwitchChannelId = "ownership4001", ActiveEmoteSetId = "set-empty" });
        await db.SaveChangesAsync();

        var sevenTv = OwnedSet("ownership4001", "set-empty");
        var logger = new RecordingLogger<EmoteSetOwnershipService>();
        var service = CreateService(db, sevenTv, Moderated(), logger);

        var result = await service.CheckAsync("tier3empty_owner", Caller());

        Assert.Empty(result.OtherModeratedChannelsSharingSet);
        await sevenTv.Received(1).ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Nothing failed here, so nothing is reported.
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNoModeratedMatches_AndLogs_WhenTheModeratedListCouldNotBeDetermined()
    {
        // Same empty Tier-3 result as above — the warning DTO has no way to express a degraded
        // Tier 3 — so the log line is the only thing separating "moderates nothing" from "we could
        // not find out", and an incomplete warning must be traceable to its cause.
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "tier3unknown_owner", TwitchChannelId = "ownership5001", ActiveEmoteSetId = "set-unknown" });
        await db.SaveChangesAsync();

        var sevenTv = OwnedSet("ownership5001", "set-unknown");
        var logger = new RecordingLogger<EmoteSetOwnershipService>();
        var service = CreateService(db, sevenTv, new ModeratedChannelsLookup(null, ReauthRequired: false), logger);

        var result = await service.CheckAsync("tier3unknown_owner", Caller());

        Assert.Empty(result.OtherModeratedChannelsSharingSet);
        await sevenTv.Received(1).ResolveSevenTvIdentityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    // AK 33 (spec 2026-09-20, E9): the three tiers parametrized on an explicit, non-active
    // emoteSetId — the picker can ask about a target set the channel is not currently using at all.

    [Fact]
    public async Task CheckAsync_WithExplicitEmoteSetId_RunsTier1AgainstThatSet_NotTheChannelsActiveSet()
    {
        await using var db = fixture.CreateDbContext();

        db.Channels.Add(new Channel
        {
            ChannelName = "settierx_owner",
            TwitchChannelId = "6001",
            ActiveEmoteSetId = "set-active-x",
        });
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync("6001", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user-x", "set-active-x")));
        // The channel's own active set has a DIFFERENT owner than the requested target — proof that
        // Tier 1 checked the target, not the active set (which would have reported IsOwnSet: true).
        sevenTv.GetEmoteSetOwnerIdAsync("set-active-x", Arg.Any<CancellationToken>()).Returns("7tv-user-x");
        sevenTv.GetEmoteSetOwnerIdAsync("set-other-x", Arg.Any<CancellationToken>()).Returns("7tv-user-someone-else");

        var service = CreateService(db, sevenTv);

        var result = await service.CheckAsync("settierx_owner", caller: null, emoteSetId: "set-other-x");

        Assert.True(result.Available);
        Assert.False(result.IsOwnSet);
        await sevenTv.Received(1).GetEmoteSetOwnerIdAsync("set-other-x", Arg.Any<CancellationToken>());
        await sevenTv.DidNotReceive().GetEmoteSetOwnerIdAsync("set-active-x", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_WithExplicitEmoteSetId_RunsTier2AgainstThatSet()
    {
        await using var db = fixture.CreateDbContext();

        var channelA = new Channel
        {
            ChannelName = "settierx_a",
            TwitchChannelId = "6002",
            ActiveEmoteSetId = "set-active-a",
        };
        // Tracks the TARGET set as its own active one — Tier 2 must find this even though it has
        // nothing to do with channelA's own active set.
        var channelB = new Channel
        {
            ChannelName = "settierx_b",
            TwitchChannelId = "6003",
            ActiveEmoteSetId = "set-target-x",
        };
        db.Channels.AddRange(channelA, channelB);
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync("6002", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user-a", "set-active-a")));
        sevenTv.GetEmoteSetOwnerIdAsync("set-target-x", Arg.Any<CancellationToken>()).Returns("7tv-user-a");

        var service = CreateService(db, sevenTv);

        var result = await service.CheckAsync("settierx_a", caller: null, emoteSetId: "set-target-x");

        Assert.Contains("settierx_b", result.OtherTrackedChannelsSharingSet);
    }

    [Fact]
    public async Task CheckAsync_WithExplicitEmoteSetId_RunsTier3AgainstThatSet()
    {
        await using var db = fixture.CreateDbContext();

        db.Channels.Add(new Channel
        {
            ChannelName = "settierx_owner3",
            TwitchChannelId = "6004",
            ActiveEmoteSetId = "set-active-3",
        });
        await db.SaveChangesAsync();

        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync("6004", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user-3", "set-active-3")));
        sevenTv.GetEmoteSetOwnerIdAsync("set-target-3", Arg.Any<CancellationToken>()).Returns("7tv-user-3");
        // The untracked moderated channel's broadcaster resolves to the TARGET set, not the current
        // channel's own active set.
        sevenTv.ResolveSevenTvIdentityAsync("7002", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user-untracked3", "set-target-3")));

        var service = CreateService(db, sevenTv, Moderated(("settierx_untracked3", "7002")));

        var result = await service.CheckAsync("settierx_owner3", Caller(), emoteSetId: "set-target-3");

        Assert.Contains("settierx_untracked3", result.OtherModeratedChannelsSharingSet);
    }

    private static TwitchPrincipalInfo Caller() => new("caller-id", "callerlogin", "caller-token");

    private static ModeratedChannelsLookup Moderated(params (string Login, string BroadcasterId)[] channels) =>
        new([.. channels.Select(c => new TwitchModeratedChannelInfo(c.Login, c.BroadcasterId))], ReauthRequired: false);

    private static ISevenTvApiClient OwnedSet(string twitchChannelId, string emoteSetId)
    {
        var sevenTv = Substitute.For<ISevenTvApiClient>();
        sevenTv.ResolveSevenTvIdentityAsync(twitchChannelId, Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("7tv-user", emoteSetId)));
        sevenTv.GetEmoteSetOwnerIdAsync(emoteSetId, Arg.Any<CancellationToken>()).Returns("7tv-user");
        return sevenTv;
    }

    private static EmoteSetOwnershipService CreateService(
        AppDbContext db,
        ISevenTvApiClient sevenTv,
        ModeratedChannelsLookup? moderated = null,
        ILogger<EmoteSetOwnershipService>? logger = null)
    {
        var moderatedChannelsProvider = Substitute.For<IModeratedChannelsProvider>();
        moderatedChannelsProvider.GetModeratedChannelsAsync(Arg.Any<TwitchPrincipalInfo>(), Arg.Any<CancellationToken>())
            .Returns(moderated ?? Moderated());

        return new EmoteSetOwnershipService(
            db,
            sevenTv,
            moderatedChannelsProvider,
            logger ?? new RecordingLogger<EmoteSetOwnershipService>());
    }
}
