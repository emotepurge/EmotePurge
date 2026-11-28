using EmotePurge.Infrastructure.BotAccount;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

// The lock deadline's arithmetic on a fake monotonic clock (#487 plan 2.5a (c); Revision 6e K6e-3, 6f K6f-1). What it sets
// in Postgres is covered against a real database in WishBotPassTests.InvitationRaces.cs.
public class BotAccountLockDeadlineTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero));

    // Before the first lock: 10 s for the lock's statement, 28 s idle; the whole HTTP budget of 25 s.
    [Fact]
    public void BeforeTheFirstLock_TheLockWaitsTenSeconds_AndTheIdleTimeoutIsTheWholeSpan()
    {
        var deadline = new BotAccountLockDeadline(_clock);

        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.False(deadline.IsStarted);
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(28)), deadline.Timeouts());
        Assert.Equal(TimeSpan.FromSeconds(28), deadline.Remaining);
        Assert.True(deadline.Fits(TimeSpan.FromSeconds(25)));
        Assert.False(deadline.Fits(TimeSpan.FromSeconds(25.001)));
        Assert.False(deadline.HasExpired);
    }

    // After the first lock both timeouts are the time left until F (also right before an HTTP call); F does not move with
    // a later Start; an HTTP call with 10 s fits while it ends by F − 3 s.
    [Fact]
    public void AfterTheFirstLock_BothTimeoutsAreTheTimeLeft_AndHttpMustEndThreeSecondsBeforeF()
    {
        var deadline = new BotAccountLockDeadline(_clock);
        deadline.Start();
        _clock.Advance(TimeSpan.FromSeconds(15));
        deadline.Start();

        Assert.Equal(TimeSpan.FromSeconds(13), deadline.Remaining);
        Assert.Equal((TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(13)), deadline.Timeouts());
        Assert.True(deadline.Fits(TimeSpan.FromSeconds(10)));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(deadline.Fits(TimeSpan.FromSeconds(10)));
    }

    // K6e-5: a page gets the smaller of its own limit and the time left until F − 3 s − reserve, and none under 1 s.
    [Fact]
    public void APage_GetsTheSmallerLimit_AndNoneUnderOneSecond()
    {
        var deadline = new BotAccountLockDeadline(_clock);
        deadline.Start();
        var tenSeconds = TimeSpan.FromSeconds(10);

        Assert.Equal(tenSeconds, deadline.PageTimeLimit(tenSeconds));
        Assert.Equal(tenSeconds, deadline.PageTimeLimit(tenSeconds, reserve: tenSeconds));

        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(5), deadline.PageTimeLimit(tenSeconds));
        Assert.Null(deadline.PageTimeLimit(tenSeconds, reserve: TimeSpan.FromSeconds(4.5)));

        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(1), deadline.PageTimeLimit(tenSeconds));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(deadline.PageTimeLimit(tenSeconds));
    }

    // No time left: expired, and no statement may be sent (ApplyAsync throws before it would).
    [Fact]
    public void AtF_TheDeadlineHasExpired()
    {
        var deadline = new BotAccountLockDeadline(_clock);
        deadline.Start();

        _clock.Advance(TimeSpan.FromSeconds(28) - TimeSpan.FromMilliseconds(1));
        Assert.False(deadline.HasExpired);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(deadline.HasExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    // The path of an expired deadline: the own exception and any database error, also wrapped (SaveChanges); never the
    // caller's cancellation or an unrelated error.
    [Fact]
    public void Expiry_IsTheDeadlineOrADatabaseError_NeverTheCallersCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.True(BotAccountLockDeadline.IsExpiry(new BotAccountLockDeadlineExceededException(), CancellationToken.None));
        Assert.True(BotAccountLockDeadline.IsExpiry(new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", "57014"), CancellationToken.None));
        Assert.True(BotAccountLockDeadline.IsExpiry(new NpgsqlException("connection broken"), CancellationToken.None));
        Assert.True(BotAccountLockDeadline.IsExpiry(new DbUpdateException("save", new NpgsqlException("x")), CancellationToken.None));
        Assert.False(BotAccountLockDeadline.IsExpiry(new InvalidOperationException("bug"), CancellationToken.None));
        Assert.False(BotAccountLockDeadline.IsExpiry(new OperationCanceledException(cancelled.Token), cancelled.Token));
    }
}
