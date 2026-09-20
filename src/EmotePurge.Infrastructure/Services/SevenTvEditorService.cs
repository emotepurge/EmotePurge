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
            .Select(grant => new SevenTvEditorGrantEntry(
                ChannelName.Normalize(grant.TwitchChannelLogin), grant.TwitchChannelId, grant.SevenTvUserId))
            .ToList();
        var grants = new SevenTvEditorGrants(
            new HashSet<string>(entries.Select(entry => entry.ChannelLogin), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(entries.Select(entry => entry.TwitchChannelId), StringComparer.Ordinal),
            entries);

        await modRoleCache.SetSevenTvEditorGrantsAsync(twitchUserId, grants, cancellationToken);
        return SevenTvEditorGrantsLookupResult.Ok(grants);
    }

    public async Task<SevenTvEmoteSetOwnershipCheckResult> CheckEmoteSetOwnershipAsync(
        string actorTwitchUserId, string actorTwitchLogin, string emoteSetId, CancellationToken cancellationToken = default)
    {
        // Step 4a of the 6.7 ladder: an unknown set is 404, whatever the reason GetEmoteSetOwnerIdAsync
        // came back empty (unknown id or a transient 7TV hiccup) — the same "one code for a null owner
        // id" simplification EmoteSetOwnershipService's own Tier 1 already makes. The 503 below is
        // reserved for the *actor's* identity/grant resolution failing, not this lookup.
        var ownerId = await sevenTvApiClient.GetEmoteSetOwnerIdAsync(emoteSetId, cancellationToken);
        if (ownerId is null)
        {
            return SevenTvEmoteSetOwnershipCheckResult.SetNotFound();
        }

        // Does the actor own the set directly? Only a genuine transport/GraphQL failure is 503 here —
        // NoSevenTvAccount is a legitimate "not this way", not a reason to give up on the grants check
        // below.
        var ownIdentity = await sevenTvApiClient.ResolveSevenTvIdentityAsync(actorTwitchUserId, cancellationToken);
        if (ownIdentity.Status == SevenTvLookupStatus.Unavailable)
        {
            return SevenTvEmoteSetOwnershipCheckResult.Unavailable();
        }

        if (ownIdentity.Status == SevenTvLookupStatus.Ok
            && string.Equals(ownIdentity.Identity!.SevenTvUserId, ownerId, StringComparison.Ordinal))
        {
            return SevenTvEmoteSetOwnershipCheckResult.Owner(ownerId, actorTwitchLogin);
        }

        var grantsResult = await GetEditorGrantsAsync(actorTwitchUserId, cancellationToken);
        if (grantsResult.Status == SevenTvLookupStatus.Unavailable)
        {
            return SevenTvEmoteSetOwnershipCheckResult.Unavailable();
        }

        if (grantsResult.Status != SevenTvLookupStatus.Ok)
        {
            // NoSevenTvAccount: the actor edits nothing, because they have no 7TV account — a
            // complete (if negative) answer, not a degradation.
            return SevenTvEmoteSetOwnershipCheckResult.Forbidden();
        }

        foreach (var entry in grantsResult.Grants!.Entries)
        {
            var entryOwnerId = entry.SevenTvUserId;
            if (entryOwnerId is null)
            {
                // F10/AK 31: a grant cached before SevenTvUserId existed. Never read the missing id
                // as "not an editor" — resolve this channel's own 7TV identity live and judge on
                // that instead, exactly like the direct-ownership check above does for the actor.
                var resolved = await sevenTvApiClient.ResolveSevenTvIdentityAsync(entry.TwitchChannelId, cancellationToken);
                if (resolved.Status == SevenTvLookupStatus.Unavailable)
                {
                    return SevenTvEmoteSetOwnershipCheckResult.Unavailable();
                }

                entryOwnerId = resolved.Status == SevenTvLookupStatus.Ok ? resolved.Identity!.SevenTvUserId : null;
            }

            if (entryOwnerId is not null && string.Equals(entryOwnerId, ownerId, StringComparison.Ordinal))
            {
                return SevenTvEmoteSetOwnershipCheckResult.Owner(ownerId, entry.ChannelLogin);
            }
        }

        return SevenTvEmoteSetOwnershipCheckResult.Forbidden();
    }
}
