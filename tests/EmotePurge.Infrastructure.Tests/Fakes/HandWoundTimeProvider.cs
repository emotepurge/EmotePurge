namespace EmotePurge.Infrastructure.Tests.Fakes;

/// <summary>
/// A clock that only moves when a test winds it, plus one seam that turns "two readers arrive at the
/// same instant" into something a single-threaded test can reproduce every time.
/// </summary>
/// <remarks>
/// <para>
/// Reusable on purpose. Several existing suites carry their own private <c>FakeClock</c> with the
/// same two members; they are deliberately left where they are (moving them would touch files this
/// change has no business touching), but anything new should start here.
/// </para>
/// <para>
/// <b>The hook.</b> <see cref="RunOnceOnNextRead"/> runs an action inside the next call to
/// <see cref="GetUtcNow"/>, before it returns. A class that reads the clock between looking at a
/// shared entry and swapping it can therefore be forced, deterministically and without threads, into
/// the exact interleaving where a second reader slips in between those two steps — the interleaving
/// where a naive cache starts a second upstream fill. Reproducing that with real threads would be a
/// probabilistic test of a race that must never happen.
/// </para>
/// </remarks>
public sealed class HandWoundTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private Action? _onNextRead;

    /// <summary>Starts at an arbitrary fixed instant — nothing under test depends on which.</summary>
    public HandWoundTimeProvider()
        : this(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero))
    {
    }

    /// <summary>The instant the clock currently shows, without triggering the hook.</summary>
    public DateTimeOffset Now => _now;

    public override DateTimeOffset GetUtcNow()
    {
        var hook = _onNextRead;
        if (hook is not null)
        {
            // Cleared first, so the action may read the clock itself without recursing.
            _onNextRead = null;
            hook();
        }

        return _now;
    }

    /// <summary>Winds the clock forward (or back, with a negative delta).</summary>
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);

    /// <summary>Moves the clock to an exact instant — used to land precisely on an expiry.</summary>
    public void SetUtcNow(DateTimeOffset now) => _now = now;

    /// <summary>
    /// Arms <paramref name="action"/> to run inside the next <see cref="GetUtcNow"/> call, exactly
    /// once. A second arming replaces an unfired one.
    /// </summary>
    public void RunOnceOnNextRead(Action action) => _onNextRead = action;
}
