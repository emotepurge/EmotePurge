using System.Diagnostics;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The base implementation of the foreign-channel-import read path (spec 2026-09-09, F1): three
/// steps, in the order the spec mandates, none of them repeated or reordered. Hardening (cache,
/// coalescing, the circuit breaker, the provider-wide budget) is a separate decorator that wraps
/// <see cref="IForeignEmoteSetService"/> — nothing in here is aware of it.
/// </summary>
public class ForeignEmoteSetService(
    IChannelIdentityService channelIdentityService,
    ISevenTvApiClient sevenTvApiClient,
    IForeignUpstreamRequestBudget requestBudget,
    ILogger<ForeignEmoteSetService> logger) : IForeignEmoteSetService
{
    // refresh (T2, spec E3) is meaningless here: this implementation never caches anything, so there
    // is nothing for it to bypass. Only the hardening decorator that wraps this class interprets it.
    public async Task<ForeignEmoteSetLookupResult> GetForeignEmoteSetAsync(
        string channelName, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        // One permit per upstream request (E5b). The two charges in this method cover the two calls it
        // makes itself; the paginated set read charges its own pages inside the client, since only
        // there is the number of requests known. Charging once per *resolution* — the shape this had
        // until Codex pointed it out — would have let one permit stand for up to twelve requests.
        //
        // Charged around the identity service's call rather than inside it: LookupByLoginAsync is
        // shared with the join path and the worker's reconcile, and neither of those belongs to this
        // feature's budget.
        if (!await requestBudget.TryChargeRequestAsync(cancellationToken))
        {
            logger.LogWarning(
                "Fremdkanal-Vorschau für {ChannelName}: providerweites Request-Budget erschöpft, Helix wurde nicht gefragt.", normalized);
            return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted);
        }

        // Step 1 (F1): the fully-solved Twitch half of the chain — Normalize, app-token handling and
        // the Helix call, in one place, never re-implemented here.
        var twitchLookup = await channelIdentityService.LookupByLoginAsync(normalized, cancellationToken);
        if (twitchLookup.Status == TwitchUserLookupStatus.NotFound)
        {
            logger.LogInformation(
                "Fremdkanal-Vorschau für {ChannelName}: Twitch kennt diesen Login nicht.", normalized);
            return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.ChannelNotOnTwitch);
        }

        if (twitchLookup.Status == TwitchUserLookupStatus.Unavailable)
        {
            // Unlike ChannelService.JoinAsync, there is no existing row to "carry on" with here — a
            // read-only preview has nothing to fall back to when Twitch cannot be asked at all.
            logger.LogInformation(
                "Fremdkanal-Vorschau für {ChannelName}: Twitch/Helix nicht erreichbar.", normalized);
            return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.TwitchUnavailable);
        }

        var twitchUserId = twitchLookup.User!.Id;

        if (!await requestBudget.TryChargeRequestAsync(cancellationToken))
        {
            logger.LogWarning(
                "Fremdkanal-Vorschau für {ChannelName}: providerweites Request-Budget erschöpft, 7TV-Identität wurde nicht aufgelöst.", normalized);
            return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted);
        }

        // Step 2 (F1): userByConnection — never ResolveTwitchUserIdAsync/GqlUsersQuery, 7TV's search
        // endpoint, which this service must never call (AK 11). Charged here rather than inside the
        // client for the same reason as the Helix call above: SevenTvSyncService uses this method too.
        var identityResult = await sevenTvApiClient.ResolveSevenTvIdentityAsync(twitchUserId, cancellationToken);
        if (identityResult.Status == SevenTvLookupStatus.NoSevenTvAccount)
        {
            logger.LogInformation("Fremdkanal-Vorschau für {ChannelName}: kein 7TV-Account.", normalized);
            return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoSevenTvAccount);
        }

        if (identityResult.Status == SevenTvLookupStatus.Unavailable)
        {
            logger.LogInformation(
                "Fremdkanal-Vorschau für {ChannelName}: 7TV-Identitätsauflösung fehlgeschlagen.", normalized);
            return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.SevenTvUnavailable);
        }

        var identity = identityResult.Identity!;

        // F2: "account exists, no active set" is Ok with a null ActiveEmoteSetId on this path — never
        // SevenTvLookupStatus.NoActiveEmoteSet, which only the v3 REST path
        // (GetChannelStateForTwitchUserAsync, not used here) can ever produce.
        if (identity.ActiveEmoteSetId is null)
        {
            logger.LogInformation(
                "Fremdkanal-Vorschau für {ChannelName}: 7TV-Account ohne aktives Emote-Set.", normalized);
            return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.NoActiveEmoteSet);
        }

        // Step 3 (F1/F3): the paginated v4 preview query, scores included at no extra request cost.
        var previewResult = await sevenTvApiClient.GetEmoteSetPreviewAsync(identity.ActiveEmoteSetId, cancellationToken);
        switch (previewResult.Status)
        {
            case SevenTvPreviewLookupStatus.RateLimited:
                logger.LogWarning(
                    "Fremdkanal-Vorschau für {ChannelName}: 7TV meldet Überlast (429).", normalized);
                return ForeignEmoteSetLookupResult.Failed(
                    ForeignEmoteSetLookupStatus.SevenTvRateLimited, previewResult.RetryAfter);
            case SevenTvPreviewLookupStatus.Unavailable:
                logger.LogInformation(
                    "Fremdkanal-Vorschau für {ChannelName}: 7TV-Set-Abruf fehlgeschlagen.", normalized);
                return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.SevenTvUnavailable);
            case SevenTvPreviewLookupStatus.BudgetExhausted:
                // Our own throttle, not 7TV's — kept apart all the way up so the circuit breaker never
                // counts it as evidence about the provider.
                logger.LogWarning(
                    "Fremdkanal-Vorschau für {ChannelName}: providerweites Request-Budget während der Seitenabfrage erschöpft.", normalized);
                return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted);
            case SevenTvPreviewLookupStatus.Ok:
                break;
            default:
                throw new UnreachableException(
                    $"Unexpected {nameof(SevenTvPreviewLookupStatus)} value: {previewResult.Status}.");
        }

        var preview = previewResult.Preview!;
        var emotes = preview.Items
            .Select(item => new ForeignEmoteRow(
                item.SevenTvEmoteId, item.Alias, item.DefaultName, item.ImageUrl, item.TopAllTime, item.Trending))
            .ToList();

        return ForeignEmoteSetLookupResult.Ok(new ForeignEmoteSet(
            normalized, identity.SevenTvUserId, identity.ActiveEmoteSetId, preview.TotalCount, preview.Truncated, emotes,
            preview.Name, preview.Capacity));
    }

    // Set-ID mode (spec 2026-09-20, 6.4/E8): no identity resolution at all — neither Helix nor the
    // 7TV userByConnection lookup runs, so unlike GetForeignEmoteSetAsync above this charges no
    // request budget of its own. The paginated preview read (F1 step 3/F3) still charges its own
    // pages inside the client, exactly as it does for the login-based path.
    public async Task<ForeignEmoteSetLookupResult> GetForeignEmoteSetBySetIdAsync(
        string channelName, string emoteSetId, bool refresh = false, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var previewResult = await sevenTvApiClient.GetEmoteSetPreviewAsync(emoteSetId, cancellationToken);
        switch (previewResult.Status)
        {
            case SevenTvPreviewLookupStatus.RateLimited:
                logger.LogWarning(
                    "Set-Vorschau für {SetId} (Kanal {ChannelName}): 7TV meldet Überlast (429).", emoteSetId, normalized);
                return ForeignEmoteSetLookupResult.Failed(
                    ForeignEmoteSetLookupStatus.SevenTvRateLimited, previewResult.RetryAfter);
            case SevenTvPreviewLookupStatus.Unavailable:
                logger.LogInformation(
                    "Set-Vorschau für {SetId} (Kanal {ChannelName}): 7TV-Set-Abruf fehlgeschlagen.", emoteSetId, normalized);
                return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.SevenTvUnavailable);
            case SevenTvPreviewLookupStatus.BudgetExhausted:
                // Our own throttle, not 7TV's — kept apart all the way up so the circuit breaker never
                // counts it as evidence about the provider.
                logger.LogWarning(
                    "Set-Vorschau für {SetId} (Kanal {ChannelName}): providerweites Request-Budget während der Seitenabfrage erschöpft.",
                    emoteSetId, normalized);
                return ForeignEmoteSetLookupResult.Failed(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted);
            case SevenTvPreviewLookupStatus.Ok:
                break;
            default:
                throw new UnreachableException(
                    $"Unexpected {nameof(SevenTvPreviewLookupStatus)} value: {previewResult.Status}.");
        }

        var preview = previewResult.Preview!;
        var emotes = preview.Items
            .Select(item => new ForeignEmoteRow(
                item.SevenTvEmoteId, item.Alias, item.DefaultName, item.ImageUrl, item.TopAllTime, item.Trending))
            .ToList();

        // channelName is the route's channel, echoed — never resolved (E8). SevenTvUserId is always
        // null here: our Channel row holds no 7TV user id to report for a channel the caller may have
        // no role in at all.
        return ForeignEmoteSetLookupResult.Ok(new ForeignEmoteSet(
            normalized, null, emoteSetId, preview.TotalCount, preview.Truncated, emotes,
            preview.Name, preview.Capacity));
    }
}
