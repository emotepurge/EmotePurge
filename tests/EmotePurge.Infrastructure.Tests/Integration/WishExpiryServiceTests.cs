using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The wish expiry run (wish emotes K2, plan #484 T3) against real Postgres in a database of its own: the run reads
// every channel's due wishes, so it must not see (or move) the rows of other classes. Emptied before every test.
// The clock shows 2026-09-14 12:00 UTC, a whole second, so stamped times read back exactly. Bot mode, ownership and
// the threshold are written straight into the rows (before K4 nothing else sets them); the global bot availability
// is a counting double. The reconcile is a substitute unless a case names the real one.
//
// This file: setup, helpers and the decision rows (plan T3 cases 1 to 12, 16 to 18). The real writers and the lock
// races live in WishExpiryServiceTests.Races.cs (cases 13 to 15, 19 to 23).
[Collection("Postgres")]
public partial class WishExpiryServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "wish_expiry_tests";
    private const int SeededVersion = 3;

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private static readonly AuditActor Actor = new("4242", "wishmod");

    private readonly HandWoundTimeProvider _clock = new();
    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly IRedisPublisher _otherPublisher = Substitute.For<IRedisPublisher>();
    private readonly IWishReconcileService _reconcile = Substitute.For<IWishReconcileService>();
    private readonly CountingAvailability _availability = new();

    private DateTime Now => _clock.Now.UtcDateTime;

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
                    // A constant name, not user input; CREATE DATABASE cannot be parameterised.
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var migrate = CreateDb();
                await migrate.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDb();
        await reset.Database.ExecuteSqlRawAsync("TRUNCATE \"Wishes\", \"ChannelWishSettings\", \"AuditLogEntries\", \"Channels\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------------------------ row 1, selection

    // Case 1: the manual reminder. Only state, reason and the stamp move; one entry with exactly three details.
    [Fact]
    public async Task Manual_ExpiredWish_IsPresentedAsExpired_WithOneDueEntryAndOneEvent()
    {
        var channel = await SeedChannelAsync();
        var expiresAt = Now.AddMinutes(-1);
        var wishId = await SeedWishAsync(channel, expiresAt: expiresAt);
        var before = await LoadAsync(wishId);

        var result = await Sut().RunAsync();

        var after = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Expired, Now), (after.Status, after.StatusReason, after.StatusChangedAtUtc));
        Assert.Equal((before.ExpiresAtUtc, before.DecisionVersion, before.EndedAtUtc), (after.ExpiresAtUtc, after.DecisionVersion, after.EndedAtUtc));
        Assert.Equal((before.SlotId, before.BotOwned, (DateTime?)null), (after.SlotId, after.BotOwned, after.RemovalQueuedAtUtc));

        var entry = Assert.Single(await DueEntriesAsync(wishId));
        Assert.Equal((AuditActor.System.TwitchUserId, AuditActor.System.Login), (entry.ActorTwitchUserId, entry.ActorLogin));
        Assert.Equal((channel.Name, "wish", wishId.ToString(System.Globalization.CultureInfo.InvariantCulture)), (entry.ChannelName, entry.TargetType, entry.TargetId));
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        Assert.Equal(["wishId", "outcome", "expiresAtUtc"], details.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(wishId, details.RootElement.GetProperty("wishId").GetInt64());
        Assert.Equal(WishStatusReason.Expired, details.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(expiresAt, details.RootElement.GetProperty("expiresAtUtc").GetDateTime());

        Assert.Equal(1, EventsFor(channel));
        Assert.Equal(1, result.Presented);
        Assert.Equal(1, result.PresentedByReason[WishStatusReason.Expired]);
    }

    // Case 2: one second before the expiry nothing happens; exactly on it the wish is due.
    [Fact]
    public async Task NotYetExpired_IsUntouched_ButExactlyOnTheExpiry_IsDue()
    {
        var channel = await SeedChannelAsync();
        var later = await SeedWishAsync(channel, expiresAt: Now.AddSeconds(1));
        var onTheDot = await SeedWishAsync(channel, expiresAt: Now);
        var untouched = await SnapshotAsync(later);

        await Sut().RunAsync();

        Assert.Equal(untouched, await SnapshotAsync(later));
        Assert.Equal(WishStatus.Presented, (await LoadAsync(onTheDot)).Status);
        Assert.Empty(await DueEntriesAsync(later));
        Assert.Single(await DueEntriesAsync(onTheDot));
    }

    // Case 3 (AK27): a second pass over the same state changes no column, writes no entry, sends no event; for a
    // presented wish and for a queued one alike.
    [Fact]
    public async Task SecondRun_OverTheSameState_ChangesNothing()
    {
        var manual = await SeedChannelAsync();
        var presented = await SeedWishAsync(manual, expiresAt: Now.AddHours(-1));
        var bot = await SeedChannelAsync(botMode: true);
        var queued = await SeedWishAsync(bot, expiresAt: Now.AddHours(-1), botOwned: true);
        _availability.Available = true;
        await Sut().RunAsync();
        var snapshots = (await SnapshotAsync(presented), await SnapshotAsync(queued));
        var audits = await CountAuditsAsync();
        _publisher.ClearReceivedCalls();
        _clock.Advance(TimeSpan.FromMinutes(15));

        var second = await Sut().RunAsync();

        Assert.Equal(snapshots, (await SnapshotAsync(presented), await SnapshotAsync(queued)));
        Assert.Equal(audits, await CountAuditsAsync());
        Assert.Empty(PublishedEvents());
        Assert.Equal((0, 0, 0, 0), (second.Presented, second.Queued, second.HeldBack, second.Failed));
    }

    // Case 4 (AK27, finding 8): a presented wish stays extendable; past the new expiry the next entry follows.
    [Fact]
    public async Task PresentedWish_ExtendedThroughTheService_FallsDueAgain_WithASecondEntryForTheNewExpiry()
    {
        var channel = await SeedChannelAsync();
        var firstExpiry = Now.AddMinutes(-5);
        var wishId = await SeedWishAsync(channel, expiresAt: firstExpiry);
        await Sut().RunAsync();

        var extended = await Commands().ExtendAsync(channel.Name, wishId, Actor);
        Assert.Equal(WishOutcome.Success, extended.Outcome);
        var secondExpiry = (await LoadAsync(wishId)).ExpiresAtUtc!.Value;
        Assert.True(secondExpiry > Now);
        _clock.SetUtcNow(new DateTimeOffset(secondExpiry, TimeSpan.Zero).AddMinutes(1));

        await Sut().RunAsync();

        var wish = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.Expired), (wish.Status, wish.StatusReason));
        Assert.Equal([firstExpiry, secondExpiry], (await DueEntriesAsync(wishId)).Select(RecordedExpiry));
    }

    // ------------------------------------------------------------------------------------ row 7, queue

    // Case 5: bot mode, ownership, bot available, authoritative start, protection off: the removal is queued and the
    // wish stays active; a second pass leaves both fields and writes no entry.
    [Fact]
    public async Task Row7_QueuesTheRemoval_KeepsTheWishActive_AndASecondRunAddsNothing()
    {
        var channel = await SeedChannelAsync(botMode: true);
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), botOwned: true);
        var before = await LoadAsync(wishId);
        _availability.Available = true;

        var result = await Sut().RunAsync();

        var after = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Active, (string?)null, before.StatusChangedAtUtc), (after.Status, after.StatusReason, after.StatusChangedAtUtc));
        Assert.Equal((Now, (int?)SeededVersion, SeededVersion), (after.RemovalQueuedAtUtc, after.RemovalQueuedVersion, after.DecisionVersion));
        Assert.Equal(WishExpiryPolicy.QueuedOutcome, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
        Assert.Equal(1, EventsFor(channel));
        Assert.Equal((1, 0), (result.Queued, result.Presented));

        var queuedAt = after.RemovalQueuedAtUtc;
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Sut().RunAsync();

        var again = await LoadAsync(wishId);
        Assert.Equal((queuedAt, (int?)SeededVersion), (again.RemovalQueuedAtUtc, again.RemovalQueuedVersion));
        Assert.Single(await DueEntriesAsync(wishId));
    }

    // Case 6 (E25): extend drops the queue; after the new expiry the run queues again at the new version.
    [Fact]
    public async Task QueuedWish_ExtendedThroughTheService_LosesItsQueue_AndIsQueuedAgainWithTheNewVersion()
    {
        var channel = await SeedChannelAsync(botMode: true);
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), botOwned: true);
        _availability.Available = true;
        await Sut().RunAsync();

        Assert.Equal(WishOutcome.Success, (await Commands().ExtendAsync(channel.Name, wishId, Actor)).Outcome);
        var extended = await LoadAsync(wishId);
        Assert.Equal(((DateTime?)null, (int?)null, SeededVersion + 1), (extended.RemovalQueuedAtUtc, extended.RemovalQueuedVersion, extended.DecisionVersion));
        _clock.SetUtcNow(new DateTimeOffset(extended.ExpiresAtUtc!.Value, TimeSpan.Zero));

        await Sut().RunAsync();

        var requeued = await LoadAsync(wishId);
        Assert.Equal((Now, (int?)(SeededVersion + 1)), (requeued.RemovalQueuedAtUtc, requeued.RemovalQueuedVersion));
        Assert.Equal(2, (await DueEntriesAsync(wishId)).Count);
    }

    // ------------------------------------------------------------------------------------ rows 5 and 6, usage

    // Case 7 (AK25): with protection on, a counting gap presents even under heavy use, by a later start of counting or
    // a deactivated channel; with protection off the same wish is queued.
    [Theory]
    [InlineData("late-tracking", true, WishStatusReason.CoverageGap)]
    [InlineData("deactivated", true, WishStatusReason.CoverageGap)]
    [InlineData("late-tracking", false, WishExpiryPolicy.QueuedOutcome)]
    public async Task Row5_ACoverageGap_PresentsUnderProtection_AndIsQueuedWithout(string gap, bool protection, string expected)
    {
        var channel = await SeedChannelAsync(botMode: true, threshold: protection ? 1m : null);
        var (wishId, _) = await SeedUsedWishAsync(channel, startsAt: Now.AddDays(-30), perDay: [100]);
        var windowStart = Now.AddDays(-7);
        await UpdateChannelAsync(channel, c =>
        {
            if (gap == "late-tracking")
            {
                c.TrackingResumedAt = windowStart.AddDays(1);
            }
            else
            {
                c.DeactivatedAtUtc = Now.AddHours(-2);
            }
        });
        _availability.Available = true;

        await Sut().RunAsync();

        Assert.Equal(expected, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
    }

    // Case 8 (AK24): the threshold is the mean per day over all slots of the emote. Two aliases with 10 and 20 a day
    // on the last seven UTC days (all inside the 7-day window; the window's first, partial day stays empty) are 30 a
    // day: protected at 30, queued at 30.01.
    [Theory]
    [InlineData("30", WishStatusReason.Protected)]
    [InlineData("30.01", WishExpiryPolicy.QueuedOutcome)]
    public async Task Row6_TheMeanOverAllSlotsOfTheEmote_IsComparedWithTheThreshold(string threshold, string expected)
    {
        var channel = await SeedChannelAsync(botMode: true, threshold: decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture));
        var (wishId, _) = await SeedUsedWishAsync(channel, startsAt: Now.AddDays(-30), perDay: [10, 20]);
        _availability.Available = true;

        await Sut().RunAsync();

        Assert.Equal(expected, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
        var wish = await LoadAsync(wishId);
        Assert.Equal(expected == WishStatusReason.Protected ? WishStatus.Presented : WishStatus.Active, wish.Status);
    }

    // Case 9 (AK23) at the database: a 2-day stay is a 2-day window, the sum divided by 2. The usage lies on
    // 2026-09-13, the one UTC day wholly inside the window (2026-09-12 12:00 to 2026-09-14 12:00): 10 / 2 = 5.
    [Theory]
    [InlineData("5", WishStatusReason.Protected)]
    [InlineData("5.01", WishExpiryPolicy.QueuedOutcome)]
    public async Task TwoDayStay_DividesTheSumByTwo(string threshold, string expected)
    {
        var channel = await SeedChannelAsync(botMode: true, threshold: decimal.Parse(threshold, System.Globalization.CultureInfo.InvariantCulture));
        var startsAt = Now.AddDays(-2);
        var (wishId, slots) = await SeedUsedWishAsync(channel, startsAt, perDay: [], expiresAt: Now);
        await SeedUsageAsync(slots[0], DateOnly.FromDateTime(Now.AddDays(-1)), 10);
        _availability.Available = true;

        await Sut().RunAsync();

        Assert.Equal(expected, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
    }

    // Case 9, second half: 23 hours in the set is shorter than the minimum window and counts as a gap. The start is
    // written straight into the row (as a start moved by an authoritative date would leave it).
    [Fact]
    public async Task TwentyThreeHoursInTheSet_IsACoverageGap()
    {
        var channel = await SeedChannelAsync(botMode: true, threshold: 1m);
        var (wishId, _) = await SeedUsedWishAsync(channel, startsAt: Now.AddHours(-23), perDay: [100], expiresAt: Now);
        _availability.Available = true;

        await Sut().RunAsync();

        Assert.Equal(WishStatusReason.CoverageGap, OutcomeOf(Assert.Single(await DueEntriesAsync(wishId))));
    }

    // ------------------------------------------------------------------------------------ rows 2 to 4

    // Case 10: without ownership the heavy use does not matter; row 2 wins over row 6.
    [Fact]
    public async Task Row2_WithoutOwnership_IsNotBotOwned_EvenAboveTheThreshold()
    {
        var channel = await SeedChannelAsync(botMode: true, threshold: 1m);
        var (wishId, _) = await SeedUsedWishAsync(channel, startsAt: Now.AddDays(-30), perDay: [100], botOwned: false);
        _availability.Available = true;

        await Sut().RunAsync();

        var wish = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.NotBotOwned), (wish.Status, wish.StatusReason));
    }

    // Case 11: with an authoritative start, the registered default availability (never) and a suspended channel with
    // an available bot both present as bot_unavailable.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Row4_DefaultAvailabilityOrSuspendedChannel_IsBotUnavailable(bool suspended)
    {
        var channel = await SeedChannelAsync(botMode: true, suspendedReason: suspended ? "not_editor" : null);
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), botOwned: true);
        IWishBotAvailability availability = suspended ? new CountingAvailability { Available = true } : new WishBotNeverAvailable();

        await Sut(availability: availability).RunAsync();

        var wish = await LoadAsync(wishId);
        Assert.Equal((WishStatus.Presented, WishStatusReason.BotUnavailable, (DateTime?)null), (wish.Status, wish.StatusReason, wish.RemovalQueuedAtUtc));
    }

    // Case 12 (AK26), first half: a provisional start, a sync an hour ago and an expiry a minute ago: held back,
    // nothing written.
    [Fact]
    public async Task ProvisionalStart_WithAFreshSyncAndARecentExpiry_IsHeldBack()
    {
        var channel = await SeedChannelAsync(lastSyncedAt: Now.AddHours(-1));
        var wishId = await SeedWishAsync(channel, expiresAt: Now.AddMinutes(-1), authoritative: false);
        var before = await SnapshotAsync(wishId);

        var result = await Sut().RunAsync();

        Assert.Equal(before, await SnapshotAsync(wishId));
        Assert.Equal(0, await CountAuditsAsync());
        Assert.Empty(PublishedEvents());
        Assert.Equal((1, 0), (result.HeldBack, result.Presented));
    }

    // Case 12, second half: past either bound (or without any sync) the wish falls due, but a provisional start is
    // only ever presented: expired in manual mode, unconfirmed_start in bot mode (owned, bot available, protection
    // off), never queued. Counter-check: the same bot case with an authoritative start is queued.
    [Theory]
    [InlineData("sync-25h", false)]
    [InlineData("sync-25h", true)]
    [InlineData("never-synced", false)]
    [InlineData("never-synced", true)]
    [InlineData("expiry-25h", false)]
    [InlineData("expiry-25h", true)]
    public async Task ProvisionalStart_PastTheHold_IsPresented_NeverQueued(string variant, bool botMode)
    {
        DateTime? lastSynced = variant switch
        {
            "sync-25h" => Now.AddHours(-25),
            "never-synced" => null,
            _ => Now.AddHours(-1)
        };
        var expiresAt = variant == "expiry-25h" ? Now.AddHours(-25) : Now.AddMinutes(-1);
        var channel = await SeedChannelAsync(botMode: botMode, lastSyncedAt: lastSynced, setLastSynced: true);
        var provisional = await SeedWishAsync(channel, expiresAt: expiresAt, authoritative: false, botOwned: botMode);
        var authoritative = await SeedWishAsync(channel, expiresAt: expiresAt, authoritative: true, botOwned: botMode);
        _availability.Available = true;

        await Sut().RunAsync();

        var expected = botMode ? WishStatusReason.UnconfirmedStart : WishStatusReason.Expired;
        var wish = await LoadAsync(provisional);
        Assert.Equal((WishStatus.Presented, expected, (DateTime?)null), (wish.Status, wish.StatusReason, wish.RemovalQueuedAtUtc));
        Assert.Equal(expected, OutcomeOf(Assert.Single(await DueEntriesAsync(provisional))));
        Assert.Equal(
            botMode ? WishExpiryPolicy.QueuedOutcome : WishStatusReason.Expired,
            OutcomeOf(Assert.Single(await DueEntriesAsync(authoritative))));
    }

    // ------------------------------------------------------------------------------------ events, selection, availability

    // Case 16: one event per channel, however many wishes the run moved there.
    [Fact]
    public async Task TwoDueWishesInOneChannel_SendOneEvent_AndInTwoChannelsTwo()
    {
        var first = await SeedChannelAsync();
        await SeedWishAsync(first, expiresAt: Now.AddMinutes(-1));
        await SeedWishAsync(first, expiresAt: Now.AddMinutes(-2));
        var second = await SeedChannelAsync();
        await SeedWishAsync(second, expiresAt: Now.AddMinutes(-1));

        await Sut().RunAsync();

        Assert.Equal((1, 1), (EventsFor(first), EventsFor(second)));
        Assert.Equal(2, PublishedEvents().Count);
    }

    // Case 17: presented, queued, ended and active-without-expiry wishes are no candidates.
    [Fact]
    public async Task NoCandidates_PresentedQueuedEndedOrWithoutExpiry()
    {
        var channel = await SeedChannelAsync(botMode: true);
        var ids = new[]
        {
            await SeedWishAsync(channel, expiresAt: Now.AddDays(-1), status: WishStatus.Presented, reason: WishStatusReason.Expired),
            await SeedWishAsync(channel, expiresAt: Now.AddDays(-1), botOwned: true, queued: true),
            await SeedWishAsync(channel, expiresAt: Now.AddDays(-1), status: WishStatus.Removed),
            await SeedWishAsync(channel, expiresAt: null),
        };
        var before = await Task.WhenAll(ids.Select(SnapshotAsync));
        _availability.Available = true;

        var result = await Sut().RunAsync();

        Assert.Equal(before, await Task.WhenAll(ids.Select(SnapshotAsync)));
        Assert.Equal(0, await CountAuditsAsync());
        Assert.Empty(PublishedEvents());
        Assert.Equal((0, 0, 0, 0), (result.Presented, result.Queued, result.Skipped, result.Failed));
    }

    // Case 18: a purely manual pass never asks the availability; otherwise it is asked at most once per pass.
    [Fact]
    public async Task Availability_IsNeverAskedInAManualRun_AndAtMostOncePerRun()
    {
        var manual = await SeedChannelAsync();
        await SeedWishAsync(manual, expiresAt: Now.AddMinutes(-1), botOwned: true);
        await Sut().RunAsync();
        Assert.Equal(0, _availability.Calls);

        var bot = await SeedChannelAsync(botMode: true);
        var otherBot = await SeedChannelAsync(botMode: true);
        await SeedWishAsync(bot, expiresAt: Now.AddMinutes(-1), botOwned: true);
        await SeedWishAsync(bot, expiresAt: Now.AddMinutes(-2), botOwned: true);
        await SeedWishAsync(otherBot, expiresAt: Now.AddMinutes(-1), botOwned: true);
        _availability.Available = true;

        var result = await Sut().RunAsync();

        Assert.Equal(1, _availability.Calls);
        Assert.Equal(3, result.Queued);
    }

    // ------------------------------------------------------------------------------------ helpers

    private AppDbContext CreateDb() => fixture.CreateDbContext(DatabaseName);

    private WishExpiryService Sut(AppDbContext? db = null, IWishReconcileService? reconcile = null, IWishBotAvailability? availability = null) =>
        new(
            db ?? CreateDb(),
            reconcile ?? _reconcile,
            availability ?? _availability,
            new WishChangedPublisher(_publisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            NullLogger<WishExpiryService>.Instance);

    // The manager commands on their own publisher, so the expiry run's events stay countable.
    private WishService Commands() =>
        new(CreateDb(), new WishChangedPublisher(_otherPublisher, NullLogger<WishChangedPublisher>.Instance), _clock, NullLogger<WishService>.Instance);

    // The real reconcile in a scope of its own per call, also on its own publisher (it announces its own changes).
    private WishReconcileService RealReconcile()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDb());
        var provider = services.BuildServiceProvider();
        return new WishReconcileService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new WishChangedPublisher(_otherPublisher, NullLogger<WishChangedPublisher>.Instance),
            _clock,
            NullLogger<WishReconcileService>.Instance);
    }

    private List<LiveEvent> PublishedEvents() =>
        _publisher.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IRedisPublisher.PublishAsync))
            .Select(c => LiveEvent.TryParse((string?)c.GetArguments()[1]))
            .OfType<LiveEvent>()
            .ToList();

    private int EventsFor(Seeded channel) =>
        PublishedEvents().Count(e => e == new LiveEvent(LiveEvents.WishChanged, channel.Name));

    // A channel that counted the whole time and synced an hour ago, with a settings row when one is asked for.
    private async Task<Seeded> SeedChannelAsync(
        bool botMode = false,
        decimal? threshold = null,
        string? suspendedReason = null,
        DateTime? lastSyncedAt = null,
        bool setLastSynced = false)
    {
        var name = $"x{Guid.NewGuid():N}"[..18];
        await using var db = CreateDb();
        var channel = new Channel
        {
            ChannelName = name,
            TwitchChannelId = Guid.NewGuid().ToString("N")[..12],
            IsBotActive = true,
            CreatedAt = Now.AddDays(-400),
            LastSyncedAtUtc = setLastSynced ? lastSyncedAt : lastSyncedAt ?? Now.AddHours(-1)
        };
        db.Channels.Add(channel);
        if (botMode || threshold is not null || suspendedReason is not null)
        {
            db.ChannelWishSettings.Add(new ChannelWishSettings
            {
                Channel = channel,
                BotModeEnabled = botMode,
                BotSuspendedReason = suspendedReason,
                ProtectionThresholdPerDay = threshold
            });
        }

        await db.SaveChangesAsync();
        return new Seeded(channel.Id, name);
    }

    private async Task UpdateChannelAsync(Seeded channel, Action<Channel> change)
    {
        await using var db = CreateDb();
        change(await db.Channels.SingleAsync(c => c.Id == channel.Id));
        await db.SaveChangesAsync();
    }

    // An active wish (unless told otherwise), started 30 days before its expiry, with an earlier decision.
    private async Task<long> SeedWishAsync(
        Seeded channel,
        DateTime? expiresAt,
        bool authoritative = true,
        bool botOwned = false,
        bool queued = false,
        string status = WishStatus.Active,
        string? reason = null,
        string? emoteId = null,
        string? slotId = null,
        string alias = "Alias",
        DateTime? startsAt = null)
    {
        await using var db = CreateDb();
        var wish = new Wish
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = emoteId ?? EmoteId(),
            EmoteName = "Emote",
            Alias = alias,
            Source = WishSource.Manual,
            Status = status,
            StatusReason = reason,
            StatusChangedAtUtc = Now.AddDays(-30),
            CreatedAtUtc = Now.AddDays(-31),
            StartsAtUtc = startsAt ?? (expiresAt ?? Now).AddDays(-30),
            StartsAtAuthoritative = authoritative,
            ExpiresAtUtc = expiresAt,
            EndedAtUtc = WishStatus.IsEnded(status) ? Now.AddDays(-1) : null,
            SlotId = slotId,
            BotOwned = botOwned,
            DecisionVersion = SeededVersion
        };
        if (queued)
        {
            wish.RemovalQueuedAtUtc = Now.AddHours(-1);
            wish.RemovalQueuedVersion = SeededVersion;
        }

        db.Wishes.Add(wish);
        await db.SaveChangesAsync();
        return wish.Id;
    }

    // A due, owned, authoritative wish on an emote with one active slot per entry of perDay, each used that often on
    // each of the last seven UTC days (2026-09-08 to 2026-09-14, inside a 7-day window that starts 2026-09-07 12:00).
    private async Task<(long WishId, List<string> Slots)> SeedUsedWishAsync(
        Seeded channel, DateTime startsAt, int[] perDay, bool botOwned = true, DateTime? expiresAt = null)
    {
        var emote = await SeedEmoteAsync(channel);
        var slots = new List<string>();
        for (var i = 0; i < Math.Max(perDay.Length, 1); i++)
        {
            var slotId = await SeedSlotAsync(emote, $"Alias{i}", startsAt);
            slots.Add(slotId);
            if (i < perDay.Length)
            {
                for (var day = 0; day < 7; day++)
                {
                    await SeedUsageAsync(slotId, DateOnly.FromDateTime(Now.AddDays(-day)), perDay[i]);
                }
            }
        }

        var wishId = await SeedWishAsync(
            channel, expiresAt: expiresAt ?? Now.AddMinutes(-1), botOwned: botOwned, emoteId: emote.SevenTvEmoteId, slotId: slots[0],
            alias: "Alias0", startsAt: startsAt);
        return (wishId, slots);
    }

    private async Task<Emote> SeedEmoteAsync(Seeded channel)
    {
        await using var db = CreateDb();
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = "Emote",
            SevenTvEmoteId = EmoteId(),
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private async Task<string> SeedSlotAsync(Emote emote, string alias, DateTime addedAt, bool archived = false)
    {
        await using var db = CreateDb();
        var slot = new EmoteSlot
        {
            EmoteId = emote.Id,
            Name = alias,
            AddedAt = addedAt,
            AddedAtFromRest = true,
            IsArchived = archived,
            ArchivedAt = archived ? Now.AddMinutes(-10) : null
        };
        db.EmoteSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot.Id;
    }

    private async Task SeedUsageAsync(string slotId, DateOnly date, int useCount)
    {
        await using var db = CreateDb();
        var emoteId = await db.EmoteSlots.Where(s => s.Id == slotId).Select(s => s.EmoteId).SingleAsync();
        db.UsageStats.Add(new UsageStat { EmoteId = emoteId, SlotId = slotId, EmoteSetId = "set-1", Date = date, UseCount = useCount });
        await db.SaveChangesAsync();
    }

    private async Task<Wish> LoadAsync(long wishId)
    {
        await using var db = CreateDb();
        return await db.Wishes.AsNoTracking().SingleAsync(w => w.Id == wishId);
    }

    // Every column of the row, for "nothing changed".
    private async Task<string> SnapshotAsync(long wishId) => JsonSerializer.Serialize(await LoadAsync(wishId));

    private async Task<List<AuditLogEntry>> DueEntriesAsync(long wishId)
    {
        var targetId = wishId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var db = CreateDb();
        return await db.AuditLogEntries.AsNoTracking()
            .Where(a => a.Action == AuditActions.WishDue && a.TargetId == targetId)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    private async Task<int> CountAuditsAsync()
    {
        await using var db = CreateDb();
        return await db.AuditLogEntries.CountAsync();
    }

    private static string? OutcomeOf(AuditLogEntry entry)
    {
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        return details.RootElement.GetProperty("outcome").GetString();
    }

    private static DateTime RecordedExpiry(AuditLogEntry entry)
    {
        using var details = JsonDocument.Parse(entry.DetailsJson!);
        return details.RootElement.GetProperty("expiresAtUtc").GetDateTime();
    }

    private static string EmoteId() => Guid.NewGuid().ToString("N")[..24];

    private sealed record Seeded(string Id, string Name);

    // The global availability as a switch the test flips, counting every question.
    private sealed class CountingAvailability : IWishBotAvailability
    {
        public bool Available { get; set; }
        public int Calls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Available);
        }
    }
}
