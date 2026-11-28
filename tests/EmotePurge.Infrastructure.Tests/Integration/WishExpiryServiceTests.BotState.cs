using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Row 4 of the expiry run asks the channel's effective bot state (#487 plan 2.3, T6): Revision 6b K6b-18 (the state as
// the Api computes it, so a channel the runner has not booked as fallen back is presented, not queued), 6c K6c-6 (read
// for every candidate, no memo of the pass) and 6d K6d-7 (settings and account from one statement, one snapshot).
public partial class WishExpiryServiceTests
{
    // K6b-18: an activated channel with an available account but a stale proof, a consent to another account or no
    // coverage presents the due bot wish right away; a fresh proof queues it as before, also through an activator who
    // is a global admin by id in an unlisted channel.
    [Theory]
    [InlineData("fresh", WishExpiryPolicy.QueuedOutcome)]
    [InlineData("stale proof", WishStatusReason.BotUnavailable)]
    [InlineData("consent to another account", WishStatusReason.BotUnavailable)]
    [InlineData("not listed", WishStatusReason.BotUnavailable)]
    [InlineData("not listed, activator admin by id", WishExpiryPolicy.QueuedOutcome)]
    public async Task BotState_DecidesRow4_LikeTheApi(string variant, string expected)
    {
        var channel = await SeedChannelAsync(botMode: true);
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), botOwned: true);
        await SetBotAvailableAsync(true);
        await using (var db = CreateDb())
        {
            if (variant == "stale proof")
            {
                await db.ChannelWishSettings.Where(s => s.ChannelId == channel.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.BotEditorCheckedAtUtc, Now - WishBotChannelState.EditorCheckFreshness - TimeSpan.FromSeconds(1)));
            }
            else if (variant == "consent to another account")
            {
                await db.ChannelWishSettings.Where(s => s.ChannelId == channel.Id)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.BotConsentSevenTvUserId, "01ANOTHERBOT"));
            }
        }

        var admins = Substitute.For<IGlobalAdminAllowlist>();
        admins.IsAdminById("4242").Returns(variant.EndsWith("admin by id", StringComparison.Ordinal));
        var pilot = variant.StartsWith("not listed", StringComparison.Ordinal) ? "" : "*";

        await Sut(pilotChannelIds: pilot, admins: admins).RunAsync();

        Assert.Equal(expected, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
        var wish = await LoadAsync(wishId);
        Assert.Equal(expected == WishExpiryPolicy.QueuedOutcome, wish.RemovalQueuedAtUtc is not null);
    }

    // The regression "bot mode off: unchanged": a manual channel presents as expired, with the account available or not,
    // and with the switches off as well.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BotState_AManualChannel_IsExpired_AsBefore(bool accountAvailable, bool botAccountEnabled)
    {
        var channel = await SeedChannelAsync(threshold: 1m);
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), botOwned: true);
        await SetBotAvailableAsync(accountAvailable);

        await Sut(botAccountEnabled: botAccountEnabled).RunAsync();

        var wish = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Expired), (wish.Status, wish.StatusReason));
    }

    // K6c-6: two due bot wishes of one channel, proof and account at I. Between the two decisions a hand PUT of the same
    // account raises the identity to I+1 and the runner books the proof at I+1: the second is queued (state bot, as the
    // Api reports it). Without the new proof the second is presented as bot_unavailable. The first is queued either way.
    [Theory]
    [InlineData(true, WishExpiryPolicy.QueuedOutcome)]
    [InlineData(false, WishStatusReason.BotUnavailable)]
    public async Task BotState_IsReadPerCandidate_SoAChangeBetweenTwoCandidatesCounts(bool newProof, string secondExpected)
    {
        var channel = await SeedChannelAsync(botMode: true);
        var first = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-2), botOwned: true);
        var second = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), botOwned: true);
        await SetBotAvailableAsync(true);
        var hook = new CommandHook(IsWishLock, async occurrence =>
        {
            if (occurrence == 2)
            {
                await HandPutSameAccountAsync(channel, newProof);
            }
        });
        await using var db = fixture.CreateTaggedDbContext("wish-expiry-per-candidate", [hook], databaseName: DatabaseName);

        await Sut(db).RunAsync();

        Assert.Equal(WishExpiryPolicy.QueuedOutcome, OutcomeOf(Assert.Single(await DueEntriesAsync(first))));
        Assert.Equal(secondExpected, OutcomeOf(Assert.Single(await DueEntriesAsync(second))));
    }

    // K6d-7: the change commits right after the one statement that read settings and account for the candidate. The run
    // decides on that statement's snapshot (proof and account at I, queued), never on a mix; exactly one statement per
    // candidate reads both tables. Counter-check: the same change without the new proof before that statement presents.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BotState_ComesFromOneStatement_NeverFromTwoSnapshots(bool afterTheStatement)
    {
        var channel = await SeedChannelAsync(botMode: true);
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), botOwned: true);
        await SetBotAvailableAsync(true);
        var hook = new CommandHook(
            afterTheStatement ? IsChannelFactsRead : IsWishLock,
            async occurrence =>
            {
                if (occurrence == 1)
                {
                    await HandPutSameAccountAsync(channel, newProof: afterTheStatement);
                }
            });
        await using var db = fixture.CreateTaggedDbContext("wish-expiry-one-statement", [hook], databaseName: DatabaseName);

        await Sut(db).RunAsync();

        var expected = afterTheStatement ? WishExpiryPolicy.QueuedOutcome : WishStatusReason.BotUnavailable;
        Assert.Equal(expected, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
        Assert.Equal(1, hook.Count(IsChannelFactsRead));
        Assert.Equal(0, hook.Count(c => IsCredentialsRead(c) && !IsSettingsRead(c)));
    }

    // A hand PUT of the same 7TV account, committed on a connection of its own: the identity goes to I+1; with newProof the
    // runner's check at I+1 follows (booked only because the row shows I+1, plan 2.3).
    private async Task HandPutSameAccountAsync(Seeded channel, bool newProof)
    {
        await using var db = CreateDb();
        await db.BotAccountCredentials.ExecuteUpdateAsync(u => u
            .SetProperty(c => c.IdentityVersion, BotIdentityVersion + 1)
            .SetProperty(c => c.CredentialVersion, c => c.CredentialVersion + 1));
        if (newProof)
        {
            await db.ChannelWishSettings.Where(s => s.ChannelId == channel.Id).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.BotEditorCheckedIdentityVersion, BotIdentityVersion + 1)
                .SetProperty(s => s.BotEditorCheckedAtUtc, Now));
        }
    }

    private static bool IsSettingsRead(string command) => command.Contains("\"ChannelWishSettings\"", StringComparison.Ordinal);

    private static bool IsCredentialsRead(string command) => command.Contains("\"BotAccountCredentials\"", StringComparison.Ordinal);

    private static bool IsChannelFactsRead(string command) => IsSettingsRead(command) && IsCredentialsRead(command);
}
