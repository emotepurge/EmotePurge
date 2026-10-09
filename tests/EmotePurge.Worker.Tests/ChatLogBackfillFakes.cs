using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Worker.ChatLogBackfill;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace EmotePurge.Worker.Tests;

/// <summary>
/// The database side of the backfill as an in-memory model, shared by every
/// <see cref="FakeBackfillService"/> instance the worker resolves — so a test can tell the loop's
/// instance from the status monitor's and still see one consistent queue. The transitions follow the
/// service's contract (spec section 3): conditional on <c>running</c>, the pause delay computed from
/// the persisted <c>PauseCount</c>, the provider cooldown never shortened, a strict FIFO head.
/// </summary>
internal sealed class FakeBackfillStore(ChatLogBackfillOptions options)
{
    private readonly Lock _gate = new();
    private readonly List<FakeRun> _runs = [];
    private readonly List<FakeBackfillService> _instances = [];
    private object? _lockHolder;

    public DateTime? CooldownUntilUtc { get; set; }

    /// <summary>Overrides the block commit; null = the contract's own behaviour.</summary>
    public Func<FakeRun, ChatLogBackfillBlockResult?>? ReplaceOverride { get; set; }

    /// <summary>Throws from the next claims while it returns an exception.</summary>
    public Func<Exception?>? ClaimFault { get; set; }

    public IReadOnlyList<FakeBackfillService> Instances
    {
        get
        {
            lock (_gate)
            {
                return [.. _instances];
            }
        }
    }

    public FakeRun AddRun(DateOnly windowFrom, DateOnly windowTo, string channelName = "zokka", IReadOnlyList<ChatLogBackfillSnapshotEmote>? snapshot = null)
    {
        lock (_gate)
        {
            var run = new FakeRun
            {
                Id = _runs.Count + 1,
                ChannelName = channelName,
                WindowFrom = windowFrom,
                WindowTo = windowTo,
                WeeksTotal = ChatLogBackfillWindow.WeeksFor(windowTo.DayNumber - windowFrom.DayNumber),
                Snapshot = snapshot ?? [new ChatLogBackfillSnapshotEmote("e1", "Kappa", null), new ChatLogBackfillSnapshotEmote("e2", "PogU", null)],
            };
            _runs.Add(run);
            return run;
        }
    }

    /// <summary>Another process holds the loop lock (or nobody, with <c>false</c>).</summary>
    public void SetLockHeldElsewhere(bool held)
    {
        lock (_gate)
        {
            _lockHolder = held ? new object() : null;
        }
    }

    public void SetStatus(FakeRun run, ChatLogBackfillRunStatus status, string? errorCode = null)
    {
        lock (_gate)
        {
            run.Status = status;
            run.ErrorCode = errorCode ?? run.ErrorCode;
        }
    }

    public FakeBackfillService NewInstance()
    {
        lock (_gate)
        {
            var instance = new FakeBackfillService(this);
            _instances.Add(instance);
            return instance;
        }
    }

    internal T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }

    internal void ReleaseLock(object holder)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_lockHolder, holder))
            {
                _lockHolder = null;
            }
        }
    }

    internal bool TryLock(object holder)
    {
        lock (_gate)
        {
            if (_lockHolder is null || ReferenceEquals(_lockHolder, holder))
            {
                _lockHolder = holder;
                return true;
            }

            return false;
        }
    }

    internal int Reset() => Locked(() =>
    {
        var running = _runs.Where(r => r.Status == ChatLogBackfillRunStatus.Running).ToList();
        running.ForEach(r => r.Status = ChatLogBackfillRunStatus.Queued);
        return running.Count;
    });

    internal ChatLogBackfillClaim? Claim(DateTime nowUtc)
    {
        if (ClaimFault?.Invoke() is { } fault)
        {
            throw fault;
        }

        return Locked(() =>
        {
            var head = _runs
                .Where(r => r.Status is ChatLogBackfillRunStatus.Queued or ChatLogBackfillRunStatus.Running or ChatLogBackfillRunStatus.Paused)
                .OrderBy(r => r.Id)
                .FirstOrDefault();
            if (head is null || (head.Status == ChatLogBackfillRunStatus.Paused && head.PausedUntilUtc > nowUtc))
            {
                return null;
            }

            head.Status = ChatLogBackfillRunStatus.Running;
            head.PausedUntilUtc = null;
            head.Claims++;
            return new ChatLogBackfillClaim(
                head.Id, "channel-" + head.ChannelName, head.ChannelName, "tw-" + head.ChannelName, head.WindowFrom, head.WindowTo,
                head.WeeksDone, head.WeeksTotal, head.PauseCount, head.BlockAttempts, "set-1");
        });
    }

    internal FakeRun? Find(long runId) => Locked(() => _runs.SingleOrDefault(r => r.Id == runId));

    internal ChatLogBackfillTransition TransitionOf(FakeRun? run) =>
        run is null
            ? new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Cancelled, 0, 0, null)
            : new ChatLogBackfillTransition(run.Status, run.PauseCount, run.BlockAttempts, run.PausedUntilUtc);

    internal ChatLogBackfillBlockResult Replace(
        long runId, DateOnly from, DateOnly to, IReadOnlyList<ChatLogBackfillAggregate> rows, long bytes, long messages, bool isLast)
    {
        var run = Find(runId);
        if (run is null)
        {
            return new ChatLogBackfillBlockResult.ChannelGone();
        }

        if (ReplaceOverride?.Invoke(run) is { } overridden)
        {
            return overridden;
        }

        return Locked<ChatLogBackfillBlockResult>(() =>
        {
            var index = (from.DayNumber - run.WindowFrom.DayNumber) / ChatLogBackfillWindow.BlockDays;
            var plannedTo = from.AddDays(ChatLogBackfillWindow.BlockDays) < run.WindowTo ? from.AddDays(ChatLogBackfillWindow.BlockDays) : run.WindowTo;
            if (run.Status != ChatLogBackfillRunStatus.Running || run.WeeksDone != index || to != plannedTo || isLast != (index + 1 == run.WeeksTotal))
            {
                return new ChatLogBackfillBlockResult.RunNotActive();
            }

            run.Commits.Add(new FakeCommit(from, to, rows, bytes, messages));
            run.WeeksDone++;
            run.BytesReceived += bytes;
            run.MessagesRead += messages;
            run.PauseCount = 0;
            run.BlockAttempts = 0;
            run.Status = run.WeeksDone >= run.WeeksTotal ? ChatLogBackfillRunStatus.Completed : ChatLogBackfillRunStatus.Running;
            return new ChatLogBackfillBlockResult.Committed(new ChatLogBackfillTransition(run.Status, 0, 0, null));
        });
    }

    internal ChatLogBackfillTransition Pause(long runId, TimeSpan? retryAfter, DateTime nowUtc) => Locked(() =>
    {
        var run = _runs.SingleOrDefault(r => r.Id == runId);
        var cap = TimeSpan.FromSeconds(options.MaxRetryAfterSeconds);
        var delay = retryAfter ?? TimeSpan.FromSeconds(60 * Math.Pow(2, run?.PauseCount ?? 0));
        delay = delay < cap ? delay : cap;
        if (run is { Status: ChatLogBackfillRunStatus.Running })
        {
            if (run.PauseCount >= options.MaxConsecutivePauses)
            {
                run.Status = ChatLogBackfillRunStatus.Failed;
                run.ErrorCode = ChatLogBackfillService.RateLimitedErrorCode;
            }
            else
            {
                run.Status = ChatLogBackfillRunStatus.Paused;
                run.PausedUntilUtc = nowUtc + delay;
                run.PauseCount++;
            }
        }

        var until = nowUtc + delay;
        CooldownUntilUtc = CooldownUntilUtc is { } existing && existing > until ? existing : until;
        return TransitionOf(run);
    });

    internal ChatLogBackfillTransition RecordAttempt(long runId, long bytes) => Locked(() =>
    {
        var run = _runs.SingleOrDefault(r => r.Id == runId);
        if (run is { Status: ChatLogBackfillRunStatus.Running })
        {
            run.BlockAttempts++;
            run.BytesReceived += bytes;
        }

        return TransitionOf(run);
    });

    internal void Fail(long runId, string errorCode, int? httpStatus, long bytes) => Locked(() =>
    {
        var run = _runs.SingleOrDefault(r => r.Id == runId);
        if (run is { Status: ChatLogBackfillRunStatus.Running })
        {
            run.Status = ChatLogBackfillRunStatus.Failed;
            run.ErrorCode = errorCode;
            run.ErrorHttpStatus = httpStatus;
            run.BytesReceived += bytes;
        }

        return 0;
    });
}

internal sealed class FakeRun
{
    public long Id { get; init; }

    public string ChannelName { get; init; } = string.Empty;

    public DateOnly WindowFrom { get; init; }

    public DateOnly WindowTo { get; init; }

    public int WeeksTotal { get; init; }

    public int WeeksDone { get; set; }

    public ChatLogBackfillRunStatus Status { get; set; } = ChatLogBackfillRunStatus.Queued;

    public int PauseCount { get; set; }

    public int BlockAttempts { get; set; }

    public DateTime? PausedUntilUtc { get; set; }

    public string? ErrorCode { get; set; }

    public int? ErrorHttpStatus { get; set; }

    public long BytesReceived { get; set; }

    public long MessagesRead { get; set; }

    public int Claims { get; set; }

    public IReadOnlyList<ChatLogBackfillSnapshotEmote> Snapshot { get; init; } = [];

    public List<FakeCommit> Commits { get; } = [];
}

internal sealed record FakeCommit(DateOnly From, DateOnly To, IReadOnlyList<ChatLogBackfillAggregate> Rows, long Bytes, long Messages);

/// <summary>
/// One resolved service instance; records which methods were called on it. Disposing it releases the
/// loop lock it holds, as closing the real instance's lock connection does.
/// </summary>
internal sealed class FakeBackfillService(FakeBackfillStore store) : IChatLogBackfillService, IAsyncDisposable
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<ChatLogBackfillStatusDto?> GetStatusAsync(string channelName, DateOnly todayUtc, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Api side.");

    public Task<ChatLogBackfillEnqueueResult> EnqueueAsync(
        string channelName, string emoteSetId, int months, DateOnly todayUtc, AuditActor actor, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Api side.");

    public Task<ChatLogBackfillCancelResult> CancelAsync(string channelName, AuditActor actor, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Api side.");

    public Task<ChatLogBackfillCoverageDto> GetCoverageAsync(string channelId, EmoteSetScope scope, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Api side.");

    public Task<int> ResetInterruptedRunsAsync(CancellationToken cancellationToken = default) =>
        Record(nameof(ResetInterruptedRunsAsync), store.Reset);

    public Task<ChatLogBackfillClaim?> ClaimNextAsync(DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Record(nameof(ClaimNextAsync), () => store.Claim(nowUtc));

    public Task<bool> IsRunActiveAsync(long runId, CancellationToken cancellationToken = default) =>
        Record(nameof(IsRunActiveAsync), () => store.Find(runId)?.Status == ChatLogBackfillRunStatus.Running);

    public Task<ChatLogBackfillSnapshot> GetSnapshotAsync(long runId, CancellationToken cancellationToken = default) =>
        Record(nameof(GetSnapshotAsync), () => new ChatLogBackfillSnapshot("set-1", store.Find(runId)?.Snapshot ?? []));

    public Task<ChatLogBackfillBlockResult> ReplaceBlockAsync(
        long runId, DateOnly blockFrom, DateOnly blockToExclusive, IReadOnlyList<ChatLogBackfillAggregate> rows, long bytes, long messages,
        bool isLastBlock, CancellationToken cancellationToken = default) =>
        Record(nameof(ReplaceBlockAsync), () => store.Replace(runId, blockFrom, blockToExclusive, rows, bytes, messages, isLastBlock));

    public Task<ChatLogBackfillTransition> PauseAsync(long runId, TimeSpan? retryAfter, DateTime nowUtc, CancellationToken cancellationToken = default) =>
        Record(nameof(PauseAsync), () => store.Pause(runId, retryAfter, nowUtc));

    public Task<DateTime?> GetCooldownUntilAsync(CancellationToken cancellationToken = default) =>
        Record(nameof(GetCooldownUntilAsync), () => store.CooldownUntilUtc);

    public Task<ChatLogBackfillTransition> RecordBlockAttemptAsync(long runId, long bytes, CancellationToken cancellationToken = default) =>
        Record(nameof(RecordBlockAttemptAsync), () => store.RecordAttempt(runId, bytes));

    public Task FailAsync(long runId, string errorCode, int? httpStatus, long bytes, CancellationToken cancellationToken = default) =>
        Record(nameof(FailAsync), () => { store.Fail(runId, errorCode, httpStatus, bytes); return 0; });

    public Task<bool> TryAcquireLoopLockAsync(CancellationToken cancellationToken = default) =>
        Record(nameof(TryAcquireLoopLockAsync), () => store.TryLock(this));

    public ValueTask DisposeAsync()
    {
        store.ReleaseLock(this);
        return ValueTask.CompletedTask;
    }

    private Task<T> Record<T>(string call, Func<T> action)
    {
        lock (_calls)
        {
            _calls.Add(call);
        }

        try
        {
            return Task.FromResult(action());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}

/// <summary>The archive: a handler per request, every request recorded with the fake clock's time.</summary>
internal sealed class FakeArchive(TimeProvider clock) : IChatLogArchiveClient
{
    private readonly List<ArchiveRequest> _requests = [];

    public delegate Task<ChatLogRangeResult> RangeHandler(ArchiveRequest request, Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct);

    /// <summary>Answers a request; the default is an empty, complete block.</summary>
    public RangeHandler Handler { get; set; } = (_, _, _) => Task.FromResult(Complete(0, 0));

    public IReadOnlyList<ArchiveRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static ChatLogRangeResult Complete(long bytes, int messages) => new(ChatLogDayStatus.Complete, bytes, messages, 0, 0, 200, null);

    public static ChatLogRangeResult Status(ChatLogDayStatus status, int? http, long bytes = 0, TimeSpan? retryAfter = null) =>
        new(status, bytes, 0, 0, 0, http, retryAfter);

    /// <summary>A read that only ends when its token is cancelled, then answers like the real client mid-body.</summary>
    public static async Task<ChatLogRangeResult> UntilCancelled(CancellationToken ct, Action onCancelled)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
        }

        onCancelled();
        return new ChatLogRangeResult(ChatLogDayStatus.Cancelled, 4096, 7, 0, 0, 200, null);
    }

    public Task<ChatLogDayResult> ReadDayAsync(
        string twitchChannelId, DateOnly day, long maxBytes, Func<ChatLogMessage, ValueTask> onMessage, CancellationToken ct) =>
        throw new NotSupportedException("The backfill reads ranges.");

    public Task<ChatLogRangeResult> ReadRangeAsync(
        string twitchChannelId, DateTime fromUtc, DateTime toUtcExclusive, long maxBytes, Func<ChatLogMessage, ValueTask> onMessage,
        CancellationToken ct)
    {
        var request = new ArchiveRequest(twitchChannelId, fromUtc, toUtcExclusive, maxBytes, clock.GetUtcNow().UtcDateTime);
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Handler(request, onMessage, ct);
    }
}

internal sealed record ArchiveRequest(string TwitchChannelId, DateTime FromUtc, DateTime ToUtcExclusive, long MaxBytes, DateTime AtUtc);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// One worker under test with its fakes. The clock only moves when <see cref="DriveUntilAsync"/> winds
/// it; between steps the worker gets a moment of real time to reach its next wait.
/// </summary>
internal sealed class BackfillRig : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _published = [];
    private readonly ChatLogBackfillWorker _worker;
    private int _clientResolutions;
    private bool _started;

    public BackfillRig(Action<ChatLogBackfillOptions>? configure = null, BackfillRig? sharingWith = null)
    {
        Options = new ChatLogBackfillOptions { Enabled = true };
        configure?.Invoke(Options);
        Clock = sharingWith?.Clock ?? new FakeTimeProvider(Start);
        Store = sharingWith?.Store ?? new FakeBackfillStore(Options);
        Archive = new FakeArchive(Clock);

        var redis = Substitute.For<IRedisPublisher>();
        redis.When(r => r.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                lock (_published)
                {
                    _published.Add(LiveEvent.TryParse(call.ArgAt<string>(1))!.Type);
                }
            });

        var excluded = Substitute.For<IExcludedChatterFilter>();
        excluded.IsExcluded(Arg.Any<string?>()).Returns(call => call.Arg<string?>() == ExcludedChatter);

        var services = new ServiceCollection();
        services.AddScoped<IChatLogBackfillService>(_ => Store.NewInstance());
        services.AddScoped<IChatLogArchiveClient>(_ =>
        {
            Interlocked.Increment(ref _clientResolutions);
            return Archive;
        });

        var gate = new BootRecoveryGate();
        gate.MarkCompleted();
        _worker = new ChatLogBackfillWorker(
            Logger,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options,
            gate,
            Signal,
            redis,
            new BotChatterDetector(new ConfigurationBuilder().Build()),
            excluded,
            Clock);
    }

    public const string ExcludedChatter = "excluded-1";

    /// <summary>Shared with the rig passed as <c>sharingWith</c>, like the store: two loops, one database.</summary>
    public FakeTimeProvider Clock { get; }

    public ChatLogBackfillOptions Options { get; }

    public FakeBackfillStore Store { get; }

    public FakeArchive Archive { get; }

    public ChatLogBackfillSignal Signal { get; } = new();

    public RecordingLogger<ChatLogBackfillWorker> Logger { get; } = new();

    public int ClientResolutions => Volatile.Read(ref _clientResolutions);

    public Task? ExecuteTask => _worker.ExecuteTask;

    public DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public IReadOnlyList<string> Published
    {
        get
        {
            lock (_published)
            {
                return [.. _published];
            }
        }
    }

    public async Task StartAsync()
    {
        _started = true;
        await _worker.StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// Winds the clock in <paramref name="step"/>s until <paramref name="condition"/> holds; throws when
    /// it does not within <paramref name="limit"/> of fake time.
    /// </summary>
    public async Task DriveUntilAsync(Func<bool> condition, TimeSpan? step = null, TimeSpan? limit = null)
    {
        var stepSize = step ?? TimeSpan.FromSeconds(1);
        var end = Now + (limit ?? TimeSpan.FromHours(2));
        while (true)
        {
            if (await SettlesAsync(condition))
            {
                return;
            }

            if (Now >= end)
            {
                throw new TimeoutException($"Condition not reached by {Now:O}.");
            }

            Clock.Advance(stepSize);
        }
    }

    /// <summary>Winds the clock by <paramref name="span"/> in steps, letting the worker run in between.</summary>
    public async Task DriveForAsync(TimeSpan span, TimeSpan? step = null)
    {
        var end = Now + span;
        await DriveUntilAsync(() => Now >= end, step, span + TimeSpan.FromSeconds(1));
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            _started = false;
            await _worker.StopAsync(CancellationToken.None);
        }

        _worker.Dispose();
    }

    /// <summary>Stops the worker as a shutdown would: its loop scope is disposed, its lock released.</summary>
    public Task StopAsync()
    {
        _started = false;
        return _worker.StopAsync(CancellationToken.None);
    }

    // A few short real waits: the worker's continuations run on the thread pool.
    private static async Task<bool> SettlesAsync(Func<bool> condition)
    {
        for (var i = 0; i < 3; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(2);
        }

        return condition();
    }
}
