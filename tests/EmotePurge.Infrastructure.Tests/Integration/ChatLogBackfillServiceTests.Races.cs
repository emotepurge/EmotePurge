using System.Data.Common;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fixtures;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The enqueue's locks (spec D35, EPIC AC 25) and the block commit's channel lock, raced for real: two
// or three transactions on their own connections, ordered by what Postgres reports in pg_stat_activity
// (PostgresLockProbe), never by sleeps.
//
// Two ways to park the enqueue: before its transaction, inside the blocked 7TV read (AC 25 a–e: something
// commits meanwhile); or inside its transaction, holding the channel lock while it waits for the
// requester's user row, which a third transaction holds FOR UPDATE like a running account deletion
// (the lock-strength races: a contender then meets the enqueue's channel lock).
public partial class ChatLogBackfillServiceTests
{
    // ---------------------------------------------------------------- AC 25 a–e (blocked 7TV read)

    // (a) The requester's account is deleted while 7TV answers: 401-equivalent, nothing written.
    [Fact]
    public async Task Race_AccountDeletedDuringThePreview_IsRequesterGone_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfraceuser");
        await SeedUserAsync();
        var (entered, release) = BlockPreview();

        await using var db = CreateDbContext();
        var enqueue = CreateService(db).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor);
        await entered.Task;

        await using (var deletion = CreateDbContext())
        {
            var deleted = await new AccountDeletionService(
                    deletion, Substitute.For<IModRoleCache>(), Substitute.For<IRateLimitTelemetry>(), NullLogger<AccountDeletionService>.Instance)
                .DeleteAsync(Actor.TwitchUserId, AuditActor.System, AccountDeletionReason.AdminRequest, null);
            Assert.Equal(AccountDeletionOutcome.Deleted, deleted.Outcome);
        }

        var before = await StateFingerprintAsync();
        release.SetResult(PreviewOf(SetB, Member("n1", "BNew1", null)));

        Assert.Equal(ChatLogBackfillEnqueueStatus.RequesterGone, (await enqueue).Status);
        Assert.Equal(before, await StateFingerprintAsync());
    }

    // (b) A leave commits while 7TV answers: the lock-time re-check sees the inactive row.
    [Fact]
    public async Task Race_LeaveDuringThePreview_IsNotActive_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfraceleave");
        await SeedUserAsync();
        var (entered, release) = BlockPreview();

        await using var db = CreateDbContext();
        var enqueue = CreateService(db).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor);
        await entered.Task;

        await using (var leaveDb = CreateDbContext())
        {
            Assert.True(await CreateChannelService(leaveDb).LeaveAsync(channel.ChannelName, Actor));
        }

        var before = await StateFingerprintAsync();
        release.SetResult(PreviewOf(SetB, Member("n1", "BNew1", null)));

        Assert.Equal(ChatLogBackfillEnqueueStatus.NotActive, (await enqueue).Status);
        Assert.Equal(before, await StateFingerprintAsync());
    }

    // (c) Two enqueues for one channel, both past every pre-check: exactly one run.
    [Fact]
    public async Task Race_TwoEnqueuesForOneChannel_OneWins_TheOtherIsAlreadyActive()
    {
        var channel = await SeedChannelAsync("bfracetwo");
        await SeedUserAsync();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ForeignEmoteSetLookupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entries = 0;
        _foreign.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref entries) == 2)
                {
                    bothEntered.TrySetResult();
                }

                return release.Task;
            });

        await using var dbA = CreateDbContext();
        await using var dbB = CreateDbContext();
        var first = CreateService(dbA).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor);
        var second = CreateService(dbB).EnqueueAsync(channel.ChannelName, SetB, 3, Today, Actor);
        await bothEntered.Task;
        release.SetResult(PreviewOf(SetB, Member("n1", "BNew1", null), Member("n2", "BNew2", null)));

        var outcomes = (await Task.WhenAll(first, second)).Select(r => r.Status).OrderBy(s => s).ToArray();
        Assert.Equal([ChatLogBackfillEnqueueStatus.Enqueued, ChatLogBackfillEnqueueStatus.AlreadyActive], outcomes);

        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.ChatLogBackfillRuns.CountAsync());
        Assert.Equal(2, await verify.ChatLogBackfillRunEmotes.CountAsync());
        Assert.Equal(2, await verify.Emotes.CountAsync(e => e.ChannelId == channel.Id));
        Assert.Equal(1, await verify.AuditLogEntries.CountAsync());
    }

    // (d) The row is purged and the login joined again as a new row while 7TV answers: the enqueue locks
    // by the id it read first, finds nothing and writes nothing — not against the new row either.
    [Fact]
    public async Task Race_PurgeAndRejoinDuringThePreview_IsChannelGone_AndLeavesTheNewRowAlone()
    {
        var channel = await SeedChannelAsync("bfracepurge");
        await SeedUserAsync();
        var (entered, release) = BlockPreview();

        await using var db = CreateDbContext();
        var enqueue = CreateService(db).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor);
        await entered.Task;

        await using (var purge = CreateDbContext())
        {
            await purge.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        var rejoined = await SeedChannelAsync("bfracepurge", twitchChannelId: channel.TwitchChannelId);
        var before = await StateFingerprintAsync();
        release.SetResult(PreviewOf(SetB, Member("n1", "BNew1", null)));

        Assert.Equal(ChatLogBackfillEnqueueStatus.ChannelGone, (await enqueue).Status);
        Assert.Equal(before, await StateFingerprintAsync());
        await using var verify = CreateDbContext();
        Assert.False(await verify.Emotes.AnyAsync(e => e.ChannelId == rejoined.Id));
    }

    // (e) A login handover (rename/merge) gives the row another Twitch identity while 7TV answers.
    [Fact]
    public async Task Race_IdentityHandoverDuringThePreview_IsChannelIdentityChanged_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfraceidentity");
        await SeedUserAsync();
        var (entered, release) = BlockPreview();

        await using var db = CreateDbContext();
        var enqueue = CreateService(db).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor);
        await entered.Task;

        await using (var handover = CreateDbContext())
        {
            await handover.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.TwitchChannelId, "tw_someone_else"));
        }

        var before = await StateFingerprintAsync();
        release.SetResult(PreviewOf(SetB, Member("n1", "BNew1", null)));

        Assert.Equal(ChatLogBackfillEnqueueStatus.ChannelIdentityChanged, (await enqueue).Status);
        Assert.Equal(before, await StateFingerprintAsync());
    }

    // A rename during the 7TV read (same Twitch id, another login on the row): the request named the old
    // login, so the identity it was made for no longer holds. Mutation probe: without the name check
    // under the lock the enqueue stores a run for the renamed row.
    [Fact]
    public async Task Race_RenameDuringThePreview_IsChannelIdentityChanged_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfracerename");
        await SeedUserAsync();
        var (entered, release) = BlockPreview();

        await using var db = CreateDbContext();
        var enqueue = CreateService(db).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor);
        await entered.Task;

        await using (var rename = CreateDbContext())
        {
            await rename.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ChannelName, "bfracerenamed"));
        }

        var before = await StateFingerprintAsync();
        release.SetResult(PreviewOf(SetB, Member("n1", "BNew1", null)));

        Assert.Equal(ChatLogBackfillEnqueueStatus.ChannelIdentityChanged, (await enqueue).Status);
        Assert.Equal(before, await StateFingerprintAsync());
    }

    // AC 25a the other way round: the enqueue already holds the requester's row FOR SHARE when the
    // account deletion arrives. The deletion waits for the enqueue's commit and then pseudonymises the
    // run it finds — the requester snapshot is never left behind.
    [Fact]
    public async Task Race_AccountDeletionWhileTheEnqueueHoldsTheUser_WaitsAndPseudonymisesTheNewRun()
    {
        var channel = await SeedChannelAsync("bfraceuserlate");
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", null));

        var atUpsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var enqueueDb = CreateDbContext(new PauseBeforeCommand("INSERT INTO \"Emotes\"", atUpsert, proceed));
        var enqueue = Task.Run(() => CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
        await atUpsert.Task;

        await using var deletionDb = fixture.CreateTaggedDbContext("bfraceuserlate-delete", DatabaseName);
        var deletion = Task.Run(() => new AccountDeletionService(
                deletionDb, Substitute.For<IModRoleCache>(), Substitute.For<IRateLimitTelemetry>(), NullLogger<AccountDeletionService>.Instance)
            .DeleteAsync(Actor.TwitchUserId, AuditActor.System, AccountDeletionReason.AdminRequest, null));
        await fixture.WaitUntilBlockedOnLockAsync("bfraceuserlate-delete", deletion);

        proceed.SetResult();
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);
        Assert.Equal(AccountDeletionOutcome.Deleted, (await deletion).Outcome);

        await using var verify = CreateDbContext();
        var run = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal((AuditActor.DeletedUser.TwitchUserId, AuditActor.DeletedUser.Login), (run.RequestedByTwitchUserId, run.RequestedByLogin));
    }

    // ---------------------------------------------------------------- Lock strength (enqueue parked under its channel lock)

    // Leave ↔ enqueue serialize on the channel row (D35). The enqueue holds the row and waits for the
    // requester; the leave must wait for the enqueue, then cancel the run it committed.
    // Mutation probes: without the enqueue's channel lock, or without the leave's loader lock, the leave
    // cancels before the run exists and the run stays queued on an inactive channel.
    [Fact]
    public async Task LockRace_LeaveWhileTheEnqueueHoldsTheChannel_WaitsAndCancelsTheNewRun()
    {
        var channel = await SeedChannelAsync("bflockleave");
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", null));

        await using var holder = CreateDbContext();
        await using var holderTx = await HoldUserRowAsync(holder);

        await using var enqueueDb = fixture.CreateTaggedDbContext("bflockleave-enq", DatabaseName);
        var enqueue = Task.Run(() => CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("bflockleave-enq", enqueue);

        await using var leaveDb = fixture.CreateTaggedDbContext("bflockleave-leave", DatabaseName);
        var leave = Task.Run(() => CreateChannelService(leaveDb).LeaveAsync(channel.ChannelName, Actor));
        await WaitUntilBlockedOrDoneAsync("bflockleave-leave", leave);

        await holderTx.RollbackAsync();
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);
        Assert.True(await leave);

        await using var verify = CreateDbContext();
        Assert.False((await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id)).IsBotActive);
        var run = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal((ChatLogBackfillRunStatus.Cancelled, "channel_left"), (run.Status, run.ErrorCode));
    }

    // The enqueue's lock must not block foreign-key inserts on the channel. A 7TV dispatch pushes an emote
    // the chosen set also lists, while the enqueue holds the channel: the dispatch completes, and the
    // enqueue's ON CONFLICT DO NOTHING then adopts the dispatch's row instead of creating one.
    // Mutation probe: LockChannelByIdAsync with FOR UPDATE — the dispatch's FK check (KEY SHARE) waits for
    // the enqueue while the enqueue's insert waits for the dispatch's uncommitted key: 40P01.
    [Fact]
    public async Task LockRace_DispatchInsertingTheSameEmoteWhileTheEnqueueHoldsTheChannel_DoesNotDeadlock()
    {
        var channel = await SeedChannelAsync("bflockdispatch");
        await SeedUserAsync();
        await SeedEmoteAsync(channel, "a1", "A1");
        PreviewReturns(SetB, "Set B", Member("x1", "XInB", null), Member("n1", "BNew1", null));

        await using var holder = CreateDbContext();
        await using var holderTx = await HoldUserRowAsync(holder);

        await using var enqueueDb = fixture.CreateTaggedDbContext("bflockdispatch-enq", DatabaseName);
        var enqueue = Task.Run(() => CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("bflockdispatch-enq", enqueue);

        await using var dispatchDb = fixture.CreateTaggedDbContext("bflockdispatch-dispatch", DatabaseName);
        var dispatch = Task.Run(() => CreateSyncService(dispatchDb, Substitute.For<ISevenTvApiClient>())
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, ActiveSetId, new SevenTvEmoteSetDelta([Live("x1", "XLive")], [], [])));
        await WaitUntilBlockedOrDoneAsync("bflockdispatch-dispatch", dispatch);

        await holderTx.RollbackAsync();
        Assert.Equal(SevenTvDeltaOutcome.Applied, (await dispatch).Outcome);
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);

        await using var verify = CreateDbContext();
        var x1 = await verify.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "x1");
        Assert.Equal(("XLive", false, false), (x1.Name, x1.IsArchived, x1.IsPlaceholder));
        var snapshot = await verify.ChatLogBackfillRunEmotes.AsNoTracking().ToDictionaryAsync(e => e.SevenTvEmoteId);
        Assert.Equal((x1.Id, "XInB", false), (snapshot["x1"].EmoteId, snapshot["x1"].Name, snapshot["x1"].CreatedRow));
        Assert.True(snapshot["n1"].CreatedRow);
    }

    // A full REST resync of the active set while the enqueue holds the channel: the sync's channel UPDATE
    // (NO KEY UPDATE) waits for the enqueue — its emote inserts come after that UPDATE in the save, so it
    // holds no key the enqueue needs. The enqueue then creates the placeholder, the sync's insert of the
    // same key conflicts, and its retry (E10) adopts and un-archives the enqueue's row (AC 4 under a race).
    [Fact]
    public async Task LockRace_FullResyncWhileTheEnqueueHoldsTheChannel_AdoptsThePlaceholder()
    {
        var channel = await SeedChannelAsync("bflocksync");
        await SeedUserAsync();
        await SeedEmoteAsync(channel, "a1", "A1");
        PreviewReturns(SetB, "Set B", Member("x1", "XInB", null));

        await using var holder = CreateDbContext();
        await using var holderTx = await HoldUserRowAsync(holder);

        await using var enqueueDb = fixture.CreateTaggedDbContext("bflocksync-enq", DatabaseName);
        var enqueue = Task.Run(() => CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("bflocksync-enq", enqueue);

        await using var syncDb = fixture.CreateTaggedDbContext("bflocksync-sync", DatabaseName);
        var sync = Task.Run(() => CreateSyncService(syncDb, RestAnswering(channel, ActiveSetId, Live("a1", "A1"), Live("x1", "XLive")))
            .SyncChannelAsync(channel.ChannelName));
        await fixture.WaitUntilBlockedOnLockAsync("bflocksync-sync", sync);

        await holderTx.RollbackAsync();
        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);
        Assert.NotNull(await sync);

        await using var verify = CreateDbContext();
        var x1 = await verify.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "x1");
        Assert.Equal(("XLive", false, false), (x1.Name, x1.IsArchived, x1.IsPlaceholder));
        var snapshot = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
        Assert.Equal((x1.Id, true), (snapshot.EmoteId, snapshot.CreatedRow));
    }

    // Two writers taking two shared missing keys in opposite order: the enqueue's upsert inserts "a" and
    // waits for "z", which another transaction (standing in for a dispatch's EF insert, Guid-ordered)
    // holds and then inserts "a" as well. Postgres aborts the enqueue (the first waiter) with 40P01; the
    // enqueue retries its whole locked transaction, finds both keys committed and adopts them.
    // Mutation probe: without the retry the enqueue throws 40P01.
    [Fact]
    public async Task LockRace_OppositeKeyOrderDeadlock_IsRetried_AndTheEnqueueAdoptsTheOtherWritersRows()
    {
        var channel = await SeedChannelAsync("bflockdeadlock");
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("a", "AliasA", null), Member("z", "AliasZ", null));
        var logger = new RecordingLogger<ChatLogBackfillService>();

        await using var other = fixture.CreateTaggedDbContext("bflockdeadlock-other", DatabaseName);
        await using var otherTx = await other.Database.BeginTransactionAsync();
        other.Emotes.Add(new Emote { ChannelId = channel.Id, SevenTvEmoteId = "z", Name = "OtherZ", ImageUrl = ImageUrl("z") });
        await other.SaveChangesAsync();

        await using var enqueueDb = fixture.CreateTaggedDbContext("bflockdeadlock-enq", DatabaseName);
        var enqueue = Task.Run(() => CreateService(enqueueDb, logger: logger).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("bflockdeadlock-enq", enqueue);

        other.Emotes.Add(new Emote { ChannelId = channel.Id, SevenTvEmoteId = "a", Name = "OtherA", ImageUrl = ImageUrl("a") });
        await other.SaveChangesAsync();
        await otherTx.CommitAsync();

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, (await enqueue).Status);
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.ChatLogBackfillRuns.CountAsync());
        var snapshot = await verify.ChatLogBackfillRunEmotes.AsNoTracking().OrderBy(e => e.SevenTvEmoteId).ToListAsync();
        Assert.Equal([("a", false), ("z", false)], snapshot.Select(e => (e.SevenTvEmoteId, e.CreatedRow)).ToArray());
        Assert.Equal(["OtherA", "OtherZ"], await verify.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).OrderBy(e => e.SevenTvEmoteId).Select(e => e.Name).ToListAsync());
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("deadlock", StringComparison.Ordinal));
    }

    // Child 3 AC 1, the index path: a run inserted by a writer that bypasses the lock (here a plain insert
    // in another transaction) is invisible to the enqueue's re-check while uncommitted; the enqueue's own
    // insert then waits on the partial unique index and loses with AlreadyActive once it commits — and
    // the placeholders it created roll back with it.
    [Fact]
    public async Task LockRace_ARunCommittedPastTheRecheck_LosesOnThePartialUniqueIndex()
    {
        var channel = await SeedChannelAsync("bflockindex");
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", null));

        await using var holder = CreateDbContext();
        await using var holderTx = await HoldUserRowAsync(holder);

        await using var enqueueDb = fixture.CreateTaggedDbContext("bflockindex-enq", DatabaseName);
        var enqueue = Task.Run(() => CreateService(enqueueDb).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("bflockindex-enq", enqueue);

        await using var other = CreateDbContext();
        await using var otherTx = await other.Database.BeginTransactionAsync();
        other.ChatLogBackfillRuns.Add(new ChatLogBackfillRun
        {
            ChannelId = channel.Id,
            Status = ChatLogBackfillRunStatus.Queued,
            RequestedMonths = 1,
            WindowFrom = new DateOnly(2026, 8, 1),
            WindowTo = new DateOnly(2026, 9, 1),
            WeeksTotal = 5,
            EmoteSetId = SetC,
            ArchiveBaseUrl = ArchiveUrl,
            RequestedByTwitchUserId = "other",
            RequestedByLogin = "other",
            RequestedAtUtc = NowUtc,
        });
        await other.SaveChangesAsync();
        var otherPid = await other.Database.SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """).SingleAsync();

        // Released from the user row, the enqueue re-checks (the uncommitted run is invisible), inserts
        // its placeholders and then waits on the index entry of exactly that other transaction.
        await holderTx.RollbackAsync();
        await WaitUntilBlockedByAsync("bflockindex-enq", otherPid, enqueue);
        await otherTx.CommitAsync();

        Assert.Equal(ChatLogBackfillEnqueueStatus.AlreadyActive, (await enqueue).Status);
        await using var verify = CreateDbContext();
        Assert.Equal(SetC, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync()).EmoteSetId);
        Assert.False(await verify.Emotes.AnyAsync(e => e.ChannelId == channel.Id));
        Assert.False(await verify.AuditLogEntries.AnyAsync());
    }

    // The block commit takes the channel FOR KEY SHARE before touching usage rows. A purge (FOR UPDATE on
    // the channel, then the cascade over the emotes) arriving mid-commit therefore waits for the whole
    // block. Mutation probe: without that first statement the commit holds KEY SHARE on emote rows (its
    // usage insert) when the purge locks the channel and waits for them; the commit's coverage insert
    // then waits for the channel: 40P01.
    [Fact]
    public async Task LockRace_PurgeDuringABlockCommit_WaitsForTheCommit()
    {
        var channel = await SeedChannelAsync("bflockpurge", createdAt: new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc));
        var emote = await SeedEmoteAsync(channel, "e1", "E1");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 3);

        var atCoverage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var commitDb = CreateDbContext(new PauseBeforeCommand("\"ChatLogBackfillCoverage\"", atCoverage, proceed));
        var commit = Task.Run(() => CreateService(commitDb).ReplaceBlockAsync(
            run.Id, run.WindowFrom, run.WindowFrom.AddDays(7), [new ChatLogBackfillAggregate(emote.Id, run.WindowFrom, 3, 0, 0)], 10, 5, isLastBlock: false));
        await atCoverage.Task;

        await using var purgeDb = fixture.CreateTaggedDbContext("bflockpurge-purge", DatabaseName);
        var purge = Task.Run(async () =>
        {
            await using var tx = await purgeDb.Database.BeginTransactionAsync();
            await purgeDb.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Channels" WHERE "Id" = {channel.Id} FOR UPDATE""");
            await purgeDb.Database.ExecuteSqlAsync($"""DELETE FROM "Channels" WHERE "Id" = {channel.Id}""");
            await tx.CommitAsync();
        });
        await fixture.WaitUntilBlockedOnLockAsync("bflockpurge-purge", purge);

        proceed.SetResult();
        Assert.IsType<ChatLogBackfillBlockResult.Committed>(await commit);
        await purge;

        await using var verify = CreateDbContext();
        Assert.False(await verify.Channels.AnyAsync(c => c.Id == channel.Id));
        Assert.False(await verify.ChatLogBackfillCoverage.AnyAsync(d => d.ChannelId == channel.Id));
    }

    // ---------------------------------------------------------------- Helpers

    private ChannelService CreateChannelService(AppDbContext db) =>
        new(
            db,
            _publisher,
            Substitute.For<IChannelIdentityService>(),
            new ChannelEmoteSetObservationService(db),
            new BroadcasterChannelLockService(db),
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            _excluded,
            NullLogger<ChannelService>.Instance);

    // Holds the requester's user row FOR UPDATE, like a running account deletion: an enqueue that got
    // its channel lock parks on its FOR SHARE until this transaction ends.
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> HoldUserRowAsync(AppDbContext holder)
    {
        var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Users" WHERE "Id" = {Actor.TwitchUserId} FOR UPDATE""");
        return transaction;
    }

    // Like PostgresLockProbe.WaitUntilBlockedOnLockAsync, but a contender that finishes instead of
    // blocking is not a failure here: the assertions on the end state are the oracle (a mutated lock lets
    // the contender run through, and the end state shows what that costs).
    private async Task WaitUntilBlockedOrDoneAsync(string applicationName, Task contender)
    {
        await using var probe = CreateDbContext();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!contender.IsCompleted)
        {
            var waiting = await probe.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE application_name = {applicationName} AND wait_event_type = 'Lock'""")
                .SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{applicationName} neither blocked nor finished.");
            }

            await Task.Delay(20);
        }
    }

    // Returns once the tagged backend waits for a lock held by exactly the backend blockerPid.
    private async Task WaitUntilBlockedByAsync(string applicationName, int blockerPid, Task contender)
    {
        await using var probe = CreateDbContext();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            if (contender.IsCompleted)
            {
                await contender;
                Assert.Fail($"{applicationName} completed without waiting for backend {blockerPid}.");
            }

            var blocked = await probe.Database
                .SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM pg_stat_activity WHERE application_name = {applicationName} AND {blockerPid} = ANY(pg_blocking_pids(pid))""")
                .SingleAsync();
            if (blocked > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"{applicationName} never waited for backend {blockerPid}.");
            }

            await Task.Delay(20);
        }
    }

    private AppDbContext CreateDbContext(IInterceptor interceptor)
    {
        using var plain = CreateDbContext();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(plain.Database.GetConnectionString())
            .AddInterceptors(interceptor)
            .Options;
        return new AppDbContext(options);
    }

    // Parks the first command whose text contains the marker until the test lets it go.
    private sealed class PauseBeforeCommand(string marker, TaskCompletionSource reached, TaskCompletionSource proceed) : DbCommandInterceptor
    {
        private int _paused;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await PauseIfMarkedAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await PauseIfMarkedAsync(command);
            return result;
        }

        private async Task PauseIfMarkedAsync(DbCommand command)
        {
            if (command.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                reached.TrySetResult();
                await proceed.Task;
            }
        }
    }
}
