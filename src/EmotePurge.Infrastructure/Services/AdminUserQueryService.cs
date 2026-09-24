using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.Services;

public class AdminUserQueryService(AppDbContext db) : IAdminUserQueryService
{
    public async Task<PagedResult<AdminUserDto>> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var totalCount = await db.Users.CountAsync(cancellationToken);

        var items = await db.Users
            .AsNoTracking()
            .OrderByDescending(u => u.LastLogin)
            // Id as the tiebreaker: LastLogin has second precision at best, and without a total
            // order Skip/Take may repeat or drop a row across pages (same reasoning as the audit log).
            .ThenBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            // HasRefreshToken is derived inside the projection on purpose: the encrypted token
            // columns are reduced to a boolean in SQL and never materialize in this process.
            .Select(u => new AdminUserDto(
                u.Id,
                u.TwitchUsername,
                u.DisplayName,
                u.LastLogin,
                u.SessionsValidFromUtc,
                u.LastSeenAtUtc,
                u.TwitchRefreshToken != null,
                u.TwitchAccessTokenExpiresAtUtc,
                u.TwitchTokenScopes))
            .ToListAsync(cancellationToken);

        return new PagedResult<AdminUserDto>(items, page, pageSize, totalCount);
    }
}
