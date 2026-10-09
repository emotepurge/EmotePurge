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

// Chat-log backfill spec, child 1 (D37, EPIC AC 27): placeholder rows — archived rows created for an
// emote never observed in the active set — stay out of the REST leave detection, and lose the marker
// the moment the emote really enters the active set. Real Postgres, because the leave observations are
// a raw upsert inside the sync's own transaction.
[Collection("Postgres")]
public class SevenTvSyncServicePlaceholderTests(PostgresFixture fixture)
{
    // 26-character ULID-shaped ids: the observation upsert drops anything that is not 1–32
    // alphanumeric characters.
    private const string SetId = "01J94NYQR0000D15QN0PLACE01";
    private const string RestoreSetId = "01J94NYQR0000D15QN0PLACE02";

    private static readonly AuditActor Actor = new("100", "placeholdertester");

    // AC 2 / EPIC AC 27, with the regression guard: in one and the same REST resync of the active set,
    // 31 minutes after the ballot created its rows, the placeholder gets no observation and its tag
    // placement keeps counting, while a formerly active archived row (b2) and a row leaving right now
    // (b1) are both still observed. A second resync changes nothing.
    [Fact]
    public async Task Resync31MinutesAfterCreation_SkipsThePlaceholder_ButStillObservesFormerlyActiveRows()
    {
        var channel = await SeedChannelAsync("placeholder_ac2",
            ("phkeep1", Archived: false, ArchivedAt: null, EnteredAtUtc: null),
            ("phformer1", Archived: true, ArchivedAt: DateTime.UtcNow.AddDays(-2), EnteredAtUtc: DateTime.UtcNow.AddDays(-3)),
            ("phleave1", Archived: false, ArchivedAt: null, EnteredAtUtc: DateTime.UtcNow.AddDays(-3)));
        await EnsurePlaceholdersAsync(channel, "phballot1");
        await MoveEntryBackAsync(channel, "phballot1", TimeSpan.FromMinutes(31));
        var (tagId, operationId) = await SeedPlacementAsync(channel, "phballot1", DateTime.UtcNow.AddMinutes(-30));

        await SyncAsync(channel, Live("phkeep1"));

        Assert.Null(await LatestAsync(channel, "phballot1"));
        Assert.NotNull(await LatestAsync(channel, "phformer1"));
        Assert.NotNull(await LatestAsync(channel, "phleave1"));
        var entry = await ReadTagEntryAsync(channel, tagId);
        Assert.Equal((true, (Guid?)operationId), (entry.PlacedByThisTag, entry.PlacementOperationId));
        await using (var verify = fixture.CreateDbContext())
        {
            var placeholder = await LoadEmoteAsync(verify, channel, "phballot1");
            Assert.Equal((true, true, (DateTime?)null), (placeholder.IsArchived, placeholder.IsPlaceholder, placeholder.ArchivedAt));
        }

        await SyncAsync(channel, Live("phkeep1"));

        Assert.Null(await LatestAsync(channel, "phballot1"));
        Assert.True((await ReadTagEntryAsync(channel, tagId)).PlacedByThisTag);
    }

    // The control for the test above: the very same row shape without the marker — what a pre-marker
    // image left behind before the migration backfilled it — is read as a credible leave. This is the
    // false observation the marker exists to prevent.
    [Fact]
    public async Task Resync31MinutesAfterCreation_WithoutTheMarker_RecordsTheFalseLeave()
    {
        var channel = await SeedChannelAsync("placeholder_ac2_control",
            ("phkeep1", Archived: false, ArchivedAt: null, EnteredAtUtc: null));
        await EnsurePlaceholdersAsync(channel, "phballot1");
        await MoveEntryBackAsync(channel, "phballot1", TimeSpan.FromMinutes(31));
        await using (var unmark = fixture.CreateDbContext())
        {
            await unmark.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "phballot1")
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsPlaceholder, false));
        }

        await SyncAsync(channel, Live("phkeep1"));

        Assert.NotNull(await LatestAsync(channel, "phballot1"));
    }

    // AC 3, the sync half: the set becomes active, the REST resync un-archives the row in place and
    // clears the marker; once the credibility window after that entry is over, a real leave is
    // observed like any other (b1).
    [Fact]
    public async Task UnarchivedByTheResync_ClearsTheMarker_AndALaterLeaveIsObserved()
    {
        var channel = await SeedChannelAsync("placeholder_unarchive",
            ("phkeep1", Archived: false, ArchivedAt: null, EnteredAtUtc: null));
        await EnsurePlaceholdersAsync(channel, "phballot1");
        string placeholderId;
        await using (var read = fixture.CreateDbContext())
        {
            placeholderId = (await LoadEmoteAsync(read, channel, "phballot1")).Id;
        }

        await SyncAsync(channel, Live("phkeep1"), Live("phballot1"));

        await using (var verify = fixture.CreateDbContext())
        {
            var row = await LoadEmoteAsync(verify, channel, "phballot1");
            Assert.Equal((placeholderId, false, false), (row.Id, row.IsArchived, row.IsPlaceholder));
            Assert.Equal(1, await verify.Emotes.CountAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "phballot1"));
        }

        await MoveEntryBackAsync(channel, "phballot1", TimeSpan.FromMinutes(31));
        await SyncAsync(channel, Live("phkeep1"));

        Assert.NotNull(await LatestAsync(channel, "phballot1"));
        await using (var verify = fixture.CreateDbContext())
        {
            var row = await LoadEmoteAsync(verify, channel, "phballot1");
            Assert.Equal((true, false), (row.IsArchived, row.IsPlaceholder));
            Assert.NotNull(row.ArchivedAt);
        }
    }

    // The EventAPI path un-archives through the same UpsertEmote branch: a PUSH is an entry into the
    // active set and clears the marker too.
    [Fact]
    public async Task UnarchivedByAPushDispatch_ClearsTheMarker()
    {
        var channel = await SeedChannelAsync("placeholder_push",
            ("phkeep1", Archived: false, ArchivedAt: null, EnteredAtUtc: null));
        await EnsurePlaceholdersAsync(channel, "phballot1");

        await using (var db = fixture.CreateDbContext())
        {
            var result = await CreateService(db, RestAnswering(channel)).ApplyEmoteSetUpdateAsync(
                channel.ChannelName, SetId, new SevenTvEmoteSetDelta([Live("phballot1")], [], []));
            Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        }

        await using var verify = fixture.CreateDbContext();
        var row = await LoadEmoteAsync(verify, channel, "phballot1");
        Assert.Equal((false, false), (row.IsArchived, row.IsPlaceholder));
    }

    // AC 3, the restore half: the set-centric restore report un-archives the row and clears the
    // marker; a later real leave after the window is observed normally.
    [Fact]
    public async Task RestoredByTheSetCentricReport_ClearsTheMarker_AndALaterLeaveIsObserved()
    {
        var channel = await SeedChannelAsync("placeholder_restore", RestoreSetId,
            ("phkeep1", Archived: false, ArchivedAt: null, EnteredAtUtc: null));
        await EnsurePlaceholdersAsync(channel, "phballot1");

        await using (var db = fixture.CreateDbContext())
        {
            var emoteService = new EmoteService(
                db, NullLogger<EmoteService>.Instance, Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db));
            await emoteService.MarkRestoredInSetAsync(
                RestoreSetId, "owner-seven-tv-id", "placeholder_restore", "tw_placeholder_restore", ["phballot1"], null, Actor);
        }

        await using (var verify = fixture.CreateDbContext())
        {
            var row = await LoadEmoteAsync(verify, channel, "phballot1");
            Assert.Equal((false, false), (row.IsArchived, row.IsPlaceholder));
        }

        await MoveEntryBackAsync(channel, "phballot1", TimeSpan.FromMinutes(31));
        await SyncAsync(channel, RestoreSetId, Live("phkeep1"));

        Assert.NotNull(await LatestAsync(channel, "phballot1", RestoreSetId));
    }

    // The skip is (b2) only. An active row that still carries the marker — left over from an image
    // older than the marker, which un-archives without clearing it — was in the set when the pass
    // began, so its leave is a real one and (b1) records it.
    [Fact]
    public async Task ActiveRowWithAStaleMarker_LeavingAfterTheWindow_IsStillObserved()
    {
        var channel = await SeedChannelAsync("placeholder_stale_leave",
            ("phkeep1", Archived: false, ArchivedAt: null, EnteredAtUtc: null),
            ("phstale1", Archived: false, ArchivedAt: null, EnteredAtUtc: DateTime.UtcNow.AddMinutes(-31)));
        await MarkAsync(channel, "phstale1");

        await SyncAsync(channel, Live("phkeep1"));

        Assert.NotNull(await LatestAsync(channel, "phstale1"));
    }

    // The same stale marker on a row the REST answer still lists: listed in the active set means
    // observed there, so the resync clears it — without counting it as an inventory change.
    [Fact]
    public async Task ActiveRowWithAStaleMarker_ListedByTheResync_LosesTheMarker()
    {
        var channel = await SeedChannelAsync("placeholder_stale_heal",
            ("phkeep1", Archived: false, ArchivedAt: null, EnteredAtUtc: null),
            ("phstale1", Archived: false, ArchivedAt: null, EnteredAtUtc: DateTime.UtcNow.AddDays(-1)));
        // A first pass settles everything else a first sync writes (set observation, dates), so the
        // pass under test has nothing left to change but the marker.
        await SyncAsync(channel, Live("phkeep1"), Live("phstale1"));
        await MarkAsync(channel, "phstale1");

        var result = await SyncAsync(channel, Live("phkeep1"), Live("phstale1"));

        Assert.NotNull(result);
        Assert.False(result.HasChanges);
        await using var verify = fixture.CreateDbContext();
        Assert.False((await LoadEmoteAsync(verify, channel, "phstale1")).IsPlaceholder);
    }

    private static SevenTvSyncService CreateService(AppDbContext db, ISevenTvApiClient apiClient) =>
        new(db, apiClient, new EmoteMatchCache(), new DuplicateEmoteNameTracker(),
            new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(),
            new BroadcasterChannelLockService(db), new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System),
            NullLogger<SevenTvSyncService>.Instance);

    private static ISevenTvApiClient RestAnswering(Channel channel, string emoteSetId = SetId, params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes))));
        return apiClient;
    }

    private Task<SevenTvSyncResult?> SyncAsync(Channel channel, params SevenTvEmote[] liveEmotes) =>
        SyncAsync(channel, SetId, liveEmotes);

    // Every step on a fresh context, like separate worker ticks.
    private async Task<SevenTvSyncResult?> SyncAsync(Channel channel, string emoteSetId, params SevenTvEmote[] liveEmotes)
    {
        await using var db = fixture.CreateDbContext();
        return await CreateService(db, RestAnswering(channel, emoteSetId, liveEmotes)).SyncChannelAsync(channel.ChannelName);
    }

    // The rows a set-session ballot creates, through the very helper VoteSessionService calls.
    private async Task EnsurePlaceholdersAsync(Channel channel, params string[] sevenTvEmoteIds)
    {
        await using var db = fixture.CreateDbContext();
        var created = await ArchivedEmoteRowUpsert.EnsureRowsAsync(
            db, channel.Id, sevenTvEmoteIds.Select(id => new ArchivedEmoteRow(id, id, ImageUrl(id), FirstSeenAt: null)).ToList(),
            DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(sevenTvEmoteIds.Length, created);
    }

    // Time passes: the entry stamp is moved back rather than waited for.
    private async Task MoveEntryBackAsync(Channel channel, string sevenTvEmoteId, TimeSpan by)
    {
        await using var db = fixture.CreateDbContext();
        var enteredAt = DateTime.UtcNow - by;
        await db.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == sevenTvEmoteId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastEnteredSetAtUtc, enteredAt));
    }

    private async Task MarkAsync(Channel channel, string sevenTvEmoteId)
    {
        await using var db = fixture.CreateDbContext();
        await db.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == sevenTvEmoteId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsPlaceholder, true));
    }

    private async Task<DateTime?> LatestAsync(Channel channel, string sevenTvEmoteId, string emoteSetId = SetId)
    {
        await using var db = fixture.CreateDbContext();
        var latest = await EmoteSetLeaveObservations.LoadLatestAsync(db, channel.Id, emoteSetId, [sevenTvEmoteId], CancellationToken.None);
        return latest.TryGetValue(sevenTvEmoteId, out var observedAt) ? observedAt : null;
    }

    private async Task<EmoteTagEntryDto> ReadTagEntryAsync(Channel channel, long tagId)
    {
        await using var db = fixture.CreateDbContext();
        var tags = new EmoteTagService(db, Substitute.For<ITrackedEmoteSetMembershipService>(), Substitute.For<IForeignEmoteSetService>());
        return Assert.Single((await tags.ListEntriesAsync(channel.ChannelName, tagId, null)).Entries);
    }

    private static Task<Emote> LoadEmoteAsync(AppDbContext db, Channel channel, string sevenTvEmoteId) =>
        db.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == sevenTvEmoteId);

    private static string ImageUrl(string sevenTvId) => $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp";

    private static SevenTvEmote Live(string sevenTvId) => new(sevenTvId, sevenTvId, ImageUrl(sevenTvId));

    private Task<Channel> SeedChannelAsync(
        string name, params (string SevenTvId, bool Archived, DateTime? ArchivedAt, DateTime? EnteredAtUtc)[] emotes) =>
        SeedChannelAsync(name, SetId, emotes);

    private async Task<Channel> SeedChannelAsync(
        string name, string activeEmoteSetId, params (string SevenTvId, bool Archived, DateTime? ArchivedAt, DateTime? EnteredAtUtc)[] emotes)
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = activeEmoteSetId, IsBotActive = true };
        db.Channels.Add(channel);
        foreach (var (sevenTvId, archived, archivedAt, enteredAtUtc) in emotes)
        {
            db.Emotes.Add(new Emote
            {
                ChannelId = channel.Id,
                SevenTvEmoteId = sevenTvId,
                Name = sevenTvId,
                ImageUrl = ImageUrl(sevenTvId),
                IsArchived = archived,
                ArchivedAt = archivedAt,
                LastEnteredSetAtUtc = enteredAtUtc,
            });
        }

        await db.SaveChangesAsync();
        return channel;
    }

    // A play-in placement of the emote into the active set, registered at the given time.
    private async Task<(long TagId, Guid OperationId)> SeedPlacementAsync(Channel channel, string sevenTvEmoteId, DateTime registeredAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        var tag = new EmoteTag { ChannelId = channel.Id, Name = "Ballot", NormalizedName = "ballot", CreatedAtUtc = registeredAtUtc };
        db.EmoteTags.Add(tag);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        db.EmoteTagEntries.Add(new EmoteTagEntry
        {
            TagId = tag.Id,
            SevenTvEmoteId = sevenTvEmoteId,
            Alias = sevenTvEmoteId,
            ImageUrl = ImageUrl(sevenTvEmoteId),
            AddedAtUtc = registeredAtUtc,
        });
        db.EmoteTagOperations.Add(new EmoteTagOperation
        {
            OperationId = operationId,
            TagId = tag.Id,
            Kind = EmoteTagOperationKind.PlayIn,
            SevenTvEmoteSetId = SetId,
            RegisteredAtUtc = registeredAtUtc,
        });
        db.EmoteTagPlacements.Add(new EmoteTagPlacement
        {
            TagId = tag.Id,
            SevenTvEmoteId = sevenTvEmoteId,
            SevenTvEmoteSetId = SetId,
            PlacedAtUtc = registeredAtUtc,
            OperationId = operationId,
            RegisteredAtUtc = registeredAtUtc,
        });
        await db.SaveChangesAsync();
        return (tag.Id, operationId);
    }
}
