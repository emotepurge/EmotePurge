using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>The decision of the expiry run for one due wish.</summary>
/// <param name="Queue">True: queue the bot removal. False: present the wish with <paramref name="Reason"/>.</param>
/// <param name="Reason">The <see cref="WishStatusReason"/> when presenting, null when queueing.</param>
public readonly record struct WishExpiryDecision(bool Queue, string? Reason)
{
    public static WishExpiryDecision Present(string reason) => new(false, reason);

    public static WishExpiryDecision QueueRemoval { get; } = new(true, null);

    /// <summary>The <c>outcome</c> of the <c>wish.due</c> audit entry: the reason, or <see cref="WishExpiryPolicy.QueuedOutcome"/>.</summary>
    public string Outcome => Queue ? WishExpiryPolicy.QueuedOutcome : Reason!;
}

/// <summary>The facts <see cref="WishExpiryPolicy.Decide"/> reads; the caller collects them.</summary>
/// <param name="BotModeEnabled">The raw switch, not <c>IsBotMode</c>: a suspended channel still counts as bot mode here.</param>
/// <param name="BotSuspendedReason">Set while the channel's bot is suspended.</param>
/// <param name="BotOwned">The wish's entry was written by the bot (ownership).</param>
/// <param name="BotGloballyAvailable">The answer of <see cref="IWishBotAvailability"/>.</param>
/// <param name="StartsAtAuthoritative">False while the start is only the provisional EventAPI date.</param>
/// <param name="ProtectionThresholdPerDay">Null: protection off.</param>
/// <param name="MeanPerDay">The window mean; required when <see cref="WishExpiryPolicy.NeedsUsage"/> is true.</param>
/// <param name="HasCoverageGap">The window gap; required when <see cref="WishExpiryPolicy.NeedsUsage"/> is true.</param>
public sealed record WishExpiryFacts(
    bool BotModeEnabled,
    string? BotSuspendedReason,
    bool BotOwned,
    bool BotGloballyAvailable,
    bool StartsAtAuthoritative,
    decimal? ProtectionThresholdPerDay,
    decimal? MeanPerDay = null,
    bool? HasCoverageGap = null);

/// <summary>
/// The pure decision table of the expiry run (spec K2, plan 2.2): no I/O, no clock. Seven rows, the first
/// that applies wins.
/// </summary>
public static class WishExpiryPolicy
{
    /// <summary>The <c>outcome</c> value of <c>wish.due</c> when the removal was queued.</summary>
    public const string QueuedOutcome = "queued";

    /// <summary>
    /// Whether a due wish is held back (no write, no audit, no event): its start is only provisional, the
    /// channel synced less than 24 hours ago and the expiry lies less than 24 hours back. Both limits are
    /// exclusive of exactly 24 hours, so a wish held back at 23:59 falls due at 24:00.
    /// </summary>
    public static bool IsHeldBack(bool startsAtAuthoritative, DateTime expiresAtUtc, DateTime? channelLastSyncedAtUtc, DateTime nowUtc)
    {
        if (startsAtAuthoritative || channelLastSyncedAtUtc is null)
        {
            return false;
        }

        return nowUtc - channelLastSyncedAtUtc.Value < WishLimits.ProvisionalStartGrace
            && nowUtc - expiresAtUtc < WishLimits.ProvisionalStartGrace;
    }

    /// <summary>
    /// Whether <see cref="Decide"/> needs the usage window: bot mode on, owned, bot available (globally and
    /// for the channel), authoritative start and a threshold set. Every earlier row decides without it.
    /// </summary>
    public static bool NeedsUsage(WishExpiryFacts facts) =>
        facts.BotModeEnabled
        && facts.BotOwned
        && facts.StartsAtAuthoritative
        && facts.BotGloballyAvailable
        && facts.BotSuspendedReason is null
        && facts.ProtectionThresholdPerDay is not null;

    public static WishExpiryDecision Decide(WishExpiryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.BotModeEnabled)
        {
            return WishExpiryDecision.Present(WishStatusReason.Expired);
        }

        if (!facts.BotOwned)
        {
            return WishExpiryDecision.Present(WishStatusReason.NotBotOwned);
        }

        // Before the bot outage: bot_unavailable allows "remove by bot" later, a provisional start never may.
        if (!facts.StartsAtAuthoritative)
        {
            return WishExpiryDecision.Present(WishStatusReason.UnconfirmedStart);
        }

        if (!facts.BotGloballyAvailable || facts.BotSuspendedReason is not null)
        {
            return WishExpiryDecision.Present(WishStatusReason.BotUnavailable);
        }

        if (facts.ProtectionThresholdPerDay is not { } threshold)
        {
            return WishExpiryDecision.QueueRemoval;
        }

        if (facts.HasCoverageGap is not { } gap || facts.MeanPerDay is not { } mean)
        {
            throw new InvalidOperationException("The usage evaluation is required when protection is on and the bot is available.");
        }

        if (gap)
        {
            return WishExpiryDecision.Present(WishStatusReason.CoverageGap);
        }

        return mean >= threshold
            ? WishExpiryDecision.Present(WishStatusReason.Protected)
            : WishExpiryDecision.QueueRemoval;
    }
}
