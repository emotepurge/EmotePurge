using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The adoption body a client sends after reading the entry from <c>GET …/wishes/slots</c> (plan #485 2.1a): the
/// slot's alias and <c>AddedAt</c> as they are right now. Tests that are not about the binding use it, so they keep
/// testing what they tested before; an unknown slot id gets an alias no slot carries.
/// </summary>
internal static class WishAdoptRequests
{
    public static async Task<AdoptWishRequest> ForCurrentAsync(
        PostgresFixture fixture, string slotId, long? wishId = null, int? durationDays = null, DateTime? startsAt = null)
    {
        await using var db = fixture.CreateDbContext();
        var slot = await db.EmoteSlots.AsNoTracking()
            .Where(s => s.Id == slotId)
            .Select(s => new { s.Name, s.AddedAt })
            .FirstOrDefaultAsync();
        return new AdoptWishRequest(slotId, slot?.Name ?? "no-such-slot", slot?.AddedAt, wishId, durationDays, startsAt);
    }
}
