using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

// EmoteCount: size of the session's explicit ballot; null = dynamic "all emotes" session.
// HideResultsUntilEnd: the session's secret-ballot setting, reported to every viewer — it describes
// the session, not the viewer's rights, so a badge can mark it while it is still running.
// EmoteSetId: null for a null-session (dynamic or fixed-by-guid ballot, spec section 9); non-null
// names the 7TV set a set-session's ballot is scoped to. Deliberately no EmoteSetName alongside it
// — the page already holds the channel's set list for the dropdown and looks the name up there
// (spec 6.9), same idiom as elsewhere in this codebase.
public record VoteSessionSummaryDto(
    long Id, string Title, AllowedRoles AllowedVoterRoles, bool IsActive, DateTime StartedAt, DateTime? EndedAt,
    int? EmoteCount, bool HideResultsUntilEnd, string? EmoteSetId);

// TotalUseCount is manager-only context (null for everyone else — and for archived subset emotes of
// a null-session, whose usage totals are no longer computed). Score = KeepVotes - DeleteVotes; chat
// usage is deliberately not part of it, managers already spent the usage data when curating the
// ballot.
// IsArchived marks a subset emote that left the 7TV set mid-session (null-session only): it stays
// listed with its votes, but casting further votes on it is rejected.
// Eligible drives that vote gate instead of IsArchived (spec section 9): true for every set-session
// row (its fixed ballot never closes to voting, even once the member has left 7TV — that is exactly
// what the ballot froze at creation), and !IsArchived for a null-session row, same rule as before.
// KeepVotes/DeleteVotes/Score are null together on a running secret-ballot session for everyone but
// a manager — same "null = withheld, not zero" convention as TotalUseCount. MyVote is never
// withheld: a voter must always see their own ballot.
public record VoteSessionResultDto(
    string EmoteId, string EmoteName, string SevenTvEmoteId, string ImageUrl, int? TotalUseCount,
    int? KeepVotes, int? DeleteVotes, int? Score, bool IsArchived, bool Eligible, VoteType? MyVote);

// VoterCount = distinct voters across the whole session, so thin results can be labeled as such.
// It stays visible on a secret ballot: turnout says nothing about which way the votes went.
// AllowedVoterRoles is reported here for the same reason it is in the summary: the audience is fixed
// at creation and was previously visible only in the create form, so nobody could tell afterwards
// whom a session had been addressed to. Like HideResultsUntilEnd it describes the session, not the
// viewer's rights, and goes to every viewer. EmoteSetId: see VoteSessionSummaryDto.
public record VoteSessionResultsDto(
    long SessionId, string Title, AllowedRoles AllowedVoterRoles, bool IsActive, DateTime StartedAt,
    DateTime? EndedAt, int VoterCount, bool HideResultsUntilEnd, IReadOnlyList<VoteSessionResultDto> Emotes,
    string? EmoteSetId);

// A session the given voter has cast at least one vote in, ever — regardless of whether it's since
// ended. LastVotedAt is the max UpdatedAt across that voter's votes in the session, used to order
// "My Votings" by most-recently-interacted-with first.
public record MyVoteSessionDto(long SessionId, string Title, string ChannelName, bool IsActive, DateTime StartedAt, DateTime? EndedAt, DateTime LastVotedAt);

public interface IVoteSessionQueryService
{
    Task<IReadOnlyList<VoteSessionSummaryDto>> ListSessionsAsync(string channelName, CancellationToken cancellationToken = default);

    // DB-level Skip/Take + Count, same ordering (creation order, newest first) as ListSessionsAsync. Only
    // used on the "manager sees everything" path in VoteSessionEndpoints.cs — the non-manager path
    // still needs the full unpaged ListSessionsAsync list to run its per-session eligibility filter
    // first (filter-then-paginate, see VoteSessionEndpoints.cs comment), so that method is left
    // untouched.
    Task<PagedResult<VoteSessionSummaryDto>> ListSessionsPagedAsync(string channelName, int page, int pageSize, CancellationToken cancellationToken = default);

    // null = channel or session not found / session doesn't belong to that channel.
    // viewerTwitchUserId is optional at this service layer (kept nullable for testability), but the
    // Api endpoint always supplies it now — VoteAudienceFilter guarantees an authenticated viewer
    // before this is called, so each result row's MyVote reflects that viewer's own vote.
    // viewerIsManager (admin/broadcaster/live-moderator — CanManageChannelAsync) drives the two
    // pieces of the read model that are not for everyone:
    //   * TotalUseCount: false reports null per emote (data absent, not "used 0 times"). Raw
    //     per-emote chat usage is management data (the usage-stats endpoints are access-filtered for
    //     that reason), and a session's audience can include every logged-in user.
    //   * The tallies of a running VoteSession.HideResultsUntilEnd session: KeepVotes/DeleteVotes/
    //     Score are null and the rows come back in name order instead of score order — with the
    //     numbers gone, the score ordering would still spell out the ranking. Once the session ends,
    //     everyone sees both again.
    // Neither affects MyVote, which is always the viewer's own vote.
    Task<VoteSessionResultsDto?> GetResultsAsync(string channelName, long sessionId, string? viewerTwitchUserId = null, bool viewerIsManager = false, CancellationToken cancellationToken = default);

    // Cross-channel "My Votings": every session the given voter has ever voted in, across all
    // channels, ordered by most-recently-voted-in first. Deliberately not gated by current audience
    // eligibility (VoteEligibilityService) — this is the voter's own history, not a fresh invitation
    // to vote, so an ended or since-restricted session still belongs in their own list.
    Task<PagedResult<MyVoteSessionDto>> ListMyVoteSessionsAsync(string voterTwitchUserId, int page, int pageSize, CancellationToken cancellationToken = default);
}
