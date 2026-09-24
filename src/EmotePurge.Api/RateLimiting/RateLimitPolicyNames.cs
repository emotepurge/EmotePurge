namespace EmotePurge.Api.RateLimiting;

/// <summary>
/// The name of every rate-limit policy, in one place.
/// </summary>
/// <remarks>
/// A policy name is a string in three unrelated spots: the registration in <c>Program.cs</c>, the
/// <c>RequireRateLimiting</c> call on every route it guards, and the rejection log an operator reads
/// when a 429 shows up. Typed out three times, a rename silently unhooks routes — ASP.NET Core does
/// not fail a build over a policy name no registration answers, it throws at the first request
/// against that route. Referencing the constants makes the compiler the guard instead, and lets the
/// tests assert the name the code actually uses rather than a re-typed copy of it.
/// </remarks>
internal static class RateLimitPolicyNames
{
    /// <summary>Ordinary navigation: reads that cost this API a database query, nothing more.</summary>
    internal const string InteractiveRead = "InteractiveRead";

    /// <summary>Casting and retracting votes, partitioned per vote session rather than per user.</summary>
    internal const string Voting = "Voting";

    /// <summary>Writes against our own database whose loss would leave data diverging from 7TV.</summary>
    internal const string Bookkeeping = "Bookkeeping";

    /// <summary>The one user-triggered action that costs an unconditional 7TV call.</summary>
    internal const string ChannelResync = "ChannelResync";

    /// <summary>The anonymous <c>GET /api/health</c>, partitioned by remote IP.</summary>
    internal const string PublicHealth = "PublicHealth";

    /// <summary>
    /// The anonymous legal-pages endpoints (<c>GET /api/legal/availability</c>,
    /// <c>GET /api/legal/{kind}/{language}</c>, issue #247), partitioned by remote IP like
    /// <see cref="PublicHealth"/> — but its own policy, not a share of it. <c>PublicHealth</c>'s
    /// budget is sized for two machine callers on fixed cadences (the container HEALTHCHECK, the
    /// uptime monitor); this one is sized for browser visitors, who can arrive from behind one
    /// shared NAT in numbers neither of those callers ever does. Codex Sol review of #247 (P2):
    /// under the shared budget, a burst of ordinary visitor traffic on a NAT'd network could 429
    /// the health check's own budget away, or a health-check blip could 429 every visitor's footer
    /// links for the rest of their session — two unrelated failure domains that had no business
    /// sharing one counter.
    /// </summary>
    internal const string PublicLegal = "PublicLegal";

    /// <summary>
    /// <c>GET /api/seventv/channels/{channelName}/emotes</c> (foreign-channel-import spec, E5a).
    /// Per-user only — the provider-wide budget across all users (E5b) is a separate, in-process
    /// concern the hardening decorator around <c>IForeignEmoteSetService</c> owns, not an ASP.NET
    /// Core rate-limit policy.
    /// </summary>
    internal const string ForeignEmoteLookup = "ForeignEmoteLookup";

    /// <summary>
    /// <c>GET /api/seventv/leaderboard</c> (7TV-leaderboard-as-import-source spec 2026-09-13, E16).
    /// Per-user only — the provider-wide window budget across all users lives in
    /// <c>SevenTvLeaderboardRequestBudget</c>, an in-process concern the leaderboard service owns, not
    /// an ASP.NET Core rate-limit policy, exactly like <see cref="ForeignEmoteLookup"/>'s split.
    /// </summary>
    internal const string SevenTvLeaderboard = "SevenTvLeaderboard";

    /// <summary>
    /// <c>POST /api/contact</c> (docs/DECISIONS.md 2026-09-24, "contact form"): reachable anonymously,
    /// but not exclusively so — nothing stops an already-authenticated visitor from submitting the
    /// form too. Partitioned by remote IP alone through its own
    /// <c>RateLimitRejection.PartitionPerIpTokenBucket</c> (revised 2026-09-24, Codex P2), never
    /// through <see cref="PublicLegal"/>'s/<c>PartitionPerUser</c>'s claim-first fallback — that would
    /// hand each signed-in visitor behind one shared IP their own budget instead of the one shared
    /// bucket this policy exists to enforce. This is only the per-IP half — the provider-wide ceiling
    /// across all visitors combined is <c>ContactSendBudget</c>, an in-process concern in
    /// Infrastructure, not an ASP.NET Core policy, same split as <see cref="ForeignEmoteLookup"/>.
    /// </summary>
    internal const string Contact = "Contact";
}
