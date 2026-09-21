using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The legacy editor-grant payload (a <c>7tveditor:</c> entry written before <c>entries</c> existed)
/// against a real <see cref="ModRoleCache"/>/Redis, once per reader that walks
/// <see cref="SevenTvEditorGrants.Entries"/>. Both used to take that shape for "edits nothing": the
/// target list (<c>GET /api/seventv/me/emote-set-targets</c>) silently dropped the granted accounts,
/// and the set-centric import's owner check answered a legitimate editor with 403 — after the 7TV
/// mutation, so the audit entry was lost for good.
/// </summary>
[Collection("Redis")]
public class SevenTvEditorServiceTests(RedisFixture fixture)
{
    private const string ActorSevenTvId = "01ACTOR";
    private const string EditedTwitchId = "49140130";
    private const string EditedLogin = "handofblood";
    private const string EditedSevenTvId = "01EDITED";
    private const string EmoteSetId = "01TARGETSET";

    /// <summary>
    /// The target list's reading: it builds one account per <see cref="SevenTvEditorGrantEntry"/>
    /// of this very result, so a refreshed, non-empty <c>Entries</c> is exactly what puts the
    /// granted account back into the picker. The endpoint itself is not driven here — the Api tests
    /// run container-free with the editor service substituted, so a legacy payload in Redis cannot
    /// reach it there.
    /// </summary>
    [Fact]
    public async Task GetEditorGrantsAsync_RefreshesALegacyPayloadLive_SoTheTargetListSeesTheGrantedAccount()
    {
        var actorTwitchId = NewActorTwitchId();
        await PlantLegacyPayloadAsync(actorTwitchId);
        var client = ClientResolvingGrants(actorTwitchId);
        var editors = CreateEditorService(client);

        var result = await editors.GetEditorGrantsAsync(actorTwitchId);

        Assert.Equal(SevenTvLookupStatus.Ok, result.Status);
        var entry = Assert.Single(result.Grants!.Entries);
        Assert.Equal(EditedTwitchId, entry.TwitchChannelId);
        Assert.Equal(EditedLogin, entry.ChannelLogin);
        await client.Received(1).GetEditorOfChannelsAsync(ActorSevenTvId, Arg.Any<CancellationToken>());

        // The refresh wrote the current shape back: the next read is a hit, not another live lookup.
        Assert.Single((await editors.GetEditorGrantsAsync(actorTwitchId)).Grants!.Entries);
        await client.Received(1).GetEditorOfChannelsAsync(ActorSevenTvId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The owner check's reading: a set owned by the granted account is admissible under that
    /// account's login, although the cached grants were the legacy shape when the report arrived.
    /// </summary>
    [Fact]
    public async Task TheImportOwnerCheck_FindsTheGrantedAccount_WhenTheCachedGrantsAreALegacyPayload()
    {
        var actorTwitchId = NewActorTwitchId();
        await PlantLegacyPayloadAsync(actorTwitchId);
        var client = ClientResolvingGrants(actorTwitchId);

        var lists = Substitute.For<ISevenTvEmoteSetListService>();
        lists.ListByTwitchIdAsync(actorTwitchId, Arg.Any<CancellationToken>())
            .Returns(EmoteSetListResult.Ok(new EmoteSetList(null, [], ActorSevenTvId)));
        lists.ListByTwitchIdAsync(EditedTwitchId, Arg.Any<CancellationToken>())
            .Returns(EmoteSetListResult.Ok(new EmoteSetList(
                null, [new EmoteSetSummary(EmoteSetId, "Emotes", 1000, "NORMAL", false, "HandOfBlood", EditedSevenTvId)], EditedSevenTvId)));

        var ownership = new ImportTargetOwnershipService(
            lists,
            CreateEditorService(client),
            client,
            new ForeignSevenTvBreakerPolicy(),
            new ForeignEmoteSetProviderBudget(),
            NullLogger<ImportTargetOwnershipService>.Instance);

        var result = await ownership.CheckAsync(actorTwitchId, "actor", EmoteSetId);

        Assert.Equal(SevenTvEmoteSetOwnershipStatus.Owner, result.Status);
        Assert.Equal(EditedSevenTvId, result.OwnerSevenTvUserId);
        Assert.Equal(EditedLogin, result.OwnerTwitchLogin);
        await client.DidNotReceive().LookUpEmoteSetOwnerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static string NewActorTwitchId() => $"editor-service-{Guid.NewGuid():N}";

    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ModCheckCacheTtlMinutes"] = "10" })
            .Build();

    private static ISevenTvApiClient ClientResolvingGrants(string actorTwitchId)
    {
        var client = Substitute.For<ISevenTvApiClient>();
        client.ResolveSevenTvIdentityAsync(actorTwitchId, Arg.Any<CancellationToken>())
            .Returns(SevenTvIdentityResult.Ok(new SevenTvIdentity(ActorSevenTvId, null)));
        client.GetEditorOfChannelsAsync(ActorSevenTvId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsResult.Ok([new SevenTvEditorGrant(EditedLogin, EditedTwitchId)]));
        return client;
    }

    // Written by hand, not through SetSevenTvEditorGrantsAsync: the shape a cache entry from before
    // the entries field existed still has in Redis, up to its TTL — grant sets, no entries.
    private Task PlantLegacyPayloadAsync(string actorTwitchId) =>
        fixture.Connection.GetDatabase().StringSetAsync(
            $"7tveditor:{actorTwitchId}",
            $$"""{"channelLogins":["{{EditedLogin}}"],"twitchChannelIds":["{{EditedTwitchId}}"]}""");

    private SevenTvEditorService CreateEditorService(ISevenTvApiClient client) => new(
        client,
        new ModRoleCache(fixture.Connection, BuildConfiguration(), NullLogger<ModRoleCache>.Instance),
        new RecordingRateLimitTelemetry(),
        NullLogger<SevenTvEditorService>.Instance);
}
