namespace EmotePurge.Core.Services;

/// <summary>Reads application work counters for one sampling interval.</summary>
public interface ISystemLoadWorkloadSource
{
    Task<SystemLoadWorkloadSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Nullable workload values; null means that the source cannot report the value.</summary>
public sealed record SystemLoadWorkloadSnapshot(
    int? ActiveChannels,
    int? LiveChannels,
    long? ChatLines,
    long? EmoteMatches);
