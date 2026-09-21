
namespace EmotePurge.Core.Services;

/// <summary>
/// One 7TV emote set as the picker, the dropdown and the source-set list all need it (spec
/// 2026-09-20, 6.1/E7).
/// </summary>
/// <param name="Capacity">
/// 7TV's own figure, with <c>0</c> normalised to <c>null</c> — an absent field and a genuine zero
/// are indistinguishable on the wire, and reading either as "no slots" would make the UI claim a
/// set is full.
/// </param>
/// <param name="Kind">
/// 7TV's <c>EmoteSetKind</c>, passed through ordinally: <c>NORMAL</c>, <c>PERSONAL</c>,
/// <c>GLOBAL</c> or <c>SPECIAL</c> (E7). Never parsed into an enum of ours: a fifth member 7TV adds
/// tomorrow must reach the UI as "something we do not offer", not as a crash or as a silent
/// <c>NORMAL</c>. Selectable is <c>NORMAL</c> alone (8.6).
/// </param>
/// <param name="IsPersonal">
/// <c>Kind == PERSONAL</c>. Only the label depends on it — every non-<c>NORMAL</c> kind is equally
/// unselectable, and this one merely gets its own wording.
/// </param>
/// <param name="OwnerDisplayName">
/// <c>owner.mainConnection.platformDisplayName</c> (E7) — a display name, never a login, and never
/// compared against anything: every identity check in this codebase runs on ids.
/// </param>
/// <param name="OwnerSevenTvUserId">
/// <c>owner.id</c> of the same answer — what the set-centric import's owner check compares against
/// (spec 2026-09-20, section 32), never shown anywhere. <c>null</c> when 7TV reported no owner; the
/// check then asks 7TV about that one set instead of guessing.
/// </param>
public record EmoteSetSummary(
    string Id, string Name, int? Capacity, string Kind, bool IsPersonal, string? OwnerDisplayName,
    string? OwnerSevenTvUserId = null);

/// <summary>
/// The sets of one 7TV account, plus the set 7TV itself considers active for it.
/// </summary>
/// <param name="SevenTvActiveEmoteSetId">
/// <c>style.activeEmoteSetId</c> of the same v4 answer (E7), or <c>null</c> when 7TV reports none.
/// Deliberately named for its source: for a <i>tracked</i> channel the authoritative active set is
/// <c>Channel.ActiveEmoteSetId</c>, our own observed state (E21), and only a caller that has no
/// observed state may fall back to this one. That is also why no <c>IsActive</c> flag sits on
/// <see cref="EmoteSetSummary"/>: one cached answer serves three routes whose notion of "active"
/// differs, so the flag belongs to the response each route assembles, not to the shared value.
/// </param>
/// <param name="SevenTvUserId">
/// The 7TV account id of the account whose sets these are (<c>userByConnection.id</c>, E7) — the
/// other half of the set-centric import's owner check (section 32): a set is admissible when its
/// <see cref="EmoteSetSummary.OwnerSevenTvUserId"/> is the id of one of the checked accounts. Always
/// set on a list read from 7TV; <c>null</c> only on a list built by hand, which the owner check
/// treats as unreadable rather than as "owns nothing".
/// </param>
public sealed record EmoteSetList(
    string? SevenTvActiveEmoteSetId, IReadOnlyList<EmoteSetSummary> Sets, string? SevenTvUserId = null);

/// <summary>
/// Why <see cref="ISevenTvEmoteSetListService.ListByTwitchIdAsync"/> produced what it produced —
/// the state table of spec 6.1. <see cref="NoSevenTvAccount"/> is an <i>answer</i> and must never
/// collapse into a failure (it is 200 with an empty list); every failure below it is a 503. There
/// is no state in which an empty list stands in for a failure.
/// </summary>
public enum EmoteSetListStatus
{
    Ok,

    /// <summary>
    /// 7TV carries no account for this Twitch connection: <c>userByConnection: null</c> at HTTP 200
    /// <i>without</i> an <c>errors</c> block (measured 2026-09-20). Distinct from every failure
    /// below — "this person has no 7TV account" is something we know, not something we failed to
    /// find out.
    /// </summary>
    NoSevenTvAccount,

    /// <summary>A confirmed 7TV overload: HTTP 429, or HTTP 200 with <c>extensions.status: 429</c>.</summary>
    RateLimited,

    /// <summary>
    /// Anything else that left us without an answer: transport failure, 5xx, unparseable body, a
    /// GraphQL error, or an answer that carried <c>userByConnection</c> but no <c>emoteSets</c>.
    /// Also what an open circuit breaker reports.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Our own provider-wide budget refused, so no request was made. Kept apart from
    /// <see cref="Unavailable"/> because it says nothing about 7TV — it is this process throttling
    /// itself — even though both answer the caller the same way.
    /// </summary>
    BudgetExhausted
}

/// <summary>
/// <see cref="List"/> is non-null if and only if <see cref="Status"/> is
/// <see cref="EmoteSetListStatus.Ok"/>. Same invariant-by-construction shape as the 7TV result
/// types in <c>EmotePurge.Core.SevenTv</c>, for the same reason: the status and the payload cannot
/// be set apart from each other.
/// </summary>
public sealed class EmoteSetListResult
{
    private EmoteSetListResult(EmoteSetListStatus status, EmoteSetList? list)
    {
        Status = status;
        List = list;
    }

    public EmoteSetListStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="EmoteSetListStatus.Ok"/>.</summary>
    public EmoteSetList? List { get; }

    public static EmoteSetListResult Ok(EmoteSetList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new EmoteSetListResult(EmoteSetListStatus.Ok, list);
    }

    public static EmoteSetListResult Failed(EmoteSetListStatus status)
    {
        if (status == EmoteSetListStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Failed() cannot carry a success status — Ok(list) is for that.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown EmoteSetListStatus.");
        }

        return new EmoteSetListResult(status, null);
    }

}

/// <summary>
/// The 7TV emote-set list of one Twitch account (spec 2026-09-20, 6.1, E6): <b>one</b> service
/// behind all three routes that need it — the tracked channel's dropdown, the target picker's own
/// accounts, and a foreign channel's source sets — and therefore one cache and one guard chain in
/// front of 7TV, rather than three that each discover the rate limit separately.
/// </summary>
public interface ISevenTvEmoteSetListService
{
    /// <summary>
    /// Never null, and never a silently empty list: the status says which of the five states of 6.1
    /// this is.
    /// </summary>
    /// <param name="twitchChannelId">
    /// The immutable Twitch user id, which is also the cache key. Not a login: the whole path runs
    /// on ids so that it never touches 7TV's shared search bucket.
    /// </param>
    Task<EmoteSetListResult> ListByTwitchIdAsync(string twitchChannelId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The 60 s Redis cache in front of <see cref="ISevenTvEmoteSetListService"/> (E12), key space
/// <c>7tvsets:{twitchId}</c> — its own prefix beside the preview's <c>7tvforeign:</c>, because the
/// two hold different answers about the same channel.
/// </summary>
/// <remarks>
/// Holds negative outcomes too, each with its own short shelf-life: without that, every reopened
/// dialog during an outage asks 7TV again (6.1, "Fehler werden gecacht, nicht wiederholt"). Which
/// is also why the time to live is the caller's to pass rather than a constant in here.
/// </remarks>
public interface ISevenTvEmoteSetListCache
{
    /// <summary>Fail-open: a Redis outage reads as a miss, never as an exception.</summary>
    Task<EmoteSetListResult?> TryGetAsync(string twitchChannelId, CancellationToken cancellationToken = default);

    /// <summary>Fail-open: a lost write only costs the next caller a live lookup.</summary>
    Task SetAsync(
        string twitchChannelId, EmoteSetListResult result, TimeSpan timeToLive, CancellationToken cancellationToken = default);
}
