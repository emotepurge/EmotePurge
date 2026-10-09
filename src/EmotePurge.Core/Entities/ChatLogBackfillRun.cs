namespace EmotePurge.Core.Entities;

/// <summary>
/// Lifecycle of a <see cref="ChatLogBackfillRun"/>. Persisted as the lowercase name
/// (<c>queued</c>, <c>running</c>, ...), guarded by a CHECK constraint — the strings are the contract.
/// </summary>
public enum ChatLogBackfillRunStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// One request to fill a channel's usage statistics backwards from a third-party chat-log archive
/// (#346). A global, strictly sequential queue ordered by <see cref="Id"/>; at most one active
/// (queued, running or paused) run per channel, enforced by a partial unique index. Terminal rows
/// are never reused — a re-run is a new row. Every status transition is a conditional UPDATE on the
/// expected status, so a lost race affects zero rows instead of overwriting a newer state.
/// <para>
/// The requester is a snapshot string pair, not a foreign key (like <see cref="AuditLogEntry"/>):
/// account deletion pseudonymises it, and a cascade from <c>Users</c> would delete the record.
/// </para>
/// </summary>
public class ChatLogBackfillRun
{
    public long Id { get; set; }

    public string ChannelId { get; set; } = string.Empty;

    public ChatLogBackfillRunStatus Status { get; set; } = ChatLogBackfillRunStatus.Queued;

    // 1, 3 or 6 (CHECK).
    public int RequestedMonths { get; set; }

    // First day filled (inclusive) and exclusive end (the channel's CreatedAt UTC date), both frozen
    // at request time.
    public DateOnly WindowFrom { get; set; }
    public DateOnly WindowTo { get; set; }

    public int WeeksTotal { get; set; }

    // Blocks committed. The resume pointer is WindowFrom + 7 * WeeksDone; there is no separate column.
    public int WeeksDone { get; set; }

    // The 7TV set the user chose; the snapshot rows belong to it.
    public string EmoteSetId { get; set; } = string.Empty;

    // The set's name as 7TV reported it at request time: a display snapshot, never an identity.
    public string? EmoteSetName { get; set; }

    // ChatLogArchive:BaseUrl as configured on the Api at request time; the worker refuses a run whose
    // value differs from its own configuration.
    public string ArchiveBaseUrl { get; set; } = string.Empty;

    public string RequestedByTwitchUserId { get; set; } = string.Empty;
    public string RequestedByLogin { get; set; } = string.Empty;

    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? PausedUntilUtc { get; set; }

    // Consecutive 429 pauses on the current block; reset to 0 on each committed block.
    public int PauseCount { get; set; }

    // Transport attempts on the current block; reset to 0 on each committed block.
    public int BlockAttempts { get; set; }

    // Decompressed bytes of every archive read the worker could book, and PRIVMSG lines the client
    // parsed in committed blocks (a count, no identity).
    public long BytesReceived { get; set; }
    public long MessagesRead { get; set; }

    // Run-level failure vocabulary, set with failed / cancelled-by-system.
    public string? ErrorCode { get; set; }
    public int? ErrorHttpStatus { get; set; }

    public Channel Channel { get; set; } = null!;
    public List<ChatLogBackfillRunEmote> Emotes { get; set; } = [];
}
