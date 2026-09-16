using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class VoteSessionService(AppDbContext db) : IVoteSessionService
{
    // AuditLogEntry.TargetType for every entry this service writes.
    private const string VoteSessionTargetType = "voteSession";

    public async Task<(CreateVoteSessionResult Result, VoteSession? Session)> CreateAsync(
        VoteSessionCreateRequest request, AuditActor actor, CancellationToken cancellationToken = default)
    {
        // Validated here rather than in the endpoint: this is the layer that has tests, and it is the
        // one every future caller goes through. The endpoint only maps these results to error codes.
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return (CreateVoteSessionResult.TitleEmpty, null);
        }

        if (request.AllowedVoterRoles == 0)
        {
            return (CreateVoteSessionResult.RolesEmpty, null);
        }

        // Not a design choice: Twitch has no self-report endpoint for VIP status, so a voter cannot
        // prove their own (see the decision log).
        if (request.AllowedVoterRoles.HasFlag(AllowedRoles.VIPs))
        {
            return (CreateVoteSessionResult.VipsNotSupported, null);
        }

        if (ValidateStartedAt(request.StartedAt) is { } startedAtError)
        {
            return (startedAtError, null);
        }

        if (!TryNormalizeBallotEmoteIds(request.EmoteIds, out var ballotEmoteIds))
        {
            return (CreateVoteSessionResult.EmoteIdsEmpty, null);
        }

        var channel = await db.LoadChannelAsync(request.ChannelName, cancellationToken);
        if (channel is null)
        {
            return (CreateVoteSessionResult.ChannelNotFound, null);
        }

        if (ballotEmoteIds is not null
            && !await AllEmoteIdsEligibleAsync(ballotEmoteIds, channel.Id, cancellationToken))
        {
            return (CreateVoteSessionResult.EmoteIdsInvalid, null);
        }

        var session = new VoteSession
        {
            ChannelId = channel.Id,
            Title = request.Title.Trim(),
            AllowedVoterRoles = request.AllowedVoterRoles,
            HideResultsUntilEnd = request.HideResultsUntilEnd,
            StartedAt = request.StartedAt ?? DateTime.UtcNow
        };
        // The only audited write in this file that cannot be a single SaveChanges: VoteSession.Id is
        // database-generated, so the audit entry's TargetId does not exist until the insert has run.
        // An explicit transaction keeps the guarantee anyway — either both rows land or neither does,
        // which is the whole point of writing audit entries in the action's own transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.VoteSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        if (ballotEmoteIds is not null)
        {
            // Needs the generated session id, hence after the first save but inside the transaction.
            db.VoteSessionEmotes.AddRange(ballotEmoteIds.Select(id => new VoteSessionEmote
            {
                VoteSessionId = session.Id,
                EmoteId = id
            }));
        }

        db.AddAuditEntry(
            actor,
            AuditActions.VoteSessionCreate,
            channelName: channel.ChannelName,
            targetType: VoteSessionTargetType,
            targetId: session.Id.ToString(CultureInfo.InvariantCulture),
            // hideResults rides along unconditionally: a secret ballot is a governance decision, and
            // the audit row is the only place it is recorded as an act rather than as a session field.
            details: ballotEmoteIds is null
                ? new { title = session.Title, hideResults = session.HideResultsUntilEnd }
                : (object)new { title = session.Title, emoteCount = ballotEmoteIds.Count, hideResults = session.HideResultsUntilEnd });
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return (CreateVoteSessionResult.Success, session);
    }

    public async Task<VoteSession?> EndAsync(string channelName, long sessionId, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var (channel, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        // The endpoint derives the summary's EmoteCount from this collection; without the explicit
        // load an ended subset session would misreport itself as a whole-set session.
        await db.Entry(session).Collection(s => s.SessionEmotes).LoadAsync(cancellationToken);

        // Ending an already-ended session is a no-op by contract, and a no-op is not an event — so
        // the audit entry sits inside this branch rather than beside it.
        if (session.IsActive)
        {
            session.IsActive = false;
            session.EndedAt = DateTime.UtcNow;
            db.AddAuditEntry(
                actor,
                AuditActions.VoteSessionEnd,
                channelName: channel!.ChannelName,
                targetType: VoteSessionTargetType,
                targetId: session.Id.ToString(CultureInfo.InvariantCulture),
                details: new { title = session.Title });
            await db.SaveChangesAsync(cancellationToken);
        }

        return session;
    }

    public async Task<(VoteCastResult Result, Vote? Vote)> CastVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, VoteType type, CancellationToken cancellationToken = default)
    {
        var (channel, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
        if (channel is null)
        {
            return (VoteCastResult.ChannelNotFound, null);
        }

        if (session is null)
        {
            return (VoteCastResult.SessionNotFound, null);
        }

        if (!session.IsActive)
        {
            return (VoteCastResult.SessionEnded, null);
        }

        if (!await IsEmoteVotableAsync(channel.Id, sessionId, emoteId, cancellationToken))
        {
            return (VoteCastResult.EmoteNotEligible, null);
        }

        var vote = await db.Votes.SingleOrDefaultAsync(
            v => v.VoteSessionId == sessionId && v.EmoteId == emoteId && v.UserId == voterTwitchUserId, cancellationToken);
        if (vote is null)
        {
            vote = new Vote
            {
                VoteSessionId = sessionId,
                EmoteId = emoteId,
                UserId = voterTwitchUserId,
                Type = type
            };
            db.Votes.Add(vote);
        }
        else
        {
            vote.Type = type;
            vote.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Read-then-insert loses a race against a second request from the same user: a double click
            // sends both clicks down the insert path (the first has not committed when the second reads),
            // and the (VoteSessionId, EmoteId, UserId) unique index rejects the loser — a 500 for what is
            // in truth an idempotent action. Reload and apply it as the update it always was.
            db.Entry(vote).State = EntityState.Detached;

            var existing = await db.Votes.SingleOrDefaultAsync(
                v => v.VoteSessionId == sessionId && v.EmoteId == emoteId && v.UserId == voterTwitchUserId, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            existing.Type = type;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return (VoteCastResult.Success, existing);
        }

        return (VoteCastResult.Success, vote);
    }

    public async Task<VoteCastResult> RetractVoteAsync(
        string channelName, long sessionId, string emoteId, string voterTwitchUserId, CancellationToken cancellationToken = default)
    {
        var (channel, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
        if (channel is null)
        {
            return VoteCastResult.ChannelNotFound;
        }

        if (session is null)
        {
            return VoteCastResult.SessionNotFound;
        }

        if (!session.IsActive)
        {
            return VoteCastResult.SessionEnded;
        }

        var vote = await db.Votes.SingleOrDefaultAsync(
            v => v.VoteSessionId == sessionId && v.EmoteId == emoteId && v.UserId == voterTwitchUserId, cancellationToken);
        if (vote is not null)
        {
            db.Votes.Remove(vote);
            await db.SaveChangesAsync(cancellationToken);
        }

        return VoteCastResult.Success;
    }

    public async Task<bool> DeleteAsync(string channelName, long sessionId, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var (channel, session) = await db.LoadChannelSessionAsync(channelName, sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        // Title captured into the entry before the row disappears — after the delete there is nothing
        // left to look the session's name up in.
        db.AddAuditEntry(
            actor,
            AuditActions.VoteSessionDelete,
            channelName: channel!.ChannelName,
            targetType: VoteSessionTargetType,
            targetId: session.Id.ToString(CultureInfo.InvariantCulture),
            details: new { title = session.Title });
        db.VoteSessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Null when <paramref name="startedAt"/> is unset or within the allowed backdating window.</summary>
    private static CreateVoteSessionResult? ValidateStartedAt(DateTime? startedAt)
    {
        if (startedAt is not { } requestedStartedAt)
        {
            return null;
        }

        if (requestedStartedAt > DateTime.UtcNow)
        {
            return CreateVoteSessionResult.StartedAtInFuture;
        }

        if (requestedStartedAt < DateTime.UtcNow.AddDays(-VoteSessionLimits.MaxBackdateDays))
        {
            return CreateVoteSessionResult.StartedAtTooFarBack;
        }

        return null;
    }

    /// <summary>
    /// null = dynamic "all emotes" session. An explicit empty list is rejected (returns false) rather
    /// than silently reinterpreted as "all" — the caller clearly meant to curate and lost the list.
    /// </summary>
    private static bool TryNormalizeBallotEmoteIds(IReadOnlyList<string>? emoteIds, out List<string>? ballotEmoteIds)
    {
        if (emoteIds is null)
        {
            ballotEmoteIds = null;
            return true;
        }

        ballotEmoteIds = emoteIds
            .Select(id => id.Trim())
            .Where(id => id.Length > 0)
            .Distinct()
            .ToList();
        return ballotEmoteIds.Count > 0;
    }

    /// <summary>
    /// All-or-nothing: one unknown, foreign or already-archived id rejects the whole create instead
    /// of silently shrinking the ballot the manager thought they submitted.
    /// </summary>
    private async Task<bool> AllEmoteIdsEligibleAsync(List<string> ballotEmoteIds, string channelId, CancellationToken cancellationToken)
    {
        var eligibleCount = await db.Emotes.CountAsync(
            e => ballotEmoteIds.Contains(e.Id) && e.ChannelId == channelId && !e.IsArchived, cancellationToken);
        return eligibleCount == ballotEmoteIds.Count;
    }

    /// <summary>
    /// Archived emotes are never votable — in a subset session they stay visible in the results
    /// (badged), but their voting is closed. A session with membership rows is a fixed ballot; one
    /// without covers the whole channel set.
    /// </summary>
    private async Task<bool> IsEmoteVotableAsync(string channelId, long sessionId, string emoteId, CancellationToken cancellationToken)
    {
        var emoteExists = await db.Emotes.AnyAsync(
            e => e.Id == emoteId && e.ChannelId == channelId && !e.IsArchived, cancellationToken);
        if (!emoteExists)
        {
            return false;
        }

        var sessionHasBallot = await db.VoteSessionEmotes.AnyAsync(
            se => se.VoteSessionId == sessionId, cancellationToken);
        if (!sessionHasBallot)
        {
            return true;
        }

        return await db.VoteSessionEmotes.AnyAsync(
            se => se.VoteSessionId == sessionId && se.EmoteId == emoteId, cancellationToken);
    }
}
