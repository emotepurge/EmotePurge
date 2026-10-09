namespace EmotePurge.Core.Entities;

// Re-add lock written when a broadcaster purges their own channel's data (#245): while a row exists
// for a Twitch broadcaster id, nobody but a global admin can create or reactivate a channel row for
// that id. Keyed by the immutable numeric Twitch id (never a login, which can be reassigned) and
// deliberately free of a foreign key — the channel row it belongs to is exactly what the purge
// deletes. No retention applies; only an admin-lifted join removes the row.
public class BroadcasterChannelLock
{
    public string TwitchChannelId { get; set; } = string.Empty;
    public DateTime LockedAtUtc { get; set; }
}
