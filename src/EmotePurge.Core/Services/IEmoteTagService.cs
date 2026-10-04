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

/// <param name="InSetCount">
/// Entries whose emote has an unarchived row in the channel; <c>null</c> when the resolved set is not
/// the channel's active set (see <see cref="IEmoteTagService"/>).
/// </param>
public sealed record EmoteTagSummaryDto(long Id, string Name, int EntryCount, int? InSetCount);

/// <param name="Alias">The emote's name when it was tagged (snapshot).</param>
/// <param name="ImageUrl">The emote's image when it was tagged (snapshot).</param>
/// <param name="InSet">
/// Whether the emote has an unarchived row in the channel; <c>null</c> when the resolved set is not the
/// channel's active set.
/// </param>
/// <param name="CurrentName">The row's current name when <paramref name="InSet"/> is <c>true</c>, otherwise <c>null</c>.</param>
public sealed record EmoteTagEntryDto(string SevenTvEmoteId, string Alias, string ImageUrl, bool? InSet, string? CurrentName);

/// <param name="EmoteSetId">The set the set-related fields refer to; <c>null</c> when none was asked for and the channel has no active set.</param>
/// <param name="Tags">The channel's tags in creation order.</param>
public sealed record EmoteTagListResult(
    EmoteTagListStatus Status, string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagSummaryDto> Tags);

/// <param name="Entries">The tag's entries in the order they were added (ties by 7TV id).</param>
public sealed record EmoteTagEntriesResult(
    EmoteTagEntriesStatus Status, string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagEntryDto> Entries);

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
/// Part C of #201 (placements and activations) extends the read results with further fields; it adds
/// them, it does not change the meaning of the ones here.
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
