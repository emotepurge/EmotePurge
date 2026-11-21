using System.Globalization;

namespace EmotePurge.Core.SevenTv;

/// <summary>
/// The one form under which EmotePurge knows a 7TV emote id: the 26-character ULID in upper case, the form
/// 7TV answers with and that sets, slots and wishes carry. Older links carry the 24-character hex ObjectID of
/// the same emote; it maps onto the ULID without a request (the ULID's 48-bit time is the ObjectID's 4-byte
/// seconds in milliseconds, then 16 zero bits, then the ObjectID's other 8 bytes). The same mapping as
/// <c>canonicalSevenTvEmoteId</c> in the web client (measured live for three emotes, plan #486 2.5a).
/// Pure, BCL only, no 7TV call.
/// </summary>
public static class SevenTvEmoteIdCanonical
{
    private const string UlidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int ObjectIdLength = 24;
    private const int UlidLength = 26;

    /// <summary>
    /// ULID (any case) to upper case; hex ObjectID (any case) to its ULID; anything else comes back unchanged
    /// (the shape check stays with <see cref="SevenTvEmoteIdValidation"/>).
    /// </summary>
    public static string Canonicalize(string id)
    {
        if (id is null)
            return id!;

        if (id.Length == UlidLength && IsUlid(id))
            return id.ToUpperInvariant();

        if (id.Length != ObjectIdLength || !IsHex(id))
            return id;

        var seconds = ulong.Parse(id.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var rest = ulong.Parse(id.AsSpan(8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var value = ((UInt128)(seconds * 1000UL) << 80) | rest;

        Span<char> ulid = stackalloc char[UlidLength];
        for (var i = UlidLength - 1; i >= 0; i--)
        {
            ulid[i] = UlidAlphabet[(int)(value & 31)];
            value >>= 5;
        }

        return new string(ulid);
    }

    private static bool IsUlid(string id)
    {
        foreach (var c in id)
        {
            // ASCII first: ToUpperInvariant folds non-ASCII letters (U+017F long s) onto the alphabet.
            if (!char.IsAscii(c) || UlidAlphabet.IndexOf(char.ToUpperInvariant(c)) < 0)
                return false;
        }

        return true;
    }

    private static bool IsHex(string id)
    {
        foreach (var c in id)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }
}
