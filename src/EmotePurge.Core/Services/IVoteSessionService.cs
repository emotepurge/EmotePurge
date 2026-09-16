using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

public enum VoteCastResult
{
    Success,
    ChannelNotFound,
    SessionNotFound,
    SessionEnded,
    EmoteNotEligible
}

/// <summary>
/// Every way creating a vote session can fail. The rules used to exist twice — as
/// <c>400 { errorCode }</c> in the endpoint and as <c>ArgumentException</c> in the service, where they
/// were unreachable. Two copies with divergent failure modes: a fifth rule added only in the service
/// would have produced a 500 instead of a 400, and one added only in the endpoint would have left the
/// service permissive for any other caller (a test, the worker, a later module).
/// The service is now the single authority and the endpoint only translates.
/// </summary>
public enum CreateVoteSessionResult
{
    Success,
    ChannelNotFound,
    TitleEmpty,
    RolesEmpty,
    VipsNotSupported,
    StartedAtInFuture,
    StartedAtTooFarBack,
    // emoteIds was provided but is empty after trimming/deduplication. null means "all emotes" and
    // is valid; an explicit empty list is rejected instead of being silently reinterpreted as "all".
    EmoteIdsEmpty,
    // At least one provided id is unknown, belongs to another channel, or is already archived.
    EmoteIdsInvalid
}

public static class VoteSessionLimits
{
    /// <summary>
    /// Backdating <c>StartedAt</c> was unbounded, and the results window is
    /// <c>StartedAt..(EndedAt ?? now)</c> — so one session backdated far enough covered a channel's
    /// entire usage history. Same cap as the usage-stats range, and shared with the endpoint so the
    /// number it reports back cannot drift from the number that is enforced.
    /// </summary>
    public const int MaxBackdateDays = 366;
}

// ChannelName/Title/AllowedVoterRoles describe the session, same as VoteSessionSummaryDto's fields.
// StartedAt null = now.
// EmoteIds null = the session covers all non-archived channel emotes dynamically; a non-null list
// becomes the session's fixed ballot (VoteSessionEmote rows, validated against the channel).
// HideResultsUntilEnd true = secret ballot: no tallies for non-managers until the session ends.
// Fixed at creation like the ballot — a flag a manager could flip mid-session would let them hide a
// result they dislike, or reveal one at the moment it suits them.
public sealed record VoteSessionCreateRequest(
    string ChannelName, string Title, AllowedRoles AllowedVoterRoles, DateTime? StartedAt = null,
    IReadOnlyList<string>? EmoteIds = null, bool HideResultsUntilEnd = false);

public interface IVoteSessionService
{
    // request describes the session to create (see VoteSessionCreateRequest). actor is audited
    // together with the created session (same transaction). The service validates; the endpoint
    // maps the result to a status code.
    Task<(CreateVoteSessionResult Result, VoteSession? Session)> CreateAsync(
        VoteSessionCreateRequest request, AuditActor actor, CancellationToken cancellationToken = default);

    // null = channel/session not found or session doesn't belong to that channel. Idempotent no-op if
    // already ended — and a no-op writes no audit entry, because nothing happened.
    Task<VoteSession?> EndAsync(string channelName, long sessionId, AuditActor actor, CancellationToken cancellationToken = default);

    Task<(VoteCastResult Result, Vote? Vote)> CastVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, VoteType type, CancellationToken cancellationToken = default);

    // Removes the caller's vote for this emote, returning to the neutral (unvoted) state. Idempotent
    // no-op (still Success) if no vote existed — mirrors EndAsync's idempotency.
    Task<VoteCastResult> RetractVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, CancellationToken cancellationToken = default);

    // Hard delete — cascades to the session's Votes (DeleteBehavior.Cascade in AppDbContext).
    // No IsActive guard: a manager may delete an active session too, same as LeaveAsync being
    // usable regardless of a channel's vote-session state. Returns false if the channel or session
    // isn't found (bool convention, mirrors ChannelService.LeaveAsync).
    Task<bool> DeleteAsync(string channelName, long sessionId, AuditActor actor, CancellationToken cancellationToken = default);
}
