namespace EmotePurge.Core.Services;

/// <summary>An active emote, reduced to the fields a cross-channel comparison needs.</summary>
/// <remarks>
/// Deliberately no <c>Emote.Id</c>: that guid is channel-scoped (rule 8) and would be meaningless
/// when this list is compared against emotes from another channel, which is the whole point of an
/// import. <c>ImageUrl</c> is a straight copy of the tracked <c>Emote</c>'s own value — never
/// derived from <c>SevenTvEmoteId</c> (the emote can be static or animated, and 7TV's URL shape
/// differs between the two).
/// </remarks>
public record EmoteListItemDto(string SevenTvEmoteId, string Name, string ImageUrl);

public interface IEmoteListQueryService
{
    /// <summary>Returns <c>null</c> for a channel that is not tracked at all.</summary>
    Task<IReadOnlyList<EmoteListItemDto>?> ListActiveAsync(string channelName, CancellationToken cancellationToken = default);
}
