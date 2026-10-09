using EmotePurge.Core.Services;
using EmotePurge.Worker.ChatLogBackfill;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Child 4 AC 1 (#350): the block plan is the rule ReplaceBlockAsync checks a committed block against,
// so its edges are pinned exactly.
public class ChatLogBackfillBlockPlannerTests
{
    [Fact]
    public void SixMonthsBeforeAFreshChannel_Is27Blocks_TheLastOneDayLong()
    {
        // Spec 4.1: channel created 2026-10-08, six months back from 2026-10-08 → 183 days.
        var from = new DateOnly(2026, 4, 8);
        var to = new DateOnly(2026, 10, 8);

        var plan = ChatLogBackfillBlockPlanner.Plan(from, to);

        Assert.Equal(183, to.DayNumber - from.DayNumber);
        Assert.Equal(27, plan.Count);
        Assert.Equal(ChatLogBackfillWindow.WeeksFor(183), plan.Count);
        Assert.Equal(new ChatLogBackfillBlock(from, from.AddDays(7)), plan[0]);
        Assert.Equal(new ChatLogBackfillBlock(new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8)), plan[^1]);
    }

    [Fact]
    public void Blocks_AreContiguous_AndCoverTheWindowExactly()
    {
        var from = new DateOnly(2026, 4, 8);
        var to = new DateOnly(2026, 10, 8);

        var plan = ChatLogBackfillBlockPlanner.Plan(from, to);

        Assert.Equal(from, plan[0].From);
        Assert.Equal(to, plan[^1].ToExclusive);
        for (var i = 1; i < plan.Count; i++)
        {
            Assert.Equal(plan[i - 1].ToExclusive, plan[i].From);
        }

        Assert.All(plan.Take(plan.Count - 1), b => Assert.Equal(7, b.ToExclusive.DayNumber - b.From.DayNumber));
    }

    [Fact]
    public void SevenDays_IsOneBlock()
    {
        var from = new DateOnly(2026, 9, 1);

        var plan = ChatLogBackfillBlockPlanner.Plan(from, from.AddDays(7));

        Assert.Equal([new ChatLogBackfillBlock(from, from.AddDays(7))], plan);
    }

    [Fact]
    public void EightDays_IsASevenDayAndAOneDayBlock()
    {
        var from = new DateOnly(2026, 9, 1);

        var plan = ChatLogBackfillBlockPlanner.Plan(from, from.AddDays(8));

        Assert.Equal(
            [new ChatLogBackfillBlock(from, from.AddDays(7)), new ChatLogBackfillBlock(from.AddDays(7), from.AddDays(8))],
            plan);
    }

    [Fact]
    public void AnEmptyWindow_HasNoBlocks()
    {
        var day = new DateOnly(2026, 9, 1);

        Assert.Empty(ChatLogBackfillBlockPlanner.Plan(day, day));
        Assert.Empty(ChatLogBackfillBlockPlanner.Plan(day, day.AddDays(-3)));
    }

    [Fact]
    public void BlockInstants_AreUtcMidnights()
    {
        var block = new ChatLogBackfillBlock(new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8));

        Assert.Equal(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), block.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc), block.ToUtcExclusive);
        Assert.Equal(DateTimeKind.Utc, block.FromUtc.Kind);
    }
}
