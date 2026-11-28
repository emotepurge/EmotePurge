using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Telemetry;
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
    /// <summary>The base address of <see cref="SevenTvWriteClient"/>: 7TV's v4 API, the GraphQL endpoint below it.</summary>
    public static readonly Uri WriteClientBaseAddress = new("https://7tv.io/v4/");

    /// <summary>The time limit of one write-client request (plan 2.4: 10 s, not shortened).</summary>
    public static readonly TimeSpan WriteClientTimeout = TimeSpan.FromSeconds(10);

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
    /// Worker only. Registers the bot account's writing side while the bot account is enabled: the
    /// <see cref="IBotAccountTokenOpener"/>, the 7TV write client <see cref="ISevenTvWriteClient"/>, the loop's
    /// <see cref="IBotAccountRunner"/>, the wishes' part of its pass <see cref="IWishBotPass"/> and the editor read
    /// <see cref="IBotAccountEditorCheck"/> (all scoped: the worker resolves them once from a scope that lives as long as
    /// its loop). The fallback run the pass calls is registered by <c>AddEmotePurgeInfrastructure</c> whatever the switch. Registers nothing while it is disabled, and never
    /// a sealer. The wake-up signal is the worker's own and registered there whatever the switch, because the command
    /// loop that sets it always runs.
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

        // RemoveAllLoggers drops the factory's LoggingHttpMessageHandler pair for this client: at trace level it writes
        // the request headers, the bot's Authorization header among them (plan 2.4, AK75). The client logs for itself.
        // The token travels per request, never in DefaultRequestHeaders. 10 s stays (probe P3: addEmote up to 4.4 s).
        services.AddHttpClient<ISevenTvWriteClient, SevenTvWriteClient>(client =>
            {
                client.BaseAddress = WriteClientBaseAddress;
                client.Timeout = WriteClientTimeout;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("EmotePurge/1.0");
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler(serviceProvider => new ProviderRequestTelemetryHandler(
                RateLimitProviders.SevenTv,
                RateLimitCallSources.SevenTvBot,
                serviceProvider.GetRequiredService<IRateLimitTelemetry>()));

        // The editor read (plan 2.5b): scoped, so its one-minute reuse lives in the loop-long scope with the pass.
        services.AddScoped<IBotAccountEditorCheck, BotAccountEditorCheck>();
        services.AddScoped<IWishBotPass, WishBotPass>();
        services.AddScoped<IBotAccountRunner, BotAccountRunner>();
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
