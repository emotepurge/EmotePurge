namespace EmotePurge.Core.SevenTv;

/// <summary>
/// The bot account's own client for 7TV's v4 GraphQL endpoint (plan #487 2.4, spec K4 "Schreib-Client"). Registered in the
/// worker only, and only while <c>BotAccount:Enabled</c> is on; the Api can never resolve it. For
/// <see cref="SevenTvWriteOutcome.Ok"/> of the add, remove and accept operations, <see cref="SevenTvWriteResult.RetryAfter"/>
/// can also be set: the answer carried a rate limit below the proven operation, so the caller books the success and then
/// pauses the account.
/// </summary>
/// <remarks>
/// <para>
/// The token is the caller's: the worker opens the seal and passes the plaintext into each call, the client sets it as the
/// <c>Authorization</c> header of that one request and keeps it nowhere else. Neither the token, nor the header, nor a
/// response body, nor a 7TV error message reaches a log; a log line names the operation, the outcome, the HTTP status and
/// 7TV's error code and status only.
/// </para>
/// <para>
/// Every call answers in the closed form of <see cref="SevenTvWriteResult"/> and never throws for anything 7TV or the
/// network does. The only exception that leaves a call is an <see cref="OperationCanceledException"/> for the caller's own
/// <see cref="CancellationToken"/>.
/// </para>
/// </remarks>
public interface ISevenTvWriteClient
{
    /// <summary>
    /// Proves the token opens a session and returns the 7TV user id behind it (<c>users.me { id }</c>, measured in the
    /// live probe P1). <see cref="SevenTvWriteResult{T}.Value"/> is never <c>null</c> on <see cref="SevenTvWriteOutcome.Ok"/>;
    /// a session that answers without a user is <see cref="SevenTvWriteOutcome.Unavailable"/>.
    /// </summary>
    Task<SevenTvWriteResult<string>> VerifyAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every entry of the set, across all pages, or no entries at all (fail-closed): a missing or unreadable page, a page
    /// count above the client's cap or changing between pages, a set 7TV does not return, or one with another id is
    /// <see cref="SevenTvWriteOutcome.Unavailable"/>. <see cref="SevenTvWriteResult{T}.Value"/> is never <c>null</c> on
    /// <see cref="SevenTvWriteOutcome.Ok"/>. An entry whose emote did not resolve is left out.
    /// </summary>
    Task<SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>> ReadSetEntriesAsync(
        string token,
        string setId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the emote under the alias (<c>addEmote(id: { emoteId, alias })</c>) and asks for the resulting set's entries in
    /// the same answer. <see cref="SevenTvWriteOutcome.Ok"/> only when the mutation field came back without an error on its
    /// own path and carries the requested set's id (plan 2.4, revision 6a S6-4). The entries are a bonus:
    /// <see cref="SevenTvWriteResult{T}.Value"/> is <c>null</c> on <see cref="SevenTvWriteOutcome.Ok"/> when they are
    /// missing, unreadable or carry an error of their own, and the caller then reads the set. Any other unclear answer after
    /// sending is <see cref="SevenTvWriteOutcome.Unavailable"/>: whether 7TV wrote is open.
    /// </summary>
    Task<SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>> AddEmoteAsync(
        string token,
        string setId,
        string emoteId,
        string alias,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes exactly the entry of this emote under this alias (<c>removeEmote(id: { emoteId, alias })</c>). The alias is
    /// mandatory: without it 7TV takes any entry of the emote. <see cref="SevenTvWriteOutcome.Ok"/> means 7TV answered the
    /// mutation without an error on its path; the read after it decides what happened (plan 2.7).
    /// </summary>
    Task<SevenTvWriteResult> RemoveEmoteAsync(
        string token,
        string setId,
        string emoteId,
        string alias,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts the pending editor invitation of <paramref name="ownerSevenTvUserId"/> for the bot account
    /// <paramref name="botSevenTvUserId"/> (<c>userEditors.editor(editorId, userId).updateState(state: ACCEPT)</c>, live probe
    /// A5). There is deliberately no parameter for the state: the client can accept, never reject or leave ([B] F7).
    /// <see cref="SevenTvWriteOutcome.Ok"/> only when 7TV returns the editor relation in state <c>ACCEPTED</c>; any other
    /// unclear answer is <see cref="SevenTvWriteOutcome.Unavailable"/> and must be resolved by the editor read (plan 2.5b).
    /// </summary>
    Task<SevenTvWriteResult> AcceptEditorInvitationAsync(
        string token,
        string botSevenTvUserId,
        string ownerSevenTvUserId,
        CancellationToken cancellationToken = default);
}
