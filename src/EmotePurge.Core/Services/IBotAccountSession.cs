namespace EmotePurge.Core.Services;

/// <summary>
/// What a user of the bot account gets from <see cref="IBotAccountRunner"/> for one pass under the lock (plan #487 2.5,
/// 2.5a (g), revision 5a): the account as the token upkeep left it, the opened token, the runner epoch, the lock barrier,
/// the action budget and the spacing between two 7TV writes. Valid for the one pass it is handed to; not thread-safe.
/// <para>
/// The send and booking transactions themselves fence on <see cref="RunnerEpoch"/> (the credential row read
/// <c>FOR SHARE</c> with that epoch; the helper lives in Infrastructure next to the runner). <see cref="HoldsLockAsync"/>
/// is the cheap probe before a commit of intent, before a send and after every 7TV call; it never retakes a lost lock.
/// </para>
/// </summary>
public interface IBotAccountSession
{
    /// <summary>The credential row after this pass's token upkeep (a missing row reads as <c>none</c>).</summary>
    BotAccountSnapshot Account { get; }

    /// <summary>
    /// The plaintext token of <see cref="BotAccountSnapshot.CredentialVersion"/>, only while <see cref="Account"/> says
    /// <c>valid</c>; otherwise <c>null</c>. For the 7TV write client only: never log it, never store it, never put it
    /// into an exception message.
    /// </summary>
    string? Token { get; }

    /// <summary>
    /// Whether <see cref="Token"/> may be used for a 7TV call now (#487 plan 2.5, 2.8; Revision 6c K6c-5): it is set and
    /// no verification is outstanding. One is outstanding after every acquisition of the lock (a new runner uses the token
    /// only after its own successful verification) and after <see cref="ReportUnauthorized"/>; only a verification that
    /// was booked clears it. A verification that ends in <c>Timeout</c> or <c>Unavailable</c>, or that the kill switch or
    /// a pause holds, leaves it outstanding, across passes. Read live: it turns false within the pass the moment
    /// <see cref="ReportUnauthorized"/> is called. False is no fallback reason and suspends nothing; the unauthenticated
    /// editor read and resolving reads stay allowed.
    /// </summary>
    bool TokenUsable { get; }

    /// <summary>This runner's epoch, raised right after it took the advisory lock.</summary>
    long RunnerEpoch { get; }

    /// <summary>Actions left in this pass's budget.</summary>
    int RemainingActions { get; }

    /// <summary>Whether the loop still holds the advisory lock. A lost lock stays lost until the next pass retakes it.</summary>
    Task<bool> HoldsLockAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes one action from the pass's budget (50); <c>false</c> once it is used up (the rest waits for the next pass).</summary>
    bool TryTakeAction();

    /// <summary>
    /// Waits until <c>BotAccount:WriteDelaySeconds</c> have passed since the previous 7TV write of this loop (across
    /// passes) and stamps the next one. Call it right before every 7TV write.
    /// </summary>
    Task WaitForWriteSlotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 7TV answered <c>Unauthorized</c> to a call with <see cref="Token"/>: <see cref="TokenUsable"/> turns false at once,
    /// the next pass verifies the token again before anything else, and that verification decides whether it is
    /// <c>invalid</c> (plan 2.8). Nothing is booked here.
    /// </summary>
    void ReportUnauthorized();

    /// <summary>
    /// A booking found the runner epoch moved (the fence failed): the lock is treated as lost, its connection closed,
    /// and the next pass takes it anew with a fresh epoch. The caller ends its pass without booking anything else.
    /// </summary>
    void ReportLockLost();

    /// <summary>
    /// Books the pause after a 429 on the account (<c>PausedUntilUtc</c>, never shortened), bound to the
    /// <c>IdentityVersion</c> the token was opened under, so it survives a renewal swap but not a new token (plan 2.2).
    /// Fenced on <see cref="RunnerEpoch"/>; returns <c>false</c> when nothing was booked.
    /// </summary>
    Task<bool> PauseAsync(TimeSpan retryAfter, CancellationToken cancellationToken = default);
}

/// <summary>The credential row's non-secret state as one pass saw it (no seal, no renewal columns).</summary>
public sealed record BotAccountSnapshot(
    string Status,
    DateTime? ExpiresAtUtc,
    string? BotSevenTvUserId,
    bool KillSwitch,
    DateTime? PausedUntilUtc,
    int CredentialVersion,
    int IdentityVersion)
{
    /// <summary>What a missing row means: no token, kill switch off.</summary>
    public static BotAccountSnapshot Missing { get; } = new(Entities.BotAccountStatus.None, null, null, false, null, 0, 0);
}
