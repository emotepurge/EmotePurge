using System.Text.Json;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// One 7TV GraphQL answer that arrived as HTTP 2xx with a JSON object body, reduced to what the write client decides on:
/// the <c>data</c> element and, per error, its path, <c>extensions.code</c>, <c>extensions.status</c> and a reset hint
/// from <c>extensions.headers</c>. The error <c>message</c> is never read, so it cannot reach a log or a decision
/// (plan #487 2.4: <c>LACKING_PRIVILEGES</c> carries two different messages and is decided by its code alone).
/// </summary>
internal sealed class SevenTvGqlReply
{
    /// <summary>Wait applied when 7TV rate-limits without saying for how long (AK76).</summary>
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>Ceiling for any wait 7TV asks for (AK76): a longer one is cut, never dropped.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(6);

    private const string LackingPrivilegesCode = "LACKING_PRIVILEGES";

    private SevenTvGqlReply(JsonElement? data, IReadOnlyList<SevenTvGqlError> errors)
    {
        Data = data;
        Errors = errors;
    }

    /// <summary>The <c>data</c> object, or <c>null</c> when it is missing, <c>null</c> or not an object.</summary>
    public JsonElement? Data { get; }

    public IReadOnlyList<SevenTvGqlError> Errors { get; }

    /// <summary>
    /// Reads a response body. <c>null</c> for anything that is not a JSON object: empty, cut off, an array, a scalar.
    /// </summary>
    public static SevenTvGqlReply? TryParse(string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object
            ? dataElement
            : (JsonElement?)null;

        var errors = new List<SevenTvGqlError>();
        if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array)
        {
            errors.AddRange(errorsElement.EnumerateArray().Select(ReadError));
        }

        return new SevenTvGqlReply(data, errors);
    }

    /// <summary>
    /// The element at <paramref name="path"/> below <c>data</c>, or <c>null</c> when any step is missing or JSON
    /// <c>null</c>.
    /// </summary>
    public JsonElement? Field(IReadOnlyList<string> path)
    {
        if (Data is not { } current)
        {
            return null;
        }

        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(name, out var next)
                || next.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// The errors that concern the field at <paramref name="fieldPath"/> itself: every error except those whose path lies
    /// strictly below it. An error without a path counts as concerning it, since nothing places it elsewhere.
    /// </summary>
    public IReadOnlyList<SevenTvGqlError> ErrorsNotBelow(IReadOnlyList<string> fieldPath) =>
        [.. Errors.Where(error => !error.IsStrictlyBelow(fieldPath))];

    /// <summary>True when any error, whatever its path, is a rate limit (<see cref="SevenTvGqlError.IsRateLimit"/>).</summary>
    public bool HasRateLimit => Errors.Any(error => error.IsRateLimit);

    /// <summary>
    /// The wait for a rate limit anywhere in the errors (first reset hint, default and ceiling as in <see cref="Classify"/>),
    /// or <c>null</c> when none carries one.
    /// </summary>
    public TimeSpan? RateLimitWait()
    {
        var rateLimited = Errors.Where(error => error.IsRateLimit).ToList();
        return rateLimited.Count == 0
            ? null
            : ClampRetryAfter(rateLimited.Select(error => error.ResetHintSeconds).FirstOrDefault(seconds => seconds is not null));
    }

    /// <summary>
    /// Maps GraphQL errors onto the closed result form (plan #487 2.4). In order: a rate limit anywhere wins (7TV did not
    /// do the work, and the wait must be honoured); then <c>LACKING_PRIVILEGES</c> or status 403; then 409 (alias taken);
    /// then any error without a status or with a non-4xx status leaves the outcome open (<see cref="SevenTvWriteOutcome.Unavailable"/>);
    /// only when every error is a 4xx without a known meaning is it <see cref="SevenTvWriteRejection.Unknown"/>. No error
    /// at all is <see cref="SevenTvWriteOutcome.Unavailable"/>: the caller only asks when its proof of success is missing.
    /// </summary>
    public static SevenTvWriteResult Classify(IReadOnlyList<SevenTvGqlError> errors)
    {
        var rateLimited = errors.Where(error => error.IsRateLimit).ToList();
        if (rateLimited.Count > 0)
        {
            var hint = rateLimited.Select(error => error.ResetHintSeconds).FirstOrDefault(seconds => seconds is not null);
            return SevenTvWriteResult.RateLimited(ClampRetryAfter(hint));
        }

        if (errors.Any(error => error.Code == LackingPrivilegesCode || error.Status == 403))
        {
            return SevenTvWriteResult.LackingPrivileges();
        }

        if (errors.Any(error => error.Status == 409))
        {
            return SevenTvWriteResult.Rejected(SevenTvWriteRejection.AliasConflict);
        }

        if (errors.Count == 0 || errors.Any(error => error.Status is not (>= 400 and < 500)))
        {
            return SevenTvWriteResult.Unavailable();
        }

        return SevenTvWriteResult.Rejected(SevenTvWriteRejection.Unknown);
    }

    /// <summary>A wait in seconds as 7TV gave it, or nothing: default 60 s, never above 6 h.</summary>
    public static TimeSpan ClampRetryAfter(int? seconds)
    {
        if (seconds is not > 0)
        {
            return DefaultRetryAfter;
        }

        var wait = TimeSpan.FromSeconds(seconds.Value);
        return wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    private static SevenTvGqlError ReadError(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object)
        {
            return new SevenTvGqlError(null, null, null, null);
        }

        string[]? path = null;
        if (error.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.Array)
        {
            // List indices arrive as numbers; kept as their text so a path compares element by element.
            path = [.. pathElement.EnumerateArray().Select(segment => segment.ValueKind == JsonValueKind.String
                ? segment.GetString() ?? string.Empty
                : segment.GetRawText())];
        }

        string? code = null;
        int? status = null;
        int? resetHint = null;
        if (error.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object)
        {
            if (extensions.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
            {
                code = codeElement.GetString();
            }

            if (extensions.TryGetProperty("status", out var statusElement) && TryReadInt(statusElement, out var statusValue))
            {
                status = statusValue;
            }

            if (extensions.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                resetHint = ReadResetHint(headers);
            }
        }

        return new SevenTvGqlError(path, code, status, resetHint);
    }

    // The reset hint 7TV may put next to a disguised 429: a header like `x-ratelimit-emote_set_change-reset` (the probe
    // read any `x-ratelimit-*reset`) or `retry-after`, in seconds, as a number or a numeric string.
    private static int? ReadResetHint(JsonElement headers)
    {
        foreach (var header in headers.EnumerateObject())
        {
            var isResetHeader = header.Name.StartsWith("x-ratelimit", StringComparison.OrdinalIgnoreCase)
                && header.Name.EndsWith("reset", StringComparison.OrdinalIgnoreCase);
            var isRetryAfter = string.Equals(header.Name, "retry-after", StringComparison.OrdinalIgnoreCase);
            if ((isResetHeader || isRetryAfter) && TryReadInt(header.Value, out var seconds) && seconds > 0)
            {
                return seconds;
            }
        }

        return null;
    }

    private static bool TryReadInt(JsonElement value, out int result)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt32(out result))
                {
                    return true;
                }

                // Larger than an int: still a wait, and one the 6 h ceiling cuts anyway.
                result = value.TryGetInt64(out var large) && large > 0 ? int.MaxValue : 0;
                return result > 0;
            case JsonValueKind.String:
                return int.TryParse(value.GetString(), out result);
            default:
                result = 0;
                return false;
        }
    }
}

/// <summary>
/// One GraphQL error, without its message. <see cref="Path"/> is <c>null</c> when the error names none.
/// </summary>
internal sealed record SevenTvGqlError(IReadOnlyList<string>? Path, string? Code, int? Status, int? ResetHintSeconds)
{
    private const string RateLimitExceededCode = "RATE_LIMIT_EXCEEDED";

    /// <summary>
    /// The one rate-limit predicate: <c>extensions.status</c> 429 or <c>extensions.code</c> <c>RATE_LIMIT_EXCEEDED</c>. Used
    /// by <see cref="SevenTvGqlReply.Classify"/> and by the client's telemetry, so the two cannot disagree.
    /// </summary>
    public bool IsRateLimit => Status == 429 || Code == RateLimitExceededCode;

    /// <summary>True when the error's path continues past <paramref name="fieldPath"/>, i.e. it concerns a sub-selection.</summary>
    public bool IsStrictlyBelow(IReadOnlyList<string> fieldPath)
    {
        if (Path is null || Path.Count <= fieldPath.Count)
        {
            return false;
        }

        for (var i = 0; i < fieldPath.Count; i++)
        {
            if (!string.Equals(Path[i], fieldPath[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
