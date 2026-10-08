using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace EmotePurge.Api.RateLimiting;

/// <summary>
/// The partitioning and the rejected answer of every rate-limit policy, in one place because they
/// share a secret: only the partitioner knows how the partition key was derived, and only the
/// rejection needs to name it.
/// </summary>
/// <remarks>
/// Before 2026-08-29 there was no rejection handler at all. A throttled request got a bare 429 — no
/// body, no Retry-After, no log line — so the frontend fell back to its generic status message and
/// the server side of the story simply did not exist. Two issues (#33, #35) had to be traced through
/// the production nginx access log to establish that the 429s were ours and not Cloudflare's.
/// </remarks>
internal static class RateLimitRejection
{
    /// <summary>
    /// The fallback value whenever a policy name, partition key, or resolved user key is not
    /// available — never a null or empty string, so a log line or decision always has something to
    /// print.
    /// </summary>
    private const string Unknown = "unknown";

    /// <summary>
    /// Category of the rejection log. A constant with a stable, explicit name rather than
    /// <c>ILogger&lt;Program&gt;</c>, because log aggregation alerts on it (module E) and a category
    /// derived from a type would move the moment the type does.
    /// </summary>
    internal const string LogCategory = "EmotePurge.Api.RateLimiting";

    /// <summary>
    /// The window shared by every fixed-window policy — those differ only in permit count. Also
    /// their Retry-After fallback: a full window is never too short, so a client that waits it out is
    /// always past the boundary. The token-bucket policies use their replenishment period instead.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const string PolicyItemKey = "RateLimit:Policy";
    private const string PartitionItemKey = "RateLimit:PartitionKey";
    private const string RetryAfterFallbackItemKey = "RateLimit:RetryAfterFallbackSeconds";

    /// <summary>
    /// Set by <see cref="OnRejectedAsync"/> and by nothing else, which is the whole point: a 429 alone
    /// does not say who produced it. The resync cooldown answers 429 from inside its handler, per
    /// channel, on a request the limiter waved through — counting that as a policy violation would put
    /// a standing baseline of local rejections onto the monitoring page and make the number an
    /// operator watches during an incident unreadable.
    /// </summary>
    private const string RejectedItemKey = "RateLimit:Rejected";

    /// <summary>
    /// The wait actually handed to the caller, kept for the telemetry path so the number an operator
    /// reads is the one the client got, rather than a second derivation of it that can drift.
    /// </summary>
    private const string RetryAfterItemKey = "RateLimit:RetryAfterSeconds";

    /// <summary>Route value carrying the vote session id, the second half of the Voting partition.</summary>
    private const string SessionIdRouteValue = "sessionId";

    /// <summary>
    /// The queue every policy below registers with, and therefore the value the admin snapshot
    /// (<c>GET /api/admin/rate-limits</c>) reports for every policy: zero, for every one of them. A
    /// queued request holds a connection and a thread for the length of the wait, which is how a
    /// limiter meant to shed load turns into the thing that exhausts the server — see
    /// <see cref="TokenBucketOptions"/>. Kept as one named constant rather than a literal repeated in
    /// three places so the three cannot drift apart.
    /// </summary>
    internal const int QueueLimit = 0;

    /// <summary>
    /// Partitions by the authenticated Twitch user, falling back to the remote IP and finally to a
    /// shared bucket. Runs after UseAuthentication, so the claim is there for every endpoint that
    /// requires auth.
    /// </summary>
    /// <remarks>
    /// A fixed window, kept for the three policies whose callers are machines on a cadence or writes
    /// nobody bursts — there is no interactive burst to smooth out there, so a bucket would buy
    /// nothing. See <see cref="Record"/> for what every partitioner leaves behind for the rejection.
    /// </remarks>
    public static RateLimitPartition<string> PartitionPerUser(
        HttpContext httpContext,
        string policyName,
        int permitLimit)
    {
        var partitionKey = ResolveUserKey(httpContext);
        Record(httpContext, policyName, partitionKey, (int)Window.TotalSeconds);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = Window,
            QueueLimit = QueueLimit
        });
    }

    /// <summary>
    /// Partitions by the authenticated Twitch user like <see cref="PartitionPerUser"/>, but hands out
    /// a token bucket instead of a fixed window.
    /// </summary>
    /// <remarks>
    /// A fixed window empties for everyone at the same instant and refills for everyone at the same
    /// instant, so a caller who happens to arrive late in a window is rejected for a full minute over
    /// traffic that was never theirs. A bucket that refills continuously turns the same budget into a
    /// sustained rate plus a burst allowance, which is what interactive navigation actually looks
    /// like: idle, then seven requests at once on entering a workspace.
    /// </remarks>
    public static RateLimitPartition<string> PartitionPerUserTokenBucket(
        HttpContext httpContext,
        string policyName,
        RateLimitingOptions.TokenBucketPolicy policy)
    {
        var partitionKey = ResolveUserKey(httpContext);
        Record(httpContext, policyName, partitionKey, FallbackSecondsFor(policy));

        return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => TokenBucketOptions(policy));
    }

    /// <summary>
    /// Partitions by the authenticated Twitch user *and* the vote session from the route, so that
    /// spending a voting budget in one session leaves navigation and every other session untouched.
    /// </summary>
    /// <remarks>
    /// The route value is there: <c>UseRouting</c> runs before <c>UseRateLimiter</c> — it has to,
    /// since the middleware finds the policy on the selected endpoint's metadata. Should it ever be
    /// missing anyway, this falls back to the user-only key rather than throwing: a partitioner that
    /// throws takes the request down with a 500, and the failure this guards against is a caller
    /// spending too much, not a caller sharing a budget too widely.
    /// </remarks>
    public static RateLimitPartition<string> PartitionPerUserAndVoteSessionTokenBucket(
        HttpContext httpContext,
        string policyName,
        RateLimitingOptions.TokenBucketPolicy policy)
    {
        var userKey = ResolveUserKey(httpContext);

        // The route is declared `{sessionId:long}`, and the handler binds the *parsed* number — so
        // "1", "01" and "001" are one and the same session to everything downstream of routing. Using
        // RouteValues' raw text as-is (the bug this closes) let a caller mint a fresh token bucket per
        // leading zero and vote past the configured limit indefinitely. Parsing here and re-formatting
        // invariantly collapses every equivalent spelling back onto one partition key.
        //
        // A value that fails to parse falls back to the user-only key rather than appending the raw
        // text: the `:long` route constraint already guarantees this parse cannot fail for a request
        // that reached this partitioner, so the branch is unreachable in practice (same reasoning as
        // the missing-route-value fallback below). Appending unparsed text back in would reintroduce
        // exactly the class of bug fixed here — two different raw strings the handler cannot even bind
        // silently becoming two different budgets — for no upside, since no real request can take it.
        var partitionKey = httpContext.Request.RouteValues.TryGetValue(SessionIdRouteValue, out var routeValue)
            && long.TryParse(routeValue?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionId)
            ? $"{userKey}:{sessionId.ToString(CultureInfo.InvariantCulture)}"
            : userKey;

        Record(httpContext, policyName, partitionKey, FallbackSecondsFor(policy));

        return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => TokenBucketOptions(policy));
    }

    /// <summary>
    /// Partitions by the remote IP alone, never by the authenticated user — for the one policy that
    /// must not let a signed-in visitor fall back onto a per-user budget (<see
    /// cref="RateLimitPolicyNames.Contact"/>). <see cref="PartitionPerUser"/>/
    /// <see cref="PartitionPerUserTokenBucket"/> prefer <see cref="ResolveUserKey"/>'s authenticated
    /// claim over the IP, which is right for every route that requires a login but wrong here: nothing
    /// stops an already-authenticated visitor from also submitting <c>POST /api/contact</c>, and
    /// partitioning that route by user id would hand each signed-in visitor behind one shared IP (an
    /// office, a household NAT) their own budget instead of the one shared bucket the route is meant
    /// to enforce (Codex P2, docs/DECISIONS.md 2026-09-24 revision).
    /// </summary>
    public static RateLimitPartition<string> PartitionPerIpTokenBucket(
        HttpContext httpContext,
        string policyName,
        RateLimitingOptions.TokenBucketPolicy policy)
    {
        var partitionKey = ResolveIpKey(httpContext);
        Record(httpContext, policyName, partitionKey, FallbackSecondsFor(policy));

        return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => TokenBucketOptions(policy));
    }

    /// <summary>
    /// Answers a rejected request: one warning in the log, a Retry-After header, and a body carrying
    /// a translatable error code.
    /// </summary>
    /// <remarks>
    /// The status code is already 429 when this runs — <c>RateLimiterOptions.RejectionStatusCode</c>
    /// is applied *before* the callback, which is why writing a body here is safe and why nothing
    /// below sets a status.
    /// </remarks>
    public static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var httpContext = context.HttpContext;
        var policyName = httpContext.Items[PolicyItemKey] as string ?? Unknown;
        var partitionKey = httpContext.Items[PartitionItemKey] as string ?? Unknown;

        // FixedWindowRateLimiter reports the wait to the next window boundary on a failed lease. On
        // .NET 10 that value is the whole window rather than the remainder — an over-estimate, and
        // the safe direction to be wrong in; .NET 11 makes it exact with no change here.
        // TokenBucketRateLimiter reports the periods it needs to refill the missing tokens, but the
        // metadata is not part of any contract we control: it may be absent, and a bucket rejecting
        // on a queue rather than on empty tokens can report zero. Falling back to the whole window
        // there would tell a caller to wait a minute for a token that returns in a second, so each
        // partitioner leaves behind the fallback that fits its own limiter. Never zero, whatever the
        // source — a client told to retry after zero seconds retries straight into the next 429.
        var fallbackSeconds = httpContext.Items[RetryAfterFallbackItemKey] as int? ?? (int)Window.TotalSeconds;
        var reported = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? (int)Math.Ceiling(retryAfter.TotalSeconds)
            : 0;
        var retryAfterSeconds = Math.Max(1, reported > 0 ? reported : fallbackSeconds);

        httpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        // Marks this request as the limiter's own rejection for RateLimitTelemetryMiddleware, which
        // runs on the way back out and cannot tell a 429 from here apart from a 429 from a handler.
        httpContext.Items[RejectedItemKey] = true;
        httpContext.Items[RetryAfterItemKey] = retryAfterSeconds;

        // The partition key is a Twitch user id for every authenticated caller and the remote IP only
        // for the anonymous health endpoint — both are already in the database respectively in the
        // reverse proxy's own access log, so this adds no category of data the host did not hold.
        var logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategory);
        logger.LogWarning(
            "Rate-Limit erreicht: Policy {RateLimitPolicy}, {RequestMethod} {RequestPath}, Partition {RateLimitPartition}, Retry-After {RetryAfterSeconds}s",
            policyName,
            httpContext.Request.Method,
            httpContext.Request.Path.Value,
            partitionKey,
            retryAfterSeconds);

        // Shaped like the resync cooldown's 429 (ChannelEndpoints.cs), so the frontend's existing
        // apiErrorTranslationKey handles both without a special case.
        await httpContext.Response.WriteAsJsonAsync(
            new { errorCode = ApiErrorCodes.RateLimitExceeded, retryAfterSeconds },
            cancellationToken);
    }

    /// <summary>
    /// Describes what the limiter did with this request, or <c>null</c> if it never saw it.
    /// </summary>
    /// <remarks>
    /// A request without a policy name never reached a partitioner: the route carries no
    /// <c>RequireRateLimiting</c> (worker health, SSE, admin, auth), or a filter short-circuited it
    /// before the limiter ran. Neither is a decision, and inventing one would file traffic under a
    /// budget that was never applied to it. Reading the keys here rather than in the middleware keeps
    /// them private to the class that writes them — they exist precisely because <c>OnRejectedAsync</c>
    /// gets a lease and not a policy.
    /// </remarks>
    public static RateLimitPolicyDecision? TryDescribeDecision(HttpContext httpContext, string routeTemplate)
    {
        if (httpContext.Items[PolicyItemKey] is not string policyName)
        {
            return null;
        }

        var rejected = httpContext.Items.ContainsKey(RejectedItemKey);

        return new RateLimitPolicyDecision(
            policyName,
            Accepted: !rejected,
            httpContext.Request.Method,
            routeTemplate,
            httpContext.Items[PartitionItemKey] as string ?? Unknown,
            rejected ? httpContext.Items[RetryAfterItemKey] as int? : null);
    }

    /// <summary>
    /// The authenticated Twitch user, the remote IP for the one anonymous endpoint, and a shared
    /// bucket only if even that is unavailable.
    /// </summary>
    private static string ResolveUserKey(HttpContext httpContext)
        => httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? Unknown;

    /// <summary>
    /// The remote IP alone — never the authenticated user, unlike <see cref="ResolveUserKey"/>'s own
    /// fallback. Correct only behind the same <c>ForwardedHeadersMiddleware</c> trust configuration in
    /// <c>Program.cs</c> that makes every other IP-partitioned policy correct, and reads the same
    /// property <c>ContactEndpoints</c> already reads for <c>TurnstileVerifier</c>.
    /// </summary>
    private static string ResolveIpKey(HttpContext httpContext)
        => httpContext.Connection.RemoteIpAddress?.ToString() ?? Unknown;

    /// <summary>
    /// Leaves behind everything <see cref="OnRejectedAsync"/> cannot derive on its own: the
    /// middleware hands it a lease, not a policy, and re-deriving the key or the wait there would
    /// duplicate the logic above — two copies that can drift, in the one place whose whole job is to
    /// say accurately what happened.
    /// </summary>
    private static void Record(
        HttpContext httpContext,
        string policyName,
        string partitionKey,
        int retryAfterFallbackSeconds)
    {
        httpContext.Items[PolicyItemKey] = policyName;
        httpContext.Items[PartitionItemKey] = partitionKey;
        httpContext.Items[RetryAfterFallbackItemKey] = retryAfterFallbackSeconds;
    }

    private static int FallbackSecondsFor(RateLimitingOptions.TokenBucketPolicy policy)
        => Math.Max(1, policy.ReplenishmentPeriodSeconds);

    private static TokenBucketRateLimiterOptions TokenBucketOptions(RateLimitingOptions.TokenBucketPolicy policy)
        => new()
        {
            TokenLimit = policy.TokenLimit,
            TokensPerPeriod = policy.TokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(policy.ReplenishmentPeriodSeconds),
            // No queueing: a rejected caller is told to come back, never parked. Queued requests hold
            // a connection and a thread for the length of the wait, which is how a limiter meant to
            // shed load turns into the thing that exhausts the server.
            QueueLimit = QueueLimit,
            AutoReplenishment = true,
        };
}
