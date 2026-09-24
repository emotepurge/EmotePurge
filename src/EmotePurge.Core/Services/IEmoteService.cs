namespace EmotePurge.Core.Services;

// ArchivedCount is the idempotent "goal state reached" count (already-archived rows included) that
// the caller reports back to the user; NewlyArchivedCount is the subset this call actually wrote —
// the only thing that may trigger a channel.synced live event.
// TargetIsActiveSetOfChannel (spec 6.6, T5.2) is part of the HTTP response: the legacy call always
// targets the active set (E3), which is why it defaults to true — the set-scoped overload below is
// the only caller that ever passes false.
public record SyncDeletedResultDto(int ArchivedCount, IReadOnlyList<string> NotFoundIds, int NewlyArchivedCount, bool TargetIsActiveSetOfChannel = true);

// Mirror of SyncDeletedResultDto for the restore direction: RestoredCount is the goal-state count,
// NewlyRestoredCount the subset this call actually un-archived.
public record SyncRestoredResultDto(int RestoredCount, IReadOnlyList<string> NotFoundIds, int NewlyRestoredCount, bool TargetIsActiveSetOfChannel = true);

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
    // Soft-archive (IsArchived=true), never a hard delete — see CLAUDE.md decision log on why
    // emote rows must survive a 7TV deletion (UsageStat/Vote history cascades off Emote.Id).
    // Idempotent: an already-archived emote counts into ArchivedCount (the goal state is reached —
    // with the EventAPI live sync enabled, the worker routinely archives the emote before this
    // bookkeeping call arrives). Only ids unknown or belonging to another channel land in
    // NotFoundIds instead of failing the whole batch.
    // actor is audited (emotes.syncDeleted) together with the archiving, in the same transaction —
    // whenever the goal-state count is > 0, not only when this call changed rows: the user's delete
    // on 7TV happened either way, and gating the paper trail on winning the race against the live
    // sync made most real deletes invisible in the audit log.
    // This is the legacy body form (spec 6.6, E3): it logs one Information line ("sync-deleted:
    // legacy body form {emoteIds} used") per call, so the decision when to retire it (Folge-Issue 1,
    // 14 days after deploy at the earliest) is measured rather than guessed.
    Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);

    // The restore counterpart (A6 in-app restore): un-archives (IsArchived=false, ArchivedAt=null)
    // the given emotes after the browser re-added them to the 7TV set. Same idempotence, audit and
    // legacy-logging semantics as MarkDeletedAsync ("sync-restored: legacy body form {emoteIds}
    // used"), with emotes.syncRestored as the audit action.
    Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);

    // The set-scoped overload (spec 6.6, T5.2): supplements the Guid-keyed overload above rather than
    // replacing it — the legacy body form (E3) still needs it until Folge-Issue 1 retires that form,
    // 14 days after deploy at the earliest and only once the API log shows no more legacy callers.
    // emoteSetId == channel.ActiveEmoteSetId matches by (ChannelId, SevenTvEmoteId) instead of
    // Emote.Id — the unique index on that pair gives the same precision the Guid match had — and
    // archives/audits exactly like the legacy path, with emoteSetId and
    // TargetIsActiveSetOfChannel = true added to the audit details, plus TargetType = "emoteSet",
    // TargetId = emoteSetId on the entry itself. A different emoteSetId is paper-only: no Emote row
    // exists to match against outside the active set, so nothing is archived — only the audit trail
    // (emoteCount = the deduplicated 7TV id count, TargetIsActiveSetOfChannel = false) records that
    // the report happened. sevenTvEmoteIds is deduplicated ordinally before counting, the same
    // discipline MarkImportedAsync already uses.
    Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, string emoteSetId, IReadOnlyList<string> sevenTvEmoteIds, AuditActor actor, CancellationToken cancellationToken = default);

    // Mirror of the set-scoped MarkDeletedAsync overload, in the restore direction.
    Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, string emoteSetId, IReadOnlyList<string> sevenTvEmoteIds, AuditActor actor, CancellationToken cancellationToken = default);

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
