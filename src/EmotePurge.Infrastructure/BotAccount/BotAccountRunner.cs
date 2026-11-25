using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EmotePurge.Infrastructure.BotAccount;

/// <summary>
/// <see cref="IBotAccountRunner"/> (plan #487 2.5, 2.5a (g), 2.8): the advisory lock on a dedicated connection, the
/// runner epoch, the token upkeep and the pass around <see cref="IWishBotPass"/>. Registered scoped by the worker's
/// <see cref="BotAccountRegistration.AddBotAccountWriter"/> and resolved once from a scope that lives as long as the
/// loop (the pattern of the chat-log backfill, D46): the instance holds the lock connection and is not thread-safe.
/// <para>
/// <b>Lock and epoch.</b> The lock is taken with <c>pg_try_advisory_lock</c> on an unpooled connection of its own and is
/// never released explicitly; it dies with that connection. Right after every new acquisition the runner raises
/// <c>RunnerEpoch</c> <i>on that same connection</i>, so the epoch only ever moves while the lock is held: a statement on
/// a terminated backend cannot commit. Within a pass the lock is probed (never retaken) before and after each 7TV call,
/// never while a row lock is held (Revision 6f K6f-1; the probe waits at most 2 s), and every booking fences on the epoch
/// (<see cref="BotAccountFence"/>) under a <see cref="BotAccountLockDeadline"/> (Revision 6e K6e-3).
/// </para>
/// <para>
/// <b>Pauses</b> (plan 2.5a (i); Revisions 6c K6c-3, 6d K6d-6, 6e K6e-6, 6f K6f-4). A 429 seen in a pass (the
/// verification's, or any <see cref="IBotAccountSession.PauseAsync"/>) makes the token unusable for the rest of that pass.
/// <c>PauseAsync</c> joins a transaction open on the shared context (a send transaction) instead of opening its own. The
/// session remembers the longest pause reported in the pass as an absolute instant with its <c>IdentityVersion</c>, and at
/// the end of every pass, also after an exception of the wish part, the runner books it once more in a short fenced
/// transaction of its own, only where it lengthens the stored pause: after a rollback of the joined transaction the pause
/// is then not lost.
/// </para>
/// <para>
/// <b>Token upkeep</b> (plan 2.8). Only <c>unverified</c> and <c>valid</c> are upkept. <c>exp</c> reached →
/// <c>expired</c>; a seal that does not open → <c>invalid</c> (<c>seal_unreadable</c>); a due verification calls
/// <see cref="ISevenTvWriteClient.VerifyAsync"/> unless the kill switch or a 429 pause holds it. A verification is
/// outstanding after every acquisition of the lock and after a reported <c>Unauthorized</c> (Revision 6c K6c-5); until one
/// is booked the session's <see cref="IBotAccountSession.TokenUsable"/> stays false, across passes. Every result is booked
/// conditionally on the <c>CredentialVersion</c> of the token that was checked, so a late answer for token A never
/// touches a token B set meanwhile; a 429 pause is booked on the <c>IdentityVersion</c> instead. Audit
/// <c>botAccount.tokenState</c> only when the update hit, in the same commit.
/// </para>
/// <para>
/// <b>Logs</b> name outcomes, versions, the epoch and the expiry. Never the token, the seal, a header or a 7TV body.
/// </para>
/// </summary>
public sealed class BotAccountRunner(
    AppDbContext db,
    IBotAccountTokenOpener opener,
    ISevenTvWriteClient writeClient,
    IWishBotPass wishPass,
    BotAccountOptions options,
    TimeProvider timeProvider,
    ILogger<BotAccountRunner> logger) : IBotAccountRunner, IDisposable
{
    /// <summary>Actions per pass before the rest waits for the next pass (plan 2.5).</summary>
    public const int ActionBudgetPerPass = 50;

    // pg_try_advisory_lock(hashtext(...)) key of the bot account's single loop (plan 2.5).
    private const string LockKey = "emotepurge:bot-account";

    // The lock probe's command timeout (Revision 6f K6f-1: at most 2 s; running out counts as a lost lock).
    private const int ProbeTimeoutSeconds = 2;

    private NpgsqlConnection? lockConnection;
    private long runnerEpoch;
    private OpenedToken? opened;
    private bool reverifyRequested;
    private bool lockLostReported;

    // Per pass: a 429 was seen (Revision 6e K6e-6), and the longest pause reported (Revision 6f K6f-4).
    private bool rateLimitedThisPass;
    private ReportedPause? reportedPause;
    private DateOnly? lastWarnedDayUtc;
    private DateTime? lastWriteAtUtc;

    // The last failed verification outcome that was logged; repeats stay quiet until a verification succeeds.
    private SevenTvWriteOutcome? lastLoggedVerifyFailure;

    public async Task<BotAccountPassResult> RunPassAsync(CancellationToken cancellationToken = default)
    {
        // A loop-long context: whatever a failed pass left staged must not ride along into this one.
        db.ChangeTracker.Clear();
        lockLostReported = false;
        rateLimitedThisPass = false;
        reportedPause = null;

        await RunDeadlineStepAsync(cancellationToken);

        if (!await EnsureLockAsync(cancellationToken))
        {
            return BotAccountPassResult.NoLock;
        }

        var upkeep = await UpkeepTokenAsync(cancellationToken);
        if (upkeep is null || lockLostReported)
        {
            await LoseLockAsync();
            return BotAccountPassResult.LockLost;
        }

        var session = new Session(this, upkeep.Value.Account, upkeep.Value.Token);
        var completed = false;
        try
        {
            await wishPass.RunAsync(session, cancellationToken);
            completed = true;
        }
        finally
        {
            db.ChangeTracker.Clear();
            await RebookReportedPauseAsync(rethrowing: !completed, cancellationToken);
        }

        if (lockLostReported)
        {
            await LoseLockAsync();
            return BotAccountPassResult.LockLost;
        }

        return BotAccountPassResult.Completed;
    }

    // Both disposals: a DI scope disposed synchronously throws for a service that is only IAsyncDisposable, and an
    // advisory lock left on an open connection would outlive its loop.
    public void Dispose()
    {
        opened = null;
        lockConnection?.Dispose();
        lockConnection = null;
    }

    public async ValueTask DisposeAsync()
    {
        opened = null;
        if (lockConnection is not null)
        {
            await lockConnection.DisposeAsync();
            lockConnection = null;
        }
    }

    // ---------------------------------------------------------------- Step 0

    // Local and independent of the lock: a failure here must not hold the rest of the pass.
    private async Task RunDeadlineStepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await wishPass.RunDeadlineStepAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Bot account: the wish deadline step failed ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    // ---------------------------------------------------------------- Lock and epoch

    private async Task<bool> EnsureLockAsync(CancellationToken cancellationToken)
    {
        if (lockConnection is not null)
        {
            if (await ProbeLockAsync(cancellationToken))
            {
                return true;
            }

            logger.LogWarning("Bot account: the loop lost its lock (runner epoch {Epoch}); trying to take it again.", runnerEpoch);
            await LoseLockAsync();
        }

        // Unpooled: a pooled connection would go back to the pool instead of closing, and the lock would outlive the
        // loop's intent.
        var connectionString = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
        {
            Pooling = false,
        }.ConnectionString;
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using (var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtext(@key))", connection))
            {
                acquire.Parameters.AddWithValue("key", LockKey);
                if (await acquire.ExecuteScalarAsync(cancellationToken) is not true)
                {
                    await connection.DisposeAsync();
                    return false;
                }
            }

            // On the lock's own connection: the raise can only commit while this backend, and with it the lock, is alive.
            // It waits for a predecessor's send transaction that holds the row FOR SHARE (plan 2.5a (g)). The upsert
            // creates the row with its defaults (status none) when there is none yet.
            await using (var raise = new NpgsqlCommand(
                """
                INSERT INTO "BotAccountCredentials" ("Id", "RunnerEpoch") VALUES (@id, 1)
                ON CONFLICT ("Id") DO UPDATE SET "RunnerEpoch" = "BotAccountCredentials"."RunnerEpoch" + 1
                RETURNING "RunnerEpoch"
                """,
                connection))
            {
                raise.Parameters.AddWithValue("id", BotAccountCredential.SingletonId);
                runnerEpoch = (long)(await raise.ExecuteScalarAsync(cancellationToken))!;
            }

            lockConnection = connection;

            // A new lock holder uses the token only after a verification of its own (Revision 6c K6c-5): an Unauthorized a
            // predecessor saw lives only in that predecessor's memory, so every acquisition asks for one.
            reverifyRequested = true;
            logger.LogInformation("Bot account loop holds the lock (runner epoch {Epoch}).", runnerEpoch);
            return true;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<bool> ProbeLockAsync(CancellationToken cancellationToken)
    {
        if (lockConnection is null)
        {
            return false;
        }

        try
        {
            // A session lock lives exactly as long as its connection; asking pg_locks on that very connection proves both
            // at once (same probe as the chat-log backfill's loop lock).
            await using var probe = new NpgsqlCommand(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted AND objsubid = 1
                      AND ((classid::bigint << 32) | objid::bigint) = hashtext(@key)::bigint)
                """,
                lockConnection);
            probe.Parameters.AddWithValue("key", LockKey);
            probe.CommandTimeout = ProbeTimeoutSeconds;
            if (await probe.ExecuteScalarAsync(cancellationToken) is true)
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Dead connection or a probe over its time limit: the lock counts as lost (Revision 6f K6f-1).
        }

        // Never retaken here: a lost lock stays lost until the next pass takes it anew (with a new epoch).
        await LoseLockAsync();
        return false;
    }

    private async Task LoseLockAsync()
    {
        if (lockConnection is not null)
        {
            await lockConnection.DisposeAsync();
            lockConnection = null;
        }
    }

    // ---------------------------------------------------------------- Token upkeep (plan 2.8)

    // Null: the lock or the epoch was lost; nothing was booked after that.
    private async Task<(BotAccountSnapshot Account, string? Token)?> UpkeepTokenAsync(CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(cancellationToken);
        if (row is null || row.SealedToken is null || !BotAccountTokenUpkeepPolicy.IsUpkept(row.Status))
        {
            // none, invalid, expired: nothing to open, nothing to verify; the plaintext of an older seal goes.
            opened = null;
            return (Snapshot(row), null);
        }

        var now = UtcNow();
        if (BotAccountTokenUpkeepPolicy.HasExpired(row.ExpiresAtUtc, now))
        {
            opened = null;
            return await BookStateAsync(row.CredentialVersion, BotAccountStatus.Expired, errorCode: null, now, cancellationToken)
                ? await CurrentAsync(cancellationToken)
                : null;
        }

        WarnAboutExpiryOncePerDay(row.ExpiresAtUtc, now);

        if (opened?.CredentialVersion != row.CredentialVersion)
        {
            // A new seal (token set, renewal swap): open it anew. Opening is local; a null is any seal this key cannot open.
            var token = opener.TryOpen(row.SealedToken);
            if (token is null)
            {
                opened = null;
                return await BookStateAsync(row.CredentialVersion, BotAccountStatus.Invalid, BotAccountErrorCodes.SealUnreadable, now, cancellationToken)
                    ? await CurrentAsync(cancellationToken)
                    : null;
            }

            opened = new OpenedToken(token, row.CredentialVersion, row.IdentityVersion);
        }

        var due = BotAccountTokenUpkeepPolicy.IsVerificationDue(
            row.Status, row.LastVerifiedAtUtc, row.LastVerifiedCredentialVersion, row.CredentialVersion, now, reverifyRequested);
        if (!due || !BotAccountTokenUpkeepPolicy.MayCallSevenTv(row.KillSwitch, row.PausedUntilUtc, now))
        {
            return await CurrentAsync(cancellationToken);
        }

        return await VerifyAsync(opened, cancellationToken) ? await CurrentAsync(cancellationToken) : null;
    }

    // False: the lock or the epoch was lost around the call.
    private async Task<bool> VerifyAsync(OpenedToken token, CancellationToken cancellationToken)
    {
        if (!await ProbeLockAsync(cancellationToken))
        {
            return false;
        }

        var result = await writeClient.VerifyAsync(token.Value, cancellationToken);

        // After every 7TV call, before any booking: a loop that lost its lock meanwhile books nothing from this answer.
        if (!await ProbeLockAsync(cancellationToken))
        {
            return false;
        }

        var now = UtcNow();
        switch (result.Outcome)
        {
            case SevenTvWriteOutcome.Ok:
                return await BookVerifiedAsync(token.CredentialVersion, result.Value!, now, cancellationToken);

            case SevenTvWriteOutcome.Unauthorized:
                return await BookStateAsync(token.CredentialVersion, BotAccountStatus.Invalid, BotAccountErrorCodes.TokenUnauthorized, now, cancellationToken);

            case SevenTvWriteOutcome.RateLimited:
                var until = ReportRateLimit(token.IdentityVersion, result.Status.RetryAfter!.Value, now);
                var pause = await BookPauseAsync(token.IdentityVersion, until, extendOnly: false, cancellationToken);
                return pause != Booking.Fenced;

            default:
                // Timeout, Unavailable, or an answer users.me should never give: nothing is known, nothing is booked; the
                // next pass tries again. Logged once per distinct outcome.
                if (lastLoggedVerifyFailure != result.Outcome)
                {
                    lastLoggedVerifyFailure = result.Outcome;
                    logger.LogWarning(
                        "Bot account: the token verification (credential version {Version}) came to {Outcome}; retrying next pass.",
                        token.CredentialVersion, result.Outcome);
                }

                return true;
        }
    }

    private async Task<bool> BookVerifiedAsync(int credentialVersion, string botSevenTvUserId, DateTime now, CancellationToken cancellationToken)
    {
        var booking = await BookAsync(
            () => db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "BotAccountCredentials" SET
                     "Status" = {BotAccountStatus.Valid},
                     "BotSevenTvUserId" = {botSevenTvUserId},
                     "LastVerifiedAtUtc" = {now},
                     "LastVerifiedCredentialVersion" = {credentialVersion},
                     "LastErrorCode" = NULL
                 WHERE "Id" = {BotAccountCredential.SingletonId} AND "CredentialVersion" = {credentialVersion}
                   AND "Status" IN ({BotAccountStatus.Unverified}, {BotAccountStatus.Valid})
                 """,
                cancellationToken),
            onHit: null,
            cancellationToken);

        if (booking == Booking.Hit)
        {
            reverifyRequested = false;
            lastLoggedVerifyFailure = null;
            logger.LogInformation("Bot account: token verified (credential version {Version}).", credentialVersion);
        }
        else if (booking == Booking.Missed)
        {
            logger.LogInformation(
                "Bot account: verification of credential version {Version} discarded, the token changed meanwhile.", credentialVersion);
        }

        return booking != Booking.Fenced;
    }

    // invalid and expired, from unverified or valid only, on the version that was checked; one audit entry per change.
    private async Task<bool> BookStateAsync(
        int credentialVersion, string status, string? errorCode, DateTime now, CancellationToken cancellationToken)
    {
        var booking = await BookAsync(
            () => status == BotAccountStatus.Expired
                ? db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE "BotAccountCredentials" SET "Status" = {status}
                     WHERE "Id" = {BotAccountCredential.SingletonId} AND "CredentialVersion" = {credentialVersion}
                       AND "Status" IN ({BotAccountStatus.Unverified}, {BotAccountStatus.Valid})
                     """,
                    cancellationToken)
                : db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE "BotAccountCredentials" SET "Status" = {status}, "LastErrorCode" = {errorCode}
                     WHERE "Id" = {BotAccountCredential.SingletonId} AND "CredentialVersion" = {credentialVersion}
                       AND "Status" IN ({BotAccountStatus.Unverified}, {BotAccountStatus.Valid})
                     """,
                    cancellationToken),
            onHit: () => db.AddAuditEntry(
                AuditActor.System,
                AuditActions.BotAccountTokenState,
                targetType: "botAccount",
                details: errorCode is null ? new { status } : (object)new { status, errorCode },
                occurredAtUtc: now),
            cancellationToken);

        if (booking == Booking.Hit)
        {
            logger.LogWarning(
                "Bot account: token (credential version {Version}) is now {Status}{Reason}.",
                credentialVersion, status, errorCode is null ? string.Empty : $" ({errorCode})");
        }

        return booking != Booking.Fenced;
    }

    // An absolute instant (Revision 6f K6f-4), never shortening a pause that is already longer; extendOnly hits only where
    // it lengthens the stored one (the rebook at the end of a pass).
    private async Task<Booking> BookPauseAsync(int identityVersion, DateTime until, bool extendOnly, CancellationToken cancellationToken)
    {
        var booking = await BookAsync(
            () => UpdatePauseAsync(identityVersion, until, extendOnly, cancellationToken),
            onHit: null,
            cancellationToken);

        if (booking == Booking.Hit && !extendOnly)
        {
            logger.LogWarning("Bot account: 7TV answered 429; pausing until {PausedUntil:O}.", until);
        }

        return booking;
    }

    private Task<int> UpdatePauseAsync(int identityVersion, DateTime until, bool extendOnly, CancellationToken cancellationToken) =>
        extendOnly
            ? db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "BotAccountCredentials" SET "PausedUntilUtc" = {until}
                 WHERE "Id" = {BotAccountCredential.SingletonId} AND "IdentityVersion" = {identityVersion}
                   AND ("PausedUntilUtc" IS NULL OR "PausedUntilUtc" < {until})
                 """,
                cancellationToken)
            : db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE "BotAccountCredentials" SET "PausedUntilUtc" = GREATEST(COALESCE("PausedUntilUtc", {until}), {until})
                 WHERE "Id" = {BotAccountCredential.SingletonId} AND "IdentityVersion" = {identityVersion}
                 """,
                cancellationToken);

    // Remembers a 429 of this pass (Revision 6e K6e-6) and the longest pause as an absolute instant, computed once at the
    // report (Revision 6f K6f-4).
    private DateTime ReportRateLimit(int identityVersion, TimeSpan retryAfter, DateTime now)
    {
        rateLimitedThisPass = true;
        var until = ToDatabasePrecision(now + retryAfter);
        if (reportedPause is not { } known || known.IdentityVersion != identityVersion || known.Until < until)
        {
            reportedPause = new ReportedPause(until, identityVersion);
        }

        return until;
    }

    // The end of every pass (Revision 6f K6f-4): the longest pause reported is booked once more on the path without an open
    // transaction, fenced, under the lock deadline, only where it lengthens the stored one. After a commit with the joined
    // pause it hits nothing; after a rollback it secures the pause before the next pass. A failure while the wish part's
    // exception is on its way is logged, not thrown over it.
    private async Task RebookReportedPauseAsync(bool rethrowing, CancellationToken cancellationToken)
    {
        if (reportedPause is not { } pause)
        {
            return;
        }

        try
        {
            if (await BookPauseAsync(pause.IdentityVersion, pause.Until, extendOnly: true, cancellationToken) == Booking.Hit)
            {
                logger.LogWarning(
                    "Bot account: 7TV answered 429 and the pause until {PausedUntil:O} was not booked in its transaction; booked at the end of the pass.",
                    pause.Until);
            }
        }
        catch (Exception ex) when (rethrowing && ex is not OperationCanceledException)
        {
            logger.LogWarning("Bot account: booking the pause again at the end of the pass failed ({ExceptionType}).", ex.GetType().Name);
        }
    }

    // One booking transaction under a lock deadline (Revision 6e K6e-3): the epoch fence first (the first lock), then the
    // conditional update, then the staged audit, one commit. Never a probe in between (Revision 6f K6f-1). An expired
    // deadline, or any database error on the way, books nothing.
    private async Task<Booking> BookAsync(Func<Task<int>> update, Action? onHit, CancellationToken cancellationToken)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var deadline = new BotAccountLockDeadline(timeProvider);
            await deadline.ApplyAsync(db, cancellationToken);
            var fenced = await db.LockBotAccountForShareAsync(runnerEpoch, cancellationToken);
            deadline.Start();
            if (fenced is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                lockLostReported = true;
                logger.LogWarning("Bot account: runner epoch {Epoch} was superseded; this pass books nothing more.", runnerEpoch);
                return Booking.Fenced;
            }

            await deadline.ApplyAsync(db, cancellationToken);
            if (await update() == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Booking.Missed;
            }

            onHit?.Invoke();
            await deadline.ApplyAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await deadline.ApplyAsync(db, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Booking.Hit;
        }
        catch (Exception ex) when (BotAccountLockDeadline.IsExpiry(ex, cancellationToken))
        {
            logger.LogWarning("Bot account: a booking ran out of its lock deadline or failed ({ExceptionType}); nothing was booked.", ex.GetType().Name);
            return Booking.Expired;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private void WarnAboutExpiryOncePerDay(DateTime? expiresAtUtc, DateTime now)
    {
        if (!BotAccountTokenUpkeepPolicy.ShouldWarnAboutExpiry(expiresAtUtc, now, lastWarnedDayUtc))
        {
            return;
        }

        lastWarnedDayUtc = DateOnly.FromDateTime(now);
        logger.LogWarning(
            "Bot account: the token expires at {ExpiresAt:O}, in {Days} days; set a new one or check the renewal.",
            expiresAtUtc, (int)Math.Floor((expiresAtUtc!.Value - now).TotalDays));
    }

    private async Task<(BotAccountSnapshot Account, string? Token)> CurrentAsync(CancellationToken cancellationToken)
    {
        var row = await ReadRowAsync(cancellationToken);
        var account = Snapshot(row);
        var token = account.Status == BotAccountStatus.Valid && opened is { } current && current.CredentialVersion == account.CredentialVersion
            ? current.Value
            : null;
        return (account, token);
    }

    private Task<BotAccountCredential?> ReadRowAsync(CancellationToken cancellationToken) =>
        db.BotAccountCredentials.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == BotAccountCredential.SingletonId, cancellationToken);

    // ---------------------------------------------------------------- Session helpers

    // Only waits (Revision 6d K6d-8): the moment of a write is stamped by MarkWriteSent right before its HTTP call.
    private async Task WaitForWriteSlotAsync(CancellationToken cancellationToken)
    {
        if (lastWriteAtUtc is { } last)
        {
            var wait = last + TimeSpan.FromSeconds(options.WriteDelaySeconds) - timeProvider.GetUtcNow().UtcDateTime;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, timeProvider, cancellationToken);
            }
        }
    }

    private void MarkWriteSent() => lastWriteAtUtc = timeProvider.GetUtcNow().UtcDateTime;

    // Null: nothing booked. Joins a transaction open on the shared context (Revision 6c K6c-3): then one conditional update
    // under the caller's fence and deadline, committed by the caller, nothing saved or cleared here.
    private async Task<DateTime?> PauseAsync(TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        if (opened is null || retryAfter <= TimeSpan.Zero)
        {
            rateLimitedThisPass = true;
            return null;
        }

        var until = ReportRateLimit(opened.IdentityVersion, retryAfter, UtcNow());
        if (db.Database.CurrentTransaction is not null)
        {
            if (await UpdatePauseAsync(opened.IdentityVersion, until, extendOnly: false, cancellationToken) == 0)
            {
                return null;
            }

            logger.LogWarning("Bot account: 7TV answered 429; pausing until {PausedUntil:O}.", until);
            return until;
        }

        return await BookPauseAsync(opened.IdentityVersion, until, extendOnly: false, cancellationToken) == Booking.Hit ? until : null;
    }

    private bool HasOpenTransaction => db.Database.CurrentTransaction is not null;

    private DateTime UtcNow() => ToDatabasePrecision(timeProvider.GetUtcNow().UtcDateTime);

    private static BotAccountSnapshot Snapshot(BotAccountCredential? row) => row is null
        ? BotAccountSnapshot.Missing
        : new BotAccountSnapshot(
            row.Status,
            row.ExpiresAtUtc,
            row.BotSevenTvUserId,
            row.KillSwitch,
            row.PausedUntilUtc,
            row.CredentialVersion,
            row.IdentityVersion);

    // Postgres keeps microseconds; trimmed so what is written reads back equal.
    private static DateTime ToDatabasePrecision(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
    }

    private enum Booking
    {
        Hit,
        Missed,
        Fenced,

        // The lock deadline ran out, or the database failed on the way: nothing was booked (Revision 6e K6e-3).
        Expired,
    }

    private readonly record struct ReportedPause(DateTime Until, int IdentityVersion);

    // The plaintext of one seal and the versions it was opened under. No ToString that shows it.
    private sealed class OpenedToken(string value, int credentialVersion, int identityVersion)
    {
        public string Value { get; } = value;
        public int CredentialVersion { get; } = credentialVersion;
        public int IdentityVersion { get; } = identityVersion;

        public override string ToString() => $"token(credential version {CredentialVersion})";
    }

    private sealed class Session(BotAccountRunner runner, BotAccountSnapshot account, string? token) : IBotAccountSession
    {
        private int remainingActions = ActionBudgetPerPass;

        // A booked pause is carried in at once (Revision 6d K6d-6).
        public BotAccountSnapshot Account { get; private set; } = account;
        public string? Token { get; } = token;

        // Live, not a copy: ReportUnauthorized and any 429 of the pass turn it false within the pass.
        public bool TokenUsable => Token is not null && !runner.reverifyRequested && !runner.rateLimitedThisPass;

        public long RunnerEpoch { get; } = runner.runnerEpoch;
        public int RemainingActions => remainingActions;

        // Never under a row lock (Revision 6f K6f-1): every transaction of the pass holds row locks.
        public Task<bool> HoldsLockAsync(CancellationToken cancellationToken = default)
        {
            if (runner.HasOpenTransaction)
            {
                throw new InvalidOperationException("The lock probe never runs while a transaction with row locks is open.");
            }

            return runner.lockLostReported ? Task.FromResult(false) : runner.ProbeLockAsync(cancellationToken);
        }

        public bool TryTakeAction()
        {
            if (remainingActions <= 0)
            {
                return false;
            }

            remainingActions--;
            return true;
        }

        public Task WaitForWriteSlotAsync(CancellationToken cancellationToken = default) =>
            runner.WaitForWriteSlotAsync(cancellationToken);

        public void MarkWriteSent() => runner.MarkWriteSent();

        public void ReportUnauthorized() => runner.reverifyRequested = true;

        public void ReportLockLost() => runner.lockLostReported = true;

        public async Task<bool> PauseAsync(TimeSpan retryAfter, CancellationToken cancellationToken = default)
        {
            if (await runner.PauseAsync(retryAfter, cancellationToken) is not { } until)
            {
                return false;
            }

            if (Account.PausedUntilUtc is not { } paused || paused < until)
            {
                Account = Account with { PausedUntilUtc = until };
            }

            return true;
        }

        public override string ToString() => $"bot account session (epoch {RunnerEpoch}, {Account.Status})";
    }
}
