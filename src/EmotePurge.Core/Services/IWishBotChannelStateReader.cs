using EmotePurge.Core.Entities;

namespace EmotePurge.Core.Services;

/// <summary>
/// The Api side's way to <see cref="WishBotChannelState.Of"/> (plan #487 2.3, Revision 6b K6b-5): it adds the pilot list,
/// the activator's admin check by id and the bot account's state. Scoped: the account row is read at most once per
/// scope (one request, one overview) and only for a channel whose bot mode is activated; a manual channel costs no read.
/// The expiry run does not use it: it reads settings and account in one statement (Revision 6d, K6d-7).
/// </summary>
public interface IWishBotChannelStateReader
{
    /// <summary>The channel's state; <paramref name="settings"/> null is a channel without a settings row (manual).</summary>
    /// <param name="settings">The channel's settings row as stored.</param>
    /// <param name="twitchChannelId">The channel row's Twitch id, for the pilot list; null is never listed.</param>
    Task<WishBotChannelMode> ReadAsync(ChannelWishSettings? settings, string? twitchChannelId, CancellationToken cancellationToken = default);
}
