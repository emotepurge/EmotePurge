using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>The exact bounds a list or reconcile query runs on; <paramref name="IsRelative"/> is true when the end is the request time.</summary>
internal sealed record LogExactRange(DateTime FromUtc, DateTime ToUtc, bool IsRelative);

/// <summary>Validates and resolves the time range of a log request (spec section 8.2). Lists use the exact bounds; only the histogram snaps (see <see cref="LogHistogramBuckets"/>).</summary>
internal static class LogTimeRange
{
    public const string DefaultPreset = "24h";

    public static readonly TimeSpan MaxSpan = TimeSpan.FromDays(8);
    public static readonly TimeSpan MaxFutureOffset = TimeSpan.FromHours(12);

    public static LogExactRange Resolve(LogRangeRequest request, DateTime nowUtc)
    {
        var hasAbsolute = request.FromUtc is not null || request.ToUtc is not null;
        if (hasAbsolute)
        {
            if (request.Range is not null)
            {
                throw new LogRangeException("range cannot be combined with from/to.");
            }
            if (request.FromUtc is null || request.ToUtc is null)
            {
                throw new LogRangeException("from and to must be given together.");
            }
            return Validate(ToUtc(request.FromUtc.Value), ToUtc(request.ToUtc.Value), nowUtc, isRelative: false);
        }

        var span = (request.Range ?? DefaultPreset) switch
        {
            "1h" => TimeSpan.FromHours(1),
            "6h" => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => throw new LogRangeException("Unknown range preset."),
        };
        var now = ToUtc(nowUtc);
        return new LogExactRange(now - span, now, IsRelative: true);
    }

    /// <summary>The <c>keysOnly</c> reconcile: <c>to</c> is optional and defaults to the request time; the span and future bounds still apply.</summary>
    public static LogExactRange ResolveKeys(DateTime fromUtc, DateTime? toUtc, DateTime nowUtc) =>
        Validate(ToUtc(fromUtc), toUtc is null ? ToUtc(nowUtc) : ToUtc(toUtc.Value), nowUtc, isRelative: toUtc is null);

    private static LogExactRange Validate(DateTime from, DateTime to, DateTime nowUtc, bool isRelative)
    {
        if (from >= to)
        {
            throw new LogRangeException("from must be before to.");
        }
        if (to - from > MaxSpan)
        {
            throw new LogRangeException("The range spans more than 8 days.");
        }
        if (to > ToUtc(nowUtc) + MaxFutureOffset)
        {
            throw new LogRangeException("to is more than 12 hours in the future.");
        }
        return new LogExactRange(from, to, isRelative);
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
