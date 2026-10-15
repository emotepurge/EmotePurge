using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The saved log filters against the real Postgres 16 Alpine container (C locale, so only application-side
/// normalization can fold non-ASCII case). Its own database: the tests rely on the four seeds and on the
/// 30-filter cap, so the table is reset to exactly the seeds before every test.
/// </summary>
[Collection("Postgres")]
public class LogFilterServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "log_filter_service_tests";
    private const string SeedReconnects = "Twitch-Reconnects";

    // The pristine migration seeds, read by the first test of the class before anything mutates them.
    private static LogFilter[]? s_seeds;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        await LogEntrySeed.PrepareDatabaseAsync(fixture, DatabaseName);
        await using var db = fixture.CreateDbContext(DatabaseName);
        s_seeds ??= await db.LogFilters.AsNoTracking().OrderBy(f => f.SortOrder).ToArrayAsync();

        await db.Database.ExecuteSqlRawAsync("TRUNCATE \"LogFilters\" RESTART IDENTITY;");
        db.LogFilters.AddRange(s_seeds.Select(s => new LogFilter
        {
            Name = s.Name,
            NameKey = s.NameKey,
            Filter = s.Filter,
            SortOrder = s.SortOrder,
            CreatedAtUtc = s.CreatedAtUtc,
            UpdatedAtUtc = s.UpdatedAtUtc,
        }));
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => LogEntrySeed.ReleasePoolAsync(fixture, DatabaseName);

    [Fact]
    public async Task List_ReturnsTheFourSeedsInSortOrder()
    {
        var list = await ListAsync();

        Assert.Equal(["Start nach Deploy", SeedReconnects, "7TV-Sync/EventAPI", "Alle Fehler"], list.Select(f => f.Name));
        Assert.Equal([1, 2, 3, 4], list.Select(f => f.SortOrder));
    }

    [Fact]
    public async Task Crud_RoundTrip()
    {
        var created = await CreateAsync("Meine Sicht", "level>=warn source:api");
        Assert.Equal(LogFilterWriteStatus.Ok, created.Status);
        Assert.Equal(5, created.Filter!.SortOrder);
        Assert.Equal("Meine Sicht", created.Filter.Name);
        Assert.Equal("level>=warn source:api", created.Filter.Filter);

        var updated = await UpdateAsync(created.Filter.Id, "Meine Sicht 2", "level>=error");
        Assert.Equal(LogFilterWriteStatus.Ok, updated.Status);
        Assert.Equal(created.Filter.Id, updated.Filter!.Id);
        Assert.Equal(5, updated.Filter.SortOrder);
        Assert.Equal("level>=error", Assert.Single(await ListAsync(), f => f.Id == created.Filter.Id).Filter);

        Assert.True(await DeleteAsync(created.Filter.Id));
        Assert.DoesNotContain(await ListAsync(), f => f.Id == created.Filter.Id);
        Assert.False(await DeleteAsync(created.Filter.Id));
    }

    [Fact]
    public async Task Create_AfterDeletingTheLast_ReusesTheNextSortOrderAsMaxPlusOne()
    {
        var first = (await CreateAsync("A", "source:api")).Filter!;
        var second = (await CreateAsync("B", "source:api")).Filter!;
        await DeleteAsync(first.Id);

        var third = (await CreateAsync("C", "source:api")).Filter!;

        Assert.Equal(second.SortOrder + 1, third.SortOrder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankName_IsInvalidName(string name)
    {
        Assert.Equal(LogFilterWriteStatus.InvalidName, (await CreateAsync(name, "source:api")).Status);
    }

    [Fact]
    public async Task Create_NameLengthLimit_Is60AfterTrimming()
    {
        Assert.Equal(LogFilterWriteStatus.Ok, (await CreateAsync("  " + new string('x', 60) + "  ", "source:api")).Status);
        Assert.Equal(LogFilterWriteStatus.InvalidName, (await CreateAsync(new string('y', 61), "source:api")).Status);
    }

    [Fact]
    public async Task Create_StoresTheTrimmedName()
    {
        var created = await CreateAsync("  Spaces around  ", "source:api");

        Assert.Equal("Spaces around", created.Filter!.Name);
    }

    [Theory]
    [InlineData("level:nonsense", "invalid_level")]
    [InlineData("cat:\"unterminated", "unterminated_quote")]
    [InlineData("level>=", "empty_value")]
    public async Task Create_InvalidFilter_ReportsTheParserError(string filter, string reason)
    {
        var result = await CreateAsync("Kaputt", filter);

        Assert.Equal(LogFilterWriteStatus.InvalidFilter, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.NotNull(result.Position);
        Assert.NotNull(result.Length);
        Assert.Null(result.Filter);
        Assert.Equal(4, (await ListAsync()).Count);
    }

    [Fact]
    public async Task Create_FilterOverLimit_IsInvalidFilter_EvenWhitespaceOnly()
    {
        var tooLong = await CreateAsync("Lang", new string(' ', 513));
        var tooLongText = await CreateAsync("Lang2", "x " + new string('a', 520));

        Assert.Equal(LogFilterWriteStatus.InvalidFilter, tooLong.Status);
        Assert.Equal("too_long", tooLong.Reason);
        Assert.Equal(LogFilterWriteStatus.InvalidFilter, tooLongText.Status);
    }

    [Fact]
    public async Task Create_FilterStringIsStoredExactlyAsSubmitted()
    {
        const string filter = "  source:api    level>=warn  ";

        var created = await CreateAsync("Roh", filter);

        Assert.Equal(filter, created.Filter!.Filter);
    }

    [Fact]
    public async Task Create_SameNameTwice_IsNameTaken()
    {
        await CreateAsync("Doppelt", "source:api");

        Assert.Equal(LogFilterWriteStatus.NameTaken, (await CreateAsync("Doppelt", "source:worker")).Status);
    }

    [Fact]
    public async Task Create_TrimmedCaseVariant_IsNameTaken()
    {
        var result = await CreateAsync("  alle fehler ", "level>=error");

        Assert.Equal(LogFilterWriteStatus.NameTaken, result.Status);
    }

    [Fact]
    public async Task Create_InnerWhitespaceVariant_IsNameTaken()
    {
        Assert.Equal(LogFilterWriteStatus.NameTaken, (await CreateAsync("ALLE   FEHLER", "level>=error")).Status);
    }

    [Fact]
    public async Task Create_NonAsciiCaseVariant_IsNameTaken()
    {
        Assert.Equal(LogFilterWriteStatus.Ok, (await CreateAsync("Ärger", "level>=error")).Status);

        Assert.Equal(LogFilterWriteStatus.NameTaken, (await CreateAsync("ärger", "level>=warn")).Status);
    }

    [Fact]
    public async Task Create_ConcurrentSameName_ExactlyOneWins()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            await using var db = fixture.CreateDbContext(DatabaseName);
            return await new LogFilterService(db, _time).CreateAsync("Rennen", "source:api", default);
        }));

        Assert.Single(results, r => r.Status == LogFilterWriteStatus.Ok);
        Assert.Equal(3, results.Count(r => r.Status == LogFilterWriteStatus.NameTaken));
        Assert.Equal(1, (await ListAsync()).Count(f => f.Name == "Rennen"));
    }

    [Fact]
    public async Task Create_ThirtyFilters_AreAllowed_TheThirtyFirstIsLimitReached()
    {
        for (var i = 0; i < 26; i++)
        {
            Assert.Equal(LogFilterWriteStatus.Ok, (await CreateAsync($"Filter {i}", "source:api")).Status);
        }

        Assert.Equal(30, (await ListAsync()).Count);
        var result = await CreateAsync("Eins zu viel", "source:api");

        Assert.Equal(LogFilterWriteStatus.LimitReached, result.Status);
        Assert.Equal(30, (await ListAsync()).Count);
    }

    [Fact]
    public async Task Create_ConcurrentAtTheCap_NeverExceedsThirty()
    {
        for (var i = 0; i < 23; i++)
        {
            await CreateAsync($"Filter {i}", "source:api");
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            await using var db = fixture.CreateDbContext(DatabaseName);
            return await new LogFilterService(db, _time).CreateAsync($"Parallel {i}", "source:api", default);
        }));

        Assert.Equal(3, results.Count(r => r.Status == LogFilterWriteStatus.Ok));
        Assert.Single(results, r => r.Status == LogFilterWriteStatus.LimitReached);
        Assert.Equal(30, (await ListAsync()).Count);
    }

    [Fact]
    public async Task Update_OwnNameDifferentCase_Succeeds()
    {
        var alleFehler = Assert.Single(await ListAsync(), f => f.Name == "Alle Fehler");

        var result = await UpdateAsync(alleFehler.Id, "ALLE FEHLER", alleFehler.Filter);

        Assert.Equal(LogFilterWriteStatus.Ok, result.Status);
        Assert.Equal("ALLE FEHLER", result.Filter!.Name);
    }

    [Fact]
    public async Task Update_OwnNameNonAsciiCaseChange_Succeeds()
    {
        var created = (await CreateAsync("Ärger", "level>=error")).Filter!;

        var result = await UpdateAsync(created.Id, "ÄRGER", "level>=error");

        Assert.Equal(LogFilterWriteStatus.Ok, result.Status);
        Assert.Equal("ÄRGER", result.Filter!.Name);
    }

    [Fact]
    public async Task Update_ToNameOfAnotherFilter_IsNameTaken()
    {
        var created = (await CreateAsync("Eigen", "source:api")).Filter!;

        Assert.Equal(LogFilterWriteStatus.NameTaken, (await UpdateAsync(created.Id, " alle FEHLER ", "source:api")).Status);
        Assert.Equal("Eigen", Assert.Single(await ListAsync(), f => f.Id == created.Id).Name);
    }

    [Fact]
    public async Task Update_ToNonAsciiCaseVariantOfOtherFilter_IsNameTaken()
    {
        await CreateAsync("Ärger", "level>=error");
        var other = (await CreateAsync("Anderes", "source:api")).Filter!;

        Assert.Equal(LogFilterWriteStatus.NameTaken, (await UpdateAsync(other.Id, "ärger", "source:api")).Status);
    }

    [Fact]
    public async Task Update_SeedUnchanged_KeepsFilterByteIdentical()
    {
        var seed = Assert.Single(await ListAsync(), f => f.Name == SeedReconnects);
        Assert.Contains("\"angestoßen (Quelle\"", seed.Filter);

        var result = await UpdateAsync(seed.Id, seed.Name, seed.Filter);

        Assert.Equal(LogFilterWriteStatus.Ok, result.Status);
        Assert.Equal(seed.Filter, result.Filter!.Filter);
        await using var db = fixture.CreateDbContext(DatabaseName);
        var stored = await db.LogFilters.AsNoTracking().SingleAsync(f => f.Id == seed.Id);
        Assert.Equal(seed.Filter, stored.Filter);
        Assert.True(System.Text.Encoding.UTF8.GetBytes(seed.Filter).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(stored.Filter)));
        Assert.Equal("twitch-reconnects", stored.NameKey);
    }

    [Fact]
    public async Task Update_MaintainsNameKeyAndTimestamp()
    {
        var created = (await CreateAsync("Alt", "source:api")).Filter!;
        _time.Advance(TimeSpan.FromMinutes(5));

        await UpdateAsync(created.Id, "  Neu   Name ", "source:worker");

        await using var db = fixture.CreateDbContext(DatabaseName);
        var stored = await db.LogFilters.AsNoTracking().SingleAsync(f => f.Id == created.Id);
        Assert.Equal("Neu   Name", stored.Name);
        Assert.Equal("neu name", stored.NameKey);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, stored.UpdatedAtUtc);
        Assert.True(stored.CreatedAtUtc < stored.UpdatedAtUtc);
    }

    [Fact]
    public async Task Update_Unknown_IsNotFound_AndInvalidInputWinsOverNotFound()
    {
        Assert.Equal(LogFilterWriteStatus.NotFound, (await UpdateAsync(9999, "Name", "source:api")).Status);
        Assert.Equal(LogFilterWriteStatus.InvalidFilter, (await UpdateAsync(9999, "Name", "level:nonsense")).Status);
        Assert.Equal(LogFilterWriteStatus.InvalidName, (await UpdateAsync(9999, " ", "source:api")).Status);
    }

    [Fact]
    public async Task Update_InvalidFilter_LeavesTheRowUntouched()
    {
        var created = (await CreateAsync("Stabil", "source:api")).Filter!;

        var result = await UpdateAsync(created.Id, "Stabil", "level:nonsense");

        Assert.Equal(LogFilterWriteStatus.InvalidFilter, result.Status);
        Assert.Equal("source:api", Assert.Single(await ListAsync(), f => f.Id == created.Id).Filter);
    }

    [Fact]
    public async Task Update_AtTheCap_StillWorks()
    {
        for (var i = 0; i < 26; i++)
        {
            await CreateAsync($"Filter {i}", "source:api");
        }

        var seed = (await ListAsync())[0];

        Assert.Equal(LogFilterWriteStatus.Ok, (await UpdateAsync(seed.Id, seed.Name, "source:api")).Status);
    }

    private async Task<IReadOnlyList<LogFilterView>> ListAsync()
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await new LogFilterService(db, _time).ListAsync(default);
    }

    private async Task<LogFilterWriteResult> CreateAsync(string name, string filter)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await new LogFilterService(db, _time).CreateAsync(name, filter, default);
    }

    private async Task<LogFilterWriteResult> UpdateAsync(int id, string name, string filter)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await new LogFilterService(db, _time).UpdateAsync(id, name, filter, default);
    }

    private async Task<bool> DeleteAsync(int id)
    {
        await using var db = fixture.CreateDbContext(DatabaseName);
        return await new LogFilterService(db, _time).DeleteAsync(id, default);
    }
}
