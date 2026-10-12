using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Integration.SlotSyncKit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Real PostgreSQL interleavings around the slot-aware sync (plan #372, 6.5 (1)–(4), (7), (8), (10)),
// plus the EventAPI delta's lock order and recovery (Z8) and the shared tick context (review P2-2).
// Every ordering is forced, never hoped for: a contender either holds a row lock before the sync
// starts, or a command interceptor runs the contender inside the sync's own flow right after a given
// statement, and PostgresLockProbe waits on pg_stat_activity until a backend really waits on a lock.
// The deadlock cases pick their victim with a per-session deadlock_timeout: Postgres checks for a
// cycle only in the waiting backend whose timeout fires first after the cycle has formed.
[Collection("Postgres")]
public class SevenTvSyncServiceSlotConcurrencyTests(PostgresFixture fixture)
{
    private const string EmoteLockMarker = "FOR NO KEY UPDATE OF e";
    private const string ChannelLock = "FROM \"Channels\" WHERE \"Id\" = ANY(";
    private const string EmoteIdLock = "FROM \"Emotes\" WHERE \"Id\" = ANY(";
    private const string LeaveUpsert = "INSERT INTO \"EmoteSetLeaveObservations\"";
    private const string RestartLine = "restarting the save";
    private const string DeadlockLine = "deadlock victim";
    private const string SlotJoin = "JOIN \"EmoteSlots\"";
    private const string DeltaRetryLine = "retrying once";
    private static readonly AuditActor Actor = new("tw_slot_admin", "slot_admin");

    // (1) AK 27, REST half: a second observation of the same entry under a new name waits for the
    // emote lock, re-reads and renames the one slot instead of creating a second one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondObservationUnderANewName_WaitsForTheEmoteLock_AndRenamesTheSameSlot(bool oldNameStillListed)
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_c_ak27_{oldNameStillListed}".ToLowerInvariant());
        var x = await SeedEmoteAsync(setup, channel, "x", "A");

        await using var holder = fixture.CreateDbContext();
        await using var holderTx = await holder.Database.BeginTransactionAsync();
        await holder.LockEmotesByIdsAsync([x.Id], CancellationToken.None);
        var first = new EmoteSlot { EmoteId = x.Id, Name = "A", AddedAt = T1, AddedAtFromRest = true };
        holder.EmoteSlots.Add(first);
        await holder.SaveChangesAsync();

        var tag = $"slot_ak27_sync_{oldNameStillListed}".ToLowerInvariant();
        await using var syncDb = fixture.CreateTaggedDbContext(tag);
        SevenTvEmote[] live = oldNameStillListed ? [Live("x", "A", T1), Live("x", "C", T1)] : [Live("x", "C", T1)];
        var sync = CreateService(syncDb, ApiReturning(channel, SetA, live), slotsEnabled: true).SyncChannelAsync(channel.ChannelName);

        await fixture.WaitUntilBlockedOnLockAsync(tag, sync);
        await holderTx.CommitAsync();
        Assert.NotNull(await sync.WaitAsync(TimeSpan.FromSeconds(30)));

        var slots = await SlotsAsync(setup, x.Id);
        if (oldNameStillListed)
        {
            // A real tie group [A(t), C(t)] stays two name-distinct slots (AK 8).
            Assert.Equal(["A", "C"], slots.Select(s => s.Name));
            Assert.Equal(first.Id, slots.Single(s => s.Name == "A").Id);
        }
        else
        {
            var slot = Assert.Single(slots);
            Assert.Equal((first.Id, "C", T1), (slot.Id, slot.Name, slot.AddedAt));
        }
    }

    // (2) Sol P1, Revision 2: a parent row created between the emote lock and the read must not be
    // written unlocked. Without the coverage check the first attempt would read E (committed by the
    // second connection, archived, without slots), plan a new slot C(t) and save it after the third
    // connection's A(t) commits — two authoritative slots for one entry, (E, t, A) and (E, t, C),
    // which the unique index cannot stop because the names differ. With the check the save restarts
    // from step 1, locks E as well, waits for the third connection and renames A(t) to C.
    [Fact]
    public async Task ParentRowCreatedBetweenLockAndRead_RestartsFromStepOne_AndBindsTheConcurrentSlot()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "slot_c_coverage");
        Assert.False(await setup.ChannelEmoteSetObservations.AnyAsync(o => o.ChannelId == channel.Id));

        await using var third = fixture.CreateDbContext();
        var thirdTx = new TransactionHolder();
        var concurrentSlotId = string.Empty;
        var contenderReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new CommandHook(
            text => text.Contains(EmoteLockMarker, StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence != 1)
                {
                    return;
                }

                // Second connection: the parent row, without any channel lock (a ballot or backfill upsert).
                await using (var second = fixture.CreateDbContext())
                {
                    await ArchivedEmoteRowUpsert.EnsureRowsAsync(
                        second, channel.Id, [new ArchivedEmoteRow("e", "A", Url("e"), null)], DateTime.UtcNow, CancellationToken.None);
                }

                // Third connection: takes that row's lock and records the entry as A(t), still open.
                var emoteId = await third.Emotes.Where(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "e").Select(e => e.Id).SingleAsync();
                thirdTx.Transaction = await third.Database.BeginTransactionAsync();
                await third.LockEmotesByIdsAsync([emoteId], CancellationToken.None);
                var slot = new EmoteSlot { EmoteId = emoteId, Name = "A", AddedAt = T1, AddedAtFromRest = true };
                third.EmoteSlots.Add(slot);
                await third.SaveChangesAsync();
                concurrentSlotId = slot.Id;
                contenderReady.TrySetResult();
            });

        const string tag = "slot_coverage_sync";
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var syncDb = fixture.CreateTaggedDbContext(tag, [hook]);
        var sync = CreateService(syncDb, ApiReturning(channel, SetA, Live("e", "C", T1)), slotsEnabled: true, logger: logger)
            .SyncChannelAsync(channel.ChannelName);

        await contenderReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await fixture.WaitUntilBlockedOnLockAsync(tag, sync);
        await thirdTx.Transaction!.CommitAsync();
        Assert.NotNull(await sync.WaitAsync(TimeSpan.FromSeconds(30)));

        var emote = await EmoteAsync(setup, channel, "e");
        var authoritative = (await SlotsAsync(setup, emote.Id)).Where(s => s.AddedAtFromRest && s.AddedAt == T1).ToList();
        var only = Assert.Single(authoritative);
        Assert.Equal((concurrentSlotId, "C", false), (only.Id, only.Name, only.IsArchived));
        Assert.False(emote.IsArchived);
        Assert.Single(Lines(logger, RestartLine));
        Assert.Equal(1, await setup.ChannelEmoteSetObservations.CountAsync(o => o.ChannelId == channel.Id && o.SevenTvEmoteSetId == SetA && o.ObservedToUtc == null));
        // The restart ran from step 1: the observation lookup and the channel lock ran twice each.
        Assert.Equal(2, hook.Count(t => t.Contains("FROM \"ChannelEmoteSetObservations\"", StringComparison.Ordinal) && t.Contains("\"ObservedToUtc\" IS NULL", StringComparison.Ordinal)));
        Assert.Equal(2, hook.Count(t => t.Contains("FROM \"Channels\" WHERE \"Id\" = ANY(", StringComparison.Ordinal) && t.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal)));
    }

    // (3) A concurrent writer inserts the same authoritative slot right before the sync's save: one
    // retry, which binds to that slot by date.
    [Fact]
    public async Task SameAuthoritativeSlotInsertedConcurrently_RetriesOnce_AndKeepsOneSlot()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "slot_c_key_conflict");
        var x = await SeedEmoteAsync(setup, channel, "x", "A");
        var insertedId = string.Empty;
        var hook = new SaveHook(async (context, _) =>
        {
            var staged = context.ChangeTracker.Entries<EmoteSlot>()
                .Where(e => e.State == EntityState.Added && e.Entity.AddedAtFromRest)
                .Select(e => e.Entity)
                .FirstOrDefault();
            if (staged is null || insertedId.Length > 0)
            {
                return;
            }

            await using var other = fixture.CreateDbContext();
            var slot = new EmoteSlot { EmoteId = staged.EmoteId, Name = staged.Name, AddedAt = staged.AddedAt, AddedAtFromRest = true };
            other.EmoteSlots.Add(slot);
            await other.SaveChangesAsync();
            insertedId = slot.Id;
        });
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var syncDb = fixture.CreateDbContext([hook]);

        var result = await CreateService(syncDb, ApiReturning(channel, SetA, Live("x", "A", T1)), slotsEnabled: true, logger: logger)
            .SyncChannelAsync(channel.ChannelName);

        Assert.NotNull(result);
        Assert.Equal(insertedId, Assert.Single(await SlotsAsync(setup, x.Id)).Id);
        Assert.Single(Lines(logger, "same authoritative key"));
    }

    // (4a) Z3, purge first: the real PurgeAsync's cascading DELETE holds the channel row; the sync
    // waits at its channel lock and ends in "row vanished" once the purge commits. Which lock it waits
    // at is asserted: without the channel lock the sync would wait at its emote lock behind the
    // purge's cascade and end as "row vanished" all the same.
    [Fact]
    public async Task PurgeFirst_SyncWaitsAtTheChannelLock_ThenEndsAsRowVanished()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "slot_c_purge_first");
        await SeedSlotAsync(setup, await SeedEmoteAsync(setup, channel, "x", "A", firstSeenAt: T1), "A", T1);

        await using var purgeDb = fixture.CreateDbContext();
        await using var purgeTx = await purgeDb.Database.BeginTransactionAsync();
        Assert.True(await CreateChannelService(purgeDb).PurgeAsync(channel.ChannelName, Actor));

        const string tag = "slot_purge_first_sync";
        var cache = new EmoteMatchCache();
        await using var syncDb = fixture.CreateTaggedDbContext(tag);
        var sync = CreateService(syncDb, ApiReturning(channel, SetA, Live("x", "A", T1)), slotsEnabled: true, cache: cache)
            .SyncChannelAsync(channel.ChannelName);

        await fixture.WaitUntilBlockedOnLockAsync(tag, sync);
        var waitedAt = await fixture.WaitingQueryAsync(tag);
        await purgeTx.CommitAsync();

        Assert.Null(await sync.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains(ChannelLock, waitedAt, StringComparison.Ordinal);
        Assert.DoesNotContain(channel.ChannelName, cache.GetCachedChannelNames());
        Assert.False(await setup.Channels.AnyAsync(c => c.Id == channel.Id));
    }

    // (4b) Z3, sync first: held after its emote lock, the sync makes the purge's DELETE wait on the
    // channel row; both finish, neither with 40P01.
    [Fact]
    public async Task SyncFirst_PurgeWaitsAtTheChannelRow_BothSucceed()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "slot_c_sync_first");
        await SeedSlotAsync(setup, await SeedEmoteAsync(setup, channel, "x", "A", firstSeenAt: T1), "A", T1);

        const string purgeTag = "slot_sync_first_purge";
        await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
        Task<bool>? purge = null;
        var hook = new CommandHook(
            text => text.Contains(EmoteLockMarker, StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence != 1)
                {
                    return;
                }

                purge = CreateChannelService(purgeDb).PurgeAsync(channel.ChannelName, Actor);
                await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);
            });
        await using var syncDb = fixture.CreateDbContext([hook]);

        var result = await CreateService(syncDb, ApiReturning(channel, SetA, Live("x", "B", T2)), slotsEnabled: true)
            .SyncChannelAsync(channel.ChannelName)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(result);
        Assert.True(await purge!.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(await setup.Channels.AnyAsync(c => c.Id == channel.Id));
    }

    // (7) Z4: the settle's usage UPDATE meets a transaction that holds a seed row and then asks for the
    // emote lock — a real 40P01. (a) The sync is the victim and retries; (b) the other side is the
    // victim, the sync goes through without a retry, and the error is transient (what lets the
    // backfill worker pause and resume at the block). The victim is always the second waiter, with a
    // short deadlock_timeout against the first waiter's 60 s: Postgres checks once per wait, when that
    // backend's timeout fires, and the cycle exists the moment the second waiter starts waiting. So in
    // (a) the counter asks for the emote lock first (from inside the sync's flow, right after the sync
    // took it) and the sync waits second at the usage row; in (b) the sync waits first at the usage
    // row and the counter second at the emote lock.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettleMeetsAUsageRowHolder_RealDeadlock_TheVictimRecovers(bool syncIsVictim)
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_c_z4_{syncIsVictim}".ToLowerInvariant());
        var x = await SeedEmoteAsync(setup, channel, "x", "A", firstSeenAt: T1);
        var rest = await SeedSlotAsync(setup, x, "A", T1);
        var seed = await SeedSeedAsync(setup, x);
        await SeedUsageAsync(setup, x, seed, SetA, 4, 5);

        var counterTag = $"slot_z4_counter_{syncIsVictim}".ToLowerInvariant();
        var syncTag = $"slot_z4_sync_{syncIsVictim}".ToLowerInvariant();
        await using var counter = fixture.CreateTaggedDbContext(counterTag, [], options: $"-c deadlock_timeout={(syncIsVictim ? 60000 : 200)}");
        await using var counterTx = await counter.Database.BeginTransactionAsync();
        await counter.Database
            .SqlQuery<long>($"""SELECT "Id" AS "Value" FROM "UsageStats" WHERE "SlotId" = {seed.Id} FOR UPDATE""")
            .ToListAsync();

        Task<IReadOnlyList<string>>? counterLock = null;
        var hook = new CommandHook(
            text => text.Contains(EmoteLockMarker, StringComparison.Ordinal),
            async occurrence =>
            {
                if (!syncIsVictim || occurrence != 1)
                {
                    return;
                }

                counterLock = counter.LockEmotesByIdsAsync([x.Id], CancellationToken.None);
                await fixture.WaitUntilBlockedOnLockAsync(counterTag, counterLock);
            });
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var syncDb = fixture.CreateTaggedDbContext(syncTag, [hook], options: $"-c deadlock_timeout={(syncIsVictim ? 200 : 60000)}");
        var sync = CreateService(syncDb, ApiReturning(channel, SetA, Live("x", "A", T1)), slotsEnabled: true, logger: logger)
            .SyncChannelAsync(channel.ChannelName);
        await fixture.WaitUntilBlockedOnLockAsync(syncTag, sync);

        if (syncIsVictim)
        {
            // Granted the moment Postgres aborts the sync's transaction — before the sync's own code
            // has caught the error, so its warning is checked only once the sync has finished.
            await counterLock!.WaitAsync(TimeSpan.FromSeconds(30));
            await counterTx.CommitAsync();
        }
        else
        {
            counterLock = counter.LockEmotesByIdsAsync([x.Id], CancellationToken.None);
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => counterLock.WaitAsync(TimeSpan.FromSeconds(30)));
            var postgres = FindPostgresException(thrown);
            Assert.Equal(PostgresErrorCodes.DeadlockDetected, postgres.SqlState);
            Assert.True(postgres.IsTransient);
            await counterTx.RollbackAsync();
        }

        Assert.NotNull(await sync.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(syncIsVictim ? 1 : 0, Lines(logger, DeadlockLine).Count);

        Assert.Equal((2, 9), Assert.Single(await UsageBySlotAsync(setup, x.Id, SetA), u => u.Key == rest.Id).Value);
        Assert.False((await SlotAsync(setup, seed.Id)).SeedUnsettled);
    }

    // (8) Limit: a second deadlock in the retry propagates (both flag states, E27). Injected — every
    // recovery above is a real one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlockInTheRetryAsWell_Propagates(bool slotsEnabled)
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_c_double_deadlock_{slotsEnabled}".ToLowerInvariant());
        var hook = new SaveHook((_, _) => throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected));
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var syncDb = fixture.CreateDbContext([hook]);
        var service = CreateService(syncDb, ApiReturning(channel, SetA, Live("x", "A", T1)), slotsEnabled, logger: logger);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.SyncChannelAsync(channel.ChannelName));

        Assert.Equal(PostgresErrorCodes.DeadlockDetected, FindPostgresException(thrown).SqlState);
        Assert.Single(Lines(logger, DeadlockLine));
        Assert.Equal(2, hook.Calls);
    }

    // (8) Limit: the coverage check gives up after two restarts; the third miss propagates.
    [Fact]
    public async Task CoverageMissOnEveryAttempt_GivesUpAfterTwoRestarts()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "slot_c_coverage_limit");
        string[] newcomers = ["n1", "n2", "n3"];
        var hook = new CommandHook(
            text => text.Contains(EmoteLockMarker, StringComparison.Ordinal),
            async occurrence =>
            {
                await using var writer = fixture.CreateDbContext();
                var id = newcomers[occurrence - 1];
                await ArchivedEmoteRowUpsert.EnsureRowsAsync(
                    writer, channel.Id, [new ArchivedEmoteRow(id, id, Url(id), null)], DateTime.UtcNow, CancellationToken.None);
            });
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var syncDb = fixture.CreateDbContext([hook]);
        var service = CreateService(
            syncDb, ApiReturning(channel, SetA, [.. newcomers.Select(id => Live(id, id, T1))]), slotsEnabled: true, logger: logger);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.SyncChannelAsync(channel.ChannelName));

        Assert.Equal(2, Lines(logger, RestartLine).Count);
        Assert.Equal(3, hook.Count(t => t.Contains(EmoteLockMarker, StringComparison.Ordinal)));
        Assert.Empty(await setup.EmoteSlots.Where(s => s.Emote.ChannelId == channel.Id).ToListAsync());
    }

    // Review P2-2: a sync that fails for good must not leave its tracked changes in the context. The
    // periodic resync runs every channel of its tick on one scope and one context; channel A fails
    // terminally (a coverage miss past the restarts, or a deadlock in the retry as well), channel B
    // follows on the same service. B must succeed with its own stamps, and nothing of A — success
    // stamps, a first observation interval, slots — may ride B's save. Without the cleared tracker,
    // A's added slots make B's lock assertion throw (deadlock case), and A's stamps and observation
    // interval are written by B's save without A's channel lock (coverage case).
    [Theory]
    [InlineData("coverage")]
    [InlineData("deadlock")]
    public async Task TerminalFailureOfOneChannel_LeavesNothingForTheNextChannelOfTheTick(string failure)
    {
        await using var setup = fixture.CreateDbContext();
        var a = await SeedChannelAsync(setup, $"slot_c_tick_a_{failure}");
        var b = await SeedChannelAsync(setup, $"slot_c_tick_b_{failure}");
        string[] newcomers = ["n1", "n2", "n3"];
        var coverageHook = new CommandHook(
            text => failure == "coverage" && text.Contains(EmoteLockMarker, StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence > newcomers.Length)
                {
                    return;
                }

                await using var writer = fixture.CreateDbContext();
                var id = newcomers[occurrence - 1];
                await ArchivedEmoteRowUpsert.EnsureRowsAsync(
                    writer, a.Id, [new ArchivedEmoteRow(id, id, Url(id), null)], DateTime.UtcNow, CancellationToken.None);
            });
        var deadlockHook = new SaveHook((_, call) => failure == "deadlock" && call <= 2
            ? throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected)
            : Task.CompletedTask);
        var apiClient = Substitute.For<ISevenTvApiClient>();
        SevenTvEmote[] liveA = failure == "coverage" ? [.. newcomers.Select(id => Live(id, id, T1))] : [Live("a1", "Alpha", T1)];
        apiClient.GetChannelStateForTwitchUserAsync(a.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-a", new SevenTvEmoteSet(SetA, liveA))));
        apiClient.GetChannelStateForTwitchUserAsync(b.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-b", new SevenTvEmoteSet(SetB, [Live("b1", "Beta", T2)]))));
        await using var tickDb = fixture.CreateDbContext([coverageHook, deadlockHook]);
        var service = CreateService(tickDb, apiClient, slotsEnabled: true);

        await Assert.ThrowsAnyAsync<Exception>(() => service.SyncChannelAsync(a.ChannelName));
        Assert.DoesNotContain(tickDb.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
        var result = await service.SyncChannelAsync(b.ChannelName);

        Assert.NotNull(result);
        var rowA = await setup.Channels.AsNoTracking().SingleAsync(c => c.Id == a.Id);
        var rowB = await setup.Channels.AsNoTracking().SingleAsync(c => c.Id == b.Id);
        Assert.Null(rowA.LastSyncedAtUtc);
        Assert.Null(rowA.LastSyncAttemptAtUtc);
        Assert.NotNull(rowB.LastSyncedAtUtc);
        Assert.Equal(SetB, rowB.ActiveEmoteSetId);
        Assert.False(await setup.ChannelEmoteSetObservations.AnyAsync(o => o.ChannelId == a.Id));
        Assert.True(await setup.ChannelEmoteSetObservations.AnyAsync(o => o.ChannelId == b.Id && o.SevenTvEmoteSetId == SetB));
        Assert.Empty(await setup.EmoteSlots.Where(s => s.Emote.ChannelId == a.Id).ToListAsync());
        var betaSlot = Assert.Single(await SlotsAsync(setup, (await EmoteAsync(setup, b, "b1")).Id));
        Assert.Equal(("Beta", T2), (betaSlot.Name, betaSlot.AddedAt));
    }

    // Z8, the EventAPI delta's statements by flag: on, the channel row, then the emote rows of the
    // dispatch, then the leave upsert, then the save; off, exactly the statements of before (no row
    // lock, the leave upsert first, a transaction only because something is pulled).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeltaLockStatements_FollowTheFlag(bool slotsEnabled)
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_c_delta_order_{slotsEnabled}".ToLowerInvariant());
        await SeedEmoteAsync(setup, channel, "x", "Xenon", firstSeenAt: T1);
        await SeedEmoteAsync(setup, channel, "y", "Yak", firstSeenAt: T1);
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
        await using var db = fixture.CreateDbContext([hook]);

        var result = await CreateService(db, ApiReturning(channel, SetA), slotsEnabled)
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, SetA, new SevenTvEmoteSetDelta([Live("z", "Zebra", null)], [], [new SevenTvPulledEntry("x")]));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        Assert.True((await EmoteAsync(setup, channel, "x")).IsArchived);
        Assert.False((await EmoteAsync(setup, channel, "z")).IsArchived);
        var channelLock = hook.IndexOf(t => t.Contains(ChannelLock, StringComparison.Ordinal));
        var emoteLock = hook.IndexOf(t => t.Contains(EmoteIdLock, StringComparison.Ordinal));
        var leave = hook.IndexOf(t => t.Contains(LeaveUpsert, StringComparison.Ordinal));
        var update = hook.IndexOf(t => t.Contains("UPDATE \"Emotes\"", StringComparison.Ordinal));
        if (!slotsEnabled)
        {
            Assert.Equal(0, hook.Count(t => t.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal)));
            Assert.True(leave >= 0 && leave < update);
            return;
        }

        Assert.Equal(2, hook.Count(t => t.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal)));
        Assert.True(channelLock >= 0 && channelLock < emoteLock && emoteLock < leave && leave < update);
        // Plan #374 3.2 step 2: the slots of the dispatch's emotes are read behind the emote lock.
        var slotRead = hook.IndexOf(t => t.Contains(SlotJoin, StringComparison.Ordinal));
        Assert.True(emoteLock < slotRead && slotRead < leave);
    }

    // Z8 recovery, both flag states: a dispatch chosen as a deadlock victim runs once more on a cleared
    // tracker and a re-read row and applies exactly once; a second deadlock propagates and writes
    // nothing. Injected — the cycle itself is aligned with the flag on (EmoteServiceInSetConcurrencyTests
    // 5b) and only reachable in the switch-over window.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DeltaDeadlockVictim_RetriesOnce_ASecondOnePropagates(bool slotsEnabled, bool twice)
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_c_delta_dl_{slotsEnabled}_{twice}".ToLowerInvariant());
        await SeedEmoteAsync(setup, channel, "x", "Xenon", firstSeenAt: T1);
        await SeedEmoteAsync(setup, channel, "y", "Yak", firstSeenAt: T1);
        var hook = new SaveHook((_, call) => call == 1 || twice
            ? throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected)
            : Task.CompletedTask);
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var db = fixture.CreateDbContext([hook]);
        var service = CreateService(db, ApiReturning(channel, SetA), slotsEnabled, logger: logger);
        var delta = new SevenTvEmoteSetDelta([Live("z", "Zebra", null)], [], [new SevenTvPulledEntry("x")]);

        if (twice)
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetA, delta));
            Assert.Equal(PostgresErrorCodes.DeadlockDetected, FindPostgresException(thrown).SqlState);
        }
        else
        {
            Assert.Equal(SevenTvDeltaOutcome.Applied, (await service.ApplyEmoteSetUpdateAsync(channel.ChannelName, SetA, delta)).Outcome);
        }

        Assert.Single(Lines(logger, DeadlockLine));
        Assert.Equal(2, hook.Calls);
        Assert.Equal(twice, !(await EmoteAsync(setup, channel, "x")).IsArchived);
        Assert.Equal(twice ? 0 : 1, await setup.Emotes.CountAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "z"));
        Assert.Equal(twice ? 0 : 1, await setup.EmoteSetLeaveObservations.CountAsync(o => o.ChannelId == channel.Id));
    }

    // Plan #374 E2/E9, the delta's lock coverage: a writer without the channel lock (a ballot or backfill
    // upsert) creates the row of a pushed id after the delta has read the dispatch's row ids and locked
    // them. The read behind the lock sees a row it does not hold, so the attempt starts over once, locks
    // that row as well and applies the push onto it — one slot, no duplicate row.
    [Fact]
    public async Task DeltaCoverageMiss_RetriesOnce_AndPushesOntoTheConcurrentRow()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "slot_c_delta_coverage");
        var r = await SeedEmoteAsync(setup, channel, "r", "Ray", firstSeenAt: T1);
        var ray = await SeedSlotAsync(setup, r, "Ray", T1);
        var hook = new CommandHook(
            text => text.Contains(EmoteIdLock, StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence != 1)
                {
                    return;
                }

                await using var second = fixture.CreateDbContext();
                await ArchivedEmoteRowUpsert.EnsureRowsAsync(
                    second, channel.Id, [new ArchivedEmoteRow("n", "Neo", Url("n"), null)], DateTime.UtcNow, CancellationToken.None);
            });
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var db = fixture.CreateDbContext([hook]);

        var result = await CreateService(db, ApiReturning(channel, SetA), slotsEnabled: true, logger: logger)
            .ApplyEmoteSetUpdateAsync(
                channel.ChannelName, SetA,
                new SevenTvEmoteSetDelta([Live("n", "Neo", null)], [new SevenTvEmoteRename("r", "Ray", "Ray2", Url("r"))], []));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        Assert.Single(Lines(logger, "appeared between its row lock and its read"));
        Assert.Single(Lines(logger, DeltaRetryLine));
        Assert.Equal(2, hook.Count(t => t.Contains(ChannelLock, StringComparison.Ordinal)));
        var n = await EmoteAsync(setup, channel, "n");
        Assert.Equal((false, false), (n.IsArchived, n.IsPlaceholder));
        Assert.Equal(1, await setup.Emotes.CountAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "n"));
        var neo = Assert.Single(await SlotsAsync(setup, n.Id));
        Assert.Equal(("Neo", false, false), (neo.Name, neo.AddedAtFromRest, neo.IsArchived));
        Assert.Equal("Ray2", (await SlotAsync(setup, ray.Id)).Name);
    }

    // Plan #374 E9: a concurrent insert of a pushed id's emote row right before the delta's save fails it
    // on IX_Emotes_ChannelId_SevenTvEmoteId; the single retry reads that row and pushes onto it.
    [Fact]
    public async Task DeltaEmoteKeyConflict_RetriesOnce_AndPushesOntoTheConcurrentRow()
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, "slot_c_delta_emote_key");
        var r = await SeedEmoteAsync(setup, channel, "r", "Ray", firstSeenAt: T1);
        await SeedSlotAsync(setup, r, "Ray", T1);
        var hook = new SaveHook(async (_, call) =>
        {
            if (call != 1)
            {
                return;
            }

            await using var other = fixture.CreateDbContext();
            await ArchivedEmoteRowUpsert.EnsureRowsAsync(
                other, channel.Id, [new ArchivedEmoteRow("n", "Neo", Url("n"), null)], DateTime.UtcNow, CancellationToken.None);
        });
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var db = fixture.CreateDbContext([hook]);

        var result = await CreateService(db, ApiReturning(channel, SetA), slotsEnabled: true, logger: logger)
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, SetA, new SevenTvEmoteSetDelta([Live("n", "Neo", null)], [], []));

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        Assert.Equal(2, hook.Calls);
        Assert.Single(Lines(logger, "was inserted concurrently — retrying once"));
        var n = await EmoteAsync(setup, channel, "n");
        Assert.False(n.IsArchived);
        Assert.Equal("Neo", Assert.Single(await SlotsAsync(setup, n.Id)).Name);
    }

    // Plan #374 E9: deadlock, coverage miss and emote key conflict share ONE retry — a second failure of
    // either kind propagates, and nothing of the dispatch is written.
    [Theory]
    [InlineData("deadlock")]
    [InlineData("key")]
    public async Task DeltaEmoteKeyConflict_ThenASecondFailure_Propagates(string second)
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_c_delta_twice_{second}");
        var r = await SeedEmoteAsync(setup, channel, "r", "Ray", firstSeenAt: T1);
        await SeedSlotAsync(setup, r, "Ray", T1);
        var hook = new SaveHook((_, call) => throw (call == 1 || second == "key"
            ? new DbUpdateException("emote key", new PostgresException(
                "duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: "IX_Emotes_ChannelId_SevenTvEmoteId"))
            : new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected)));
        var logger = new RecordingLogger<SevenTvSyncService>();
        await using var db = fixture.CreateDbContext([hook]);
        var service = CreateService(db, ApiReturning(channel, SetA), slotsEnabled: true, logger: logger);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyEmoteSetUpdateAsync(
            channel.ChannelName, SetA, new SevenTvEmoteSetDelta([Live("n", "Neo", null)], [], [])));

        Assert.Equal(second == "key" ? PostgresErrorCodes.UniqueViolation : PostgresErrorCodes.DeadlockDetected, FindPostgresException(thrown).SqlState);
        Assert.Equal(2, hook.Calls);
        Assert.Single(Lines(logger, DeltaRetryLine));
        Assert.False(await setup.Emotes.AnyAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "n"));
    }

    // Plan #374 section 6, flush phase 1: the flush's usage insert holds FOR KEY SHARE on the slot and
    // emote rows it references. The delta's emote lock (FOR NO KEY UPDATE) and its non-key slot updates
    // (rename; archive of the last slot and its emote) are compatible with it — the delta finishes while
    // the flush is still open, and both commit.
    [Theory]
    [InlineData("rename")]
    [InlineData("pull")]
    public async Task DeltaAgainstAnOpenFlushInsert_DoesNotWait(string change)
    {
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_c_delta_flush_{change}");
        var keeper = await SeedEmoteAsync(setup, channel, "k", "Keeper", firstSeenAt: T0);
        await SeedSlotAsync(setup, keeper, "Keeper", T0);
        var r = await SeedEmoteAsync(setup, channel, "r", "Ray", firstSeenAt: T1);
        var ray = await SeedSlotAsync(setup, r, "Ray", T1);

        await using var flush = fixture.CreateDbContext();
        await using var flushTx = await flush.Database.BeginTransactionAsync();
        flush.UsageStats.Add(new UsageStat { EmoteId = r.Id, SlotId = ray.Id, EmoteSetId = SetA, Date = new DateOnly(2026, 10, 12), UseCount = 1 });
        await flush.SaveChangesAsync();

        await using var db = fixture.CreateDbContext();
        var delta = change == "rename"
            ? new SevenTvEmoteSetDelta([], [new SevenTvEmoteRename("r", "Ray", "Ray2", Url("r"))], [])
            : new SevenTvEmoteSetDelta([], [], [new SevenTvPulledEntry("r", "Ray")]);
        var result = await CreateService(db, ApiReturning(channel, SetA), slotsEnabled: true)
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, SetA, delta)
            .WaitAsync(TimeSpan.FromSeconds(15));
        await flushTx.CommitAsync();

        Assert.Equal(SevenTvDeltaOutcome.Applied, result.Outcome);
        var slot = await SlotAsync(setup, ray.Id);
        Assert.Equal(change == "rename" ? ("Ray2", false) : ("Ray", true), (slot.Name, slot.IsArchived));
        Assert.Equal((1, 1), (await UsageBySlotAsync(setup, r.Id, SetA))[ray.Id]);
    }

    // (10) E28: renamed while the 7TV call is in flight, with a warm cache under the old login that
    // nobody's LEAVE cleans up — the sync removes it and caches under the new login only. The slot
    // summary (flag on) names the new login too (review P3-6).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamedInFlight_WithAWarmCache_CachesUnderTheNewLoginOnly(bool slotsEnabled)
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, $"slot_if_old_{slotsEnabled}".ToLowerInvariant());
        var newName = $"slot_if_new_{slotsEnabled}".ToLowerInvariant();
        await SeedEmoteAsync(db, channel, "a", "Alpha", firstSeenAt: T1);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();
        var (service, entered, release) = CreateBlockedService(db, cache, channel, slotsEnabled, logger);

        var sync = service.SyncChannelAsync(channel.ChannelName);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The warm start built a cache entry under the old login. With the flag on the emote has no own
        // slot yet and the map has no virtual entries (plan #373, E7), so the name is still missing.
        Assert.Contains(channel.ChannelName, cache.GetCachedChannelNames());
        Assert.Equal(!slotsEnabled, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId.ContainsKey("Alpha"));
        await RenameAsync(channel, newName);
        release.SetResult();
        var result = await sync.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(newName, result!.ChannelName);
        Assert.Contains("Alpha", cache.GetChannelSnapshot(newName).NameToSlotId.Keys);
        Assert.DoesNotContain(channel.ChannelName, cache.GetCachedChannelNames());
        Assert.Equal(slotsEnabled ? [$"{newName}: slot reconcile created 1"] : [], Lines(logger, "slot reconcile created").Select(l => l[..l.IndexOf(" and", StringComparison.Ordinal)]));
    }

    // (10) The other in-flight writers of SevenTvSyncServiceInFlightWriterTests, over both flag states.
    [Theory]
    [InlineData(false, "deactivated")]
    [InlineData(true, "deactivated")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    public async Task DeactivatedOrDeletedInFlight_ReturnsNull_AndLeavesNoCacheEntry(bool slotsEnabled, string writer)
    {
        await using var db = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(db, $"slot_if_{writer}_{slotsEnabled}".ToLowerInvariant());
        await SeedEmoteAsync(db, channel, "a", "Alpha", firstSeenAt: T1);
        var cache = new EmoteMatchCache();
        var (service, entered, release) = CreateBlockedService(db, cache, channel, slotsEnabled);

        var sync = service.SyncChannelAsync(channel.ChannelName);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await using (var other = fixture.CreateDbContext())
        {
            var row = other.Channels.Where(c => c.Id == channel.Id);
            if (writer == "deleted")
            {
                await row.ExecuteDeleteAsync();
            }
            else
            {
                await row.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsBotActive, false));
            }
        }

        release.SetResult();

        Assert.Null(await sync.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.DoesNotContain(channel.ChannelName, cache.GetCachedChannelNames());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldLoginNowCarriedByAnotherActiveRow_LeavesThatRowsCacheEntryAlone(bool slotsEnabled)
    {
        await using var db = fixture.CreateDbContext();
        var oldName = $"slot_if_swap_a_{slotsEnabled}".ToLowerInvariant();
        var channel = await SeedChannelAsync(db, oldName);
        var cache = new EmoteMatchCache();
        var (service, entered, release) = CreateBlockedService(db, cache, channel, slotsEnabled);

        var sync = service.SyncChannelAsync(oldName);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await RenameAsync(channel, $"slot_if_swap_b_{slotsEnabled}".ToLowerInvariant());
        await using (var other = fixture.CreateDbContext())
        {
            other.Channels.Add(new Channel { ChannelName = oldName, TwitchChannelId = $"tw_other_{oldName}", ActiveEmoteSetId = SetA, IsBotActive = true });
            await other.SaveChangesAsync();
        }

        cache.ReplaceChannel(oldName, "other-set", new Dictionary<string, string> { ["Live"] = "other-emote" });
        release.SetResult();
        await sync.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains("Live", cache.GetChannelSnapshot(oldName).NameToSlotId.Keys);
    }

    // (10) E17 + E28: renamed in flight, then the first save is a deadlock victim; after the retry's
    // re-read the cleanup still removes the entry under the old login.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenamedInFlightThenDeadlockRetry_StillCleansUpTheOldLogin(bool slotsEnabled)
    {
        var hook = new SaveHook((_, call) => call == 1
            ? throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected)
            : Task.CompletedTask);
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"slot_if_retry_old_{slotsEnabled}".ToLowerInvariant());
        var newName = $"slot_if_retry_new_{slotsEnabled}".ToLowerInvariant();
        await SeedEmoteAsync(setup, channel, "a", "Alpha", firstSeenAt: T1);
        await using var db = fixture.CreateDbContext([hook]);
        var cache = new EmoteMatchCache();
        var logger = new RecordingLogger<SevenTvSyncService>();
        var (service, entered, release) = CreateBlockedService(db, cache, channel, slotsEnabled, logger);

        var sync = service.SyncChannelAsync(channel.ChannelName);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // The warm start built a cache entry under the old login. With the flag on the emote has no own
        // slot yet and the map has no virtual entries (plan #373, E7), so the name is still missing.
        Assert.Contains(channel.ChannelName, cache.GetCachedChannelNames());
        Assert.Equal(!slotsEnabled, cache.GetChannelSnapshot(channel.ChannelName).NameToSlotId.ContainsKey("Alpha"));
        await RenameAsync(channel, newName);
        release.SetResult();
        var result = await sync.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(newName, result!.ChannelName);
        Assert.Single(Lines(logger, DeadlockLine));
        Assert.Contains("Alpha", cache.GetChannelSnapshot(newName).NameToSlotId.Keys);
        Assert.DoesNotContain(channel.ChannelName, cache.GetCachedChannelNames());
    }

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        throw new Xunit.Sdk.XunitException($"No PostgresException in {exception}");
    }

    private ChannelService CreateChannelService(AppDbContext db) =>
        new(
            db,
            Substitute.For<IRedisPublisher>(),
            Substitute.For<IChannelIdentityService>(),
            Substitute.For<IChannelEmoteSetObservationService>(),
            new BroadcasterChannelLockService(db),
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            Substitute.For<IExcludedChannelFilter>(),
            NullLogger<ChannelService>.Instance);

    private async Task RenameAsync(Channel channel, string newName)
    {
        await using var writer = fixture.CreateDbContext();
        await writer.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ChannelName, newName));
    }

    private static (SevenTvSyncService Service, TaskCompletionSource Entered, TaskCompletionSource Release) CreateBlockedService(
        AppDbContext db, EmoteMatchCache cache, Channel channel, bool slotsEnabled, RecordingLogger<SevenTvSyncService>? logger = null)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(SetA, [Live("a", "Alpha", T1)])));
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return state;
            });
        return (CreateService(db, apiClient, slotsEnabled, cache, logger), entered, release);
    }

    /// <summary>A transaction a hook opens inside the sync's flow and the test commits later.</summary>
    private sealed class TransactionHolder
    {
        public Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? Transaction { get; set; }
    }
}
