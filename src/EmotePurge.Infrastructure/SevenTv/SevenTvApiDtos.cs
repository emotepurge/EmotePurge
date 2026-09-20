using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmotePurge.Infrastructure.SevenTv;

// GQL: POST gql, query { users(query: $q) { id username connections { platform username id } } }
internal sealed class SevenTvGqlUsersResponseDto
{
    public SevenTvGqlDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlDataDto
{
    // Nullable, not defaulted to `[]`: a GraphQL error response omits `users` entirely, and the
    // client needs to tell that apart from a query that genuinely matched nobody.
    public List<SevenTvGqlUserDto>? Users { get; set; }
}

internal sealed class SevenTvGqlUserDto
{
    public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public List<SevenTvGqlConnectionDto> Connections { get; set; } = [];
}

internal sealed class SevenTvGqlConnectionDto
{
    public string Platform { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

// GQL: userByConnection(platform, id) { id connections { platform id emote_set_id } }
internal sealed class SevenTvGqlUserByConnectionResponseDto
{
    public SevenTvGqlUserByConnectionDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlUserByConnectionDataDto
{
    public SevenTvGqlIdentityUserDto? UserByConnection { get; set; }
}

internal sealed class SevenTvGqlIdentityUserDto
{
    public string Id { get; set; } = string.Empty;
    public List<SevenTvGqlIdentityConnectionDto> Connections { get; set; } = [];
}

internal sealed class SevenTvGqlIdentityConnectionDto
{
    public string Platform { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? EmoteSetId { get; set; }
}

// GQL: emoteSet(id) { owner_id }
internal sealed class SevenTvGqlEmoteSetOwnerResponseDto
{
    public SevenTvGqlEmoteSetOwnerDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlEmoteSetOwnerDataDto
{
    public SevenTvGqlEmoteSetOwnerDto? EmoteSet { get; set; }
}

internal sealed class SevenTvGqlEmoteSetOwnerDto
{
    public string? OwnerId { get; set; }
}

// GQL: user(id) { editor_of { user { connections { platform id username } } } }
internal sealed class SevenTvGqlEditorOfResponseDto
{
    public SevenTvGqlEditorOfDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlEditorOfDataDto
{
    public SevenTvGqlEditorOfUserDto? User { get; set; }
}

internal sealed class SevenTvGqlEditorOfUserDto
{
    public List<SevenTvGqlEditorOfGrantDto> EditorOf { get; set; } = [];
}

internal sealed class SevenTvGqlEditorOfGrantDto
{
    public SevenTvGqlEditorOfOwnerDto? User { get; set; }
}

internal sealed class SevenTvGqlEditorOfOwnerDto
{
    public List<SevenTvGqlConnectionDto> Connections { get; set; } = [];
}

// REST: GET users/twitch/{id}. The response models the Twitch *connection*: its top-level id is
// the Twitch user id, while the 7TV account is the nested user object.
//
// EmoteSetId and User.Connections[].EmoteSetId exist for issue #43: 7TV announced (no date) that it
// will null the embedded EmoteSet object out of this response. Measured live 2026-09-01: the
// top-level EmoteSet is still populated, while the same object inside Connections[] is already null
// everywhere and these plain-id fields stay filled either way. SevenTvApiClient falls back to them
// and reloads the set from GET emote-sets/{id} when EmoteSet is absent; see
// GetChannelStateForTwitchUserAsync.
internal sealed class SevenTvUserRestDto
{
    public SevenTvEmoteSetJsonDto? EmoteSet { get; set; }
    public string? EmoteSetId { get; set; }
    public SevenTvUserRestUserDto? User { get; set; }
}

internal sealed class SevenTvUserRestUserDto
{
    public string Id { get; set; } = string.Empty;
    public List<SevenTvUserRestConnectionDto> Connections { get; set; } = [];
}

// One of the account's linked platform connections. Only ever read for Platform == "TWITCH" — a
// user can have YouTube/Kick connections with entirely different active sets, and syncing those
// would be silent data pollution (issue #43 fallback resolution).
internal sealed class SevenTvUserRestConnectionDto
{
    public string Platform { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? EmoteSetId { get; set; }
}

internal sealed class SevenTvEmoteSetJsonDto
{
    public string Id { get; set; } = string.Empty;

    // Slot limit of the set. Present in the response we already fetch (verified live 2026-08-01);
    // emote_count is deliberately not read — it is emotes.Length of this same payload, so it can
    // never tell us anything the list itself doesn't.
    public int Capacity { get; set; }
    public List<SevenTvEmoteJsonDto> Emotes { get; set; } = [];
}

internal sealed class SevenTvEmoteJsonDto
{
    public string Id { get; set; } = string.Empty;

    // Alias/name as used within this specific emote set (not the emote's global base name).
    public string Name { get; set; } = string.Empty;

    // The payload also carries a `timestamp` per emote, which is deliberately NOT read: it looks
    // like the set entry's added-at but is filled by the v4 compat layer with the emote's *upload*
    // date (proven live 2026-08-03 — 968/968 entries of a large set carried the ULID creation
    // instant of the emote id, while the real added-at from v4 differed by days to over a year).
    public SevenTvEmoteDataJsonDto? Data { get; set; }
}

internal sealed class SevenTvEmoteDataJsonDto
{
    public SevenTvHostJsonDto? Host { get; set; }
}

internal sealed class SevenTvHostJsonDto
{
    public string Url { get; set; } = string.Empty;
    public List<SevenTvFileJsonDto> Files { get; set; } = [];
}

internal sealed class SevenTvFileJsonDto
{
    public string Name { get; set; } = string.Empty;

    // 7TV's own name for the unanimated still of this same file: "4x_static.webp" on an animated
    // emote, and simply a repeat of Name on a still one. Reading it means we never have to decide
    // whether an emote is animated — the payload has already decided. Present on every file of
    // every emote in both wire formats (verified 2026-08-28: 931/931 emotes of the REST set for
    // HandOfBlood, and all 12 files of each captured EventAPI frame under Unit/TestData).
    public string StaticName { get; set; } = string.Empty;
}

// GQL v4 (host-absolute /v4/gql — the shared BaseAddress points at v3):
// emoteSets { emoteSet(id) { emotes(page, perPage) { pageCount items { addedAt emote { id } } } } }
// The only source of the real set-entry added-at; see the comment on SevenTvEmoteJsonDto.
internal sealed class SevenTvGqlSetEntriesResponseDto
{
    public SevenTvGqlSetEntriesDataDto? Data { get; set; }
}

internal sealed class SevenTvGqlSetEntriesDataDto
{
    public SevenTvGqlSetEntriesRootDto? EmoteSets { get; set; }
}

internal sealed class SevenTvGqlSetEntriesRootDto
{
    public SevenTvGqlSetEntriesSetDto? EmoteSet { get; set; }
}

internal sealed class SevenTvGqlSetEntriesSetDto
{
    public SevenTvGqlSetEntriesPageDto? Emotes { get; set; }
}

internal sealed class SevenTvGqlSetEntriesPageDto
{
    public int PageCount { get; set; }
    public List<SevenTvGqlSetEntryDto> Items { get; set; } = [];
}

internal sealed class SevenTvGqlSetEntryDto
{
    public DateTimeOffset? AddedAt { get; set; }
    public SevenTvGqlSetEntryEmoteDto? Emote { get; set; }
}

internal sealed class SevenTvGqlSetEntryEmoteDto
{
    public string Id { get; set; } = string.Empty;
}

// GQL v4 (host-absolute /v4/gql), foreign-channel-import spec: emoteSets { emoteSet(id) {
// emotes(page, perPage) { totalCount pageCount items { alias emote { id defaultName scores {
// topAllTime trendingDay } } } } } — deliberately does NOT request Emote.images: measured live
// 2026-09-09 that including it multiplies the payload roughly 13x (101541 bytes for 45 emotes with
// images vs. 7734 bytes without, both against 7TV's own "global" set). The image url is instead
// built from the emote id directly (SevenTvApiClient.BuildForeignImageUrl) — 7TV's CDN url shape is
// fixed and keyed only by that id, confirmed live against the same measurement.
internal sealed class SevenTvGqlEmoteSetPreviewResponseDto : ISevenTvGqlErrorEnvelope
{
    public SevenTvGqlEmoteSetPreviewDataDto? Data { get; set; }

    // Present alongside `data` (which is then null) on a GraphQL-level failure — the same envelope
    // shape already captured for userByConnection (SevenTvApiClientResolveIdentityTests). 7TV signals
    // overload as HTTP 200 with one of these entries carrying extensions.status == 429; that is the
    // one detail this DTO exists to expose, since every other query on this client infers failure
    // from `data` being empty and never needed to read `errors` itself.
    public List<SevenTvGqlErrorDto>? Errors { get; set; }
}

/// <summary>
/// The shape <c>SevenTvApiClient</c>'s shared v4 page-fetch (<c>FetchV4PageAsync</c>) needs from
/// any v4 GraphQL response DTO it parses: just enough to find a disguised-as-200 rate limit
/// (<c>errors[].extensions.status == 429</c>), the same way both the preview and the leaderboard
/// search responses expose it. Lets that one shared method stay generic over the concrete DTO.
/// </summary>
internal interface ISevenTvGqlErrorEnvelope
{
    List<SevenTvGqlErrorDto>? Errors { get; }
}

internal sealed class SevenTvGqlErrorDto
{
    public SevenTvGqlErrorExtensionsDto? Extensions { get; set; }
}

internal sealed class SevenTvGqlErrorExtensionsDto
{
    public int? Status { get; set; }

    /// <summary>
    /// Header-shaped extras some GraphQL error payloads carry alongside the status. Read
    /// opportunistically for an <c>x-ratelimit-…-reset</c> hint
    /// (<c>SevenTvApiClient.ReadResetHintSeconds</c>) and ignored entirely when absent, which is the
    /// only shape we have ever actually captured.
    /// </summary>
    /// <remarks>
    /// <b>Unverified by design.</b> A Codex review claimed 7TV reports the reset time of its
    /// semantic HTTP-200/429 answers here. We could neither confirm nor refute it: the only payload
    /// of this shape on file is a 404 with no <c>headers</c> member, 7TV publishes no schema for its
    /// error extensions, and two web searches (2026-09-09) turned up nothing about their shape —
    /// while provoking a real 429 to find out costs roughly an hour of IP lockout, which is the exact
    /// thing this feature's hardening exists to avoid. So the argument is made moot rather than
    /// settled: the field is read if it happens to be there, and everything downstream falls back to
    /// <c>Retry-After</c> and then to the breaker's 60 s default exactly as before. A
    /// <see cref="JsonElement"/> value rather than a string because an unverified payload's numbers
    /// may be JSON numbers or quoted strings, and neither should throw.
    /// </remarks>
    public Dictionary<string, JsonElement>? Headers { get; set; }
}

internal sealed class SevenTvGqlEmoteSetPreviewDataDto
{
    public SevenTvGqlEmoteSetPreviewRootDto? EmoteSets { get; set; }
}

internal sealed class SevenTvGqlEmoteSetPreviewRootDto
{
    public SevenTvGqlEmoteSetPreviewSetDto? EmoteSet { get; set; }
}

internal sealed class SevenTvGqlEmoteSetPreviewSetDto
{
    // Added spec 2026-09-20 (F6/6.4): the set's own name and slot capacity, read once per lookup
    // alongside the paginated entries rather than in a separate request. 7TV returns the same values
    // on every page of the same query — capturing them from whichever page happens to be classified
    // successfully first (SevenTvApiClient.GetEmoteSetPreviewAsync) is correct either way.
    public string? Name { get; set; }
    public int Capacity { get; set; }
    public SevenTvGqlEmoteSetPreviewPageDto? Emotes { get; set; }
}

internal sealed class SevenTvGqlEmoteSetPreviewPageDto
{
    // What 7TV reports the set holds in total — can exceed Items.Count on the last page fetched; see
    // MaxSetEntryPages and the truncated handling in SevenTvApiClient.GetEmoteSetPreviewAsync.
    public int TotalCount { get; set; }
    public int PageCount { get; set; }
    public List<SevenTvGqlEmoteSetPreviewItemDto> Items { get; set; } = [];
}

internal sealed class SevenTvGqlEmoteSetPreviewItemDto
{
    // The name as used within this specific set — not the emote's global default name, which sits
    // one level down on the embedded Emote (issue #37-style: two different "name" fields with two
    // different meanings, kept apart on purpose).
    public string Alias { get; set; } = string.Empty;
    public SevenTvGqlEmoteSetPreviewEmoteDto? Emote { get; set; }
}

internal sealed class SevenTvGqlEmoteSetPreviewEmoteDto
{
    public string Id { get; set; } = string.Empty;
    public string DefaultName { get; set; } = string.Empty;
    public SevenTvGqlEmoteSetPreviewFlagsDto? Flags { get; set; }
    public SevenTvGqlEmoteSetPreviewScoresDto? Scores { get; set; }
}

// Only the one flag the image url depends on — EmoteFlags carries six more (publicListed, private,
// nsfw, defaultZeroWidth, approvedPersonal, deniedPersonal) that nothing on this path reads.
// See SevenTvApiClient.BuildForeignImageUrl for why this one is worth its ~26 bytes an emote.
internal sealed class SevenTvGqlEmoteSetPreviewFlagsDto
{
    public bool Animated { get; set; }
}

// Only the two fields the response contract (spec section 4) exposes — EmoteScores carries five more
// (trendingWeek/Month, topDaily/Weekly/Monthly) that nothing on this path reads.
internal sealed class SevenTvGqlEmoteSetPreviewScoresDto
{
    public int TopAllTime { get; set; }
    public int TrendingDay { get; set; }
}

// GQL v4 (host-absolute /v4/gql), the leaderboard import source (spec 2026-09-13, F7): emotes {
// search(sort, page, perPage) { totalCount pageCount items { id defaultName flags { animated }
// scores { topAllTime trendingDay } } } } — deliberately without query/filters/tags (E5) and
// without defaultZeroWidth (E4). Reuses SevenTvGqlEmoteSetPreviewFlagsDto/ScoresDto: EmoteQuery.
// search returns flat Emote objects, and both fields have the exact same JSON shape there as on
// the preview's embedded Emote — only the wrapping item (no per-set alias here, see F7) differs.
internal sealed class SevenTvGqlLeaderboardSearchResponseDto : ISevenTvGqlErrorEnvelope
{
    public SevenTvGqlLeaderboardSearchDataDto? Data { get; set; }

    // Same envelope 7TV uses to disguise an overload as HTTP 200 for this query too — see the
    // comment on SevenTvGqlEmoteSetPreviewResponseDto.Errors.
    public List<SevenTvGqlErrorDto>? Errors { get; set; }
}

internal sealed class SevenTvGqlLeaderboardSearchDataDto
{
    public SevenTvGqlEmoteQueryDto? Emotes { get; set; }
}

internal sealed class SevenTvGqlEmoteQueryDto
{
    public SevenTvGqlLeaderboardSearchPageDto? Search { get; set; }
}

internal sealed class SevenTvGqlLeaderboardSearchPageDto
{
    public int TotalCount { get; set; }
    public int PageCount { get; set; }
    public List<SevenTvGqlLeaderboardSearchItemDto> Items { get; set; } = [];
}

internal sealed class SevenTvGqlLeaderboardSearchItemDto
{
    public string Id { get; set; } = string.Empty;

    // The emote's global base name — a flat Emote object from EmoteQuery.search carries no
    // per-set alias (F7), unlike SevenTvGqlEmoteSetPreviewItemDto.Alias.
    public string DefaultName { get; set; } = string.Empty;
    public SevenTvGqlEmoteSetPreviewFlagsDto? Flags { get; set; }
    public SevenTvGqlEmoteSetPreviewScoresDto? Scores { get; set; }
}

// GQL v4 (host-absolute /v4/gql), the emote-set list of one account (spec 2026-09-20, E7, Sonde 7).
// Deliberately NOT aliased to snake_case the way the older v4 queries in this client are: the query
// text is the one that was measured live on 2026-09-20, character for character, and F17 makes that
// worth protecting — a query we adjusted for our own deserializer's convenience is a query nobody
// ever ran against 7TV. The camelCase members therefore carry [JsonPropertyName] instead, which the
// shared SnakeCaseLower options honour.
internal sealed class SevenTvGqlEmoteSetListResponseDto : ISevenTvGqlErrorEnvelope
{
    public SevenTvGqlEmoteSetListDataDto? Data { get; set; }

    public List<SevenTvGqlErrorDto>? Errors { get; set; }
}

internal sealed class SevenTvGqlEmoteSetListDataDto
{
    public SevenTvGqlEmoteSetListUsersDto? Users { get; set; }
}

internal sealed class SevenTvGqlEmoteSetListUsersDto
{
    // Null at HTTP 200 without an errors block means "7TV carries no account for this Twitch
    // connection" — the one case that is an answer rather than a failure (measured 2026-09-20).
    [JsonPropertyName("userByConnection")]
    public SevenTvGqlEmoteSetListUserDto? UserByConnection { get; set; }
}

internal sealed class SevenTvGqlEmoteSetListUserDto
{
    public string Id { get; set; } = string.Empty;

    public SevenTvGqlEmoteSetListStyleDto? Style { get; set; }

    // Absent (rather than empty) means we could not read the list, not that the account has no
    // sets — the reason this is nullable and the mapper tells the two apart.
    [JsonPropertyName("emoteSets")]
    public List<SevenTvGqlEmoteSetListSetDto>? EmoteSets { get; set; }
}

internal sealed class SevenTvGqlEmoteSetListStyleDto
{
    [JsonPropertyName("activeEmoteSetId")]
    public string? ActiveEmoteSetId { get; set; }
}

internal sealed class SevenTvGqlEmoteSetListSetDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Capacity { get; set; }

    // 7TV's EmoteSetKind, passed through as the string it is — see EmoteSetSummary.Kind for why it
    // is never parsed into an enum of ours.
    public string Kind { get; set; } = string.Empty;

    public SevenTvGqlEmoteSetListOwnerDto? Owner { get; set; }
}

internal sealed class SevenTvGqlEmoteSetListOwnerDto
{
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mainConnection")]
    public SevenTvGqlEmoteSetListConnectionDto? MainConnection { get; set; }
}

internal sealed class SevenTvGqlEmoteSetListConnectionDto
{
    [JsonPropertyName("platformDisplayName")]
    public string? PlatformDisplayName { get; set; }
}
