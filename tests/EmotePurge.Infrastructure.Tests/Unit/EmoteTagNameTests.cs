using EmotePurge.Core.Entities;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class EmoteTagNameTests
{
    [Theory]
    [InlineData("  Funny ", "funny")]
    [InlineData("HAHA", "haha")]
    [InlineData("already", "already")]
    public void Normalize_TrimsAndLowercases(string input, string expected) =>
        Assert.Equal(expected, EmoteTagName.Normalize(input));

    [Fact]
    public void Normalize_TreatsDifferentCasingAsTheSameTag() =>
        Assert.Equal(EmoteTagName.Normalize("Funny"), EmoteTagName.Normalize(" fUNNY"));

    [Fact]
    public void IsValid_AcceptsExactlyTheMaxLength() =>
        Assert.True(EmoteTagName.IsValid(new string('a', 40)));

    [Fact]
    public void IsValid_RejectsOneCharacterOverTheMaxLength() =>
        Assert.False(EmoteTagName.IsValid(new string('a', 41)));

    [Fact]
    public void IsValid_MeasuresTheTrimmedName() =>
        Assert.True(EmoteTagName.IsValid("  " + new string('a', 40) + "  "));

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    [InlineData("a\u0007b")]
    public void IsValid_RejectsControlCharacters(string name) =>
        Assert.False(EmoteTagName.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t ")]
    [InlineData(null)]
    public void IsValid_RejectsEmptyWhitespaceAndNull(string? name) =>
        Assert.False(EmoteTagName.IsValid(name));

    [Fact]
    public void IsValid_AcceptsOrdinaryNamesWithSpacesAndUnicode() =>
        Assert.True(EmoteTagName.IsValid("Lustig & süß"));
}
