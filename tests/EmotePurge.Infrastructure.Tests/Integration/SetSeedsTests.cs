using EmotePurge.Infrastructure.Persistence;
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
}
