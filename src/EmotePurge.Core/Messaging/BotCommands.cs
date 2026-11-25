namespace EmotePurge.Core.Messaging;

/// <summary>
/// The Api -> Worker command protocol on the shared Redis pub/sub channel: plain strings of the
/// form <c>PREFIX:channelname</c>, no JSON (see the decision log — the payload is a single
/// normalized channel name, and a schema would outgrow the problem). The channel name and the
/// prefixes live here so producer (ChannelService) and consumer (Worker) cannot drift apart;
/// unknown prefixes are ignored by the worker, which is what makes adding a new one safe.
/// </summary>
public static class BotCommands
{
    public const string Channel = "channel:bot:commands";

    public const string JoinPrefix = "JOIN:";
    public const string LeavePrefix = "LEAVE:";
    public const string ResyncPrefix = "RESYNC:";

    /// <summary>
    /// Nudge for the chat-log backfill worker. The payload is a normalized channel name for the log
    /// only; the worker never trusts it for identity (the queued run row is the truth).
    /// </summary>
    public const string BackfillPrefix = "BACKFILL:";

    /// <summary>
    /// Nudge for the bot account worker (#487): the token or the kill switch changed. Sent exactly as the prefix, with no
    /// payload; the credential row is the truth and the worker's own poll finds every change anyway.
    /// </summary>
    public const string BotAccountPrefix = "BOTACCOUNT:";
}
