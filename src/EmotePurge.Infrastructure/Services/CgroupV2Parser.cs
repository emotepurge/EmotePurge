using System.Globalization;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Parses selected cgroup v2 files without accessing the filesystem.</summary>
public static class CgroupV2Parser
{
    public static long? ParseCpuStat(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "usage_usec")
            {
                return ParseNonNegativeLong(parts[1]);
            }
        }

        return null;
    }

    public static CgroupCpuLimit ParseCpuMax(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new CgroupCpuLimit(null, null);
        }

        var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return new CgroupCpuLimit(null, null);
        }

        var quota = parts[0] == "max" ? null : ParseNonNegativeLong(parts[0]);
        return new CgroupCpuLimit(quota, ParseNonNegativeLong(parts[1]));
    }

    public static long? ParseMemoryCurrent(string? content) => ParseSingleLong(content);

    public static long? ParseMemoryMax(string? content) =>
        string.Equals(content?.Trim(), "max", StringComparison.Ordinal) ? null : ParseSingleLong(content);

    private static long? ParseSingleLong(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var value = content.Trim();
        return value.Contains(' ') || value.Contains('\t') || value.Contains('\n')
            ? null
            : ParseNonNegativeLong(value);
    }

    private static long? ParseNonNegativeLong(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
}

public sealed record CgroupCpuLimit(long? QuotaMicroseconds, long? PeriodMicroseconds);
