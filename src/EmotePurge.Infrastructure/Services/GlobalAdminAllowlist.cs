using System.Collections.Frozen;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Services;

public sealed class GlobalAdminAllowlist : IGlobalAdminAllowlist
{
    private readonly FrozenSet<string> _adminTwitchUserIds;
    private readonly FrozenSet<string> _adminTwitchLogins;

    public GlobalAdminAllowlist(IConfiguration configuration, ILogger<GlobalAdminAllowlist> logger)
    {
        _adminTwitchUserIds = ReadList(configuration, "Auth:AdminTwitchUserIds").ToFrozenSet(StringComparer.Ordinal);
        _adminTwitchLogins = ReadList(configuration, "Auth:AdminTwitchLogins").ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // Counts only, on purpose: never the ids or logins themselves, so these lines can never
        // name an operator account even if someone later widens the log level.
        if (_adminTwitchUserIds.Count > 0)
        {
            if (_adminTwitchLogins.Count > 0)
            {
                logger.LogWarning(
                    "Auth:AdminTwitchLogins is ignored because Auth:AdminTwitchUserIds is configured ({IdCount} id(s), {LoginCount} login(s)).",
                    _adminTwitchUserIds.Count, _adminTwitchLogins.Count);
            }
            else
            {
                logger.LogInformation("Configured {IdCount} global admin id(s).", _adminTwitchUserIds.Count);
            }
        }
        else if (_adminTwitchLogins.Count > 0)
        {
            logger.LogWarning(
                "The global admin allowlist is login-based ({LoginCount} login(s)); migrate to Auth:AdminTwitchUserIds.",
                _adminTwitchLogins.Count);
        }
        else
        {
            logger.LogInformation("No global admins are configured.");
        }
    }

    public bool IsAdmin(TwitchPrincipalInfo principal)
    {
        // Transition rule: a non-empty id list decides alone. A Twitch login can be released and
        // re-registered by someone else after a rename, and lifting a broadcaster's lock is the first
        // right that overrides a streamer's own decision — it must hang on the immutable id.
        if (_adminTwitchUserIds.Count > 0)
        {
            return _adminTwitchUserIds.Contains(principal.TwitchUserId);
        }

        return _adminTwitchLogins.Contains(principal.TwitchLogin);
    }

    // An empty id list contains nothing, so the login fallback never reaches this answer.
    public bool IsAdminById(TwitchPrincipalInfo principal) => _adminTwitchUserIds.Contains(principal.TwitchUserId);

    // Accepts both shapes on purpose, and the scalar wins: a JSON array in appsettings.json lands on
    // the indexed keys (Key:0..), while an environment variable or user secret can only set the plain
    // key. Same rule as ExcludedChannelFilter.ReadExcludedChannelIds. A blank scalar (compose expands
    // an unset variable to "") falls back to the array.
    private static IEnumerable<string> ReadList(IConfiguration configuration, string key)
    {
        var scalar = configuration[key];
        var raw = string.IsNullOrWhiteSpace(scalar)
            ? configuration.GetSection(key).Get<string[]>() ?? []
            : scalar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in raw)
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}
