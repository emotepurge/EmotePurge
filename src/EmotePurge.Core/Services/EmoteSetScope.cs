namespace EmotePurge.Core.Services;

/// <summary>
/// Which emote sets a usage query reads. A named value with three states rather than a nullable
/// set id, because <c>null</c> already means "the channel's active set" on the channel-scoped
/// reads of <see cref="IUsageStatQueryService"/>; giving it a second meaning ("no filter") on the
/// same interface is the trap a null-session's usage fell into. <c>default</c> is
/// <see cref="ActiveSet"/>, so a caller that omits the parameter keeps the behaviour it had.
/// </summary>
public readonly record struct EmoteSetScope
{
    private readonly ScopeKind kind;
    private readonly string? setId;

    private EmoteSetScope(ScopeKind kind, string? setId)
    {
        this.kind = kind;
        this.setId = setId;
    }

    /// <summary>The channel's currently active set — resolved by the query against the channel.</summary>
    public static EmoteSetScope ActiveSet => default;

    /// <summary>Every set: no set predicate at all, usage summed across whatever sets the rows carry.</summary>
    public static EmoteSetScope AllSets => new(ScopeKind.All, null);

    public bool IsActiveSet => kind == ScopeKind.Active;

    public bool IsAllSets => kind == ScopeKind.All;

    /// <summary>The named set's id, or <c>null</c> for <see cref="ActiveSet"/> and <see cref="AllSets"/>.</summary>
    public string? SetId => kind == ScopeKind.Set ? setId : null;

    /// <summary>
    /// Exactly this set. A null or empty id is rejected: it would otherwise silently match no row,
    /// and this type is the last line of defence before the query.
    /// </summary>
    public static EmoteSetScope Set(string setId)
    {
        if (string.IsNullOrEmpty(setId))
        {
            throw new ArgumentException("A set scope needs a non-empty emote set id.", nameof(setId));
        }

        return new EmoteSetScope(ScopeKind.Set, setId);
    }

    private enum ScopeKind
    {
        Active = 0,
        Set = 1,
        All = 2
    }
}
