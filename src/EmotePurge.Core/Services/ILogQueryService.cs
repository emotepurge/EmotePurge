using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// Read side of the admin log viewer (spec section 8): pages, reconcile keys, targeted reloads by id, the
/// detail of one entry and the summary (histogram, facets, drops, process starts). Every call runs in one
/// read-only transaction with a statement time limit; exceeding it throws
/// <see cref="LogQueryTimeoutException"/>. An invalid filter throws <see cref="LogFilterSyntaxException"/>,
/// an invalid range, cursor or id list <see cref="LogRangeException"/>. Callers run it inside the capture's
/// suppression scope so the viewer's own EF and Npgsql diagnostics are never captured.
/// </summary>
public interface ILogQueryService
{
    /// <summary>
    /// One page, newest first by <c>(TimestampUtc, Id)</c>, within the exact range. <paramref name="before"/>
    /// is the opaque cursor of the previous page (<see cref="LogPage.NextCursor"/>); blank means the first
    /// page. <paramref name="limit"/> is clamped: 0 or less becomes 100, more than 200 becomes 200.
    /// </summary>
    Task<LogPage> GetPageAsync(string? filter, LogRangeRequest range, string? before, int limit, CancellationToken ct);

    /// <summary>
    /// Reconcile keys (<c>keysOnly</c>): the ids and timestamps of every matching entry from
    /// <paramref name="fromUtc"/> to <paramref name="toUtc"/> (or the request time), newest first, at most
    /// 2,500; more set <see cref="LogKeyPage.Truncated"/>.
    /// </summary>
    Task<LogKeyPage> GetKeysAsync(string? filter, DateTime fromUtc, DateTime? toUtc, CancellationToken ct);

    /// <summary>
    /// Exactly the requested entries that still exist, newest first — without a time range and without a
    /// filter (the ids come from the filtered key query). At most 200 ids, otherwise <see cref="LogRangeException"/>.
    /// </summary>
    Task<IReadOnlyList<LogEntryView>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct);

    /// <summary>One entry with its exception text, or <c>null</c> when it does not exist (any more).</summary>
    Task<LogEntryDetailView?> GetEntryAsync(long id, CancellationToken ct);

    /// <summary>Histogram, facets, drop totals and process starts of the filtered set in the range.</summary>
    Task<LogSummary> GetSummaryAsync(string? filter, LogRangeRequest range, CancellationToken ct);

    /// <summary>
    /// The uncached capture status: liveness per source, 24 h coverage and retention. Callers go through
    /// <see cref="ILogStatusReader"/>, which caches and enters the suppression scope.
    /// </summary>
    Task<LogCaptureStatusView> ComputeStatusAsync(CancellationToken ct);
}

/// <summary>A captured entry as lists show it; the exception text is only in <see cref="LogEntryDetailView"/>.</summary>
public sealed record LogEntryView(
    long Id,
    DateTime TimestampUtc,
    LogEntryLevel Level,
    LogSource Source,
    string InstanceId,
    string Category,
    int? EventId,
    string? EventName,
    string Message,
    bool HasException,
    string? ExceptionType,
    string? PropertiesJson,
    bool Truncated);

/// <summary>One entry with its exception, its process start and the uptime in whole seconds at the time it was logged.</summary>
public sealed record LogEntryDetailView(LogEntryView Entry, string? Exception, DateTime ProcessStartedUtc, long UptimeSeconds);

/// <summary>
/// One page within the exact bounds <see cref="RangeFromUtc"/>/<see cref="RangeToUtc"/>, which a client
/// sends back as absolute <c>from</c>/<c>to</c> with the cursor. <see cref="NextCursor"/> is <c>null</c> on
/// the last page. <see cref="Truncated"/> is always <c>false</c> for a page (the cursor carries "more"); it
/// is part of the record because the endpoint's list response shares one shape.
/// </summary>
public sealed record LogPage(IReadOnlyList<LogEntryView> Entries, string? NextCursor, DateTime RangeFromUtc, DateTime RangeToUtc, bool Truncated);

/// <summary>Reconcile keys, newest first, within the exact bounds; <see cref="Truncated"/> when more than 2,500 matched.</summary>
public sealed record LogKeyPage(IReadOnlyList<LogEntryKey> Keys, DateTime RangeFromUtc, DateTime RangeToUtc, bool Truncated);

public sealed record LogEntryKey(long Id, DateTime TimestampUtc);

/// <summary>
/// Summary of the filtered set. <see cref="RangeFromUtc"/>/<see cref="RangeToUtc"/> are the exact bounds;
/// <see cref="BucketFromUtc"/>/<see cref="BucketToUtc"/> the snapped histogram bounds with
/// <see cref="BucketStartsUtc"/> as bucket starts. <see cref="Info"/>, <see cref="Warn"/> and <see cref="Error"/>
/// (Error and Critical) count per bucket, only entries inside the exact bounds, empty buckets 0.
/// <see cref="Dropped"/> ignores the filter; <see cref="ProcessStarts"/> lists instances started in the range.
/// </summary>
public sealed record LogSummary(
    DateTime RangeFromUtc,
    DateTime RangeToUtc,
    DateTime BucketFromUtc,
    DateTime BucketToUtc,
    int BucketSeconds,
    IReadOnlyList<DateTime> BucketStartsUtc,
    IReadOnlyList<int> Info,
    IReadOnlyList<int> Warn,
    IReadOnlyList<int> Error,
    int Total,
    LogFacets Facets,
    LogDropTotals Dropped,
    IReadOnlyList<LogProcessStart> ProcessStarts);

/// <summary>
/// Facets over the filtered set in the range. <see cref="Source"/> and <see cref="Level"/> carry the wire
/// tokens of <see cref="LogViewerTokens"/> in enum order, only values with entries. <see cref="Category"/> is
/// the top 15 by count (ties by name); <see cref="CategoryOther"/> is the number of entries in all other categories.
/// </summary>
public sealed record LogFacets(IReadOnlyList<LogFacetValue> Source, IReadOnlyList<LogFacetValue> Level, IReadOnlyList<LogCategoryFacetValue> Category, int CategoryOther);

public sealed record LogFacetValue(string Value, int Count);

/// <summary>A category facet value: the full name, its last segment after a dot (<see cref="Short"/>) and the count.</summary>
public sealed record LogCategoryFacetValue(string Value, string Short, int Count);

/// <summary>
/// Sums of the synthetic drop reports (<c>EmotePurge.LogCapture</c>) in the range, independent of the filter.
/// <see cref="Total"/> is the sum of their <c>dropped</c> property. <see cref="WriteUnconfirmed"/> entries are
/// only <em>possibly</em> lost: a batch without a confirmed commit may still have been stored, so the same
/// entries can be in the list and counted here — consumers must word it as "possibly not stored".
/// </summary>
public sealed record LogDropTotals(long Total, long QueueFull, long Budget, long WriteUnconfirmed, long CaptureFailed, long Reentrant, long Rejected);

public sealed record LogProcessStart(LogSource Source, string InstanceId, DateTime StartedUtc);

/// <summary>The wire tokens of sources and levels: what the endpoints emit and what the filter language accepts.</summary>
public static class LogViewerTokens
{
    public static string Source(LogSource source) => source switch
    {
        LogSource.Api => "api",
        LogSource.Worker => "worker",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown log source."),
    };

    public static string Level(LogEntryLevel level) => level switch
    {
        LogEntryLevel.Information => "info",
        LogEntryLevel.Warning => "warn",
        LogEntryLevel.Error => "error",
        LogEntryLevel.Critical => "crit",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown log level."),
    };
}
