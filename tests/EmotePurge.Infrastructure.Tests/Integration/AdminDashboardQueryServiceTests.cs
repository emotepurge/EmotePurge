using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace EmotePurge.Infrastructure.Tests.Integration;

[Collection("Postgres")]
public class AdminDashboardQueryServiceTests(PostgresFixture fixture, ITestOutputHelper output) : IAsyncLifetime
{
    private const string DatabaseName = "admin_dashboard_tests";
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;
    private static readonly DateTime NowUtc = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var db = fixture.CreateDbContext(DatabaseName);
                await db.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDbContext();
        await reset.Database.ExecuteSqlRawAsync("TRUNCATE \"ChannelLiveDays\", \"Channels\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(AdminDashboardActivityRange.Days30, 30, "2026-09-12")]
    [InlineData(AdminDashboardActivityRange.Days90, 90, "2026-07-14")]
    public async Task Series_HasOneEntryPerDayInAscendingOrder(AdminDashboardActivityRange range, int count, string from)
    {
        await using var db = CreateDbContext();

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(range, NowUtc);

        Assert.Equal(count, result.Days.Count);
        Assert.Equal(DateOnly.Parse(from), result.FromDate);
        Assert.Equal(new DateOnly(2026, 10, 11), result.ToDate);
        Assert.Equal(result.FromDate, result.Days[0].Date);
        Assert.Equal(result.ToDate, result.Days[^1].Date);
        Assert.Equal(result.Days.Select(d => d.Date).OrderBy(d => d), result.Days.Select(d => d.Date));
        Assert.Equal(count, result.Days.Select(d => d.Date).Distinct().Count());
    }

    [Fact]
    public async Task Series_30d_AtEndOfDay_HasExactlyThirtyEntries()
    {
        await using var db = CreateDbContext();
        var now = new DateTime(2026, 10, 11, 23, 59, 0, DateTimeKind.Utc);

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(AdminDashboardActivityRange.Days30, now);

        Assert.Equal(30, result.Days.Count);
        Assert.Equal(new DateOnly(2026, 9, 12), result.FromDate);
        Assert.Equal("30d", result.Range);
    }

    [Fact]
    public async Task Series_13mo_StartsOnTheFirstOfTheMonthAndSpansAllCalendarDays()
    {
        await using var db = CreateDbContext();

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(AdminDashboardActivityRange.Months13, NowUtc);

        Assert.Equal(new DateOnly(2025, 9, 1), result.FromDate);
        Assert.Equal(new DateOnly(2026, 10, 11).DayNumber - new DateOnly(2025, 9, 1).DayNumber + 1, result.Days.Count);
        Assert.Equal("13mo", result.Range);
    }

    [Fact]
    public async Task Series_13mo_AcrossTheYearBoundary_StartsOnDecemberFirst()
    {
        await using var db = CreateDbContext();
        var now = new DateTime(2027, 1, 3, 8, 0, 0, DateTimeKind.Utc);

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(AdminDashboardActivityRange.Months13, now);

        Assert.Equal(new DateOnly(2025, 12, 1), result.FromDate);
    }

    [Fact]
    public async Task RunningDay_IsIncludedAndMarkedPartial()
    {
        await AddLiveDaysAsync(("a", new DateOnly(2026, 10, 11), 30));
        await using var db = CreateDbContext();

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(AdminDashboardActivityRange.Days30, NowUtc);

        Assert.True(result.TodayIsPartial);
        Assert.Equal(NowUtc, result.GeneratedAtUtc);
        Assert.Equal(1, result.Days[^1].LiveChannels);
    }

    [Fact]
    public async Task ZeroMinuteRows_AreNotCounted_AndDaysWithoutRowsAreNull()
    {
        var day = new DateOnly(2026, 10, 1);
        await AddLiveDaysAsync(("a", day, 0), ("b", day, 0), ("a", day.AddDays(1), 10));
        await using var db = CreateDbContext();

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(AdminDashboardActivityRange.Days30, NowUtc);

        Assert.Equal(0, Entry(result, day));
        Assert.Equal(1, Entry(result, day.AddDays(1)));
        Assert.Null(Entry(result, day.AddDays(2)));
    }

    [Fact]
    public async Task CountsChannelsPerDay()
    {
        var d1 = new DateOnly(2026, 10, 5);
        var d2 = new DateOnly(2026, 10, 6);
        await AddLiveDaysAsync(("a", d1, 60), ("b", d1, 5), ("a", d2, 20));
        await using var db = CreateDbContext();

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(AdminDashboardActivityRange.Days30, NowUtc);

        Assert.Equal(2, Entry(result, d1));
        Assert.Equal(1, Entry(result, d2));
    }

    [Fact]
    public async Task RowsOutsideTheWindow_DoNotChangeAnything()
    {
        await AddLiveDaysAsync(
            ("a", new DateOnly(2026, 9, 11), 60),
            ("a", new DateOnly(2025, 1, 1), 60),
            ("b", new DateOnly(2026, 10, 12), 60),
            ("a", new DateOnly(2026, 10, 10), 60));
        await using var db = CreateDbContext();

        var result = await new AdminDashboardQueryService(db).GetActivityAsync(AdminDashboardActivityRange.Days30, NowUtc);

        Assert.Equal(1, result.Days.Count(d => d.LiveChannels is not null));
        Assert.Equal(1, Entry(result, new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public async Task ExplainPlan_IsLoggedForReview()
    {
        await using var db = CreateDbContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "EXPLAIN SELECT \"Date\", COUNT(*) FILTER (WHERE \"LiveMinutes\" > 0) FROM \"ChannelLiveDays\" "
            + "WHERE \"Date\" >= DATE '2026-09-12' AND \"Date\" <= DATE '2026-10-11' GROUP BY \"Date\"";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            output.WriteLine(reader.GetString(0));
        }
    }

    private static int? Entry(AdminDashboardActivity result, DateOnly date) => result.Days.Single(d => d.Date == date).LiveChannels;

    private async Task AddLiveDaysAsync(params (string Channel, DateOnly Date, int Minutes)[] rows)
    {
        await using var db = CreateDbContext();
        var channels = rows.Select(r => r.Channel).Distinct()
            .ToDictionary(name => name, name => new Channel { ChannelName = name });
        db.Channels.AddRange(channels.Values);
        db.ChannelLiveDays.AddRange(rows.Select(r => new ChannelLiveDay
        {
            ChannelId = channels[r.Channel].Id,
            Date = r.Date,
            LiveMinutes = r.Minutes,
        }));
        await db.SaveChangesAsync();
    }

    private AppDbContext CreateDbContext() => fixture.CreateDbContext(DatabaseName);
}
