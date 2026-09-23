using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class UserService(AppDbContext db, ITokenCipher tokenCipher, IModRoleCache modRoleCache) : IUserService
{
    // Throttle window for the LastSeenAtUtc stamp in CheckSessionAsync — see that method's
    // contract comment on IUserService for why 24 hours keeps this a cheap per-request side effect.
    private static readonly TimeSpan LastSeenStampThrottle = TimeSpan.FromHours(24);

    public async Task<User> UpsertLoginAsync(string twitchUserId, string twitchUsername, string displayName, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            user = new User { Id = twitchUserId, TwitchUsername = twitchUsername, DisplayName = displayName };
            db.Users.Add(user);
        }
        else
        {
            user.TwitchUsername = twitchUsername;
            user.DisplayName = displayName;
            user.LastLogin = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<SessionCheckResult?> CheckSessionAsync(string twitchUserId, DateTime issuedAtUtc, CancellationToken cancellationToken = default)
    {
        // Runs on every authenticated request (see OnValidatePrincipal): a single primary-key
        // lookup, projected to both columns this needs so it stays an index-only read and nothing
        // gets tracked — the same projection that used to carry SessionsValidFromUtc alone now also
        // carries LastSeenAtUtc, so the throttle check below costs no extra roundtrip on its own.
        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == twitchUserId)
            .Select(u => new { u.SessionsValidFromUtc, u.LastSeenAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            // No row to stamp or read a cutoff from — the caller must reject the principal instead
            // of reading this as "never revoked" (see the interface comment).
            return null;
        }

        if (row.SessionsValidFromUtc is { } revokedBefore && issuedAtUtc < revokedBefore)
        {
            // The cookie predates the last revocation: reject without touching LastSeenAtUtc. A
            // client that keeps sending this cookie after being logged out elsewhere must not keep
            // moving the retention clock for an account it no longer has valid access to — that is
            // the whole reason this check runs before the throttle below, not after.
            return new SessionCheckResult(IsValid: false);
        }

        var now = DateTime.UtcNow;
        if (row.LastSeenAtUtc is null || row.LastSeenAtUtc < now - LastSeenStampThrottle)
        {
            // A single conditional UPDATE, not a load-modify-save: the WHERE clause is re-evaluated
            // against the current row when the statement runs, so it is safe against several
            // concurrent requests for the same user racing this. Whichever commits first moves
            // LastSeenAtUtc to "now" (no longer older than the throttle window), so every other
            // concurrent request's WHERE clause then matches zero rows instead of overwriting the
            // same value again — no lost update, no duplicate write. Not gated on user existence
            // separately: the row was just read to exist above, and a delete racing this either
            // commits first (this affects zero rows, harmless) or commits after (its own DELETE
            // then removes whatever this just wrote).
            await db.Users
                .Where(u => u.Id == twitchUserId && (u.LastSeenAtUtc == null || u.LastSeenAtUtc < now - LastSeenStampThrottle))
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.LastSeenAtUtc, now), cancellationToken);
        }

        return new SessionCheckResult(IsValid: true);
    }

    public async Task<bool> RevokeSessionsAsync(string twitchUserId, AuditActor? actor, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.SessionsValidFromUtc = DateTime.UtcNow;

        if (actor is not null)
        {
            // Same-transaction audit (see AuditLogWrites). The revoked user's login goes into the
            // details as a snapshot — TargetId alone is just a number to whoever reads the log later.
            db.AddAuditEntry(
                actor,
                AuditActions.UserRevokeSessions,
                targetType: "user",
                targetId: twitchUserId,
                details: new { login = user.TwitchUsername });
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int?> InvalidateRoleCacheAsync(string twitchUserId, AuditActor actor, CancellationToken cancellationToken = default)
    {
        // The audit entry below names this user (TargetId and the login detail) without changing the
        // user row in the same SaveChanges — nothing in the database would stop it from landing after
        // an account deletion had already pseudonymised every entry naming them. The FOR SHARE lock
        // held until the commit is that stop: an account deletion takes FOR UPDATE on the same row,
        // so either it waits for this entry and pseudonymises it along with the rest, or it commits
        // first and this lookup then finds no row and writes nothing (see IAccountDeletionService).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var user = await db.LockUserAsync(twitchUserId, UserRowLock.ForShare, cancellationToken);
        if (user is null)
        {
            // Unknown user means nothing could have been cached under this id (entries only exist
            // for users who logged in and triggered a role check) — and it keeps arbitrary route
            // input away from the Redis SCAN pattern. Also the answer when a deletion won the lock:
            // its own post-commit cleanup clears the same keys.
            return null;
        }

        var removedEntries = await modRoleCache.InvalidateUserAsync(twitchUserId, cancellationToken);

        // Audited after the deletion so the entry carries the real count. The Redis write and the
        // audit row cannot share a transaction anyway; if this save fails the cache is already
        // clean, which is the harmless direction (entries repopulate on the next check).
        db.AddAuditEntry(
            actor,
            AuditActions.UserInvalidateRoleCache,
            targetType: "user",
            targetId: twitchUserId,
            details: new { login = user.TwitchUsername, removedEntries });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return removedEntries;
    }

    public async Task StoreTwitchTokensAsync(string twitchUserId, string accessToken, DateTime accessTokenExpiresAtUtc, string refreshToken, string? scopes, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            // The login callback upserts the user before storing tokens, so this only happens if a
            // refresh races a user deletion — nothing sensible to attach the tokens to then.
            return;
        }

        user.TwitchRefreshToken = tokenCipher.Protect(refreshToken);
        user.TwitchAccessToken = tokenCipher.Protect(accessToken);
        user.TwitchAccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
        user.TwitchTokenScopes = scopes;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<TwitchStoredTokens?> GetTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var row = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == twitchUserId)
            .Select(u => new { u.TwitchRefreshToken, u.TwitchAccessToken, u.TwitchAccessTokenExpiresAtUtc, u.TwitchTokenScopes })
            .SingleOrDefaultAsync(cancellationToken);

        if (row?.TwitchRefreshToken is null)
        {
            return null;
        }

        var refreshToken = tokenCipher.Unprotect(row.TwitchRefreshToken);
        if (refreshToken is null)
        {
            // Undecryptable (rotated key, tampered row) — same consequence as no stored token.
            return null;
        }

        var accessToken = row.TwitchAccessToken is null ? null : tokenCipher.Unprotect(row.TwitchAccessToken);
        return new TwitchStoredTokens(
            refreshToken,
            accessToken,
            accessToken is null ? null : row.TwitchAccessTokenExpiresAtUtc,
            row.TwitchTokenScopes);
    }

    public async Task ClearTwitchTokensAsync(string twitchUserId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            return;
        }

        user.TwitchRefreshToken = null;
        user.TwitchAccessToken = null;
        user.TwitchAccessTokenExpiresAtUtc = null;
        user.TwitchTokenScopes = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}
