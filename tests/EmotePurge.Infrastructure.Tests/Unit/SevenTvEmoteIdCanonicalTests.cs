using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SevenTvEmoteIdCanonicalTests
{
    // The three pairs measured live against 7TV in K3 and pinned by the web spec of canonicalSevenTvEmoteId.
    [Theory]
    [InlineData("60ae958e229664e8667aea38", "01F6MZGCNG000255K4X1K7NTHR")]
    [InlineData("603cb17bc20d020014423c29", "01EZPHAJ3R000C438200A44F19")]
    [InlineData("60A1BABB3C3362F9A4B8B33A", "01F5VW2TKR0003RCV2Z6JBHCST")]
    public void HexObjectId_MapsOntoTheMeasuredUlid(string objectId, string ulid) =>
        Assert.Equal(ulid, SevenTvEmoteIdCanonical.Canonicalize(objectId));

    [Fact]
    public void HexObjectId_UpperAndLowerCase_GiveTheSameUlid() =>
        Assert.Equal(
            SevenTvEmoteIdCanonical.Canonicalize("60a1babb3c3362f9a4b8b33a"),
            SevenTvEmoteIdCanonical.Canonicalize("60A1BABB3C3362F9A4B8B33A"));

    [Theory]
    [InlineData("01ezphaj3r000c438200a44f19", "01EZPHAJ3R000C438200A44F19")]
    [InlineData("01EzPhAj3R000c438200A44f19", "01EZPHAJ3R000C438200A44F19")]
    [InlineData("01EZPHAJ3R000C438200A44F19", "01EZPHAJ3R000C438200A44F19")]
    public void Ulid_IsUpperCased(string input, string expected) =>
        Assert.Equal(expected, SevenTvEmoteIdCanonical.Canonicalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("KEKW")]
    [InlineData("60ae958e229664e8667aea3")]       // 23 characters
    [InlineData("60ae958e229664e8667aea388")]     // 25 characters
    [InlineData("60ae958e229664e8667aea38il")]    // 26 characters, I and L are outside the ULID alphabet
    [InlineData("60ae958e229664e8667aeg38")]      // 24 characters, not hex
    [InlineData("01EZPHAJ3R000C438200A44F1U")]    // 26 characters with U, outside Crockford base32
    [InlineData("01EZPHAJ3R000C438200A44F190")]   // 27 characters
    [InlineData(" 01EZPHAJ3R000C438200A44F19")]
    public void Anything_Else_ComesBackUnchanged(string input) =>
        Assert.Equal(input, SevenTvEmoteIdCanonical.Canonicalize(input));
}
