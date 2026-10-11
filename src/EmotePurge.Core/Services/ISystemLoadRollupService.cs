namespace EmotePurge.Core.Services;

/// <summary>Recomputes closable system-load hours and fills missing hours in the raw retention window.</summary>
public interface ISystemLoadRollupService
{
    Task<int> RecomputeClosableHoursAsync(
        DateTime nowUtc,
        TimeSpan gracePeriod,
        int samplingIntervalSeconds,
        CancellationToken cancellationToken = default);
}
