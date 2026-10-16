using System.Buffers.Binary;
using System.Buffers.Text;
using System.Data;
using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>
/// The log viewer's queries (spec section 8) as raw, parametrized SQL over the <see cref="AppDbContext"/>'s
/// connection. Each call is one read-only transaction whose first statement is
/// <c>SET LOCAL statement_timeout</c> (<see cref="LogViewerOptions.QueryTimeoutMilliseconds"/>); SQLSTATE
/// <c>57014</c> without a cancelled caller becomes <see cref="LogQueryTimeoutException"/>. Lists and keys use
/// the exact bounds <c>[from, to)</c> and keyset order <c>(TimestampUtc DESC, Id DESC)</c>; only the summary
/// snaps, for its histogram. The queries go through EF's raw SQL API, so EF logs a failing one on Error —
/// which is why callers run them inside <see cref="LogCaptureSuppression"/> (recursion layer 4).
/// </summary>
internal sealed class LogQueryService(AppDbContext db, LogViewerOptions options, TimeProvider time) : ILogQueryService
{
    internal const int DefaultPageSize = 100;
    internal const int MaxPageSize = 200;
    internal const int MaxKeys = 2500;
    internal const int MaxIds = 200;
    internal const int CategoryFacetSize = 15;

    private const string EntryColumns =
        "e.\"Id\" AS \"Id\", e.\"TimestampUtc\" AS \"TimestampUtc\", e.\"Level\" AS \"Level\", e.\"Source\" AS \"Source\", " +
        "e.\"InstanceId\" AS \"InstanceId\", e.\"Category\" AS \"Category\", e.\"EventId\" AS \"EventId\", e.\"EventName\" AS \"EventName\", " +
        "e.\"Message\" AS \"Message\", (e.\"Exception\" IS NOT NULL) AS \"HasException\", e.\"ExceptionType\" AS \"ExceptionType\", " +
        "e.\"Properties\"::text AS \"Properties\", e.\"Truncated\" AS \"Truncated\"";

    private const string InRange = "e.\"TimestampUtc\" >= @from AND e.\"TimestampUtc\" < @to";
    private const string NewestFirst = "ORDER BY e.\"TimestampUtc\" DESC, e.\"Id\" DESC";
    private const int CursorBytes = 16;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CoverageWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan OverdueGrace = TimeSpan.FromHours(2);

    public async Task<LogPage> GetPageAsync(string? filter, LogRangeRequest range, string? before, int limit, CancellationToken ct)
    {
        var where = LogFilterSql.Translate(LogFilterParser.Parse(filter).ExpressionOrThrow());
        var exact = LogTimeRange.Resolve(range, Now());
        var cursor = string.IsNullOrWhiteSpace(before) ? ((DateTime TimestampUtc, long Id)?)null : DecodeCursor(before);
        var pageSize = limit <= 0 ? DefaultPageSize : Math.Min(limit, MaxPageSize);

        var sql =
            $"SELECT {EntryColumns} FROM \"LogEntries\" e WHERE {InRange}" +
            (cursor is null ? string.Empty : " AND (e.\"TimestampUtc\", e.\"Id\") < (@cursorTs, @cursorId)") +
            $" AND {where.Condition} {NewestFirst} LIMIT @limit";
        var parameters = RangeParameters(exact.FromUtc, exact.ToUtc).Concat(where.CreateParameters()).ToList();
        parameters.Add(Parameter("limit", NpgsqlDbType.Integer, pageSize + 1));
        if (cursor is { } c)
        {
            parameters.Add(Parameter("cursorTs", NpgsqlDbType.TimestampTz, c.TimestampUtc));
            parameters.Add(Parameter("cursorId", NpgsqlDbType.Bigint, c.Id));
        }

        var rows = await RunAsync(IsolationLevel.ReadCommitted, () => db.Database.SqlQueryRaw<EntryRow>(sql, [.. parameters]).ToListAsync(ct), ct);
        var entries = rows.Take(pageSize).Select(ToView).ToList();
        var next = rows.Count > pageSize ? EncodeCursor(entries[^1].TimestampUtc, entries[^1].Id) : null;
        return new LogPage(entries, next, exact.FromUtc, exact.ToUtc, Truncated: false);
    }

    public async Task<LogKeyPage> GetKeysAsync(string? filter, DateTime fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var where = LogFilterSql.Translate(LogFilterParser.Parse(filter).ExpressionOrThrow());
        var exact = LogTimeRange.ResolveKeys(fromUtc, toUtc, Now());

        var sql =
            "SELECT e.\"Id\" AS \"Id\", e.\"TimestampUtc\" AS \"TimestampUtc\" FROM \"LogEntries\" e " +
            $"WHERE {InRange} AND {where.Condition} {NewestFirst} LIMIT @limit";
        var parameters = RangeParameters(exact.FromUtc, exact.ToUtc).Concat(where.CreateParameters()).ToList();
        parameters.Add(Parameter("limit", NpgsqlDbType.Integer, MaxKeys + 1));

        var rows = await RunAsync(IsolationLevel.ReadCommitted, () => db.Database.SqlQueryRaw<KeyRow>(sql, [.. parameters]).ToListAsync(ct), ct);
        var keys = rows.Take(MaxKeys).Select(r => new LogEntryKey(r.Id, Utc(r.TimestampUtc))).ToList();
        return new LogKeyPage(keys, exact.FromUtc, exact.ToUtc, rows.Count > MaxKeys);
    }

    public async Task<IReadOnlyList<LogEntryView>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count > MaxIds)
        {
            throw new LogRangeException($"At most {MaxIds} ids can be requested at once.");
        }

        if (ids.Count == 0)
        {
            return [];
        }

        var sql = $"SELECT {EntryColumns} FROM \"LogEntries\" e WHERE e.\"Id\" = ANY(@ids) {NewestFirst}";
        var parameter = Parameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids.Distinct().ToArray());

        var rows = await RunAsync(IsolationLevel.ReadCommitted, () => db.Database.SqlQueryRaw<EntryRow>(sql, parameter).ToListAsync(ct), ct);
        return rows.Select(ToView).ToList();
    }

    public async Task<LogEntryDetailView?> GetEntryAsync(long id, CancellationToken ct)
    {
        var sql =
            $"SELECT {EntryColumns}, e.\"Exception\" AS \"Exception\", e.\"ProcessStartedUtc\" AS \"ProcessStartedUtc\" " +
            "FROM \"LogEntries\" e WHERE e.\"Id\" = @id";
        var parameter = Parameter("id", NpgsqlDbType.Bigint, id);

        var rows = await RunAsync(IsolationLevel.ReadCommitted, () => db.Database.SqlQueryRaw<DetailRow>(sql, parameter).ToListAsync(ct), ct);
        if (rows.Count == 0)
        {
            return null;
        }

        var row = rows[0];
        var view = ToView(row);
        var started = Utc(row.ProcessStartedUtc);
        var uptime = (long)Math.Floor((view.TimestampUtc - started).TotalSeconds);
        return new LogEntryDetailView(view, row.Exception, started, uptime);
    }

    public async Task<LogSummary> GetSummaryAsync(string? filter, LogRangeRequest range, CancellationToken ct)
    {
        var where = LogFilterSql.Translate(LogFilterParser.Parse(filter).ExpressionOrThrow());
        var exact = LogTimeRange.Resolve(range, Now());
        var buckets = LogHistogramBuckets.Compute(exact.FromUtc, exact.ToUtc);

        // One pass over the filtered set: per bucket and level (histogram and level facet), per source, per category.
        var groupsSql =
            "SELECT CASE WHEN GROUPING(f.s) = 0 THEN 1 WHEN GROUPING(f.c) = 0 THEN 2 ELSE 0 END AS \"Kind\", " +
            "f.b AS \"Bucket\", f.l AS \"Level\", f.s AS \"Source\", f.c AS \"Category\", COUNT(*) AS \"Count\" " +
            "FROM (SELECT date_bin(@width, e.\"TimestampUtc\", @origin) AS b, e.\"Level\" AS l, e.\"Source\" AS s, e.\"Category\" AS c " +
            $"FROM \"LogEntries\" e WHERE {InRange} AND {where.Condition}) f " +
            "GROUP BY GROUPING SETS ((f.b, f.l), (f.s), (f.c))";
        var groupParameters = RangeParameters(exact.FromUtc, exact.ToUtc).Concat(where.CreateParameters()).ToList();
        groupParameters.Add(Parameter("width", NpgsqlDbType.Interval, buckets.Width));
        groupParameters.Add(Parameter("origin", NpgsqlDbType.TimestampTz, buckets.BucketFromUtc));

        // One pass over everything in the range, ignoring the filter: per instance its drop-report sums (a
        // filter must not hide lost entries) and its process start.
        var instancesSql =
            "SELECT e.\"Source\" AS \"Source\", e.\"InstanceId\" AS \"InstanceId\", e.\"ProcessStartedUtc\" AS \"ProcessStartedUtc\", " +
            $"{DropSum(LogDropReasonNames.TotalPropertyName, "Dropped")}, {DropSum(LogDropReason.QueueFull, "QueueFull")}, " +
            $"{DropSum(LogDropReason.Budget, "Budget")}, {DropSum(LogDropReason.WriteUnconfirmed, "WriteUnconfirmed")}, " +
            $"{DropSum(LogDropReason.CaptureFailed, "CaptureFailed")}, {DropSum(LogDropReason.Reentrant, "Reentrant")}, " +
            $"{DropSum(LogDropReason.Rejected, "Rejected")} " +
            $"FROM \"LogEntries\" e WHERE {InRange} GROUP BY e.\"Source\", e.\"InstanceId\", e.\"ProcessStartedUtc\"";
        var instanceParameters = RangeParameters(exact.FromUtc, exact.ToUtc).ToList();
        instanceParameters.Add(Parameter("dropCategory", NpgsqlDbType.Varchar, LogCaptureWriter.DropCategory));

        var statusSql =
            "SELECT s.\"Source\" AS \"Source\", s.\"InstanceId\" AS \"InstanceId\", s.\"ProcessStartedUtc\" AS \"ProcessStartedUtc\" " +
            "FROM \"LogCaptureStatus\" s WHERE s.\"ProcessStartedUtc\" >= @from AND s.\"ProcessStartedUtc\" < @to";

        // Repeatable read: counts, facets and drops describe the same snapshot.
        var (groups, instances, statuses) = await RunAsync(
            IsolationLevel.RepeatableRead,
            async () => (
                await db.Database.SqlQueryRaw<GroupRow>(groupsSql, [.. groupParameters]).ToListAsync(ct),
                await db.Database.SqlQueryRaw<InstanceRow>(instancesSql, [.. instanceParameters]).ToListAsync(ct),
                await db.Database.SqlQueryRaw<StatusRow>(statusSql, [.. RangeParameters(exact.FromUtc, exact.ToUtc)]).ToListAsync(ct)),
            ct);

        return BuildSummary(exact, buckets, groups, instances, statuses);
    }

    public async Task<LogCaptureStatusView> ComputeStatusAsync(CancellationToken ct)
    {
        var now = Now();
        var coverageFrom = now - CoverageWindow;

        // The status table holds one row per process instance of the last 7 days; entries are only counted
        // and bounded. One snapshot, so the liveness rows and the error count describe the same moment.
        var (rows, recordedErrors, oldest) = await RunAsync(
            IsolationLevel.RepeatableRead,
            async () => (
                await db.LogCaptureStatuses.AsNoTracking().ToListAsync(ct),
                await db.LogEntries.CountAsync(e => e.Level >= LogEntryLevel.Error && e.TimestampUtc >= coverageFrom, ct),
                await db.LogEntries.MinAsync(e => (DateTime?)e.TimestampUtc, ct)),
            ct);

        // Only the newest instance of a source counts: after a restart the old one may still write heartbeats
        // for a moment, and its fresher flush must not vouch for the new process (Review Focus 2).
        var newest = rows
            .GroupBy(r => r.Source)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.ProcessStartedUtc).ThenByDescending(r => r.InstanceId, StringComparer.Ordinal).First());

        var sources = new List<LogSourceStatus>();
        var incomplete = new List<LogIncompleteSince>();
        var complete = true;
        foreach (var source in new[] { LogSource.Api, LogSource.Worker })
        {
            if (!newest.TryGetValue(source, out var row))
            {
                sources.Add(new LogSourceStatus(source, LogSourceState.Missing, null, null, null));
                complete = false;
                continue;
            }

            var lastFlush = Utc(row.LastFlushUtc);
            var state = lastFlush < now - StaleAfter
                ? LogSourceState.Stale
                : row.CaptureEnabled ? LogSourceState.Healthy : LogSourceState.Disabled;
            sources.Add(new LogSourceStatus(source, state, row.InstanceId, Utc(row.ProcessStartedUtc), lastFlush));

            var marker = Utc(row.LastIncompleteAtUtc);
            if (marker >= coverageFrom)
            {
                complete = false;
                incomplete.Add(new LogIncompleteSince(source, marker));
            }
        }

        var oldestUtc = oldest is { } value ? Utc(value) : (DateTime?)null;
        var overdue = oldestUtc is { } o && o < now - RetentionPolicy.LogEntry - OverdueGrace;
        return new LogCaptureStatusView(
            now,
            sources,
            new LogCoverage(complete, recordedErrors, incomplete),
            new LogRetentionStatus(oldestUtc, overdue));
    }

    /// <summary>
    /// Runs <paramref name="query"/> in a read-only transaction under the statement time limit. The limit is a
    /// validated integer from configuration, never request input; it is passed as a bound parameter to
    /// <c>set_config</c> (transaction-local), because <c>SET</c> itself takes no parameters.
    /// </summary>
    private async Task<T> RunAsync<T>(IsolationLevel isolation, Func<Task<T>> query, CancellationToken ct)
    {
        var timeoutText = options.QueryTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(isolation, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            await db.Database.ExecuteSqlAsync($"SELECT set_config('statement_timeout', {timeoutText}, true)", ct);
            var result = await query();
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested && FindStatementTimeout(exception) is { } timeout)
        {
            throw new LogQueryTimeoutException(timeout);
        }
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private static LogSummary BuildSummary(
        LogExactRange exact,
        LogBucketing buckets,
        List<GroupRow> groups,
        List<InstanceRow> instances,
        List<StatusRow> statuses)
    {
        var info = new int[buckets.Count];
        var warn = new int[buckets.Count];
        var error = new int[buckets.Count];
        var levelCounts = new Dictionary<LogEntryLevel, int>();
        foreach (var row in groups.Where(g => g.Kind == 0))
        {
            var level = (LogEntryLevel)row.Level!.Value;
            var count = checked((int)row.Count);
            levelCounts[level] = levelCounts.GetValueOrDefault(level) + count;
            var index = (int)((Utc(row.Bucket!.Value) - buckets.BucketFromUtc).Ticks / buckets.Width.Ticks);
            var series = level switch
            {
                LogEntryLevel.Information => info,
                LogEntryLevel.Warning => warn,
                _ => error,
            };
            series[index] += count;
        }

        var sourceFacet = groups
            .Where(g => g.Kind == 1)
            .OrderBy(g => g.Source)
            .Select(g => new LogFacetValue(LogViewerTokens.Source((LogSource)g.Source!.Value), checked((int)g.Count)))
            .ToList();
        var levelFacet = levelCounts
            .OrderBy(pair => pair.Key)
            .Select(pair => new LogFacetValue(LogViewerTokens.Level(pair.Key), pair.Value))
            .ToList();
        var categories = groups
            .Where(g => g.Kind == 2)
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Category, StringComparer.Ordinal)
            .ToList();
        var categoryFacet = categories
            .Take(CategoryFacetSize)
            .Select(g => new LogCategoryFacetValue(g.Category!, ShortName(g.Category!), checked((int)g.Count)))
            .ToList();
        var categoryOther = checked((int)categories.Skip(CategoryFacetSize).Sum(g => g.Count));

        var dropped = new LogDropTotals(
            instances.Sum(i => i.Dropped),
            instances.Sum(i => i.QueueFull),
            instances.Sum(i => i.Budget),
            instances.Sum(i => i.WriteUnconfirmed),
            instances.Sum(i => i.CaptureFailed),
            instances.Sum(i => i.Reentrant),
            instances.Sum(i => i.Rejected));

        // Instances started in the range: the status rows, plus instances known only from their entries
        // (status row missing or already expired). One start per instance, oldest first.
        var processStarts = statuses
            .Select(s => new LogProcessStart((LogSource)s.Source, s.InstanceId, Utc(s.ProcessStartedUtc)))
            .Concat(instances.Select(i => new LogProcessStart((LogSource)i.Source, i.InstanceId, Utc(i.ProcessStartedUtc))))
            .Where(p => p.StartedUtc >= exact.FromUtc && p.StartedUtc < exact.ToUtc)
            .DistinctBy(p => (p.Source, p.InstanceId))
            .OrderBy(p => p.StartedUtc)
            .ThenBy(p => p.Source)
            .ThenBy(p => p.InstanceId, StringComparer.Ordinal)
            .ToList();

        var bucketStarts = Enumerable.Range(0, buckets.Count).Select(i => buckets.BucketFromUtc + (buckets.Width * i)).ToList();
        return new LogSummary(
            exact.FromUtc,
            exact.ToUtc,
            buckets.BucketFromUtc,
            buckets.BucketToUtc,
            (int)buckets.Width.TotalSeconds,
            bucketStarts,
            info,
            warn,
            error,
            levelCounts.Values.Sum(),
            new LogFacets(sourceFacet, levelFacet, categoryFacet, categoryOther),
            dropped,
            processStarts);
    }

    private static PostgresException? FindStatementTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static string EncodeCursor(DateTime timestampUtc, long id)
    {
        Span<byte> bytes = stackalloc byte[CursorBytes];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, timestampUtc.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], id);
        return Base64Url.EncodeToString(bytes);
    }

    /// <summary>The cursor is opaque base64url over (ticks, id); anything else is an invalid range request (L8).</summary>
    private static (DateTime TimestampUtc, long Id) DecodeCursor(string cursor)
    {
        var text = cursor.Trim();
        Span<byte> bytes = stackalloc byte[CursorBytes];
        if (text.Length > 32
            || !Base64Url.IsValid(text, out var length)
            || length != CursorBytes
            || !Base64Url.TryDecodeFromChars(text, bytes, out var written)
            || written != CursorBytes)
        {
            throw new LogRangeException("The page cursor is invalid.");
        }

        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var id = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || id <= 0)
        {
            throw new LogRangeException("The page cursor is invalid.");
        }

        return (new DateTime(ticks, DateTimeKind.Utc), id);
    }

    private static IEnumerable<NpgsqlParameter> RangeParameters(DateTime fromUtc, DateTime toUtc) =>
    [
        Parameter("from", NpgsqlDbType.TimestampTz, fromUtc),
        Parameter("to", NpgsqlDbType.TimestampTz, toUtc),
    ];

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object value) => new(name, type) { Value = value };

    // Sum of one reason's count over the drop reports, under the key the writer writes (LogCaptureWriter.BuildDropEntry).
    private static string DropSum(LogDropReason reason, string alias) => DropSum(LogDropReasonNames.PropertyName(reason), alias);

    // Sum of one numeric property of the drop reports; anything that is not a JSON number counts as 0.
    private static string DropSum(string property, string alias) =>
        $"COALESCE(SUM(CASE WHEN jsonb_typeof(e.\"Properties\" -> '{property}') = 'number' THEN (e.\"Properties\" ->> '{property}')::numeric END) " +
        $"FILTER (WHERE e.\"Category\" = @dropCategory), 0)::bigint AS \"{alias}\"";

    private static string ShortName(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 || dot == category.Length - 1 ? category : category[(dot + 1)..];
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static LogEntryView ToView(EntryRow row) =>
        new(
            row.Id,
            Utc(row.TimestampUtc),
            (LogEntryLevel)row.Level,
            (LogSource)row.Source,
            row.InstanceId,
            row.Category,
            row.EventId,
            row.EventName,
            row.Message,
            row.HasException,
            row.ExceptionType,
            row.Properties,
            row.Truncated);

    private class EntryRow
    {
        public long Id { get; set; }

        public DateTime TimestampUtc { get; set; }

        public short Level { get; set; }

        public short Source { get; set; }

        public string InstanceId { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public int? EventId { get; set; }

        public string? EventName { get; set; }

        public string Message { get; set; } = string.Empty;

        public bool HasException { get; set; }

        public string? ExceptionType { get; set; }

        public string? Properties { get; set; }

        public bool Truncated { get; set; }
    }

    private sealed class DetailRow : EntryRow
    {
        public string? Exception { get; set; }

        public DateTime ProcessStartedUtc { get; set; }
    }

    private sealed class KeyRow
    {
        public long Id { get; set; }

        public DateTime TimestampUtc { get; set; }
    }

    private sealed class GroupRow
    {
        public int Kind { get; set; }

        public DateTime? Bucket { get; set; }

        public short? Level { get; set; }

        public short? Source { get; set; }

        public string? Category { get; set; }

        public long Count { get; set; }
    }

    private sealed class InstanceRow
    {
        public short Source { get; set; }

        public string InstanceId { get; set; } = string.Empty;

        public DateTime ProcessStartedUtc { get; set; }

        public long Dropped { get; set; }

        public long QueueFull { get; set; }

        public long Budget { get; set; }

        public long WriteUnconfirmed { get; set; }

        public long CaptureFailed { get; set; }

        public long Reentrant { get; set; }

        public long Rejected { get; set; }
    }

    private sealed class StatusRow
    {
        public short Source { get; set; }

        public string InstanceId { get; set; } = string.Empty;

        public DateTime ProcessStartedUtc { get; set; }
    }
}
