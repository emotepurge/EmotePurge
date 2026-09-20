using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// <see cref="SevenTvEditorService.CheckEmoteSetOwnershipAsync"/> — the set-centric import's owner
/// check (spec 6.7, E22) — against a real <see cref="ModRoleCache"/>/Redis, because the case it has
/// to get right (AK 31, F10) is specifically about what a <em>cached</em> grant looks like: a
/// <c>SevenTvEditorGrantEntry</c> written before <see cref="SevenTvEditorGrantEntry.SevenTvUserId"/>
/// existed round-trips through Redis with that field simply missing (<c>null</c>), and the check must
/// resolve it live rather than reading the absence as "not an editor".
/// </summary>
[Collection("Redis")]
public class SevenTvEditorServiceTests(RedisFixture fixture)
{
    private const string ActorTwitchUserId = "100";
    private const string ActorTwitchLogin = "actorlogin";
    private const string EmoteSetId = "set-x";
    private const string OwnerSevenTvUserId = "owner-seven-tv-id";

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ModCheckCacheTtlMinutes"] = "10" })
            .Build();

    [Fact]
    public async Task CheckEmoteSetOwnershipAsync_ResolvesALegacyGrantLive_AndGrantsAccessWhenItMatchesTheOwner()
    {
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);

        // A grant cached before SevenTvUserId existed: functionally identical to what deserializing a
        // pre-upgrade JSON payload produces (ModRoleCache.TryGetSevenTvEditorGrantsAsync maps a
        // missing "sevenTvUserId" property to null the same way).
        var legacyEntry = new SevenTvEditorGrantEntry("ownerlogin", "555");
        await cache.SetSevenTvEditorGrantsAsync(
            ActorTwitchUserId,
            new SevenTvEditorGrants(
                new HashSet<string> { "ownerlogin" }, new HashSet<string> { "555" }, [legacyEntry]));

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetEmoteSetOwnerIdAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(OwnerSevenTvUserId);
        // The actor does not own the set directly — falls through to the grants check.
        apiClient.ResolveSevenTvIdentityAsync(ActorTwitchUserId, Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        // F10/AK 31: the legacy entry's own channel (Twitch id "555") is resolved live, and its 7TV
        // identity matches the set's owner.
        apiClient.ResolveSevenTvIdentityAsync("555", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity(OwnerSevenTvUserId, null)));

        var service = new SevenTvEditorService(apiClient, cache, new RecordingRateLimitTelemetry(), NullLogger<SevenTvEditorService>.Instance);

        var result = await service.CheckEmoteSetOwnershipAsync(ActorTwitchUserId, ActorTwitchLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(OwnerSevenTvUserId, result.OwnerSevenTvUserId);
        // The matching grant's own channel login (not the actor's) — spec 6.7.
        Assert.Equal("ownerlogin", result.OwnerTwitchLogin);
        // The whole point: the missing id was actually resolved live, not silently read as "no match".
        await apiClient.Received(1).ResolveSevenTvIdentityAsync("555", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckEmoteSetOwnershipAsync_SkipsTheLiveResolve_ForAGrantThatAlreadyCarriesAnId_AndStillFindsALaterMatch()
    {
        var cache = new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance);

        // One legacy entry that will not match once resolved (proves the loop does not stop on the
        // first non-matching legacy entry), and one modern entry that already carries the right id.
        var legacyEntry = new SevenTvEditorGrantEntry("someoneelse", "111");
        var modernEntry = new SevenTvEditorGrantEntry("ownerlogin", "555", OwnerSevenTvUserId);
        await cache.SetSevenTvEditorGrantsAsync(
            ActorTwitchUserId,
            new SevenTvEditorGrants(
                new HashSet<string> { "someoneelse", "ownerlogin" },
                new HashSet<string> { "111", "555" },
                [legacyEntry, modernEntry]));

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetEmoteSetOwnerIdAsync(EmoteSetId, Arg.Any<CancellationToken>())
            .Returns(OwnerSevenTvUserId);
        apiClient.ResolveSevenTvIdentityAsync(ActorTwitchUserId, Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        apiClient.ResolveSevenTvIdentityAsync("111", Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity("some-other-seven-tv-id", null)));

        var service = new SevenTvEditorService(apiClient, cache, new RecordingRateLimitTelemetry(), NullLogger<SevenTvEditorService>.Instance);

        var result = await service.CheckEmoteSetOwnershipAsync(ActorTwitchUserId, ActorTwitchLogin, EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(OwnerSevenTvUserId, result.OwnerSevenTvUserId);
        Assert.Equal("ownerlogin", result.OwnerTwitchLogin);
        // The legacy entry was resolved live (it has no cached id)...
        await apiClient.Received(1).ResolveSevenTvIdentityAsync("111", Arg.Any<CancellationToken>());
        // ...but the modern entry never needed a live lookup for its own identity at all.
        await apiClient.DidNotReceive().ResolveSevenTvIdentityAsync("555", Arg.Any<CancellationToken>());
    }
}
