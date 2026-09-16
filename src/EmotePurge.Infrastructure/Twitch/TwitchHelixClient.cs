using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EmotePurge.Core.Twitch;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.Twitch;

public class TwitchHelixClient(HttpClient httpClient, ILogger<TwitchHelixClient> logger) : ITwitchHelixClient
{
    private const string BearerScheme = "Bearer";

    private const int MaxModeratedChannelPages = 10;
    private const int MaxStreamsLoginsPerRequest = 100;
    private const int MaxUsersParametersPerRequest = 100;

    public async Task<TwitchUserInfo?> GetUserInfoAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "users");
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Twitch Get Users fehlgeschlagen mit Status {Status}.", response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<TwitchGetUsersResponseDto>(TwitchJsonOptions.Value, cancellationToken);
            var user = dto?.Data.FirstOrDefault();
            return user is null ? null : new TwitchUserInfo(user.Id, user.Login, user.DisplayName, user.ProfileImageUrl);
        }
        // See GetUsersAsync below: a malformed/truncated 200 body (JsonException) or a 200 answer
        // with the wrong content type (NotSupportedException) is the same kind of "Helix
        // unavailable" outage as a transport failure and must land here too, not bubble up as an
        // unhandled 500.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "Twitch Get Users fehlgeschlagen.");
            return null;
        }
    }

    public async Task<IReadOnlyList<TwitchModeratedChannelInfo>?> GetModeratedChannelsAsync(string accessToken, string twitchUserId, CancellationToken cancellationToken = default)
    {
        var channels = await FetchModeratedChannelsAsync(accessToken, twitchUserId, cancellationToken);
        return channels?.Select(c => new TwitchModeratedChannelInfo(c.BroadcasterLogin, c.BroadcasterId)).ToList();
    }

    private async Task<List<TwitchModeratedChannelDto>?> FetchModeratedChannelsAsync(string accessToken, string twitchUserId, CancellationToken cancellationToken)
    {
        var channels = new List<TwitchModeratedChannelDto>();
        string? cursor = null;

        try
        {
            for (var page = 0; page < MaxModeratedChannelPages; page++)
            {
                var url = $"moderation/channels?user_id={twitchUserId}&first=100";
                if (cursor is not null)
                {
                    url += $"&after={cursor}";
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);

                var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Twitch Get Moderated Channels fehlgeschlagen mit Status {Status}.", response.StatusCode);
                    return null;
                }

                var dto = await response.Content.ReadFromJsonAsync<TwitchGetModeratedChannelsResponseDto>(TwitchJsonOptions.Value, cancellationToken);
                if (dto is null)
                {
                    return null;
                }

                channels.AddRange(dto.Data);

                cursor = dto.Pagination?.Cursor;
                if (string.IsNullOrEmpty(cursor))
                {
                    break;
                }
            }

            if (!string.IsNullOrEmpty(cursor))
            {
                // Page cap reached while Helix still offers more: report a failure instead of a
                // silently truncated list. Unreachable below 1000 moderated channels today, but a
                // partial list must never be mistaken for a complete one — it would be cached, and
                // every channel past the cut would read as "not moderated" for the whole TTL.
                logger.LogWarning(
                    "Twitch Get Moderated Channels für User {UserId} nach {Pages} Seiten abgebrochen, obwohl weitere Seiten vorliegen — melde Fehlschlag statt Teilergebnis.",
                    twitchUserId, MaxModeratedChannelPages);
                return null;
            }

            return channels;
        }
        // See GetUsersAsync below for why JsonException/NotSupportedException join the transport
        // exceptions here. The whole loop sits inside this one try, so a broken page anywhere in
        // the pagination — not just the first — fails the entire call instead of returning a
        // truncated list silently mistaken for a complete one.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "Twitch Get Moderated Channels fehlgeschlagen für User {UserId}.", twitchUserId);
            return null;
        }
    }

    public async Task<bool?> GetUserSubscriptionStatusAsync(string accessToken, string broadcasterTwitchId, string userTwitchId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"subscriptions/user?broadcaster_id={broadcasterTwitchId}&user_id={userTwitchId}");
            request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);

            var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Twitch Get User Subscription fehlgeschlagen mit Status {Status}.", response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<TwitchGetUserSubscriptionResponseDto>(TwitchJsonOptions.Value, cancellationToken);
            return dto?.Data.Count > 0;
        }
        // See GetUsersAsync below for why JsonException/NotSupportedException join the transport
        // exceptions here.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "Twitch Get User Subscription fehlgeschlagen für User {UserId}/Broadcaster {BroadcasterId}.", userTwitchId, broadcasterTwitchId);
            return null;
        }
    }

    public async Task<IReadOnlyList<TwitchStreamInfo>?> GetLiveStreamsByLoginsAsync(
        IReadOnlyCollection<string> userLogins, string accessToken, CancellationToken cancellationToken = default)
    {
        var streams = new List<TwitchStreamInfo>();

        try
        {
            // Helix caps user_login at 100 values per request; no pagination needed inside a batch,
            // because a 100-login filter can never yield more than 100 live streams.
            foreach (var batch in userLogins.Chunk(MaxStreamsLoginsPerRequest))
            {
                var url = "streams?first=100&" + string.Join('&', batch.Select(login => $"user_login={Uri.EscapeDataString(login)}"));

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);

                var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Twitch Get Streams fehlgeschlagen mit Status {Status}.", response.StatusCode);
                    return null;
                }

                var dto = await response.Content.ReadFromJsonAsync<TwitchGetStreamsResponseDto>(TwitchJsonOptions.Value, cancellationToken);
                if (dto is null)
                {
                    return null;
                }

                streams.AddRange(dto.Data.Select(s =>
                    new TwitchStreamInfo(s.UserLogin, DateTime.SpecifyKind(s.StartedAt, DateTimeKind.Utc))));
            }

            return streams;
        }
        // See GetUsersAsync below for why JsonException/NotSupportedException join the transport
        // exceptions here. The whole batch loop sits inside this one try, so a broken batch
        // anywhere fails the entire call instead of returning a truncated live-stream list.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "Twitch Get Streams fehlgeschlagen.");
            return null;
        }
    }

    public async Task<IReadOnlyList<TwitchUserIdentity>?> GetUsersAsync(
        IReadOnlyCollection<string> ids, IReadOnlyCollection<string> logins, string accessToken, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0 && logins.Count == 0)
        {
            // An empty id/login filter isn't "nothing to resolve" to Helix — it falls back to the
            // token owner. That would silently resolve the wrong account, so short-circuit instead.
            return [];
        }

        var identities = new List<TwitchUserIdentity>();

        try
        {
            // Helix caps id and login together at 100 values per request, not 100 each — a batch
            // can therefore mix both kinds of parameters.
            var parameters = ids.Select(id => (Key: "id", Value: id))
                .Concat(logins.Select(login => (Key: "login", Value: login)));

            foreach (var batch in parameters.Chunk(MaxUsersParametersPerRequest))
            {
                var url = "users?" + string.Join('&', batch.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, accessToken);

                var response = await httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Twitch Get Users (Identities) fehlgeschlagen mit Status {Status}.", response.StatusCode);
                    return null;
                }

                var dto = await response.Content.ReadFromJsonAsync<TwitchGetUserIdentitiesResponseDto>(TwitchJsonOptions.Value, cancellationToken);
                if (dto is null)
                {
                    return null;
                }

                identities.AddRange(dto.Data.Select(u => new TwitchUserIdentity(u.Id, u.Login)));
            }

            return identities;
        }
        // JoinAsync and the periodic identity reconciliation both treat a null result as
        // "Helix unavailable, carry on as before" — a status on our side that costs nothing but the
        // resolved id. A malformed or truncated 200 body (JsonException) and a 200 answer with the
        // wrong content type, e.g. an HTML error page from an intermediary (NotSupportedException),
        // are exactly that kind of outage and must land here too, not bubble up as an unhandled 500.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "Twitch Get Users (Identities) fehlgeschlagen.");
            return null;
        }
    }
}
