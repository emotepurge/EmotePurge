using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Contact;

/// <summary>
/// The provider-wide ceiling on contact-form sends: at most <see cref="MaxSends"/> messages may be
/// sent within any rolling <see cref="Window"/>, across every visitor combined — distinct from the
/// per-IP ASP.NET Core policy (<c>RateLimitPolicyNames.Contact</c>) the endpoint's own
/// <c>RequireRateLimiting</c> already enforces, same split as <c>ForeignEmoteSetProviderBudget</c>
/// sits beside <c>ForeignEmoteLookup</c> and <c>SevenTvLeaderboardRequestBudget</c> sits beside
/// <c>SevenTvLeaderboard</c>. What the per-IP policy cannot catch is many different visitors (or one
/// behind a rotating pool of addresses) each staying under their own budget while jointly running
/// the operator's SMTP account into whatever sending limit their provider enforces.
/// </summary>
/// <remarks>
/// Rolling, not fixed, for the same reason as its two siblings above: a fixed window lets a burst
/// just before the boundary and another just after both pass as "one window", doubling the effective
/// rate at the seam. In-process and per run — a restart empties the budget, which is accepted here
/// exactly as it is for the two siblings (an operator-caused restart is not something a visitor can
/// trigger on demand).
/// </remarks>
public sealed class ContactSendBudget
{
    /// <summary>
    /// A sensible global ceiling above the sum of a great many honest visitors and well below what
    /// would make an operator's SMTP account look like a spam source: 30 sends per rolling hour.
    /// </summary>
    public const int DefaultMaxSends = 30;

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(1);

    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _granted = new();

    public ContactSendBudget(int maxSends = DefaultMaxSends, TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSends);
        var resolvedWindow = window ?? DefaultWindow;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolvedWindow, TimeSpan.Zero);

        MaxSends = maxSends;
        _window = resolvedWindow;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int MaxSends { get; }

    /// <summary>
    /// Takes one permit for one send, without waiting. <see langword="false"/> means the caller must
    /// not send — and, importantly, must not have spent a Turnstile verification call or an SMTP
    /// round trip getting here; the caller charges this before either.
    /// </summary>
    public bool TryCharge()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            while (_granted.Count > 0 && now - _granted.Peek() >= _window)
            {
                _granted.Dequeue();
            }

            if (_granted.Count >= MaxSends)
            {
                return false;
            }

            _granted.Enqueue(now);
            return true;
        }
    }
}
