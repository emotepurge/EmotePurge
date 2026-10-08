using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// In-memory stand-in for <see cref="IForeignChannelIdentityCache"/> — a plain dictionary with no
/// shelf-life of its own, since every test that uses it only cares whether a value was written and
/// whether a later read observes it, never how long it would survive in Redis. Same shape as
/// <see cref="InMemoryEditorGrantsHoldCache"/>.
/// </summary>
public sealed class InMemoryForeignChannelIdentityCache : IForeignChannelIdentityCache
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> TryGetTwitchUserIdAsync(string normalizedChannelName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(normalizedChannelName, out var value) ? value : null);

    public Task SetTwitchUserIdAsync(string normalizedChannelName, string twitchUserId, CancellationToken cancellationToken = default)
    {
        _values[normalizedChannelName] = twitchUserId;
        return Task.CompletedTask;
    }
}
