namespace EmotePurge.Core.Services;

/// <summary>
/// The three-state live answer the API gives per channel. A string contract rather than an enum so
/// the JSON wire value is the value named here, independent of serializer enum settings — the same
/// reasoning as <c>ApiErrorCodes</c>.
/// </summary>
public static class ChannelLiveStates
{
    public const string Live = "live";
    public const string Offline = "offline";

    /// <summary>
    /// No statement, not "offline": the snapshot is absent (worker down, poll disabled, key
    /// expired) or the channel was not part of the poll (bot inactive).
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// The one place the three-way decision is made. <paramref name="liveLogins"/> is null when no
    /// snapshot exists; <paramref name="wasPolled"/> says whether the channel was in the polled set
    /// (the worker only polls bot-active channels), because absence from the live set proves
    /// nothing about a channel that was never asked about.
    /// </summary>
    public static string Derive(IReadOnlySet<string>? liveLogins, string normalizedChannelName, bool wasPolled)
    {
        if (liveLogins is null)
        {
            return Unknown;
        }

        if (liveLogins.Contains(normalizedChannelName))
        {
            return Live;
        }

        return wasPolled ? Offline : Unknown;
    }
}

/// <summary>
/// The worker's most recent Helix live-poll result: which of the polled channels were live, and
/// when that was asked. Published as a Redis TTL key so a dead worker (or a disabled poll) expires
/// into "unknown" instead of asserting "offline" forever — the worker stays the only holder of the
/// app access token, the API never talks to Helix for this (idea B10; see the A10 decision on
/// competing client-credentials grants).
/// </summary>
/// <param name="GeneratedAtUtc">
/// When the poll ran. Travels in the payload so the API can report "as of x minutes ago" — the
/// key's TTL only ever says "gone", same arrangement as <see cref="WorkerRosterSnapshot"/>.
/// </param>
public record TwitchLiveStatusSnapshot(
    DateTime GeneratedAtUtc,
    IReadOnlyList<string>? LiveChannelLogins = null)
{
    /// <summary>
    /// Never null, whatever the payload said — a snapshot from a writer version whose array was
    /// renamed or dropped must read as "nobody live", handled once here.
    /// </summary>
    public IReadOnlyList<string> LiveChannelLogins { get; init; } = LiveChannelLogins ?? [];
}

/// <summary>
/// Reads the live-status snapshot the worker publishes. Same arrangement as
/// <see cref="IWorkerRosterReader"/>: API and worker never talk directly, and the wire format has a
/// single home.
/// </summary>
public interface ITwitchLiveStatusReader
{
    /// <summary>
    /// <c>null</c> when the key is absent (expired, poll disabled, or the worker never started) or
    /// unreadable — which is itself the answer, not an error.
    /// </summary>
    Task<TwitchLiveStatusSnapshot?> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the same snapshot without logging transient Redis failures. Transient Redis exceptions
    /// propagate to high-frequency telemetry paths so their throttled failure reporting can handle them.
    /// </summary>
    Task<TwitchLiveStatusSnapshot?> ReadQuietlyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The worker's side of the same key. A successful poll is a statement about every polled channel —
/// including "nobody is live" — so an empty set is published like any other result; only a failed
/// poll publishes nothing and lets the key age out.
/// </summary>
public interface ITwitchLiveStatusWriter
{
    Task PublishAsync(TwitchLiveStatusSnapshot snapshot, TimeSpan timeToLive, CancellationToken cancellationToken = default);
}
