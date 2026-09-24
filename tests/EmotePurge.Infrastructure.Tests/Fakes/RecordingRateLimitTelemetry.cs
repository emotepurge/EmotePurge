using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// Minimal <see cref="IRateLimitTelemetry"/> that keeps what was written — same shape as
/// <see cref="RecordingLogger{T}"/>. Doubles as a plain no-op sink for tests that only need a valid
/// <see cref="IRateLimitTelemetry"/> to satisfy a constructor and never inspect
/// <see cref="Observations"/> or <see cref="CacheLookups"/> at all.
/// </summary>
public sealed class RecordingRateLimitTelemetry : IRateLimitTelemetry
{
    private readonly List<ProviderResponseObservation> _observations = [];
    private readonly List<(string CacheName, bool Hit)> _cacheLookups = [];

    public IReadOnlyList<ProviderResponseObservation> Observations => _observations;

    public IReadOnlyList<(string CacheName, bool Hit)> CacheLookups => _cacheLookups;

    public Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default)
    {
        _observations.Add(observation);
        return Task.CompletedTask;
    }

    public Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default)
    {
        _cacheLookups.Add((cacheName, hit));
        return Task.CompletedTask;
    }

    public Task<bool> ForgetPartitionAsync(string partition, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
