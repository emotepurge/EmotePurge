namespace EmotePurge.Core.Services;

/// <summary>
/// The one request budget for 7TV's GraphQL search bucket (<c>x-ratelimit-search-*</c>), shared by
/// every process that draws from it — the Api's leaderboard and the Worker's Twitch-id resolution.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> 7TV allows 100 search requests per ~60 seconds, shared across its v3 and
/// v4 APIs, and answers an overdraft with a lockout of about an hour — disguised as HTTP 200 with
/// <c>extensions.status: 429</c>. Api and Worker are separate processes, so an in-process budget in
/// either cannot see the other's traffic; this one lives where both can.
/// </para>
/// <para>
/// <b>Two halves, two seams.</b> The consumer charges <see cref="TryChargeAsync"/> before each
/// search request, because only the consumer knows who it is and what a refusal means for it. The
/// 7TV client reports <see cref="ObserveResponseAsync"/> after each one, because only the client sees
/// the headers and the GraphQL error payload. A new caller of a search query owes both.
/// </para>
/// <para>
/// <b>Fail-closed for charging, fail-open for observing.</b> A charge that cannot reach the store is
/// refused (<see cref="SevenTvSearchRefusal.StoreUnavailable"/>): an outage of the shared state must
/// never turn into unguarded traffic on a bucket whose overdraft costs an hour. An observation that
/// cannot be written is dropped — with the store down, every charge is refused anyway. Neither method
/// throws for a store failure.
/// </para>
/// </remarks>
public interface ISevenTvSearchBudget
{
    /// <summary>
    /// Takes one permit for one search request, without waiting. A refused permit means the caller
    /// must not make the request at all; a refusal consumes nothing.
    /// </summary>
    Task<SevenTvSearchPermit> TryChargeAsync(SevenTvSearchConsumer consumer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Feeds 7TV's answer to one search request back into the budget: a rate limit, or a nearly
    /// empty bucket, blocks every consumer until 7TV's reset; the remaining count also feeds the
    /// minimum-remaining telemetry. Never throws.
    /// </summary>
    Task ObserveResponseAsync(SevenTvSearchObservation observation, CancellationToken cancellationToken = default);
}

/// <summary>Who draws from the search bucket. Each consumer has its own share of the window.</summary>
public enum SevenTvSearchConsumer
{
    /// <summary>The Worker's resolution of a channel's Twitch id through <c>users(query:)</c>.</summary>
    ChannelIdentity,

    /// <summary>The Api's leaderboard import source through <c>emotes.search</c>.</summary>
    Leaderboard,
}

/// <summary>Why a permit was refused.</summary>
public enum SevenTvSearchRefusal
{
    /// <summary>Not refused.</summary>
    None,

    /// <summary>
    /// 7TV itself said stop — a rate limit, or a nearly empty bucket — and the reset has not passed
    /// yet. <see cref="SevenTvSearchPermit.BlockedFor"/> says for how long.
    /// </summary>
    Blocked,

    /// <summary>The shared window holds as many requests as all consumers together may make.</summary>
    WindowFull,

    /// <summary>The window has room, but not within this consumer's share of it.</summary>
    ConsumerShareFull,

    /// <summary>The shared store could not be reached; refused because the budget is fail-closed.</summary>
    StoreUnavailable,
}

/// <summary>The answer to one <see cref="ISevenTvSearchBudget.TryChargeAsync"/>.</summary>
/// <param name="Refusal"><see cref="SevenTvSearchRefusal.None"/> if and only if the permit was granted.</param>
/// <param name="UsedInWindow">
/// Requests in the shared window once this call is done, including a permit just granted; for a
/// refusal the count that caused it (zero when unknown).
/// </param>
/// <param name="BlockedFor">The remaining block, set only for <see cref="SevenTvSearchRefusal.Blocked"/>.</param>
public sealed record SevenTvSearchPermit(SevenTvSearchRefusal Refusal, int UsedInWindow, TimeSpan? BlockedFor = null)
{
    /// <summary>Whether the request may be made.</summary>
    public bool Granted => Refusal == SevenTvSearchRefusal.None;
}

/// <summary>
/// What 7TV's answer to one search request said about the bucket. Every field is optional: a
/// transport failure carries none, and 7TV sends no rate-limit headers on some rejections.
/// </summary>
/// <param name="Remaining">The <c>x-ratelimit-search-remaining</c> header, parsed.</param>
/// <param name="ResetSeconds">The <c>x-ratelimit-search-reset</c> header, parsed: seconds until reset.</param>
/// <param name="RateLimited">A 429 in either form — HTTP status or <c>extensions.status</c>.</param>
/// <param name="RetryAfter">The best reset hint the client found for a rate limit, if any.</param>
public sealed record SevenTvSearchObservation(
    int? Remaining,
    int? ResetSeconds,
    bool RateLimited,
    TimeSpan? RetryAfter = null);
