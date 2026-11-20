namespace EmotePurge.Worker.WishCapture;

/// <summary>
/// What <c>TwitchChatManager.OnMessageReceived</c> does with one chat line after the liveness bookkeeping: the
/// order "exclusion, then capture, then the cache check" as a pure function (plan #486 2.1, AK67), so the order is
/// tested here and the handler only follows the flags. A <c>readonly record struct</c> of four flags: one call on the
/// hot path, no allocation.
/// </summary>
/// <param name="CountLine">The line counts as an accepted chat line (<c>WorkerStats.RecordChatLine</c>) and goes on
/// to splice diagnostics; false only for an excluded chatter (GDPR Art. 21, #252), whose line is dropped whole.</param>
/// <param name="Observe">The reward tag is evaluated (<see cref="RewardCapture.Capture"/>).</param>
/// <param name="CountSplicedRedemption">A line with the reward tag that the splice sentinel flagged: not evaluated,
/// only counted (plan W23 — tags and text of two messages may be mixed, and a wish with the wrong viewer or text is
/// worse than a missed redemption).</param>
/// <param name="MatchEmotes">The line goes on to room/bot classification and emote counting. Independent of the
/// reward tag and the capture switch (AK49): a redemption line is counted exactly like any other line.</param>
public readonly record struct ChatMessageRoute(bool CountLine, bool Observe, bool CountSplicedRedemption, bool MatchEmotes)
{
    /// <summary>The route of one line.</summary>
    /// <param name="isExcluded">The sender is on the exclusion list (<c>IExcludedChatterFilter</c>).</param>
    /// <param name="captureEnabled">The worker's effective capture switch (<c>WishOptions.CaptureActive</c>).</param>
    /// <param name="hasRewardTag">The line carries <c>custom-reward-id</c> (<c>ChatMessage.CustomRewardId</c> is not null).</param>
    /// <param name="isSpliced"><c>IrcLineSpliceRule.IsSpliced</c> flagged the raw line.</param>
    /// <param name="snapshotIsEmpty">The channel's emote snapshot has no entry (empty set or cold cache).</param>
    public static ChatMessageRoute Decide(
        bool isExcluded,
        bool captureEnabled,
        bool hasRewardTag,
        bool isSpliced,
        bool snapshotIsEmpty)
    {
        if (isExcluded)
        {
            return default;
        }

        var redemption = captureEnabled && hasRewardTag;

        // The capture sits before the cache check: a channel with an empty set or a cold cache still
        // captures (Sol finding 7 of the spec), so snapshotIsEmpty plays no part in Observe.
        return new ChatMessageRoute(
            CountLine: true,
            Observe: redemption && !isSpliced,
            CountSplicedRedemption: redemption && isSpliced,
            MatchEmotes: !snapshotIsEmpty);
    }
}
