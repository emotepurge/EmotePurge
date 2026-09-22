using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Services;

public class VoteSessionService(AppDbContext db, IForeignEmoteSetService foreignEmoteSetService) : IVoteSessionService
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

        // Set-session exclusion rule (spec 6.9/9), checked on the raw request shape before either list
        // is touched further: emoteSetId set requires a non-empty sevenTvEmoteIds and forbids emoteIds;
        // emoteSetId absent forbids sevenTvEmoteIds. Ahead of TryNormalizeBallotEmoteIds below on
        // purpose: an empty or whitespace-only emoteIds list (`[]`, `["  "]`) alongside a set-session's
        // emoteSetId must fail this rule, not fall through to TryNormalizeBallotEmoteIds and come back
        // as EmoteIdsEmpty instead — that check runs on EmoteIds alone and has no way to know a
        // set-session ballot was also present.
        var isSetSession = request.EmoteSetId is not null;
        if (isSetSession
            ? request.SevenTvEmoteIds is null || request.SevenTvEmoteIds.Count == 0 || request.EmoteIds is not null
            : request.SevenTvEmoteIds is not null)
        {
            return (CreateVoteSessionResult.SetBallotInvalid, null);
        }

        if (!TryNormalizeBallotEmoteIds(request.EmoteIds, out var ballotEmoteIds))
        {
            return (CreateVoteSessionResult.EmoteIdsEmpty, null);
        }

        // Checked on the raw list above (the exclusion rule needs the caller's actual "was
        // sevenTvEmoteIds empty" answer, not a shrunk one), deduplicated only now. VoteSessionEmote's
        // (VoteSessionId, EmoteId) primary key would otherwise reject a request that names the same
        // 7TV emote twice.
        var sevenTvEmoteIds = isSetSession ? request.SevenTvEmoteIds!.Distinct(StringComparer.Ordinal).ToList() : null;

        var channel = await db.LoadChannelAsync(request.ChannelName, cancellationToken);
        if (channel is null)
        {
            return (CreateVoteSessionResult.ChannelNotFound, null);
        }

        // Set-session steps 1–2 (spec section 9): read the set's live membership from 7TV, then check
        // all-or-nothing on the 7TV identity. Done ahead of the transaction below — an HTTP round trip
        // has no business holding a database transaction open.
        IReadOnlyDictionary<string, ForeignEmoteRow>? liveMembers = null;
        if (isSetSession)
        {
            var lookup = await foreignEmoteSetService.GetForeignEmoteSetBySetIdAsync(
                channel.ChannelName, request.EmoteSetId!, cancellationToken: cancellationToken);
            if (lookup.Status != ForeignEmoteSetLookupStatus.Ok)
            {
                return (CreateVoteSessionResult.SevenTvUnavailable, null);
            }

            // Not ToDictionary: 7TV's own sets routinely list the same emote id twice under two
            // aliases (measured on real sets — every one examined so far had at least one such pair),
            // and ToDictionary throws on the second occurrence. First-wins is deterministic and, for
            // this ballot, harmless either way — Name/ImageUrl only ever seed a never-active row or
            // freeze VoteSessionEmote.NameAtCreation, neither of which cares which alias won.
            var liveMembersById = new Dictionary<string, ForeignEmoteRow>(StringComparer.Ordinal);
            foreach (var emote in lookup.EmoteSet!.Emotes)
            {
                liveMembersById.TryAdd(emote.SevenTvEmoteId, emote);
            }

            liveMembers = liveMembersById;
            if (sevenTvEmoteIds!.Any(id => !liveMembers.ContainsKey(id)))
            {
                return (CreateVoteSessionResult.EmoteIdsInvalid, null);
            }
        }
        else if (ballotEmoteIds is not null
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
            StartedAt = request.StartedAt ?? DateTime.UtcNow,
            EmoteSetId = request.EmoteSetId
        };
        // The only audited write in this file that cannot be a single SaveChanges: VoteSession.Id is
        // database-generated, so the audit entry's TargetId does not exist until the insert has run.
        // An explicit transaction keeps the guarantee anyway — either both rows land or neither does,
        // which is the whole point of writing audit entries in the action's own transaction. The
        // set-session's Emotes upsert (steps 3–4) joins this same transaction below, so a session never
        // exists without its ballot rows, or the ballot rows without their session.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.VoteSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        int? ballotCount = null;
        if (isSetSession)
        {
            // Steps 3–4: race-safe upsert into Emotes, then read back the local guid each 7TV id now
            // has (existing active rows are taken over unmodified; missing ones are created archived).
            var localIdsBySevenTvId = await UpsertSetSessionEmotesAsync(
                channel.Id, sevenTvEmoteIds!, liveMembers!, cancellationToken);
            db.VoteSessionEmotes.AddRange(sevenTvEmoteIds!.Select(sevenTvId => new VoteSessionEmote
            {
                VoteSessionId = session.Id,
                EmoteId = localIdsBySevenTvId[sevenTvId],
                NameAtCreation = liveMembers![sevenTvId].Name,
                ImageUrlAtCreation = liveMembers[sevenTvId].ImageUrl
            }));
            ballotCount = sevenTvEmoteIds!.Count;
        }
        else if (ballotEmoteIds is not null)
        {
            // Needs the generated session id, hence after the first save but inside the transaction.
            db.VoteSessionEmotes.AddRange(ballotEmoteIds.Select(id => new VoteSessionEmote
            {
                VoteSessionId = session.Id,
                EmoteId = id
            }));
            ballotCount = ballotEmoteIds.Count;
        }

        db.AddAuditEntry(
            actor,
            AuditActions.VoteSessionCreate,
            channelName: channel.ChannelName,
            targetType: VoteSessionTargetType,
            targetId: session.Id.ToString(CultureInfo.InvariantCulture),
            // hideResults rides along unconditionally: a secret ballot is a governance decision, and
            // the audit row is the only place it is recorded as an act rather than as a session field.
            // Same two shapes as before the set-session ballot existed (spec 9: "Audit wie heute") —
            // ballotCount is null for a dynamic null-session and non-null for every fixed ballot,
            // local-guid or 7TV-id alike.
            details: ballotCount is null
                ? new { title = session.Title, hideResults = session.HideResultsUntilEnd }
                : (object)new { title = session.Title, emoteCount = ballotCount, hideResults = session.HideResultsUntilEnd });
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

        if (!await IsEmoteVotableAsync(channel.Id, sessionId, emoteId, session.EmoteSetId is not null, cancellationToken))
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
    /// Archived emotes are never votable in a null-session — in a subset session they stay visible in
    /// the results (badged), but their voting is closed. A set-session drops that gate entirely (spec
    /// section 9): being on the ballot is the only criterion, since a set-session's ballot rows are
    /// created archived on purpose (never-active 7TV members) and archived is simply not a signal
    /// about whether that membership still holds. A session with membership rows is a fixed ballot;
    /// one without covers the whole channel set (null-session only — a set-session's invariant
    /// guarantees SessionEmotes is never empty, spec section 9).
    /// </summary>
    private async Task<bool> IsEmoteVotableAsync(
        string channelId, long sessionId, string emoteId, bool isSetSession, CancellationToken cancellationToken)
    {
        var emoteExists = await db.Emotes.AnyAsync(
            e => e.Id == emoteId && e.ChannelId == channelId && (isSetSession || !e.IsArchived), cancellationToken);
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

    /// <summary>
    /// Set-session steps 3–4 (spec section 9): an all-or-nothing race-safe upsert of the ballot's 7TV
    /// emote ids into this channel's Emotes table, then a read-back by SevenTvEmoteId. A row that
    /// already exists — active or archived — is left exactly as it is (<c>DO NOTHING</c> on the
    /// existing <c>(ChannelId, SevenTvEmoteId)</c> unique index); a row that does not exist yet is
    /// created archived (<c>IsArchived = true</c>, <c>ArchivedAt = null</c>, "never active") so a
    /// set-session's ballot never grants a never-synced 7TV emote the appearance of being live in our
    /// own database. <c>FirstSeenAt</c> is left null for a new row: 7TV's set-entry response does
    /// carry an "added to set" timestamp (<c>SevenTvGqlSetEntryDto.AddedAt</c>), but
    /// <see cref="IForeignEmoteSetService"/>'s set-ID read (<see cref="ForeignEmoteRow"/>) does not
    /// thread it through — this preview path was built for reading, not for backfilling that column.
    /// If this set later becomes the channel's active one, the worker's regular resync corrects
    /// <c>FirstSeenAt</c> from the live <c>AddedToSetAt</c> it does carry
    /// (<c>SevenTvSyncService.UpsertEmote</c>'s own correction, not a write-once backfill). Runs
    /// inside the caller's transaction, so this
    /// insert never lands without the session it belongs to, or the reverse. A concurrent worker sync
    /// racing the same insert either lands first (this one then no-ops and reads the synced row back)
    /// or after (rare; left to the worker's own retry, spec E10 — out of scope here, see AK 78).
    /// </summary>
    private async Task<Dictionary<string, string>> UpsertSetSessionEmotesAsync(
        string channelId, IReadOnlyList<string> sevenTvEmoteIds, IReadOnlyDictionary<string, ForeignEmoteRow> liveMembers,
        CancellationToken cancellationToken)
    {
        var newLocalIds = sevenTvEmoteIds.Select(_ => Guid.NewGuid().ToString()).ToArray();
        var names = sevenTvEmoteIds.Select(id => liveMembers[id].Name).ToArray();
        var imageUrls = sevenTvEmoteIds.Select(id => liveMembers[id].ImageUrl).ToArray();
        var now = DateTime.UtcNow;

        const string sql = """
            INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "FirstSeenAt", "LastSyncedAt")
            SELECT input."Id", input."SevenTvEmoteId", @channelId, input."Name", input."ImageUrl", true, NULL, NULL, @now
            FROM UNNEST(@ids, @sevenTvEmoteIds, @names, @imageUrls) AS input("Id", "SevenTvEmoteId", "Name", "ImageUrl")
            ON CONFLICT ("ChannelId", "SevenTvEmoteId") DO NOTHING;
            """;

        await db.Database.ExecuteSqlRawAsync(
            sql,
            [
                new NpgsqlParameter("channelId", NpgsqlDbType.Text) { Value = channelId },
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = newLocalIds },
                new NpgsqlParameter("sevenTvEmoteIds", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = sevenTvEmoteIds.ToArray() },
                new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names },
                new NpgsqlParameter("imageUrls", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = imageUrls },
            ],
            cancellationToken);

        var rows = await db.Emotes
            .Where(e => e.ChannelId == channelId && sevenTvEmoteIds.Contains(e.SevenTvEmoteId))
            .Select(e => new { e.Id, e.SevenTvEmoteId })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(r => r.SevenTvEmoteId, r => r.Id, StringComparer.Ordinal);
    }
}
