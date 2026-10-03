using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// In-memory <see cref="IEmptySetConfirmationTracker"/>, registered as a singleton (the sync service
/// itself is scoped, so state kept there would die with every scope). Per channel row id it keeps the
/// set the streak belongs to, the streak length and the time of the last counted zero.
/// </summary>
public sealed class EmptySetConfirmationTracker(EmptySetConfirmationOptions options, TimeProvider timeProvider)
    : IEmptySetConfirmationTracker
{
    private const int MaxAgeSpacings = 10;

    private readonly object _lock = new();
    private readonly Dictionary<string, Streak> _streaks = [];

    public EmptySetVerdict ObserveZero(string channelId, string emoteSetId)
    {
        var now = timeProvider.GetUtcNow();
        var spacing = TimeSpan.FromSeconds(options.EmptySetConfirmationSpacingSeconds);

        lock (_lock)
        {
            if (!_streaks.TryGetValue(channelId, out var streak) || streak.EmoteSetId != emoteSetId)
            {
                streak = new Streak(emoteSetId, 1, now);
                _streaks[channelId] = streak;
                return Verdict(streak.Count, counted: true);
            }

            // A streak is "repeated zeros in a row", not "zeros ever seen": once the last counted zero
            // is older than a generous multiple of the spacing, the earlier ones no longer vouch for
            // this one and the streak starts over. Derived from the spacing so there is no knob to
            // tune; with spacing 0 there is no cadence to measure against and the bound is skipped.
            if (spacing > TimeSpan.Zero && now - streak.LastCountedAt > spacing * MaxAgeSpacings)
            {
                streak = new Streak(emoteSetId, 1, now);
                _streaks[channelId] = streak;
                return Verdict(streak.Count, counted: true);
            }

            if (now - streak.LastCountedAt < spacing)
            {
                return Verdict(streak.Count, counted: false);
            }

            streak = streak with { Count = streak.Count + 1, LastCountedAt = now };
            _streaks[channelId] = streak;
            return Verdict(streak.Count, counted: true);
        }
    }

    public void Reset(string channelId)
    {
        lock (_lock)
        {
            _streaks.Remove(channelId);
        }
    }

    private EmptySetVerdict Verdict(int count, bool counted) =>
        new(count >= options.EmptySetConfirmations, count, options.EmptySetConfirmations, counted);

    private sealed record Streak(string EmoteSetId, int Count, DateTimeOffset LastCountedAt);
}
