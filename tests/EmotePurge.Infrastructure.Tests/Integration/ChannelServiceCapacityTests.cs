using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// Covers the active-channel cap (Channels:MaxActiveChannels) on top of the join-path behaviour
/// already exercised in <see cref="ChannelServiceTests"/> — a small configured cap here rather than
/// the default 80, so a handful of seeded rows is enough to reach it.
/// <para>
/// Each test runs against its own freshly created, freshly migrated database on the shared
/// "Postgres" container (same technique as <c>PendingMigrationGuardTests</c>), rather than the
/// container's default "emotepurge" database that every other test in this collection shares. The
/// cap check is a real <c>COUNT(*) WHERE IsBotActive</c> across the whole table, and the shared
/// database accumulates active channels from every other test that runs before these in the same
/// collection (Testcontainers here is one container for the entire assembly, not one per test) —
/// against that database a cap of 2 or 3 would already be "reached" before a test body runs a
/// single line.
/// </para>
/// </summary>
[Collection("Postgres")]
public class ChannelServiceCapacityTests(PostgresFixture fixture)
{
    private static readonly AuditActor Actor = new("4711", "sensitron");

    [Fact]
    public async Task JoinAsync_UnderCap_NonAdmin_Activates()
    {
        await using var db = await CreateIsolatedDbContextAsync();
        await SeedActiveChannelAsync(db, "capacitytestunder1");
        var service = CreateService(db, maxActiveChannels: 3);

        var result = await service.JoinAsync("capacitytestunder2", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.True(result.Channel.IsBotActive);
    }

    [Fact]
    public async Task JoinAsync_AtCap_NonAdmin_NewChannel_IsRejected_AndWritesNothing()
    {
        await using var db = await CreateIsolatedDbContextAsync();
        await SeedActiveChannelAsync(db, "capacitytestfull1");
        await SeedActiveChannelAsync(db, "capacitytestfull2");
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher, maxActiveChannels: 2);

        var result = await service.JoinAsync("capacitytestnew3", Actor);

        Assert.Equal(ChannelJoinStatus.CapacityReached, result.Status);
        Assert.Null(result.Channel);
        Assert.Null(await db.Channels.AsNoTracking()
            .SingleOrDefaultAsync(c => c.ChannelName == "capacitytestnew3"));
        await redisPublisher.DidNotReceive().PublishAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_AtCap_NonAdmin_RejoinOfInactiveChannel_IsRejected_AndLeavesItInactive()
    {
        await using var db = await CreateIsolatedDbContextAsync();
        await SeedActiveChannelAsync(db, "capacitytestfull4");
        await SeedActiveChannelAsync(db, "capacitytestfull5");
        var left = await SeedChannelAsync(db, "capacitytestleft6", isBotActive: false);
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher, maxActiveChannels: 2);

        var result = await service.JoinAsync("capacitytestleft6", Actor);

        Assert.Equal(ChannelJoinStatus.CapacityReached, result.Status);
        Assert.Null(result.Channel);
        var stored = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == left.Id);
        Assert.False(stored.IsBotActive);
        await redisPublisher.DidNotReceive().PublishAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JoinAsync_AtCap_GlobalAdmin_IsExempt_AndActivates()
    {
        await using var db = await CreateIsolatedDbContextAsync();
        await SeedActiveChannelAsync(db, "capacitytestfull7");
        await SeedActiveChannelAsync(db, "capacitytestfull8");
        var service = CreateService(db, maxActiveChannels: 2);

        var result = await service.JoinAsync("capacitytestadmin9", Actor, isGlobalAdmin: true);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.True(result.Channel.IsBotActive);

        // The admin's join still counts toward the total it was exempted from — the cap becomes
        // three active channels here, not two, confirming the join was not silently skipped.
        Assert.Equal(3, await db.Channels.AsNoTracking().CountAsync(c => c.IsBotActive));
    }

    [Fact]
    public async Task JoinAsync_AtCap_OnAnAlreadyActiveChannel_StaysIdempotent()
    {
        await using var db = await CreateIsolatedDbContextAsync();
        await SeedActiveChannelAsync(db, "capacitytestfull10");
        var active = await SeedActiveChannelAsync(db, "capacitytestfull11");
        var redisPublisher = Substitute.For<IRedisPublisher>();
        var service = CreateService(db, redisPublisher, maxActiveChannels: 2);

        var result = await service.JoinAsync("capacitytestfull11", Actor);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.NotNull(result.Channel);
        Assert.Equal(active.Id, result.Channel.Id);
        Assert.True(result.Channel.IsBotActive);
        await redisPublisher.Received(1).PublishAsync(
            BotCommands.Channel, "JOIN:capacitytestfull11", Arg.Any<CancellationToken>());
    }

    private static ChannelService CreateService(
        AppDbContext db, IRedisPublisher? redisPublisher = null, int maxActiveChannels = 80)
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable));

        return new ChannelService(
            db,
            redisPublisher ?? Substitute.For<IRedisPublisher>(),
            identityService,
            new ChannelCapacityOptions { MaxActiveChannels = maxActiveChannels },
            NullLogger<ChannelService>.Instance);
    }

    private static Task<Channel> SeedActiveChannelAsync(AppDbContext db, string channelName) =>
        SeedChannelAsync(db, channelName, isBotActive: true);

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string channelName, bool isBotActive)
    {
        var channel = new Channel
        {
            ChannelName = ChannelName.Normalize(channelName),
            IsBotActive = isBotActive,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    /// <summary>
    /// A fresh, empty, fully migrated database on the shared container — see the class comment for
    /// why the cap tests cannot share "emotepurge" with the rest of the collection. Same
    /// CREATE-DATABASE-then-migrate technique as <c>PendingMigrationGuardTests</c>.
    /// </summary>
    private async Task<AppDbContext> CreateIsolatedDbContextAsync()
    {
        var databaseName = $"capacity_test_{Guid.NewGuid():N}";
        await using (var admin = fixture.CreateDbContext())
        {
#pragma warning disable EF1002 // locally generated Guid, not user input — CREATE DATABASE cannot be parameterized anyway
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {databaseName}");
#pragma warning restore EF1002
        }

        var db = fixture.CreateDbContext(databaseName);
        await db.Database.MigrateAsync();
        return db;
    }
}
