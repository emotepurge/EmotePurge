using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Reads <c>Channels:ExcludedChannelIds</c> (env <c>EXCLUDED_CHANNEL_IDS</c>) once at startup — same
/// accepted shapes and the same scalar-wins rule as <c>Twitch:AdditionalBotAccountIds</c>
/// (<c>BotChatterDetector</c>) and <c>Twitch:ExcludedChatterIds</c> (<c>ExcludedChatterFilter</c> in
/// <c>EmotePurge.Worker</c>): indexed array keys or one comma-separated scalar, scalar wins.
/// <para>
/// Global admins are deliberately <b>not</b> exempt — unlike <c>Channels:MaxActiveChannels</c>, which
/// an admin can override because the cap protects Twitch's own connection limit, not a person's
/// right under Art. 21. The only way to undo a block is for the operator to remove the id from the
/// list.
/// </para>
/// <para>
/// An empty or missing list changes nothing: <see cref="IsExcluded"/> then always answers
/// <c>false</c>, exactly as if this class were not in the join path at all.
/// </para>
/// </summary>
public sealed class ExcludedChannelFilter : IExcludedChannelFilter
{
    private readonly FrozenSet<string> _excludedChannelIds;

    public ExcludedChannelFilter(IConfiguration configuration, ILogger<ExcludedChannelFilter> logger)
    {
        _excludedChannelIds = ReadExcludedChannelIds(configuration).ToFrozenSet(StringComparer.Ordinal);

        // Count only, on purpose — never the ids themselves, so this line can never name which
        // channel an objection concerns even if someone later widens the log level.
        logger.LogInformation("Configured {Count} excluded channel id(s).", _excludedChannelIds.Count);
    }

    public bool IsExcluded(string? twitchChannelId) =>
        !string.IsNullOrEmpty(twitchChannelId) && _excludedChannelIds.Contains(twitchChannelId);

    // Same two accepted shapes and the same scalar-wins rule as
    // BotChatterDetector.ReadAdditionalBotAccountIds / ExcludedChatterFilter.ReadExcludedChatterIds —
    // see either method's comment for why.
    private static IEnumerable<string> ReadExcludedChannelIds(IConfiguration configuration)
    {
        var scalar = configuration["Channels:ExcludedChannelIds"];
        var rawIds = string.IsNullOrWhiteSpace(scalar)
            ? configuration.GetSection("Channels:ExcludedChannelIds").Get<string[]>() ?? []
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
