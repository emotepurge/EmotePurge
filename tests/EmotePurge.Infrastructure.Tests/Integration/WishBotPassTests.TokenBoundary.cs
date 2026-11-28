using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Unit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Unit.SevenTvWriteClientTestData;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The bot token's trust boundary for everything the wish bot does with it (plan #487 T11b, spec AK74/AK75, Sol-P2-9,
// extending T11a's BotAccountTokenBoundaryTests): the real runner, the real wish pass and the real write client run
// against real Postgres with every logger at trace level and the real persisted log capture next to a console stand-in.
// Only the network end is a stub of "7TV" that keeps a set per id, so an add, a removal, an acceptance and a repair
// really take effect. Searched after each run: every console line (all categories), every stored log row, every audit
// entry, every wish and channel row, every wish.changed event, the credential row without its seal column and every
// exception that left a pass, for the token (whole and its segments), Bearer, the seal, and the echoes a failing 7TV
// puts into its answers (message, body, exception text). The routes through the real services close the circle.
public partial class WishBotPassTests
{
    private const string EchoMarker = "SECRETECHOMARKER";

    private static readonly JsonSerializerOptions DumpOptions = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };

    // ------------------------------------------------------------------------------------ the writes with an Ok answer

    // Accept, add, removal and repair in one run over three passes, with the answers of a working 7TV.
    [Fact]
    public async Task AcceptAddRemoveAndRepair_WithOkAnswers_LeakNothingIntoAnySink()
    {
        await using var rig = await StartBoundaryAsync();
        var work = await SeedBoundaryWorkAsync(rig);

        await rig.RunPassesAsync(this, 3);

        var seen = string.Join(",", rig.Handler.Requests.Select(r => r.Kind));
        Assert.Equal(1, rig.Handler.Count("accept"));
        Assert.True(rig.Handler.Count("add") >= 2, $"The add and the repair both send an addEmote. Seen: {seen}; repair wish: {(await LoadAsync(work.Repaired)).Status}");
        Assert.Equal(1, rig.Handler.Count("remove"));
        Assert.All(rig.Handler.Requests.Where(r => r.Kind is "add" or "remove" or "accept" or "verify"), r => Assert.Equal($"Bearer {rig.Token}", r.Authorization));
        Assert.True(((await SettingsAsync(work.Request)).BotModeEnabled), "The acceptance activated the channel.");
        Assert.Equal(WishStatus.Active, (await LoadAsync(work.Added)).Status);
        Assert.Equal((WishStatus.Presented, WishStatusReason.RepairReview), StatusOf(await LoadAsync(work.Removed)));
        // One repair by addEmote, one open phase of the second channel resolved by a read.
        Assert.Equal(2, await CountAuditsAsync(AuditActions.WishBotRepair));
        await rig.AssertNothingLeakedAsync(this, expectInformationRows: true);
    }

    // ------------------------------------------------------------------------------------ the writes with a failure

    // Every failure of a write, for the acceptance, the add, the removal and the repair at once: 401, 429, a refusal for
    // lacking privileges, an unavailable 7TV whose body echoes the token, a hang past the time limit, an exception that
    // names the token, and a cut-off answer. Two passes, the second after the pauses ran out.
    [Theory]
    [InlineData("Unauthorized")]
    [InlineData("RateLimited")]
    [InlineData("Lacking")]
    [InlineData("Unavailable")]
    [InlineData("Timeout")]
    [InlineData("Throws")]
    [InlineData("CutOff")]
    public async Task EveryWrite_WithAFailure_LeaksNothingIntoAnySink(string failure)
    {
        await using var rig = await StartBoundaryAsync();
        await SeedBoundaryWorkAsync(rig);
        rig.Handler.FailWrites(Enum.Parse<Failure>(failure));

        await rig.RunPassesAsync(this, 2, TimeSpan.FromMinutes(3));

        Assert.True(rig.Handler.Count("add") + rig.Handler.Count("remove") + rig.Handler.Count("accept") >= 1, "At least one write must have been tried.");
        Assert.All(rig.Handler.Requests.Where(r => r.Kind is "add" or "remove" or "accept"), r => Assert.Equal($"Bearer {rig.Token}", r.Authorization));
        await rig.AssertNothingLeakedAsync(this, expectInformationRows: false);
    }

    // K6b-3 and K6c-5: a 401 in pass 1 (report), the verification of pass 2 decides. A 401 there makes the token invalid;
    // a timeout there decides nothing: no write in that pass, and the verification of pass 3 lets the writes go on.
    [Theory]
    [InlineData("Unauthorized")]
    [InlineData("Timeout")]
    public async Task A401InThePass_ThenTheVerification_LeaksNothingAtAnyStep(string verification)
    {
        await using var rig = await StartBoundaryAsync();
        var work = await SeedBoundaryWorkAsync(rig);
        rig.Handler.FailWrites(Failure.Unauthorized);
        rig.Handler.VerifyScript.Enqueue(Failure.None);
        rig.Handler.VerifyScript.Enqueue(Enum.Parse<Failure>(verification));
        rig.Handler.VerifyScript.Enqueue(Failure.None);

        await rig.RunPassesAsync(this, 1);
        var writesAfterReport = rig.Handler.Writes();
        Assert.True(writesAfterReport >= 1);
        Assert.Equal(BotAccountStatus.Valid, (await CredentialAsync()).Status);

        rig.Handler.FailWrites(Failure.None);
        await rig.RunPassesAsync(this, 1);
        if (verification == "Unauthorized")
        {
            Assert.Equal(BotAccountStatus.Invalid, (await CredentialAsync()).Status);
            Assert.Equal(1, await CountAuditsAsync(AuditActions.BotAccountTokenState));
        }
        else
        {
            Assert.Equal(BotAccountStatus.Valid, (await CredentialAsync()).Status);
        }

        Assert.Equal(writesAfterReport, rig.Handler.Writes());

        await rig.RunPassesAsync(this, 1);
        Assert.Equal(verification == "Unauthorized" ? writesAfterReport : rig.Handler.Writes(), rig.Handler.Writes());
        await rig.AssertNothingLeakedAsync(this, expectInformationRows: true);
        _ = work;
    }

    // K6d-4, K6e-1, K6e-2: an acceptance that hangs past the time limit stays unresolved; DELETE gives the activation up,
    // and the pass that resolves the attempt negatively writes wish.botInvitationUnresolved, whose details are the time
    // of the attempt and nothing else. A second request bound to another bot account is given up by activate: its audit
    // names the public 7TV id of the abandoned account only.
    [Fact]
    public async Task AnUnresolvedAcceptance_AndAnAbandonedOne_WriteAuditsWithoutAnyTokenMaterial()
    {
        await using var rig = await StartBoundaryAsync();
        await SeedBotChannelAsync();
        var channel = await SeedRequestAsync();
        _editors[BotA][channel.TwitchId] = ("PENDING", true);
        rig.Handler.FailWrites(Failure.Timeout);

        await rig.RunPassesAsync(this, 1);
        Assert.Equal(1, rig.Handler.Count("accept"));
        Assert.NotNull((await SettingsAsync(channel)).BotInvitationAttemptAtUtc);

        rig.Handler.FailWrites(Failure.None);
        await DeleteBotAsync(channel);
        _clock.Advance(TimeSpan.FromSeconds(340));
        await rig.RunPassesAsync(this, 1, TimeSpan.FromSeconds(61));
        await rig.RunPassesAsync(this, 1);

        var unresolved = Assert.Single(await AuditsAsync(AuditActions.WishBotInvitationUnresolved));
        Assert.Equal(["attemptedAtUtc"], unresolved.Details.Keys);

        var other = await SeedRequestAsync(consent: BotB, configure: s => s.BotInvitationAttemptAtUtc = Now.AddMinutes(-2));
        await ActivateAsync(other);
        var abandoned = Assert.Single(await AuditsAsync(AuditActions.WishBotInvitationAbandoned));
        Assert.Equal(["abandonedBotSevenTvUserId", "attemptedAtUtc"], abandoned.Details.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(BotB, abandoned.Details["abandonedBotSevenTvUserId"]);
        await rig.AssertNothingLeakedAsync(this, expectInformationRows: true);
    }

    // ------------------------------------------------------------------------------------ the routes behind the services

    // GET and activate, deactivate, bot-remove, recheck and the permission read through the real services over the
    // credential row of a sealed token: what they answer, audit and publish holds no token material, with the answers of
    // success and of every refusal serialised as the routes would.
    [Fact]
    public async Task TheChannelRoutes_ThroughTheRealServices_LeakNothingIntoAnAnswerAnAuditOrAnEvent()
    {
        await using var rig = await StartBoundaryAsync();
        await using var db = rig.OpenDb();
        var admin = new AuditActor("900001", "admin");
        var answers = new List<string>();
        var channel = await SeedChannelAsync(botMode: false);
        var active = await SeedBotChannelAsync();
        var owned = await SeedOwnedAsync(active, WishStatus.Presented, queued: false);
        var unconfirmed = await SeedWishAsync(active, WishStatus.Unconfirmed, w =>
        {
            w.BotWriteAtUtc = Now.AddMinutes(-40);
            w.BotAddAckAtUtc = Now.AddMinutes(-40);
            w.BotSetId = SetA;
        });
        await using (var presented = CreateDb())
        {
            await presented.Wishes.Where(w => w.Id == unconfirmed).ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, WishStatus.Presented)
                .SetProperty(w => w.StatusReason, WishStatusReason.Unconfirmed));
        }

        var botRoutes = rig.BotService(this, db);
        var wishRoutes = rig.WishService(this, db);

        void Record(object? value) => answers.Add(JsonSerializer.Serialize(value, JsonSerializerOptions.Web));

        Record(await botRoutes.GetChannelAsync(channel.Name, new WishBotCaller(Activator, true, true)));
        Record(await botRoutes.GetChannelAsync(channel.Name, new WishBotCaller(Activator, false, false)));
        Record(await botRoutes.GetChannelAsync("nosuchchannel", new WishBotCaller(Activator, false, false)));
        Record(await botRoutes.ActivateAsync(channel.Name, admin));
        Record(await botRoutes.ActivateAsync(channel.Name, admin));
        Record(await botRoutes.ActivateAsync("nosuchchannel", admin));
        Record(await botRoutes.IsModeAvailableAsync(channel.Name, true));
        Record(await botRoutes.IsModeAvailableAsync(channel.Name, false));
        Record(await botRoutes.DeactivateAsync(channel.Name, admin, true));
        Record(await botRoutes.DeactivateAsync(channel.Name, admin, false));
        Record(await wishRoutes.RequestBotRemovalAsync(active.Name, owned, admin));
        Record(await wishRoutes.RequestBotRemovalAsync(active.Name, owned, admin));
        Record(await wishRoutes.RequestBotRemovalAsync(active.Name, long.MaxValue, admin));
        Record(await wishRoutes.RecheckAsync(active.Name, unconfirmed, admin));
        Record(await wishRoutes.RecheckAsync(active.Name, unconfirmed, admin));
        Record(await wishRoutes.RecheckAsync(active.Name, long.MaxValue, admin));

        rig.Collect(answers);
        await rig.AssertNothingLeakedAsync(this, expectInformationRows: false);
        Assert.NotEmpty(answers);
        Assert.True(await CountAuditsAsync(AuditActions.WishRecheck) >= 1);
    }

    // ------------------------------------------------------------------------------------ helpers (token boundary)

    private bool _boundaryRemoveTakesEveryAlias;

    private string BoundaryConnectionString => fixture.ConnectionStringFor(DatabaseName);

    private enum Failure
    {
        None,
        Unauthorized,
        RateLimited,
        Lacking,
        Unavailable,
        Timeout,
        Throws,
        CutOff,
    }

    private async Task<BoundaryRig> StartBoundaryAsync()
    {
        await using (var reset = CreateDb())
        {
            await reset.Database.ExecuteSqlRawAsync("""TRUNCATE "LogEntries", "LogCaptureStatus";""");
        }

        var console = new CapturingLoggerProvider();
        var harness = new Harness(
            fixture.ConnectionStringFor(DatabaseName),
            LogCaptureWriterTests.Fast,
            configure: builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(console));
        await harness.StartAsync();
        var rig = new BoundaryRig(this, harness, console);
        await rig.SealAndSeedAsync();
        return rig;
    }

    private sealed record BoundaryWork(Seeded Request, Seeded Channel, long Added, long Removed, long Repaired);

    // One channel waiting for an acceptance, one with an approved wish (add) and a queued removal, one with an open
    // repair phase; the editor read shows the invitation as pending and the others as accepted.
    private async Task<BoundaryWork> SeedBoundaryWorkAsync(BoundaryRig rig)
    {
        _ = rig;
        var channel = await SeedBotChannelAsync();
        var added = await SeedApprovedAsync(channel);
        var removed = await SeedOwnedAsync(channel, WishStatus.Active);

        // 7TV's removeEmote takes a second alias of the emote along: the pass repairs it with an addEmote of its own.
        Set(SetA).Add(new SevenTvSetEntry((await LoadAsync(removed)).SevenTvEmoteId, "Alias2", Now.AddDays(-20), Owner));
        _boundaryRemoveTakesEveryAlias = true;
        var repairChannel = await SeedBotChannelAsync();
        var (repaired, _) = await SeedPhaseAsync(repairChannel, WishStatus.Removed, SetA);
        var request = await SeedRequestAsync();
        _editors[BotA][request.TwitchId] = ("PENDING", true);
        return new BoundaryWork(request, channel, added, removed, repaired);
    }

    private static string AddedAt(DateTime? at) =>
        (at ?? DateTime.UnixEpoch).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + "+00:00";

    private string SetItems(string setId) =>
        "[" + string.Join(',', Set(setId).Select(e => ReadItem(e.EmoteId, e.Alias, AddedAt(e.AddedAtUtc), e.AddedById))) + "]";

    // What "7TV" answers when it works: the sets of the test are the truth, writes take effect.
    private HttpResponseMessage WorkingAnswer(WriteRequest request)
    {
        switch (request.Kind)
        {
            case "verify":
                return Json(HttpStatusCode.OK, """{"data":{"users":{"me":{"id":"@BOT@"}}}}""".Replace("@BOT@", BotA, StringComparison.Ordinal));
            case "read":
                return Json(HttpStatusCode.OK, ReadPage(SetItems(request.Variable("id")), pageCount: 1, request.Variable("id")));
            case "add":
                {
                    var set = Set(request.Variable("setId"));
                    if (!set.Any(e => e.EmoteId == request.Variable("emoteId") && e.Alias == request.Variable("alias")))
                    {
                        set.Add(new SevenTvSetEntry(request.Variable("emoteId"), request.Variable("alias"), Now, BotA));
                    }

                    return Json(HttpStatusCode.OK, AddAnswer(request.Variable("setId"), SetItems(request.Variable("setId"))));
                }

            case "remove":
                Set(request.Variable("setId")).RemoveAll(
                    e => e.EmoteId == request.Variable("emoteId") && (_boundaryRemoveTakesEveryAlias || e.Alias == request.Variable("alias")));
                return Json(HttpStatusCode.OK, """{"data":{"emoteSets":{"emoteSet":{"removeEmote":{"id":"@SET@"}}}}}""".Replace("@SET@", request.Variable("setId"), StringComparison.Ordinal));
            default:
                TakeEffect(request.Variable("editorId"), "01OWNER" + request.Variable("userId")["01OWNER".Length..]);
                return Json(HttpStatusCode.OK, """{"data":{"userEditors":{"editor":{"updateState":{"state":"ACCEPTED"}}}}}""");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record WriteRequest(string Kind, string? Authorization, string Body, IReadOnlyDictionary<string, string> Variables)
    {
        public string Variable(string name) => Variables.TryGetValue(name, out var value) ? value : string.Empty;
    }

    // The network end: tells the five calls of the write client apart by their GraphQL text, records them with their
    // Authorization header, and answers from WorkingAnswer unless a failure is armed for the writes or scripted for the
    // verification.
    private sealed class BoundaryHandler(Func<WriteRequest, HttpResponseMessage> working, string token) : HttpMessageHandler
    {
        private readonly List<WriteRequest> _requests = [];
        private readonly Lock _gate = new();
        private Failure _writes;

        public Queue<Failure> VerifyScript { get; } = new();

        public IReadOnlyList<WriteRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        public void FailWrites(Failure failure) => _writes = failure;

        public int Count(string kind) => Requests.Count(r => r.Kind == kind);

        public int Writes() => Requests.Count(r => r.Kind is "add" or "remove" or "accept");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var kind = body.Contains("AddEmote", StringComparison.Ordinal) ? "add"
                : body.Contains("RemoveSlotEntry", StringComparison.Ordinal) ? "remove"
                : body.Contains("updateState", StringComparison.Ordinal) ? "accept"
                : body.Contains("users { me", StringComparison.Ordinal) ? "verify"
                : "read";
            var variables = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var document = JsonDocument.Parse(body))
            {
                if (document.RootElement.TryGetProperty("variables", out var values))
                {
                    foreach (var property in values.EnumerateObject())
                    {
                        variables[property.Name] = property.Value.ToString();
                    }
                }
            }

            var recorded = new WriteRequest(kind, request.Headers.Authorization?.ToString(), body, variables);
            var armed = Failure.None;
            lock (_gate)
            {
                _requests.Add(recorded);
                if (kind == "verify")
                {
                    armed = VerifyScript.TryDequeue(out var scripted) ? scripted : Failure.None;
                }
                else if (kind is "add" or "remove" or "accept")
                {
                    armed = _writes;
                }
            }

            switch (armed)
            {
                case Failure.None:
                    return working(recorded);
                case Failure.Unauthorized:
                    return Json(HttpStatusCode.Unauthorized, UnauthorizedBody);
                case Failure.RateLimited:
                    return Json(HttpStatusCode.OK, ErrorsOnly(Error($"rate limited {EchoMarker}", "RATE_LIMIT_EXCEEDED", 429)));
                case Failure.Lacking:
                    return Json(HttpStatusCode.OK, ErrorsOnly(Error($"{NotEditorMessage} {EchoMarker}", "LACKING_PRIVILEGES", 403)));
                case Failure.Unavailable:
                    return Json(HttpStatusCode.ServiceUnavailable, $"upstream said Bearer {token} {EchoMarker}");
                case Failure.CutOff:
                    return Json(HttpStatusCode.OK, $$"""{"data":{"cut off {{EchoMarker}} """);
                case Failure.Throws:
                    throw new HttpRequestException($"failure while sending Bearer {token} {EchoMarker}");
                default:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("unreachable");
            }
        }
    }

    // The runner, the pass and the client wired over one logger factory, plus everything the search needs.
    private sealed class BoundaryRig : IAsyncDisposable
    {
        private readonly WishBotPassTests _owner;
        private readonly Harness _harness;
        private readonly CapturingLoggerProvider _console;
        private readonly List<string> _forbidden = ["Bearer", EchoMarker];
        private readonly List<string> _extra = [];
        private readonly List<string> _thrown = [];
        private readonly List<IAsyncDisposable> _disposables = [];
        private readonly HttpClient _httpClient;
        private BotAccountRunner? _runner;

        public BoundaryRig(WishBotPassTests owner, Harness harness, CapturingLoggerProvider console)
        {
            _owner = owner;
            _harness = harness;
            _console = console;
            var payload = B64($$"""{"sub":"wish-bot-boundary","exp":{{new DateTimeOffset(owner.Now.AddDays(30)).ToUnixTimeSeconds()}}}""");
            Token = $"{B64("""{"alg":"HS256","typ":"JWT"}""")}.{payload}.{B64("signature-" + Guid.NewGuid().ToString("N"))}";
            var parts = Token.Split('.');
            _forbidden.AddRange([Token, parts[1], parts[2]]);
            Handler = new BoundaryHandler(owner.WorkingAnswer, Token);
            _httpClient = new HttpClient(Handler)
            {
                BaseAddress = BotAccountRegistration.WriteClientBaseAddress,
                Timeout = TimeSpan.FromMilliseconds(400),
            };
            Client = new SevenTvWriteClient(_httpClient, new RecordingRateLimitTelemetry(), harness.Factory.CreateLogger<SevenTvWriteClient>());
        }

        public string Token { get; }

        public BoundaryHandler Handler { get; }

        public SevenTvWriteClient Client { get; }

        // The sealed token on the account row: the whole value and a stretch from each end are forbidden everywhere.
        public async Task SealAndSeedAsync()
        {
            using var sealer = new RsaBotAccountTokenSealer(BotAccountTokenKeys.ImportPublicKey(BotAccountTestKeys.PublicKey));
            var seal = sealer.Seal(Token);
            _forbidden.AddRange([seal, seal[..24], seal[^24..]]);
            await _owner.SeedSealedAccountAsync(seal);
        }

        public AppDbContext OpenDb()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_owner.BoundaryConnectionString)
                .UseLoggerFactory(_harness.Factory)
                .Options;
            var db = new AppDbContext(options);
            _disposables.Add(db);
            return db;
        }

        public WishBotService BotService(WishBotPassTests owner, AppDbContext db) => owner.BotService(db);

        public WishService WishService(WishBotPassTests owner, AppDbContext db)
        {
            var availability = Substitute.For<IWishBotAvailability>();
            availability.GetStateAsync(Arg.Any<CancellationToken>()).Returns(new BotAccountAvailabilityState(true, BotA, Identity));
            var reader = new WishBotChannelStateReader(availability, new WishOptions { Enabled = true, PilotChannelIds = "*" }, owner._admins, owner._clock);
            return new WishService(
                db,
                new WishChangedPublisher(owner._publisher, _harness.Factory.CreateLogger<WishChangedPublisher>()),
                owner._clock,
                _harness.Factory.CreateLogger<WishService>(),
                reader);
        }

        public void Collect(IEnumerable<string> texts) => _extra.AddRange(texts);

        // Passes of the real runner; an exception that leaves one is kept for the search and fails the test afterwards.
        public async Task RunPassesAsync(WishBotPassTests owner, int count, TimeSpan? between = null)
        {
            for (var i = 0; i < count; i++)
            {
                try
                {
                    await Runner(owner).RunPassAsync();
                }
                catch (Exception exception)
                {
                    _thrown.Add(exception.ToString());
                }

                owner._clock.Advance(between ?? TimeSpan.FromSeconds(31));
            }
        }

        public async Task AssertNothingLeakedAsync(WishBotPassTests owner, bool expectInformationRows)
        {
            await _harness.StopAsync();

            var sinks = new List<(string Name, string Text)>();
            foreach (var line in _console.Lines)
            {
                sinks.Add(($"console {line.Category} {line.Level}", line.AllText));
            }

            await using var db = owner.CreateDb();
            var rows = await db.LogEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
            foreach (var row in rows)
            {
                sinks.Add(($"stored log row {row.Id} {row.Category}", JsonSerializer.Serialize(row)));
            }

            foreach (var entry in await db.AuditLogEntries.AsNoTracking().ToListAsync())
            {
                sinks.Add(($"audit entry {entry.Id} {entry.Action}", JsonSerializer.Serialize(entry)));
            }

            foreach (var wish in await db.Wishes.AsNoTracking().ToListAsync())
            {
                sinks.Add(($"wish {wish.Id}", JsonSerializer.Serialize(wish, DumpOptions)));
            }

            foreach (var settings in await db.ChannelWishSettings.AsNoTracking().ToListAsync())
            {
                sinks.Add(($"channel wish settings {settings.ChannelId}", JsonSerializer.Serialize(settings, DumpOptions)));
            }

            // The credential row without the seal column itself, which is the one place the seal belongs.
            foreach (var credential in await db.BotAccountCredentials.AsNoTracking().ToListAsync())
            {
                credential.SealedToken = null;
                sinks.Add(("credential row", JsonSerializer.Serialize(credential, DumpOptions)));
            }

            foreach (var call in owner._publisher.ReceivedCalls())
            {
                sinks.Add(($"event {call.GetMethodInfo().Name}", string.Join('\n', call.GetArguments().Select(a => a?.ToString()))));
            }

            sinks.AddRange(_thrown.Select(text => ("thrown exception", text)));
            sinks.AddRange(_extra.Select(text => ("route answer", text)));

            // The trace-level lines of the paths must be there, or the search would pass over nothing.
            Assert.Contains(_console.Lines, line => line.Level < LogLevel.Information);
            if (expectInformationRows)
            {
                Assert.NotEmpty(rows);
            }

            foreach (var (name, text) in sinks)
            {
                foreach (var value in _forbidden)
                {
                    Assert.False(
                        text.Contains(value, StringComparison.OrdinalIgnoreCase),
                        $"{name} contains a forbidden value ({value.Length} characters, starting {value[..Math.Min(8, value.Length)]}).");
                }
            }

            Assert.Empty(_thrown);
        }

        public async ValueTask DisposeAsync()
        {
            await _harness.DisposeAsync();
            _runner?.Dispose();
            _httpClient.Dispose();
            foreach (var disposable in _disposables)
            {
                await disposable.DisposeAsync();
            }
        }

        private BotAccountRunner Runner(WishBotPassTests owner)
        {
            if (_runner is not null)
            {
                return _runner;
            }

            var db = OpenDb();
            var wishOptions = new WishOptions { Enabled = true, PilotChannelIds = "*" };
            var botOptions = new BotAccountOptions { Enabled = true, WriteDelaySeconds = 0, EditorVisibilitySeconds = owner._visibilitySeconds };
            var publisher = new WishChangedPublisher(owner._publisher, _harness.Factory.CreateLogger<WishChangedPublisher>());
            var fallback = new WishBotFallback(db, wishOptions, botOptions, owner._admins, publisher, owner._clock, _harness.Factory.CreateLogger<WishBotFallback>());
            var pass = new WishBotPass(
                db, owner.EditorCheck, Client, fallback, wishOptions, botOptions, owner._admins, publisher, owner._clock, _harness.Factory.CreateLogger<WishBotPass>());
            var opener = new RsaBotAccountTokenOpener(BotAccountTokenKeys.ImportPrivateKey(BotAccountTestKeys.PrivateKey));
            _disposables.Add(new Disposal(opener));
            _runner = new BotAccountRunner(db, opener, Client, pass, botOptions, owner._clock, _harness.Factory.CreateLogger<BotAccountRunner>());
            return _runner;
        }
    }
}
