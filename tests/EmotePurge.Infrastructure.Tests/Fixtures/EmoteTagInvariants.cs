using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Fixtures;

/// <summary>
/// The server-side invariants of #201 T-C that every tag scenario must leave intact, asserted on a
/// fresh context against what is committed. Shared by every test file that drives tag reports, so
/// the invariant is stated once.
/// </summary>
public static class EmoteTagInvariants
{
    /// <summary>
    /// Spec 5.5 rule 4 / E26 rev. 3: a tag that is not active in a set holds no placement there —
    /// every placement <c>(T, ·, S)</c> of the channel's tags has an activation <c>(T, S)</c>.
    /// </summary>
    public static async Task AssertInactiveTagsHoldNoPlacementAsync(this PostgresFixture fixture, string channelId)
    {
        await using var db = fixture.CreateDbContext();
        var tagIds = await db.EmoteTags.Where(t => t.ChannelId == channelId).Select(t => t.Id).ToListAsync();
        var placed = await db.EmoteTagPlacements
            .Where(p => tagIds.Contains(p.TagId))
            .Select(p => new { p.TagId, p.SevenTvEmoteSetId })
            .Distinct()
            .ToListAsync();
        var active = (await db.EmoteTagActivations
                .Where(a => tagIds.Contains(a.TagId))
                .Select(a => new { a.TagId, a.SevenTvEmoteSetId })
                .ToListAsync())
            .ToHashSet();

        Assert.All(placed, p => Assert.Contains(p, active));
    }

    /// <summary>
    /// Spec 5.5 rule 2: no placement without an entry — a left join of the channel's placements onto
    /// the entries by <c>(TagId, SevenTvEmoteId)</c> finds no row without a partner. The composite
    /// foreign key holds this in the database; the assertion states it where the scenarios are read.
    /// </summary>
    public static async Task AssertNoPlacementWithoutEntryAsync(this PostgresFixture fixture, string channelId)
    {
        await using var db = fixture.CreateDbContext();
        var tagIds = await db.EmoteTags.Where(t => t.ChannelId == channelId).Select(t => t.Id).ToListAsync();
        var orphans = await db.EmoteTagPlacements
            .Where(p => tagIds.Contains(p.TagId))
            .LeftJoin(
                db.EmoteTagEntries,
                p => new { p.TagId, p.SevenTvEmoteId },
                e => new { e.TagId, e.SevenTvEmoteId },
                (p, e) => new { p.TagId, p.SevenTvEmoteId, p.SevenTvEmoteSetId, HasEntry = e != null })
            .Where(x => !x.HasEntry)
            .ToListAsync();

        Assert.Empty(orphans);
    }

    /// <summary>Both placement invariants, for the scenarios that write placements.</summary>
    public static async Task AssertPlacementInvariantsAsync(this PostgresFixture fixture, string channelId)
    {
        await fixture.AssertInactiveTagsHoldNoPlacementAsync(channelId);
        await fixture.AssertNoPlacementWithoutEntryAsync(channelId);
    }
}
