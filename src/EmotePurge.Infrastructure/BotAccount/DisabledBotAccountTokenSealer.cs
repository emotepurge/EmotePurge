using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// Stands in for the sealer while <c>BotAccount:Enabled</c> is off (plan #487 2.2): the token route answers 404 before
/// it would seal anything, so reaching <see cref="Seal"/> is a programming error.
/// </summary>
public sealed class DisabledBotAccountTokenSealer : IBotAccountTokenSealer
{
    public string? KeyFingerprint => null;

    public string Seal(string token) =>
        throw new InvalidOperationException("The bot account is disabled (BotAccount:Enabled is false); no token can be sealed.");
}
