using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>Persists one raw system-load sample.</summary>
public interface ISystemLoadSamplingService
{
    Task RecordAsync(
        SystemLoadSource source,
        Guid processInstanceId,
        SystemLoadSnapshot snapshot,
        SystemLoadWorkloadSnapshot workload,
        CancellationToken cancellationToken = default);
}
