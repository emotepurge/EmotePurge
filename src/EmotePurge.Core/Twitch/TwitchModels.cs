namespace EmotePurge.Core.Twitch;

// RefreshToken/Scopes are null when Twitch omitted them from the token response; Scopes is the
// space-joined form of the response's scope array (same shape as the authorize request parameter).
public record TwitchTokenResult(string AccessToken, DateTime ExpiresAtUtc, string? RefreshToken = null, string? Scopes = null);

// InvalidGrant is Twitch's definitive "this refresh token is dead" answer (HTTP 400,
// "Invalid refresh token") — the stored tokens must be dropped and only a fresh login helps.
// TransientFailure is everything else (5xx, timeout, malformed response): keep the refresh
// token and try again on the next demand.
public enum TwitchRefreshStatus
{
    Success,
    InvalidGrant,
    TransientFailure
}

public record TwitchTokenRefreshResult(TwitchRefreshStatus Status, TwitchTokenResult? Token);

public static class TwitchOAuthDefaults
{
    // Space-delimited scope list sent to id.twitch.tv/oauth2/authorize at login. Also the
    // reference for scope-drift detection: a stored token pair granted with fewer scopes than
    // this cannot be repaired by refreshing — scopes are only ever granted in the authorize flow.
    public const string RequestedScopes = "user:read:moderated_channels user:read:subscriptions";
}

// ProfileImageUrl is nullable because it is optional to us, not to Twitch: an account without a
// custom picture still gets a default URL, but a session created before this field existed carries
// no claim for it. The avatar falls back to a monogram in that case.
public record TwitchUserInfo(string Id, string Login, string DisplayName, string? ProfileImageUrl = null);

public record TwitchModeratedChannelInfo(string Login, string BroadcasterId);

// One currently live stream from GET /helix/streams. UserLogin is Twitch's lowercase login, i.e.
// already in ChannelName.Normalize form. StartedAtUtc is carried for the eventual per-stream
// accounting (A10 Stufe 2), even though the per-day coverage only needs "was live".
public record TwitchStreamInfo(string UserLogin, DateTime StartedAtUtc);

// One identity from GET /helix/users, resolved by id or by login for identity reconciliation
// (rename tracking, id backfill). Login is Twitch's lowercase login, but the caller normalizes it
// anyway (Regel 9) rather than relying on Twitch always sending it that way. Id is the immutable,
// non-normalized numeric Twitch account id.
public record TwitchUserIdentity(string Id, string Login);
