namespace EmotePurge.Worker;

/// <summary>
/// Decides whether a chat message's sender has objected to being processed (GDPR Art. 21, issue
/// #252) and must therefore be dropped before any counting or classification — nothing
/// TwitchLib-specific, so it can be tested without a client (see <see cref="ExcludedChatterFilter"/>).
/// </summary>
public interface IExcludedChatterFilter
{
    /// <summary>
    /// Never throws — this runs on the hot path in <c>TwitchChatManager.OnMessageReceived</c>,
    /// right after the mandatory watchdog liveness bookkeeping and before anything else touches the
    /// message. A missing/empty <paramref name="chatterId"/> just returns <c>false</c> rather than
    /// failing.
    /// </summary>
    bool IsExcluded(string? chatterId);
}
