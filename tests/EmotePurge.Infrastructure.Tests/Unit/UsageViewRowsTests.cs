using EmotePurge.Infrastructure.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The row formation of the slot-aware usage readers (plan #376, sections 2.1–2.3, R1, R2): mode per emote,
// row set per view, display name/date, isArchived, siblingNames, and the invariant
// "slotId set <=> the row carries exactly that one slot's numbers".
public class UsageViewRowsTests
{
    private const string SetX = "set-x";

    private static readonly DateTime T1 = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EmoteDate = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);

    private static readonly UsageViewEmote Active = new("e-1", "emoteName", EmoteDate, IsArchived: false);
    private static readonly UsageViewEmote Archived = new("e-1", "emoteName", EmoteDate, IsArchived: true);

    [Fact]
    public void TwoActiveOwnSlots_ActiveView_OneRowPerSlot_InD2Order_WithSiblingsAndSlotDates()
    {
        // AK 28: hob44 is listed first but added later; the D2 order puts ome44 first.
        var rows = Build(
            [Active],
            [Slot("s-hob", "hob44", T2, fromRest: true), Slot("s-ome", "ome44", T1, fromRest: false)],
            rowsInSet: ["s-ome"]);

        Assert.Equal(2, rows.Count);
        var ome = rows[0];
        var hob = rows[1];
        Assert.Equal(("s-ome", "ome44", T1, false), (ome.SlotId, ome.Name, ome.FirstSeenAt, ome.FirstSeenAtFromRest));
        Assert.Equal(("s-hob", "hob44", T2, true), (hob.SlotId, hob.Name, hob.FirstSeenAt, hob.FirstSeenAtFromRest));
        Assert.Equal(["hob44"], ome.SiblingNames);
        Assert.Equal(["ome44"], hob.SiblingNames);
        Assert.All(rows, r => Assert.False(r.IsArchived));
        AssertInvariant(rows);
    }

    [Fact]
    public void ActiveView_ZeroFillsAnActiveSlotWithoutRows()
    {
        var rows = Build([Active], [Slot("s-1", "a", T1)], rowsInSet: []);

        var row = Assert.Single(rows);
        Assert.Equal("s-1", row.SlotId);
        Assert.Equal(["s-1"], row.AggregateSlotIds);
    }

    [Fact]
    public void SeedOfTheRequestedSet_PutsTheEmoteInEmoteMode_WithTheEmotesNameDateAndAllItsRows()
    {
        // R2: the seed carries rows of X; a settled active slot next to it (the transient case after a
        // backfill enqueue) does not split the card, its rows are folded into the one emote-mode row.
        var rows = Build(
            [Active],
            [Slot("s-seed", "staleName", null, seed: true), Slot("s-own", "ome44", T1, fromRest: true)],
            rowsInSet: ["s-seed", "s-own"],
            seedsOfSet: ["s-seed"]);

        var row = Assert.Single(rows);
        Assert.Null(row.SlotId);
        Assert.Equal(("emoteName", EmoteDate, false), (row.Name, row.FirstSeenAt, row.FirstSeenAtFromRest));
        Assert.Empty(row.SiblingNames);
        Assert.Equal(["s-own", "s-seed"], row.AggregateSlotIds);
        AssertInvariant(rows);
    }

    [Fact]
    public void ActiveView_EmoteWithoutActiveOwnSlot_IsOneEmoteModeRow_CarryingItsArchivedSlotsRows()
    {
        // E14, the virtual slot: an active emote whose only own slot is archived reads as the emote-wide row.
        var rows = Build(
            [Active],
            [Slot("s-old", "old", T1, archived: true)],
            rowsInSet: ["s-old"]);

        var row = Assert.Single(rows);
        Assert.Null(row.SlotId);
        Assert.Equal("emoteName", row.Name);
        Assert.False(row.IsArchived);
        Assert.Equal(["s-old"], row.AggregateSlotIds);
    }

    [Fact]
    public void ActiveView_EmoteWithoutAnySlot_IsOneZeroFilledEmoteModeRow()
    {
        var rows = Build([Active], [], rowsInSet: []);

        var row = Assert.Single(rows);
        Assert.Null(row.SlotId);
        Assert.Empty(row.AggregateSlotIds);
    }

    [Fact]
    public void ASeedOfAnotherSet_OrARowlessSeed_DoesNotTriggerEmoteMode()
    {
        // Neither seed is in the seeds of X: one carries rows of another set only, the other none at all
        // (a backfill enqueue seed). The emote stays in slot mode and neither seed gets a row.
        var rows = Build(
            [Active],
            [
                Slot("s-own", "ome44", T1, fromRest: true),
                Slot("s-seed-y", "ome44", null, seed: true),
                Slot("s-seed-empty", "ome44", null, seed: true),
            ],
            rowsInSet: ["s-own"]);

        var row = Assert.Single(rows);
        Assert.Equal("s-own", row.SlotId);
        Assert.Equal(["s-own"], row.AggregateSlotIds);
        Assert.Empty(row.SiblingNames);
    }

    [Fact]
    public void ActiveView_AnArchivedOwnSlotWithRows_IsNotShownNextToTheActiveSlot()
    {
        var rows = Build(
            [Active],
            [Slot("s-archived", "old", T1, archived: true), Slot("s-active", "new", T2)],
            rowsInSet: ["s-archived", "s-active"]);

        var row = Assert.Single(rows);
        Assert.Equal("s-active", row.SlotId);
        Assert.Equal(["s-active"], row.AggregateSlotIds);
        Assert.Empty(row.SiblingNames);
    }

    [Fact]
    public void NonActiveView_ShowsOwnSlotsWithRowsInX_ArchivedIncluded_AndSkipsAnEmptiedSlot()
    {
        var rows = Build(
            [Active],
            [
                Slot("s-archived", "old", T1, archived: true),
                Slot("s-active", "new", T2),
                Slot("s-emptied", "gone", T1.AddDays(1), archived: true),
            ],
            rowsInSet: ["s-archived", "s-active"],
            isActiveView: false);

        Assert.Equal(["s-archived", "s-active"], rows.Select(r => r.SlotId));
        Assert.True(rows[0].IsArchived);
        Assert.False(rows[1].IsArchived);
        Assert.Equal(["new"], rows[0].SiblingNames);
        Assert.Equal(["old"], rows[1].SiblingNames);
        AssertInvariant(rows);
    }

    [Fact]
    public void NonActiveView_EmoteWithoutRowsInX_HasNoRow_EvenWithActiveSlots()
    {
        var rows = Build([Active], [Slot("s-1", "a", T1)], rowsInSet: [], isActiveView: false);

        Assert.Empty(rows);
    }

    [Fact]
    public void NonActiveView_ArchivedEmote_MarksEverySlotRowArchived()
    {
        var rows = Build(
            [Archived],
            [Slot("s-1", "a", T1)],
            rowsInSet: ["s-1"],
            isActiveView: false);

        var row = Assert.Single(rows);
        Assert.Equal("s-1", row.SlotId);
        Assert.True(row.IsArchived);
    }

    [Fact]
    public void NonActiveView_SeedOfX_IsOneEmoteModeRow_WithTheEmotesArchiveState()
    {
        var rows = Build(
            [Archived],
            [Slot("s-seed", "staleName", null, seed: true)],
            rowsInSet: ["s-seed"],
            seedsOfSet: ["s-seed"],
            isActiveView: false);

        var row = Assert.Single(rows);
        Assert.Null(row.SlotId);
        Assert.True(row.IsArchived);
        Assert.Equal(["s-seed"], row.AggregateSlotIds);
    }

    [Fact]
    public void NonActiveView_ARowBearingSeedMissingFromTheSeedsOfX_StillKeepsTheEmote()
    {
        // An inconsistent snapshot (a settle between the caller's queries): the emote's rows in X sit on an
        // unsettled seed the seeds-of-X read no longer returned. It must not vanish from the view.
        var rows = Build(
            [Active],
            [Slot("s-seed", "staleName", null, seed: true)],
            rowsInSet: ["s-seed"],
            isActiveView: false);

        var row = Assert.Single(rows);
        Assert.Null(row.SlotId);
        Assert.Equal(["s-seed"], row.AggregateSlotIds);
    }

    [Fact]
    public void ActiveView_ArchivedEmote_HasNoRow()
    {
        var rows = Build([Archived], [Slot("s-1", "a", T1)], rowsInSet: ["s-1"]);

        Assert.Empty(rows);
    }

    [Fact]
    public void SiblingNames_AreDistinct_AndNeverTheRowsOwnName()
    {
        // Non-active view: an archived and an active stay under the same alias plus a third alias.
        var rows = Build(
            [Active],
            [
                Slot("s-a1", "same", T1, archived: true),
                Slot("s-a2", "same", T2),
                Slot("s-b", "other", T2.AddDays(1)),
                Slot("s-c", "other", T2.AddDays(2), archived: true),
            ],
            rowsInSet: ["s-a1", "s-a2", "s-b", "s-c"],
            isActiveView: false);

        Assert.Equal(["s-a1", "s-a2", "s-b", "s-c"], rows.Select(r => r.SlotId));
        Assert.Equal(["other"], rows[0].SiblingNames);
        Assert.Equal(["other"], rows[1].SiblingNames);
        Assert.Equal(["same"], rows[2].SiblingNames);
        Assert.Equal(["same"], rows[3].SiblingNames);
    }

    [Fact]
    public void D2Order_UndatedSlotsLast_AndANameTieInUtf8ByteOrder()
    {
        // Same date: U+FF5E (UTF-8 EF BD 9E) before U+1F600 (UTF-8 F0 …), although UTF-16 ordinal says
        // the opposite; an undated slot goes last whatever its name.
        const string tilde = "～";
        const string emoji = "\U0001F600";
        var rows = Build(
            [Active],
            [Slot("s-undated", "aaa", null), Slot("s-emoji", emoji, T1), Slot("s-tilde", tilde, T1)],
            rowsInSet: []);

        Assert.Equal(["s-tilde", "s-emoji", "s-undated"], rows.Select(r => r.SlotId));
        Assert.Equal([emoji, "aaa"], rows[0].SiblingNames);
        Assert.Equal([tilde, emoji], rows[2].SiblingNames);
        Assert.Null(rows[2].FirstSeenAt);
    }

    [Fact]
    public void Rows_AreGroupedByEmoteIdOrdinally_AndIgnoreSlotsOfUnknownEmotes()
    {
        UsageViewEmote upper = new("E-0", "upper", null, IsArchived: false);
        UsageViewEmote lower = new("e-1", "lower", null, IsArchived: false);
        var rows = Build(
            [lower, upper],
            [Slot("s-l", "lower", T1), Slot("s-u", "upper", T1, emoteId: "E-0"), Slot("s-ghost", "x", T1, emoteId: "e-9")],
            rowsInSet: ["s-ghost"]);

        Assert.Equal(["E-0", "e-1"], rows.Select(r => r.EmoteId));
        Assert.DoesNotContain(rows, r => r.SlotId == "s-ghost");
    }

    // F6 (plan #377 2.12): a slot row says whether another own slot of the emote carries the same authoritative
    // date, computed over the emote's whole slot list and not over the rows that happen to be shown.
    [Fact]
    public void FirstSeenAtShared_IsFalse_WhenTheAuthoritativeDateIsUnique()
    {
        var rows = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: true), Slot("s-b", "b", T2, fromRest: true)],
            rowsInSet: ["s-a", "s-b"]);

        Assert.All(rows, r => Assert.False(r.FirstSeenAtShared));
    }

    [Fact]
    public void FirstSeenAtShared_IsTrueOnBothRows_WhenTwoSlotsWithRowsShareTheirDate()
    {
        var rows = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: true), Slot("s-b", "b", T1, fromRest: true)],
            rowsInSet: ["s-a", "s-b"],
            isActiveView: false);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.FirstSeenAtShared));
    }

    [Fact]
    public void FirstSeenAtShared_IsTrue_WhenTheTwinHasNoRowsInTheViewedNonActiveSet()
    {
        var rows = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: true), Slot("s-b", "b", T1, fromRest: true)],
            rowsInSet: ["s-a"],
            isActiveView: false);

        var row = Assert.Single(rows);
        Assert.Equal("s-a", row.SlotId);
        Assert.True(row.FirstSeenAtShared);
    }

    [Fact]
    public void FirstSeenAtShared_CountsAnArchivedTwin_ButNeitherASeedNorAProvisionalDate()
    {
        var archivedTwin = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: true), Slot("s-b", "b", T1, fromRest: true, archived: true)],
            rowsInSet: ["s-a"]);
        var seedTwin = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: true), Slot("s-seed", "stale", T1, fromRest: true, seed: true)],
            rowsInSet: ["s-a"]);
        var provisionalTwin = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: true), Slot("s-b", "b", T1, fromRest: false)],
            rowsInSet: ["s-a"],
            isActiveView: false);

        Assert.True(Assert.Single(archivedTwin).FirstSeenAtShared);
        Assert.False(Assert.Single(seedTwin).FirstSeenAtShared);
        Assert.False(Assert.Single(provisionalTwin).FirstSeenAtShared);
    }

    [Fact]
    public void FirstSeenAtShared_IsFalse_ForEmoteModeAndProvisionalRows()
    {
        var emoteMode = Build(
            [Active],
            [Slot("s-seed", "stale", null, seed: true), Slot("s-a", "a", T1, fromRest: true), Slot("s-b", "b", T1, fromRest: true)],
            rowsInSet: ["s-seed"],
            seedsOfSet: ["s-seed"]);
        var provisional = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: false), Slot("s-b", "b", T1, fromRest: false)],
            rowsInSet: ["s-a", "s-b"]);

        Assert.Null(Assert.Single(emoteMode).SlotId);
        Assert.All(emoteMode.Concat(provisional), r => Assert.False(r.FirstSeenAtShared));
    }

    [Fact]
    public void FirstSeenAtShared_InTheActiveView_FollowsTheSameRule()
    {
        var rows = Build(
            [Active],
            [Slot("s-a", "a", T1, fromRest: true), Slot("s-b", "b", T1, fromRest: true), Slot("s-c", "c", T2, fromRest: true)],
            rowsInSet: []);

        Assert.Equal([true, true, false], rows.Select(r => r.FirstSeenAtShared));
    }

    private static IReadOnlyList<UsageViewRow> Build(
        UsageViewEmote[] emotes,
        UsageViewSlot[] slots,
        string[] rowsInSet,
        string[]? seedsOfSet = null,
        bool isActiveView = true) =>
        UsageViewRows.Build(
            emotes,
            slots,
            new HashSet<string>(seedsOfSet ?? [], StringComparer.Ordinal),
            new HashSet<string>(rowsInSet, StringComparer.Ordinal),
            isActiveView);

    private static UsageViewSlot Slot(
        string id, string name, DateTime? addedAt, bool fromRest = false, bool archived = false, bool seed = false, string emoteId = "e-1") =>
        new(id, emoteId, name, addedAt, fromRest, archived, seed);

    // R2: slotId set <=> the row carries exactly that slot; at most one row per slot id; one emote-mode row
    // per emote at most, and never next to a slot-mode row of the same emote.
    private static void AssertInvariant(IReadOnlyList<UsageViewRow> rows)
    {
        foreach (var row in rows.Where(r => r.SlotId is not null))
        {
            Assert.Equal([row.SlotId!], row.AggregateSlotIds);
        }

        var slotIds = rows.Where(r => r.SlotId is not null).Select(r => r.SlotId).ToList();
        Assert.Equal(slotIds.Count, slotIds.Distinct(StringComparer.Ordinal).Count());
        foreach (var group in rows.GroupBy(r => r.EmoteId, StringComparer.Ordinal))
        {
            var emoteModeRows = group.Count(r => r.SlotId is null);
            Assert.True(emoteModeRows == 0 || group.Count() == 1, $"Emote {group.Key} mixes modes or repeats its emote-mode row.");
        }
    }
}
