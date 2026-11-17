using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishReconcileService (wish emotes, T7) against real Postgres with slots seeded straight into the tables:
// AK7 to AK10, AK12, the generation rules (plan finding 19, Sol-R2-2, Sol-R2-3), the K4 exceptions, the
// pre-check's single query, idempotence and the one-event rule. The real slot writers, the savepoint and the
// lock order against a command are in the other parts of this class. Every test seeds its own channel.
[Collection("Postgres")]
public partial class WishReconcileServiceTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4242", "wishmod");

    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly HandWoundTimeProvider _clock = new();
    private readonly RecordingLogger<WishReconcileService> _logger = new();

    private DateTime Now => _clock.Now.UtcDateTime;

    // ------------------------------------------------------------------------------------ AK7: approved

    [Fact]
    public async Task Ak7_ApprovedAndOneNewSlot_BecomesActive_AndTheAliasFollowsTheSlot()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w =>
        {
            w.Alias = "Wanted";
            w.ApprovedAtUtc = Now.AddHours(-1);
            w.DurationDays = 21;
        });
        var addedAt = Now.AddMinutes(-30);
        var slot = await SeedSlotAsync(emote, "InTheSet", addedAt);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot, "InTheSet", WishLinkOrigin.Auto), (wish.Status, wish.SlotId, wish.Alias, wish.LinkOrigin));
        Assert.Equal((addedAt, addedAt.AddDays(21), true), (wish.StartsAtUtc, wish.ExpiresAtUtc, wish.StartsAtAuthoritative));
        Assert.Equal((addedAt, Now, Now, Now), (wish.SlotAddedAtUtc, wish.SlotConfirmedAtUtc, wish.FirstActivatedAtUtc, wish.StatusChangedAtUtc));
        Assert.Equal((0, false, (DateTime?)null), (wish.DecisionVersion, wish.BotOwned, wish.EndedAtUtc));
        AssertPublishedOnce(channel);
    }

    [Fact]
    public async Task Ak7_TwoNewSlots_LinksTheOneCarryingTheWishesAlias()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.Alias = "Kappa");
        await SeedSlotAsync(emote, "Other", Now.AddMinutes(5));
        var bearer = await SeedSlotAsync(emote, "Kappa", Now.AddMinutes(10));

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, bearer), (wish.Status, wish.SlotId));
    }

    [Fact]
    public async Task Ak7_TwoNewSlotsWithoutTheAlias_StayApproved_AndNothingIsAnnounced()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.Alias = "Kappa");
        await SeedSlotAsync(emote, "One", Now.AddMinutes(5));
        await SeedSlotAsync(emote, "Two", Now.AddMinutes(10));
        var before = await SnapshotAsync(wishId);

        await ReconcileAsync(channel);

        Assert.Equal(before, await SnapshotAsync(wishId));
        AssertNothingPublished();
    }

    [Fact]
    public async Task Ak7_ASlotOneSecondBeforeTheApproval_IsNoCandidate()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var approvedAt = Now.AddHours(-1);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = approvedAt);
        await SeedSlotAsync(emote, "Alias", approvedAt.AddSeconds(-1));

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Approved, (await WishAsync(wishId)).Status);
        AssertNothingPublished();
    }

    // Plan finding 7: archived, unsettled and undated slots are no candidates.
    [Theory]
    [InlineData("archived")]
    [InlineData("seed")]
    [InlineData("undated")]
    public async Task InadmissibleSlots_AreNoCandidates(string kind)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-1));
        await SeedSlotAsync(
            emote, "Alias", kind == "undated" ? null : Now, archived: kind == "archived", seed: kind == "seed", fromRest: kind != "undated");

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Approved, (await WishAsync(wishId)).Status);
    }

    // ------------------------------------------------------------------------------------ AK8: date confirmation

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ak8_ProvisionalThenAuthoritative_ShiftsStartAndExpiryByTheSameDifference(bool extendedInBetween)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-2));
        var provisional = Now.AddHours(-1);
        var slot = await SeedSlotAsync(emote, "Alias", provisional, fromRest: false);

        await ReconcileAsync(channel);
        var linked = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, provisional, false), (linked.Status, linked.StartsAtUtc, linked.StartsAtAuthoritative));

        if (extendedInBetween)
        {
            Assert.True((await WishSut().ExtendAsync(channel.Name, wishId, Actor)).IsSuccess);
        }

        var beforeConfirmation = await WishAsync(wishId);
        var authoritative = provisional.AddMinutes(-10);
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (authoritative, true));
        _clock.Advance(TimeSpan.FromHours(1));
        _publisher.ClearReceivedCalls();

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot), (wish.Status, wish.SlotId));
        Assert.Equal(authoritative, wish.StartsAtUtc);
        Assert.Equal(beforeConfirmation.ExpiresAtUtc!.Value.AddMinutes(-10), wish.ExpiresAtUtc);
        Assert.Equal((beforeConfirmation.DurationDays, beforeConfirmation.ExtensionCount), (wish.DurationDays, wish.ExtensionCount));
        Assert.Equal((true, authoritative, Now), (wish.StartsAtAuthoritative, wish.SlotAddedAtUtc, wish.SlotConfirmedAtUtc));
        // A date correction is no decision (plan finding 21).
        Assert.Equal(beforeConfirmation.DecisionVersion, wish.DecisionVersion);
        AssertPublishedOnce(channel);
    }

    // Sol-R2-2: an authoritative date a second after the provisional one (clock skew) runs as a generation
    // change and returns in the same pass: deadline unshifted, the flag true, and it stays so.
    [Fact]
    public async Task LaterConfirmation_ReturnsInTheSamePass_DeadlineUnshifted_FlagTrue_AndASecondRunChangesNothing()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w =>
        {
            w.ApprovedAtUtc = Now.AddHours(-1);
            w.DurationDays = 1;
        });
        var provisional = Now;
        var slot = await SeedSlotAsync(emote, "Alias", provisional, fromRest: false);
        await ReconcileAsync(channel);
        var linked = await WishAsync(wishId);

        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (provisional.AddSeconds(1), true));
        _clock.Advance(TimeSpan.FromMinutes(5));
        _publisher.ClearReceivedCalls();
        await ReconcileAsync(channel);

        var first = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot, (DateTime?)null), (first.Status, first.SlotId, first.EndedAtUtc));
        Assert.Equal((linked.StartsAtUtc, linked.ExpiresAtUtc), (first.StartsAtUtc, first.ExpiresAtUtc));
        Assert.Equal((true, provisional.AddSeconds(1)), (first.StartsAtAuthoritative, first.SlotAddedAtUtc));
        AssertPublishedOnce(channel);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _publisher.ClearReceivedCalls();
        var snapshot = await SnapshotAsync(wishId);
        await ReconcileAsync(channel);

        Assert.Equal(snapshot, await SnapshotAsync(wishId));
        AssertNothingPublished();

        // The expiry run's selection (K2, AK26) as a query on the fields: an overdue, authoritative active wish.
        var later = Now.AddDays(2);
        await using var db = fixture.CreateDbContext();
        Assert.True(await db.Wishes.AnyAsync(w =>
            w.Id == wishId && w.Status == WishStatus.Active && w.StartsAtAuthoritative && w.ExpiresAtUtc <= later));
    }

    [Fact]
    public async Task AWronglyStandingFlag_IsHealed_WhenTheSlotIsAuthoritativeInTheSameGeneration()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var addedAt = Now.AddDays(-2);
        var slot = await SeedSlotAsync(emote, "Alias", addedAt);
        var wishId = await SeedLinkedAsync(channel, emote, slot, addedAt, authoritative: false);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, true, addedAt, addedAt), (wish.Status, wish.StartsAtAuthoritative, wish.StartsAtUtc, wish.SlotAddedAtUtc));
        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ AK9: rename

    [Fact]
    public async Task Ak9_Rename_AliasFollows_DeadlineStays_OwnershipEnds_AsADecision()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var addedAt = Now.AddDays(-3);
        var slot = await SeedSlotAsync(emote, "Old", addedAt);
        var wishId = await SeedLinkedAsync(channel, emote, slot, addedAt, alias: "Old", configure: w =>
        {
            w.BotOwned = true;
            w.BotOwnedAddedAtUtc = addedAt;
            w.DecisionVersion = 2;
            w.RemovalQueuedAtUtc = Now;
            w.RemovalQueuedVersion = 2;
        });
        var before = await WishAsync(wishId);
        await UpdateSlotAsync(slot, s => s.Name = "New");

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot, "New"), (wish.Status, wish.SlotId, wish.Alias));
        Assert.Equal((before.StartsAtUtc, before.ExpiresAtUtc), (wish.StartsAtUtc, wish.ExpiresAtUtc));
        Assert.Equal((false, (DateTime?)null, 3), (wish.BotOwned, wish.BotOwnedAddedAtUtc, wish.DecisionVersion));
        Assert.Equal(((DateTime?)null, (int?)null), (wish.RemovalQueuedAtUtc, wish.RemovalQueuedVersion));
        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ AK10: remove and return

    [Theory]
    [InlineData(5, true)]
    [InlineData(7, false)]
    public async Task Ak10_ArchivedSlot_IsRemovedExternallyAtItsArchiveTime_AndANewSlotReturnsItOnlyInsideTheWindow(int hours, bool returns)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var addedAt = Now.AddDays(-3);
        var slot = await SeedSlotAsync(emote, "Kappa", addedAt);
        var wishId = await SeedLinkedAsync(channel, emote, slot, addedAt, alias: "Kappa");
        var linked = await WishAsync(wishId);
        var archivedAt = Now.AddHours(1);
        await UpdateSlotAsync(slot, s => (s.IsArchived, s.ArchivedAt) = (true, archivedAt));
        _clock.Advance(TimeSpan.FromHours(2));

        await ReconcileAsync(channel);

        var removed = await WishAsync(wishId);
        Assert.Equal((WishStatus.RemovedExternally, archivedAt, slot), (removed.Status, removed.EndedAtUtc, removed.SlotId));
        AssertPublishedOnce(channel);

        var replacementAt = archivedAt.AddHours(hours);
        var replacement = await SeedSlotAsync(emote, "Kappa", replacementAt);
        _clock.SetUtcNow(replacementAt.AddMinutes(1));
        _publisher.ClearReceivedCalls();
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);

        await ReconcileAsync(channel, hook);

        var wish = await WishAsync(wishId);
        if (returns)
        {
            Assert.Equal((WishStatus.Active, replacement, (DateTime?)null), (wish.Status, wish.SlotId, wish.EndedAtUtc));
            Assert.Equal((linked.StartsAtUtc, linked.ExpiresAtUtc, false), (wish.StartsAtUtc, wish.ExpiresAtUtc, wish.BotOwned));
            Assert.Equal((replacementAt, true), (wish.SlotAddedAtUtc, wish.StartsAtAuthoritative));
            AssertPublishedOnce(channel);
        }
        else
        {
            Assert.Equal((WishStatus.RemovedExternally, archivedAt, slot), (wish.Status, wish.EndedAtUtc, wish.SlotId));
            // The pre-check found nothing to do: one query, no transaction.
            Assert.Equal(1, hook.Count(_ => true));
            AssertNothingPublished();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ak10_TwoReplacementSlots_ReturnOnlyToTheAliasBearer(bool oneCarriesTheAlias)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var endedAt = Now.AddHours(-3);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.RemovedExternally, w =>
        {
            w.Alias = "Kappa";
            w.EndedAtUtc = endedAt;
            w.StartsAtUtc = Now.AddDays(-5);
            w.ExpiresAtUtc = Now.AddDays(25);
        });
        var bearer = await SeedSlotAsync(emote, oneCarriesTheAlias ? "Kappa" : "First", endedAt.AddHours(1));
        await SeedSlotAsync(emote, "Second", endedAt.AddHours(2));

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal(oneCarriesTheAlias ? WishStatus.Active : WishStatus.RemovedExternally, wish.Status);
        Assert.Equal(oneCarriesTheAlias ? bearer : null, wish.SlotId);
    }

    // Sol-R2-3: a run long after the removal still finds the returner, by the replacement's date, not by "now".
    [Theory]
    [InlineData(25)]
    [InlineData(240)]
    public async Task LateRun_StillReturns_WhenTheReplacementLiesInTheWindow(int runAfterHours)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var t0 = Now;
        var expiresAt = t0.AddDays(20);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.RemovedExternally, w =>
        {
            w.Alias = "Kappa";
            w.EndedAtUtc = t0;
            w.StartsAtUtc = t0.AddDays(-10);
            w.ExpiresAtUtc = expiresAt;
        });
        var replacement = await SeedSlotAsync(emote, "Kappa", t0.AddHours(5));
        _clock.SetUtcNow(t0.AddHours(runAfterHours));

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, replacement, expiresAt, (DateTime?)null), (wish.Status, wish.SlotId, wish.ExpiresAtUtc, wish.EndedAtUtc));
    }

    [Fact]
    public async Task PresentedWish_LeavesWithItsReason_AndReturnsAsPresented()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var addedAt = Now.AddDays(-31);
        var slot = await SeedSlotAsync(emote, "Kappa", addedAt);
        var wishId = await SeedLinkedAsync(channel, emote, slot, addedAt, alias: "Kappa", configure: w =>
        {
            w.Status = WishStatus.Presented;
            w.StatusReason = WishStatusReason.Expired;
        });
        await UpdateSlotAsync(slot, s => (s.IsArchived, s.ArchivedAt) = (true, Now));

        await ReconcileAsync(channel);
        var removed = await WishAsync(wishId);
        Assert.Equal((WishStatus.RemovedExternally, WishStatusReason.Expired), (removed.Status, removed.StatusReason));

        var replacement = await SeedSlotAsync(emote, "Kappa", Now.AddHours(1));
        _clock.Advance(TimeSpan.FromHours(2));
        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Expired, replacement), (wish.Status, wish.StatusReason, wish.SlotId));
    }

    [Fact]
    public async Task TwoReturnersForOneSlot_TheOlderWishWins_TheYoungerStays()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var endedAt = Now.AddHours(-1);
        var younger = await SeedWishAsync(channel, emote, WishStatus.RemovedExternally, w => (w.Alias, w.EndedAtUtc, w.CreatedAtUtc) = ("Kappa", endedAt, Now.AddDays(-1)));
        var older = await SeedWishAsync(channel, emote, WishStatus.RemovedExternally, w => (w.Alias, w.EndedAtUtc, w.CreatedAtUtc) = ("Kappa", endedAt, Now.AddDays(-2)));
        var slot = await SeedSlotAsync(emote, "Kappa", Now);

        await ReconcileAsync(channel);

        Assert.Equal((WishStatus.Active, slot), ((await WishAsync(older)).Status, (await WishAsync(older)).SlotId));
        Assert.Equal(WishStatus.RemovedExternally, (await WishAsync(younger)).Status);
    }

    // The pre-filter: a new wish of the emote runs, so the old one stays; the rest of the pass commits.
    [Fact]
    public async Task Return_WhileAnotherWishOfTheEmoteRuns_StaysRemoved_AndTheOtherTransitionsCommit()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var other = await SeedEmoteAsync(channel);
        var old = await SeedWishAsync(channel, emote, WishStatus.RemovedExternally, w => (w.Alias, w.EndedAtUtc) = ("Kappa", Now.AddHours(-2)));
        var running = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(1));
        var independent = await SeedWishAsync(channel, other, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-1));
        await SeedSlotAsync(emote, "Kappa", Now);
        var otherSlot = await SeedSlotAsync(other, "Other", Now);

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.RemovedExternally, (await WishAsync(old)).Status);
        Assert.Equal(WishStatus.Approved, (await WishAsync(running)).Status);
        Assert.Equal((WishStatus.Active, otherSlot), ((await WishAsync(independent)).Status, (await WishAsync(independent)).SlotId));
    }

    // ------------------------------------------------------------------------------------ AK12 and re-add

    // AK12 and the T5 hand-off: re-add keeps the SlotId while the wish is approved again; the old own slot is
    // still a candidate, and the reconcile links it when it comes back after the new approval.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReAdded_RelinksItsOwnSlot_WhenItComesBackAfterTheNewApproval_KeptWhenPermanent(bool permanent)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Kappa", Now.AddDays(-40), archived: true);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Removed, w =>
        {
            w.Alias = "Kappa";
            w.SlotId = slot;
            w.EndedAtUtc = Now.AddDays(-1);
        });
        var reAdd = await WishSut().ReAddAsync(
            channel.Name, wishId, new ReAddWishRequest(permanent ? WishReAddMode.Permanent : WishReAddMode.Timed, 10), Actor);
        Assert.Equal((WishStatus.Approved, slot), (reAdd.Wish!.Status, reAdd.Wish.SlotId));

        var returnedAt = Now.AddHours(1);
        await UpdateSlotAsync(slot, s => (s.IsArchived, s.ArchivedAt, s.AddedAt) = (false, null, returnedAt));
        _clock.Advance(TimeSpan.FromHours(2));
        _publisher.ClearReceivedCalls();

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((permanent ? WishStatus.Kept : WishStatus.Active, slot), (wish.Status, wish.SlotId));
        Assert.Equal((returnedAt, returnedAt.AddDays(10)), (wish.StartsAtUtc, wish.ExpiresAtUtc));
        Assert.Equal(permanent ? Now : null, wish.EndedAtUtc);
        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ adopt hand-off (T4 M4)

    // An adopted start the manager chose stays; one the adoption took from the slot (or stood in for an
    // undated slot with "now") follows the slot's authoritative date. A null generation is a confirmation,
    // never a removal.
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task Adopted_ConfirmationMovesOnlyAStartTakenFromTheSlot(bool slotDated, bool explicitStart, bool shifts)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var provisional = Now.AddDays(-1);
        var slot = await SeedSlotAsync(emote, "Kappa", slotDated ? provisional : null, fromRest: false);
        DateTime? requested = explicitStart ? Now.AddHours(-12) : null;
        var adopted = await WishSut().AdoptAsync(channel.Name, new AdoptWishRequest(slot, null, 30, requested), Actor);
        Assert.True(adopted.IsSuccess);
        var wishId = adopted.Wish!.Id;
        var before = await WishAsync(wishId);
        Assert.False(before.StartsAtAuthoritative);

        // Before the slot has its authoritative date: nothing a client sees changes (an undated slot leaves
        // the wish entirely alone, so its stand-in start stays recognisable).
        _clock.Advance(TimeSpan.FromMinutes(40));
        _publisher.ClearReceivedCalls();
        await ReconcileAsync(channel);
        AssertNothingPublished();
        if (!slotDated)
        {
            Assert.Equal(SnapshotOf(before), await SnapshotAsync(wishId));
        }

        var confirmed = Now.AddDays(-3);
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (confirmed, true));
        _clock.Advance(TimeSpan.FromHours(1));
        _publisher.ClearReceivedCalls();
        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot, (DateTime?)null), (wish.Status, wish.SlotId, wish.EndedAtUtc));
        Assert.Equal((true, confirmed), (wish.StartsAtAuthoritative, wish.SlotAddedAtUtc));
        if (shifts)
        {
            Assert.Equal((confirmed, confirmed.AddDays(30)), (wish.StartsAtUtc, wish.ExpiresAtUtc));
        }
        else
        {
            Assert.Equal((before.StartsAtUtc, before.ExpiresAtUtc), (wish.StartsAtUtc, wish.ExpiresAtUtc));
        }

        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ K4 exceptions

    [Fact]
    public async Task BotWrittenApprovedWish_IsNotMatched_AndAnActiveWishWithoutSlot_GetsOneWithoutOtherChanges()
    {
        var channel = await SeedChannelAsync();
        var botEmote = await SeedEmoteAsync(channel);
        var ownEmote = await SeedEmoteAsync(channel);
        var botWritten = await SeedWishAsync(channel, botEmote, WishStatus.Approved, w =>
        {
            w.ApprovedAtUtc = Now.AddHours(-1);
            w.BotWriteAtUtc = Now.AddMinutes(-50);
        });
        var start = Now.AddMinutes(-40);
        var withoutSlot = await SeedWishAsync(channel, ownEmote, WishStatus.Active, w =>
        {
            w.ApprovedAtUtc = Now.AddHours(-1);
            w.StartsAtUtc = start;
            w.ExpiresAtUtc = start.AddDays(30);
            w.BotOwned = true;
            w.LinkOrigin = WishLinkOrigin.Bot;
            w.FirstActivatedAtUtc = start;
        });
        await SeedSlotAsync(botEmote, "Alias", Now.AddMinutes(-45));
        var ownSlot = await SeedSlotAsync(ownEmote, "Alias", Now.AddMinutes(-30));

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Approved, (await WishAsync(botWritten)).Status);
        var wish = await WishAsync(withoutSlot);
        Assert.Equal((WishStatus.Active, ownSlot, true, WishLinkOrigin.Bot), (wish.Status, wish.SlotId, wish.BotOwned, wish.LinkOrigin));
        Assert.Equal((start, start.AddDays(30), Now.AddMinutes(-30)), (wish.StartsAtUtc, wish.ExpiresAtUtc, wish.SlotAddedAtUtc));
    }

    // ------------------------------------------------------------------------------------ idempotence, cost, events

    [Fact]
    public async Task SecondRun_ChangesNothing_AndARefreshOfTheConfirmationIsNoVisibleChange()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-1));
        await SeedSlotAsync(emote, "Alias", Now.AddMinutes(-10));
        await ReconcileAsync(channel);
        _publisher.ClearReceivedCalls();
        var linked = await SnapshotAsync(wishId);

        _clock.Advance(TimeSpan.FromMinutes(10));
        await ReconcileAsync(channel);
        Assert.Equal(linked, await SnapshotAsync(wishId));

        _clock.Advance(TimeSpan.FromMinutes(25));
        await ReconcileAsync(channel);
        var refreshed = await WishAsync(wishId);
        Assert.Equal(Now, refreshed.SlotConfirmedAtUtc);
        AssertNothingPublished();
    }

    [Fact]
    public async Task ChannelWithoutRunningWishes_CostsExactlyOneQuery_EvenWithOldExternalRemovalsWithoutACandidate()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var other = await SeedEmoteAsync(channel);
        await SeedWishAsync(channel, emote, WishStatus.Removed);
        await SeedWishAsync(channel, other, WishStatus.Closed);
        var endedAt = Now.AddDays(-30);
        await SeedWishAsync(channel, emote, WishStatus.RemovedExternally, w => (w.Alias, w.EndedAtUtc) = ("Kappa", endedAt));
        await SeedWishAsync(channel, other, WishStatus.RemovedExternally, w => (w.Alias, w.EndedAtUtc) = ("Kappa", endedAt));
        // Same alias, but outside the window; in the window, but another alias; in the window, but archived.
        await SeedSlotAsync(emote, "Kappa", endedAt.AddHours(7));
        await SeedSlotAsync(other, "Other", endedAt.AddHours(1));
        await SeedSlotAsync(other, "Kappa", endedAt.AddHours(1), archived: true);
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);

        await ReconcileAsync(channel, hook);

        Assert.Equal(1, hook.Count(_ => true));
        AssertNothingPublished();
    }

    [Fact]
    public async Task UnknownChannel_IsNoError()
    {
        await Sut().ReconcileChannelAsync(Guid.NewGuid().ToString());

        AssertNothingPublished();
    }

    // ------------------------------------------------------------------------------------ helpers

    private WishReconcileService Sut(Func<AppDbContext>? contextFactory = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => (contextFactory ?? fixture.CreateDbContext)());
        var provider = services.BuildServiceProvider();
        return new WishReconcileService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            _logger);
    }

    private Task ReconcileAsync(Seeded channel) => Sut().ReconcileChannelAsync(channel.Id);

    private Task ReconcileAsync(Seeded channel, CommandHook hook) =>
        Sut(() => fixture.CreateTaggedDbContext($"wish-reconcile-{Guid.NewGuid():N}"[..40], [hook])).ReconcileChannelAsync(channel.Id);

    private WishService WishSut(AppDbContext? db = null) =>
        new(db ?? fixture.CreateDbContext(), new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance), _clock);

    private void AssertPublishedOnce(Seeded channel)
    {
        var calls = _publisher.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync)).ToList();
        var call = Assert.Single(calls);
        Assert.Equal(LiveEvents.Channel, call.GetArguments()[0]);
        Assert.Equal(new LiveEvent(LiveEvents.WishChanged, channel.Name), LiveEvent.TryParse((string?)call.GetArguments()[1]));
    }

    private void AssertNothingPublished() =>
        Assert.DoesNotContain(_publisher.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync));

    private async Task<Wish> WishAsync(long id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private async Task<string> SnapshotAsync(long id) => SnapshotOf(await WishAsync(id));

    // Every column, for "nothing changed" checks.
    private static string SnapshotOf(Wish w) => string.Join(
        "|",
        w.Status, w.StatusReason, w.StatusChangedAtUtc.Ticks, w.Alias, w.DurationDays, w.ApprovedAtUtc?.Ticks, w.StartsAtUtc?.Ticks,
        w.StartsAtAuthoritative, w.ExpiresAtUtc?.Ticks, w.EndedAtUtc?.Ticks, w.SlotId, w.ExtensionCount, w.Permanent, w.BotOwned,
        w.BotOwnedAddedAtUtc?.Ticks, w.RemovalQueuedAtUtc?.Ticks, w.RemovalQueuedVersion, w.DecisionVersion, w.LinkOrigin,
        w.SlotAddedAtUtc?.Ticks, w.SlotConfirmedAtUtc?.Ticks, w.FirstActivatedAtUtc?.Ticks);

    private async Task<Seeded> SeedChannelAsync()
    {
        var name = $"r{Guid.NewGuid():N}"[..18];
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, TwitchChannelId = Guid.NewGuid().ToString("N")[..12] };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return new Seeded(channel.Id, name);
    }

    private async Task<Emote> SeedEmoteAsync(Seeded channel)
    {
        await using var db = fixture.CreateDbContext();
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "Emote",
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private async Task<string> SeedSlotAsync(
        Emote emote, string alias, DateTime? addedAt, bool fromRest = true, bool archived = false, bool seed = false)
    {
        await using var db = fixture.CreateDbContext();
        var slot = new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = alias,
            AddedAt = addedAt,
            AddedAtFromRest = fromRest && addedAt is not null,
            IsArchived = archived,
            ArchivedAt = archived ? Now : null,
            SeedUnsettled = seed
        };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private async Task UpdateSlotAsync(string slotId, Action<EmoteSlot> change)
    {
        await using var db = fixture.CreateDbContext();
        var slot = await db.EmoteSlots.SingleAsync(s => s.Id == slotId);
        change(slot);
        await db.SaveChangesAsync();
    }

    private async Task<long> SeedWishAsync(Seeded channel, Emote emote, string status, Action<Wish>? configure = null) =>
        await SeedWishAsync(channel, emote.SevenTvEmoteId, status, configure);

    private async Task<long> SeedWishAsync(Seeded channel, string sevenTvEmoteId, string status, Action<Wish>? configure = null)
    {
        await using var db = fixture.CreateDbContext();
        var wish = new Wish
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = sevenTvEmoteId,
            EmoteName = "Emote",
            Alias = "Alias",
            Source = WishSource.Manual,
            Status = status,
            StatusChangedAtUtc = Now,
            CreatedAtUtc = Now.AddDays(-1),
            ApprovedAtUtc = Now.AddDays(-1),
            EndedAtUtc = WishStatus.IsEnded(status) ? Now : null
        };
        configure?.Invoke(wish);
        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    // An active wish linked to the slot in the generation addedAt, last confirmed now, on a 30-day run.
    private Task<long> SeedLinkedAsync(
        Seeded channel, Emote emote, string slotId, DateTime addedAt, bool authoritative = true, string alias = "Alias",
        Action<Wish>? configure = null) =>
        SeedWishAsync(channel, emote, WishStatus.Active, w =>
        {
            w.Alias = alias;
            w.SlotId = slotId;
            w.LinkOrigin = WishLinkOrigin.Auto;
            w.StartsAtUtc = addedAt;
            w.StartsAtAuthoritative = authoritative;
            w.ExpiresAtUtc = addedAt.AddDays(30);
            w.SlotAddedAtUtc = addedAt;
            w.SlotConfirmedAtUtc = Now;
            w.FirstActivatedAtUtc = Now;
            configure?.Invoke(w);
        });

    private sealed record Seeded(string Id, string Name);
}
