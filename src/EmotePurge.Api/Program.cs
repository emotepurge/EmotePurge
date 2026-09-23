using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using EmotePurge.Api.Auth;
using EmotePurge.Api.Endpoints;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Local-only overrides for the `lan` launch profile (mobile LAN testing, see
// docs/Operations.md). The repo is public, so the operator's private LAN
// hostname cannot live in launchSettings.json — instead the `lan` profile sets only this flag,
// and the actual hostname lives in a gitignored appsettings.Lan.json (see
// appsettings.Lan.json.example for the one-time setup). Added last so it wins over
// appsettings.Development.json's Auth:Twitch:PostLoginRedirectUrl; harmless no-op for every
// other profile, where the flag is unset and the file is never read.
if (builder.Configuration.GetValue<bool>("EMOTEPURGE_LAN"))
{
    builder.Configuration.AddJsonFile("appsettings.Lan.json", optional: true, reloadOnChange: true);
}

builder.Services.AddOpenApi();

builder.Services.AddEmotePurgeInfrastructure(builder.Configuration);

// Without a shared key ring every container restart invalidates every auth cookie, and two API
// replicas would reject each other's logins outright. Only enabled when a path is configured
// (docker-compose sets DataProtection__KeyPath=/keys onto a named volume) — local `dotnet run`
// keeps the default per-user key store, which already persists.
var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
        .SetApplicationName("EmotePurge");
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;

        // Secure regardless of what the proxy sends. The default SameAsRequest derives the flag from
        // Request.IsHttps, which in this topology exists *only* because ForwardedHeadersMiddleware
        // saw an X-Forwarded-Proto that the app cannot guarantee: a replaced reverse proxy or a new
        // vhost missing `proxy_set_header X-Forwarded-Proto` would silently start handing out a
        // cookie without Secure — carrying the Twitch access token and all of the victim's
        // broadcaster/mod rights. Fail closed: a missing header now breaks login visibly instead of
        // working insecurely. Browsers treat http://localhost as trustworthy, so dev is unaffected.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;

        // Default cookie-auth behavior redirects to a login page on 401/403 — wrong for a JSON API.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };

        // Server-side revocation. Costs one primary-key lookup per authenticated request, which buys
        // the only way to invalidate an issued cookie: before this, logout deleted the browser's
        // copy while the cookie itself stayed valid for its full 14 days.
        options.Events.OnValidatePrincipal = async context =>
        {
            var twitchUserId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var issuedAtRaw = context.Principal?.FindFirstValue(TwitchClaimTypes.SessionIssuedAtUtc);

            // A cookie without the claim predates session tracking. Rejected rather than
            // grandfathered in — accepting it would leave a permanent bypass of the check.
            if (twitchUserId is null
                || !DateTime.TryParse(issuedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issuedAt))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var userService = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
            var validFrom = await userService.GetSessionsValidFromUtcAsync(twitchUserId, context.HttpContext.RequestAborted);
            if (validFrom is { } revokedBefore && issuedAt.ToUniversalTime() < revokedBefore)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();

// The local policies guard this API against loops and abuse. They deliberately do *not* model what a
// request costs at Twitch or 7TV: the request count and the provider cost are unrelated quantities
// here — a plain database read spends the same permit as /channels/mine, which may page through ten
// Helix responses — so a budget shaped like a provider quota is a made-up number that fires on the
// wrong traffic. Provider cost is answered by caches and, from step 4 of the rate-limit plan, by
// observation; for no investigated case is a provider 429 on record, while every 429 users actually
// hit came from the policy right here (issues #33/#35).
// All budgets come from configuration (RateLimiting section, RateLimitingOptions) and can be moved
// per environment variable and restart — the numbers below are no longer compiled in.
var rateLimits = new RateLimitingOptions();
builder.Configuration.GetSection(RateLimitingOptions.SectionName).Bind(rateLimits);
// Fail fast, like the S3-34 migration guard further down: a capacity of zero from a mistyped
// environment variable is not a lax limiter but a total outage of every route the policy guards,
// and one that looks exactly like a genuine rate-limit incident in the log.
rateLimits.Validate();

// Exposed as IOptions so the read-only admin snapshot (GET /api/admin/rate-limits) can report the
// effective, already-validated configuration without binding the section a second time.
builder.Services.AddSingleton<IOptions<RateLimitingOptions>>(Options.Create(rateLimits));

// Plain singleton, not configuration-bound: see LiveStreamKeepaliveOptions for why (issue #128).
builder.Services.AddSingleton<LiveStreamKeepaliveOptions>();

// The DELETE /api/live/connections/{connectionId} endpoint's backing store (issue #128) — one
// instance shared by every request, like the options above. See LiveStreamConnectionRegistry.
builder.Services.AddSingleton<LiveStreamConnectionRegistry>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Until 2026-08-29 there was none, and a rejected request left no trace anywhere on this side —
    // see RateLimitRejection for what that cost.
    options.OnRejected = RateLimitRejection.OnRejectedAsync;

    // Each helper names the policy exactly once. The partitioner needs the name for the rejection
    // log, and AddPolicy does not hand it over.
    void AddTokenBucketPolicy(string policyName, RateLimitingOptions.TokenBucketPolicy policy) =>
        options.AddPolicy(policyName, httpContext =>
            RateLimitRejection.PartitionPerUserTokenBucket(httpContext, policyName, policy));

    void AddFixedWindowPolicy(string policyName, RateLimitingOptions.FixedWindowPolicy policy) =>
        options.AddPolicy(policyName, httpContext =>
            RateLimitRejection.PartitionPerUser(httpContext, policyName, policy.PermitLimit));

    // Ordinary navigation: /channels/mine, channel status, permissions, usage stats, emote reads,
    // vote lists and vote results. Generous on purpose — entering a workspace costs seven requests
    // in one burst, and the app is in a test phase where a false local rejection hurts more than the
    // extra load ever could. A bucket rather than a window because that burst is exactly what a
    // fixed window cannot tell apart from a loop.
    AddTokenBucketPolicy(RateLimitPolicyNames.InteractiveRead, rateLimits.InteractiveRead);

    // Vote mutations, partitioned per user *and* vote session: a session someone is clicking through
    // must not be able to lock that user out of navigation or of a second session.
    options.AddPolicy(RateLimitPolicyNames.Voting, httpContext =>
        RateLimitRejection.PartitionPerUserAndVoteSessionTokenBucket(
            httpContext, RateLimitPolicyNames.Voting, rateLimits.Voting));

    // Bookkeeping against our own database with no downstream cost. Deliberately its own policy:
    // sync-deleted is the one call that must never be dropped — a 429 there leaves the database
    // diverging from 7TV with no signal, because the deletion already happened over there.
    AddFixedWindowPolicy(RateLimitPolicyNames.Bookkeeping, rateLimits.Bookkeeping);

    // Far stricter, for the one endpoint a user can trigger that costs an unconditional 7TV call
    // *and* fans a live event out to every open page of the channel. It is only half the guard: this
    // partitions per user, so fifteen moderators of one channel would still get fifteen budgets —
    // the per-channel half is IChannelResyncCooldown. Neither mechanism covers the other's case.
    AddFixedWindowPolicy(RateLimitPolicyNames.ChannelResync, rateLimits.ChannelResync);

    // For the payload-free GET /api/health: public and anonymous, so this always partitions by IP
    // (PartitionPerUser falls back to it). One Redis read per hit — cheap, but unauthenticated,
    // and its legitimate callers are machines on fixed cadences: the container HEALTHCHECK
    // (every 30 s, from localhost) and the external uptime monitor (every 60 s).
    AddFixedWindowPolicy(RateLimitPolicyNames.PublicHealth, rateLimits.PublicHealth);

    // GET /api/seventv/channels/{channelName}/emotes (foreign-channel-import spec, E5a): any
    // logged-in user, any Twitch channel, no role required — see the group's own comment for why that
    // is deliberate. Stricter than InteractiveRead because unlike an ordinary navigation read this
    // call always costs 7TV a real round trip (up to ten paginated pages), closer in shape to
    // ChannelResync. This is only the per-user half of the guard (E5a); the provider-wide budget
    // across all users (E5b) is a separate, in-process concern the hardening decorator around
    // IForeignEmoteSetService owns, not a policy here.
    AddFixedWindowPolicy(RateLimitPolicyNames.ForeignEmoteLookup, rateLimits.ForeignEmoteLookup);

    // GET /api/seventv/leaderboard (7TV-leaderboard-as-import-source spec 2026-09-13, E16): unlike
    // ForeignEmoteLookup above this call never costs 7TV a round trip directly — it reads an
    // in-process stock guarded by its own window budget and circuit breaker — but its response runs
    // to tens of KB, so it gets its own policy rather than sharing InteractiveRead's.
    AddFixedWindowPolicy(RateLimitPolicyNames.SevenTvLeaderboard, rateLimits.SevenTvLeaderboard);
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Without this an unhandled exception aborts the response body-less, so the frontend cannot tell a
// crash apart from a network failure and shows its generic message. Concretely reachable: two
// concurrent votes racing the (VoteSessionId, EmoteId, UserId) unique index. Deliberately no
// exception detail in the body — only a stable errorCode the frontend can translate.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { errorCode = ApiErrorCodes.UnexpectedError });
}));

// Behind a host-level reverse proxy (TLS termination) the connection reaches the container through
// the Docker bridge gateway address, not through loopback, so the middleware's default trust list
// (loopback only) would drop X-Forwarded-Proto and X-Forwarded-For even though the proxy sets both.
//
// This block used to read `KnownIPNetworks = { }, KnownProxies = { }` and was believed to clear both
// lists. It does not: both properties are get-only collections, so that is a collection initializer
// adding zero elements — the loopback defaults stayed in place and every forwarded header was
// silently discarded from 2026-07-26 until 2026-09-16 (see the DECISIONS entry of that date).
//
// The fix deliberately widens the trust list instead of emptying it. Emptying it means trusting any
// sender, which is only ever as safe as the port binding; adding the network keeps the middleware
// checking. The gateway address itself is not stable enough to pin (Docker assigns it per network),
// Docker's default address pool is: 172.16.0.0/12 covers every bridge network Compose creates here.
// The loopback defaults stay on purpose — local `dotnet run` behind the Vite dev proxy arrives from
// 127.0.0.1, and dropping them would break that path.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
// Fully qualified on purpose: the obsolete Microsoft.AspNetCore.HttpOverrides.IPNetwork is in scope
// through the using above and makes the bare name ambiguous. KnownIPNetworks wants the BCL type.
forwardedHeadersOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
app.UseForwardedHeaders(forwardedHeadersOptions);

// Deliberately no UseHttpsRedirection(): Kestrel only listens on http://+:8080 inside the container
// and no ASPNETCORE_HTTPS_PORT is set, so it was a no-op that merely suggested protection it never
// provided. TLS is terminated by the host reverse proxy; HSTS below is what actually enforces it.

// Security headers on every response. CSP allow-lists are deliberately narrow: connect-src covers
// the frontend's direct browser calls to 7TV (mass-delete GraphQL mutations bypass the API on
// purpose, s. CLAUDE.md "Zero-Knowledge für Schreib-Tokens"), img-src covers the 7TV CDN that
// serves emote preview images embedded via Emote.ImageUrl, plus Twitch's own CDN for the account
// menu's avatar. Without that second host no picture loads at all, whatever the claim says.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    // Review open question 17: API responses are per-user, cookie-authenticated data and must
    // never be served from a shared cache — the API itself never sent any Cache-Control, leaving
    // the decision to whatever proxy sits in front. Static assets are unaffected: their headers
    // are set later by ApplyStaticCacheHeaders, and they never live under /api/.
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        headers.CacheControl = "no-store";
    }
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // Set as a plain response header rather than via app.UseHsts(), which takes the same unreliable
    // Request.IsHttps detour as the cookie flag did. Without HSTS, a user typing "emotepurge.app"
    // without a scheme makes the first request over http:// — enough for an sslstrip attacker on an
    // open network to read the session cookie once. Browsers ignore this header over plain http, so
    // local development is unaffected. Deliberately no includeSubDomains and no preload: both are
    // effectively irreversible for a year once cached, and would also bind subdomains this app knows
    // nothing about. Adding them later is safe; removing them is not.
    headers["Strict-Transport-Security"] = "max-age=31536000";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https://*.7tv.app https://7tv.io https://static-cdn.jtvnw.net; " +
        "connect-src 'self' https://7tv.io; " +
        "font-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";
    await next();
});

// Angular fingerprints its bundles — eight hash characters before the extension, in two different
// alphabets depending on whether esbuild emitted an entry point (main-4HRH6XFH.js) or a lazy chunk
// (chunk-8nP_m4am.js). A pattern covering only one of the two would leave the other uncached.
// Deliberately restricted to .js/.css: favicon.ico, robots.txt and the i18n JSONs sit right next to
// them in wwwroot with stable, unhashed names and must never be frozen for a year.
var fingerprintedAsset = new Regex(@"-[A-Za-z0-9_-]{8}\.(js|css)$", RegexOptions.Compiled);

// Without an explicit Cache-Control, ASP.NET Core sends only ETag/Last-Modified, and browsers then
// apply heuristic freshness — reusing a file for a fraction of its age without revalidating at all.
// That shipped stale translations to production on 2026-07-30 (/i18n/de.json has a stable URL, so a
// cached copy simply lacked the newly added keys and Transloco rendered the raw key names). A stale
// index.html is the more serious case: it references hashed bundles that no longer exist after a
// redeploy, and since we send X-Content-Type-Options: nosniff, the SPA fallback's text/html response
// for those bundles is refused outright rather than misparsed — a blank page instead of a stale one.
// "no-cache" does not forbid caching; it requires revalidation, which the existing ETag answers with
// an empty 304.
void ApplyStaticCacheHeaders(StaticFileResponseContext context)
{
    var path = context.Context.Request.Path.Value ?? string.Empty;
    context.Context.Response.Headers.CacheControl = fingerprintedAsset.IsMatch(path)
        ? "public, max-age=31536000, immutable"
        : "no-cache";
}

app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ApplyStaticCacheHeaders });
app.UseAuthentication();
app.UseAuthorization();

// Outside the limiter on purpose, and it measures on the way back out: the policy name and partition
// are left behind by the partitioner on the way in, the rejection marker by OnRejectedAsync on the
// way out, and neither is visible from inside. Step 4 of the rate-limit plan — this only counts, it
// never decides: there is no observe/enforce switch and no reservation anywhere in this path.
app.UseMiddleware<RateLimitTelemetryMiddleware>();
app.UseRateLimiter();

app.MapChannelEndpoints();
app.MapEmoteEndpoints();
app.MapUsageStatsEndpoints();
app.MapVoteSessionEndpoints();
app.MapAuthEndpoints();
app.MapWorkerHealthEndpoints();
app.MapAdminEndpoints();
app.MapLiveEndpoints();
app.MapSevenTvEndpoints();
app.MapLegalEndpoints();

app.MapFallback("/api/{**rest}", () => Results.NotFound());
// Needs the options passed separately: the SPA fallback serves index.html through its own endpoint,
// not through the static-file middleware configured above, so it would otherwise stay uncontrolled —
// which is exactly the file that must not go stale.
app.MapFallbackToFile("index.html", new StaticFileOptions { OnPrepareResponse = ApplyStaticCacheHeaders });

// S3-34 fail-fast: migrations are applied manually in this project, and a container running
// against a database that is missing one used to start "healthy" and answer with silent 500s.
// Crashing here makes the forgotten migration visible as a restart loop instead.
await using (var migrationScope = app.Services.CreateAsyncScope())
{
    await migrationScope.ServiceProvider.GetRequiredService<IPendingMigrationGuard>()
        .EnsureNoPendingMigrationsAsync();
}

await app.RunAsync();
