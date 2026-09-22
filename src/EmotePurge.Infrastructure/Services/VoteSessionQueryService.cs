using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class VoteSessionQueryService(AppDbContext db, IUsageStatQueryService usageStatQueryService) : IVoteSessionQueryService
{
    public async Task<IReadOnlyList<VoteSessionSummaryDto>> ListSessionsAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var sessions = await db.VoteSessions
            .Where(s => s.Channel.ChannelName == normalized)
            // Id, not StartedAt: StartedAt is the start of the usage window and is freely
            // backdatable (the create form prefills it 30 days back), so ordering by it buries a
            // session created today under older ones. The identity column is the creation order.
            .OrderByDescending(s => s.Id)
            .Select(s => new { s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, EmoteCount = s.SessionEmotes.Count, s.HideResultsUntilEnd, s.EmoteSetId })
            .ToListAsync(cancellationToken);

        // 0 membership rows = dynamic "all emotes" session; the DTO reports that as null, not 0.
        return sessions
            .Select(s => new VoteSessionSummaryDto(
                s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, s.EmoteCount == 0 ? null : s.EmoteCount, s.HideResultsUntilEnd, s.EmoteSetId))
            .ToList();
    }

    public async Task<PagedResult<VoteSessionSummaryDto>> ListSessionsPagedAsync(string channelName, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);
        var query = db.VoteSessions.Where(s => s.Channel.ChannelName == normalized);

        var totalCount = await query.CountAsync(cancellationToken);
        var pageRows = await query
            // Creation order, for the reason spelled out in ListSessionsAsync. Both methods must
            // agree: managers page through here, everyone else through the unpaged sibling.
            .OrderByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new { s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, EmoteCount = s.SessionEmotes.Count, s.HideResultsUntilEnd, s.EmoteSetId })
            .ToListAsync(cancellationToken);

        // 0 membership rows = dynamic "all emotes" session; the DTO reports that as null, not 0.
        var items = pageRows
            .Select(s => new VoteSessionSummaryDto(
                s.Id, s.Title, s.AllowedVoterRoles, s.IsActive, s.StartedAt, s.EndedAt, s.EmoteCount == 0 ? null : s.EmoteCount, s.HideResultsUntilEnd, s.EmoteSetId))
            .ToList();

        return new PagedResult<VoteSessionSummaryDto>(items, page, pageSize, totalCount);
    }

    public async Task<VoteSessionResultsDto?> GetResultsAsync(string channelName, long sessionId, string? viewerTwitchUserId = null, bool viewerIsManager = false, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        var (channel, session) = await db.LoadChannelSessionAsync(normalized, sessionId, cancellationToken);
        if (channel is null || session is null)
        {
            return null;
        }

        var includeRawUsage = viewerIsManager;
        // Secret ballot, enforced server-side rather than hidden in the client. It lapses the moment
        // the session ends — IsActive is the only "is it over" signal in this codebase, and EndAsync
        // is therefore also the moment of the reveal, with no extra field or scheduler involved.
        var includeTallies = viewerIsManager || !session.HideResultsUntilEnd || !session.IsActive;

        var subsetEmoteIds = await db.VoteSessionEmotes
            .Where(se => se.VoteSessionId == sessionId)
            .Select(se => se.EmoteId)
            .ToListAsync(cancellationToken);

        // A set-session's ballot is never empty (spec section 9 invariant), so EmoteSetId != null is
        // the same test as "this is a fixed ballot" here — subsetEmoteIds.Count == 0 already implies
        // a null-session.
        var isSetSession = session.EmoteSetId is not null;

        // No membership rows = dynamic "all emotes" session: archived emotes vanish from the results,
        // exactly as before the subset feature. An explicit ballot (null-session subset or
        // set-session) keeps its members visible even once archived (badged in the UI for a
        // null-session; a set-session shows no such badge, section 9) so a curated list never loses
        // entries silently. A fixed ballot reads through VoteSessionEmotes rather than Emotes
        // directly, so a set-session's frozen NameAtCreation/ImageUrlAtCreation can win over the
        // live Emote row (null for a null-session's row, where the live Emote is always the answer —
        // VoteSessionEmote.cs).
        var candidateEmotes = subsetEmoteIds.Count == 0
            ? await db.Emotes
                .Where(e => e.ChannelId == channel.Id && !e.IsArchived)
                .Select(e => new CandidateEmote(e.Id, e.Name, e.SevenTvEmoteId, e.ImageUrl, e.IsArchived))
                .ToListAsync(cancellationToken)
            : await db.VoteSessionEmotes
                .Where(se => se.VoteSessionId == sessionId)
                .Select(se => new CandidateEmote(
                    se.EmoteId,
                    se.NameAtCreation ?? se.Emote.Name,
                    se.Emote.SevenTvEmoteId,
                    se.ImageUrlAtCreation ?? se.Emote.ImageUrl,
                    se.Emote.IsArchived))
                .ToListAsync(cancellationToken);

        var myVotesByEmoteId = viewerTwitchUserId is null
            ? new Dictionary<string, VoteType>()
            : await db.Votes
                .Where(v => v.VoteSessionId == sessionId && v.UserId == viewerTwitchUserId)
                .ToDictionaryAsync(v => v.EmoteId, v => v.Type, cancellationToken);

        var from = DateOnly.FromDateTime(session.StartedAt);
        var to = DateOnly.FromDateTime(session.EndedAt ?? DateTime.UtcNow);

        // Usage is manager-only context now and no part of the score, so the totals query can be
        // skipped entirely for everyone else. Scoped to the ballot rather than to the channel: a
        // subset session may hold twenty emotes out of a thousand, and asking for the channel's
        // totals meant zero-filling all thousand only to discard the rest here.
        // A set-session's own set (its ballot was drawn from it, spec section 9); a null-session
        // reads the channel's active set, exactly as before set-sessions existed.
        var usageByEmoteId = !includeRawUsage || candidateEmotes.Count == 0
            ? new Dictionary<string, int>()
            : await usageStatQueryService.GetTotalsByEmoteIdsAsync(
                candidateEmotes.Select(e => e.Id).ToList(), from, to, session.EmoteSetId ?? channel.ActiveEmoteSetId, cancellationToken);

        // Same as the usage totals above: not computed at all for a viewer who may not see them.
        var voteTallies = !includeTallies
            ? []
            : await db.Votes
                .Where(v => v.VoteSessionId == sessionId)
                .GroupBy(v => v.EmoteId)
                .Select(g => new VoteTallyRow(
                    g.Key,
                    g.Count(v => v.Type == VoteType.Keep),
                    g.Count(v => v.Type == VoteType.Delete)))
                .ToDictionaryAsync(t => t.EmoteId, cancellationToken);

        var voterCount = await db.Votes
            .Where(v => v.VoteSessionId == sessionId)
            .Select(v => v.UserId)
            .Distinct()
            .CountAsync(cancellationToken);

        var rows = candidateEmotes.Select(e =>
            BuildResultRow(e, isSetSession, includeTallies, includeRawUsage, voteTallies, myVotesByEmoteId, usageByEmoteId));

        // With the tallies withheld, the score ordering is the leak: the position of a row would spell
        // out its ranking just as precisely as the numbers did. Name order carries no such signal — and
        // it is what a voter working through a ballot wants anyway.
        var results = includeTallies
            // Delete candidates first: ascending net score, contested emotes before quiet ties, name as
            // the stable fallback so equal rows don't reshuffle between loads.
            ? rows.OrderBy(r => r.Score)
                .ThenByDescending(r => r.KeepVotes + r.DeleteVotes)
                .ThenBy(r => r.EmoteName, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : rows.OrderBy(r => r.EmoteName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.EmoteId, StringComparer.Ordinal)
                .ToList();

        return new VoteSessionResultsDto(
            session.Id, session.Title, session.AllowedVoterRoles, session.IsActive, session.StartedAt,
            session.EndedAt, voterCount, session.HideResultsUntilEnd, results, session.EmoteSetId);
    }

    public async Task<PagedResult<MyVoteSessionDto>> ListMyVoteSessionsAsync(string voterTwitchUserId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        // Group over Votes first, then join back to VoteSessions/Channel — grouping directly on a
        // navigation-joined query has previously failed to translate in this codebase (see
        // UsageStatQueryService.GetUsageTotalsAsync's decision-log entry), so the reduction to a
        // scalar (VoteSessionId, LastVotedAt) pair happens before any join.
        var votedSessionIds = db.Votes
            .Where(v => v.UserId == voterTwitchUserId)
            .GroupBy(v => v.VoteSessionId)
            .Select(g => new { SessionId = g.Key, LastVotedAt = g.Max(v => v.UpdatedAt) });

        var joined = votedSessionIds.Join(
            db.VoteSessions,
            x => x.SessionId,
            s => s.Id,
            (x, s) => new { x.LastVotedAt, s.Id, s.Title, ChannelName = s.Channel.ChannelName, s.IsActive, s.StartedAt, s.EndedAt });

        var totalCount = await joined.CountAsync(cancellationToken);
        var items = await joined
            .OrderByDescending(x => x.LastVotedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new MyVoteSessionDto(x.Id, x.Title, x.ChannelName, x.IsActive, x.StartedAt, x.EndedAt, x.LastVotedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<MyVoteSessionDto>(items, page, pageSize, totalCount);
    }

    /// <summary>Assembles one result row from a candidate emote plus the tallies/votes/usage looked up for it.</summary>
    private static VoteSessionResultDto BuildResultRow(
        CandidateEmote emote,
        bool isSetSession,
        bool includeTallies,
        bool includeRawUsage,
        IReadOnlyDictionary<string, VoteTallyRow> voteTallies,
        IReadOnlyDictionary<string, VoteType> myVotesByEmoteId,
        IReadOnlyDictionary<string, int> usageByEmoteId)
    {
        var tally = voteTallies.GetValueOrDefault(emote.Id);
        // null = withheld (running secret ballot, non-manager), not "nobody voted for it".
        int? keep = includeTallies ? tally?.Keep ?? 0 : null;
        int? delete = includeTallies ? tally?.Delete ?? 0 : null;
        var myVote = myVotesByEmoteId.TryGetValue(emote.Id, out var voteType) ? voteType : (VoteType?)null;

        // "member of the session's set" replaces "not archived" for a set-session (spec section 9):
        // its fixed ballot never closes to voting just because the member has since left 7TV — that
        // is exactly what the ballot froze at creation. A null-session keeps the archived-row rule.
        var eligible = isSetSession || !emote.IsArchived;

        // Non-manager: withheld, same as before. Null-session: unchanged — archived rows report no
        // usage (a fabricated 0 for a row GetUsageTotalsAsync excludes would be wrong), everything
        // else defaults a missing dictionary entry to a genuine 0 (no UsageStat row in range means
        // no use happened, not that the answer is unknown). Set-session: eligible is always true, so
        // that gate no longer applies — instead a missing dictionary entry means "no UsageStat row
        // under this set at all" and reports null rather than a fabricated 0 (spec section 9, AK 80).
        int? useCount = !includeRawUsage
            ? null
            : isSetSession
                ? usageByEmoteId.TryGetValue(emote.Id, out var setUseCount) ? setUseCount : null
                : !emote.IsArchived ? usageByEmoteId.GetValueOrDefault(emote.Id, 0) : null;

        return new VoteSessionResultDto(
            emote.Id, emote.Name, emote.SevenTvEmoteId, emote.ImageUrl, useCount, keep, delete, keep - delete,
            emote.IsArchived, eligible, myVote);
    }

    /// <summary>A candidate ballot row, projected by name instead of an anonymous type so <see cref="BuildResultRow"/> can take it as a parameter.</summary>
    private sealed record CandidateEmote(string Id, string Name, string SevenTvEmoteId, string ImageUrl, bool IsArchived);

    /// <summary>One emote's Keep/Delete tally, projected by name for the same reason as <see cref="CandidateEmote"/>.</summary>
    private sealed record VoteTallyRow(string EmoteId, int Keep, int Delete);
}
