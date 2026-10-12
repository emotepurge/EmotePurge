using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The "seeds of a set" query, shared by the sync (settle population and flag-on match-map exclusion)
/// and the slot-aware readers, so that none of them can drift from the others (emote-slots plan #376,
/// section 2.1).
/// </summary>
public static class SetSeeds
{
    /// <summary>
    /// The channel's unsettled seeds that carry at least one usage row of <paramref name="emoteSetId"/>
    /// (no date bound). Derived from <c>UsageStats</c>, never from <c>IsArchived</c>: the archive state of
    /// a seed is copied 1:1 from its emote by M1 and says nothing about its set. Not tracked-aware, not
    /// ordered; callers compose further.
    /// </summary>
    public static IQueryable<EmoteSlot> SeedsOfSet(this AppDbContext db, string channelId, string emoteSetId) =>
        db.EmoteSlots.Where(s => s.SeedUnsettled
                                 && s.Emote.ChannelId == channelId
                                 && db.UsageStats.Any(u => u.SlotId == s.Id && u.EmoteSetId == emoteSetId));
}
