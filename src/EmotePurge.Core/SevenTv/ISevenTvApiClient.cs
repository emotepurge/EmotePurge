namespace EmotePurge.Core.SevenTv;

public interface ISevenTvApiClient
{
    // Never null: the outcome is the answer. Ok carries the resolved Twitch user id, the three
    // failure statuses say why there is none — a distinction that used to be lost in a bare null.
    Task<SevenTvTwitchUserIdResult> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default);

    // The channel's active emote set plus the 7TV account id behind the Twitch connection — both
    // come from the same users/twitch/{id} response, so resolving them together costs no extra call.
    // Never null; State is populated if and only if Status is Ok. The three failure statuses are the
    // three ways this call can legitimately produce nothing, and they must stay apart: only one of
    // them ("no active emote set") is something the channel owner can fix.
    Task<SevenTvChannelStateResult> GetChannelStateForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // Resolves a Twitch user's own 7TV account identity plus their currently active Twitch-linked
    // emote set, via 7TV's userByConnection GQL query. Never null; Identity is populated if and only
    // if Status is Ok. NoSevenTvAccount ("no 7TV user carries this Twitch connection") must stay
    // distinct from Unavailable — collapsing them onto one null used to make a user who simply has no
    // 7TV account look like a failed lookup (issue #37).
    Task<SevenTvIdentityResult> ResolveSevenTvIdentityAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // The actual owner of a given 7TV emote set — distinct from whichever channel currently has it
    // active, since 7TV lets an editor point a channel's active set at someone else's set entirely.
    Task<string?> GetEmoteSetOwnerIdAsync(string emoteSetId, CancellationToken cancellationToken = default);

    // The same owner question behind the provider-wide request budget, with the failure kept apart
    // from "no such set" — the fallback of the set-centric import's owner check (spec 2026-09-20,
    // section 32), which must not reach 7TV unbudgeted. Charges one permit before the request is
    // built; a refusal is BudgetExhausted and nothing is sent. Never null.
    Task<SevenTvEmoteSetOwnerLookupResult> LookUpEmoteSetOwnerAsync(string emoteSetId, CancellationToken cancellationToken = default);

    // Channels (by their Twitch connection) that the given 7TV account holds editor rights on. Never
    // null; Grants is populated if and only if Status is Ok — an account with zero grants still
    // answers Ok with an empty list, since only a genuinely unusable response reaches Unavailable.
    Task<SevenTvEditorGrantsResult> GetEditorOfChannelsAsync(string sevenTvUserId, CancellationToken cancellationToken = default);

    // The same two questions as ResolveSevenTvIdentityAsync followed by GetEditorOfChannelsAsync —
    // the account behind a Twitch id, then the channels it edits — behind the provider-wide request
    // budget: one permit before each of the (at most two) requests, and a refusal sends nothing
    // further. For the set-centric import's owner check only (spec 2026-09-20, section 32, second
    // review round); the authorization path keeps the unbudgeted pair on purpose. Never null.
    Task<SevenTvEditorGrantsLookup> LookUpEditorGrantsAsync(string twitchUserId, CancellationToken cancellationToken = default);

    // A read-only, paginated preview of an arbitrary 7TV emote set — the foreign-channel-import
    // source (spec 2026-09-09), distinct from every method above in that the caller need not own,
    // moderate, or even track the channel the set belongs to. Uses the v4 GQL emoteSet(id) query, not
    // the v3 REST path GetChannelStateForTwitchUserAsync reads: that path exists for our own synced
    // channels and its NoActiveEmoteSet/fallback machinery does not apply here. Never null; Preview is
    // populated if and only if Status is Ok. RateLimited is reported apart from Unavailable
    // specifically so a hardening decorator can react to a confirmed 7TV overload differently from a
    // generic upstream failure (spec E4) — see SevenTvPreviewLookupStatus.
    Task<SevenTvEmoteSetPreviewResult> GetEmoteSetPreviewAsync(string emoteSetId, CancellationToken cancellationToken = default);

    // One page of 7TV's network-wide emote leaderboard for the given sort — the leaderboard
    // import source (spec 2026-09-13), sibling to the preview above but reading
    // EmoteQuery.search rather than emoteSet(id): there is no set here, only a global ranking, so
    // this takes a sort instead of a set id. page is 1-based, matching 7TV's own convention; the
    // per-page size is fixed at 250 inside the client (E5) and is never taken from the caller.
    // Never null; Page is populated if and only if Status is Ok. See
    // SevenTvEmoteSearchPageResult for why every outcome — including a failure — still carries
    // 7TV's rate-limit header sample.
    Task<SevenTvEmoteSearchPageResult> SearchEmotesAsync(
        SevenTvLeaderboardSort sortBy, int page, CancellationToken cancellationToken = default);

    // Every emote set a 7TV account owns, by the Twitch id of its connection — the source behind
    // all three set-list routes (spec 2026-09-20, 6.1/E6/E7). v4 rather than v3 because v3 is
    // measurably incomplete: the same account answers with three sets there and four here, the
    // missing one being the personal set a picker must be able to see in order to refuse it. One
    // request carries the sets, their kind and owner, and the account's active set id, so the whole
    // list costs a single permit. Never null; Listing is populated if and only if Status is Ok, and
    // NoSevenTvAccount ("7TV carries no account for this connection") must stay distinct from
    // Unavailable — an empty list standing in for a failed read is what 6.1 forbids outright.
    Task<SevenTvEmoteSetListResult> GetEmoteSetListForTwitchUserAsync(
        string twitchUserId, CancellationToken cancellationToken = default);
}
