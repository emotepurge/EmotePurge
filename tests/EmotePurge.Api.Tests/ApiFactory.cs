using System.Security.Claims;
using System.Text.Encodings.Web;
using EmotePurge.Api.Auth;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace EmotePurge.Api.Tests;

/// <summary>
/// Boots the real <c>Program.cs</c> pipeline — same middleware order, same <c>MapGroup</c> tree,
/// same filter registrations — with three substitutions and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The two authorization services are substituted because they are what the filters ask; the tests
/// then drive the filter matrix by deciding what those services answer. The database and Redis are
/// never resolved: every assertion below is reached by a filter short-circuiting before the handler,
/// and the one allow-path test substitutes the single service its handler uses.
/// </para>
/// <para>
/// The cookie scheme is replaced rather than driven, because it cannot be driven from a test:
/// <c>CookieSecurePolicy.Always</c> means <c>HttpClient</c> never stores the cookie over the
/// TestServer's <c>http://localhost</c> base address, and <c>OnValidatePrincipal</c> would hit
/// Postgres on every authenticated request. The replacement keeps the status codes identical —
/// a challenge is still 401 and a forbid is still 403.
/// </para>
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public IChannelAccessService ChannelAccess { get; } = Substitute.For<IChannelAccessService>();

    public IVoteEligibilityService VoteEligibility { get; } = Substitute.For<IVoteEligibilityService>();

    public IChannelService Channels { get; } = Substitute.For<IChannelService>();

    /// <summary>
    /// Substituted explicitly rather than left to the real Redis-backed implementation: the resync
    /// handler consults it, and per the note below every handler service is constructed before the
    /// filter pipeline runs — so even a 403 case would otherwise reach for a Redis that is not
    /// there. It also lets a test drive the cooldown outcome directly.
    /// </summary>
    public IChannelResyncCooldown ResyncCooldown { get; } = Substitute.For<IChannelResyncCooldown>();

    /// <summary>
    /// Substituted so the health tests can put the worker snapshot into any state; the real
    /// implementation reads Redis. Defaults to returning null ("no snapshot"), which is also
    /// what the substituted multiplexer would amount to.
    /// </summary>
    public IWorkerHealthReader WorkerHealth { get; } = Substitute.For<IWorkerHealthReader>();

    /// <summary>
    /// Substituted so the live-stream failure-path tests (issue #42) can drive
    /// <c>SubscribeAsync</c>'s result directly. The real <c>RedisLiveEventStream</c> would need
    /// <c>EnsureRedisSubscribedAsync</c> to succeed against the substituted
    /// <see cref="IConnectionMultiplexer"/> below, which it cannot.
    /// </summary>
    public ILiveEventStream LiveEventStream { get; } = Substitute.For<ILiveEventStream>();

    /// <summary>
    /// Substituted so the new <c>/api/seventv/...</c> group's allow-path test (any logged-in user,
    /// no role) never reaches real Twitch/7TV — the real implementation composes
    /// <c>IChannelIdentityService</c> and <c>ISevenTvApiClient</c>, both themselves typed HTTP
    /// clients this factory otherwise leaves real.
    /// </summary>
    public IForeignEmoteSetService ForeignEmoteSet { get; } = Substitute.For<IForeignEmoteSetService>();

    /// <summary>
    /// Substituted for the same reason as <see cref="ForeignEmoteSet"/> above: the leaderboard
    /// endpoint's whole filter-matrix test suite never needs a real 7TV round trip, only control over
    /// what <c>GetLeaderboardAsync</c> answers.
    /// </summary>
    public ISevenTvLeaderboardService Leaderboard { get; } = Substitute.For<ISevenTvLeaderboardService>();

    /// <summary>
    /// Substituted so the sync-imported contract tests can reach the handler at all: every other
    /// case in that group is answered by a filter or a body check short-circuiting before it, but
    /// "this SourceKind is accepted" can only be shown by the call arriving at the service — and the
    /// real implementation would go to Postgres. It is also what lets a test read back the exact
    /// vocabulary and source name the endpoint forwarded (spec F5/AK 12).
    /// </summary>
    public IEmoteService Emotes { get; } = Substitute.For<IEmoteService>();

    /// <summary>
    /// Substituted so the new set-listing routes (<c>GET /emote-sets</c>, <c>GET
    /// /me/emote-set-targets</c>, the <c>?emoteSetId=</c> mode of the foreign-channel preview) never
    /// reach real 7TV — the real implementation sits behind the guard chain (cache, coalescer,
    /// breaker, budget) this factory otherwise leaves real, none of which resolves without Redis.
    /// </summary>
    public ISevenTvEmoteSetListService EmoteSetList { get; } = Substitute.For<ISevenTvEmoteSetListService>();

    /// <summary>
    /// Substituted so <c>GET /emote-sets</c>' Ok branch never reaches the real Postgres-backed
    /// implementation. Defaults to an empty map (no channel has observations unless a test
    /// configures one) rather than leaving the substitute's un-stubbed result — an
    /// <c>IReadOnlyDictionary</c> — as <c>null</c>, which the handler's <c>TryGetValue</c> call would
    /// throw on.
    /// </summary>
    public IChannelEmoteSetObservationService EmoteSetObservations { get; } = CreateDefaultObservationService();

    /// <summary>
    /// Substituted for <c>GET /emotes/set-warning</c>'s allow-path tests: the real implementation
    /// takes <c>AppDbContext</c>, <c>ISevenTvApiClient</c> and <c>IModeratedChannelsProvider</c>, and
    /// this factory has no real database behind the placeholder connection string below.
    /// </summary>
    public IEmoteSetOwnershipService EmoteSetOwnership { get; } = Substitute.For<IEmoteSetOwnershipService>();

    /// <summary>
    /// Substituted for <c>GET /me/emote-set-targets</c>'s AK 25 cases (the "Grants Failed" case in
    /// particular): the real implementation goes through <c>IModRoleCache</c>'s Redis-backed
    /// <see cref="IConnectionMultiplexer"/>, which this factory substitutes but never answers a real
    /// grants lookup from.
    /// </summary>
    public ISevenTvEditorService EditorService { get; } = Substitute.For<ISevenTvEditorService>();

    /// <summary>
    /// Substituted for the set-centric <c>sync-imported</c>'s owner check, which reads its grants
    /// through the guarded refresh (spec 2026-09-20, section 32, second review round) rather than
    /// through <see cref="EditorService"/>. Its real implementation is pinned in the Infrastructure
    /// tests; here it would only meet the substituted multiplexer.
    /// </summary>
    public IGuardedSevenTvEditorGrantsService GuardedEditorGrants { get; } = Substitute.For<IGuardedSevenTvEditorGrantsService>();

    /// <summary>
    /// Substituted for the set-centric <c>sync-imported</c>'s owner check (spec 2026-09-20, section
    /// 32), which runs for real here: its one direct owner lookup is the only thing in this factory
    /// that would otherwise reach 7TV. The check's breaker and budget stay the real singletons.
    /// </summary>
    public ISevenTvApiClient SevenTvApi { get; } = Substitute.For<ISevenTvApiClient>();

    /// <summary>
    /// Substituted because Program.cs now runs the S3-34 migration guard at startup — the real
    /// implementation would open a connection to the placeholder database configured below.
    /// The substitute simply completes, which is the "fully migrated" answer.
    /// </summary>
    private readonly IPendingMigrationGuard _migrationGuard = Substitute.For<IPendingMigrationGuard>();

    /// <summary>
    /// Substituted so the legal-pages tests (issue #247) can drive "configured" vs. "not configured"
    /// directly, without a real ContentPath on disk. NSubstitute's own default for a Task&lt;record&gt;
    /// member is a null record, not a record of falses/nulls, so the availability default is set
    /// explicitly below — the endpoint dereferences the record's fields directly and would otherwise
    /// throw on every test that never overrides it.
    /// </summary>
    public ILegalContentService LegalContent { get; } = Substitute.For<ILegalContentService>();

    /// <summary>
    /// Substituted so the admin account-deletion filter-matrix tests can reach the handler at all
    /// (the real implementation locks a Postgres row) and drive its outcome directly — the same
    /// reasoning as <see cref="Emotes"/>.
    /// </summary>
    public IAccountDeletionService AccountDeletion { get; } = Substitute.For<IAccountDeletionService>();

    public ApiFactory()
    {
        LegalContent.GetAvailabilityAsync(Arg.Any<CancellationToken>())
            .Returns(new LegalDocumentAvailability(ImprintAvailable: false, PrivacyAvailable: false));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" rather than the default "Development": Development would load the developer's
        // user-secrets (the API project has a UserSecretsId), which makes the suite depend on the
        // machine it runs on. Redis:ConnectionString has to be present or AddEmotePurgeInfrastructure
        // throws at startup — present, not reachable: nothing here ever opens the connection.
        builder.UseEnvironment("Testing");
        builder.UseSetting("Redis:ConnectionString", "localhost:6379");
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=none;Username=none;Password=none");
        builder.UseSetting("Auth:AdminTwitchLogins", string.Empty);

        // Warnings and above still reach the console. A failing filter test reports only a status
        // code, and the difference between "the filter answered 500" and "the filter answered the
        // wrong code" is otherwise invisible — the unhandled-exception log is what tells them apart.
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole();
            logging.SetMinimumLevel(LogLevel.Warning);
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddScoped(_ => ChannelAccess);
            services.AddScoped(_ => VoteEligibility);
            services.AddScoped(_ => Channels);
            services.AddSingleton(_ => ResyncCooldown);
            services.AddSingleton(_ => WorkerHealth);
            services.AddSingleton(_ => LiveEventStream);
            services.AddScoped(_ => ForeignEmoteSet);
            services.AddScoped(_ => Leaderboard);
            services.AddScoped(_ => Emotes);
            services.AddScoped(_ => EmoteSetList);
            services.AddScoped(_ => EmoteSetObservations);
            services.AddScoped(_ => EmoteSetOwnership);
            services.AddScoped(_ => EditorService);
            services.AddScoped(_ => GuardedEditorGrants);
            services.AddScoped(_ => SevenTvApi);
            services.AddScoped(_ => _migrationGuard);
            services.AddSingleton(_ => LegalContent);
            services.AddScoped(_ => AccountDeletion);

            // Load-bearing, and not obvious: RequestDelegateFactory resolves a handler's injected
            // services *before* it runs the endpoint filter pipeline. A request the filter is about
            // to reject with 403 therefore still constructs every service that handler asks for —
            // and several of those take IConnectionMultiplexer, whose registration calls
            // ConnectionMultiplexer.Connect eagerly. Without this line the matrix spends twelve
            // seconds per case waiting for a Redis that is not there and then answers 500 instead of
            // the status code under test. Substituting the multiplexer keeps the whole suite free of
            // external infrastructure while leaving the real service graph in place.
            services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

            // Registered after the cookie scheme, so this DefaultScheme assignment is the one that
            // sticks. Forbid() and the RequireAuthorization challenge both resolve to this handler.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    private static IChannelEmoteSetObservationService CreateDefaultObservationService()
    {
        var service = Substitute.For<IChannelEmoteSetObservationService>();
        service.ListIntervalsByChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, IReadOnlyList<ChannelEmoteSetObservationInterval>>());
        return service;
    }
}

/// <summary>
/// Turns two request headers into a principal. Absent header = anonymous, which is how the 401 cases
/// are driven; a user id without a login is the "authenticated but claims incomplete" case that
/// <c>TryBuildTwitchPrincipal</c> rejects, and which every filter answers with 401 rather than 403.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserIdHeader = "X-Test-UserId";
    public const string LoginHeader = "X-Test-Login";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserIdHeader, out var userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (Request.Headers.TryGetValue(LoginHeader, out var login))
        {
            claims.Add(new Claim(TwitchClaimTypes.Login, login.ToString()));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
