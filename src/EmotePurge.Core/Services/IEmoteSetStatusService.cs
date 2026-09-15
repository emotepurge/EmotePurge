namespace EmotePurge.Core.Services;

/// <summary>
/// The channel's 7TV set as a slot budget: how many slots the set has and how many are taken.
/// </summary>
/// <param name="ActiveEmoteSetId">Empty while the first sync is still pending.</param>
/// <param name="Capacity">
/// The set's slot limit as 7TV last reported it, or <c>null</c> when it did not report one.
/// Consumers must render no budget at all in that case rather than assuming 1000 — 7TV subscribers
/// get larger sets, and inventing a denominator would understate how full the set is.
/// </param>
/// <param name="OccupiedSlots">
/// Counted from our own emote rows, not from 7TV's <c>emote_count</c>. The two agree right after
/// every full sync anyway (the sync reconciles onto exactly that list), but between syncs only our
/// count reflects the EventAPI deltas that have already arrived — and it is the number the user
/// sees in the grid below the bar.
/// </param>
/// <param name="TrackedSince">
/// Since when this channel's usage data can be trusted: the last join that reactivated the channel,
/// or its creation if it was never left and rejoined. Older than this, we simply were not counting.
/// </param>
/// <param name="SyncFailureReason">
/// One of <see cref="SevenTvSyncFailureReasons"/>, or <c>null</c> when the last sync attempt
/// succeeded — or when none has been made yet. Together with an empty
/// <paramref name="ActiveEmoteSetId"/> that absence is what tells "the first sync is still running"
/// apart from "this channel has no active emote set on 7TV", which used to look identical.
/// </param>
/// <param name="LastSyncAttemptAtUtc">
/// When the last attempt finished, successful or not. <c>null</c> means none has been made. Read
/// with <paramref name="SyncFailureReason"/>: it says how current the reason is.
/// </param>
/// <param name="BotsExcludedSince">
/// The earliest UTC day on which this channel has a <c>UsageStat</c> row with <c>BotUseCount &gt;
/// 0</c> — the first time a bot was <em>seen</em> here, not the day bot usage started being
/// counted apart. That separation began the moment this feature was deployed, which is an event
/// in the deploy history, not in the data: rows written before and after look identical when
/// <c>BotUseCount</c> happens to be 0, so there is no way to derive the true cutover from the
/// data alone. For a channel joined long before the deploy, this understates how far back the
/// mixing goes; for one joined after, it can wrongly suggest older numbers are mixed when they
/// never were. <c>null</c> means no bot has ever been seen here, in which case there is nothing
/// to explain and a consumer should show nothing — except that, since #73, <c>BotUseCount</c>
/// only counts bots seen in the channel's own room (mirrored "shared chat" bots land in
/// <c>SharedChatUseCount</c> instead), so a channel first tracked only after that deploy whose bot
/// traffic is effectively all mirrored keeps this <c>null</c> forever even though bots are in fact
/// excluded: a known, accepted gap (DECISIONS.md, D2), not evidence the channel never saw a bot.
/// </param>
/// <param name="SharedChatSeparatedSince">
/// The earliest UTC day on which this channel has a <c>UsageStat</c> row with
/// <c>SharedChatUseCount &gt; 0</c> — the first time a mirrored shared-chat message was
/// <em>seen</em> here, not the day that usage started being counted apart. Same E4 imprecision as
/// <paramref name="BotsExcludedSince"/> and for the same reason: the separation began with a
/// deploy, an event in the deploy history and not in the data. Numbers for days before this one
/// carry usage from other channels' shared chat inside <c>UseCount</c>, indistinguishably — that is
/// what a consumer has to say when it shows this date. <c>null</c> means no shared chat has ever
/// been seen here: nothing was separated away for this channel, so a consumer shows nothing. Note
/// what <c>null</c> does <em>not</em> mean — the separation itself applies to every channel, so a
/// later consumer must not read it as "shared chat is still counted here".
/// </param>
/// <param name="DuplicateNames">
/// Active emotes that collide on chat-match name (issue #45). Always a list, never null: "no
/// collisions" is an empty list, and there is no third state to report. Empty while the first sync
/// is still pending, for the same reason <paramref name="OccupiedSlots"/> is 0 there — no emote rows
/// can exist yet, so the query is skipped rather than run for a guaranteed empty answer.
/// </param>
public record EmoteSetStatusDto(
    string ActiveEmoteSetId,
    int? Capacity,
    int OccupiedSlots,
    DateTime TrackedSince,
    string? SyncFailureReason,
    DateTime? LastSyncAttemptAtUtc,
    DateOnly? BotsExcludedSince,
    DateOnly? SharedChatSeparatedSince,
    IReadOnlyList<DuplicateEmoteNameDto> DuplicateNames);

public interface IEmoteSetStatusService
{
    /// <summary>Returns <c>null</c> for a channel that is not tracked at all.</summary>
    Task<EmoteSetStatusDto?> GetAsync(string channelName, CancellationToken cancellationToken = default);
}
