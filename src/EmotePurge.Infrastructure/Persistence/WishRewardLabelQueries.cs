using EmotePurge.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The label of the learned reward a captured wish was redeemed with (K6, plan #486 2.6): one query per response over
/// the reward ids that occur in it, never one per wish. The label is redeemer-adjacent data and is only read for a
/// caller who may see it (2.4a). A forgotten reward has no row, so its wishes simply have no label. Reads only.
/// </summary>
public static class WishRewardLabelQueries
{
    public static async Task<IReadOnlyDictionary<long, string>> ReadRewardLabelsAsync(
        this AppDbContext db, IReadOnlyCollection<Wish> wishes, CancellationToken cancellationToken)
    {
        var ids = wishes.Where(w => w.RewardId is not null).Select(w => w.RewardId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        return await db.ChannelWishRewards.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Label, cancellationToken);
    }
}
