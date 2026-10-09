using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Issue #74: 7TV can list one emote id twice in a set under two alias names. The unique index
// (ChannelId, SevenTvEmoteId) leaves a single row, so both live entries hit it; without a dedup the
// second write flips the name back on every pass and the channel never settles.
[Collection("Postgres")]
public class SevenTvSyncServiceDuplicateEmoteIdTests(PostgresFixture fixture)
{
    private const string SetId = "01GV88A38G0006FW5TVZVMG507";
    private const string Url = "https://cdn.7tv.app/emote/dup/2x.webp";

    private static Task<SevenTvSyncResult?> SyncAsync(Infrastructure.Persistence.AppDbContext db, Channel channel, params SevenTvEmote[] emotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetId, emotes))));
        var service = new SevenTvSyncService(
            db, apiClient, new EmoteMatchCache(), new DuplicateEmoteNameTracker(), new ChannelEmoteSetObservationService(db), new ChannelSyncGate(),
            Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db), new RecordingSevenTvSearchBudget(),
            new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System), new RecordingLogger<SevenTvSyncService>());
        return service.SyncChannelAsync(channel.ChannelName);
    }

    [Fact]
    public async Task SameEmoteIdUnderTwoNames_SettlesOnTheLastName_AndStopsReportingChanges()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "dup_alias", TwitchChannelId = "tw_dup_alias", ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        var live = new[]
        {
            new SevenTvEmote("dup1", "inkorrekt", Url),
            new SevenTvEmote("dup1", "Nerdge", Url),
        };

        var first = await SyncAsync(db, channel, live);
        var stamp = await db.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).Select(e => e.LastSyncedAt).SingleAsync();
        await Task.Delay(20);
        var second = await SyncAsync(db, channel, live);

        var row = await db.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id);
        Assert.True(first!.HasChanges);
        Assert.False(second!.HasChanges);
        Assert.Equal("Nerdge", row.Name);
        Assert.Equal(stamp, row.LastSyncedAt);
    }

    [Fact]
    public async Task RowAlreadyHoldingTheLastName_FirstReconcileAfterTheFix_ReportsNoChange()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "dup_alias_deploy", TwitchChannelId = "tw_dup_alias_deploy", ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        // The pre-fix state: the old loop left the last alias stored.
        var stamp = DateTime.UtcNow.AddHours(-1);
        db.Emotes.Add(new Emote { ChannelId = channel.Id, SevenTvEmoteId = "dup1", Name = "Nerdge", ImageUrl = Url, LastSyncedAt = stamp });
        await db.SaveChangesAsync();

        var result = await SyncAsync(db, channel, new SevenTvEmote("dup1", "inkorrekt", Url), new SevenTvEmote("dup1", "Nerdge", Url));

        var row = await db.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id);
        Assert.False(result!.HasChanges);
        Assert.Equal("Nerdge", row.Name);
        Assert.Equal(stamp, row.LastSyncedAt, TimeSpan.FromMilliseconds(1));
    }
}
