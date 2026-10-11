using System.Globalization;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Parses selected Linux proc files without accessing the filesystem.</summary>
public static class ProcFileParser
{
    public static ProcCpuCounters? ParseCpuStat(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static value => value.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 9)
        {
            return null;
        }

        Span<long> values = stackalloc long[8];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryParseNonNegativeLong(parts[index + 1], out values[index]))
            {
                return null;
            }
        }

        if (!TrySum(values, out var totalJiffies)
            || !TrySum([values[0], values[1], values[2], values[5], values[6], values[7]], out var busyJiffies))
        {
            return null;
        }

        return new ProcCpuCounters(busyJiffies, totalJiffies, values[4], values[7]);
    }

    public static ProcLoadAverage ParseLoadAverage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ProcLoadAverage(null, null, null);
        }

        var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new ProcLoadAverage(
            parts.Length > 0 ? ParseFiniteDouble(parts[0]) : null,
            parts.Length > 1 ? ParseFiniteDouble(parts[1]) : null,
            parts.Length > 2 ? ParseFiniteDouble(parts[2]) : null);
    }

    public static ProcMemoryInfo ParseMemoryInfo(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ProcMemoryInfo(null, null, null, null);
        }

        var values = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator];
            if (name is not ("MemTotal" or "MemAvailable" or "SwapTotal" or "SwapFree"))
            {
                continue;
            }

            values[name] = ParseMemoryValue(line[(separator + 1)..]);
        }

        return new ProcMemoryInfo(
            Get(values, "MemTotal"),
            Get(values, "MemAvailable"),
            Get(values, "SwapTotal"),
            Get(values, "SwapFree"));
    }

    private static long? ParseMemoryValue(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryParseNonNegativeLong(parts[0], out var amount))
        {
            return null;
        }

        return parts[1] switch
        {
            "kB" when amount <= long.MaxValue / 1024 => amount * 1024,
            "B" => amount,
            _ => null,
        };
    }

    private static long? Get(IReadOnlyDictionary<string, long?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static double? ParseFiniteDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        && double.IsFinite(parsed)
            ? parsed
            : null;

    private static bool TryParseNonNegativeLong(string value, out long parsed) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;

    private static bool TrySum(ReadOnlySpan<long> values, out long sum)
    {
        sum = 0;
        foreach (var value in values)
        {
            if (sum > long.MaxValue - value)
            {
                sum = 0;
                return false;
            }

            sum += value;
        }

        return true;
    }
}

public sealed record ProcCpuCounters(long BusyJiffies, long TotalJiffies, long IowaitJiffies, long StealJiffies);

public sealed record ProcLoadAverage(double? OneMinute, double? FiveMinutes, double? FifteenMinutes);

public sealed record ProcMemoryInfo(long? MemTotalBytes, long? MemAvailableBytes, long? SwapTotalBytes, long? SwapFreeBytes);
