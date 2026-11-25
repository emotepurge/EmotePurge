using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// <see cref="IBotAccountAvailability"/> (plan #487 2.3): the own process's <c>BotAccount:Enabled</c>, then one fresh
/// read of the credential row. Scoped like every reader on <see cref="Persistence.AppDbContext"/>. A missing row is
/// "no token", so not available; <c>PausedUntilUtc</c> is deliberately not part of the answer.
/// </summary>
public sealed class BotAccountAvailability(
    Persistence.AppDbContext db,
    BotAccountOptions options,
    TimeProvider timeProvider) : IBotAccountAvailability
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.BotAccountCredentials.AsNoTracking()
            .AnyAsync(
                c => c.Id == BotAccountCredential.SingletonId
                    && c.Status == BotAccountStatus.Valid
                    && c.ExpiresAtUtc != null
                    && c.ExpiresAtUtc > now
                    && !c.KillSwitch,
                cancellationToken);
    }
}
