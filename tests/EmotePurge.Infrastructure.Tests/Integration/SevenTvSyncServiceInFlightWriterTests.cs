using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The window the row gate cannot close: a writer that does not take ChannelSyncGate (the identity
// service, leave, purge — some of them in the Api process) commits while a sync is parked in the
// 7TV REST call. The sync must neither leave a match-cache entry under a login the row no longer
// carries nor throw for a row that is gone. The 7TV call is held open with a TaskCompletionSource,
// so the writer provably lands between the row load and the save.
[Collection("Postgres")]
public class SevenTvSyncServiceInFlightWriterTests(PostgresFixture fixture)
{
    private const string SetId = "64c9e0f0aa1234567890abcd";

    [Fact]
    public async Task SyncChannel_RenamedWhileTheSevenTvCallIsInFlight_CachesUnderTheNewLoginOnly()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "inflight_old");
        var cache = new EmoteMatchCache();
        var (service, entered, release) = CreateBlockedService(db, cache, channel);

        var syncTask = service.SyncChannelAsync("inflight_old");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using (var writer = fixture.CreateDbContext())
        {
            await writer.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ChannelName, "inflight_new"));
        }

        release.SetResult();
        var result = await syncTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(result);
        Assert.Equal("inflight_new", result.ChannelName);
        Assert.Contains("Alpha", cache.GetChannelSnapshot("inflight_new").NameToEmoteId.Keys);
        Assert.DoesNotContain("inflight_old", cache.GetCachedChannelNames());
    }

    [Fact]
    public async Task SyncChannel_DeactivatedWhileTheSevenTvCallIsInFlight_ReturnsNullAndLeavesNoCacheEntry()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "inflight_leave");
        var cache = new EmoteMatchCache();
        var (service, entered, release) = CreateBlockedService(db, cache, channel);

        var syncTask = service.SyncChannelAsync("inflight_leave");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using (var writer = fixture.CreateDbContext())
        {
            await writer.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsBotActive, false));
        }

        release.SetResult();

        Assert.Null(await syncTask.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain("inflight_leave", cache.GetCachedChannelNames());
    }

    [Fact]
    public async Task SyncChannel_DeletedWhileTheSevenTvCallIsInFlight_ReturnsNullInsteadOfThrowing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "inflight_purged");
        var cache = new EmoteMatchCache();
        var (service, entered, release) = CreateBlockedService(db, cache, channel);

        var syncTask = service.SyncChannelAsync("inflight_purged");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await using (var writer = fixture.CreateDbContext())
        {
            await writer.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        release.SetResult();

        Assert.Null(await syncTask.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain("inflight_purged", cache.GetCachedChannelNames());
    }

    [Fact]
    public async Task SyncChannel_OldLoginNowCarriedByAnotherActiveRow_LeavesThatRowsCacheEntryAlone()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "inflight_swap_a");
        var cache = new EmoteMatchCache();
        var (service, entered, release) = CreateBlockedService(db, cache, channel);

        var syncTask = service.SyncChannelAsync("inflight_swap_a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A login swap: the synced row moves away and a different active row takes its old login.
        await using (var writer = fixture.CreateDbContext())
        {
            await writer.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ChannelName, "inflight_swap_b"));
            writer.Channels.Add(new Channel { ChannelName = "inflight_swap_a", TwitchChannelId = "tw_inflight_swap_other", ActiveEmoteSetId = SetId, IsBotActive = true });
            await writer.SaveChangesAsync();
        }

        cache.ReplaceChannel("inflight_swap_a", "other-set", new Dictionary<string, string> { ["Live"] = "other-emote" });
        release.SetResult();
        await syncTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Live", cache.GetChannelSnapshot("inflight_swap_a").NameToEmoteId.Keys);
    }

    [Fact]
    public async Task IsRowVanishedFor_ForeignKeyViolationOnThisChannelsEmote_IsTrueOnlyForThatChannel()
    {
        await using var db = fixture.CreateDbContext();
        db.Emotes.Add(new Emote { ChannelId = "no-such-channel", SevenTvEmoteId = "fk-1", Name = "Fk", ImageUrl = "https://cdn.7tv.app/emote/fk-1/2x.webp" });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.True(SevenTvSyncService.IsRowVanishedFor(ex, "no-such-channel"));
        Assert.False(SevenTvSyncService.IsRowVanishedFor(ex, "some-other-channel"));
    }

    [Fact]
    public async Task IsRowVanishedFor_ForeignKeyViolationOnThisChannelsObservation_IsTrueOnlyForThatChannel()
    {
        // Epic #200's observation row rides the sync's save; once the channel row is gone, its insert
        // is what hits the channel foreign key.
        await using var db = fixture.CreateDbContext();
        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = "no-such-channel",
            SevenTvEmoteSetId = SetId,
            ObservedFromUtc = DateTime.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.True(SevenTvSyncService.IsRowVanishedFor(ex, "no-such-channel"));
        Assert.False(SevenTvSyncService.IsRowVanishedFor(ex, "some-other-channel"));
    }

    [Fact]
    public async Task IsRowVanishedFor_ConcurrencyFailureOfThisChannelsRow_IsTrueOnlyForThatChannel()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "inflight_concurrency");
        channel.LastSyncedAtUtc = DateTime.UtcNow;
        await using (var writer = fixture.CreateDbContext())
        {
            await writer.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());

        Assert.True(SevenTvSyncService.IsRowVanishedFor(ex, channel.Id));
        Assert.False(SevenTvSyncService.IsRowVanishedFor(ex, "some-other-channel"));
    }

    [Fact]
    public async Task SyncChannel_AfterAVanishedRow_AFollowingChannelOnTheSameContextStillSyncs()
    {
        await using var db = fixture.CreateDbContext();
        var vanishing = await SeedChannelAsync(db, "inflight_ctx_gone");
        var following = await SeedChannelAsync(db, "inflight_ctx_next");
        var cache = new EmoteMatchCache();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = SevenTvChannelStateResult.Ok(new SevenTvChannelState(
            "7tv-user",
            new SevenTvEmoteSet(SetId, [new SevenTvEmote("7tv-a", "Alpha", "https://cdn.7tv.app/emote/7tv-a/2x.webp")])));
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(vanishing.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return state;
            });
        apiClient.GetChannelStateForTwitchUserAsync(following.TwitchChannelId!, Arg.Any<CancellationToken>()).Returns(state);
        var service = new SevenTvSyncService(
            db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(),
            Substitute.For<IExcludedChannelFilter>(), new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System), new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System), NullLogger<SevenTvSyncService>.Instance);

        var first = service.SyncChannelAsync("inflight_ctx_gone");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await using (var writer = fixture.CreateDbContext())
        {
            await writer.Channels.Where(c => c.Id == vanishing.Id).ExecuteDeleteAsync();
        }

        release.SetResult();
        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(30)));

        var second = await service.SyncChannelAsync("inflight_ctx_next");

        Assert.NotNull(second);
        Assert.Contains("Alpha", cache.GetChannelSnapshot("inflight_ctx_next").NameToEmoteId.Keys);
    }

    private static (SevenTvSyncService Service, TaskCompletionSource Entered, TaskCompletionSource Release) CreateBlockedService(
        AppDbContext db, EmoteMatchCache cache, Channel channel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = SevenTvChannelStateResult.Ok(new SevenTvChannelState(
            "7tv-user",
            new SevenTvEmoteSet(SetId, [new SevenTvEmote("7tv-a", "Alpha", "https://cdn.7tv.app/emote/7tv-a/2x.webp")])));

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return state;
            });

        var service = new SevenTvSyncService(
            db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(),
            Substitute.For<IExcludedChannelFilter>(), new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System), new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System), NullLogger<SevenTvSyncService>.Instance);
        return (service, entered, release);
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string name)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }
}
