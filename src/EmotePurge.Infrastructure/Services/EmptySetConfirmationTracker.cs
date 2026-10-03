using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// In-memory <see cref="IEmptySetConfirmationTracker"/>, registered as a singleton (the sync service
/// itself is scoped, so state kept there would die with every scope). Per normalized channel name it
/// keeps the set the streak belongs to, the streak length and the time of the last counted zero.
/// There is deliberately no maximum age: the streak is "zeros in a row" because the caller resets it
/// on every other observation, so a gap between two zeros is only ever a gap without observations.
/// </summary>
public sealed class EmptySetConfirmationTracker(EmptySetConfirmationOptions options, TimeProvider timeProvider)
    : IEmptySetConfirmationTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Streak> _streaks = [];

    public EmptySetVerdict ObserveZero(string channelName, string emoteSetId)
    {
        var key = ChannelName.Normalize(channelName);
        var now = timeProvider.GetUtcNow();
        var spacing = TimeSpan.FromSeconds(options.EmptySetConfirmationSpacingSeconds);

        lock (_lock)
        {
            if (!_streaks.TryGetValue(key, out var streak) || streak.EmoteSetId != emoteSetId)
            {
                streak = new Streak(emoteSetId, 1, now);
                _streaks[key] = streak;
                return Verdict(streak.Count, counted: true);
            }

            if (now - streak.LastCountedAt < spacing)
            {
                return Verdict(streak.Count, counted: false);
            }

            streak = streak with { Count = streak.Count + 1, LastCountedAt = now };
            _streaks[key] = streak;
            return Verdict(streak.Count, counted: true);
        }
    }

    public void Reset(string channelName)
    {
        var key = ChannelName.Normalize(channelName);
        lock (_lock)
        {
            _streaks.Remove(key);
        }
    }

    private EmptySetVerdict Verdict(int count, bool counted) =>
        new(count >= options.EmptySetConfirmations, count, options.EmptySetConfirmations, counted);

    private sealed record Streak(string EmoteSetId, int Count, DateTimeOffset LastCountedAt);
}
