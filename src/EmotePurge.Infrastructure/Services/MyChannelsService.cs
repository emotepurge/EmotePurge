using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public class MyChannelsService(
    AppDbContext db,
    ITwitchHelixClient helixClient,
    ISevenTvEditorService sevenTvEditorService,
    IModeratedChannelsProvider moderatedChannelsProvider,
    ITwitchLiveStatusReader liveStatusReader,
    ITwitchAppTokenProvider appTokenProvider,
    ILogger<MyChannelsService> logger) : IMyChannelsService
{
    private sealed class ChannelFlags
    {
        public bool IsBroadcaster;
        public bool IsModerator;
        public bool IsSevenTvEditor;
    }

    public async Task<MyChannelsResultDto> GetMyChannelsAsync(TwitchPrincipalInfo principal, CancellationToken cancellationToken = default)
    {
        var selfLogin = ChannelName.Normalize(principal.TwitchLogin);

        // Helix's moderated-channels list only ever contains channels the user moderates for
        // someone else — it never includes the channel the user broadcasts themselves.
        var flagsByChannel = new Dictionary<string, ChannelFlags> { [selfLogin] = new() { IsBroadcaster = true } };

        // Shares the moderated-channel list (and therefore its cache) with the authorization path;
        // this used to paginate Helix here, uncached, on every single overview load. A null list is
        // the degradation the DTO reports as HelixUnavailable — an empty one is a complete answer
        // and must not raise that flag, or every user who moderates nothing would be told Twitch is
        // unreachable.
        var moderated = await moderatedChannelsProvider.GetModeratedChannelsAsync(principal, cancellationToken);
        var reauthRequired = moderated.ReauthRequired;
        var helixUnavailable = moderated.Channels is null;
        foreach (var moderatedChannel in moderated.Channels ?? [])
        {
            GetOrAdd(flagsByChannel, ChannelName.Normalize(moderatedChannel.Login)).IsModerator = true;
        }

        // Independent of the Twitch-role axis above — a 7TV editor grant doesn't require any Twitch
        // relationship at all, so this can add brand-new channel keys, not just annotate existing ones.
        // Shares ISevenTvEditorService (and therefore its cache) with the authorization path; this used
        // to run the same two-call chain here, uncached, on every single overview load.
        var grantsResult = await sevenTvEditorService.GetEditorGrantsAsync(principal.TwitchUserId, cancellationToken);
        var grants = grantsResult.Grants;
        // NoSevenTvAccount is not a degradation — it just means this user has no 7TV account at
        // all, which is the common case, not a failed lookup (issue #37). Only a genuine Unavailable
        // means the grant list may be incomplete.
        var sevenTvUnavailable = grantsResult.Status == SevenTvLookupStatus.Unavailable;

        var (isLegacyGrantPayload, grantTwitchIds, grantLogins) = PrepareSevenTvGrantLookup(grants, flagsByChannel);

        // One query for both criteria: channels already known by name (self/moderated/legacy-grant/
        // grant login) and channels the db tracks under a grant's Twitch id, whatever name they
        // currently have. Matching the db-tracked, renamed case here — rather than after a Helix
        // round trip — is exactly why this stays a single query instead of two.
        var trackedChannels = await db.Channels
            .AsNoTracking()
            .Where(c =>
                flagsByChannel.Keys.Contains(c.ChannelName) ||
                grantLogins.Contains(c.ChannelName) ||
                (c.TwitchChannelId != null && grantTwitchIds.Contains(c.TwitchChannelId)))
            .Select(c => new { c.ChannelName, c.IsBotActive, c.TwitchChannelId })
            .ToListAsync(cancellationToken);

        var trackedByName = trackedChannels.ToDictionary(c => c.ChannelName, c => c.IsBotActive);
        var trackedNameById = trackedChannels
            .Where(c => c.TwitchChannelId is not null)
            .ToDictionary(c => c.TwitchChannelId!, c => c.ChannelName, StringComparer.Ordinal);

        await ApplyResolvedSevenTvGrantsAsync(isLegacyGrantPayload, grants, trackedNameById, flagsByChannel, cancellationToken);

        // The worker's last live-poll result; only bot-active channels were polled, so for every
        // other row absence from the live set must read as "unknown", not "offline".
        var liveStatus = await liveStatusReader.ReadAsync(cancellationToken);
        var liveLogins = liveStatus is null ? null : liveStatus.LiveChannelLogins.ToHashSet();

        var channels = flagsByChannel
            .Select(kv => new MyChannelDto(
                kv.Key,
                kv.Value.IsBroadcaster,
                kv.Value.IsModerator,
                kv.Value.IsSevenTvEditor,
                IsTracked: trackedByName.ContainsKey(kv.Key),
                IsBotActive: trackedByName.GetValueOrDefault(kv.Key, false),
                LiveState: ChannelLiveStates.Derive(liveLogins, kv.Key, trackedByName.GetValueOrDefault(kv.Key, false))))
            .OrderByDescending(c => c.IsBroadcaster)
            .ThenBy(c => c.ChannelName)
            .ToList();

        return new MyChannelsResultDto(helixUnavailable, reauthRequired, sevenTvUnavailable, channels, liveStatus?.GeneratedAtUtc);
    }

    /// <summary>
    /// Flags the legacy-payload channels directly (by login, no Helix call — issue #34 still applies
    /// to these grants until the cache entry's TTL expires and a fresh lookup repopulates Entries),
    /// and otherwise derives the two lookup sets the tracked-channels query below needs.
    /// <para>
    /// Grant logins join the name criterion purely so a pre-backfill row (TwitchChannelId still null,
    /// so it can't match via the id set) is found by name instead. They must NOT be written into
    /// <paramref name="flagsByChannel"/> here — that would resurrect the issue #34 ghost-channel bug:
    /// a dead grant's stale login would produce an output row on its own, instead of being dropped
    /// once <see cref="ResolveUntrackedGrantsAsync"/> finds Helix has no such id.
    /// </para>
    /// </summary>
    private static (bool IsLegacyGrantPayload, IReadOnlySet<string> GrantTwitchIds, IReadOnlySet<string> GrantLogins) PrepareSevenTvGrantLookup(
        SevenTvEditorGrants? grants, Dictionary<string, ChannelFlags> flagsByChannel)
    {
        // A cache entry written before Entries existed: still authoritative for authorization, but
        // there is nothing to resolve by id yet.
        if (grants is { Entries.Count: 0, ChannelLogins.Count: > 0 })
        {
            foreach (var login in grants.ChannelLogins)
            {
                GetOrAdd(flagsByChannel, login).IsSevenTvEditor = true;
            }

            return (true, new HashSet<string>(), new HashSet<string>());
        }

        if (grants is null)
        {
            return (false, new HashSet<string>(), new HashSet<string>());
        }

        var grantTwitchIds = new HashSet<string>(grants.Entries.Select(entry => entry.TwitchChannelId), StringComparer.Ordinal);
        var grantLogins = new HashSet<string>(grants.Entries.Select(entry => entry.ChannelLogin), StringComparer.Ordinal);
        return (false, grantTwitchIds, grantLogins);
    }

    /// <summary>
    /// Matches grants against the now-known tracked channels: a grant whose Twitch id the db already
    /// tracks is flagged under the db's current name (which may differ from 7TV's stale login).
    /// Whatever cannot be matched this way is handed to <see cref="ResolveUntrackedGrantsAsync"/>, the
    /// only place this method ever asks Helix.
    /// </summary>
    private async Task ApplyResolvedSevenTvGrantsAsync(
        bool isLegacyGrantPayload,
        SevenTvEditorGrants? grants,
        IReadOnlyDictionary<string, string> trackedNameById,
        Dictionary<string, ChannelFlags> flagsByChannel,
        CancellationToken cancellationToken)
    {
        if (isLegacyGrantPayload || grants is null)
        {
            return;
        }

        var unresolved = new List<SevenTvEditorGrantEntry>();
        foreach (var entry in grants.Entries)
        {
            if (trackedNameById.TryGetValue(entry.TwitchChannelId, out var currentName))
            {
                // The db already tracks this channel by id — its current name wins over whatever
                // login 7TV's copy still carries.
                GetOrAdd(flagsByChannel, currentName).IsSevenTvEditor = true;
            }
            else
            {
                unresolved.Add(entry);
            }
        }

        if (unresolved.Count > 0)
        {
            await ResolveUntrackedGrantsAsync(unresolved, flagsByChannel, cancellationToken);
        }
    }

    // Grant entries whose Twitch id the db doesn't know: the only case where Helix gets asked. Never
    // adds a flag under an entry's stale 7TV login on the success path — that would leave an orphan
    // next to the resolved name, which GetOrAdd's merge would then never clean up.
    private async Task ResolveUntrackedGrantsAsync(
        IReadOnlyList<SevenTvEditorGrantEntry> unresolved,
        Dictionary<string, ChannelFlags> flagsByChannel,
        CancellationToken cancellationToken)
    {
        var appToken = await appTokenProvider.GetTokenAsync(cancellationToken);
        if (appToken is null)
        {
            logger.LogInformation(
                "Kein App-Token verfügbar, um {Count} 7TV-Editor-Grant(s) ohne DB-Treffer nach Twitch-ID aufzulösen — falle auf die 7TV-Logins zurück.",
                unresolved.Count);
            FallBackToSevenTvLogins(unresolved, flagsByChannel);
            return;
        }

        var identities = await helixClient.GetUsersAsync(
            ids: [.. unresolved.Select(entry => entry.TwitchChannelId)],
            logins: [],
            appToken,
            cancellationToken);
        if (identities is null)
        {
            logger.LogInformation(
                "Helix hat {Count} 7TV-Editor-Grant(s) ohne DB-Treffer nicht auflösen können — falle auf die 7TV-Logins zurück.",
                unresolved.Count);
            FallBackToSevenTvLogins(unresolved, flagsByChannel);
            return;
        }

        var identityById = identities.ToDictionary(identity => identity.Id, StringComparer.Ordinal);
        foreach (var entry in unresolved)
        {
            if (identityById.TryGetValue(entry.TwitchChannelId, out var identity))
            {
                // Helix's own current login wins, not the one the grant carried — an untracked,
                // renamed channel surfaces under its new name.
                GetOrAdd(flagsByChannel, ChannelName.Normalize(identity.Login)).IsSevenTvEditor = true;
            }
            else
            {
                // The id resolved to nothing in an otherwise successful Helix response: this Twitch
                // account no longer exists under this id. This is the dead-grant case from issue
                // #34 — dropped rather than shown under its stale 7TV login.
                logger.LogInformation(
                    "7TV-Editor-Grant {Login} (Twitch-ID {TwitchId}) existiert auf Twitch nicht mehr — nicht in der Übersicht angezeigt.",
                    entry.ChannelLogin, entry.TwitchChannelId);
            }
        }
    }

    private static ChannelFlags GetOrAdd(Dictionary<string, ChannelFlags> flagsByChannel, string channelName)
    {
        if (!flagsByChannel.TryGetValue(channelName, out var flags))
        {
            flags = new ChannelFlags();
            flagsByChannel[channelName] = flags;
        }

        return flags;
    }

    private static void FallBackToSevenTvLogins(IReadOnlyList<SevenTvEditorGrantEntry> entries, Dictionary<string, ChannelFlags> flagsByChannel)
    {
        foreach (var entry in entries)
        {
            GetOrAdd(flagsByChannel, entry.ChannelLogin).IsSevenTvEditor = true;
        }
    }
}
