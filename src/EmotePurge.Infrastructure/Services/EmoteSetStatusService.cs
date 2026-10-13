using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteSetStatusService(
    AppDbContext db,
    IUsageStatQueryService usageStatQueryService) : IEmoteSetStatusService
{
    public async Task<EmoteSetStatusDto?> GetAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var channel = await db.LoadChannelReadOnlyAsync(channelName, cancellationToken);
        if (channel is null)
        {
            return null;
        }

        // Skipped entirely while no set is known: that is exactly the window the usage-stats page
        // polls this endpoint in a loop waiting for the first sync, and counting rows that cannot
        // exist yet would put a query behind every one of those polls for guaranteed nulls —
        // occupiedSlots, botsExcludedSince and sharedChatSeparatedSince share this one gate, not
        // three copies of it.
        int occupiedSlots;
        DateOnly? botsExcludedSince;
        DateOnly? sharedChatSeparatedSince;
        IReadOnlyList<DuplicateEmoteNameDto> duplicateNames;
        if (channel.ActiveEmoteSetId.Length == 0)
        {
            occupiedSlots = 0;
            botsExcludedSince = null;
            sharedChatSeparatedSince = null;
            duplicateNames = [];
        }
        else
        {
            occupiedSlots = await db.ForChannel(channel.Id).CountAsync(cancellationToken);
            botsExcludedSince = await usageStatQueryService.GetEarliestBotUsageDateAsync(channel.Id, cancellationToken);
            sharedChatSeparatedSince = await usageStatQueryService.GetEarliestSharedChatUsageDateAsync(channel.Id, cancellationToken);
            duplicateNames = await FindDuplicateNamesBySlotAsync(channel.Id, cancellationToken);
        }

        return new EmoteSetStatusDto(
            channel.ActiveEmoteSetId,
            channel.ActiveEmoteSetCapacity,
            occupiedSlots,
            TrackingCoverage.TrackedSince(channel.TrackingResumedAt, channel.CreatedAt),
            channel.LastSyncFailureReason,
            channel.LastSyncAttemptAtUtc,
            botsExcludedSince,
            sharedChatSeparatedSince,
            duplicateNames);
    }

    // D15/D27, E14: a name collides when it is listed by more than one distinct emote, counting each
    // emote's active own slots and — for an emote without one — its virtual slot under Emote.Name. Two
    // slots of one emote are never a duplicate, and the reported rows are the emotes involved. The
    // entries are a union, so the count is over distinct emote ids rather than rows.
    //
    // Two queries rather than one: the collision scan itself runs entirely in SQL (text equality is a
    // byte comparison under the column's default, uncustomized collation, so this is already ordinal
    // case-sensitive), and only the names that actually collide are then used to fetch the few entries
    // involved — the cost stays proportional to the collisions, not to the whole active set. The two
    // queries are not one snapshot, though: an emote archived in between can shrink a name to one
    // emote, so the result is re-filtered after the in-memory grouping rather than trusted as-is.
    private async Task<IReadOnlyList<DuplicateEmoteNameDto>> FindDuplicateNamesBySlotAsync(string channelId, CancellationToken cancellationToken)
    {
        var collidingNames = await db.ForChannel(channelId)
            .GroupBy(x => x.Name)
            .Where(g => g.Select(x => x.EmoteId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        if (collidingNames.Count == 0)
        {
            return [];
        }

        var entries = await db.ForChannel(channelId)
            .Where(x => collidingNames.Contains(x.Name))
            .ToListAsync(cancellationToken);

        var emoteIds = entries.Select(x => x.EmoteId).Distinct().ToList();
        var emotes = (await db.Emotes
                .Where(e => emoteIds.Contains(e.Id))
                .Select(e => new { e.Id, e.SevenTvEmoteId, e.ImageUrl })
                .ToListAsync(cancellationToken))
            .ToDictionary(e => e.Id, StringComparer.Ordinal);

        return entries
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, EmoteIds: g.Select(x => x.EmoteId).Distinct(StringComparer.Ordinal).Where(emotes.ContainsKey).ToList()))
            .Where(g => g.EmoteIds.Count > 1)
            .OrderBy(g => g.Name, StringComparer.Ordinal)
            .Select(g => new DuplicateEmoteNameDto(
                g.Name,
                g.EmoteIds.Select(id => emotes[id])
                    .OrderBy(e => e.SevenTvEmoteId, StringComparer.Ordinal)
                    .Select(e => new DuplicateEmoteDto(e.Id, e.SevenTvEmoteId, e.ImageUrl))
                    .ToList()))
            .ToList();
    }
}
