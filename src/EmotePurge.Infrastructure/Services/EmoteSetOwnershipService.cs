using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class EmoteSetOwnershipService(
    AppDbContext db,
    ISevenTvApiClient sevenTvApiClient,
    IModeratedChannelsProvider moderatedChannelsProvider,
    ILogger<EmoteSetOwnershipService> logger) : IEmoteSetOwnershipService
{
    // Bounds how many parallel 7TV lookups Tier 3 fires off for a single check — a prolific mod's
    // moderated-channel list could otherwise flood 7TV with dozens of simultaneous requests.
    private const int MaxConcurrentTier3Lookups = 5;

    public async Task<EmoteSetWarningDto> CheckAsync(
        string channelName,
        TwitchPrincipalInfo? caller,
        string? emoteSetId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);

        if (channel is null || channel.TwitchChannelId is null)
        {
            return new EmoteSetWarningDto(Available: false, IsOwnSet: false, [], []);
        }

        // The set to check (E9): the caller's choice when given, the channel's own active set
        // otherwise — the only source before this parameter existed. Both tiers below were already
        // set-agnostic; this is what makes "which set" a caller decision instead of an implicit one.
        var targetSetId = emoteSetId ?? channel.ActiveEmoteSetId;
        if (string.IsNullOrEmpty(targetSetId))
        {
            return new EmoteSetWarningDto(Available: false, IsOwnSet: false, [], []);
        }

        // Tier 1: does this channel's own 7TV account actually own the target set?
        var ownIdentityResult = await sevenTvApiClient.ResolveSevenTvIdentityAsync(channel.TwitchChannelId, cancellationToken);
        var setOwnerId = await sevenTvApiClient.GetEmoteSetOwnerIdAsync(targetSetId, cancellationToken);

        var available = ownIdentityResult.Status == SevenTvLookupStatus.Ok && setOwnerId is not null;
        var isOwnSet = available && string.Equals(ownIdentityResult.Identity!.SevenTvUserId, setOwnerId, StringComparison.Ordinal);

        if (!available)
        {
            logger.LogWarning("Owner-Check für Channel {Channel} / Set {SetId} nicht möglich (7TV nicht erreichbar oder unbekannt).",
                normalized, targetSetId);
        }

        // Tier 2: other channels WE already track that happen to share the target set as their own
        // active one.
        var otherTracked = await db.Channels.AsNoTracking()
            .Where(c => c.ActiveEmoteSetId == targetSetId && c.Id != channel.Id)
            .Select(c => c.ChannelName)
            .ToListAsync(cancellationToken);

        // Tier 3: channels the acting user moderates on Twitch but that we've never tracked/synced —
        // Tier 2 can't see these at all, since they have no row (or a stale one) in our own DB.
        var otherModerated = await CheckModeratedChannelsAsync(
            targetSetId, normalized, otherTracked, caller, cancellationToken);

        return new EmoteSetWarningDto(available, isOwnSet, otherTracked, otherModerated);
    }

    private async Task<IReadOnlyList<string>> CheckModeratedChannelsAsync(
        string activeEmoteSetId,
        string currentChannelName,
        IReadOnlyList<string> alreadyCoveredByTier2,
        TwitchPrincipalInfo? caller,
        CancellationToken cancellationToken)
    {
        if (caller is null)
        {
            return [];
        }

        // The same shared list the overview and the moderator check read, so a Tier-3 pass inside the
        // cache TTL costs no Helix call at all.
        var moderated = await moderatedChannelsProvider.GetModeratedChannelsAsync(caller, cancellationToken);
        if (moderated.Channels is null)
        {
            // Tier 3 is skipped, not answered: the warning DTO has no field for a degraded tier, so
            // this log line is the only thing that separates it from a caller who genuinely
            // moderates nothing and therefore has no candidates.
            logger.LogInformation(
                "Tier-3-Prüfung für Channel {Channel} übersprungen: moderierte Channels von {User} nicht ermittelbar.",
                currentChannelName, caller.TwitchUserId);
            return [];
        }

        var alreadyCovered = new HashSet<string>(alreadyCoveredByTier2, StringComparer.OrdinalIgnoreCase) { currentChannelName };
        var candidates = moderated.Channels.Where(c => !alreadyCovered.Contains(c.Login)).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        using var semaphore = new SemaphoreSlim(MaxConcurrentTier3Lookups);
        var matches = new List<string>();
        var matchesLock = new object();

        var lookups = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var identityResult = await sevenTvApiClient.ResolveSevenTvIdentityAsync(candidate.BroadcasterId, cancellationToken);
                if (identityResult.Status == SevenTvLookupStatus.Ok && identityResult.Identity!.ActiveEmoteSetId == activeEmoteSetId)
                {
                    lock (matchesLock)
                    {
                        matches.Add(candidate.Login);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(lookups);
        return matches;
    }
}
