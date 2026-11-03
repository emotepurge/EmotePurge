using System.Collections.Frozen;

namespace EmotePurge.Core.Matching;

/// <summary>
/// The one emote-name matching rule shared by the live chat path
/// (<c>TwitchChatManager.OnMessageReceived</c>), the live match-cache build
/// (<c>SevenTvSyncService.RefreshMatchCacheAsync</c>) and the chat-log backfill.
/// <para>
/// The rule is deliberately naive — split on plain spaces, ordinal lookup, no trimming, no
/// case-folding, no Twitch emote tags — and is not a place to fix anything: the backfill must
/// count an archived message exactly as the live path would have, and a "better" rule here would
/// change what is counted on both sides at once. See the DECISIONS.md entry for this file.
/// </para>
/// </summary>
public static class EmoteNameMatching
{
    // FrozenSet.Empty, not a plain HashSet cast to IReadOnlySet<string>: a plain HashSet is still
    // mutable behind the interface, so a caller that casts back could poison this shared instance
    // for every consumer process-wide (Task 5/6 are two more callers of the two-argument overload
    // below). FrozenSet.Empty is a genuine immutable singleton.
    private static readonly IReadOnlySet<string> EmptyMatches = FrozenSet<string>.Empty;
    private static readonly IReadOnlySet<string> EmptyAmbiguousNames = FrozenSet<string>.Empty;

    /// <summary>
    /// Splits <paramref name="message"/> on single spaces and looks up every token ordinally in
    /// <paramref name="nameToId"/>, returning the set of matched emote ids deduplicated per
    /// message — i.e. "messages using this emote", not "occurrences". An empty message or an
    /// empty map returns a shared empty instance without allocating.
    /// <para>
    /// Iterating the result through this <see cref="IReadOnlySet{T}"/>-typed overload boxes
    /// <see cref="HashSet{T}"/>'s struct enumerator on every call, because <c>foreach</c> then has
    /// to go through <c>IEnumerable&lt;T&gt;.GetEnumerator()</c> instead of the concrete type's own
    /// method. That is an acceptable one-time cost for occasional callers (Task 5/6), but not for
    /// the chat hot path, which handles every incoming message — see the three-argument overload
    /// below for that case.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> MatchEmoteIds(string message, IReadOnlyDictionary<string, string> nameToId)
    {
        if (message.Length == 0 || nameToId.Count == 0)
        {
            return EmptyMatches;
        }

        var matched = new HashSet<string>();
        MatchEmoteIds(message, nameToId, matched);
        return matched;
    }

    /// <summary>
    /// The buffer-taking twin of <see cref="MatchEmoteIds(string, IReadOnlyDictionary{string, string})"/>:
    /// same rule (single-space split, ordinal lookup, per-message dedup via <see cref="HashSet{T}.Add"/>'s
    /// own dedup), but the caller owns <paramref name="into"/> and can therefore iterate it through
    /// its concrete type afterwards — the struct enumerator, no boxing. This overload only ever
    /// <b>adds</b> to <paramref name="into"/>; it does not clear it first. Exists for exactly one
    /// caller today, <c>TwitchChatManager.OnMessageReceived</c>, which allocates one
    /// <see cref="HashSet{T}"/> per message (same as before this class existed) and then iterates it
    /// directly instead of through the two-argument overload's interface-typed return.
    /// </summary>
    public static void MatchEmoteIds(string message, IReadOnlyDictionary<string, string> nameToId, HashSet<string> into)
    {
        if (message.Length == 0 || nameToId.Count == 0)
        {
            return;
        }

        // Exactly one array from Split, no further allocation here: no LINQ, no closure, no boxed
        // enumerator — `into` is iterated by its caller through its concrete type.
        foreach (var token in message.Split(' '))
        {
            if (nameToId.TryGetValue(token, out var emoteId))
            {
                into.Add(emoteId);
            }
        }
    }

    /// <summary>
    /// Coalesces emotes sharing the same chat name onto the first one encountered in
    /// <paramref name="emotesInLoadOrder"/>, since the same name can legitimately be active on two
    /// emotes at once (observed live). The winner is therefore whatever order the caller loaded
    /// its emotes in — for the live match cache that is an unspecified query order without an
    /// <c>OrderBy</c>, which this shared rule preserves rather than fixes.
    /// </summary>
    public static EmoteNameMap Coalesce(IEnumerable<KeyValuePair<string, string>> emotesInLoadOrder)
    {
        var nameToId = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string>? ambiguousNames = null;

        foreach (var (name, id) in emotesInLoadOrder)
        {
            if (!nameToId.TryAdd(name, id))
            {
                (ambiguousNames ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);
            }
        }

        return new EmoteNameMap(nameToId, (IReadOnlySet<string>?)ambiguousNames ?? EmptyAmbiguousNames);
    }
}

/// <summary>
/// Result of <see cref="EmoteNameMatching.Coalesce"/>: the winning name-to-id map (first entry per
/// name) plus every name that collided along the way.
/// </summary>
public readonly record struct EmoteNameMap(Dictionary<string, string> NameToId, IReadOnlySet<string> AmbiguousNames);
