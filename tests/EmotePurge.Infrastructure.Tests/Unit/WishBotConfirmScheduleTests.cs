using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The confirmation schedule E11 (#487 plan 2.6 point 5, Sol-P2-7, R2-7): reads at 0 s, 30 s, 2 min, 5 min, 15 min after the
// anchor, each computed from the planned instant of the one before, nothing after the fifth; the deadline anchor + 16 min,
// also for a crash remnant that never got one stored.
public class WishBotConfirmScheduleTests
{
    private static readonly DateTime Anchor = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheReads_FollowE11_FromThePlannedInstant_AndEndAfterTheFifth()
    {
        var planned = WishBotConfirmSchedule.First(Anchor);
        var plan = new List<DateTime> { planned };
        for (var attempt = 0; WishBotConfirmSchedule.Next(planned, attempt) is { } next; attempt++)
        {
            plan.Add(next);
            planned = next;
        }

        Assert.Equal(
            new[] { Anchor, Anchor.AddSeconds(30), Anchor.AddMinutes(2), Anchor.AddMinutes(5), Anchor.AddMinutes(15) },
            plan);
        Assert.Null(WishBotConfirmSchedule.Next(Anchor.AddMinutes(15), 4));
        Assert.Null(WishBotConfirmSchedule.Next(Anchor, -1));
    }

    // A read that ran late does not move the plan: the next one counts from where the late one was planned.
    [Fact]
    public void ALateRead_DoesNotShiftTheNextOne()
    {
        Assert.Equal(Anchor.AddMinutes(2), WishBotConfirmSchedule.Next(Anchor.AddSeconds(30), 1));
    }

    [Fact]
    public void TheDeadline_IsTheAnchorPlus16Minutes()
    {
        Assert.Equal(Anchor.AddMinutes(16), WishBotConfirmSchedule.Deadline(Anchor));
    }

    [Fact]
    public void DeadlineOf_TheStoredOneForUnconfirmed_TheWritesForARemnant_NoneWithoutAnOpenAdd()
    {
        var unconfirmed = new Wish { Status = WishStatus.Unconfirmed, BotWriteAtUtc = Anchor, BotConfirmDeadlineAtUtc = Anchor.AddMinutes(40) };
        var withoutStored = new Wish { Status = WishStatus.Unconfirmed, BotWriteAtUtc = Anchor };
        var remnant = new Wish { Status = WishStatus.Approved, BotWriteAtUtc = Anchor };
        var plainApproved = new Wish { Status = WishStatus.Approved };
        var active = new Wish { Status = WishStatus.Active, BotWriteAtUtc = Anchor, BotConfirmDeadlineAtUtc = Anchor };

        Assert.Equal(Anchor.AddMinutes(40), WishBotConfirmSchedule.DeadlineOf(unconfirmed));
        Assert.Equal(Anchor.AddMinutes(16), WishBotConfirmSchedule.DeadlineOf(withoutStored));
        Assert.Equal(Anchor.AddMinutes(16), WishBotConfirmSchedule.DeadlineOf(remnant));
        Assert.Null(WishBotConfirmSchedule.DeadlineOf(plainApproved));
        Assert.Null(WishBotConfirmSchedule.DeadlineOf(active));
    }
}
