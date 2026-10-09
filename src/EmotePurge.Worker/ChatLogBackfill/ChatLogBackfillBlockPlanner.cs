using EmotePurge.Core.Services;

namespace EmotePurge.Worker.ChatLogBackfill;

/// <summary>One block of a backfill run: the UTC days <c>[From, ToExclusive)</c>, read in one archive request.</summary>
public readonly record struct ChatLogBackfillBlock(DateOnly From, DateOnly ToExclusive)
{
    /// <summary>The block's first instant, <c>From 00:00:00Z</c>.</summary>
    public DateTime FromUtc => From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>The instant right after the block, <c>ToExclusive 00:00:00Z</c>.</summary>
    public DateTime ToUtcExclusive => ToExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}

/// <summary>
/// Cuts a frozen run window into its blocks (spec 4.4, D5): consecutive 7-day blocks from
/// <c>windowFrom</c>, the last one shorter when the window is not a whole number of weeks. Block n is
/// <c>[windowFrom + 7n, min(windowFrom + 7(n + 1), windowTo))</c> — the same rule
/// <see cref="IChatLogBackfillService.ReplaceBlockAsync"/> checks a committed block against, so the
/// count equals the run's <c>WeeksTotal</c> (<see cref="ChatLogBackfillWindow.WeeksFor"/>).
/// </summary>
public static class ChatLogBackfillBlockPlanner
{
    /// <summary>The blocks of <c>[windowFrom, windowTo)</c>, in order; empty for an empty window.</summary>
    public static IReadOnlyList<ChatLogBackfillBlock> Plan(DateOnly windowFrom, DateOnly windowTo)
    {
        var blocks = new List<ChatLogBackfillBlock>();
        for (var from = windowFrom; from < windowTo; from = from.AddDays(ChatLogBackfillWindow.BlockDays))
        {
            var to = from.AddDays(ChatLogBackfillWindow.BlockDays);
            blocks.Add(new ChatLogBackfillBlock(from, to < windowTo ? to : windowTo));
        }

        return blocks;
    }
}
