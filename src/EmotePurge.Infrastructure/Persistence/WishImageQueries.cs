using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The image of a wish (plan #485 2.1a): the stored <c>Emotes.ImageUrl</c> of the channel's row for the same 7TV
/// emote. A row the channel never had in its set (<c>IsPlaceholder</c>) or one without an image has none, so the
/// client never shows a guessed (possibly animated) picture. One query per response over the emote ids that
/// occur in it, never one per wish. Reads only; nothing is tracked.
/// </summary>
public static class WishImageQueries
{
    /// <summary>
    /// The images keyed by (channel id, 7TV emote id). <paramref name="channelIds"/> × <paramref name="sevenTvEmoteIds"/>
    /// is the query's superset; a caller looks up exactly the pairs it has.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(string ChannelId, string SevenTvEmoteId), string>> ReadWishImageUrlsAsync(
        this AppDbContext db,
        IReadOnlyCollection<string> channelIds,
        IReadOnlyCollection<string> sevenTvEmoteIds,
        CancellationToken cancellationToken)
    {
        // The empty K6 placeholder of an open wish without an emote has no image and is never looked up.
        var emotes = sevenTvEmoteIds.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (channelIds.Count == 0 || emotes.Count == 0)
        {
            return new Dictionary<(string, string), string>();
        }

        var channels = channelIds.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.Emotes.AsNoTracking()
            .Where(e => channels.Contains(e.ChannelId) && emotes.Contains(e.SevenTvEmoteId) && !e.IsPlaceholder && e.ImageUrl != "")
            .Select(e => new { e.ChannelId, e.SevenTvEmoteId, e.ImageUrl })
            .ToListAsync(cancellationToken);

        // (ChannelId, SevenTvEmoteId) is unique on Emotes, so every pair has at most one row.
        return rows.ToDictionary(r => (r.ChannelId, r.SevenTvEmoteId), r => r.ImageUrl);
    }

    /// <summary>The same rule for a row already in hand (the slot list reads its emote with the slot).</summary>
    public static string? WishImageUrlOf(string imageUrl, bool isPlaceholder) =>
        isPlaceholder || string.IsNullOrEmpty(imageUrl) ? null : imageUrl;
}
