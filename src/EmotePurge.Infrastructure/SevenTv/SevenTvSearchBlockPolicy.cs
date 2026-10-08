using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// Decides whether one 7TV search response blocks the shared bucket, and for how long — the pure
/// half of <see cref="ISevenTvSearchBudget.ObserveResponseAsync"/>, kept apart from Redis so every
/// branch is testable without a container.
/// </summary>
/// <remarks>
/// <para>
/// <b>A rate limit</b> (either disguise) blocks until the best reset hint: the client's
/// <see cref="SevenTvSearchObservation.RetryAfter"/> first — it already prefers the search-reset
/// header over the GraphQL hint — then the bare reset header, then
/// <see cref="SevenTvSearchBudgetOptions.DefaultLockoutSeconds"/>. Clamped to at least a minute,
/// because a lockout we were told about is never shorter than 7TV's own window.
/// </para>
/// <para>
/// <b>A nearly empty bucket</b> without a rate limit — <see cref="SevenTvSearchObservation.Remaining"/>
/// at or below <see cref="SevenTvSearchBudgetOptions.LowWatermark"/> — blocks until the reset header
/// says the window turns over. Our own ceiling is half the bucket, so seeing it this low means
/// traffic we do not count; stopping until the reset costs at most a minute, overdrawing costs an
/// hour. Without a reset header there is nothing to block until, and no block is set.
/// </para>
/// <para>Every duration is capped at <see cref="SevenTvSearchBudgetOptions.MaxBlockSeconds"/>.</para>
/// </remarks>
public static class SevenTvSearchBlockPolicy
{
    private static readonly TimeSpan MinRateLimitBlock = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan MinWatermarkBlock = TimeSpan.FromSeconds(1);

    /// <summary>The block this observation imposes, or <c>null</c> for none.</summary>
    public static SevenTvSearchBlock? BlockFor(SevenTvSearchObservation observation, SevenTvSearchBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(options);

        if (observation.RateLimited)
        {
            var hint = observation.RetryAfter
                ?? PositiveSeconds(observation.ResetSeconds)
                ?? TimeSpan.FromSeconds(options.DefaultLockoutSeconds);
            return new SevenTvSearchBlock(Clamp(hint, MinRateLimitBlock), SevenTvSearchBlockCause.RateLimited);
        }

        if (observation.Remaining is { } remaining
            && remaining <= options.LowWatermark
            && PositiveSeconds(observation.ResetSeconds) is { } reset)
        {
            return new SevenTvSearchBlock(Clamp(reset, MinWatermarkBlock), SevenTvSearchBlockCause.LowWatermark);
        }

        return null;
    }

    private static TimeSpan? PositiveSeconds(int? seconds) =>
        seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;

    private static TimeSpan Clamp(TimeSpan value, TimeSpan floor)
    {
        var ceiling = TimeSpan.FromSeconds(SevenTvSearchBudgetOptions.MaxBlockSeconds);
        if (value < floor)
        {
            return floor;
        }

        return value > ceiling ? ceiling : value;
    }
}

/// <summary>One block decision: how long, and why.</summary>
public readonly record struct SevenTvSearchBlock(TimeSpan Duration, SevenTvSearchBlockCause Cause);
