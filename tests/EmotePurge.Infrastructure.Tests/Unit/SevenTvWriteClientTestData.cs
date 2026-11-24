using System.Globalization;
using EmotePurge.Infrastructure.BotAccount;
using EmotePurge.Infrastructure.Tests.Fakes;

namespace EmotePurge.Infrastructure.Tests.Unit;

/// <summary>
/// Ids and answer shapes of the live probe T0 (2026-11-24, <c>docs/nachtlaeufe/live-487-probe-2026-11-24.md</c>), so the
/// write-client tests feed the client what 7TV actually sent rather than what the client expects. The token is a
/// recognisable fake that the logging tests search for.
/// </summary>
internal static class SevenTvWriteClientTestData
{
    public const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJib3QtdGVzdCJ9.c2VjcmV0LXNpZ25hdHVyZS1kby1ub3QtbG9n";
    public const string BotId = "01M7XAHYKT63HSZMS71AW0C5TG";
    public const string OwnerId = "01KYJM4NCAXW8FASM2KWG38SBT";
    public const string SetId = "01M16ZBA3NHWWYNM7Q9416ZCH7";
    public const string OtherSetId = "01M16ZBA3NHWWYNM7Q9416ZZZZ";
    public const string EmoteId = "01FZ8W19JG000DX0N9GHCRXSK8";
    public const string Alias = "epT0dino";

    // The three error messages 7TV sent in the probe; the client must decide without reading any of them.
    public const string NotEditorMessage = "you are not an editor for this user";
    public const string PendingInvitationMessage = "you do not have permission to manage this user's emote sets";
    public const string ConflictMessage = "this emote has a conflicting name";

    // Probe P9: the real HTTP 401 after "Sign Out Everywhere", outside the GraphQL schema.
    public const string UnauthorizedBody = """{"status":"Unauthorized","error_code":1000,"error":"invalid session"}""";

    /// <summary>The bot's new entry plus one human entry, in the return selection's item shape.</summary>
    public static readonly string TwoItems = Fill(
        """
        [{"alias":"@ALIAS@","addedAt":"2026-11-24T16:55:23.498+00:00","addedById":"@BOT@","emote":{"id":"@EMOTE@"}},
         {"alias":"frenn","addedAt":"2026-10-01T10:00:00+00:00","addedById":"@OWNER@","emote":{"id":"01FQN716Q00001SJ04CBQJT3Z8"}}]
        """);

    public static SevenTvWriteClient Client(ScriptedHttpHandler handler, RecordingLogger<SevenTvWriteClient>? logger = null, TimeSpan? timeout = null) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = BotAccountRegistration.WriteClientBaseAddress,
                Timeout = timeout ?? BotAccountRegistration.WriteClientTimeout,
            },
            logger ?? new RecordingLogger<SevenTvWriteClient>());

    /// <summary>A GraphQL error object as 7TV shapes it; <paramref name="extra"/> is spliced into <c>extensions</c>.</summary>
    public static string Error(string message, string? code, int? status, string? path = null, string? extra = null)
    {
        var parts = new List<string>();
        if (code is not null)
        {
            parts.Add($"\"code\":\"{code}\"");
        }

        if (status is not null)
        {
            parts.Add($"\"status\":{status}");
        }

        if (extra is not null)
        {
            parts.Add(extra);
        }

        var pathPart = path is null ? string.Empty : $",\"path\":{path}";
        return $"{{\"message\":\"{message}\"{pathPart},\"extensions\":{{{string.Join(',', parts)}}}}}";
    }

    public static string Errors(params string[] errors) => $"[{string.Join(',', errors)}]";

    /// <summary>A successful <c>addEmote</c> answer: the returned set with id and its entries.</summary>
    public static string AddAnswer(string setId = SetId, string? items = null, int pageCount = 1, int? totalCount = null) =>
        """{"data":{"emoteSets":{"emoteSet":{"addEmote":{"id":"@ID@","emotes":{"totalCount":@TOTAL@,"pageCount":@PAGES@,"items":@ITEMS@}}}}}}"""
            .Replace("@ID@", setId, StringComparison.Ordinal)
            .Replace("@TOTAL@", (totalCount ?? CountItems(items ?? TwoItems)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@PAGES@", pageCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@ITEMS@", items ?? TwoItems, StringComparison.Ordinal);

    // The raw item count of a page, which is the honest default for its `totalCount` on a single page; an unparsable or
    // non-array list counts 0.
    private static int CountItems(string items)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(items);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
        }
        catch (System.Text.Json.JsonException)
        {
            return 0;
        }
    }

    /// <summary>One page of the set read. `totalCount` defaults to the page's raw item count (a single, complete page).</summary>
    public static string ReadPage(string items, int pageCount, string setId = SetId, int? totalCount = null) =>
        """{"data":{"emoteSets":{"emoteSet":{"id":"@ID@","capacity":1000,"emotes":{"totalCount":@TOTAL@,"pageCount":@PAGES@,"items":@ITEMS@}}}}}"""
            .Replace("@ID@", setId, StringComparison.Ordinal)
            .Replace("@TOTAL@", (totalCount ?? CountItems(items)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@PAGES@", pageCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("@ITEMS@", items, StringComparison.Ordinal);

    /// <summary>An answer with <c>data: null</c> and the given errors, the shape of every refused mutation in the probe.</summary>
    public static string ErrorsOnly(params string[] errors) => """{"data":null,"errors":""" + Errors(errors) + "}";

    /// <summary>Replaces the placeholders <c>@SET@</c>, <c>@OTHERSET@</c>, <c>@BOT@</c>, <c>@OWNER@</c>, <c>@EMOTE@</c>, <c>@ALIAS@</c>.</summary>
    public static string Fill(string template) => template
        .Replace("@SET@", SetId, StringComparison.Ordinal)
        .Replace("@OTHERSET@", OtherSetId, StringComparison.Ordinal)
        .Replace("@BOT@", BotId, StringComparison.Ordinal)
        .Replace("@OWNER@", OwnerId, StringComparison.Ordinal)
        .Replace("@EMOTE@", EmoteId, StringComparison.Ordinal)
        .Replace("@ALIAS@", Alias, StringComparison.Ordinal);

    /// <summary>One read item with <c>defaultName</c>, as the read selection asks for it.</summary>
    public static string ReadItem(string emoteId, string alias, string? addedAt = "2026-11-24T16:55:23.498+00:00", string? addedById = BotId)
    {
        var addedAtPart = addedAt is null ? string.Empty : $",\"addedAt\":\"{addedAt}\"";
        var addedByPart = addedById is null ? string.Empty : $",\"addedById\":\"{addedById}\"";
        return $"{{\"alias\":\"{alias}\"{addedAtPart}{addedByPart},\"emote\":{{\"id\":\"{emoteId}\",\"defaultName\":\"{alias}\"}}}}";
    }
}
