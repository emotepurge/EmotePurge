namespace EmotePurge.Core.Services;

/// <summary>
/// One redemption of a channel-point reward with text input, as the worker read it from chat (spec K6, plan #486
/// 2.1): the seven fields of the spec and nothing else. The channel is named by Twitch's <c>room-id</c> only, never by
/// a name (plan 2.4, Sol-P1-1).
/// </summary>
/// <param name="RoomId">Twitch's <c>room-id</c> tag: the channel's Twitch id.</param>
/// <param name="RewardId">Twitch's <c>custom-reward-id</c> tag.</param>
/// <param name="MessageId">Twitch's <c>id</c> tag of the message: the dedupe key.</param>
/// <param name="UserId">The redeemer's Twitch id.</param>
/// <param name="Login">The redeemer's login.</param>
/// <param name="Text">The redemption's text, possibly empty.</param>
/// <param name="SentAtUtc">Twitch's <c>tmi-sent-ts</c>.</param>
public sealed record RewardObservation(
    string RoomId,
    string RewardId,
    string MessageId,
    string UserId,
    string Login,
    string Text,
    DateTime SentAtUtc);

/// <summary>What <see cref="IWishCaptureService.ProcessAsync"/> did with an observation.</summary>
public enum WishCaptureOutcome
{
    /// <summary>The redemption of an unknown reward became the candidate of the channel's learning session.</summary>
    CandidateStored,

    /// <summary>The redemption of a known, active reward became a <c>pending</c> wish.</summary>
    WishCreated,

    /// <summary>The channel has decided this message before (dedupe receipt); nothing written.</summary>
    Duplicate,

    /// <summary>The redeemer already has the configured number of open wishes in the channel; nothing written.</summary>
    RequesterLimit,

    /// <summary>The channel already holds <see cref="Entities.WishLimits.MaxPendingPerChannel"/> open wishes; nothing written.</summary>
    ChannelLimit,

    /// <summary>No channel row carries the observation's room id (before or behind the channel lock); nothing written.</summary>
    ChannelUnbound,

    /// <summary>
    /// Everything else that is dropped without a trace: a deactivated channel, a paused reward, an unknown reward
    /// without a session waiting for its candidate, a redemption sent before the session started, a malformed
    /// observation.
    /// </summary>
    Discarded,
}

/// <summary>The counts of one housekeeping pass (<see cref="IWishCaptureService.RunHousekeepingAsync"/>).</summary>
/// <param name="LearningsDeleted">Expired learning rows deleted, with their candidate's text.</param>
/// <param name="ReceiptsDeleted">Dedupe receipts older than <see cref="Entities.WishLimits.CaptureReceiptRetention"/> deleted.</param>
/// <param name="WishesExpired">Open wishes moved to <c>expired</c> (only with the pending expiry).</param>
/// <param name="ChannelsExpired">Channels among them (one <c>wish.changed</c> each).</param>
/// <param name="ChannelsFailed">Channels whose expiry step failed; the others were still decided.</param>
public sealed record WishCaptureHousekeepingResult(
    int LearningsDeleted,
    int ReceiptsDeleted,
    int WishesExpired,
    int ChannelsExpired,
    int ChannelsFailed);

/// <summary>A channel that can get a capture gap (plan 2.7): the persistent set, read from the database.</summary>
/// <param name="ChannelId">The internal channel id; what <see cref="WishCaptureGapRecord.ChannelId"/> names.</param>
/// <param name="TwitchChannelId">The bound Twitch id.</param>
/// <param name="ChannelName">The normalized channel name, the key of the worker's roster.</param>
public sealed record WishCaptureGapChannel(string ChannelId, string TwitchChannelId, string ChannelName);

/// <summary>A closed capture gap to store (<see cref="IWishCaptureService.RecordGapsAsync"/>).</summary>
/// <param name="ChannelId">The internal channel id (<see cref="WishCaptureGapChannel.ChannelId"/>).</param>
/// <param name="Reason">One of <see cref="Entities.WishCaptureGapReason"/>.</param>
public sealed record WishCaptureGapRecord(string ChannelId, DateTime FromUtc, DateTime ToUtc, string Reason);

/// <summary>
/// The worker's side of the K6 chat capture (plan #486 2.4 and 2.7); the manager's side is
/// <see cref="IWishRewardService"/>. Only the worker calls it.
/// <para>
/// <b>No pre-filter anywhere (Sol-P2-5).</b> Every observation is decided on the database: first a read without a
/// lock (no channel, a paused or unknown reward without a waiting session → done, nothing written, no transaction),
/// then, only when something is to be written, a transaction behind the channel row lock in which the binding,
/// the reward and the learning row are read again. The first redemption after a start, a confirm or a reactivation
/// therefore counts at once.
/// </para>
/// <para>
/// <b>Binding (Sol-P1-1).</b> A channel is found only by <c>TwitchChannelId == RoomId</c>, and that equality is checked
/// again behind the lock. A row without a Twitch id, or whose id the identity reconcile changed meanwhile, captures
/// nothing.
/// </para>
/// <para>
/// <b>Dedupe (Sol-P2-6).</b> A content-free receipt <c>(ChannelId, IrcMessageId)</c> is inserted in the same
/// transaction as the candidate or the wish and goes with every refusal's rollback. It hangs on no wish, so neither
/// confirming the learning session nor an account deletion lets the same line create a second wish; the housekeeping
/// removes it after <see cref="Entities.WishLimits.CaptureReceiptRetention"/>.
/// </para>
/// <para>
/// Nothing is audited and nothing logged with text, login or user id (AK64). Every method is independent of both
/// switches; whether the worker calls <see cref="ProcessAsync"/> is the worker's decision (plan 2.3).
/// </para>
/// </summary>
public interface IWishCaptureService
{
    /// <summary>
    /// Decides one observation (plan 2.4, steps 1 to 6). After a commit it publishes <c>wish.changed</c> for the
    /// channel and, for a new wish, starts the wish reconcile of the channel, so the hints stand at once and a slot
    /// already in the window links immediately; a failure of either never changes the answer. Database failures
    /// propagate (the worker retries the same observation).
    /// </summary>
    Task<WishCaptureOutcome> ProcessAsync(RewardObservation observation, CancellationToken cancellationToken = default);

    /// <summary>
    /// One housekeeping pass, independent of both switches (E26): deletes expired learning rows and dedupe receipts
    /// older than <see cref="Entities.WishLimits.CaptureReceiptRetention"/>; with <paramref name="includePendingExpiry"/>
    /// moves every open wish whose <c>StatusChangedAtUtc</c> lies more than the channel's <c>PendingExpiryDays</c> back
    /// to <c>expired</c>, channel by channel behind the channel lock, one <c>wish.changed</c> per channel, no audit.
    /// </summary>
    Task<WishCaptureHousekeepingResult> RunHousekeepingAsync(bool includePendingExpiry, CancellationToken cancellationToken = default);

    /// <summary>
    /// The channels that can get a capture gap (plan 2.7), ordered by name: the bot is active in them, they are not
    /// deactivated, they are bound to a Twitch id and they have at least one active reward.
    /// </summary>
    Task<IReadOnlyList<WishCaptureGapChannel>> ListGapChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>The capture's life stamp ("last surely captured"), or null when it was never written.</summary>
    Task<DateTime?> ReadLastAliveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the life stamp (an upsert of the single state row). The worker calls it only while the capture is
    /// effective and no connection gap is open (plan 2.7, W6), and reads the old stamp before its first write.
    /// </summary>
    Task WriteLastAliveAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores closed gaps in one statement and returns how many rows were written. Skipped silently: a gap shorter
    /// than <see cref="Entities.WishLimits.MinCaptureGap"/>, and a gap of a channel that has no active reward at the
    /// time of writing (or no longer exists). An unknown reason throws <see cref="ArgumentException"/> before anything
    /// is written. Publishes <c>wish.changed</c> once per channel that got a row, best effort: once the insert has
    /// committed, a failure of that announcement is only logged and the count is still returned. A failure that
    /// escapes therefore means nothing was written, so the caller may pass the same list again.
    /// </summary>
    Task<int> RecordGapsAsync(IReadOnlyCollection<WishCaptureGapRecord> gaps, CancellationToken cancellationToken = default);
}
