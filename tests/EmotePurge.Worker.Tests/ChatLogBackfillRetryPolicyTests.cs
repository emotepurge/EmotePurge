using EmotePurge.Worker.ChatLogBackfill;
using Xunit;

namespace EmotePurge.Worker.Tests;

// Child 4 AC 3, transport half (#350, spec 4.5, D13). The 429 half — 60 s · 2^PauseCount, Retry-After up
// to the cap, the eleventh consecutive 429 → rate_limited, reset by a commit — is computed inside
// PauseAsync's conditional update from the persisted row (D38) and pinned in the Infrastructure tests
// (ChatLogBackfillServiceTests.Worker: Pause_*); ChatLogBackfillWorkerTests show the worker only ever
// acts on what that update returned.
public class ChatLogBackfillRetryPolicyTests
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(900);

    [Fact]
    public void ThreeAttemptsPerBlock_ThirtySecondsBeforeTheSecond_HundredTwentyBeforeTheThird()
    {
        Assert.True(ChatLogBackfillRetryPolicy.ShouldRetryTransport(blockAttempts: 1, transportRetries: 3));
        Assert.Equal(TimeSpan.FromSeconds(30), ChatLogBackfillRetryPolicy.TransportRetryDelay(1, Cap));

        Assert.True(ChatLogBackfillRetryPolicy.ShouldRetryTransport(blockAttempts: 2, transportRetries: 3));
        Assert.Equal(TimeSpan.FromSeconds(120), ChatLogBackfillRetryPolicy.TransportRetryDelay(2, Cap));

        // The third failed attempt ends the block: transport_failure.
        Assert.False(ChatLogBackfillRetryPolicy.ShouldRetryTransport(blockAttempts: 3, transportRetries: 3));
    }

    [Fact]
    public void ACountCarriedOverARestart_ContinuesTheSeries()
    {
        // BlockAttempts is persisted (D38): two attempts before a restart and one after leave none.
        Assert.False(ChatLogBackfillRetryPolicy.ShouldRetryTransport(blockAttempts: 3, transportRetries: 3));
        Assert.False(ChatLogBackfillRetryPolicy.ShouldRetryTransport(blockAttempts: 4, transportRetries: 3));
    }

    [Fact]
    public void FurtherAttempts_KeepQuadrupling_WhenMoreRetriesAreConfigured()
    {
        Assert.True(ChatLogBackfillRetryPolicy.ShouldRetryTransport(blockAttempts: 3, transportRetries: 5));
        Assert.Equal(TimeSpan.FromSeconds(480), ChatLogBackfillRetryPolicy.TransportRetryDelay(3, Cap));
    }

    [Fact]
    public void TheDelay_IsCappedAtMaxRetryAfter_HoweverManyRetriesAreConfigured()
    {
        // 30 · 4^3 = 1920 s would exceed the 900 s cap; 4^60 would overflow a TimeSpan.
        Assert.Equal(Cap, ChatLogBackfillRetryPolicy.TransportRetryDelay(4, Cap));
        Assert.Equal(Cap, ChatLogBackfillRetryPolicy.TransportRetryDelay(60, Cap));
        Assert.Equal(TimeSpan.FromSeconds(100), ChatLogBackfillRetryPolicy.TransportRetryDelay(2, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void ADelayBeforeTheFirstAttempt_IsNotDefined()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatLogBackfillRetryPolicy.TransportRetryDelay(0, Cap));
    }

    [Fact]
    public void CooldownWait_IsTheRemainderOrZero()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.Zero, ChatLogBackfillRetryPolicy.CooldownWait(null, now));
        Assert.Equal(TimeSpan.Zero, ChatLogBackfillRetryPolicy.CooldownWait(now.AddSeconds(-1), now));
        Assert.Equal(TimeSpan.Zero, ChatLogBackfillRetryPolicy.CooldownWait(now, now));
        Assert.Equal(TimeSpan.FromSeconds(90), ChatLogBackfillRetryPolicy.CooldownWait(now.AddSeconds(90), now));
    }

    [Fact]
    public void SpacingWait_CountsFromThePreviousRequestStart()
    {
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var spacing = TimeSpan.FromSeconds(10);

        Assert.Equal(TimeSpan.Zero, ChatLogBackfillRetryPolicy.SpacingWait(null, spacing, now));
        Assert.Equal(TimeSpan.FromSeconds(7), ChatLogBackfillRetryPolicy.SpacingWait(now.AddSeconds(-3), spacing, now));
        Assert.Equal(TimeSpan.Zero, ChatLogBackfillRetryPolicy.SpacingWait(now.AddSeconds(-10), spacing, now));
        Assert.Equal(TimeSpan.Zero, ChatLogBackfillRetryPolicy.SpacingWait(now.AddMinutes(-5), spacing, now));
    }
}
