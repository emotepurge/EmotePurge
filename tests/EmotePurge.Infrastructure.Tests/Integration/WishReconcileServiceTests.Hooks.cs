using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishReconcileService part 3 (T8): the hooks in the full sync, the EventAPI delta and the in-set report, and
// the trigger wrapper they go through. A throwing, a counting and the real reconcile stand behind the trigger.
public partial class WishReconcileServiceTests
{
    // ------------------------------------------------------------------------------------ the trigger

    [Fact]
    public async Task Trigger_SwitchOff_NeverCallsTheReconcile()
    {
        var reconcile = Substitute.For<IWishReconcileService>();
        var trigger = new WishReconcileTrigger(reconcile, new WishOptions { Enabled = false }, NullLogger<WishReconcileTrigger>.Instance);

        await trigger.TriggerAsync("channel-id");

        Assert.Empty(reconcile.ReceivedCalls());
    }

    [Fact]
    public async Task Trigger_AFailure_IsSwallowedAndLoggedOnce_WithoutAnythingButTheChannelId()
    {
        var reconcile = Substitute.For<IWishReconcileService>();
        reconcile.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var logger = new RecordingLogger<WishReconcileTrigger>();

        await new WishReconcileTrigger(reconcile, new WishOptions { Enabled = true }, logger).TriggerAsync("channel-id");

        var line = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("channel-id", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_ACancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reconcile = Substitute.For<IWishReconcileService>();
        reconcile.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cts.Token));
        var trigger = new WishReconcileTrigger(reconcile, new WishOptions { Enabled = true }, NullLogger<WishReconcileTrigger>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trigger.TriggerAsync("channel-id", cts.Token));
    }

    // ------------------------------------------------------------------------------------ full sync

    [Fact]
    public async Task FullSync_ThrowingReconcile_ChangesNeitherTheResultNorTheSlots_AndLogsOneWarning()
    {
        var reconcile = Substitute.For<IWishReconcileService>();
        reconcile.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var logger = new RecordingLogger<WishReconcileTrigger>();
        var trigger = new WishReconcileTrigger(reconcile, new WishOptions { Enabled = true }, logger);
        await using var db = fixture.CreateDbContext();
        var channel = await SlotSyncKit.SeedChannelAsync(db, $"h{Guid.NewGuid():N}"[..18]);
        Assert.NotNull(await SlotSyncKit.SyncOnAsync(db, channel, SlotSyncKit.Live("y", "other", SlotSyncKit.T1)));

        var result = await SlotSyncKit.CreateService(
                db, SlotSyncKit.ApiReturning(channel, SlotSyncKit.SetA, SlotSyncKit.Live("y", "other", SlotSyncKit.T1), SlotSyncKit.Live("x", "kappa", SlotSyncKit.T1)),
                wishReconcile: trigger)
            .SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.True(result.HasChanges);
        var slot = Assert.Single(await SlotSyncKit.SlotsAsync(db, (await SlotSyncKit.EmoteAsync(db, channel, "x")).Id));
        Assert.Equal(("kappa", false), (slot.Name, slot.IsArchived));
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task FullSync_RealReconcile_TurnsAnApprovedWishActive()
    {
        var trigger = RealTrigger();
        await using var db = fixture.CreateDbContext();
        var channel = await SlotSyncKit.SeedChannelAsync(db, $"h{Guid.NewGuid():N}"[..18]);
        Assert.NotNull(await SlotSyncKit.SyncOnAsync(db, channel, SlotSyncKit.Live("y", "other", SlotSyncKit.T1)));
        var seeded = new Seeded(channel.Id, channel.ChannelName);
        _clock.SetUtcNow(WallClock());
        var wishId = await SeedWishAsync(seeded, "x", WishStatus.Approved, w =>
        {
            w.Alias = "kappa";
            w.ApprovedAtUtc = SlotSyncKit.T1.AddDays(-1);
            w.CreatedAtUtc = SlotSyncKit.T1.AddDays(-1);
        });

        var result = await SlotSyncKit.CreateService(
                db, SlotSyncKit.ApiReturning(channel, SlotSyncKit.SetA, SlotSyncKit.Live("y", "other", SlotSyncKit.T1), SlotSyncKit.Live("x", "kappa", SlotSyncKit.T1)),
                wishReconcile: trigger)
            .SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        var wish = await WishAsync(wishId);
        Assert.Equal(WishStatus.Active, wish.Status);
        Assert.NotNull(wish.SlotId);
    }

    // ------------------------------------------------------------------------------------ delta

    [Fact]
    public async Task Delta_Applied_Reconciles_NoChangeAndAmbiguous_DoNot()
    {
        var trigger = Substitute.For<IWishReconcileTrigger>();
        await using var db = fixture.CreateDbContext();
        var channel = await SlotSyncKit.SeedChannelAsync(db, $"h{Guid.NewGuid():N}"[..18]);
        var x = await SlotSyncKit.SeedEmoteAsync(db, channel, "x", "probeA", firstSeenAt: SlotSyncKit.T1);
        await SlotSyncKit.SeedSlotAsync(db, x, "probeA", SlotSyncKit.T1);
        await SlotSyncKit.SeedSlotAsync(db, x, "probeC", SlotSyncKit.T2, archived: true, archivedAt: SlotSyncKit.T5);

        async Task<SevenTvDeltaOutcome> ApplyAsync(string name)
        {
            var delta = new SevenTvEmoteSetDelta([new SevenTvEmote("x", name, SlotSyncKit.Url("x"))], [], []);
            var result = await SlotSyncKit.CreateService(db, SlotSyncKit.ApiReturning(channel, SlotSyncKit.SetA), wishReconcile: trigger)
                .ApplyEmoteSetUpdateAsync(channel.ChannelName, SlotSyncKit.SetA, delta);
            return result.Outcome;
        }

        Assert.Equal(SevenTvDeltaOutcome.NoChange, await ApplyAsync("probeA"));
        Assert.Equal(SevenTvDeltaOutcome.AmbiguousDelta, await ApplyAsync("probeC"));
        Assert.Empty(trigger.ReceivedCalls());

        Assert.Equal(SevenTvDeltaOutcome.Applied, await ApplyAsync("probeB"));
        await trigger.Received(1).TriggerAsync(channel.Id, Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------------------------ report

    // Delete through the report with the real reconcile behind the hook: the linked wish ends in the same call.
    [Fact]
    public async Task Report_Delete_RemovesTheLinkedWishExternally_AndOnlyChangedChannelsReconcile()
    {
        var (seeded, set, x, slotId, wishId) = await SeedLinkedForReportAsync();
        var entry = new SyncInSetEntryDto(x, "kappa", slotId, SlotSyncKit.T1);
        _clock.SetUtcNow(WallClock());
        var recording = Substitute.For<IWishReconcileTrigger>();

        await ReportWithAsync(set, x, entry, restore: false, trigger: new ForwardingTrigger(RealTrigger(), recording));
        Assert.Equal(WishStatus.RemovedExternally, (await WishAsync(wishId)).Status);
        await recording.Received(1).TriggerAsync(seeded.Id, Arg.Any<CancellationToken>());

        // The same report again changes nothing: no further call.
        recording.ClearReceivedCalls();
        await ReportWithAsync(set, x, entry, restore: false, trigger: recording, expectChanged: 0);
        Assert.Empty(recording.ReceivedCalls());
    }

    // The reconcile after the archive fails (swallowed, the report still succeeds); the next reconcile heals
    // it from the state alone (T7 case iv): ended at the archive time.
    [Fact]
    public async Task Report_Delete_ASwallowedFailure_IsHealedByTheNextReconcile()
    {
        var (seeded, set, x, slotId, wishId) = await SeedLinkedForReportAsync();
        var entry = new SyncInSetEntryDto(x, "kappa", slotId, SlotSyncKit.T1);
        var failing = Substitute.For<IWishReconcileService>();
        failing.ReconcileChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        var logger = new RecordingLogger<WishReconcileTrigger>();

        await ReportWithAsync(set, x, entry, restore: false, trigger: new WishReconcileTrigger(failing, new WishOptions { Enabled = true }, logger));

        Assert.Equal(WishStatus.Active, (await WishAsync(wishId)).Status);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
        DateTime archivedAt;
        await using (var db = fixture.CreateDbContext())
        {
            archivedAt = (await SlotSyncKit.SlotAsync(db, slotId)).ArchivedAt!.Value;
        }

        _clock.SetUtcNow(WallClock().AddMinutes(120));
        await ReconcileAsync(seeded);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.RemovedExternally, archivedAt), (wish.Status, wish.EndedAtUtc));
    }

    [Fact]
    public async Task Report_Delete_SwitchOff_NeverCallsTheReconcile()
    {
        var (_, set, x, slotId, wishId) = await SeedLinkedForReportAsync();
        var reconcile = Substitute.For<IWishReconcileService>();
        var trigger = new WishReconcileTrigger(reconcile, new WishOptions { Enabled = false }, NullLogger<WishReconcileTrigger>.Instance);

        await ReportWithAsync(set, x, new SyncInSetEntryDto(x, "kappa", slotId, SlotSyncKit.T1), restore: false, trigger: trigger);

        Assert.Empty(reconcile.ReceivedCalls());
        Assert.Equal(WishStatus.Active, (await WishAsync(wishId)).Status);
    }

    // ------------------------------------------------------------------------------------ helpers (hooks)

    private WishReconcileTrigger RealTrigger() =>
        new(Sut(), new WishOptions { Enabled = true }, NullLogger<WishReconcileTrigger>.Instance);

    private async Task<(Seeded Channel, string Set, string X, string SlotId, long WishId)> SeedLinkedForReportAsync()
    {
        var set = "01WISH" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var x = "w" + Guid.NewGuid().ToString("N")[..23];
        Channel channel;
        EmoteSlot slot;
        await using (var db = fixture.CreateDbContext())
        {
            channel = await SlotSyncKit.SeedChannelAsync(db, $"r{Guid.NewGuid():N}"[..18], set);
            var emote = await SlotSyncKit.SeedEmoteAsync(db, channel, x, "kappa", firstSeenAt: SlotSyncKit.T1);
            slot = await SlotSyncKit.SeedSlotAsync(db, emote, "kappa", SlotSyncKit.T1);
        }

        var seeded = new Seeded(channel.Id, channel.ChannelName);
        var (wishId, _) = await LinkAtAsync(seeded, x, "kappa", WallClock().AddHours(-1).AddMinutes(-1));
        return (seeded, set, x, slot.Id, wishId);
    }

    private async Task ReportWithAsync(
        string set, string x, SyncInSetEntryDto entry, bool restore, IWishReconcileTrigger trigger, int expectChanged = 1)
    {
        await using var db = fixture.CreateDbContext();
        var service = new EmoteService(
            db, NullLogger<EmoteService>.Instance, Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db), trigger);
        var actor = new AuditActor("100", "synctester");
        var channels = restore
            ? (await service.MarkRestoredInSetAsync(set, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, [x], null, actor, [entry])).Channels
            : (await service.MarkDeletedInSetAsync(set, OwnerSevenTvUserId, OwnerTwitchLogin, OwnerTwitchUserId, [x], null, actor, [entry])).Channels;
        Assert.Equal(expectChanged, Assert.Single(channels).NewlyChangedCount);
    }

    // Runs the real trigger and tells a counting double the same call.
    private sealed class ForwardingTrigger(IWishReconcileTrigger real, IWishReconcileTrigger recording) : IWishReconcileTrigger
    {
        public async Task TriggerAsync(string channelId, CancellationToken cancellationToken = default)
        {
            await recording.TriggerAsync(channelId, cancellationToken);
            await real.TriggerAsync(channelId, cancellationToken);
        }
    }
}
