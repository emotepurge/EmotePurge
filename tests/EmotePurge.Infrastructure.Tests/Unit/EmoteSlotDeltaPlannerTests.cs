using System.Text.Json;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The pure EventAPI delta decisions (plan #374 section 4, test list 7.2): every row
/// of the rule table, the reason codes of 4.3, E3–E8, E10, E12, the Sol counterexamples, the R5 leave
/// exception and order independence (permutations). Each case builds the snapshot the service would read
/// behind its locks.
/// </summary>
public class EmoteSlotDeltaPlannerTests
{
    private const string X = "emote-x";
    private const string Y = "emote-y";
    private const string Z = "emote-z";

    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    // ---- State H (D12, AK 20) ----

    public static TheoryData<string> ChangeKinds => new() { "push", "rename", "pull" };

    [Theory]
    [MemberData(nameof(ChangeKinds))]
    public void SeedOfTheActiveSet_MakesEveryChangeAmbiguousAndObservesNoLeave(string kind)
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Seed("seed1", "A")]);
        var delta = kind switch
        {
            "push" => Delta(pushed: [Push(X, "B")]),
            "rename" => Delta(updated: [Rename(X, "A", "C")]),
            _ => Delta(pulled: [Pull(X, "A")]),
        };

        var plan = Plan(delta, [snapshot], activeSeeds: ["seed1"]);

        Assert.Equal(SlotDeltaAmbiguity.SeedOfActiveSet, Single(plan).AmbiguityReason);
        Assert.True(plan.IsAmbiguous);
        Assert.Empty(plan.LeaveIds);
    }

    [Fact]
    public void ForeignSeed_IsNeitherReadNorChanged()
    {
        // A seed of another set is no H and no own slot: the push lands as a second slot beside it.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Seed("foreign", "B")]);

        var plan = Plan(Delta(pushed: [Push(X, "B")]), [snapshot]);

        var emote = Single(plan);
        Assert.Null(emote.AmbiguityReason);
        Assert.Empty(emote.UpdatedSlots);
        Assert.Equal("B", Assert.Single(emote.CreatedSlots).Name);
    }

    // ---- pushed ----

    [Fact]
    public void Push_WithoutName_IsAmbiguous()
    {
        var plan = Plan(Delta(pushed: [Push(X, string.Empty)]), []);

        Assert.Equal(SlotDeltaAmbiguity.NameMissing, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Push_OfAnUnknownId_CreatesTheEmoteWithOneProvisionalSlot()
    {
        var plan = Plan(Delta(pushed: [Push(X, "A", "img-new")]), []);

        var emote = Single(plan);
        Assert.Equal(SlotDeltaEmoteAction.Create, emote.EmoteAction);
        var slot = Assert.Single(emote.CreatedSlots);
        Assert.Equal(("A", (DateTime?)Now, false, false, (DateTime?)null, false),
            (slot.Name, slot.AddedAt, slot.AddedAtFromRest, slot.IsArchived, slot.ArchivedAt, slot.SeedUnsettled));
        Assert.Equal(new EmoteNameDerivation("A", Now), emote.Derivation);
        Assert.Equal("img-new", emote.ImageUrl);
        Assert.Equal((false, true, false), (emote.WasActive, emote.IsActiveAfter, emote.ObserveLeave));
        Assert.True(plan.HasInventoryChanges);
        Assert.Equal(1, plan.ActiveEmoteCountChange);
    }

    [Fact]
    public void Push_TwoNamesInOneDispatch_DeriveTheUtf8SmallestNameOnTheTie()
    {
        // #403: one dispatch stamps both new slots with the same `now`; D13 breaks the tie in UTF-8 order.
        var emoji = "\U0001F600";
        Assert.Equal(0x1F600, char.ConvertToUtf32(emoji, 0));

        var plan = Plan(Delta(pushed: [Push(X, emoji), Push(X, "\uFF5E")]), []);

        var emote = Single(plan);
        Assert.Equal(2, emote.CreatedSlots.Count);
        Assert.All(emote.CreatedSlots, s => Assert.Equal(Now, s.AddedAt));
        Assert.Equal("\uFF5E", emote.Derivation!.Name);
    }

    [Fact]
    public void Push_OfAnActiveName_IsANoOp()
    {
        // AK 19: our REST pass was faster — nothing at all, not even the image.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))], image: "img-old");

        var plan = Plan(Delta(pushed: [Push(X, "A", "img-new")]), [snapshot]);

        var emote = Single(plan);
        Assert.Null(emote.AmbiguityReason);
        Assert.False(emote.WritesInventory);
        Assert.Null(emote.Derivation);
        Assert.False(plan.HasInventoryChanges);
    }

    [Fact]
    public void Push_OfASecondName_AddsAProvisionalSlotAndKeepsTheOlderName()
    {
        // Integration case (1) in small: push (X, probeB) on probeA → second slot, Emote.Name = probeA.
        var snapshot = Emote(X, "probeA", [Active("s1", "probeA", Day(1))]);

        var plan = Plan(Delta(pushed: [Push(X, "probeB")]), [snapshot]);

        var emote = Single(plan);
        Assert.Equal(SlotDeltaEmoteAction.None, emote.EmoteAction);
        Assert.Equal("probeB", Assert.Single(emote.CreatedSlots).Name);
        Assert.Equal(new EmoteNameDerivation("probeA", Day(1)), emote.Derivation);
        Assert.False(emote.ObserveLeave);
        Assert.True(plan.HasInventoryChanges);
    }

    [Fact]
    public void Push_OrdersBeforeAnUndatedSlotAndTakesOverTheName()
    {
        // Plan 3.3: a push slot (UtcNow) orders before an undated slot (null last).
        var snapshot = Emote(X, "A", [Provisional("s1", "A", null)]);

        var plan = Plan(Delta(pushed: [Push(X, "B")]), [snapshot]);

        Assert.Equal(new EmoteNameDerivation("B", Now), Single(plan).Derivation);
    }

    [Fact]
    public void Push_UnderTheNameOfAResumptionCandidate_IsAmbiguous()
    {
        // AK 19 / D7: the return needs REST's date.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Archived("s2", "B", Day(2), archivedAt: Day(5))]);

        var plan = Plan(Delta(pushed: [Push(X, "B")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.ArchivedNamesake, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Push_UnderTheNameOfAnArchivedSlotWithoutArchiveStamp_CreatesANewSlot()
    {
        // E3: archived without ArchivedAt (backfill "never in", emptied seed) is no D7 candidate.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), new SlotState("s2", "B", null, false, true, null, false)]);

        var plan = Plan(Delta(pushed: [Push(X, "B")]), [snapshot]);

        var emote = Single(plan);
        Assert.Null(emote.AmbiguityReason);
        Assert.Equal("B", Assert.Single(emote.CreatedSlots).Name);
    }

    [Fact]
    public void Push_OfAnActiveAndOfAnArchivedName_IsAmbiguous()
    {
        // The no-op for A does not shortcut the D7 check for B.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Archived("s2", "B", Day(2), archivedAt: Day(5))]);

        var plan = Plan(Delta(pushed: [Push(X, "A"), Push(X, "B")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.ArchivedNamesake, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Push_OntoAnArchivedEmoteUnderANewName_UnarchivesWithANewSlot()
    {
        var snapshot = Emote(X, "A", [Archived("s1", "A", Day(1), archivedAt: Day(5))], archived: true, image: "img-old");

        var plan = Plan(Delta(pushed: [Push(X, "B", "img-new")]), [snapshot]);

        var emote = Single(plan);
        Assert.Equal(SlotDeltaEmoteAction.Unarchive, emote.EmoteAction);
        Assert.Equal("B", Assert.Single(emote.CreatedSlots).Name);
        Assert.Empty(emote.UpdatedSlots);
        Assert.Equal(new EmoteNameDerivation("B", Now), emote.Derivation);
        Assert.Equal("img-new", emote.ImageUrl);
        Assert.Equal((false, true), (emote.WasActive, emote.IsActiveAfter));
    }

    [Fact]
    public void Push_OntoAnArchivedEmoteUnderItsArchivedName_IsAmbiguous()
    {
        // Return after archiving under the old name (D7, AK 47): REST resumes the same slot.
        var snapshot = Emote(X, "A", [Archived("s1", "A", Day(1), archivedAt: Day(5))], archived: true);

        var plan = Plan(Delta(pushed: [Push(X, "A")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.ArchivedNamesake, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Push_OntoAnArchivedEmoteWithActiveSlots_IsAmbiguous()
    {
        // E4: stale state, left from before the slot work.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))], archived: true);

        var plan = Plan(Delta(pushed: [Push(X, "B")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.StaleSlotsUnderArchivedEmote, Single(plan).AmbiguityReason);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void Push_InTheVirtualState_IsAlwaysAmbiguous(string name)
    {
        // E5: also under Emote.Name (a no-op would leave the emote uncounted by the match map).
        var snapshot = Emote(X, "A", []);

        var plan = Plan(Delta(pushed: [Push(X, name)]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.VirtualSlot, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Push_InTheVirtualStateAfterARestoreWithoutEntry_IsAmbiguousWithTheResumptionReason()
    {
        // Sol-P2-2 / plan #376 4.2a: emote active again, its only slot archived with a stamp (a D7
        // candidate). The candidate check runs before anything else.
        var snapshot = Emote(X, "A", [Archived("s1", "A", Day(1), archivedAt: Day(5))]);

        var plan = Plan(Delta(pushed: [Push(X, "A")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.ArchivedNamesake, Single(plan).AmbiguityReason);
        Assert.True(plan.IsAmbiguous);
    }

    [Fact]
    public void Push_DuplicateEntriesAreOne()
    {
        var plan = Plan(Delta(pushed: [Push(X, "A"), Push(X, "A")]), []);

        Assert.Single(Single(plan).CreatedSlots);
    }

    [Fact]
    public void Push_TwoNamesOfAnUnknownId_CreateTwoSlotsAndDeriveTheOrdinallySmallerName()
    {
        var plan = Plan(Delta(pushed: [Push(X, "b"), Push(X, "B")]), []);

        var emote = Single(plan);
        Assert.Equal(["B", "b"], emote.CreatedSlots.Select(s => s.Name));
        Assert.Equal("B", emote.Derivation!.Name);
    }

    // ---- updated ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rename_WithExactlyOneActiveSlot_IsInPlace(bool authoritative)
    {
        // Integration case (6) in small; E10. Same slot id, AddedAt and date origin unchanged.
        var slot = new SlotState("s1", "probeB", Day(1), authoritative, false, null, false);
        var snapshot = Emote(X, "probeB", [slot, Archived("s0", "old", Day(0), archivedAt: Day(1))]);

        var plan = Plan(Delta(updated: [Rename(X, "probeB", "probeC")]), [snapshot]);

        var emote = Single(plan);
        Assert.Null(emote.AmbiguityReason);
        Assert.Equal(slot with { Name = "probeC" }, Assert.Single(emote.UpdatedSlots));
        Assert.Empty(emote.CreatedSlots);
        Assert.Equal(new EmoteNameDerivation("probeC", Day(1)), emote.Derivation);
        Assert.True(plan.HasInventoryChanges);
    }

    [Fact]
    public void Rename_ToTheSameName_ChangesOnlyTheImage()
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))], image: "img-old");

        var changed = Plan(Delta(updated: [Rename(X, "A", "A", "img-new")]), [snapshot]);
        var same = Plan(Delta(updated: [Rename(X, "A", "A", "img-old")]), [snapshot]);

        Assert.Equal("img-new", Single(changed).ImageUrl);
        Assert.Empty(Single(changed).UpdatedSlots);
        Assert.True(changed.HasInventoryChanges);
        Assert.False(same.HasInventoryChanges);
    }

    [Fact]
    public void Rename_WithAnEmptyImage_KeepsTheKnownImage()
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))], image: "img-old");

        var plan = Plan(Delta(updated: [Rename(X, "A", "B", string.Empty)]), [snapshot]);

        Assert.Null(Single(plan).ImageUrl);
    }

    [Theory]
    [InlineData(null, "B")]
    [InlineData("A", null)]
    [InlineData(null, null)]
    [InlineData("", "B")]
    public void Rename_WithoutBothNames_IsAmbiguous(string? oldName, string? newName)
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))]);

        var plan = Plan(Delta(updated: [Rename(X, oldName, newName)]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.NameMissing, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Rename_WhoseSourceIsNotTheActiveSlot_IsAmbiguous()
    {
        // Also the doubled dispatch: the second copy of A → B finds B.
        var snapshot = Emote(X, "B", [Active("s1", "B", Day(1))]);

        var plan = Plan(Delta(updated: [Rename(X, "A", "B")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.RenameSourceMissing, Single(plan).AmbiguityReason);
    }

    public static TheoryData<string> NoActiveSlotStates => new() { "missing", "archived", "virtual" };

    [Theory]
    [MemberData(nameof(NoActiveSlotStates))]
    public void Rename_WithoutAnActiveOwnSlot_IsAmbiguous(string state)
    {
        SlotDeltaEmoteSnapshot[] snapshots = state switch
        {
            "missing" => [],
            "archived" => [Emote(X, "A", [Archived("s1", "A", Day(1), archivedAt: Day(5))], archived: true)],
            _ => [Emote(X, "A", [])],
        };

        var plan = Plan(Delta(updated: [Rename(X, "A", "B")]), snapshots);

        Assert.Equal(SlotDeltaAmbiguity.RenameSourceMissing, Single(plan).AmbiguityReason);
    }

    [Theory]
    [InlineData("probeA", "probeA")] // quirk: value.name = the first slot's name
    [InlineData("probeB", "probeC")] // a free name
    [InlineData("probeA", "probeC")]
    public void Rename_WithTwoActiveSlots_IsAmbiguous(string oldName, string newName)
    {
        // E10 / operator decision R4, integration case (7) in small.
        var snapshot = Emote(X, "probeA", [Active("s1", "probeA", Day(1)), Active("s2", "probeB", Day(2))]);

        var plan = Plan(Delta(updated: [Rename(X, oldName, newName)]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.RenameWithSiblings, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Rename_OfTheRealQuirkFrame_AgainstTwoActiveSlots_IsAmbiguous()
    {
        // The frame captured live (probeB -> probeC reported as o = probeB, n = probeD), parsed for real.
        var body = JsonDocument.Parse(File.ReadAllText(
            Path.Combine("Unit", "TestData", "dispatch-emote-set-update-renamed-with-siblings.json")));
        var delta = SevenTvDispatchParser.ParseEmoteSetUpdate(
            body.RootElement.GetProperty("d").GetProperty("body"), NullLogger.Instance);
        const string id = "01KAMA120V9DC2P1YW9QWFHA16";
        var snapshot = Emote(id, "probeD", [Active("s1", "probeD", Day(1)), Active("s2", "probeB", Day(2))]);

        var plan = Plan(delta, [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.RenameWithSiblings, Single(plan).AmbiguityReason);
        Assert.False(plan.HasInventoryChanges);
    }

    [Fact]
    public void Rename_QuirkAfterAStaleRestRollback_IsAmbiguous()
    {
        // Sol-P1-1: A → C was applied, a stale REST rolled the slots back to A(t1), B(t2); 7TV then renames
        // its second entry B → D and the dispatch carries o = B, n = C. C is no local sibling name — the
        // pre-revision rule would have taken B → C and counted chat "C" at t2.
        var snapshot = Emote(X, "A", [Active("t1", "A", Day(1)), Active("t2", "B", Day(2))]);

        var plan = Plan(Delta(updated: [Rename(X, "B", "C")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.RenameWithSiblings, Single(plan).AmbiguityReason);
        Assert.False(plan.HasInventoryChanges);
    }

    [Fact]
    public void Rename_OntoTheNameOfAnArchivedTieSlot_IsAmbiguous()
    {
        // E12 / Sol-P2-1: active A(t) beside archived B(t), both authoritative; A → B would violate the
        // authoritative unique index.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(3)), Archived("s2", "B", Day(3), archivedAt: Day(4))]);

        var plan = Plan(Delta(updated: [Rename(X, "A", "B")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.AuthoritativeKeyCollision, Single(plan).AmbiguityReason);
    }

    [Fact]
    public void Rename_OfAProvisionalSlot_IsUntouchedByTheIndexCheck()
    {
        // E12: a provisional slot is outside the index.
        var snapshot = Emote(X, "A", [Provisional("s1", "A", Day(3)), Archived("s2", "B", Day(3), archivedAt: Day(4))]);

        var plan = Plan(Delta(updated: [Rename(X, "A", "B")]), [snapshot]);

        Assert.Null(Single(plan).AmbiguityReason);
        Assert.Equal("B", Assert.Single(Single(plan).UpdatedSlots).Name);
    }

    [Fact]
    public void Rename_OntoTheNameOfAnArchivedSlotOfAnotherDate_IsInPlace()
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(3)), Archived("s2", "B", Day(2), archivedAt: Day(4))]);

        var plan = Plan(Delta(updated: [Rename(X, "A", "B")]), [snapshot]);

        Assert.Null(Single(plan).AmbiguityReason);
    }

    // ---- pulled ----

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "A")]
    [InlineData(true, null)]
    [InlineData(true, "A")]
    public void Pull_WithoutRowOrOfAnArchivedEmote_OnlyObservesTheLeave(bool rowExists, string? name)
    {
        // E8 / F5: no row → observed as before; archived → today's "already archived".
        SlotDeltaEmoteSnapshot[] snapshots = rowExists
            ? [Emote(X, "A", [Archived("s1", "A", Day(1), archivedAt: Day(5))], archived: true)]
            : [];

        var plan = Plan(Delta(pulled: [Pull(X, name)]), snapshots);

        var emote = Single(plan);
        Assert.Null(emote.AmbiguityReason);
        Assert.False(emote.WritesInventory);
        Assert.Equal([X], plan.LeaveIds);
        Assert.False(plan.HasInventoryChanges);
        Assert.Equal(0, plan.ActiveEmoteCountChange);
    }

    [Fact]
    public void Pull_OfAnArchivedEmoteWithStaleActiveSlots_OnlyObservesTheLeave()
    {
        // Plan section 5: active slots under an archived emote — pull is only a leave, REST archives them.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))], archived: true);

        var plan = Plan(Delta(pulled: [Pull(X, "A")]), [snapshot]);

        Assert.False(Single(plan).WritesInventory);
        Assert.Equal([X], plan.LeaveIds);
    }

    [Fact]
    public void Pull_WithoutNameOfTheOnlyActiveSlot_ArchivesSlotAndEmoteAndObservesTheLeave()
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))]);

        var plan = Plan(Delta(pulled: [Pull(X, null)]), [snapshot]);

        var emote = Single(plan);
        Assert.Equal(SlotDeltaEmoteAction.Archive, emote.EmoteAction);
        Assert.Equal(Active("s1", "A", Day(1)) with { IsArchived = true, ArchivedAt = Now }, Assert.Single(emote.UpdatedSlots));
        Assert.Null(emote.Derivation);
        Assert.Equal([X], plan.LeaveIds);
        Assert.Equal(-1, plan.ActiveEmoteCountChange);
    }

    [Fact]
    public void Pull_WithoutNameBesideASibling_IsAmbiguous()
    {
        // AK 20, integration case (8) in small.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Active("s2", "B", Day(2))]);

        var plan = Plan(Delta(pulled: [Pull(X, null)]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.NamelessPullWithSiblings, Single(plan).AmbiguityReason);
        Assert.Empty(plan.LeaveIds);
    }

    [Fact]
    public void Pull_OfOneOfTwoSlots_ArchivesOnlyItAndRederivesWithoutLeave()
    {
        // Integration case (4) in small; AK 19.
        var snapshot = Emote(X, "probeA", [Active("s1", "probeA", Day(1)), Active("s2", "probeB", Day(2))]);

        var plan = Plan(Delta(pulled: [Pull(X, "probeA")]), [snapshot]);

        var emote = Single(plan);
        Assert.Equal(SlotDeltaEmoteAction.None, emote.EmoteAction);
        var archived = Assert.Single(emote.UpdatedSlots);
        Assert.Equal(("s1", true, (DateTime?)Now), (archived.Id, archived.IsArchived, archived.ArchivedAt));
        Assert.Equal(new EmoteNameDerivation("probeB", Day(2)), emote.Derivation);
        Assert.Empty(plan.LeaveIds);
        Assert.True(emote.IsActiveAfter);
        Assert.Equal(0, plan.ActiveEmoteCountChange);
    }

    [Fact]
    public void Pull_OfTheLastSlot_ArchivesTheEmoteAndObservesTheLeave()
    {
        // Integration case (5) in small: the remaining slot goes; an archived sibling does not count.
        var snapshot = Emote(X, "probeB", [Archived("s1", "probeA", Day(1), archivedAt: Day(5)), Active("s2", "probeB", Day(2))]);

        var plan = Plan(Delta(pulled: [Pull(X, "probeB")]), [snapshot]);

        Assert.Equal(SlotDeltaEmoteAction.Archive, Single(plan).EmoteAction);
        Assert.Equal([X], plan.LeaveIds);
    }

    [Theory]
    [InlineData("C")]
    [InlineData("old")] // only an archived slot carries it
    public void Pull_OfANameWithoutActiveSlot_IsAmbiguous(string name)
    {
        // Operator decision R1 (F1).
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Archived("s0", "old", Day(0), archivedAt: Day(1))]);

        var plan = Plan(Delta(pulled: [Pull(X, name)]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.PullTargetMissing, Single(plan).AmbiguityReason);
        Assert.Empty(plan.LeaveIds);
    }

    public static TheoryData<string, string> ChangesOnADuplicateActiveName => new()
    {
        { "pull", SlotDeltaAmbiguity.DuplicateActiveName },
        { "pull+missing", SlotDeltaAmbiguity.DuplicateActiveName },
        { "missing+pull", SlotDeltaAmbiguity.DuplicateActiveName },
        { "push", SlotDeltaAmbiguity.DuplicateActiveName },
        { "rename", SlotDeltaAmbiguity.RenameWithSiblings },
        { "nameless-pull", SlotDeltaAmbiguity.NamelessPullWithSiblings },
    };

    [Theory]
    [MemberData(nameof(ChangesOnADuplicateActiveName))]
    public void AChangeMeetingTwoActiveSlotsOfOneName_IsAmbiguous(string change, string reason)
    {
        // Orchestrator 12.10. (from T2): two active own slots named A contradict 7TV's unique aliases per
        // set — REST decides instead of a guess which one left. Rename and nameless pull already meet it
        // through their sibling rules; the code does not depend on the wire order.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Active("s2", "A", Day(2))]);
        var delta = change switch
        {
            "pull" => Delta(pulled: [Pull(X, "A")]),
            "pull+missing" => Delta(pulled: [Pull(X, "A"), Pull(X, "C")]),
            "missing+pull" => Delta(pulled: [Pull(X, "C"), Pull(X, "A")]),
            "push" => Delta(pushed: [Push(X, "A")]),
            "rename" => Delta(updated: [Rename(X, "A", "B")]),
            _ => Delta(pulled: [Pull(X, null)]),
        };

        var plan = Plan(delta, [snapshot]);

        Assert.Equal(reason, Single(plan).AmbiguityReason);
        Assert.Empty(plan.LeaveIds);
        Assert.False(plan.HasInventoryChanges);
    }

    [Fact]
    public void Push_OfAnotherNameBesideADuplicateActiveName_StillAddsASlot()
    {
        // The duplicate rule fires only for the name it concerns.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Active("s2", "A", Day(2))]);

        var plan = Plan(Delta(pushed: [Push(X, "B")]), [snapshot]);

        Assert.Equal("B", Assert.Single(Single(plan).CreatedSlots).Name);
    }

    [Fact]
    public void TwoPullsOfOneId_ArchiveBothSlotsAndObserveOneLeave()
    {
        // 7TV's remove without alias: one pulled item per slot (AK 18, 19); E6 against the snapshot.
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1)), Active("s2", "B", Day(2))]);

        var plan = Plan(Delta(pulled: [Pull(X, "B"), Pull(X, "A")]), [snapshot]);

        var emote = Single(plan);
        Assert.Equal(SlotDeltaEmoteAction.Archive, emote.EmoteAction);
        Assert.Equal(["s1", "s2"], emote.UpdatedSlots.Select(s => s.Id));
        Assert.All(emote.UpdatedSlots, s => Assert.Equal((true, (DateTime?)Now), (s.IsArchived, s.ArchivedAt)));
        Assert.Equal([X], plan.LeaveIds);
    }

    [Fact]
    public void ANamelessAndANamedPullOfTheOnlySlot_ArchiveItOnce()
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))]);

        var plan = Plan(Delta(pulled: [Pull(X, "A"), Pull(X, null)]), [snapshot]);

        Assert.Single(Single(plan).UpdatedSlots);
        Assert.Equal(SlotDeltaEmoteAction.Archive, Single(plan).EmoteAction);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A")]
    public void Pull_InTheVirtualStateOfTheVirtualName_ArchivesTheEmoteWithLeave(string? name)
    {
        // E5: no slot exists, nothing is miscounted.
        var snapshot = Emote(X, "A", [Archived("s1", "old", Day(1), archivedAt: Day(2))]);

        var plan = Plan(Delta(pulled: [Pull(X, name)]), [snapshot]);

        var emote = Single(plan);
        Assert.Equal(SlotDeltaEmoteAction.Archive, emote.EmoteAction);
        Assert.Empty(emote.UpdatedSlots);
        Assert.Null(emote.Derivation);
        Assert.Equal([X], plan.LeaveIds);
        Assert.True(plan.HasInventoryChanges);
    }

    [Fact]
    public void Pull_InTheVirtualStateOfAnotherName_IsAmbiguous()
    {
        var snapshot = Emote(X, "A", [Archived("s1", "old", Day(1), archivedAt: Day(2))]);

        var plan = Plan(Delta(pulled: [Pull(X, "old")]), [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.VirtualSlot, Single(plan).AmbiguityReason);
        Assert.Empty(plan.LeaveIds);
    }

    [Fact]
    public void Pull_ThenPushInTwoDispatches_IsLeaveThenNewSlot()
    {
        // Out of order across two dispatches (plan section 5): the pull finds no row, the push creates it.
        var first = Plan(Delta(pulled: [Pull(X, "A")]), []);
        var second = Plan(Delta(pushed: [Push(X, "A")]), []);

        Assert.Equal([X], first.LeaveIds);
        Assert.Equal(SlotDeltaEmoteAction.Create, Single(second).EmoteAction);
    }

    // ---- E6: one change kind per id ----

    public static TheoryData<string> Mixes => new() { "push+pull", "rename+pull", "push+rename", "rename+rename" };

    [Theory]
    [MemberData(nameof(Mixes))]
    public void TwoChangeKindsOfOneId_AreAmbiguous(string mix)
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))]);
        var delta = mix switch
        {
            "push+pull" => Delta(pushed: [Push(X, "B")], pulled: [Pull(X, "A")]),
            "rename+pull" => Delta(updated: [Rename(X, "A", "B")], pulled: [Pull(X, "A")]),
            "push+rename" => Delta(pushed: [Push(X, "B")], updated: [Rename(X, "A", "C")]),
            _ => Delta(updated: [Rename(X, "A", "B"), Rename(X, "A", "C")]),
        };

        var plan = Plan(delta, [snapshot]);

        Assert.Equal(SlotDeltaAmbiguity.MixedChangeKinds, Single(plan).AmbiguityReason);
        Assert.Empty(plan.LeaveIds);
    }

    [Fact]
    public void DuplicateRenamesAreOne()
    {
        var snapshot = Emote(X, "A", [Active("s1", "A", Day(1))]);

        var plan = Plan(Delta(updated: [Rename(X, "A", "B"), Rename(X, "A", "B")]), [snapshot]);

        Assert.Null(Single(plan).AmbiguityReason);
    }

    [Fact]
    public void ArchivedA_PushBAndPullA_IsTheSameInEveryOrder()
    {
        // Sol-P2-3: push first made the pull a missing target, pull first gave leave + new slot B.
        var snapshot = Emote(X, "A", [Archived("s1", "A", Day(1), archivedAt: Day(5))], archived: true);
        var changes = new Change[] { new PushChange(Push(X, "B")), new PushChange(Push(X, "C")), new PullChange(Pull(X, "A")) };

        var results = Permutations(changes).Select(order => Describe(Plan(Build(order), [snapshot]))).Distinct().ToList();

        var only = Assert.Single(results);
        Assert.Contains(SlotDeltaAmbiguity.MixedChangeKinds, only, StringComparison.Ordinal);
        Assert.Contains("leave=[]", only, StringComparison.Ordinal);
    }

    [Fact]
    public void ADispatchOverSeveralEmotes_IsTheSameInEveryOrder()
    {
        // E6: one plan per id, resolved against the snapshot — no wire order reaches the result.
        var snapshots = new[]
        {
            Emote(X, "A", [Active("x1", "A", Day(1)), Active("x2", "B", Day(2)), Active("x3", "C", Day(3))]),
            Emote(Y, "P", [Active("y1", "P", Day(1)), Archived("y0", "Q", Day(0), archivedAt: Day(1))]),
        };
        var changes = new Change[]
        {
            new PullChange(Pull(X, "C")),
            new PullChange(Pull(X, "A")),
            new PushChange(Push(Y, "R")),
            new PushChange(Push(Y, "P")),
            new PushChange(Push(Z, "N")),
        };

        var results = Permutations(changes).Select(order => Describe(Plan(Build(order), snapshots))).Distinct().ToList();

        var only = Assert.Single(results);
        Assert.DoesNotContain("ambiguous", only, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmbiguousDispatchWithLeaves_IsTheSameInEveryOrder()
    {
        var snapshots = new[]
        {
            Emote(X, "A", [Active("x1", "A", Day(1)), Active("x2", "B", Day(2))]),
            Emote(Y, "P", [Active("y1", "P", Day(1))]),
        };
        var changes = new Change[]
        {
            new RenameChange(Rename(X, "A", "C")),
            new PullChange(Pull(Y, "P")),
            new PullChange(Pull(Z, null)),
        };

        var results = Permutations(changes).Select(order => Describe(Plan(Build(order), snapshots))).Distinct().ToList();

        Assert.Single(results);
    }

    // ---- E7 / operator decision R5: leaves of an ambiguous dispatch ----

    [Fact]
    public void AmbiguousX_AndLastSlotPullOfY_WriteNoInventoryButObserveY()
    {
        // Sol-P1-2: the leave of Y must not be lost inside the credibility window.
        var snapshots = new[]
        {
            Emote(X, "A", [Active("x1", "A", Day(1)), Active("x2", "B", Day(2))]),
            Emote(Y, "P", [Active("y1", "P", Day(1))]),
        };

        var plan = Plan(Delta(updated: [Rename(X, "A", "C")], pulled: [Pull(Y, "P")]), snapshots);

        Assert.True(plan.IsAmbiguous);
        Assert.False(plan.HasInventoryChanges);
        Assert.Equal(X, Assert.Single(plan.Ambiguities).SevenTvEmoteId);
        Assert.Equal([Y], plan.LeaveIds);
    }

    [Fact]
    public void AmbiguousPullOfY_ObservesNoLeaveForY()
    {
        var snapshots = new[] { Emote(Y, "P", [Active("y1", "P", Day(1)), Active("y2", "Q", Day(2))]) };

        var plan = Plan(Delta(pulled: [Pull(Y, null)]), snapshots);

        Assert.True(plan.IsAmbiguous);
        Assert.Empty(plan.LeaveIds);
    }

    [Fact]
    public void AmbiguousX_AndPullOfYThatKeepsASlot_ObservesNoLeave()
    {
        var snapshots = new[]
        {
            Emote(X, "A", []),
            Emote(Y, "P", [Active("y1", "P", Day(1)), Active("y2", "Q", Day(2))]),
        };

        var plan = Plan(Delta(pushed: [Push(X, "A")], pulled: [Pull(Y, "P")]), snapshots);

        Assert.True(plan.IsAmbiguous);
        Assert.Empty(plan.LeaveIds);
    }

    [Fact]
    public void AmbiguousX_AndPullsOfUnknownAndArchivedIds_ObserveThem()
    {
        var snapshots = new[]
        {
            Emote(X, "A", [Active("x1", "A", Day(1)), Active("x2", "B", Day(2))]),
            Emote(Y, "P", [], archived: true),
        };

        var plan = Plan(Delta(pulled: [Pull(X, null), Pull(Y, "P"), Pull(Z, "N")]), snapshots);

        Assert.True(plan.IsAmbiguous);
        Assert.Equal([Y, Z], plan.LeaveIds);
    }

    // ---- Plausibility input and Applied vs NoChange ----

    [Fact]
    public void ActiveEmoteCountChange_SumsTheEmotesThatLeaveAndEnter()
    {
        var snapshots = new[]
        {
            Emote(X, "A", [Active("x1", "A", Day(1))]),
            Emote(Y, "P", [Active("y1", "P", Day(1))]),
            Emote(Z, "N", [Archived("z1", "N", Day(1), archivedAt: Day(2))], archived: true),
        };

        var plan = Plan(Delta(pushed: [Push(Z, "M"), Push("emote-w", "W")], pulled: [Pull(X, "A"), Pull(Y, null)]), snapshots);

        Assert.False(plan.IsAmbiguous);
        Assert.Equal(0, plan.ActiveEmoteCountChange); // −2 + 2
        Assert.Equal([X, Y], plan.LeaveIds);
    }

    [Fact]
    public void ADispatchWithOnlyLeaves_HasNoInventoryChanges()
    {
        var plan = Plan(Delta(pulled: [Pull(X, "A"), Pull(Y, null)]), []);

        Assert.False(plan.HasInventoryChanges);
        Assert.Equal([X, Y], plan.LeaveIds);
    }

    [Fact]
    public void Push_OfAnUnknownIdWithoutImage_CreatesTheRowWithAnEmptyImage()
    {
        var plan = Plan(Delta(pushed: [Push(X, "A", string.Empty)]), []);

        Assert.Equal(string.Empty, Single(plan).ImageUrl);
    }

    [Fact]
    public void Push_UnarchivingWithoutImage_KeepsTheKnownImage()
    {
        var snapshot = Emote(X, "A", [], archived: true, image: "img-old");

        var plan = Plan(Delta(pushed: [Push(X, "B", string.Empty)]), [snapshot]);

        Assert.Null(Single(plan).ImageUrl);
        Assert.Equal(SlotDeltaEmoteAction.Unarchive, Single(plan).EmoteAction);
    }

    [Fact]
    public void TwoSnapshotsOfOneId_AreRejected()
    {
        var snapshot = Emote(X, "A", []);

        Assert.Throws<ArgumentException>(() => Plan(Delta(pushed: [Push(X, "A")]), [snapshot, snapshot]));
    }

    // ---- Helpers ----

    private static DateTime Day(int day) => new DateTime(2026, 8, 1, 9, 30, 15, 123, DateTimeKind.Utc).AddDays(day);

    private static SlotState Active(string id, string name, DateTime addedAt) => new(id, name, addedAt, true, false, null, false);

    private static SlotState Provisional(string id, string name, DateTime? addedAt) => new(id, name, addedAt, false, false, null, false);

    private static SlotState Archived(string id, string name, DateTime addedAt, DateTime archivedAt) =>
        new(id, name, addedAt, true, true, archivedAt, false);

    private static SlotState Seed(string id, string name) => new(id, name, null, false, false, null, true);

    private static SlotDeltaEmoteSnapshot Emote(
        string id, string name, SlotState[] slots, bool archived = false, string image = "img-old") =>
        new(id, name, image, archived, slots);

    private static SevenTvEmote Push(string id, string name, string image = "img-old") => new(id, name, image);

    private static SevenTvEmoteRename Rename(string id, string? oldName, string? newName, string image = "img-old") =>
        new(id, oldName, newName, image);

    private static SevenTvPulledEntry Pull(string id, string? name) => new(id, name);

    private static SevenTvEmoteSetDelta Delta(
        SevenTvEmote[]? pushed = null, SevenTvEmoteRename[]? updated = null, SevenTvPulledEntry[]? pulled = null) =>
        new(pushed ?? [], updated ?? [], pulled ?? []);

    private static SlotDeltaPlan Plan(
        SevenTvEmoteSetDelta delta, SlotDeltaEmoteSnapshot[] snapshots, string[]? activeSeeds = null) =>
        EmoteSlotDeltaPlanner.Plan(delta, snapshots, new HashSet<string>(activeSeeds ?? [], StringComparer.Ordinal), Now);

    private static SlotDeltaEmotePlan Single(SlotDeltaPlan plan) => Assert.Single(plan.Emotes);

    // A wire-order-free rendering of a plan: created slots without their random ids.
    private static string Describe(SlotDeltaPlan plan) =>
        string.Join(" | ", plan.Emotes.Select(e =>
            $"{e.SevenTvEmoteId}: {(e.IsAmbiguous ? "ambiguous " + e.AmbiguityReason : "ok")} {e.EmoteAction} img={e.ImageUrl} "
            + $"upd=[{string.Join(",", e.UpdatedSlots)}] new=[{string.Join(",", e.CreatedSlots.Select(s => s with { Id = "-" }))}] "
            + $"der={e.Derivation} active={e.WasActive}->{e.IsActiveAfter}"))
        + $" || leave=[{string.Join(",", plan.LeaveIds)}] changes={plan.HasInventoryChanges} count={plan.ActiveEmoteCountChange}";

    // One dispatch whose three lists keep the relative order of the permutation.
    private static SevenTvEmoteSetDelta Build(IEnumerable<Change> order)
    {
        var list = order.ToList();
        return new SevenTvEmoteSetDelta(
            list.OfType<PushChange>().Select(c => c.Value).ToList(),
            list.OfType<RenameChange>().Select(c => c.Value).ToList(),
            list.OfType<PullChange>().Select(c => c.Value).ToList());
    }

    private static IEnumerable<List<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items.ToList();
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, j) => j != i).ToList();
            foreach (var tail in Permutations(rest))
            {
                tail.Insert(0, items[i]);
                yield return tail;
            }
        }
    }

    private abstract record Change;

    private sealed record PushChange(SevenTvEmote Value) : Change;

    private sealed record RenameChange(SevenTvEmoteRename Value) : Change;

    private sealed record PullChange(SevenTvPulledEntry Value) : Change;
}
