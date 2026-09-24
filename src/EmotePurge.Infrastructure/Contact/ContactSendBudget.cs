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

    // A LinkedList, not a Queue: TryCharge still only ever trims from the front (oldest first, same
    // as before), but Release now needs to drop one specific, caller-identified entry — not
    // necessarily the front or the back — and a Queue offers no way to remove from the middle at all.
    // Trimming and Release are both still O(1): trimming only ever walks from the front, and
    // LinkedList<T>.Remove(node) is a constant-time unlink once the node itself is already in hand
    // (see ContactSendReservation), no traversal needed.
    private readonly LinkedList<DateTimeOffset> _granted = new();

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
    /// not send, and <paramref name="reservation"/> is then meaningless. The caller charges this only
    /// once a Turnstile verification has already succeeded — and, importantly, must not have spent an
    /// SMTP round trip getting here — so that a burst of shape-valid requests carrying an invalid
    /// token can fail Turnstile as many times as it likes without ever touching this budget (Codex P1,
    /// docs/DECISIONS.md 2026-09-24 revision). Pass the granted <paramref name="reservation"/> to
    /// <see cref="Release"/> to undo a charge whose SMTP send then failed anyway.
    /// </summary>
    public bool TryCharge(out ContactSendReservation reservation)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            while (_granted.Count > 0 && now - _granted.First!.Value >= _window)
            {
                _granted.RemoveFirst();
            }

            if (_granted.Count >= MaxSends)
            {
                reservation = default;
                return false;
            }

            reservation = new ContactSendReservation(_granted.AddLast(now));
            return true;
        }
    }

    /// <summary>
    /// Refunds exactly the permit <paramref name="reservation"/> identifies — for a caller that
    /// charged <see cref="TryCharge"/> expecting to send, but whose SMTP send then failed: only a
    /// message that actually left for the operator's mailbox should count against this budget, not an
    /// attempt an unrelated SMTP hiccup aborted.
    /// </summary>
    /// <remarks>
    /// Revised 2026-09-24 (Codex P2): this used to drop whichever entry was newest, on the assumption
    /// that a charge and its own release always run back-to-back with nothing else interleaved. Two
    /// sends overlapping in flight broke that assumption — charge A, charge B, then A's SMTP send
    /// fails — and dropping "newest" refunded B's still-good reservation while leaving A's, the one
    /// that actually failed, occupying a slot until it aged out on its own. Removing the node the
    /// caller's own reservation names, instead of a position in the list, makes the refund correct
    /// regardless of how many other charges happened in between. A no-op if the reservation's node is
    /// no longer in this budget — already trimmed by a since-elapsed window, or already released once
    /// (defensive only: a caller must never be able to call this twice for the same reservation, or
    /// without a matching prior <see cref="TryCharge"/>, but a double release must not corrupt an
    /// unrelated entry that happens to occupy the same list position afterwards).
    /// </remarks>
    public void Release(ContactSendReservation reservation)
    {
        lock (_gate)
        {
            var node = reservation.Node;
            if (node is not null && node.List == _granted)
            {
                _granted.Remove(node);
            }
        }
    }
}

/// <summary>
/// An opaque handle to one permit granted by <see cref="ContactSendBudget.TryCharge"/>, consumed by
/// <see cref="ContactSendBudget.Release"/> to refund that exact permit — never "whichever one is
/// newest" (see that method's remarks for why that used to be wrong). The default value (as produced
/// by a refused <see cref="ContactSendBudget.TryCharge"/>) identifies nothing and releases nothing.
/// </summary>
public readonly struct ContactSendReservation
{
    internal ContactSendReservation(LinkedListNode<DateTimeOffset> node)
    {
        Node = node;
    }

    internal LinkedListNode<DateTimeOffset>? Node { get; }
}
