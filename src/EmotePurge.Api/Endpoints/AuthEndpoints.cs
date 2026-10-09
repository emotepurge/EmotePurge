using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using EmotePurge.Api.Auth;
using EmotePurge.Api.RateLimiting;
using EmotePurge.Api.Validation;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace EmotePurge.Api.Endpoints;

public static class AuthEndpoints
{
    private const string OAuthStateCookieName = "ep_oauth_state";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Deliberately no rate-limit policy: a budget on the login path locks people out of the app
        // over the one request they cannot retry their way around, and a rejected callback loses the
        // OAuth state cookie with it. Abuse here is bounded by Twitch's own authorize flow.
        var group = app.MapGroup("/api/auth");

        group.MapGet("/twitch/login", (HttpContext httpContext, IConfiguration configuration) =>
        {
            var clientId = configuration["Auth:Twitch:ClientId"]
                ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:ClientId' fehlt.");
            var redirectUri = configuration["Auth:Twitch:RedirectUri"]
                ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:RedirectUri' fehlt.");

            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            httpContext.Response.Cookies.Append(OAuthStateCookieName, state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,

                // Hard true, not Request.IsHttps — the same fail-closed reasoning the session cookie
                // got in S2-10 (CookieSecurePolicy.Always, Program.cs). IsHttps only ever becomes
                // true here because ForwardedHeadersMiddleware saw an X-Forwarded-Proto the app
                // cannot guarantee: a replaced reverse proxy, a new vhost missing
                // `proxy_set_header X-Forwarded-Proto`, or — as was the case until 2026-09-16 — a
                // middleware that silently distrusts the proxy would hand out the OAuth state
                // cookie without Secure, which is the CSRF defence of the whole login flow.
                // Browsers treat http://localhost as a trustworthy origin and store Secure cookies
                // there, so plain-HTTP local development is unaffected; this cookie was simply
                // overlooked when the session cookie was hardened.
                Secure = true,
                Expires = DateTimeOffset.UtcNow.AddMinutes(5)
            });

            var authorizeUrl = "https://id.twitch.tv/oauth2/authorize" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                "&response_type=code" +
                $"&scope={Uri.EscapeDataString(TwitchOAuthDefaults.RequestedScopes)}" +
                $"&state={state}";

            return Results.Redirect(authorizeUrl);
        });

        group.MapGet("/twitch/callback", async (
            HttpContext httpContext,
            string? code,
            string? state,
            IConfiguration configuration,
            ITwitchAuthClient authClient,
            ITwitchHelixClient helixClient,
            IUserService userService,
            CancellationToken ct) =>
        {
            var expectedState = httpContext.Request.Cookies[OAuthStateCookieName];
            httpContext.Response.Cookies.Delete(OAuthStateCookieName);

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || expectedState is null || state != expectedState)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.InvalidOAuthState });
            }

            var redirectUri = configuration["Auth:Twitch:RedirectUri"]
                ?? throw new InvalidOperationException("Konfigurationswert 'Auth:Twitch:RedirectUri' fehlt.");

            var token = await authClient.ExchangeAuthorizationCodeAsync(code, redirectUri, ct);
            if (token is null)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.TwitchTokenExchangeFailed });
            }

            var userInfo = await helixClient.GetUserInfoAsync(token.AccessToken, ct);
            if (userInfo is null)
            {
                return Results.BadRequest(new { errorCode = ApiErrorCodes.TwitchUserInfoUnavailable });
            }

            await userService.UpsertLoginAsync(userInfo.Id, userInfo.Login, userInfo.DisplayName, ct);

            // Stored server-side (encrypted) so the on-demand refresh flow can outlive the ~4h
            // access-token claim below. If Twitch ever omits the refresh token, any previously
            // stored one is kept — it may still be valid.
            if (token.RefreshToken is not null)
            {
                await userService.StoreTwitchTokensAsync(
                    userInfo.Id, token.AccessToken, token.ExpiresAtUtc, token.RefreshToken, token.Scopes, ct);
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userInfo.Id),
                new(TwitchClaimTypes.Login, userInfo.Login),
                new(TwitchClaimTypes.DisplayName, userInfo.DisplayName),
                new(TwitchClaimTypes.AccessToken, token.AccessToken),
                new(TwitchClaimTypes.TokenExpiresAtUtc, token.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                // Compared against User.SessionsValidFromUtc on every request (OnValidatePrincipal).
                // One second of slack: the claim is written before SignInAsync, and a logout landing
                // in the same second must not invalidate the session being created right now.
                new(TwitchClaimTypes.SessionIssuedAtUtc,
                    DateTime.UtcNow.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture))
            };
            // Conditionally, because Claim's constructor rejects a null value — and an account
            // Twitch answered for without a picture URL is a legitimate outcome, not an error.
            if (!string.IsNullOrEmpty(userInfo.ProfileImageUrl))
            {
                claims.Add(new Claim(TwitchClaimTypes.ProfileImageUrl, TwitchProfileImage.ToAvatarSize(userInfo.ProfileImageUrl)));
            }
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            // Without IsPersistent the cookie carries no Max-Age and dies with the browser session,
            // silently capping every login at "until the browser closes" instead of the configured
            // 14-day sliding expiration.
            await httpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });

            var postLoginRedirectUrl = configuration["Auth:Twitch:PostLoginRedirectUrl"] ?? "/";
            return Results.Redirect(postLoginRedirectUrl);
        });

        group.MapGet("/me", (ClaimsPrincipal user, IChannelAccessService channelAccessService) =>
        {
            // Pure config lookup (Auth:AdminTwitchUserIds) — no DB, no HTTP, safe to do per request.
            // Lets the frontend gate its admin area off the cached /me instead of probing an
            // admin-only endpoint and reading its 403 as a permission bit.
            var principal = user.TryBuildTwitchPrincipal();

            return Results.Ok(new
            {
                twitchUserId = user.FindFirstValue(ClaimTypes.NameIdentifier),
                login = user.FindFirstValue(TwitchClaimTypes.Login),
                displayName = user.FindFirstValue(TwitchClaimTypes.DisplayName),
                profileImageUrl = user.FindFirstValue(TwitchClaimTypes.ProfileImageUrl),
                tokenExpiresAtUtc = user.FindFirstValue(TwitchClaimTypes.TokenExpiresAtUtc),
                isGlobalAdmin = principal is not null && channelAccessService.IsGlobalAdmin(principal)
            });
        }).RequireAuthorization();

        group.MapDelete("/me", async (
            HttpContext httpContext,
            ClaimsPrincipal user,
            string? expectedTwitchUserId,
            IUserService userService,
            IAccountDeletionService accountDeletionService,
            ITwitchAuthClient authClient,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            // The id comes from the session claim and from nowhere else — no route value, no body — so
            // a caller can only ever delete the account their own cookie belongs to.
            var actor = user.TryBuildAuditActor();
            if (actor is null)
            {
                // Unreachable behind RequireAuthorization for a real session; guard, not a case.
                return Results.Unauthorized();
            }

            // The client names the account whose login the user just retyped. The cookie is shared
            // across tabs, so another tab may have signed in as someone else since: deleting "the
            // session's account" would then erase an account the user never confirmed. Missing counts
            // as a mismatch (409, not 400): the only caller without it is a stale cached client
            // bundle, and for it the right remedy is the same reload the mismatch notice asks for.
            if (!string.Equals(expectedTwitchUserId, actor.TwitchUserId, StringComparison.Ordinal))
            {
                return Results.Conflict(new { errorCode = ApiErrorCodes.AccountMismatch });
            }

            // Read before the deletion, because the row (and with it the encrypted tokens) is gone
            // afterwards. Revoked only after the commit, though: a deletion that fails must not leave
            // the user logged in with tokens Twitch has already invalidated.
            TwitchStoredTokens? stored = null;
            try
            {
                stored = await userService.GetTwitchTokensAsync(actor.TwitchUserId, ct);
            }
            catch (InvalidOperationException ex)
            {
                // The cipher cannot decrypt the stored tokens (rotated or lost key). Narrow on purpose:
                // a database failure or a cancellation still fails the request, since nothing has been
                // deleted yet. Here the ciphertext is about to be deleted anyway, so the user must not
                // be locked into an account they cannot erase; only the revocation of those two tokens
                // is skipped. The exception is the cipher's own and carries no token or ciphertext.
                logger.LogWarning(
                    ex,
                    "Stored Twitch tokens could not be read; account deletion proceeds, token revocation skipped (Twitch user {TwitchUserId})",
                    actor.TwitchUserId);
            }

            // SelfRequest, so onlyIfInactiveBeforeUtc is null: unconditional, active or not. The service
            // records the marker as actor on the user.delete entry when actor and target are the same.
            var result = await accountDeletionService.DeleteAsync(
                actor.TwitchUserId, actor, AccountDeletionReason.SelfRequest, onlyIfInactiveBeforeUtc: null, ct);
            if (result.Outcome == AccountDeletionOutcome.Deleted)
            {
                await RevokeTwitchTokensBestEffortAsync(
                    authClient, user.FindFirstValue(TwitchClaimTypes.AccessToken), stored);
            }

            // NotFound is answered like Deleted: the row vanished between the session check and the
            // deletion (a concurrent admin deletion), which is the state the caller asked for, and the
            // cookie still has to be cleared. This branch is a narrow race, not the retry path: once
            // the row is gone, the cookie scheme's OnValidatePrincipal rejects the session before the
            // handler runs, and a retry or double submit answers 410 (SessionRejection) — 401 stays
            // reserved for causes that do not prove deletion. StillActive cannot occur for SelfRequest.
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return result.Outcome is AccountDeletionOutcome.Deleted or AccountDeletionOutcome.NotFound
                ? Results.NoContent()
                : Results.Problem();
        })
        .RequireAuthorization()
        .RequireRateLimiting(RateLimitPolicyNames.Bookkeeping);

        group.MapPost("/logout", async (
            HttpContext httpContext,
            IUserService userService,
            CancellationToken ct) =>
        {
            // Revoke server-side as well, not just delete the browser's copy: the cookie stays
            // cryptographically valid otherwise, and with persisted Data Protection keys nothing
            // else would ever invalidate it. Deliberately not behind RequireAuthorization — an
            // already-expired session must still be able to clear its cookie.
            var twitchUserId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (twitchUserId is not null)
            {
                // actor: null — self-logout is deliberately not audited (no login/logout events).
                await userService.RevokeSessionsAsync(twitchUserId, actor: null, ct);
                // Logout is already global per user (the revocation above kills every session), so
                // the server also gives up its ability to refresh Twitch tokens on this user's behalf.
                await userService.ClearTwitchTokensAsync(twitchUserId, ct);
            }

            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });
    }

    // Every token that could still be used on this user's behalf: the cookie claim and the two stored
    // ones (usually the claim equals the stored access token, hence the distinct set). Failures are
    // logged by the client and deliberately ignored here — Twitch being unreachable must not keep a
    // user from erasing their data. Revocation is best-effort: the stored ciphertext is deleted with
    // the row either way, but a token Twitch was not told about stays valid on its side (refresh tokens
    // do not expire on their own). The calls run in parallel, each isolated, so the request waits for
    // the slowest one (bounded by the client's timeout) rather than their sum.
    private static async Task RevokeTwitchTokensBestEffortAsync(
        ITwitchAuthClient authClient, string? claimAccessToken, TwitchStoredTokens? stored)
    {
        var tokens = new[] { claimAccessToken, stored?.AccessToken, stored?.RefreshToken }
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList();

        // CancellationToken.None: the deletion has committed, so a caller that goes away now must
        // not leave a token unrevoked. Bounded by the typed client's 10 s timeout.
        await Task.WhenAll(tokens.Select(token => authClient.RevokeTokenAsync(token!, CancellationToken.None)));
    }
}
