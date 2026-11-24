using System.Text.Json;

namespace EmotePurge.Core.Services;

/// <summary>
/// The form check of the bot account's 7TV token (spec 7.3): three base64url segments, a JSON payload with a numeric
/// <c>exp</c>. The signature is <b>not</b> verified; <c>exp</c> is read for display and for rejecting a token that has
/// already expired. Pure, no I/O. Every deviation reads as "malformed" (<c>bot_account_token_malformed</c>); a token
/// without <c>exp</c> is malformed too, because the admin page has to show the expiry (spec 7.7).
/// </summary>
public static class BotAccountJwt
{
    /// <summary>Upper bound for a token, in characters. Anything longer is malformed (plan #487 T2).</summary>
    public const int MaxLength = 8 * 1024;

    // DateTimeOffset.MaxValue as Unix seconds; beyond it FromUnixTimeSeconds throws.
    private const long MaxUnixSeconds = 253_402_300_799;

    /// <summary>
    /// Returns <c>exp</c> as a UTC <see cref="DateTime"/>, or <c>null</c> when the token is not of JWT form or carries
    /// no positive numeric <c>exp</c>.
    /// </summary>
    public static DateTime? TryReadExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxLength)
        {
            return null;
        }

        var segments = token.Split('.');
        if (segments.Length != 3 || !segments.All(IsBase64UrlSegment))
        {
            return null;
        }

        var header = DecodeBase64Url(segments[0]);
        var payload = DecodeBase64Url(segments[1]);
        if (header is null || payload is null || !IsJsonObject(header))
        {
            return null;
        }

        return ReadExp(payload);
    }

    private static DateTime? ReadExp(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("exp", out var exp)
                || exp.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            long seconds;
            if (exp.TryGetInt64(out var whole))
            {
                seconds = whole;
            }
            else if (exp.TryGetDouble(out var fractional) && double.IsFinite(fractional) && fractional > 0 && fractional < MaxUnixSeconds)
            {
                // RFC 7519 allows a fractional NumericDate; the whole second is enough here.
                seconds = (long)Math.Floor(fractional);
            }
            else
            {
                return null;
            }

            if (seconds <= 0 || seconds > MaxUnixSeconds)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJsonObject(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsBase64UrlSegment(string segment) =>
        segment.Length > 0 && segment.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    private static byte[]? DecodeBase64Url(string segment)
    {
        // A remainder of 1 is impossible for base64 of whole bytes.
        if (segment.Length % 4 == 1)
        {
            return null;
        }

        var padded = segment.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
