namespace EmotePurge.Core.Services;

// The DTOs this interface returns now live in DuplicateEmoteNameDto.cs: EmoteSetStatusDto embeds
// the same shape (issue #45), and keeping them in this file would have dragged them into the
// deletion of this interface once the dedicated /duplicate-names route is retired.
public interface IDuplicateEmoteNameQueryService
{
    /// <summary>Returns <c>null</c> for a channel that is not tracked at all.</summary>
    Task<IReadOnlyList<DuplicateEmoteNameDto>?> GetAsync(string channelName, CancellationToken cancellationToken = default);
}
