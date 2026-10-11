namespace EmotePurge.Core.Services;

/// <summary>Reads application work counters for one sampling interval.</summary>
public interface ISystemLoadWorkloadSource
{
    Task<SystemLoadWorkloadSnapshot> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Confirms this workload's cumulative counters after its sample was persisted successfully.</summary>
    void ConfirmRecorded(SystemLoadWorkloadSnapshot workload);
}

/// <summary>
/// Nullable workload values; null means that the source cannot report the value. The total fields
/// are source checkpoints, not persisted values, and are confirmed only after the sample is stored.
/// </summary>
public sealed record SystemLoadWorkloadSnapshot(
    int? ActiveChannels,
    int? LiveChannels,
    long? ChatLines,
    long? EmoteMatches,
    long? ChatLinesTotal = null,
    long? EmoteMatchesTotal = null);
