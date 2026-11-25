using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// The bot account's typed client for 7TV's v4 GraphQL endpoint (plan #487 2.4). Registered by
/// <see cref="BotAccountRegistration.AddBotAccountWriter"/> only, with the HttpClient factory's own request logging removed
/// (it would log request headers at trace level) and a 10 s time limit.
/// </summary>
/// <remarks>
/// <para>
/// Every GraphQL string here is the one the live probe T0 ran against <c>https://7tv.io/v4/gql</c> on 2026-11-24,
/// character for character (protocol <c>docs/nachtlaeufe/live-487-probe-2026-11-24.md</c>). A wrong query does not look
/// wrong, it looks like 7TV being down; whoever changes one changes it live first.
/// </para>
/// <para>
/// The token lives in the <c>Authorization</c> header of the one request it is passed for, never in
/// <see cref="HttpClient.DefaultRequestHeaders"/> or a field. Logs carry the operation, the outcome, the HTTP status and
/// 7TV's error code and status, never the token, a body or an error message.
/// </para>
/// <para>
/// Telemetry: the shared <see cref="ProviderRequestTelemetryHandler"/> is silenced for every request
/// (<see cref="ProviderTelemetrySuppression.OptionsKey"/>) and this client reports each HTTP response itself under
/// <see cref="RateLimitCallSources.SevenTvBot"/>, once, at <c>PostAsync</c>: a rate limit anywhere in the answer's
/// <c>errors</c> (also below the mutation field) counts as 429 although the HTTP status is 200. A call that got no
/// answer (timeout, connection failure) reports nothing, as the handler would not. A rate limit below the proven
/// operation also sets <see cref="SevenTvWriteResult.RetryAfter"/> on an <see cref="SevenTvWriteOutcome.Ok"/>, and on the
/// <see cref="SevenTvWriteOutcome.Unavailable"/> of a mutation without its proof; the caller books the pause.
/// </para>
/// </remarks>
public sealed partial class SevenTvWriteClient(
    HttpClient httpClient,
    IRateLimitTelemetry telemetry,
    ILogger<SevenTvWriteClient> logger) : ISevenTvWriteClient
{
    /// <summary>Relative to the base address <c>https://7tv.io/v4/</c>.</summary>
    public const string GqlPath = "gql";

    // Probe Q_ME (P1).
    internal const string VerifyQuery = "query { users { me { id } } }";

    // Probe Q_V4_ENTRIES (P3, AK86): `addedById` is readable on EmoteSetEmote, `id` on the set lets the read prove it got
    // the set it asked for.
    internal const string SetEntriesQuery =
        "query($id: Id!, $page: Int!, $perPage: Int!) { emoteSets { emoteSet(id: $id) { id capacity emotes(page: $page, perPage: $perPage) { totalCount pageCount items { alias addedAt addedById emote { id defaultName } } } } } }";

    // Probe M_ADD_RETURN (P3, AK89): the mutation with a return selection on the resulting set, `id` included for the
    // proof of success (revision 6a, S6-4).
    internal const string AddEmoteMutation =
        "mutation AddEmote($setId: Id!, $emoteId: Id!, $alias: String) { emoteSets { emoteSet(id: $setId) { addEmote(id: { emoteId: $emoteId, alias: $alias }) { id emotes(page: 1, perPage: 500) { totalCount pageCount items { alias addedAt addedById emote { id } } } } } } }";

    // Probe M_REMOVE (P5): `$alias` is non-null on purpose, without it 7TV takes any entry of the emote.
    internal const string RemoveEmoteMutation =
        "mutation RemoveSlotEntry($setId: Id!, $emoteId: Id!, $alias: String!) { emoteSets { emoteSet(id: $setId) { removeEmote(id: { emoteId: $emoteId, alias: $alias }) { id } } } }";

    // Probe M_ACCEPT (A5). The state is a literal: this client can only ever accept ([B] F7).
    internal const string AcceptInvitationMutation =
        "mutation($editorId: Id!, $userId: Id!) { userEditors { editor(editorId: $editorId, userId: $userId) { updateState(state: ACCEPT) { state } } } }";

    // UserEditorState after an accepted invitation (7TV v4 schema: ACCEPTED | PENDING | REJECTED).
    private const string AcceptedState = "ACCEPTED";

    // As SevenTvApiClient: 500 per page keeps even large sets at a handful of requests; the page cap is a runaway guard.
    // A set above it cannot be read completely, so it is Unavailable rather than silently cut (fail-closed).
    private const int SetEntriesPerPage = 500;
    private const int MaxSetEntryPages = 10;

    // The shortest time limit a page under a deadline is still requested with (K6e-5).
    private static readonly TimeSpan MinimumPageTimeLimit = TimeSpan.FromSeconds(1);

    private static readonly string[] VerifyPath = ["users", "me"];
    private static readonly string[] SetPath = ["emoteSets", "emoteSet"];
    private static readonly string[] AddEmotePath = ["emoteSets", "emoteSet", "addEmote"];
    private static readonly string[] RemoveEmotePath = ["emoteSets", "emoteSet", "removeEmote"];
    private static readonly string[] AcceptInvitationPath = ["userEditors", "editor", "updateState"];

    public async Task<SevenTvWriteResult<string>> VerifyAsync(string token, CancellationToken cancellationToken = default)
    {
        var exchange = await PostAsync("verify", token, VerifyQuery, new { }, cancellationToken);
        if (exchange.Reply is not { } reply)
        {
            return SevenTvWriteResult<string>.Failed(exchange.Failure!);
        }

        if (reply.Errors.Count > 0)
        {
            return Failed<string>("verify", exchange.HttpStatus, reply.Errors);
        }

        var me = reply.Field(VerifyPath);
        if (me is { ValueKind: JsonValueKind.Object } user && ReadString(user, "id") is { } id)
        {
            return SevenTvWriteResult<string>.Ok(id);
        }

        LogOutcome("verify", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
        return SevenTvWriteResult<string>.Failed(SevenTvWriteResult.Unavailable());
    }

    public async Task<SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>> ReadSetEntriesAsync(
        string token,
        string setId,
        Func<TimeSpan>? timeLeft = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);

        var entries = new List<SevenTvSetEntry>();
        int? firstPageCount = null;
        int? firstTotalCount = null;
        var rawItems = 0;
        for (var page = 1; page <= MaxSetEntryPages; page++)
        {
            // K6e-5: under a deadline every page gets the smaller of the client's time limit and the time left, and none
            // starts with less than a second left.
            TimeSpan? pageLimit = null;
            if (timeLeft is not null)
            {
                var left = timeLeft();
                if (left < MinimumPageTimeLimit)
                {
                    logger.LogWarning("7TV bot read stopped before page {Page}: its deadline leaves no time for it, outcome Unavailable.", page);
                    return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
                }

                pageLimit = left < BotAccountRegistration.WriteClientTimeout ? left : null;
            }

            var exchange = await PostAsync(
                "read", token, SetEntriesQuery, new { id = setId, page, perPage = SetEntriesPerPage }, cancellationToken, pageLimit);
            if (exchange.Reply is not { } reply)
            {
                return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(exchange.Failure!);
            }

            // Fail-closed: any error on any page means the list cannot be trusted to be complete.
            if (reply.Errors.Count > 0)
            {
                return Failed<IReadOnlyList<SevenTvSetEntry>>("read", exchange.HttpStatus, reply.Errors);
            }

            var set = reply.Field(SetPath);
            var entriesPage = set is { } setElement && SameId(setElement, setId)
                ? ReadEntriesPage(setElement, entries)
                : null;

            // A page count or total that moves between pages means the set changed under the read; a page count above the
            // cap means the read cannot finish. Neither yields a list the bot may decide on.
            if (entriesPage is not { } current
                || current.PageCount > MaxSetEntryPages
                || (firstPageCount is { } first && first != current.PageCount)
                || (firstTotalCount is { } firstTotal && firstTotal != current.TotalCount))
            {
                LogOutcome("read", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
                return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
            }

            firstPageCount = current.PageCount;
            firstTotalCount = current.TotalCount;
            rawItems += current.RawItems;
            if (page >= current.PageCount)
            {
                // Fail-closed (AK76): 7TV may list fewer entries than it counts; such a list is not the whole set.
                if (rawItems != current.TotalCount)
                {
                    LogOutcome("read", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
                    return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
                }

                return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(entries);
            }
        }

        // Unreachable: a page count above the cap is refused on the first page.
        return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
    }

    public async Task<SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>> AddEmoteAsync(
        string token,
        string setId,
        string emoteId,
        string alias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        ArgumentException.ThrowIfNullOrWhiteSpace(emoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var exchange = await PostAsync("add", token, AddEmoteMutation, new { setId, emoteId, alias }, cancellationToken);
        if (exchange.Reply is not { } reply)
        {
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(exchange.Failure!);
        }

        var errorsOnMutation = reply.ErrorsNotBelow(AddEmotePath);
        if (errorsOnMutation.Count > 0)
        {
            return Failed<IReadOnlyList<SevenTvSetEntry>>("add", exchange.HttpStatus, errorsOnMutation);
        }

        // The proof of success (plan 2.4, S6-4): the mutation field itself, carrying the requested set's id.
        var returnedSet = reply.Field(AddEmotePath);
        if (returnedSet is not { ValueKind: JsonValueKind.Object } set || !SameId(set, setId))
        {
            LogOutcome("add", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(UnprovenResult(exchange));
        }

        // The entries are a bonus. An error below the mutation field (only the `emotes` selection can carry one once the
        // id matched), a missing or malformed list, or a set larger than the one returned page leaves the caller to read.
        IReadOnlyList<SevenTvSetEntry>? entries = null;
        if (reply.Errors.Count == 0)
        {
            var returned = new List<SevenTvSetEntry>();
            // Complete means: one page at most, and as many raw items as the set counts.
            if (ReadEntriesPage(set, returned) is { PageCount: 0 or 1 } returnedPage && returnedPage.RawItems == returnedPage.TotalCount)
            {
                entries = returned;
            }
        }

        return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(entries, exchange.RateLimitWait);
    }

    public async Task<SevenTvWriteResult> RemoveEmoteAsync(
        string token,
        string setId,
        string emoteId,
        string alias,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        ArgumentException.ThrowIfNullOrWhiteSpace(emoteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var exchange = await PostAsync("remove", token, RemoveEmoteMutation, new { setId, emoteId, alias }, cancellationToken);
        if (exchange.Reply is not { } reply)
        {
            return exchange.Failure!;
        }

        var errorsOnMutation = reply.ErrorsNotBelow(RemoveEmotePath);
        if (errorsOnMutation.Count > 0)
        {
            return Failed("remove", exchange.HttpStatus, errorsOnMutation);
        }

        if (reply.Field(RemoveEmotePath) is { ValueKind: JsonValueKind.Object })
        {
            return OkResult(exchange);
        }

        LogOutcome("remove", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
        return UnprovenResult(exchange);
    }

    public async Task<SevenTvWriteResult> AcceptEditorInvitationAsync(
        string token,
        string botSevenTvUserId,
        string ownerSevenTvUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botSevenTvUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSevenTvUserId);

        var exchange = await PostAsync(
            "accept",
            token,
            AcceptInvitationMutation,
            new { editorId = botSevenTvUserId, userId = ownerSevenTvUserId },
            cancellationToken);
        if (exchange.Reply is not { } reply)
        {
            return exchange.Failure!;
        }

        var errorsOnMutation = reply.ErrorsNotBelow(AcceptInvitationPath);
        if (errorsOnMutation.Count > 0)
        {
            return Failed("accept", exchange.HttpStatus, errorsOnMutation);
        }

        if (reply.Field(AcceptInvitationPath) is { ValueKind: JsonValueKind.Object } editor
            && string.Equals(ReadString(editor, "state"), AcceptedState, StringComparison.Ordinal))
        {
            return OkResult(exchange);
        }

        LogOutcome("accept", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
        return UnprovenResult(exchange);
    }

    private async Task<Exchange> PostAsync(
        string operation,
        string token,
        string query,
        object variables,
        CancellationToken cancellationToken,
        TimeSpan? timeLimit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // A time limit below the client's own (a page under a lock deadline, K6e-5) cancels the request on its own token;
        // the caller's token stays untouched, so the cut is told apart from both the caller's cancellation and the
        // HttpClient timeout.
        using var limit = timeLimit is { } shorter ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        limit?.CancelAfter(timeLimit!.Value);
        var requestToken = limit?.Token ?? cancellationToken;

        using var request = new HttpRequestMessage(HttpMethod.Post, GqlPath)
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Suppressed per request: this client reports every HTTP response itself (see the class remarks).
        request.Options.Set(ProviderTelemetrySuppression.OptionsKey, true);

        HttpStatusCode status;
        string body;
        ResponseFacts? facts = null;
        var reported = false;
        try
        {
            using var response = await httpClient.SendAsync(request, requestToken);
            status = response.StatusCode;
            facts = ResponseFacts.Read(response);

            // Decided before the body is read: a real 401 carries 7TV's own body outside the GraphQL schema
            // ({status, error_code, error}, probe P9), a 429 at most a Retry-After header.
            var transportFailure = (int)status switch
            {
                429 => SevenTvWriteResult.RateLimited(SevenTvGqlReply.ClampRetryAfter(facts.RetryAfterSeconds)),
                401 => SevenTvWriteResult.Unauthorized(),
                403 => null,
                < 200 or > 299 => SevenTvWriteResult.Unavailable(),
                _ => null,
            };
            if (transportFailure is not null)
            {
                Report(facts, null);
                reported = true;
                LogOutcome(operation, transportFailure, status, null);
                return Exchange.Failed(transportFailure, status);
            }

            body = await response.Content.ReadAsStringAsync(requestToken);

            if ((int)status == 403)
            {
                Report(facts, null);
                reported = true;

                // Orchestrator decision at T3: only a 403 with 7TV's own error shape (a numeric error_code) means the
                // token is dead; a CDN or WAF 403 must not mark it invalid and trigger the global fallback.
                var result = HasSevenTvErrorShape(body)
                    ? SevenTvWriteResult.Unauthorized()
                    : SevenTvWriteResult.Unavailable();
                LogOutcome(operation, result, status, null);
                return Exchange.Failed(result, status);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as a cancellation the caller did not ask for. Without an answer nothing is
            // reported; with one (the body read timed out) the answer already counted at the handler's place.
            if (facts is not null && !reported)
            {
                Report(facts, null);
            }

            // Cut at the caller's deadline (K6e-5): Unavailable, never a Timeout of the request itself.
            var cut = limit is { IsCancellationRequested: true } ? SevenTvWriteResult.Unavailable() : SevenTvWriteResult.Timeout();
            LogOutcome(operation, cut, null, null);
            return Exchange.Failed(cut, null);
        }
        catch (HttpRequestException ex)
        {
            if (facts is not null && !reported)
            {
                Report(facts, null);
            }

            // The exception's type only: its message is not ours to vouch for.
            logger.LogWarning(
                "7TV bot {Operation} failed in transport ({ExceptionType}), outcome Unavailable.",
                operation, ex.GetType().Name);
            return Exchange.Failed(SevenTvWriteResult.Unavailable(), null);
        }

        if (SevenTvGqlReply.TryParse(body) is not { } reply)
        {
            Report(facts, null);
            LogOutcome(operation, SevenTvWriteResult.Unavailable(), status, null);
            return Exchange.Failed(SevenTvWriteResult.Unavailable(), status);
        }

        // The status is decided from the whole answer, whatever the operation makes of it: a rate limit 7TV put below the
        // mutation field is still a rate limit (same predicate as Classify).
        var wait = reply.RateLimitWait();
        Report(facts, wait is null ? null : 429);
        return new Exchange(reply, null, status, wait);
    }

    private void Report(ResponseFacts facts, int? statusOverride) =>
        telemetry.ReportProviderResponse(new ProviderResponseObservation(
            RateLimitProviders.SevenTv,
            RateLimitCallSources.SevenTvBot,
            statusOverride ?? facts.StatusCode,
            facts.RetryAfterSeconds,
            facts.Limit,
            facts.Remaining,
            facts.Reset));

    private static SevenTvWriteResult OkResult(Exchange exchange) =>
        exchange.RateLimitWait is { } wait ? SevenTvWriteResult.Ok(wait) : SevenTvWriteResult.Ok();

    // A mutation without its proof (K6e-4): the outcome stays open, a rate limit anywhere in the answer still pauses.
    private static SevenTvWriteResult UnprovenResult(Exchange exchange) =>
        exchange.RateLimitWait is { } wait ? SevenTvWriteResult.Unavailable(wait) : SevenTvWriteResult.Unavailable();

    private static bool HasSevenTvErrorShape(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("error_code", out var code)
                && code.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private SevenTvWriteResult<T> Failed<T>(string operation, HttpStatusCode? status, IReadOnlyList<SevenTvGqlError> errors)
        where T : class
        => SevenTvWriteResult<T>.Failed(Failed(operation, status, errors));

    private SevenTvWriteResult Failed(string operation, HttpStatusCode? status, IReadOnlyList<SevenTvGqlError> errors)
    {
        var result = SevenTvGqlReply.Classify(errors);
        LogOutcome(operation, result, status, errors);
        return result;
    }

    private void LogOutcome(
        string operation,
        SevenTvWriteResult result,
        HttpStatusCode? status,
        IReadOnlyList<SevenTvGqlError>? errors)
    {
        // Code and status of the errors only, never their messages (plan 2.4); a code that is not a plain constant is
        // replaced so 7TV cannot put text into this line through it.
        var codes = errors is null
            ? "-"
            : string.Join(',', errors.Select(error =>
                $"{SafeCode(error.Code)}/{error.Status?.ToString(CultureInfo.InvariantCulture) ?? "-"}"));
        var level = result.Outcome is SevenTvWriteOutcome.LackingPrivileges or SevenTvWriteOutcome.Rejected
            ? LogLevel.Information
            : LogLevel.Warning;
        logger.Log(
            level,
            "7TV bot {Operation} answered {Outcome} (HTTP {HttpStatus}, GraphQL errors {ErrorCodes}).",
            operation,
            result.ToString(),
            status is { } code ? (int)code : null,
            codes);
    }

    private readonly record struct EntriesPage(int PageCount, int TotalCount, int RawItems);

    // Reads `emotes { totalCount pageCount items { alias addedAt addedById emote { id } } }` of one set element into
    // `entries`. Returns the counts, or null when the page is not readable as a whole. `RawItems` includes the items
    // left out of `entries` because their emote did not resolve, so it is comparable with `totalCount`.
    private static EntriesPage? ReadEntriesPage(JsonElement set, List<SevenTvSetEntry> entries)
    {
        if (!set.TryGetProperty("emotes", out var emotes)
            || emotes.ValueKind != JsonValueKind.Object
            || ReadInt(emotes, "pageCount") is not { } pageCount
            || pageCount < 0
            || ReadInt(emotes, "totalCount") is not { } totalCount
            || totalCount < 0
            || !emotes.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var page = new List<SevenTvSetEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // An entry whose emote did not resolve cannot be matched by emote id, so it is nothing the bot can own or
            // compare against; left out, as in SevenTvApiClient's read.
            if (!item.TryGetProperty("emote", out var emote) || emote.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var emoteId = emote.ValueKind == JsonValueKind.Object ? ReadString(emote, "id") : null;
            var alias = ReadString(item, "alias") ?? (emote.ValueKind == JsonValueKind.Object ? ReadString(emote, "defaultName") : null);
            if (emoteId is null || alias is null)
            {
                return null;
            }

            page.Add(new SevenTvSetEntry(emoteId, alias, ReadUtc(item, "addedAt"), ReadString(item, "addedById")));
        }

        entries.AddRange(page);
        return new EntriesPage(pageCount, totalCount, items.GetArrayLength());
    }

    // TryGetInt32 throws on a non-number element, so the kind is checked first.
    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool SameId(JsonElement set, string expectedId) =>
        string.Equals(ReadString(set, "id"), expectedId, StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static DateTime? ReadUtc(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private static string SafeCode(string? code) =>
        code is null ? "-" : PlainCode().IsMatch(code) ? code : "other";

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex PlainCode();

    /// <summary>A sent request's answer: either a parsed GraphQL reply or the outcome decided before parsing.</summary>
    private sealed record Exchange(SevenTvGqlReply? Reply, SevenTvWriteResult? Failure, HttpStatusCode? HttpStatus, TimeSpan? RateLimitWait = null)
    {
        public static Exchange Failed(SevenTvWriteResult failure, HttpStatusCode? status) => new(null, failure, status);
    }

    /// <summary>What the telemetry needs from a response, read before it is disposed.</summary>
    private sealed record ResponseFacts(int StatusCode, int? RetryAfterSeconds, string? Limit, string? Remaining, string? Reset)
    {
        public static ResponseFacts Read(HttpResponseMessage response) => new(
            (int)response.StatusCode,
            ProviderRequestTelemetryHandler.ReadRetryAfterSeconds(response),
            ProviderRequestTelemetryHandler.ReadHeader(response, "Ratelimit-Limit"),
            ProviderRequestTelemetryHandler.ReadHeader(response, "Ratelimit-Remaining"),
            ProviderRequestTelemetryHandler.ReadHeader(response, "Ratelimit-Reset"));
    }
}
