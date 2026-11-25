using EmotePurge.Core.Entities;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Step 0 of the expiry run, the wish bot's fallback run (#487 plan 2.8, T8b): Revision 6b K6b-7 (it runs with the bot
// account switched off, so switching it off leaves nothing hanging) and 6e K6e-9 (the channel's state from one statement,
// the read of the expiry decision), plus the regression "bot mode off: unchanged".
public partial class WishExpiryServiceTests
{
    // K6b-7: with BotAccount:Enabled off every activated channel falls back; the run hands the open bot actions over: an
    // open add and a queued removal are presented, an open removal attempt is resolved without 7TV and leaves its
    // evidence as a repair phase. approved without an intent stays. One wish.changed for the channel.
    [Fact]
    public async Task BotFallback_WithTheBotAccountOff_HandsTheOpenBotActionsOver()
    {
        var channel = await SeedChannelAsync(botMode: true);
        var approved = await SeedWishAsync(channel, expiresAt: null, status: WishStatus.Approved, authoritative: false);
        var unconfirmed = await SeedWishAsync(channel, expiresAt: Now.AddDays(5), status: WishStatus.Unconfirmed);
        var queued = await SeedWishAsync(channel, expiresAt: Now.AddDays(5), botOwned: true, queued: true);
        var attempt = await SeedWishAsync(channel, expiresAt: Now.AddDays(5), botOwned: true, queued: true);
        await using (var db = CreateDb())
        {
            await db.Wishes.Where(w => w.Id == unconfirmed).ExecuteUpdateAsync(u => u.SetProperty(w => w.BotWriteAtUtc, Now.AddMinutes(-3)));
            await db.Wishes.Where(w => w.Id == attempt).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.BotRemoveAtUtc, Now.AddMinutes(-2))
                .SetProperty(w => w.BotAttemptVersion, SeededVersion)
                .SetProperty(w => w.BotRemoveEvidence, "{\"setId\":\"01SET\"}"));
        }

        await SetBotAvailableAsync(true);

        await Sut(botAccountEnabled: false).RunAsync();

        Assert.Equal((WishStatus.Approved, (string?)null), StatusOf(await LoadAsync(approved)));
        Assert.Equal((WishStatus.Presented, WishStatusReason.Unconfirmed), StatusOf(await LoadAsync(unconfirmed)));
        var queuedWish = await LoadAsync(queued);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(queuedWish));
        Assert.Null(queuedWish.RemovalQueuedAtUtc);
        var attemptWish = await LoadAsync(attempt);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable), StatusOf(attemptWish));
        Assert.Null(attemptWish.BotRemoveAtUtc);
        Assert.Equal("{\"setId\": \"01SET\"}", attemptWish.BotRemoveEvidence);
        Assert.Equal(Now, attemptWish.BotRepairAtUtc);
        Assert.Equal(SeededVersion, attemptWish.DecisionVersion);
        Assert.Equal(1, EventsFor(channel));
        Assert.Equal(0, await CountAuditsAsync());
    }

    // The regression "bot mode off: unchanged": a manual channel's wishes are never touched by step 0, whatever their bot
    // columns say, and a channel in the bot state keeps its open actions (the runner works them).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BotFallback_LeavesManualChannels_AndBotChannelsInTheBotState_Alone(bool botAccountEnabled)
    {
        var manual = await SeedChannelAsync(threshold: 1m);
        var bot = await SeedChannelAsync(botMode: true);
        var manualQueued = await SeedWishAsync(manual, expiresAt: Now.AddDays(5), status: WishStatus.Presented, reason: WishStatusReason.Protected, queued: true);
        var botUnconfirmed = await SeedWishAsync(bot, expiresAt: Now.AddDays(5), status: WishStatus.Unconfirmed);
        await SetBotAvailableAsync(true);
        var before = await Task.WhenAll(new[] { manualQueued, botUnconfirmed }.Select(SnapshotAsync));

        await Sut(botAccountEnabled: botAccountEnabled).RunAsync();

        var after = await Task.WhenAll(new[] { manualQueued, botUnconfirmed }.Select(SnapshotAsync));
        Assert.Equal(before[0], after[0]);
        Assert.Equal(0, EventsFor(manual));
        if (botAccountEnabled)
        {
            Assert.Equal(before[1], after[1]);
            Assert.Equal(0, EventsFor(bot));
        }
        else
        {
            Assert.Equal(WishStatus.Presented, (await LoadAsync(botUnconfirmed)).Status);
        }
    }

    // K6e-9: the fallback run of the expiry run takes the channel's state from the one statement that reads settings and
    // account. A hand PUT to I+1 with the runner's proof at I+1 commits right after that statement: the snapshot (proof
    // and account at I) says bot, nothing is presented. Counter-check: the same PUT without a new proof before the
    // statement presents.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BotFallback_TakesTheStateFromOneStatement(bool afterTheStatement)
    {
        var channel = await SeedChannelAsync(botMode: true);
        var unconfirmed = await SeedWishAsync(channel, expiresAt: Now.AddDays(5), status: WishStatus.Unconfirmed);
        await SetBotAvailableAsync(true);
        if (!afterTheStatement)
        {
            await HandPutSameAccountAsync(channel, newProof: false);
        }

        var hook = new CommandHook(IsChannelFactsRead, async occurrence =>
        {
            if (afterTheStatement && occurrence == 1)
            {
                await HandPutSameAccountAsync(channel, newProof: true);
            }
        });
        await using var db = fixture.CreateTaggedDbContext("wish-expiry-fallback-one-statement", [hook], databaseName: DatabaseName);

        await Sut(db).RunAsync();

        var expected = afterTheStatement ? WishStatus.Unconfirmed : WishStatus.Presented;
        Assert.Equal(expected, (await LoadAsync(unconfirmed)).Status);
        Assert.Equal(1, hook.Count(IsChannelFactsRead));
    }

    private static (string Status, string? Reason) StatusOf(Wish wish) => (wish.Status, wish.StatusReason);
}
