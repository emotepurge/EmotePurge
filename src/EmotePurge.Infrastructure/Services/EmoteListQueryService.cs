using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteListQueryService(AppDbContext db) : IEmoteListQueryService
{
    public async Task<IReadOnlyList<EmoteListItemDto>?> ListActiveAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        // One row per entry of the active set (#444), not one per emote: an emote under two aliases
        // is two entries, and the import preview has to see both to tell "already present" from a
        // further alias to add. The same entry view the occupied-slot count and the duplicate-name
        // report read (ActiveNameEntries): active own slots of active emotes, unsettled seeds left
        // out, plus one entry under Emote.Name for an active emote that has no active slot yet.
        // Backfill-only slots ("was never in") are archived and therefore never listed.
        var activeEntries = await db.ForChannel(channel.Id)
            .Join(
                db.Emotes,
                entry => entry.EmoteId,
                emote => emote.Id,
                (entry, emote) => new { emote.SevenTvEmoteId, entry.Name, emote.ImageUrl })
            .ToListAsync(cancellationToken);

        // Sorted in memory: Postgres orders by the column's collation, not ordinally, and EF Core
        // cannot translate an OrderBy(StringComparer.Ordinal) into SQL at all. Chat matching is
        // ordinal case-sensitive, and the import dialog compares names against that same semantic,
        // so the sort here has to match it. The id breaks a tie, so the order is deterministic.
        return activeEntries
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.SevenTvEmoteId, StringComparer.Ordinal)
            .Select(e => new EmoteListItemDto(e.SevenTvEmoteId, e.Name, e.ImageUrl))
            .ToList();
    }
}
