namespace EmotePurge.Core.Services;

/// <summary>
/// The closed, language-neutral vocabulary of <c>reason</c> values of an invalid log filter. The frontend translates each
/// under <c>admin.logs.filterErrors.&lt;reason&gt;</c>, so a new value needs the same entry in both locale files.
/// </summary>
public static class LogFilterErrorReasons
{
    public const string UnterminatedQuote = "unterminated_quote";
    public const string EmptyTerm = "empty_term";
    public const string EmptyValue = "empty_value";
    public const string InvalidOperator = "invalid_operator";
    public const string InvalidLevel = "invalid_level";
    public const string InvalidSource = "invalid_source";
    public const string InvalidInstance = "invalid_instance";
    public const string InvalidDuration = "invalid_duration";
    public const string InvalidValue = "invalid_value";
    public const string TooLong = "too_long";
    public const string TooManyTerms = "too_many_terms";
    public const string TooManyValues = "too_many_values";
}

/// <summary>An invalid log filter. <see cref="Position"/> and <see cref="Length"/> are UTF-16 indices into the submitted string.</summary>
public sealed class LogFilterSyntaxException(string reason, int position, int length)
    : Exception($"Invalid log filter ({reason}) at {position}, length {length}.")
{
    public string Reason { get; } = reason;

    public int Position { get; } = position;

    public int Length { get; } = length;
}

/// <summary>An invalid, mixed or out-of-bounds time range request.</summary>
public sealed class LogRangeException(string message) : Exception(message);

/// <summary>A log query that exceeded its time limit.</summary>
public sealed class LogQueryTimeoutException : Exception
{
    public LogQueryTimeoutException()
        : base("The log query exceeded its time limit.") { }

    public LogQueryTimeoutException(Exception innerException)
        : base("The log query exceeded its time limit.", innerException) { }
}

/// <summary>Either a <c>range</c> preset (<c>1h</c>, <c>6h</c>, <c>24h</c>, <c>7d</c>) or an absolute <c>from</c>/<c>to</c> pair; nothing set means the default preset.</summary>
public sealed record LogRangeRequest(string? Range, DateTime? FromUtc, DateTime? ToUtc);
