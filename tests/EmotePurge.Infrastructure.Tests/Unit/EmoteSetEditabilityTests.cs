using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Pins <see cref="EmoteSetEditability"/> — the one rule <c>GET /api/seventv/me/emote-set-targets</c>
/// (spec 2026-09-24 restore-per-set addendum, 5.8) and <c>ImportTargetOwnershipService</c>'s ownership
/// check (spec 2026-09-20, section 32) both call to decide whether a set may be written to. AK 29/30.
/// </summary>
public class EmoteSetEditabilityTests
{
    private const string ActorSevenTvId = "01ACTOR";
    private const string EditedSevenTvId = "01EDITED";
    private const string StrangerSevenTvId = "01STRANGER";

    // The four contract cases from the task brief's Grenzfälle.

    [Fact]
    public void SetWithoutAnOwnerId_IsNeverEditable_EvenWhenAccountsAreReadable()
    {
        // F16: no owner id at all means no lookup is ever performed to answer this question — the
        // caller is deliberately stricter here than a live 7TV lookup might be.
        Assert.False(EmoteSetEditability.IsEditable(null, [ActorSevenTvId]));
    }

    [Fact]
    public void OwnerIsADifferentAccountOfTheSameResponseWithAReadableList_IsEditable()
    {
        // The set is listed under account A (not part of this call at all) but owned by account B,
        // whose own list was read successfully in the same response.
        Assert.True(EmoteSetEditability.IsEditable(EditedSevenTvId, [ActorSevenTvId, EditedSevenTvId]));
    }

    [Fact]
    public void OwnerIsAnAccountOfTheResponseWhoseListWasNotReadable_IsNotEditable()
    {
        // The owner account is part of this response, but its own list lookup failed
        // (setsUnavailable) — it never contributes its id to the readable set, so it cannot match.
        Assert.False(EmoteSetEditability.IsEditable(EditedSevenTvId, [ActorSevenTvId]));
    }

    [Fact]
    public void NoAccountOfTheResponseHasAReadableList_IsNotEditable()
    {
        // E.g. an account with NoSevenTvAccount contributes no id at all — there is nothing to match
        // an owner id against.
        Assert.False(EmoteSetEditability.IsEditable(EditedSevenTvId, []));
    }

    // AK 30: every ImportTargetOwnershipServiceTests constellation that needs no live owner lookup
    // reaches the same true/false answer here, from the very same list data
    // (ImportTargetOwnershipServiceTests.cs). The one constellation that needs a live lookup in the
    // service — no list carries the set at all — never reaches this function with an owner id, so it
    // answers false regardless of what the lookup would have found (F16).

    [Theory]
    [InlineData(ActorSevenTvId, new[] { ActorSevenTvId }, true)] // ASetOwnedByTheActor_InTheActorsOwnList_IsAdmissible
    // ASetOwnedByAnEditorOfAccount_InThatAccountsList_IsAdmissible_UnderTheGrantsLogin and
    // ASetListedUnderOneCheckedAccount_ButOwnedByALaterOne_IsAdmissible both reduce to the same
    // owner-id/readable-ids pair here — the pure function does not distinguish which list carried the
    // set, only who owns it and who is readable.
    [InlineData(EditedSevenTvId, new[] { ActorSevenTvId, EditedSevenTvId }, true)]
    [InlineData(StrangerSevenTvId, new[] { ActorSevenTvId }, false)] // ASetListedUnderAForeignOwner_IsForbidden_WithoutAnyLookup
    [InlineData(EditedSevenTvId, new[] { EditedSevenTvId }, true)] // APartialOutage_WithTheSetFoundAdmissibleElsewhere_IsStillAdmissible
    [InlineData(null, new[] { ActorSevenTvId }, false)] // ASetInNoList_OwnedByACheckedAccount_IsAdmissible (live lookup only, F16)
    public void MatchesTheOwnershipServicesAnswer_ForEachListOnlyConstellation(
        string? ownerSevenTvUserId, string[] readableAccountSevenTvUserIds, bool expectedEditable)
    {
        Assert.Equal(expectedEditable, EmoteSetEditability.IsEditable(ownerSevenTvUserId, readableAccountSevenTvUserIds));
    }
}
