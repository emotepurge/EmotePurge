using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The order of the derived queue (#487 plan 2.5 "Warteschlange", K6b-14): repair checks, then open removal attempts, then
// adds, due confirmations and queued removals together; within a tier the oldest first, then the wish id.
public class WishBotQueueTests
{
    private static readonly DateTime At = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RepairChecksFirst_ThenRemovalAttempts_ThenTheRestByAge_ThenById()
    {
        var items = new[]
        {
            Item(WishBotQueueKind.Add, 7, At.AddMinutes(-1)),
            Item(WishBotQueueKind.Removal, 6, At.AddMinutes(-3)),
            Item(WishBotQueueKind.Confirm, 5, At.AddMinutes(-2)),
            Item(WishBotQueueKind.RemovalAttempt, 4, At),
            Item(WishBotQueueKind.RepairCheck, 3, At.AddMinutes(1)),
            Item(WishBotQueueKind.Add, 2, At.AddMinutes(-1)),
        };

        var order = WishBotQueue.Order(items).Select(i => i.WishId);

        Assert.Equal(new long[] { 3, 4, 6, 5, 2, 7 }, order);
    }

    private static WishBotQueueItem Item(WishBotQueueKind kind, long id, DateTime at) => new(kind, id, "channel", at);
}
