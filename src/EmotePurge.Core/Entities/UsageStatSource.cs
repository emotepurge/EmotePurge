namespace EmotePurge.Core.Entities;

/// <summary>
/// Where a <see cref="UsageStat"/> row came from. Persisted as its integer value
/// (<c>UsageStats.Source</c>), so the numbers are the contract and must never be renumbered.
/// </summary>
public enum UsageStatSource
{
    /// <summary>Counted by the live chat path and written by the usage flush (the default).</summary>
    Live = 0,

    /// <summary>Counted from a third-party chat-log archive by the chat-log backfill (#346).</summary>
    ChatLogArchive = 1
}
