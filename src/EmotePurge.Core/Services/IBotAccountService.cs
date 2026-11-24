namespace EmotePurge.Core.Services;

/// <summary>
/// The admin side of the bot account (plan #487 2.2, 2.10, spec K4 "Admin-Api"): read the credential row, set and delete
/// the 7TV token, flip the kill switch. Api only, because setting a token needs <see cref="IBotAccountTokenSealer"/>.
/// <para>
/// Every write locks the one row (<c>Id = 1</c>, upserted where it creates the row), writes its audit entry in the same
/// commit and, after the commit, nudges the worker with <c>BOTACCOUNT:</c> (no payload; a Redis outage does not fail the
/// write, the worker's own poll finds the row anyway). Nothing here ever returns the token or the seal.
/// </para>
/// </summary>
public interface IBotAccountService
{
    /// <summary>The account half of <c>GET /api/admin/bot-account</c>. A missing row reads as status <c>none</c>.</summary>
    Task<BotAccountStateDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the form (<see cref="BotAccountJwt"/>), rejects a token whose <c>exp</c> is not in the future, seals it and
    /// stores it with <c>Status = unverified</c>. Raises <c>CredentialVersion</c> and <c>IdentityVersion</c> by one and
    /// writes <c>TokenKeyFingerprint</c>; leaves the kill switch, the pause and <c>RunnerEpoch</c> alone. Audit
    /// <c>botAccount.tokenSet</c> with <c>expiresAtUtc</c> only. A refusal writes, audits and publishes nothing.
    /// </summary>
    Task<BotAccountTokenSetOutcome> SetTokenAsync(string? token, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears seal, expiry, bot id, error and verification and sets <c>Status = none</c>; the kill switch stays. Raises
    /// both versions by one; audit <c>botAccount.tokenDelete</c>. Returns <c>false</c> (and writes nothing) when no token
    /// is stored, which includes a missing row: the route answers 204 either way.
    /// </summary>
    Task<bool> DeleteTokenAsync(AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the kill switch (upsert: works without a token row). A change of the value raises <c>KillSwitchVersion</c>
    /// by one and writes <c>botAccount.killSwitch</c> with <c>enabled</c>; the same value changes nothing and returns
    /// <c>false</c>. The credential and identity versions never move here.
    /// </summary>
    Task<bool> SetKillSwitchAsync(bool enabled, AuditActor actor, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IBotAccountService.SetTokenAsync"/> did.</summary>
public enum BotAccountTokenSetOutcome
{
    /// <summary>Stored, <c>unverified</c>; the worker verifies it.</summary>
    Set,

    /// <summary><c>BotAccount:Enabled</c> is off (404 <c>bot_account_disabled</c>).</summary>
    Disabled,

    /// <summary>Not of JWT form or without a usable <c>exp</c> (400 <c>bot_account_token_malformed</c>).</summary>
    Malformed,

    /// <summary><c>exp</c> is not in the future (400 <c>bot_account_token_expired</c>).</summary>
    Expired,
}

/// <summary>
/// The account fields of the admin read (spec K4). Deliberately no seal, no token, no fingerprint and no renewal state
/// (the renewal fields join in PR 3).
/// </summary>
/// <param name="TokenPresent">A seal is stored.</param>
/// <param name="Status">One of <see cref="Entities.BotAccountStatus"/>.</param>
/// <param name="ExpiresAtUtc">The token's <c>exp</c>.</param>
/// <param name="ExpiresInDays">Whole days until <c>exp</c>, rounded down, never below 0; null without an expiry.</param>
public sealed record BotAccountStateDto(
    bool TokenPresent,
    string Status,
    DateTime? ExpiresAtUtc,
    int? ExpiresInDays,
    string? BotSevenTvUserId,
    DateTime? LastVerifiedAtUtc,
    string? LastErrorCode,
    bool KillSwitch,
    DateTime? PausedUntilUtc);
