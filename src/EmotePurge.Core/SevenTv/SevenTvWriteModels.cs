namespace EmotePurge.Core.SevenTv;

/// <summary>
/// What one call of <see cref="ISevenTvWriteClient"/> came to (plan #487 2.4, one closed form for every operation).
/// </summary>
/// <remarks>
/// The members fall into two groups that callers must keep apart. <see cref="RateLimited"/>, <see cref="Unauthorized"/>,
/// <see cref="LackingPrivileges"/> and <see cref="Rejected"/> are answers in which 7TV said why it did not do the work, so
/// a mutation behind them was not executed. <see cref="Timeout"/> and <see cref="Unavailable"/> are the unresolved ones:
/// after a mutation was sent, nobody knows whether 7TV wrote, and only a later read can tell (plan 2.5a, 2.5b).
/// </remarks>
public enum SevenTvWriteOutcome
{
    /// <summary>
    /// 7TV demonstrably did the work. For a mutation this is never inferred from the absence of an error: it needs the
    /// mutation field itself in the answer (plan 2.4 "Erfolgsnachweis", revision 6a S6-4).
    /// </summary>
    Ok,

    /// <summary>HTTP 429, or HTTP 200 with <c>extensions.status = 429</c>. <see cref="SevenTvWriteResult.RetryAfter"/> says how long to wait.</summary>
    RateLimited,

    /// <summary>A real HTTP 401, or an HTTP 403 with 7TV's own error body (numeric <c>error_code</c>): the token no longer opens a session. Any other 403 is Unavailable. Never derived from a GraphQL error.</summary>
    Unauthorized,

    /// <summary>
    /// HTTP 200 with <c>extensions.code = LACKING_PRIVILEGES</c> or <c>extensions.status = 403</c>. Decided by the code and
    /// status only, never by the message: 7TV sends two messages under this code (not an editor; invitation still
    /// pending), and whether an invitation is open is for the editor read to say.
    /// </summary>
    LackingPrivileges,

    /// <summary>7TV refused the request with a 4xx status in the GraphQL error. <see cref="SevenTvWriteResult.Rejection"/> says which.</summary>
    Rejected,

    /// <summary>No answer within the client's time limit (10 s).</summary>
    Timeout,

    /// <summary>Network failure, a 5xx, an unreadable or incomplete answer, or an answer without the proof of success.</summary>
    Unavailable,
}

/// <summary>
/// Why 7TV refused (<see cref="SevenTvWriteOutcome.Rejected"/>). Only <see cref="AliasConflict"/> has a measured form
/// (HTTP 200, <c>extensions.status = 409</c>, code <c>BAD_REQUEST</c>). A full set and an emote removed from 7TV could
/// not be produced in the live probe of 2026-11-24, so their forms are unknown and no answer maps to them yet; they arrive
/// as <see cref="Unknown"/> until a measured form says otherwise (plan #487 2.4, finding 13).
/// </summary>
public enum SevenTvWriteRejection
{
    /// <summary>The alias is already taken in the set (<c>extensions.status = 409</c>).</summary>
    AliasConflict,

    /// <summary>The set has no free slot. Reserved: no measured form maps here yet.</summary>
    SetFull,

    /// <summary>The emote no longer exists on 7TV. Reserved: no measured form maps here yet.</summary>
    EmoteGone,

    /// <summary>Any other 4xx refusal.</summary>
    Unknown,
}

/// <summary>
/// One entry of an emote set as the bot reads it: the emote id, the alias it is listed under, when it entered the set and
/// which 7TV account added it. <see cref="AddedAtUtc"/> and <see cref="AddedById"/> are <c>null</c> when 7TV left them
/// out; the ownership rule treats a missing value as "not proven", never as a match.
/// </summary>
public sealed record SevenTvSetEntry(string EmoteId, string Alias, DateTime? AddedAtUtc, string? AddedById);

/// <summary>
/// The outcome of a <see cref="ISevenTvWriteClient"/> call that carries no value, and the status part of every one that
/// does (<see cref="SevenTvWriteResult{T}"/>). A sealed class with factories, so a result can only be built in one of its
/// valid shapes: <see cref="RetryAfter"/> exists for <see cref="SevenTvWriteOutcome.RateLimited"/>, for an
/// <see cref="SevenTvWriteOutcome.Ok"/> whose answer carried a rate limit somewhere below the proven operation (plan #487
/// K6e-4) and for an <see cref="SevenTvWriteOutcome.Unavailable"/> mutation whose answer carried one (no proof of success,
/// or errors that classify it so); <see cref="Rejection"/> for <see cref="SevenTvWriteOutcome.Rejected"/> only.
/// </summary>
public sealed class SevenTvWriteResult
{
    private static readonly SevenTvWriteResult OkResult = new(SevenTvWriteOutcome.Ok, null, null);
    private static readonly SevenTvWriteResult UnauthorizedResult = new(SevenTvWriteOutcome.Unauthorized, null, null);
    private static readonly SevenTvWriteResult LackingPrivilegesResult = new(SevenTvWriteOutcome.LackingPrivileges, null, null);
    private static readonly SevenTvWriteResult TimeoutResult = new(SevenTvWriteOutcome.Timeout, null, null);
    private static readonly SevenTvWriteResult UnavailableResult = new(SevenTvWriteOutcome.Unavailable, null, null);

    private SevenTvWriteResult(SevenTvWriteOutcome outcome, TimeSpan? retryAfter, SevenTvWriteRejection? rejection)
    {
        Outcome = outcome;
        RetryAfter = retryAfter;
        Rejection = rejection;
    }

    public SevenTvWriteOutcome Outcome { get; }

    /// <summary>
    /// How long to wait before the next call to 7TV; set for <see cref="SevenTvWriteOutcome.RateLimited"/>, and also for an
    /// <see cref="SevenTvWriteOutcome.Ok"/> whose answer carried a rate limit below the proven operation (the operation
    /// still succeeded; the caller books it and pauses the account) and for an unproven mutation
    /// (<see cref="SevenTvWriteOutcome.Unavailable"/>) whose answer carried one (the outcome stays open; the caller pauses
    /// the account all the same).
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Why 7TV refused; set for <see cref="SevenTvWriteOutcome.Rejected"/> only.</summary>
    public SevenTvWriteRejection? Rejection { get; }

    public bool IsOk => Outcome == SevenTvWriteOutcome.Ok;

    public static SevenTvWriteResult Ok() => OkResult;

    /// <summary>A success whose answer also carried a rate limit: the proof of success stands, the wait must be honoured.</summary>
    public static SevenTvWriteResult Ok(TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryAfter, TimeSpan.Zero);
        return new SevenTvWriteResult(SevenTvWriteOutcome.Ok, retryAfter, null);
    }

    public static SevenTvWriteResult RateLimited(TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryAfter, TimeSpan.Zero);
        return new SevenTvWriteResult(SevenTvWriteOutcome.RateLimited, retryAfter, null);
    }

    public static SevenTvWriteResult Unauthorized() => UnauthorizedResult;

    public static SevenTvWriteResult LackingPrivileges() => LackingPrivilegesResult;

    public static SevenTvWriteResult Rejected(SevenTvWriteRejection rejection) =>
        new(SevenTvWriteOutcome.Rejected, null, rejection);

    public static SevenTvWriteResult Timeout() => TimeoutResult;

    public static SevenTvWriteResult Unavailable() => UnavailableResult;

    /// <summary>
    /// An unavailable mutation (no proof of success, or errors that classify it so) whose answer carried a rate limit (plan
    /// #487 K6e-4): whether 7TV wrote stays open, the wait must be honoured all the same.
    /// </summary>
    public static SevenTvWriteResult Unavailable(TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryAfter, TimeSpan.Zero);
        return new SevenTvWriteResult(SevenTvWriteOutcome.Unavailable, retryAfter, null);
    }

    public override string ToString() => Outcome switch
    {
        SevenTvWriteOutcome.RateLimited => $"{Outcome}({RetryAfter})",
        SevenTvWriteOutcome.Rejected => $"{Outcome}({Rejection})",
        _ => Outcome.ToString(),
    };
}

/// <summary>
/// A <see cref="SevenTvWriteResult"/> plus the value the call read. <see cref="Value"/> is only ever set when
/// <see cref="Status"/> is <see cref="SevenTvWriteOutcome.Ok"/>; whether it can be <c>null</c> on success is part of each
/// operation's contract on <see cref="ISevenTvWriteClient"/>.
/// </summary>
public sealed class SevenTvWriteResult<T>
    where T : class
{
    private SevenTvWriteResult(SevenTvWriteResult status, T? value)
    {
        Status = status;
        Value = value;
    }

    public SevenTvWriteResult Status { get; }

    public T? Value { get; }

    public SevenTvWriteOutcome Outcome => Status.Outcome;

    public bool IsOk => Status.IsOk;

    public static SevenTvWriteResult<T> Ok(T? value) => new(SevenTvWriteResult.Ok(), value);

    /// <summary>A success that also carries a wait (see <see cref="SevenTvWriteResult.RetryAfter"/>); <c>null</c> means none.</summary>
    public static SevenTvWriteResult<T> Ok(T? value, TimeSpan? retryAfter) =>
        new(retryAfter is { } wait ? SevenTvWriteResult.Ok(wait) : SevenTvWriteResult.Ok(), value);

    /// <summary>A result without a value. <paramref name="status"/> must not be <see cref="SevenTvWriteOutcome.Ok"/>.</summary>
    public static SevenTvWriteResult<T> Failed(SevenTvWriteResult status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsOk)
        {
            throw new ArgumentException("A failed result needs a failure status.", nameof(status));
        }

        return new SevenTvWriteResult<T>(status, null);
    }

    public override string ToString() => Status.ToString();
}
