using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The wish numbers of the bot account's admin read (#487, plan 2.10, T4) against real Postgres in a database of its
// own: the counts run over every channel, so they must not see the rows of other classes. Emptied before every test.
[Collection("Postgres")]
public partial class WishBotServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "wish_bot_service_tests";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
                    // A constant name, not user input; CREATE DATABASE cannot be parameterised.
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var migrate = CreateDb();
                await migrate.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDb();
        await reset.Database.ExecuteSqlRawAsync("""TRUNCATE "Wishes", "ChannelWishSettings", "Channels", "AuditLogEntries", "BotAccountCredentials" CASCADE;""");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Empty_IsAllZero()
    {
        var summary = await Sut().GetAdminSummaryAsync();

        Assert.Equal(new WishBotAdminSummary(new WishBotQueueCounts(0, 0, 0), 0, 0, 0), summary);
    }

    // adds: approved in bot channels only; confirms: unconfirmed; removals: queued removals; channels: activated, and
    // of those the suspended ones; repairs: open check phases. A manual channel's approved wish is a manager's job.
    [Fact]
    public async Task CountsEachNumber_OverEveryChannel()
    {
        var bot = await SeedChannelAsync(botMode: true);
        var otherBot = await SeedChannelAsync(botMode: true, suspendedReason: WishStatusReason.NotEditor);
        var manual = await SeedChannelAsync(botMode: false);
        var withoutSettings = await SeedChannelAsync(botMode: null);

        await SeedWishAsync(bot, WishStatus.Approved);
        await SeedWishAsync(otherBot, WishStatus.Approved);
        await SeedWishAsync(manual, WishStatus.Approved);
        await SeedWishAsync(withoutSettings, WishStatus.Approved);
        await SeedWishAsync(bot, WishStatus.Unconfirmed);
        await SeedWishAsync(otherBot, WishStatus.Unconfirmed);
        await SeedWishAsync(bot, WishStatus.Active, queued: true);
        await SeedWishAsync(bot, WishStatus.Presented, reason: WishStatusReason.Protected, queued: true);
        await SeedWishAsync(bot, WishStatus.Active);
        await SeedWishAsync(bot, WishStatus.Presented, reason: WishStatusReason.RepairReview, repair: true);
        await SeedWishAsync(bot, WishStatus.Rejected);

        var summary = await Sut().GetAdminSummaryAsync();

        Assert.Equal(new WishBotAdminSummary(new WishBotQueueCounts(Adds: 2, Confirms: 2, Removals: 2), BotChannels: 2, SuspendedChannels: 1, OpenRepairChecks: 1), summary);
    }

    private AppDbContext CreateDb() => fixture.CreateDbContext(DatabaseName);


    private async Task<string> SeedChannelAsync(bool? botMode, string? suspendedReason = null)
    {
        await using var db = CreateDb();
        var channel = new Channel
        {
            ChannelName = $"x{Guid.NewGuid():N}"[..18],
            TwitchChannelId = Guid.NewGuid().ToString("N")[..12],
            IsBotActive = true,
            CreatedAt = Now.AddDays(-100),
        };
        db.Channels.Add(channel);
        if (botMode is { } enabled)
        {
            db.ChannelWishSettings.Add(new ChannelWishSettings { Channel = channel, BotModeEnabled = enabled, BotSuspendedReason = suspendedReason });
        }

        await db.SaveChangesAsync();
        return channel.Id;
    }

    private async Task SeedWishAsync(string channelId, string status, string? reason = null, bool queued = false, bool repair = false)
    {
        await using var db = CreateDb();
        var running = status is WishStatus.Active or WishStatus.Presented;
        db.Wishes.Add(new Wish
        {
            ChannelId = channelId,
            SevenTvEmoteId = Guid.NewGuid().ToString("N")[..24],
            EmoteName = "Emote",
            Alias = "Alias",
            Source = WishSource.Manual,
            Status = status,
            StatusReason = reason,
            StatusChangedAtUtc = Now.AddDays(-1),
            CreatedAtUtc = Now.AddDays(-2),
            StartsAtUtc = running ? Now.AddDays(-30) : null,
            StartsAtAuthoritative = running,
            ExpiresAtUtc = running ? Now.AddDays(-1) : null,
            EndedAtUtc = WishStatus.IsEnded(status) ? Now.AddHours(-1) : null,
            RemovalQueuedAtUtc = queued ? Now.AddHours(-1) : null,
            RemovalQueuedVersion = queued ? 0 : null,
            BotRepairAtUtc = repair ? Now.AddMinutes(-5) : null,
        });
        await db.SaveChangesAsync();
    }
}
