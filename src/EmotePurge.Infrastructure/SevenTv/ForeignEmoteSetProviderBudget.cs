namespace EmotePurge.Infrastructure.SevenTv;

/// <summary>
/// One permit for one upstream request made on behalf of the foreign-channel-import preview — the
/// rate half of the provider-wide budget (spec 2026-09-09, E5b), seen from the place the request
/// actually leaves the process.
/// </summary>
/// <remarks>
/// Deliberately not the same seam as the concurrency half. Concurrency ("at most two foreign-set
/// lookups in flight") is a property of a whole <i>resolution</i> and is taken once, by the hardening
/// decorator, around the entire chain. The rate limit ("60 upstream requests a minute") is a property
/// of a single <i>request</i>, and one resolution issues up to twelve of them (one Helix lookup, one
/// <c>userByConnection</c>, up to ten set pages) — charging it once per resolution, as the first
/// draft did, licensed up to 720 upstream requests a minute against a documented ceiling of 60.
/// </remarks>
public interface IForeignUpstreamRequestBudget
{
    /// <summary>
    /// Takes one permit for one upstream request, waiting up to
    /// <see cref="ForeignEmoteSetProviderBudget.DefaultRequestWaitTimeout"/> for the rolling window to
    /// free one. <c>false</c> means the caller must not issue the request at all — it is the answer to
    /// a self-inflicted congestion, never evidence about the provider's health, and callers must keep
    /// the two apart (a refused permit must not feed the circuit breaker).
    /// </summary>
    Task<bool> TryChargeRequestAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes one permit only while at least <paramref name="reserve"/> permits of the rolling window
    /// stay free after it, waiting up to <paramref name="timeout"/> for that headroom
    /// (<see cref="TimeSpan.Zero"/>: no wait). A <paramref name="reserve"/> of <c>0</c> is exactly
    /// <see cref="TryChargeRequestAsync(CancellationToken)"/> with an explicit wait. The set preview
    /// charges its pages with <see cref="ForeignEmoteSetProviderBudget.PreviewReserve"/> (#457), so
    /// preview traffic can never take the permits the set list, the set-owner lookup and the editor
    /// grants depend on; those keep charging with no reserve.
    /// </summary>
    Task<bool> TryChargeRequestAsync(int reserve, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// The provider-wide half of the foreign-channel-import preview's rate limiting (spec 2026-09-09,
/// E5b) — the half the per-user <c>ForeignEmoteLookup</c> ASP.NET policy cannot express at all: 7TV
/// sees one server IP, not N users, and ten different logins hitting this feature at once would
/// bypass both the cache and request coalescing entirely, each costing up to a Helix call, a
/// <c>userByConnection</c> call and ten paginated set pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two independent limits, two independent seams, one process-wide instance.</b> At most
/// <see cref="MaxConcurrent"/> foreign-set <i>lookups</i> may be in flight at once
/// (<see cref="TryAcquireConcurrencySlotAsync"/>, taken once around a whole resolution by the
/// hardening decorator), and at most <see cref="MaxRequestsPerWindow"/> upstream <i>requests</i> may
/// start within any rolling <see cref="Window"/> (<see cref="TryChargeRequestAsync(CancellationToken)"/>,
/// taken by each step that actually issues one). They were a single call until Codex pointed out that
/// this collapses the two units: one resolution is up to twelve requests, so charging the window once
/// per resolution allowed up to twelve times the documented rate.
/// </para>
/// <para>
/// <b>Rolling, not fixed.</b> The window is a queue of the timestamps of the last
/// <see cref="MaxRequestsPerWindow"/> permits, not a counter reset wholesale at a boundary. A fixed
/// window lets 60 permits just before the boundary and 60 more just after it pass as "60 a minute",
/// which is ~120 in the rolling minute that straddles it — precisely the burst this limit exists to
/// keep off 7TV. The spec says "60 Upstream-Requests/Minute" without spelling out which reading it
/// means, so this is not a contract correction; it is the reading that serves the limit's stated
/// purpose. Bounded memory: the queue never holds more than <see cref="MaxRequestsPerWindow"/> entries.
/// </para>
/// <para>
/// <b>Waits, does not reject (E5).</b> Unlike the ASP.NET rate-limit policies elsewhere in this
/// codebase, exceeding either limit here does not produce an immediate rejection — it makes the
/// caller wait for a slot, up to the timeout it passed. Only a caller that is still waiting when that
/// timeout elapses is refused, which maps to the spec's "Unavailable" outcome.
/// </para>
/// <para>
/// <b>A reserve for everything that is not a preview (#457).</b> One window serves the set preview's
/// pages and the single-request reads behind permission and ownership checks (set list, set-owner
/// lookup, editor grants). Preview pages are charged with <see cref="PreviewReserve"/>, so they are
/// granted only while that many permits stay free; the other callers charge with none. Preview
/// traffic, however heavy, therefore leaves those checks at least <see cref="PreviewReserve"/>
/// permits a minute.
/// </para>
/// <para>
/// <b>In-process, deliberately.</b> Same limitation as the Twitch token refresh's in-process lock
/// (see <c>docs/DECISIONS.md</c>): with more than one API replica each would hold its own budget, so
/// the real ceiling would be <c>replicas × (MaxConcurrent, MaxRequestsPerWindow)</c>. Acceptable
/// today because there is exactly one replica; a second would need a distributed version of this.
/// </para>
/// <para>
/// Not a "pure" policy like <see cref="ForeignSevenTvBreakerPolicy"/> — E4 asks specifically for
/// that shape, E5 does not, and a concurrency/rate gate is inherently about coordinating real
/// waiting, not a stateless decision function.
/// </para>
/// </remarks>
public sealed class ForeignEmoteSetProviderBudget(TimeProvider? timeProvider = null)
    : IForeignUpstreamRequestBudget, IDisposable
{
    /// <summary>At most this many foreign-set lookups may be in flight at once.</summary>
    public const int MaxConcurrent = 2;

    /// <summary>At most this many upstream requests may start within any rolling <see cref="Window"/>.</summary>
    public const int MaxRequestsPerWindow = 60;

    /// <summary>
    /// The permits of the rolling window a set-preview page must leave free (#457): preview pages,
    /// first pass and verification re-read alike, are charged with this reserve, everything else
    /// with none. The ceiling stays <see cref="MaxRequestsPerWindow"/>; previews can take at most
    /// <c>MaxRequestsPerWindow - PreviewReserve</c> permits a minute, and the set list, the set-owner
    /// lookup and the editor grants are guaranteed the rest whatever the preview traffic.
    /// </summary>
    public const int PreviewReserve = 20;

    /// <summary>The length of the rolling rate window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long <see cref="TryChargeRequestAsync(CancellationToken)"/> waits for window headroom
    /// before refusing. Chosen to match the decorator's own budget wait: a single request inside an
    /// already-admitted lookup is worth waiting a few seconds for, since giving up throws away the
    /// pages already fetched, but not worth waiting so long that the caller's browser gives up first.
    /// </summary>
    public static readonly TimeSpan DefaultRequestWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrent, MaxConcurrent);
    private readonly Lock _windowGate = new();

    // Timestamps of the permits granted within the current rolling window, oldest first. Never longer
    // than MaxRequestsPerWindow: an entry is only enqueued after everything older than Window has been
    // dequeued and the remaining count was found to be below the ceiling.
    private readonly Queue<DateTimeOffset> _recentRequests = new();

    /// <summary>
    /// Waits for one of the <see cref="MaxConcurrent"/> lookup slots, up to <paramref name="timeout"/>.
    /// Returns a token that releases the slot on <see cref="IDisposable.Dispose"/> — callers must
    /// dispose it exactly once, normally in a <c>finally</c> around the guarded lookup — or <c>null</c>
    /// if <paramref name="timeout"/> elapsed first. Charges no request permit: the lookup this admits
    /// pays per request, as it makes them.
    /// </summary>
    public async Task<IDisposable?> TryAcquireConcurrencySlotAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;

        return await _concurrency.WaitAsync(Remaining(deadline), cancellationToken)
            ? new ConcurrencySlot(_concurrency)
            : null;
    }

    /// <inheritdoc />
    public Task<bool> TryChargeRequestAsync(CancellationToken cancellationToken = default) =>
        TryChargeRequestAsync(DefaultRequestWaitTimeout, cancellationToken);

    /// <summary>
    /// Same as <see cref="TryChargeRequestAsync(CancellationToken)"/> with an explicit wait budget —
    /// the seam the tests use to prove the window without waiting on it in real time.
    /// </summary>
    public Task<bool> TryChargeRequestAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        TryChargeRequestAsync(0, timeout, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> TryChargeRequestAsync(int reserve, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(reserve, MaxRequestsPerWindow);

        // The charge is granted while fewer than this many permits are held, so that after it at
        // least `reserve` stay free.
        var admitBelow = MaxRequestsPerWindow - reserve;
        var deadline = _timeProvider.GetUtcNow() + timeout;

        while (true)
        {
            TimeSpan waitForWindow;
            lock (_windowGate)
            {
                var now = _timeProvider.GetUtcNow();
                Trim(now);

                if (_recentRequests.Count < admitBelow)
                {
                    _recentRequests.Enqueue(now);
                    return true;
                }

                // The count must drop to admitBelow - 1, so the permit whose expiry frees this
                // charge is the (Count - admitBelow)-th oldest: for no reserve, the oldest one.
                waitForWindow = _recentRequests.ElementAt(_recentRequests.Count - admitBelow) + Window - now;
            }

            var remaining = Remaining(deadline);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var delay = waitForWindow < remaining ? waitForWindow : remaining;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, cancellationToken);
            }
        }
    }

    public void Dispose() => _concurrency.Dispose();

    // Drops every permit that has aged out of the rolling window. Called under _windowGate only.
    private void Trim(DateTimeOffset now)
    {
        while (_recentRequests.Count > 0 && now - _recentRequests.Peek() >= Window)
        {
            _recentRequests.Dequeue();
        }
    }

    private TimeSpan Remaining(DateTimeOffset deadline)
    {
        var remaining = deadline - _timeProvider.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private sealed class ConcurrencySlot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
