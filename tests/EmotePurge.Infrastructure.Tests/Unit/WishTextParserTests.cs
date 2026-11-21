using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class WishTextParserTests
{
    private const string Ulid = "01EZPHAJ3R000C438200A44F19";
    private const string Hex = "603cb17bc20d020014423c29"; // maps onto Ulid

    private static void AssertLink(string? text, string id = Ulid)
    {
        var result = WishTextParser.Parse(text);
        Assert.Equal(WishMatchKind.Link, result.MatchKind);
        Assert.Equal(id, result.SevenTvEmoteId);
        Assert.Null(result.RequestedName);
    }

    private static void AssertName(string? text, string name)
    {
        var result = WishTextParser.Parse(text);
        Assert.Equal(WishMatchKind.Name, result.MatchKind);
        Assert.Equal(name, result.RequestedName);
        Assert.Null(result.SevenTvEmoteId);
    }

    private static void AssertNone(string? text)
    {
        var result = WishTextParser.Parse(text);
        Assert.Equal(WishMatchKind.None, result.MatchKind);
        Assert.Null(result.SevenTvEmoteId);
        Assert.Null(result.RequestedName);
    }

    [Theory]
    [InlineData("https://7tv.app/emotes/" + Ulid)]
    [InlineData("http://7tv.app/emotes/" + Ulid)]
    [InlineData("7tv.app/emotes/" + Ulid)]
    [InlineData("https://www.7tv.app/emotes/" + Ulid)]
    [InlineData("www.7tv.app/emotes/" + Ulid)]
    [InlineData("https://old.7tv.app/emotes/" + Ulid)]
    [InlineData("HTTPS://7TV.APP/EMOTES/" + Ulid)]
    [InlineData("https://7tv.app/emotes/" + Ulid + "?ref=chat")]
    [InlineData("https://7tv.app/emotes/" + Ulid + "#top")]
    [InlineData("https://7tv.app/emotes/" + Ulid + "/")]
    [InlineData("https://7tv.app/emotes/" + Ulid + ".")]
    [InlineData("(https://7tv.app/emotes/" + Ulid + ")")]
    [InlineData("<https://7tv.app/emotes/" + Ulid + ">")]
    [InlineData("pls add https://7tv.app/emotes/" + Ulid + " thanks")]
    [InlineData("pls\tadd\r\nhttps://7tv.app/emotes/" + Ulid)]
    public void Link_26CharacterId_IsALink(string text) => AssertLink(text);

    [Theory]
    [InlineData("https://7tv.app/emotes/" + Hex)]
    [InlineData("https://7tv.app/emotes/" + "603CB17BC20D020014423C29")]
    [InlineData("7tv.app/emotes/" + Hex + "?x=1")]
    public void Link_24CharacterHexId_IsALinkWithTheCanonicalId(string text) => AssertLink(text);

    [Fact]
    public void Link_AllThreeFormsOfTheSameEmote_GiveTheSameId()
    {
        var forms = new[]
        {
            "https://7tv.app/emotes/" + Ulid,
            "https://7tv.app/emotes/" + Ulid.ToLowerInvariant(),
            "https://7tv.app/emotes/" + Hex,
        };

        Assert.All(forms, form => Assert.Equal(Ulid, WishTextParser.Parse(form).SevenTvEmoteId));
    }

    [Fact]
    public void Link_TwoLinks_TheFirstCounts() =>
        AssertLink("https://7tv.app/emotes/" + Ulid + " https://7tv.app/emotes/01F6MZGCNG000255K4X1K7NTHR");

    [Fact]
    public void Link_WinsOverANameInTheSameText() => AssertLink("KEKW https://7tv.app/emotes/" + Ulid);

    [Fact]
    public void Link_AnInvalidLinkBeforeAValidOne_DoesNotHideIt() =>
        AssertLink("https://7tv.app/emotes/short https://7tv.app/emotes/" + Ulid);

    [Theory]
    [InlineData("https://7tv.app/emotes/01EZPHAJ3R000C438200A44F1")]   // 25
    [InlineData("https://7tv.app/emotes/01EZPHAJ3R000C438200A44F190")] // 27
    [InlineData("https://7tv.app/emotes/603cb17bc20d020014423c2")]     // 23
    [InlineData("https://7tv.app/emotes/603cb17bc20d020014423c299")]   // 25
    [InlineData("https://7tv.app/emotes/603cb17bc20d020014423g29")]    // 24, not hex
    [InlineData("https://7tv.app/emotes/")]
    [InlineData("https://7tv.app/users/" + Ulid)]
    [InlineData("https://7tv.app/emote-sets/" + Ulid)]
    [InlineData("https://example.org/emotes/" + Ulid)]
    [InlineData("https://evil7tv.app/emotes/" + Ulid)]
    [InlineData("https://cdn.7tv.app/emotes/" + Ulid)]
    [InlineData("https://example.org/7tv.app/emotes/" + Ulid)]
    [InlineData("https://7tv.gg/emotes/" + Ulid)]
    public void NotALink_IsNotMatchedAsOne(string text) =>
        Assert.NotEqual(WishMatchKind.Link, WishTextParser.Parse(text).MatchKind);

    [Fact]
    public void NotALink_WrongLength_FallsBackToTheNameGrammarWhereTheTokenIsAnAddress() =>
        AssertNone("https://7tv.app/emotes/01EZPHAJ3R000C438200A44F1");

    // The example table of spec K6.
    [Theory]
    [InlineData(Ulid)]
    [InlineData("01ezphaj3r000c438200a44f19")]
    [InlineData(Hex)]
    [InlineData("603CB17BC20D020014423C29")]
    [InlineData("bitte " + Ulid + " danke!")]
    [InlineData(Ulid + ".")]
    [InlineData("\"" + Ulid + "\"")]
    [InlineData("KEKW " + Ulid)]
    [InlineData("  " + Hex + "\n")]
    public void BareId_StandaloneIdShapedToken_IsALinkMatch(string text) => AssertLink(text);

    [Fact]
    public void BareId_BeatsAnEarlierNameToken() => AssertLink("please add this catJAM " + Ulid);

    [Fact]
    public void BareId_TheFirstIdShapedTokenCounts() => AssertLink(Ulid + " 01FC4W70J80005D6HG0ANTP023");

    [Fact]
    public void BareId_ALinkBeatsABareId() =>
        AssertLink("01FC4W70J80005D6HG0ANTP023 https://7tv.app/emotes/" + Ulid);

    [Theory]
    [InlineData("01EZPHAJ3R000C438200A44F1")] // 25
    [InlineData("01EZPHAJ3R000C438200A44F199")] // 27
    [InlineData("01EZPHAJ3R000C438200A44F1I")] // I
    [InlineData("01EZPHAJ3R000C438200A44F1L")] // L
    [InlineData("01EZPHAJ3R000C438200A44F1O")] // O
    [InlineData("01EZPHAJ3R000C438200A44F1U")] // U
    [InlineData("81EZPHAJ3R000C438200A44F19")] // first character above 7
    [InlineData("Z1EZPHAJ3R000C438200A44F19")]
    [InlineData("603cb17bc20d020014423c2")] // 23 hex
    [InlineData("603cb17bc20d020014423c299")] // 25 hex
    [InlineData("603cb17bc20d020014423cZZ")] // 24, not hex
    [InlineData("abcdefghijklmnopqrstuvwx")] // 24-character name
    [InlineData("01EZPHAJ3R000C438200A44F1\u017F")] // long s folds to S under ToUpperInvariant
    [InlineData("01EZPHAJ3R000C438200A44F1\u212A")] // Kelvin sign
    [InlineData("01EZPHAJ3R000C438200A44F1\u0131")] // dotless i
    [InlineData("\uFF10\uFF11EZPHAJ3R000C438200A44F19")] // fullwidth digits
    [InlineData("\uFF16\uFF10\uFF13cb17bc20d020014423c29")] // fullwidth digits in a 24-character token
    public void BareId_NotIdShaped_StaysAName(string token) => AssertName(token, token);

    [Theory]
    [InlineData("@" + Ulid)]
    [InlineData("https://example.com/" + Ulid)]
    [InlineData("www." + Ulid)]
    public void BareId_AnAddressOrMention_IsNotAnId(string text) => AssertNone(text);

    [Fact]
    public void BareId_InsideALongerWord_IsNotAnId() => AssertName("x" + Ulid, "x" + Ulid);

    [Theory]
    [InlineData("KEKW", "KEKW")]
    [InlineData("kekw bitte", "kekw")]
    [InlineData("bitte KEKW", "bitte")]
    [InlineData("@streamer KEKW!", "KEKW")]
    [InlineData("\"peepoHappy\".", "peepoHappy")]
    [InlineData("(ditto)", "(ditto)")]
    [InlineData("https://example.org/x KEKW", "KEKW")]
    public void Name_SpecTable(string text, string name) => AssertName(text, name);

    [Theory]
    [InlineData("a")]
    [InlineData("!!! ???")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@streamer")]
    [InlineData("@streamer @mod")]
    [InlineData("www.example.org")]
    [InlineData("WWW.example.org")]
    [InlineData("...")]
    [InlineData("a b c")]
    [InlineData("(-)")]
    [InlineData(":)")]
    public void None_UnparsedCases(string text) => AssertNone(text);

    [Fact]
    public void None_NullText() => AssertNone(null);

    [Theory]
    [InlineData("KEKW\tplease", "KEKW")]
    [InlineData("\tKEKW", "KEKW")]
    [InlineData("\r\n  KEKW  \r\n", "KEKW")]
    [InlineData("KEKW\nplease", "KEKW")]
    [InlineData("a   KEKW", "KEKW")]
    [InlineData("a ab", "ab")]
    [InlineData(" KEKW", "KEKW")]
    public void Name_WhitespaceOfAnyKindSeparatesTokens(string text, string name) => AssertName(text, name);

    [Theory]
    [InlineData("'KEKW'", "KEKW")]
    [InlineData("KEKW,", "KEKW")]
    [InlineData("KEKW;", "KEKW")]
    [InlineData("KEKW?!", "KEKW")]
    [InlineData("...KEKW...", "KEKW")]
    [InlineData("K.E.K.W", "K.E.K.W")]
    [InlineData(":3", ":3")]
    [InlineData("monkaS:", "monkaS:")]
    [InlineData("a!b", "a!b")]
    public void Name_EdgePunctuationIsTrimmedInnerCharactersStay(string text, string name) => AssertName(text, name);

    [Theory]
    [InlineData("Ünicode", "Ünicode")]
    [InlineData("日本語", "日本語")]
    [InlineData("😀😀", null)]               // two code points, but neither a letter nor a digit
    [InlineData("😀a", "😀a")]
    [InlineData("ÄÖ bitte", "ÄÖ")]
    public void Name_NonAscii(string text, string? name)
    {
        if (name is null)
            AssertNone(text);
        else
            AssertName(text, name);
    }

    [Fact]
    public void Name_CountedInCodePoints_NotUtf16Units() =>
        AssertName(string.Concat(Enumerable.Repeat("😀", 99)) + "a", string.Concat(Enumerable.Repeat("😀", 99)) + "a");

    [Fact]
    public void Name_100CharactersAreTheLongestName()
    {
        var name = new string('a', 100);
        AssertName(name, name);
    }

    [Fact]
    public void Name_101CharactersAreNoName_TheNextTokenIsTaken()
    {
        AssertNone(new string('a', 101));
        AssertName(new string('a', 101) + " KEKW", "KEKW");
    }

    [Fact]
    public void Name_ExactlyTwoCharactersIsTheShortest()
    {
        AssertName("ab", "ab");
        AssertNone("a");
    }

    [Fact]
    public void Name_ATokenThatShrinksBelowTheMinimumAfterTrimmingIsSkipped() => AssertName("a. KEKW", "KEKW");

    [Fact]
    public void Text_500CharactersWithoutWhitespace_IsNone() => AssertNone(new string('x', 500));

    [Fact]
    public void Text_500CharactersOfShortTokens_FindsTheFirstName() =>
        AssertName(string.Concat(Enumerable.Repeat("a ", 249)) + "KEKW", "KEKW");

    [Fact]
    public void Text_HostileInput_FinishesQuickly()
    {
        var hostile = string.Concat(Enumerable.Repeat("7tv.app/emotes/", 5000)) + new string('a', 50_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        WishTextParser.Parse(hostile);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Result_NeverExceedsTheColumnLimits()
    {
        var result = WishTextParser.Parse(new string('b', 100));

        Assert.True(result.RequestedName!.Length <= WishLimits.RequestedNameMaxLength);
    }
}
