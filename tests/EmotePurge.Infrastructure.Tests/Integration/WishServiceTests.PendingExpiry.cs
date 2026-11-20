using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// PendingExpiryDays as a channel setting (K6, plan #486 2.8, T4): read with the settings, written only when sent,
// 1 to 90, part of the wish.settings audit entry.
public partial class WishServiceTests
{
    [Fact]
    public async Task Settings_PendingExpiryDays_DefaultsToFourteen_IsWrittenWhenSent_AndKeptWhenNot()
    {
        var channel = await SeedChannelAsync();
        Assert.Equal(WishLimits.DefaultPendingExpiryDays, (await Sut().GetSettingsAsync(channel.Name))!.PendingExpiryDays);

        var written = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, null, PendingExpiryDays: 5), Actor);
        Assert.Equal(5, written.Settings!.PendingExpiryDays);

        var kept = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(20, 7, null), Actor);
        Assert.Equal((20, 5), (kept.Settings!.DefaultDurationDays, kept.Settings.PendingExpiryDays));
        Assert.Equal(5, (await Sut().GetSettingsAsync(channel.Name))!.PendingExpiryDays);

        await using var db = fixture.CreateDbContext();
        var last = await db.AuditLogEntries
            .Where(a => a.ChannelName == channel.Name && a.Action == AuditActions.WishSettings)
            .OrderByDescending(a => a.Id)
            .FirstAsync();
        using var details = JsonDocument.Parse(last.DetailsJson!);
        Assert.Equal(5, details.RootElement.GetProperty("pendingExpiryDays").GetInt32());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(90, true)]
    [InlineData(91, false)]
    public async Task Settings_PendingExpiryDays_AreOneToNinety(int days, bool accepted)
    {
        var channel = await SeedChannelAsync();

        var result = await Sut().UpdateSettingsAsync(channel.Name, new UpdateWishSettingsRequest(30, 7, null, PendingExpiryDays: days), Actor);

        Assert.Equal(accepted ? WishOutcome.Success : WishOutcome.SettingsOutOfRange, result.Outcome);
        if (!accepted)
        {
            await AssertNothingWrittenAsync(channel);
        }
    }
}
