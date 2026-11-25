using System.Text.Json;
using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Services;

/// <summary>What a read of the bot's set says about the entry it is about to remove, or has removed (#487 plan 2.7).</summary>
public enum WishBotRemovalRead
{
    /// <summary>The bot's entry is there with all four features (emote id, alias, <c>addedAt</c>, <c>addedById</c>).</summary>
    Owned,

    /// <summary>
    /// The bot's entry is there but differs in one feature, or it was renamed (an entry of the emote with the bot's
    /// generation under another alias, probe P4): ownership ends, nothing is sent (<c>ownership_lost</c>).
    /// </summary>
    Changed,

    /// <summary>No entry of the emote under the alias, and none of the bot's generation: removed outside the bot.</summary>
    Gone,
}

/// <summary>
/// The evidence of a removal attempt (#487 plan 2.5a, 2.7, spec 8), stored as jsonb in <c>Wishes.BotRemoveEvidence</c>
/// before <c>removeEmote</c> is sent: the attempt's set (<see cref="SetId"/>, Revision 6c K6c-2, so every later read goes
/// to it even after <c>BotSetId</c> was cleared), the bot's entry with its four features, and the other entries of the
/// same emote with alias and <c>addedAt</c> only (never a foreign <c>addedById</c>). Pure: the comparisons a removal makes
/// against a read (<see cref="Classify"/>, <see cref="OthersUnchanged"/>, <see cref="MissingOthers"/>) and the json
/// form (<see cref="ToJson"/>, <see cref="TryParse"/>). Never in a DTO, never in the audit.
/// </summary>
/// <param name="SetId">The set of the attempt (<c>BotSetId</c> when it was recorded).</param>
/// <param name="EmoteId">The wish's 7TV emote id.</param>
/// <param name="Alias">The alias of the bot's entry.</param>
/// <param name="AddedAtUtc">The bot's entry's <c>addedAt</c> (= <c>BotOwnedAddedAtUtc</c>).</param>
/// <param name="AddedById">The bot account's 7TV id that added it.</param>
/// <param name="Others">The other entries of the emote in the set at the read, by alias and <c>addedAt</c>.</param>
public sealed record WishBotRemovalEvidence(
    string SetId,
    string EmoteId,
    string Alias,
    DateTime AddedAtUtc,
    string AddedById,
    IReadOnlyList<WishBotRemovalOtherEntry> Others)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The decision of a read before a removal (plan 2.7 transaction 1, spec 8): <see cref="WishBotRemovalRead.Owned"/>
    /// only with the alias exact (ordinal), <c>addedAt</c> equal to <paramref name="ownedAddedAtUtc"/> (to the
    /// microsecond) and <c>addedById</c> equal to <paramref name="botSevenTvUserId"/>.
    /// </summary>
    public static WishBotRemovalRead Classify(
        IReadOnlyCollection<SevenTvSetEntry> entries,
        string emoteId,
        string alias,
        DateTime ownedAddedAtUtc,
        string? botSevenTvUserId)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var target = entries.FirstOrDefault(e => SameEmote(e, emoteId) && string.Equals(e.Alias, alias, StringComparison.Ordinal));
        if (target is not null)
        {
            return SameInstant(target.AddedAtUtc, ownedAddedAtUtc)
                && botSevenTvUserId is not null
                && string.Equals(target.AddedById, botSevenTvUserId, StringComparison.Ordinal)
                    ? WishBotRemovalRead.Owned
                    : WishBotRemovalRead.Changed;
        }

        // Probe P4: a rename keeps addedAt and addedById, only the alias moves. The bot's generation under another alias
        // is still in the set, so it was not removed: ownership ends without a removal.
        return entries.Any(e => SameEmote(e, emoteId)
                && SameInstant(e.AddedAtUtc, ownedAddedAtUtc)
                && botSevenTvUserId is not null
                && string.Equals(e.AddedById, botSevenTvUserId, StringComparison.Ordinal))
            ? WishBotRemovalRead.Changed
            : WishBotRemovalRead.Gone;
    }

    /// <summary>The evidence of an <see cref="WishBotRemovalRead.Owned"/> read: the bot's entry and the others of the emote.</summary>
    public static WishBotRemovalEvidence Capture(
        IReadOnlyCollection<SevenTvSetEntry> entries,
        string setId,
        string emoteId,
        string alias,
        DateTime ownedAddedAtUtc,
        string botSevenTvUserId)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        return new WishBotRemovalEvidence(setId, emoteId, alias, ownedAddedAtUtc, botSevenTvUserId, OthersOf(entries, emoteId, alias));
    }

    /// <summary>
    /// <see cref="Classify"/> of a read against this evidence's bot entry: the fresh read before the send (plan 2.7
    /// transaction 2), the read after it (transaction 3), the resumption (2.5a (e)) and the repair check (2.5a (h)).
    /// </summary>
    public WishBotRemovalRead Classify(IReadOnlyCollection<SevenTvSetEntry> entries) =>
        Classify(entries, EmoteId, Alias, AddedAtUtc, AddedById);

    /// <summary>
    /// The other entries of the emote are exactly the recorded ones, by alias and <c>addedAt</c> (plan 2.7 transaction 2):
    /// one that changed, left or came in means the evidence is stale and a new one is recorded first.
    /// </summary>
    public bool OthersUnchanged(IReadOnlyCollection<SevenTvSetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var now = OthersOf(entries, EmoteId, Alias);
        return now.Count == Others.Count
            && now.All(n => Others.Any(o => string.Equals(o.Alias, n.Alias, StringComparison.Ordinal) && SameInstant(o.AddedAtUtc, n.AddedAtUtc)));
    }

    /// <summary>
    /// The recorded other entries of which the read shows no entry of the emote under that alias any more (plan 2.7
    /// "Reparatur", 2.5a (h)), in the recorded order. A repaired entry carries a new <c>addedAt</c>, so only the alias
    /// counts here.
    /// </summary>
    public IReadOnlyList<WishBotRemovalOtherEntry> MissingOthers(IReadOnlyCollection<SevenTvSetEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Others
            .Where(o => !entries.Any(e => SameEmote(e, EmoteId) && string.Equals(e.Alias, o.Alias, StringComparison.Ordinal)))
            .ToList();
    }

    /// <summary>The jsonb form stored in <c>BotRemoveEvidence</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new Stored(SetId, new StoredTarget(EmoteId, Alias, AddedAtUtc, AddedById), [.. Others.Select(o => new StoredOther(o.Alias, o.AddedAtUtc))]),
        Json);

    /// <summary>The evidence stored in <paramref name="json"/>; null for none or for a form that cannot be read.</summary>
    public static WishBotRemovalEvidence? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonSerializer.Deserialize<Stored>(json, Json) is not { SetId: { Length: > 0 } setId, Target: { } target } stored
                || string.IsNullOrEmpty(target.EmoteId)
                || string.IsNullOrEmpty(target.Alias)
                || string.IsNullOrEmpty(target.AddedById))
            {
                return null;
            }

            var others = (stored.Others ?? [])
                .Where(o => !string.IsNullOrEmpty(o.Alias))
                .Select(o => new WishBotRemovalOtherEntry(o.Alias!, o.AddedAtUtc))
                .ToList();
            return new WishBotRemovalEvidence(setId, target.EmoteId!, target.Alias!, Utc(target.AddedAtUtc), target.AddedById!, others);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<WishBotRemovalOtherEntry> OthersOf(IReadOnlyCollection<SevenTvSetEntry> entries, string emoteId, string alias) =>
        entries
            .Where(e => SameEmote(e, emoteId) && !string.Equals(e.Alias, alias, StringComparison.Ordinal))
            .Select(e => new WishBotRemovalOtherEntry(e.Alias, e.AddedAtUtc is { } at ? Utc(at) : null))
            .ToList();

    private static bool SameEmote(SevenTvSetEntry entry, string emoteId) =>
        string.Equals(entry.EmoteId, emoteId, StringComparison.Ordinal);

    // To the microsecond, as Postgres stores timestamptz; 7TV reports milliseconds. Null matches only null.
    private static bool SameInstant(DateTime? a, DateTime? b) =>
        a is null || b is null ? a is null && b is null : Microseconds(a.Value) == Microseconds(b.Value);

    private static long Microseconds(DateTime value) => Utc(value).Ticks / TimeSpan.TicksPerMicrosecond;

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private sealed record Stored(string? SetId, StoredTarget? Target, List<StoredOther>? Others);

    private sealed record StoredTarget(string? EmoteId, string? Alias, DateTime AddedAtUtc, string? AddedById);

    private sealed record StoredOther(string? Alias, DateTime? AddedAtUtc);
}

/// <summary>Another entry of the removed emote in the set, as the evidence keeps it: alias and <c>addedAt</c>, no <c>addedById</c>.</summary>
public sealed record WishBotRemovalOtherEntry(string Alias, DateTime? AddedAtUtc);
