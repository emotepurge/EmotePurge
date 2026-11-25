using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>What a confirmation read of a bot add says (<see cref="WishBotOwnership.Confirm"/>).</summary>
/// <param name="Confirmed">Exactly one entry of the emote under the alias, of this attempt's generation.</param>
/// <param name="AddedAtUtc">That entry's <c>addedAt</c>, the wish's authoritative start; set only when confirmed.</param>
/// <param name="Owned">The bot added it (its <c>addedById</c>) and the add was acknowledged in this attempt.</param>
public readonly record struct WishBotConfirmation(bool Confirmed, DateTime? AddedAtUtc, bool Owned)
{
    public static WishBotConfirmation NotConfirmed { get; } = new(false, null, false);
}

/// <summary>
/// The ownership decision of a bot add (#487 plan 2.6 point 6, 2.5a (d), spec 8, AK77), pure. A read of the attempt's set
/// confirms when <b>exactly one</b> entry carries the emote id <b>and</b> the alias (ordinal) <b>and</b> an <c>addedAt</c>
/// within the attempt's generation; an entry without <c>addedAt</c> is not confirmed yet (plan 2.1 point 3: no
/// provisional start in the bot flow). Ownership needs, on top, <c>addedById</c> equal to the current bot account
/// <b>and</b> the proof of sending (<c>BotAddAckAtUtc</c>, [B] F6): without it the confirmation is the safe one without
/// ownership, whose expiry presents instead of removing.
/// </summary>
public static class WishBotOwnership
{
    /// <summary>How far 7TV's clock may lie behind or ahead of the worker's around the generation's bounds (plan 2.5a (d)).</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <param name="entries">The set's entries as the read (or the add's answer) returned them.</param>
    /// <param name="emoteId">The wish's 7TV emote id.</param>
    /// <param name="alias">The alias the bot wrote (the wish's alias).</param>
    /// <param name="botSevenTvUserId">The bot account's 7TV id of the identity that applies now.</param>
    /// <param name="writtenAtUtc"><c>BotWriteAtUtc</c>: the generation starts <see cref="ClockSkew"/> before it.</param>
    /// <param name="readAtUtc">When the read began: the generation ends <see cref="ClockSkew"/> after it.</param>
    /// <param name="sendAcknowledged"><c>BotAddAckAtUtc</c> is set: <c>addEmote</c> answered a proven <c>Ok</c> in this attempt.</param>
    public static WishBotConfirmation Confirm(
        IReadOnlyCollection<SevenTvSetEntry> entries,
        string emoteId,
        string alias,
        string? botSevenTvUserId,
        DateTime writtenAtUtc,
        DateTime readAtUtc,
        bool sendAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(emoteId);
        ArgumentNullException.ThrowIfNull(alias);

        var matches = entries
            .Where(e => string.Equals(e.EmoteId, emoteId, StringComparison.Ordinal) && string.Equals(e.Alias, alias, StringComparison.Ordinal))
            .ToList();
        if (matches.Count != 1 || matches[0].AddedAtUtc is not { } addedAt)
        {
            return WishBotConfirmation.NotConfirmed;
        }

        if (addedAt < writtenAtUtc - ClockSkew || addedAt > readAtUtc + ClockSkew)
        {
            return WishBotConfirmation.NotConfirmed;
        }

        var owned = sendAcknowledged
            && botSevenTvUserId is not null
            && string.Equals(matches[0].AddedById, botSevenTvUserId, StringComparison.Ordinal);
        return new WishBotConfirmation(true, addedAt, owned);
    }
}
