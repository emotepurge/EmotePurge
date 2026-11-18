using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishService part 3 (wish emotes K2, T2c, contract 2.7): a decision on an overdue wish keeps the expiry it
// replaced in its own audit entry, so the pilot measurement still finds it.
public partial class WishServiceTests
{
    // (command, expired?) -> audit action; every command runs on a wish whose expiry is before now and on one whose is not.
    [Theory]
    [InlineData("extend", true)]
    [InlineData("extend", false)]
    [InlineData("keep", true)]
    [InlineData("keep", false)]
    [InlineData("close", true)]
    [InlineData("close", false)]
    [InlineData("complete-removal", true)]
    [InlineData("complete-removal", false)]
    public async Task LateDecision_RecordsThePreviousExpiry_OnlyWhenTheWishWasOverdue(string command, bool overdue)
    {
        var channel = await SeedChannelAsync();
        var expires = overdue ? Now.AddHours(-30).AddMilliseconds(123) : Now.AddDays(2);
        var status = command switch
        {
            "complete-removal" => WishStatus.RemovedExternally,
            "close" => WishStatus.Presented,
            _ => WishStatus.Active
        };
        var wishId = await SeedRowAsync(channel, status, w =>
        {
            w.ExpiresAtUtc = expires;
            w.EndedAtUtc = status == WishStatus.RemovedExternally ? Now.AddHours(-1) : null;
        });

        var result = await RunLateCommandAsync(command, channel.Name, wishId);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        await using var db = fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        using var json = JsonDocument.Parse(entry.DetailsJson!);
        var details = json.RootElement;
        if (overdue)
        {
            Assert.Equal(expires, details.GetProperty("previousExpiresAtUtc").GetDateTime().ToUniversalTime());
        }
        else
        {
            Assert.False(details.TryGetProperty("previousExpiresAtUtc", out _));
        }

        // AK19: nothing but ids, the duration and the previous expiry.
        var names = details.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Subset(new HashSet<string> { "wishId", "sevenTvEmoteId", "durationDays", "previousExpiresAtUtc" }, names.ToHashSet());
    }

    // The previous value, not the new expiry, and the boundary (expiry equal to now) counts as overdue.
    [Fact]
    public async Task Extend_OfAPresentedWishAtTheBoundary_RecordsTheOldExpiry_NotTheNewOne()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Presented, w =>
        {
            w.StatusReason = WishStatusReason.Expired;
            w.ExpiresAtUtc = Now;
        });

        var result = await Sut().ExtendAsync(channel.Name, wishId, Actor);

        Assert.Equal(WishOutcome.Success, result.Outcome);
        Assert.NotEqual(Now, result.Wish!.ExpiresAtUtc);
        await using var db = fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        using var json = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal(Now, json.RootElement.GetProperty("previousExpiresAtUtc").GetDateTime().ToUniversalTime());
    }

    // A presented wish without an expiry (e.g. reason unconfirmed) gets no field.
    [Fact]
    public async Task Close_OfAPresentedWishWithoutExpiry_RecordsNoPreviousExpiry()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Presented, w => w.StatusReason = WishStatusReason.Unconfirmed);

        Assert.Equal(WishOutcome.Success, (await Sut().CloseAsync(channel.Name, wishId, Actor)).Outcome);

        await using var db = fixture.CreateDbContext();
        var entry = await db.AuditLogEntries.SingleAsync(a => a.ChannelName == channel.Name);
        using var json = JsonDocument.Parse(entry.DetailsJson!);
        Assert.False(json.RootElement.TryGetProperty("previousExpiresAtUtc", out _));
    }

    // A refused command writes no entry at all, overdue or not.
    [Fact]
    public async Task Keep_RefusedOnAnOverdueWish_WritesNoAuditEntry()
    {
        var channel = await SeedChannelAsync();
        var wishId = await SeedRowAsync(channel, WishStatus.Closed, w =>
        {
            w.ExpiresAtUtc = Now.AddDays(-3);
            w.EndedAtUtc = Now.AddDays(-1);
        });

        Assert.Equal(WishOutcome.StateConflict, (await Sut().KeepAsync(channel.Name, wishId, Actor)).Outcome);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.AuditLogEntries.AnyAsync(a => a.ChannelName == channel.Name));
    }

    private Task<WishCommandResult> RunLateCommandAsync(string command, string channelName, long wishId) => command switch
    {
        "extend" => Sut().ExtendAsync(channelName, wishId, Actor),
        "keep" => Sut().KeepAsync(channelName, wishId, Actor),
        "close" => Sut().CloseAsync(channelName, wishId, Actor),
        _ => Sut().CompleteRemovalAsync(channelName, wishId, Actor)
    };
}
