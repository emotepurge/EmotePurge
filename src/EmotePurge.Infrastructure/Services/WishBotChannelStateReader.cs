using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// <see cref="IWishBotChannelStateReader"/>: the pure <see cref="WishBotChannelState.Of"/> fed with the pilot list of the
/// own process, the activator's admin check by id (<see cref="IGlobalAdminAllowlist.IsAdminById(string)"/>, never the login
/// list) and the bot account's state, which <see cref="IWishBotAvailability.GetStateAsync"/> reads once per scope and only
/// when an activated channel asks.
/// </summary>
public sealed class WishBotChannelStateReader(
    IWishBotAvailability availability,
    WishOptions wishOptions,
    IGlobalAdminAllowlist admins,
    TimeProvider timeProvider) : IWishBotChannelStateReader
{
    private readonly WishPilotList _pilotList = wishOptions.PilotList;
    private BotAccountAvailabilityState? _account;

    public async Task<WishBotChannelMode> ReadAsync(
        ChannelWishSettings? settings, string? twitchChannelId, CancellationToken cancellationToken = default)
    {
        if (settings is not { BotModeEnabled: true })
        {
            return WishBotChannelMode.Manual;
        }

        _account ??= await availability.GetStateAsync(cancellationToken);
        return WishBotChannelState.Of(
            settings,
            _account.Value,
            _pilotList.Covers(twitchChannelId),
            ActivatorIsAdminById(admins, settings),
            timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>The activator of the stored activation is a global admin by id; no activator is none.</summary>
    internal static bool ActivatorIsAdminById(IGlobalAdminAllowlist admins, ChannelWishSettings settings) =>
        settings.BotModeActivatedByTwitchUserId is { Length: > 0 } activator && admins.IsAdminById(activator);
}
