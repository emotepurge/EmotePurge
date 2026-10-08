using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// The figures behind the shared 7TV search-bucket budget and the Twitch-id resolution backoff
/// (design note <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>).
/// </summary>
/// <remarks>
/// Api and Worker bind the same section, and they must agree: each process checks the shared window
/// against the ceiling it knows itself. Bound and validated eagerly in
/// <c>AddEmotePurgeInfrastructure</c>, like <c>ChannelCapacityOptions</c>, so a typo in an environment
/// variable stops the container instead of silently granting zero or unlimited searches.
/// </remarks>
public sealed class SevenTvSearchBudgetOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "SevenTv:SearchBudget";

    /// <summary>
    /// The longest block any observation may impose — six hours, the same plausibility bound the 7TV
    /// client puts on a reset hint: well above the ~1 h lockout measured live, far below anything that
    /// could be an epoch timestamp read in the wrong unit.
    /// </summary>
    public const int MaxBlockSeconds = 6 * 60 * 60;

    /// <summary>
    /// 7TV's search bucket per window, as measured live (<c>x-ratelimit-search-limit: 100</c>). The
    /// ceiling the validation below keeps our own figures under.
    /// </summary>
    public const int SevenTvBucketSize = 100;

    /// <summary>
    /// Requests all consumers together may start within one rolling window. Default 50, half of 7TV's
    /// 100: the margin covers clock and window offsets to 7TV and traffic we cannot see.
    /// </summary>
    public int MaxRequestsPerWindow { get; set; } = 50;

    /// <summary>The rolling window, in seconds. Default 60, matching 7TV's bucket.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// The Worker's Twitch-id resolution may use at most this many of
    /// <see cref="MaxRequestsPerWindow"/>; the difference is the leaderboard's reserve. Default 40,
    /// which leaves ten.
    /// </summary>
    public int ChannelIdentityMaxRequestsPerWindow { get; set; } = 40;

    /// <summary>
    /// A response reporting this many remaining requests or fewer blocks the bucket until 7TV's reset
    /// — something we do not count is draining it. Default 10; 0 disables the early block and leaves
    /// only an actual rate limit.
    /// </summary>
    public int LowWatermark { get; set; } = 10;

    /// <summary>How long a rate limit blocks the bucket when 7TV gave no reset hint. Default one hour.</summary>
    public int DefaultLockoutSeconds { get; set; } = 3600;

    /// <summary>The first retry delay after a resolution that found no usable Twitch id. Default 60 s.</summary>
    public int ResolutionBackoffBaseSeconds { get; set; } = 60;

    /// <summary>The ceiling the doubling retry delay stops at. Default one hour.</summary>
    public int ResolutionBackoffMaxSeconds { get; set; } = 3600;

    /// <summary>The rolling window as a span.</summary>
    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);

    /// <summary>
    /// The per-consumer share of the window. The leaderboard may use all of it: its own in-process
    /// lid of ten requests an hour already sits in front.
    /// </summary>
    public int ShareOf(SevenTvSearchConsumer consumer) => consumer switch
    {
        SevenTvSearchConsumer.ChannelIdentity => ChannelIdentityMaxRequestsPerWindow,
        SevenTvSearchConsumer.Leaderboard => MaxRequestsPerWindow,
        _ => throw new ArgumentOutOfRangeException(nameof(consumer), consumer, "Unknown SevenTvSearchConsumer."),
    };

    /// <summary>Throws unless every figure is usable.</summary>
    public void Validate()
    {
        if (MaxRequestsPerWindow <= 1 || MaxRequestsPerWindow >= SevenTvBucketSize)
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaxRequestsPerWindow must be between 2 and {SevenTvBucketSize - 1} (7TV's bucket holds {SevenTvBucketSize}), got {MaxRequestsPerWindow}.");
        }

        if (WindowSeconds < 60)
        {
            throw new InvalidOperationException(
                $"{SectionName}:WindowSeconds must be at least 60 (7TV's own window), got {WindowSeconds}.");
        }

        // Strictly below the total: an identity share equal to it would leave the leaderboard no
        // reserve, and a stuck-channel storm could then lock it out entirely.
        if (ChannelIdentityMaxRequestsPerWindow <= 0 || ChannelIdentityMaxRequestsPerWindow >= MaxRequestsPerWindow)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ChannelIdentityMaxRequestsPerWindow must be at least 1 and below MaxRequestsPerWindow ({MaxRequestsPerWindow}), got {ChannelIdentityMaxRequestsPerWindow}.");
        }

        // Below what our own permitted traffic can leave in 7TV's bucket of 100: otherwise a window
        // we filled ourselves, within our own ceiling, would read as "someone else is draining it"
        // and block every consumer until the reset.
        if (LowWatermark < 0 || LowWatermark >= SevenTvBucketSize - MaxRequestsPerWindow)
        {
            throw new InvalidOperationException(
                $"{SectionName}:LowWatermark must be at least 0 and below {SevenTvBucketSize} - MaxRequestsPerWindow ({SevenTvBucketSize - MaxRequestsPerWindow}), got {LowWatermark}.");
        }

        if (DefaultLockoutSeconds < 60 || DefaultLockoutSeconds > MaxBlockSeconds)
        {
            throw new InvalidOperationException(
                $"{SectionName}:DefaultLockoutSeconds must be between 60 and {MaxBlockSeconds}, got {DefaultLockoutSeconds}.");
        }

        if (ResolutionBackoffBaseSeconds <= 0 || ResolutionBackoffMaxSeconds < ResolutionBackoffBaseSeconds)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ResolutionBackoffBaseSeconds must be positive and not above ResolutionBackoffMaxSeconds, got {ResolutionBackoffBaseSeconds}/{ResolutionBackoffMaxSeconds}.");
        }
    }
}
