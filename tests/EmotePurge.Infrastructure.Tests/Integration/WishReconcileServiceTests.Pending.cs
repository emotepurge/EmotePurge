using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishReconcileService part 4 (K6, plan #486 T3): open wishes against slots. Link and name matches, the hints,
// the exclusion list, wish.autoLink, the earlier approved window of a captured wish, the three id forms, and the
// date correction of a capture link (Sol-P1-3) with its scope (Sol-R2-P2-4). Every test runs against the migrated
// schema, so the placeholder CHECK of plan 2.8a guards every save.
public partial class WishReconcileServiceTests
{
    // ------------------------------------------------------------------------------------ AK55: link

    [Fact]
    public async Task Ak55_OpenLinkWish_AndANewSlot_BecomesActive_FillsThePlaceholder_AndAuditsOneAutoLink()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel, name: "EmoteRowName");
        var wishId = await SeedPendingAsync(
            channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => (w.DurationDays, w.CreatedAtUtc) = (10, Now.AddHours(-1)));
        var addedAt = Now.AddMinutes(-5);
        var slot = await SeedSlotAsync(emote, "InTheSet", addedAt, fromRest: false);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, (string?)null, slot), (wish.Status, wish.StatusReason, wish.SlotId));
        Assert.Equal((emote.SevenTvEmoteId, "EmoteRowName", "InTheSet"), (wish.SevenTvEmoteId, wish.EmoteName, wish.Alias));
        Assert.Equal((addedAt, addedAt.AddDays(10), false), (wish.StartsAtUtc, wish.ExpiresAtUtc, wish.StartsAtAuthoritative));
        Assert.Equal((WishLinkOrigin.Auto, false, (DateTime?)null, Now), (wish.LinkOrigin, wish.BotOwned, wish.ApprovedAtUtc, wish.FirstActivatedAtUtc));
        var audit = Assert.Single(await AutoLinksAsync(channel));
        Assert.Equal((AuditActor.System.TwitchUserId, "wish", wishId.ToString()), (audit.ActorTwitchUserId, audit.TargetType, audit.TargetId));
        var details = JsonDocument.Parse(audit.DetailsJson!).RootElement;
        Assert.Equal(
            (wishId, WishSource.Irc, WishStatus.Pending, WishMatchKind.Link),
            (details.GetProperty("wishId").GetInt64(), details.GetProperty("source").GetString(),
                details.GetProperty("fromStatus").GetString(), details.GetProperty("matchKind").GetString()));
        // No alias, text or requester in the details.
        Assert.Equal(["fromStatus", "matchKind", "source", "wishId"], details.EnumerateObject().Select(p => p.Name).Order());
        AssertPublishedOnce(channel);

        // A second run changes nothing and audits nothing.
        _publisher.ClearReceivedCalls();
        await ReconcileAsync(channel);
        Assert.Single(await AutoLinksAsync(channel));
        AssertNothingPublished();
    }

    [Fact]
    public async Task Ak55_ASlotWhoseEmoteRowHasNoName_GivesTheWishTheSlotsName()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel, name: "");
        var wishId = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link);
        await SeedSlotAsync(emote, "SlotName", Now);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, "SlotName", "SlotName"), (wish.Status, wish.EmoteName, wish.Alias));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public async Task Ak57_TheWindowStartsAtTheRedemption_WithoutTolerance(int secondsAfterRedemption, bool links)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var createdAt = Now.AddHours(-1);
        var wishId = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = createdAt);
        await SeedSlotAsync(emote, "Alias", createdAt.AddSeconds(secondsAfterRedemption));

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal(
            links ? (WishStatus.Active, null) : (WishStatus.Pending, WishStatusReason.AlreadyInSet),
            (wish.Status, wish.StatusReason));
    }

    // The window ends at the nominal expiry (StatusChangedAtUtc + PendingExpiryDays), whether or not the
    // housekeeping expired the wish yet.
    [Fact]
    public async Task Ak57_ASlotAfterTheNominalExpiry_IsNoCandidate_EvenBeforeTheHousekeepingRan()
    {
        var channel = await SeedChannelAsync();
        await SetPendingExpiryDaysAsync(channel, 2);
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w =>
        {
            w.CreatedAtUtc = Now.AddDays(-3);
            w.StatusChangedAtUtc = Now.AddDays(-3);
        });
        await SeedSlotAsync(emote, "Alias", Now.AddDays(-1).AddSeconds(1));

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Pending, (string?)null), (wish.Status, wish.StatusReason));
        AssertNothingPublished();
    }

    // ------------------------------------------------------------------------------------ AK56: name

    [Fact]
    public async Task Ak56_OpenNameWish_AndExactlyOneSlotWithThatAlias_BecomesActive_WithTheSlotsEmote()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel, name: "catJAM");
        var other = await SeedEmoteAsync(channel, name: "Other");
        await SeedSlotAsync(other, "SomethingElse", Now);
        var wishId = await SeedPendingAsync(channel, "", WishMatchKind.Name, w => w.RequestedName = "catJAM");
        var slot = await SeedSlotAsync(emote, "catJAM", Now);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot), (wish.Status, wish.SlotId));
        Assert.Equal((emote.SevenTvEmoteId, "catJAM", "catJAM"), (wish.SevenTvEmoteId, wish.EmoteName, wish.Alias));
        var details = JsonDocument.Parse(Assert.Single(await AutoLinksAsync(channel)).DetailsJson!).RootElement;
        Assert.Equal(WishMatchKind.Name, details.GetProperty("matchKind").GetString());
    }

    [Fact]
    public async Task Ak56_TwoSlotsDifferingOnlyInCase_AndNoOrdinalHit_StayPendingWithAmbiguousMatch_AndAnnounceTheHint()
    {
        var channel = await SeedChannelAsync();
        var a = await SeedEmoteAsync(channel);
        var b = await SeedEmoteAsync(channel);
        var wishId = await SeedPendingAsync(channel, "", WishMatchKind.Name, w => w.RequestedName = "KAPPA");
        await SeedSlotAsync(a, "Kappa", Now);
        await SeedSlotAsync(b, "kappa", Now);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Pending, WishStatusReason.AmbiguousMatch, ""), (wish.Status, wish.StatusReason, wish.SevenTvEmoteId));
        // A hint is no state change: the pending expiry keeps counting from the old stamp.
        Assert.Equal(Now, wish.StatusChangedAtUtc);
        Assert.Empty(await AutoLinksAsync(channel));
        AssertPublishedOnce(channel);

        // The same hint again is no visible change.
        _publisher.ClearReceivedCalls();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ReconcileAsync(channel);
        AssertNothingPublished();
    }

    [Fact]
    public async Task Ak56_AnOrdinalHitNextToACaseVariant_Wins()
    {
        var channel = await SeedChannelAsync();
        var a = await SeedEmoteAsync(channel);
        var b = await SeedEmoteAsync(channel);
        var wishId = await SeedPendingAsync(channel, "", WishMatchKind.Name, w => w.RequestedName = "kappa");
        await SeedSlotAsync(a, "Kappa", Now);
        var exact = await SeedSlotAsync(b, "kappa", Now);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, exact, b.SevenTvEmoteId), (wish.Status, wish.SlotId, wish.SevenTvEmoteId));
    }

    [Fact]
    public async Task NameWish_WhoseCandidateCarriesAnEmoteWithARunningWish_ShowsAlreadyRunning()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddDays(-30));
        var wishId = await SeedPendingAsync(channel, "", WishMatchKind.Name, w => w.RequestedName = "Fresh");
        await SeedSlotAsync(emote, "Fresh", Now);

        await ReconcileAsync(channel);

        // The approved hand entry is older and takes the slot; the open wish says the emote runs.
        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Pending, WishStatusReason.AlreadyRunning), (wish.Status, wish.StatusReason));
    }

    [Fact]
    public async Task NoneWish_IsNeverLinked_AndShowsUnparsed()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedPendingAsync(channel, "", WishMatchKind.None, w => w.StatusReason = null);
        await SeedSlotAsync(emote, "Alias", Now);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Pending, WishStatusReason.Unparsed), (wish.Status, wish.StatusReason));
    }

    // ------------------------------------------------------------------------------------ AK58: contenders

    [Fact]
    public async Task Ak58_TwoOpenWishesAndOneSlot_TheOlderBecomesActive_TheYoungerShowsAlreadyRunning()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var younger = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddMinutes(-10));
        var older = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddMinutes(-20));
        await SeedSlotAsync(emote, "Alias", Now);

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Active, (await WishAsync(older)).Status);
        var other = await WishAsync(younger);
        Assert.Equal((WishStatus.Pending, WishStatusReason.AlreadyRunning), (other.Status, other.StatusReason));
        Assert.Single(await AutoLinksAsync(channel));
    }

    // W12: the approved hand entry's link audits too, with its own source.
    [Fact]
    public async Task Ak58_AnOlderApprovedHandEntryWinsAgainstAYoungerRedemption_AndBothLinksAreAudited()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var hand = await SeedWishAsync(channel, emote, WishStatus.Approved, w => (w.CreatedAtUtc, w.ApprovedAtUtc) = (Now.AddHours(-2), Now.AddHours(-2)));
        var open = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddHours(-1));
        await SeedSlotAsync(emote, "Alias", Now);

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Active, (await WishAsync(hand)).Status);
        Assert.Equal(WishStatusReason.AlreadyRunning, (await WishAsync(open)).StatusReason);
        var details = JsonDocument.Parse(Assert.Single(await AutoLinksAsync(channel)).DetailsJson!).RootElement;
        Assert.Equal(
            (WishSource.Manual, WishStatus.Approved, JsonValueKind.Null),
            (details.GetProperty("source").GetString(), details.GetProperty("fromStatus").GetString(), details.GetProperty("matchKind").ValueKind));
    }

    // ------------------------------------------------------------------------------------ AK61: exclusions

    [Fact]
    public async Task Ak61_AnExcludedSlotAsTheOnlyCandidate_IsNoMatchAndNoHint_ButAnotherOpenWishOfTheEmoteGetsIt()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var released = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddMinutes(-20));
        var slot = await SeedSlotAsync(emote, "Alias", Now);
        await ExcludeAsync(released, slot);

        // A fresh service instance is the "restart": the exclusion lives in the database.
        await ReconcileAsync(channel);
        var alone = await WishAsync(released);
        Assert.Equal((WishStatus.Pending, (string?)null), (alone.Status, alone.StatusReason));

        var second = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddMinutes(-10));
        await ReconcileAsync(channel);

        Assert.Equal((WishStatus.Active, slot), ((await WishAsync(second)).Status, (await WishAsync(second)).SlotId));
        Assert.Equal(WishStatusReason.AlreadyRunning, (await WishAsync(released)).StatusReason);
    }

    [Fact]
    public async Task Ak61_TheExclusionHoldsForAnApprovedWishToo()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-1));
        var slot = await SeedSlotAsync(emote, "Alias", Now);
        await ExcludeAsync(wishId, slot);

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Approved, (await WishAsync(wishId)).Status);
    }

    // ------------------------------------------------------------------------------------ W11: approved window

    [Theory]
    [InlineData(WishSource.Irc, null, true)]
    [InlineData(WishSource.Manual, null, false)]
    [InlineData(WishSource.Irc, -10, false)]
    public async Task ApprovedFromTheCapture_StartsItsWindowAtTheRedemption_WhileItWasNeverActive(
        string source, int? activatedDaysAgo, bool links)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var redeemed = Now.AddHours(-2);
        var approved = Now.AddMinutes(-1);
        var wishId = await SeedWishAsync(channel, emote, WishStatus.Approved, w =>
        {
            w.Source = source;
            w.CreatedAtUtc = redeemed;
            w.ApprovedAtUtc = approved;
            w.FirstActivatedAtUtc = activatedDaysAgo is { } days ? Now.AddDays(days) : null;
        });
        // Added in 7TV between redemption and approval, before the next sync.
        await SeedSlotAsync(emote, "Alias", Now.AddMinutes(-30));

        await ReconcileAsync(channel);

        Assert.Equal(links ? WishStatus.Active : WishStatus.Approved, (await WishAsync(wishId)).Status);
    }

    // ------------------------------------------------------------------------------------ hints

    [Fact]
    public async Task AlreadyInSet_ByName_IsCaseInsensitive_AndOnlyForASlotBeforeTheRedemption()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedPendingAsync(channel, "", WishMatchKind.Name, w => (w.RequestedName, w.CreatedAtUtc) = ("POGGERS", Now.AddHours(-1)));
        await SeedSlotAsync(emote, "Poggers", Now.AddDays(-5));

        await ReconcileAsync(channel);

        Assert.Equal(WishStatusReason.AlreadyInSet, (await WishAsync(wishId)).StatusReason);
    }

    [Fact]
    public async Task AHintGoesAgain_WhenItsReasonIsGone()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Alias", Now.AddDays(-5));
        var wishId = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddHours(-1));
        await ReconcileAsync(channel);
        Assert.Equal(WishStatusReason.AlreadyInSet, (await WishAsync(wishId)).StatusReason);

        await UpdateSlotAsync(slot, s => (s.IsArchived, s.ArchivedAt) = (true, Now));
        _publisher.ClearReceivedCalls();
        await ReconcileAsync(channel);

        Assert.Equal((WishStatus.Pending, (string?)null), ((await WishAsync(wishId)).Status, (await WishAsync(wishId)).StatusReason));
        AssertPublishedOnce(channel);
    }

    // ------------------------------------------------------------------------------------ the running index

    // A running wish of the emote commits after the run read the channel's wishes: the link hits the running
    // index, the savepoint takes it back, the open wish stays pending with already_running, and no autoLink
    // survives.
    [Fact]
    public async Task ALinkThatHitsTheRunningIndex_StaysPendingWithAlreadyRunning_AndLeavesNoAudit()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var wishId = await SeedPendingAsync(channel, emote.SevenTvEmoteId, WishMatchKind.Link);
        await SeedSlotAsync(emote, "Alias", Now);
        // After the run's slot read (the pre-check and the wish reads name "Wishes", this one does not).
        var hook = new CommandHook(
            c => c.Contains("FROM \"EmoteSlots\"", StringComparison.Ordinal) && !c.Contains("\"Wishes\"", StringComparison.Ordinal),
            async n =>
            {
                if (n == 1)
                {
                    await SeedWishAsync(channel, emote, WishStatus.Approved);
                }
            });

        await ReconcileAsync(channel, hook);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Pending, WishStatusReason.AlreadyRunning, "", ""), (wish.Status, wish.StatusReason, wish.Alias, wish.EmoteName));
        Assert.Empty(await AutoLinksAsync(channel));
        Assert.Contains(_logger.Entries, e => e.Message.Contains("IX_Wishes_ChannelId_SevenTvEmoteId_Running", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------ 2.5a: three forms

    [Fact]
    public async Task TheThreeFormsOfOneEmoteId_CapturedThroughTheParser_AllMeetTheirSlot()
    {
        const string canonical = "01F6MZGCNG000255K4X1K7NTHR";
        string[] texts =
        [
            "https://7tv.app/emotes/60ae958e229664e8667aea38",
            "https://7tv.app/emotes/01f6mzgcng000255k4x1k7nthr",
            $"https://7tv.app/emotes/{canonical}",
        ];

        foreach (var text in texts)
        {
            var parsed = WishTextParser.Parse(text);
            var channel = await SeedChannelAsync();
            var emote = await SeedEmoteAsync(channel, sevenTvEmoteId: canonical);
            var wishId = await SeedPendingAsync(channel, parsed.SevenTvEmoteId!, parsed.MatchKind);
            await SeedSlotAsync(emote, "Alias", Now);

            await ReconcileAsync(channel);

            Assert.Equal((WishStatus.Active, canonical), ((await WishAsync(wishId)).Status, (await WishAsync(wishId)).SevenTvEmoteId));
        }

        // A running wish under the canonical id makes the hex link's wish show already_running.
        var running = await SeedChannelAsync();
        await SeedWishAsync(running, canonical, WishStatus.Approved);
        var hex = WishTextParser.Parse(texts[0]);
        var hexWish = await SeedPendingAsync(running, hex.SevenTvEmoteId!, hex.MatchKind);

        await ReconcileAsync(running);

        Assert.Equal(WishStatusReason.AlreadyRunning, (await WishAsync(hexWish)).StatusReason);
    }

    // ------------------------------------------------------------------------------------ AK55 on three paths

    [Fact]
    public async Task Ak55_FullSync_LinksAnOpenWish()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SlotSyncKit.SeedChannelAsync(db, $"p{Guid.NewGuid():N}"[..18]);
        Assert.NotNull(await SlotSyncKit.SyncOnAsync(db, channel, SlotSyncKit.Live("y", "other", SlotSyncKit.T1)));
        var seeded = new Seeded(channel.Id, channel.ChannelName);
        _clock.SetUtcNow(WallClock());
        var wishId = await SeedPendingAsync(seeded, "x", WishMatchKind.Link, w => w.CreatedAtUtc = Now.AddDays(-1));

        await SlotSyncKit.CreateService(
                db, SlotSyncKit.ApiReturning(channel, SlotSyncKit.SetA, SlotSyncKit.Live("y", "other", SlotSyncKit.T1), SlotSyncKit.Live("x", "kappa", Now)),
                wishReconcile: RealTrigger())
            .SyncChannelAsync(channel.ChannelName);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, "kappa", true), (wish.Status, wish.Alias, wish.StartsAtAuthoritative));
        Assert.Single(await AutoLinksAsync(seeded));
    }

    [Fact]
    public async Task Ak55_Delta_LinksAnOpenWish_Provisionally()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SlotSyncKit.SeedChannelAsync(db, $"p{Guid.NewGuid():N}"[..18]);
        var seeded = new Seeded(channel.Id, channel.ChannelName);
        _clock.SetUtcNow(WallClock().AddMinutes(1));
        var wishId = await SeedPendingAsync(seeded, "x", WishMatchKind.Link, w => w.CreatedAtUtc = WallClock().AddMinutes(-10));

        await ApplyDeltaAsync(db, channel, "x", "kappa");

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, "kappa", false), (wish.Status, wish.Alias, wish.StartsAtAuthoritative));
        Assert.Single(await AutoLinksAsync(seeded));
    }

    [Fact]
    public async Task Ak55_InSetReport_LinksAnOpenWish()
    {
        var set = "01WISH" + Guid.NewGuid().ToString("N")[..20].ToUpperInvariant();
        var x = "w" + Guid.NewGuid().ToString("N")[..23];
        Channel channel;
        EmoteSlot slot;
        await using (var db = fixture.CreateDbContext())
        {
            channel = await SlotSyncKit.SeedChannelAsync(db, $"p{Guid.NewGuid():N}"[..18], set);
            var emote = await SlotSyncKit.SeedEmoteAsync(db, channel, x, "kappa", firstSeenAt: SlotSyncKit.T1);
            slot = await SlotSyncKit.SeedSlotAsync(db, emote, "kappa", SlotSyncKit.T1);
        }

        // The entry left the set long ago (reported by the browser engine).
        await ReportWithAsync(set, x, new SyncInSetEntryDto(x, "kappa", slot.Id, SlotSyncKit.T1), restore: false, trigger: RealTrigger());
        var seeded = new Seeded(channel.Id, channel.ChannelName);
        _clock.SetUtcNow(WallClock().AddMinutes(1));
        var wishId = await SeedPendingAsync(seeded, x, WishMatchKind.Link, w => w.CreatedAtUtc = WallClock().AddMinutes(-10));

        // The manager restored the entry from the browser; the report names the slot's generation, the restore
        // stamps the new stay with a provisional date of now.
        await ReportWithAsync(set, x, new SyncInSetEntryDto(x, "kappa", slot.Id, SlotSyncKit.T1), restore: true, trigger: RealTrigger());

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot.Id, false), (wish.Status, wish.SlotId, wish.StartsAtAuthoritative));
        Assert.Single(await AutoLinksAsync(seeded));
    }

    // ------------------------------------------------------------------------------------ Sol-P1-3: date correction

    // The entry lies in the set before the redemption but the reconcile does not know it; a late delta creates
    // the slot with the time it was seen, after the redemption, so the open wish becomes active, provisionally.
    // The full sync brings 7TV's real date, before the redemption: the wish is open again with already_in_set,
    // without slot, start or expiry, and it is never active with an authoritative start, so nothing can remove
    // the entry.
    [Fact]
    public async Task SolP1_3_ALateDelta_ThenTheRestDateBeforeTheRedemption_ReleasesTheLink_AndRemovalStaysLocked()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SlotSyncKit.SeedChannelAsync(db, $"p{Guid.NewGuid():N}"[..18]);
        var seeded = new Seeded(channel.Id, channel.ChannelName);
        var redeemed = WallClock().AddMinutes(-10);
        _clock.SetUtcNow(WallClock().AddMinutes(1));
        var wishId = await SeedPendingAsync(seeded, "x", WishMatchKind.Link, w => w.CreatedAtUtc = redeemed);

        await ApplyDeltaAsync(db, channel, "x", "kappa");
        var linked = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, false), (linked.Status, linked.StartsAtAuthoritative));
        var slotId = linked.SlotId!;
        var removal = new CompleteWishRemovalRequest(slotId, linked.SlotAddedAtUtc, SlotSyncKit.SetA, WishRemovalEvidence.Acknowledged);

        _clock.Advance(TimeSpan.FromMinutes(5));
        _publisher.ClearReceivedCalls();
        await SlotSyncKit.CreateService(db, SlotSyncKit.ApiReturning(channel, SlotSyncKit.SetA, SlotSyncKit.Live("x", "kappa", SlotSyncKit.T1)), wishReconcile: RealTrigger())
            .SyncChannelAsync(channel.ChannelName);

        await using (var verify = fixture.CreateDbContext())
        {
            var corrected = await SlotSyncKit.SlotAsync(verify, slotId);
            Assert.Equal((SlotSyncKit.T1, true, false), (corrected.AddedAt, corrected.AddedAtFromRest, corrected.IsArchived));
        }

        var wish = await WishAsync(wishId);
        AssertReleasedByDateCorrection(wish, linked);
        var unlink = Assert.Single(await AuditsAsync(seeded, AuditActions.WishUnlink));
        var details = JsonDocument.Parse(unlink.DetailsJson!).RootElement;
        Assert.Equal(
            ("date_correction", WishStatus.Active, slotId, AuditActor.System.TwitchUserId),
            (details.GetProperty("cause").GetString(), details.GetProperty("fromStatus").GetString(), details.GetProperty("slotId").GetString(), unlink.ActorTwitchUserId));
        // The autoLink stays; metric (c) subtracts the date corrections.
        Assert.Single(await AutoLinksAsync(seeded));
        AssertPublishedOnce(seeded);

        Assert.Equal(WishOutcome.StateConflict, (await WishSut().CompleteRemovalAsync(seeded.Name, wishId, removal, Actor)).Outcome);
        Assert.Equal(WishOutcome.StateConflict, (await WishSut().ExtendAsync(seeded.Name, wishId, Actor)).Outcome);

        // The next run leaves it alone: the slot is outside the window and stays in the set.
        _publisher.ClearReceivedCalls();
        await ReconcileAsync(seeded);
        Assert.Equal(WishStatus.Pending, (await WishAsync(wishId)).Status);
        AssertNothingPublished();
    }

    // The same after the expiry run presented the wish for its provisional start (K2).
    [Fact]
    public async Task SolP1_3_APresentedUnconfirmedStart_IsReleasedTheSameWay()
    {
        var (channel, slot, wishId) = await SeedAutoLinkedProvisionalAsync(w =>
        {
            w.Status = WishStatus.Presented;
            w.StatusReason = WishStatusReason.UnconfirmedStart;
        });
        var before = await WishAsync(wishId);
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (before.CreatedAtUtc.AddSeconds(-1), true));

        await ReconcileAsync(channel);

        AssertReleasedByDateCorrection(await WishAsync(wishId), before);
        var details = JsonDocument.Parse(Assert.Single(await AuditsAsync(channel, AuditActions.WishUnlink)).DetailsJson!).RootElement;
        Assert.Equal(WishStatus.Presented, details.GetProperty("fromStatus").GetString());
    }

    // Counter-check: an authoritative date inside the window confirms as before K6.
    [Fact]
    public async Task DateCorrection_InsideTheWindow_KeepsTheLink_AndMovesTheStart()
    {
        var (channel, slot, wishId) = await SeedAutoLinkedProvisionalAsync();
        var before = await WishAsync(wishId);
        var authoritative = before.CreatedAtUtc;
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (authoritative, true));

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot, authoritative, true), (wish.Status, wish.SlotId, wish.StartsAtUtc, wish.StartsAtAuthoritative));
        Assert.Empty(await AuditsAsync(channel, AuditActions.WishUnlink));
    }

    // Sol-R2-P2-4: the check is only for links nobody decided on. A hand entry, an approved capture, an extended
    // capture link and a kept one keep the behaviour from before K6: the link stays and the date is taken over.
    [Theory]
    [InlineData("hand")]
    [InlineData("approvedCapture")]
    [InlineData("extended")]
    public async Task DateCorrection_BeforeTheWindow_ChangesNothingForDecidedOrHandEnteredWishes(string kind)
    {
        var (channel, slot, wishId) = await SeedAutoLinkedProvisionalAsync(w =>
        {
            switch (kind)
            {
                case "hand":
                    (w.Source, w.MatchKind, w.ApprovedAtUtc) = (WishSource.Manual, null, w.CreatedAtUtc);
                    break;
                case "approvedCapture":
                    w.ApprovedAtUtc = w.CreatedAtUtc.AddMinutes(1);
                    break;
                default:
                    (w.ExtensionCount, w.ExpiresAtUtc) = (1, w.ExpiresAtUtc!.Value.AddDays(30));
                    break;
            }
        });
        var before = await WishAsync(wishId);
        var authoritative = before.CreatedAtUtc.AddDays(-3);
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (authoritative, true));

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, slot, authoritative, true), (wish.Status, wish.SlotId, wish.StartsAtUtc, wish.StartsAtAuthoritative));
        Assert.Equal(before.ExpiresAtUtc!.Value + (authoritative - before.StartsAtUtc!.Value), wish.ExpiresAtUtc);
        Assert.Equal(before.DecisionVersion, wish.DecisionVersion);
        Assert.Empty(await AuditsAsync(channel, AuditActions.WishUnlink));
    }

    // K1's own regression, through the real writers: a hand entry linked by a late delta keeps its link when the
    // full sync dates the entry before the approval (the open K1 gap, plan section 14, stays as it was).
    [Fact]
    public async Task DateCorrection_HandEntry_ThroughTheRealWriters_StaysActiveAsBeforeK6()
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SlotSyncKit.SeedChannelAsync(db, $"p{Guid.NewGuid():N}"[..18]);
        var seeded = new Seeded(channel.Id, channel.ChannelName);
        _clock.SetUtcNow(WallClock().AddMinutes(1));
        var wishId = await SeedWishAsync(seeded, "x", WishStatus.Approved, w =>
            (w.Alias, w.CreatedAtUtc, w.ApprovedAtUtc) = ("kappa", WallClock().AddMinutes(-10), WallClock().AddMinutes(-10)));

        await ApplyDeltaAsync(db, channel, "x", "kappa");
        Assert.Equal((WishStatus.Active, false), ((await WishAsync(wishId)).Status, (await WishAsync(wishId)).StartsAtAuthoritative));

        _clock.Advance(TimeSpan.FromMinutes(5));
        await SlotSyncKit.CreateService(db, SlotSyncKit.ApiReturning(channel, SlotSyncKit.SetA, SlotSyncKit.Live("x", "kappa", SlotSyncKit.T1)), wishReconcile: RealTrigger())
            .SyncChannelAsync(channel.ChannelName);

        var wish = await WishAsync(wishId);
        Assert.Equal((WishStatus.Active, SlotSyncKit.T1, true), (wish.Status, wish.StartsAtUtc, wish.StartsAtAuthoritative));
        Assert.Empty(await AuditsAsync(seeded, AuditActions.WishUnlink));
    }

    // kept is a booked decision: the reconcile confirms no date for it, so the correction leaves it alone.
    [Fact]
    public async Task DateCorrection_LeavesAKeptCaptureLinkAlone()
    {
        var (channel, slot, wishId) = await SeedAutoLinkedProvisionalAsync(w =>
            (w.Status, w.EndedAtUtc, w.ExpiresAtUtc) = (WishStatus.Kept, Now, null));
        var before = await SnapshotAsync(wishId);
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (SlotSyncKit.T1, true));

        await ReconcileAsync(channel);

        Assert.Equal(before, await SnapshotAsync(wishId));
    }

    // Extend against the date correction of a capture link, in both commit orders on two connections: extended
    // first, the link stays and the date is taken over; corrected first, the wish is open and extend is refused.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DateCorrectionAgainstExtend_EitherCommitOrder(bool reconcileFirst)
    {
        var (channel, slot, wishId) = await SeedAutoLinkedProvisionalAsync();
        var before = await WishAsync(wishId);
        var authoritative = before.CreatedAtUtc.AddHours(-1);
        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (authoritative, true));
        _clock.Advance(TimeSpan.FromMinutes(5));

        static bool IsWishLock(string c) => c.Contains("FROM \"Wishes\"", StringComparison.Ordinal) && c.Contains("FOR UPDATE", StringComparison.Ordinal);

        WishCommandResult extended;
        if (reconcileFirst)
        {
            Task<WishCommandResult>? extend = null;
            var hook = new CommandHook(IsWishLock, async _ =>
            {
                extend = WishSut(fixture.CreateTaggedDbContext("wish-k6-extend-waits")).ExtendAsync(channel.Name, wishId, Actor);
                await fixture.WaitUntilBlockedOnLockAsync("wish-k6-extend-waits", extend);
            });

            await Sut(() => fixture.CreateTaggedDbContext("wish-k6-reconcile-first", [hook])).ReconcileChannelAsync(channel.Id);
            extended = await extend!;
        }
        else
        {
            Task? reconcile = null;
            var hook = new CommandHook(IsWishLock, async _ =>
            {
                reconcile = Sut(() => fixture.CreateTaggedDbContext("wish-k6-reconcile-waits")).ReconcileChannelAsync(channel.Id);
                await fixture.WaitUntilBlockedOnLockAsync("wish-k6-reconcile-waits", reconcile);
            });

            extended = await WishSut(fixture.CreateTaggedDbContext("wish-k6-extend-first", [hook])).ExtendAsync(channel.Name, wishId, Actor);
            await reconcile!;
        }

        var wish = await WishAsync(wishId);
        if (reconcileFirst)
        {
            Assert.Equal(WishOutcome.StateConflict, extended.Outcome);
            AssertReleasedByDateCorrection(wish, before);
        }
        else
        {
            Assert.True(extended.IsSuccess);
            Assert.Equal((WishStatus.Active, slot, authoritative, true, 1), (wish.Status, wish.SlotId, wish.StartsAtUtc, wish.StartsAtAuthoritative, wish.ExtensionCount));
            Assert.Empty(await AuditsAsync(channel, AuditActions.WishUnlink));
        }
    }

    // ------------------------------------------------------------------------------------ helpers (pending)

    private static void AssertReleasedByDateCorrection(Wish wish, Wish linked)
    {
        Assert.Equal((WishStatus.Pending, WishStatusReason.AlreadyInSet), (wish.Status, wish.StatusReason));
        Assert.Equal(((string?)null, (DateTime?)null, (DateTime?)null, false), (wish.SlotId, wish.StartsAtUtc, wish.ExpiresAtUtc, wish.StartsAtAuthoritative));
        Assert.Equal(((string?)null, (DateTime?)null, (DateTime?)null, (DateTime?)null), (wish.LinkOrigin, wish.SlotAddedAtUtc, wish.SlotConfirmedAtUtc, wish.EndedAtUtc));
        Assert.Equal((0, linked.DecisionVersion + 1, false), (wish.ExtensionCount, wish.DecisionVersion, wish.BotOwned));
        // A link wish keeps its id; alias and name go back to the placeholder (plan 2.8a).
        Assert.Equal((linked.SevenTvEmoteId, "", ""), (wish.SevenTvEmoteId, wish.Alias, wish.EmoteName));
        Assert.Equal((linked.CreatedAtUtc, linked.FirstActivatedAtUtc, linked.DurationDays), (wish.CreatedAtUtc, wish.FirstActivatedAtUtc, wish.DurationDays));
    }

    // A capture link wish (irc, link, never approved), redeemed an hour ago, linked by the reconcile to a slot
    // the delta saw 30 minutes ago: active, provisional.
    private async Task<(Seeded Channel, string SlotId, long WishId)> SeedAutoLinkedProvisionalAsync(Action<Wish>? configure = null)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var slot = await SeedSlotAsync(emote, "Alias", Now.AddMinutes(-30), fromRest: false);
        var wishId = await SeedLinkedAsync(channel, emote, slot, Now.AddMinutes(-30), authoritative: false, configure: w =>
        {
            (w.Source, w.MatchKind, w.ApprovedAtUtc, w.CreatedAtUtc) = (WishSource.Irc, WishMatchKind.Link, null, Now.AddHours(-1));
            w.RequesterTwitchUserId = "777";
            w.RequesterLogin = "viewer";
            configure?.Invoke(w);
        });
        return (channel, slot, wishId);
    }

    // An open captured wish, redeemed a minute ago; emote id empty for a name or none wish (the placeholder).
    private async Task<long> SeedPendingAsync(Seeded channel, string sevenTvEmoteId, string matchKind, Action<Wish>? configure = null) =>
        await SeedWishAsync(channel, sevenTvEmoteId, WishStatus.Pending, w =>
        {
            w.Source = WishSource.Irc;
            w.MatchKind = matchKind;
            w.StatusReason = matchKind == WishMatchKind.None ? WishStatusReason.Unparsed : null;
            w.EmoteName = "";
            w.Alias = "";
            w.ApprovedAtUtc = null;
            w.CreatedAtUtc = Now.AddMinutes(-1);
            w.RequesterTwitchUserId = "777";
            w.RequesterLogin = "viewer";
            w.RequestText = "please";
            configure?.Invoke(w);
        });

    private async Task<Emote> SeedEmoteAsync(Seeded channel, string? name = null, string? sevenTvEmoteId = null)
    {
        await using var db = fixture.CreateDbContext();
        var emote = new Emote
        {
            ChannelId = channel.Id,
            Name = name ?? "Emote",
            SevenTvEmoteId = sevenTvEmoteId ?? Guid.NewGuid().ToString("N")[..24],
            ImageUrl = "https://cdn.7tv.app/emote/example/2x.webp"
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private async Task SetPendingExpiryDaysAsync(Seeded channel, int days)
    {
        await using var db = fixture.CreateDbContext();
        db.ChannelWishSettings.Add(new ChannelWishSettings { ChannelId = channel.Id, PendingExpiryDays = days });
        await db.SaveChangesAsync();
    }

    private async Task ExcludeAsync(long wishId, string slotId)
    {
        await using var db = fixture.CreateDbContext();
        db.WishSlotExclusions.Add(new WishSlotExclusion { WishId = wishId, SlotId = slotId, CreatedAtUtc = Now });
        await db.SaveChangesAsync();
    }

    private Task<List<AuditLogEntry>> AutoLinksAsync(Seeded channel) => AuditsAsync(channel, AuditActions.WishAutoLink);

    private async Task<List<AuditLogEntry>> AuditsAsync(Seeded channel, string action)
    {
        await using var db = fixture.CreateDbContext();
        return await db.AuditLogEntries.AsNoTracking()
            .Where(a => a.ChannelName == channel.Name && a.Action == action)
            .OrderBy(a => a.Id)
            .ToListAsync();
    }

    private async Task ApplyDeltaAsync(AppDbContext db, Channel channel, string sevenTvEmoteId, string name)
    {
        var delta = new SevenTvEmoteSetDelta([new SevenTvEmote(sevenTvEmoteId, name, SlotSyncKit.Url(sevenTvEmoteId))], [], []);
        var result = await SlotSyncKit.CreateService(db, SlotSyncKit.ApiReturning(channel, SlotSyncKit.SetA), wishReconcile: RealTrigger())
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, SlotSyncKit.SetA, delta);
        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
    }
}
