using EmotePurge.Core.Chat;

namespace EmotePurge.Worker;

/// <summary>
/// The three buckets a matched emote hit lands in, one-to-one with
/// <see cref="EmotePurge.Core.Services.EmoteUsageCounts"/>. Replaces the earlier <c>bool isBot</c>
/// on <see cref="IEmoteUsageCounter.Increment"/>, which had no way to represent the room question
/// at all (#73).
/// </summary>
public enum UsageCategory
{
    /// <summary>A message from the channel's own room, from a chatter not classified as a bot.</summary>
    Human,

    /// <summary>A message from the channel's own room, from a recognized bot account.</summary>
    Bot,

    /// <summary>
    /// A message mirrored in from a foreign room during a Twitch Shared Chat session, or one whose
    /// room could not be determined — see <see cref="UsageCategoryRule.Resolve"/> for the
    /// precedence that folds both into this one bucket.
    /// </summary>
    SharedChat
}

/// <summary>
/// The one place the room-vs-bot precedence from the design doc (D2) is decided. Both the live path
/// (<c>TwitchChatManager.OnMessageReceived</c>) and the chat-log replay path
/// (<c>ReplayDayCounter.Count</c>) call this instead of each carrying their own <c>if</c>, so the
/// mapping cannot drift between them.
/// </summary>
public static class UsageCategoryRule
{
    /// <summary>
    /// <see cref="MessageOrigin.Own"/> resolves by <paramref name="isBot"/>;
    /// <see cref="MessageOrigin.Foreign"/> and <see cref="MessageOrigin.Indeterminate"/> both
    /// resolve to <see cref="UsageCategory.SharedChat"/> regardless of <paramref name="isBot"/> —
    /// the fourth state, "bot in a foreign room", is deliberately not representable
    /// (DECISIONS.md, "Shared Chat", 2026-09-06, D2).
    /// </summary>
    public static UsageCategory Resolve(MessageOrigin origin, bool isBot) => (origin, isBot) switch
    {
        (MessageOrigin.Own, true) => UsageCategory.Bot,
        (MessageOrigin.Own, false) => UsageCategory.Human,
        _ => UsageCategory.SharedChat
    };
}
