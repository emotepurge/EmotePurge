using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Twitch;

public class TwitchAuthClient(HttpClient httpClient, IConfiguration configuration, ILogger<TwitchAuthClient> logger) : ITwitchAuthClient
{
    public async Task<TwitchTokenResult?> ExchangeAuthorizationCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = GetRequired("Auth:Twitch:ClientId"),
                ["client_secret"] = GetRequired("Auth:Twitch:ClientSecret"),
                ["code"] = code,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri
            };

            var response = await httpClient.PostAsync("oauth2/token", new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Twitch-Token-Exchange fehlgeschlagen mit Status {Status}.", response.StatusCode);
                return null;
            }

            return await ReadTokenResultAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch-Token-Exchange fehlgeschlagen.");
            return null;
        }
    }

    public async Task<TwitchTokenResult?> GetAppAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = GetRequired("Auth:Twitch:ClientId"),
                ["client_secret"] = GetRequired("Auth:Twitch:ClientSecret"),
                ["grant_type"] = "client_credentials"
            };

            var response = await httpClient.PostAsync("oauth2/token", new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Twitch-App-Token-Abruf fehlgeschlagen mit Status {Status}.", response.StatusCode);
                return null;
            }

            return await ReadTokenResultAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch-App-Token-Abruf fehlgeschlagen.");
            return null;
        }
    }

    public async Task<TwitchTokenRefreshResult> RefreshUserTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        try
        {
            // FormUrlEncodedContent already URL-encodes each value — Twitch refresh tokens contain
            // reserved characters ('%', '='), and encoding them a second time would corrupt them.
            var form = new Dictionary<string, string>
            {
                ["client_id"] = GetRequired("Auth:Twitch:ClientId"),
                ["client_secret"] = GetRequired("Auth:Twitch:ClientSecret"),
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            };

            var response = await httpClient.PostAsync("oauth2/token", new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Twitch answers an invalid/revoked refresh token with 400 "Invalid refresh token".
                // Matched against the message, not the status alone: a 400 caused by a malformed
                // request on our side must not destroy a possibly still-valid stored token.
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (body.Contains("invalid refresh token", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning("Twitch-Refresh endgültig abgelehnt (Invalid refresh token) — Re-Login nötig.");
                        return new TwitchTokenRefreshResult(TwitchRefreshStatus.InvalidGrant, null);
                    }
                }

                logger.LogWarning("Twitch-Refresh fehlgeschlagen mit Status {Status} — wird als transient behandelt.", response.StatusCode);
                return new TwitchTokenRefreshResult(TwitchRefreshStatus.TransientFailure, null);
            }

            var token = await ReadTokenResultAsync(response, cancellationToken);
            return token is null
                ? new TwitchTokenRefreshResult(TwitchRefreshStatus.TransientFailure, null)
                : new TwitchTokenRefreshResult(TwitchRefreshStatus.Success, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch-Refresh fehlgeschlagen — wird als transient behandelt.");
            return new TwitchTokenRefreshResult(TwitchRefreshStatus.TransientFailure, null);
        }
    }

    public async Task<bool?> ValidateTokenAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "oauth2/validate");
            request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return false;
            }

            logger.LogWarning("Twitch-Validate fehlgeschlagen mit Status {Status} — Ergebnis unbekannt.", response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Twitch-Validate fehlgeschlagen — Ergebnis unbekannt.");
            return null;
        }
    }

    public async Task<bool> RevokeTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            // FormUrlEncodedContent encodes the values itself; the token is never put in the URL.
            var form = new Dictionary<string, string>
            {
                ["client_id"] = GetRequired("Auth:Twitch:ClientId"),
                ["token"] = token
            };

            using var response = await httpClient.PostAsync("oauth2/revoke", new FormUrlEncodedContent(form), cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Twitch answers 400 for a token it does not know (already expired or revoked), which is
            // the state we wanted anyway. Logged as a warning regardless: the caller cannot tell the
            // two apart, and neither blocks anything. The token itself is never logged.
            logger.LogWarning("Twitch token revocation failed with status {Status}.", response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // InvalidOperationException: a missing Auth:Twitch:ClientId (GetRequired) — the same
            // "could not revoke" outcome, and the deletion must not fail on it.
            logger.LogWarning(ex, "Twitch token revocation failed.");
            return false;
        }
    }

    private async Task<TwitchTokenResult?> ReadTokenResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var dto = await response.Content.ReadFromJsonAsync<TwitchTokenResponseDto>(TwitchJsonOptions.Value, cancellationToken);
        if (dto is null || string.IsNullOrEmpty(dto.AccessToken))
        {
            return null;
        }

        return new TwitchTokenResult(
            dto.AccessToken,
            DateTime.UtcNow.AddSeconds(GetEffectiveExpiresIn(dto.ExpiresIn)),
            string.IsNullOrEmpty(dto.RefreshToken) ? null : dto.RefreshToken,
            dto.Scope is { Count: > 0 } ? string.Join(' ', dto.Scope) : null);
    }

    // Test-only knob (Auth:Twitch:AccessTokenLifetimeOverrideSeconds): shortens the local view of
    // the token lifetime so the refresh path can be exercised live without waiting ~4 hours.
    // Twitch's actual token lifetime is unaffected — we merely treat the token as expired earlier.
    private int GetEffectiveExpiresIn(int expiresIn)
    {
        var overrideRaw = configuration["Auth:Twitch:AccessTokenLifetimeOverrideSeconds"];
        if (int.TryParse(overrideRaw, out var seconds) && seconds > 0)
        {
            logger.LogWarning("Twitch-Token-Lebensdauer per Override auf {Seconds}s verkürzt — nur für Tests gedacht.", seconds);
            return Math.Min(seconds, expiresIn);
        }

        return expiresIn;
    }

    private string GetRequired(string key) => configuration[key]
        ?? throw new InvalidOperationException($"Konfigurationswert '{key}' fehlt.");
}
