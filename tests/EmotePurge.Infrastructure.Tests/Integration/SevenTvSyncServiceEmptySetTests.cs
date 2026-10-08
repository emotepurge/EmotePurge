using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Issue #76: a set that really is empty must eventually be believed, while a single bad 7TV answer
// still must not archive the channel. Real Postgres, because what the guard protects is the emote
// rows and what accepting a zero changes is the channel row.
[Collection("Postgres")]
public class SevenTvSyncServiceEmptySetTests(PostgresFixture fixture)
{
    private const string OldSetId = "64c9e0f0aa1234567890abcd";
    private const string NewSetId = "01M1V5HDNEWSET00000000000";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly HandWoundTimeProvider _clock = new();

    private async Task<(Channel Channel, EmoteMatchCache Cache, EmptySetConfirmationTracker Tracker)> SeedAsync(AppDbContext db, string name, int confirmations = 3)
    {
        // The state the epic (#200) leaves behind for a synced channel: an open observation interval
        // for the active set, and usage counted against that set.
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = OldSetId, IsBotActive = true };
        db.Channels.Add(channel);
        var emote = new Emote
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = "e1",
            Name = "stable",
            ImageUrl = "https://cdn.7tv.app/emote/e1/2x.webp"
        };
        db.Emotes.Add(emote);
        db.ChannelEmoteSetObservations.Add(new ChannelEmoteSetObservation
        {
            ChannelId = channel.Id,
            SevenTvEmoteSetId = OldSetId,
            ObservedFromUtc = DateTime.UtcNow.AddDays(-3),
        });
        db.UsageStats.Add(new UsageStat { EmoteId = emote.Id, Date = Today, UseCount = 7, EmoteSetId = OldSetId });
        await db.SaveChangesAsync();

        var tracker = new EmptySetConfirmationTracker(new EmptySetConfirmationOptions { EmptySetConfirmations = confirmations }, _clock);
        return (channel, new EmoteMatchCache(), tracker);
    }

    private static Task<SevenTvSyncResult?> SyncAsync(
        AppDbContext db, Channel channel, EmoteMatchCache cache, IEmptySetConfirmationTracker tracker, string setId, params SevenTvEmote[] emotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(setId, emotes))));
        var service = new SevenTvSyncService(
            db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            tracker, new RecordingLogger<SevenTvSyncService>());
        return service.SyncChannelAsync(channel.ChannelName);
    }

    // A zero from v3 together with what v4 lists for the same set (RemoteEntryCount).
    private static Task<SevenTvSyncResult?> SyncZeroAsync(
        AppDbContext db, Channel channel, EmoteMatchCache cache, IEmptySetConfirmationTracker tracker, string setId, int? remoteEntryCount,
        RecordingLogger<SevenTvSyncService>? logger = null)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(setId, [], RemoteEntryCount: remoteEntryCount))));
        var service = new SevenTvSyncService(
            db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            tracker, logger ?? new RecordingLogger<SevenTvSyncService>());
        return service.SyncChannelAsync(channel.ChannelName);
    }

    private static Task<List<ChannelEmoteSetObservation>> ObservationsAsync(AppDbContext db, Channel channel) =>
        db.ChannelEmoteSetObservations.AsNoTracking().Where(o => o.ChannelId == channel.Id).OrderBy(o => o.Id).ToListAsync();

    private static Task<bool> IsArchivedAsync(AppDbContext db, Channel channel) =>
        db.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).Select(e => e.IsArchived).SingleAsync();

    [Fact]
    public async Task EmptyNewSet_IsAcceptedImmediately_AndTheSetIdIsUpdated()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_switch");

        var result = await SyncAsync(db, channel, cache, tracker, NewSetId);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
        Assert.Equal(NewSetId, result.EmoteSetId);
        Assert.True(await IsArchivedAsync(db, channel));
        Assert.Empty(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId);
        Assert.Equal(NewSetId, (await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id)).ActiveEmoteSetId);
    }

    [Fact]
    public async Task EmptySameSet_IsSkippedUntilTheNthSpacedZero()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_same");

        var first = await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        var second = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.False(first!.HasChanges);
        Assert.False(second!.HasChanges);
        Assert.False(await IsArchivedAsync(db, channel));
        Assert.Single(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.Keys, "stable");

        _clock.Advance(Tick);
        var third = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.True(third!.HasChanges);
        Assert.True(await IsArchivedAsync(db, channel));
        Assert.Empty(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId);
    }

    [Fact]
    public async Task BurstOfEmptySyncs_WithinTheSpacing_NeverConfirmsItself()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_burst");

        for (var i = 0; i < 10; i++)
        {
            await SyncAsync(db, channel, cache, tracker, OldSetId);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task NonEmptyAnswerInBetween_ResetsTheStreak()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_reset");
        var stable = new SevenTvEmote("e1", "stable", "https://cdn.7tv.app/emote/e1/2x.webp");

        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId, stable);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);

        // Two zeros since the reset: still held back.
        Assert.False(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task FreshTracker_RestartsTheCount_SoARestartOnlyDelaysAcceptance()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_restart");
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);

        var afterRestart = new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), _clock);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, afterRestart, OldSetId);
        Assert.False(await IsArchivedAsync(db, channel));

        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, afterRestart, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, afterRestart, OldSetId);
        Assert.True(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task HeldBackZero_LogsTheStreak()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, _, tracker) = await SeedAsync(db, "emptyset_log");
        var logger = new RecordingLogger<SevenTvSyncService>();
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(OldSetId, []))));
        var service = new SevenTvSyncService(
            db, apiClient, new EmoteMatchCache(), new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System), tracker, logger);

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Contains(logger.Entries, e => e.Message.Contains("1 of 3") && e.Message.Contains("v4 cross-check unavailable"));
    }

    [Theory]
    [InlineData("00000000000000000000000000")]
    [InlineData("")]
    public async Task ImplausibleSetId_IsRejectedAsUnusable_BeforeAnythingIsWritten(string setId)
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, $"emptyset_badid_{setId.Length}");

        var result = await SyncAsync(db, channel, cache, tracker, setId);

        Assert.Null(result);
        var row = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);
        Assert.Equal(SevenTvSyncFailureReasons.ResponseUnusable, row.LastSyncFailureReason);
        Assert.Equal(OldSetId, row.ActiveEmoteSetId);
        Assert.False(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task PushDispatchForTheActiveSet_ResetsTheStreak()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_pushreset");
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);

        await ApplyAsync(db, channel, cache, tracker, new SevenTvEmoteSetDelta(
            [new SevenTvEmote("e1", "stable", "https://cdn.7tv.app/emote/e1/2x.webp")], [], []));

        _clock.Advance(Tick);
        var third = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.False(third!.HasChanges);
        Assert.False(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task PullOnlyDispatch_LeavesTheStreakAlone()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_pullkeeps");
        db.Emotes.Add(new Emote { ChannelId = channel.Id, SevenTvEmoteId = "e2", Name = "other", ImageUrl = "https://cdn.7tv.app/emote/e2/2x.webp" });
        await db.SaveChangesAsync();
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);

        await ApplyAsync(db, channel, cache, tracker, new SevenTvEmoteSetDelta([], [], ["e2"]));

        _clock.Advance(Tick);
        var third = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.True(third!.HasChanges);
        Assert.True(await db.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).AllAsync(e => e.IsArchived));
    }

    [Fact]
    public async Task FailedLookupBetweenZeros_ResetsTheStreak()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_failedlookup");
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);

        var failing = Substitute.For<ISevenTvApiClient>();
        failing.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable));
        var failingService = new SevenTvSyncService(
            db, failing, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            tracker, new RecordingLogger<SevenTvSyncService>());
        _clock.Advance(Tick);
        Assert.Null(await failingService.SyncChannelAsync(channel.ChannelName));

        // Two zeros, a failure, a zero: not three in a row.
        _clock.Advance(Tick);
        var third = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.False(third!.HasChanges);
        Assert.False(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task UnusableResponseBetweenZeros_ResetsTheStreak()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_unusable");
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);

        _clock.Advance(Tick);
        Assert.Null(await SyncAsync(db, channel, cache, tracker, "00000000000000000000000000"));

        _clock.Advance(Tick);
        var third = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.False(third!.HasChanges);
        Assert.False(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task ZeroContradictedByV4_IsSkippedWithoutAFailureReason_AndResetsTheStreak()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_v4veto");
        var logger = new RecordingLogger<SevenTvSyncService>();
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);

        _clock.Advance(Tick);
        var vetoed = await SyncZeroAsync(db, channel, cache, tracker, OldSetId, remoteEntryCount: 5, logger);

        Assert.NotNull(vetoed);
        Assert.False(vetoed.HasChanges);
        Assert.Equal(OldSetId, vetoed.EmoteSetId);
        Assert.Equal("7tv-user", vetoed.SevenTvUserId);
        Assert.False(await IsArchivedAsync(db, channel));
        Assert.Single(cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.Keys, "stable");
        var row = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);
        Assert.Null(row.LastSyncFailureReason);
        Assert.Equal(OldSetId, row.ActiveEmoteSetId);
        Assert.Contains(logger.Entries, e => e.Message.Contains("v4 lists 5 entries"));

        // Without the reset the first of these would have been the third zero in a row.
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        Assert.False(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task ZeroOnANewSetContradictedByV4_HoldsTheSwitchBack()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_v4vetoswitch");

        var result = await SyncZeroAsync(db, channel, cache, tracker, NewSetId, remoteEntryCount: 3);

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        Assert.False(await IsArchivedAsync(db, channel));
        var row = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);
        Assert.Equal(OldSetId, row.ActiveEmoteSetId);
        Assert.Null(row.LastSyncFailureReason);
    }

    [Fact]
    public async Task ZeroContradictedByV4_IsHeldBackEvenWithASingleConfirmation()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_v4vetoone", confirmations: 1);

        var vetoed = await SyncZeroAsync(db, channel, cache, tracker, OldSetId, remoteEntryCount: 1);
        Assert.False(vetoed!.HasChanges);
        Assert.False(await IsArchivedAsync(db, channel));

        var accepted = await SyncZeroAsync(db, channel, cache, tracker, OldSetId, remoteEntryCount: 0);
        Assert.True(accepted!.HasChanges);
        Assert.True(await IsArchivedAsync(db, channel));
    }

    // End to end with the real client: v3 lists nothing, v4 page 1 lists an entry, page 2 fails.
    // The partial count still vetoes the zero, for the same set and for a new set id alike.
    [Theory]
    [InlineData(OldSetId)]
    [InlineData(NewSetId)]
    public async Task ZeroWithPartialV4Evidence_IsHeldBack(string setId)
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, $"emptyset_v4part_{setId[..4].ToLowerInvariant()}", confirmations: 1);
        var v4Calls = 0;
        var handler = new DelegatingStub(request =>
            request.RequestUri!.AbsolutePath.Contains("/v4/gql", StringComparison.Ordinal)
                ? v4Calls++ == 0
                    ? Json("""{"data":{"emote_sets":{"emote_set":{"emotes":{"page_count":2,"items":[{"added_at":null,"emote":{"id":"e1"}}]}}}}}""")
                    : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                : Json($$$"""{"emote_set":{"id":"{{{setId}}}","capacity":600},"user":{"id":"7tv-user","connections":[]}}"""));
        var apiClient = new SevenTvApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://7tv.io/v3/") },
            new RecordingRateLimitTelemetry(), new RecordingForeignUpstreamRequestBudget(), new RecordingSevenTvSearchBudget(), new RecordingLogger<SevenTvApiClient>());
        var service = new SevenTvSyncService(
            db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            tracker, new RecordingLogger<SevenTvSyncService>());

        var result = await service.SyncChannelAsync(channel.ChannelName);

        Assert.False(result!.HasChanges);
        Assert.False(await IsArchivedAsync(db, channel));
        var row = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);
        Assert.Equal(OldSetId, row.ActiveEmoteSetId);
        Assert.Null(row.LastSyncFailureReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task ZeroWithoutAV4Contradiction_CountsTowardTheStreak(int? remoteEntryCount)
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, $"emptyset_v4none_{remoteEntryCount?.ToString() ?? "null"}");

        await SyncZeroAsync(db, channel, cache, tracker, OldSetId, remoteEntryCount);
        _clock.Advance(Tick);
        await SyncZeroAsync(db, channel, cache, tracker, OldSetId, remoteEntryCount);
        Assert.False(await IsArchivedAsync(db, channel));

        _clock.Advance(Tick);
        var third = await SyncZeroAsync(db, channel, cache, tracker, OldSetId, remoteEntryCount);

        Assert.True(third!.HasChanges);
        Assert.True(await IsArchivedAsync(db, channel));
    }

    [Fact]
    public async Task AcceptedSwitch_PersistsTheNewSetCapacity()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, _, tracker) = await SeedAsync(db, "emptyset_capacity");
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(NewSetId, [], 750))));
        var service = new SevenTvSyncService(
            db, apiClient, new EmoteMatchCache(), new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System), tracker, new RecordingLogger<SevenTvSyncService>());

        await service.SyncChannelAsync(channel.ChannelName);

        var row = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);
        Assert.Equal(NewSetId, row.ActiveEmoteSetId);
        Assert.Equal(750, row.ActiveEmoteSetCapacity);
    }

    // Interactions with the multiple-emote-sets epic (#200). An accepted zero runs the ordinary
    // write path, so the observation log, the match cache's set id and the per-set usage history
    // must read exactly as for any other sync — a switch to an empty set is a switch, a really
    // emptied set is not, and a held-back zero leaves no trace at all.

    [Fact]
    public async Task AcceptedEmptySwitch_IsBookedAsASetSwitch_AndTheOldSetKeepsItsHistory()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_epic_switch");

        await SyncAsync(db, channel, cache, tracker, NewSetId);

        var observations = await ObservationsAsync(db, channel);
        Assert.Equal(2, observations.Count);
        Assert.Equal(OldSetId, observations[0].SevenTvEmoteSetId);
        Assert.NotNull(observations[0].ObservedToUtc);
        Assert.Equal(ChannelEmoteSetObservationClosedBy.SetSwitch, observations[0].ClosedBy);
        Assert.Equal(NewSetId, observations[1].SevenTvEmoteSetId);
        Assert.Null(observations[1].ObservedToUtc);

        // Chat counts from here on are keyed by the new, empty set — and there is nothing to count.
        var snapshot = cache.GetChannelSnapshot(channel.ChannelName);
        Assert.Equal(NewSetId, snapshot.EmoteSetId);
        Assert.Empty(snapshot.NameToEmoteId);

        // The active-set view is empty; the old set's view is historical and still shows its
        // emote with the usage counted against it, archived like after any switch.
        var usage = new UsageStatQueryService(db);
        Assert.Empty(await usage.GetUsageContextAsync(channel.ChannelName, Today.AddDays(-6), Today));
        var oldSet = Assert.Single(await usage.GetUsageContextAsync(channel.ChannelName, Today.AddDays(-6), Today, OldSetId));
        Assert.Equal("stable", oldSet.EmoteName);
        Assert.Equal(7, oldSet.TotalUseCount);
        Assert.True(oldSet.IsArchived);
    }

    [Fact]
    public async Task ConfirmedEmptySameSet_IsNotBookedAsASetSwitch()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_epic_same");

        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        await SyncAsync(db, channel, cache, tracker, OldSetId);
        _clock.Advance(Tick);
        var third = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.True(third!.HasChanges);
        var observation = Assert.Single(await ObservationsAsync(db, channel));
        Assert.Equal(OldSetId, observation.SevenTvEmoteSetId);
        Assert.Null(observation.ObservedToUtc);
        Assert.Null(observation.ClosedBy);
        var snapshot = cache.GetChannelSnapshot(channel.ChannelName);
        Assert.Equal(OldSetId, snapshot.EmoteSetId);
        Assert.Empty(snapshot.NameToEmoteId);
    }

    [Theory]
    [InlineData(OldSetId, null)]
    [InlineData(NewSetId, 3)]
    public async Task HeldBackZero_WritesNeitherAnObservationNorTheSyncStamp(string setId, int? remoteEntryCount)
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, $"emptyset_epic_held_{setId[..4].ToLowerInvariant()}");
        var before = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);

        var result = await SyncZeroAsync(db, channel, cache, tracker, setId, remoteEntryCount);

        Assert.False(result!.HasChanges);
        var observation = Assert.Single(await ObservationsAsync(db, channel));
        Assert.Equal(OldSetId, observation.SevenTvEmoteSetId);
        Assert.Null(observation.ObservedToUtc);
        var after = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);
        Assert.Equal(OldSetId, after.ActiveEmoteSetId);
        Assert.Equal(before.LastSyncedAtUtc, after.LastSyncedAtUtc);
        Assert.Equal(before.LastSyncAttemptAtUtc, after.LastSyncAttemptAtUtc);
    }

    [Fact]
    public async Task SyncAfterAnAcceptedEmptySwitch_IsAPlainNoOp_AndTheFirstEmoteFillsTheSameInterval()
    {
        await using var db = fixture.CreateDbContext();
        var (channel, cache, tracker) = await SeedAsync(db, "emptyset_epic_after");
        await SyncAsync(db, channel, cache, tracker, NewSetId);
        var logger = new RecordingLogger<SevenTvSyncService>();

        _clock.Advance(Tick);
        var again = await SyncZeroAsync(db, channel, cache, tracker, NewSetId, remoteEntryCount: 0, logger);

        // Nothing active is known any more, so the guard is not involved: no held-back line, no
        // second switch, no change.
        Assert.False(again!.HasChanges);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("sync skipped"));
        Assert.Equal(2, (await ObservationsAsync(db, channel)).Count);

        _clock.Advance(Tick);
        var filled = await SyncAsync(db, channel, cache, tracker, NewSetId, new SevenTvEmote("e9", "fresh", "https://cdn.7tv.app/emote/e9/2x.webp"));

        Assert.True(filled!.HasChanges);
        var observations = await ObservationsAsync(db, channel);
        Assert.Equal(2, observations.Count);
        Assert.Null(observations[1].ObservedToUtc);
        Assert.Equal(["fresh"], cache.GetChannelSnapshot(channel.ChannelName).NameToEmoteId.Keys);
    }

    private static Task<SevenTvDeltaResult> ApplyAsync(
        AppDbContext db, Channel channel, EmoteMatchCache cache, IEmptySetConfirmationTracker tracker, SevenTvEmoteSetDelta delta)
    {
        var service = new SevenTvSyncService(
            db, Substitute.For<ISevenTvApiClient>(), cache, new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db),
            new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            tracker, new RecordingLogger<SevenTvSyncService>());
        return service.ApplyEmoteSetUpdateAsync(channel.ChannelName, OldSetId, delta);
    }

    private static HttpResponseMessage Json(string payload) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") };

    private sealed class DelegatingStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
