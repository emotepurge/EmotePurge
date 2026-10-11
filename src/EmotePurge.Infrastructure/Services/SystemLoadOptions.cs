namespace EmotePurge.Infrastructure.Services;

/// <summary>Configuration for system-load sampling and hourly rollup.</summary>
public sealed class SystemLoadOptions
{
    public const string SectionName = "SystemLoad";
    public const int MaximumIntervalSeconds = 3_600;

    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public int RollupGraceMinutes { get; set; } = 5;

    public void Validate()
    {
        if (IntervalSeconds is <= 0 or > MaximumIntervalSeconds)
        {
            throw new InvalidOperationException(
                $"Invalid system-load configuration: '{SectionName}:IntervalSeconds' must be between 1 and {MaximumIntervalSeconds}.");
        }

        if (RollupGraceMinutes < 0)
        {
            throw new InvalidOperationException($"Invalid system-load configuration: '{SectionName}:RollupGraceMinutes' must not be negative.");
        }
    }
}
