using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// The pure backfill binding plan per preview kind (plan #375 2.2–2.4, T4): the active preview runs the
/// REST steps 3–5 without touching membership (E2), the preview of another set binds by exact date only
/// and settles its seed with the date-only target selection (Sol-P2-2), and the 409 reasons. Each case
/// builds the slot snapshot the enqueue would read behind its locks. Settle ties use ASCII names only
/// (plan 12 A); the UTF-8 tie order is pinned by the REST settle tests of #403.
/// </summary>
public class EmoteSlotReconcilePlannerBackfillTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    // ---- Active preview (2.2) ----

    [Fact]
    public void Active_NoSlots_EveryDatedEntryIsANewSlot()
    {
        // AK 22: one emote, two aliases, no slot yet.
        var plan = Active([Live("ome44", Day(1)), Live("hob44", Day(30))], []);

        Assert.Null(plan.Rejection);
        Assert.Equal([new BackfillEntryBinding("ome44", Day(1), null), new BackfillEntryBinding("hob44", Day(30), null)], plan.Bindings);
        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.UsageRebinds);
    }

    [Fact]
    public void Active_BindsAuthoritativeSlotsByDate_ActiveOrArchived_WithoutChangingTheirArchiveState()
    {
        // AK 22 second half: a backfill slot "never in" (archived, no date) and an active REST slot.
        var slots = new[] { NeverIn("never", "ome44", Day(1)), Authoritative("rest", "hob44", Day(30)) };

        var plan = Active([Live("ome44", Day(1)), Live("hob44", Day(30))], slots);

        Assert.Equal(["never", "rest"], plan.Bindings.Select(b => b.SlotId));
        Assert.Empty(plan.UpdatedSlots);
    }

    [Fact]
    public void Active_ARenameBindsBySingleDateAndRenamesTheArchivedSlot_WhichStaysArchived()
    {
        // AK 27: entry A(t) was renamed to C(t) in 7TV; the slot keeps its id, takes the new name, and REST
        // un-archives it on its next tick, not the enqueue (E2).
        var slots = new[] { Archived("s1", "A", Day(4), archivedAt: Day(8)) };

        var plan = Active([Live("C", Day(4))], slots);

        Assert.Equal("s1", Assert.Single(plan.Bindings).SlotId);
        Assert.Equal(new SlotState("s1", "C", Day(4), true, true, Day(8), false), Assert.Single(plan.UpdatedSlots));
    }

    [Fact]
    public void Active_UnsettledSeedOfTheActiveSet_IsRejected()
    {
        var slots = new[] { Seed("seed", "A"), Authoritative("rest", "A", Day(4)) };

        var plan = Active([Live("A", Day(4))], slots, activeSeeds: ["seed"]);

        Assert.Equal(BackfillSlotBindingRejection.ActiveSetSeedUnsettled, plan.Rejection);
        Assert.Empty(plan.Bindings);
        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.UsageRebinds);
    }

    [Fact]
    public void Active_SeedOfAnotherSet_IsNeitherARejectionNorTouched()
    {
        // AK 22: an unsettled seed of set B keeps its rows and its marker.
        var slots = new[] { Seed("seed-b", "A") };

        var plan = Active([Live("A", Day(4))], slots);

        Assert.Null(plan.Rejection);
        Assert.Null(Assert.Single(plan.Bindings).SlotId);
        Assert.Empty(plan.UpdatedSlots);
    }

    [Fact]
    public void Active_ResumptionOnlyReKeysTheArchivedSlot_ItStaysArchivedWithItsArchiveDate()
    {
        // AK 53 (a): archived S1(A, t1) with usage, no active A; the preview has A(t3).
        var slots = new[] { Archived("s1", "A", Day(1), archivedAt: Day(5)) };

        var plan = Active([Live("A", Day(9))], slots);

        Assert.Equal("s1", Assert.Single(plan.Bindings).SlotId);
        Assert.Equal(new SlotState("s1", "A", Day(9), true, true, Day(5), false), Assert.Single(plan.UpdatedSlots));
    }

    [Fact]
    public void Active_ResumptionIsBlockedByAnActiveSlotOfTheName_WhichBindsByName()
    {
        // AK 53 (c): the restore created an active provisional A next to the archived S1; the dated entry
        // binds that one by name and authorizes its date, S1 stays as it is.
        var restoredAt = Now.AddMinutes(-3);
        var slots = new[] { Archived("s1", "A", Day(1), archivedAt: Day(5)), Provisional("restored", "A", restoredAt) };

        var plan = Active([Live("A", Day(9))], slots);

        Assert.Equal("restored", Assert.Single(plan.Bindings).SlotId);
        Assert.Equal(new SlotState("restored", "A", Day(9), true, false, null, false), Assert.Single(plan.UpdatedSlots));
    }

    [Fact]
    public void Active_PushSlot_IsBoundByNameAndItsDateAuthorized()
    {
        // Hand-off #374: the EventAPI push created a provisional slot with the receive time.
        var slots = new[] { Provisional("push", "A", Now.AddMinutes(-5)) };

        var plan = Active([Live("A", Day(9))], slots);

        Assert.Equal("push", Assert.Single(plan.Bindings).SlotId);
        Assert.Equal(new SlotState("push", "A", Day(9), true, false, null, false), Assert.Single(plan.UpdatedSlots));
    }

    [Fact]
    public void Active_DatedEntry_NeverBindsAnAuthoritativeSlotOfAnotherDateByName_AndArchivesNothing()
    {
        var slots = new[] { Authoritative("rest", "A", Day(2)) };

        var plan = Active([Live("A", Day(9))], slots);

        Assert.Null(Assert.Single(plan.Bindings).SlotId);
        // No step 7: the unexplained active slot is REST's to archive.
        Assert.Empty(plan.UpdatedSlots);
    }

    [Theory]
    [InlineData(true)] // authoritative
    [InlineData(false)] // provisional
    public void Active_UndatedEntry_BindsAnActiveSlotOfItsNameWithoutTouchingItsDate(bool authoritative)
    {
        var slot = authoritative ? Authoritative("s1", "A", Day(2)) : Provisional("s1", "A", Now.AddMinutes(-5));

        var plan = Active([Live("A")], [slot]);

        Assert.Null(plan.Rejection);
        Assert.Equal(new BackfillEntryBinding("A", null, "s1"), Assert.Single(plan.Bindings));
        Assert.Empty(plan.UpdatedSlots);
    }

    [Fact]
    public void Active_UndatedEntryWithoutAnActiveSlotOfItsName_IsRejected()
    {
        // R4: never a provisional slot from the backfill; an archived namesake does not count.
        var slots = new[] { Archived("s1", "A", Day(1), archivedAt: Day(5)) };

        var plan = Active([Live("B", Day(3)), Live("A")], slots);

        Assert.Equal(BackfillSlotBindingRejection.UndatedEntryWithoutBinding, plan.Rejection);
        Assert.Empty(plan.Bindings);
        Assert.Empty(plan.UpdatedSlots);
    }

    [Fact]
    public void DuplicateNameInThePreview_FirstEntryCounts()
    {
        var plan = Active([Live("A", Day(1)), Live("A", Day(2))], []);

        Assert.Equal(new BackfillEntryBinding("A", Day(1), null), Assert.Single(plan.Bindings));
    }

    [Fact]
    public void EmptyPreviewGroup_IsRejectedAsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => Active([], []));
        Assert.Throws<ArgumentException>(() => Inactive([], []));
    }

    // ---- Preview of another set (2.3) ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inactive_AnUndatedEntry_IsRejected_WithOrWithoutASeedOfTheSet(bool withSeed)
    {
        // R7 (a), 2.4 b/b': also a single-entry group waits for its date.
        var slots = withSeed ? new[] { Seed("seed-x", "A") } : [];

        var plan = Inactive([Live("A", Day(1)), Live("B")], slots, previewSeeds: withSeed ? ["seed-x"] : []);

        Assert.Equal(BackfillSlotBindingRejection.UndatedEntryOutsideActiveSet, plan.Rejection);
        Assert.Empty(plan.Bindings);
        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.UsageRebinds);
    }

    [Fact]
    public void Inactive_SeedOfTheSet_IsSettledInPlaceOntoTheOldestEntryAndStaysArchived()
    {
        // AK 26: seed with A's rows (active since M1), preview [ome44 (old), hob44] dated.
        var slots = new[] { Seed("seed-x", "hob44") };

        var plan = Inactive([Live("hob44", Day(30)), Live("ome44", Day(1))], slots, previewSeeds: ["seed-x"]);

        Assert.Null(plan.Rejection);
        Assert.Equal([new BackfillEntryBinding("hob44", Day(30), null), new BackfillEntryBinding("ome44", Day(1), "seed-x")], plan.Bindings);
        // R2: archived, the archive date stamped now since the seed had none; marker off; authoritative.
        Assert.Equal(new SlotState("seed-x", "ome44", Day(1), true, true, Now, false), Assert.Single(plan.UpdatedSlots));
        Assert.Empty(plan.UsageRebinds);
    }

    [Fact]
    public void Inactive_SeedSettledInPlace_KeepsItsArchiveDate()
    {
        var slots = new[] { Seed("seed-x", "A", archived: true, archivedAt: Day(7)) };

        var plan = Inactive([Live("A", Day(2))], slots, previewSeeds: ["seed-x"]);

        Assert.Equal(new SlotState("seed-x", "A", Day(2), true, true, Day(7), false), Assert.Single(plan.UpdatedSlots));
    }

    [Fact]
    public void Inactive_OldestTie_PicksTheSmallestName()
    {
        var slots = new[] { Seed("seed-x", "x") };

        var plan = Inactive([Live("b", Day(3)), Live("Z", Day(3))], slots, previewSeeds: ["seed-x"]);

        Assert.Equal("Z", Assert.Single(plan.UpdatedSlots).Name);
        Assert.Equal("seed-x", plan.Bindings.Single(b => b.Name == "Z").SlotId);
        Assert.Null(plan.Bindings.Single(b => b.Name == "b").SlotId);
    }

    [Theory]
    [InlineData(true)] // target archived
    [InlineData(false)] // target active (a slot of the active set with the same exact date)
    public void Inactive_TargetByExactDate_MovesTheSeedOntoItAndEmptiesTheSeed(bool targetArchived)
    {
        // AK 26 "target exists": the seed's rows of X and the snapshot rows move, the seed ends empty.
        var target = targetArchived ? Archived("t", "ome44", Day(1), archivedAt: Day(5)) : Authoritative("t", "ome44", Day(1));
        var slots = new[] { target, Seed("seed-x", "hob44") };

        var plan = Inactive([Live("ome44", Day(1)), Live("hob44", Day(30))], slots, previewSeeds: ["seed-x"]);

        Assert.Equal([new SlotUsageRebind("seed-x", "t")], plan.UsageRebinds);
        Assert.Equal(["t", null], plan.Bindings.Select(b => b.SlotId));
        // The target keeps its membership (E2); the seed is archived without date like plan #372 E12.
        Assert.Equal(new SlotState("seed-x", "hob44", null, false, true, null, false), Assert.Single(plan.UpdatedSlots));
    }

    [Fact]
    public void Inactive_TargetBySingleDate_IsRenamedButKeepsItsArchiveState()
    {
        var slots = new[] { Archived("t", "A", Day(4), archivedAt: Day(8)), Seed("seed-x", "A") };

        var plan = Inactive([Live("C", Day(4))], slots, previewSeeds: ["seed-x"]);

        Assert.Equal([new SlotUsageRebind("seed-x", "t")], plan.UsageRebinds);
        Assert.Equal(
            [new SlotState("seed-x", "A", null, false, true, null, false), new SlotState("t", "C", Day(4), true, true, Day(8), false)],
            plan.UpdatedSlots);
    }

    [Fact]
    public void Inactive_SeedSettle_NeverBindsTheActiveSetsProvisionalNamesake()
    {
        // Sol-P2-2 regression: X's seed and, in the active set Y, an active provisional push slot A carrying
        // live rows. The REST name fallback would bind it and give it X's date and X's rows; the preview
        // settles the seed in place and leaves Y's slot exactly as it is.
        var pushedAt = Now.AddMinutes(-5);
        var push = Provisional("push", "A", pushedAt);
        var slots = new[] { push, Seed("seed-x", "A") };

        var plan = Inactive([Live("A", Day(3))], slots, previewSeeds: ["seed-x"]);

        Assert.Equal("seed-x", Assert.Single(plan.Bindings).SlotId);
        Assert.Equal(new SlotState("seed-x", "A", Day(3), true, true, Now, false), Assert.Single(plan.UpdatedSlots));
        Assert.DoesNotContain(plan.UpdatedSlots, s => s.Id == "push");
        Assert.Empty(plan.UsageRebinds);

        // Y's next REST tick still binds its own entry to the untouched push slot by name: no new slot.
        var rest = EmoteSlotReconcilePlanner.PlanLiveGroup(
            [Live("A", Day(20))], [push, new SlotState("seed-x", "A", Day(3), true, true, Now, false)], new HashSet<string>(), "A", true, Now);
        Assert.Empty(rest.CreatedSlots);
        Assert.Equal(new SlotState("push", "A", Day(20), true, false, null, false), Assert.Single(rest.UpdatedSlots));
    }

    [Fact]
    public void RestSettleOfTheActiveSet_StillBindsTheProvisionalNamesakeByName()
    {
        // Counter-check: the REST target selection is unchanged (date, then name).
        var slots = new[] { Provisional("push", "A", Now.AddMinutes(-5)), Seed("seed", "A") };

        var plan = EmoteSlotReconcilePlanner.PlanLiveGroup(
            [Live("A", Day(3))], slots, new HashSet<string>(["seed"], StringComparer.Ordinal), "A", true, Now);

        Assert.Equal([new SlotUsageRebind("seed", "push")], plan.UsageRebinds);
        Assert.Contains(new SlotState("push", "A", Day(3), true, false, null, false), plan.UpdatedSlots);
    }

    [Fact]
    public void Inactive_TwoSeedsOfTheSet_TheSecondMovesOntoTheFirst()
    {
        var slots = new[] { Seed("seed-b", "A"), Seed("seed-a", "A") };

        var plan = Inactive([Live("A", Day(3))], slots, previewSeeds: ["seed-a", "seed-b"]);

        Assert.Equal("seed-a", Assert.Single(plan.Bindings).SlotId);
        Assert.Equal([new SlotUsageRebind("seed-b", "seed-a")], plan.UsageRebinds);
        Assert.Equal(
            [new SlotState("seed-a", "A", Day(3), true, true, Now, false), new SlotState("seed-b", "A", null, false, true, null, false)],
            plan.UpdatedSlots);
    }

    [Fact]
    public void Inactive_DatedEntries_BindByExactDateOnly_NoResumptionAndNoNameBinding()
    {
        // D28: an archived namesake with an archive date (a D7 candidate) and an active provisional
        // namesake are both left alone; only the exact date binds.
        var slots = new[]
        {
            Archived("old", "A", Day(1), archivedAt: Day(5)),
            Provisional("push", "B", Now.AddMinutes(-5)),
            Archived("exact", "C", Day(6), archivedAt: Day(7)),
        };

        var plan = Inactive([Live("A", Day(9)), Live("B", Day(10)), Live("C", Day(6))], slots);

        Assert.Null(plan.Rejection);
        Assert.Equal([null, null, "exact"], plan.Bindings.Select(b => b.SlotId));
        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.UsageRebinds);
    }

    [Fact]
    public void Inactive_SeedsOfTheActiveSetAndOfOtherSets_StayUntouched()
    {
        var slots = new[] { Seed("seed-y", "A"), Seed("seed-z", "A") };

        var plan = Inactive([Live("A", Day(3))], slots);

        Assert.Null(Assert.Single(plan.Bindings).SlotId);
        Assert.Empty(plan.UpdatedSlots);
        Assert.Empty(plan.UsageRebinds);
    }

    // ---- helpers ----

    private static DateTime Day(int day) => new DateTime(2026, 8, 1, 9, 30, 15, 123, DateTimeKind.Utc).AddDays(day);

    private static SlotLiveEntry Live(string name, DateTime? addedAt = null) => new(name, addedAt);

    private static SlotState Authoritative(string id, string name, DateTime addedAt) => new(id, name, addedAt, true, false, null, false);

    private static SlotState Provisional(string id, string name, DateTime? addedAt) => new(id, name, addedAt, false, false, null, false);

    private static SlotState Archived(string id, string name, DateTime addedAt, DateTime archivedAt) =>
        new(id, name, addedAt, true, true, archivedAt, false);

    // A backfill slot "never in the active set" (ArchivedSlotUpsert): archived without archive date.
    private static SlotState NeverIn(string id, string name, DateTime addedAt) => new(id, name, addedAt, true, true, null, false);

    private static SlotState Seed(string id, string name, bool archived = false, DateTime? archivedAt = null) =>
        new(id, name, null, false, archived, archivedAt, true);

    private static BackfillSlotBindingPlan Active(IReadOnlyList<SlotLiveEntry> preview, IReadOnlyList<SlotState> slots, string[]? activeSeeds = null) =>
        EmoteSlotReconcilePlanner.PlanActivePreviewGroup(preview, slots, new HashSet<string>(activeSeeds ?? [], StringComparer.Ordinal));

    private static BackfillSlotBindingPlan Inactive(IReadOnlyList<SlotLiveEntry> preview, IReadOnlyList<SlotState> slots, string[]? previewSeeds = null) =>
        EmoteSlotReconcilePlanner.PlanInactivePreviewGroup(preview, slots, new HashSet<string>(previewSeeds ?? [], StringComparer.Ordinal), Now);
}
