using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>
/// The global saved log filters (spec section 6.3). Names are trimmed (1 to 60 characters) and unique by
/// <see cref="LogFilterNames.NormalizeKey"/>; the filter string goes through <see cref="LogFilterParser"/> and is
/// stored exactly as submitted, never normalized, so a seed's quoting survives a round trip. At most
/// <see cref="MaxFilters"/> exist. Creating takes a transaction-scoped advisory lock, so the cap and the next
/// <c>SortOrder</c> hold under concurrent writers; the unique index on <c>NameKey</c> settles the name race
/// of updates and of any creator that slips past the pre-check (SQLSTATE 23505 becomes
/// <see cref="LogFilterWriteStatus.NameTaken"/>). Writes are not audited (A12).
/// </summary>
internal sealed class LogFilterService(AppDbContext db, TimeProvider time) : ILogFilterService
{
    internal const int MaxFilters = 30;
    internal const int MaxNameLength = 60;

    private const string NameKeyIndex = "IX_LogFilters_NameKey";

    // Arbitrary constant key of the creator lock (pg_advisory_xact_lock takes a bigint).
    private const long CreateLockKey = 383_001;

    public async Task<IReadOnlyList<LogFilterView>> ListAsync(CancellationToken ct)
    {
        var rows = await db.LogFilters.AsNoTracking().OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    public async Task<LogFilterWriteResult> CreateAsync(string name, string filter, CancellationToken ct)
    {
        if (Validate(name, filter) is { } invalid)
        {
            return invalid;
        }

        var trimmed = name.Trim();
        var key = LogFilterNames.NormalizeKey(trimmed);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({CreateLockKey})", ct);

            if (await db.LogFilters.AnyAsync(f => f.NameKey == key, ct))
            {
                return new LogFilterWriteResult(LogFilterWriteStatus.NameTaken);
            }

            if (await db.LogFilters.CountAsync(ct) >= MaxFilters)
            {
                return new LogFilterWriteResult(LogFilterWriteStatus.LimitReached);
            }

            var now = time.GetUtcNow().UtcDateTime;
            var next = (await db.LogFilters.MaxAsync(f => (int?)f.SortOrder, ct) ?? 0) + 1;
            var row = new LogFilter
            {
                Name = trimmed,
                NameKey = key,
                Filter = filter,
                SortOrder = next,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            db.LogFilters.Add(row);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new LogFilterWriteResult(LogFilterWriteStatus.Ok, ToView(row));
        }
        catch (DbUpdateException exception) when (IsNameKeyConflict(exception))
        {
            return new LogFilterWriteResult(LogFilterWriteStatus.NameTaken);
        }
    }

    public async Task<LogFilterWriteResult> UpdateAsync(int id, string name, string filter, CancellationToken ct)
    {
        if (Validate(name, filter) is { } invalid)
        {
            return invalid;
        }

        var row = await db.LogFilters.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (row is null)
        {
            return new LogFilterWriteResult(LogFilterWriteStatus.NotFound);
        }

        var trimmed = name.Trim();
        var key = LogFilterNames.NormalizeKey(trimmed);
        if (await db.LogFilters.AnyAsync(f => f.NameKey == key && f.Id != id, ct))
        {
            return new LogFilterWriteResult(LogFilterWriteStatus.NameTaken);
        }

        row.Name = trimmed;
        row.NameKey = key;
        row.Filter = filter;
        row.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsNameKeyConflict(exception))
        {
            return new LogFilterWriteResult(LogFilterWriteStatus.NameTaken);
        }

        return new LogFilterWriteResult(LogFilterWriteStatus.Ok, ToView(row));
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await db.LogFilters.Where(f => f.Id == id).ExecuteDeleteAsync(ct) > 0;

    // Name first (it is the cheaper check and the form shows it first), then the filter through the one parser.
    private static LogFilterWriteResult? Validate(string name, string filter)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength || LogFilterNames.NormalizeKey(trimmed).Length > MaxNameLength)
        {
            return new LogFilterWriteResult(LogFilterWriteStatus.InvalidName);
        }

        var parsed = LogFilterParser.Parse(filter);
        return parsed.IsSuccess
            ? null
            : new LogFilterWriteResult(LogFilterWriteStatus.InvalidFilter, null, parsed.Reason, parsed.Position, parsed.Length);
    }

    private static bool IsNameKeyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: NameKeyIndex };

    private static LogFilterView ToView(LogFilter row) => new(row.Id, row.Name, row.Filter, row.SortOrder);
}
