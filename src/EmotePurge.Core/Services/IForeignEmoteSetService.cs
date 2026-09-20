namespace EmotePurge.Core.Services;

/// <summary>
/// Reads a channel's active 7TV emote set for a caller who has no role there at all — the read side
/// of "Fremde Kanäle als Import-Quelle" (spec 2026-09-09). Every step is read-only: nothing is
/// written to our database, no channel is joined, no worker code runs, and this is deliberately not
/// the group's <c>UsageStatsAccessAuthorizationFilter</c> path — any logged-in user may call this for
/// any Twitch channel.
/// </summary>
/// <remarks>
/// A thin seam by design. Hardening — the 60 s Redis cache on the assembled response, request
/// coalescing, the 429-aware circuit breaker, and the provider-wide concurrency budget (spec section
/// 6, E4, E5) — is a separate task that wraps this interface (or the two collaborators behind its
/// implementation) with a decorator. The base implementation stays exactly what its name says: the
/// three-step resolution chain (F1) plus the paginated set read (F3), nothing else.
/// </remarks>
public interface IForeignEmoteSetService
{
    /// <param name="channelName">A Twitch login, in any casing — normalized inside.</param>
    /// <param name="refresh">
    /// <c>true</c> bypasses the 60 s cache (spec E3's "neu laden") — the hardening decorator's
    /// concern entirely. It still passes through the same rate-limit policy and the same circuit
    /// breaker (spec section 6): a forced refresh is not a way around either guard, only around the
    /// cache. The base implementation registered under this interface ignores the flag — it never
    /// caches anything in the first place, so there is nothing for it to bypass.
    /// </param>
    Task<ForeignEmoteSetLookupResult> GetForeignEmoteSetAsync(
        string channelName, bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The set-ID read mode (spec 2026-09-20, 6.4/E8): reads an arbitrary 7TV emote set by its own
    /// id, never by resolving a channel's identity first. <paramref name="channelName"/> is the
    /// route's channel and is only ever echoed back onto <see cref="ForeignEmoteSet.ChannelName"/> —
    /// it is never normalized against Twitch, never used to look anything up, and its value has no
    /// bearing on which set is read. There is deliberately no Helix call and no 7TV identity
    /// resolution on this path: <see cref="ForeignEmoteSet.SevenTvUserId"/> on the result is always
    /// <c>null</c>, because our <c>Channel</c> row holds no 7TV user id to report in the first place
    /// (only the worker's live subscription registry does) — a channel with no role at all for the
    /// caller has nothing to resolve to.
    /// </summary>
    /// <param name="channelName">The route's channel — echoed, not resolved.</param>
    /// <param name="emoteSetId">The 7TV set id to read, already format-validated at the API edge.</param>
    /// <param name="refresh">Same meaning as on <see cref="GetForeignEmoteSetAsync"/>.</param>
    Task<ForeignEmoteSetLookupResult> GetForeignEmoteSetBySetIdAsync(
        string channelName, string emoteSetId, bool refresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every way <see cref="IForeignEmoteSetService.GetForeignEmoteSetAsync"/> can end, matching the
/// state table in section 5 of the spec one-to-one — with one addition that never reaches the wire:
/// see <see cref="SevenTvRateLimited"/>.
/// </summary>
public enum ForeignEmoteSetLookupStatus
{
    Ok,
    ChannelNotOnTwitch,
    TwitchUnavailable,
    NoSevenTvAccount,
    NoActiveEmoteSet,
    SevenTvUnavailable,

    /// <summary>
    /// A confirmed 7TV overload (HTTP 429, or HTTP 200 with <c>extensions.status: 429</c>) rather
    /// than a generic upstream failure. Kept apart from <see cref="SevenTvUnavailable"/> only so a
    /// hardening decorator (T2's circuit breaker, spec E4) can react differently — open immediately
    /// and honor a <c>Retry-After</c>, instead of counting toward the five-failure threshold. The API
    /// endpoint maps both statuses to the same 503 error code: the spec's state table has a single
    /// row for "7TV nicht erreichbar / 429", so this distinction is invisible on the wire.
    /// </summary>
    SevenTvRateLimited,

    /// <summary>
    /// The provider-wide request budget (spec E5b) refused a permit, so an upstream request this
    /// lookup needed was never made. Like <see cref="SevenTvRateLimited"/> this never reaches the wire
    /// as its own code — the endpoint answers the same 503 as "7TV nicht erreichbar", which is what
    /// the caller can act on — but it must stay apart internally: this is our own throttle, not
    /// anything 7TV said, so it must not count toward the circuit breaker's failure streak.
    /// </summary>
    ProviderBudgetExhausted
}

/// <summary>
/// <see cref="EmoteSet"/> is non-null if and only if <see cref="Status"/> is
/// <see cref="ForeignEmoteSetLookupStatus.Ok"/>, and the two factories are the only way to build one
/// — the same invariant-by-construction pattern <c>TwitchUserLookup</c> and the <c>SevenTv*Result</c>
/// family already use, for the same reason: a "found" result without a payload is exactly the value
/// every caller of this type assumes cannot exist.
/// </summary>
public sealed class ForeignEmoteSetLookupResult
{
    private ForeignEmoteSetLookupResult(ForeignEmoteSetLookupStatus status, ForeignEmoteSet? emoteSet, TimeSpan? retryAfter)
    {
        Status = status;
        EmoteSet = emoteSet;
        RetryAfter = retryAfter;
    }

    public ForeignEmoteSetLookupStatus Status { get; }

    /// <summary>Non-null if and only if <see cref="Status"/> is <see cref="ForeignEmoteSetLookupStatus.Ok"/>.</summary>
    public ForeignEmoteSet? EmoteSet { get; }

    /// <summary>
    /// Carried through from <see cref="EmotePurge.Core.SevenTv.SevenTvEmoteSetPreviewResult.RetryAfter"/> when
    /// <see cref="Status"/> is <see cref="ForeignEmoteSetLookupStatus.SevenTvRateLimited"/> — the
    /// hardening decorator's circuit breaker (spec E4) honors it over its own default open duration.
    /// <c>null</c> for every other status, and also for a rate limit 7TV reported with no such header.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public static ForeignEmoteSetLookupResult Ok(ForeignEmoteSet emoteSet)
    {
        ArgumentNullException.ThrowIfNull(emoteSet);
        return new ForeignEmoteSetLookupResult(ForeignEmoteSetLookupStatus.Ok, emoteSet, null);
    }

    public static ForeignEmoteSetLookupResult Failed(ForeignEmoteSetLookupStatus status, TimeSpan? retryAfter = null)
    {
        if (status == ForeignEmoteSetLookupStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "Failed() kann keinen Erfolgsstatus tragen — für Ok ist Ok(emoteSet) zuständig.");
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unbekannter ForeignEmoteSetLookupStatus.");
        }

        return new ForeignEmoteSetLookupResult(status, null, retryAfter);
    }
}

/// <summary>
/// The successful answer, shaped to match <c>ForeignEmoteSetResponse</c> from the spec (section 4)
/// property for property — <c>System.Text.Json</c>'s default camelCase policy is the only mapping
/// step the API endpoint needs, the same convention <c>ResyncCooldownState</c> and
/// <c>SevenTvChannelState</c> already rely on elsewhere in this codebase.
/// </summary>
/// <param name="SevenTvUserId">
/// <c>null</c> in the set-ID read mode (spec E8) — see
/// <see cref="IForeignEmoteSetService.GetForeignEmoteSetBySetIdAsync"/>. Never <c>null</c> for a
/// result the login-based <see cref="IForeignEmoteSetService.GetForeignEmoteSetAsync"/> produced.
/// </param>
/// <param name="EmoteSetName">
/// What 7TV reports as the set's own name (spec 6.4, F6); <c>null</c> when 7TV omits it. Trailing
/// and optional, together with <paramref name="Capacity"/>, purely so every existing positional
/// construction of this record — none of which knew this field existed — keeps compiling unchanged
/// (AK 28: the 9 Hardened-decorator tests and this record's other callers must stay untouched).
/// </param>
/// <param name="Capacity">
/// The set's slot limit as 7TV reports it; <c>0</c> is already normalised to <c>null</c> here, the
/// same idiom <see cref="EmotePurge.Core.SevenTv.SevenTvEmoteSet"/> and
/// <see cref="EmotePurge.Core.SevenTv.SevenTvEmoteSetListEntry"/> already use.
/// </param>
public sealed record ForeignEmoteSet(
    string ChannelName,
    string? SevenTvUserId,
    string EmoteSetId,
    int TotalCount,
    bool Truncated,
    IReadOnlyList<ForeignEmoteRow> Emotes,
    string? EmoteSetName = null,
    int? Capacity = null);

/// <summary>
/// One emote in a foreign set preview. <see cref="SevenTvEmoteId"/> is 7TV's own ObjectID, never our
/// internal <c>Emote.Id</c> guid (Regel 8) — this type never touches our database at all.
/// </summary>
public sealed record ForeignEmoteRow(
    string SevenTvEmoteId,
    string Name,
    string DefaultName,
    string ImageUrl,
    int? TopAllTime,
    int? Trending);
