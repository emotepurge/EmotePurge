namespace EmotePurge.Core.Entities;

public class Channel
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? TwitchChannelId { get; set; }
    public string ChannelName { get; set; } = string.Empty;
    public string ActiveEmoteSetId { get; set; } = string.Empty;

    // Slot limit of the active set, as 7TV reports it — a property of the *set*, not the channel,
    // which is why it sits next to ActiveEmoteSetId and is written only where that id is written.
    // Null means 7TV did not report one; consumers must then show no budget rather than assume 1000
    // (7TV subscribers get larger sets).
    public int? ActiveEmoteSetCapacity { get; set; }
    public bool IsBotActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // When the bot last (re-)entered the channel, set only on a join that actually reactivated it.
    // CreatedAt alone would overstate the coverage of our usage data: LeaveAsync keeps the row, so a
    // channel that was left for two months and rejoined yesterday still carries its original
    // CreatedAt — and the resulting tracking gap would stay invisible to anyone reading "we count
    // since". Null means the channel was never left and rejoined, so CreatedAt is the honest answer.
    public DateTime? TrackingResumedAt { get; set; }

    // When a full 7TV REST sync last completed for this channel, whether or not it changed anything.
    // The obvious-looking alternative — MAX(Emote.LastSyncedAt) — answers a different question:
    // emote rows are only stamped when they actually change, so a channel syncing successfully every
    // minute against a set nobody edits reads as "last synced three days ago". That is the exact
    // reading a support case turns on, so the two are now separate columns rather than one number
    // doing double duty. Null means no full sync has ever completed since this column existed.
    public DateTime? LastSyncedAtUtc { get; set; }

    // When the last full 7TV sync attempt finished, successful or not. Deliberately a second column
    // next to LastSyncedAtUtc rather than a reinterpretation of it: the pair is the diagnosis. Equal
    // values mean the last attempt succeeded; a fresh attempt next to an old success means the
    // channel has been failing since then. Without it, a reason written three days ago would read as
    // a statement about right now.
    public DateTime? LastSyncAttemptAtUtc { get; set; }

    // Why that last attempt produced nothing, as one of SevenTvSyncFailureReasons — null when it
    // succeeded. Null plus an empty ActiveEmoteSetId is therefore the honest "no sync has been
    // attempted yet", which is precisely the state the usage page polls on. Cleared by
    // SevenTvSyncService on every successful sync, in the same block that stamps LastSyncedAtUtc, so
    // a channel that fixed its 7TV side stops being told it is broken.
    public string? LastSyncFailureReason { get; set; }

    // When this channel was last deactivated by LeaveAsync, the measuring point for the 180-day
    // retention purge. Null means the channel is active, or was never left, or (for a row that
    // was already inactive when this column was introduced) was backfilled to the migration
    // timestamp rather than left blank — the purge counts from "since we started measuring", not
    // from the true, unrecorded leave date, which is the honest answer given what the row can
    // prove. Nulled again by whatever reactivates the row: CompleteJoinAsync's reactivation branch,
    // and the ChannelIdentityService merge when it makes the survivor active.
    public DateTime? DeactivatedAtUtc { get; set; }

    public ICollection<Emote> Emotes { get; set; } = new List<Emote>();
}
