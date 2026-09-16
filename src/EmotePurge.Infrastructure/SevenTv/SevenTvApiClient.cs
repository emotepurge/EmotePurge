using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.SevenTv;

public class SevenTvApiClient(
    HttpClient httpClient,
    IRateLimitTelemetry telemetry,
    IForeignUpstreamRequestBudget foreignRequestBudget,
    ILogger<SevenTvApiClient> logger) : ISevenTvApiClient
{
    // 7TV's own spelling of the Twitch platform on a Connection object — matched against exactly,
    // never derived, so the five call sites that filter on it cannot drift apart.
    private const string TwitchPlatform = "TWITCH";

    private const string GqlUsersQuery =
        "query($q: String!) { users(query: $q) { id username connections { platform username id } } }";

    // Aliased to snake_case (`user_by_connection`/`emote_set`) so the shared SnakeCaseLower
    // JsonSerializerOptions (see SevenTvEmoteJsonMapper) can match them from PascalCase DTO
    // properties without per-property [JsonPropertyName] attributes — 7TV's schema names these two
    // particular query-root fields in camelCase, unlike every other field this client already reads.
    private const string GqlUserByConnectionQuery =
        "query($p: ConnectionPlatform!, $id: String!) { user_by_connection: userByConnection(platform: $p, id: $id) { id connections { platform id emote_set_id } } }";

    private const string GqlEmoteSetOwnerQuery =
        "query($id: ObjectID!) { emote_set: emoteSet(id: $id) { owner_id } }";

    // v4 schema, not the v3 the BaseAddress points at — the real set-entry added-at only exists
    // there (EmoteSetEmote.addedAt); the v3 payload's timestamp is the emote's upload date. Same
    // snake_case aliasing trick as above, since v4 names everything in camelCase.
    private const string GqlSetEntriesQuery =
        "query($id: Id!, $page: Int!, $perPage: Int!) { emote_sets: emoteSets { emote_set: emoteSet(id: $id) { emotes(page: $page, perPage: $perPage) { page_count: pageCount items { added_at: addedAt emote { id } } } } } }";

    // Host-absolute so it escapes the /v3/ BaseAddress.
    private const string V4GqlPath = "/v4/gql";

    // 500 per page keeps even subscriber-sized sets (capacity can exceed 1000) at a handful of
    // requests; the page cap is a runaway guard, not an expected limit.
    private const int SetEntriesPerPage = 500;
    private const int MaxSetEntryPages = 10;

    // Ceiling for a reset hint, whether read out of a GraphQL error payload's extensions.headers
    // (ReadResetHintSeconds) or out of the HTTP x-ratelimit-search-reset header directly
    // (ReadSearchResetHeaderSeconds) — both feed a circuit breaker's open duration. Six hours is
    // comfortably above the ~1 h search-bucket lockout measured live and far below anything that
    // could be a Unix timestamp, so an unexpected unit is dropped instead of silently keeping the
    // circuit breaker shut for years.
    private const int MaxResetHintSeconds = 6 * 60 * 60;

    private const string GqlEditorOfQuery =
        "query($id: ObjectID!) { user(id: $id) { editor_of { user { connections { platform id username } } } } }";

    // v4 schema, foreign-channel-import spec (F1 step 3): a set-agnostic, paginated preview of an
    // arbitrary emote set's entries, including the set-local alias, the emote's global default name,
    // and its network-wide scores — all three land in this one request per page (measured live
    // 2026-09-09: 7734 bytes for a 45-emote set, extrapolating to ~164 KB for a 956-emote set, close
    // to the 174 547 bytes the design doc measured for HandOfBlood's set with a near-identical
    // shape). Deliberately omits Emote.images — see the comment on SevenTvGqlEmoteSetPreviewResponseDto
    // for why, and BuildForeignImageUrl for how the image url is built instead.
    private const string GqlEmoteSetPreviewQuery =
        "query($id: Id!, $page: Int!, $perPage: Int!) { emote_sets: emoteSets { emote_set: emoteSet(id: $id) { emotes(page: $page, perPage: $perPage) { total_count: totalCount page_count: pageCount items { alias emote { id default_name: defaultName flags { animated } scores { top_all_time: topAllTime trending_day: trendingDay } } } } } } }";

    // v4 schema, the leaderboard import source (spec 2026-09-13, F7): EmoteQuery.search — a
    // network-wide ranking, not a set's contents, so this takes a sort instead of a set id and
    // returns flat Emote objects (no per-set alias). Deliberately no query/filters/tags (E5) and
    // no defaultZeroWidth (E4).
    //
    // $sort: Sort! is an INPUT_OBJECT, not an enum — introspected live 2026-09-14: `Sort {
    // sortBy: SortBy!, order: SortOrder! }`. An earlier revision of this client sent a bare string
    // for $sort, reconstructed from two documents that were each individually correct and jointly
    // misleading (one showed the SDL argument `sort: Sort!`, the other listed SortBy's members
    // under the shorthand "SortBy = …"); 7TV answered every such request with a coercion error —
    // HTTP 200, errors[] without extensions.status: 429 — which this client reads as Unavailable,
    // so the symptom was a permanently unavailable leaderboard, not an obviously wrong shape. See
    // BuildSortVariable for the corrected object and spec section 4 for the introspection result.
    private const string GqlLeaderboardSearchQuery =
        "query($sort: Sort!, $page: Int!, $perPage: Int!) { emotes { search(sort: $sort, page: $page, perPage: $perPage) { total_count: totalCount page_count: pageCount items { id default_name: defaultName flags { animated } scores { top_all_time: topAllTime trending_day: trendingDay } } } } }";

    // SortOrder's other member (ASCENDING) would rank the leaderboard from the bottom — never what
    // this import source wants. A named constant rather than an inline literal so the choice reads
    // as deliberate, not as a typo candidate next to sortBy's own wire code.
    private const string SearchSortOrderDescending = "DESCENDING";

    // 7TV's own hard ceiling (E5, measured live): 251+ answers "Failed to parse "Int": the value is
    // …, must be less than or equal to 250" — a validation rejection, not a rate limit, and one that
    // costs no bucket charge (Sonde 2, 2026-09-13). Fixed here, never taken from the caller.
    private const int SearchPerPage = 250;

    // 7TV's own rate-limit headers for the search bucket specifically (Sonde 1, 2026-09-13) —
    // distinct from Twitch's Ratelimit-* spelling FetchV4PageAsync's preview caller reads, and
    // distinct from the GraphQL-payload reset hint ReadResetHintSeconds reads. Read only for
    // RateLimitCallSources.SevenTvLeaderboard (UsesSearchRateLimitHeaders) — never for the preview
    // path, which this same header would otherwise silently start affecting (spec 2026-09-13, the
    // characterization test HttpSearchResetHeader_HasNoEffect_OnRetryAfterOrTheObservation pins
    // exactly this).
    private const string SearchRateLimitHeader = "x-ratelimit-search-limit";
    private const string SearchRateLimitRemainingHeader = "x-ratelimit-search-remaining";
    private const string SearchRateLimitResetHeader = "x-ratelimit-search-reset";

    // Latches the fallback-set-load path (issue #43) from Information down to Debug after its first
    // occurrence in this process. Once 7TV finishes rolling out the null embedded emote_set, this
    // fallback becomes the permanent path for every channel on every 60s resync tick — an
    // Information line there forever would be exactly the per-tick log spam NoActiveEmoteSet's Debug
    // level (below) already exists to avoid. One Information line still marks the rollout as visibly
    // arrived; every later tick logs Debug like the rest of this method's steady-state paths.
    private static int _fallbackEmoteSetPathLoggedOnce;

    public async Task<SevenTvTwitchUserIdResult> ResolveTwitchUserIdAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var normalized = ChannelName.Normalize(channelName);

        try
        {
            var payload = new { query = GqlUsersQuery, variables = new { q = normalized } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlUsersResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // GraphQL errors surface as HTTP 200 with `data: null` (or `users` missing inside it)
            // plus an `errors` array — that's a failed query, not evidence the account is missing.
            // Only a successfully returned, genuinely empty `users` list means "no match".
            if (dto?.Data?.Users is null)
            {
                logger.LogWarning(
                    "7TV-Nutzersuche für {Channel} lieferte keine verwertbaren Daten (GraphQL-Fehlerantwort?).",
                    normalized);
                return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            var match = dto.Data.Users
                .SelectMany(u => u.Connections)
                .FirstOrDefault(c => c.Platform == TwitchPlatform &&
                    string.Equals(c.Username, normalized, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                // Debug for the same reason as the missing emote set below: only SevenTvSyncService
                // calls this, the periodic resync calls it again every 60 seconds for as long as the
                // channel has no 7TV account, and the line that states the finding is the throttled
                // one there. Measured: this one repeated every tick while that one stayed silent.
                logger.LogDebug("Kein 7TV-Twitch-Match für {Channel}.", normalized);
                return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            return SevenTvTwitchUserIdResult.Ok(match.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "7TV-Nutzersuche für {Channel} fehlgeschlagen, wird übersprungen.", normalized);
            return SevenTvTwitchUserIdResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }

    public async Task<SevenTvChannelStateResult> GetChannelStateForTwitchUserAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"users/twitch/{twitchUserId}", cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("Kein 7TV-Account für Twitch-ID {Id}.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvUserRestDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // A literal JSON `null` body lands here as dto == null; a genuinely empty or malformed
            // body never reaches this line at all — ReadFromJsonAsync throws JsonException first,
            // caught below as Unavailable. Either way this is a broken answer, not a statement about
            // the account, so it must not read as "no emote set" and send the owner off to fix
            // something that is fine.
            if (dto is null)
            {
                logger.LogWarning("7TV-Antwort für Twitch-ID {Id} war leer.", twitchUserId);
                return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            var emoteSetDto = dto.EmoteSet;
            if (emoteSetDto is null)
            {
                // Issue #43: 7TV announced (no date) that it will null this embedded object out of
                // the response. Measured live 2026-09-01: this top-level one is still populated,
                // but the same object one level down (user.connections[].emote_set) is already
                // null for every checked channel while the plain-id fields stay filled — the
                // rollout has started, it just has not reached this field yet. Resolve a usable set
                // id from whatever the response still offers and reload the set separately rather
                // than presenting every channel with a false "no active emote set".
                var fallbackEmoteSetId = ResolveFallbackEmoteSetId(dto, twitchUserId);

                // The state behind issue #32, and the only one of the four that used to return
                // silently: the account exists, but no emote set is active on the Twitch connection.
                // Debug rather than Information because this runs on every resync tick: the periodic
                // worker asks again every 60 seconds for as long as the channel stays like this,
                // which is ~1440 lines a day per affected channel. The line that carries the finding
                // is the one in SevenTvSyncService, which knows the channel name and only speaks when
                // the reason changes; this one adds the Twitch id and is worth the log level it costs
                // only while someone is actually debugging a lookup.
                if (fallbackEmoteSetId is null)
                {
                    logger.LogDebug(
                        "7TV-Account für Twitch-ID {Id} hat kein aktives Emote-Set.", twitchUserId);
                    return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.NoActiveEmoteSet);
                }

                LogFallbackEmoteSetPathEntered(twitchUserId);

                emoteSetDto = await FetchEmoteSetAsync(fallbackEmoteSetId, cancellationToken);
                if (emoteSetDto is null)
                {
                    // A non-success status (EnsureSuccessStatusCode, incl. 404) or a malformed body
                    // already threw and landed in the catch below as Unavailable; only a literal
                    // JSON `null` reload body reaches this branch, and it means the same thing: a
                    // broken reload must not read as "no emote set" (see the null-body guard for the
                    // primary response above).
                    logger.LogWarning(
                        "7TV-Emote-Set-Nachladen für Set {SetId} (Twitch-ID {TwitchId}) war leer.",
                        fallbackEmoteSetId, twitchUserId);
                    return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
                }
            }

            return await BuildChannelStateResultAsync(emoteSetDto, dto.User, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "7TV-Emote-Set-Abruf für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return SevenTvChannelStateResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }

    public async Task<SevenTvIdentityResult> ResolveSevenTvIdentityAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlUserByConnectionQuery, variables = new { p = TwitchPlatform, id = twitchUserId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlUserByConnectionResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // A null user here is a broken GraphQL response (`data: null` plus an `errors`
            // array, or `userByConnection` missing from an otherwise-parseable body), not
            // evidence the account is missing: 7TV never answers a Twitch id with no linked
            // account with a literal null — it returns HTTP 200 with a placeholder user
            // instead, which the connection check right below already catches and maps to
            // NoSevenTvAccount. That placeholder covers the "no account" case completely, so
            // nothing is left for a null user to mean except a failed lookup. Same distinction
            // ResolveTwitchUserIdAsync already draws for its own GraphQL response above; this
            // brings this method in line with it rather than introducing anything new.
            var user = dto?.Data?.UserByConnection;
            if (user is null)
            {
                logger.LogWarning(
                    "7TV-Identitätsauflösung für Twitch-ID {Id} lieferte keine verwertbaren Daten (GraphQL-Fehlerantwort?).",
                    twitchUserId);
                return SevenTvIdentityResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            // 7TV never returns a literal null for a Twitch id with no linked account: it answers
            // HTTP 200 with a placeholder user instead (measured live 2026-08-31, id
            // "00000000000000000000000000", `connections: []`). 7TV finds the account BY this
            // Twitch connection, so the connection's presence in the answer — not the account id —
            // is the only thing that tells a real match apart from the placeholder; this also
            // survives 7TV changing the sentinel value itself. `EmoteSetId` on that connection may
            // still be null (account exists, no active set), which is a legitimate Ok — only the
            // connection itself being absent means NoSevenTvAccount.
            var twitchConnection = user.Connections
                .FirstOrDefault(c => c.Platform == TwitchPlatform && c.Id == twitchUserId);
            if (twitchConnection is null)
            {
                logger.LogInformation("Kein 7TV-Account für Twitch-ID {Id}.", twitchUserId);
                return SevenTvIdentityResult.Failed(SevenTvLookupStatus.NoSevenTvAccount);
            }

            return SevenTvIdentityResult.Ok(new SevenTvIdentity(user.Id, twitchConnection.EmoteSetId));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Identitätsauflösung für Twitch-ID {Id} fehlgeschlagen, wird übersprungen.", twitchUserId);
            return SevenTvIdentityResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }

    public async Task<string?> GetEmoteSetOwnerIdAsync(string emoteSetId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlEmoteSetOwnerQuery, variables = new { id = emoteSetId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlEmoteSetOwnerResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            var ownerId = dto?.Data?.EmoteSet?.OwnerId;
            if (ownerId is null)
            {
                logger.LogInformation("Kein Owner für 7TV-Set {SetId} auflösbar.", emoteSetId);
            }

            return ownerId;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Set-Owner-Abruf für Set {SetId} fehlgeschlagen, wird übersprungen.", emoteSetId);
            return null;
        }
    }

    public async Task<SevenTvEditorGrantsResult> GetEditorOfChannelsAsync(string sevenTvUserId, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = new { query = GqlEditorOfQuery, variables = new { id = sevenTvUserId } };
            var response = await httpClient.PostAsJsonAsync("gql", payload, cancellationToken);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlEditorOfResponseDto>(
                SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

            // EditorOf on the DTO defaults to an empty list and 7TV's GraphQL schema returns list
            // fields as `[]`, never `null`, for "no entries" — so a genuinely editor-of-nothing
            // account already lands here as an empty (non-null) list, not this branch. Reaching here
            // means the response itself is unusable: a GraphQL error (`data: null`) or `user` coming
            // back null for an id this same call chain already resolved as valid moments earlier.
            // That premise only holds since ResolveSevenTvIdentityAsync stopped treating 7TV's
            // placeholder user (measured live 2026-08-31: HTTP 200, id
            // "00000000000000000000000000", `connections: []`) as a real match — before that fix,
            // this method could receive that placeholder id and its 404 `LOAD_ERROR user not found`
            // landed here too, indistinguishable from a genuinely broken response. Now only an id
            // 7TV already confirmed via a real Twitch connection ever reaches this call, so `user:
            // null` here is a broken lookup (Unavailable), never "no account".
            var grants = dto?.Data?.User?.EditorOf;
            if (grants is null)
            {
                logger.LogWarning(
                    "7TV-Editor-Abfrage für 7TV-User {Id} lieferte keine verwertbaren Daten (GraphQL-Fehlerantwort?).",
                    sevenTvUserId);
                return SevenTvEditorGrantsResult.Failed(SevenTvLookupStatus.Unavailable);
            }

            var result = grants
                .SelectMany(g => g.User?.Connections ?? [])
                .Where(c => c.Platform == TwitchPlatform)
                .Select(c => new SevenTvEditorGrant(c.Username, c.Id))
                .ToList();
            return SevenTvEditorGrantsResult.Ok(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "7TV-Editor-Abfrage für 7TV-User {Id} fehlgeschlagen, wird übersprungen.", sevenTvUserId);
            return SevenTvEditorGrantsResult.Failed(SevenTvLookupStatus.Unavailable);
        }
    }

    public async Task<SevenTvEmoteSetPreviewResult> GetEmoteSetPreviewAsync(string emoteSetId, CancellationToken cancellationToken = default)
    {
        try
        {
            var items = new List<SevenTvEmoteSetPreviewItem>();
            var totalCount = 0;

            for (var page = 1; page <= MaxSetEntryPages; page++)
            {
                var fetch = await FetchClassifiedPreviewPageAsync(emoteSetId, page, cancellationToken);
                if (fetch.Failure is { } failure)
                {
                    return failure;
                }

                var pageDto = fetch.PageDto!;
                totalCount = pageDto.TotalCount;
                items.AddRange(pageDto.Items.Select(MapPreviewItem));

                if (page >= pageDto.PageCount)
                {
                    break;
                }

                // F3: the guard is a runaway stop, not an expected limit (see the comment on
                // MaxSetEntryPages) — but unlike the addedAt overlay this reads back to a user as a
                // preview list, and a silently short one is exactly what the spec forbids. Truncated
                // is derived once more, robustly, right after the loop from items.Count < totalCount;
                // this log line exists only to say *why*, while the reason is still known.
                if (page == MaxSetEntryPages)
                {
                    logger.LogWarning(
                        "7TV-Set {SetId} überschreitet die Seitendecke ({MaxPages} Seiten à {PerPage}) — Vorschau wird als unvollständig (truncated) markiert, angesagte Gesamtzahl {TotalCount}.",
                        emoteSetId, MaxSetEntryPages, SetEntriesPerPage, totalCount);
                }
            }

            var truncated = items.Count < totalCount;
            return SevenTvEmoteSetPreviewResult.Ok(new SevenTvEmoteSetPreview(totalCount, truncated, items));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "7TV-Vorschau-Abruf für Set {SetId} fehlgeschlagen, wird übersprungen.", emoteSetId);
            return SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.Unavailable);
        }
    }

    public async Task<SevenTvEmoteSearchPageResult> SearchEmotesAsync(
        SevenTvLeaderboardSort sortBy, int page, CancellationToken cancellationToken = default)
    {
        // No log line here by design (spec 2026-09-13, T1, operator decision 2026-09-14): the one
        // line per upstream request that matters — sortBy, page, outcome, remaining, reset,
        // usedInWindow — belongs to SevenTvLeaderboardService, which knows the budget window this
        // client does not. A second line here would make that count ambiguous (AK 31). Neither
        // branch below logs: a transport or parse failure still reaches the service as a plain
        // Unavailable, which is all it needs for its one line.
        try
        {
            var payload = new
            {
                query = GqlLeaderboardSearchQuery,
                variables = new { sort = BuildSortVariable(sortBy), page, perPage = SearchPerPage }
            };

            var result = await FetchV4PageAsync<SevenTvGqlLeaderboardSearchResponseDto>(
                payload, RateLimitCallSources.SevenTvLeaderboard, cancellationToken);

            if (result.Status == V4PageStatus.RateLimited)
            {
                return SevenTvEmoteSearchPageResult.Failed(
                    SevenTvEmoteSearchLookupStatus.RateLimited,
                    result.HeaderSample?.Limit,
                    result.HeaderSample?.Remaining,
                    result.HeaderSample?.Reset,
                    result.RetryAfter);
            }

            // Covers both the generic-failure branch of FetchV4PageAsync (5xx, e.g.) and a
            // validation rejection (Data present, but Search null — 7TV answers those as HTTP 200
            // with an errors[] that never carries extensions.status: 429, so IsRateLimited above
            // already ruled that branch out and this is the only place left to catch it).
            var searchDto = result.Dto?.Data?.Emotes?.Search;
            if (result.Status == V4PageStatus.Unavailable || searchDto is null)
            {
                return SevenTvEmoteSearchPageResult.Failed(
                    SevenTvEmoteSearchLookupStatus.Unavailable,
                    result.HeaderSample?.Limit,
                    result.HeaderSample?.Remaining,
                    result.HeaderSample?.Reset);
            }

            var items = searchDto.Items.Select(MapSearchItem).ToList();
            return SevenTvEmoteSearchPageResult.Ok(
                new SevenTvEmoteSearchPage(searchDto.TotalCount, searchDto.PageCount, items),
                result.HeaderSample?.Limit,
                result.HeaderSample?.Remaining,
                result.HeaderSample?.Reset);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Reached only when SendAsync itself throws (connection/DNS/TLS failure, or a timeout/
            // cancellation while it buffers the body) — no response exists to read headers from.
            // JsonException stays in the guard defensively; FetchV4PageAsync handles a parse failure.
            return SevenTvEmoteSearchPageResult.Failed(SevenTvEmoteSearchLookupStatus.Unavailable, null, null, null);
        }
    }

    // One loop iteration of GetEmoteSetPreviewAsync, split out along its own business seam: obtain
    // one preview page, or determine — and already log — why it cannot be obtained (exhausted
    // budget, a confirmed 429 in either of its two disguises, or an unparseable/GraphQL-error
    // response). Charges one budget permit per page rather than once per resolution (E5b): a single
    // preview walks up to ten pages, and a permit charged per *resolution* would license ten times
    // the documented rate. The permit is taken before the request is built, so a refusal really does
    // mean "no request was made".
    private async Task<PreviewPageFetch> FetchClassifiedPreviewPageAsync(
        string emoteSetId, int page, CancellationToken cancellationToken)
    {
        if (!await foreignRequestBudget.TryChargeRequestAsync(cancellationToken))
        {
            logger.LogWarning(
                "Providerweites 7TV-Budget erschöpft — Vorschau-Abruf für Set {SetId} bei Seite {Page} abgebrochen.",
                emoteSetId, page);
            return PreviewPageFetch.Failed(SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.BudgetExhausted));
        }

        var pageResult = await FetchPreviewPageAsync(emoteSetId, page, cancellationToken);

        // The single most expensive mistake in the whole spec (section 5/AK7): 7TV answers an
        // overload — HTTP 429 outright, or HTTP 200 with a GraphQL error carrying
        // extensions.status == 429 — which without this check is indistinguishable from a set that
        // genuinely has zero entries. The second is explicitly not an error (see the state table), so
        // both forms of a confirmed 429 are checked before the "no usable data" branch below, not
        // folded into it. FetchPreviewPageAsync tells the two forms apart itself; from here on they
        // are one outcome.
        if (pageResult.Status == V4PageStatus.RateLimited)
        {
            logger.LogWarning(
                "7TV meldet Überlast (429) beim Vorschau-Abruf für Set {SetId}, Seite {Page}.",
                emoteSetId, page);
            return PreviewPageFetch.Failed(
                SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.RateLimited, pageResult.RetryAfter));
        }

        var pageDto = pageResult.Dto?.Data?.EmoteSets?.EmoteSet?.Emotes;
        if (pageResult.Status == V4PageStatus.Unavailable || pageDto is null)
        {
            // A parse failure says so and carries the exception; everything else keeps the GraphQL hint.
            if (pageResult.ParseException is { } parseException)
            {
                logger.LogWarning(parseException,
                    "7TV preview fetch for set {SetId} returned an unparseable response body, page {Page}.",
                    emoteSetId, page);
            }
            else
            {
                logger.LogWarning(
                    "7TV-Vorschau-Abruf für Set {SetId} lieferte keine verwertbaren Daten (GraphQL-Fehlerantwort?), Seite {Page}.",
                    emoteSetId, page);
            }

            return PreviewPageFetch.Failed(SevenTvEmoteSetPreviewResult.Failed(SevenTvPreviewLookupStatus.Unavailable));
        }

        return PreviewPageFetch.Success(pageDto);
    }

    // The shared v4 GraphQL page-fetch behind both FetchPreviewPageAsync and SearchEmotesAsync
    // (spec 2026-09-13, E11/F3) — one HTTP request, one telemetry observation, the unit the
    // telemetry vertrag (spec section 6) is written against. ProviderRequestTelemetryHandler is
    // silenced for this request (ProviderTelemetrySuppression.OptionsKey) because it only ever sees
    // the raw HTTP status: a 7TV overload disguised as HTTP 200 with extensions.status 429 would
    // land in its count as a plain success. This method reports itself instead, after parsing far
    // enough to know the real, semantic outcome, under the caller's own call source —
    // RecordForeignPreviewObservation takes that call source as a parameter rather than assuming
    // SevenTvForeignPreview, precisely so this one fetch can serve more than one caller without
    // either one's telemetry landing under the other's name.
    //
    // The one place this method itself branches on which caller it is serving: whether to read
    // 7TV's search-bucket headers at all. That branch hangs on callSource
    // (UsesSearchRateLimitHeaders), not on any structural difference between the two callers'
    // requests or responses — the preview path must keep reading none of this header (Task 2's
    // characterization test pins exactly that), and the only thing that tells the two callers apart
    // here is which call source they passed in.
    private async Task<V4PageResult<TDto>> FetchV4PageAsync<TDto>(
        object payload, string callSource, CancellationToken cancellationToken)
        where TDto : class, ISevenTvGqlErrorEnvelope
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, V4GqlPath) { Content = JsonContent.Create(payload) };
        request.Options.Set(ProviderTelemetrySuppression.OptionsKey, true);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var retryAfterSeconds = ProviderRequestTelemetryHandler.ReadRetryAfterSeconds(response);
        var searchResetHeaderSeconds = UsesSearchRateLimitHeaders(callSource)
            ? ReadSearchResetHeaderSeconds(response)
            : null;
        var headerSample = BuildSearchHeaderSample(response, callSource);

        // A literal HTTP 429 — distinct from the disguised-as-200 form checked below, and previously
        // indistinguishable from it: EnsureSuccessStatusCode() used to throw here and fall into the
        // generic catch below as a plain Unavailable, losing exactly the distinction E4 needs to open
        // the breaker immediately instead of counting toward its five-failure threshold.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var effectiveRetryAfterSeconds = searchResetHeaderSeconds ?? retryAfterSeconds;
            RecordForeignPreviewObservation(response, callSource, statusOverride: 429, effectiveRetryAfterSeconds);
            return new V4PageResult<TDto>(V4PageStatus.RateLimited, null, ToRetryAfter(effectiveRetryAfterSeconds), headerSample);
        }

        if (!response.IsSuccessStatusCode)
        {
            RecordForeignPreviewObservation(response, callSource, statusOverride: null, retryAfterSeconds);
            return new V4PageResult<TDto>(V4PageStatus.Unavailable, null, null, headerSample);
        }

        TDto? dto;
        try
        {
            dto = await response.Content.ReadFromJsonAsync<TDto>(SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            // HTTP 200 did answer (that's what gets counted) and the headers already exist, so
            // return Unavailable with the sample instead of rethrowing into a catch with no response to read;
            // the exception rides along for callers that log it.
            RecordForeignPreviewObservation(response, callSource, statusOverride: null, retryAfterSeconds);
            return new V4PageResult<TDto>(V4PageStatus.Unavailable, null, null, headerSample, ex);
        }

        if (IsRateLimited(dto?.Errors))
        {
            // Reset hint order (F3): the HTTP search-reset header first (only ever present for the
            // leaderboard call source — see UsesSearchRateLimitHeaders), then the GraphQL payload's
            // own hint, then Retry-After, the breaker's own 60 s default last (E4). The GraphQL hint
            // is read opportunistically — see SevenTvGqlErrorExtensionsDto.Headers for why its
            // existence is neither assumed nor denied — so on the preview path, where
            // searchResetHeaderSeconds is always null, this behaves exactly as it did before:
            // ReadResetHintSeconds(...) ?? retryAfterSeconds, or nothing.
            var effectiveRetryAfterSeconds = searchResetHeaderSeconds ?? ReadResetHintSeconds(dto!.Errors) ?? retryAfterSeconds;
            RecordForeignPreviewObservation(response, callSource, statusOverride: 429, effectiveRetryAfterSeconds);
            return new V4PageResult<TDto>(V4PageStatus.RateLimited, null, ToRetryAfter(effectiveRetryAfterSeconds), headerSample);
        }

        RecordForeignPreviewObservation(response, callSource, statusOverride: null, retryAfterSeconds);
        return new V4PageResult<TDto>(V4PageStatus.Ok, dto, null, headerSample);
    }

    private Task<V4PageResult<SevenTvGqlEmoteSetPreviewResponseDto>> FetchPreviewPageAsync(
        string emoteSetId, int page, CancellationToken cancellationToken)
    {
        var payload = new
        {
            query = GqlEmoteSetPreviewQuery,
            variables = new { id = emoteSetId, page, perPage = SetEntriesPerPage }
        };

        return FetchV4PageAsync<SevenTvGqlEmoteSetPreviewResponseDto>(
            payload, RateLimitCallSources.SevenTvForeignPreview, cancellationToken);
    }

    // The one predicate FetchV4PageAsync hangs its search-header behaviour on (spec 2026-09-13,
    // Task 3 dispatch note "die engste Stelle des ganzen Tasks"): true only for the leaderboard
    // call source. Everything that must NOT affect the preview path — the HTTP
    // x-ratelimit-search-reset header's influence on RetryAfter, and its appearance in the
    // telemetry observation and in SevenTvEmoteSearchPageResult — is conditioned on this one
    // check, so the distinction lives at the call source, not in some incidental difference
    // between the two callers' requests.
    private static bool UsesSearchRateLimitHeaders(string callSource) =>
        callSource == RateLimitCallSources.SevenTvLeaderboard;

    // Same plausibility bound as ReadResetHintSeconds's MaxResetHintSeconds, and for the same
    // reason (Fix-Lauf 1, Befund A): this value feeds the leaderboard breaker's open duration
    // exactly as the GraphQL hint feeds the preview breaker's, and an epoch-shaped or negative
    // x-ratelimit-search-reset would otherwise hold it shut for decades. The stock's own [60 s, 1 h]
    // clamp only protects the stock entry, not this earlier read.
    private static int? ReadSearchResetHeaderSeconds(HttpResponseMessage response) =>
        int.TryParse(ProviderRequestTelemetryHandler.ReadHeader(response, SearchRateLimitResetHeader), out var seconds)
            && seconds > 0
            && seconds <= MaxResetHintSeconds
            ? seconds
            : null;

    private static V4HeaderSample? BuildSearchHeaderSample(HttpResponseMessage response, string callSource) =>
        UsesSearchRateLimitHeaders(callSource)
            ? new V4HeaderSample(
                ProviderRequestTelemetryHandler.ReadHeader(response, SearchRateLimitHeader),
                ProviderRequestTelemetryHandler.ReadHeader(response, SearchRateLimitRemainingHeader),
                ProviderRequestTelemetryHandler.ReadHeader(response, SearchRateLimitResetHeader))
            : null;

    private void RecordForeignPreviewObservation(
        HttpResponseMessage response, string callSource, int? statusOverride, int? retryAfterSeconds)
    {
        var usesSearchHeaders = UsesSearchRateLimitHeaders(callSource);
        telemetry.RecordProviderResponse(new ProviderResponseObservation(
            RateLimitProviders.SevenTv,
            callSource,
            statusOverride ?? (int)response.StatusCode,
            retryAfterSeconds,
            ProviderRequestTelemetryHandler.ReadHeader(response, usesSearchHeaders ? SearchRateLimitHeader : "Ratelimit-Limit"),
            ProviderRequestTelemetryHandler.ReadHeader(response, usesSearchHeaders ? SearchRateLimitRemainingHeader : "Ratelimit-Remaining"),
            ProviderRequestTelemetryHandler.ReadHeader(response, usesSearchHeaders ? SearchRateLimitResetHeader : "Ratelimit-Reset")));
    }

    private static TimeSpan? ToRetryAfter(int? retryAfterSeconds) =>
        retryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    // Common continuation for both branches of GetChannelStateForTwitchUserAsync: whether emoteSetDto
    // came straight off the primary response or was reloaded via the issue #43 fallback, everything
    // from here on (emote mapping, the v4 AddedToSetAt overlay, the account id, capacity handling)
    // is identical.
    private async Task<SevenTvChannelStateResult> BuildChannelStateResultAsync(
        SevenTvEmoteSetJsonDto emoteSetDto, SevenTvUserRestUserDto? user, CancellationToken cancellationToken)
    {
        var emotes = emoteSetDto.Emotes.Select(SevenTvEmoteJsonMapper.MapDto).ToList();

        // Overlay the real set-entry dates from v4. Null (lookup failed) simply leaves every
        // AddedToSetAt unknown — the sync's correction pass fills the gap on a later resync,
        // which is strictly better than failing the whole channel sync over a date.
        var addedAtByEmoteId = await GetSetEntryAddedAtAsync(emoteSetDto.Id, cancellationToken);
        if (addedAtByEmoteId is not null)
        {
            emotes = emotes
                .Select(e => addedAtByEmoteId.TryGetValue(e.Id, out var addedAt)
                    ? e with { AddedToSetAt = addedAt }
                    : e)
                .ToList();
        }

        // The response's top-level id is the Twitch connection id, not the 7TV account —
        // the account lives under user.id (verified live 2026-07-30).
        var sevenTvUserId = string.IsNullOrEmpty(user?.Id) ? null : user.Id;

        // 0 reads as "not reported", not as "no slots" — an absent field and a genuine zero are
        // indistinguishable here, and treating either as a capacity of zero would make the UI
        // claim the set is full.
        var capacity = emoteSetDto.Capacity > 0 ? emoteSetDto.Capacity : (int?)null;

        return SevenTvChannelStateResult.Ok(
            new SevenTvChannelState(sevenTvUserId, new SevenTvEmoteSet(emoteSetDto.Id, emotes, capacity)));
    }

    // Reloads a set that the primary response no longer embeds (issue #43). Any non-success status —
    // EnsureSuccessStatusCode covers 404 too — or a malformed body throws HttpRequestException/
    // JsonException/TaskCanceledException, which the caller's try/catch already maps to Unavailable;
    // only a literal JSON `null` body returns normally, and the caller treats that null as Unavailable
    // too. Either way this must never surface as NoActiveEmoteSet — a broken reload says nothing about
    // whether a set is actually active.
    private async Task<SevenTvEmoteSetJsonDto?> FetchEmoteSetAsync(string emoteSetId, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync($"emote-sets/{emoteSetId}", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SevenTvEmoteSetJsonDto>(
            SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);
    }

    // First entry into the issue #43 fallback path this process has made logs at Information so the
    // rollout's arrival is visible; every later entry — which, once 7TV finishes rolling out, is
    // every channel on every 60s resync tick — drops to Debug for the same reason NoActiveEmoteSet
    // above stays off Information: ~1440 lines a day per channel would drown everything else.
    private void LogFallbackEmoteSetPathEntered(string twitchUserId)
    {
        if (Interlocked.Exchange(ref _fallbackEmoteSetPathLoggedOnce, 1) == 0)
        {
            logger.LogInformation(
                "7TV liefert kein eingebettetes Emote-Set mehr für Twitch-ID {Id} (Ausrollung #43), lade Set separat über emote-sets/{{id}} nach.",
                twitchUserId);
        }
        else
        {
            logger.LogDebug(
                "7TV liefert kein eingebettetes Emote-Set für Twitch-ID {Id}, lade Set separat nach.",
                twitchUserId);
        }
    }

    private async Task<Dictionary<string, DateTime>?> GetSetEntryAddedAtAsync(string emoteSetId, CancellationToken cancellationToken)
    {
        try
        {
            var result = new Dictionary<string, DateTime>();
            for (var page = 1; page <= MaxSetEntryPages; page++)
            {
                var payload = new
                {
                    query = GqlSetEntriesQuery,
                    variables = new { id = emoteSetId, page, perPage = SetEntriesPerPage }
                };
                var response = await httpClient.PostAsJsonAsync(V4GqlPath, payload, cancellationToken);
                response.EnsureSuccessStatusCode();

                var dto = await response.Content.ReadFromJsonAsync<SevenTvGqlSetEntriesResponseDto>(
                    SevenTvEmoteJsonMapper.JsonOptions, cancellationToken);

                var entryPage = dto?.Data?.EmoteSets?.EmoteSet?.Emotes;
                if (entryPage is null)
                {
                    logger.LogWarning(
                        "7TV-v4-addedAt-Abruf für Set {SetId} lieferte keine Daten — Beitrittsdaten bleiben vorerst unbekannt.",
                        emoteSetId);
                    return null;
                }

                foreach (var entry in entryPage.Items.Where(entry => entry.Emote is not null && entry.AddedAt is not null))
                {
                    result[entry.Emote!.Id] = entry.AddedAt!.Value.UtcDateTime;
                }

                if (page >= entryPage.PageCount)
                {
                    break;
                }
            }

            return result;
        }
        // JsonException belongs here rather than in the caller's catch: a malformed v4 answer must
        // stay a missing date, not turn the whole channel Unavailable. The caller now treats
        // JsonException as a broken lookup, which is right for the v3 payload it reads itself — but
        // this optional overlay has always been allowed to fail on its own without taking the sync
        // with it, and letting its parse errors bubble would quietly reverse that.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex,
                "7TV-v4-addedAt-Abruf für Set {SetId} fehlgeschlagen — Beitrittsdaten bleiben vorerst unbekannt.",
                emoteSetId);
            return null;
        }
    }

    // Resolution order for issue #43: the top-level id — this whole response *is* the requested
    // Twitch connection — and otherwise that same connection found by its exact Twitch user id in
    // the account's connection list. Nothing else, and deliberately so. Every looser candidate
    // belongs to a *different* channel: a non-TWITCH connection can point at an entirely different
    // set, and so can a second TWITCH connection on the same 7TV account. Such a candidate could
    // only ever be reached when the requested connection has no set of its own — which is exactly
    // the case NoActiveEmoteSet exists to report. Taking one instead would make SevenTvSyncService
    // persist a foreign set id and reconcile a foreign channel's emotes into this one, where voting
    // and the 7TV mass delete would then act on it: silent data pollution instead of a visible,
    // truthful failure reason. Same exact-id contract ResolveSevenTvIdentityAsync already holds.
    private static string? ResolveFallbackEmoteSetId(SevenTvUserRestDto dto, string twitchUserId)
    {
        if (IsUsableSevenTvId(dto.EmoteSetId))
        {
            return dto.EmoteSetId;
        }

        var ownConnection = (dto.User?.Connections ?? []).FirstOrDefault(c =>
            c.Platform == TwitchPlatform && c.Id == twitchUserId && IsUsableSevenTvId(c.EmoteSetId));

        return ownConnection?.EmoteSetId;
    }

    // 7TV represents "no id" two different ways depending on the endpoint: sometimes a genuine
    // absence (null/empty), sometimes a placeholder sentinel of all-zero characters — proven live for
    // a different lookup on this same client (ResolveSevenTvIdentityAsync's
    // "00000000000000000000000000" placeholder account id, measured 2026-08-31). Both must read as
    // "not present" here, or a sentinel would be mistaken for a real emote-set id and forwarded to
    // GET emote-sets/{id}. Checking "every character is '0'" rather than a fixed-length literal
    // survives 7TV changing the sentinel's length or format.
    //
    // For this particular field the sentinel has not been observed: 62 accounts without an active
    // set, sampled live 2026-09-01, all answered with a plain null emote_set_id (top level and in
    // connections[]). The all-zero branch is therefore unproven defence, not a fix for a known
    // behaviour — the null case, which keeps NoActiveEmoteSet reachable after the rollout, is the
    // measured one.
    private static bool IsUsableSevenTvId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Any(c => c != '0');

    // 7TV wraps a GraphQL-level failure as HTTP 200 (see GqlEmoteSetPreviewQuery's comment and
    // SevenTvGqlEmoteSetPreviewResponseDto); a rate limit is one specific `errors[].extensions.status`
    // value among those failures, matching the shape already captured live for a different query
    // (SevenTvApiClientResolveIdentityTests' GraphQlErrorPayload: `extensions: { code, status }`).
    private static bool IsRateLimited(List<SevenTvGqlErrorDto>? errors) =>
        errors?.Any(error => error.Extensions?.Status == 429) ?? false;

    /// <summary>
    /// The reset hint a GraphQL error payload may carry in <c>extensions.headers</c>, in seconds —
    /// <c>null</c> whenever it is absent, unreadable, or not plausibly a duration.
    /// </summary>
    /// <remarks>
    /// Read defensively on purpose. We have no confirmation that 7TV sends this at all (see
    /// <c>SevenTvGqlErrorExtensionsDto.Headers</c>); the one thing we do know is the shape of the
    /// value if it is the same one the live measurement saw in a header —
    /// <c>x-ratelimit-search-reset: 3583</c>, seconds remaining. So: seconds remaining is the only
    /// reading accepted, both as a JSON number and as a quoted string, and anything outside a
    /// plausible duration is discarded rather than reinterpreted. A Unix timestamp would land far
    /// above the ceiling and be dropped — deliberately, because guessing that it *is* a timestamp
    /// would be inventing a semantics we cannot check, and getting it wrong means holding the breaker
    /// shut for decades.
    /// </remarks>
    private static int? ReadResetHintSeconds(List<SevenTvGqlErrorDto>? errors)
    {
        if (errors is null)
        {
            return null;
        }

        foreach (var headers in errors.Select(error => error.Extensions?.Headers).OfType<Dictionary<string, JsonElement>>())
        {
            foreach (var (name, value) in headers)
            {
                if (!IsResetHintHeaderName(name) || !TryReadSeconds(value, out var seconds))
                {
                    continue;
                }

                if (seconds > 0 && seconds <= MaxResetHintSeconds)
                {
                    return seconds;
                }
            }
        }

        return null;
    }

    // "x-ratelimit-reset" and the per-bucket variants like "x-ratelimit-search-reset" — the form the
    // live measurement of 7TV's search bucket produced.
    private static bool IsResetHintHeaderName(string name) =>
        name.StartsWith("x-ratelimit-", StringComparison.OrdinalIgnoreCase)
        && name.EndsWith("-reset", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadSeconds(JsonElement value, out int seconds)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt32(out seconds);
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), out seconds);
            default:
                seconds = 0;
                return false;
        }
    }

    private static SevenTvEmoteSetPreviewItem MapPreviewItem(SevenTvGqlEmoteSetPreviewItemDto dto)
    {
        var emoteId = dto.Emote?.Id ?? string.Empty;
        return new SevenTvEmoteSetPreviewItem(
            emoteId,
            dto.Alias,
            dto.Emote?.DefaultName ?? string.Empty,
            BuildForeignImageUrl(emoteId, dto.Emote?.Flags?.Animated ?? false),
            dto.Emote?.Scores?.TopAllTime,
            dto.Emote?.Scores?.TrendingDay);
    }

    // 7TV's emote CDN url is fixed and keyed only by the emote id — every variant sits under
    // https://cdn.7tv.app/emote/{id}/{scale}{_static?}.{ext}. Building the 4x directly from the id
    // avoids requesting the images list at all (see GqlEmoteSetPreviewQuery's comment for the
    // measured payload cost of doing so).
    //
    // The "_static" rendition, however, only exists when the source is animated — it is 7TV's
    // flattened first frame, and there is nothing to flatten otherwise. Hardcoding it cost a 404 for
    // every still emote: measured 2026-09-09 against HandOfBlood's set, 305 of 956 emotes (31.9 %)
    // are stills and answered 404 on 4x_static.webp while 4x.webp answered 200. That is why the flag
    // is queried rather than assumed — Emote.flags.animated is a plain Boolean and costs ~26 bytes an
    // emote (+18.1 % on a 45-item page, against +1640 % for pulling Emote.images).
    //
    // The resulting string is byte-identical to what the tracked-channel path produces, and must
    // stay so: SevenTvEmoteJsonMapper.BuildImageUrl reads the same distinction out of 7TV's own
    // host.files[].static_name — literally "4x_static.webp" for an animated emote and "4x.webp" for a
    // still one (both verified live 2026-09-09 on this very set) — and the frontend's STILL_SUFFIX
    // derives the animated url by stripping that marker, so its presence is load-bearing on both
    // paths alike.
    //
    // The false default when the flag is absent is a guard, not a behaviour: the v4 schema types
    // Emote.flags and its animated member as non-null, so no captured payload omits them. It falls to
    // 4x.webp on purpose, because that rendition exists for every emote — on an animated one it
    // simply carries all frames — whereas the other guess would render nothing at all.
    private static string BuildForeignImageUrl(string emoteId, bool animated)
    {
        if (emoteId.Length == 0)
        {
            return string.Empty;
        }

        var fileName = animated ? "4x_static.webp" : "4x.webp";
        return $"https://cdn.7tv.app/emote/{emoteId}/{fileName}";
    }

    private static SevenTvEmoteSetPreviewItem MapSearchItem(SevenTvGqlLeaderboardSearchItemDto dto) =>
        new(
            dto.Id,
            dto.DefaultName, // Alias = DefaultName (spec 2026-09-13, T1): a leaderboard hit has no per-set alias.
            dto.DefaultName,
            BuildForeignImageUrl(dto.Id, dto.Flags?.Animated ?? false),
            dto.Scores?.TopAllTime,
            dto.Scores?.TrendingDay);

    // The GraphQL $sort: Sort! variable's value — an anonymous object shaped exactly like the
    // introspected INPUT_OBJECT (Sort { sortBy: SortBy!, order: SortOrder! }, 2026-09-14), not the
    // bare enum value an earlier revision of this method sent. See GqlLeaderboardSearchQuery's
    // comment for why that shape was wrong and what it broke.
    private static object BuildSortVariable(SevenTvLeaderboardSort sortBy) =>
        new { sortBy = sortBy.ToWireCode(), order = SearchSortOrderDescending };

    private enum V4PageStatus
    {
        Ok,
        RateLimited,
        Unavailable
    }

    // Nested at the class end (Regel 19) — a pure return-value carrier local to FetchV4PageAsync and
    // its callers (FetchPreviewPageAsync, SearchEmotesAsync), not part of this client's public shape.
    // HeaderSample is null unless UsesSearchRateLimitHeaders(callSource) — FetchPreviewPageAsync
    // never reads or forwards one. ParseException is set only when a JSON parse failure caused the
    // Unavailable outcome, so a caller can tell that case apart from the rest.
    private readonly record struct V4PageResult<TDto>(
        V4PageStatus Status, TDto? Dto, TimeSpan? RetryAfter, V4HeaderSample? HeaderSample = null, JsonException? ParseException = null);

    // 7TV's x-ratelimit-search-* header sample, as read for one response. Only ever built for the
    // leaderboard call source (BuildSearchHeaderSample) — see SevenTvEmoteSearchPageResult for why
    // it has to survive into every outcome, not just Ok.
    private readonly record struct V4HeaderSample(string? Limit, string? Remaining, string? Reset);

    // Outcome of FetchClassifiedPreviewPageAsync: exactly one of the two is set. PageDto is the page
    // ready to fold into GetEmoteSetPreviewAsync's accumulated preview; Failure is the already-built
    // result that method returns as-is, since the reason (and its log line) was decided here.
    private readonly record struct PreviewPageFetch(SevenTvGqlEmoteSetPreviewPageDto? PageDto, SevenTvEmoteSetPreviewResult? Failure)
    {
        public static PreviewPageFetch Success(SevenTvGqlEmoteSetPreviewPageDto pageDto) => new(pageDto, null);

        public static PreviewPageFetch Failed(SevenTvEmoteSetPreviewResult failure) => new(null, failure);
    }
}
