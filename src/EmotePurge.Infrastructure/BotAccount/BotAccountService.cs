using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// <see cref="IBotAccountService"/> on the one credential row (plan #487 2.2, 2.10). Registered by the Api's entry point
/// <see cref="BotAccountRegistration.AddBotAccountTokenSealing"/> next to the sealer it needs, so the worker never gets
/// an admin writer.
/// <para>
/// Every write is one transaction: take the row lock (<c>SELECT … FOR UPDATE</c>; creating writes insert the row first
/// with <c>ON CONFLICT DO NOTHING</c>, so two first writes meet at the key), change the row, stage the audit entry,
/// commit, then publish. Two token writes at once therefore run one after the other and each raises the versions by
/// one. The plaintext token lives only inside <see cref="SetTokenAsync"/> and is never logged; no log line here names
/// it, the seal or an exception message of the sealer.
/// </para>
/// </summary>
public sealed class BotAccountService(
    AppDbContext db,
    IBotAccountTokenSealer sealer,
    BotAccountOptions options,
    IRedisPublisher redisPublisher,
    TimeProvider timeProvider,
    ILogger<BotAccountService> logger) : IBotAccountService
{
    private const string TargetType = "botAccount";

    public async Task<BotAccountStateDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.BotAccountCredentials.AsNoTracking()
            .Where(c => c.Id == BotAccountCredential.SingletonId)
            .Select(c => new
            {
                TokenPresent = c.SealedToken != null,
                c.Status,
                c.ExpiresAtUtc,
                c.BotSevenTvUserId,
                c.LastVerifiedAtUtc,
                c.LastErrorCode,
                c.KillSwitch,
                c.PausedUntilUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new BotAccountStateDto(false, BotAccountStatus.None, null, null, null, null, null, false, null);
        }

        return new BotAccountStateDto(
            row.TokenPresent,
            row.Status,
            row.ExpiresAtUtc,
            DaysUntil(row.ExpiresAtUtc, UtcNow()),
            row.BotSevenTvUserId,
            row.LastVerifiedAtUtc,
            row.LastErrorCode,
            row.KillSwitch,
            row.PausedUntilUtc);
    }

    public async Task<BotAccountTokenSetOutcome> SetTokenAsync(
        string? token, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        // The route answers 404 before it gets here; checked again so no caller can reach the refusing sealer.
        if (!options.Enabled)
        {
            return BotAccountTokenSetOutcome.Disabled;
        }

        if (BotAccountJwt.TryReadExpiry(token) is not { } expiresAt)
        {
            return BotAccountTokenSetOutcome.Malformed;
        }

        var now = UtcNow();
        if (expiresAt <= now)
        {
            return BotAccountTokenSetOutcome.Expired;
        }

        // Sealed before the transaction: RSA is the slow part, and nothing about it depends on the row.
        var sealedToken = sealer.Seal(token!);
        expiresAt = ToDatabasePrecision(expiresAt);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var row = await LockRowAsync(createIfMissing: true, cancellationToken)
                ?? throw new InvalidOperationException("The bot account row is missing right after its upsert.");

            row.SealedToken = sealedToken;
            row.ExpiresAtUtc = expiresAt;
            row.SetAtUtc = now;
            row.Status = BotAccountStatus.Unverified;
            row.BotSevenTvUserId = null;
            row.LastVerifiedAtUtc = null;
            row.LastErrorCode = null;
            row.TokenKeyFingerprint = sealer.KeyFingerprint;
            row.CredentialVersion++;
            row.IdentityVersion++;

            db.AddAuditEntry(actor, AuditActions.BotAccountTokenSet, targetType: TargetType, details: new { expiresAtUtc = expiresAt });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await NudgeWorkerAsync(cancellationToken);
        return BotAccountTokenSetOutcome.Set;
    }

    public async Task<bool> DeleteTokenAsync(AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // No row, or a row without a token (a kill switch set before any token): nothing to delete, nothing to
            // record, and no version moves, so a repeated delete is a no-op.
            var row = await LockRowAsync(createIfMissing: false, cancellationToken);
            if (row?.SealedToken is null)
            {
                return false;
            }

            row.SealedToken = null;
            row.ExpiresAtUtc = null;
            row.SetAtUtc = null;
            row.Status = BotAccountStatus.None;
            row.BotSevenTvUserId = null;
            row.LastVerifiedAtUtc = null;
            row.LastErrorCode = null;
            row.TokenKeyFingerprint = null;
            row.CredentialVersion++;
            row.IdentityVersion++;

            db.AddAuditEntry(actor, AuditActions.BotAccountTokenDelete, targetType: TargetType);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await NudgeWorkerAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetKillSwitchAsync(bool enabled, AuditActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var row = await LockRowAsync(createIfMissing: true, cancellationToken)
                ?? throw new InvalidOperationException("The bot account row is missing right after its upsert.");
            if (row.KillSwitch == enabled)
            {
                // The upsert may have created the row (all defaults); that is the state a missing row means anyway.
                await transaction.CommitAsync(cancellationToken);
                return false;
            }

            row.KillSwitch = enabled;
            row.KillSwitchVersion++;

            db.AddAuditEntry(actor, AuditActions.BotAccountKillSwitch, targetType: TargetType, details: new { enabled });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // The worker reads the switch in every send transaction anyway; the nudge only lets its fallback run start now.
        await NudgeWorkerAsync(cancellationToken);
        return true;
    }

    // Materialised with ToListAsync and nothing composed on top, so the locking clause stays on the outermost SELECT.
    private async Task<BotAccountCredential?> LockRowAsync(bool createIfMissing, CancellationToken cancellationToken)
    {
        if (createIfMissing)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""INSERT INTO "BotAccountCredentials" ("Id") VALUES ({BotAccountCredential.SingletonId}) ON CONFLICT ("Id") DO NOTHING""",
                cancellationToken);
        }

        var rows = await db.BotAccountCredentials
            .FromSql($"""SELECT * FROM "BotAccountCredentials" WHERE "Id" = {BotAccountCredential.SingletonId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    // Committed before announced. The nudge is an accelerator only: a Redis outage must not turn a stored write into an
    // error, the worker's poll converges.
    private async Task NudgeWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await redisPublisher.PublishAsync(BotCommands.Channel, BotCommands.BotAccountPrefix, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Bot account: publishing the worker nudge failed ({ExceptionType}); the row is committed and the worker's poll converges.",
                ex.GetType().Name);
        }
    }

    private DateTime UtcNow() => ToDatabasePrecision(timeProvider.GetUtcNow().UtcDateTime);

    private static int? DaysUntil(DateTime? expiresAt, DateTime now) =>
        expiresAt is { } at ? Math.Max(0, (int)Math.Floor((at - now).TotalDays)) : null;

    // Postgres keeps microseconds; trimmed so what is written reads back equal.
    private static DateTime ToDatabasePrecision(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }
}
