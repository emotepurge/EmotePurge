using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The slot half of the snapshot <see cref="UsageViewRows.Build"/> reads for one channel and one requested set
/// X (emote-slots plan #376, 2.1–2.3): every slot of the channel's emotes — own slots and unsettled seeds
/// alike, archived ones included — the ids of those with at least one <c>UsageStats</c> row in X, and the
/// seeds of X. One round trip: the seeds are derived from the same answer rather than asked for again.
/// </summary>
internal sealed record UsageViewSlotSnapshot(
    IReadOnlyList<UsageViewSlot> Slots,
    IReadOnlySet<string> SlotIdsWithRowsInSet,
    IReadOnlySet<string> SeedIdsOfSet)
{
    /// <summary>
    /// Loads the snapshot of <paramref name="channelId"/> for <paramref name="emoteSetId"/>; see the type
    /// summary.
    /// </summary>
    public static async Task<UsageViewSlotSnapshot> LoadAsync(
        AppDbContext db, string channelId, string emoteSetId, CancellationToken cancellationToken)
    {
        // "Has a row under the set" is the per-slot form of the pre-slot base-set test (spec E16): no date
        // bound and whatever the row's UseCount. Written as a correlated scalar subquery with LIMIT 1 rather
        // than as Any(): EF translates Any() to an EXISTS in the select list, which Postgres may plan as a
        // hashed SubPlan — one sequential scan of every channel's rows of the set, hashed by slot id, whose
        // cost grows with the whole table (measured on a copy of prod: 11 ms, 119k rows removed by filter, for
        // an active set of 1,510 rows). A scalar subquery with LIMIT cannot be hashed, so every slot is one
        // probe of the unique slot index (SlotId, EmoteSetId, Date) — an index-only scan that stops at the
        // first entry, under a custom and a generic plan alike. A plain filter with a navigation and no
        // GroupBy, so rule 10 is not touched.
        var slots = await db.EmoteSlots
            .Where(s => s.Emote.ChannelId == channelId)
            .Select(s => new
            {
                s.Id,
                s.EmoteId,
                s.Name,
                s.AddedAt,
                s.AddedAtFromRest,
                s.IsArchived,
                s.SeedUnsettled,
                HasRowsInSet = db.UsageStats
                    .Where(u => u.SlotId == s.Id && u.EmoteSetId == emoteSetId)
                    .Select(u => true)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var viewSlots = new List<UsageViewSlot>(slots.Count);
        var slotIdsWithRowsInSet = new HashSet<string>(StringComparer.Ordinal);
        var seedIdsOfSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in slots)
        {
            viewSlots.Add(new UsageViewSlot(s.Id, s.EmoteId, s.Name, s.AddedAt, s.AddedAtFromRest, s.IsArchived, s.SeedUnsettled));
            if (!s.HasRowsInSet)
            {
                continue;
            }

            slotIdsWithRowsInSet.Add(s.Id);

            // SetSeeds.SeedsOfSet, the one definition of "seed of X" shared with the settle and the match-map
            // exclusion (plan 2.1), read off this answer: an unsettled seed of the channel with a row in X.
            // Same channel filter, same row test, no archive filter — so the grid can never call a seed
            // settled that the sync still treats as open, or the reverse. SetSeedsTests pins the two equal.
            if (s.SeedUnsettled)
            {
                seedIdsOfSet.Add(s.Id);
            }
        }

        return new UsageViewSlotSnapshot(viewSlots, slotIdsWithRowsInSet, seedIdsOfSet);
    }
}
