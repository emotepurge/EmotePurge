using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Logging.Query;

public enum LogFilterOperator
{
    /// <summary>The <c>:</c> operator (equality, or membership in a comma list).</summary>
    Equal,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
}

/// <summary>A typed filter value. <see cref="Value"/> is the unescaped text with any <c>*</c> still in place.</summary>
internal sealed record LogTextPattern(string Value, bool HasWildcard)
{
    /// <summary>The value as an <c>ILIKE</c> pattern (default <c>\</c> escape): <c>%</c>, <c>_</c> and <c>\</c> are masked, <c>*</c> becomes <c>%</c>.</summary>
    public string ToIlikePattern()
    {
        var builder = new System.Text.StringBuilder(Value.Length + 4);
        foreach (var c in Value)
        {
            switch (c)
            {
                case '\\':
                case '%':
                case '_':
                    builder.Append('\\').Append(c);
                    break;
                case '*':
                    builder.Append('%');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }
}

internal abstract record LogFilterTerm(bool Negated);

internal sealed record LevelTerm(LogFilterOperator Operator, IReadOnlyList<LogEntryLevel> Levels, bool Negated = false) : LogFilterTerm(Negated);

internal sealed record SourceTerm(IReadOnlyList<LogSource> Sources, bool Negated = false) : LogFilterTerm(Negated);

internal sealed record CategoryTerm(IReadOnlyList<LogTextPattern> Patterns, bool Negated = false) : LogFilterTerm(Negated);

/// <summary>Event name (case-insensitive) or numeric event id; the query service decides per pattern whether it is a number.</summary>
internal sealed record EventTerm(IReadOnlyList<LogTextPattern> Patterns, bool Negated = false) : LogFilterTerm(Negated);

/// <summary><paramref name="Latest"/> and/or lower-case 8-hex instance ids (OR within the term).</summary>
internal sealed record InstanceTerm(bool Latest, IReadOnlyList<string> Ids, bool Negated = false) : LogFilterTerm(Negated);

internal sealed record UptimeTerm(LogFilterOperator Operator, TimeSpan Duration, bool Negated = false) : LogFilterTerm(Negated);

internal sealed record HasExceptionTerm(bool Negated = false) : LogFilterTerm(Negated);

/// <summary>A property with the lower-cased <paramref name="Key"/> (user text: bind it as a SQL parameter, never interpolate it); a pattern that is just <c>*</c> means "property exists".</summary>
internal sealed record PropertyTerm(string Key, IReadOnlyList<LogTextPattern> Patterns, bool Negated = false) : LogFilterTerm(Negated)
{
    public bool IsPresenceCheck => Patterns.Any(p => p.Value == "*");
}

/// <summary>Substring match in the message or the exception (the query service wraps the pattern in <c>%</c>).</summary>
internal sealed record FreeTextTerm(LogTextPattern Pattern, bool Negated = false) : LogFilterTerm(Negated);

internal sealed record LogFilterExpression(IReadOnlyList<LogFilterTerm> Terms)
{
    public static LogFilterExpression Empty { get; } = new([]);
}

/// <summary>Outcome of <see cref="LogFilterParser.Parse"/>: an expression or a single error, never a partial result.</summary>
internal sealed record LogFilterParseResult
{
    public LogFilterExpression? Expression { get; private init; }

    public string? Reason { get; private init; }

    public int Position { get; private init; }

    public int Length { get; private init; }

    public bool IsSuccess => Expression is not null;

    public static LogFilterParseResult Success(LogFilterExpression expression) => new() { Expression = expression };

    public static LogFilterParseResult Failure(string reason, int position, int length) =>
        new()
        {
            Reason = reason,
            Position = position,
            Length = length,
        };

    /// <summary>The expression, or the matching <see cref="Core.Services.LogFilterSyntaxException"/>.</summary>
    public LogFilterExpression ExpressionOrThrow() =>
        Expression ?? throw new Core.Services.LogFilterSyntaxException(Reason!, Position, Length);
}
