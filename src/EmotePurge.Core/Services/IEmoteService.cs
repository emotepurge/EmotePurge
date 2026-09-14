namespace EmotePurge.Core.Services;

// ArchivedCount is the idempotent "goal state reached" count (already-archived rows included) that
// the caller reports back to the user; NewlyArchivedCount is the subset this call actually wrote —
// the only thing that may trigger a channel.synced live event. Not part of the HTTP response.
public record SyncDeletedResultDto(int ArchivedCount, IReadOnlyList<string> NotFoundIds, int NewlyArchivedCount);

// Mirror of SyncDeletedResultDto for the restore direction: RestoredCount is the goal-state count,
// NewlyRestoredCount the subset this call actually un-archived.
public record SyncRestoredResultDto(int RestoredCount, IReadOnlyList<string> NotFoundIds, int NewlyRestoredCount);

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
    Task<SyncDeletedResultDto> MarkDeletedAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);

    // The restore counterpart (A6 in-app restore): un-archives (IsArchived=false, ArchivedAt=null)
    // the given emotes after the browser re-added them to the 7TV set. Same idempotence and audit
    // semantics as MarkDeletedAsync, with emotes.syncRestored as the audit action.
    Task<SyncRestoredResultDto> MarkRestoredAsync(string channelName, IReadOnlyList<string> emoteIds, AuditActor actor, CancellationToken cancellationToken = default);

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
    Task<bool> MarkImportedAsync(string channelName, IReadOnlyList<string> sevenTvEmoteIds, string? sourceChannelName, string sourceKind, string? leaderboardSort, AuditActor actor, CancellationToken cancellationToken = default);
}
