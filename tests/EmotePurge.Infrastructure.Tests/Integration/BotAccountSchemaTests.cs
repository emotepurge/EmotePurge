using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The schema contract of AddBotAccount (#487, T1) against the migrated head schema: the singleton row, the two
// status CHECKs, the database defaults and the shape of the table. Shares the head-schema database, so every test
// clears the one row first and last.
[Collection("Postgres")]
public class BotAccountSchemaTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string ClearSql = """DELETE FROM "BotAccountCredentials";""";

    public async Task InitializeAsync() => await ClearAsync();

    public async Task DisposeAsync() => await ClearAsync();

    [Fact]
    public async Task SecondRow_FailsOnTheSingletonCheck_AndOnTheKey()
    {
        await using var db = fixture.CreateDbContext();
        db.BotAccountCredentials.Add(new BotAccountCredential());
        await db.SaveChangesAsync();

        // Another id breaks the CHECK, the same id breaks the primary key.
        await AssertViolatesAsync(db, """INSERT INTO "BotAccountCredentials" ("Id") VALUES (2)""", "23514", "CK_BotAccountCredentials_Singleton");
        await AssertViolatesAsync(db, """INSERT INTO "BotAccountCredentials" ("Id") VALUES (1)""", "23505", "PK_BotAccountCredentials");
    }

    [Fact]
    public async Task IdOtherThanOne_FailsOnTheSingletonCheck_EvenOnAnEmptyTable()
    {
        await using var db = fixture.CreateDbContext();

        await AssertViolatesAsync(db, """INSERT INTO "BotAccountCredentials" ("Id") VALUES (0)""", "23514", "CK_BotAccountCredentials_Singleton");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("unverified")]
    [InlineData("valid")]
    [InlineData("invalid")]
    [InlineData("expired")]
    public async Task EachKnownStatus_IsAccepted(string status)
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "BotAccountCredentials" ("Id", "Status") VALUES (1, {0})""", status);

        Assert.Equal(status, (await db.BotAccountCredentials.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("revoked")]
    public async Task UnknownStatus_FailsOnTheStatusCheck(string status)
    {
        await using var db = fixture.CreateDbContext();
        db.BotAccountCredentials.Add(new BotAccountCredential { Status = status });

        await AssertViolatesAsync(() => db.SaveChangesAsync(), "23514", "CK_BotAccountCredentials_Status");
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("malformed", true)]
    [InlineData("lost", true)]
    [InlineData("expired", false)]
    [InlineData("", false)]
    public async Task TwitchSessionState_AcceptsOnlyTheThreeValues_AndNull(string state, bool accepted)
    {
        await using var db = fixture.CreateDbContext();
        db.BotAccountCredentials.Add(new BotAccountCredential { TwitchSessionState = state });

        if (accepted)
        {
            await db.SaveChangesAsync();
            return;
        }

        await AssertViolatesAsync(() => db.SaveChangesAsync(), "23514", "CK_BotAccountCredentials_TwitchSessionState");
    }

    [Fact]
    public async Task RowCreatedByAnUpsertThatNamesOnlyTheId_HasTheDocumentedDefaults()
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "BotAccountCredentials" ("Id") VALUES (1) ON CONFLICT ("Id") DO NOTHING""");

        var row = await db.BotAccountCredentials.AsNoTracking().SingleAsync();

        Assert.Equal((BotAccountStatus.None, false, 0, 0L, 0, 0L, 0), (row.Status, row.KillSwitch, row.CredentialVersion, row.RunnerEpoch, row.IdentityVersion, row.KillSwitchVersion, row.RenewalFailureCount));
        Assert.Null(row.SealedToken);
        Assert.Null(row.TwitchSessionState);
        Assert.Null(row.LastVerifiedCredentialVersion);
        Assert.Null(row.RenewalRunId);
    }

    [Fact]
    public async Task Table_HasTwentyOneRenewalColumns_AndNoColumnForCookies()
    {
        await using var db = fixture.CreateDbContext();
        var columns = await db.Database
            .SqlQueryRaw<string>("""SELECT column_name AS "Value" FROM information_schema.columns WHERE table_name = 'BotAccountCredentials'""")
            .ToListAsync();

        Assert.Equal(33, columns.Count);
        Assert.DoesNotContain(columns, c => c.Contains("cookie", StringComparison.OrdinalIgnoreCase));

        string[] baseColumns =
        [
            "Id", "SealedToken", "ExpiresAtUtc", "SetAtUtc", "Status", "BotSevenTvUserId", "LastVerifiedAtUtc", "LastErrorCode",
            "KillSwitch", "PausedUntilUtc", "CredentialVersion", "RunnerEpoch",
        ];
        Assert.Equal(21, columns.Except(baseColumns).Count());
    }

    [Fact]
    public async Task BotAttemptColumnsOnWishes_AreNullable_AndTheModelMatches()
    {
        await using var db = fixture.CreateDbContext();
        var nullable = await db.Database
            .SqlQueryRaw<string>("""
                SELECT column_name AS "Value" FROM information_schema.columns
                WHERE table_name = 'Wishes' AND is_nullable = 'YES'
                  AND column_name IN ('BotSetId', 'BotAttemptVersion', 'BotRemoveAtUtc', 'BotRemoveEvidence', 'BotAddAckAtUtc', 'BotConfirmDeadlineAtUtc', 'BotRepairAtUtc')
                """)
            .ToListAsync();
        var settings = await db.Database
            .SqlQueryRaw<string>("""
                SELECT column_name AS "Value" FROM information_schema.columns
                WHERE table_name = 'ChannelWishSettings' AND column_name = 'BotEditorCheckedIdentityVersion' AND is_nullable = 'YES'
                """)
            .ToListAsync();

        Assert.Equal(7, nullable.Count);
        Assert.Single(settings);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private async Task ClearAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(ClearSql);
    }

    private static async Task AssertViolatesAsync(AppDbContext db, string sql, string sqlState, string constraint) =>
        await AssertViolatesAsync(() => db.Database.ExecuteSqlRawAsync(sql), sqlState, constraint);

    private static async Task AssertViolatesAsync(Func<Task> action, string sqlState, string constraint)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        var pg = Assert.IsType<PostgresException>(ex is DbUpdateException ? ex.InnerException : ex);
        Assert.Equal((sqlState, constraint), (pg.SqlState, pg.ConstraintName));
    }
}
