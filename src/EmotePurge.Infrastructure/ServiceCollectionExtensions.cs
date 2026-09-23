using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Core.Twitch;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Redis;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Telemetry;
using EmotePurge.Infrastructure.Twitch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EmotePurge.Infrastructure;

public static class ServiceCollectionExtensions
{
    // The raw foreign-channel-import chain's DI key (see its registration below) — private because
    // nothing outside this method resolves it directly; the hardening decorator is the only consumer,
    // and it takes the raw chain through a plain constructor parameter, not a keyed-service attribute.
    private const string RawForeignEmoteSetServiceKey = "raw-foreign-emote-set";

    // The leaderboard's own circuit-breaker instance (see its registration below). Keyed rather than
    // typed because the foreign-channel preview already holds the typed registration of the same
    // policy class, and the two must not share state: a 7TV search-bucket lockout has to stop the
    // leaderboard without closing a preview path that never touches that bucket.
    private const string LeaderboardBreakerKey = "seventv-leaderboard";

    public static IServiceCollection AddEmotePurgeInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));

        // Called once at startup by both hosts (S3-34 fail-fast); scoped like everything else
        // that takes the AppDbContext.
        services.AddScoped<IPendingMigrationGuard, PendingMigrationGuard>();

        var redisConnectionString = configuration["Redis:ConnectionString"]
            ?? throw new InvalidOperationException("Konfigurationswert 'Redis:ConnectionString' fehlt.");

        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(redisConnectionString));

        services.AddSingleton<IRedisPublisher, RedisPublisher>();
        services.AddSingleton<IRedisSubscriber, RedisSubscriber>();

        // Singleton by necessity, not convenience: it holds the one process-wide subscription to
        // live:events and the fan-out table of the open browser connections. Built through a factory
        // so its options record keeps its defaults instead of needing its own registration.
        services.AddSingleton<ILiveEventStream>(sp => new RedisLiveEventStream(
            sp.GetRequiredService<IRedisSubscriber>(),
            sp.GetRequiredService<ILogger<RedisLiveEventStream>>()));

        // Bound and validated eagerly (fail-fast, same reasoning as RateLimitingOptions.Validate() in
        // the Api's Program.cs) rather than behind IOptions: ChannelService reads it on every join,
        // and there is no reload hook that would ever make a live snapshot indirection pay for itself.
        var channelCapacityOptions = new ChannelCapacityOptions();
        configuration.GetSection(ChannelCapacityOptions.SectionName).Bind(channelCapacityOptions);
        channelCapacityOptions.Validate();
        services.AddSingleton(channelCapacityOptions);

        services.AddScoped<IChannelService, ChannelService>();
        // Scoped like every other AppDbContext consumer, with its warning deduplication parked in a
        // singleton beside it: the worker opens a fresh scope per reconcile tick, so a set living on
        // the service itself would be empty every time. Same split as ChannelSyncGate below.
        services.AddSingleton<ChannelIdentityWarningState>();
        services.AddScoped<IChannelIdentityService, ChannelIdentityService>();
        services.AddScoped<IAdminChannelQueryService, AdminChannelQueryService>();
        services.AddScoped<IAdminUserQueryService, AdminUserQueryService>();
        // Read side only — audit entries are written by the services that perform the actions, into
        // those actions' own transactions (see AuditLogWrites).
        services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
        services.AddScoped<IEmoteService, EmoteService>();
        services.AddScoped<IEmoteSetOwnershipService, EmoteSetOwnershipService>();
        services.AddScoped<IEmoteSetStatusService, EmoteSetStatusService>();
        services.AddScoped<IEmoteListQueryService, EmoteListQueryService>();

        services.AddSingleton<IEmoteMatchCache, EmoteMatchCache>();
        services.AddSingleton<IDuplicateEmoteNameTracker, DuplicateEmoteNameTracker>();
        services.AddScoped<IUsageStatFlushService, UsageStatFlushService>();
        services.AddScoped<IUsageStatQueryService, UsageStatQueryService>();
        services.AddScoped<ILiveCoverageService, LiveCoverageService>();

        services.AddHttpClient<ISevenTvApiClient, SevenTvApiClient>(client =>
        {
            client.BaseAddress = new Uri("https://7tv.io/v3/");
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EmotePurge/1.0");
        })
        .AddHttpMessageHandler(sp => ProviderTelemetry(sp, RateLimitProviders.SevenTv, RateLimitCallSources.SevenTvRest));
        services.AddSingleton<ChannelSyncGate>();
        services.AddScoped<ISevenTvSyncService, SevenTvSyncService>();

        // Foreign-channel-import read path (spec 2026-09-09). The raw resolution chain (T1) is
        // registered under a key so the hardening decorator (T2, below) can depend on
        // IForeignEmoteSetService for its inner collaborator without resolving itself — the two
        // share one interface on purpose, so a unit test can substitute either side independently of
        // the other, the same way ForeignEmoteSetServiceTests already substitutes the raw chain's own
        // two collaborators.
        services.AddKeyedScoped<IForeignEmoteSetService, ForeignEmoteSetService>(RawForeignEmoteSetServiceKey);

        // Hardening (spec section 6, T2): a 60 s Redis cache (E3), request coalescing, a 429-aware
        // circuit breaker (E4) and a provider-wide concurrency/rate budget (E5b) wrapped around the
        // raw chain above. The three collaborators that carry state across requests are singletons —
        // a breaker, a budget or an in-flight-request table scoped per request would guard nothing —
        // while the decorator itself stays scoped, matching the raw chain it wraps.
        services.AddSingleton<IForeignEmoteSetCache, ForeignEmoteSetCache>();
        services.AddSingleton<ForeignEmoteSetRequestCoalescer>();
        services.AddSingleton<ForeignSevenTvBreakerPolicy>();
        services.AddSingleton<ForeignEmoteSetProviderBudget>();

        // Same instance under its request-charging face: the concurrency gate is taken once per
        // lookup by the decorator (which needs the concrete type), while every step that actually
        // issues an upstream request charges the rolling rate window through this interface. Two
        // registrations of one object, never two budgets.
        services.AddSingleton<IForeignUpstreamRequestBudget>(sp => sp.GetRequiredService<ForeignEmoteSetProviderBudget>());
        services.AddScoped<IForeignEmoteSetService>(sp => new HardenedForeignEmoteSetService(
            sp.GetRequiredKeyedService<IForeignEmoteSetService>(RawForeignEmoteSetServiceKey),
            sp.GetRequiredService<IForeignEmoteSetCache>(),
            sp.GetRequiredService<ForeignEmoteSetRequestCoalescer>(),
            sp.GetRequiredService<ForeignSevenTvBreakerPolicy>(),
            sp.GetRequiredService<ForeignEmoteSetProviderBudget>(),
            sp.GetRequiredService<IRateLimitTelemetry>(),
            sp.GetRequiredService<ILogger<HardenedForeignEmoteSetService>>()));

        // Leaderboard import source (spec 2026-09-13, section 6, T3). Purely additive: the typed
        // ForeignSevenTvBreakerPolicy registration above is untouched, and the HardenedForeignEmoteSetService
        // factory above still takes that same typed instance through GetRequiredService, not a keyed
        // lookup. The keyed registration here is a second, independent object of the same class — the
        // policy keeps all its state in instance fields, so two of them share nothing.
        services.AddKeyedSingleton<ForeignSevenTvBreakerPolicy>(LeaderboardBreakerKey);

        // Singletons by necessity, like the preview's guards: a stock, a window budget or an alarm
        // latch scoped per request would guard nothing at all. The budget is registered as a built
        // instance so that the two figures the spec fixes stay where they are documented, on the
        // class itself.
        services.AddSingleton<SevenTvLeaderboardStore<SevenTvLeaderboardResult>>();
        services.AddSingleton(new SevenTvLeaderboardRequestBudget(
            SevenTvLeaderboardRequestBudget.DefaultMaxRequests, SevenTvLeaderboardRequestBudget.DefaultWindow));
        services.AddSingleton<SevenTvLeaderboardBudgetAlarm>();

        // The service itself stays scoped, matching every other read path: it holds no state of its
        // own, and its 7TV client is a typed HttpClient that must not be captured for the life of the
        // process.
        services.AddScoped<ISevenTvLeaderboardService>(sp => new SevenTvLeaderboardService(
            sp.GetRequiredService<ISevenTvApiClient>(),
            sp.GetRequiredService<SevenTvLeaderboardStore<SevenTvLeaderboardResult>>(),
            sp.GetRequiredService<SevenTvLeaderboardRequestBudget>(),
            sp.GetRequiredKeyedService<ForeignSevenTvBreakerPolicy>(LeaderboardBreakerKey),
            sp.GetRequiredService<SevenTvLeaderboardBudgetAlarm>(),
            sp.GetRequiredService<ILogger<SevenTvLeaderboardService>>()));

        services.AddHttpClient<ITwitchAuthClient, TwitchAuthClient>(client =>
        {
            client.BaseAddress = new Uri("https://id.twitch.tv/");
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .AddHttpMessageHandler(sp => ProviderTelemetry(sp, RateLimitProviders.Twitch, RateLimitCallSources.TwitchAuth));

        var twitchClientId = configuration["Auth:Twitch:ClientId"];
        services.AddHttpClient<ITwitchHelixClient, TwitchHelixClient>(client =>
        {
            client.BaseAddress = new Uri("https://api.twitch.tv/helix/");
            client.Timeout = TimeSpan.FromSeconds(10);
            if (!string.IsNullOrEmpty(twitchClientId))
            {
                client.DefaultRequestHeaders.Add("Client-Id", twitchClientId);
            }
        })
        .AddHttpMessageHandler(sp => ProviderTelemetry(sp, RateLimitProviders.Twitch, RateLimitCallSources.TwitchHelix));

        var chatLogArchiveOptions = new ChatLogArchiveOptions();
        configuration.GetSection("ChatLogArchive").Bind(chatLogArchiveOptions);
        services.AddSingleton(chatLogArchiveOptions);

        // T3 (#69): read-only, sequential-by-contract client for the third-party chat-log archive
        // that backs the accuracy-harness backfill. No telemetry handler here on purpose
        // (Plan-Entscheidung 3) — the archive is an optional, unreliable harness dependency
        // behind its own feature flag (design doc Premise 5), not a provider the rate-limit
        // dashboard needs to track like Twitch/7TV.
        //
        // Registered transient, as the typed-client pattern (and the plan) dictate — but the
        // RequestDelay pacing that keeps this client from hammering a free third-party service
        // lives in *instance* state on ChatLogArchiveClient (see its class comment). Resolving a
        // fresh instance per call silently drops that pacing: no exception, no log line, just a
        // client that no longer waits between requests. Task 6 (the harness) must resolve this
        // client exactly once per run and hold it for the whole day-loop (Fixrunde 1 finding).
        services.AddHttpClient<IChatLogArchiveClient, ChatLogArchiveClient>(client =>
        {
            client.BaseAddress = new Uri(chatLogArchiveOptions.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30); // header phase only — see ChatLogArchiveClient's body-timeout CTS
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EmotePurge/1.0");
        });

        // Singleton cache over the transient typed client — see the class comment for why it
        // resolves ITwitchAuthClient through a scope instead of injecting it.
        services.AddSingleton<ITwitchAppTokenProvider, TwitchAppTokenProvider>();

        services.AddSingleton<ITokenCipher, AesGcmTokenCipher>();
        services.AddSingleton<TwitchTokenRefreshGate>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ITwitchUserTokenService, TwitchUserTokenService>();
        services.AddScoped<IModeratedChannelsProvider, ModeratedChannelsProvider>();
        services.AddScoped<IModeratorCheckService, ModeratorCheckService>();
        services.AddScoped<ISevenTvEditorService, SevenTvEditorService>();
        services.AddScoped<IChannelAccessService, ChannelAccessService>();
        services.AddScoped<IMyChannelsService, MyChannelsService>();
        services.AddSingleton<IModRoleCache, ModRoleCache>();

        // Next to the role cache: same shape (a Redis key with a TTL), same lifetime, and both hold
        // no state of their own. Consumed only by the channel-scoped resync endpoint — the worker's
        // periodic resync must not see it.
        services.AddSingleton<IChannelResyncCooldown, ChannelResyncCooldown>();
        services.AddSingleton<IWorkerHealthReader, WorkerHealthReader>();
        services.AddSingleton<IWorkerRosterReader, WorkerRosterReader>();

        // One instance behind both interfaces: the worker writes, the API reads, the wire format
        // lives in one class (see TwitchLiveStatusStore).
        services.AddSingleton<TwitchLiveStatusStore>();
        services.AddSingleton<ITwitchLiveStatusReader>(sp => sp.GetRequiredService<TwitchLiveStatusStore>());
        services.AddSingleton<ITwitchLiveStatusWriter>(sp => sp.GetRequiredService<TwitchLiveStatusStore>());

        // Same one-instance-behind-two-interfaces shape as the live-status store above: the product
        // path writes, the admin page reads, and the bucket layout lives in one class. Registered with
        // TimeProvider.System explicitly instead of resolving it from DI, so the only thing that can
        // move this clock is a test constructing the store itself.
        services.AddSingleton(sp => new RateLimitTelemetryStore(
            sp.GetRequiredService<IConnectionMultiplexer>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<RateLimitTelemetryStore>>()));
        services.AddSingleton<IRateLimitTelemetry>(sp => sp.GetRequiredService<RateLimitTelemetryStore>());
        services.AddSingleton<IRateLimitTelemetryReader>(sp => sp.GetRequiredService<RateLimitTelemetryStore>());

        services.AddScoped<IVoteSessionService, VoteSessionService>();
        services.AddScoped<IVoteSessionQueryService, VoteSessionQueryService>();
        services.AddScoped<IVoteEligibilityService, VoteEligibilityService>();

        return services;
    }

    /// <summary>
    /// One counting handler for one typed client. All three outgoing clients get one — a call nobody
    /// counts is a call the monitoring page silently reports as not having happened, and the two
    /// Twitch clients have to stay apart: a Helix pagination and a token refresh are the same provider
    /// and nothing like the same traffic.
    /// </summary>
    /// <remarks>
    /// The browser's direct 7TV GraphQL mutations (the mass-delete engine) never pass through here —
    /// they leave the user's machine, not this server. That is a real gap, and it is the admin page's
    /// job to say so rather than this handler's to pretend otherwise.
    /// </remarks>
    private static ProviderRequestTelemetryHandler ProviderTelemetry(
        IServiceProvider serviceProvider,
        string providerName,
        string callSource)
        => new(providerName, callSource, serviceProvider.GetRequiredService<IRateLimitTelemetry>());
}
