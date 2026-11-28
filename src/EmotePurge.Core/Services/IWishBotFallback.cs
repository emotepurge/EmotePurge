namespace EmotePurge.Core.Services;

/// <summary>
/// The fallback run of the wish bot (#487 plan 2.8, [W8]; Revision 6b K6b-7, K6b-11, K6b-17; 6e K6e-9): for every
/// activated channel whose effective state is <see cref="WishBotChannelMode.Fallback"/> (all of them while the account is
/// unavailable) it hands the open bot actions to the managers: an add whose outcome is open (<c>approved</c> with a
/// committed intent, <c>unconfirmed</c>) → <c>presented</c> (<c>unconfirmed</c>); <c>active</c> with a queued removal →
/// <c>presented</c> (<c>bot_unavailable</c>), queue cleared; <c>presented</c> with a queued removal → queue cleared; an open
/// removal attempt → resolved without 7TV (<see cref="WishBotAttemptResolution"/>, the evidence stays as a repair phase).
/// <c>approved</c> without an intent stays (it counts as needing a human); open repair phases stay. Each wish under its own
/// row lock in its own transaction; a row gone by then (K7a) is "nothing to do". One <c>wish.changed</c> per channel, no
/// audit. Without an activated channel with open bot actions it costs one query.
/// <para>
/// Scoped, registered by <c>AddEmotePurgeInfrastructure</c> whatever <c>BotAccount:Enabled</c> says, because the expiry
/// run calls it as its step 0 also with the bot account switched off, so switching it off leaves nothing hanging. Needs
/// no advisory lock. Leaves nothing tracked in the caller's context.
/// </para>
/// </summary>
public interface IWishBotFallback
{
    /// <summary>
    /// The runner's call (step 4): the channels' states from <paramref name="account"/>, the account as the pass's session
    /// saw it (only the runner books editor proofs, and only for the version its commit sees, so a mixed state can delay a
    /// fallback but never cause a wrong one). Returns the number of wishes changed.
    /// </summary>
    Task<int> ApplyAsync(BotAccountAvailabilityState account, CancellationToken cancellationToken = default);

    /// <summary>
    /// The expiry run's call (its step 0): each channel's state from its settings row and the bot account's row read in
    /// one statement (one snapshot, the read of the expiry decision, K6d-7), with the switches of this process. Returns the
    /// number of wishes changed.
    /// </summary>
    Task<int> ApplyAsync(CancellationToken cancellationToken = default);
}
