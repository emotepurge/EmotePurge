namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from configuration section <c>Retention:*</c> — same shape as <see cref="ChannelCapacityOptions"/>:
/// a plain POCO, bound and validated once at startup, registered as a singleton. Holds only the switch
/// and the pacing of the retention job; the periods themselves are <c>RetentionPolicy</c> constants and
/// deliberately not configurable (the privacy policy states them).
/// </summary>
public sealed class RetentionOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "Retention";

    /// <summary>
    /// Whether the job deletes (<c>true</c>) or only counts and logs a warning (<c>false</c>, the
    /// default). A safe default that is loud beats an enforcing one that could be quietly wrong on the
    /// first run against the whole existing database.
    /// </summary>
    public bool Enforce { get; set; }

    /// <summary>Hours between two passes. Default 24.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// Minutes the job waits after the worker's boot recovery before its first pass, so a restart loop
    /// does not begin every start with a pass. Default 10; 0 runs straight after the boot recovery.
    /// </summary>
    public int StartupDelayMinutes { get; set; } = 10;

    /// <summary>
    /// Ceiling on account deletions per pass (each is its own transaction); the rest follow on the next
    /// pass. Bounds the duration of the first enforced pass. Default 100.
    /// </summary>
    public int MaxAccountsPerRun { get; set; } = 100;

    /// <summary>
    /// Throws unless every value is usable. Called during startup by <c>AddEmotePurgeInfrastructure</c>,
    /// so a typo in an environment variable stops the container with a readable message instead of, say,
    /// a zero interval spinning the job or a zero cap silently disabling account deletion.
    /// </summary>
    public void Validate()
    {
        if (IntervalHours <= 0)
        {
            throw Invalid(nameof(IntervalHours), "must be greater than 0", IntervalHours);
        }

        if (StartupDelayMinutes < 0)
        {
            throw Invalid(nameof(StartupDelayMinutes), "must not be negative", StartupDelayMinutes);
        }

        if (MaxAccountsPerRun <= 0)
        {
            throw Invalid(nameof(MaxAccountsPerRun), "must be greater than 0", MaxAccountsPerRun);
        }
    }

    private static InvalidOperationException Invalid(string key, string rule, int value) =>
        new($"Invalid retention configuration: '{SectionName}:{key}' {rule}, but is {value}.");
}
