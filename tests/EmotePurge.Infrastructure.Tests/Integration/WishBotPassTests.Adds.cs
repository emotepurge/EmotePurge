using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Step 5 of the bot pass, adds and confirmations (#487 plan 2.6 with 2.5a; T9), against real Postgres with a stubbed
// write client that plays "7TV": a set per id (_sets) that an add writes into unless told otherwise, and the answers of
// add and read as the test sets them. The session is the hand-made one of WishBotPassTests.cs (its pause records whether
// it was joined to an open transaction); the real runner is used where the plan asks for two passes or a booked pause.
public partial class WishBotPassTests
{
    private const string SetA = "01SETA00000000000000000000";
    private const string SetB = "01SETB00000000000000000000";

    private readonly Dictionary<string, List<SevenTvSetEntry>> _sets = new(StringComparer.Ordinal);
    private readonly List<AddCall> _addCalls = [];
    private readonly List<string> _readCalls = [];
    private readonly Queue<SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>> _readResults = new();
    private Func<AddCall, SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>>? _addAnswer;
    private Func<Task>? _whileAdding;
    private Func<int, Task>? _whileReading;
    private string _addedBy = BotA;
    private int _eventsBeforeDeadline;

    // ------------------------------------------------------------------------------------ AK89, AK77: the add's own answer

    // The answer carries the bot's entry: bot_write and bot_confirm in the send transaction's commit, no read. Ownership
    // with the authoritative start from addedAt, the set kept, one wish.botAdd (wishId and emote id only), MarkWriteSent
    // right before the call, two decisions counted (the intent, the ownership).
    [Fact]
    public async Task AnAnswerWithTheBotsEntry_IsActiveWithOwnership_InTheSendCommit_WithoutARead()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, null), StatusOf(wish));
        Assert.True(wish.BotOwned);
        Assert.Equal((Now, Now, Now), (wish.StartsAtUtc!.Value, wish.BotOwnedAddedAtUtc!.Value, wish.BotAddAckAtUtc!.Value));
        Assert.Equal(Now.AddDays(wish.DurationDays), wish.ExpiresAtUtc);
        Assert.True(wish.StartsAtAuthoritative);
        Assert.Equal((WishLinkOrigin.Bot, SetA, (string?)null), (wish.LinkOrigin, wish.BotSetId, wish.SlotId));
        Assert.Equal(Now, wish.FirstActivatedAtUtc);
        Assert.Equal(SeededVersion + 2, wish.DecisionVersion);
        Assert.Equal(((DateTime?)null, (DateTime?)null, (int?)null), (wish.BotNextAttemptAtUtc, wish.BotConfirmDeadlineAtUtc, wish.BotAttemptVersion));
        var add = Assert.Single(_addCalls);
        Assert.Equal((SetA, emote, "Alias", "sent"), (add.SetId, add.EmoteId, add.Alias, add.LastSessionCall));
        Assert.Equal(new[] { SetA }, _readCalls);
        var audit = Assert.Single(await AuditsAsync(AuditActions.WishBotAdd));
        Assert.Equal(new Dictionary<string, string> { ["wishId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["sevenTvEmoteId"] = emote }, audit.Details);
        Assert.Equal(AuditActor.System.TwitchUserId, audit.Actor);
        Assert.Equal(1, EventsFor(channel));
    }

    // Revision 6 (AK89): a foreign addedById in the answer is active without ownership, the set cleared.
    [Fact]
    public async Task AnAnswerWithAForeignAddedById_IsActiveWithoutOwnership()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _addedBy = "01SOMEONEELSE";

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, false, (string?)null), (wish.Status, wish.BotOwned, wish.BotSetId));
        Assert.Equal(SeededVersion + 1, wish.DecisionVersion);
        Assert.Single(_readCalls);
    }

    // Revision 6a (S6-4): Ok without entries (an error only in the nested selection): BotAddAckAtUtc is set, the read at
    // 0 s in the same pass confirms with ownership.
    [Fact]
    public async Task AnOkWithoutEntries_IsAcknowledged_AndTheReadAtZeroConfirmsWithOwnership()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _addAnswer = call =>
        {
            TakeEffect(call);
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(null);
        };

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, true), (wish.Status, wish.BotOwned));
        Assert.Equal(Now, wish.BotAddAckAtUtc);
        Assert.Equal(new[] { SetA, SetA }, _readCalls);
    }

    // An entry in the answer outside the generation (older than the write − 2 min) confirms nothing; the read at 0 s sees
    // the same: unconfirmed, the next read planned 30 s after the write.
    [Fact]
    public async Task AnEntryOutsideTheGeneration_IsNotConfirmed_TheNextReadIsPlanned()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _addAnswer = call =>
        {
            var entries = Set(call.SetId);
            entries.Add(new SevenTvSetEntry(call.EmoteId, call.Alias, Now.AddMinutes(-3), BotA));
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(entries.ToList());
        };

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Unconfirmed, null), StatusOf(wish));
        Assert.Equal((Now, 1, Now.AddSeconds(30), Now.AddMinutes(16)), (wish.BotAddAckAtUtc!.Value, wish.BotAttemptCount, wish.BotNextAttemptAtUtc!.Value, wish.BotConfirmDeadlineAtUtc!.Value));
        Assert.Equal(1, await CountAuditsAsync(AuditActions.WishBotAdd));
    }

    // Revision 6a (S6-4): an answer without the proof of success (stub: Unavailable, the add took effect at 7TV) is
    // unconfirmed WITHOUT BotAddAckAtUtc; the read with the bot's addedById confirms only without ownership, no second add.
    [Fact]
    public async Task AnAnswerWithoutProof_IsUnconfirmedWithoutAck_AndTheReadConfirmsWithoutOwnership()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _addAnswer = call =>
        {
            Set(call.SetId).Add(new SevenTvSetEntry(call.EmoteId, call.Alias, Now, BotA));
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
        };

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, false, (string?)null, (DateTime?)null), (wish.Status, wish.BotOwned, wish.BotSetId, wish.BotAddAckAtUtc));
        Assert.Equal(Now, wish.StartsAtUtc);
        Assert.Single(_addCalls);
        Assert.Empty(await AuditsAsync(AuditActions.WishBotAdd));
    }

    // Timeout after sending, then the reads: they find nothing (or fail every time, Sol-P2-7) at 0 s, 30 s, 2, 5 and
    // 15 minutes, no second addEmote; at 16 minutes the deadline step presents it (unconfirmed), keeping set, anchor and
    // acknowledgement for recheck.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATimeout_IsReadOnTheSchedule_NeverAddedAgain_AndPresentedAtTheDeadline(bool readsFail)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        var written = Now;
        _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Timeout());
        if (readsFail)
        {
            // From the send on, every read of the set fails.
            _whileAdding = () =>
            {
                _sets.Remove(SetA);
                return Task.CompletedTask;
            };
        }

        await RunPassAsync();
        var afterSend = await LoadAsync(id);
        Assert.Equal((WishStatus.Unconfirmed, (DateTime?)null, 1), (afterSend.Status, afterSend.BotAddAckAtUtc, afterSend.BotAttemptCount));

        foreach (var offset in new[] { 30, 120, 300, 900 })
        {
            _clock.Advance(written.AddSeconds(offset) - Now);
            await RunPassAsync();
        }

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Unconfirmed, 5, (DateTime?)null), (wish.Status, wish.BotAttemptCount, wish.BotNextAttemptAtUtc));
        Assert.Equal(6, _readCalls.Count);

        _clock.Advance(written.AddMinutes(16) - Now);
        await RunPassAsync();
        Assert.Equal(WishStatus.Unconfirmed, (await LoadAsync(id)).Status);
        await RunDeadlineStepAsync();

        wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(wish));
        Assert.Equal((SetA, written), (wish.BotSetId, wish.BotWriteAtUtc!.Value));
        Assert.Single(_addCalls);
        Assert.Equal(1, EventsAfterDeadline(channel));
    }

    // ------------------------------------------------------------------------------------ the other answers of addEmote

    // Spec F2: a rejection fails the wish with its reason, the attempt cleared, no retry in a later pass.
    [Theory]
    [InlineData(SevenTvWriteRejection.AliasConflict, WishStatusReason.AliasConflict)]
    [InlineData(SevenTvWriteRejection.Unknown, WishStatusReason.Unknown)]
    public async Task ARejection_FailsTheWishWithItsReason_AndIsNotRetried(SevenTvWriteRejection rejection, string reason)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Rejected(rejection));

        await RunPassAsync();
        _clock.Advance(TimeSpan.FromSeconds(30));
        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Failed, reason), StatusOf(wish));
        AssertAttemptCleared(wish);
        Assert.Single(_addCalls);
    }

    // AK80: LackingPrivileges keeps the wish approved (attempt cleared), suspends the channel as not_editor with one audit.
    [Fact]
    public async Task LackingPrivileges_KeepsApproved_AndSuspendsTheChannelAsNotEditor()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.LackingPrivileges());

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Approved, null), StatusOf(wish));
        AssertAttemptCleared(wish);
        Assert.Equal(WishBotChannelReasons.NotEditor, (await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Equal(new[] { (channel.Name, "reason", WishBotChannelReasons.NotEditor) }, await SuspendAuditsAsync());
    }

    // K6b-3, pass 1: Unauthorized clears the attempt, the wish stays approved, the channel is not suspended, the session
    // hears it, and the next approved is not tried (no further call with the token).
    [Fact]
    public async Task AnUnauthorizedAdd_KeepsApproved_DoesNotSuspend_AndTheNextApprovedIsNotTried()
    {
        var channel = await SeedBotChannelAsync();
        var first = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var second = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unauthorized());

        await RunPassAsync();

        Assert.Single(_addCalls);
        Assert.Contains("unauthorized", _lastSession!.Calls);
        foreach (var id in new[] { first, second })
        {
            var wish = await LoadAsync(id);
            Assert.Equal((WishStatus.Approved, null), StatusOf(wish));
            AssertAttemptCleared(wish);
        }

        Assert.Null((await SettingsAsync(channel)).BotSuspendedReason);
        Assert.Equal(SeededVersion, (await LoadAsync(second)).DecisionVersion);
    }

    // K6c-3: RateLimited clears the attempt and the pause joins the send transaction (one commit); nothing else is called
    // with the token in the pass.
    [Fact]
    public async Task ARateLimitedAdd_ClearsTheAttempt_JoinsThePauseToTheSendCommit_AndTheTokenRests()
    {
        var channel = await SeedBotChannelAsync();
        var first = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var second = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        _addAnswer = _ => SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(90)));

        await RunPassAsync();

        Assert.Single(_addCalls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(90) }, _lastSession!.Pauses);
        Assert.Equal("pause", _lastSession.Calls.Single(c => c.StartsWith("pause", StringComparison.Ordinal)));
        Assert.Equal((WishStatus.Approved, null), StatusOf(await LoadAsync(first)));
        AssertAttemptCleared(await LoadAsync(first));
        Assert.Null((await LoadAsync(second)).BotWriteAtUtc);
    }

    // K6e-4: an Ok with a Retry-After (a rate limit below the mutation field) books like Ok (unconfirmed, acknowledged,
    // wish.botAdd) and joins the pause to the same commit; then no call with the token: no read, no next add.
    [Fact]
    public async Task AnOkWithARetryAfter_BooksTheAddAndThePauseTogether_AndTheTokenRests()
    {
        var channel = await SeedBotChannelAsync();
        var first = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var second = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        _addAnswer = call =>
        {
            Set(call.SetId).Add(new SevenTvSetEntry(call.EmoteId, call.Alias, Now, BotA));
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(null, TimeSpan.FromSeconds(30));
        };

        await RunPassAsync();

        var wish = await LoadAsync(first);
        Assert.Equal((WishStatus.Unconfirmed, Now), (wish.Status, wish.BotAddAckAtUtc!.Value));
        Assert.Equal(1, await CountAuditsAsync(AuditActions.WishBotAdd));
        Assert.Equal("pause", _lastSession!.Calls.Single(c => c.StartsWith("pause", StringComparison.Ordinal)));
        Assert.Single(_addCalls);
        Assert.Single(_readCalls);
        Assert.Null((await LoadAsync(second)).BotWriteAtUtc);
    }

    // ------------------------------------------------------------------------------------ the pre-read (2.6 point 2)

    // The emote is in the attempt's set already: presented (already_in_set), the attempt cleared, nothing sent.
    [Fact]
    public async Task ThePreReadFindsTheEmote_PresentsAlreadyInSet_WithoutSending()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel);
        Set(SetA).Add(new SevenTvSetEntry((await LoadAsync(id)).SevenTvEmoteId, "SomeoneElsesAlias", Now.AddDays(-3), "01OWNER"));

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Presented, WishStatusReason.AlreadyInSet), StatusOf(wish));
        AssertAttemptCleared(wish);
        Assert.Empty(_addCalls);
        Assert.Equal(1, EventsFor(channel));
    }

    // K6d-6: a pre-read that is not possible takes the intent back (approved, one decision counted); Unauthorized is
    // reported and a 429 pauses (joined to the take-back's commit), and then the next approved is not tried; a plain
    // outage lets the next one try.
    [Theory]
    [InlineData(SevenTvWriteOutcome.Unavailable)]
    [InlineData(SevenTvWriteOutcome.Unauthorized)]
    [InlineData(SevenTvWriteOutcome.RateLimited)]
    public async Task APreReadThatFails_TakesTheIntentBack_AndReportsToTheSession(SevenTvWriteOutcome outcome)
    {
        var channel = await SeedBotChannelAsync();
        var first = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var second = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        var failure = outcome switch
        {
            SevenTvWriteOutcome.Unauthorized => SevenTvWriteResult.Unauthorized(),
            SevenTvWriteOutcome.RateLimited => SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(60)),
            _ => SevenTvWriteResult.Unavailable(),
        };
        _readResults.Enqueue(SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(failure));
        _readResults.Enqueue(SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(failure));

        await RunPassAsync();

        Assert.Empty(_addCalls);
        var wish = await LoadAsync(first);
        Assert.Equal((WishStatus.Approved, SeededVersion + 1), (wish.Status, wish.DecisionVersion));
        AssertAttemptCleared(wish);
        Assert.Equal(outcome == SevenTvWriteOutcome.Unavailable ? 2 : 1, _readCalls.Count);
        Assert.Equal(outcome == SevenTvWriteOutcome.Unauthorized, _lastSession!.Calls.Contains("unauthorized"));
        Assert.Equal(outcome == SevenTvWriteOutcome.RateLimited ? new[] { "pause" } : [], _lastSession.Calls.Where(c => c.StartsWith("pause", StringComparison.Ordinal)));
        Assert.Equal(WishStatus.Approved, (await LoadAsync(second)).Status);
        Assert.Null((await LoadAsync(second)).BotWriteAtUtc);
    }

    // ------------------------------------------------------------------------------------ confirmation reads

    // K6d-6, K6e-4: a confirmation read with a 429 pauses (joined to the booking) and counts as a read; one with
    // Unauthorized reports and counts as well.
    [Theory]
    [InlineData(SevenTvWriteOutcome.RateLimited)]
    [InlineData(SevenTvWriteOutcome.Unauthorized)]
    public async Task AFailedConfirmationRead_CountsAsARead_AndReportsToTheSession(SevenTvWriteOutcome outcome)
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedUnconfirmedAsync(channel, writtenSecondsAgo: 30, attempt: 1);
        _readResults.Enqueue(SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(
            outcome == SevenTvWriteOutcome.RateLimited ? SevenTvWriteResult.RateLimited(TimeSpan.FromSeconds(60)) : SevenTvWriteResult.Unauthorized()));

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Unconfirmed, 2, Now.AddSeconds(90)), (wish.Status, wish.BotAttemptCount, wish.BotNextAttemptAtUtc!.Value));
        Assert.Equal(outcome == SevenTvWriteOutcome.RateLimited ? "pause" : "unauthorized", _lastSession!.Calls.Single(c => c is "pause" or "pause-alone" or "unauthorized"));
    }

    // R2-7: no read can run (the token unusable, a 6 h pause, the budget used up, the token expired, the editor state
    // unknown for 15 minutes); the deadline step still presents at the deadline, without any read or add.
    [Theory]
    [InlineData("tokenUnusable")]
    [InlineData("paused")]
    [InlineData("budget")]
    [InlineData("expired")]
    [InlineData("editorUnknown")]
    public async Task WithoutAnyRead_TheDeadlineStepPresentsAtTheDeadline(string condition)
    {
        var channel = await SeedBotChannelAsync(proofAt: condition == "editorUnknown" ? Now.AddMinutes(-20) : null);
        var id = await SeedUnconfirmedAsync(channel, writtenSecondsAgo: 15 * 60, attempt: 4);
        if (condition == "editorUnknown")
        {
            _editors.Remove(BotA);
        }

        if (condition == "expired")
        {
            await SeedAccountAsync(BotA, status: BotAccountStatus.Expired);
        }

        var session = await SessionAsync();
        session.TokenUsable = condition != "tokenUnusable";
        session.RemainingActions = condition == "budget" ? 0 : 50;
        if (condition == "paused")
        {
            session.Account = session.Account with { PausedUntilUtc = Now.AddHours(6) };
        }

        await RunPassAsync(session: session);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await RunDeadlineStepAsync();

        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(id)));
        Assert.Empty(_readCalls);
        Assert.Empty(_addCalls);
    }

    // ------------------------------------------------------------------------------------ AK83, 2.5a (e): the crash path

    // An approved wish with BotWriteAtUtc of an earlier pass (a crash between addEmote and its booking): never added again,
    // unconfirmed without the acknowledgement, and the read with the bot's entry confirms without ownership.
    [Fact]
    public async Task ACrashRemnant_IsNeverAddedAgain_AndIsConfirmedWithoutOwnership()
    {
        var channel = await SeedBotChannelAsync();
        var written = Now.AddMinutes(-1);
        var id = await SeedApprovedAsync(channel, configure: w =>
        {
            w.BotWriteAtUtc = written;
            w.BotSetId = SetA;
            w.BotAttemptVersion = SeededVersion;
        });
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias", written.AddSeconds(2), BotA));

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, false, (DateTime?)null), (wish.Status, wish.BotOwned, wish.BotAddAckAtUtc));
        Assert.Equal(written.AddSeconds(2), wish.StartsAtUtc);
        Assert.Empty(_addCalls);
        Assert.Equal(new[] { SetA }, _readCalls);
    }

    // Sol-P1-2: the set changed after the write and the new active set already holds a bot entry of the same emote and
    // alias: reads go to the attempt's set, nothing is confirmed (no ownership), and the deadline presents it.
    [Fact]
    public async Task ASetChangeAfterTheWrite_ConfirmsNothing_AndTheDeadlinePresents()
    {
        var channel = await SeedBotChannelAsync(set: SetB);
        var id = await SeedUnconfirmedAsync(channel, writtenSecondsAgo: 0, attempt: 0, set: SetA);
        var emote = (await LoadAsync(id)).SevenTvEmoteId;
        Set(SetA).Add(new SevenTvSetEntry(emote, "Alias", Now, BotA));
        Set(SetB).Add(new SevenTvSetEntry(emote, "Alias", Now, BotA));

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Unconfirmed, false, 1), (wish.Status, wish.BotOwned, wish.BotAttemptCount));
        Assert.Equal(new[] { SetA }, _readCalls);

        _clock.Advance(TimeSpan.FromMinutes(17));
        await RunDeadlineStepAsync();
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(id)));
    }

    // Plan 2.9 (R2-8), with T7's recheck: presented (unconfirmed) back to unconfirmed, the schedule anew from the recheck;
    // the read finds the entry of the original write (generation from BotWriteAtUtc) and confirms with ownership.
    [Fact]
    public async Task AfterARecheck_TheReadConfirmsTheOriginalWrite_WithOwnership()
    {
        var channel = await SeedBotChannelAsync();
        var written = Now.AddMinutes(-40);
        var id = await SeedWishAsync(channel, WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.Unconfirmed;
            w.StartsAtUtc = null;
            w.ExpiresAtUtc = null;
            w.StartsAtAuthoritative = false;
            w.BotWriteAtUtc = written;
            w.BotAddAckAtUtc = written;
            w.BotSetId = SetA;
            w.BotAttemptVersion = SeededVersion;
        });
        Set(SetA).Add(new SevenTvSetEntry((await LoadAsync(id)).SevenTvEmoteId, "Alias", written.AddSeconds(1), BotA));
        await using (var db = CreateDb())
        {
            Assert.Equal(WishOutcome.Success, (await WishSut(db).RecheckAsync(channel.Name, id, new AuditActor("900001", "admin"))).Outcome);
        }

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Active, true, written.AddSeconds(1)), (wish.Status, wish.BotOwned, wish.StartsAtUtc!.Value));
        Assert.Empty(_addCalls);
    }

    // Plan 2.1 point 1: a permanent wish ends in kept, without ownership, with the start from addedAt.
    [Fact]
    public async Task APermanentWish_IsKept_WithoutOwnership()
    {
        var channel = await SeedBotChannelAsync();
        var id = await SeedApprovedAsync(channel, configure: w => w.Permanent = true);

        await RunPassAsync();

        var wish = await LoadAsync(id);
        Assert.Equal((WishStatus.Kept, false, (string?)null, Now), (wish.Status, wish.BotOwned, wish.BotSetId, wish.EndedAtUtc!.Value));
        Assert.Equal((Now, true, WishLinkOrigin.Bot), (wish.StartsAtUtc!.Value, wish.StartsAtAuthoritative, wish.LinkOrigin));
        Assert.Null(wish.FirstActivatedAtUtc);
    }

    // ------------------------------------------------------------------------------------ the frame of the queue

    // The budget (K6b-14): with two actions left only the two oldest approved are added (one action each, their answers
    // confirm), the youngest waits untouched for the next pass.
    [Fact]
    public async Task TheBudget_TakesTheOldestFirst_AndLeavesTheRestUntouched()
    {
        var channel = await SeedBotChannelAsync();
        var youngest = await SeedApprovedAsync(channel, approvedMinutesAgo: 1);
        var oldest = await SeedApprovedAsync(channel, approvedMinutesAgo: 30);
        var middle = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var session = await SessionAsync();
        session.RemainingActions = 2;
        var untouched = await SnapshotAsync(youngest);

        await RunPassAsync(session: session);

        Assert.Equal(WishStatus.Active, (await LoadAsync(oldest)).Status);
        Assert.Equal(WishStatus.Active, (await LoadAsync(middle)).Status);
        Assert.Equal(untouched, await SnapshotAsync(youngest));
        Assert.Equal(2, _addCalls.Count);
    }

    // K6b-17: a wish deleted between the selection and its lock is nothing to do; the next one is added.
    [Fact]
    public async Task AWishDeletedBeforeItsLock_IsNothingToDo_AndTheNextIsAdded()
    {
        var channel = await SeedBotChannelAsync();
        var deleted = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var next = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        var session = await SessionAsync();
        session.OnWait = async wait =>
        {
            if (wait == 1)
            {
                await using var other = CreateDb();
                await other.Wishes.Where(w => w.Id == deleted).ExecuteUpdateAsync(u => u
                    .SetProperty(w => w.Status, WishStatus.Kept)
                    .SetProperty(w => w.EndedAtUtc, Now));
                await other.Wishes.Where(w => w.Id == deleted).ExecuteDeleteAsync();
            }
        };

        await RunPassAsync(session: session);

        Assert.Null(await FindAsync(deleted));
        Assert.Equal(WishStatus.Active, (await LoadAsync(next)).Status);
        Assert.Single(_addCalls);
    }

    // T8b note: the editor proof counts for one minute at a write; a queue that outlives step 3's read refreshes it by a
    // new read before the intent (40 s of write spacing here); an unavailable refresh read leaves the add for later.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStaleProofInALongQueue_IsRefreshedByANewRead_BeforeTheIntent(bool refreshAvailable)
    {
        var channel = await SeedBotChannelAsync();
        var first = await SeedApprovedAsync(channel, approvedMinutesAgo: 10);
        var second = await SeedApprovedAsync(channel, approvedMinutesAgo: 5);
        var session = await SessionAsync();
        session.WriteDelay = TimeSpan.FromSeconds(40);
        session.OnWait = wait =>
        {
            if (wait == 2 && !refreshAvailable)
            {
                _editors.Remove(BotA);
            }

            return Task.CompletedTask;
        };
        var start = Now;

        await RunPassAsync(session: session);

        Assert.Equal(WishStatus.Active, (await LoadAsync(first)).Status);
        await _sevenTv.Received(2).GetEditorForAsync(BotA, Arg.Any<CancellationToken>());
        var wish = await LoadAsync(second);
        if (refreshAvailable)
        {
            Assert.Equal(WishStatus.Active, wish.Status);
            Assert.Equal(start.AddSeconds(40), (await SettingsAsync(channel)).BotEditorCheckedAtUtc);
            Assert.Equal(2, _addCalls.Count);
        }
        else
        {
            Assert.Equal((WishStatus.Approved, SeededVersion, (DateTime?)null), (wish.Status, wish.DecisionVersion, wish.BotWriteAtUtc));
            Assert.Single(_addCalls);
        }
    }

    // Regression "bot mode off: unchanged": in a manual channel and in a channel in fallback the queue touches no wish and
    // calls nothing; the deadline step leaves every wish without an open add as it is.
    [Fact]
    public async Task ManualAndFallbackChannels_AreUntouchedByTheQueueAndTheDeadlineStep()
    {
        var manual = await SeedBotChannelAsync(botMode: false);
        var fallback = await SeedBotChannelAsync(proofAt: Now.AddMinutes(-20));
        _editors.Remove(BotA);
        var ids = new[]
        {
            await SeedApprovedAsync(manual),
            await SeedWishAsync(manual, WishStatus.Active),
            await SeedApprovedAsync(fallback),
            await SeedWishAsync(fallback, WishStatus.Presented, w => w.StatusReason = WishStatusReason.Unconfirmed),
        };
        var before = await Task.WhenAll(ids.Select(SnapshotAsync));

        await RunPassAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        await RunDeadlineStepAsync();

        Assert.Equal(before, await Task.WhenAll(ids.Select(SnapshotAsync)));
        Assert.Empty(_addCalls);
        Assert.Empty(_readCalls);
    }

    // ------------------------------------------------------------------------------------ helpers (adds)

    // An activated bot channel (consent A, proof 10 s old) with its active set and A's positive editor read; the account
    // row of A's valid token unless a test seeded one already.
    private async Task<Seeded> SeedBotChannelAsync(string set = SetA, bool botMode = true, DateTime? proofAt = null)
    {
        await using var db = CreateDb();
        if (!await db.BotAccountCredentials.AnyAsync())
        {
            await SeedAccountAsync(BotA);
        }

        var channel = await SeedChannelAsync(botMode: botMode, proofAt: proofAt);
        await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.ActiveEmoteSetId, set));
        if (!_editors.TryGetValue(BotA, out var entries))
        {
            _editors[BotA] = entries = new Dictionary<string, (string State, bool Manage)>(StringComparer.Ordinal);
        }

        entries[channel.TwitchId] = ("ACCEPTED", true);
        Set(set);
        return channel;
    }

    private Task<long> SeedApprovedAsync(Seeded channel, int approvedMinutesAgo = 5, Action<Wish>? configure = null) =>
        SeedWishAsync(channel, WishStatus.Approved, w =>
        {
            w.ApprovedAtUtc = Now.AddMinutes(-approvedMinutesAgo);
            configure?.Invoke(w);
        });

    // unconfirmed after an acknowledged write, with read number attempt planned now.
    private Task<long> SeedUnconfirmedAsync(Seeded channel, int writtenSecondsAgo, int attempt, string set = SetA)
    {
        var written = Now.AddSeconds(-writtenSecondsAgo);
        return SeedWishAsync(channel, WishStatus.Unconfirmed, w =>
        {
            w.StartsAtUtc = null;
            w.ExpiresAtUtc = null;
            w.StartsAtAuthoritative = false;
            w.BotWriteAtUtc = written;
            w.BotAddAckAtUtc = written;
            w.BotSetId = set;
            w.BotAttemptVersion = SeededVersion;
            w.BotAttemptCount = attempt;
            w.BotNextAttemptAtUtc = Now;
            w.BotConfirmDeadlineAtUtc = WishBotConfirmSchedule.Deadline(written);
        });
    }

    private List<SevenTvSetEntry> Set(string setId)
    {
        if (!_sets.TryGetValue(setId, out var entries))
        {
            _sets[setId] = entries = [];
        }

        return entries;
    }

    private async Task RunDeadlineStepAsync()
    {
        await using var db = CreateDb();
        _eventsBeforeDeadline = _publisher.ReceivedCalls().Count();
        await Pass(db).RunDeadlineStepAsync();
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private int EventsAfterDeadline(Seeded channel) =>
        _publisher.ReceivedCalls()
            .Skip(_eventsBeforeDeadline)
            .Where(c => c.GetMethodInfo().Name == nameof(Core.Messaging.IRedisPublisher.PublishAsync))
            .Select(c => Core.Messaging.LiveEvent.TryParse((string?)c.GetArguments()[1]))
            .Count(e => e == new Core.Messaging.LiveEvent(Core.Messaging.LiveEvents.WishChanged, channel.Name));

    // "7TV" answers an add: by default it takes effect (an entry by _addedBy at the test clock) and returns the set.
    private async Task<SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>> AnswerAddAsync(string setId, string emoteId, string alias)
    {
        var call = new AddCall(setId, emoteId, alias, Now, _lastSession?.Calls.LastOrDefault());
        _addCalls.Add(call);
        if (_whileAdding is { } hook)
        {
            await hook();
        }

        return _addAnswer is { } answer ? answer(call) : TakeEffect(call);
    }

    // A read of a set: a queued answer first, otherwise the set as it is (Unavailable for a set "7TV" does not know).
    private async Task<SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>> AnswerSetReadAsync(string setId)
    {
        _readCalls.Add(setId);
        if (_whileReading is { } hook)
        {
            await hook(_readCalls.Count);
        }

        if (_readResults.Count > 0)
        {
            return _readResults.Dequeue();
        }

        return _sets.TryGetValue(setId, out var entries)
            ? SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(entries.ToList())
            : SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
    }

    // The wish service the manager routes call, with the bot channel state over an available account A.
    private WishService WishSut(AppDbContext db)
    {
        var availability = Substitute.For<IWishBotAvailability>();
        availability.GetStateAsync(Arg.Any<CancellationToken>()).Returns(new BotAccountAvailabilityState(true, BotA, Identity));
        var reader = new WishBotChannelStateReader(
            availability, new WishOptions { Enabled = true, PilotChannelIds = "*" }, _admins, _clock);
        return new WishService(db, new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance), _clock, NullLogger<WishService>.Instance, reader);
    }

    private static void AssertAttemptCleared(Wish wish)
    {
        Assert.Equal(
            ((DateTime?)null, (string?)null, (int?)null, (DateTime?)null, (DateTime?)null, (DateTime?)null),
            (wish.BotWriteAtUtc, wish.BotSetId, wish.BotAttemptVersion, wish.BotAddAckAtUtc, wish.BotNextAttemptAtUtc, wish.BotConfirmDeadlineAtUtc));
    }

    private sealed record AddCall(string SetId, string EmoteId, string Alias, DateTime At, string? LastSessionCall);
}
