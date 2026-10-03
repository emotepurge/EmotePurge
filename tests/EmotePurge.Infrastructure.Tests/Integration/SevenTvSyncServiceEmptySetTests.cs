using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
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

    private readonly HandWoundTimeProvider _clock = new();

    private async Task<(Channel Channel, EmoteMatchCache Cache, EmptySetConfirmationTracker Tracker)> SeedAsync(AppDbContext db, string name, int confirmations = 3)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = OldSetId };
        db.Channels.Add(channel);
        db.Emotes.Add(new Emote
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = "e1",
            Name = "stable",
            ImageUrl = "https://cdn.7tv.app/emote/e1/2x.webp"
        });
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
            db, apiClient, cache, new DuplicateEmoteNameTracker(), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(),
            tracker, new RecordingLogger<SevenTvSyncService>());
        return service.SyncChannelAsync(channel.ChannelName);
    }

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
        Assert.Empty(cache.GetChannelEmotes(channel.ChannelName));
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
        Assert.Single(cache.GetChannelEmotes(channel.ChannelName).Keys, "stable");

        _clock.Advance(Tick);
        var third = await SyncAsync(db, channel, cache, tracker, OldSetId);

        Assert.True(third!.HasChanges);
        Assert.True(await IsArchivedAsync(db, channel));
        Assert.Empty(cache.GetChannelEmotes(channel.ChannelName));
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
            db, apiClient, new EmoteMatchCache(), new DuplicateEmoteNameTracker(), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(), tracker, logger);

        await service.SyncChannelAsync(channel.ChannelName);

        Assert.Contains(logger.Entries, e => e.Message.Contains("1 of 3"));
    }
}
