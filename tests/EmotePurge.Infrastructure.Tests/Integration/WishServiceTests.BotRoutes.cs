using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The two wish commands of the bot (#487 plan 2.9, T7): recheck (R2-8) and bot-remove (AK84, route part). Both need the
// bot to act for the channel; the open-attempt guard answers first; a channel whose consent is bound to another account
// (S6-2) is not bot. The manual flow's answers are pinned by the transition matrix.
public partial class WishServiceTests
{
    private readonly IBotAccountNudge _nudge = Substitute.For<IBotAccountNudge>();

    // ------------------------------------------------------------------------------------ recheck

    [Fact]
    public async Task Recheck_InTheAttemptsSet_StartsANewDeadline_KeepingSetAnchorAndAck()
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel);
        var written = Now.AddMinutes(-40);
        var wishId = await SeedUnconfirmedPresentedAsync(channel, written);

        var result = await BotRouteSut().RecheckAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal(WishStatus.Unconfirmed, result.Wish!.Status);
        var row = await LoadWishOrNullAsync(wishId);
        Assert.Equal((WishStatus.Unconfirmed, (string?)null, Now), (row!.Status, row.StatusReason, row.StatusChangedAtUtc));
        Assert.Equal((0, Now, Now + WishLimits.BotConfirmWindow), (row.BotAttemptCount, row.BotNextAttemptAtUtc!.Value, row.BotConfirmDeadlineAtUtc!.Value));
        Assert.Equal((ActiveSet, written, written), (row.BotSetId, row.BotWriteAtUtc!.Value, row.BotAddAckAtUtc!.Value));
        Assert.Equal((4, 4), (row.DecisionVersion, row.BotAttemptVersion!.Value));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(AuditActions.WishRecheck, (await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name)).Action);
        AssertPublishedOnce(channel);
        await _nudge.Received(1).NudgeAsync(Arg.Any<CancellationToken>());
    }

    // R2-8: a set change since the write, or no set at all, refuses without a write.
    [Theory]
    [InlineData("01ANOTHERSET00000000000000")]
    [InlineData(null)]
    public async Task Recheck_WithAnotherSetOrNone_IsBotSetChanged(string? attemptSet)
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel);
        var wishId = await SeedUnconfirmedPresentedAsync(channel, Now.AddMinutes(-40), attemptSet);
        var before = await SnapshotAsync(wishId);

        var result = await BotRouteSut().RecheckAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.BotSetChanged, result.Outcome);
        await AssertBotRouteUntouchedAsync(channel, wishId, before);
    }

    // S6-2 and the fallback: consent to another account, or an unavailable account, is not bot.
    [Theory]
    [InlineData("consent to another account")]
    [InlineData("account unavailable")]
    public async Task Recheck_WhileTheBotDoesNotAct_IsBotUnavailable(string cause)
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel, s =>
        {
            if (cause == "consent to another account")
            {
                s.BotConsentSevenTvUserId = "01OTHERBOTACCOUNT000000000";
            }
        });
        var wishId = await SeedUnconfirmedPresentedAsync(channel, Now.AddMinutes(-40));
        var before = await SnapshotAsync(wishId);

        var result = await BotRouteSut(cause == "account unavailable" ? BotUp with { IsAvailable = false } : null)
            .RecheckAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.BotUnavailable, result.Outcome);
        await AssertBotRouteUntouchedAsync(channel, wishId, before);
    }

    [Fact]
    public async Task Recheck_AnotherPresentedReason_IsAConflict_EvenInABotChannel()
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel);
        var wishId = await SeedUnconfirmedPresentedAsync(channel, Now.AddMinutes(-40), configure: w => w.StatusReason = WishStatusReason.Protected);
        var before = await SnapshotAsync(wishId);

        Assert.Equal(WishOutcome.StateConflict, (await BotRouteSut().RecheckAsync(channel.Name, wishId, Actor)).Outcome);
        await AssertBotRouteUntouchedAsync(channel, wishId, before);
    }

    // ------------------------------------------------------------------------------------ bot-remove

    [Theory]
    [InlineData(WishStatusReason.Protected)]
    [InlineData(WishStatusReason.CoverageGap)]
    [InlineData(WishStatusReason.BotUnavailable)]
    public async Task BotRemove_QueuesARemoval_BoundToTheVersionAfterTheRequest(string reason)
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel);
        var expired = Now.AddDays(-1);
        var wishId = await SeedOwnedPresentedAsync(channel, w =>
        {
            w.StatusReason = reason;
            w.ExpiresAtUtc = expired;
        });

        var result = await BotRouteSut().RequestBotRemovalAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.Equal((WishStatus.Presented, reason), (result.Wish!.Status, result.Wish.StatusReason));
        var row = (await LoadWishOrNullAsync(wishId))!;
        Assert.Equal((4, Now, 4), (row.DecisionVersion, row.RemovalQueuedAtUtc!.Value, row.RemovalQueuedVersion!.Value));
        Assert.Equal((true, ActiveSet), (row.BotOwned, row.BotSetId));
        await using var db = fixture.CreateDbContext();
        var audit = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        Assert.Equal((AuditActions.WishBotRemoveRequest, Actor.TwitchUserId), (audit.Action, audit.ActorTwitchUserId));
        using var details = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal(expired, details.RootElement.GetProperty("previousExpiresAtUtc").GetDateTime());
        AssertPublishedOnce(channel);
        await _nudge.Received(1).NudgeAsync(Arg.Any<CancellationToken>());
    }

    // AK84 in the plan's order of refusals; every refusal leaves the row untouched.
    [Theory]
    [InlineData("reason expired", WishOutcome.StateConflict)]
    [InlineData("reason not_bot_owned", WishOutcome.StateConflict)]
    [InlineData("already queued", WishOutcome.StateConflict)]
    [InlineData("open removal attempt", WishOutcome.StateConflict)]
    [InlineData("not owned", WishOutcome.NotBotOwned)]
    [InlineData("provisional start", WishOutcome.StartUnconfirmed)]
    [InlineData("fallback", WishOutcome.BotUnavailable)]
    [InlineData("consent to another account", WishOutcome.BotUnavailable)]
    public async Task BotRemove_Refusals(string shape, WishOutcome expected)
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel, s =>
        {
            if (shape == "fallback")
            {
                s.BotSuspendedReason = WishBotChannelReasons.NotEditor;
            }
            else if (shape == "consent to another account")
            {
                s.BotConsentSevenTvUserId = "01OTHERBOTACCOUNT000000000";
            }
        });
        var wishId = await SeedOwnedPresentedAsync(channel, w =>
        {
            switch (shape)
            {
                case "reason expired":
                    w.StatusReason = WishStatusReason.Expired;
                    break;
                case "reason not_bot_owned":
                    w.StatusReason = WishStatusReason.NotBotOwned;
                    break;
                case "already queued":
                    w.RemovalQueuedAtUtc = Now.AddMinutes(-1);
                    w.RemovalQueuedVersion = w.DecisionVersion;
                    break;
                case "open removal attempt":
                    w.BotRemoveAtUtc = Now.AddMinutes(-1);
                    break;
                case "not owned":
                    w.BotOwned = false;
                    break;
                case "provisional start":
                    w.StartsAtAuthoritative = false;
                    break;
            }
        });
        var before = await SnapshotAsync(wishId);

        var result = await BotRouteSut().RequestBotRemovalAsync(channel.Name, wishId, Actor);

        Assert.Equal(expected, result.Outcome);
        await AssertBotRouteUntouchedAsync(channel, wishId, before);
    }

    [Fact]
    public async Task BotRemove_Twice_IsAConflictTheSecondTime()
    {
        var channel = await SeedChannelAsync();
        await ActivateBotAsync(channel);
        var wishId = await SeedOwnedPresentedAsync(channel);

        Assert.Equal(WishOutcome.Success, (await BotRouteSut().RequestBotRemovalAsync(channel.Name, wishId, Actor)).Outcome);
        var afterFirst = await SnapshotAsync(wishId);
        Assert.Equal(WishOutcome.StateConflict, (await BotRouteSut().RequestBotRemovalAsync(channel.Name, wishId, Actor)).Outcome);

        Assert.Equal(afterFirst, await SnapshotAsync(wishId));
    }

    // ------------------------------------------------------------------------------------ helpers (bot routes)

    private WishService BotRouteSut(BotAccountAvailabilityState? account = null, AppDbContext? db = null)
    {
        var availability = Substitute.For<IWishBotAvailability>();
        availability.GetStateAsync(Arg.Any<CancellationToken>()).Returns(account ?? BotUp);
        var reader = new WishBotChannelStateReader(
            availability, new WishOptions { Enabled = true, PilotChannelIds = "*" }, Substitute.For<IGlobalAdminAllowlist>(), _clock);
        return new(
            db ?? fixture.CreateDbContext(),
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            NullLogger<WishService>.Instance,
            reader,
            botNudge: _nudge);
    }

    // presented (unconfirmed) after the deadline step: the attempt's set, anchor and ack kept for recheck.
    private async Task<long> SeedUnconfirmedPresentedAsync(
        Seeded channel, DateTime written, string? attemptSet = ActiveSet, Action<Wish>? configure = null) =>
        await SeedRowAsync(channel, WishStatus.Presented, w =>
        {
            w.StartsAtUtc = null;
            w.StartsAtAuthoritative = false;
            w.ExpiresAtUtc = null;
            w.RemovalQueuedAtUtc = null;
            w.RemovalQueuedVersion = null;
            w.BotWriteAtUtc = written;
            w.BotAddAckAtUtc = written;
            w.BotSetId = attemptSet;
            w.BotAttemptVersion = SeededDecisionVersion;
            w.BotAttemptCount = 5;
            configure?.Invoke(w);
        });

    // presented (protected) with the bot's ownership of its own entry, the start authoritative, nothing queued.
    private async Task<long> SeedOwnedPresentedAsync(Seeded channel, Action<Wish>? configure = null) =>
        await SeedRowAsync(channel, WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.Protected;
            w.RemovalQueuedAtUtc = null;
            w.RemovalQueuedVersion = null;
            w.BotOwned = true;
            w.BotOwnedAddedAtUtc = w.StartsAtUtc;
            w.BotSetId = ActiveSet;
            configure?.Invoke(w);
        });

    private async Task AssertBotRouteUntouchedAsync(Seeded channel, long wishId, string before)
    {
        Assert.Equal(before, await SnapshotAsync(wishId));
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
        _ = _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
        await _nudge.DidNotReceiveWithAnyArgs().NudgeAsync(default);
    }
}
