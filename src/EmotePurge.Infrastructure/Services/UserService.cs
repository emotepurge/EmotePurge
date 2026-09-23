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

    public async Task<SessionCheckResult?> CheckSessionAsync(string twitchUserId, CancellationToken cancellationToken = default)
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

        return new SessionCheckResult(row.SessionsValidFromUtc);
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
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == twitchUserId, cancellationToken);
        if (user is null)
        {
            // Unknown user means nothing could have been cached under this id (entries only exist
            // for users who logged in and triggered a role check) — and it keeps arbitrary route
            // input away from the Redis SCAN pattern.
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
