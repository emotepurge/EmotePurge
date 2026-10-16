using Xunit;
using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using EmotePurge.Core.Logging;
using EmotePurge.Infrastructure.Logging;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogEntryRenderingTests
{
    private static IReadOnlyList<KeyValuePair<string, object?>> State(params (string Key, object? Value)[] items) =>
        items.Select(i => new KeyValuePair<string, object?>(i.Key, i.Value)).ToList();

    private static JsonElement Parse(RenderedProperties rendered)
    {
        Assert.NotNull(rendered.Json);
        return JsonDocument.Parse(rendered.Json).RootElement;
    }

    // --- normalization ---

    [Fact]
    public void NormalizeForPostgres_Nul_BecomesSymbol()
    {
        Assert.Equal("a␀b", LogEntryRendering.NormalizeForPostgres("a\0b"));
    }

    [Theory]
    [InlineData("a\uD800b")]
    [InlineData("a\uDC00b")]
    [InlineData("a\uD800")]
    [InlineData("\uDC00")]
    public void NormalizeForPostgres_LoneSurrogate_BecomesReplacementChar(string input)
    {
        var result = LogEntryRendering.NormalizeForPostgres(input);
        Assert.Contains('�', result);
        Assert.Equal(input.Length, result.Length);
        Assert.DoesNotContain(result, char.IsSurrogate);
    }

    [Fact]
    public void NormalizeForPostgres_ValidEmoji_Unchanged()
    {
        var value = "ok \U0001F600 done";
        Assert.Same(value, LogEntryRendering.NormalizeForPostgres(value));
        Assert.Equal("x\U0001F600␀", LogEntryRendering.NormalizeForPostgres("x\U0001F600\0"));
    }

    // --- message ---

    [Fact]
    public void TruncateMessage_AtLimit_Unchanged()
    {
        var message = new string('a', 4096);
        Assert.Equal((message, false), LogEntryRendering.TruncateMessage(message));
    }

    [Fact]
    public void TruncateMessage_OverLimit_CutsAndMarks()
    {
        var (text, truncated) = LogEntryRendering.TruncateMessage(new string('a', 4097));
        Assert.True(truncated);
        Assert.Equal(new string('a', 4096) + "…[truncated]", text);
    }

    [Fact]
    public void TruncateMessage_CutInsideSurrogatePair_DoesNotSplitIt()
    {
        var message = new string('a', 4095) + "\U0001F600" + "tail";
        var (text, _) = LogEntryRendering.TruncateMessage(message);
        Assert.Equal(new string('a', 4095) + "…[truncated]", text);
    }

    // --- exceptions ---

    private sealed class HostileException : Exception
    {
        public static int ToStringCalls;

        public override string ToString()
        {
            ToStringCalls++;
            return ToString();
        }
    }

    [Fact]
    public void RenderException_HostileToString_IsNeverCalled()
    {
        HostileException.ToStringCalls = 0;
        var rendered = LogEntryRendering.RenderException(new HostileException(), false);
        Assert.Equal(0, HostileException.ToStringCalls);
        Assert.Equal(typeof(HostileException).FullName, rendered.Type);
    }

    [Fact]
    public void RenderException_ThrownWithInner_ListsTypesMessagesAndStacks()
    {
        Exception caught;
        try
        {
            try
            {
                throw new InvalidOperationException("inner boom");
            }
            catch (Exception inner)
            {
                throw new ArgumentException("outer boom", inner);
            }
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        var rendered = LogEntryRendering.RenderException(caught, false);
        Assert.Equal("System.ArgumentException", rendered.Type);
        Assert.Contains("System.ArgumentException: outer boom", rendered.Text);
        Assert.Contains("System.InvalidOperationException: inner boom", rendered.Text);
        Assert.Contains("RenderException_ThrownWithInner", rendered.Text);
        Assert.False(rendered.Truncated);
    }

    [Fact]
    public void RenderException_FiftyLevelChain_StaysWithinBudgetAndEightLevels()
    {
        Exception chain = new Exception(new string('m', 2000));
        for (var i = 0; i < 49; i++)
        {
            chain = new Exception(new string('m', 2000), chain);
        }

        var rendered = LogEntryRendering.RenderException(chain, false);
        Assert.True(rendered.Truncated);
        Assert.True(rendered.Text.Length <= 16384);
        Assert.True(rendered.Text.Split("\n--- ").Length <= 8);
    }

    [Fact]
    public void RenderException_OneMiBMessage_CutAndTruncated()
    {
        var rendered = LogEntryRendering.RenderException(new Exception(new string('x', 1024 * 1024)), false);
        Assert.True(rendered.Truncated);
        Assert.True(rendered.Text.Length < 16384);
        Assert.True(rendered.Text.Length <= "System.Exception: ".Length + 1024);
    }

    [Fact]
    public void RenderException_AggregateOfMany_StopsAtBudget()
    {
        var inners = Enumerable.Range(0, 500).Select(i => new Exception(new string('z', 1000))).ToArray();
        var rendered = LogEntryRendering.RenderException(new AggregateException("agg", inners), false);
        Assert.True(rendered.Truncated);
        Assert.Equal(16384, rendered.Text.Length);
    }

    [Fact]
    public void RenderException_Withheld_UsesOriginalTypeAndNoMessage()
    {
        var withheld = WithheldMessageException.From(new FormatException("GEHEIMTEXT from chat"));
        var rendered = LogEntryRendering.RenderException(withheld, false);
        Assert.Equal("System.FormatException", rendered.Type);
        Assert.Contains("System.FormatException", rendered.Text);
        Assert.DoesNotContain("GEHEIMTEXT", rendered.Text);
        Assert.DoesNotContain("message withheld", rendered.Text);
    }

    [Fact]
    public void RenderException_WithholdMessages_OmitsMessagesKeepsTypes()
    {
        var chain = new InvalidOperationException("GEHEIMTEXT outer", new ArgumentException("GEHEIMTEXT inner"));
        var rendered = LogEntryRendering.RenderException(chain, withholdMessages: true);
        Assert.DoesNotContain("GEHEIMTEXT", rendered.Text);
        Assert.Contains("System.InvalidOperationException", rendered.Text);
        Assert.Contains("System.ArgumentException", rendered.Text);
    }

    [Fact]
    public void RenderException_NulAndLoneSurrogateInMessage_AreNormalized()
    {
        var rendered = LogEntryRendering.RenderException(new Exception("a\0b\uD800"), false);
        Assert.DoesNotContain('\0', rendered.Text);
        Assert.DoesNotContain(rendered.Text, char.IsSurrogate);
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");

        public override string? StackTrace => throw new InvalidOperationException("no stack");
    }

    [Fact]
    public void RenderException_ThrowingMessageAndStack_DoesNotThrow()
    {
        var rendered = LogEntryRendering.RenderException(new ThrowingMessageException(), false);
        Assert.Contains(nameof(ThrowingMessageException), rendered.Text);
    }

    [Fact]
    public void RenderException_ExceptionTypeOver256_IsCutToColumnWidth()
    {
        var exception = new GenericException<Dictionary<string, List<Dictionary<string, List<int>>>>>();
        Assert.True(exception.GetType().FullName!.Length > 256);
        var rendered = LogEntryRendering.RenderException(exception, false);
        Assert.Equal(256, rendered.Type.Length);
        Assert.True(rendered.Truncated);
    }

    [Fact]
    public void RenderException_BearerTokenStraddlingMessageLimit_LeavesNoTokenCharacters()
    {
        var token = new string('T', 200);
        var message = new string('a', 1000) + " Bearer " + token + " tail";
        var rendered = LogEntryRendering.RenderException(new Exception(message), false);
        Assert.DoesNotContain("TTTT", rendered.Text);
    }

    // --- properties ---

    [Fact]
    public void RenderProperties_PasswordInValue_IsScrubbedAndJsonStaysValid()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("conn", "Host=x;Password=abc")), null, null);
        Assert.Equal("Host=x;Password=[redacted]", Parse(rendered).GetProperty("conn").GetString());
    }

    [Fact]
    public void RenderProperties_BearerStraddlingValueLimit_LeavesNoTokenCharacters()
    {
        var value = new string('a', 250) + " Bearer " + new string('T', 100);
        var rendered = LogEntryRendering.RenderProperties(State(("v", value)), null, null);
        Assert.DoesNotContain("TTTT", rendered.Json);
    }

    // 234 + " Bearer " puts the token start at index 242: the 257 element cut would leave 15 token
    // characters, one short of the scrubber's 16-character minimum.
    private static string BearerStraddlingElementCut() => new string('a', 234) + " Bearer " + new string('T', 32);

    [Fact]
    public void RenderProperties_CollectionElementBearerStraddlingElementCut_LeavesNoTokenCharacters()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("c", new[] { BearerStraddlingElementCut() })), null, null);
        var value = Parse(rendered).GetProperty("c").GetString();
        Assert.Contains("Bearer [redacted]", value);
        Assert.DoesNotContain("TT", value);
    }

    [Fact]
    public void RenderProperties_SecondCollectionElementBearerStraddlingElementCut_LeavesNoTokenCharacters()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("c", new[] { "x", BearerStraddlingElementCut() })), null, null);
        var value = Parse(rendered).GetProperty("c").GetString();
        Assert.StartsWith("x, ", value);
        Assert.Contains("Bearer [redacted]", value);
        Assert.DoesNotContain("TT", value);
    }

    [Fact]
    public void RenderProperties_HugeStringElementsInCollection_AreBounded()
    {
        var items = Enumerable.Range(0, 20).Select(_ => new string('x', 1_000_000)).ToArray();
        var rendered = LogEntryRendering.RenderProperties(State(("c", items)), null, null);
        Assert.Equal(256, Parse(rendered).GetProperty("c").GetString()!.Length);
        Assert.True(rendered.Truncated);
    }

    [Fact]
    public void RenderProperties_KeysLowercased_OriginalFormatDropped()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("ChannelName", "abc"), ("{OriginalFormat}", "Joined {ChannelName}")), null, null);
        var root = Parse(rendered);
        Assert.Equal("abc", root.GetProperty("channelname").GetString());
        Assert.Single(root.EnumerateObject());
        Assert.False(rendered.Truncated);
    }

    [Fact]
    public void RenderProperties_ObjectTypedState_IsAccepted()
    {
        IReadOnlyList<KeyValuePair<string, object>> state = [new("A", 1)];
        Assert.Equal(1, Parse(LogEntryRendering.RenderProperties(state, null, null)).GetProperty("a").GetInt64());
    }

    [Fact]
    public void RenderProperties_NonListState_YieldsNothing()
    {
        Assert.Null(LogEntryRendering.RenderProperties("just a string", null, null).Json);
    }

    private enum Color { Red = 1 }

    [Fact]
    public void RenderProperties_SupportedTypes_RenderInvariant()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var rendered = LogEntryRendering.RenderProperties(State(
            ("enum", Color.Red),
            ("guid", guid),
            ("span", TimeSpan.FromSeconds(90)),
            ("flag", true),
            ("num", 42),
            ("ratio", 1.5),
            ("ch", 'x'),
            ("none", null)), null, null);
        var root = Parse(rendered);
        Assert.Equal("Red", root.GetProperty("enum").GetString());
        Assert.Equal(guid.ToString(), root.GetProperty("guid").GetString());
        Assert.Equal("00:01:30", root.GetProperty("span").GetString());
        Assert.True(root.GetProperty("flag").GetBoolean());
        Assert.Equal(42, root.GetProperty("num").GetInt64());
        Assert.Equal(1.5, root.GetProperty("ratio").GetDouble());
        Assert.Equal("x", root.GetProperty("ch").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("none").ValueKind);
    }

    [Fact]
    public void RenderProperties_DateTimes_AreIsoUtc()
    {
        var local = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(2));
        var rendered = LogEntryRendering.RenderProperties(State(
            ("dto", local),
            ("dt", new DateTime(2026, 10, 14, 8, 30, 0, DateTimeKind.Utc))), null, null);
        var root = Parse(rendered);
        Assert.Equal("2026-10-14T10:00:00.0000000Z", root.GetProperty("dto").GetString());
        Assert.Equal("2026-10-14T08:30:00.0000000Z", root.GetProperty("dt").GetString());
    }

    [Fact]
    public void RenderProperties_ArrayOf25_KeepsFirst20Joined()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("ids", Enumerable.Range(1, 25).ToArray())), null, null);
        var expected = string.Join(", ", Enumerable.Range(1, 20));
        Assert.Equal(expected, Parse(rendered).GetProperty("ids").GetString());
    }

    [Fact]
    public void RenderProperties_ListAndReadOnlyCollection_AreRendered()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("l", new List<string> { "a", "b" }), ("s", new HashSet<int> { 7 })), null, null);
        var root = Parse(rendered);
        Assert.Equal("a, b", root.GetProperty("l").GetString());
        Assert.Equal("7", root.GetProperty("s").GetString());
    }

    private static IEnumerable<int> Endless()
    {
        var i = 0;
        while (true)
        {
            yield return i++;
        }
    }

    [Fact]
    public void RenderProperties_LazyEndlessEnumerable_RendersTypeNameAndReturns()
    {
        LogEntryRendering.RenderProperties(State(("lazy", Endless())), null, null); // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        var rendered = LogEntryRendering.RenderProperties(State(("lazy", Endless())), null, null);
        sw.Stop();
        // 50 ms instead of 1 ms: an endless enumeration would never return, and a loaded devbox cannot guarantee 1 ms.
        Assert.True(sw.ElapsedMilliseconds < 50, $"took {sw.ElapsedMilliseconds} ms");
        Assert.StartsWith("<", Parse(rendered).GetProperty("lazy").GetString());
        Assert.Contains("Endless", Parse(rendered).GetProperty("lazy").GetString());
    }

    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void RenderProperties_ObjectWithThrowingToString_RendersTypeName()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("o", new ThrowingToString())), null, null);
        Assert.Equal($"<{typeof(ThrowingToString).FullName}>", Parse(rendered).GetProperty("o").GetString());
    }

    private sealed class CountingState(int count) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int MaxIndexRead { get; private set; } = -1;

        public int Count { get; } = count;

        public KeyValuePair<string, object?> this[int index]
        {
            get
            {
                if (index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                MaxIndexRead = Math.Max(MaxIndexRead, index);
                return new($"k{index}", index);
            }
        }

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new NotSupportedException();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void RenderProperties_HugeCount_ReadsOnlyIndices0To16()
    {
        var state = new CountingState(1_000_000);
        var rendered = LogEntryRendering.RenderProperties(state, null, null);
        Assert.Equal(16, state.MaxIndexRead);
        Assert.True(rendered.Truncated);
        Assert.Equal(16, Parse(rendered).EnumerateObject().Count());
    }

    [Fact]
    public void RenderProperties_SmallCount_NeverReadsPastCount()
    {
        var state = new CountingState(3);
        var rendered = LogEntryRendering.RenderProperties(state, null, null);
        Assert.Equal(2, state.MaxIndexRead);
        Assert.False(rendered.Truncated);
    }

    [Fact]
    public void RenderProperties_SeventeenKeys_DropsLastAndTruncates()
    {
        var items = Enumerable.Range(0, 17).Select(i => ($"k{i}", (object?)i)).ToArray();
        var rendered = LogEntryRendering.RenderProperties(State(items), null, null);
        var root = Parse(rendered);
        Assert.Equal(16, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("k16", out _));
        Assert.True(rendered.Truncated);
    }

    [Fact]
    public void RenderProperties_ScopeValuesCountAmongTheSixteen()
    {
        var items = Enumerable.Range(0, 16).Select(i => ($"k{i}", (object?)i)).ToArray();
        var rendered = LogEntryRendering.RenderProperties(State(items), "/api/x", "trace");
        var root = Parse(rendered);
        Assert.Equal(16, root.EnumerateObject().Count());
        Assert.True(rendered.Truncated);

        var withScope = LogEntryRendering.RenderProperties(State(("a", 1)), "/api/x", "trace-1");
        var scoped = Parse(withScope);
        Assert.Equal("/api/x", scoped.GetProperty("requestpath").GetString());
        Assert.Equal("trace-1", scoped.GetProperty("traceid").GetString());
    }

    [Fact]
    public void RenderProperties_DuplicateKeyAfterLowercase_FirstWins()
    {
        var root = Parse(LogEntryRendering.RenderProperties(State(("Name", "first"), ("name", "second")), null, null));
        Assert.Equal("first", root.GetProperty("name").GetString());
        Assert.Single(root.EnumerateObject());
    }

    [Fact]
    public void RenderProperties_LongValue_CutTo256AndTruncated()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("v", new string('a', 300))), null, null);
        Assert.Equal(256, Parse(rendered).GetProperty("v").GetString()!.Length);
        Assert.True(rendered.Truncated);
    }

    [Fact]
    public void RenderProperties_Over4KiB_DropsTrailingKeysAndTruncates()
    {
        var items = Enumerable.Range(0, 16).Select(i => ($"k{i:00}", (object?)new string('v', 256))).ToArray();
        var rendered = LogEntryRendering.RenderProperties(State(items), null, null);
        Assert.True(rendered.Truncated);
        Assert.True(rendered.Json!.Length <= 4096);
        var count = Parse(rendered).EnumerateObject().Count();
        Assert.InRange(count, 1, 15);
    }

    [Fact]
    public void RenderProperties_NulInKeyAndValue_AreNormalizedAndJsonIsValid()
    {
        var rendered = LogEntryRendering.RenderProperties(State(("k\0", "v\uD800")), null, null);
        Assert.DoesNotContain("\\u0000", rendered.Json);
        Assert.Equal("v\uFFFD", Parse(rendered).GetProperty("k␀").GetString());
    }

    [Fact]
    public void RenderProperties_ThrowingCount_YieldsNoProperties()
    {
        var rendered = LogEntryRendering.RenderProperties(new ThrowingCountState(), null, null);
        Assert.Null(rendered.Json);
    }

    private sealed class ThrowingCountState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => throw new InvalidOperationException();

        public KeyValuePair<string, object?> this[int index] => throw new InvalidOperationException();

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => throw new NotSupportedException();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public sealed class GenericException<T> : Exception;
