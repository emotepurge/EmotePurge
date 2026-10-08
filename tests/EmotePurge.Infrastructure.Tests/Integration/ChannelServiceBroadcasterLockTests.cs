using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The broadcaster re-add lock (#245) where ChannelService reads it: the join (who may lift it, in
// which order, and that a refused join writes nothing), the active-channel roster and the "tracked
// channel by Twitch id" lookup. Every lock is scoped to its test (BroadcasterLockScope): the shared
// database must not hand a later identity-reconcile test an active row with a locked id.
[Collection("Postgres")]
public class ChannelServiceBroadcasterLockTests(PostgresFixture fixture)
{
    private static readonly AuditActor Moderator = new("bl-mod", "blmod");

    [Fact]
    public async Task Join_ByAModerator_OfALockedChannel_IsRefusedWithTheDate_AndWritesNothing()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100001");
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100001", "bllockedmod")).JoinAsync("bllockedmod", Moderator);

        Assert.Equal(ChannelJoinStatus.LockedByBroadcaster, result.Status);
        Assert.Equal(lockScope.LockedAtUtc, result.LockedAtUtc);
        await AssertNothingWrittenAsync("bllockedmod", "bl-100001");
    }

    [Fact]
    public async Task Join_ByTheBroadcaster_LiftsTheLock_AndRecordsItOnTheJoinEntry()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100002");
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100002", "blowner")).JoinAsync("blowner", new AuditActor("bl-100002", "blowner"));

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.Null(await LoadLockedAtAsync("bl-100002"));
        var details = await LoadJoinDetailsAsync("blowner");
        Assert.True(details.GetProperty("broadcasterLockLifted").GetBoolean());
        Assert.False(details.TryGetProperty("liftedByAdmin", out _));
        await DeactivateAsync("blowner");
    }

    [Fact]
    public async Task Join_ByAGlobalAdminWithoutTheFlag_IsRefusedWithTheDate_AndWritesNothing()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100003");
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100003", "bladminnoflag"))
            .JoinAsync("bladminnoflag", new AuditActor("bl-admin", "bladmin"), isGlobalAdmin: true);

        Assert.Equal(ChannelJoinStatus.LockedByBroadcaster, result.Status);
        Assert.Equal(lockScope.LockedAtUtc, result.LockedAtUtc);
        await AssertNothingWrittenAsync("bladminnoflag", "bl-100003");
    }

    [Fact]
    public async Task Join_ByAGlobalAdminWithTheFlag_LiftsTheLock_AndTheEntryNamesTheAdminOverride()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100004");
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100004", "bladminflag"))
            .JoinAsync("bladminflag", new AuditActor("bl-admin", "bladmin"), isGlobalAdmin: true, liftBroadcasterLock: true);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.Null(await LoadLockedAtAsync("bl-100004"));
        var details = await LoadJoinDetailsAsync("bladminflag");
        Assert.True(details.GetProperty("broadcasterLockLifted").GetBoolean());
        Assert.True(details.GetProperty("liftedByAdmin").GetBoolean());
        Assert.Equal(lockScope.LockedAtUtc, details.GetProperty("lockedAtUtc").GetDateTime().ToUniversalTime());
        await DeactivateAsync("bladminflag");
    }

    [Fact]
    public async Task Join_WithTheFlagButWithoutTheAdminRole_IsRefusedLikeAnyModerator()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100005");
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100005", "blflagnoadmin"))
            .JoinAsync("blflagnoadmin", Moderator, isGlobalAdmin: false, liftBroadcasterLock: true);

        Assert.Equal(ChannelJoinStatus.LockedByBroadcaster, result.Status);
        await AssertNothingWrittenAsync("blflagnoadmin", "bl-100005");
    }

    [Fact]
    public async Task Join_ByABroadcasterWhoIsAlsoAGlobalAdmin_WithoutTheFlag_LiftsTheLockAsTheOwner()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100006");
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100006", "blowneradmin"))
            .JoinAsync("blowneradmin", new AuditActor("bl-100006", "blowneradmin"), isGlobalAdmin: true);

        Assert.Equal(ChannelJoinStatus.Joined, result.Status);
        Assert.Null(await LoadLockedAtAsync("bl-100006"));
        var details = await LoadJoinDetailsAsync("blowneradmin");
        Assert.True(details.GetProperty("broadcasterLockLifted").GetBoolean());
        Assert.False(details.TryGetProperty("liftedByAdmin", out _));
        await DeactivateAsync("blowneradmin");
    }

    [Fact]
    public async Task Join_ByTheBroadcaster_WhenTheCapIsFull_IsRefusedForTheCap_AndTheLockStays()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100007");
        await SeedChannelAsync("blcapfull", "bl-100007", isBotActive: false);
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100007", "blcapfull"), maxActiveChannels: 0)
            .JoinAsync("blcapfull", new AuditActor("bl-100007", "blcapfull"));

        Assert.Equal(ChannelJoinStatus.CapacityReached, result.Status);
        Assert.Equal(lockScope.LockedAtUtc, await LoadLockedAtAsync("bl-100007"));
        Assert.Empty(await LoadAuditEntriesAsync("blcapfull"));
    }

    [Fact]
    public async Task Join_ByTheBroadcaster_OfAnIdOnTheExcludedList_StaysExcluded_AndTheLockStays()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100008");
        var excluded = Substitute.For<IExcludedChannelFilter>();
        excluded.IsExcluded("bl-100008").Returns(true);
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Found("bl-100008", "blexcluded"), excludedChannelFilter: excluded)
            .JoinAsync("blexcluded", new AuditActor("bl-100008", "blexcluded"));

        Assert.Equal(ChannelJoinStatus.ChannelExcluded, result.Status);
        Assert.Equal(lockScope.LockedAtUtc, await LoadLockedAtAsync("bl-100008"));
    }

    [Fact]
    public async Task Join_OfALoginTwitchDoesNotKnow_OnARowWhoseStoredIdIsLocked_IsRefusedForAModerator()
    {
        // The row the identity reconcile left behind for its lock: inactive, id stored. Helix does not
        // know the login (NotFound), so the stored id is the only thing the join can check.
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100009");
        await SeedChannelAsync("blnotfound", "bl-100009", isBotActive: false);
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Lookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.NotFound)))
            .JoinAsync("blnotfound", Moderator);

        Assert.Equal(ChannelJoinStatus.LockedByBroadcaster, result.Status);
        Assert.Equal(lockScope.LockedAtUtc, result.LockedAtUtc);
        await using var verify = fixture.CreateDbContext();
        Assert.False((await verify.Channels.AsNoTracking().SingleAsync(c => c.ChannelName == "blnotfound")).IsBotActive);
        Assert.Empty(await LoadAuditEntriesAsync("blnotfound"));
    }

    [Fact]
    public async Task Join_DuringAHelixOutage_OnARowWhoseStoredIdIsLocked_IsRefusedForAModerator()
    {
        // Unavailable carries no identity; the row the join lands on by name is what carries the id.
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100010");
        await SeedChannelAsync("bloutage", "bl-100010", isBotActive: false);
        await using var db = fixture.CreateDbContext();

        var result = await CreateService(db, Lookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable)))
            .JoinAsync("bloutage", Moderator);

        Assert.Equal(ChannelJoinStatus.LockedByBroadcaster, result.Status);
        Assert.Empty(await LoadAuditEntriesAsync("bloutage"));
    }

    [Fact]
    public async Task ListActiveChannelNames_LeavesOutARowWhoseStoredIdIsLocked_ButKeepsAnIdLessRow()
    {
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100011");
        await SeedChannelAsync("blrosterlocked", "bl-100011", isBotActive: true);
        await SeedChannelAsync("blrosteridless", twitchChannelId: null, isBotActive: true);
        await SeedChannelAsync("blrosterfree", "bl-100012", isBotActive: true);
        try
        {
            await using var db = fixture.CreateDbContext();
            var names = await CreateService(db).ListActiveChannelNamesAsync();

            Assert.DoesNotContain("blrosterlocked", names);
            Assert.Contains("blrosteridless", names);
            Assert.Contains("blrosterfree", names);
        }
        finally
        {
            await DeactivateAsync("blrosterlocked");
        }
    }

    [Fact]
    public async Task GetActiveByTwitchChannelId_ReadsALockedChannelAsUntracked()
    {
        // Epic #200's read site (the target picker, the editability pre-check, the paper entry): a
        // locked channel must look exactly like one we do not track, as an excluded one does.
        await using var lockScope = await BroadcasterLockScope.CreateAsync(fixture, "bl-100013");
        await SeedChannelAsync("blpickerlocked", "bl-100013", isBotActive: true);
        await SeedChannelAsync("blpickerfree", "bl-100014", isBotActive: true);
        try
        {
            await using var db = fixture.CreateDbContext();
            var service = CreateService(db);

            Assert.Null(await service.GetActiveByTwitchChannelIdAsync("bl-100013"));
            Assert.Equal("blpickerfree", (await service.GetActiveByTwitchChannelIdAsync("bl-100014"))?.ChannelName);
        }
        finally
        {
            await DeactivateAsync("blpickerlocked");
        }
    }

    private static ChannelService CreateService(
        AppDbContext db,
        IChannelIdentityService? identity = null,
        int maxActiveChannels = int.MaxValue,
        IExcludedChannelFilter? excludedChannelFilter = null) =>
        new(
            db,
            Substitute.For<IRedisPublisher>(),
            identity ?? Lookup(TwitchUserLookup.Failed(TwitchUserLookupStatus.Unavailable)),
            new ChannelEmoteSetObservationService(db),
            new BroadcasterChannelLockService(db),
            new ChannelCapacityOptions { MaxActiveChannels = maxActiveChannels },
            excludedChannelFilter ?? Substitute.For<IExcludedChannelFilter>(),
            new RecordingLogger<ChannelService>());

    private static IChannelIdentityService Found(string twitchChannelId, string login) =>
        Lookup(TwitchUserLookup.Found(new TwitchUserIdentity(twitchChannelId, login)));

    private static IChannelIdentityService Lookup(TwitchUserLookup lookup)
    {
        var identityService = Substitute.For<IChannelIdentityService>();
        identityService.LookupByLoginAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(lookup);
        return identityService;
    }

    private async Task SeedChannelAsync(string name, string? twitchChannelId, bool isBotActive)
    {
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = name, TwitchChannelId = twitchChannelId, IsBotActive = isBotActive });
        await db.SaveChangesAsync();
    }

    // A joined test channel is left again, so the shared database's active-channel count and the
    // identity reconcile tests that scan it are not affected by this class.
    private async Task DeactivateAsync(string name)
    {
        await using var db = fixture.CreateDbContext();
        await db.Channels.Where(c => c.ChannelName == name).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsBotActive, false));
    }

    private async Task AssertNothingWrittenAsync(string channelName, string lockedId)
    {
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.ChannelName == channelName));
        Assert.Empty(await LoadAuditEntriesAsync(channelName));
        Assert.NotNull(await LoadLockedAtAsync(lockedId));
    }

    private async Task<DateTime?> LoadLockedAtAsync(string twitchChannelId)
    {
        await using var db = fixture.CreateDbContext();
        return await new BroadcasterChannelLockService(db).GetLockedAtUtcAsync(twitchChannelId);
    }

    private async Task<IReadOnlyList<AuditLogEntry>> LoadAuditEntriesAsync(string channelName)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking().Where(e => e.ChannelName == channelName).OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<JsonElement> LoadJoinDetailsAsync(string channelName)
    {
        var entry = Assert.Single(await LoadAuditEntriesAsync(channelName));
        Assert.Equal(AuditActions.ChannelJoin, entry.Action);
        Assert.NotNull(entry.DetailsJson);
        return JsonDocument.Parse(entry.DetailsJson).RootElement.Clone();
    }
}
