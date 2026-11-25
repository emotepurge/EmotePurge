namespace EmotePurge.Core.Services;

/// <summary>
/// The pilot list of <c>Wishes:PilotChannelIds</c> (spec E8), evaluated purely: either every channel (<c>*</c>) or a
/// set of numeric Twitch channel ids. Empty means no channel (global admins are decided elsewhere). Entries are
/// comma-separated and trimmed, empty entries are ignored, <c>*</c> wins over any ids beside it, and any other
/// non-numeric entry is a configuration error, so a typo never silently opens or closes a channel.
/// </summary>
public sealed class WishPilotList
{
    /// <summary>The configuration key named in error messages.</summary>
    public const string KeyName = "Wishes:PilotChannelIds";

    private const int MaxIdLength = 20;

    private readonly HashSet<string> _ids;

    private WishPilotList(bool all, HashSet<string> ids)
    {
        All = all;
        _ids = ids;
    }

    /// <summary>True when the list is <c>*</c>: every channel is covered.</summary>
    public bool All { get; }

    /// <summary>True when no channel is covered.</summary>
    public bool IsEmpty => !All && _ids.Count == 0;

    /// <summary>Parses the configured string; throws <see cref="InvalidOperationException"/> naming the key on a bad entry.</summary>
    public static WishPilotList Parse(string? value)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var all = false;

        foreach (var raw in (value ?? string.Empty).Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry == "*")
            {
                all = true;
            }
            else if (IsNumericId(entry))
            {
                ids.Add(entry);
            }
            else
            {
                throw new InvalidOperationException(
                    $"{KeyName} must be a comma-separated list of numeric Twitch channel ids or '*', but contains an invalid entry (length {entry.Length}).");
            }
        }

        return new WishPilotList(all, all ? [] : ids);
    }

    /// <summary>Whether the channel with this Twitch id is covered. A missing id is never covered.</summary>
    public bool Covers(string? twitchChannelId) =>
        All || (!string.IsNullOrEmpty(twitchChannelId) && _ids.Contains(twitchChannelId));

    private static bool IsNumericId(string entry) =>
        entry.Length <= MaxIdLength && entry.All(c => c is >= '0' and <= '9');
}
