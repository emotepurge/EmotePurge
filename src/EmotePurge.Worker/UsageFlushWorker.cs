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
    // A retry follows only an attempt that put counts back (a thrown phase 1, or keys phase 2 handed
    // back). Budget: the host's shutdown timeout is not set in this repo, so the .NET default of 30 s
    // applies (Docker holds 60 s, see docker-compose.prod.yml). Nothing in an attempt has a timeout of
    // its own that adds up to that bound — the 5-s lock_timeout of phase 2 caps each single lock wait,
    // phase 1 and the reads have none. What bounds the final flush is the shutdown token: it is passed
    // to every statement and cancels the one in flight when the timeout expires, and once it has fired
    // no further attempt starts. The lock_timeout is therefore left as it is.
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

    /// <summary>
    /// One flush cycle. Returns how many counter keys it put back into the counter — the whole batch
    /// after a throw, the handed-back keys after a partial result (plan #373, E9) — and 0 when nothing
    /// went back: the counter was empty, everything was written, or the streak bound dropped the rest.
    /// Only the final flush reads it (E9a).
    /// </summary>
    internal async Task<int> FlushOnceAsync(CancellationToken ct)
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
            return 0;
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
                return counts.Count;
            }

            logger.LogError(
                ex,
                "Usage flush has failed for {Attempt} runs in a row, {Count} counts dropped.",
                consecutiveFailures, counts.Count);
            return 0;
        }

        var putBack = RequeueHandedBack(result.Requeued);

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

        return putBack;
    }

    // Plan #373, E9a. The first attempt always runs; up to two more follow, after a short random pause,
    // only while the previous attempt put counts back (a thrown phase 1 or handed-back keys) and the
    // shutdown token has not fired. What is still in the counter after attempts that kept failing is
    // dropped with an error line.
    //
    // The counter does not stand still meanwhile: hosted services stop in reverse registration order,
    // so Worker — which owns the Twitch client — stops only after this service and keeps counting
    // during the final flush. Counts in the counter after a clean attempt are therefore fresh, not
    // failed ones; they get one more plain flush without the retry loop, and whatever is left after it
    // is dropped with an information line — a residue of shutting down, not a failure — or with a
    // warning if that last flush itself put counts back.
    private async Task FinalFlushAsync(CancellationToken ct)
    {
        if (!await FlushWithRetriesAsync(ct))
        {
            var (keys, uses) = DrainResidue();
            if (keys > 0)
            {
                logger.LogError(
                    "Final usage flush gave up: {Keys} counter keys with {Uses} counted uses dropped.",
                    keys, uses);
            }

            return;
        }

        var putBack = ct.IsCancellationRequested ? 0 : await FlushOnceAsync(ct);
        var (freshKeys, freshUses) = DrainResidue();
        if (freshKeys == 0)
        {
            return;
        }

        if (putBack > 0)
        {
            // FlushOnceAsync has logged why; no retry for counts that came in after a clean attempt.
            logger.LogWarning(
                "Final usage flush of counts that arrived during shutdown did not go through: {Keys} counter keys with {Uses} counted uses dropped.",
                freshKeys, freshUses);
        }
        else
        {
            logger.LogInformation(
                "Final usage flush left {Keys} counter keys with {Uses} counted uses that arrived during shutdown; dropped.",
                freshKeys, freshUses);
        }
    }

    // True once an attempt put nothing back; false when the attempts ran out or the token fired first.
    private async Task<bool> FlushWithRetriesAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await FlushOnceAsync(ct) == 0)
            {
                return true;
            }

            if (attempt >= FinalFlushMaxAttempts || ct.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(RandomPause(), ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private (int Keys, long Uses) DrainResidue()
    {
        var left = usageCounter.DrainAndReset();
        return (left.Count, left.Values.Sum(c => (long)c.Human + c.Bot + c.SharedChat));
    }

    private TimeSpan RandomPause() =>
        FinalFlushMaxPause <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * FinalFlushMaxPause.TotalMilliseconds * 0.8
                + FinalFlushMaxPause.TotalMilliseconds * 0.2);

    // The keys a flush wrote nothing for and handed back (they are pending keys and go back as such,
    // to be resolved afresh). Dropped keys — of deleted slots or emotes — never come back here.
    // Returns how many keys went back into the counter.
    private int RequeueHandedBack(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> requeued)
    {
        if (requeued.Count == 0)
        {
            _consecutivePartialFlushes = 0;
            return 0;
        }

        _consecutivePartialFlushes++;
        if (_consecutivePartialFlushes <= MaxConsecutiveFailuresToRequeue)
        {
            usageCounter.Merge(requeued);
            logger.LogWarning(
                "Usage flush handed back {Count} counts without a slot yet ({Attempt}. time in a row); they are put back for the next run.",
                requeued.Count, _consecutivePartialFlushes);
            return requeued.Count;
        }

        logger.LogError(
            "Usage flush has handed back counts without a slot yet for {Attempt} runs in a row, {Count} counts dropped.",
            _consecutivePartialFlushes, requeued.Count);
        return 0;
    }
}
