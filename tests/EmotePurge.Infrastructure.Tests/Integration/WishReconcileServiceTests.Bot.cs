using EmotePurge.Core.Entities;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// WishReconcileService and the bot flow (#487, plan T6): a wish the bot confirmed only gets a slot under its own alias
// ([W7]), also a permanent one in kept (2.1 point 1); a wish with an open bot attempt stays out of steps 3 and 4 (2.5a
// (b)); a provisional slot heals with the next REST date without moving start or expiry (2.1 point 3).
public partial class WishReconcileServiceTests
{
    // Two live slots of the confirmed emote, one under another alias: only the one under the wish's alias is the bot's.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BotConfirmed_TakesOnlyTheSlotUnderItsOwnAlias(bool rightAliasPresent)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var start = Now.AddMinutes(-40);
        var wishId = await SeedBotConfirmedAsync(channel, emote, WishStatus.Active, start);
        var wrong = await SeedSlotAsync(emote, "OtherAlias", Now.AddMinutes(-35));
        var right = rightAliasPresent ? await SeedSlotAsync(emote, "Kappa", start) : null;
        var before = await SnapshotAsync(wishId);

        await ReconcileAsync(channel);

        var wish = await WishAsync(wishId);
        if (rightAliasPresent)
        {
            Assert.Equal((right, "Kappa", WishStatus.Active, true), (wish.SlotId, wish.Alias, wish.Status, wish.BotOwned));
            Assert.Equal((start, start.AddDays(30)), (wish.StartsAtUtc, wish.ExpiresAtUtc));
        }
        else
        {
            Assert.NotEqual(wrong, wish.SlotId);
            Assert.Equal(before, await SnapshotAsync(wishId));
        }
    }

    // 2.1 point 1: a permanent wish the bot confirmed ends in kept without a slot; the reconcile fills its slot in (the
    // same alias rule), so a later wish of the emote cannot take the entry; ownership and expiry never come in.
    [Fact]
    public async Task BotConfirmedKept_GetsItsSlotFilledIn_AndHoldsItAgainstALaterWish()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var start = Now.AddMinutes(-40);
        var keptId = await SeedBotConfirmedAsync(channel, emote, WishStatus.Kept, start, w =>
        {
            w.Permanent = true;
            w.BotOwned = false;
            w.ExpiresAtUtc = null;
            w.EndedAtUtc = Now.AddMinutes(-39);
        });
        var slot = await SeedSlotAsync(emote, "Kappa", start);

        await ReconcileAsync(channel);

        var kept = await WishAsync(keptId);
        Assert.Equal((WishStatus.Kept, slot, false, (DateTime?)null), (kept.Status, kept.SlotId, kept.BotOwned, kept.ExpiresAtUtc));
        Assert.Equal((start, Now), (kept.SlotAddedAtUtc!.Value, kept.SlotConfirmedAtUtc!.Value));

        var later = await SeedWishAsync(channel, emote, WishStatus.Approved, w =>
        {
            w.Alias = "Kappa";
            w.ApprovedAtUtc = Now.AddHours(-2);
        });
        await ReconcileAsync(channel);

        Assert.Equal((WishStatus.Approved, (string?)null), ((await WishAsync(later)).Status, (await WishAsync(later)).SlotId));
        Assert.Equal(slot, (await WishAsync(keptId)).SlotId);
    }

    // A kept wish that gave its slot back (the stay ended) carries its confirmation stamp: it is not filled in again,
    // and the pre-check does not even start a run for it.
    [Fact]
    public async Task AReleasedBotKeptWish_IsNotFilledInAgain()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var keptId = await SeedBotConfirmedAsync(channel, emote, WishStatus.Kept, Now.AddDays(-5), w =>
        {
            w.Permanent = true;
            w.BotOwned = false;
            w.EndedAtUtc = Now.AddDays(-5);
            w.SlotAddedAtUtc = Now.AddDays(-5);
            w.SlotConfirmedAtUtc = Now.AddDays(-2);
        });
        await SeedSlotAsync(emote, "Kappa", Now.AddMinutes(-10));
        var before = await SnapshotAsync(keptId);

        await ReconcileAsync(channel);

        Assert.Equal(before, await SnapshotAsync(keptId));
    }

    // 2.1 point 3: a slot known only from the EventAPI (provisional date) gives the bot-confirmed wish its slot with a
    // non-authoritative flag; the next REST date heals the flag through the confirmation branch, start and expiry stay.
    [Fact]
    public async Task BotConfirmed_OnAProvisionalSlot_IsNotAuthoritative_UntilTheRestDate_StartAndExpiryStay()
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var start = Now.AddMinutes(-40);
        var wishId = await SeedBotConfirmedAsync(channel, emote, WishStatus.Active, start);
        var observedAt = Now.AddMinutes(-39);
        var slot = await SeedSlotAsync(emote, "Kappa", observedAt, fromRest: false);

        await ReconcileAsync(channel);

        var linked = await WishAsync(wishId);
        Assert.Equal((slot, false), (linked.SlotId, linked.StartsAtAuthoritative));
        Assert.Equal((start, start.AddDays(30)), (linked.StartsAtUtc, linked.ExpiresAtUtc));

        await UpdateSlotAsync(slot, s => (s.AddedAt, s.AddedAtFromRest) = (start, true));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ReconcileAsync(channel);

        var healed = await WishAsync(wishId);
        Assert.Equal((slot, true, WishStatus.Active, true), (healed.SlotId, healed.StartsAtAuthoritative, healed.Status, healed.BotOwned));
        Assert.Equal((start, start.AddDays(30)), (healed.StartsAtUtc, healed.ExpiresAtUtc));
    }

    // 2.5a (b): an archived slot under a removal attempt is the bot's own removal in progress, not an external one; the
    // reconcile leaves the wish to the runner. Without the attempt the same row leaves the set as before.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnArchivedSlot_UnderARemovalAttempt_IsNoExternalRemoval(bool attemptOpen)
    {
        var channel = await SeedChannelAsync();
        var emote = await SeedEmoteAsync(channel);
        var addedAt = Now.AddDays(-30);
        var slot = await SeedSlotAsync(emote, "Alias", addedAt, archived: true);
        var wishId = await SeedLinkedAsync(channel, emote, slot, addedAt, configure: w =>
        {
            w.SlotConfirmedAtUtc = Now.AddMinutes(-10);
            if (attemptOpen)
            {
                w.BotRemoveAtUtc = Now.AddMinutes(-1);
                w.BotSetId = "01BOTSET";
            }
        });
        var before = await SnapshotAsync(wishId);

        await ReconcileAsync(channel);

        if (attemptOpen)
        {
            Assert.Equal(before, await SnapshotAsync(wishId));
            AssertNothingPublished();
        }
        else
        {
            Assert.Equal(WishStatus.RemovedExternally, (await WishAsync(wishId)).Status);
        }
    }

    // 2.5a (b), step 4: an approved wish with a committed add intent is never linked (also by an alias-matching slot).
    [Fact]
    public async Task AnApprovedWishWithAnAddIntent_IsNotLinked_ButWithoutItIsAsBefore()
    {
        var channel = await SeedChannelAsync();
        var intentEmote = await SeedEmoteAsync(channel);
        var plainEmote = await SeedEmoteAsync(channel);
        var intent = await SeedWishAsync(channel, intentEmote, WishStatus.Approved, w =>
        {
            w.ApprovedAtUtc = Now.AddHours(-1);
            w.BotWriteAtUtc = Now.AddMinutes(-2);
        });
        var plain = await SeedWishAsync(channel, plainEmote, WishStatus.Approved, w => w.ApprovedAtUtc = Now.AddHours(-1));
        await SeedSlotAsync(intentEmote, "Alias", Now.AddMinutes(-1));
        var plainSlot = await SeedSlotAsync(plainEmote, "Alias", Now.AddMinutes(-1));

        await ReconcileAsync(channel);

        Assert.Equal(WishStatus.Approved, (await WishAsync(intent)).Status);
        Assert.Equal((WishStatus.Active, plainSlot), ((await WishAsync(plain)).Status, (await WishAsync(plain)).SlotId));
    }

    // A wish as the bot's confirmation leaves it (plan 2.6 point 6): active or kept, link origin bot, no slot and no
    // generation yet, start and expiry from 7TV's addedAt, approved an hour ago, alias "Kappa".
    private Task<long> SeedBotConfirmedAsync(
        Seeded channel, Emote emote, string status, DateTime start, Action<Wish>? configure = null) =>
        SeedWishAsync(channel, emote, status, w =>
        {
            w.Alias = "Kappa";
            w.ApprovedAtUtc = Now.AddHours(-1);
            w.LinkOrigin = WishLinkOrigin.Bot;
            w.StartsAtUtc = start;
            w.StartsAtAuthoritative = true;
            w.ExpiresAtUtc = start.AddDays(30);
            w.BotOwned = true;
            w.BotOwnedAddedAtUtc = start;
            w.BotWriteAtUtc = start.AddSeconds(-1);
            w.FirstActivatedAtUtc = start;
            configure?.Invoke(w);
        });
}
