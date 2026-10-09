namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// Bound from configuration section <c>ChatLogBackfill:*</c> — same shape as <see cref="RetentionOptions"/>:
/// a plain POCO, bound and validated once at startup, registered as a singleton and read by Api and
/// Worker. The block length (7 days) is deliberately a constant, not a key.
/// </summary>
public sealed class ChatLogBackfillOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    public const string SectionName = "ChatLogBackfill";

    /// <summary>
    /// The feature flag. <c>false</c> by default: the Api answers the backfill routes with 404
    /// <c>backfill_disabled</c> and the worker loop exits after one log line.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Worker-side spacing between archive requests in seconds; the effective spacing is the larger of
    /// this and <c>ChatLogArchive:RequestDelay</c>. Default 10.
    /// </summary>
    public int RequestDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Byte cap per block (decompressed). Exceeding it ends the run with <c>block_too_large</c>. It
    /// bounds transfer volume and time, not memory (the bounded line scanner does that). Default 256.
    /// </summary>
    public int MaxBlockMegabytes { get; set; } = 256;

    /// <summary>Cap on one 429 pause in seconds. Default 900.</summary>
    public int MaxRetryAfterSeconds { get; set; } = 900;

    /// <summary>Transport attempts per block before the run fails. Default 3.</summary>
    public int TransportRetries { get; set; } = 3;

    /// <summary>Consecutive 429 pauses on one block before the run fails with <c>rate_limited</c>. Default 10.</summary>
    public int MaxConsecutivePauses { get; set; } = 10;

    /// <summary>Seconds between convergence polls when no signal arrives. Default 60.</summary>
    public int IdlePollSeconds { get; set; } = 60;

    /// <summary>Seconds between the per-run status monitor's polls. Default 2.</summary>
    public int CancelPollSeconds { get; set; } = 2;

    /// <summary>
    /// Throws unless every value is usable. Called during startup by <c>AddEmotePurgeInfrastructure</c>,
    /// so a typo in an environment variable stops the container with a readable message.
    /// </summary>
    public void Validate()
    {
        Require(nameof(RequestDelaySeconds), RequestDelaySeconds);
        Require(nameof(MaxBlockMegabytes), MaxBlockMegabytes);
        Require(nameof(TransportRetries), TransportRetries);
    }

    private static void Require(string key, int value)
    {
        if (value < 1)
        {
            throw new InvalidOperationException(
                $"Invalid chat-log backfill configuration: '{SectionName}:{key}' must be at least 1, but is {value}.");
        }
    }
}
