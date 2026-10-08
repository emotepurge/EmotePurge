namespace EmotePurge.Core.Entities;

public class UsageStat
{
    public long Id { get; set; }
    public string EmoteId { get; set; } = string.Empty;

    // UTC calendar day; one row per emote per day.
    public DateOnly Date { get; set; }
    public int UseCount { get; set; }

    // A row stays (EmoteId, Date)-unique regardless of these columns: either of them can push a row
    // into existence with UseCount = 0 — BotUseCount > 0 (only a bot posted this emote in the
    // batch) or SharedChatUseCount > 0 (only foreign-room messages did). The read queries in
    // UsageStatQueryService treat both cases identically and filter on UseCount > 0, so neither
    // kind of row reads as "used" (issue #73, DECISIONS 2026-09-08 "Die Oberfläche zeigt nur noch
    // eigene Nutzung").
    public int BotUseCount { get; set; }

    // Everything mirrored in from a foreign room during a Twitch Shared Chat session — bots and
    // undeterminable messages included (DECISIONS 2026-09-06 "Shared Chat bekommt eine dritte
    // Spalte", #73).
    public int SharedChatUseCount { get; set; }

    // The 7TV emote set this row's usage was counted against — the locally observed set at count
    // time, not necessarily the channel's current one (spec section 5: the chat path keys counts by
    // the match cache's observed set id, which can lag a switch by a batch flush). Never a default:
    // the AddUsageStatEmoteSetId migration (#200) backfills every existing row before this column
    // becomes NOT NULL, so no code path ever sees an empty value here.
    public string EmoteSetId { get; set; } = string.Empty;

    public Emote Emote { get; set; } = null!;
}
