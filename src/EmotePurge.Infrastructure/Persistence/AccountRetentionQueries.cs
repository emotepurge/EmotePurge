using System.Linq.Expressions;
using EmotePurge.Core.Entities;

namespace EmotePurge.Infrastructure.Persistence;

/// <summary>
/// The predicates that decide what an account deletion touches, in one place, because two services
/// must agree on them: <c>AccountDeletionService</c> deletes and rewrites with them, and the retention
/// job's dry run counts with them (<c>DataRetentionService</c>). A count formulated separately from the
/// delete it predicts is exactly how the two drift apart unnoticed.
/// </summary>
/// <remarks>
/// Set-shaped (<c>ids.Contains(…)</c>, sent as <c>= ANY(@ids)</c>), so the deletion passes its one id
/// and the dry run a whole batch through the very same expression.
/// </remarks>
internal static class AccountRetentionQueries
{
    /// <summary>The <c>TargetType</c> of an audit entry that points at a user by Twitch id.</summary>
    public const string UserTargetType = "user";

    /// <summary>
    /// "Last active before <paramref name="cutoffUtc"/>": <c>max(LastLogin, LastSeenAtUtc) &lt; cutoff</c>,
    /// a missing <c>LastSeenAtUtc</c> counting as absent. Written as the equivalent conjunction so it
    /// stays a plain, index-friendly comparison in SQL. Must agree with <see cref="LastActivityUtc"/>,
    /// the in-memory form the deletion rechecks under its row lock.
    /// </summary>
    public static Expression<Func<User, bool>> LastActiveBefore(DateTime cutoffUtc) =>
        u => u.LastLogin < cutoffUtc && (u.LastSeenAtUtc == null || u.LastSeenAtUtc < cutoffUtc);

    /// <summary>In-memory counterpart of <see cref="LastActiveBefore"/>: <c>max(LastLogin, LastSeenAtUtc)</c>.</summary>
    public static DateTime LastActivityUtc(User user) =>
        user.LastSeenAtUtc is { } lastSeen && lastSeen > user.LastLogin ? lastSeen : user.LastLogin;

    /// <summary>Every vote cast by one of the users, in any session.</summary>
    public static IQueryable<Vote> CastByAnyOf(this IQueryable<Vote> votes, IReadOnlyCollection<string> userIds) =>
        votes.Where(v => userIds.Contains(v.UserId));

    /// <summary>Audit entries in which one of the users is the actor.</summary>
    public static IQueryable<AuditLogEntry> ActedByAnyOf(this IQueryable<AuditLogEntry> entries, IReadOnlyCollection<string> userIds) =>
        entries.Where(e => userIds.Contains(e.ActorTwitchUserId));

    /// <summary>Audit entries with <c>TargetType = "user"</c> pointing at one of the users.</summary>
    public static IQueryable<AuditLogEntry> TargetingAnyOf(this IQueryable<AuditLogEntry> entries, IReadOnlyCollection<string> userIds) =>
        entries.Where(e => e.TargetType == UserTargetType && e.TargetId != null && userIds.Contains(e.TargetId));
}
