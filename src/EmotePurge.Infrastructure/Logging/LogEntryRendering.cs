using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EmotePurge.Core.Logging;

namespace EmotePurge.Infrastructure.Logging;

internal sealed record RenderedException(string Type, string Text, bool Truncated);

internal sealed record RenderedProperties(string? Json, bool Truncated);

/// <summary>
/// Bounded, side-effect-free rendering of the parts of a log call that are stored: message, exception
/// and structured state. Nothing here calls <c>ToString()</c> on caller-supplied objects (a hostile or
/// recursive <c>ToString()</c> must not stall the logging thread) and every produced string is safe for
/// Postgres <c>text</c>/<c>jsonb</c>.
/// </summary>
internal static class LogEntryRendering
{
    internal const int MaxMessageLength = 4096;
    internal const int MaxExceptionLength = 16384;
    internal const int MaxExceptionMessageLength = 1024;
    internal const int MaxExceptionTypeLength = 256;
    internal const int MaxChainDepth = 8;
    internal const int MaxProperties = 16;
    internal const int MaxStateIndices = 17;
    internal const int MaxPropertyValueLength = 256;
    internal const int MaxPropertiesJsonLength = 4096;
    internal const int MaxCollectionElements = 20;
    private const int ScrubWindow = 8192;
    internal const string TruncatedMarker = "…[truncated]";

    private const string OriginalFormatKey = "{OriginalFormat}";

    /// <summary><c>U+0000</c> becomes <c>U+2400</c>, an unpaired surrogate becomes <c>U+FFFD</c>.</summary>
    public static string NormalizeForPostgres(string value)
    {
        var first = FirstInvalidIndex(value);
        if (first < 0)
        {
            return value;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                if (c == '\0')
                {
                    span[i] = '␀';
                }
                else if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    span[i] = c;
                    span[++i] = source[i];
                }
                else if (char.IsSurrogate(c))
                {
                    span[i] = '�';
                }
                else
                {
                    span[i] = c;
                }
            }
        });
    }

    /// <remarks>Does not scrub: the caller scrubs the formatted message before calling this.</remarks>
    public static (string Text, bool Truncated) TruncateMessage(string message)
    {
        if (message.Length <= MaxMessageLength)
        {
            return (message, false);
        }

        return (CutSafely(message, MaxMessageLength) + TruncatedMarker, true);
    }

    /// <remarks>
    /// Link messages are scrubbed before the per-link cut. Stack traces are not scrubbed.
    /// </remarks>
    public static RenderedException RenderException(Exception exception, bool withholdMessages)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var type = exception is WithheldMessageException withheld
            ? withheld.OriginalTypeName
            : TypeNameOf(exception);
        var truncated = false;
        if (type.Length > MaxExceptionTypeLength)
        {
            type = CutSafely(type, MaxExceptionTypeLength);
            truncated = true;
        }

        var builder = new StringBuilder();
        var state = new ChainState(builder, withholdMessages);
        WalkException(exception, 1, state);

        var text = builder.ToString();
        truncated |= state.Truncated;
        return new RenderedException(NormalizeForPostgres(type), NormalizeForPostgres(text), truncated);
    }

    /// <remarks>
    /// String values are scrubbed with <see cref="SecretScrubber"/> here, before normalization and the
    /// 256-character cut; the returned JSON must not be scrubbed again (a scrub over JSON can eat the
    /// closing quote and brace).
    /// </remarks>
    public static RenderedProperties RenderProperties<TState>(TState state, string? requestPath, string? traceId)
    {
        var entries = new List<(string Key, object? Value)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        var realKeys = 0;

        if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
        {
            var limit = SafeCount(list);
            if (limit > MaxStateIndices)
            {
                // Entries beyond index 16 are never read; at least one of them is a real property.
                truncated = true;
            }

            limit = Math.Min(limit, MaxStateIndices);
            for (var i = 0; i < limit; i++)
            {
                var pair = list[i];
                var rawKey = pair.Key;
                if (rawKey is null || string.Equals(rawKey, OriginalFormatKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                realKeys++;
                AddProperty(entries, keys, rawKey, pair.Value, ref truncated);
            }

            if (realKeys > MaxProperties)
            {
                truncated = true;
            }
        }

        AddScopeProperty(entries, keys, "requestpath", requestPath, ref truncated);
        AddScopeProperty(entries, keys, "traceid", traceId, ref truncated);

        if (entries.Count == 0)
        {
            return new RenderedProperties(null, truncated);
        }

        return new RenderedProperties(SerializeBounded(entries, ref truncated), truncated);
    }

    private static int FirstInvalidIndex(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0')
            {
                return i;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return i;
            }
        }

        return -1;
    }

    internal static string CutSafely(string value, int length)
    {
        if (value.Length <= length)
        {
            return value;
        }

        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    private static string TypeNameOf(Exception exception)
    {
        var type = exception.GetType();
        return type.FullName ?? type.Name;
    }

    private static void WalkException(Exception exception, int depth, ChainState state)
    {
        if (state.BudgetExhausted)
        {
            return;
        }

        if (exception is WithheldMessageException withheld)
        {
            foreach (var link in withheld.Chain)
            {
                AppendLink(state, link.TypeName, null, link.StackTrace);
            }

            return;
        }

        string? message = null;
        if (!state.WithholdMessages)
        {
            message = SafeMessage(exception);
        }

        AppendLink(state, TypeNameOf(exception), message, SafeStackTrace(exception));

        IReadOnlyList<Exception> inners = exception switch
        {
            AggregateException aggregate => aggregate.InnerExceptions,
            { InnerException: { } inner } => [inner],
            _ => [],
        };
        if (inners.Count == 0)
        {
            return;
        }

        if (depth >= MaxChainDepth)
        {
            state.Truncated = true;
            return;
        }

        foreach (var inner in inners)
        {
            WalkException(inner, depth + 1, state);
            if (state.BudgetExhausted)
            {
                return;
            }
        }
    }

    private static void AppendLink(ChainState state, string typeName, string? message, string? stackTrace)
    {
        var builder = state.Builder;
        if (builder.Length > 0)
        {
            Append(state, "\n--- ");
        }

        Append(state, typeName);
        if (!string.IsNullOrEmpty(message))
        {
            Append(state, ": ");
            // Scrub before the cut so a limit cannot split a secret into an unrecognizable fragment.
            // The pre-cut only bounds regex cost; anything past it is discarded by the final cut anyway.
            var scrubbed = SecretScrubber.Scrub(CutSafely(message, ScrubWindow));
            if (message.Length > MaxExceptionMessageLength || scrubbed.Length > MaxExceptionMessageLength)
            {
                Append(state, CutSafely(scrubbed, MaxExceptionMessageLength));
                state.Truncated = true;
            }
            else
            {
                Append(state, scrubbed);
            }
        }

        if (!string.IsNullOrEmpty(stackTrace))
        {
            Append(state, "\n");
            Append(state, stackTrace);
        }
    }

    private static void Append(ChainState state, string text)
    {
        if (state.BudgetExhausted)
        {
            return;
        }

        var remaining = MaxExceptionLength - state.Builder.Length;
        if (text.Length <= remaining)
        {
            state.Builder.Append(text);
            return;
        }

        state.Builder.Append(CutSafely(text, remaining));
        state.Truncated = true;
        state.BudgetExhausted = true;
    }

    private static string? SafeMessage(Exception exception)
    {
        try
        {
            return exception.Message;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeStackTrace(Exception exception)
    {
        try
        {
            return exception.StackTrace;
        }
        catch
        {
            return null;
        }
    }

    private static int SafeCount(IReadOnlyList<KeyValuePair<string, object?>> list)
    {
        try
        {
            return Math.Max(0, list.Count);
        }
        catch
        {
            return 0;
        }
    }

    private static void AddScopeProperty(List<(string Key, object? Value)> entries, HashSet<string> keys, string key, string? value, ref bool truncated)
    {
        if (!string.IsNullOrEmpty(value))
        {
            AddProperty(entries, keys, key, value, ref truncated);
        }
    }

    private static void AddProperty(List<(string Key, object? Value)> entries, HashSet<string> keys, string rawKey, object? value, ref bool truncated)
    {
        var key = NormalizeForPostgres(rawKey.ToLowerInvariant());
        if (key.Length == 0 || keys.Contains(key))
        {
            return;
        }

        if (entries.Count >= MaxProperties)
        {
            truncated = true;
            return;
        }

        keys.Add(key);
        entries.Add((key, RenderValue(value, ref truncated)));
    }

    private static string? SerializeBounded(List<(string Key, object? Value)> entries, ref bool truncated)
    {
        var fragments = new List<string>(entries.Count);
        var size = 2;
        foreach (var (key, value) in entries)
        {
            var fragment = JsonSerializer.Serialize(key, JsonSerializerOptionsCache.Options) + ":" + JsonSerializer.Serialize(value, JsonSerializerOptionsCache.Options);
            var added = fragment.Length + (fragments.Count > 0 ? 1 : 0);
            if (size + added > MaxPropertiesJsonLength)
            {
                truncated = true;
                break;
            }

            size += added;
            fragments.Add(fragment);
        }

        return fragments.Count == 0 ? null : "{" + string.Join(',', fragments) + "}";
    }

    private static object? RenderValue(object? value, ref bool truncated)
    {
        switch (value)
        {
            case null:
                return null;
            case bool b:
                return b;
            case string s:
                return LimitValue(s, ref truncated);
            case char c:
                return LimitValue(c.ToString(), ref truncated);
        }

        if (TryRenderScalar(value, out var scalar))
        {
            return scalar is string text ? LimitValue(text, ref truncated) : scalar;
        }

        if (IsFiniteCollection(value))
        {
            return LimitValue(JoinCollection((IEnumerable)value), ref truncated);
        }

        return LimitValue("<" + (value.GetType().FullName ?? value.GetType().Name) + ">", ref truncated);
    }

    private static string LimitValue(string value, ref bool truncated)
    {
        // Scrub first (see RenderProperties); the pre-cut only bounds regex cost.
        var wasCut = value.Length > ScrubWindow;
        value = NormalizeForPostgres(SecretScrubber.Scrub(CutSafely(value, ScrubWindow)));
        if (wasCut)
        {
            truncated = true;
        }

        if (value.Length <= MaxPropertyValueLength)
        {
            return value;
        }

        truncated = true;
        return CutSafely(value, MaxPropertyValueLength);
    }

    private static bool TryRenderScalar(object value, out object? scalar)
    {
        scalar = value switch
        {
            sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ulong u => u,
            nint n => (long)n,
            nuint nu => (ulong)nu,
            decimal d => d,
            double d => FiniteOrText(d),
            float f => FiniteOrText(double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)),
            Half h => FiniteOrText((double)h),
            BigInteger bi => bi.ToString(CultureInfo.InvariantCulture),
            Int128 i128 => i128.ToString(null, CultureInfo.InvariantCulture),
            UInt128 u128 => u128.ToString(null, CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            Guid g => g.ToString("D", CultureInfo.InvariantCulture),
            TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
            DateTime dt => FormatUtc(dt),
            DateTimeOffset dto => FormatUtc(dto.UtcDateTime),
            _ => NoScalar.Instance,
        };
        return !ReferenceEquals(scalar, NoScalar.Instance);
    }

    private static object FiniteOrText(double d) =>
        double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture);

    private static string FormatUtc(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static bool IsFiniteCollection(object value)
    {
        if (value is Array or ICollection)
        {
            return true;
        }

        foreach (var contract in value.GetType().GetInterfaces())
        {
            if (contract.IsGenericType)
            {
                var definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(ICollection<>) || definition == typeof(IReadOnlyCollection<>))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string JoinCollection(IEnumerable collection)
    {
        // Stop accumulating once the joined text is past the value limit; the caller cuts it anyway.
        var builder = new StringBuilder();
        var count = 0;
        foreach (var element in collection)
        {
            if (count++ >= MaxCollectionElements || builder.Length > MaxPropertyValueLength)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append(", ");
            }

            // Scrub before the element cut, as LimitValue does for scalars: a cut token can fall under
            // the scrubber's minimum length. The ScrubWindow pre-cut only bounds regex cost.
            var rendered = SecretScrubber.Scrub(CutSafely(RenderElement(element), ScrubWindow));
            builder.Append(CutSafely(rendered, MaxPropertyValueLength + 1));
        }

        return builder.ToString();
    }

    private static string RenderElement(object? element)
    {
        switch (element)
        {
            case null:
                return string.Empty;
            case string s:
                return s;
            case bool b:
                return b ? "true" : "false";
            case char c:
                return c.ToString();
        }

        if (TryRenderScalar(element, out var scalar))
        {
            return Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return "<" + (element.GetType().FullName ?? element.GetType().Name) + ">";
    }

    private sealed class ChainState(StringBuilder builder, bool withholdMessages)
    {
        public StringBuilder Builder { get; } = builder;

        public bool WithholdMessages { get; } = withholdMessages;

        public bool Truncated { get; set; }

        public bool BudgetExhausted { get; set; }
    }

    private sealed class NoScalar
    {
        public static readonly NoScalar Instance = new();
    }

    private static class JsonSerializerOptionsCache
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
