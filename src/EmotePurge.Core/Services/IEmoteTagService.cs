namespace EmotePurge.Core.Services;

public enum EmoteTagListStatus
{
    Ok,
    ChannelNotFound
}

public enum EmoteTagEntriesStatus
{
    Ok,
    ChannelNotFound,
    TagNotFound
}

/// <summary>
/// Outcome of creating, renaming or deleting a tag. <see cref="LimitReached"/> only comes from
/// creating, <see cref="NameInvalid"/>/<see cref="NameTaken"/> only from creating and renaming;
/// deleting answers <see cref="Ok"/>, <see cref="ChannelNotFound"/> or <see cref="TagNotFound"/>.
/// </summary>
public enum EmoteTagMutationStatus
{
    Ok,
    ChannelNotFound,
    TagNotFound,
    NameInvalid,
    NameTaken,
    LimitReached
}

public enum EmoteTagAddEntriesStatus
{
    Ok,
    ChannelNotFound,
    TagNotFound,
    EmoteIdsEmpty,
    EmoteIdsInvalid,
    EntryLimitReached
}

public enum EmoteTagRemoveEntriesStatus
{
    Ok,
    ChannelNotFound,
    TagNotFound,
    EmoteIdsEmpty,
    EmoteIdsInvalid
}

/// <summary>
/// Outcome of registering a tag operation. <see cref="Replayed"/>: the same operation id was already
/// registered for the same tag, kind and set — the stored registration time comes back unchanged.
/// <see cref="Conflict"/>: the id is registered for another tag, kind or set.
/// </summary>
public enum TagOperationRegistrationStatus
{
    Ok,
    Replayed,
    ChannelNotFound,
    TagNotFound,
    Conflict
}

/// <summary>
/// Outcome of a tag report (play-in or removal). <see cref="OperationUnknown"/>: no operation
/// with that id is registered. <see cref="OperationConflict"/>: it is registered for another tag, set or
/// kind. A report of an operation that was already applied is <see cref="Ok"/> with <c>Replayed</c> set.
/// </summary>
public enum TagReportStatus
{
    Ok,
    ChannelNotFound,
    TagNotFound,
    OperationUnknown,
    OperationConflict
}

public sealed record EmoteTagDto(long Id, string Name);

/// <summary>Another tag of the same channel, as the entry read names it.</summary>
public sealed record EmoteTagRefDto(long Id, string Name);

/// <param name="InSetCount">
/// Entries whose emote has an unarchived row in the channel; <c>null</c> when the resolved set is not
/// the channel's active set (see <see cref="IEmoteTagService"/>).
/// </param>
/// <param name="PlacedCount">The tag's valid placements in the resolved set (read-time rule); 0 without a set.</param>
/// <param name="Active">Whether the tag counts as played in to the resolved set (an activation row exists); <c>false</c> without a set.</param>
/// <param name="ActivatedAtUtc">When that activation was last set; <c>null</c> when <paramref name="Active"/> is <c>false</c>.</param>
public sealed record EmoteTagSummaryDto(
    long Id, string Name, int EntryCount, int? InSetCount, int PlacedCount, bool Active, DateTime? ActivatedAtUtc);

/// <param name="Alias">The emote's name when it was tagged (snapshot).</param>
/// <param name="ImageUrl">The emote's image when it was tagged (snapshot).</param>
/// <param name="InSet">
/// Whether the emote has an unarchived row in the channel; <c>null</c> when the resolved set is not the
/// channel's active set.
/// </param>
/// <param name="CurrentName">The row's current name when <paramref name="InSet"/> is <c>true</c>, otherwise <c>null</c>.</param>
/// <param name="PlacedByThisTag">Whether this tag holds a valid placement of the emote in the resolved set (read-time rule).</param>
/// <param name="PlacedAtUtc">That placement's <c>PlacedAtUtc</c>; <c>null</c> without a valid placement.</param>
/// <param name="PlacementOperationId">That placement's revision (its <c>OperationId</c>); <c>null</c> without a valid placement.</param>
/// <param name="HeldByActiveTags">
/// The channel's other tags that are active in the resolved set and have an entry for the emote,
/// placements or not; oldest tag first (<c>CreatedAtUtc</c>, then id).
/// </param>
/// <param name="PlacedByOtherTags">The channel's other tags with a valid placement of the emote in the resolved set; same order.</param>
public sealed record EmoteTagEntryDto(
    string SevenTvEmoteId,
    string Alias,
    string ImageUrl,
    bool? InSet,
    string? CurrentName,
    bool PlacedByThisTag,
    DateTime? PlacedAtUtc,
    Guid? PlacementOperationId,
    IReadOnlyList<EmoteTagRefDto> HeldByActiveTags,
    IReadOnlyList<EmoteTagRefDto> PlacedByOtherTags);

/// <param name="EmoteSetId">The set the set-related fields refer to; <c>null</c> when none was asked for and the channel has no active set.</param>
/// <param name="Tags">The channel's tags in creation order.</param>
public sealed record EmoteTagListResult(
    EmoteTagListStatus Status, string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagSummaryDto> Tags);

/// <param name="Entries">The tag's entries in the order they were added (ties by 7TV id).</param>
/// <param name="ActivationOperationId">
/// The operation of the tag's activation in the resolved set, or <c>null</c> when it is not active there
/// (or there is no set). A removal run hands it back in its report.
/// </param>
public sealed record EmoteTagEntriesResult(
    EmoteTagEntriesStatus Status,
    string? EmoteSetId,
    bool IsActiveSet,
    IReadOnlyList<EmoteTagEntryDto> Entries,
    Guid? ActivationOperationId);

/// <param name="Tag">The tag as stored after the mutation; <c>null</c> unless <paramref name="Status"/> is <c>Ok</c>.</param>
public sealed record EmoteTagMutationResult(EmoteTagMutationStatus Status, EmoteTagDto? Tag);

/// <param name="AddedCount">Entries actually written.</param>
/// <param name="AlreadyTaggedCount">Requested emotes in the set that already carried the tag.</param>
/// <param name="SkippedNotInSetIds">Requested ids without an unarchived row in the channel, in request order; nothing was written for them.</param>
public sealed record EmoteTagAddEntriesResult(
    EmoteTagAddEntriesStatus Status, int AddedCount, int AlreadyTaggedCount, IReadOnlyList<string> SkippedNotInSetIds);

/// <param name="RemovedCount">Entries actually deleted; ids the tag did not carry are not counted and are no error.</param>
public sealed record EmoteTagRemoveEntriesResult(EmoteTagRemoveEntriesStatus Status, int RemovedCount);

/// <param name="OperationId">Generated by the browser for one run.</param>
/// <param name="Kind">One of <see cref="Entities.EmoteTagOperationKind"/>; anything else is the caller's error.</param>
/// <param name="EmoteSetId">The set the run will write to, frozen at the click.</param>
public sealed record RegisterTagOperationRequest(Guid OperationId, string Kind, string EmoteSetId);

/// <param name="RegisteredAtUtc">The server's registration time; <c>null</c> unless <paramref name="Status"/> is <c>Ok</c> or <c>Replayed</c>.</param>
public sealed record TagOperationRegistrationResult(TagOperationRegistrationStatus Status, DateTime? RegisteredAtUtc);

/// <param name="OperationId">The registered play-in operation the report belongs to.</param>
/// <param name="EmoteSetId">The set the run wrote to; must be the registered one.</param>
/// <param name="SevenTvEmoteIds">The ids the run actually added; empty is legal (a play-in that added nothing).</param>
public sealed record TagPlacementReport(Guid OperationId, string EmoteSetId, IReadOnlyList<string> SevenTvEmoteIds);

/// <param name="Replayed">The operation had already been applied; nothing was written and every count is 0.</param>
/// <param name="RecordedCount">Placements newly created.</param>
/// <param name="AlreadyRecordedCount">Placements that existed already and were overwritten (operation, placement time, anchor).</param>
/// <param name="NotTaggedIds">Reported ids the tag has no entry for, in request order; not placed.</param>
/// <param name="DiscardedStaleIds">
/// Reported ids with a leave observed in that set after the operation was registered, in request
/// order; discarded for good, not placed.
/// </param>
public sealed record TagPlacementReportResult(
    TagReportStatus Status,
    bool Replayed,
    int RecordedCount,
    int AlreadyRecordedCount,
    IReadOnlyList<string> NotTaggedIds,
    IReadOnlyList<string> DiscardedStaleIds);

/// <param name="SevenTvEmoteId">An own placement the removal preview captured.</param>
/// <param name="PlacementOperationId">
/// The revision the preview read (the placement's <c>OperationId</c>). The report touches the row only
/// while it still carries that revision; a row rewritten since is left alone.
/// </param>
public sealed record TagPlacementSnapshotEntry(string SevenTvEmoteId, Guid PlacementOperationId);

/// <param name="OperationId">The registered removal operation the report belongs to.</param>
/// <param name="EmoteSetId">The set the run cleared; must be the registered one.</param>
/// <param name="ActivationOperationId">
/// The tag's activation operation as the preview read it; the tag is deactivated only if the
/// activation still carries it. <c>null</c> (no activation read) never deactivates.
/// </param>
/// <param name="Snapshot">The tag's own valid placements as the preview captured them, each with its revision.</param>
/// <param name="RemovedIds">The ids the delete run actually removed from the set (empty without a run).</param>
/// <param name="KeptIds">The ids of own placements that stay in the set (unchecked by the person, or not done).</param>
public sealed record TagRemovalReport(
    Guid OperationId,
    string EmoteSetId,
    Guid? ActivationOperationId,
    IReadOnlyList<TagPlacementSnapshotEntry> Snapshot,
    IReadOnlyList<string> RemovedIds,
    IReadOnlyList<string> KeptIds);

/// <param name="Replayed">
/// The operation had already been applied; nothing was written, every count is 0 and
/// <paramref name="Deactivated"/> is <c>false</c>. On a replay none of these fields describes the
/// outcome — the first application's counts and whether it deactivated the tag are not reconstructed,
/// so a caller must not show or act on them (re-read the tag instead).
/// </param>
/// <param name="DeletedCount">Snapshot hits among <c>RemovedIds</c>, deleted.</param>
/// <param name="TransferredCount">
/// Snapshot hits among <c>KeptIds</c> handed to the oldest other tag that is active in the set and has
/// an entry for the emote (whether that tag already held the emote validly, or got the row — a new
/// one, or its own expired row rewritten). Only when <paramref name="Deactivated"/>.
/// </param>
/// <param name="DroppedCount">
/// Snapshot hits deleted without a transfer: kept ones without such a tag (or already expired), and
/// hits neither removed nor kept.
/// </param>
/// <param name="SweptCount">
/// Placements the snapshot did not hit — wandered in, re-placed, revised or expired — transferred or
/// deleted by the deactivation sweep. Only when <paramref name="Deactivated"/>.
/// </param>
/// <param name="Deactivated">The activation carried the reported operation and was removed.</param>
public sealed record TagRemovalReportResult(
    TagReportStatus Status,
    bool Replayed,
    int DeletedCount,
    int TransferredCount,
    int DroppedCount,
    int SweptCount,
    bool Deactivated);

/// <summary>
/// Channel-owned emote tags (#201): creating, renaming and deleting tags, and putting emotes into and
/// out of them by 7TV emote id. Every method takes the channel name raw and normalizes it itself
/// (rule 9); a tag id is always looked up together with its channel, so a tag of another channel is
/// <c>TagNotFound</c>, never "found but foreign".
/// <para>
/// <b>Set resolution</b> (both reads): <c>emoteSetId</c> absent (null or empty) means the channel's
/// active set. Without one, <c>EmoteSetId</c> is <c>null</c>, <c>IsActiveSet</c> is <c>false</c> and
/// every set-related field is empty. Otherwise <c>IsActiveSet</c> says whether the resolved id is the
/// channel's active set.
/// </para>
/// <para>
/// <b>"In the set"</b> means an unarchived <c>Emote</c> row of the channel with that 7TV id. That
/// status is channel-wide, not per set — the sync only observes the active set — so
/// <c>InSetCount</c>/<c>InSet</c> are only meaningful for the active set and are <c>null</c> for any
/// other <c>emoteSetId</c>.
/// </para>
/// <para>
/// <b>Placements and activations</b> (part C of #201) are per set, so both reads compute them for any
/// resolved set, active or not, and leave them empty without one. Every placement field counts only
/// <em>valid</em> placements — the read-time rule: a placement holds unless the channel has a leave
/// observation for the same emote and set that is later than the placement's own
/// <c>RegisteredAtUtc</c> (the registration time of the operation that last wrote it, carried on the
/// placement itself; the operation row is not consulted). Activations are never affected by
/// observations.
/// </para>
/// </summary>
public interface IEmoteTagService
{
    Task<EmoteTagListResult> ListAsync(string channelName, string? emoteSetId, CancellationToken cancellationToken = default);

    Task<EmoteTagEntriesResult> ListEntriesAsync(
        string channelName, long tagId, string? emoteSetId, CancellationToken cancellationToken = default);

    /// <param name="name">Raw user input; <c>null</c> is <c>NameInvalid</c>, like any other unfit name.</param>
    Task<EmoteTagMutationResult> CreateAsync(
        string channelName, string? name, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a tag. The tag's own name in another casing or padding is allowed (the normalized form
    /// stays, the display name changes); the exact same name is a no-op without an audit entry.
    /// </summary>
    Task<EmoteTagMutationResult> RenameAsync(
        string channelName, long tagId, string? name, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a tag; its entries go with it (FK cascade).</summary>
    Task<EmoteTagMutationStatus> DeleteAsync(
        string channelName, long tagId, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tags emotes of the channel's set. Partial success by design: ids without an unarchived row are
    /// skipped and reported, ids already tagged are counted, the rest is written with alias and image
    /// taken from the row (never from the client). The entry limit is checked before anything is
    /// written: if the tag would exceed it, nothing is written at all. No audit entry.
    /// </summary>
    /// <param name="sevenTvEmoteIds">Duplicates are ignored; <c>null</c> or empty is <c>EmoteIdsEmpty</c>.</param>
    Task<EmoteTagAddEntriesResult> AddEntriesAsync(
        string channelName, long tagId, IReadOnlyList<string>? sevenTvEmoteIds, CancellationToken cancellationToken = default);

    /// <summary>Takes emotes out of a tag. Ids the tag does not carry are tolerated. No audit entry.</summary>
    /// <param name="sevenTvEmoteIds">Duplicates are ignored; <c>null</c> or empty is <c>EmoteIdsEmpty</c>.</param>
    Task<EmoteTagRemoveEntriesResult> RemoveEntriesAsync(
        string channelName, long tagId, IReadOnlyList<string>? sevenTvEmoteIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a play-in or removal run before the browser writes to 7TV (E27): the server stamps the
    /// registration time a later report is judged against. Idempotent for the same operation id, tag,
    /// kind and set; the same id for anything else is <c>Conflict</c>. No audit entry — a registration
    /// is an intent, not an event, hence no actor either.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is not one of <see cref="Entities.EmoteTagOperationKind"/>.</exception>
    Task<TagOperationRegistrationResult> RegisterOperationAsync(
        string channelName, long tagId, RegisterTagOperationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a play-in run actually added and marks the tag as played in to the set. Applied once
    /// per operation; a repeat is <c>Replayed</c> and writes nothing. Ids the tag has no entry for are
    /// not placed, nor are ids with a leave observed after the registration. The activation is set even
    /// when nothing was placed. The set need not be the channel's active one. Audited as
    /// <c>tag.playedIn</c> without the tag's name.
    /// </summary>
    Task<TagPlacementReportResult> ReportPlacementsAsync(
        string channelName, long tagId, TagPlacementReport report, AuditActor actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies what a removal run did to the tag's placements in the set and, if the activation the
    /// preview read is still the current one, deactivates the tag there. Applied once per operation; a
    /// repeat is <c>Replayed</c> and writes nothing. A snapshot entry touches a placement only if id
    /// <em>and</em> revision still match. Removed hits are deleted; kept hits are handed to the oldest
    /// other tag that is active in the set and has an entry for the emote, or dropped; hits neither
    /// removed nor kept are dropped. On deactivation every remaining placement of the tag in the set is
    /// swept the same way, so an inactive tag never holds a placement; an expired placement is only
    /// ever deleted, never transferred. A target whose own row for the emote has expired gets that row
    /// rewritten as if it were absent. Audited as <c>tag.removed</c> without the tag's name.
    /// <para>
    /// On <c>Replayed</c> the result carries no outcome: the counts are 0 and <c>Deactivated</c> is
    /// <c>false</c> whatever the first application did.
    /// </para>
    /// </summary>
    Task<TagRemovalReportResult> ReportRemovalAsync(
        string channelName, long tagId, TagRemovalReport report, AuditActor actor, CancellationToken cancellationToken = default);
}
