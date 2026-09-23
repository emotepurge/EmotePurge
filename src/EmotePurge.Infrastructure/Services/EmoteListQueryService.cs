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

        var activeEmotes = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
            .Select(e => new { e.SevenTvEmoteId, e.Name, e.ImageUrl })
            .ToListAsync(cancellationToken);

        // Sorted in memory: Postgres orders by the column's collation, not ordinally, and EF Core
        // cannot translate an OrderBy(StringComparer.Ordinal) into SQL at all. Chat matching is
        // ordinal case-sensitive, and the import dialog compares names against that same semantic,
        // so the sort here has to match it.
        return activeEmotes
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new EmoteListItemDto(e.SevenTvEmoteId, e.Name, e.ImageUrl))
            .ToList();
    }
}
