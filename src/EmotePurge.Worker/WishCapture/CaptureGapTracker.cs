using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.WishCapture;

/// <summary>
/// The watchdog's side of the capture gaps (plan #486 2.7): two calls, both synchronous and in memory only. They sit
/// outside the rebuild decision — backoff, signals and the rebuild itself do not depend on them.
/// </summary>
public interface ICaptureGapRecorder
{
    /// <summary>
    /// The connection was lost; called before the first rebuild attempt with the signal's time. Opens a
    /// <c>disconnect</c> gap from <paramref name="signalUtc"/> for every channel of the set without an open gap, and
    /// marks a connection gap open until <see cref="RejoinRoundCompleted"/>.
    /// </summary>
    void ConnectionLost(DateTime signalUtc);

    /// <summary>
    /// The rejoin round after a rebuild returned. Ends the open connection gap and asks for one immediate sample;
    /// closes nothing by itself.
    /// </summary>
    void RejoinRoundCompleted();
}

/// <summary>
/// Which channels currently miss chat lines, and since when (plan 2.7, Sol-P2-8 and Sol-R2-P2-6, both scaled down by
/// the operator). Pure and deliberately coarse: three reasons, at most one open gap per channel, nothing finer.
/// <para>
/// <b>The set</b> comes from the database (<see cref="IWishCaptureService.ListGapChannelsAsync"/>, read again on every
/// tick), not from the manager's roster, which the boot recovery fills only channel by channel.
/// </para>
/// <para>
/// <b>Closing</b> needs the confirmed join of the current client generation: <see cref="TwitchRosterEntry.JoinConfirmed"/>,
/// which the manager resets on every rebuild and confirms only under the current client's identity check.
/// <c>IsConnected</c> is not used, it is already true at the handshake, before the paced joins are confirmed. While a
/// connection gap is open a sample decides nothing: between the signal and the client swap the roster still carries the
/// old client's confirmations.
/// </para>
/// <para>
/// <b>An approximation, not a measurement.</b> Precision is about one tick (a minute) or one rejoin round; a channel
/// confirmed early in a round is charged until the round's end. A closed gap shorter than
/// <see cref="WishLimits.MinCaptureGap"/> is dropped here already (the service skips it as well), so a short deliberate
/// reconnect leaves no row. The tracker is thread-safe: the watchdog and the capture worker call it from different loops.
/// </para>
/// </summary>
public sealed class CaptureGapTracker : ICaptureGapRecorder
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, OpenGap> _open = new(StringComparer.Ordinal);

    private IReadOnlyList<WishCaptureGapChannel> _set = [];
    private bool _connectionGapOpen;
    private TaskCompletionSource _sampleRequest = NewSampleRequest();

    /// <summary>Whether a connection gap is open (between <see cref="ConnectionLost"/> and the end of the rejoin round).</summary>
    public bool IsConnectionGapOpen
    {
        get
        {
            lock (_gate)
            {
                return _connectionGapOpen;
            }
        }
    }

    /// <summary>
    /// Completes when <see cref="RejoinRoundCompleted"/> asks for an immediate sample; <see cref="AcknowledgeSampleRequest"/>
    /// re-arms it.
    /// </summary>
    public Task SampleRequested
    {
        get
        {
            lock (_gate)
            {
                return _sampleRequest.Task;
            }
        }
    }

    /// <summary>The open gaps by channel id, a copy (for tests and diagnostics).</summary>
    internal IReadOnlyDictionary<string, OpenGap> OpenGaps
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, OpenGap>(_open, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// The capture worker's start, once, with the life stamp read before its first write. With a stamp, every channel of
    /// <paramref name="set"/> gets a <c>worker_restart</c> gap from the stamp; without one (the very first start) none.
    /// </summary>
    public void Start(DateTime? lastAliveUtc, IReadOnlyList<WishCaptureGapChannel> set)
    {
        lock (_gate)
        {
            _set = set;
            if (lastAliveUtc is not { } stamp)
            {
                return;
            }

            foreach (var channel in set)
            {
                _open.TryAdd(channel.ChannelId, new OpenGap(stamp, WishCaptureGapReason.WorkerRestart));
            }
        }
    }

    /// <inheritdoc />
    public void ConnectionLost(DateTime signalUtc)
    {
        lock (_gate)
        {
            // A channel that already has a gap keeps it with its first reason and start; two rebuilds in a row
            // therefore keep one beginning.
            foreach (var channel in _set)
            {
                _open.TryAdd(channel.ChannelId, new OpenGap(signalUtc, WishCaptureGapReason.Disconnect));
            }

            _connectionGapOpen = true;
        }
    }

    /// <inheritdoc />
    public void RejoinRoundCompleted()
    {
        lock (_gate)
        {
            _connectionGapOpen = false;
            _sampleRequest.TrySetResult();
        }
    }

    /// <summary>Re-arms <see cref="SampleRequested"/> once it has completed.</summary>
    public void AcknowledgeSampleRequest()
    {
        lock (_gate)
        {
            if (_sampleRequest.Task.IsCompleted)
            {
                _sampleRequest = NewSampleRequest();
            }
        }
    }

    /// <summary>
    /// One sample against the current set and roster; returns the gaps it closed (at <paramref name="nowUtc"/>) that are
    /// at least <see cref="WishLimits.MinCaptureGap"/> long. While a connection gap is open it only takes the new set.
    /// Otherwise: a channel no longer in the set drops its open gap; a channel with an open gap and a confirmed join closes
    /// it; a channel of the set without an open gap and without a confirmed join opens a <c>not_joined</c> gap from now.
    /// </summary>
    public IReadOnlyList<WishCaptureGapRecord> Sample(
        IReadOnlyList<WishCaptureGapChannel> set,
        IReadOnlyList<TwitchRosterEntry> roster,
        DateTime nowUtc)
    {
        lock (_gate)
        {
            _set = set;
            if (_connectionGapOpen)
            {
                return [];
            }

            var confirmed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in roster)
            {
                if (entry.JoinConfirmed)
                {
                    confirmed.Add(entry.ChannelName);
                }
            }

            var inSet = new HashSet<string>(set.Count, StringComparer.Ordinal);
            List<WishCaptureGapRecord>? closed = null;
            foreach (var channel in set)
            {
                inSet.Add(channel.ChannelId);
                var joined = confirmed.Contains(channel.ChannelName);
                if (_open.TryGetValue(channel.ChannelId, out var gap))
                {
                    if (!joined)
                    {
                        continue;
                    }

                    _open.Remove(channel.ChannelId);
                    if (nowUtc - gap.FromUtc >= WishLimits.MinCaptureGap)
                    {
                        (closed ??= []).Add(new WishCaptureGapRecord(channel.ChannelId, gap.FromUtc, nowUtc, gap.Reason));
                    }
                }
                else if (!joined)
                {
                    _open[channel.ChannelId] = new OpenGap(nowUtc, WishCaptureGapReason.NotJoined);
                }
            }

            foreach (var channelId in _open.Keys.Where(id => !inSet.Contains(id)).ToList())
            {
                _open.Remove(channelId);
            }

            return closed ?? (IReadOnlyList<WishCaptureGapRecord>)[];
        }
    }

    private static TaskCompletionSource NewSampleRequest() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>An open gap: since when, and why.</summary>
    internal readonly record struct OpenGap(DateTime FromUtc, string Reason);
}
