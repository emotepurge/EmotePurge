using System.Text;
using EmotePurge.Core.Services;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Unit;

public class BotAccountJwtTests
{
    private const string Header = """{"alg":"HS256","typ":"JWT"}""";
    private const string Signature = "c2lnbmF0dXJl";

    [Fact]
    public void ReadsExp_AsUtc()
    {
        var expiry = BotAccountJwt.TryReadExpiry(Jwt("""{"sub":"bot","exp":1893456000}"""));

        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), expiry);
        Assert.Equal(DateTimeKind.Utc, expiry!.Value.Kind);
    }

    [Fact]
    public void ReadsAFractionalExp_ToTheWholeSecond() =>
        Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), BotAccountJwt.TryReadExpiry(Jwt("""{"exp":1893456000.75}""")));

    [Fact]
    public void AnExpInThePast_IsStillReadable()
    {
        // Rejecting an expired token is the caller's decision (400 bot_account_token_expired), not a form error.
        Assert.Equal(new DateTime(2001, 9, 9, 1, 46, 40, DateTimeKind.Utc), BotAccountJwt.TryReadExpiry(Jwt("""{"exp":1000000000}""")));
    }

    [Fact]
    public void DoesNotVerifyTheSignature() =>
        Assert.NotNull(BotAccountJwt.TryReadExpiry(Jwt("""{"exp":1893456000}""", signature: "bm90LWEtcmVhbC1zaWduYXR1cmU")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("..")]
    public void NoJwtForm_IsMalformed(string? token) => Assert.Null(BotAccountJwt.TryReadExpiry(token));

    [Fact]
    public void FourSegments_IsMalformed() =>
        Assert.Null(BotAccountJwt.TryReadExpiry(Jwt("""{"exp":1893456000}""") + ".ZXh0cmE"));

    [Fact]
    public void AnEmptySignature_IsMalformed() =>
        Assert.Null(BotAccountJwt.TryReadExpiry(Jwt("""{"exp":1893456000}""", signature: string.Empty)));

    [Fact]
    public void ASignatureThatIsNotDecodableBase64Url_IsMalformed() =>
        Assert.Null(BotAccountJwt.TryReadExpiry(Jwt("""{"exp":1893456000}""", signature: "A")));

    [Theory]
    [InlineData("""{"exp":"1893456000"}""")] // as text
    [InlineData("""{"exp":-1}""")] // negative
    [InlineData("""{"exp":-1.5}""")]
    [InlineData("""{"exp":0}""")]
    [InlineData("""{"sub":"bot"}""")] // missing
    [InlineData("""{"exp":null}""")]
    [InlineData("""{"exp":true}""")]
    [InlineData("""{"exp":1e30}""")] // beyond DateTimeOffset
    [InlineData("""{"exp":253402300800}""")]
    [InlineData("""[1893456000]""")] // not an object
    [InlineData("""{"exp":1893456000""")] // broken JSON
    public void AnUnusableExp_IsMalformed(string payload) => Assert.Null(BotAccountJwt.TryReadExpiry(Jwt(payload)));

    [Fact]
    public void AHeaderThatIsNoJsonObject_IsMalformed() =>
        Assert.Null(BotAccountJwt.TryReadExpiry(Jwt("""{"exp":1893456000}""", header: "not json")));

    [Theory]
    [InlineData("+")] // standard base64, not base64url
    [InlineData("/")]
    [InlineData("=")]
    [InlineData(" ")]
    public void ForeignCharacters_AreMalformed(string character)
    {
        var token = Jwt("""{"exp":1893456000}""");
        var tampered = token.Insert(token.IndexOf('.', StringComparison.Ordinal) + 2, character);

        Assert.Null(BotAccountJwt.TryReadExpiry(tampered));
    }

    [Fact]
    public void ASegmentOfImpossibleLength_IsMalformed()
    {
        // Five characters cannot be base64 of whole bytes.
        Assert.Null(BotAccountJwt.TryReadExpiry(Encode(Header) + ".abcde." + Signature));
    }

    [Fact]
    public void TheLengthLimit_IsInclusive()
    {
        var token = Jwt("""{"exp":1893456000}""");
        var atLimit = token + new string('A', BotAccountJwt.MaxLength - token.Length);

        Assert.Equal(BotAccountJwt.MaxLength, atLimit.Length);
        Assert.NotNull(BotAccountJwt.TryReadExpiry(atLimit));
        Assert.Null(BotAccountJwt.TryReadExpiry(atLimit + "A"));
    }

    [Fact]
    public void A64KilobyteToken_IsMalformed()
    {
        var padding = new string('x', 64 * 1024);

        Assert.Null(BotAccountJwt.TryReadExpiry(Jwt($$"""{"exp":1893456000,"pad":"{{padding}}"}""")));
    }

    private static string Jwt(string payload, string header = Header, string signature = Signature) =>
        $"{Encode(header)}.{Encode(payload)}.{signature}";

    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
