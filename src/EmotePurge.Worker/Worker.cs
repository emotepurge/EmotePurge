using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Worker.SevenTv;

namespace EmotePurge.Worker;

public class Worker(
    ILogger<Worker> logger,
    ITwitchChatManager twitchChatManager,
    IRedisSubscriber redisSubscriber,
    IRedisPublisher redisPublisher,
    IEmoteMatchCache emoteMatchCache,
    IEmptySetConfirmationTracker emptySetConfirmations,
    BootRecoveryGate bootRecoveryGate,
    ISevenTvEventClient sevenTvEventClient,
    ITwitchLiveStatusReader liveStatusReader,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : BackgroundService
{
    // Worker-local debug command for the RECONNECT path (issue #68, Entscheidung 7.5, Task 6):
    // the Api never sends this. BotCommands in EmotePurge.Core stays the Api<->Worker contract
    // and is deliberately not extended for a command that only ever originates locally.
    private const string DebugTwitchReconnectCommand = "DEBUG:TWITCH-RECONNECT";

    // Read once in the constructor, same pattern as Twitch:LivePollIntervalSeconds in
    // TwitchLivePollWorker.cs:26. Default false, and never set in docker-compose.yml or
    // .env.example (Konzept 2.8) — only a deliberately configured local run can ever reach the
    // injected branch in HandleDebugTwitchReconnectAsync below.
    private readonly bool _allowTwitchReconnectTrigger =
        configuration.GetValue("Worker:Debug:AllowTwitchReconnectTrigger", false);

    /// <summary>
    /// Upper bound for the live-status read that orders boot recovery. Settable only so a test can
    /// shorten it; production keeps the default.
    /// </summary>
    public TimeSpan LiveStatusReadTimeout { get; init; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        twitchChatManager.Initialize();

        // Does not necessarily return connected: this is one attempt, bounded to ~25s, and a
        // failure leaves a reconnect signal for the rebuild loop in TwitchConnectionWatchdog rather
        // than retrying here. Boot recovery runs either way — joins record their intent and the
        // first successful rebuild rejoins them.
        await twitchChatManager.ConnectAsync(stoppingToken);

        await RunBootRecoveryAsync(stoppingToken);

        // Echtzeit-Join-/Leave-/Resync-Kommandos von der Api
        await redisSubscriber.SubscribeAsync(BotCommands.Channel, async (_, message) =>
        {
            if (message.StartsWith(BotCommands.JoinPrefix, StringComparison.Ordinal))
            {
                var channelName = message[BotCommands.JoinPrefix.Length..];
                if (!await IsInActiveRosterAsync(channelName, stoppingToken))
                {
                    return;
                }

                logger.LogInformation("Redis-Kommando: joine {Channel}.", channelName);
                await twitchChatManager.JoinChannelAsync(channelName);
                await SyncSevenTvAsync(channelName, stoppingToken, publishCompletion: true);
            }
            else if (message.StartsWith(BotCommands.LeavePrefix, StringComparison.Ordinal))
            {
                var channelName = message[BotCommands.LeavePrefix.Length..];
                // The login only at Debug (fourth Codex review of the block list): the identity
                // reconcile publishes a LEAVE for a channel it deactivates because its Twitch id is
                // on the excluded-channel list, and a line naming it next to the reconcile's own
                // anonymous line would tie the block to that channel. Every LEAVE comes from an
                // audited write (leave, purge, rename or merge handover), so the name is on record.
                logger.LogInformation("Redis command: leaving a channel.");
                logger.LogDebug("Redis command: leaving {Channel}.", channelName);
                emoteMatchCache.RemoveChannel(channelName);
                // A channel off the roster is no longer observed, so its zero-emote streak (issue
                // #76) must not survive into a later rejoin.
                emptySetConfirmations.Reset(channelName);
                sevenTvEventClient.Unsubscribe(channelName);
                await twitchChatManager.LeaveChannelAsync(channelName);
            }
            else if (message.StartsWith(BotCommands.ResyncPrefix, StringComparison.Ordinal))
            {
                // Admin-getriggerter Sofort-Resync: gleiche Schritte wie ein Tick des periodischen
                // Resyncs für genau diesen Channel (EnsureJoined als Konvergenznetz inklusive).
                var channelName = message[BotCommands.ResyncPrefix.Length..];
                if (!await IsInActiveRosterAsync(channelName, stoppingToken))
                {
                    return;
                }

                logger.LogInformation("Redis-Kommando: resynce {Channel}.", channelName);
                await twitchChatManager.EnsureJoinedAsync(channelName);
                await SyncSevenTvAsync(channelName, stoppingToken, publishCompletion: true);
            }
            else if (string.Equals(message, DebugTwitchReconnectCommand, StringComparison.Ordinal))
            {
                await HandleDebugTwitchReconnectAsync();
            }
        }, stoppingToken);

        // Only now may anything publish a command the worker has to act on. SubscribeAsync has
        // returned, which means Redis acknowledged the SUBSCRIBE and the ChannelMessageQueue is
        // buffering — a message published from here on is delivered even if OnMessage's first
        // callback has not run yet. Before this point Redis would have thrown the message away
        // without an error (issue #54).
        bootRecoveryGate.MarkCommandChannelSubscribed();

        // Ab hier passiert alle Arbeit in Event-Handlern; ExecuteAsync bleibt nur am Leben,
        // bis der Host das Shutdown-Token feuert.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    // Boot recovery (docs/Architectur.md principle 3), in two phases. Phase 1 walks the channels
    // and, per channel, warms the match cache from Postgres (no 7TV call, a few ms) and then joins:
    // TwitchChatManager drops messages of a channel whose cache is empty, so the warm-up has to
    // happen right before the join for the channel to count from its first message (#116). Phase 2
    // runs the 7TV syncs. A join costs ~600 ms of throttle, a sync a full 7TV round trip plus a
    // Postgres write; interleaved, the last channel's chat gap grew with N x (throttle + sync)
    // instead of N x throttle. A never-synced fresh row has nothing to warm from and counts nothing
    // until its first sync, so phase 2 syncs the still-cold channels first.
    // The syncs stay serial, one channel at a time: that respects ChannelSyncGate and the shared 7TV
    // quota, and BootRecoveryGate keeps the periodic resync out until both phases are done.
    private async Task RunBootRecoveryAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var channelService = scope.ServiceProvider.GetRequiredService<IChannelService>();
            var activeChannels = await channelService.ListActiveChannelNamesAsync(stoppingToken);
            var channels = BootRecoveryOrderPolicy.LiveFirst(
                activeChannels, await ReadLiveStatusOrNullAsync(stoppingToken));

            foreach (var channelName in channels)
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    await WarmMatchCacheAsync(channelName, stoppingToken);
                    logger.LogInformation("Boot recovery: joining {Channel}.", channelName);
                    await twitchChatManager.JoinChannelAsync(channelName);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // One failing join must not keep the channels behind it from being joined.
                    logger.LogWarning(ex, "Boot recovery join for {Channel} failed.", channelName);
                }
            }

            // Phase 2 starts with the channels that still count nothing (never-synced rows, a warm-up
            // that found nothing or failed): only their sync makes them count, so they must not wait
            // behind channels that are already counting from the warm start.
            var cold = channels.Where(name => emoteMatchCache.GetChannelSnapshot(name).NameToEmoteId.Count == 0).ToHashSet(StringComparer.Ordinal);
            foreach (var channelName in BootRecoveryOrderPolicy.ColdFirst(channels, cold))
            {
                stoppingToken.ThrowIfCancellationRequested();
                try
                {
                    await SyncSevenTvAsync(channelName, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Unlike JoinChannelAsync, SyncChannelAsync can throw (JsonException from 7TV,
                    // DbUpdateException on the (ChannelId, SevenTvEmoteId) unique index). Escaping
                    // ExecuteAsync would stop the whole host (BackgroundServiceExceptionBehavior
                    // defaults to StopHost), and since boot recovery runs in the same order every
                    // time, the restart would hit the same channel again — a crash loop in which
                    // every channel behind the failing one is never synced at all.
                    logger.LogWarning(ex, "Boot recovery sync for {Channel} failed.", channelName);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during boot recovery: stop quietly instead of one warning per remaining channel.
            logger.LogInformation("Boot recovery stopped by shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Boot recovery failed — channels are only picked up by the periodic resync.");
        }
        finally
        {
            // Releases the periodic resync worker even if boot recovery failed, so a broken boot
            // never turns into a permanently blocked convergence path. It stays after the sync
            // phase on purpose: the gate exists to keep the periodic resync from syncing the same
            // channels concurrently with boot recovery's own syncs.
            bootRecoveryGate.MarkCompleted();
        }
    }

    // A failed warm-up (database error) only costs the head start: the channel is still joined and
    // its sync warms the cache again.
    private async Task WarmMatchCacheAsync(string channelName, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
            await syncService.WarmChannelAsync(channelName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Boot recovery warm-up for {Channel} failed — joining anyway.", channelName);
        }
    }

    // The live snapshot only orders the joins, so it must never be able to fail or delay the boot:
    // null (absent, expired, Redis down), any read error and a read slower than the bound all mean
    // "keep the roster order".
    private async Task<TwitchLiveStatusSnapshot?> ReadLiveStatusOrNullAsync(CancellationToken ct)
    {
        try
        {
            return await liveStatusReader.ReadAsync(ct).WaitAsync(LiveStatusReadTimeout, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Boot recovery could not read the live status in time — joining in roster order.");
            return null;
        }
    }

    /// <summary>
    /// The guard in front of the two commands that make this process enter a channel (JOIN, RESYNC):
    /// only a channel on the same roster source boot recovery and the periodic resync use —
    /// <see cref="IChannelService.ListActiveChannelNamesAsync"/>, which leaves out a row whose stored
    /// Twitch id is on the excluded-channel list — is joined. Without it an Api still running with
    /// an older <c>EXCLUDED_CHANNEL_IDS</c>, or an admin RESYNC of a row the identity reconcile has
    /// not deactivated yet, would make this worker observe a channel its own configuration blocks,
    /// until the roster prune parted it again two resync ticks later (fourth Codex review of the
    /// block list). Reading the whole list rather than one row keeps a single definition of "this
    /// worker should be in that channel"; it is capped well below a hundred names and JOIN/RESYNC
    /// commands are rare, user-triggered events.
    /// <para>
    /// A refusal names nothing: the channel may be refused precisely because of an objection, and a
    /// log line naming it would leak that. Fails closed on a database error for the same reason the
    /// periodic resync can afford to — its <c>EnsureJoinedAsync</c> is the convergence net that joins
    /// a legitimately active channel on the next tick.
    /// </para>
    /// </summary>
    private async Task<bool> IsInActiveRosterAsync(string channelName, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var channelService = scope.ServiceProvider.GetRequiredService<IChannelService>();
            var activeChannels = await channelService.ListActiveChannelNamesAsync(ct);
            var normalized = ChannelName.Normalize(channelName);
            if (activeChannels.Contains(normalized, StringComparer.Ordinal))
            {
                return true;
            }

            logger.LogInformation(
                "Redis command ignored: the channel is not on the active roster (inactive, gone, or on the excluded-channel list).");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Redis command ignored: the active roster could not be read — the periodic resync joins the channel on its next tick if it is still active.");
            return false;
        }
    }

    /// <param name="publishCompletion">
    /// Announces the finished sync as a live event even when it changed nothing. Only set for the
    /// two user-triggered paths (a JOIN and an admin RESYNC), where somebody is waiting in front of
    /// a screen and the event doubles as "done" feedback. Unattended paths (boot recovery here, the
    /// periodic resync, the EventAPI follow-ups) leave it off and publish only on a real change:
    /// a per-minute no-op resync must not make every open page refetch on a timer.
    /// </param>
    private async Task SyncSevenTvAsync(string channelName, CancellationToken ct, bool publishCompletion = false)
    {
        using var scope = scopeFactory.CreateScope();
        var syncService = scope.ServiceProvider.GetRequiredService<ISevenTvSyncService>();
        var result = await syncService.SyncChannelAsync(channelName, ct);
        if (result is not null)
        {
            // result.ChannelName everywhere below, not the name this method was called with: the
            // sync re-reads its row under the row gate, so a rename committed while this call was
            // queued means the caller's name is already retired (issue #60).
            logger.LogInformation("7TV-Set {SetId} für {Channel} synchronisiert.", result.EmoteSetId, result.ChannelName);

            // Desired-state first: safe even before the EventAPI session exists; the client
            // converges the socket towards the registry after every Hello.
            sevenTvEventClient.EnsureSubscribed(result.ChannelName, result.EmoteSetId, result.SevenTvUserId);

            if (publishCompletion || result.HasChanges)
            {
                await redisPublisher.PublishChannelSyncedAsync(logger, result.ChannelName, ct);
            }
        }
    }

    // Debug-only trigger for the RECONNECT path (Entscheidung 7.5, Task 6). Fall B (Twitch sends
    // RECONNECT) is otherwise not reproducible locally at all. This gate is the only place that
    // decides whether the trigger fires — TwitchChatManager.SimulateServerReconnectAsync checks
    // nothing itself. The "ignoriert" line below is the positive evidence that the gate holds,
    // not merely the absence of an effect (G4).
    private async Task HandleDebugTwitchReconnectAsync()
    {
        if (!_allowTwitchReconnectTrigger)
        {
            logger.LogWarning("Debug-Auslöser ignoriert — Worker:Debug:AllowTwitchReconnectTrigger ist nicht gesetzt.");
            return;
        }

        logger.LogWarning("Debug-Auslöser: Twitch-RECONNECT wird injiziert.");

        // Blocks this command handler for ≈2s (TwitchLib's own ClosePrivate wait of 0.4s plus its
        // internal 1.5s delay inside ReconnectAsync) — acceptable for a debug-only path fired by
        // hand, and named here rather than hidden behind an unawaited Task.Run.
        await twitchChatManager.SimulateServerReconnectAsync();
    }
}
