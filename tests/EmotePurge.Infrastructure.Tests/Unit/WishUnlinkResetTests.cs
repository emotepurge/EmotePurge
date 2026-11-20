using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The fields of a released capture link (spec K6 unlink, plan #486 2.6 and 2.8a), shared by the reconcile's date
// correction (T3) and the manager's unlink (T6).
public class WishUnlinkResetTests
{
    private static readonly DateTime T = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(WishMatchKind.Link, "01EMOTE")]
    [InlineData(WishMatchKind.Name, "")]
    public void ToPending_ClearsTheLinkAndTheSlotsFields_AndKeepsTheRest(string matchKind, string expectedEmoteId)
    {
        var wish = Linked(matchKind);

        WishUnlinkReset.ToPending(wish, T, WishStatusReason.AlreadyInSet);

        Assert.Equal((WishStatus.Pending, WishStatusReason.AlreadyInSet, T), (wish.Status, wish.StatusReason, wish.StatusChangedAtUtc));
        Assert.Equal(((string?)null, (DateTime?)null, false, (DateTime?)null), (wish.SlotId, wish.StartsAtUtc, wish.StartsAtAuthoritative, wish.ExpiresAtUtc));
        Assert.Equal(((string?)null, false, (DateTime?)null), (wish.LinkOrigin, wish.BotOwned, wish.BotOwnedAddedAtUtc));
        Assert.Equal(((DateTime?)null, (int?)null), (wish.RemovalQueuedAtUtc, wish.RemovalQueuedVersion));
        Assert.Equal(((DateTime?)null, (DateTime?)null), (wish.SlotAddedAtUtc, wish.SlotConfirmedAtUtc));
        Assert.Equal((0, 4), (wish.ExtensionCount, wish.DecisionVersion));
        Assert.Equal((expectedEmoteId, "", ""), (wish.SevenTvEmoteId, wish.EmoteName, wish.Alias));
        Assert.Equal((21, T.AddDays(-2), (DateTime?)null, T.AddDays(-1)), (wish.DurationDays, wish.CreatedAtUtc, wish.EndedAtUtc, wish.FirstActivatedAtUtc));
        Assert.Equal(("777", "viewer", "please"), (wish.RequesterTwitchUserId, wish.RequesterLogin, wish.RequestText));
    }

    private static Wish Linked(string matchKind) => new()
    {
        SevenTvEmoteId = "01EMOTE",
        EmoteName = "Kappa",
        Alias = "Kappa",
        Source = WishSource.Irc,
        MatchKind = matchKind,
        Status = WishStatus.Active,
        StatusChangedAtUtc = T.AddDays(-1),
        DurationDays = 21,
        CreatedAtUtc = T.AddDays(-2),
        StartsAtUtc = T.AddDays(-1),
        StartsAtAuthoritative = true,
        ExpiresAtUtc = T.AddDays(20),
        SlotId = "slot",
        ExtensionCount = 2,
        BotOwned = true,
        BotOwnedAddedAtUtc = T,
        RemovalQueuedAtUtc = T,
        RemovalQueuedVersion = 3,
        DecisionVersion = 3,
        LinkOrigin = WishLinkOrigin.Auto,
        SlotAddedAtUtc = T.AddDays(-1),
        SlotConfirmedAtUtc = T,
        FirstActivatedAtUtc = T.AddDays(-1),
        RequesterTwitchUserId = "777",
        RequesterLogin = "viewer",
        RequestText = "please",
    };
}
