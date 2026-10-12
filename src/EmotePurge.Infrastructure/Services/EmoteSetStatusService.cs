using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class EmoteSetStatusService(
    AppDbContext db,
    IUsageStatQueryService usageStatQueryService,
    SlotReconcileOptions? slotReconcile = null) : IEmoteSetStatusService
{
    // Null (the unit-test constructor) is off, like the flag's default.
    private readonly bool slotsEnabled = slotReconcile?.Enabled ?? false;

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
            occupiedSlots = slotsEnabled
                ? await db.ForChannel(channel.Id).CountAsync(cancellationToken)
                : await db.Emotes.CountAsync(e => e.ChannelId == channel.Id && !e.IsArchived, cancellationToken);
            botsExcludedSince = await usageStatQueryService.GetEarliestBotUsageDateAsync(channel.Id, cancellationToken);
            sharedChatSeparatedSince = await usageStatQueryService.GetEarliestSharedChatUsageDateAsync(channel.Id, cancellationToken);
            duplicateNames = slotsEnabled
                ? await FindDuplicateNamesBySlotAsync(channel.Id, cancellationToken)
                : await FindDuplicateNamesAsync(channel.Id, cancellationToken);
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

    // Slot-aware twin of FindDuplicateNamesAsync (flag on, D15/D27, E14): a name collides when it is
    // listed by more than one distinct emote, counting each emote's active own slots and — for an emote
    // without one — its virtual slot under Emote.Name. Two slots of one emote are never a duplicate,
    // and the reported rows are the emotes involved, so the DTO keeps its shape. Same two-step shape
    // and the same re-check after the second read as above; the entries are a union, so the count is
    // over distinct emote ids rather than rows.
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

    // Two queries rather than one: the collision scan itself runs entirely in SQL (GROUP BY ...
    // HAVING COUNT(*) > 1 — text equality is a byte comparison under this column's default,
    // uncustomized collation, so this is already ordinal case-sensitive), and only the names that
    // actually collide are then used to fetch the few rows involved. Keeping the cost proportional
    // to the collisions, not to the whole active set, is the point of the two-step shape.
    //
    // The two queries are not one snapshot, though: nothing pins them to the same point in time, so
    // an emote archived in between can shrink a name from two rows down to one between the first
    // query and the second. That is why the result below is re-filtered by count after the in-memory
    // grouping rather than trusted as-is — see that filter's own comment.
    private async Task<IReadOnlyList<DuplicateEmoteNameDto>> FindDuplicateNamesAsync(string channelId, CancellationToken cancellationToken)
    {
        var collidingNames = await db.Emotes
            .Where(e => e.ChannelId == channelId && !e.IsArchived)
            .GroupBy(e => e.Name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        if (collidingNames.Count == 0)
        {
            return [];
        }

        var collidingRows = await db.Emotes
            .Where(e => e.ChannelId == channelId && !e.IsArchived && collidingNames.Contains(e.Name))
            .Select(e => new { e.Id, e.SevenTvEmoteId, e.Name, e.ImageUrl })
            .ToListAsync(cancellationToken);

        // Grouped and ordered in memory, ordinal case-sensitive to mirror chat matching — see
        // EmoteListQueryService.ListActiveAsync for why EF Core cannot do this in SQL at all. Only
        // the handful of colliding rows pay for this, never the whole active set.
        return collidingRows
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            // Re-checked rather than trusted from the first query: an emote archived between the two
            // queries above can leave a name with only one surviving row here, and a group that no
            // longer collides must not be reported as if it still did.
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DuplicateEmoteNameDto(
                g.Key,
                g.OrderBy(e => e.SevenTvEmoteId, StringComparer.Ordinal)
                    .Select(e => new DuplicateEmoteDto(e.Id, e.SevenTvEmoteId, e.ImageUrl))
                    .ToList()))
            .ToList();
    }
}
