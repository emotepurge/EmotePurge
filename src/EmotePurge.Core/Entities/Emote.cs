namespace EmotePurge.Core.Entities;

public class Emote
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    // 7TV ObjectID (24-hex string). Not the PK: the same 7TV emote can be
    // active in multiple channels at once, so uniqueness is scoped per channel
    // via the (ChannelId, SevenTvEmoteId) index below instead of globally.
    public string SevenTvEmoteId { get; set; } = string.Empty;

    public string ChannelId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public bool IsArchived { get; set; }

    // When the emote was (last) archived — written at every archiving site, cleared on un-archive.
    // Null on an archived row means "archived before this column existed, date unknown": there is
    // deliberately no backfill (LastSyncedAt is not a reliable proxy — the REST reconcile path
    // never stamped it), same "null = unknown, never guessed" convention as FirstSeenAt.
    public DateTime? ArchivedAt { get; set; }

    // When the emote entered the channel's 7TV set, taken from the set entry's own timestamp rather
    // than from when we first saw it — so it is correct for emotes that predate this column too.
    // Null means 7TV did not report one; consumers must read that as "unknown", never as "new".
    public DateTime? FirstSeenAt { get; set; }
    public DateTime LastSyncedAt { get; set; } = DateTime.UtcNow;

    // When the row last began as a member of the active set — stamped on creation and on every
    // un-archive, never cleared. Null on rows that predate this column means "unknown", which reads
    // as "older than any credibility window", so a REST-observed leave of such a row is trusted.
    public DateTime? LastEnteredSetAtUtc { get; set; }

    public Channel Channel { get; set; } = null!;
    public ICollection<UsageStat> UsageStats { get; set; } = new List<UsageStat>();
}
