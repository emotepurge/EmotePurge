using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Worker;

/// <summary>
/// Pure, TwitchLib-free gate (same shape as <see cref="BotChatterDetector"/>) for chatters who
/// objected to being processed (GDPR Art. 21, issue #252): a message from a configured id is
/// dropped in <c>TwitchChatManager.OnMessageReceived</c> before any counting or classification, so
/// it reaches neither the emote counters nor <see cref="IBotChatterDetector"/> nor anything else.
/// <para>
/// Same configuration shape as <c>Twitch:AdditionalBotAccountIds</c>: array keys and a single
/// comma-separated scalar both work, scalar wins (see <see cref="BotChatterDetector"/>'s own
/// comment on that rule). Matches only the immutable Twitch user id, never the login — this class
/// never reads a login, and nothing here or in its caller logs one for an excluded id.
/// </para>
/// <para>
/// An empty or missing list changes nothing: <see cref="IsExcluded"/> then always answers
/// <c>false</c>, exactly as if this class were not in the message path at all.
/// </para>
/// </summary>
public sealed class ExcludedChatterFilter : IExcludedChatterFilter
{
    private readonly FrozenSet<string> _excludedChatterIds;

    public ExcludedChatterFilter(IConfiguration configuration, ILogger<ExcludedChatterFilter> logger)
    {
        _excludedChatterIds = ReadExcludedChatterIds(configuration).ToFrozenSet(StringComparer.Ordinal);

        // Count only, on purpose — never the ids themselves and never a login, so this line can
        // never leak whom an objection concerns even if someone later widens the log level.
        logger.LogInformation("Configured {Count} excluded chatter id(s).", _excludedChatterIds.Count);
    }

    public bool IsExcluded(string? chatterId) =>
        !string.IsNullOrEmpty(chatterId) && _excludedChatterIds.Contains(chatterId);

    // Same two accepted shapes and the same scalar-wins rule as
    // BotChatterDetector.ReadAdditionalBotAccountIds — see that method's comment for why.
    private static IEnumerable<string> ReadExcludedChatterIds(IConfiguration configuration)
    {
        var scalar = configuration["Twitch:ExcludedChatterIds"];
        var rawIds = string.IsNullOrWhiteSpace(scalar)
            ? configuration.GetSection("Twitch:ExcludedChatterIds").Get<string[]>() ?? []
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
