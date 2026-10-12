using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker;

public class UsageFlushWorker(
    ILogger<UsageFlushWorker> logger,
    IEmoteUsageCounter usageCounter,
    WorkerStats stats,
    IRedisPublisher redisPublisher,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    // How often in a row a failed batch is put back before it is dropped. The bound is not about
    // memory (the counter is bounded by the number of distinct emotes) but about attribution:
    // UsageStat.Date is the day the flush *succeeds*, so counts carried across a long outage would
    // eventually be booked on the wrong calendar day. Five attempts ≈ 2.5 minutes of tolerance.
    private const int MaxConsecutiveFailuresToRequeue = 5;

    // Plan #373, E9a: the final flush gets this many attempts in total (the first plus two retries).
    // Budget: the host's shutdown timeout is not set in this repo, so the .NET default of 30 s applies
    // (Docker holds 60 s, see docker-compose.prod.yml). Each attempt is bounded by the phase-2
    // lock_timeout of 5 s plus a few quick queries, so three attempts and two short pauses stay well
    // below 30 s and the lock_timeout is deliberately left alone. The token still decides: once it is
    // cancelled no further attempt starts.
    private const int FinalFlushMaxAttempts = 3;

    // Plan #373, E9: how many flushes in a row handed keys back without throwing (flush phase 2, the
    // pending keys of the flag-off map). Its own streak, so the overall failure streak and the admin
    // health keep meaning "the flush itself fails"; same bound and same reason (the day a count is
    // booked on). Only the flush loop and the final flush touch it, and those never run concurrently
    // (see StopAsync).
    private int _consecutivePartialFlushes;

    // Upper bound of the random pause between two final-flush attempts (the lower bound is a fifth of
    // it). Settable for tests only.
    internal TimeSpan FinalFlushMaxPause { get; set; } = TimeSpan.FromMilliseconds(500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await FlushOnceAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // base.StopAsync first: it cancels the stopping token and thereby ends the PeriodicTimer
        // loop. Flushing before that left the loop armed, so a due 30s tick could run concurrently
        // with the final flush — two writers on the same (EmoteId, Date) rows.
        await base.StopAsync(cancellationToken);

        // Final flush so the last <30s of buffered counts aren't lost on a normal shutdown.
        await FinalFlushAsync(cancellationToken);
    }

    // Plan #373, E9a: the first attempt always runs; while counts are left in the counter (a thrown
    // phase 1 or handed-back keys put them back) up to two more follow after a short random pause, as
    // long as the shutdown token has not fired. What remains afterwards is dropped with an error line.
    private async Task FinalFlushAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await FlushOnceAsync(ct);
            if (usageCounter.PendingEmoteCount == 0)
            {
                return;
            }

            if (attempt >= FinalFlushMaxAttempts || ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(RandomPause(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        var left = usageCounter.DrainAndReset();
        if (left.Count > 0)
        {
            logger.LogError(
                "Final usage flush gave up: {Keys} counter keys with {Uses} counted uses dropped.",
                left.Count, left.Values.Sum(c => (long)c.Human + c.Bot + c.SharedChat));
        }
    }

    private TimeSpan RandomPause() =>
        FinalFlushMaxPause <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * FinalFlushMaxPause.TotalMilliseconds * 0.8
                + FinalFlushMaxPause.TotalMilliseconds * 0.2);

    internal async Task FlushOnceAsync(CancellationToken ct)
    {
        // Drained first and unconditionally — before the early return below and outside the try/catch
        // around the flush — because this is the only production signal for messages whose room
        // origin could not be determined (#73), and it is exactly on a quiet window (no emote matches
        // this cycle) or a failed flush that noticing is cheapest. Not per-message logging
        // (WorkerStats.RecordIndeterminateSharedChatMessage stays silent) — once per call here, and
        // only when there is something to say.
        var indeterminate = stats.TakeIndeterminateSharedChatMessagesSinceLastFlush();
        if (indeterminate > 0)
        {
            logger.LogInformation(
                "{Count} Chat-Nachrichten seit dem letzten Durchlauf hatten keinen bestimmbaren Ursprungsraum und wurden als fremd (Shared Chat) gezählt.",
                indeterminate);
        }

        // Same unconditional drain as above, same reasoning — this is the only production signal
        // for the TwitchLib double-read-loop defect (#114). Warning rather than Information: unlike
        // the indeterminate count, a nonzero value here means chat messages could be corrupted.
        var splicedIrcLines = stats.TakeSplicedIrcLinesSinceLastFlush();
        if (splicedIrcLines > 0)
        {
            logger.LogWarning(
                "{Count} gespleißte IRC-Zeilen seit dem letzten Durchlauf erkannt (#114) — nicht verworfen, nur markiert.",
                splicedIrcLines);
        }

        var counts = usageCounter.DrainAndReset();
        if (counts.Count == 0)
        {
            return;
        }

        UsageFlushResult result;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var flushService = scope.ServiceProvider.GetRequiredService<IUsageStatFlushService>();
            result = await flushService.FlushAsync(counts, ct);
            // Bookkeeping moved to the shared WorkerStats so GET /api/admin/health can report it. A
            // result with handed-back keys is still a success for what it wrote (plan #373, E9).
            stats.RecordFlushSuccess(counts.Count - result.Requeued.Count, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // A throw means nothing was written (flush phase 1 failed), so the whole batch goes back.
            // An unhandled exception here would (StopHost default) kill the whole Worker process, not
            // just this flush cycle — so it never propagates.
            var consecutiveFailures = stats.RecordFlushFailure();
            if (consecutiveFailures <= MaxConsecutiveFailuresToRequeue)
            {
                usageCounter.Merge(counts);
                logger.LogWarning(
                    ex,
                    "Usage flush failed ({Attempt}. attempt in a row), {Count} counts put back for the next run.",
                    consecutiveFailures, counts.Count);
            }
            else
            {
                logger.LogError(
                    ex,
                    "Usage flush has failed for {Attempt} runs in a row, {Count} counts dropped.",
                    consecutiveFailures, counts.Count);
            }

            return;
        }

        RequeueHandedBack(result.Requeued);

        // Deliberately its own try/catch, and deliberately *after* RecordFlushSuccess: at this point
        // the flush is committed. If a Redis outage were allowed to fall into the catch above, a
        // successful flush would be booked as a failure and its counts requeued — the next flush
        // would then add them a second time onto the same (EmoteId, Date) row (ON CONFLICT DO UPDATE
        // ... + EXCLUDED, now for all three columns — UseCount, BotUseCount and SharedChatUseCount),
        // i.e. silently double-count chat usage. A missed notification only costs the browser its
        // automatic refresh; every page still has its refresh button.
        try
        {
            foreach (var channelName in result.WrittenChannels)
            {
                await redisPublisher.PublishAsync(
                    LiveEvents.Channel,
                    new LiveEvent(LiveEvents.UsageFlushed, channelName).Serialize(),
                    ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live-Event '{Type}' konnte nicht veröffentlicht werden.", LiveEvents.UsageFlushed);
        }
    }

    // The keys a flush wrote nothing for and handed back (they are pending keys and go back as such,
    // to be resolved afresh). Dropped keys — of deleted slots or emotes — never come back here.
    private void RequeueHandedBack(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> requeued)
    {
        if (requeued.Count == 0)
        {
            _consecutivePartialFlushes = 0;
            return;
        }

        _consecutivePartialFlushes++;
        if (_consecutivePartialFlushes <= MaxConsecutiveFailuresToRequeue)
        {
            usageCounter.Merge(requeued);
            logger.LogWarning(
                "Usage flush handed back {Count} counts without a slot yet ({Attempt}. time in a row); they are put back for the next run.",
                requeued.Count, _consecutivePartialFlushes);
        }
        else
        {
            logger.LogError(
                "Usage flush has handed back counts without a slot yet for {Attempt} runs in a row, {Count} counts dropped.",
                _consecutivePartialFlushes, requeued.Count);
        }
    }
}
