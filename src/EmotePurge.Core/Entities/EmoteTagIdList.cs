using EmotePurge.Core.SevenTv;

namespace EmotePurge.Core.Entities;

/// <summary>Verdict of <see cref="EmoteTagIdList.Check"/>.</summary>
public enum EmoteTagIdListStatus
{
    Ok,

    /// <summary><c>null</c> or no ids at all.</summary>
    Empty,

    /// <summary>More than <see cref="EmoteTagLimits.MaxIdsPerRequest"/> raw ids, or an id that fails <see cref="SevenTvEmoteIdValidation"/>.</summary>
    Invalid
}

/// <summary>
/// The one rule for a list of 7TV emote ids in a tag request body (#201): at most
/// <see cref="EmoteTagLimits.MaxIdsPerRequest"/> raw ids (counted before de-duplication, so an
/// unbounded body never reaches the database) and every id in the
/// <see cref="SevenTvEmoteIdValidation"/> format. Shared rather than copied: the tag service applies it
/// to entry requests, and the Api applies the same rule to the report bodies before it asks 7TV
/// anything.
/// <para>
/// Whether an empty list is legal is the caller's call, which is why <see cref="EmoteTagIdListStatus.Empty"/>
/// is its own verdict: tagging nothing is a malformed request, reporting a run that added nothing is
/// not. Emptiness wins over unfitness, so a <c>null</c> list is never "invalid".
/// </para>
/// </summary>
public static class EmoteTagIdList
{
    public static EmoteTagIdListStatus Check(IReadOnlyList<string>? sevenTvEmoteIds)
    {
        if (sevenTvEmoteIds is null || sevenTvEmoteIds.Count == 0)
        {
            return EmoteTagIdListStatus.Empty;
        }

        return sevenTvEmoteIds.Count > EmoteTagLimits.MaxIdsPerRequest || !sevenTvEmoteIds.All(SevenTvEmoteIdValidation.IsValid)
            ? EmoteTagIdListStatus.Invalid
            : EmoteTagIdListStatus.Ok;
    }
}
