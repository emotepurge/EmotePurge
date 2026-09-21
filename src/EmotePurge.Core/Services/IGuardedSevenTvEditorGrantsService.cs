using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>
/// A user's 7TV editor grants for the set-centric import's owner check, and for nobody else (spec
/// 2026-09-20, section 32, second review round). Reads the same grant cache as
/// <see cref="ISevenTvEditorService"/>, but resolves a miss behind the provider guards the set lists
/// and the preview already sit behind — circuit breaker, concurrency slot, one request permit per
/// upstream request — and holds a failure for a short while instead of retrying it on every report.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second way to the same grants.</b> The report runs under the <c>Bookkeeping</c> policy
/// (120/min per user) and after the import's 7TV mutation has happened. Through the unguarded
/// <see cref="ISevenTvEditorService"/>, a report against an empty or unreachable grant cache cost up
/// to two raw 7TV requests every time, outside the shared budget — repeated forged reports during an
/// outage could drain the bucket the preview and the lists depend on.
/// </para>
/// <para>
/// <b>Why not the cache alone.</b> The cache holds ten minutes. Answering a miss with 503 would lose
/// the audit entry of every report that arrives after a long import or during a Redis blip — the
/// mutation has already happened, and the frontend does not retry.
/// </para>
/// <para>
/// <b>Why only this caller.</b> Behind a shared budget, an exhausted budget would make roles unknown
/// on the authorization path, and every channel page would answer 403. That is a far larger effect
/// than the finding, and not this interface's decision: <see cref="ISevenTvEditorService"/> stays
/// unguarded for authorization, the target picker and the channel overview.
/// </para>
/// </remarks>
public interface IGuardedSevenTvEditorGrantsService
{
    /// <summary>
    /// Never null, never throws for an upstream failure. <c>Ok</c> on a cache hit (no upstream
    /// request) or a successful refresh (written back to the grant cache every reader shares);
    /// <c>NoSevenTvAccount</c> when 7TV knows no account for the Twitch id; <c>Unavailable</c> for
    /// everything else — a failed or rate-limited refresh, a refused permit or slot, an open breaker,
    /// or any of these still being held from an earlier report.
    /// </summary>
    Task<SevenTvEditorGrantsLookupResult> GetEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The short-lived memory of <see cref="IGuardedSevenTvEditorGrantsService"/>'s unsuccessful
/// refreshes, keyed by Twitch user id in a key space of its own. Holds only the answer to hand back
/// — <see cref="SevenTvLookupStatus.Unavailable"/> or <see cref="SevenTvLookupStatus.NoSevenTvAccount"/>
/// — with the shelf-life its caller passes in (the pattern of spec 6.1, "Fehler werden gecacht").
/// </summary>
/// <remarks>
/// Never read by the unguarded <see cref="ISevenTvEditorService"/>: a failure held here must not
/// reach the authorization path, which fails closed on it.
/// </remarks>
public interface ISevenTvEditorGrantsHoldCache
{
    /// <summary>Fail-open: a Redis outage or an unreadable entry reads as "nothing held".</summary>
    Task<SevenTvLookupStatus?> TryGetAsync(string twitchUserId, CancellationToken cancellationToken = default);

    /// <summary>Fail-open: a lost write only costs the next report one more guarded attempt.</summary>
    Task SetAsync(string twitchUserId, SevenTvLookupStatus status, TimeSpan timeToLive, CancellationToken cancellationToken = default);
}
