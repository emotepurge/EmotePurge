using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>One name an active emote of a channel is listed under, with the emote that owns it.</summary>
public sealed class ActiveNameEntry
{
    public string EmoteId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The slot-aware view of "which names are taken in the active set", shared by the occupied-slot count,
/// the duplicate-name report and the collision tracker so that all three agree (emote-slots plan 372,
/// section 2.5, E14). Only meaningful with the slot reconcile flag on; with it off the readers keep
/// their emote-wide queries.
/// </summary>
public static class ActiveNameEntries
{
    /// <summary>
    /// One entry per active own slot (not archived, not an unsettled seed) of an active emote, plus
    /// — E14 — exactly one virtual entry under <c>Emote.Name</c> for an active emote that has no
    /// active own slot (before the first reconcile after switching on, after a delta push, with a
    /// deferred settle). Slots under an archived emote never count. Not distinct: two slots of one
    /// emote under one name are two entries, which is what the occupied-slot count wants.
    /// </summary>
    public static IQueryable<ActiveNameEntry> ForChannel(this AppDbContext db, string channelId)
    {
        var slotted = db.EmoteSlots
            .Where(s => !s.IsArchived && !s.SeedUnsettled
                        && db.Emotes.Any(e => e.Id == s.EmoteId && e.ChannelId == channelId && !e.IsArchived))
            .Select(s => new ActiveNameEntry { EmoteId = s.EmoteId, Name = s.Name });

        var virtualEntries = db.Emotes
            .Where(e => e.ChannelId == channelId && !e.IsArchived
                        && !db.EmoteSlots.Any(s => s.EmoteId == e.Id && !s.IsArchived && !s.SeedUnsettled))
            .Select(e => new ActiveNameEntry { EmoteId = e.Id, Name = e.Name });

        return slotted.Concat(virtualEntries);
    }
}
