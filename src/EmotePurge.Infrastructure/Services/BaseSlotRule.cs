using EmotePurge.Core.Services;

namespace EmotePurge.Infrastructure.Services;

/// <summary>
/// The D2 order of an emote's slots (slot plan #373, E5): <c>AddedAt</c> ascending with null last, then
/// <c>Name</c>, then <c>Id</c>, both in UTF-8 byte order (<see cref="Utf8Order"/>, what
/// <c>COLLATE "C"</c> does in <c>EmoteSlotSeedSql</c>) — decided in memory, never by an
/// <c>ORDER BY "Name"</c> in SQL, whose result would follow the column collation (the Alpine/ICU trap).
/// The first slot in this order is the emote's base slot. Readers order an emote's slots by it; ballots
/// and tag snapshots take "the oldest alias" from it; the reconcile planner breaks name ties with
/// <see cref="Utf8Order"/>. Pure: no I/O.
/// </summary>
/// <remarks>
/// The name is historical: the type also held the get-or-create rule of the base slot of an
/// (emote, set) pair, which only the writers of the retired flag-off path used (plan 379, part B).
/// </remarks>
internal static class BaseSlotRule
{
    /// <summary>
    /// The D2 order of the slots of one emote: <c>AddedAt</c> ascending with null last, then
    /// <c>Name</c>, then <c>Id</c>, both in <see cref="Utf8Order"/>. The first element is the base
    /// slot. The same order as <c>EmoteSlotSeedSql</c> and <c>EmoteSlotRollbackSql</c> (there with
    /// <c>COLLATE "C"</c>).
    /// </summary>
    public static readonly IComparer<BaseSlotCandidate> D2Order = Comparer<BaseSlotCandidate>.Create(CompareD2);

    /// <summary>
    /// Strings in UTF-8 byte order, the order of Postgres' <c>COLLATE "C"</c> on a UTF-8 database.
    /// <see cref="StringComparer.Ordinal"/> is not that order: it compares UTF-16 code units, so a
    /// supplementary character (a surrogate pair, 0xD800–0xDBFF first) sorts before U+E000–U+FFFF,
    /// while its UTF-8 form (0xF0 lead byte) sorts after them. Comparing Unicode scalar values is
    /// UTF-8 byte order; lone surrogates, which Postgres cannot store, fall back to ordinal.
    /// </summary>
    public static readonly IComparer<string> Utf8Order = Comparer<string>.Create(CompareUtf8);

    /// <summary>
    /// The entries of a live 7TV set list (one emote id may appear under several aliases) in the D2 order
    /// that decides "the oldest alias" for ballots and tag snapshots (spec D26/D29): <c>AddedAt</c>
    /// ascending with null last, then <c>Name</c> in <see cref="Utf8Order"/>. Live entries carry no slot
    /// id, so entries that tie on both stay in list order (stable sort).
    /// </summary>
    internal static IReadOnlyList<ForeignEmoteRow> OldestAliasFirst(IEnumerable<ForeignEmoteRow> entries) =>
        entries.OrderBy(e => new BaseSlotCandidate(string.Empty, e.Name, e.AddedAt), D2Order).ToList();

    private static int CompareD2(BaseSlotCandidate? x, BaseSlotCandidate? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        var byDate = (x.AddedAt, y.AddedAt) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (a, b) => a.Value.CompareTo(b.Value),
        };
        if (byDate != 0)
        {
            return byDate;
        }

        var byName = CompareUtf8(x.Name, y.Name);
        return byName != 0 ? byName : CompareUtf8(x.SlotId, y.SlotId);
    }

    private static int CompareUtf8(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        var a = x.EnumerateRunes();
        var b = y.EnumerateRunes();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB)
            {
                // A prefix sorts first; two strings equal rune by rune differ at most in lone
                // surrogates (both read as U+FFFD), which ordinal keeps apart.
                return hasA == hasB ? string.CompareOrdinal(x, y) : (hasA ? 1 : -1);
            }

            var byScalar = a.Current.Value.CompareTo(b.Current.Value);
            if (byScalar != 0)
            {
                return byScalar;
            }
        }
    }
}

/// <summary>A slot as the D2 order sees it.</summary>
internal sealed record BaseSlotCandidate(string SlotId, string Name, DateTime? AddedAt);
