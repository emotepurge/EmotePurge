namespace EmotePurge.Core.Services;

// ArchivedCount is the count of the reported ids that exist as rows of this channel (restore-per-set
// spec 5.6/E4): the legacy Guid-keyed call no longer archives anything itself, so this is a found
// count, not a "goal state reached" one — it exists only so an old open tab still reads its report as
// succeeded (E24) rather than a false partial. No live event is ever published for this form any more
// (nothing changed), which is why there is no NewlyArchivedCount here — that field died with the
// row-changing behavior it used to gate.
public record SyncDeletedResultDto(int ArchivedCount, IReadOnlyList<string> NotFoundIds);

// Mirror of SyncDeletedResultDto for the restore direction: RestoredCount is the same kind of found
// count, not a changed one.
public record SyncRestoredResultDto(int RestoredCount, IReadOnlyList<string> NotFoundIds);

// The set-centric report (restore-per-set spec 5.2/5.3): one entry per tracked channel whose active
// set is the reported set. Count is the goal-state count (rows found for the reported ids in this
// channel, already-archived/already-active ones included) and goes on the wire as archivedCount or
// restoredCount; NewlyChangedCount is the subset this call actually wrote and never leaves the
// server — it only decides whether the endpoint publishes channel.synced (spec 5.4). NotFoundIds are
// the reported 7TV ids with no row in *this* channel.
public record SyncInSetChannelResultDto(string ChannelName, int Count, int NewlyChangedCount, IReadOnlyList<string> NotFoundIds);

// The channel the client expected to hit (spec E18) when the report did not hit it. Reason is one of
// UnresolvedChannelReasons; ChannelName is the normalized name the client sent.
public record UnresolvedChannelDto(string ChannelName, string Reason);

// The two wire values of UnresolvedChannelDto.Reason (spec 5.3). NotTracked deliberately also
// covers a channel on the block list (Channels:ExcludedChannelIds): the block is never revealed
// here, the same way channel_excluded only ever exists at join time.
public static class UnresolvedChannelReasons
{
    public const string NotTracked = "notTracked";
    public const string ActiveSetDiffers = "activeSetDiffers";
}

// ReportedCount = the reported 7TV ids after ordinal deduplication. Channels empty and
// UnresolvedChannel null together mean "paper only": no tracked channel has this set active.
public record SyncDeletedInSetResultDto(int ReportedCount, IReadOnlyList<SyncInSetChannelResultDto> Channels, UnresolvedChannelDto? UnresolvedChannel);

// Mirror of SyncDeletedInSetResultDto for the restore direction.
public record SyncRestoredInSetResultDto(int ReportedCount, IReadOnlyList<SyncInSetChannelResultDto> Channels, UnresolvedChannelDto? UnresolvedChannel);

public interface IEmoteService
{
    // The legacy body form (restore-per-set spec 5.6, E4): channel-bound, Guid-keyed, and kept alive
    // only as audit-plus-resync until the E3 gate of the spec-200 plan (§21, Folge-Issue 1: behind
    // K7, 14 days after deploy at the earliest, once the API log shows no more legacy callers). It no
    // longer touches a row at all (H4) — a tab left open across a 7TV set switch would otherwise
    // archive a row of the *new* active set on the strength of a body that only ever meant the old
    // one, since this form carries no set of its own. It only counts how many of the reported Guids
    // are rows of this channel (ArchivedCount) and audits that count with legacyBodyForm = true in
    // the details, whenever it is > 0 — the endpoint's own guarded resync (spec 5.1 stage 7) is what
    // actually reconciles the channel against 7TV afterwards.
    // actor is audited (emotes.syncDeleted) in the same transaction as the count is read.
    // It logs one Information line ("sync-deleted: legacy body form {emoteIds} used") per call, so
    // the decision when to retire it is measured rather than guessed.
    Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);

    // The restore counterpart (A6 in-app restore): same row-untouched, audit-with-legacyBodyForm and
    // logging semantics as MarkDeletedAsync ("sync-restored: legacy body form {emoteIds} used"), with
    // emotes.syncRestored as the audit action.
    Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);

    // The set-centric report (restore-per-set spec 5.2): the reported set, not a channel, is the
    // subject. The caller (the endpoint's owner check, IImportTargetOwnershipService.CheckAsync) has
    // already resolved ownerSevenTvUserId/ownerTwitchLogin. Every tracked channel whose active set is
    // emoteSetId (IsBotActive, not on the block list) is hit: its rows matched by (ChannelId,
    // SevenTvEmoteId) are archived — only the not-yet-archived ones, so an earlier archive date
    // survives — and one audit entry naming that channel is written when rows were found.
    // expectedChannelName (spec E18) is the channel the client meant to hit; if it is not among the
    // hits it comes back as UnresolvedChannel (notTracked / activeSetDiffers), and none of its rows is
    // touched. A paper entry (ChannelName = null, owner identity in the details) is written whenever
    // no channel entry was, or a channel stayed unresolved — so every call leaves at least one audit
    // entry. Rows and audit entries are saved together, in one SaveChangesAsync.
    Task<SyncDeletedInSetResultDto> MarkDeletedInSetAsync(
        string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, IReadOnlyList<string> sevenTvEmoteIds,
        string? expectedChannelName, AuditActor actor, CancellationToken cancellationToken = default);

    // Mirror of MarkDeletedInSetAsync, in the restore direction: un-archives (IsArchived = false,
    // ArchivedAt = null) only the rows that are still archived, and audits emotes.syncRestored.
    Task<SyncRestoredInSetResultDto> MarkRestoredInSetAsync(
        string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, IReadOnlyList<string> sevenTvEmoteIds,
        string? expectedChannelName, AuditActor actor, CancellationToken cancellationToken = default);

    // Unlike MarkDeletedAsync/MarkRestoredAsync, this touches no Emote row at all: an import never
    // creates or un-archives anything here, the target channel's own resync does that afterwards
    // (the "Nachlauf-Gate" in the import design). This call exists purely to leave the one thing a
    // resync cannot reconstruct — an emotes.syncImported audit entry naming how many 7TV ids were
    // reported and where they came from. sevenTvEmoteIds is deduplicated ordinally before counting
    // (a client that reported the same id twice did not import it twice); sourceChannelName is the
    // normalized source channel, or null for a file import or a leaderboard import, neither of
    // which carries one; sourceKind is "channel", "file", "seventv-channel" or "seventv-leaderboard".
    // leaderboardSort is the 7TV sort wire code (SevenTvLeaderboardSortWireCode) for a
    // "seventv-leaderboard" import — the only kind that has one, since a network-wide ranking has no
    // source channel to name (leaderboard-import spec E8) — and null for every other kind. Returns
    // false — writing nothing — for an unknown target channel.
    // targetEmoteSetId (spec 6.7, E5) stays optional forever: a client that omits it (an old open
    // tab) is still a valid call. When set, the written audit entry additionally carries
    // targetEmoteSetId and targetIsActiveSetOfChannel (a three-valued comparison against
    // Channel.ActiveEmoteSetId taken at write time — true/false when targetEmoteSetId is set, null
    // when it is not) plus TargetType = "emoteSet"/TargetId = targetEmoteSetId on the entry itself.
    Task<bool> MarkImportedAsync(
        string channelName, IReadOnlyList<string> sevenTvEmoteIds, string? sourceChannelName, string sourceKind,
        string? leaderboardSort, AuditActor actor, string? targetEmoteSetId = null, CancellationToken cancellationToken = default);

    // The set-centric counterpart (spec 6.7): the import's target is an arbitrary 7TV emote set, not
    // necessarily one belonging to any channel EmotePurge tracks — so there is no Channel row to load
    // and no "unknown target" failure the way MarkImportedAsync has one. The caller (the endpoint's
    // owner check, IImportTargetOwnershipService.CheckAsync) has already resolved
    // ownerSevenTvUserId/ownerTwitchLogin by the time this runs; this call only ever writes the audit
    // entry — ChannelName = null (so the row surfaces in the global admin log, not a channel's own
    // activity feed — a deliberate rest, not a bug, since the target may not be a channel at all),
    // TargetType = "emoteSet", TargetId = emoteSetId, with sevenTvEmoteIds deduplicated ordinally
    // before counting like MarkImportedAsync.
    Task MarkImportedToSetAsync(
        string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, IReadOnlyList<string> sevenTvEmoteIds,
        string? sourceChannelName, string sourceKind, string? leaderboardSort, AuditActor actor,
        CancellationToken cancellationToken = default);
}
