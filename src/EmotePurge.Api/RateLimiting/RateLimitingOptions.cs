namespace EmotePurge.Api.RateLimiting;

/// <summary>
/// The budget of every rate-limit policy, bound from the <c>RateLimiting</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the numbers were literals in <c>Program.cs</c>, so every adjustment was a
/// rebuild and a redeploy of the image — and the tests had to mirror the literals to be able to
/// exhaust a budget at all. Environment variables override each value individually
/// (<c>RateLimiting__InteractiveRead__TokenLimit</c> and so on).
/// </para>
/// <para>
/// Read once at startup and never again: there is no write endpoint and no reload hook, so a changed
/// value takes effect on the next restart. That is deliberate — a limiter whose budget can move
/// under a running process cannot be reasoned about from a log line after the fact.
/// </para>
/// </remarks>
internal sealed class RateLimitingOptions
{
    /// <summary>The configuration section these options are bound from.</summary>
    internal const string SectionName = "RateLimiting";

    /// <summary>Ordinary navigation. Generous on purpose: an abuse guard, not a provider surrogate.</summary>
    public TokenBucketPolicy InteractiveRead { get; set; } = new()
    {
        TokenLimit = 300,
        TokensPerPeriod = 5,
        ReplenishmentPeriodSeconds = 1,
    };

    /// <summary>Vote mutations. A burst of clicking is normal here and must not read as abuse.</summary>
    public TokenBucketPolicy Voting { get; set; } = new()
    {
        TokenLimit = 120,
        TokensPerPeriod = 2,
        ReplenishmentPeriodSeconds = 1,
    };

    /// <summary>Writes against our own database, guarded loosely because dropping one costs data.</summary>
    public FixedWindowPolicy Bookkeeping { get; set; } = new() { PermitLimit = 120 };

    /// <summary>Manual channel resync: one unconditional 7TV call plus a live fan-out per hit.</summary>
    public FixedWindowPolicy ChannelResync { get; set; } = new() { PermitLimit = 5 };

    /// <summary>The anonymous health endpoint, whose legitimate callers are machines on fixed cadences.</summary>
    public FixedWindowPolicy PublicHealth { get; set; } = new() { PermitLimit = 30 };

    /// <summary>
    /// The anonymous legal-pages endpoints (issue #247): one availability check per SPA load, one
    /// document fetch per page view of <c>/imprint</c>/<c>/privacy</c>, one more per language
    /// switch while on one of those pages. A single visitor's whole session rarely exceeds half a
    /// dozen of these. 60/min gives headroom for roughly a dozen such visitors a minute from behind
    /// one shared IP (an office or campus NAT) without ever touching <see cref="PublicHealth"/>'s
    /// separate, much smaller machine-only budget.
    /// </summary>
    public FixedWindowPolicy PublicLegal { get; set; } = new() { PermitLimit = 60 };

    /// <summary>
    /// Foreign-channel-import preview (spec E5a): per user, between <see cref="ChannelResync"/> (5/min,
    /// the one call that always costs an unconditional 7TV round trip) and <see cref="Bookkeeping"/>
    /// (120/min, writes against our own database only). This call costs 7TV a Helix lookup, a
    /// userByConnection lookup, and up to ten paginated set-preview requests — closer to ChannelResync
    /// in shape than to ordinary navigation, hence a fixed window rather than InteractiveRead's bucket.
    /// </summary>
    public FixedWindowPolicy ForeignEmoteLookup { get; set; } = new() { PermitLimit = 10 };

    /// <summary>
    /// 7TV leaderboard (spec 2026-09-13, E16): unlike <see cref="ForeignEmoteLookup"/> this endpoint
    /// never costs 7TV anything directly — it reads an in-process stock — but its response is up to
    /// ~30 KB, so it is not as cheap as <see cref="Bookkeeping"/> either. 20/min per user comfortably
    /// covers every sort switch and re-open of the import dialog.
    /// </summary>
    public FixedWindowPolicy SevenTvLeaderboard { get; set; } = new() { PermitLimit = 20 };

    /// <summary>
    /// <c>POST /api/contact</c> (docs/DECISIONS.md 2026-09-24, "contact form"), per remote IP: a
    /// token bucket rather than a fixed window because the shared 60-second <see
    /// cref="RateLimitRejection.Window"/> every fixed-window policy runs on cannot express an hourly
    /// figure — three tokens, refilling one every 20 minutes, approximate "up to three per hour" with
    /// a burst allowance for a visitor who mistypes and resubmits right away. The provider-wide
    /// ceiling this does not cover lives in <c>ContactSendBudget</c> (Infrastructure), not here — see
    /// <see cref="RateLimitPolicyNames.Contact"/>.
    /// </summary>
    public TokenBucketPolicy Contact { get; set; } = new()
    {
        TokenLimit = 3,
        TokensPerPeriod = 1,
        ReplenishmentPeriodSeconds = 1200,
    };

    /// <summary>
    /// Throws unless every budget is usable. Called during startup, so a typo in an environment
    /// variable stops the container with a readable message instead of silently handing some policy
    /// a capacity of zero — which is not a lax limiter but a total outage of every route it guards,
    /// and one that looks exactly like a rate-limit incident in the log.
    /// </summary>
    public void Validate()
    {
        InteractiveRead.Validate(nameof(InteractiveRead));
        Voting.Validate(nameof(Voting));
        Bookkeeping.Validate(nameof(Bookkeeping));
        ChannelResync.Validate(nameof(ChannelResync));
        PublicHealth.Validate(nameof(PublicHealth));
        PublicLegal.Validate(nameof(PublicLegal));
        ForeignEmoteLookup.Validate(nameof(ForeignEmoteLookup));
        SevenTvLeaderboard.Validate(nameof(SevenTvLeaderboard));
        Contact.Validate(nameof(Contact));
    }

    private static void RequirePositive(string policyName, string valueName, int value)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException(
                $"Ungültige Rate-Limit-Konfiguration: '{SectionName}:{policyName}:{valueName}' muss größer als 0 sein, ist aber {value}.");
        }
    }

    /// <summary>A token bucket: a capacity that refills continuously while the app runs.</summary>
    internal sealed class TokenBucketPolicy
    {
        /// <summary>Capacity of the bucket, and therefore the largest burst a caller may spend at once.</summary>
        public int TokenLimit { get; set; }

        /// <summary>Tokens added per replenishment period.</summary>
        public int TokensPerPeriod { get; set; }

        /// <summary>Length of one replenishment period.</summary>
        public int ReplenishmentPeriodSeconds { get; set; }

        public void Validate(string policyName)
        {
            RequirePositive(policyName, nameof(TokenLimit), TokenLimit);
            RequirePositive(policyName, nameof(TokensPerPeriod), TokensPerPeriod);
            RequirePositive(policyName, nameof(ReplenishmentPeriodSeconds), ReplenishmentPeriodSeconds);
        }
    }

    /// <summary>A fixed window: a permit count that resets wholesale at every window boundary.</summary>
    internal sealed class FixedWindowPolicy
    {
        /// <summary>Permits granted per <see cref="RateLimitRejection.Window"/>.</summary>
        public int PermitLimit { get; set; }

        public void Validate(string policyName) => RequirePositive(policyName, nameof(PermitLimit), PermitLimit);
    }
}
