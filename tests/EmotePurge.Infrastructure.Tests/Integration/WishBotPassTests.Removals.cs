using System.Globalization;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Step 5 of the bot pass, removals (#487 plan 2.7 with 2.5a, spec 8; T10), against real Postgres with the stubbed write
// client of WishBotPassTests.Adds.cs: "7TV" keeps a set per id (_sets); removeEmote takes the entry of emote and alias out
// unless the test says otherwise. The values of the live probe T0: P4 (a rename keeps addedAt and addedById) and P5 (a
// human re-add carries the owner's addedById and a new addedAt).
public partial class WishBotPassTests
{
    private const string Owner = "01KYJM4NCAXW8FASM2KWG38SBT";

    private readonly List<RemoveCall> _removeCalls = [];
    private readonly List<bool> _readDeadlines = [];
    private Func<RemoveCall, SevenTvWriteResult>? _removeAnswer;
    private Func<Task>? _whileRemoving;
    private Func<int, string, Func<TimeSpan>?, SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>?>? _readStub;

    // ------------------------------------------------------------------------------------ AK84: the way through

    // AK84 from the route on: presented (protected) with ownership, bot-remove at the wish service (202 with the audit of
    // the manager), the next pass sends exactly one removeEmote with emote id and alias, MarkWriteSent right before it:
    // removed, wish.botRemove (ids only), attempt, evidence and queue empty, one decision counted by the route and one by
    // the removal. Three reads: before the attempt, right before the send (with the budget), after it (without).
    [Fact]
    public async Task BotRemoveFromTheRoute_SendsExactlyOneRemoveEmote_AndTheWishIsRemoved()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented, queued: false);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        await using (var db = CreateDb())
        {
            Assert.Equal(WishOutcome.Success, (await WishSut(db).RequestBotRemovalAsync(channel.Name, id, Manager)).Outcome);
        }

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Removed, null), StatusOf(wish));
        Assert.Equal(Now, wish.EndedAtUtc);
        Assert.Equal(SeededVersion + 2, wish.DecisionVersion);
        AssertRemovalCleared(wish);
        Assert.False(wish.BotOwned);
        var remove = Assert.Single(_removeCalls);
        Assert.Equal((SetA, emote, "Alias", "sent"), (remove.SetId, remove.EmoteId, remove.Alias, remove.LastSessionCall));
        Assert.Equal(new[] { SetA, SetA, SetA }, _readCalls);
        Assert.Equal(new[] { true, true, false }, _readDeadlines);
        Assert.Empty(Set(SetA));
        var audit = Assert.Single(await AuditsAsync(AuditActions.WishBotRemove));
        Assert.Equal(new Dictionary<string, string> { ["wishId"] = Id(id), ["sevenTvEmoteId"] = emote }, audit.Details);
        Assert.Equal(AuditActor.System.TwitchUserId, audit.Actor);
        Assert.Equal(Manager.TwitchUserId, Assert.Single(await AuditsAsync(AuditActions.WishBotRemoveRequest)).Actor);
    }

    // An active wish past its expiry, queued by the expiry run: removed, wish.botRemove with the overdue expiry (K2 (e)).
    [Fact]
    public async Task AnExpiredActiveWish_IsRemoved_WithThePreviousExpiryInTheAudit()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var expiry = (await LoadAsync(id)).ExpiresAtUtc!.Value;

        await RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        var details = Assert.Single(await AuditsAsync(AuditActions.WishBotRemove)).Details;
        Assert.Equal(expiry, DateTime.Parse(details["previousExpiresAtUtc"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
        Assert.Equal(1, EventsFor(channel));
    }

    // Spec 8: a second alias of the same emote stays in the set; no repair.
    [Fact]
    public async Task ASecondAliasOfTheSameEmote_StaysInTheSet()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));

        await RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Equal("Alias2", Assert.Single(Set(SetA)).Alias);
        Assert.Empty(_addCalls);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotRepair));
    }

    // ------------------------------------------------------------------------------------ AK77: one feature off before the send

    // Transaction 1's read decides: the alias renamed by a human (P4: addedAt and addedById stay), a human remove and re-add
    // under the same alias (P5: the owner, a new addedAt), only addedById, only addedAt → ownership_lost; the entry gone →
    // removed_externally. Never a removeEmote, never an attempt; ownership, set and queue end, one decision counted.
    [Theory]
    [InlineData("rename", WishStatus.Presented, WishStatusReason.OwnershipLost)]
    [InlineData("readd", WishStatus.Presented, WishStatusReason.OwnershipLost)]
    [InlineData("addedBy", WishStatus.Presented, WishStatusReason.OwnershipLost)]
    [InlineData("addedAt", WishStatus.Presented, WishStatusReason.OwnershipLost)]
    [InlineData("gone", WishStatus.RemovedExternally, null)]
    public async Task OneFeatureOffInTheRead_SendsNothing_AndEndsOwnership(string off, string status, string? reason)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var seeded = await LoadAsync(id);
        var entries = Set(SetA);
        var bot = entries[0];
        entries.Clear();
        switch (off)
        {
            case "rename":
                entries.Add(bot with { Alias = "AliasX" });
                break;
            case "readd":
                entries.Add(bot with { AddedAtUtc = Now.AddMinutes(-1), AddedById = Owner });
                break;
            case "addedBy":
                entries.Add(bot with { AddedById = Owner });
                break;
            case "addedAt":
                entries.Add(bot with { AddedAtUtc = bot.AddedAtUtc!.Value.AddMilliseconds(1) });
                break;
        }

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((status, reason), StatusOf(wish));
        Assert.Equal((false, (string?)null, (DateTime?)null), (wish.BotOwned, wish.BotSetId, wish.BotOwnedAddedAtUtc));
        Assert.Equal(seeded.DecisionVersion + 1, wish.DecisionVersion);
        AssertRemovalCleared(wish);
        Assert.Empty(_removeCalls);
        Assert.Single(_readCalls);
        Assert.Equal(status == WishStatus.RemovedExternally ? Now : null, wish.EndedAtUtc);
    }

    // 2.5a (d): the active set is no longer BotSetId: no read, no send, ownership_lost.
    [Fact]
    public async Task AnActiveSetOtherThanTheBotSet_SendsNothing_OwnershipLost()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented);
        await using (var db = CreateDb())
        {
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ActiveEmoteSetId, SetB));
        }

        await RunPassAsync();

        Assert.Equal((WishStatus.Presented, WishStatusReason.OwnershipLost), StatusOf(await LoadAsync(id)));
        Assert.Empty(_removeCalls);
        Assert.Empty(_readCalls);
    }

    // ------------------------------------------------------------------------------------ eligibility (spec 8, E25, AK78)

    // Not eligible behind the lock: the queue bound to an older version, no ownership (AK78), a provisional start. The
    // queue is cleared without counting a decision; nothing is read or sent.
    [Theory]
    [InlineData("version")]
    [InlineData("owned")]
    [InlineData("start")]
    public async Task NotEligible_ClearsTheQueue_WithoutADecision_AndSendsNothing(string reason)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented, configure: w =>
        {
            switch (reason)
            {
                case "version":
                    w.RemovalQueuedVersion = SeededVersion - 1;
                    break;
                case "owned":
                    w.BotOwned = false;
                    break;
                default:
                    w.StartsAtAuthoritative = false;
                    break;
            }
        });

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Protected, SeededVersion), (wish.Status, wish.StatusReason, wish.DecisionVersion));
        Assert.Equal(((DateTime?)null, (int?)null), (wish.RemovalQueuedAtUtc, wish.RemovalQueuedVersion));
        Assert.Empty(_removeCalls);
        Assert.Empty(_readCalls);
        Assert.Equal(1, EventsFor(channel));
    }

    // AK78 and plan-484 finding 11: the expiry run queued an owned wish (one wish.due); the ownership is gone before the
    // removal: transaction 1 clears the queue without counting, the expiry run decides again (presented, not_bot_owned)
    // without a second wish.due.
    [Fact]
    public async Task AClearedQueue_TheExpiryRunDecidesAgain_WithExactlyOneWishDue()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active, queued: false);
        await RunExpiryAsync();
        Assert.NotNull((await LoadAsync(id)).RemovalQueuedAtUtc);
        await using (var db = CreateDb())
        {
            await db.Wishes.Where(w => w.Id == id).ExecuteUpdateAsync(u => u.SetProperty(w => w.BotOwned, false));
        }

        await RunPassAsync();
        Assert.Null((await LoadAsync(id)).RemovalQueuedAtUtc);
        await RunExpiryAsync();

        Assert.Equal((WishStatus.Presented, WishStatusReason.NotBotOwned), StatusOf(await LoadAsync(id)));
        Assert.Single(await AuditsAsync(AuditActions.WishDue));
        Assert.Empty(_removeCalls);
    }

    // AK83: extend, keep, a PATCH of the duration (409 on a running wish) and switching the bot mode off after the queueing:
    // no removeEmote, the entry stays. A PATCH on a queued wish is a conflict and leaves the queue as it is ([W19]).
    [Theory]
    [InlineData("extend")]
    [InlineData("keep")]
    [InlineData("deactivate")]
    public async Task ADecisionAfterTheQueueing_DropsTheQueue_AndNothingIsRemoved(string command)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented);
        await using (var db = CreateDb())
        {
            var service = WishSut(db);
            Assert.Equal(WishOutcome.StateConflict, (await service.UpdateAsync(channel.Name, id, new UpdateWishRequest(null, 14), Manager)).Outcome);
            Assert.NotNull((await LoadAsync(id)).RemovalQueuedAtUtc);
            switch (command)
            {
                case "extend":
                    Assert.Equal(WishOutcome.Success, (await service.ExtendAsync(channel.Name, id, Manager)).Outcome);
                    break;
                case "keep":
                    Assert.Equal(WishOutcome.Success, (await service.KeepAsync(channel.Name, id, Manager)).Outcome);
                    break;
                default:
                    await DeleteBotAsync(channel);
                    break;
            }
        }

        await RunPassAsync();

        Assert.Null((await LoadAsync(id)).RemovalQueuedAtUtc);
        Assert.Empty(_removeCalls);
        Assert.Single(Set(SetA));
    }

    // K6b-17: a queued wish is selected, gets keep and is deleted (K7a) before transaction 1 locks it: skipped without an
    // error, the next queued wish is removed.
    [Fact]
    public async Task AQueuedWishDeletedBeforeItsLock_IsSkipped_AndTheNextOneIsRemoved()
    {
        var channel = await SeedBotChannelAsync();
        var deleted = await SeedOwnedAsync(channel, WishStatus.Presented, queuedMinutesAgo: 10);
        var next = await SeedOwnedAsync(channel, WishStatus.Presented, queuedMinutesAgo: 5);
        var hook = new CommandHook(IsWishLock, async occurrence =>
        {
            if (occurrence == 1)
            {
                await using var other = CreateDb();
                await other.Wishes.Where(w => w.Id == deleted).ExecuteUpdateAsync(u => u
                    .SetProperty(w => w.Status, WishStatus.Kept)
                    .SetProperty(w => w.RemovalQueuedAtUtc, (DateTime?)null)
                    .SetProperty(w => w.EndedAtUtc, Now));
                await other.Wishes.Where(w => w.Id == deleted).ExecuteDeleteAsync();
            }
        });
        await using var db = fixture.CreateTaggedDbContext("wish-bot-removal-k6b17", [hook], databaseName: DatabaseName);

        await RunPassAsync(db: db);

        Assert.Null(await FindAsync(deleted));
        Assert.Equal(WishStatus.Removed, (await LoadAsync(next)).Status);
        Assert.Single(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ repair (R2-4) and detection limit

    // removeEmote took another entry of the emote with it: presented (repair_review) with the phase, then exactly one
    // addEmote under the missing alias into the evidence's set, wish.botRepair outcome ok, phase and evidence empty.
    [Fact]
    public async Task AnotherEntryGoneWithIt_IsRepairedOnce_AndPresentedForReview()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));
        _removeAnswer = call =>
        {
            Set(call.SetId).RemoveAll(e => e.EmoteId == call.EmoteId);
            return SevenTvWriteResult.Ok();
        };

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Presented, WishStatusReason.RepairReview), StatusOf(wish));
        Assert.Equal(((DateTime?)null, (string?)null, (DateTime?)null), (wish.BotRepairAtUtc, wish.BotRemoveEvidence, wish.RemovalQueuedAtUtc));
        Assert.False(wish.BotOwned);
        var add = Assert.Single(_addCalls);
        Assert.Equal((SetA, emote, "Alias2", "sent"), (add.SetId, add.EmoteId, add.Alias, add.LastSessionCall));
        var repair = Assert.Single(await AuditsAsync(AuditActions.WishBotRepair));
        Assert.Equal("ok", repair.Details["outcome"]);
        Assert.DoesNotContain("Alias2", JsonSerializer.Serialize(repair.Details), StringComparison.Ordinal);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotRemove));
    }

    // The repair's answer: Rejected is definite (booked, phase empty). Timeout is unclear: the phase stays, and the next
    // pass resolves it read only: the alias visible after all → phase empty without an audit; still missing → needs_review,
    // never a second addEmote.
    [Theory]
    [InlineData("rejected", null)]
    [InlineData("timeout", true)]
    [InlineData("timeout", false)]
    public async Task TheRepairsAnswer_DefiniteIsBooked_UnclearIsResolvedReadOnly(string answer, bool? visibleLater)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));
        _removeAnswer = call =>
        {
            Set(call.SetId).RemoveAll(e => e.EmoteId == call.EmoteId);
            return SevenTvWriteResult.Ok();
        };
        _addAnswer = _ => answer == "rejected"
            ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Rejected(SevenTvWriteRejection.AliasConflict))
            : SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Timeout());

        await RunPassAsync();

        var wish = await LoadAsync(id);
        if (answer == "rejected")
        {
            Assert.Null(wish.BotRepairAtUtc);
            Assert.Equal("rejected", Assert.Single(await AuditsAsync(AuditActions.WishBotRepair)).Details["outcome"]);
            return;
        }

        Assert.NotNull(wish.BotRepairAtUtc);
        Assert.NotNull(wish.BotRemoveEvidence);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotRepair));
        if (visibleLater == true)
        {
            Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now, BotA));
        }

        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        wish = await LoadAsync(id);
        Assert.Equal(((DateTime?)null, (string?)null), (wish.BotRepairAtUtc, wish.BotRemoveEvidence));
        Assert.Single(_addCalls);
        var audits = await AuditsAsync(AuditActions.WishBotRepair);
        if (visibleLater == true)
        {
            Assert.Empty(audits);
        }
        else
        {
            Assert.Equal("needs_review", Assert.Single(audits).Details["outcome"]);
        }
    }

    // The detection limit (spec 8, AK77): a human replaces the bot's entry by one of the same alias between the fresh read
    // and removeEmote; the bot removes the replacement, the read after it sees nothing to repair (no wish.botRepair).
    // A replacement a sync had seen as a live slot gives exactly one wish.botSuspectRemoval (wish, slot); one never seen
    // gives none.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReplacementBetweenTheReadAndTheSend_IsRemoved_AndOnlyASeenOneIsSuspected(bool seenBySync)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        var replacedAt = Now.AddSeconds(-1);
        _whileRemoving = () =>
        {
            Set(SetA).Clear();
            Set(SetA).Add(new SevenTvSetEntry(emote, "Alias", replacedAt, Owner));
            return Task.CompletedTask;
        };
        string? slotId = null;
        if (seenBySync)
        {
            slotId = await SeedSlotAsync(channel, emote, "Alias", replacedAt);
        }

        await RunPassAsync();

        Assert.Equal(WishStatus.Removed, (await LoadAsync(id)).Status);
        Assert.Single(_removeCalls);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotRepair));
        var suspects = await AuditsAsync(AuditActions.WishBotSuspectRemoval);
        if (seenBySync)
        {
            Assert.Equal(new Dictionary<string, string> { ["wishId"] = Id(id), ["slotId"] = slotId! }, Assert.Single(suspects).Details);
        }
        else
        {
            Assert.Empty(suspects);
        }
    }

    // ------------------------------------------------------------------------------------ resumption (2.5a (e))

    // removeEmote Timeout and the read after it fails: the attempt stays. The next pass reads the evidence's set: the bot's
    // entry and the second alias gone → removed with outcome resumed, the phase read only (no addEmote); the pass after it
    // resolves the phase with one needs_review. The bot's entry still there → the attempt is cleared and the queue stays.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnclearRemoval_IsResumedByARead_NeverSentAgain(bool tookEffect)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));
        _removeAnswer = call =>
        {
            if (tookEffect)
            {
                Set(call.SetId).RemoveAll(e => e.EmoteId == call.EmoteId);
            }

            return SevenTvWriteResult.Timeout();
        };
        _readStub = (number, _, _) => number == 3 ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable()) : null;

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.NotNull(wish.BotRemoveAtUtc);
        Assert.NotNull(wish.BotRemoveEvidence);
        Assert.Equal(WishStatus.Active, wish.Status);

        _readStub = null;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        wish = await LoadAsync(id);
        if (!tookEffect)
        {
            Assert.Equal(WishStatus.Active, wish.Status);
            Assert.Null(wish.BotRemoveAtUtc);
            Assert.Null(wish.BotRemoveEvidence);
            Assert.NotNull(wish.RemovalQueuedAtUtc);
            Assert.Single(_removeCalls);
            return;
        }

        Assert.Equal(WishStatus.Removed, wish.Status);
        Assert.Equal("resumed", Assert.Single(await AuditsAsync(AuditActions.WishBotRemove)).Details["outcome"]);
        Assert.NotNull(wish.BotRepairAtUtc);
        Assert.Single(_removeCalls);
        Assert.Empty(_addCalls);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        wish = await LoadAsync(id);
        Assert.Equal(((DateTime?)null, (string?)null), (wish.BotRepairAtUtc, wish.BotRemoveEvidence));
        Assert.Equal("needs_review", Assert.Single(await AuditsAsync(AuditActions.WishBotRepair)).Details["outcome"]);
        Assert.Empty(_addCalls);
    }

    // 15 minutes without a read (7TV down): the deadline step presents it (bot_unavailable), attempt and queue empty, the
    // evidence kept as a repair phase (Sol-P2-5). Kill switch with the phase open: it stays; after the kill switch it is
    // resolved read only, never an addEmote.
    [Fact]
    public async Task AnAttemptWithoutARead_IsPresentedAfter15Minutes_ThePhaseSurvivesTheKillSwitch()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Active);
        _removeAnswer = _ => SevenTvWriteResult.Unavailable();
        _readStub = (number, _, _) => number >= 3 ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable()) : null;
        await RunPassAsync();
        Assert.NotNull((await LoadAsync(id)).BotRemoveAtUtc);

        _clock.Advance(TimeSpan.FromMinutes(15));
        await RunDeadlineStepAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(wish));
        Assert.Equal(((DateTime?)null, (DateTime?)null), (wish.BotRemoveAtUtc, wish.RemovalQueuedAtUtc));
        Assert.Equal((Now, true), (wish.BotRepairAtUtc!.Value, wish.BotRemoveEvidence is not null));
        Assert.Equal(1, EventsAfterDeadline(channel));

        await KillSwitchAsync(true);
        _readStub = null;
        await RunPassAsync(session: await SessionAsync());
        Assert.NotNull((await LoadAsync(id)).BotRepairAtUtc);

        await KillSwitchAsync(false);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        wish = await LoadAsync(id);
        Assert.Null(wish.BotRepairAtUtc);
        Assert.Empty(_addCalls);
        Assert.Single(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ the repair phase (2.5a (h))

    // K6b-12: removed with an open repair phase: the hard delete answers 409 wish_repair_check_open; the pass resolves the
    // phase read only (needs_review, or empty when nothing of the evidence is missing); then the delete succeeds.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARemovedWishWithAnOpenPhase_CannotBeDeleted_UntilTheReadResolvesIt(bool otherMissing)
    {
        var channel = await SeedBotChannelAsync();
        var (id, emote) = await SeedPhaseAsync(channel, WishStatus.Removed, SetA);
        if (!otherMissing)
        {
            Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));
        }

        var ended = (await LoadAsync(id)).EndedAtUtc!.Value;
        Assert.Equal(WishOutcome.RepairCheckOpen, await DeleteWishAsync(channel, id, ended));

        await RunPassAsync();

        Assert.Null((await LoadAsync(id)).BotRepairAtUtc);
        Assert.Equal(otherMissing ? 1 : 0, (await AuditsAsync(AuditActions.WishBotRepair)).Count);
        Assert.Equal(WishOutcome.Success, await DeleteWishAsync(channel, id, ended));
        Assert.Empty(_addCalls);
    }

    // A new bot-remove of the same wish with an open phase is committed only after the phase is resolved: while its read
    // fails, nothing is sent; once it is resolved (earlier in the same pass), the removal runs.
    [Fact]
    public async Task ANewRemovalWithAnOpenPhase_WaitsForThePhase()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented, configure: w =>
        {
            w.BotRepairAtUtc = Now.AddHours(-1);
            w.BotRemoveEvidence = EvidenceJson(SetA, w.SevenTvEmoteId, "Old", Now.AddDays(-30), []);
        });
        _readStub = (_, _, _) => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());

        await RunPassAsync();

        Assert.Single(_readCalls);
        Assert.Empty(_removeCalls);
        Assert.NotNull((await LoadAsync(id)).RemovalQueuedAtUtc);

        _readStub = null;
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Removed, (DateTime?)null), (wish.Status, wish.BotRepairAtUtc));
        Assert.Single(_removeCalls);
    }

    // K6c-2, K6d-1, one connected case: the removal in S1 answers Timeout and the read after it fails; DELETE …/wishes/bot
    // resolves the attempt (BotSetId empty, evidence with S1, phase), the full sync moves the active set to S2, a manager
    // closes the presented wish, the hard delete answers 409. The read goes to S1 only (no read of S2), the phase is
    // empty or needs_review per S1, and the delete answers 204 afterwards. Bound to the phase's stamp alone: the
    // decision version DELETE raised does not keep it from booking.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APhaseAfterDeleteAndASetChange_ReadsTheEvidenceSetOnly(bool removedInS1)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedOwnedAsync(channel, WishStatus.Presented);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias2", Now.AddDays(-20), Owner));
        _removeAnswer = call =>
        {
            if (removedInS1)
            {
                Set(call.SetId).RemoveAll(e => e.EmoteId == call.EmoteId);
            }

            return SevenTvWriteResult.Timeout();
        };
        _readStub = (number, _, _) => number == 3 ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable()) : null;
        await RunPassAsync();

        await DeleteBotAsync(channel);
        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable, (string?)null), (wish.Status, wish.StatusReason, wish.BotSetId));
        Assert.Equal(SetA, WishBotRemovalEvidence.TryParse(wish.BotRemoveEvidence)!.SetId);
        await using (var db = CreateDb())
        {
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ActiveEmoteSetId, SetB));
            Assert.Equal(WishOutcome.Success, (await WishSut(db).CloseAsync(channel.Name, id, Manager)).Outcome);
        }

        var ended = (await LoadAsync(id)).EndedAtUtc!.Value;
        Assert.Equal(WishOutcome.RepairCheckOpen, await DeleteWishAsync(channel, id, ended));
        _readCalls.Clear();
        _readStub = null;
        _clock.Advance(TimeSpan.FromSeconds(30));

        await RunPassAsync();

        Assert.Equal(new[] { SetA }, _readCalls);
        Assert.Null((await LoadAsync(id)).BotRepairAtUtc);
        Assert.Equal(removedInS1 ? 1 : 0, (await AuditsAsync(AuditActions.WishBotRepair)).Count);
        Assert.Equal(WishOutcome.Success, await DeleteWishAsync(channel, id, ended));
    }

    // K6c-2, K6d-1 with re-add and retry instead of DELETE: they clear BotSetId and the attempt's fields and raise the
    // decision version, the phase stays and its read goes to the evidence's set.
    [Theory]
    [InlineData("readd")]
    [InlineData("retry")]
    public async Task APhaseAfterReAddOrRetry_IsBookedOnItsStamp_ReadingTheEvidenceSet(string command)
    {
        var channel = await SeedBotChannelAsync(set: SetB);
        var (id, _) = await SeedPhaseAsync(channel, command == "readd" ? WishStatus.Removed : WishStatus.Failed, SetA);
        await using (var db = CreateDb())
        {
            var service = WishSut(db);
            var outcome = command == "readd"
                ? (await service.ReAddAsync(channel.Name, id, new ReAddWishRequest(WishReAddMode.Timed, null), Manager)).Outcome
                : (await service.RetryAsync(channel.Name, id, Manager)).Outcome;
            Assert.Equal(WishOutcome.Success, outcome);
        }

        var before = await LoadAsync(id);
        Assert.Equal(((string?)null, true), (before.BotSetId, before.BotRepairAtUtc is not null));
        _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());

        await RunPassAsync();

        Assert.Contains(SetA, _readCalls);
        Assert.Null((await LoadAsync(id)).BotRepairAtUtc);
        Assert.Equal("needs_review", Assert.Single(await AuditsAsync(AuditActions.WishBotRepair)).Details["outcome"]);
    }

    // K6d-1: two passes read the same phase; the first books, the second hits nothing (no second needs_review).
    [Fact]
    public async Task TwoPassesReadingThePhase_OnlyTheFirstBooks()
    {
        var channel = await SeedBotChannelAsync();
        var (id, _) = await SeedPhaseAsync(channel, WishStatus.Removed, SetA);
        _whileReading = async number =>
        {
            if (number == 1)
            {
                _whileReading = null;
                await using var second = CreateDb();
                var session = await SessionAsync();
                session.Db = second;
                await Pass(second).RunAsync(session);
            }
        };

        await RunPassAsync(session: await SessionAsync());

        Assert.Null((await LoadAsync(id)).BotRepairAtUtc);
        Assert.Single(await AuditsAsync(AuditActions.WishBotRepair));
    }

    // ------------------------------------------------------------------------------------ Bot mode off: unchanged

    // The deadline step and the queue touch nothing of a manual channel: an expired active wish, a presented one with an
    // old queue left from the bot and an ended one keep every field (the shared booking path and the new candidates).
    [Fact]
    public async Task BotModeOff_TheRemovalPartsChangeNothing()
    {
        var channel = await SeedBotChannelAsync(botMode: false);
        var ids = new[]
        {
            await SeedWishAsync(channel, WishStatus.Active, w => w.ExpiresAtUtc = Now.AddDays(-1)),
            await SeedWishAsync(channel, WishStatus.Presented, w =>
            {
                w.StatusReason = WishStatusReason.Expired;
                Queue(w);
            }),
            await SeedWishAsync(channel, WishStatus.Removed, w => w.EndedAtUtc = Now.AddDays(-1)),
        };
        var before = await Task.WhenAll(ids.Select(SnapshotAsync));

        await RunDeadlineStepAsync();
        await RunPassAsync();

        Assert.Equal(before, await Task.WhenAll(ids.Select(SnapshotAsync)));
        Assert.Empty(_readCalls);
        Assert.Empty(_removeCalls);
    }

    // ------------------------------------------------------------------------------------ helpers (removals)

    // An owned wish whose entry the bot added three days ago (milliseconds, as 7TV reports them), in SetA, queued a minute
    // ago (or as given) at the seeded version: presented (protected) as after bot-remove, or active past its expiry as
    // after the expiry run.
    private async Task<long> SeedOwnedAsync(
        Seeded channel, string status, bool queued = true, int queuedMinutesAgo = 1, Action<Wish>? configure = null)
    {
        var added = Now.AddDays(-3).AddMilliseconds(498);
        var id = await SeedWishAsync(channel, status, w =>
        {
            w.StatusReason = status == WishStatus.Presented ? WishStatusReason.Protected : null;
            w.BotOwned = true;
            w.BotOwnedAddedAtUtc = added;
            w.BotSetId = SetA;
            w.StartsAtUtc = added;
            w.StartsAtAuthoritative = true;
            w.LinkOrigin = WishLinkOrigin.Bot;
            w.ExpiresAtUtc = status == WishStatus.Active ? Now.AddMinutes(-5) : Now.AddDays(1);
            if (queued)
            {
                w.RemovalQueuedAtUtc = Now.AddMinutes(-queuedMinutesAgo);
                w.RemovalQueuedVersion = SeededVersion;
            }

            configure?.Invoke(w);
        });
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias", added, BotA));
        return id;
    }

    // A wish with an open repair phase whose evidence lists one other entry ("Alias2"), in the given set; the set itself
    // holds neither the bot's entry nor the other one.
    private async Task<(long Id, string Emote)> SeedPhaseAsync(Seeded channel, string status, string setId)
    {
        var id = await SeedWishAsync(channel, status, w =>
        {
            w.EndedAtUtc = WishStatus.IsEnded(status) ? Now.AddHours(-1) : null;
            w.BotRepairAtUtc = Now.AddHours(-1);
            w.BotRemoveEvidence = EvidenceJson(setId, w.SevenTvEmoteId, "Alias", Now.AddDays(-3), [new WishBotRemovalOtherEntry("Alias2", Now.AddDays(-20))]);
        });
        Set(setId);
        return (id, (await LoadAsync(id)).SevenTvEmoteId);
    }

    private static string EvidenceJson(string setId, string emote, string alias, DateTime addedAt, IReadOnlyList<WishBotRemovalOtherEntry> others) =>
        new WishBotRemovalEvidence(setId, emote, alias, addedAt, BotA, others).ToJson();

    private async Task<string> SeedSlotAsync(Seeded channel, string emote, string alias, DateTime addedAt)
    {
        await using var db = CreateDb();
        var row = new Emote { SevenTvEmoteId = emote, ChannelId = channel.Id, Name = alias };
        var slot = new EmoteSlot { Name = alias, AddedAt = addedAt, AddedAtFromRest = true, Emote = row };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private async Task<WishOutcome> DeleteWishAsync(Seeded channel, long id, DateTime endedAt)
    {
        await using var db = CreateDb();
        return (await WishSut(db).DeleteAsync(channel.Name, id, endedAt, Manager, new WishCaptureAccess(channel.Id, channel.TwitchId, IdConfirmed: true))).Outcome;
    }

    private async Task RunExpiryAsync()
    {
        await using var db = CreateDb();
        var wishOptions = new WishOptions { Enabled = true, PilotChannelIds = "*" };
        var botAccountOptions = new BotAccountOptions { Enabled = true };
        var publisher = new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance);
        var fallback = new WishBotFallback(db, wishOptions, botAccountOptions, _admins, publisher, _clock, NullLogger<WishBotFallback>.Instance);
        await new WishExpiryService(
                db,
                Substitute.For<IWishReconcileService>(),
                wishOptions,
                botAccountOptions,
                _admins,
                fallback,
                publisher,
                _clock,
                NullLogger<WishExpiryService>.Instance)
            .RunAsync();
    }

    // "7TV" answers a removal: by default it takes the entry of emote and alias out of the set.
    private async Task<SevenTvWriteResult> AnswerRemoveAsync(string setId, string emoteId, string alias)
    {
        var call = new RemoveCall(setId, emoteId, alias, _lastSession?.Calls.LastOrDefault());
        _removeCalls.Add(call);
        if (_whileRemoving is { } hook)
        {
            await hook();
        }

        if (_removeAnswer is { } answer)
        {
            return answer(call);
        }

        Set(setId).RemoveAll(e => e.EmoteId == emoteId && e.Alias == alias);
        return SevenTvWriteResult.Ok();
    }

    private static void AssertRemovalCleared(Wish wish) =>
        Assert.Equal(
            ((DateTime?)null, (int?)null, (string?)null, (DateTime?)null, (DateTime?)null),
            (wish.BotRemoveAtUtc, wish.BotAttemptVersion, wish.BotRemoveEvidence, wish.RemovalQueuedAtUtc, wish.BotRepairAtUtc));

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private sealed record RemoveCall(string SetId, string EmoteId, string Alias, string? LastSessionCall);
}
