using EmotePurge.Core.Entities;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The sync-gate half of issue #54, against real Postgres: the row a sync reconciles can be renamed
// or merged away by TwitchIdentityReconcileWorker while that sync is still running, and the name
// the caller started from is then no longer the name of the row it is about to write.
//
// The blocked caller is produced by taking the row gate in the test itself rather than by racing two
// syncs — the effect under test is "a second caller cannot enter the row's critical section", and
// who is holding it is irrelevant to that.
//
// Every case here waits for ChannelSyncGate.RowGateWaitStarting instead of sleeping. The signal is
// what makes the cases mean anything: it fires only after the sync has loaded the row and only
// immediately before it waits for the gate this test is holding, so the handover below provably
// lands between the load and the ReloadAsync that has to notice it. A timed sleep proves neither
// half — too short on a loaded runner and the sync is still in the row query, in which case the
// rename makes the *load* miss and the case fails for the wrong reason; and a sync that never got
// that far would let the deletion cases pass without ReloadAsync ever running.
[Collection("Postgres")]
public class SevenTvSyncServiceRenameHandoverTests(PostgresFixture fixture)
{
    private const string SetId = "64c9e0f0aa1234567890abcd";

    [Fact]
    public async Task SyncChannel_RowGateHeld_WaitsAndThenWritesTheCacheUnderTheRenamedLogin()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "handover_old");
        var gate = new ChannelSyncGate();
        var cache = new EmoteMatchCache();
        var service = CreateService(db, cache, gate, channel.TwitchChannelId!, LiveEmote("7tv-a", "Alpha"));

        // Stands in for the sync that is already reconciling this row under the channel's other
        // login. The name gate for "handover_old" is free, so only the row gate can hold this back.
        var rowLease = await gate.AcquireByChannelIdAsync(channel.Id);
        var reachedRowGate = WatchForRowGate(gate, channel.Id);
        var syncTask = service.SyncChannelAsync("handover_old");
        await reachedRowGate.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(syncTask.IsCompleted);

        // The handover commits while the sync sits in the queue — the case that made the old
        // name-only gate write the match cache under a login Twitch no longer routes anywhere.
        await using (var renamer = fixture.CreateDbContext())
        {
            var row = await renamer.Channels.SingleAsync(c => c.Id == channel.Id);
            row.ChannelName = "handover_new";
            await renamer.SaveChangesAsync();
        }

        rowLease.Dispose();
        var result = await syncTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(result);
        Assert.True(cache.GetChannelSnapshot("handover_new").NameToEmoteId.ContainsKey("Alpha"));
        Assert.Empty(cache.GetChannelSnapshot("handover_old").NameToEmoteId);

        // Issue #60: the cache assertions above only prove the *service* followed the rename. Until
        // the new login also travelled out with the result, every worker caller went on keying its
        // EventAPI registration and its channel.synced publish on "handover_old".
        Assert.Equal("handover_new", result.ChannelName);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_RowRenamedWhileWaitingOnTheRowGate_ReportsTheNewLogin()
    {
        // The delta path's half of the same propagation gap (issue #60). Its caller
        // (SevenTvEventClient) takes the channel name straight out of the subscription registry, so
        // it is the *most* likely of the three to be holding a login the handover has already
        // retired.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "handover_delta_old");
        var gate = new ChannelSyncGate();
        var service = CreateService(db, new EmoteMatchCache(), gate, channel.TwitchChannelId!);

        var rowLease = await gate.AcquireByChannelIdAsync(channel.Id);
        var reachedRowGate = WatchForRowGate(gate, channel.Id);
        var deltaTask = service.ApplyEmoteSetUpdateAsync(
            "handover_delta_old", SetId, new SevenTvEmoteSetDelta([LiveEmote("7tv-d", "Delta")], [], []));
        await reachedRowGate.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(deltaTask.IsCompleted);

        await using (var renamer = fixture.CreateDbContext())
        {
            var row = await renamer.Channels.SingleAsync(c => c.Id == channel.Id);
            row.ChannelName = "handover_delta_new";
            await renamer.SaveChangesAsync();
        }

        rowLease.Dispose();
        var result = await deltaTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        Assert.Equal("handover_delta_new", result.ChannelName);
    }

    [Fact]
    public async Task SyncChannel_RowDeletedWhileWaitingOnTheRowGate_ReturnsNullInsteadOfThrowing()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "handover_merged");
        var gate = new ChannelSyncGate();
        var cache = new EmoteMatchCache();
        var service = CreateService(db, cache, gate, channel.TwitchChannelId!, LiveEmote("7tv-b", "Beta"));

        var rowLease = await gate.AcquireByChannelIdAsync(channel.Id);
        var reachedRowGate = WatchForRowGate(gate, channel.Id);
        var syncTask = service.SyncChannelAsync("handover_merged");
        await reachedRowGate.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(syncTask.IsCompleted);

        // What a merge does to the losing row. Writing on from here would mean a
        // DbUpdateConcurrencyException on the channel update, or a foreign-key violation on the
        // emote insert.
        await using (var merger = fixture.CreateDbContext())
        {
            await merger.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        rowLease.Dispose();

        Assert.Null(await syncTask.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(cache.GetChannelSnapshot("handover_merged").NameToEmoteId);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_RowDeletedWhileWaitingOnTheRowGate_ReportsChannelUnknown()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "handover_delta");
        var gate = new ChannelSyncGate();
        var service = CreateService(db, new EmoteMatchCache(), gate, channel.TwitchChannelId!);

        var rowLease = await gate.AcquireByChannelIdAsync(channel.Id);
        var reachedRowGate = WatchForRowGate(gate, channel.Id);
        var deltaTask = service.ApplyEmoteSetUpdateAsync(
            "handover_delta", SetId, new SevenTvEmoteSetDelta([LiveEmote("7tv-c", "Gamma")], [], []));
        await reachedRowGate.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(deltaTask.IsCompleted);

        await using (var merger = fixture.CreateDbContext())
        {
            await merger.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        rowLease.Dispose();

        var result = await deltaTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SevenTvDeltaOutcome.ChannelUnknown, result.Outcome);
        // The one outcome that provably has no login to report: there is no row left to read one
        // from, so the caller keeps addressing the registry key it already holds.
        Assert.Null(result.ChannelName);
    }

    /// <summary>
    /// Completes once the channel under test is parked at the row gate — row loaded, gate not yet
    /// entered. Registered before the sync starts, so the signal cannot be missed.
    /// </summary>
    private static Task WatchForRowGate(ChannelSyncGate gate, string channelId)
    {
        // RunContinuationsAsynchronously: the hook runs on the sync's own thread, and a synchronous
        // continuation would carry the rest of the test onto it instead of letting it proceed into
        // the wait.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.RowGateWaitStarting = waitingFor =>
        {
            if (waitingFor == channelId)
            {
                reached.TrySetResult();
            }
        };
        return reached.Task;
    }

    private static SevenTvEmote LiveEmote(string sevenTvId, string name) =>
        new(sevenTvId, name, $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp");

    private static SevenTvSyncService CreateService(
        AppDbContext db, EmoteMatchCache cache, ChannelSyncGate gate, string twitchUserId, params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(twitchUserId, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetId, liveEmotes))));
        return new SevenTvSyncService(
            db, apiClient, cache, new DuplicateEmoteNameTracker(), gate, NullLogger<SevenTvSyncService>.Instance);
    }

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string name)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = SetId };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }
}
