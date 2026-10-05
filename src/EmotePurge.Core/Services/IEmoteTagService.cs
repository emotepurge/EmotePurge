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
/// observation for the same emote and set that is later than the registration of the operation that
/// last wrote the placement. A placement whose operation is missing does not hold either. Activations
/// are never affected by observations.
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
}
