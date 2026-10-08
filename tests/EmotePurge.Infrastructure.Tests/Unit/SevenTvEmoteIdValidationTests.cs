using EmotePurge.Core.SevenTv;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class SevenTvEmoteIdValidationTests
{
    [Theory]
    [InlineData("01HQXJ5B0000000000000000AB")] // 26-character ULID, today's shape
    [InlineData("60ae958e229664e8667aea38")] // 24-character hex ObjectID, the older shape
    [InlineData("a")]
    [InlineData("A2345678901234567890123456789012")] // exactly 32
    public void IsValid_AcceptsAlphanumericIdsUpToThirtyTwoCharacters(string id) =>
        Assert.True(SevenTvEmoteIdValidation.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A23456789012345678901234567890123")] // 33
    [InlineData("has space")]
    [InlineData("../etc")]
    [InlineData("dash-ed")]
    [InlineData("quote\"")]
    [InlineData("trailing\n")]
    [InlineData("ümlaut")]
    public void IsValid_RejectsAnythingElse(string? id) =>
        Assert.False(SevenTvEmoteIdValidation.IsValid(id));
}
