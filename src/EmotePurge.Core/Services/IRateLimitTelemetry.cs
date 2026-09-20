namespace EmotePurge.Core.Services;

/// <summary>
/// The write side of the rate-limit telemetry: three counting calls, one per thing worth watching.
/// </summary>
/// <remarks>
/// <para>
/// Every implementation must be safe to call fire-and-forget from the product path: the returned task
/// never faults, and no method throws. Telemetry that can break a request is worse than no telemetry —
/// counting a 429 must never be able to cause one.
/// </para>
/// <para>
/// Every dimension is a stable name — a policy name, a call source, a cache name, a route
/// <em>template</em> — and never a raw URL. Raw paths would turn a counter into an unbounded key space
/// and would put user-supplied text (channel names, ids) into monitoring keys.
/// </para>
/// </remarks>
public interface IRateLimitTelemetry
{
    /// <summary>Records one decision of a local ASP.NET rate-limit policy, accepted or rejected.</summary>
    Task RecordPolicyDecisionAsync(RateLimitPolicyDecision decision, CancellationToken cancellationToken = default);

    /// <summary>Records one response observed at the outgoing provider boundary.</summary>
    Task RecordProviderResponseAsync(ProviderResponseObservation observation, CancellationToken cancellationToken = default);

    /// <summary>Records one lookup in one of the server-side caches named in <see cref="RateLimitCacheNames"/>.</summary>
    Task RecordCacheLookupAsync(string cacheName, bool hit, CancellationToken cancellationToken = default);
}

/// <summary>
/// One decision of a local rate-limit policy.
/// </summary>
/// <param name="PolicyName">The registered policy name, from the API's <c>RateLimitPolicyNames</c>.</param>
/// <param name="Accepted">
/// <c>false</c> only for a rejection the limiter itself produced. A domain 429 such as the resync
/// cooldown is not a policy violation and is reported as accepted (see the spec).
/// </param>
/// <param name="RouteTemplate">The endpoint's route template, never the raw request path.</param>
/// <param name="Partition">
/// A stable description of the partition the request fell into (for example <c>user:42</c>), so an
/// operator can tell a single noisy caller apart from a broad wave.
/// </param>
/// <param name="RetryAfterSeconds">The <c>Retry-After</c> handed to the caller; <c>null</c> when accepted.</param>
public record RateLimitPolicyDecision(
    string PolicyName,
    bool Accepted,
    string HttpMethod,
    string RouteTemplate,
    string Partition,
    int? RetryAfterSeconds = null);

/// <summary>
/// One response from an external provider, observed on the way back in.
/// </summary>
/// <param name="ProviderName">A name from <see cref="RateLimitProviders"/>.</param>
/// <param name="CallSource">A name from <see cref="RateLimitCallSources"/> — which of our clients made the call.</param>
/// <param name="StatusCode">The HTTP status code; only 429 counts as a real provider rate limit.</param>
/// <param name="RetryAfterSeconds">The provider's <c>Retry-After</c>, when it sent one.</param>
/// <param name="RateLimitLimit">
/// The <c>Ratelimit-Limit</c> header, kept verbatim as a string. A sample of what was last seen, not a
/// reservable or authoritative shared budget — the spec is explicit that this round builds no
/// coordinator, so these values are never parsed into a decision.
/// </param>
public record ProviderResponseObservation(
    string ProviderName,
    string CallSource,
    int StatusCode,
    int? RetryAfterSeconds = null,
    string? RateLimitLimit = null,
    string? RateLimitRemaining = null,
    string? RateLimitReset = null);

/// <summary>
/// The providers whose responses are counted. Shared by the writing handler, the reader and the admin
/// page so all three agree on one spelling.
/// </summary>
public static class RateLimitProviders
{
    public const string Twitch = "twitch";

    public const string SevenTv = "seventv";
}

/// <summary>
/// Which of our own clients made an outgoing call. One provider has several of them, and they behave
/// very differently — a token refresh is rare, a Helix pagination is not.
/// </summary>
public static class RateLimitCallSources
{
    /// <summary>The typed Helix client (<c>api.twitch.tv/helix</c>).</summary>
    public const string TwitchHelix = "twitch-helix";

    /// <summary>The typed auth client (<c>id.twitch.tv</c>): OAuth exchange, refresh and validation.</summary>
    public const string TwitchAuth = "twitch-auth";

    /// <summary>The typed 7TV REST client (<c>7tv.io/v3</c>). The browser's direct GQL calls are not counted here.</summary>
    public const string SevenTvRest = "seventv-rest";

    /// <summary>
    /// The foreign-channel-import preview's own paginated v4 GraphQL requests (spec 2026-09-09,
    /// section 6 telemetry vertrag). Reported by <c>SevenTvApiClient.GetEmoteSetPreviewAsync</c>
    /// itself rather than by <see cref="EmotePurge.Infrastructure.Telemetry.ProviderRequestTelemetryHandler"/>,
    /// which is silenced for these requests: only the client sees the parsed GraphQL body, and 7TV
    /// disguises an overload as HTTP 200 with <c>extensions.status: 429</c> — the shared handler only
    /// ever sees the raw HTTP status and would file that as a plain success under
    /// <see cref="SevenTvRest"/>, the exact gap the spec calls "the most expensive single mistake".
    /// </summary>
    public const string SevenTvForeignPreview = "seventv-foreign-preview";

    /// <summary>
    /// The leaderboard import source's own paginated v4 GraphQL requests (spec 2026-09-13, F7/T1) —
    /// <c>SevenTvApiClient.SearchEmotesAsync</c>'s call source, reported by the client itself for
    /// the same reason as <see cref="SevenTvForeignPreview"/>: only the client sees the parsed
    /// GraphQL body, and 7TV disguises an overload as HTTP 200 with <c>extensions.status: 429</c>
    /// here too. Kept apart from <see cref="SevenTvForeignPreview"/> because it is a genuinely
    /// different upstream bucket — 7TV's <c>x-ratelimit-search-*</c> headers, distinct from the
    /// <c>Ratelimit-*</c> ones the preview path (still) never reads.
    /// </summary>
    public const string SevenTvLeaderboard = "seventv-leaderboard";

    /// <summary>
    /// The emote-set list of a 7TV account (spec 2026-09-20, 6.1/E7) — one v4 <c>userByConnection</c>
    /// request per account, reported by <c>SevenTvApiClient.GetEmoteSetListForTwitchUserAsync</c>
    /// itself for the same reason as the two above: only the client sees the parsed GraphQL body,
    /// and 7TV disguises an overload as HTTP 200 here too. Named apart from
    /// <see cref="SevenTvForeignPreview"/> even though both sit on the same upstream budget,
    /// because reading which of the two is failing is the whole point of separating their circuit
    /// breaker counters.
    /// </summary>
    public const string SevenTvEmoteSetList = "seventv-emote-set-list";
}

/// <summary>
/// The server-side caches whose hit rate is reported. A fixed list on purpose: a cache nobody named
/// here is a cache nobody watches, and a free-form name would let a typo silently create a second one.
/// </summary>
public static class RateLimitCacheNames
{
    /// <summary>The shared moderated-channels list per Twitch user.</summary>
    public const string ModeratedChannels = "moderated-channels";

    /// <summary>The 7TV editor grants of a user.</summary>
    public const string SevenTvGrants = "seventv-grants";

    /// <summary>The subscriber check behind the voting eligibility.</summary>
    public const string SubscriberCheck = "subscriber-check";

    /// <summary>
    /// The 60 s foreign-channel-import preview cache, keyed on the normalized source login (spec
    /// 2026-09-09, E3).
    /// </summary>
    public const string ForeignEmoteSet = "foreign-emote-set";

    /// <summary>
    /// The same 60 s cache's second key space (spec 2026-09-20, E12): the set-ID read mode, keyed on
    /// the 7TV set id instead of a channel login. Named apart from <see cref="ForeignEmoteSet"/> so a
    /// hit-rate dashboard can tell the two read paths apart, even though both live in the one cache.
    /// </summary>
    public const string ForeignEmoteSetBySetId = "foreign-emote-set-by-set-id";
}
