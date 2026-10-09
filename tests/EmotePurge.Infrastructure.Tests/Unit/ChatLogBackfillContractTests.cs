using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The invariants the backfill contract types promise by construction (#349): an enqueue result carries
// a run exactly when it is Enqueued and a Retry-After only when 7TV was unavailable; the window math is
// the planner's ceil(days / 7) and never reports negative days.
public class ChatLogBackfillContractTests
{
    private static readonly ChatLogBackfillRunDto Run = new(
        1, "queued", 1, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), 0, 1, 1, null, DateTime.UtcNow, null, null,
        "mod", "01J9SET", null, 1, null, null, 0, 0);

    [Fact]
    public void EnqueueResult_CarriesARunOnlyWhenEnqueued_AndRetryAfterOnlyWhenSevenTvWasUnavailable()
    {
        var enqueued = ChatLogBackfillEnqueueResult.Enqueued(Run);
        Assert.Equal((ChatLogBackfillEnqueueStatus.Enqueued, Run, (TimeSpan?)null), (enqueued.Status, enqueued.Run, enqueued.RetryAfter));

        var unavailable = ChatLogBackfillEnqueueResult.SevenTvUnavailable(TimeSpan.FromSeconds(9));
        Assert.Equal((ChatLogBackfillEnqueueStatus.SevenTvUnavailable, (ChatLogBackfillRunDto?)null, (TimeSpan?)TimeSpan.FromSeconds(9)),
            (unavailable.Status, unavailable.Run, unavailable.RetryAfter));

        var failed = ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.WindowEmpty);
        Assert.Equal((ChatLogBackfillEnqueueStatus.WindowEmpty, (ChatLogBackfillRunDto?)null, (TimeSpan?)null), (failed.Status, failed.Run, failed.RetryAfter));

        Assert.Throws<ArgumentNullException>(() => ChatLogBackfillEnqueueResult.Enqueued(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.Enqueued));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatLogBackfillEnqueueResult.Failed(ChatLogBackfillEnqueueStatus.SevenTvUnavailable));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatLogBackfillEnqueueResult.Failed((ChatLogBackfillEnqueueStatus)999));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(183, 27)]
    public void WeeksFor_IsTheBlockCount(int days, int weeks) => Assert.Equal(weeks, ChatLogBackfillWindow.WeeksFor(days));

    [Fact]
    public void BlockResults_CompareByValue()
    {
        var transition = new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Running, 0, 0, null);
        Assert.Equal(new ChatLogBackfillBlockResult.Committed(transition), new ChatLogBackfillBlockResult.Committed(transition));
        Assert.NotEqual<ChatLogBackfillBlockResult>(new ChatLogBackfillBlockResult.RunNotActive(), new ChatLogBackfillBlockResult.ChannelGone());
        Assert.Equal(new ChatLogBackfillBlockResult.LiveRowConflict(), new ChatLogBackfillBlockResult.LiveRowConflict());
    }
}
