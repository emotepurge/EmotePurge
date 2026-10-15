using System.Globalization;
using System.Text;
using EmotePurge.Core.Entities;
using Npgsql;
using NpgsqlTypes;

namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>
/// Translates a parsed <see cref="LogFilterExpression"/> into a SQL condition over <c>"LogEntries"</c> aliased
/// <c>e</c>. Every value — including a property key, which is user text — becomes a parameter (<c>@f0</c>,
/// <c>@f1</c> …); the SQL text holds only column names, operators and constants from closed sets, never a
/// filter value. Terms are joined with <c>AND</c>, the values of one term with <c>OR</c>; a negated term is
/// <c>NOT COALESCE(…, FALSE)</c> so a row whose column is <c>NULL</c> (no event, no exception, no such
/// property) counts as "does not match" and is kept by the negation. The SQL never contains braces: EF's raw
/// SQL APIs run <c>string.Format</c> over it.
/// </summary>
internal sealed class LogFilterSql
{
    private readonly List<(string Name, NpgsqlDbType Type, object Value)> _values;

    private LogFilterSql(string condition, List<(string Name, NpgsqlDbType Type, object Value)> values)
    {
        Condition = condition;
        _values = values;
    }

    /// <summary>The condition, <c>TRUE</c> for an empty filter.</summary>
    public string Condition { get; }

    public static LogFilterSql Translate(LogFilterExpression expression)
    {
        var builder = new Builder();
        var terms = expression.Terms.Select(builder.Term).ToList();
        var condition = terms.Count == 0 ? "TRUE" : string.Join(" AND ", terms);
        return new LogFilterSql(condition, builder.Values);
    }

    /// <summary>Fresh parameter objects for one command: Npgsql does not let one parameter belong to two commands.</summary>
    public IEnumerable<NpgsqlParameter> CreateParameters() =>
        _values.Select(v => new NpgsqlParameter(v.Name, v.Type) { Value = v.Value });

    private sealed class Builder
    {
        public List<(string Name, NpgsqlDbType Type, object Value)> Values { get; } = [];

        public string Term(LogFilterTerm term)
        {
            var condition = term switch
            {
                LevelTerm level => Level(level),
                SourceTerm source => $"e.\"Source\" = ANY({Add(NpgsqlDbType.Array | NpgsqlDbType.Smallint, source.Sources.Select(s => (short)s).ToArray())})",
                CategoryTerm category => AnyOf(category.Patterns.Select(Category)),
                EventTerm eventTerm => AnyOf(eventTerm.Patterns.Select(Event)),
                InstanceTerm instance => Instance(instance),
                UptimeTerm uptime => $"(e.\"TimestampUtc\" - e.\"ProcessStartedUtc\") {Comparison(uptime.Operator)} {Add(NpgsqlDbType.Interval, uptime.Duration)}",
                HasExceptionTerm => "e.\"Exception\" IS NOT NULL",
                PropertyTerm property => Property(property),
                FreeTextTerm text => FreeText(text),
                _ => throw new ArgumentOutOfRangeException(nameof(term), term.GetType().Name, "Unknown filter term."),
            };
            return term.Negated ? $"NOT COALESCE(({condition}), FALSE)" : $"({condition})";
        }

        private string Level(LevelTerm term)
        {
            if (term.Operator == LogFilterOperator.Equal)
            {
                return $"e.\"Level\" = ANY({Add(NpgsqlDbType.Array | NpgsqlDbType.Smallint, term.Levels.Select(l => (short)l).ToArray())})";
            }

            return $"e.\"Level\" {Comparison(term.Operator)} {Add(NpgsqlDbType.Smallint, (short)term.Levels.Single())}";
        }

        // Without a wildcard: the full name or a name ending in "." + value. With one: the pattern over the full
        // name or over any suffix after a dot. Both are the same two ILIKEs.
        private string Category(LogTextPattern pattern)
        {
            var value = Add(NpgsqlDbType.Text, pattern.ToIlikePattern());
            return $"e.\"Category\" ILIKE {value} OR e.\"Category\" ILIKE ('%.' || {value})";
        }

        private string Event(LogTextPattern pattern)
        {
            var name = $"e.\"EventName\" ILIKE {Add(NpgsqlDbType.Text, pattern.ToIlikePattern())}";
            return !pattern.HasWildcard && int.TryParse(pattern.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? $"e.\"EventId\" = {Add(NpgsqlDbType.Integer, id)} OR {name}"
                : name;
        }

        private string Instance(InstanceTerm term)
        {
            var parts = new List<string>();
            if (term.Latest)
            {
                // Per source the instance with the newest process start: from the status rows, else from the
                // entries themselves. Uncorrelated, so it runs once, not per row.
                var sources = string.Join(", ", Enum.GetValues<LogSource>().Select(s => ((short)s).ToString(CultureInfo.InvariantCulture) + "::smallint"));
                parts.Add(
                    "(e.\"Source\", e.\"InstanceId\") IN (SELECT src.s, COALESCE(" +
                    "(SELECT st.\"InstanceId\" FROM \"LogCaptureStatus\" st WHERE st.\"Source\" = src.s ORDER BY st.\"ProcessStartedUtc\" DESC, st.\"InstanceId\" DESC LIMIT 1), " +
                    "(SELECT le.\"InstanceId\" FROM \"LogEntries\" le WHERE le.\"Source\" = src.s ORDER BY le.\"ProcessStartedUtc\" DESC, le.\"InstanceId\" DESC LIMIT 1)) " +
                    $"FROM unnest(ARRAY[{sources}]) AS src(s))");
            }

            if (term.Ids.Count > 0)
            {
                parts.Add($"e.\"InstanceId\" = ANY({Add(NpgsqlDbType.Array | NpgsqlDbType.Text, term.Ids.ToArray())})");
            }

            return AnyOf(parts);
        }

        private string Property(PropertyTerm term)
        {
            var key = Add(NpgsqlDbType.Text, term.Key);
            if (term.IsPresenceCheck)
            {
                // "->" keeps a JSON null as a value, so this is presence, not "has a non-null value".
                return $"(e.\"Properties\" -> {key}) IS NOT NULL";
            }

            return AnyOf(term.Patterns.Select(p => $"(e.\"Properties\" ->> {key}) ILIKE {Add(NpgsqlDbType.Text, p.ToIlikePattern())}"));
        }

        private string FreeText(FreeTextTerm term)
        {
            var value = Add(NpgsqlDbType.Text, "%" + term.Pattern.ToIlikePattern() + "%");
            return $"e.\"Message\" ILIKE {value} OR e.\"Exception\" ILIKE {value}";
        }

        private string Add(NpgsqlDbType type, object value)
        {
            var name = "f" + Values.Count.ToString(CultureInfo.InvariantCulture);
            Values.Add((name, type, value));
            return "@" + name;
        }

        private static string AnyOf(IEnumerable<string> alternatives)
        {
            var builder = new StringBuilder();
            foreach (var alternative in alternatives)
            {
                builder.Append(builder.Length == 0 ? "(" : " OR (").Append(alternative).Append(')');
            }

            return builder.ToString();
        }

        private static string Comparison(LogFilterOperator op) => op switch
        {
            LogFilterOperator.Greater => ">",
            LogFilterOperator.GreaterOrEqual => ">=",
            LogFilterOperator.Less => "<",
            LogFilterOperator.LessOrEqual => "<=",
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not a comparison operator."),
        };
    }
}
