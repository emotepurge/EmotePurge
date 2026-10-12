using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The shared "seeds of a set" query (plan #376, 2.1): an unsettled seed of the channel that carries
// at least one usage row of the asked set — the settle's population, the map exclusion and the readers.
[Collection("Postgres")]
public class SetSeedsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SeedsOfSet_ReturnsOnlyUnsettledSeedsWithUsageRowsOfThatSet()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setseeds_scope");

        var inA = await SeedEmoteAsync(db, channel, "in-a", "InA");
        var seedInA = await SeedSeedAsync(db, inA);
        await SeedUsageAsync(db, inA, seedInA, SetA, 3);

        var inBoth = await SeedEmoteAsync(db, channel, "in-both", "InBoth");
        var seedInBoth = await SeedSeedAsync(db, inBoth);
        await SeedUsageAsync(db, inBoth, seedInBoth, SetA, 1);
        await SeedUsageAsync(db, inBoth, seedInBoth, SetB, 1);

        var onlyB = await SeedEmoteAsync(db, channel, "only-b", "OnlyB");
        var seedOnlyB = await SeedSeedAsync(db, onlyB);
        await SeedUsageAsync(db, onlyB, seedOnlyB, SetB, 2);

        var noUsage = await SeedEmoteAsync(db, channel, "no-usage", "NoUsage");
        await SeedSeedAsync(db, noUsage);

        var settled = await SeedEmoteAsync(db, channel, "settled", "Settled");
        var settledSlot = await SeedSlotAsync(db, settled, "Settled", T1);
        await SeedUsageAsync(db, settled, settledSlot, SetA, 4);

        var idsA = db.SeedsOfSet(channel.Id, SetA).Select(s => s.Id).ToList();
        var idsB = db.SeedsOfSet(channel.Id, SetB).Select(s => s.Id).ToList();

        Assert.Equal(new[] { seedInA.Id, seedInBoth.Id }.Order(StringComparer.Ordinal), idsA.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { seedInBoth.Id, seedOnlyB.Id }.Order(StringComparer.Ordinal), idsB.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SeedsOfSet_IgnoresArchiveStateAndOtherChannels()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setseeds_own");
        var other = await SeedChannelAsync(db, "setseeds_other");

        var archived = await SeedEmoteAsync(db, channel, "archived", "Archived", archived: true);
        var archivedSeed = await SeedSeedAsync(db, archived);
        await SeedUsageAsync(db, archived, archivedSeed, SetA, 1);

        var foreign = await SeedEmoteAsync(db, other, "foreign", "Foreign");
        var foreignSeed = await SeedSeedAsync(db, foreign);
        await SeedUsageAsync(db, foreign, foreignSeed, SetA, 1);

        var ids = db.SeedsOfSet(channel.Id, SetA).Select(s => s.Id).ToList();

        Assert.Equal([archivedSeed.Id], ids);
    }

    // The slot-aware readers do not ask SeedsOfSet a second time: they read the seeds off the slot snapshot's
    // own "has a row in X" answer (UsageViewSlotSnapshot). Both ways must name the same slots, for every set —
    // settled and unsettled, archived and not, rows in one set, both or none, another channel alongside.
    [Fact]
    public async Task SlotSnapshot_DerivesExactlyTheSeedsOfSetForEverySet()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "setseeds_snapshot");
        var other = await SeedChannelAsync(db, "setseeds_snapshot_other");

        var inA = await SeedEmoteAsync(db, channel, "snap-in-a", "SnapInA");
        var seedInA = await SeedSeedAsync(db, inA);
        await SeedUsageAsync(db, inA, seedInA, SetA, 3);

        var inBoth = await SeedEmoteAsync(db, channel, "snap-in-both", "SnapInBoth");
        var seedInBoth = await SeedSeedAsync(db, inBoth);
        await SeedUsageAsync(db, inBoth, seedInBoth, SetA, 1);
        await SeedUsageAsync(db, inBoth, seedInBoth, SetB, 1);

        var archived = await SeedEmoteAsync(db, channel, "snap-archived", "SnapArchived", archived: true);
        var archivedSeed = await SeedSeedAsync(db, archived);
        await SeedUsageAsync(db, archived, archivedSeed, SetB, 0, 2);

        var noUsage = await SeedEmoteAsync(db, channel, "snap-no-usage", "SnapNoUsage");
        var seedNoUsage = await SeedSeedAsync(db, noUsage);

        // A settled emote with an own slot carrying rows, an archived own slot carrying rows, and a seed
        // settled away (SeedUnsettled false) that still carries its history.
        var settled = await SeedEmoteAsync(db, channel, "snap-settled", "SnapSettled");
        var ownSlot = await SeedSlotAsync(db, settled, "SnapSettled", T1);
        await SeedUsageAsync(db, settled, ownSlot, SetA, 4);
        var archivedOwnSlot = await SeedSlotAsync(db, settled, "SnapOld", T1.AddDays(-3), archived: true, archivedAt: T1);
        await SeedUsageAsync(db, settled, archivedOwnSlot, SetB, 5);
        var settledSeed = await SeedSlotAsync(db, settled, "SnapSettled", addedAt: null, fromRest: false);
        await SeedUsageAsync(db, settled, settledSeed, SetA, 6);

        var foreign = await SeedEmoteAsync(db, other, "snap-foreign", "SnapForeign");
        var foreignSeed = await SeedSeedAsync(db, foreign);
        await SeedUsageAsync(db, foreign, foreignSeed, SetA, 1);
        await SeedUsageAsync(db, foreign, foreignSeed, SetB, 1);

        const string emptySet = "01SLOTSETC00000000000000CC";
        foreach (var setId in new[] { SetA, SetB, emptySet })
        {
            var snapshot = await UsageViewSlotSnapshot.LoadAsync(db, channel.Id, setId, CancellationToken.None);
            var seedsOfSet = db.SeedsOfSet(channel.Id, setId).Select(s => s.Id).ToList();

            Assert.Equal(seedsOfSet.Order(StringComparer.Ordinal), snapshot.SeedIdsOfSet.Order(StringComparer.Ordinal));
        }

        // The fixture actually exercises both sides of every filter, rather than agreeing on empty sets.
        var a = await UsageViewSlotSnapshot.LoadAsync(db, channel.Id, SetA, CancellationToken.None);
        var b = await UsageViewSlotSnapshot.LoadAsync(db, channel.Id, SetB, CancellationToken.None);
        Assert.Equal(new[] { seedInA.Id, seedInBoth.Id }.Order(StringComparer.Ordinal), a.SeedIdsOfSet.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { seedInBoth.Id, archivedSeed.Id }.Order(StringComparer.Ordinal), b.SeedIdsOfSet.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { seedInA.Id, seedInBoth.Id, ownSlot.Id, settledSeed.Id }.Order(StringComparer.Ordinal),
            a.SlotIdsWithRowsInSet.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { seedInBoth.Id, archivedSeed.Id, archivedOwnSlot.Id }.Order(StringComparer.Ordinal),
            b.SlotIdsWithRowsInSet.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { seedInA.Id, seedInBoth.Id, archivedSeed.Id, seedNoUsage.Id, ownSlot.Id, archivedOwnSlot.Id, settledSeed.Id }
                .Order(StringComparer.Ordinal),
            a.Slots.Select(s => s.Id).Order(StringComparer.Ordinal));
    }
}
