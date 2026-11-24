using EmotePurge.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// The two separate entry points of the bot account (plan #487 2.2), each called by exactly one process and never from
/// <c>AddEmotePurgeInfrastructure</c>: the Api seals with the public token key, the worker opens with the private one.
/// Each reads its own key straight from configuration (the keys are not part of <see cref="BotAccountOptions"/>, which
/// both processes can inject) and stops the start when <c>BotAccount:Enabled</c> is on and its key is missing or
/// unusable. The error names the key, never its value.
/// </summary>
public static class BotAccountRegistration
{
    /// <summary>
    /// Api only. Registers <see cref="IBotAccountTokenSealer"/>: the real sealer while the bot account is enabled,
    /// otherwise one that always refuses (the token route answers 404 first). Never registers an opener. Also registers
    /// the admin writer <see cref="IBotAccountService"/>, the one consumer of the sealer, whatever the switch: its read,
    /// token delete and kill switch stay reachable with the bot account off (plan #487 2.10).
    /// </summary>
    public static IServiceCollection AddBotAccountTokenSealing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IBotAccountService, BotAccountService>();

        if (!IsEnabled(configuration))
        {
            services.AddSingleton<IBotAccountTokenSealer, DisabledBotAccountTokenSealer>();
            return services;
        }

        // Imported here, not lazily, so a bad key stops the start instead of the first token request. Registered through
        // a factory so the container owns (and disposes) the sealer and with it the key.
        var publicKey = BotAccountTokenKeys.ImportPublicKey(configuration[BotAccountTokenKeys.PublicKeyName]);
        services.AddSingleton<IBotAccountTokenSealer>(_ => new RsaBotAccountTokenSealer(publicKey));
        return services;
    }

    /// <summary>
    /// Worker only. Registers the bot account's writing side while the bot account is enabled: for now the
    /// <see cref="IBotAccountTokenOpener"/>; the write client, runner and signal join here in later tasks. Registers
    /// nothing while it is disabled, and never a sealer.
    /// </summary>
    public static IServiceCollection AddBotAccountWriter(this IServiceCollection services, IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return services;
        }

        // Eager for the same reason as in AddBotAccountTokenSealing.
        var privateKey = BotAccountTokenKeys.ImportPrivateKey(configuration[BotAccountTokenKeys.PrivateKeyName]);
        services.AddSingleton<IBotAccountTokenOpener>(_ => new RsaBotAccountTokenOpener(privateKey));
        return services;
    }

    // Read the same way the options binder reads it, so the two never disagree about the switch.
    private static bool IsEnabled(IConfiguration configuration)
    {
        var options = new BotAccountOptions();
        configuration.GetSection(BotAccountOptions.SectionName).Bind(options);
        return options.Enabled;
    }
}
