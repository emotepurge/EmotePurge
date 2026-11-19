using EmotePurge.Core.Twitch;
using EmotePurge.Worker.WishCapture;

namespace EmotePurge.Worker;

/// <summary>
/// Two jobs, one sequential loop (issue #68).
/// <para>
/// <b>The net against silent connections.</b> Some losses produce no TwitchLib event at all: the
/// socket stays <c>Open</c>, the receive call simply never returns (live observed 2026-07-24/25,
/// ~6 minutes of standstill without a single event). Every 60 s of waiting, the loop therefore asks
/// <see cref="TwitchWatchdogPolicy"/> whether the connection looks stale — measured in received IRC
/// frames including Twitch's ~5-minute server PING since 2026-08-03, not in chat messages.
/// </para>
/// <para>
/// <b>The rebuild loop.</b> Since the client is built with <c>NoReconnectionPolicy</c>, TwitchLib no
/// longer reconnects on its own; this service does. It waits for a reconnect signal, paces the
/// attempts with <see cref="TwitchReconnectBackoffPolicy"/>, replaces the client object per attempt,
/// and rejoins the desired channels afterwards — outside TwitchLib's read loop (design decision E2).
/// </para>
/// <para>
/// <b>Why here and not in the manager or a third service.</b> The manager is not a hosted service
/// and never sees the stopping token, and it must stay pure transport. A second hosted service next
/// to this one would mean two independent triggers driving the same manager into a rebuild — the
/// duplication this design removes. The watchdog already ticks and already asked the manager to
/// reconnect, so the loop lands where the decision always was.
/// </para>
/// <para>
/// <b>Why sequential.</b> The wait for a signal and the 60 s tick are one wait with a deadline, not
/// two concurrent timers: while a rebuild runs, no tick happens, so the tick can never observe an
/// in-flight rebuild. That is also why <see cref="TwitchWatchdogPolicy"/> has no in-flight input.
/// </para>
/// <para>
/// <b>Shutdown promise.</b> Every wait in the loop and in the manager's rebuild path takes the
/// stopping token — the signal, the backoff delay, the connect, the handshake, the join gate, the
/// 600 ms pacing and the confirmation wait — so <c>StopAsync</c> returns within about a second in
/// any state. That matters because hosted services stop sequentially in reverse registration order:
/// this one stops before <see cref="UsageFlushWorker"/>, and every second spent here is a second
/// missing from the final flush before Docker's stop grace period expires (#122).
/// </para>
/// <para>
/// <b>Capture gaps (K6, plan #486 2.7).</b> Two calls on <see cref="ICaptureGapRecorder"/>, both synchronous and in
/// memory only: the loss before the first rebuild attempt, with the signal's time, and the end of the rejoin round.
/// Neither feeds anything back into the rebuild, the backoff or the signals.
/// </para>
/// </summary>
public class TwitchConnectionWatchdog(
    ILogger<TwitchConnectionWatchdog> logger,
    ITwitchChatManager twitchChatManager,
    ITwitchAppTokenProvider appTokenProvider,
    IServiceScopeFactory scopeFactory,
    WorkerStats stats,
    ICaptureGapRecorder captureGaps) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly TwitchReconnectBackoffPolicy _backoff = new();

    private DateTime? _lastForcedReconnectUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Fault-isolated per iteration (the SevenTvEventWorker pattern): a failed rebuild round
            // must never take the worker host down with it.
            try
            {
                var request = await WaitForSignalOrTickAsync(stoppingToken);
                if (request is not null)
                {
                    await RebuildAsync(request, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Twitch-Wiederaufbau-Durchlauf fehlgeschlagen.");
            }
        }
    }

    /// <summary>
    /// One wait with a deadline: a signal within 60 s is a loss to act on, the deadline itself is
    /// the tick. Returns <c>null</c> when it was a tick — which may have deposited a signal of its
    /// own, and the next pass then picks it up immediately.
    /// </summary>
    private async Task<TwitchReconnectRequest?> WaitForSignalOrTickAsync(CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(CheckInterval);

        try
        {
            return await twitchChatManager.WaitForReconnectRequestAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);
            return null;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var decision = TwitchWatchdogPolicy.Decide(
            twitchChatManager.IsConnected,
            Elapsed(now, twitchChatManager.ConnectAttemptedUtc),
            Elapsed(now, twitchChatManager.LastFrameReceivedUtc),
            Elapsed(now, _lastForcedReconnectUtc));

        if (!decision.ForceReconnect)
        {
            return;
        }

        logger.LogWarning("Erzwinge Reconnect: {Reason}", decision.Reason);

        // Only before a tick-triggered signal: the Helix context explains silence and is worth a
        // round-trip when the evidence is an absence. The event-driven path must not wait on Helix
        // before rebuilding — there the loss is already proven.
        await LogLiveContextAsync(ct);
        _lastForcedReconnectUtc = now;

        var reason = twitchChatManager.IsConnected
            ? TwitchSessionEndReason.FrameStale
            : TwitchSessionEndReason.DisconnectedBackstop;
        twitchChatManager.RequestReconnect(reason, decision.Reason ?? string.Empty);
    }

    private async Task RebuildAsync(TwitchReconnectRequest request, CancellationToken ct)
    {
        stats.RecordTwitchRebuild();

        var lostAt = request.RequestedUtc;
        captureGaps.ConnectionLost(lostAt);
        var delay = _backoff.NextDelay(new TwitchSessionResult(request.Reason, request.SessionDuration));
        logger.LogWarning(
            "Twitch-Verbindung verloren ({Reason}: {Detail}, Sitzung {SessionSeconds}s) — Wiederaufbau #1 in {Delay}s, Client #{Generation}.",
            request.Reason,
            request.Detail,
            Seconds(request.SessionDuration),
            Seconds(delay),
            request.ClientGeneration);

        var attempt = 1;
        TwitchConnectOutcome outcome;
        while (true)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            outcome = await twitchChatManager.ReconnectOnceAsync(ct);
            if (outcome.HandshakeCompleted)
            {
                break;
            }

            stats.RecordTwitchRebuildAttemptFailure();
            delay = _backoff.NextDelay(new TwitchSessionResult(outcome.FailureReason!.Value, null));
            logger.LogWarning(
                "Wiederaufbau #{Attempt} fehlgeschlagen ({Reason} nach {Elapsed}s) — nächster Versuch in {Delay}s.",
                attempt, outcome.FailureReason, Seconds(outcome.Elapsed), Seconds(delay));
            attempt++;
        }

        // SLO-1: from the loss to the handshake of the new client.
        logger.LogInformation(
            "Twitch-Verbindung steht nach {Seconds}s (Versuch #{Attempt}, Client #{Generation}).",
            Seconds(DateTime.UtcNow - lostAt), attempt, outcome.ClientGeneration);

        var rejoin = await twitchChatManager.RejoinDesiredChannelsAsync(ct);
        captureGaps.RejoinRoundCompleted();

        // SLO-2: from the loss to the last join confirmation. Warning as soon as a desired channel
        // is left unconfirmed or the round was cut short — those are the two ways this line could
        // otherwise certify a connection that counts nothing.
        var level = rejoin.Open > 0 || rejoin.Aborted ? LogLevel.Warning : LogLevel.Information;
        logger.Log(
            level,
            "Rejoin abgeschlossen (Client #{Generation}): {Desired} gewünscht, {Confirmed} bestätigt, {Open} offen, {Seconds}s seit Verlust{Aborted}; seit Prozessstart: {Rebuilds} Wiederaufbauten, {Failures} Fehlversuche.",
            outcome.ClientGeneration,
            rejoin.Desired,
            rejoin.Confirmed,
            rejoin.Open,
            Seconds(DateTime.UtcNow - lostAt),
            rejoin.Aborted ? " (abgebrochen)" : string.Empty,
            stats.TwitchRebuildCount,
            stats.TwitchRebuildAttemptFailureCount);
    }

    // Diagnostic context only, never an input to the decision: knowing that every joined channel is
    // offline *explains* silence, it does not *prove* the connection is alive — the frame timestamp
    // does that. Best effort on the A10 app token; a missing token or a failed Helix call costs
    // nothing but this one log line.
    private async Task LogLiveContextAsync(CancellationToken ct)
    {
        try
        {
            var channels = twitchChatManager.GetRoster().Select(entry => entry.ChannelName).ToList();
            if (channels.Count == 0)
            {
                return;
            }

            var accessToken = await appTokenProvider.GetTokenAsync(ct);
            if (accessToken is null)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var helixClient = scope.ServiceProvider.GetRequiredService<ITwitchHelixClient>();
            var streams = await helixClient.GetLiveStreamsByLoginsAsync(channels, accessToken, ct);
            if (streams is null)
            {
                return;
            }

            logger.LogInformation(
                "Kontext zum erzwungenen Reconnect: {LiveCount} von {TotalCount} gejointen Channels laut Helix live.",
                streams.Count, channels.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Live-Kontext-Abfrage vor Reconnect fehlgeschlagen (ignoriert).");
        }
    }

    private static TimeSpan? Elapsed(DateTime now, DateTime? since) =>
        since is { } utc ? now - utc : null;

    private static double Seconds(TimeSpan elapsed) => Math.Round(elapsed.TotalSeconds, 1);

    private static double? Seconds(TimeSpan? elapsed) => elapsed is { } value ? Seconds(value) : null;
}
