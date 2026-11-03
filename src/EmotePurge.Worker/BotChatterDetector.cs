using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;

namespace EmotePurge.Worker;

/// <summary>
/// Pure, TwitchLib-free detector (same shape as <see cref="TwitchWatchdogPolicy"/> and
/// <see cref="TwitchReconnectBackoffPolicy"/>): the mapping from <c>ChatMessage</c> to chatter
/// id/badges happens exclusively in <c>TwitchChatManager</c>, never here.
/// <para>
/// Check order: <see cref="BotBadgeSetId"/> badge, then the account id against the union of the
/// statically known bot accounts and <c>Twitch:AdditionalBotAccountIds</c> from configuration. All
/// three end in "is a bot", so the order is readability only, not a semantic difference.
/// </para>
/// <para>
/// The six static ids below were verified against the twitchbots.info API on 2026-09-01 (see the
/// design spec's "Messung twitchbots.info" section). Nothing unverified goes on this list — the
/// config key exists precisely for the channel-owned bots that list can never know about.
/// </para>
/// </summary>
public sealed class BotChatterDetector : IBotChatterDetector
{
    /// <summary>Badge-set id Twitch attaches to every message a verified bot account sends.</summary>
    private const string BotBadgeSetId = "bot-badge";

    // nightbot, streamelements, fossabot, moobot, streamlabs, sery_bot — verified 2026-09-01.
    private static readonly string[] StaticBotAccountIds =
    [
        "19264788",
        "100135110",
        "237719657",
        "1564983",
        "105166207",
        "402337290"
    ];

    private readonly FrozenSet<string> _botAccountIds;

    public BotChatterDetector(IConfiguration configuration)
    {
        var ids = new HashSet<string>(StaticBotAccountIds, StringComparer.Ordinal);
        foreach (var additionalId in ReadAdditionalBotAccountIds(configuration))
        {
            // A config entry only ever adds to the static set — it never replaces it, and a
            // duplicate of a static id is harmless because this is a set.
            ids.Add(additionalId);
        }

        // Frozen rather than the mutable builder above: faster lookups on the hot path.
        _botAccountIds = ids.ToFrozenSet(StringComparer.Ordinal);
    }

    public bool IsBot(string? chatterId, IReadOnlyList<KeyValuePair<string, string>>? badges)
    {
        if (badges is not null)
        {
            for (var i = 0; i < badges.Count; i++)
            {
                if (string.Equals(badges[i].Key, BotBadgeSetId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return !string.IsNullOrEmpty(chatterId) && _botAccountIds.Contains(chatterId);
    }

    // Accepts both configuration shapes on purpose, scalar wins — same rule as
    // ChannelAccessService.GetAdminLogins: a JSON array in appsettings.json lands on the indexed
    // keys Twitch:AdditionalBotAccountIds:0.., while an env var/user-secret can only ever set the
    // plain scalar key. Twitch ids are opaque digit strings, trimmed but never otherwise
    // normalized.
    private static IEnumerable<string> ReadAdditionalBotAccountIds(IConfiguration configuration)
    {
        var scalar = configuration["Twitch:AdditionalBotAccountIds"];
        var rawIds = string.IsNullOrWhiteSpace(scalar)
            ? configuration.GetSection("Twitch:AdditionalBotAccountIds").Get<string[]>() ?? []
            : scalar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawId in rawIds)
        {
            var trimmed = rawId.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}
