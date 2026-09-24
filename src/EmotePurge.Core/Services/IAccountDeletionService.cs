namespace EmotePurge.Core.Services;

/// <summary>Why an account is being deleted. Recorded (as camelCase text) in the <c>user.delete</c> audit entry.</summary>
public enum AccountDeletionReason
{
    /// <summary>An admin deleted the account on request (e.g. an e-mailed deletion request).</summary>
    AdminRequest,

    /// <summary>The retention job deleted the account after twelve months without activity.</summary>
    Inactivity
}

/// <summary>What <see cref="IAccountDeletionService.DeleteAsync"/> did.</summary>
public enum AccountDeletionOutcome
{
    /// <summary>The account was deleted and the transaction committed.</summary>
    Deleted,

    /// <summary>No user row with this id — nothing was written, not even an audit entry.</summary>
    NotFound,

    /// <summary>
    /// Only for <see cref="AccountDeletionReason.Inactivity"/>: the recheck under the row lock found
    /// activity at or after the cutoff (a login or a <c>LastSeenAtUtc</c> stamp that landed between
    /// the job's candidate selection and this call). Nothing was written.
    /// </summary>
    StillActive
}

/// <summary>
/// Outcome plus the counts of what the deletion touched. All counts are zero unless the outcome is
/// <see cref="AccountDeletionOutcome.Deleted"/>.
/// </summary>
/// <param name="VotesDeleted">Every vote of the user, across all sessions.</param>
/// <param name="VotesInOpenSessionsDeleted">
/// The part of <paramref name="VotesDeleted"/> that sat in still-running sessions — those scores
/// visibly change; the rest belonged to ended sessions.
/// </param>
/// <param name="AuditEntriesPseudonymisedAsActor">Entries whose actor was the user.</param>
/// <param name="AuditEntriesPseudonymisedAsTarget">Entries with <c>TargetType = "user"</c> pointing at the user.</param>
/// <param name="AuditEntriesPseudonymised">
/// Distinct entries touched — an entry where the user was actor <em>and</em> target (an admin
/// revoking their own sessions) counts once here and once in each of the two sets above.
/// </param>
public record AccountDeletionResult(
    AccountDeletionOutcome Outcome,
    int VotesDeleted = 0,
    int VotesInOpenSessionsDeleted = 0,
    int AuditEntriesPseudonymisedAsActor = 0,
    int AuditEntriesPseudonymisedAsTarget = 0,
    int AuditEntriesPseudonymised = 0);

/// <summary>
/// The one path that deletes a user account — called by the admin endpoint (on request) and by the
/// retention job (after twelve months of inactivity). Everything happens in one transaction under a
/// <c>FOR UPDATE</c> lock on the user row: the user's votes are deleted (<c>Vote → User</c> is
/// <c>Restrict</c>), audit entries naming the user are pseudonymised to
/// <see cref="AuditActor.DeletedUser"/> instead of deleted, the row goes (its encrypted Twitch
/// tokens with it), and a <c>user.delete</c> entry is written that carries no identity of the deleted
/// user. After the commit, the Redis role-cache keys and the rate-limit telemetry slot of the user are
/// cleared, with one retry and otherwise bounded by their TTLs.
/// </summary>
/// <remarks>
/// A second call for the same id returns <see cref="AccountDeletionOutcome.NotFound"/>; concurrent
/// calls serialise on the row lock and the later one finds no row. A re-login after deletion creates
/// a fresh row with the same Twitch id — the pseudonymised entries do not carry that id any more, so
/// they cannot be linked back to it.
/// </remarks>
public interface IAccountDeletionService
{
    /// <summary>
    /// Deletes the account <paramref name="twitchUserId"/>.
    /// </summary>
    /// <param name="actor">
    /// Who triggers the deletion: the admin from the principal, or <see cref="AuditActor.System"/> from
    /// the retention job. When the actor is the deleted user themself, the <c>user.delete</c> entry is
    /// written with <see cref="AuditActor.DeletedUser"/> as its actor instead — otherwise that one entry
    /// would restore the identity the same transaction just removed everywhere else.
    /// </param>
    /// <param name="onlyIfInactiveBeforeUtc">
    /// Required for <see cref="AccountDeletionReason.Inactivity"/> and rechecked under the row lock
    /// (<c>max(LastLogin, LastSeenAtUtc) &lt; cutoff</c>, otherwise
    /// <see cref="AccountDeletionOutcome.StillActive"/>); must be <c>null</c> for
    /// <see cref="AccountDeletionReason.AdminRequest"/>. A mismatch throws
    /// <see cref="ArgumentException"/>.
    /// </param>
    Task<AccountDeletionResult> DeleteAsync(
        string twitchUserId,
        AuditActor actor,
        AccountDeletionReason reason,
        DateTime? onlyIfInactiveBeforeUtc,
        CancellationToken cancellationToken = default);
}
