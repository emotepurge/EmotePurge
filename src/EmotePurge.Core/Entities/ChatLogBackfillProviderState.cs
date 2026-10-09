namespace EmotePurge.Core.Entities;

/// <summary>
/// The archive's rate-limit cooldown, a single row (<c>Id = 1</c>, seeded by the migration) that is
/// independent of any run: a 429 pauses the whole provider, and cancelling, failing or purging the
/// paused run must not lift it — only time does. State, not data: the migration's <c>Down</c> drops it
/// without a guard.
/// </summary>
public class ChatLogBackfillProviderState
{
    public int Id { get; set; } = 1;

    public DateTime? CooldownUntilUtc { get; set; }

    public DateTime? LastRateLimitedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
