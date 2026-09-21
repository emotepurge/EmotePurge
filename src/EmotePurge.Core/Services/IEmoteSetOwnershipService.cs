namespace EmotePurge.Core.Services;

public record EmoteSetWarningDto(
    bool Available,
    bool IsOwnSet,
    IReadOnlyList<string> OtherTrackedChannelsSharingSet,
    IReadOnlyList<string> OtherModeratedChannelsSharingSet);

public interface IEmoteSetOwnershipService
{
    // caller is null for anonymous requests — the moderated-channels tier is then skipped.
    // emoteSetId (spec 2026-09-20, E9): the set to check ownership/sharing for, defaulting to the
    // channel's own ActiveEmoteSetId when null — the picker's ability to preview a target other than
    // the channel's current set means "which set" can no longer be implicit. All three tiers were
    // already set-agnostic internally; this parameter is what lets a caller actually choose.
    Task<EmoteSetWarningDto> CheckAsync(
        string channelName,
        TwitchPrincipalInfo? caller,
        string? emoteSetId = null,
        CancellationToken cancellationToken = default);
}
