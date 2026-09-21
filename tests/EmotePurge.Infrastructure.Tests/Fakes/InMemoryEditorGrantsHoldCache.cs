using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// <see cref="ISevenTvEditorGrantsHoldCache"/> without Redis and without expiry: a held status stays
/// held for the rest of the test, and every shelf-life the caller asked for is kept for inspection.
/// </summary>
public sealed class InMemoryEditorGrantsHoldCache : ISevenTvEditorGrantsHoldCache
{
    private readonly Dictionary<string, SevenTvLookupStatus> _held = new(StringComparer.Ordinal);
    private readonly List<(string TwitchUserId, SevenTvLookupStatus Status, TimeSpan TimeToLive)> _writes = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<(string TwitchUserId, SevenTvLookupStatus Status, TimeSpan TimeToLive)> Writes
    {
        get
        {
            lock (_gate)
            {
                return [.. _writes];
            }
        }
    }

    public Task<SevenTvLookupStatus?> TryGetAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_held.TryGetValue(twitchUserId, out var status) ? status : (SevenTvLookupStatus?)null);
        }
    }

    public Task SetAsync(string twitchUserId, SevenTvLookupStatus status, TimeSpan timeToLive, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _held[twitchUserId] = status;
            _writes.Add((twitchUserId, status, timeToLive));
        }

        return Task.CompletedTask;
    }
}
