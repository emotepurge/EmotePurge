using System.Runtime.InteropServices;
using EmotePurge.Core.Services;
using EmotePurge.Worker;
using EmotePurge.Worker.Harness;

// The very first statement of the image, before any host exists. The worker image has two entry
// points, and this is the only thing between them: no arguments means the long-running worker,
// exactly "harness <kanal> [--days <n>] [--diagnostic]" means one accuracy run, exactly
// "harness <kanal> --report-only <datei>" means a report-only recompute of an existing run instead
// (#119 — zero archive requests, see HarnessRunner.RecomputeReportAsync), and anything else is
// refused with exit code 2 instead of guessed. A lenient parser here would let a harness container
// whose verb went missing (docker compose run replaces command, not entrypoint) start the full
// worker beside the production one and double every usage row — Codex-adversarial "Fail-open CLI".
switch (HarnessCommandLine.Parse(args))
{
    case HarnessCommandLineResult.Invalid invalid:
        await Console.Error.WriteLineAsync(invalid.Message);
        return HarnessRunner.ExitInvalidArguments;

    case HarnessCommandLineResult.RunHarness harness:
        return await RunHarnessAsync(harness);

    case HarnessCommandLineResult.RunWorker:
        await RunWorkerAsync();
        return 0;

    default:
        // Unreachable today: the result hierarchy is closed and has exactly three cases. It stays a
        // refusal rather than a fall-through to the worker, because this is the one switch whose
        // failure mode is named "fail open" — a fourth case added later must stop here, not boot IRC.
        await Console.Error.WriteLineAsync(
            "Unerwartetes Ergebnis der Argumentprüfung; der Worker wird nicht gestartet.");
        return HarnessRunner.ExitInvalidArguments;
}

async Task RunWorkerAsync()
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWorkerCore(builder.Configuration);
    builder.Services.AddWorkerHostedServices();

    var host = builder.Build();
    await EnsureSchemaIsCurrentAsync(host);
    await host.RunAsync();
}

async Task<int> RunHarnessAsync(HarnessCommandLineResult.RunHarness request)
{
    // An empty argument array on purpose (Plan-Entscheidung 7): the host's command-line
    // configuration provider would otherwise turn "--days 3" into the configuration key "days",
    // where nothing reads it and everything could.
    var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
    builder.Services.AddHarness(builder.Configuration);

    var host = builder.Build();

    // Runs here too (Plan-Entscheidung 10): the guard only reads, and a harness against a stale
    // schema would otherwise fail at its first query, after the first archive requests were spent.
    await EnsureSchemaIsCurrentAsync(host);

    using var cancellation = new CancellationTokenSource();

    // docker stop sends SIGTERM, Ctrl-C sends SIGINT. Both are intercepted (Cancel = true) rather
    // than left to the runtime's default termination, so the run ends itself: it drops the day it
    // was fetching, writes the abort event and returns exit code 4 naming the resume point,
    // instead of being killed somewhere between two writes. A ProcessExit handler would be the
    // wrong tool twice over: it fires after the run has already returned, and by then this token
    // source is disposed — Cancel() on it would throw during shutdown of every successful run.
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, StopRun);
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, StopRun);

    await using var scope = host.Services.CreateAsyncScope();

    HarnessRunner runner;
    HarnessOptions options;
    try
    {
        // Resolving HarnessRunner also resolves IChannelService, which takes an IRedisPublisher
        // dependency sitting on the eager-connecting IConnectionMultiplexer singleton
        // (ServiceCollectionExtensions, no abortConnect=false) — shared DI wiring the harness cannot
        // opt out of even though it never publishes or subscribes itself. Without this try/catch, an
        // unreachable Redis at startup would throw ConnectionMultiplexer.Connect's exception straight
        // out of Main with a runtime-invented exit status instead of one of the six documented ones.
        runner = scope.ServiceProvider.GetRequiredService<HarnessRunner>();
        options = scope.ServiceProvider.GetRequiredService<HarnessOptions>();
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(
            $"Der Dienst-Graph des Harness ließ sich nicht aufbauen, vermutlich weil Redis beim Start nicht erreichbar war: {ex.Message}");
        return HarnessRunner.ExitUnexpectedError;
    }

    // A report-only recompute (#119) branches off here rather than through HarnessOptions: it takes
    // its window, its bot list and its cutovers from the frozen header of the file it reads, never
    // from Harness:WindowDays or Harness:SharedChatCutover — the same reason RecomputeReportAsync is
    // its own entry point on HarnessRunner rather than a flag threaded through RunAsync.
    if (request.ReportOnlyFile is { } reportOnlyFile)
    {
        return await runner.RecomputeReportAsync(request.ChannelName, reportOnlyFile, cancellation.Token);
    }

    // The parser cannot see the configuration (it runs before the builder), so the configured
    // default window is applied here.
    return await runner.RunAsync(
        request.ChannelName, request.Days ?? options.WindowDays, request.Diagnostic, cancellation.Token);

    void StopRun(PosixSignalContext context)
    {
        context.Cancel = true;
        cancellation.Cancel();
    }
}

// S3-34 fail-fast, same guard as the Api: without it a worker against a stale schema boots,
// reports healthy, and fails only when the first flush or sync touches the missing column.
static async Task EnsureSchemaIsCurrentAsync(IHost host)
{
    await using var migrationScope = host.Services.CreateAsyncScope();
    await migrationScope.ServiceProvider.GetRequiredService<IPendingMigrationGuard>()
        .EnsureNoPendingMigrationsAsync();
}
