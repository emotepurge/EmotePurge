using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

// Keeps stored channel rows in step with Twitch's own view of who they are (issue #44): the login
// is a display name an owner may rename at any time without Twitch telling anyone, so the only way
// to notice is to periodically ask Helix. Waits for BootRecoveryGate on purpose — unlike
// TwitchLivePollWorker, which only ever writes coverage rows nothing else touches, this worker can
// rename or merge the very channel rows boot recovery itself reads and writes, so the two must not
// overlap.
public class TwitchIdentityReconcileWorker(
    ILogger<TwitchIdentityReconcileWorker> logger,
    BootRecoveryGate bootRecoveryGate,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly ChannelIdentityReconcileSummary EmptySummary = new(0, 0, 0, 0, 0, 0, 0);

    // 60 minutes default (Betreiber-Antwort 2): one tick costs one Helix request per 100 channels,
    // and renames are rare enough that hourly is plenty responsive.
    private readonly TimeSpan _reconcileInterval =
        TimeSpan.FromMinutes(configuration.GetValue("Twitch:IdentityReconcileIntervalMinutes", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Without client credentials every tick would fail on the token fetch — say it once
        // instead of warning forever. The Api shares the same config values, so a correctly
        // configured deployment cannot land here.
        if (string.IsNullOrEmpty(configuration["Auth:Twitch:ClientId"]) ||
            string.IsNullOrEmpty(configuration["Auth:Twitch:ClientSecret"]))
        {
            logger.LogWarning("Twitch-Identity-Reconcile deaktiviert — Auth:Twitch:ClientId/ClientSecret nicht konfiguriert.");
            return;
        }

        // Two signals, not one. Boot recovery has to be over because this worker renames and merges
        // the very rows boot recovery reads and writes (see the class comment). And the command
        // channel has to be subscribed because a rename or merge publishes the LEAVE/JOIN handover
        // pair — Redis Pub/Sub drops a message nobody is listening for, and this worker's *first*
        // pass runs immediately, so without this wait it races the subscribe (issue #54).
        await Task.WhenAll(bootRecoveryGate.Completed, bootRecoveryGate.CommandChannelSubscribed)
            .WaitAsync(stoppingToken);

        // First run happens immediately, before the timer: this is the production backfill for
        // channel rows that predate the immutable-id migration (Entscheidung 6), not just the
        // first regular tick.
        await ReconcileOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_reconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ReconcileOnceAsync(stoppingToken);
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var identityService = scope.ServiceProvider.GetRequiredService<IChannelIdentityService>();
            var summary = await identityService.ReconcileActiveChannelsAsync(ct);

            // null means the tick was skipped (no app token, Helix unreachable) — deliberately
            // not the empty summary, and not worth a log line either: the next tick retries.
            //
            // The EmptySummary check is *not* a no-op filter, and reads like one: Checked carries
            // rows.Count, so equality holds only when there is not a single active channel. With
            // 13 tracked channels in production that means one INFO line per hour even when the
            // pass changed nothing — wanted, not tolerated: it is the only proof that this worker
            // is alive and past the boot gate, and it is what the first production run (the
            // backfill) will be read from. The suppressed case is the genuinely empty deployment,
            // where an hourly "0 geprüft" would say nothing at all.
            if (summary is null || summary == EmptySummary)
            {
                return;
            }

            // LoginsMissing sums two cases, so the wording must not name only one of them: a row
            // without an id whose login Helix does not know (case 5), and a row *with* an id that
            // resolves to nothing (case 6). Saying "Logins" alone sends the first production
            // investigation looking for an id-less row that may not exist.
            logger.LogInformation(
                "Identity-Reconcile: {Checked} geprüft, {IdsBackfilled} IDs nachgetragen, {Renamed} umbenannt, {Merged} zusammengeführt, {MergesRefused} Zusammenführungen abgelehnt, {LoginsMissing} Kanäle bei Twitch nicht mehr auffindbar (Login oder ID unbekannt), {Deactivated} wegen Sperrliste deaktiviert.",
                summary.Checked, summary.IdsBackfilled, summary.Renamed, summary.Merged, summary.MergesRefused, summary.LoginsMissing, summary.Deactivated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Regular shutdown.
        }
        catch (Exception ex)
        {
            // Runs forever on a timer — a Postgres hiccup or a Twitch outage must cost one tick,
            // never the worker host (StopHost default would take the usage flush down with it).
            logger.LogWarning(ex, "Identity-Reconcile-Durchlauf fehlgeschlagen.");
        }
    }
}
