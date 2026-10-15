using Xunit;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Logging.Query;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class LogFilterParserTests
{
    private static LogFilterExpression Ok(string? filter)
    {
        var result = LogFilterParser.Parse(filter);
        Assert.True(result.IsSuccess, $"unexpected {result.Reason} at {result.Position}/{result.Length} for [{filter}]");
        return result.Expression!;
    }

    private static T Single<T>(string filter)
        where T : LogFilterTerm
    {
        var terms = Ok(filter).Terms;
        return Assert.IsType<T>(Assert.Single(terms));
    }

    private static void Fails(string filter, string reason, int position, int length)
    {
        var result = LogFilterParser.Parse(filter);
        Assert.False(result.IsSuccess);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(position, result.Position);
        Assert.Equal(length, result.Length);
        var ex = Assert.Throws<LogFilterSyntaxException>(() => result.ExpressionOrThrow());
        Assert.Equal((reason, position, length), (ex.Reason, ex.Position, ex.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t ")]
    public void EmptyAndWhitespace_AreValidAndMatchEverything(string? filter) => Assert.Empty(Ok(filter).Terms);

    [Theory]
    [InlineData("level:info", LogEntryLevel.Information)]
    [InlineData("level:INFORMATION", LogEntryLevel.Information)]
    [InlineData("level:warn", LogEntryLevel.Warning)]
    [InlineData("Level:Warning", LogEntryLevel.Warning)]
    [InlineData("level:error", LogEntryLevel.Error)]
    [InlineData("level:err", LogEntryLevel.Error)]
    [InlineData("level:crit", LogEntryLevel.Critical)]
    [InlineData("level:critical", LogEntryLevel.Critical)]
    [InlineData("level:fatal", LogEntryLevel.Critical)]
    public void Level_Aliases(string filter, LogEntryLevel expected)
    {
        var term = Single<LevelTerm>(filter);
        Assert.Equal(LogFilterOperator.Equal, term.Operator);
        Assert.Equal([expected], term.Levels);
    }

    [Theory]
    [InlineData("level>=error", LogFilterOperator.GreaterOrEqual)]
    [InlineData("level<=warn", LogFilterOperator.LessOrEqual)]
    [InlineData("level>warn", LogFilterOperator.Greater)]
    [InlineData("level<error", LogFilterOperator.Less)]
    public void Level_Comparisons(string filter, LogFilterOperator op) => Assert.Equal(op, Single<LevelTerm>(filter).Operator);

    [Fact]
    public void Level_CommaList() =>
        Assert.Equal([LogEntryLevel.Warning, LogEntryLevel.Error], Single<LevelTerm>("level:warn,error").Levels);

    [Fact]
    public void Source_Values_AreCaseInsensitive() =>
        Assert.Equal([LogSource.Api, LogSource.Worker], Single<SourceTerm>("SOURCE:Api,worker").Sources);

    [Fact]
    public void Category_Wildcards_AndLists()
    {
        var term = Single<CategoryTerm>("cat:TwitchChatManager,SevenTv*");
        Assert.Equal(["TwitchChatManager", "SevenTv*"], term.Patterns.Select(p => p.Value));
        Assert.Equal([false, true], term.Patterns.Select(p => p.HasWildcard));
    }

    [Fact]
    public void Event_NameOrNumber() =>
        Assert.Equal(["ClientDisconnected", "4711"], Single<EventTerm>("event:ClientDisconnected,4711").Patterns.Select(p => p.Value));

    [Fact]
    public void Instance_LatestAndIds()
    {
        var term = Single<InstanceTerm>("instance:LATEST,AbCdEf01");
        Assert.True(term.Latest);
        Assert.Equal(["abcdef01"], term.Ids);
        Assert.False(Single<InstanceTerm>("instance:0123abcd").Latest);
    }

    [Theory]
    [InlineData("uptime<15m", LogFilterOperator.Less, 15 * 60)]
    [InlineData("uptime<=30s", LogFilterOperator.LessOrEqual, 30)]
    [InlineData("uptime>2h", LogFilterOperator.Greater, 7200)]
    [InlineData("uptime>=1d", LogFilterOperator.GreaterOrEqual, 86400)]
    [InlineData("uptime<5M", LogFilterOperator.Less, 300)]
    public void Uptime_Durations(string filter, LogFilterOperator op, int seconds)
    {
        var term = Single<UptimeTerm>(filter);
        Assert.Equal(op, term.Operator);
        Assert.Equal(TimeSpan.FromSeconds(seconds), term.Duration);
    }

    [Fact]
    public void Has_Exception() => Assert.IsType<HasExceptionTerm>(Assert.Single(Ok("has:EXCEPTION").Terms));

    [Fact]
    public void Property_KeyIsLowerCased_AndKeepsDots()
    {
        var term = Single<PropertyTerm>("Channel:HandOfBlood,other");
        Assert.Equal("channel", term.Key);
        Assert.Equal(["HandOfBlood", "other"], term.Patterns.Select(p => p.Value));
        Assert.False(term.IsPresenceCheck);
        Assert.Equal("a.b_c1", Single<PropertyTerm>("a.b_c1:x").Key);
    }

    [Fact]
    public void Property_StarMeansPresent() => Assert.True(Single<PropertyTerm>("channel:*").IsPresenceCheck);

    [Fact]
    public void FreeText_WordAndQuote()
    {
        Assert.Equal("timeout", Single<FreeTextTerm>("timeout").Pattern.Value);
        Assert.Equal("a b", Single<FreeTextTerm>("\"a b\"").Pattern.Value);
        Assert.Equal("say \"hi\"", Single<FreeTextTerm>("\"say \\\"hi\\\"\"").Pattern.Value);
    }

    [Fact]
    public void Url_WithoutQuotes_IsPropertyHttp()
    {
        var term = Single<PropertyTerm>("http://x");
        Assert.Equal("http", term.Key);
        Assert.Equal("//x", Assert.Single(term.Patterns).Value);
    }

    [Fact]
    public void Url_InQuotes_IsFreeText() => Assert.Equal("http://x", Single<FreeTextTerm>("\"http://x\"").Pattern.Value);

    [Fact]
    public void WordStartingWithDigit_IsFreeText() => Assert.Equal("12:30", Single<FreeTextTerm>("12:30").Pattern.Value);

    [Fact]
    public void QuotedValue_AllowsSpacesCommasAndColons()
    {
        var term = Single<PropertyTerm>("msg:\"a, b: c\",d");
        Assert.Equal(["a, b: c", "d"], term.Patterns.Select(p => p.Value));
    }

    [Fact]
    public void Negation_AppliesToTheWholeTerm()
    {
        var terms = Ok("-cat:a,b -\"x y\" -level>=error plain").Terms;
        Assert.Equal([true, true, true, false], terms.Select(t => t.Negated));
        Assert.Equal(2, Assert.IsType<CategoryTerm>(terms[0]).Patterns.Count);
        Assert.IsType<FreeTextTerm>(terms[1]);
        Assert.IsType<LevelTerm>(terms[2]);
    }

    [Fact]
    public void MultipleTerms_AreKeptInOrder()
    {
        var terms = Ok("source:worker   cat:X  level>=warn  foo").Terms;
        Assert.Equal(4, terms.Count);
        Assert.IsType<SourceTerm>(terms[0]);
        Assert.IsType<FreeTextTerm>(terms[3]);
    }

    [Theory]
    [InlineData("100%_done\\x", "100\\%\\_done\\\\x")]
    [InlineData("Sev*Tv*", "Sev%Tv%")]
    [InlineData("a%*_", "a\\%%\\_")]
    [InlineData("plain", "plain")]
    public void IlikePattern_MasksMetaCharacters_AndMapsStar(string value, string expected) =>
        Assert.Equal(expected, new LogTextPattern(value, value.Contains('*')).ToIlikePattern());

    [Fact]
    public void Wildcard_InQuotedFreeText_IsRecognised()
    {
        var pattern = Single<FreeTextTerm>("\"a*b\"").Pattern;
        Assert.True(pattern.HasWildcard);
        Assert.Equal("a%b", pattern.ToIlikePattern());
    }

    // ---- errors: reason, position, length (UTF-16) ----

    [Fact]
    public void UnterminatedQuote_PointsAtOpeningQuote() => Fails("level:error \"abc", LogFilterErrorReasons.UnterminatedQuote, 12, 1);

    [Fact]
    public void UnterminatedQuote_InValue() => Fails("msg:\"abc", LogFilterErrorReasons.UnterminatedQuote, 4, 1);

    [Fact]
    public void UnterminatedQuote_EscapedClosingQuoteDoesNotClose() => Fails("\"a\\\"", LogFilterErrorReasons.UnterminatedQuote, 0, 1);

    [Theory]
    [InlineData("-", 0)]
    [InlineData("level:error -", 12)]
    [InlineData("a - b", 2)]
    public void EmptyTerm_LoneMinus(string filter, int position) => Fails(filter, LogFilterErrorReasons.EmptyTerm, position, 1);

    [Fact]
    public void EmptyTerm_EmptyQuotedFreeText() => Fails("a \"\"", LogFilterErrorReasons.EmptyTerm, 2, 2);

    [Fact]
    public void EmptyValue_TrailingColon_IsTheStumblingBlock() => Fails("Fehler:", LogFilterErrorReasons.EmptyValue, 6, 1);

    [Fact]
    public void EmptyValue_FollowedByAnotherTerm() => Fails("cat: level:error", LogFilterErrorReasons.EmptyValue, 3, 1);

    [Fact]
    public void EmptyValue_ComparisonOperatorSpansItsLength() => Fails("level>=", LogFilterErrorReasons.EmptyValue, 5, 2);

    [Fact]
    public void EmptyValue_TrailingComma() => Fails("cat:a,", LogFilterErrorReasons.EmptyValue, 5, 1);

    [Fact]
    public void EmptyValue_DoubleComma() => Fails("cat:a,,b", LogFilterErrorReasons.EmptyValue, 5, 1);

    [Fact]
    public void EmptyValue_LeadingComma() => Fails("cat:,a", LogFilterErrorReasons.EmptyValue, 3, 1);

    [Fact]
    public void EmptyValue_EmptyQuotes() => Fails("cat:\"\"", LogFilterErrorReasons.EmptyValue, 4, 2);

    [Theory]
    [InlineData("source>api", 6, 1)]
    [InlineData("cat<=x", 3, 2)]
    [InlineData("has>=exception", 3, 2)]
    [InlineData("foo<bar", 3, 1)]
    [InlineData("uptime:5m", 6, 1)]
    public void InvalidOperator(string filter, int position, int length) =>
        Fails(filter, LogFilterErrorReasons.InvalidOperator, position, length);

    [Fact]
    public void InvalidLevel_Debug() => Fails("debug".Insert(0, "level:"), LogFilterErrorReasons.InvalidLevel, 6, 5);

    [Fact]
    public void InvalidLevel_PointsAtTheOffendingListEntry() => Fails("level:warn,trace", LogFilterErrorReasons.InvalidLevel, 11, 5);

    [Fact]
    public void InvalidLevel_QuotedValueSpansTheQuotes() => Fails("level:\"nope\"", LogFilterErrorReasons.InvalidLevel, 6, 6);

    [Fact]
    public void InvalidSource() => Fails("source:api,db", LogFilterErrorReasons.InvalidSource, 11, 2);

    [Theory]
    [InlineData("instance:xyz")]
    [InlineData("instance:abcdef0")]
    [InlineData("instance:abcdef012")]
    [InlineData("instance:ghijklmn")]
    [InlineData("instance:*")]
    public void InvalidInstance(string filter) => Fails(filter, LogFilterErrorReasons.InvalidInstance, 9, filter.Length - 9);

    [Theory]
    [InlineData("uptime<15")]
    [InlineData("uptime<m")]
    [InlineData("uptime<1.5h")]
    [InlineData("uptime<5w")]
    [InlineData("uptime<99999999999999999999s")]
    [InlineData("uptime<999999999999d")]
    public void InvalidDuration(string filter) => Fails(filter, LogFilterErrorReasons.InvalidDuration, 7, filter.Length - 7);

    [Theory]
    [InlineData("level:*", 6, 1)]
    [InlineData("level:err*", 6, 4)]
    [InlineData("uptime<5*", 7, 2)]
    [InlineData("has:*", 4, 1)]
    [InlineData("has:foo", 4, 3)]
    public void InvalidValue_WildcardOrUnknown(string filter, int position, int length) =>
        Fails(filter, LogFilterErrorReasons.InvalidValue, position, length);

    [Fact]
    public void InvalidValue_ListAfterComparison() => Fails("level>=warn,error", LogFilterErrorReasons.InvalidValue, 12, 5);

    [Fact]
    public void InvalidValue_ListForUptime() => Fails("uptime<5m,10m", LogFilterErrorReasons.InvalidValue, 10, 3);

    [Fact]
    public void InvalidValue_TextGluedToClosingQuote() => Fails("cat:\"a\"b", LogFilterErrorReasons.InvalidValue, 7, 1);

    [Fact]
    public void Positions_AreUtf16_AfterUmlaut() => Fails("ä level:debug", LogFilterErrorReasons.InvalidLevel, 8, 5);

    [Fact]
    public void Positions_AreUtf16_AfterSurrogatePair()
    {
        var filter = "😀 \"abc"; // the emoji is two UTF-16 units
        Assert.Equal(2, "😀".Length);
        Fails(filter, LogFilterErrorReasons.UnterminatedQuote, 3, 1);
    }

    [Fact]
    public void Positions_AreUtf16_ForValueContainingEmoji() =>
        Fails("source:😀", LogFilterErrorReasons.InvalidSource, 7, 2);

    // ---- limits ----

    [Fact]
    public void FilterLength_512_IsAccepted() => Ok(string.Join(' ', new string('a', 128), new string('a', 128), new string('a', 128), new string('a', 125)).PadRight(512)[..512]);

    [Fact]
    public void FilterLength_513_IsTooLong() => Fails(string.Concat(Enumerable.Repeat("abc ", 128)) + "x", LogFilterErrorReasons.TooLong, 512, 1);

    [Fact]
    public void FilterLength_TooLong_ReportsTheExcess() => Fails(string.Concat(Enumerable.Repeat("abc ", 150)), LogFilterErrorReasons.TooLong, 512, 88);

    [Fact]
    public void Terms_16_AreAccepted() => Assert.Equal(16, Ok(string.Join(' ', Enumerable.Repeat("ab", 16))).Terms.Count);

    [Fact]
    public void Terms_17_AreTooMany()
    {
        var filter = string.Join(' ', Enumerable.Repeat("ab", 17));
        Fails(filter, LogFilterErrorReasons.TooManyTerms, 48, 2);
    }

    [Fact]
    public void Values_16_AreAccepted() =>
        Assert.Equal(16, Single<CategoryTerm>("cat:" + string.Join(',', Enumerable.Range(0, 16).Select(i => "c" + i))).Patterns.Count);

    [Fact]
    public void Values_17_AreTooMany()
    {
        var list = string.Join(',', Enumerable.Range(0, 17).Select(i => "c" + i));
        // the 17th value starts after 16 values "c0".."c9" (2 chars) and "c10".."c15" (3 chars) plus 16 commas
        var start = 4 + (10 * 2) + (6 * 3) + 16;
        Fails("cat:" + list, LogFilterErrorReasons.TooManyValues, start, 3);
    }

    [Fact]
    public void ValueLength_200_IsAccepted() => Ok("cat:" + new string('x', 200));

    [Fact]
    public void ValueLength_201_IsTooLong() => Fails("cat:" + new string('x', 201), LogFilterErrorReasons.TooLong, 4, 201);

    [Fact]
    public void ValueLength_IsMeasuredAfterUnescaping_AndFreeTextIsLimitedToo()
    {
        Fails("\"" + new string('x', 201) + "\"", LogFilterErrorReasons.TooLong, 0, 203);
        Ok("\"" + new string('x', 200) + "\"");
    }

    [Fact]
    public void FirstError_Wins_NoPartialResult()
    {
        var result = LogFilterParser.Parse("level:error cat:ok source:bogus");
        Assert.False(result.IsSuccess);
        Assert.Null(result.Expression);
        Assert.Equal(LogFilterErrorReasons.InvalidSource, result.Reason);
    }
}
