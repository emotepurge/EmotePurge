namespace EmotePurge.Worker;

/// <summary>
/// Decides whether a chat message came from a bot, once per message, out of the chatter id and the
/// IRC badges — nothing TwitchLib-specific, so it can be tested without a client (see
/// <see cref="BotChatterDetector"/>).
/// </summary>
public interface IBotChatterDetector
{
    /// <summary>
    /// Never throws — this runs on the hot path in <c>TwitchChatManager.OnMessageReceived</c>. A
    /// missing/empty <paramref name="chatterId"/> or a <c>null</c> <paramref name="badges"/> list
    /// just skips that half of the check rather than failing.
    /// </summary>
    bool IsBot(string? chatterId, IReadOnlyList<KeyValuePair<string, string>>? badges);
}
