using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>The state of the slot a test row is seeded onto (emote slots spec section 4.1).</summary>
public enum UsageSlotKind
{
    /// <summary>What the M1 migration creates on production: name and archive state as the emote's, no date, not yet settled.</summary>
    UnsettledSeed,

    /// <summary>A slot a sync has settled: authoritative 7TV date, not an unsettled seed.</summary>
    Settled,

    /// <summary>A settled slot whose date is provisional (REST has not reported one yet).</summary>
    Provisional
}

/// <summary>
/// Explicit, opt-in way for a test to give the usage rows it adds a real slot (plan #373, E14). A
/// test calls <see cref="AssignUsageSlotsAsync"/> after adding its <c>UsageStat</c> rows and before
/// <c>SaveChangesAsync</c>; nothing in any <c>DbContext</c> does it implicitly, so a writer that
/// forgets the slot stays visible.
/// <para>
/// Contract: every added row without a <c>SlotId</c> gets one. Rows of one <c>(EmoteId,
/// EmoteSetId)</c> pair share one slot — an existing slot already carrying rows of the pair, else
/// one created here (as <paramref name="kind"/>), so the pair never splits across slots. A row that
/// already names its slot is left alone. The new slots are only added to the context and are saved
/// together with the rows.
/// </para>
/// </summary>
public static class UsageSlots
{
    public static async Task AssignUsageSlotsAsync(this AppDbContext db, UsageSlotKind kind = UsageSlotKind.UnsettledSeed)
    {
        var pending = db.ChangeTracker.Entries<UsageStat>()
            .Where(e => e.State == EntityState.Added && e.Entity.SlotId is null)
            .Select(e => e.Entity)
            .ToList();
        await AssignAsync(db, pending, kind);
    }

    /// <summary>The same for rows that are not tracked yet (a test that builds them before adding).</summary>
    public static Task AssignUsageSlotsAsync(this AppDbContext db, IEnumerable<UsageStat> rows, UsageSlotKind kind = UsageSlotKind.UnsettledSeed) =>
        AssignAsync(db, [.. rows.Where(r => r.SlotId is null)], kind);

    /// <summary>
    /// Creates and saves one slot of <paramref name="emoteId"/> on <paramref name="db"/> and returns its id, for a
    /// test that must not touch the emote or slot tables through the context under test (lock-ordering probes).
    /// </summary>
    public static async Task<string> SeedUsageSlotAsync(this AppDbContext db, string emoteId, UsageSlotKind kind = UsageSlotKind.UnsettledSeed)
    {
        var emote = await db.Emotes.AsNoTracking().SingleAsync(e => e.Id == emoteId);
        var slot = NewSlot(emote, kind);
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private static async Task AssignAsync(AppDbContext db, List<UsageStat> rows, UsageSlotKind kind)
    {
        foreach (var pair in rows.GroupBy(r => (r.EmoteId, r.EmoteSetId)))
        {
            var (emoteId, setId) = pair.Key;
            var tracked = db.ChangeTracker.Entries<UsageStat>()
                .Select(e => e.Entity)
                .FirstOrDefault(u => u.EmoteId == emoteId && u.EmoteSetId == setId && u.SlotId is not null)?.SlotId;
            var slotId = tracked ?? await db.UsageStats.AsNoTracking()
                .Where(u => u.EmoteId == emoteId && u.EmoteSetId == setId && u.SlotId != null)
                .Select(u => u.SlotId)
                .FirstOrDefaultAsync();
            if (slotId is null)
            {
                var emote = db.Emotes.Local.FirstOrDefault(e => e.Id == emoteId) ?? await db.Emotes.AsNoTracking().SingleAsync(e => e.Id == emoteId);
                var slot = NewSlot(emote, kind);
                db.EmoteSlots.Add(slot);
                slotId = slot.Id;
            }

            foreach (var row in pair)
            {
                row.SlotId = slotId;
            }
        }
    }

    private static EmoteSlot NewSlot(Emote emote, UsageSlotKind kind) => kind switch
    {
        UsageSlotKind.UnsettledSeed => new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = emote.Name,
            IsArchived = emote.IsArchived,
            ArchivedAt = emote.ArchivedAt,
            SeedUnsettled = true
        },
        UsageSlotKind.Settled => new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = emote.Name,
            AddedAt = emote.FirstSeenAt ?? new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            AddedAtFromRest = true,
            IsArchived = emote.IsArchived,
            ArchivedAt = emote.ArchivedAt
        },
        _ => new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = emote.Name,
            AddedAt = DateTime.UtcNow,
            AddedAtFromRest = false,
            IsArchived = emote.IsArchived,
            ArchivedAt = emote.ArchivedAt
        }
    };
}
