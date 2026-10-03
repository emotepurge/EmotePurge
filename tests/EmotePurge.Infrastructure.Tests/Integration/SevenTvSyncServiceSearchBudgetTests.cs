using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

/// <summary>
/// The Twitch-id resolution path behind its two gates — the shared 7TV search budget and the
/// per-channel backoff (design note <c>docs/Konzept-7TV-Such-Budget-2026-10-03.md</c>, 2.1/2.3).
/// Against real Postgres, because what is pinned is what gets written to the channel row.
/// </summary>
[Collection("Postgres")]
public class SevenTvSyncServiceSearchBudgetTests(PostgresFixture fixture)
{
    private const string SetId = "64c9e0f0aa1234567890beef";

    [Fact]
    public async Task ARefusedSearch_AsksNothing_AndWritesNoFailureReason()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedIdLessChannelAsync(db, "wstest_budget_refused");
        var harness = new Harness(db);
        harness.Budget.NextRefusal = SevenTvSearchRefusal.Blocked;

        var result = await harness.Service.SyncChannelAsync(channel.ChannelName);

        Assert.Null(result);
        Assert.Equal([SevenTvSearchConsumer.ChannelIdentity], harness.Budget.Charges);
        await harness.Client.DidNotReceive().ResolveTwitchUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        var row = await db.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id);
        Assert.Null(row.LastSyncFailureReason);
        Assert.Null(row.LastSyncAttemptAtUtc);

        // Nothing was asked, so nothing counts as a miss: the next tick may try again at once.
        Assert.True(harness.Backoff.IsDue(channel.Id, out _));
    }

    [Fact]
    public async Task AFailedResolution_BacksTheChannelOff_SoTheNextTickSpendsNoSearch()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedIdLessChannelAsync(db, "wstest_budget_nomatch");
        var harness = new Harness(db);
        harness.Client.ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));

        await harness.Service.SyncChannelAsync(channel.ChannelName);
        await harness.Service.SyncChannelAsync(channel.ChannelName);

        await harness.Client.Received(1).ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>());
        Assert.Single(harness.Budget.Charges);
        // The reason the first attempt recorded is still the one the UI shows.
        Assert.Equal(
            SevenTvSyncFailureReasons.NoSevenTvAccount,
            await db.Channels.AsNoTracking().Where(c => c.Id == channel.Id).Select(c => c.LastSyncFailureReason).SingleAsync());

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        await harness.Service.SyncChannelAsync(channel.ChannelName);

        await harness.Client.Received(2).ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAttemptThatThrowsAfterTheCharge_StillBacksTheChannelOff()
    {
        // The search is paid for once the permit is granted. If anything below throws — here the
        // failure record's save, cancelled while 7TV was answering — the channel must still be backed
        // off, or the next tick spends another search on the same answer.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedIdLessChannelAsync(db, "wstest_budget_throws");
        var harness = new Harness(db);
        using var cancellation = new CancellationTokenSource();
        harness.Client.ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.SyncChannelAsync(channel.ChannelName, cancellation.Token));

        Assert.False(harness.Backoff.IsDue(channel.Id, out _));
        await harness.Service.SyncChannelAsync(channel.ChannelName);
        Assert.Single(harness.Budget.Charges);
        await harness.Client.Received(1).ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARenameDuplicate_CountsAsAMiss()
    {
        // Resolution succeeds, but the id belongs to another row: a search was spent, nothing stored.
        // This is the case that used to cost a search a minute without recording any reason.
        await using var db = fixture.CreateDbContext();
        db.Channels.Add(new Channel { ChannelName = "wstest_budget_dup_original", TwitchChannelId = "tw_wstest_budget_dup", ActiveEmoteSetId = SetId });
        await db.SaveChangesAsync();
        var duplicate = await SeedIdLessChannelAsync(db, "wstest_budget_dup_renamed");
        var harness = new Harness(db);
        harness.Client.ResolveTwitchUserIdAsync(duplicate.ChannelName, Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Ok("tw_wstest_budget_dup"));

        await harness.Service.SyncChannelAsync(duplicate.ChannelName);

        Assert.False(harness.Backoff.IsDue(duplicate.Id, out _));
    }

    [Fact]
    public async Task RepeatedMisses_AreLoggedFromTheThird_ButNeverForAnExcludedChannel()
    {
        await using var db = fixture.CreateDbContext();
        var stuck = await SeedIdLessChannelAsync(db, "wstest_budget_stuck");
        var excluded = await SeedIdLessChannelAsync(db, "wstest_budget_excluded");
        var excludedChannelFilter = Substitute.For<IExcludedChannelFilter>();
        excludedChannelFilter.IsExcluded("880165").Returns(true);
        var logger = new RecordingLogger<SevenTvSyncService>();
        var harness = new Harness(db, excludedChannelFilter, logger);
        harness.Client.ResolveTwitchUserIdAsync(stuck.ChannelName, Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount));
        harness.Client.ResolveTwitchUserIdAsync(excluded.ChannelName, Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Ok("880165"));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await harness.Service.SyncChannelAsync(stuck.ChannelName);
            await harness.Service.SyncChannelAsync(excluded.ChannelName);
            harness.Clock.Advance(TimeSpan.FromHours(1));
        }

        await harness.Client.Received(3).ResolveTwitchUserIdAsync(excluded.ChannelName, Arg.Any<CancellationToken>());
        var missLines = logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("still unresolved", StringComparison.Ordinal))
            .ToList();
        var line = Assert.Single(missLines);
        Assert.Contains(stuck.ChannelName, line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Level > LogLevel.Debug && e.Message.Contains(excluded.ChannelName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASuccessfulResolution_ForgetsTheBackoff()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedIdLessChannelAsync(db, "wstest_budget_success");
        var harness = new Harness(db);
        harness.Backoff.RecordMiss(channel.Id);
        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        harness.Client.ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Ok("tw_wstest_budget_success"));
        harness.Client.GetChannelStateForTwitchUserAsync("tw_wstest_budget_success", Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetId, []))));

        var result = await harness.Service.SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.Equal(
            "tw_wstest_budget_success",
            await db.Channels.AsNoTracking().Where(c => c.Id == channel.Id).Select(c => c.TwitchChannelId).SingleAsync());
        Assert.Equal(1, harness.Backoff.RecordMiss(channel.Id).ConsecutiveMisses);
    }

    [Fact]
    public async Task AResolvedIdThatIsNeverStored_StillCountsAsAMiss()
    {
        // 7TV knows the account but it has no active set: the sync stops before the id is saved, so
        // without the provisional miss every tick would spend a fresh search on the same answer.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedIdLessChannelAsync(db, "wstest_budget_noset");
        var harness = new Harness(db);
        harness.Client.ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>())
            .Returns(SevenTvTwitchUserIdResult.Ok("tw_wstest_budget_noset"));
        harness.Client.GetChannelStateForTwitchUserAsync("tw_wstest_budget_noset", Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoActiveEmoteSet));

        await harness.Service.SyncChannelAsync(channel.ChannelName);
        await harness.Service.SyncChannelAsync(channel.ChannelName);

        await harness.Client.Received(1).ResolveTwitchUserIdAsync(channel.ChannelName, Arg.Any<CancellationToken>());
        Assert.False(harness.Backoff.IsDue(channel.Id, out _));
        Assert.Null(await db.Channels.AsNoTracking().Where(c => c.Id == channel.Id).Select(c => c.TwitchChannelId).SingleAsync());
    }

    [Fact]
    public async Task WarmingAnIdLessChannel_SpendsNoSearch()
    {
        // Boot recovery warms every channel before the first sync; the warm-up reads Postgres only.
        await using var db = fixture.CreateDbContext();
        var channel = await SeedIdLessChannelAsync(db, "wstest_budget_warm");
        var harness = new Harness(db);

        await harness.Service.WarmChannelAsync(channel.ChannelName);

        Assert.Empty(harness.Budget.Charges);
        Assert.Empty(harness.Client.ReceivedCalls());
        Assert.True(harness.Backoff.IsDue(channel.Id, out _));
    }

    [Fact]
    public async Task AChannelWithAStoredId_NeverTouchesTheSearchBudget()
    {
        await using var db = fixture.CreateDbContext();
        var channel = new Channel { ChannelName = "wstest_budget_hasid", TwitchChannelId = "tw_wstest_budget_hasid", ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        var harness = new Harness(db);
        harness.Budget.NextRefusal = SevenTvSearchRefusal.StoreUnavailable;
        harness.Client.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetId, []))));

        // A Redis outage refuses every search, and a channel that needs none syncs regardless.
        Assert.NotNull(await harness.Service.SyncChannelAsync(channel.ChannelName));
        Assert.Empty(harness.Budget.Charges);
    }

    private static async Task<Channel> SeedIdLessChannelAsync(AppDbContext db, string name)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = null, ActiveEmoteSetId = SetId, IsBotActive = true };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private sealed class Harness
    {
        public Harness(
            AppDbContext db,
            IExcludedChannelFilter? excludedChannelFilter = null,
            ILogger<SevenTvSyncService>? logger = null)
        {
            Clock = new HandWoundTimeProvider();
            Client = Substitute.For<ISevenTvApiClient>();
            Budget = new RecordingSevenTvSearchBudget();
            Backoff = new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), Clock);
            Service = new SevenTvSyncService(
                db,
                Client,
                new EmoteMatchCache(),
                new DuplicateEmoteNameTracker(),
                new ChannelEmoteSetObservationService(db),
                new ChannelSyncGate(),
                excludedChannelFilter ?? Substitute.For<IExcludedChannelFilter>(),
                Budget,
                Backoff,
                logger ?? NullLogger<SevenTvSyncService>.Instance);
        }

        public HandWoundTimeProvider Clock { get; }

        public ISevenTvApiClient Client { get; }

        public RecordingSevenTvSearchBudget Budget { get; }

        public TwitchIdResolutionBackoff Backoff { get; }

        public SevenTvSyncService Service { get; }
    }
}
