namespace EmotePurge.Worker.SevenTv;

public interface ISevenTvEventClient
{
    /// <summary>Feature flag SevenTv:EventApi:Enabled, read once at construction.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Runs exactly one connection lifetime: connect, await Hello, converge the socket's
    /// subscriptions towards the registry's desired state, gap-fill via full resyncs, then pump
    /// frames until the server ends the session, the heartbeat times out or shutdown is requested.
    /// Returns for every expected end instead of throwing; the calling loop owns retry pacing.
    /// </summary>
    Task<SevenTvSessionResult> RunSessionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records the channel's desired subscriptions and nudges a running session to converge.
    /// Never blocks on socket IO and is safe before any connection exists — desired state first;
    /// sending subscribe frames ahead of an open socket is the bug that sank the 2026-07 client.
    /// </summary>
    void EnsureSubscribed(string channelName, string emoteSetId, string? sevenTvUserId);

    void Unsubscribe(string channelName);

    /// <summary>Snapshot of the channels the registry currently desires subscriptions for.</summary>
    IReadOnlyList<string> DesiredChannels { get; }

    /// <summary>
    /// The per-connection subscription limit 7TV stated in its last Hello, or null until the first
    /// one arrives. Kept across disconnects on purpose: the admin bar needs a denominator, and the
    /// last figure 7TV gave is a far better one than "unknown" every time the socket drops.
    /// </summary>
    int? SubscriptionLimit { get; }

    bool IsConnected { get; }
    DateTime? LastFrameReceivedUtc { get; }
    DateTime? LastDispatchReceivedUtc { get; }
    DateTime? ConnectAttemptedUtc { get; }
}
