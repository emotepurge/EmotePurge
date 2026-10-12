using System.Diagnostics.CodeAnalysis;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The pending slot key (slot plan #373, E4/E16): what the match map hands out, with the slot
/// reconcile flag off, for an emote whose (emote, active set) pair has no slot yet — "no slot yet
/// for emote X" in place of a slot id. The chat counter and the worker treat it as an opaque string
/// like any slot id; only the usage flush recognises it and resolves it to a base slot of the pair
/// (<see cref="BaseSlotRule"/>), creating the seed together with the pair's first row.
/// <para>
/// Format: <c>pending-slot:</c> followed by the <c>Emote.Id</c>. The prefix contains a colon, which
/// neither a slot id (a Guid string, from <c>Guid.NewGuid()</c> or <c>gen_random_uuid()</c>) nor an
/// emote id ever does, so a real slot id is never mistaken for a pending key. This class is the only
/// producer and the only recogniser; nothing outside Infrastructure depends on the format.
/// </para>
/// </summary>
internal static class PendingSlotKey
{
    /// <summary>The marker every pending key starts with (ordinal).</summary>
    public const string Prefix = "pending-slot:";

    /// <summary>The pending key standing for the not yet existing slot of <paramref name="emoteId"/>.</summary>
    public static string For(string emoteId)
    {
        ArgumentException.ThrowIfNullOrEmpty(emoteId);
        return Prefix + emoteId;
    }

    /// <summary>
    /// True when <paramref name="slotKey"/> is a pending key; <paramref name="emoteId"/> is then the
    /// emote it stands for. False for every real slot id, and for the bare prefix (no emote).
    /// </summary>
    public static bool TryGetEmoteId(string slotKey, [NotNullWhen(true)] out string? emoteId)
    {
        if (slotKey.Length > Prefix.Length && slotKey.StartsWith(Prefix, StringComparison.Ordinal))
        {
            emoteId = slotKey[Prefix.Length..];
            return true;
        }

        emoteId = null;
        return false;
    }

    /// <summary>True when <paramref name="slotKey"/> is a pending key rather than a slot id.</summary>
    public static bool IsPending(string slotKey) => TryGetEmoteId(slotKey, out _);
}
