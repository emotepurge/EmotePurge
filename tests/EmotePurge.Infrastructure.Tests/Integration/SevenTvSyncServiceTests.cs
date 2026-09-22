using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Covers the EventAPI delta path (ApplyEmoteSetUpdateAsync) against real Postgres — the unique
// (ChannelId, SevenTvEmoteId) index and the change-tracker-based no-op detection are exactly the
// parts an in-memory provider would fake away. The REST full-sync path is additionally covered
// where the two share behaviour (SevenTvUserId pass-through).
[Collection("Postgres")]
public class SevenTvSyncServiceTests(PostgresFixture fixture)
{
    private const string SetId = "64c9e0f0aa1234567890abcd";

    // A second, clearly distinct set for the switch cases below — neither id is a
    // substring of the other, so a log line can be attributed to one of them.
    private const string SwitchedSetId = "64c9e0f0aa1234567890beef";

    private static SevenTvEmoteSetDelta Delta(
        IReadOnlyList<SevenTvEmote>? pushed = null,
        IReadOnlyList<SevenTvEmote>? updated = null,
        IReadOnlyList<string>? pulledIds = null) =>
        new(pushed ?? [], updated ?? [], pulledIds ?? []);

    private static SevenTvSyncService CreateService(Persistence.AppDbContext db, EmoteMatchCache cache) =>
        new(db, Substitute.For<ISevenTvApiClient>(), cache, new DuplicateEmoteNameTracker(),
            new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

    // The REST answer a seeded channel would get back unchanged — same set, same emotes, same
    // image urls, so a sync over it is a true no-op.
    private static SevenTvSyncService CreateRestService(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        string emoteSetId,
        params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes))));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
    }

    // Same as CreateRestService, but with an explicit set capacity. Separate method because a
    // params array has to stay last, so the capacity could not be an optional parameter in front
    // of it without breaking every positional call above.
    private static SevenTvSyncService CreateRestServiceWithCapacity(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        string emoteSetId,
        int? capacity,
        params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes, capacity))));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
    }

    // As CreateRestService, but with a logger of the caller's choosing. Separate method for the
    // same reason CreateRestServiceWithCapacity is one: the params array has to stay last.
    private static SevenTvSyncService CreateRestServiceWithLogger(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        string emoteSetId,
        ILogger<SevenTvSyncService> logger,
        params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes))));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), logger);
    }

    private static string SeededImageUrl(string sevenTvId) => $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp";

    private static SevenTvEmote LiveEmote(string sevenTvId, string name) => new(sevenTvId, name, SeededImageUrl(sevenTvId));

    private async Task<Channel> SeedChannelAsync(Persistence.AppDbContext db, string name, params (string SevenTvId, string Name, bool Archived)[] emotes)
    {
        // Channels.TwitchChannelId carries a unique index — derive it from the (unique) test
        // channel name instead of sharing one literal across tests. IsBotActive = true because
        // SyncChannelAsync is only ever called for an active channel in real operation (the
        // periodic resync worker filters its channel list on this flag); ChannelEmoteSetObservationService
        // now leans on that precondition to tell a stale, racing sync apart from a real one.
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        foreach (var (sevenTvId, emoteName, archived) in emotes)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                SevenTvEmoteId = sevenTvId,
                Name = emoteName,
                ImageUrl = SeededImageUrl(sevenTvId),
                IsArchived = archived
            });
        }

        await db.SaveChangesAsync();
        return channel;
    }

    [Fact]
    public async Task SyncChannel_WithDuplicateActiveNames_CoalescesOntoOneIdAndRecordsTheCollision()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, "dupsync1");
        var cache = new EmoteMatchCache();
        var tracker = new DuplicateEmoteNameTracker();
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetId,
                [LiveEmote("7tv-dup-a", "Dup"), LiveEmote("7tv-dup-b", "Dup"), LiveEmote("7tv-solo", "Solo")]))));
        var service = new SevenTvSyncService(db, apiClient, cache, tracker, new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        await service.SyncChannelAsync(channel.ChannelName);

        var cached = cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId;
        Assert.Equal(2, cached.Count);
        Assert.True(cached.ContainsKey("Dup"));
        Assert.True(cached.ContainsKey("Solo"));
        // The collision was recorded during the sync: reporting the same set again is no change.
        Assert.False(tracker.Update(channel.ChannelName, ["Dup"]));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_Pushed_AddsEmoteAndRefreshesCache()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_pushed", ("e1", "existing", false));
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e2", "catJAM", "https://cdn/e2.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2");
        Assert.Equal("catJAM", row.Name);
        Assert.False(row.IsArchived);
        Assert.True(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.ContainsKey("catJAM"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_Pulled_ArchivesEmoteAndDropsItFromCache()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_pulled", ("e1", "keepme", false), ("e2", "removeme", false));
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetId, Delta(pulledIds: ["e2"]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2");
        Assert.True(row.IsArchived);
        var cached = cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId;
        Assert.True(cached.ContainsKey("keepme"));
        Assert.False(cached.ContainsKey("removeme"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_PushedArchivedEmote_Unarchives()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_unarchive", ("e1", "phoenix", true));
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e1", "phoenix", "https://cdn/e1.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.False(row.IsArchived);
        Assert.True(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.ContainsKey("phoenix"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_UpdatedRename_ReplacesNameInDbAndCache()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_rename", ("e1", "oldname", false));
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(updated: [new SevenTvEmote("e1", "newname", "https://cdn/e1.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.Equal("newname", row.Name);
        var cached = cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId;
        Assert.True(cached.ContainsKey("newname"));
        Assert.False(cached.ContainsKey("oldname"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_DeltaWipingWholeSet_IsSkippedAsImplausible()
    {
        // Same asymmetry as the empty-set guard in SyncChannelAsync (S3-12): one malformed delta
        // must not stop chat matching for the whole channel. The non-effect is the assertion.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_wipe",
            ("e1", "one", false), ("e2", "two", false), ("e3", "three", false));
        var service = CreateService(db, cache);
        cache.ReplaceChannel(channel.ChannelName, SetId, new Dictionary<string, string> { ["one"] = "x" });

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pulledIds: ["e1", "e2", "e3"]));

        Assert.Equal(SevenTvDeltaOutcome.ImplausibleSkipped, result.Outcome);
        Assert.Equal(0, await db.Emotes.CountAsync(e => e.ChannelId == channel.Id && e.IsArchived));
        Assert.True(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.ContainsKey("one"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_LegitimatelyEmptyChannel_IsNotBlockedByTheGuard()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_empty");
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetId, Delta(pulledIds: ["unknown"]));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, result.Outcome);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_ForNoLongerActiveSet_AppliesNothing()
    {
        // A still-live subscription on a set the channel switched away from must not write.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_setswitch", ("e1", "stale", false));
        channel.ActiveEmoteSetId = "0000e0f0aa1234567890ffff";
        await db.SaveChangesAsync();
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e9", "ghost", "https://cdn/e9.webp")]));

        Assert.Equal(SevenTvDeltaOutcome.SetNotActive, result.Outcome);
        Assert.False(await db.Emotes.AnyAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e9"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_UnknownChannel_ReportsChannelUnknown()
    {
        await using var db = fixture.CreateDbContext();
        var service = CreateService(db, new EmoteMatchCache());

        var result = await service.ApplyEmoteSetUpdateAsync(
            "wstest_missing", SetId, Delta(pulledIds: ["e1"]));

        Assert.Equal(SevenTvDeltaOutcome.ChannelUnknown, result.Outcome);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_SharedSet_AppliesToEachChannelWithoutIndexConflict()
    {
        // Two tracked channels sharing one active set is a real configuration (observed live);
        // the same delta lands as one row per channel on the (ChannelId, SevenTvEmoteId) index.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channelA = await SeedChannelAsync(db, "wstest_shared_a");
        var channelB = await SeedChannelAsync(db, "wstest_shared_b");
        var service = CreateService(db, cache);
        var delta = Delta(pushed: [new SevenTvEmote("e7", "sharedjam", "https://cdn/e7.webp")]);

        Assert.Equal(SevenTvDeltaOutcome.Applied, (await service.ApplyEmoteSetUpdateAsync(channelA.ChannelName, SetId, delta)).Outcome);
        Assert.Equal(SevenTvDeltaOutcome.Applied, (await service.ApplyEmoteSetUpdateAsync(channelB.ChannelName, SetId, delta)).Outcome);

        Assert.Equal(2, await db.Emotes.CountAsync(e => e.SevenTvEmoteId == "e7"));
        Assert.True(cache.GetChannelSnapshot(channelA.ChannelName).NameToEmoteId.ContainsKey("sharedjam"));
        Assert.True(cache.GetChannelSnapshot(channelB.ChannelName).NameToEmoteId.ContainsKey("sharedjam"));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_EmptyDispatchImageUrl_DoesNotOverwriteKnownUrl()
    {
        // Dispatch payloads have not been proven to always carry the image-host block.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_imageurl", ("e1", "pic", false));
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(updated: [new SevenTvEmote("e1", "pic_renamed", "")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.Equal("pic_renamed", row.Name);
        Assert.Equal("https://cdn.7tv.app/emote/e1/2x.webp", row.ImageUrl);
    }

    [Fact]
    public async Task SyncChannel_PassesSevenTvUserIdThrough()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        db.Channels.Add(new Channel { ChannelName = "wstest_syncresult", TwitchChannelId = "77", ActiveEmoteSetId = "" });
        await db.SaveChangesAsync();

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync("77", Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState(
                "7tv-user-77",
                new SevenTvEmoteSet(SetId, [new SevenTvEmote("e1", "hi", "https://cdn/e1.webp")]))));
        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync("wstest_syncresult");

        Assert.NotNull(result);
        Assert.Equal(SetId, result.EmoteSetId);
        Assert.Equal("7tv-user-77", result.SevenTvUserId);
    }

    // HasChanges is what keeps the unattended sync paths (periodic resync every 60s, EventAPI
    // follow-ups, boot recovery) from publishing channel.synced on every tick — a false positive
    // would make every open page refetch on a timer.
    [Fact]
    public async Task SyncChannel_NoOpResync_ReportsNoChanges()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_noop", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task SyncChannel_NewEmote_ReportsChange()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_new", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"), LiveEmote("e2", "fresh"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
        Assert.True(await db.Emotes.AnyAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2"));
    }

    // AK 78, first case. The worker has read the channel's rows and staged an insert for a new set
    // member; before its SaveChangesAsync runs, the Api (a vote session over this set) inserts the
    // very same (ChannelId, SevenTvEmoteId) as an archived row. The interleaving is forced by an
    // interceptor on the worker's own context, never hoped for by timing: the "Api" insert commits
    // on a second AppDbContext inside SavingChangesAsync, i.e. strictly after the read and strictly
    // before the worker's INSERT reaches Postgres.
    [Fact]
    public async Task SyncChannel_VoteSessionInsertsTheSameEmoteMidSync_RetriesOnceAndTheRowCarriesTheSyncState()
    {
        await using (var seedDb = fixture.CreateDbContext())
        {
            await SeedChannelAsync(seedDb, "wstest_ak78_retry", ("e1", "stable", false));
        }

        var addedAt = new DateTime(2026, 9, 20, 18, 0, 0, DateTimeKind.Utc);
        var interceptor = new ConcurrentEmoteInsertInterceptor(fixture, maxInsertions: 1);
        await using var db = fixture.CreateDbContext([interceptor]);
        var channel = await db.Channels.SingleAsync(c => c.ChannelName == "wstest_ak78_retry");
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();
        var service = CreateRestServiceWithLogger(
            db, cache, channel, SetId, logger,
            LiveEmote("e1", "stable"),
            new SevenTvEmote("race-x", "RaceX", SeededImageUrl("race-x"), addedAt));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
        var inserted = Assert.Single(interceptor.InsertedRows);
        Assert.Equal("race-x", inserted.SevenTvEmoteId);

        await using var verifyDb = fixture.CreateDbContext();
        var row = await verifyDb.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "race-x");
        // Both sides came through: the Api's row survived (its id is what a ballot references), and
        // it now carries what the sync decided — a live member of the active set is active.
        Assert.Equal(inserted.Id, row.Id);
        Assert.False(row.IsArchived);
        Assert.Null(row.ArchivedAt);
        Assert.Equal("RaceX", row.Name);
        Assert.Equal(addedAt, row.FirstSeenAt);

        // The rest of the sync's state landed with the retried save, including the observation
        // interval a first sync opens — a tracked-only row that the cleared change tracker would
        // have dropped had the retry not recorded it again.
        var reloadedChannel = await verifyDb.Channels.SingleAsync(c => c.Id == channel.Id);
        Assert.NotNull(reloadedChannel.LastSyncedAtUtc);
        Assert.Null(reloadedChannel.LastSyncFailureReason);
        Assert.True(await verifyDb.ChannelEmoteSetObservations.AnyAsync(
            o => o.ChannelId == channel.Id && o.SevenTvEmoteSetId == SetId && o.ObservedToUtc == null));
        Assert.Equal(row.Id, cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId["RaceX"]);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // AK 78, second case: the retry is exactly one. A second conflict inside the retry run — the Api
    // inserting another new member between the retry's read and its save — propagates as before.
    [Fact]
    public async Task SyncChannel_SecondConflictInTheRetryRun_Propagates()
    {
        await using (var seedDb = fixture.CreateDbContext())
        {
            await SeedChannelAsync(seedDb, "wstest_ak78_second", ("e1", "stable", false));
        }

        var interceptor = new ConcurrentEmoteInsertInterceptor(fixture, maxInsertions: 2);
        await using var db = fixture.CreateDbContext([interceptor]);
        var channel = await db.Channels.SingleAsync(c => c.ChannelName == "wstest_ak78_second");
        var service = CreateRestService(
            db, new EmoteMatchCache(), channel, SetId,
            LiveEmote("e1", "stable"), LiveEmote("race-a", "RaceA"), LiveEmote("race-b", "RaceB"));

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.SyncChannelAsync(channel.ChannelName));

        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        // One insert per attempt, and no third attempt: the first save lost race-a, the retry lost
        // race-b, and the exception left SyncChannelAsync from there.
        Assert.Equal(["race-a", "race-b"], interceptor.InsertedRows.Select(r => r.SevenTvEmoteId));
        Assert.Equal(2, interceptor.CallsWithStagedEmoteInserts);
    }

    [Fact]
    public async Task SyncChannel_Success_OpensAnObservationIntervalForTheReportedSet()
    {
        // Spec 4.3, T1.5: a successful sync opens an observation interval when none is open yet.
        // SeedChannelAsync already stamps ActiveEmoteSetId = SetId on the row (so the code's own
        // emoteSetSwitched comparison sees no change here) — the observation log's own decision is
        // independent of that column and looks only at whether it already has an open row, which it
        // never does on a channel's first sync.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_obs_open", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        await using var verify = fixture.CreateDbContext();
        var interval = await verify.ChannelEmoteSetObservations.AsNoTracking()
            .SingleAsync(o => o.ChannelId == channel.Id);
        Assert.Equal(SetId, interval.SevenTvEmoteSetId);
        Assert.Null(interval.ObservedToUtc);
        Assert.Null(interval.ClosedBy);
    }

    [Fact]
    public async Task SyncChannel_EmoteMissingFromLiveSet_ArchivesAndReportsChange()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_gone", ("e1", "stays", false), ("e2", "goes", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stays"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
        Assert.True(await db.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_SwitchedActiveSetWithIdenticalEmotes_ReportsChange()
    {
        // The emote rows stay byte-identical, but which set the channel runs is itself state the
        // UI shows (and the mass-delete panel needs) — so it counts as a change.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_setswitch", ("e1", "stable", false));
        const string OtherSetId = "1111e0f0aa1234567890abcd";
        var service = CreateRestService(db, cache, channel, OtherSetId, LiveEmote("e1", "stable"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.Equal(OtherSetId, result.EmoteSetId);
        Assert.True(result.HasChanges);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_ReportsNoChanges()
    {
        // The guard skips the write entirely; reporting a change would publish an event for a sync
        // that deliberately did nothing.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_change_emptyguard", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        Assert.False(await db.Emotes.Where(e => e.ChannelId == channel.Id).Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_TouchesNoObservationInterval()
    {
        // Spec 4.3/22, F9: while the #76 guard blocks a sync as an implausible wipe, the observation
        // log's call site (SevenTvSyncService.cs, right after the guard) is never reached — the open
        // interval, if any, stays exactly as it was. This is the "writes nothing" occasion.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_obs_guardblocked", ("e1", "stable", false));
        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = channel.Id,
            SevenTvEmoteSetId = SetId,
            ObservedFromUtc = DateTime.UtcNow.AddHours(-1),
        });
        await db.SaveChangesAsync();
        var service = CreateRestService(db, cache, channel, SetId);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        await using var verify = fixture.CreateDbContext();
        var interval = await verify.ChannelEmoteSetObservations.AsNoTracking()
            .SingleAsync(o => o.ChannelId == channel.Id);
        Assert.Equal(SetId, interval.SevenTvEmoteSetId);
        Assert.Null(interval.ObservedToUtc);
        Assert.Null(interval.ClosedBy);
    }

    [Fact]
    public async Task SyncChannel_PersistsTheSetCapacity()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity", ("e1", "stable", false));
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, 1500, LiveEmote("e1", "stable"));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(1500, await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_UnreportedCapacity_StaysNull()
    {
        // "7TV told us nothing" must stay distinguishable from a number — the UI shows no budget
        // bar at all rather than inventing a denominator.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity_null", ("e1", "stable", false));
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, null, LiveEmote("e1", "stable"));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_CapacityChangeAlone_ReportsNoChanges()
    {
        // A resized set is not a changed inventory. If this reported a change, the resize would
        // make every open page of the channel refetch for nothing.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity_nochange", ("e1", "stable", false));
        channel.ActiveEmoteSetCapacity = 1000;
        await db.SaveChangesAsync();
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, 1500, LiveEmote("e1", "stable"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        Assert.Equal(1500, await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_NoOpResync_StillRecordsTheSuccessfulSync()
    {
        // The whole reason LastSyncedAtUtc exists as its own column: a sync that changed nothing is
        // still a sync that reached 7TV. Emote.LastSyncedAt does not move here, so the admin view
        // would otherwise keep showing the last inventory change as if it were the last sync.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_lastsync_noop", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"));

        var before = DateTime.UtcNow;
        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        var lastSynced = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.LastSyncedAtUtc).SingleAsync();
        Assert.NotNull(lastSynced);
        Assert.True(lastSynced >= before);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_DoesNotRecordASuccessfulSync()
    {
        // The empty-set guard skips the reconciliation entirely, so nothing was verified against
        // 7TV's real state. Stamping it as a successful sync would report a channel as healthily
        // syncing precisely while its syncs are being thrown away.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_lastsync_emptyguard", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, SetId);

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.LastSyncedAtUtc).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_SetsFirstSeenAt_FromTheSetEntryTimestamp_OnInsert()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_insert", ("e1", "stable", false));
        var addedAt = new DateTime(2026, 7, 20, 8, 30, 0, DateTimeKind.Utc);
        var service = CreateRestService(
            db, cache, channel, SetId,
            LiveEmote("e1", "stable"),
            new SevenTvEmote("e2", "fresh", SeededImageUrl("e2"), addedAt));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(addedAt, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_BackfillsFirstSeenAt_ForARowThatPredatesTheColumn()
    {
        // Taking the set entry's own timestamp rather than "when we first saw it" is what makes the
        // date correct for emotes that have been in the set for months.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_backfill", ("e1", "stable", false));
        var addedAt = new DateTime(2026, 2, 3, 17, 0, 0, DateTimeKind.Utc);
        var service = CreateRestService(db, cache, channel, SetId, new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), addedAt));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(addedAt, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_BackfillingFirstSeenAt_ReportsNoChanges()
    {
        // The expensive mistake of this feature: counted as an inventory change, the first resync
        // after the deploy would publish channel.synced for every channel at once and make every
        // open page in the app refetch — for a date nobody's list is sorted by.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_nochange", ("e1", "stable", false));
        var service = CreateRestService(
            db, cache, channel, SetId,
            new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), new DateTime(2026, 2, 3, 17, 0, 0, DateTimeKind.Utc)));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task SyncChannel_MissingTimestamp_LeavesAKnownFirstSeenAtAlone()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_keep", ("e1", "stable", false));
        var known = new DateTime(2026, 1, 9, 6, 0, 0, DateTimeKind.Utc);
        await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.FirstSeenAt, known));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stable"));

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(known, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_CorrectsAKnownWrongFirstSeenAt_WithoutCountingItAsAChange()
    {
        // The column was historically filled from the v3 payload's timestamp — the emote's upload
        // date, not the set-entry date. A differing live value is therefore a correction, and like
        // the original backfill it must not count as an inventory change (channel.synced storm).
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_correct", ("e1", "stable", false));
        var wrong = new DateTime(2023, 5, 6, 12, 0, 0, DateTimeKind.Utc);
        await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.FirstSeenAt, wrong));
        var corrected = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var service = CreateRestService(
            db, cache, channel, SetId, new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), corrected));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        Assert.Equal(corrected, await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_PushedInsert_StampsFirstSeenAtWithNow()
    {
        // Dispatch payloads carry no trustworthy added-at (the v3-shaped timestamp is the upload
        // date), but a push IS the join moment — so the insert must be stamped with "now" instead
        // of arriving dateless and waiting a resync tick for v4.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_push", ("e1", "stable", false));
        var service = CreateService(db, cache);
        var before = DateTime.UtcNow.AddSeconds(-1);

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [LiveEmote("e2", "fresh")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var firstSeen = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.FirstSeenAt).SingleAsync();
        Assert.NotNull(firstSeen);
        Assert.InRange(firstSeen.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_NoOpDispatch_StaysNoChange_EvenWhenItCouldBackfill()
    {
        // The dispatch path decides NoChange vs Applied by asking the ChangeTracker, so it must not
        // backfill at all — the periodic resync fills the same gap within a tick without turning a
        // no-op dispatch into a live event for every open page.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_firstseen_dispatch", ("e1", "stable", false));
        var service = CreateService(db, cache);

        var result = await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName,
            SetId,
            Delta(updated: [new SevenTvEmote("e1", "stable", SeededImageUrl("e1"), new DateTime(2026, 2, 3, 17, 0, 0, DateTimeKind.Utc))]));

        Assert.Equal(SevenTvDeltaOutcome.NoChange, result.Outcome);
        Assert.Null(await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.FirstSeenAt).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_ArchivingViaRestReconcile_StampsArchivedAt()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_archivedat_rest", ("e1", "stays", false), ("e2", "goes", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "stays"));

        var before = DateTime.UtcNow;
        await service.SyncChannelAsync(channel.ChannelName);

        var archivedAt = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.True(archivedAt >= before);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_Pulled_StampsArchivedAt()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_archivedat_pull", ("e1", "keepme", false), ("e2", "removeme", false));
        var service = CreateService(db, cache);

        var before = DateTime.UtcNow;
        await service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetId, Delta(pulledIds: ["e2"]));

        var archivedAt = await db.Emotes
            .Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e2")
            .Select(e => e.ArchivedAt).SingleAsync();
        Assert.NotNull(archivedAt);
        Assert.True(archivedAt >= before);
    }

    [Fact]
    public async Task ApplyEmoteSetUpdate_UnarchivingPush_ClearsArchivedAt()
    {
        // The restore path (A6) relies on exactly this: a re-added emote heals to "not archived,
        // no archive date" through the ordinary sync, no dedicated endpoint involved.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_archivedat_clear", ("e1", "phoenix", true));
        await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ArchivedAt, DateTime.UtcNow.AddDays(-1)));
        var service = CreateService(db, cache);

        await service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetId, Delta(pushed: [new SevenTvEmote("e1", "phoenix", SeededImageUrl("e1"))]));

        var row = await db.Emotes.SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1");
        Assert.False(row.IsArchived);
        Assert.Null(row.ArchivedAt);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_DoesNotWriteCapacity()
    {
        // The empty-set guard returns before the set id is assigned, and the capacity has to share
        // that fate: a partial 7TV outage must not leave a wrong slot limit behind.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_capacity_guard", ("e1", "stable", false));
        channel.ActiveEmoteSetCapacity = 1000;
        await db.SaveChangesAsync();
        var service = CreateRestServiceWithCapacity(db, cache, channel, SetId, 7);

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Equal(1000, await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.ActiveEmoteSetCapacity).SingleAsync());
    }

    // ---- Warum ein Sync nichts geliefert hat (Issue #32) ----

    // Builds a sync service whose 7TV client fails with a given status, so the four outcomes of the
    // analysis can be driven one by one. Separate from CreateRestService, which only knows success.
    private static SevenTvSyncService CreateFailingService(
        Persistence.AppDbContext db,
        EmoteMatchCache cache,
        Channel channel,
        SevenTvLookupStatus status)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Failed(status));
        return new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);
    }

    [Theory]
    [InlineData(SevenTvLookupStatus.NoActiveEmoteSet, "no_active_emote_set")]
    [InlineData(SevenTvLookupStatus.NoSevenTvAccount, "no_seventv_account")]
    [InlineData(SevenTvLookupStatus.Unavailable, "seventv_unavailable")]
    public async Task SyncChannel_NoActiveEmoteSet_PersistsTheReason(SevenTvLookupStatus status, string expectedReason)
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, $"wstest_reason_{expectedReason}");
        var service = CreateFailingService(db, cache, channel, status);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);
        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc, c.LastSyncedAtUtc })
            .SingleAsync();
        Assert.Equal(expectedReason, row.LastSyncFailureReason);
        Assert.NotNull(row.LastSyncAttemptAtUtc);
        // LastSyncedAtUtc keeps meaning "last *successful* sync" — a failed attempt must not
        // advance it, or the admin drilldown would report a healthy sync for a broken channel.
        Assert.Null(row.LastSyncedAtUtc);
    }

    [Fact]
    public async Task SyncChannel_OkWithABlankSetId_IsRejectedBeforeAnythingIsWritten()
    {
        // 7TV can answer Ok with a set whose id field is missing; its DTO defaults that to an empty
        // string, so no lookup status catches it — the response parsed, it is just unusable. The
        // assertion that matters is not the reason but the *absence of writes*: carrying on used to
        // stamp the empty string onto ActiveEmoteSetId, which the delta path then compares against
        // every incoming dispatch and the usage page reads as "the first sync is still running".
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_blank_setid", ("e1", "stable", false));
        var service = CreateRestService(db, cache, channel, string.Empty, LiveEmote("e2", "fresh"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);

        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc, c.LastSyncedAtUtc, c.ActiveEmoteSetId })
            .SingleAsync();
        Assert.Equal("seventv_response_unusable", row.LastSyncFailureReason);
        Assert.NotNull(row.LastSyncAttemptAtUtc);
        // Nothing moved: the known set id survives, the success stamp is not advanced, and the emote
        // the unusable answer offered was never inserted. The cache is not empty, though — the warm
        // start (Task 1) already seeded it from Postgres before this call ran, and the rejection
        // below never touches the cache either way.
        Assert.Equal(SetId, row.ActiveEmoteSetId);
        Assert.Null(row.LastSyncedAtUtc);
        Assert.Equal(1, await db.Emotes.CountAsync(e => e.ChannelId == channel.Id));
        Assert.True(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.ContainsKey("stable"));
        Assert.False(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.ContainsKey("fresh"));
    }

    [Fact]
    public async Task SyncChannel_FailedAttempt_LeavesTheKnownSetAndItsEmotesAlone()
    {
        // The asymmetry that governs this whole area: a 7TV outage must not take the mass-delete
        // panel away or archive a channel's entire set. A failure records *why*, and nothing else.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_keeps", ("e1", "stable", false));
        var service = CreateFailingService(db, cache, channel, SevenTvLookupStatus.Unavailable);

        await service.SyncChannelAsync(channel.ChannelName);

        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.ActiveEmoteSetId, c.ActiveEmoteSetCapacity })
            .SingleAsync();
        Assert.Equal(SetId, row.ActiveEmoteSetId);
        Assert.False(await db.Emotes.Where(e => e.ChannelId == channel.Id)
            .Select(e => e.IsArchived).SingleAsync());
    }

    [Fact]
    public async Task SyncChannel_Success_ClearsAPreviousReason()
    {
        // The half that gets forgotten. A channel that activated an emote set on 7TV must stop being
        // told it has none — otherwise the empty state outlives the problem it describes.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_cleared");
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.NoActiveEmoteSet;
        channel.LastSyncAttemptAtUtc = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "fresh"));

        await service.SyncChannelAsync(channel.ChannelName);

        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc, c.LastSyncedAtUtc })
            .SingleAsync();
        Assert.Null(row.LastSyncFailureReason);
        Assert.NotNull(row.LastSyncedAtUtc);
        // Attempt and success are stamped from the same instant on a successful run, so the pair
        // reads as "current" rather than leaving a stale attempt behind an up-to-date success.
        Assert.Equal(row.LastSyncedAtUtc, row.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task SyncChannel_ImplausibleEmptyLiveSet_TouchesNeitherReasonNorAttempt()
    {
        // The empty-set guard (S3-12) deliberately makes no statement about the channel: it neither
        // succeeded nor failed, it declined to act. Writing an attempt timestamp there would claim a
        // reconciliation that never happened, and clearing a reason would hide a real one.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_reason_guard", ("e1", "stable", false));
        channel.LastSyncFailureReason = SevenTvSyncFailureReasons.Unavailable;
        await db.SaveChangesAsync();
        var service = CreateRestService(db, cache, channel, SetId);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        var row = await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => new { c.LastSyncFailureReason, c.LastSyncAttemptAtUtc })
            .SingleAsync();
        Assert.Equal("seventv_unavailable", row.LastSyncFailureReason);
        Assert.Null(row.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task SyncChannel_UnresolvableTwitchId_RecordsTheMissingAccount()
    {
        // The pre-step: a channel whose TwitchChannelId was never backfilled resolves it through
        // 7TV's own user search. Its "no match" answer is a missing 7TV account, not a network
        // problem, and used to vanish into the same null as everything else.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        db.Channels.Add(new Channel { ChannelName = "wstest_reason_noid", TwitchChannelId = null, ActiveEmoteSetId = "" });
        await db.SaveChangesAsync();

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.ResolveTwitchUserIdAsync("wstest_reason_noid", Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync("wstest_reason_noid");

        Assert.Null(result);
        Assert.Equal("no_seventv_account", await db.Channels
            .Where(c => c.ChannelName == "wstest_reason_noid")
            .Select(c => c.LastSyncFailureReason).SingleAsync());
    }

    // ---- Rename duplicates (Channel.TwitchChannelId unique index) ----

    [Fact]
    public async Task SyncChannel_ResolvedTwitchIdNotHeldByAnyOtherRow_BackfillsNormally()
    {
        // The counterpart to the duplicate guard below: LoadChannelByTwitchIdAsync misses (no other
        // row holds this id yet), so the ordinary first-time backfill must still go through.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = new Channel { ChannelName = "wstest_dup_nomatch", TwitchChannelId = null, ActiveEmoteSetId = "" };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.ResolveTwitchUserIdAsync("wstest_dup_nomatch", Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Ok("222"));
        apiClient.GetChannelStateForTwitchUserAsync("222", Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user-222", new SevenTvEmoteSet(SetId, [LiveEmote("e1", "hi")]))));
        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.Equal("222", await db.Channels.Where(c => c.Id == channel.Id)
            .Select(c => c.TwitchChannelId).SingleAsync());
    }

    // ---- Warm start: the match cache is seeded from Postgres before the first 7TV call ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncChannel_SevenTvUnavailable_EmptyCache_IsWarmedFromPostgres(bool hasTwitchChannelId)
    {
        // Both 7TV failure spots must warm the cache the same way: the set lookup failing on a
        // channel that already has a TwitchChannelId, and the identity resolve failing on a channel
        // that does not have one yet.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var name = $"wstest_warmstart_{(hasTwitchChannelId ? "withid" : "noid")}";
        var channel = await SeedChannelAsync(db, name, ("e1", "active", false), ("e2", "archived", true));
        if (!hasTwitchChannelId)
        {
            channel.TwitchChannelId = null;
            await db.SaveChangesAsync();
        }

        var apiClient = Substitute.For<ISevenTvApiClient>();
        if (hasTwitchChannelId)
        {
            apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
                .Returns(SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable));
        }
        else
        {
            apiClient.ResolveTwitchUserIdAsync(name, Arg.Any<CancellationToken>())
                .Returns(SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable));
        }

        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);
        var cached = cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId;
        Assert.Single(cached);
        Assert.True(cached.ContainsKey("active"));
        Assert.False(cached.ContainsKey("archived"));
    }

    [Fact]
    public async Task SyncChannel_SevenTvUnavailable_FilledCache_IsLeftUntouched()
    {
        // The existing asymmetry (a filled cache is never touched by a failed sync) must survive
        // the warm start: it must not run at all once the cache already holds something, even
        // content Postgres knows nothing about.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_warmstart_filled", ("e1", "postgresonly", false));
        var marker = new Dictionary<string, string> { ["markeronly"] = "marker-id" };
        cache.ReplaceChannel(channel.ChannelName, SetId, marker);
        var service = CreateFailingService(db, cache, channel, SevenTvLookupStatus.Unavailable);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);
        var cached = cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId;
        Assert.Single(cached);
        Assert.True(cached.ContainsKey("markeronly"));
        Assert.False(cached.ContainsKey("postgresonly"));
    }

    [Fact]
    public async Task SyncChannel_WarmStartDoesNotOutliveASuccessfulSync()
    {
        // A successful sync must still fully overwrite whatever the warm start installed.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_warmstart_success", ("e1", "oldname", false));
        var service = CreateRestService(db, cache, channel, SetId, LiveEmote("e2", "newname"));

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        var cached = cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId;
        Assert.True(cached.ContainsKey("newname"));
        Assert.False(cached.ContainsKey("oldname"));
    }

    [Fact]
    public async Task SyncChannel_ChannelWithoutRows_LeavesCacheEmpty()
    {
        // A channel with no emote rows at all (first-ever join) must not throw and must not fake up
        // cache content — the warm start is a no-op, and the sync proceeds as before.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_warmstart_norows");
        var service = CreateFailingService(db, cache, channel, SevenTvLookupStatus.Unavailable);

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);
        Assert.Empty(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId);
    }

    [Fact]
    public async Task SyncChannel_ResolvedTwitchIdAlreadyOwnedByAnotherRow_SkipsWithoutBackfillingOrPersisting()
    {
        // A rename leaves a second row under the new name without a TwitchChannelId. The resolve
        // step finds the same Twitch account the original row already holds — writing it into this
        // row via `??=` would collide with the unique index on Channel.TwitchChannelId.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        db.Channels.Add(new Channel { ChannelName = "wstest_dup_original", TwitchChannelId = "111", ActiveEmoteSetId = SetId });
        var renamed = new Channel { ChannelName = "wstest_dup_renamed", TwitchChannelId = null, ActiveEmoteSetId = "" };
        db.Channels.Add(renamed);
        await db.SaveChangesAsync();

        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.ResolveTwitchUserIdAsync("wstest_dup_renamed", Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Ok("111"));
        // Stubbed so the pre-fix path — which does not short-circuit before this call — reaches the
        // backfill and its SaveChangesAsync instead of failing earlier on an unconfigured NSubstitute
        // member; the real bug is the unique-index collision that follows, not a missing stub.
        apiClient.GetChannelStateForTwitchUserAsync("111", Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user-dup", new SevenTvEmoteSet(SetId, [LiveEmote("e1", "hi")]))));
        var service = new SevenTvSyncService(db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), NullLogger<SevenTvSyncService>.Instance);

        var result = await service.SyncChannelAsync(renamed.ChannelName);

        Assert.Null(result);
        Assert.False(await db.Emotes.AnyAsync(e => e.ChannelId == renamed.Id));
        Assert.Null(await db.Channels.Where(c => c.Id == renamed.Id)
            .Select(c => c.TwitchChannelId).SingleAsync());
    }

    // The set the match cache was built for, from the two places that fill it. Deliberately one
    // case over both: the point of rule 2 is that the two writers agree on where the id comes
    // from, and a switch between them is what makes a disagreement visible at all.
    [Fact]
    public async Task SyncChannel_PutsTheObservedSetIdIntoTheMatchCache_OnTheWarmStartAndOnTheSync()
    {
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_setid_travels", ("e1", "beforeswitch", false));

        // Warm start: 7TV is unreachable, so the cache is filled from the emote rows in Postgres
        // alone — and those rows belong to the set the channel row still names.
        var failing = CreateFailingService(db, cache, channel, SevenTvLookupStatus.Unavailable);
        Assert.Null(await failing.SyncChannelAsync(channel.ChannelName));
        var warmed = cache.GetChannelSnapshot(channel.ChannelName);
        Assert.Equal(SetId, warmed.EmoteSetId);
        Assert.True(warmed.NameToEmoteId.ContainsKey("beforeswitch"));

        // Sync: 7TV reports a different active set, and the snapshot follows it in lockstep with
        // the dictionary — the pair a chat message is counted under.
        var service = CreateRestService(db, cache, channel, SwitchedSetId, LiveEmote("e2", "afterswitch"));
        Assert.NotNull(await service.SyncChannelAsync(channel.ChannelName));
        var synced = cache.GetChannelSnapshot(channel.ChannelName);
        Assert.Equal(SwitchedSetId, synced.EmoteSetId);
        Assert.True(synced.NameToEmoteId.ContainsKey("afterswitch"));
        Assert.False(synced.NameToEmoteId.ContainsKey("beforeswitch"));
    }

    [Fact]
    public async Task RefreshMatchCache_LogsTheSetSwitchExactlyOnce_AndNotWhenTheSetStaysTheSame()
    {
        // The line is what makes the gap between a switch on 7TV and our noticing it measurable in
        // production, so it has to fire on the switch and stay quiet around it. Asserted through a
        // recording logger and matched on the two set ids and the two generation timestamps it has
        // to carry — not on its wording.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();
        var channel = await SeedChannelAsync(db, "wstest_setid_switchlog", ("e1", "beforeswitch", false));

        // Same set: the warm start populates the empty snapshot and the sync refreshes onto the
        // very set that was already there. Neither is a switch, and the second is the ordinary
        // resync tick — once a minute per channel, so a line here would be noise.
        var sameSet = CreateRestServiceWithLogger(db, cache, channel, SetId, logger, LiveEmote("e1", "beforeswitch"));
        Assert.NotNull(await sameSet.SyncChannelAsync(channel.ChannelName));
        Assert.Empty(SwitchLines(logger));
        var generationBefore = cache.GetChannelSnapshot(channel.ChannelName).GeneratedAtUtc;

        // Different set: exactly one line, naming both sets and both generations.
        var switched = CreateRestServiceWithLogger(db, cache, channel, SwitchedSetId, logger, LiveEmote("e2", "afterswitch"));
        Assert.NotNull(await switched.SyncChannelAsync(channel.ChannelName));
        var generationAfter = cache.GetChannelSnapshot(channel.ChannelName).GeneratedAtUtc;

        var line = Assert.Single(SwitchLines(logger));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(generationBefore.ToString(CultureInfo.InvariantCulture), line.Message, StringComparison.Ordinal);
        Assert.Contains(generationAfter.ToString(CultureInfo.InvariantCulture), line.Message, StringComparison.Ordinal);

        // And a refresh back onto the set that is now current adds nothing.
        var again = CreateRestServiceWithLogger(db, cache, channel, SwitchedSetId, logger, LiveEmote("e2", "afterswitch"));
        Assert.NotNull(await again.SyncChannelAsync(channel.ChannelName));
        Assert.Single(SwitchLines(logger));
    }

    // ---- Wechsel-Tests: counting across the cache swap (spec section 5, AK 14) ----
    //
    // Both tests below drive the exact per-message algorithm TwitchChatManager runs
    // (TwitchChatManager.cs:1024-1055) — one GetChannelSnapshot call per message, a hit keyed by
    // (resolved emote id, snapshot.EmoteSetId) — through the real cache and a real switch performed
    // by SyncChannelAsync against Postgres, not through TwitchLib (that stays live-verified, rule
    // 11/16). The counting dictionary is not EmoteUsageCounter: EmotePurge.Worker is not referenced
    // from this project, so the key (EmotePurge.Core.Services.UsageCounterKey) is used directly —
    // the same composite key the real counter buffers under.
    private static void CountHit(Dictionary<UsageCounterKey, int> counts, EmoteMatchSnapshot snapshot, string emoteName)
    {
        var key = new UsageCounterKey(snapshot.NameToEmoteId[emoteName], snapshot.EmoteSetId);
        counts[key] = counts.GetValueOrDefault(key) + 1;
    }

    [Fact]
    public async Task SwitchTest_MessagesAroundTheCacheSwap_SplitIntoTwoUsageKeys()
    {
        // AK 14, first case: an emote that survives the switch unchanged. Three messages — before,
        // during (after the swap, before any flush) and after — land as two keys: the first under
        // the old set, the other two under the new one, because the swap is the only boundary that
        // exists once it has happened.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_ak14_split", ("e1", "combo", false));
        var beforeSwitch = CreateRestService(db, cache, channel, SetId, LiveEmote("e1", "combo"));
        Assert.NotNull(await beforeSwitch.SyncChannelAsync(channel.ChannelName));
        var counts = new Dictionary<UsageCounterKey, int>();

        CountHit(counts, cache.GetChannelSnapshot(channel.ChannelName), "combo"); // before

        var afterSwitch = CreateRestService(db, cache, channel, SwitchedSetId, LiveEmote("e1", "combo"));
        Assert.NotNull(await afterSwitch.SyncChannelAsync(channel.ChannelName));

        CountHit(counts, cache.GetChannelSnapshot(channel.ChannelName), "combo"); // during
        CountHit(counts, cache.GetChannelSnapshot(channel.ChannelName), "combo"); // after

        var emoteId = await db.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e1")
            .Select(e => e.Id).SingleAsync();
        Assert.Equal(2, counts.Count);
        Assert.Equal(1, counts[new UsageCounterKey(emoteId, SetId)]);
        Assert.Equal(2, counts[new UsageCounterKey(emoteId, SwitchedSetId)]);
    }

    [Fact]
    public async Task SwitchTest_SameNamedEmoteAcrossTheSwitch_NeverCountsOnBothRows()
    {
        // AK 14, second case: two distinct 7TV emotes sharing a display name — "Stare" on row A
        // before the switch, row B after. The counter key is the resolved emote id, never the name,
        // so a hit for one row must never land on, or bleed into, the other row's key.
        await using var db = fixture.CreateDbContext();
        var cache = new EmoteMatchCache();
        var channel = await SeedChannelAsync(db, "wstest_ak14_stare");
        var beforeSwitch = CreateRestService(db, cache, channel, SetId, LiveEmote("stare-a", "Stare"));
        Assert.NotNull(await beforeSwitch.SyncChannelAsync(channel.ChannelName));
        var counts = new Dictionary<UsageCounterKey, int>();

        CountHit(counts, cache.GetChannelSnapshot(channel.ChannelName), "Stare"); // row A, before

        var afterSwitch = CreateRestService(db, cache, channel, SwitchedSetId, LiveEmote("stare-b", "Stare"));
        Assert.NotNull(await afterSwitch.SyncChannelAsync(channel.ChannelName));

        CountHit(counts, cache.GetChannelSnapshot(channel.ChannelName), "Stare"); // row B, after
        CountHit(counts, cache.GetChannelSnapshot(channel.ChannelName), "Stare"); // row B, after

        var rowAId = await db.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "stare-a")
            .Select(e => e.Id).SingleAsync();
        var rowBId = await db.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "stare-b")
            .Select(e => e.Id).SingleAsync();
        Assert.NotEqual(rowAId, rowBId);
        Assert.Equal(2, counts.Count);
        Assert.Equal(1, counts[new UsageCounterKey(rowAId, SetId)]);
        Assert.Equal(2, counts[new UsageCounterKey(rowBId, SwitchedSetId)]);
        // Never counted on both: neither row's id appears combined with the other switch state.
        Assert.False(counts.ContainsKey(new UsageCounterKey(rowAId, SwitchedSetId)));
        Assert.False(counts.ContainsKey(new UsageCounterKey(rowBId, SetId)));
    }

    // A switch line is the only one that has to name both sets at once, which identifies it without
    // pinning its prose.
    private static IReadOnlyList<(LogLevel Level, string Message)> SwitchLines(RecordingLogger<SevenTvSyncService> logger) =>
        [.. logger.Entries.Where(e =>
            e.Message.Contains(SetId, StringComparison.Ordinal)
            && e.Message.Contains(SwitchedSetId, StringComparison.Ordinal))];

    // The forced interleaving for AK 78. Fires inside the worker context's SavingChangesAsync — after
    // ReconcileAsync has read the rows and staged its inserts, before any INSERT is sent — and, for
    // the first maxInsertions saves that stage an Emote insert, commits one of those same keys from
    // a second AppDbContext first. The SQL mirrors VoteSessionService.UpsertSetSessionEmotesAsync:
    // an archived row with no archive date, inserted ON CONFLICT DO NOTHING.
    private sealed class ConcurrentEmoteInsertInterceptor(PostgresFixture fixture, int maxInsertions) : SaveChangesInterceptor
    {
        private readonly List<(string Id, string SevenTvEmoteId)> _insertedRows = [];

        public IReadOnlyList<(string Id, string SevenTvEmoteId)> InsertedRows => _insertedRows;

        public int CallsWithStagedEmoteInserts { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var staged = eventData.Context!.ChangeTracker.Entries<Emote>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .OrderBy(e => e.SevenTvEmoteId, StringComparer.Ordinal)
                .ToList();
            if (staged.Count == 0)
            {
                return result;
            }

            CallsWithStagedEmoteInserts++;
            if (_insertedRows.Count < maxInsertions)
            {
                var target = staged[0];
                var id = Guid.NewGuid().ToString();
                await using var apiDb = fixture.CreateDbContext();
                await apiDb.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Emotes" ("Id", "SevenTvEmoteId", "ChannelId", "Name", "ImageUrl", "IsArchived", "ArchivedAt", "FirstSeenAt", "LastSyncedAt")
                    VALUES ({id}, {target.SevenTvEmoteId}, {target.ChannelId}, {target.Name}, {target.ImageUrl}, true, NULL, NULL, {DateTime.UtcNow})
                    ON CONFLICT ("ChannelId", "SevenTvEmoteId") DO NOTHING
                    """, cancellationToken);
                _insertedRows.Add((id, target.SevenTvEmoteId));
            }

            return result;
        }
    }
}
