using System.Diagnostics;
using EmotePurge.Core.Services;

namespace EmotePurge.Api.Endpoints;

/// <summary>
/// The one translation of the 7TV ownership ladder's verdict into a response, shared by every route
/// that runs <see cref="IImportTargetOwnershipService.CheckAsync"/> in its handler: the set-centric
/// sync-imported/sync-deleted/sync-restored reports and the tag operation and placement reports.
/// "Exactly the same ladder" holds by construction instead of by copying a <c>switch</c>.
/// </summary>
internal static class EmoteSetOwnershipRejection
{
    /// <summary>
    /// The cap on each owner-hint value, whether it arrives in a report's body or on the editable
    /// pre-check's query (owner-hint design 3.4).
    /// </summary>
    private const int OwnerHintMaxLength = 64;

    /// <summary>
    /// The response that ends the request for <paramref name="status"/>, or <c>null</c> for
    /// <see cref="SevenTvEmoteSetOwnershipStatus.Owner"/> (the caller proceeds).
    /// </summary>
    public static IResult? For(SevenTvEmoteSetOwnershipStatus status) => status switch
    {
        SevenTvEmoteSetOwnershipStatus.Owner => null,
        SevenTvEmoteSetOwnershipStatus.SetNotFound => Results.NotFound(new { errorCode = Validation.ApiErrorCodes.EmoteSetNotFound }),
        // Bare Forbid(), like the IEndpointFilter-based authorization filters (spec 6.7) — no
        // error-code body, since "you may not do this" needs no explanation a caller could act on.
        SevenTvEmoteSetOwnershipStatus.Forbidden => Results.Forbid(),
        // Nothing was determined, let alone written: 7TV could not be asked.
        SevenTvEmoteSetOwnershipStatus.Unavailable => Results.Json(
            new { errorCode = Validation.ApiErrorCodes.ForeignChannelSevenTvUnavailable },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => throw new UnreachableException($"Unexpected {nameof(SevenTvEmoteSetOwnershipStatus)} value: {status}.")
    };

    /// <summary>
    /// Turns an optional owner hint — the reports' <c>targetOwnerTwitchId</c> body field, or the
    /// editable pre-check's <c>ownerTwitchId</c>/<c>ownerLogin</c> query — into an
    /// <see cref="EmoteSetOwnerHint"/>, or drops it. Each value on its own is dropped when blank
    /// (rule 7: never a 400) or longer than <see cref="OwnerHintMaxLength"/>; with both dropped there
    /// is no hint at all. <see cref="IImportTargetOwnershipService"/> only ever resolves a hint
    /// against the actor and the actor's editor grants, so an implausible or foreign value makes
    /// no list request of its own; resolving it may cost the grants lookup (identity + editor_of)
    /// when the grant cache is cold — the cap only keeps a client from handing this class an
    /// arbitrarily large string to hold and log.
    /// </summary>
    public static EmoteSetOwnerHint? BuildOwnerHint(string? twitchUserId, string? twitchLogin = null)
    {
        var usableTwitchUserId = UsableOwnerHintValue(twitchUserId);
        var usableTwitchLogin = UsableOwnerHintValue(twitchLogin);
        return usableTwitchUserId is null && usableTwitchLogin is null
            ? null
            : new EmoteSetOwnerHint(usableTwitchUserId, usableTwitchLogin);
    }

    private static string? UsableOwnerHintValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > OwnerHintMaxLength ? null : value;
}
