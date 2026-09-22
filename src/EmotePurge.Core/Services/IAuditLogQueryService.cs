namespace EmotePurge.Core.Services;

/// <summary>
/// An action's target set, projected onto an <see cref="AuditLogDetail"/> whenever
/// <c>DetailsJson</c> names one. Two independent write paths feed this: the import ladder
/// (<c>targetEmoteSetId</c>, spec 6.7) — from either <c>emotes.MarkImportedAsync</c> (a set on the
/// channel-scoped endpoint, E5) or <c>emotes.MarkImportedToSetAsync</c> (the set-centric endpoint,
/// always) — and the set-scoped <c>emotes.syncDeleted</c>/<c>emotes.syncRestored</c>
/// (<c>emoteSetId</c>, spec 6.6, K5/T5.2). <see cref="Id"/> and <see cref="OwnerLogin"/> are never a
/// display name: identification is always by id (<c>targetOwnerSevenTvUserId</c>, the entry's own
/// <c>TargetId</c>), and the paper trail records a Twitch login rather than 7TV's own display name,
/// which can change without the account changing (spec 6.7's deliberate split between the
/// confirmation dialog's <c>ownerDisplayName</c> and this field). <see cref="OwnerLogin"/> is
/// exclusively an import-ladder field — sync-deleted/sync-restored never resolves one.
/// </summary>
/// <param name="Id">The target set's 7TV id — the entry's own <c>TargetId</c>, echoed here so a
/// consumer never has to cross-reference the two.</param>
/// <param name="IsActiveSetOfChannel">
/// Three-valued (E5): <c>true</c>/<c>false</c> when the channel-scoped endpoint compared the
/// reported set against <c>Channel.ActiveEmoteSetId</c> at write time, <c>null</c> when no set was
/// reported at all, or — for the set-centric endpoint — because the target set has no channel of
/// ours to compare against in the first place. The set-scoped sync-deleted/sync-restored always
/// writes <c>true</c> or <c>false</c> when it names a target at all (spec 6.6) — it never reports a
/// set without comparing it against the channel's active one.
/// </param>
/// <param name="OwnerLogin">
/// The Twitch login of the set's owner (the set-centric endpoint's own account, or the matching
/// editor grant's channel) — <c>null</c> for the channel-scoped endpoint, which never resolves one.
/// </param>
public record AuditLogTargetEmoteSet(string Id, bool? IsActiveSetOfChannel, string? OwnerLogin);

/// <summary>
/// The renderable part of an entry's <c>DetailsJson</c>, reduced to a closed set of shapes.
/// <paramref name="Kind"/> is language-neutral like <see cref="Entities.AuditActions"/> — the
/// frontend maps it to a translation key and owns the wording. <paramref name="Count"/> carries the
/// number for counting kinds, <paramref name="Text"/> the string for naming kinds; exactly one of
/// the two is set per kind.
/// <para>
/// This exists because <c>DetailsJson</c> is free-form by design: every action writes its own shape
/// into a jsonb column, and nothing stops a future write path from putting something in there that
/// its author never meant for a channel's moderators to read. Whitelisting here rather than in the
/// client makes that a structural guarantee instead of a review question — a new key is invisible
/// to every consumer until someone adds it to <see cref="Kinds"/> on purpose.
/// </para>
/// </summary>
public record AuditLogDetail(string Kind, long? Count, string? Text, AuditLogTargetEmoteSet? TargetEmoteSet = null)
{
    /// <summary>The recognized <see cref="Kind"/> values. Anything else is dropped.</summary>
    public static class Kinds
    {
        public const string EmoteCount = "emoteCount";
        public const string RemovedEntries = "removedEntries";
        public const string Title = "title";
        // The three emotes.syncImported shapes: same payload (an emote count plus the source), but
        // "where from" only renders when there is somewhere to point to. Count and Text are both
        // set for ImportedFromChannel; ImportedFromFile carries only Count.
        public const string ImportedFromChannel = "importedFromChannel";
        public const string ImportedFromFile = "importedFromFile";
        // A network-wide 7TV ranking has no source channel (leaderboard-import spec E2/E8/E9): Text
        // carries the language-neutral sort wire code (e.g. "TRENDING_DAILY") instead of a channel
        // name, and the frontend translates it — the same contract as every other Kind here (rule 7).
        public const string ImportedFromLeaderboard = "importedFromLeaderboard";
    }
}

/// <summary>
/// One audit-log row as the UI receives it — a projection of <see cref="Entities.AuditLogEntry"/>,
/// including <paramref name="Id"/> so the client has a stable list key.
/// <paramref name="Action"/> is one of the <see cref="Entities.AuditActions"/> constants and stays
/// language-neutral; the frontend owns the wording.
/// <para>
/// Two entity fields are deliberately absent. <c>DetailsJson</c> never leaves the server raw — see
/// <see cref="AuditLogDetail"/> — and <c>ActorTwitchUserId</c> has no consumer: every surface
/// identifies the actor by login, and shipping the numeric id would only widen what a channel's
/// moderators learn about each other. Add it back together with its first consumer.
/// </para>
/// </summary>
public record AuditLogEntryDto(
    long Id,
    DateTime OccurredAtUtc,
    string ActorLogin,
    string Action,
    string? ChannelName,
    string? TargetType,
    string? TargetId,
    AuditLogDetail? Detail);

/// <summary>
/// Optional narrowing of the audit-log list; every field is AND-combined, null means "no filter".
/// <paramref name="Action"/> matches exactly against the <see cref="Entities.AuditActions"/>
/// constants (an unknown value yields an empty page rather than an error).
/// <paramref name="ChannelName"/> matches exactly on the normalized form — callers may pass raw
/// user input, the implementation normalizes via <see cref="Entities.ChannelName.Normalize"/>.
/// <paramref name="ActorLogin"/> is a case-insensitive substring match, because actor logins are
/// not enumerable in the UI the way actions and channels are.
/// </summary>
public record AuditLogFilter(string? Action, string? ChannelName, string? ActorLogin);

/// <summary>
/// Read side of the audit log, behind GET /api/admin/audit-log and GET
/// /api/channels/{channelName}/audit-log. Separate from the services that write entries: those own
/// one action each and add their entry to the action's own transaction, while this one only ever
/// reads.
/// <para>
/// The two callers differ only in who may ask and how the channel filter is set — the
/// channel-scoped route takes it from the route value and never from the query string, so a
/// caller authorized for one channel cannot read another's log through it.
/// </para>
/// </summary>
public interface IAuditLogQueryService
{
    /// <summary>
    /// Newest first. <paramref name="page"/> is 1-based; both arguments are expected to be
    /// pre-validated by the endpoint, same as the other paged query services.
    /// <paramref name="filter"/> narrows the result; the page count reflects the filtered set.
    /// </summary>
    Task<PagedResult<AuditLogEntryDto>> ListAsync(int page, int pageSize, AuditLogFilter? filter = null, CancellationToken cancellationToken = default);
}
