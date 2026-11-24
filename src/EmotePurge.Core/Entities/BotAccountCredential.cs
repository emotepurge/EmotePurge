namespace EmotePurge.Core.Entities;

/// <summary>
/// The bot account's single credential row (#487; CHECK <c>Id = 1</c>). It is account-wide and not tied to wishes.
/// There is no seed row: while it is missing the status is <see cref="BotAccountStatus.None"/> and the kill switch is
/// off; every write is an upsert on <see cref="SingletonId"/>. It holds the sealed 7TV token and an unsecret renewal
/// state, never a cookie.
/// </summary>
public class BotAccountCredential
{
    /// <summary>The only allowed key.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>The sealed 7TV token; null = no token.</summary>
    public string? SealedToken { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? SetAtUtc { get; set; }

    /// <summary>One of <see cref="BotAccountStatus"/>.</summary>
    public string Status { get; set; } = BotAccountStatus.None;

    public string? BotSevenTvUserId { get; set; }
    public DateTime? LastVerifiedAtUtc { get; set; }

    /// <summary>A language-neutral code.</summary>
    public string? LastErrorCode { get; set; }

    public bool KillSwitch { get; set; }

    /// <summary>Cool-down after a 429; survives a worker restart.</summary>
    public DateTime? PausedUntilUtc { get; set; }

    /// <summary>Raised by every token set and delete and by every renewal swap: which seal is current.</summary>
    public int CredentialVersion { get; set; }

    /// <summary>Raised by the runner right after it takes the advisory lock (fencing).</summary>
    public long RunnerEpoch { get; set; }

    /// <summary>Raised by token set and delete only: which account the admin set.</summary>
    public int IdentityVersion { get; set; }

    /// <summary>Raised by every change of <see cref="KillSwitch"/>; monotonic.</summary>
    public long KillSwitchVersion { get; set; }

    /// <summary>SHA-256 of the API's public token key, written with the token.</summary>
    public string? TokenKeyFingerprint { get; set; }

    // Renewal state (plan #487 2.13). Counters default to 0, everything else is nullable; none of it is a secret
    // except hashes.

    public DateTime? RenewalRequestedAtUtc { get; set; }
    public Guid? RenewalRunId { get; set; }
    public DateTime? RenewalRunStartedAtUtc { get; set; }
    public DateTime? RenewalRunRequestedAtUtc { get; set; }
    public int? RenewalRunCredentialVersion { get; set; }
    public long? RenewalRunKillSwitchVersion { get; set; }
    public DateTime? LastRenewalSucceededAtUtc { get; set; }
    public DateTime? NextRenewalAttemptAtUtc { get; set; }
    public int RenewalFailureCount { get; set; }
    public string? LastRenewalErrorCode { get; set; }
    public DateTime? RenewerSeenAtUtc { get; set; }
    public string? RenewerSecretHash { get; set; }
    public DateTime? RenewerSecretSetAtUtc { get; set; }

    /// <summary>The session expiry the renewer reported.</summary>
    public DateTime? TwitchSessionExpiresAtUtc { get; set; }

    public Guid? LastRenewalRunId { get; set; }
    public string? LastRenewalRunOutcome { get; set; }

    /// <summary>One of <see cref="TwitchSessionStates"/>; null = never reported.</summary>
    public string? TwitchSessionState { get; set; }

    /// <summary>The <see cref="CredentialVersion"/> the worker verified last.</summary>
    public int? LastVerifiedCredentialVersion { get; set; }
}

/// <summary>The values of <see cref="BotAccountCredential.Status"/>.</summary>
public static class BotAccountStatus
{
    public const string None = "none";
    public const string Unverified = "unverified";
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Expired = "expired";

    public static readonly IReadOnlyList<string> All = [None, Unverified, Valid, Invalid, Expired];
}

/// <summary>The values of <see cref="BotAccountCredential.TwitchSessionState"/>.</summary>
public static class TwitchSessionStates
{
    public const string Valid = "valid";
    public const string Malformed = "malformed";
    public const string Lost = "lost";

    public static readonly IReadOnlyList<string> All = [Valid, Malformed, Lost];
}

/// <summary>
/// The language-neutral values the worker writes to <see cref="BotAccountCredential.LastErrorCode"/> when it moves the
/// token to <see cref="BotAccountStatus.Invalid"/> (plan #487 2.8). Field values for the admin page, not Api error codes.
/// </summary>
public static class BotAccountErrorCodes
{
    /// <summary>The worker could not open the seal (another key, a damaged value).</summary>
    public const string SealUnreadable = "seal_unreadable";

    /// <summary>7TV answered the verification with a real 401 or a 7TV-shaped 403: the token opens no session.</summary>
    public const string TokenUnauthorized = "token_unauthorized";
}
