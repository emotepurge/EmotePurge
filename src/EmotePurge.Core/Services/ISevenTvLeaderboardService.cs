using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>
/// Reads 7TV's network-wide emote ranking for one of the two allowed sorts — the third import
/// source (spec 2026-09-13), sibling to <see cref="IForeignEmoteSetService"/>. There is no channel
/// here and nothing is written: any logged-in user may ask for either ranking, and the answer is
/// the same for everyone.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole guard sits behind this one method.</b> Unlike the foreign-channel preview, whose
/// hardening is a decorator around a raw chain, the leaderboard's in-process stock, window budget
/// and circuit breaker are the implementation itself: there is no unguarded face of this service to
/// resolve, and no flag on this method that can bypass any of them.
/// </para>
/// <para>
/// <b>What the caller deliberately cannot ask for.</b> No <c>page</c>, no <c>perPage</c>, no
/// <c>refresh</c>, no query, no tags, no filters (spec section 4). The page size and the page
/// count are the implementation's business, and a "reload" button reads the stock like every other
/// caller — a per-click upstream request is exactly the bypass the ceiling exists to prevent.
/// </para>
/// </remarks>
public interface ISevenTvLeaderboardService
{
    /// <param name="sortBy">Which of the two rankings to read. Parsed from the wire vocabulary by the endpoint's filter, never taken raw.</param>
    Task<SevenTvLeaderboardResult> GetLeaderboardAsync(
        SevenTvLeaderboardSort sortBy, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every way <see cref="ISevenTvLeaderboardService.GetLeaderboardAsync"/> can end (spec section 5).
/// The three failure arms all answer the same 503 <c>foreign_channel_seventv_unavailable</c> on the
/// wire — they stay apart in here for the same reason
/// <see cref="ForeignEmoteSetLookupStatus.ProviderBudgetExhausted"/> does: what caused the failure
/// decides how long it is stocked and whether the circuit breaker is allowed to learn anything from
/// it.
/// </summary>
public enum SevenTvLeaderboardStatus
{
    Ok,

    /// <summary>
    /// A confirmed 7TV overload (HTTP 429, or HTTP 200 with <c>extensions.status: 429</c>) on one of
    /// the pages, or the leaderboard's circuit breaker standing open because of one.
    /// </summary>
    SevenTvRateLimited,

    /// <summary>
    /// Any other upstream failure — 5xx, timeout, unparseable body, validation rejection — or the
    /// circuit breaker standing open because of a streak of them.
    /// </summary>
    SevenTvUnavailable,

    /// <summary>
    /// Our own rolling window budget refused a permit, so the upstream request was never made. It
    /// must stay apart from the two above: this is congestion we inflicted on ourselves, it says
    /// nothing about 7TV's health, and it is therefore never reported to the circuit breaker.
    /// </summary>
    BudgetRefused
}

/// <summary>
/// <see cref="Response"/> is non-null if and only if <see cref="Status"/> is
/// <see cref="SevenTvLeaderboardStatus.Ok"/>, and the two factories are the only way to build one —
/// the same invariant-by-construction shape as <see cref="ForeignEmoteSetLookupResult"/> and the
/// <c>SevenTv*Result</c> family, for the same reason.
/// </summary>
/// <remarks>
/// A failure carries no retry hint outward on purpose. 7TV's hint is consumed where it is acted on —
/// the circuit breaker's open duration and the stock entry's shelf-life — and handing it to the API
/// as well would invite a second, unsynchronised retry policy on the wire.
/// </remarks>
public sealed class SevenTvLeaderboardResult
{
    private SevenTvLeaderboardResult(SevenTvLeaderboardStatus status, SevenTvLeaderboardResponse? response)
    {
        Status = status;
        Response = response;
    }

    public SevenTvLeaderboardStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="SevenTvLeaderboardStatus.Ok"/>.</summary>
    public SevenTvLeaderboardResponse? Response { get; }

    public static SevenTvLeaderboardResult Ok(SevenTvLeaderboardResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new SevenTvLeaderboardResult(SevenTvLeaderboardStatus.Ok, response);
    }

    public static SevenTvLeaderboardResult Failed(SevenTvLeaderboardStatus status)
    {
        if (status == SevenTvLeaderboardStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Failed() cannot carry a success status — Ok(response) is responsible for Ok.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown SevenTvLeaderboardStatus.");
        }

        return new SevenTvLeaderboardResult(status, null);
    }
}

/// <summary>
/// The successful answer, shaped to match the wire contract in section 4 of the spec property for
/// property — <c>System.Text.Json</c>'s default camelCase policy is the only mapping the endpoint
/// needs, exactly as with <see cref="ForeignEmoteSet"/>.
/// </summary>
/// <param name="SortBy">
/// 7TV's own wire code (<c>TRENDING_DAILY</c> / <c>TOP_ALL_TIME</c>), not the .NET enum name: the
/// JSON echoes back the vocabulary the caller sent, so a client can compare what it asked for with
/// what it got without knowing our spelling.
/// </param>
/// <param name="TotalCount">What 7TV reports for the whole ranking — routinely far more than <paramref name="Emotes"/> holds.</param>
/// <param name="Truncated">
/// <c>TotalCount &gt; Emotes.Count</c>, said explicitly rather than left to be inferred. On this
/// list it is the normal case, not the exception, and the frontend words it differently per sort.
/// </param>
/// <param name="Emotes">
/// In 7TV's own ranking order, at most the server-side ceiling of two pages, de-duplicated by 7TV
/// id. <see cref="ForeignEmoteRow"/> is reused from the foreign-channel preview so both sources
/// feed the identical grid; a leaderboard row has no per-set alias, so its <c>Name</c> is its
/// <c>DefaultName</c>.
/// </param>
public sealed record SevenTvLeaderboardResponse(
    string SortBy,
    int TotalCount,
    bool Truncated,
    IReadOnlyList<ForeignEmoteRow> Emotes);
