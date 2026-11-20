using System.Text;
using EmotePurge.Core.Chat;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;

namespace EmotePurge.Worker.WishCapture;

/// <summary>
/// Turns the values of one chat line that carries Twitch's <c>custom-reward-id</c> tag into a
/// <see cref="RewardObservation"/>, or into nothing (plan #486 2.1, spec K6 "Lesen im Worker"). Pure and
/// TwitchLib-free: it takes plain values, not TwitchLib's <c>ChatMessage</c>. Which TwitchLib field each value
/// comes from is <see cref="RewardCapture.Read"/>'s business, and <c>RewardRedemptionTwitchLibTests</c> pins it
/// against the installed library.
/// <para>
/// Only a line of the channel's own room becomes an observation (Sol finding 8 of the spec): a Shared Chat line
/// mirrored in from another room, and one whose room cannot be determined, never does, for learning and for
/// redemptions alike. The channel is named by <c>room-id</c> only, never by a name (plan W5).
/// </para>
/// <para>
/// The shape checks are a guard against splice remnants (#114, decision [E] of the plan): a value that does not
/// look like what Twitch sends is dropped rather than stored. A line the splice sentinel flags never gets here
/// (<see cref="ChatMessageRoute"/>). Never throws and never logs; the text is chat under a stranger's control.
/// </para>
/// </summary>
public static class RewardRedemptionParser
{
    /// <summary>Longest Twitch id this parser accepts for <c>room-id</c> and <c>user-id</c> (digits only).</summary>
    public const int MaxTwitchIdLength = 20;

    /// <summary>Longest login this parser accepts (Twitch logins are 25 characters at most).</summary>
    public const int MaxLoginLength = 25;

    /// <summary>
    /// The observation for one line, or <c>null</c> when the line yields none: no reward id, a room other than
    /// <see cref="MessageOrigin.Own"/>, a value outside its shape, or no usable send time.
    /// </summary>
    /// <param name="rewardId">The value of <c>custom-reward-id</c>; <c>null</c> without the tag, empty with an empty tag.</param>
    /// <param name="origin">The room classification of <see cref="SharedChatRule.FromTags"/> for this line.</param>
    /// <param name="roomId">The value of <c>room-id</c>.</param>
    /// <param name="messageId">The value of <c>id</c> (the message's id, the dedupe key).</param>
    /// <param name="userId">The value of <c>user-id</c>.</param>
    /// <param name="login">The sender's login from the IRC prefix.</param>
    /// <param name="text">The message text, possibly empty; an empty text still yields an observation (<c>unparsed</c>).</param>
    /// <param name="sentAtUtc">The value of <c>tmi-sent-ts</c>, in UTC; a value at or before the Unix epoch (tag missing or zero) yields nothing.</param>
    public static RewardObservation? Parse(
        string? rewardId,
        MessageOrigin origin,
        string? roomId,
        string? messageId,
        string? userId,
        string? login,
        string? text,
        DateTime sentAtUtc)
    {
        if (rewardId is null || origin != MessageOrigin.Own)
        {
            return null;
        }

        if (!IsTagId(rewardId, WishLimits.RewardIdMaxLength)
            || !IsTagId(messageId, WishLimits.IrcMessageIdMaxLength)
            || !IsTwitchId(roomId)
            || !IsTwitchId(userId)
            || !IsLogin(login))
        {
            return null;
        }

        // TwitchLib leaves TmiSent at default(DateTimeOffset) without the tag; a send time from year one would open
        // the wish's link window at the beginning of time (plan 2.5). A non-UTC value is a caller bug the database
        // would reject later; here it is dropped instead of thrown, like everything else on this path.
        if (sentAtUtc.Kind != DateTimeKind.Utc || sentAtUtc <= DateTime.UnixEpoch)
        {
            return null;
        }

        return new RewardObservation(roomId!, rewardId, messageId!, userId!, login!, CleanText(text), sentAtUtc);
    }

    /// <summary>
    /// Drops every control character (Unicode category Cc, which includes the <c>\u0001</c> of a <c>/me</c> line
    /// TwitchLib did not unwrap) and cuts the rest to <see cref="WishLimits.RequestTextMaxLength"/> code points,
    /// never inside a surrogate pair. A lone surrogate becomes U+FFFD, so the text always encodes as UTF-8.
    /// </summary>
    internal static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, WishLimits.RequestTextMaxLength * 2));
        Span<char> utf16 = stackalloc char[2];
        var codePoints = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                continue;
            }

            if (codePoints == WishLimits.RequestTextMaxLength)
            {
                break;
            }

            builder.Append(utf16[..rune.EncodeToUtf16(utf16)]);
            codePoints++;
        }

        return builder.ToString();
    }

    // Twitch's reward ids and message ids are UUIDs; 1 to maxLength characters of [A-Za-z0-9-] admits them and
    // nothing a splice could carry in (';', '=', '@', spaces).
    private static bool IsTagId(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTwitchId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTwitchIdLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    // Twitch logins are [a-z0-9_]; the upper case letters are admitted because the service lower-cases anyway.
    private static bool IsLogin(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLoginLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
