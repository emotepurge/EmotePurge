using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// <see cref="IBotAccountAvailability"/> (plan #487 2.3): the own process's <c>BotAccount:Enabled</c>, then one fresh
/// read of the credential row, judged by <see cref="BotAccountAvailabilityState.IsUsable"/> (the rule the expiry run's
/// single-statement read shares, Revision 6d K6d-7). Scoped like every reader on <see cref="Persistence.AppDbContext"/>. A
/// missing row is "no token", so not available; <c>PausedUntilUtc</c> is deliberately not part of the answer.
/// </summary>
public sealed class BotAccountAvailability(
    Persistence.AppDbContext db,
    BotAccountOptions options,
    TimeProvider timeProvider) : IBotAccountAvailability
{
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        (await GetStateAsync(cancellationToken)).IsAvailable;

    public async Task<BotAccountAvailabilityState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return BotAccountAvailabilityState.Unavailable;
        }

        // Only the five columns the answer needs: no seal, no renewal state.
        var row = await db.BotAccountCredentials.AsNoTracking()
            .Where(c => c.Id == BotAccountCredential.SingletonId)
            .Select(c => new { c.Status, c.ExpiresAtUtc, c.KillSwitch, c.BotSevenTvUserId, c.IdentityVersion })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return BotAccountAvailabilityState.Unavailable;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        return BotAccountAvailabilityState.Of(
            switchesOn: true, row.Status, row.ExpiresAtUtc, row.KillSwitch, row.BotSevenTvUserId, row.IdentityVersion, now);
    }
}
