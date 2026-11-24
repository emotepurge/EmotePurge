using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// </remarks>
public sealed partial class SevenTvWriteClient(HttpClient httpClient, ILogger<SevenTvWriteClient> logger) : ISevenTvWriteClient
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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);

        var entries = new List<SevenTvSetEntry>();
        int? firstPageCount = null;
        for (var page = 1; page <= MaxSetEntryPages; page++)
        {
            var exchange = await PostAsync(
                "read", token, SetEntriesQuery, new { id = setId, page, perPage = SetEntriesPerPage }, cancellationToken);
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
            var pageCount = set is { } setElement && SameId(setElement, setId)
                ? ReadEntriesPage(setElement, entries)
                : null;

            // A page count that moves between pages means the set changed under the read; one above the cap means the
            // read cannot finish. Neither yields a list the bot may decide on.
            if (pageCount is not { } count
                || count > MaxSetEntryPages
                || (firstPageCount is { } first && first != count))
            {
                LogOutcome("read", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
                return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
            }

            firstPageCount = count;
            if (page >= count)
            {
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
            return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Failed(SevenTvWriteResult.Unavailable());
        }

        // The entries are a bonus. An error below the mutation field (only the `emotes` selection can carry one once the
        // id matched), a missing or malformed list, or a set larger than the one returned page leaves the caller to read.
        IReadOnlyList<SevenTvSetEntry>? entries = null;
        if (reply.Errors.Count == 0)
        {
            var returned = new List<SevenTvSetEntry>();
            if (ReadEntriesPage(set, returned) is 0 or 1)
            {
                entries = returned;
            }
        }

        return SevenTvWriteResult<IReadOnlyList<SevenTvSetEntry>>.Ok(entries);
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
            return SevenTvWriteResult.Ok();
        }

        LogOutcome("remove", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
        return SevenTvWriteResult.Unavailable();
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
            return SevenTvWriteResult.Ok();
        }

        LogOutcome("accept", SevenTvWriteResult.Unavailable(), exchange.HttpStatus, null);
        return SevenTvWriteResult.Unavailable();
    }

    private async Task<Exchange> PostAsync(
        string operation,
        string token,
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var request = new HttpRequestMessage(HttpMethod.Post, GqlPath)
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpStatusCode status;
        string body;
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            status = response.StatusCode;

            // Decided before the body is read: a real 401 carries 7TV's own body outside the GraphQL schema
            // ({status, error_code, error}, probe P9), a 429 at most a Retry-After header.
            var transportFailure = (int)status switch
            {
                429 => SevenTvWriteResult.RateLimited(
                    SevenTvGqlReply.ClampRetryAfter(ProviderRequestTelemetryHandler.ReadRetryAfterSeconds(response))),
                401 => SevenTvWriteResult.Unauthorized(),
                403 => null,
                < 200 or > 299 => SevenTvWriteResult.Unavailable(),
                _ => null,
            };
            if (transportFailure is not null)
            {
                LogOutcome(operation, transportFailure, status, null);
                return Exchange.Failed(transportFailure, status);
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if ((int)status == 403)
            {
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
            // HttpClient.Timeout surfaces as a cancellation the caller did not ask for.
            LogOutcome(operation, SevenTvWriteResult.Timeout(), null, null);
            return Exchange.Failed(SevenTvWriteResult.Timeout(), null);
        }
        catch (HttpRequestException ex)
        {
            // The exception's type only: its message is not ours to vouch for.
            logger.LogWarning(
                "7TV bot {Operation} failed in transport ({ExceptionType}), outcome Unavailable.",
                operation, ex.GetType().Name);
            return Exchange.Failed(SevenTvWriteResult.Unavailable(), null);
        }

        if (SevenTvGqlReply.TryParse(body) is not { } reply)
        {
            LogOutcome(operation, SevenTvWriteResult.Unavailable(), status, null);
            return Exchange.Failed(SevenTvWriteResult.Unavailable(), status);
        }

        return new Exchange(reply, null, status);
    }

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

    // Reads `emotes { pageCount items { alias addedAt addedById emote { id } } }` of one set element into
    // `entries`. Returns the page count, or null when the page is not readable as a whole.
    private static int? ReadEntriesPage(JsonElement set, List<SevenTvSetEntry> entries)
    {
        if (!set.TryGetProperty("emotes", out var emotes)
            || emotes.ValueKind != JsonValueKind.Object
            || !emotes.TryGetProperty("pageCount", out var pageCountElement)
            || !pageCountElement.TryGetInt32(out var pageCount)
            || pageCount < 0
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
        return pageCount;
    }

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
    private sealed record Exchange(SevenTvGqlReply? Reply, SevenTvWriteResult? Failure, HttpStatusCode? HttpStatus)
    {
        public static Exchange Failed(SevenTvWriteResult failure, HttpStatusCode? status) => new(null, failure, status);
    }
}
