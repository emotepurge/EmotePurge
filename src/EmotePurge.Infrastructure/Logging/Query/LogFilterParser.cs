using System.Globalization;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Logging.Query;

/// <summary>
/// The one parser of the log filter language (grammar and semantics: spec section 7). Pure; every error carries a
/// <see cref="LogFilterErrorReasons"/> value and a UTF-16 position and length, and no partial result is ever returned.
/// </summary>
internal static class LogFilterParser
{
    public const int MaxFilterLength = 512;
    public const int MaxTerms = 16;
    public const int MaxValues = 16;
    public const int MaxValueLength = 200;

    public static LogFilterParseResult Parse(string? filter)
    {
        if (filter is { Length: > MaxFilterLength })
        {
            return LogFilterParseResult.Failure(LogFilterErrorReasons.TooLong, MaxFilterLength, filter.Length - MaxFilterLength);
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            return LogFilterParseResult.Success(LogFilterExpression.Empty);
        }

        try
        {
            return LogFilterParseResult.Success(new Scanner(filter).ParseAll());
        }
        catch (LogFilterSyntaxException ex)
        {
            return LogFilterParseResult.Failure(ex.Reason, ex.Position, ex.Length);
        }
    }

    private readonly record struct RawValue(string Text, int Start, int Length);

    private sealed class Scanner(string input)
    {
        private int _pos;

        public LogFilterExpression ParseAll()
        {
            if (input.Length > MaxFilterLength)
            {
                throw Error(LogFilterErrorReasons.TooLong, MaxFilterLength, input.Length - MaxFilterLength);
            }

            var terms = new List<LogFilterTerm>();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= input.Length)
                {
                    break;
                }

                var start = _pos;
                var term = ParseTerm();
                if (terms.Count >= MaxTerms)
                {
                    throw Error(LogFilterErrorReasons.TooManyTerms, start, _pos - start);
                }
                terms.Add(term);
            }
            return new LogFilterExpression(terms);
        }

        private LogFilterTerm ParseTerm()
        {
            var negated = false;
            var start = _pos;
            if (input[_pos] == '-')
            {
                negated = true;
                _pos++;
                if (_pos >= input.Length || char.IsWhiteSpace(input[_pos]))
                {
                    throw Error(LogFilterErrorReasons.EmptyTerm, start, 1);
                }
            }

            var keyStart = _pos;
            if (char.IsLetter(input[_pos]))
            {
                var end = _pos;
                while (end < input.Length && IsKeyChar(input[end]))
                {
                    end++;
                }
                if (TryReadOperator(end, out var op, out var opLength))
                {
                    var key = input[keyStart..end].ToLowerInvariant();
                    var opStart = end;
                    _pos = end + opLength;
                    return ParseKeyTerm(key, op, opStart, opLength, negated);
                }
            }

            return ParseFreeText(negated);
        }

        private FreeTextTerm ParseFreeText(bool negated)
        {
            RawValue value;
            if (input[_pos] == '"')
            {
                value = ReadQuoted();
                if (value.Text.Length == 0)
                {
                    throw Error(LogFilterErrorReasons.EmptyTerm, value.Start, value.Length);
                }
                EnsureTermBoundaryAfterQuote();
            }
            else
            {
                var start = _pos;
                while (_pos < input.Length && !char.IsWhiteSpace(input[_pos]))
                {
                    _pos++;
                }
                value = new RawValue(input[start.._pos], start, _pos - start);
            }
            CheckLength(value);
            return new FreeTextTerm(ToPattern(value.Text), negated);
        }

        private LogFilterTerm ParseKeyTerm(string key, LogFilterOperator op, int opStart, int opLength, bool negated)
        {
            var allowed = key switch
            {
                "level" => true,
                "uptime" => op != LogFilterOperator.Equal,
                _ => op == LogFilterOperator.Equal,
            };
            if (!allowed)
            {
                throw Error(LogFilterErrorReasons.InvalidOperator, opStart, opLength);
            }

            var values = ReadValueList(opStart, opLength);
            var multi = op != LogFilterOperator.Equal || key is "uptime" or "has";
            if (multi && values.Count > 1)
            {
                throw Error(LogFilterErrorReasons.InvalidValue, values[1].Start, values[1].Length);
            }

            switch (key)
            {
                case "level":
                    return new LevelTerm(op, values.Select(ParseLevel).ToList(), negated);
                case "source":
                    return new SourceTerm(values.Select(ParseSource).ToList(), negated);
                case "cat":
                    return new CategoryTerm(values.Select(v => ToPattern(v.Text)).ToList(), negated);
                case "event":
                    return new EventTerm(values.Select(v => ToPattern(v.Text)).ToList(), negated);
                case "instance":
                    return ParseInstance(values, negated);
                case "uptime":
                    return new UptimeTerm(op, ParseDuration(values[0]), negated);
                case "has":
                    RejectWildcard(values[0]);
                    if (!string.Equals(values[0].Text, "exception", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Error(LogFilterErrorReasons.InvalidValue, values[0].Start, values[0].Length);
                    }
                    return new HasExceptionTerm(negated);
                default:
                    return new PropertyTerm(key, values.Select(v => ToPattern(v.Text)).ToList(), negated);
            }
        }

        private List<RawValue> ReadValueList(int delimiterStart, int delimiterLength)
        {
            var values = new List<RawValue>();
            while (true)
            {
                var value = ReadValue(delimiterStart, delimiterLength);
                if (values.Count >= MaxValues)
                {
                    throw Error(LogFilterErrorReasons.TooManyValues, value.Start, value.Length);
                }
                CheckLength(value);
                values.Add(value);

                if (_pos < input.Length && input[_pos] == ',')
                {
                    delimiterStart = _pos;
                    delimiterLength = 1;
                    _pos++;
                    continue;
                }
                return values;
            }
        }

        private RawValue ReadValue(int delimiterStart, int delimiterLength)
        {
            if (_pos >= input.Length || char.IsWhiteSpace(input[_pos]) || input[_pos] == ',')
            {
                throw Error(LogFilterErrorReasons.EmptyValue, delimiterStart, delimiterLength);
            }

            if (input[_pos] == '"')
            {
                var quoted = ReadQuoted();
                if (quoted.Text.Length == 0)
                {
                    throw Error(LogFilterErrorReasons.EmptyValue, quoted.Start, quoted.Length);
                }
                EnsureValueBoundaryAfterQuote();
                return quoted;
            }

            var start = _pos;
            while (_pos < input.Length && !char.IsWhiteSpace(input[_pos]) && input[_pos] != ',')
            {
                _pos++;
            }
            return new RawValue(input[start.._pos], start, _pos - start);
        }

        private RawValue ReadQuoted()
        {
            var start = _pos;
            _pos++;
            var text = new System.Text.StringBuilder();
            while (_pos < input.Length)
            {
                var c = input[_pos];
                if (c == '\\' && _pos + 1 < input.Length && input[_pos + 1] == '"')
                {
                    text.Append('"');
                    _pos += 2;
                    continue;
                }
                if (c == '"')
                {
                    _pos++;
                    return new RawValue(text.ToString(), start, _pos - start);
                }
                text.Append(c);
                _pos++;
            }
            throw Error(LogFilterErrorReasons.UnterminatedQuote, start, 1);
        }

        private void EnsureTermBoundaryAfterQuote()
        {
            if (_pos < input.Length && !char.IsWhiteSpace(input[_pos]))
            {
                throw Error(LogFilterErrorReasons.InvalidValue, _pos, 1);
            }
        }

        private void EnsureValueBoundaryAfterQuote()
        {
            if (_pos < input.Length && !char.IsWhiteSpace(input[_pos]) && input[_pos] != ',')
            {
                throw Error(LogFilterErrorReasons.InvalidValue, _pos, 1);
            }
        }

        private bool TryReadOperator(int at, out LogFilterOperator op, out int length)
        {
            op = LogFilterOperator.Equal;
            length = 1;
            if (at >= input.Length)
            {
                return false;
            }

            var next = at + 1 < input.Length ? input[at + 1] : '\0';
            switch (input[at])
            {
                case ':':
                    return true;
                case '>':
                    op = next == '=' ? LogFilterOperator.GreaterOrEqual : LogFilterOperator.Greater;
                    length = next == '=' ? 2 : 1;
                    return true;
                case '<':
                    op = next == '=' ? LogFilterOperator.LessOrEqual : LogFilterOperator.Less;
                    length = next == '=' ? 2 : 1;
                    return true;
                default:
                    return false;
            }
        }

        private LogEntryLevel ParseLevel(RawValue value)
        {
            RejectWildcard(value);
            return value.Text.ToLowerInvariant() switch
            {
                "info" or "information" => LogEntryLevel.Information,
                "warn" or "warning" => LogEntryLevel.Warning,
                "error" or "err" => LogEntryLevel.Error,
                "crit" or "critical" or "fatal" => LogEntryLevel.Critical,
                _ => throw Error(LogFilterErrorReasons.InvalidLevel, value.Start, value.Length),
            };
        }

        private LogSource ParseSource(RawValue value) =>
            value.Text.ToLowerInvariant() switch
            {
                "api" => LogSource.Api,
                "worker" => LogSource.Worker,
                _ => throw Error(LogFilterErrorReasons.InvalidSource, value.Start, value.Length),
            };

        private InstanceTerm ParseInstance(List<RawValue> values, bool negated)
        {
            var latest = false;
            var ids = new List<string>();
            foreach (var value in values)
            {
                if (string.Equals(value.Text, "latest", StringComparison.OrdinalIgnoreCase))
                {
                    latest = true;
                }
                else if (value.Text.Length == 8 && value.Text.All(Uri.IsHexDigit))
                {
                    ids.Add(value.Text.ToLowerInvariant());
                }
                else
                {
                    throw Error(LogFilterErrorReasons.InvalidInstance, value.Start, value.Length);
                }
            }
            return new InstanceTerm(latest, ids, negated);
        }

        private TimeSpan ParseDuration(RawValue value)
        {
            RejectWildcard(value);
            var text = value.Text.ToLowerInvariant();
            if (text.Length >= 2 && text[..^1].All(char.IsAsciiDigit) && long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                var unit = text[^1] switch
                {
                    's' => TimeSpan.TicksPerSecond,
                    'm' => TimeSpan.TicksPerMinute,
                    'h' => TimeSpan.TicksPerHour,
                    'd' => TimeSpan.TicksPerDay,
                    _ => 0L,
                };
                if (unit != 0 && amount <= TimeSpan.MaxValue.Ticks / unit)
                {
                    return TimeSpan.FromTicks(amount * unit);
                }
            }
            throw Error(LogFilterErrorReasons.InvalidDuration, value.Start, value.Length);
        }

        private void RejectWildcard(RawValue value)
        {
            if (value.Text.Contains('*'))
            {
                throw Error(LogFilterErrorReasons.InvalidValue, value.Start, value.Length);
            }
        }

        private void CheckLength(RawValue value)
        {
            if (value.Text.Length > MaxValueLength)
            {
                throw Error(LogFilterErrorReasons.TooLong, value.Start, value.Length);
            }
        }

        private void SkipWhitespace()
        {
            while (_pos < input.Length && char.IsWhiteSpace(input[_pos]))
            {
                _pos++;
            }
        }

        private static bool IsKeyChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

        private static LogTextPattern ToPattern(string text) => new(text, text.Contains('*'));

        private static LogFilterSyntaxException Error(string reason, int position, int length) => new(reason, position, length);
    }
}
