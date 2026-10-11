using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>Workload values shared by hosts; Worker-only counters remain unknown in the Api.</summary>
public sealed class InfrastructureSystemLoadWorkloadSource(IChannelService channelService) : ISystemLoadWorkloadSource
{
    public async Task<SystemLoadWorkloadSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var activeChannels = await channelService.ListActiveChannelNamesAsync(cancellationToken);
        return new SystemLoadWorkloadSnapshot(activeChannels.Count, null, null, null);
    }
}
