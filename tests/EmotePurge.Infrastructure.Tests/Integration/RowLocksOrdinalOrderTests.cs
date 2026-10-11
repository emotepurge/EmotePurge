using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// COLLATE "C" in the row-lock helper (plan #372 E6). The Testcontainers image (alpine, musl) compares
// bytewise by default, which would let a missing clause pass unnoticed; so the key columns live on an
// ICU collation in a database of their own, where 'a1' sorts before 'B1' unless the helper says
// COLLATE "C". The proof is the lock itself: B asks for ['a1', 'B1'] while another transaction holds
// 'a1'; B must already hold 'B1' (the ordinal first) while it waits, which a NOWAIT probe shows.
[Collection("Postgres")]
public class RowLocksOrdinalOrderTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LockEmotesByIds_AndLockChannelsByIds_TakeTheirLocksInOrdinalOrder_NotInTheColumnCollation()
    {
        var databaseName = $"rowlocks_ordinal_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
#pragma warning disable EF1002 // locally generated Guid, CREATE DATABASE cannot be parameterized
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        await using (var migrate = fixture.CreateDbContext(databaseName))
        {
            await migrate.Database.MigrateAsync();
            await migrate.Database.ExecuteSqlRawAsync("""ALTER TABLE "Emotes" ALTER COLUMN "Id" TYPE text COLLATE "und-x-icu";""");
            await migrate.Database.ExecuteSqlRawAsync("""ALTER TABLE "Channels" ALTER COLUMN "Id" TYPE text COLLATE "und-x-icu";""");
            migrate.Channels.Add(new Channel { Id = "a1", ChannelName = "ordinal_a", IsBotActive = true });
            migrate.Channels.Add(new Channel { Id = "B1", ChannelName = "ordinal_b", IsBotActive = true });
            await migrate.SaveChangesAsync();
            migrate.Emotes.Add(new Emote { Id = "a1", ChannelId = "a1", SevenTvEmoteId = "x1", Name = "x", ImageUrl = "i" });
            migrate.Emotes.Add(new Emote { Id = "B1", ChannelId = "a1", SevenTvEmoteId = "x2", Name = "y", ImageUrl = "i" });
            await migrate.SaveChangesAsync();

            // Sanity: the column collation really orders the other way round.
            Assert.Equal(["a1", "B1"], await migrate.Database
                .SqlQuery<string>($"""SELECT "Id" AS "Value" FROM "Emotes" ORDER BY "Id" """).ToListAsync());
        }

        await AssertOrdinalAsync(databaseName, "Emotes", (db, ids) => db.LockEmotesByIdsAsync(ids, CancellationToken.None));
        await AssertOrdinalAsync(databaseName, "Channels", (db, ids) => db.LockChannelsByIdsAsync(ids, CancellationToken.None));

        // The reconcile entry sorts the same way.
        await using var reconcile = fixture.CreateDbContext(databaseName);
        await using var transaction = await reconcile.Database.BeginTransactionAsync();
        Assert.Equal(["B1", "a1"], await reconcile.LockEmotesForReconcileAsync("a1", ["x1", "x2"], CancellationToken.None));
    }

    private async Task AssertOrdinalAsync(
        string databaseName, string table, Func<AppDbContext, string[], Task<IReadOnlyList<string>>> lockRows)
    {
        var holderTag = $"ordinal-{table}-holder";
        await using var holder = fixture.CreateTaggedDbContext(holderTag, databaseName);
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
#pragma warning disable EF1002 // constant table name from this test
        await holder.Database.ExecuteSqlRawAsync($"""SELECT 1 FROM "{table}" WHERE "Id" = 'a1' FOR NO KEY UPDATE""");
#pragma warning restore EF1002

        var contenderTag = $"ordinal-{table}-contender";
        await using var contender = fixture.CreateTaggedDbContext(contenderTag, databaseName);
        await using var contenderTransaction = await contender.Database.BeginTransactionAsync();
        var waiting = lockRows(contender, ["a1", "B1"]);
        await fixture.WaitUntilBlockedOnLockAsync(contenderTag, waiting);

        // 'B1' is held by the waiting contender already: a NOWAIT probe cannot take it.
        await using var probe = fixture.CreateDbContext(databaseName);
        await using var probeTransaction = await probe.Database.BeginTransactionAsync();
#pragma warning disable EF1002 // constant table name from this test
        var failure = await Assert.ThrowsAsync<PostgresException>(() => probe.Database.ExecuteSqlRawAsync(
            $"""SELECT 1 FROM "{table}" WHERE "Id" = 'B1' FOR NO KEY UPDATE NOWAIT"""));
#pragma warning restore EF1002
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, failure.SqlState);

        await holderTransaction.CommitAsync();
        Assert.Equal(["B1", "a1"], await waiting);
    }
}
