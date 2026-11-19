namespace EmotePurge.Core.Services;

/// <summary>
/// The worker's Twitch connection health, as published to Redis. One type shared by writer and
/// reader: the worker used to serialize this as an anonymous object and the API deserialize it into
/// its own private record, so the same wire format was declared twice with nothing tying them
/// together — and the health payload is precisely the place where the next field gets appended.
/// </summary>
/// <param name="ConnectAttemptedUtc">
/// Reference point for staleness while no chat message has ever arrived. Without it a freshly started
/// worker is indistinguishable from one that has been connected but silent for hours.
/// </param>
/// <param name="SevenTvLastFrameUtc">
/// Any received EventAPI frame including heartbeats (~45s cadence) — the liveness signal. The last
/// dispatch is deliberately not used for staleness: a channel whose set nobody edits receives no
/// dispatches for days and would read as permanently stale.
/// </param>
/// <param name="SevenTvLastDispatchUtc">Observability only; never feeds a status derivation.</param>
public record WorkerHealthSnapshot(
    bool IsConnected,
    DateTime? LastMessageReceivedUtc,
    DateTime? ConnectAttemptedUtc,
    // 7TV EventAPI fields, appended 2026-07-30. Defaults equal default(T) so a payload written by
    // an older worker deserializes without special-casing during a rolling deploy.
    bool SevenTvEnabled = false,
    bool SevenTvConnected = false,
    DateTime? SevenTvLastFrameUtc = null,
    DateTime? SevenTvLastDispatchUtc = null,
    DateTime? SevenTvConnectAttemptedUtc = null,
    // Admin-only detail fields, appended 2026-07-31 for GET /api/admin/health (Z1 health split).
    // Nullable rather than 0-defaulted on purpose: a payload from an older worker must read as
    // "unknown", not as "zero subscriptions / zero failures", which would look like a healthy idle
    // worker on the monitoring page. The public GET /api/worker/health never surfaces these.
    int? SevenTvDesiredChannelCount = null,
    int? SevenTvDesiredSubscriptionCount = null,
    int? SevenTvUnacknowledgedCount = null,
    int? FlushConsecutiveFailures = null,
    DateTime? FlushLastSuccessUtc = null,
    int? FlushLastRowCount = null,
    int? PendingEmoteCount = null,
    // Capacity context, appended 2026-08-01 so the admin page states ceilings instead of implying
    // them. Same trailing-and-defaulted rule as above: an older worker's payload must still
    // deserialize during a rolling deploy.
    //
    // The subscription limit is 7TV's own figure from the last Hello — the constant in the Api stays
    // as the fallback for exactly the window where this is still null.
    int? SevenTvSubscriptionLimit = null,
    // The Api cannot read the worker's configuration, so the resync cadence has to travel with the
    // snapshot. Without it "one REST request per channel per tick" is not a rate anyone can check.
    int? SevenTvResyncIntervalSeconds = null,
    // Both answer "how do I read the numbers above": counters that reset on restart mean something
    // different in the first minute of a process than in its sixth hour.
    DateTime? ProcessStartedUtc = null,
    string? WorkerInstanceId = null,
    // Appended 2026-08-03: the Twitch-side liveness signal, mirroring SevenTvLastFrameUtc — any
    // received IRC line including Twitch's ~5-minute server PING, so it stays fresh on a healthy
    // connection whose channels are all silent. Staleness derivation keys off this; the last chat
    // message above remains observability. Same trailing-and-defaulted rule as every field before.
    DateTime? TwitchLastFrameUtc = null,
    // Appended 2026-11-19 (K6, plan #486 2.3): whether this worker captures reward redemptions, i.e. its own
    // WishOptions.CaptureActive. The worker is the authority for the capture; null means an older worker that does not
    // know the field, not "off".
    bool? WishCaptureActive = null);

/// <summary>
/// Reads the health snapshot the worker publishes. The API and the worker deliberately never talk
/// directly — this keeps that true while giving the contract a single home. Owns the Redis key, which
/// the API previously repeated as a string literal because the two projects do not reference each
/// other.
/// </summary>
public interface IWorkerHealthReader
{
    /// <summary>
    /// <c>null</c> when the key is absent (expired, or the worker never started) or unreadable. That
    /// absence is itself the signal — the caller reports it, it is not an error.
    /// </summary>
    Task<WorkerHealthSnapshot?> ReadAsync(CancellationToken cancellationToken = default);
}
