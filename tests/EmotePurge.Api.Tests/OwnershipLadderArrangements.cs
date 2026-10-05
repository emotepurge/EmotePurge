using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using NSubstitute;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Arrangements for the 7TV ownership ladder that runs for real in every route test whose handler
/// calls <c>IImportTargetOwnershipService</c> (the set-centric reports and the tag reports): the
/// check is not substituted, so each case stages the set lists, the editor grants and the 7TV owner
/// lookup underneath it.
/// </summary>
internal static class OwnershipLadderArrangements
{
    /// <summary>A set list of one 7TV account with the given sets, each owned by the stated 7TV user.</summary>
    public static EmoteSetListResult SetList(string accountSevenTvUserId, params (string Id, string OwnerSevenTvUserId)[] sets) =>
        EmoteSetListResult.Ok(new EmoteSetList(
            null,
            [.. sets.Select(set => new EmoteSetSummary(set.Id, "Some Set", 1000, "NORMAL", false, "Some Owner", set.OwnerSevenTvUserId))],
            accountSevenTvUserId));

    /// <summary>The actor's own list is <paramref name="actorList"/>, and the actor holds no editor grant at all.</summary>
    public static void ArrangeActorWithoutGrants(this ApiFactory factory, string userId, EmoteSetListResult actorList)
    {
        factory.EmoteSetList.ListByTwitchIdAsync(userId, Arg.Any<CancellationToken>()).Returns(actorList);
        factory.GuardedEditorGrants.GetEditorGrantsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(SevenTvEditorGrantsLookupResult.Ok(new SevenTvEditorGrants(new HashSet<string>(), new HashSet<string>())));
    }
}
