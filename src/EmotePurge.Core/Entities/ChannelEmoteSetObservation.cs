namespace EmotePurge.Core.Entities;

// The vocabulary of ChannelEmoteSetObservation.ClosedBy — why an open interval was closed.
// Constants rather than an enum because the value is persisted and one literal carries a load-
// bearing meaning outside this table: the AddUsageStatEmoteSetId migration's Down safeguard (#200)
// treats any row with ClosedBy == SetSwitch as proof that it was written after that migration ran,
// because the migration's own seed (spec section 4.3) never assigns that value — only the live
// emoteSetSwitched sync path does. Whoever writes a new closer here must not repurpose SetSwitch for
// anything the migration itself could produce, or that safeguard locks itself out.
public static class ChannelEmoteSetObservationClosedBy
{
    public const string SetSwitch = "set-switch";
    public const string Leave = "leave";
    public const string Rename = "rename";
    public const string Merge = "merge";
    public const string Migration = "migration";
}

// An interval during which EmotePurge observed a given 7TV emote set as this channel's active set.
// ObservedFromUtc/ObservedToUtc record *when we noticed*, not when the switch actually happened
// (spec section 4.3) — the timestamp is our detection, never a claim about 7TV's own history. A
// channel has at most one open row (ObservedToUtc == null) at a time; AppDbContext enforces that
// with a partial unique index rather than application code, so a race between two writers fails at
// the database instead of producing two open rows. No inverse navigation on Channel, same as
// ChannelLiveDay (AppDbContext.cs) — nothing navigates from a channel to its observations, every
// reader queries this table directly by ChannelId.
public class ChannelEmoteSetObservation
{
    public long Id { get; set; }
    public string ChannelId { get; set; } = string.Empty;
    public string SevenTvEmoteSetId { get; set; } = string.Empty;
    public DateTime ObservedFromUtc { get; set; }
    public DateTime? ObservedToUtc { get; set; }

    // One of ChannelEmoteSetObservationClosedBy's constants once closed; null while the row is open.
    public string? ClosedBy { get; set; }

    public Channel Channel { get; set; } = null!;
}
