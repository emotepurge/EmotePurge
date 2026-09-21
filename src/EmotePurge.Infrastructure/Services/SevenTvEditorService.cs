using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class SevenTvEditorService(
    ISevenTvApiClient sevenTvApiClient,
    IModRoleCache modRoleCache,
    IRateLimitTelemetry telemetry,
    ILogger<SevenTvEditorService> logger) : ISevenTvEditorService
{
    public async Task<SevenTvEditorGrantsLookupResult> GetEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var cached = await modRoleCache.TryGetSevenTvEditorGrantsAsync(twitchUserId, cancellationToken);
        // A miss here costs two 7TV REST calls (identity, then grants), which is what makes this hit
        // rate worth watching at all.
        telemetry.RecordCacheLookup(RateLimitCacheNames.SevenTvGrants, cached is not null);
        if (cached is not null)
        {
            return SevenTvEditorGrantsLookupResult.Ok(cached);
        }

        var identityResult = await sevenTvApiClient.ResolveSevenTvIdentityAsync(twitchUserId, cancellationToken);
        if (identityResult.Status != SevenTvLookupStatus.Ok)
        {
            // Not cached, and not reported as "edits nothing": a 7TV outage — or simply no 7TV
            // account at all — means "unknown", and storing either as a negative would lock genuine
            // editors out for the whole TTL.
            logger.LogInformation("7TV-Identität für {User} nicht auflösbar — Editor-Grants unbekannt.", twitchUserId);
            return SevenTvEditorGrantsLookupResult.Failed(identityResult.Status);
        }

        var editorOfResult = await sevenTvApiClient.GetEditorOfChannelsAsync(identityResult.Identity!.SevenTvUserId, cancellationToken);
        if (editorOfResult.Status != SevenTvLookupStatus.Ok)
        {
            logger.LogInformation("7TV-Editor-Grants für {User} nicht abrufbar.", twitchUserId);
            return SevenTvEditorGrantsLookupResult.Failed(editorOfResult.Status);
        }

        // The one place where grant logins get normalized. Previously done twice with two different
        // comparison strategies (OrdinalIgnoreCase in the access check, ToLowerInvariant dictionary
        // keys in the overview), so a change to 7TV's grant semantics had to be followed correctly in
        // both — and a test for one said nothing about the other. Entries is built first and the two
        // sets are derived from it, so there is exactly one projection over editorOf, not three.
        var entries = editorOfResult.Grants!
            .Select(grant => new SevenTvEditorGrantEntry(ChannelName.Normalize(grant.TwitchChannelLogin), grant.TwitchChannelId))
            .ToList();
        var grants = new SevenTvEditorGrants(
            new HashSet<string>(entries.Select(entry => entry.ChannelLogin), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(entries.Select(entry => entry.TwitchChannelId), StringComparer.Ordinal),
            entries);

        await modRoleCache.SetSevenTvEditorGrantsAsync(twitchUserId, grants, cancellationToken);
        return SevenTvEditorGrantsLookupResult.Ok(grants);
    }

}
