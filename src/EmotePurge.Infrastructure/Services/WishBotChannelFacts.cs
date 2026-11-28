using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The one read point for a channel's bot state where the caller books from it (#487 plan 2.3, Revision 6d K6d-7, 6e
/// K6e-9): the channel's settings row, left-joined with the bot account's singleton row, and the channel's Twitch id for
/// the pilot list, all from <b>one statement</b> (one snapshot). Two statements would let a token set, a check and a new
/// editor proof commit in between and mix a proof of one identity with the account of the next. Used by the expiry run's
/// decision (row 4) and by its fallback run (step 0); the Api reads through <see cref="IWishBotChannelStateReader"/>.
/// </summary>
internal sealed record WishBotChannelFacts(
    ChannelWishSettings Settings,
    string? TwitchChannelId,
    bool HasAccountRow,
    string? Status,
    DateTime? ExpiresAtUtc,
    bool KillSwitch,
    string? BotSevenTvUserId,
    int IdentityVersion)
{
    /// <summary>The facts of <paramref name="channelId"/>; null without a settings row (a manual channel).</summary>
    public static Task<WishBotChannelFacts?> ReadAsync(AppDbContext db, string channelId, CancellationToken cancellationToken) =>
        (from s in db.ChannelWishSettings.AsNoTracking().Where(s => s.ChannelId == channelId)
         from c in db.BotAccountCredentials.AsNoTracking().Where(c => c.Id == BotAccountCredential.SingletonId).DefaultIfEmpty()
         select new WishBotChannelFacts(
             new ChannelWishSettings
             {
                 ChannelId = s.ChannelId,
                 ProtectionThresholdPerDay = s.ProtectionThresholdPerDay,
                 BotModeEnabled = s.BotModeEnabled,
                 BotModeActivatedByTwitchUserId = s.BotModeActivatedByTwitchUserId,
                 BotSuspendedReason = s.BotSuspendedReason,
                 BotEditorCheckedAtUtc = s.BotEditorCheckedAtUtc,
                 BotEditorCheckedIdentityVersion = s.BotEditorCheckedIdentityVersion,
                 BotConsentSevenTvUserId = s.BotConsentSevenTvUserId,
             },
             s.Channel!.TwitchChannelId,
             c != null,
             c != null ? c.Status : null,
             c != null ? c.ExpiresAtUtc : null,
             c != null && c.KillSwitch,
             c != null ? c.BotSevenTvUserId : null,
             c != null ? c.IdentityVersion : 0))
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The same rule as the Api's reader, from the one snapshot: the account-wide answer by the shared rule with the
    /// switches of this process, the pilot list and the activator's admin check by id. No facts: manual.
    /// </summary>
    public static WishBotChannelMode StateOf(
        WishBotChannelFacts? facts, bool switchesOn, WishPilotList pilotList, IGlobalAdminAllowlist admins, DateTime nowUtc)
    {
        if (facts is null || !facts.Settings.BotModeEnabled)
        {
            return WishBotChannelMode.Manual;
        }

        var account = facts.HasAccountRow
            ? BotAccountAvailabilityState.Of(
                switchesOn,
                facts.Status,
                facts.ExpiresAtUtc,
                facts.KillSwitch,
                facts.BotSevenTvUserId,
                facts.IdentityVersion,
                nowUtc)
            : BotAccountAvailabilityState.Unavailable;
        return WishBotChannelState.Of(
            facts.Settings,
            account,
            pilotList.Covers(facts.TwitchChannelId),
            WishBotChannelStateReader.ActivatorIsAdminById(admins, facts.Settings),
            nowUtc);
    }
}
