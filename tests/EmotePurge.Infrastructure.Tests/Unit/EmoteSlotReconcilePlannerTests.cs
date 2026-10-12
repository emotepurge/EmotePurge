using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The pure slot reconcile decisions (plan #372 E10, test list 6.2): identity by date before name, D7
/// resumption, D11 name binding, the per-set settle, D13 derivation incl. a null FirstSeenAt and the E18
/// meaning of "changed". Each case builds the slot snapshot the service would read behind its locks.
/// </summary>
public class EmoteSlotReconcilePlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    // ---- Date identity (step 3, D4, D16, §6.5) ----

    [Fact]
    public void Swap_BindsEachEntryByDateAndRenamesBothSlots()
    {
        // AK 7 / §6.2 example (i): S1(A,t1), S2(B,t2); live [C(t1), A(t2)] — no D7 over the name A.
        var slots = new[] { Active("s1", "A", Day(1)), Active("s2", "B", Day(2)) };

        var plan = Plan([Live("C", Day(1)), Live("A", Day(2))], slots);

        var after = After(slots, plan);
        Assert.Empty(plan.CreatedSlots);
        Assert.Equal(("C", Day(1)), (after["s1"].Name, after["s1"].AddedAt));
        Assert.Equal(("A", Day(2)), (after["s2"].Name, after["s2"].AddedAt));
        Assert.All(after.Values, s => Assert.False(s.IsArchived));
        Assert.True(plan.InventoryChanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleEntryAndSingleSlotOfADate_AreTheSameEntryWhateverTheName(bool slotArchived)
    {
        // §6.5: one live identity binds the one stored slot of that AddedAt generation and takes its name.
        var slot = slotArchived ? Archived("s1", "A", Day(1), archivedAt: Day(20)) : Active("s1", "A", Day(1));

        var plan = Plan([Live("C", Day(1))], [slot]);

        var renamed = Assert.Single(plan.UpdatedSlots);
        Assert.Equal(new SlotState("s1", "C", Day(1), true, false, null, false), renamed);
        Assert.Empty(plan.CreatedSlots);
    }

    [Fact]
    public void DateTie_SameNamesBindTheirOwnSlots()
    {
        // AK 8 tie, first half: live [A(t), B(t)] on slots [A(t), B(t)] → no write at all.
        var slots = new[] { Active("s1", "A", Day(5)), Active("s2", "B", Day(5)) };

        var plan = Plan([Live("A", Day(5)), Live("B", Day(5))], slots);

        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.CreatedSlots);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void DateTie_ARenameInsideTheTieGroupIsRemoveAndAdd()
    {
        // AK 8 tie, second half: live [A(t), C(t)] on slots [A(t), B(t)] → B archived, C new.
        var slots = new[] { Active("s1", "A", Day(5)), Active("s2", "B", Day(5)) };

        var plan = Plan([Live("A", Day(5)), Live("C", Day(5))], slots);

        var after = After(slots, plan);
        Assert.False(after["s1"].IsArchived);
        Assert.Equal((true, (DateTime?)Now), (after["s2"].IsArchived, after["s2"].ArchivedAt));
        var created = Assert.Single(plan.CreatedSlots);
        Assert.Equal(("C", (DateTime?)Day(5), true), (created.Name, created.AddedAt, created.AddedAtFromRest));
    }

    [Fact]
    public void TwoLiveEntriesOnOneStoredDate_DoNotUseTheOneToOneRule()
    {
        // §6.5 Gegenprobe: live [A(t), C(t)] on the single slot A(t) → A binds by name, C is a new slot.
        var slots = new[] { Active("s1", "A", Day(5)) };

        var plan = Plan([Live("A", Day(5)), Live("C", Day(5))], slots);

        Assert.Empty(plan.UpdatedSlots);
        Assert.Equal("C", Assert.Single(plan.CreatedSlots).Name);
    }

    [Fact]
    public void OneLiveEntryOnTwoStoredSlotsOfADate_BindsOnlyByName()
    {
        var slots = new[] { Active("s1", "A", Day(5)), Active("s2", "B", Day(5)) };

        var plan = Plan([Live("C", Day(5))], slots);

        var after = After(slots, plan);
        Assert.True(after["s1"].IsArchived);
        Assert.True(after["s2"].IsArchived);
        Assert.Equal("C", Assert.Single(plan.CreatedSlots).Name);
    }

    [Fact]
    public void SameEntryReturningAfterRestLag_IsUnarchivedNotDuplicated()
    {
        // AK 8 first half: S1(A,t1) archived by a lagging REST answer, live [A(t1)] again.
        var slots = new[] { Archived("s1", "A", Day(1), archivedAt: Day(20)) };

        var plan = Plan([Live("A", Day(1))], slots, wasActive: false);

        Assert.Equal(new SlotState("s1", "A", Day(1), true, false, null, false), Assert.Single(plan.UpdatedSlots));
        Assert.Empty(plan.CreatedSlots);
        Assert.True(plan.InventoryChanged);
    }

    [Fact]
    public void RemoveAndAdd_ArchivesTheOldSlotAndStartsANewOne()
    {
        // AK 7: S1(A,t1); live [B(t2)].
        var slots = new[] { Active("s1", "A", Day(1)) };

        var plan = Plan([Live("B", Day(2))], slots);

        Assert.Equal(new SlotState("s1", "A", Day(1), true, true, Now, false), Assert.Single(plan.UpdatedSlots));
        var created = Assert.Single(plan.CreatedSlots);
        Assert.Equal(new SlotState(created.Id, "B", Day(2), true, false, null, false), created);
        Assert.Equal(new EmoteNameDerivation("B", Day(2)), plan.Derivation);
        Assert.True(plan.InventoryChanged);
    }

    [Fact]
    public void DuplicateNameInTheLiveGroup_FirstEntryCounts()
    {
        var plan = Plan([Live("A", Day(1)), Live("A", Day(2))], []);

        var created = Assert.Single(plan.CreatedSlots);
        Assert.Equal(Day(1), created.AddedAt);
    }

    [Fact]
    public void EmptyLiveGroup_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Plan([], [Active("s1", "A", Day(1))]));
    }

    // ---- Resumption (step 4, D7) ----

    [Fact]
    public void ReturnUnderTheOldName_ResumesTheArchivedSlotOnTheNewDate()
    {
        // AK 7 / AK 47 / §6.2 example (ii): S1(A,t1) archived; live [A(t3)], no active A.
        var slots = new[] { Archived("s1", "A", Day(1), archivedAt: Day(20)) };

        var plan = Plan([Live("A", Day(3))], slots, wasActive: false);

        Assert.Equal(new SlotState("s1", "A", Day(3), true, false, null, false), Assert.Single(plan.UpdatedSlots));
        Assert.Empty(plan.CreatedSlots);
        Assert.Equal(new EmoteNameDerivation("A", Day(3)), plan.Derivation);
    }

    [Theory]
    [InlineData("a-older", 10, "b-newer", 15, "b-newer")] // latest ArchivedAt wins
    [InlineData("b-newer", 15, "a-older", 10, "b-newer")] // independent of input order
    [InlineData("z-tie", 12, "m-tie", 12, "m-tie")] // ArchivedAt tie → Id ordinal ascending
    [InlineData("Z-tie", 12, "a-tie", 12, "Z-tie")] // ordinal, not culture: 'Z' (0x5A) < 'a' (0x61)
    public void ResumptionPicksTheMostRecentlyArchivedSlotThenTheSmallestId(
        string firstId, int firstArchivedDay, string secondId, int secondArchivedDay, string expected)
    {
        var slots = new[]
        {
            Archived(firstId, "A", Day(1), archivedAt: Day(firstArchivedDay)),
            Archived(secondId, "A", Day(2), archivedAt: Day(secondArchivedDay)),
        };

        var plan = Plan([Live("A", Day(25))], slots, wasActive: false);

        var resumed = Assert.Single(plan.UpdatedSlots);
        Assert.Equal(expected, resumed.Id);
        Assert.Equal(Day(25), resumed.AddedAt);
    }

    [Fact]
    public void ActiveSlotOfTheSameName_BlocksResumption()
    {
        // §6.2 example (iii): active A(t2) remains unexplained, A(t3) comes in → new slot at 0, A(t2)
        // archived, the older archived A(t1) untouched.
        var slots = new[] { Archived("s0", "A", Day(1), archivedAt: Day(3)), Active("s1", "A", Day(2)) };

        var plan = Plan([Live("A", Day(4))], slots);

        Assert.Equal(new SlotState("s1", "A", Day(2), true, true, Now, false), Assert.Single(plan.UpdatedSlots));
        Assert.Equal(new SlotState(plan.CreatedSlots[0].Id, "A", Day(4), true, false, null, false), Assert.Single(plan.CreatedSlots));
    }

    [Fact]
    public void ResumptionSeesTheWorkingStateAfterDateMatches()
    {
        // Step 3 renames the only active A (S1, t1) to C; afterwards no active slot carries A, so the
        // dated A(t5) resumes the archived A — judged on the original names it would have been blocked.
        var slots = new[] { Active("s1", "A", Day(1)), Archived("s0", "A", Day(2), archivedAt: Day(3)) };

        var plan = Plan([Live("C", Day(1)), Live("A", Day(5))], slots);

        var after = After(slots, plan);
        Assert.Equal(("C", false), (after["s1"].Name, after["s1"].IsArchived));
        Assert.Equal(new SlotState("s0", "A", Day(5), true, false, null, false), after["s0"]);
        Assert.Empty(plan.CreatedSlots);
    }

    [Fact]
    public void Resumption_SkipsSlotsWithoutArchiveDateAndUnsettledSeeds()
    {
        // E8 + D7 "ArchivedAt != null": a backfill slot ("never in"), an emptied settled seed and a
        // foreign seed archived by M1 all carry the name A — none is a resumption candidate.
        var slots = new[]
        {
            new SlotState("backfill", "A", Day(1), true, true, null, false),
            new SlotState("emptied-seed", "A", null, false, true, null, false),
            Seed("foreign-seed", "A", archived: true, archivedAt: Day(9)),
        };

        var plan = Plan([Live("A", Day(7))], slots, wasActive: false);

        Assert.Empty(plan.UpdatedSlots);
        Assert.Equal(Day(7), Assert.Single(plan.CreatedSlots).AddedAt);
    }

    [Fact]
    public void ReturnDuringAnOverlayOutage_IsANewProvisionalSlotAndTheNextDatedPassAuthorizesIt()
    {
        // Plan §3: an undated entry never resumes; the next dated pass binds the provisional by name.
        var slots = new[] { Archived("s1", "A", Day(1), archivedAt: Day(3)) };

        var undated = Plan([Live("A")], slots, wasActive: false);

        Assert.Empty(undated.UpdatedSlots);
        var provisional = Assert.Single(undated.CreatedSlots);
        Assert.Equal(new SlotState(provisional.Id, "A", null, false, false, null, false), provisional);

        var next = After(slots, undated).Values.ToList();
        var dated = Plan([Live("A", Day(9))], next);

        Assert.Equal(new SlotState(provisional.Id, "A", Day(9), true, false, null, false), Assert.Single(dated.UpdatedSlots));
        Assert.Empty(dated.CreatedSlots);
        Assert.False(dated.InventoryChanged);
    }

    // ---- Name binding (step 5, D11) ----

    [Fact]
    public void OverlayOutageWithoutRename_KeepsSlotsDatesAndFirstSeenAt()
    {
        // Undated entries bind to their authoritative slots by name and never overwrite a date.
        var slots = new[] { Active("s1", "ome44", Day(1)), Active("s2", "hob44", Day(2)) };

        var plan = Plan([Live("ome44"), Live("hob44")], slots, emoteName: "ome44");

        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.CreatedSlots);
        Assert.Equal(new EmoteNameDerivation("ome44", Day(1)), plan.Derivation);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void DatedEntry_AuthorizesTheProvisionalSlotOfItsName()
    {
        // AK 13 first half: push slot C (UtcNow, provisional); live [C(t5)] → authoritative, no change.
        var slots = new[] { Provisional("s1", "C", Now.AddMinutes(-3)) };

        var plan = Plan([Live("C", Day(5))], slots, emoteName: "C");

        Assert.Equal(new SlotState("s1", "C", Day(5), true, false, null, false), Assert.Single(plan.UpdatedSlots));
        Assert.Equal(new EmoteNameDerivation("C", Day(5)), plan.Derivation);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void DatedEntryOfAnotherName_ArchivesTheProvisionalSlot()
    {
        // AK 13 second half: push slot C, live [D(t5)] → D new, C archived.
        var slots = new[] { Provisional("s1", "C", Now.AddMinutes(-3)) };

        var plan = Plan([Live("D", Day(5))], slots, emoteName: "C");

        Assert.True(Assert.Single(plan.UpdatedSlots).IsArchived);
        Assert.Equal("D", Assert.Single(plan.CreatedSlots).Name);
        Assert.True(plan.InventoryChanged);
    }

    [Fact]
    public void UndatedEntry_BindsAProvisionalSlotWithoutTouchingItsDate()
    {
        var pushedAt = Now.AddMinutes(-3);
        var slots = new[] { Provisional("s1", "C", pushedAt) };

        var plan = Plan([Live("C")], slots, emoteName: "C");

        Assert.Empty(plan.UpdatedSlots);
        Assert.Equal(new EmoteNameDerivation("C", pushedAt), plan.Derivation);
    }

    [Fact]
    public void DatedEntry_NeverBindsAnAuthoritativeSlotByName()
    {
        // A(t1) authoritative active and A(t2) live: different identities (D4), so a new slot.
        var slots = new[] { Active("s1", "A", Day(1)) };

        var plan = Plan([Live("A", Day(2))], slots);

        Assert.True(Assert.Single(plan.UpdatedSlots).IsArchived);
        Assert.Single(plan.CreatedSlots);
    }

    // ---- Derivation (step 8, D13 strictly) ----

    [Theory]
    [InlineData(new[] { "B", "A" }, new[] { 2, 1 }, "A", 1)] // smallest AddedAt
    [InlineData(new[] { "A", "B" }, new[] { 0, 3 }, "B", 3)] // null last (0 = undated)
    [InlineData(new[] { "a", "B" }, new[] { 4, 4 }, "B", 4)] // date tie → ordinal name ('B' < 'a')
    [InlineData(new[] { "b", "a" }, new[] { 0, 0 }, "a", 0)] // all null → ordinal name, date null
    public void DerivedNameAndFirstSeenAtFollowTheOldestActiveSlot(string[] names, int[] days, string expectedName, int expectedDay)
    {
        var entries = names.Select((n, i) => Live(n, days[i] == 0 ? null : Day(days[i]))).ToList();

        var plan = Plan(entries, [], wasActive: false);

        Assert.Equal(new EmoteNameDerivation(expectedName, expectedDay == 0 ? null : Day(expectedDay)), plan.Derivation);
    }

    [Fact]
    public void UndatedReplacementOfADatedSlot_DerivesANullFirstSeenAt()
    {
        // Plan 2.3 step 8 (replaces E9): a replacement never inherits the departed slot's date.
        var slots = new[] { Active("s1", "A", Day(1)) };

        var plan = Plan([Live("B")], slots, emoteName: "A");

        Assert.Equal(new SlotState("s1", "A", Day(1), true, true, Now, false), Assert.Single(plan.UpdatedSlots));
        Assert.Equal(new SlotState(plan.CreatedSlots[0].Id, "B", null, false, false, null, false), Assert.Single(plan.CreatedSlots));
        Assert.Equal(new EmoteNameDerivation("B", null), plan.Derivation);
        Assert.True(plan.InventoryChanged);
    }

    [Fact]
    public void DerivedNameIgnoresArchivedSlotsAndUnsettledSeeds()
    {
        var slots = new[]
        {
            Archived("old", "aaa", Day(1), archivedAt: Day(2)),
            Seed("foreign", "aaa"),
            Active("s1", "zzz", Day(5)),
        };

        var plan = Plan([Live("zzz", Day(5))], slots, emoteName: "zzz");

        Assert.Equal(new EmoteNameDerivation("zzz", Day(5)), plan.Derivation);
        Assert.Empty(plan.UpdatedSlots);
    }

    // ---- Settle (step 2, §6.3, D12, plan 2.4) ----

    [Fact]
    public void Settle_MultiEntryAllDated_RekeysTheSeedOntoTheOldestEntry()
    {
        // AK 5: seed with A's rows; live [ome44 (07-01), hob44 (09-01)].
        var ome = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var hob = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var slots = new[] { Seed("seed", "hob44") };

        var plan = Plan([Live("ome44", ome), Live("hob44", hob)], slots, activeSeeds: ["seed"], emoteName: "hob44");

        Assert.False(plan.SettleDeferred);
        Assert.Equal(new SlotState("seed", "ome44", ome, true, false, null, false), Assert.Single(plan.UpdatedSlots));
        Assert.Equal(new SlotState(plan.CreatedSlots[0].Id, "hob44", hob, true, false, null, false), Assert.Single(plan.CreatedSlots));
        Assert.Empty(plan.UsageRebinds);
        Assert.Equal(new EmoteNameDerivation("ome44", ome), plan.Derivation);
        Assert.True(plan.InventoryChanged);

        // HasChanges only on the first pass.
        var second = Plan([Live("ome44", ome), Live("hob44", hob)], After(slots, plan).Values.ToList(), emoteName: "ome44");
        Assert.Empty(second.UpdatedSlots);
        Assert.Empty(second.CreatedSlots);
        Assert.False(second.InventoryChanged);
    }

    [Fact]
    public void Settle_OldestTie_PicksTheOrdinallySmallestName()
    {
        var slots = new[] { Seed("seed", "x") };

        var plan = Plan([Live("b", Day(3)), Live("Z", Day(3))], slots, activeSeeds: ["seed"]);

        Assert.Equal("Z", plan.UpdatedSlots.Single(s => s.Id == "seed").Name);
        Assert.Equal("b", Assert.Single(plan.CreatedSlots).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Settle_SingleEntry_TakesNameAndDateOrStaysProvisional(bool dated)
    {
        DateTime? at = dated ? Day(6) : null;
        var slots = new[] { Seed("seed", "old", archived: true, archivedAt: Day(2)) };

        var plan = Plan([Live("new", at)], slots, activeSeeds: ["seed"], wasActive: false);

        // An M1 copy of an archived emote's state is irrelevant: the settled seed is active, no archive date.
        Assert.Equal(new SlotState("seed", "new", at, dated, false, null, false), Assert.Single(plan.UpdatedSlots));
        Assert.Empty(plan.CreatedSlots);
        Assert.Empty(plan.UsageRebinds);
        Assert.Equal(new EmoteNameDerivation("new", at), plan.Derivation);
    }

    [Theory]
    [InlineData(false, false)] // all undated
    [InlineData(true, false)] // hob44 dated, ome44 undated (AK 6 second variant)
    public void Settle_MultiEntryWithAnUndatedEntry_IsDeferredWithoutAnyWrite(bool firstDated, bool secondDated)
    {
        var slots = new[] { Seed("seed", "hob44"), Active("s1", "other", Day(1)) };

        var plan = Plan(
            [Live("hob44", firstDated ? Day(9) : null), Live("ome44", secondDated ? Day(7) : null)],
            slots,
            activeSeeds: ["seed"]);

        Assert.True(plan.SettleDeferred);
        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.CreatedSlots);
        Assert.Empty(plan.UsageRebinds);
        Assert.Null(plan.Derivation);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void MultiEntryWithAnUndatedEntryButNoSeedOfTheActiveSet_IsPlainNameBinding()
    {
        // D11 (e): only the settle waits for dates; a foreign seed does not make the group wait.
        var slots = new[] { Seed("foreign", "hob44") };

        var plan = Plan([Live("hob44"), Live("ome44", Day(7))], slots);

        Assert.False(plan.SettleDeferred);
        Assert.Equal(2, plan.CreatedSlots.Count);
        Assert.Empty(plan.UpdatedSlots);
    }

    [Fact]
    public void Settle_SeedNextToARestSlot_MovesTheRowsAndArchivesTheEmptySeedWithoutDate()
    {
        // #371 edge case / E11 + E12: the REST slot R(A,t) exists, the seed of the active set lies next to it.
        var slots = new[] { Active("rest", "A", Day(4)), Seed("seed", "A") };

        var plan = Plan([Live("A", Day(4))], slots, activeSeeds: ["seed"], emoteName: "A");

        Assert.Equal([new SlotUsageRebind("seed", "rest")], plan.UsageRebinds);
        Assert.Equal(new SlotState("seed", "A", null, false, true, null, false), Assert.Single(plan.UpdatedSlots));
        Assert.Empty(plan.CreatedSlots);
        Assert.Equal(new EmoteNameDerivation("A", Day(4)), plan.Derivation);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void Settle_TargetFoundByTheOneToOneDateRule_RebindsOntoTheRenamedSlot()
    {
        var slots = new[] { Archived("s1", "A", Day(4), archivedAt: Day(8)), Seed("seed", "A") };

        var plan = Plan([Live("C", Day(4))], slots, activeSeeds: ["seed"], wasActive: false);

        var after = After(slots, plan);
        Assert.Equal([new SlotUsageRebind("seed", "s1")], plan.UsageRebinds);
        Assert.Equal(new SlotState("s1", "C", Day(4), true, false, null, false), after["s1"]);
        Assert.Equal(new SlotState("seed", "A", null, false, true, null, false), after["seed"]);
    }

    [Theory]
    [InlineData(true)] // dated candidate authorizes the provisional target
    [InlineData(false)] // undated candidate keeps the target's date
    public void Settle_TargetFoundByNameBinding_RebindsOntoTheProvisionalSlot(bool dated)
    {
        var pushedAt = Now.AddMinutes(-5);
        var slots = new[] { Provisional("push", "A", pushedAt), Seed("seed", "A") };

        var plan = Plan([Live("A", dated ? Day(6) : null)], slots, activeSeeds: ["seed"], emoteName: "A");

        var after = After(slots, plan);
        Assert.Equal([new SlotUsageRebind("seed", "push")], plan.UsageRebinds);
        Assert.Equal(dated ? new SlotState("push", "A", Day(6), true, false, null, false) : slots[0], after["push"]);
        Assert.True(after["seed"].IsArchived);
        Assert.False(after["seed"].SeedUnsettled);
        Assert.Empty(plan.CreatedSlots);
    }

    [Fact]
    public void Settle_UndatedCandidate_UsesAnAuthoritativeSlotOfItsNameAsTarget()
    {
        var slots = new[] { Active("rest", "A", Day(4)), Seed("seed", "A") };

        var plan = Plan([Live("A")], slots, activeSeeds: ["seed"], emoteName: "A");

        Assert.Equal([new SlotUsageRebind("seed", "rest")], plan.UsageRebinds);
        Assert.Equal(new EmoteNameDerivation("A", Day(4)), plan.Derivation);
    }

    [Fact]
    public void Settle_TwoSeedsOfTheActiveSet_TheSecondFindsTheFirstAsItsTarget()
    {
        var slots = new[] { Seed("seed-b", "A"), Seed("seed-a", "A") };

        var plan = Plan([Live("A", Day(3))], slots, activeSeeds: ["seed-a", "seed-b"], emoteName: "A");

        var after = After(slots, plan);
        Assert.Equal(new SlotState("seed-a", "A", Day(3), true, false, null, false), after["seed-a"]);
        Assert.Equal(new SlotState("seed-b", "A", null, false, true, null, false), after["seed-b"]);
        Assert.Equal([new SlotUsageRebind("seed-b", "seed-a")], plan.UsageRebinds);
    }

    [Fact]
    public void Settle_TargetSearchDoesNotUseResumption()
    {
        // Plan E11 names steps 3 and 5 only: the archived A(t0) stays archived, the seed (holding the
        // active set's rows) becomes A(t5). Conservative: no history is merged across slots.
        var slots = new[] { Archived("old", "A", Day(1), archivedAt: Day(2)), Seed("seed", "A") };

        var plan = Plan([Live("A", Day(5))], slots, activeSeeds: ["seed"], wasActive: false);

        var after = After(slots, plan);
        Assert.Equal(slots[0], after["old"]);
        Assert.Equal(new SlotState("seed", "A", Day(5), true, false, null, false), after["seed"]);
        Assert.Empty(plan.UsageRebinds);
    }

    [Fact]
    public void Settle_RestOfTheGroupStillUsesDateIdentityAndResumption()
    {
        // E13: after the seed takes the oldest entry, the others go through steps 3–6 — B binds its own
        // slot by date, C resumes its archived slot.
        var slots = new[]
        {
            Seed("seed", "A"),
            Active("b", "B", Day(5)),
            Archived("c", "C", Day(2), archivedAt: Day(3)),
        };

        var plan = Plan([Live("A", Day(1)), Live("B", Day(5)), Live("C", Day(6))], slots, activeSeeds: ["seed"]);

        var after = After(slots, plan);
        Assert.Equal(new SlotState("seed", "A", Day(1), true, false, null, false), after["seed"]);
        Assert.Equal(slots[1], after["b"]);
        Assert.Equal(new SlotState("c", "C", Day(6), true, false, null, false), after["c"]);
        Assert.Empty(plan.CreatedSlots);
    }

    [Fact]
    public void ForeignSeed_IsNeverTouchedNotArchivedAndNotCountedAsVisible()
    {
        // #371 handover: an active emote with usage only in set B has an ACTIVE seed for B after M1.
        // Step 7 must not stamp it, and it does not replace the virtual slot.
        var slots = new[] { Seed("foreign", "A") };

        var plan = Plan([Live("A", Day(4))], slots, emoteName: "A");

        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.UsageRebinds);
        Assert.Equal(new SlotState(plan.CreatedSlots[0].Id, "A", Day(4), true, false, null, false), Assert.Single(plan.CreatedSlots));
        Assert.False(plan.InventoryChanged); // the new slot replaces the virtual slot A
    }

    [Fact]
    public void SwitchToSetB_SettlesBsSeedAndArchivesTheSlotsOfA()
    {
        // AK 4 half: A's slot has no date match in B's entries; B's seed becomes the slot.
        var slots = new[] { Active("a-slot", "X", Day(1)), Seed("seed-b", "X") };

        var plan = Plan([Live("X", Day(8))], slots, activeSeeds: ["seed-b"], emoteName: "X");

        var after = After(slots, plan);
        Assert.Equal(new SlotState("a-slot", "X", Day(1), true, true, Now, false), after["a-slot"]);
        Assert.Equal(new SlotState("seed-b", "X", Day(8), true, false, null, false), after["seed-b"]);
        Assert.Empty(plan.UsageRebinds);
    }

    // ---- Emote without live entry ----

    [Fact]
    public void AbsentEmote_ArchivesActiveOwnSlotsAndLeavesEverySeedAlone()
    {
        var slots = new[]
        {
            Active("s1", "A", Day(1)),
            Provisional("s2", "B", null),
            Archived("s3", "C", Day(1), archivedAt: Day(2)),
            new SlotState("emptied", "A", null, false, true, null, false),
            Seed("active-set-seed", "A"),
            Seed("foreign-seed", "A"),
        };

        var plan = SlotPlanAbsent(slots);

        Assert.Equal(
            [new SlotState("s1", "A", Day(1), true, true, Now, false), new SlotState("s2", "B", null, false, true, Now, false)],
            plan.UpdatedSlots);
        Assert.Empty(plan.CreatedSlots);
        Assert.Empty(plan.UsageRebinds);
        Assert.Null(plan.Derivation);
        Assert.False(plan.InventoryChanged);
        Assert.False(plan.SettleDeferred);
    }

    // ---- changed (E18) ----

    [Theory]
    [InlineData("A", false)] // the first own slot exactly replaces the virtual slot
    [InlineData("B", true)] // other name: visible rename of the virtual slot (and of the derived name)
    public void FirstOwnSlotOfAnActiveEmote_ReplacesTheVirtualSlot(string emoteName, bool expected)
    {
        var plan = Plan([Live("A", Day(1))], [], emoteName: emoteName);

        Assert.Single(plan.CreatedSlots);
        Assert.Equal(expected, plan.InventoryChanged);
    }

    [Fact]
    public void FirstOwnSlotsOfAnActiveEmote_ASecondSlotIsAVisibleChange()
    {
        var plan = Plan([Live("A", Day(1)), Live("B", Day(2))], [], emoteName: "A");

        Assert.Equal(2, plan.CreatedSlots.Count);
        Assert.True(plan.InventoryChanged);
    }

    [Fact]
    public void UnarchivedSlotReplacingTheVirtualSlot_IsNoChange()
    {
        // On → off → on: the emote came back while the flag was off (no slot written), its own slot is
        // still archived; the first pass with the flag on brings exactly the virtual slot back.
        var slots = new[] { Archived("s1", "A", Day(1), archivedAt: Day(2)) };

        var plan = Plan([Live("A", Day(1))], slots, emoteName: "A");

        Assert.Single(plan.UpdatedSlots);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void NewEmote_HasNoVirtualSlotToReplace()
    {
        var plan = Plan([Live("A", Day(1))], [], emoteName: "A", wasActive: false);

        Assert.True(plan.InventoryChanged);
    }

    [Fact]
    public void SettledSeedReplacingTheVirtualSlot_IsNoChange()
    {
        var plan = Plan([Live("A", Day(1))], [Seed("seed", "A")], activeSeeds: ["seed"], emoteName: "A");

        Assert.Single(plan.UpdatedSlots);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void ArchivingASlotUnderAnEmoteThatWasNotActive_IsNoChange()
    {
        // Delta pull archived the emote, its slots stayed active; the emote returns under a slot that
        // still is active, the other one is archived invisibly. (The service counts the un-archive.)
        var slots = new[] { Active("s1", "A", Day(1)), Active("s2", "B", Day(2)) };

        var plan = Plan([Live("A", Day(1))], slots, emoteName: "A", wasActive: false);

        Assert.True(Assert.Single(plan.UpdatedSlots).IsArchived);
        Assert.False(plan.InventoryChanged);
    }

    [Fact]
    public void ArchivingASlotUnderAnActiveEmote_IsAChange()
    {
        // AK 9 first half: [A, B] active, live [A] → B archived, the emote stays active.
        var slots = new[] { Active("s1", "A", Day(1)), Active("s2", "B", Day(2)) };

        var plan = Plan([Live("A", Day(1))], slots, emoteName: "A");

        Assert.Equal("s2", Assert.Single(plan.UpdatedSlots).Id);
        Assert.True(plan.InventoryChanged);
    }

    [Fact]
    public void DerivedNameChange_IsAChange()
    {
        // No slot event at all: the stored emote name is stale (K1 kept the last alias).
        var slots = new[] { Active("s1", "ome44", Day(1)), Active("s2", "hob44", Day(2)) };

        var plan = Plan([Live("ome44", Day(1)), Live("hob44", Day(2))], slots, emoteName: "hob44");

        Assert.Empty(plan.UpdatedSlots);
        Assert.Equal("ome44", plan.Derivation!.Name);
        Assert.True(plan.InventoryChanged);
    }

    // ---- SlotState ↔ entity ----

    [Fact]
    public void SlotState_RoundTripsThroughTheEntityAndStampsLastSyncedAt()
    {
        var entity = new EmoteSlot { Id = "s1", EmoteId = "e1", Name = "A", LastSyncedAt = Day(1), SeedUnsettled = true };
        var planned = SlotState.From(entity) with { Name = "B", AddedAt = Day(2), AddedAtFromRest = true, SeedUnsettled = false };

        planned.ApplyTo(entity, Now);

        Assert.Equal(planned, SlotState.From(entity));
        Assert.Equal(Now, entity.LastSyncedAt);
        Assert.Throws<InvalidOperationException>(() => planned.ApplyTo(new EmoteSlot { Id = "other" }, Now));

        var created = new SlotState("n1", "C", null, false, false, null, false).ToNewEntity("e1", Now);
        Assert.Equal(("n1", "e1", "C", Now), (created.Id, created.EmoteId, created.Name, created.LastSyncedAt));
    }

    // ---- helpers ----

    private static DateTime Day(int day) => new DateTime(2026, 8, 1, 9, 30, 15, 123, DateTimeKind.Utc).AddDays(day);

    // ---- Derive (D13), reachable on its own since plan #376 so report code shares the rule ----

    [Fact]
    public void Derive_PicksSmallestDateThenNameThenIdAndIgnoresArchived()
    {
        var slots = new[]
        {
            new SlotState("s0", "Archived", Day(1), true, true, Now, false),
            new SlotState("s2", "Zed", Day(3), true, false, null, false),
            new SlotState("s3", "Alpha", Day(3), true, false, null, false),
            new SlotState("s1", "Late", null, false, false, null, false)
        };

        var derived = EmoteSlotReconcilePlanner.Derive(slots);

        Assert.Equal(new EmoteNameDerivation("Alpha", Day(3)), derived);
    }

    [Fact]
    public void Derive_UndatedSlotComesLastButYieldsNullDateWhenAlone()
    {
        var derived = EmoteSlotReconcilePlanner.Derive([new SlotState("s1", "Only", null, false, false, null, false)]);

        Assert.Equal(new EmoteNameDerivation("Only", null), derived);
    }

    [Fact]
    public void Derive_WithoutActiveSlot_ReturnsNull()
    {
        Assert.Null(EmoteSlotReconcilePlanner.Derive([new SlotState("s1", "Gone", Day(1), true, true, Now, false)]));
        Assert.Null(EmoteSlotReconcilePlanner.Derive([]));
    }

    private static SlotLiveEntry Live(string name, DateTime? addedAt = null) => new(name, addedAt);

    private static SlotState Active(string id, string name, DateTime addedAt) => new(id, name, addedAt, true, false, null, false);

    private static SlotState Provisional(string id, string name, DateTime? addedAt) => new(id, name, addedAt, false, false, null, false);

    private static SlotState Archived(string id, string name, DateTime addedAt, DateTime archivedAt) =>
        new(id, name, addedAt, true, true, archivedAt, false);

    private static SlotState Seed(string id, string name, bool archived = false, DateTime? archivedAt = null) =>
        new(id, name, null, false, archived, archivedAt, true);

    private static SlotReconcilePlan Plan(
        IReadOnlyList<SlotLiveEntry> live,
        IReadOnlyList<SlotState> slots,
        string[]? activeSeeds = null,
        string? emoteName = null,
        bool wasActive = true) =>
        EmoteSlotReconcilePlanner.PlanLiveGroup(
            live,
            slots,
            new HashSet<string>(activeSeeds ?? [], StringComparer.Ordinal),
            emoteName ?? (live.Count > 0 ? live[0].Name : string.Empty),
            wasActive,
            Now);

    private static SlotReconcilePlan SlotPlanAbsent(IReadOnlyList<SlotState> slots) =>
        EmoteSlotReconcilePlanner.PlanAbsentEmote(slots, Now);

    // The slot table after the service applied the plan: updates replace, created slots are added.
    private static Dictionary<string, SlotState> After(IEnumerable<SlotState> before, SlotReconcilePlan plan)
    {
        var after = before.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var slot in plan.UpdatedSlots.Concat(plan.CreatedSlots))
        {
            after[slot.Id] = slot;
        }

        return after;
    }
}
