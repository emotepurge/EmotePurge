using EmotePurge.Api.Validation;
using Xunit;

namespace EmotePurge.Api.Tests;

/// <summary>
/// The pure format check behind <see cref="EmoteSetIdValidationFilter"/> (spec 2026-09-20, E14):
/// not empty, 1-32 characters, <c>[0-9A-Za-z]</c> only, ordinal.
/// </summary>
/// <remarks>
/// Placed here rather than under <c>tests/EmotePurge.Infrastructure.Tests/Unit/</c>, where the
/// task brief and the spec's test pyramid (15.1) both list it: <c>EmoteSetIdValidationFilter</c> is
/// an <c>IEndpointFilter</c>, an ASP.NET Core type that can only live in <c>EmotePurge.Api</c> (Regel
/// on layer purity — Infrastructure may not reference ASP.NET Core types), and
/// <c>EmotePurge.Infrastructure.Tests</c> carries no project reference to <c>EmotePurge.Api</c> at
/// all (only Core and Infrastructure) — the same reason no earlier validator of this shape
/// (<c>ChannelNameValidation</c>/<c>ChannelNameValidationFilter</c>) has ever had a test outside this
/// project. Reported as a deviation in the task's final report rather than silently "fixed" by moving
/// the type, which would have broken the Api/Infrastructure layering the other way.
/// </remarks>
public class EmoteSetIdValidationTests
{
    [Theory]
    [InlineData("01GV88A38G0006FW5TVZVMG507")] // a real, measured 7TV v4 ULID (E14)
    [InlineData("60ae1b2c3d4e5f6789012345")] // a 24-char hex ObjectID, the older set-id shape
    [InlineData("a")] // the one-character lower bound
    public void IsValid_AcceptsWellFormedIds(string emoteSetId)
    {
        Assert.True(EmoteSetIdValidation.IsValid(emoteSetId));
    }

    [Fact]
    public void IsValid_RejectsAnEmptyId()
    {
        Assert.False(EmoteSetIdValidation.IsValid(""));
    }

    [Fact]
    public void IsValid_AcceptsExactlyThirtyTwoCharacters_ButRejectsThirtyThree()
    {
        var thirtyTwo = new string('a', 32);
        var thirtyThree = new string('a', 33);

        Assert.True(EmoteSetIdValidation.IsValid(thirtyTwo));
        Assert.False(EmoteSetIdValidation.IsValid(thirtyThree));
    }

    [Theory]
    [InlineData("../x")] // path traversal characters — the one concrete case the spec calls out
    [InlineData("has space")]
    [InlineData("quote\"here")]
    [InlineData("dash-not-allowed")]
    public void IsValid_RejectsAnythingOutsideTheAlphanumericAllowlist(string emoteSetId)
    {
        Assert.False(EmoteSetIdValidation.IsValid(emoteSetId));
    }

    /// <summary>
    /// "Ordinal" (E14): the check compares literal ASCII characters only, case included, with no
    /// culture-sensitive folding — a mixed-case id (exactly what 7TV's own ULIDs look like) is neither
    /// rejected nor silently normalized, and a value that would only match under a culture-aware
    /// comparison (e.g. the Turkish dotless/dotted "I" pair) is not accidentally let through by one.
    /// </summary>
    [Theory]
    [InlineData("AbCdEf0123456789")]
    [InlineData("IIIIiiii")]
    public void IsValid_ComparesOrdinally_MixedCaseAsciiPassesUnchanged(string emoteSetId)
    {
        Assert.True(EmoteSetIdValidation.IsValid(emoteSetId));
    }
}
