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

// Real PostgreSQL interleavings around the legacy set-centric report EmoteService.MarkInSetAsync (plan
// #372, 2.9 and 6.5 (5), (6), (8) report half, (9)), and of the EventAPI dispatch against the report
// and the sync (Z8). The report locks the affected channel rows, then the emote rows, then upserts the
// leave rows (E25); a deadlock victim, and a report that finds an affected channel deleted at its
// channel lock, runs the whole transaction once more (E27). Every ordering is forced: a command interceptor runs the
// contender inside the first side's own flow right after a given statement, and PostgresLockProbe waits
// on pg_stat_activity until a backend really waits on a lock. Deadlock victims are picked with a
// per-session deadlock_timeout: Postgres checks for a cycle only in the waiting backend whose timeout
// fires first after the cycle has formed, and that backend aborts itself.
[Collection("Postgres")]
public class EmoteServiceInSetConcurrencyTests(PostgresFixture fixture)
{
    private const string ReportEmoteLock = "FROM \"Emotes\" WHERE \"Id\" = ANY(";
    private const string ChannelLock = "FROM \"Channels\" WHERE \"Id\" = ANY(";
    private const string SyncEmoteLock = "FOR NO KEY UPDATE OF e";
    private const string LeaveUpsert = "INSERT INTO \"EmoteSetLeaveObservations\"";
    private const string DeadlockLine = "deadlock victim";
    private const string VanishedLine = "deleted meanwhile";
    private const string Reported = "xemote1";
    private const string Newcomer = "yemote1";
    private static readonly AuditActor Actor = new("tw_inset_admin", "inset_admin");

    // (5) Z1 aligned: the first side is held right after its emote lock;
    // the second waits already at its channel lock (Revision 2), and both finish without 40P01 once
    // the first commits. End state as if run one after the other.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportAndSync_TheSecondWaitsAtTheChannelLock_NoDeadlock(bool reportFirst)
    {
        var tag = reportFirst ? "zonerf" : "zonesf";
        var set = SetOf(tag);
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"inset_{tag}", set);
        await SeedActiveEmoteAsync(setup, channel, Reported);

        var reportTag = $"inset_{tag}_report";
        var syncTag = $"inset_{tag}_sync";
        var reportLogger = new RecordingLogger<EmoteService>();
        var syncLogger = new RecordingLogger<SevenTvSyncService>();
        Task<SyncDeletedInSetResultDto>? report = null;
        Task<SevenTvSyncResult?>? sync = null;
        var waitingQuery = string.Empty;

        AppDbContext reportDb;
        AppDbContext syncDb;
        if (reportFirst)
        {
            syncDb = fixture.CreateTaggedDbContext(syncTag);
            var hook = new CommandHook(
                text => text.Contains(ReportEmoteLock, StringComparison.Ordinal),
                async occurrence =>
                {
                    sync = StartSync(syncDb, channel, set, syncLogger);
                    await fixture.WaitUntilBlockedOnLockAsync(syncTag, sync);
                    waitingQuery = await fixture.WaitingQueryAsync(syncTag);
                });
            reportDb = fixture.CreateTaggedDbContext(reportTag, [hook]);
            report = StartReport(reportDb, set, reportLogger);
        }
        else
        {
            reportDb = fixture.CreateTaggedDbContext(reportTag);
            var hook = new CommandHook(
                text => text.Contains(SyncEmoteLock, StringComparison.Ordinal),
                async occurrence =>
                {
                    if (occurrence != 1)
                    {
                        return;
                    }

                    report = StartReport(reportDb, set, reportLogger);
                    await fixture.WaitUntilBlockedOnLockAsync(reportTag, report);
                    waitingQuery = await fixture.WaitingQueryAsync(reportTag);
                });
            syncDb = fixture.CreateTaggedDbContext(syncTag, [hook]);
            sync = StartSync(syncDb, channel, set, syncLogger);
        }

        await using (reportDb)
        await using (syncDb)
        {
            await SettleAsync(() => reportFirst ? report : sync, () => reportFirst ? sync : report);
            var reported = await report!.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(await sync!.WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Contains(ChannelLock, waitingQuery, StringComparison.Ordinal);
            Assert.Equal(1, Assert.Single(reported.Channels).Count);
        }

        Assert.Empty(ReportLines(reportLogger, DeadlockLine));
        Assert.Empty(Lines(syncLogger, DeadlockLine));
        await AssertSerialEndStateAsync(setup, channel, set, newcomerSlots: 1);
    }

    // (5b) Z8 aligned: an EventAPI dispatch that pulls the reported emote
    // (the mass delete's own REMOVE) and pushes a newcomer, against the report and against the sync.
    // The dispatch takes the channel row first like the other two, so whichever side comes first, the
    // second waits at its channel lock and both finish without 40P01 or a retry. Before, the dispatch
    // inserted its leave row first and then waited for the emote row the report or sync held, while
    // that side's own leave upsert waited for the dispatch's fresh leave row.
    [Theory]
    [InlineData("report", true)]
    [InlineData("report", false)]
    [InlineData("sync", true)]
    [InlineData("sync", false)]
    public async Task DeltaPull_AgainstReportOrSync_TheSecondWaitsAtTheChannelLock_NoDeadlock(string contender, bool deltaFirst)
    {
        var tag = $"zeight{contender[0]}{(deltaFirst ? "d" : "c")}";
        var set = SetOf(tag);
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"inset_{tag}", set);
        await SeedActiveEmoteAsync(setup, channel, Reported);

        var deltaTag = $"inset_{tag}_delta";
        var otherTag = $"inset_{tag}_{contender}";
        var deltaLogger = new RecordingLogger<SevenTvSyncService>();
        var syncLogger = new RecordingLogger<SevenTvSyncService>();
        var reportLogger = new RecordingLogger<EmoteService>();
        Task<SevenTvDeltaResult>? delta = null;
        Task? other = null;
        var waitingQuery = string.Empty;
        Task StartOther(AppDbContext db) => contender == "report"
            ? StartReport(db, set, reportLogger)
            : StartSync(db, channel, set, syncLogger);

        AppDbContext deltaDb;
        AppDbContext otherDb;
        if (deltaFirst)
        {
            // Held right after its emote lock (the statement the report's lock shares).
            otherDb = fixture.CreateTaggedDbContext(otherTag);
            var hook = new CommandHook(
                text => text.Contains(ReportEmoteLock, StringComparison.Ordinal),
                async occurrence =>
                {
                    if (occurrence != 1)
                    {
                        return;
                    }

                    other = StartOther(otherDb);
                    await fixture.WaitUntilBlockedOnLockAsync(otherTag, other);
                    waitingQuery = await fixture.WaitingQueryAsync(otherTag);
                });
            deltaDb = fixture.CreateTaggedDbContext(deltaTag, [hook]);
            delta = StartDelta(deltaDb, channel, set, deltaLogger);
        }
        else
        {
            deltaDb = fixture.CreateTaggedDbContext(deltaTag);
            var marker = contender == "report" ? ReportEmoteLock : SyncEmoteLock;
            var hook = new CommandHook(
                text => text.Contains(marker, StringComparison.Ordinal),
                async occurrence =>
                {
                    if (occurrence != 1)
                    {
                        return;
                    }

                    delta = StartDelta(deltaDb, channel, set, deltaLogger);
                    await fixture.WaitUntilBlockedOnLockAsync(deltaTag, delta);
                    waitingQuery = await fixture.WaitingQueryAsync(deltaTag);
                });
            otherDb = fixture.CreateTaggedDbContext(otherTag, [hook]);
            other = StartOther(otherDb);
        }

        await using (deltaDb)
        await using (otherDb)
        {
            await SettleAsync(() => deltaFirst ? delta : other, () => deltaFirst ? other : delta);
            var applied = await delta!.WaitAsync(TimeSpan.FromSeconds(30));
            await other!.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Contains(ChannelLock, waitingQuery, StringComparison.Ordinal);
            Assert.Contains(applied.Outcome, new[] { SevenTvDeltaOutcome.Applied, SevenTvDeltaOutcome.NoChange });
        }

        Assert.Empty(Lines(deltaLogger, DeadlockLine));
        Assert.Empty(Lines(syncLogger, DeadlockLine));
        Assert.Empty(ReportLines(reportLogger, DeadlockLine));
        Assert.True((await EmoteAsync(setup, channel, Reported)).IsArchived);
        var newcomer = await EmoteAsync(setup, channel, Newcomer);
        Assert.False(newcomer.IsArchived);
        // Plan #374 7.4, the slot writes: the push slot and the sync's dated entry are one slot, whichever
        // side came first (bound by name, or a no-op push onto the sync's slot).
        Assert.Single(await SlotsAsync(setup, newcomer.Id), s => !s.IsArchived);
        Assert.Equal(1, await LeaveRowsAsync(setup, channel, set));
        if (contender == "report")
        {
            Assert.Equal([channel.ChannelName], await AuditChannelsAsync(setup, set));
        }
    }

    // (9) Z6, report against the admin purge (the real MarkDeletedInSetAsync and the real
    // PurgeAsync). Two hit channels; K2 is purged. K2 comes first in lock order (Id) but second in hit
    // order (name), and K2 has no leave row yet, so the report's leave insert needs the KEY SHARE on
    // K2 that the purge's cascading DELETE holds against it — the cycle E25 removes.
    [Theory]
    [InlineData("reportfirst")]
    [InlineData("purgefirst")]
    [InlineData("rollback")]
    public async Task ReportAndAdminPurge_NeverDeadlock_InEveryStartOrder(string variant)
    {
        var tag = $"zsix{variant}";
        var set = SetOf(tag);
        var stem = Guid.NewGuid().ToString();
        await using var setup = fixture.CreateDbContext();
        var k1 = await SeedChannelAsync(setup, $"inset_{tag}_a", set, id: "f" + stem[1..]);
        var k2 = await SeedChannelAsync(setup, $"inset_{tag}_b", set, id: "0" + stem[1..]);
        await SeedActiveEmoteAsync(setup, k1, Reported);
        await SeedActiveEmoteAsync(setup, k2, Reported);
        await EmoteSetLeaveObservations.RecordAsync(setup, k1.Id, set, [Reported], DateTime.UtcNow.AddDays(-2), CancellationToken.None);

        var publisher = Substitute.For<IRedisPublisher>();
        var reportLogger = new RecordingLogger<EmoteService>();
        const string reportTag = "inset_zsix_report";
        const string purgeTag = "inset_zsix_purge";
        SyncDeletedInSetResultDto reported;
        if (variant == "reportfirst")
        {
            // (a) Held after its emote lock, the report makes the purge's DELETE wait at K2's row.
            await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
            Task<bool>? purge = null;
            var purgeWaitedOn = string.Empty;
            var hook = new CommandHook(
                text => text.Contains(ReportEmoteLock, StringComparison.Ordinal),
                async _ =>
                {
                    purge = CreateChannelService(purgeDb, publisher).PurgeAsync(k2.ChannelName, Actor);
                    await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);
                    purgeWaitedOn = await fixture.WaitingQueryAsync(purgeTag);
                });
            await using var reportDb = fixture.CreateTaggedDbContext(reportTag, [hook]);
            var report = StartReport(reportDb, set, reportLogger);
            await SettleAsync(() => report, () => purge);
            reported = await report.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(await purge!.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains("DELETE FROM \"Channels\"", purgeWaitedOn, StringComparison.Ordinal);
            Assert.Equal([(k1.ChannelName, 1), (k2.ChannelName, 1)], reported.Channels.Select(c => (c.ChannelName, c.Count)));
            Assert.Equal([k1.ChannelName, k2.ChannelName], await AuditChannelsAsync(setup, set));
        }
        else
        {
            // (b)/(c) The purge's DELETE runs first in a transaction the test controls; the report waits
            // at its channel lock until the purge commits or rolls back.
            await using var purgeDb = fixture.CreateDbContext();
            await using var purgeTx = await purgeDb.Database.BeginTransactionAsync();
            Assert.True(await CreateChannelService(purgeDb, publisher).PurgeAsync(k2.ChannelName, Actor));

            await using var reportDb = fixture.CreateTaggedDbContext(reportTag);
            var report = StartReport(reportDb, set, reportLogger);
            await fixture.WaitUntilBlockedOnLockAsync(reportTag, report);
            Assert.Contains(ChannelLock, await fixture.WaitingQueryAsync(reportTag), StringComparison.Ordinal);
            if (variant == "purgefirst")
            {
                await purgeTx.CommitAsync();
            }
            else
            {
                await purgeTx.RollbackAsync();
            }

            reported = await report.WaitAsync(TimeSpan.FromSeconds(30));
        }

        await publisher.Received(1).PublishAsync(BotCommands.Channel, $"{BotCommands.LeavePrefix}{k2.ChannelName}", Arg.Any<CancellationToken>());
        Assert.Empty(ReportLines(reportLogger, DeadlockLine));
        Assert.True((await EmoteAsync(setup, k1, Reported)).IsArchived);
        Assert.Equal(1, await LeaveRowsAsync(setup, k1, set));
        switch (variant)
        {
            case "reportfirst":
                Assert.Empty(ReportLines(reportLogger, VanishedLine));
                await AssertPurgedAsync(setup, k2);
                break;
            case "purgefirst":
                // The first attempt found K2 gone at its channel lock and was retried without it.
                Assert.Single(ReportLines(reportLogger, VanishedLine));
                Assert.Equal([(k1.ChannelName, 1)], reported.Channels.Select(c => (c.ChannelName, c.Count)));
                Assert.Equal([k1.ChannelName], await AuditChannelsAsync(setup, set));
                await AssertPurgedAsync(setup, k2);
                break;
            default:
                Assert.Empty(ReportLines(reportLogger, VanishedLine));
                Assert.Equal([(k1.ChannelName, 1), (k2.ChannelName, 1)], reported.Channels.Select(c => (c.ChannelName, c.Count)));
                Assert.Equal([k1.ChannelName, k2.ChannelName], await AuditChannelsAsync(setup, set));
                Assert.True((await EmoteAsync(setup, k2, Reported)).IsArchived);
                Assert.Equal(1, await LeaveRowsAsync(setup, k2, set));
                break;
        }
    }

    // (8) Limit: a deadlock in the retry as well propagates. Injected — every recovery above is a real
    // one. Nothing of either attempt is written.
    [Fact]
    public async Task DeadlockInTheRetryAsWell_Propagates_AndWritesNothing()
    {
        var tag = "zeightddon";
        var set = SetOf(tag);
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"inset_{tag}", set);
        await SeedActiveEmoteAsync(setup, channel, Reported);
        var hook = new SaveHook((_, _) => throw Deadlock());
        var logger = new RecordingLogger<EmoteService>();
        await using var reportDb = fixture.CreateDbContext([hook]);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => StartReport(reportDb, set, logger));

        Assert.Equal(PostgresErrorCodes.DeadlockDetected, FindPostgresException(thrown).SqlState);
        Assert.Single(ReportLines(logger, DeadlockLine));
        Assert.Equal(2, hook.Calls);
        await AssertNothingWrittenAsync(setup, channel, set);
    }

    // (8) Limit: a deleted affected channel and a deadlock share the one retry, in either
    // order. The channel is really deleted (committed between the report's reads and its channel lock);
    // the deadlock is injected.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VanishedChannelAndDeadlock_ShareTheOneRetry(bool vanishedFirst)
    {
        var tag = $"zeightvd{(vanishedFirst ? "v" : "d")}";
        var set = SetOf(tag);
        await using var setup = fixture.CreateDbContext();
        var k1 = await SeedChannelAsync(setup, $"inset_{tag}_a", set);
        var k2 = await SeedChannelAsync(setup, $"inset_{tag}_b", set);
        await SeedActiveEmoteAsync(setup, k1, Reported);
        await SeedActiveEmoteAsync(setup, k2, Reported);

        // The hit read is the last read before the channel lock (plan 376 R6: rows are read behind the
        // locks now): deleting K2 right after it makes the channel lock of that attempt miss K2. The save
        // is reached only by an attempt that passed it.
        var rowsRead = new CommandHook(
            text => text.Contains("FROM \"Channels\"", StringComparison.Ordinal) && text.Contains("WHERE c.\"IsBotActive\" AND", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence == (vanishedFirst ? 1 : 2))
                {
                    await using var other = fixture.CreateDbContext();
                    await other.Channels.Where(c => c.Id == k2.Id).ExecuteDeleteAsync();
                }
            });
        var save = new SaveHook((_, call) => call == 1 ? throw Deadlock() : Task.CompletedTask);
        var logger = new RecordingLogger<EmoteService>();
        await using var reportDb = fixture.CreateDbContext([rowsRead, save]);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => StartReport(reportDb, set, logger));

        if (vanishedFirst)
        {
            Assert.Equal(PostgresErrorCodes.DeadlockDetected, FindPostgresException(thrown).SqlState);
            Assert.Single(ReportLines(logger, VanishedLine));
            Assert.Empty(ReportLines(logger, DeadlockLine));
        }
        else
        {
            Assert.IsAssignableFrom<InvalidOperationException>(thrown);
            Assert.Single(ReportLines(logger, DeadlockLine));
            Assert.Empty(ReportLines(logger, VanishedLine));
        }

        Assert.Equal(1, save.Calls);
        await AssertNothingWrittenAsync(setup, k1, set);
    }

    // E25, statement by statement: the affected channel rows, then the changed emote rows, then the
    // leave upsert. Both directions; a restore upserts no leave row.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockStatements_ChannelRowsThenEmoteRowsThenLeaveUpsert(bool delete)
    {
        var tag = $"orderon{(delete ? "del" : "res")}";
        var set = SetOf(tag);
        await using var setup = fixture.CreateDbContext();
        var k1 = await SeedChannelAsync(setup, $"inset_{tag}_a", set);
        var k2 = await SeedChannelAsync(setup, $"inset_{tag}_b", set);
        // A hit without a found row: not affected, so not locked (E25), and counted 0.
        await SeedChannelAsync(setup, $"inset_{tag}_c", set);
        await SeedActiveEmoteAsync(setup, k1, Reported, archived: !delete);
        await SeedActiveEmoteAsync(setup, k2, Reported, archived: !delete);
        var hook = new CommandHook(_ => false, _ => Task.CompletedTask);
        await using var reportDb = fixture.CreateDbContext([hook]);
        var service = CreateReportService(reportDb, new RecordingLogger<EmoteService>());

        var result = delete
            ? (await service.MarkDeletedInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", [Reported], null, Actor)).Channels
            : (await service.MarkRestoredInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", [Reported], null, Actor)).Channels;

        Assert.Equal([1, 1, 0], result.Select(c => c.Count));
        Assert.Equal(delete, (await EmoteAsync(setup, k1, Reported)).IsArchived);
        Assert.Equal(delete ? 1 : 0, await LeaveRowsAsync(setup, k1, set));
        var locks = hook.Count(t => t.Contains("FOR NO KEY UPDATE", StringComparison.Ordinal));
        var order = Positions(hook, ChannelLock, ReportEmoteLock, LeaveUpsert, "UPDATE \"Emotes\"");
        Assert.Equal(2, locks);
        Assert.Equal(delete ? [0, 1, 2, 3] : [0, 1, -1, 2], order);
    }

    // Plan 376 5.5: a report with entries against the sync of the same emote, in both
    // start orders. The emote has two slots; the report deletes hob, 7TV lists only ome. Whichever side
    // comes first, the second waits at its channel lock, neither is a deadlock victim, and the end state
    // is the serial one: hob archived, ome and the emote active, no leave (the last slot stayed).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntriesReportAndSync_TheSecondWaitsAtTheChannelLock_NoDeadlock(bool reportFirst)
    {
        var tag = reportFirst ? "slotsrf" : "slotssf";
        var set = SetOf(tag);
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"inset_{tag}", set);
        var emote = await SeedEmoteAsync(setup, channel, Reported, "ome", firstSeenAt: T1);
        var ome = await SeedSlotAsync(setup, emote, "ome", T1);
        var hob = await SeedSlotAsync(setup, emote, "hob", T2);

        var reportTag = $"inset_{tag}_report";
        var syncTag = $"inset_{tag}_sync";
        var reportLogger = new RecordingLogger<EmoteService>();
        var syncLogger = new RecordingLogger<SevenTvSyncService>();
        Task<SyncDeletedInSetResultDto>? report = null;
        Task<SevenTvSyncResult?>? sync = null;
        var waitingQuery = string.Empty;
        Task<SyncDeletedInSetResultDto> StartEntriesReport(AppDbContext db) =>
            CreateReportService(db, reportLogger).MarkDeletedInSetAsync(
                set, "owner-7tv", "owner", "owner-tw-none", [Reported], null, Actor, [new SyncInSetEntryDto(Reported, "hob", hob.Id, T2)]);
        Task<SevenTvSyncResult?> StartOmeOnlySync(AppDbContext db) =>
            CreateService(db, ApiReturning(channel, set, Live(Reported, "ome", T1), Live(Newcomer, "Newcomer", T1)), logger: syncLogger)
                .SyncChannelAsync(channel.ChannelName);

        AppDbContext reportDb;
        AppDbContext syncDb;
        if (reportFirst)
        {
            syncDb = fixture.CreateTaggedDbContext(syncTag);
            var hook = new CommandHook(
                text => text.Contains(ReportEmoteLock, StringComparison.Ordinal),
                async _ =>
                {
                    sync = StartOmeOnlySync(syncDb);
                    await fixture.WaitUntilBlockedOnLockAsync(syncTag, sync);
                    waitingQuery = await fixture.WaitingQueryAsync(syncTag);
                });
            reportDb = fixture.CreateTaggedDbContext(reportTag, [hook]);
            report = StartEntriesReport(reportDb);
        }
        else
        {
            reportDb = fixture.CreateTaggedDbContext(reportTag);
            var hook = new CommandHook(
                text => text.Contains(SyncEmoteLock, StringComparison.Ordinal),
                async occurrence =>
                {
                    if (occurrence != 1)
                    {
                        return;
                    }

                    report = StartEntriesReport(reportDb);
                    await fixture.WaitUntilBlockedOnLockAsync(reportTag, report);
                    waitingQuery = await fixture.WaitingQueryAsync(reportTag);
                });
            syncDb = fixture.CreateTaggedDbContext(syncTag, [hook]);
            sync = StartOmeOnlySync(syncDb);
        }

        await using (reportDb)
        await using (syncDb)
        {
            await SettleAsync(() => reportFirst ? report : sync, () => reportFirst ? sync : report);
            var reported = await report!.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(await sync!.WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Contains(ChannelLock, waitingQuery, StringComparison.Ordinal);
            Assert.Null(reported.Rejection);
            Assert.Equal((1, reportFirst ? 1 : 0), (Assert.Single(reported.Channels).Count, Assert.Single(reported.Channels).NewlyChangedCount));
        }

        Assert.Empty(ReportLines(reportLogger, DeadlockLine));
        Assert.Empty(Lines(syncLogger, DeadlockLine));
        Assert.True((await SlotAsync(setup, hob.Id)).IsArchived);
        Assert.False((await SlotAsync(setup, ome.Id)).IsArchived);
        Assert.False((await EmoteAsync(setup, channel, Reported)).IsArchived);
        Assert.False((await EmoteAsync(setup, channel, Newcomer)).IsArchived);
        Assert.Equal(0, await LeaveRowsAsync(setup, channel, set));
    }

    // Plan 376 5.5, Z6 with entries: the report with a slot entry of K2 (its origin) against the admin
    // purge of K2. Report first: the purge's DELETE waits at K2's channel row, both channels are written
    // (K2 as origin, K1 by date), then K2 goes. Purge first: the report waits at its channel lock, finds
    // K2 gone, retries once — now K2's slot id is unknown, so the entry fans out to K1 by date.
    [Theory]
    [InlineData("reportfirst")]
    [InlineData("purgefirst")]
    public async Task EntriesReportAndAdminPurge_NeverDeadlock(string variant)
    {
        var tag = $"slotszsix{variant[0]}";
        var set = SetOf(tag);
        var stem = Guid.NewGuid().ToString();
        await using var setup = fixture.CreateDbContext();
        var k1 = await SeedChannelAsync(setup, $"inset_{tag}_a", set, id: "f" + stem[1..]);
        var k2 = await SeedChannelAsync(setup, $"inset_{tag}_b", set, id: "0" + stem[1..]);
        var k1Slot = await SeedSlotAsync(setup, await SeedEmoteAsync(setup, k1, Reported, "aaa", firstSeenAt: T1), "aaa", T1);
        var k2Slot = await SeedSlotAsync(setup, await SeedEmoteAsync(setup, k2, Reported, "aaa", firstSeenAt: T1), "aaa", T1);
        IReadOnlyList<SyncInSetEntryDto> entries = [new SyncInSetEntryDto(Reported, "aaa", k2Slot.Id, T1)];

        var publisher = Substitute.For<IRedisPublisher>();
        var reportLogger = new RecordingLogger<EmoteService>();
        const string reportTag = "inset_slotszsix_report";
        const string purgeTag = "inset_slotszsix_purge";
        SyncDeletedInSetResultDto reported;
        if (variant == "reportfirst")
        {
            await using var purgeDb = fixture.CreateTaggedDbContext(purgeTag);
            Task<bool>? purge = null;
            var purgeWaitedOn = string.Empty;
            var hook = new CommandHook(
                text => text.Contains(ReportEmoteLock, StringComparison.Ordinal),
                async _ =>
                {
                    purge = CreateChannelService(purgeDb, publisher).PurgeAsync(k2.ChannelName, Actor);
                    await fixture.WaitUntilBlockedOnLockAsync(purgeTag, purge);
                    purgeWaitedOn = await fixture.WaitingQueryAsync(purgeTag);
                });
            await using var reportDb = fixture.CreateTaggedDbContext(reportTag, [hook]);
            var report = CreateReportService(reportDb, reportLogger)
                .MarkDeletedInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", [Reported], null, Actor, entries);
            await SettleAsync(() => report, () => purge);
            reported = await report.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(await purge!.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Contains("DELETE FROM \"Channels\"", purgeWaitedOn, StringComparison.Ordinal);
            Assert.Equal([(k1.ChannelName, 1), (k2.ChannelName, 1)], reported.Channels.Select(c => (c.ChannelName, c.NewlyChangedCount)));
            Assert.Empty(ReportLines(reportLogger, VanishedLine));
        }
        else
        {
            await using var purgeDb = fixture.CreateDbContext();
            await using var purgeTx = await purgeDb.Database.BeginTransactionAsync();
            Assert.True(await CreateChannelService(purgeDb, publisher).PurgeAsync(k2.ChannelName, Actor));

            await using var reportDb = fixture.CreateTaggedDbContext(reportTag);
            var report = CreateReportService(reportDb, reportLogger)
                .MarkDeletedInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", [Reported], null, Actor, entries);
            await fixture.WaitUntilBlockedOnLockAsync(reportTag, report);
            Assert.Contains(ChannelLock, await fixture.WaitingQueryAsync(reportTag), StringComparison.Ordinal);
            await purgeTx.CommitAsync();
            reported = await report.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Single(ReportLines(reportLogger, VanishedLine));
            Assert.Equal([(k1.ChannelName, 1)], reported.Channels.Select(c => (c.ChannelName, c.NewlyChangedCount)));
        }

        Assert.Null(reported.Rejection);
        Assert.Empty(ReportLines(reportLogger, DeadlockLine));
        Assert.True((await SlotAsync(setup, k1Slot.Id)).IsArchived);
        Assert.True((await EmoteAsync(setup, k1, Reported)).IsArchived);
        Assert.Equal(1, await LeaveRowsAsync(setup, k1, set));
        await AssertPurgedAsync(setup, k2);
    }

    // Plan 376 R6: the report reads its hits again behind the channel lock. A channel switched to another
    // set, or deactivated, between the unlocked hit read and the lock is no hit any more — nothing of it
    // is written, and the report ends as a paper entry. Before child 6 such a channel was still written.
    [Theory]
    [InlineData("switched")]
    [InlineData("deactivated")]
    public async Task ChannelChangedBetweenHitReadAndLock_IsNoHit_AndNothingIsWritten(string change)
    {
        var tag = $"slotsrsix{change[0]}";
        var set = SetOf(tag);
        await using var setup = fixture.CreateDbContext();
        var channel = await SeedChannelAsync(setup, $"inset_{tag}", set);
        var slot = await SeedSlotAsync(setup, await SeedEmoteAsync(setup, channel, Reported, "aaa", firstSeenAt: T1), "aaa", T1);
        var hitRead = new CommandHook(
            text => text.Contains("FROM \"Channels\"", StringComparison.Ordinal) && text.Contains("WHERE c.\"IsBotActive\" AND", StringComparison.Ordinal),
            async occurrence =>
            {
                if (occurrence != 1)
                {
                    return;
                }

                await using var other = fixture.CreateDbContext();
                await (change == "switched"
                    ? other.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.ActiveEmoteSetId, SetOf(tag + "x")))
                    : other.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.IsBotActive, false)));
            });
        await using var reportDb = fixture.CreateDbContext([hitRead]);

        var result = await CreateReportService(reportDb, new RecordingLogger<EmoteService>())
            .MarkDeletedInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", [Reported], null, Actor);

        Assert.Empty(result.Channels);
        Assert.False((await EmoteAsync(setup, channel, Reported)).IsArchived);
        Assert.False((await SlotAsync(setup, slot.Id)).IsArchived);
        Assert.Equal(0, await LeaveRowsAsync(setup, channel, set));
        Assert.Equal([null], await AuditChannelsAsync(setup, set));
    }

    // Lets every side finish, whatever happened to the others, before the test leaves the scope of
    // their contexts: disposing a context under a running command would hang a failing test instead of
    // failing it. A side is read lazily, because the second one is started inside the first one's hook.
    private static async Task SettleAsync(params Func<Task?>[] sides)
    {
        foreach (var side in sides)
        {
            if (side() is { } task)
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
            }
        }
    }

    private static PostgresException Deadlock() =>
        new("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);

    private static string SetOf(string tag) => ("01INSET" + tag.ToUpperInvariant()).PadRight(26, '0');

    private static Task<SyncDeletedInSetResultDto> StartReport(
        AppDbContext db, string set, RecordingLogger<EmoteService> logger) =>
        CreateReportService(db, logger)
            .MarkDeletedInSetAsync(set, "owner-7tv", "owner", "owner-tw-none", [Reported], null, Actor);

    private static Task<SevenTvSyncResult?> StartSync(
        AppDbContext db, Channel channel, string set, RecordingLogger<SevenTvSyncService> logger) =>
        CreateService(db, ApiReturning(channel, set, Live(Newcomer, "Newcomer", T1)), logger: logger)
            .SyncChannelAsync(channel.ChannelName);

    // The EventAPI dispatch of the mass delete: REMOVE of the reported emote, plus a push so the
    // dispatch is no implausible wipe of the one-emote set.
    private static Task<SevenTvDeltaResult> StartDelta(
        AppDbContext db, Channel channel, string set, RecordingLogger<SevenTvSyncService> logger) =>
        CreateService(db, ApiReturning(channel, set), logger: logger)
            .ApplyEmoteSetUpdateAsync(channel.ChannelName, set, new SevenTvEmoteSetDelta([Live(Newcomer, "Newcomer", null)], [], [new SevenTvPulledEntry(Reported)]));

    private static EmoteService CreateReportService(AppDbContext db, RecordingLogger<EmoteService> logger) =>
        new(db, logger, Substitute.For<IExcludedChannelFilter>(), new BroadcasterChannelLockService(db));

    private static ChannelService CreateChannelService(AppDbContext db, IRedisPublisher publisher) =>
        new(
            db,
            publisher,
            Substitute.For<IChannelIdentityService>(),
            Substitute.For<IChannelEmoteSetObservationService>(),
            new BroadcasterChannelLockService(db),
            new ChannelCapacityOptions { MaxActiveChannels = int.MaxValue },
            Substitute.For<IExcludedChannelFilter>(),
            NullLogger<ChannelService>.Instance);

    private static async Task<Channel> SeedChannelAsync(AppDbContext db, string name, string set, string? id = null)
    {
        var channel = new Channel { ChannelName = name, TwitchChannelId = $"tw_{name}", ActiveEmoteSetId = set, IsBotActive = true };
        if (id is not null)
        {
            channel.Id = id;
        }

        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    // An active row that entered the set long ago, so both the sync's and the report's leave are credible.
    private static async Task SeedActiveEmoteAsync(AppDbContext db, Channel channel, string sevenTvId, bool archived = false)
    {
        db.Emotes.Add(new Emote
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = sevenTvId,
            Name = sevenTvId,
            ImageUrl = Url(sevenTvId),
            IsArchived = archived,
            ArchivedAt = archived ? DateTime.UtcNow.AddDays(-3) : null,
            FirstSeenAt = T0,
            LastSyncedAt = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();
    }

    // Report and sync one after the other would leave: the reported emote archived with exactly one
    // leave row, the newcomer active, and exactly one channel entry of the report.
    private static async Task AssertSerialEndStateAsync(AppDbContext db, Channel channel, string set, int newcomerSlots)
    {
        Assert.True((await EmoteAsync(db, channel, Reported)).IsArchived);
        var newcomer = await EmoteAsync(db, channel, Newcomer);
        Assert.False(newcomer.IsArchived);
        Assert.Equal(newcomerSlots, (await SlotsAsync(db, newcomer.Id)).Count);
        Assert.Equal(1, await LeaveRowsAsync(db, channel, set));
        Assert.Equal([channel.ChannelName], await AuditChannelsAsync(db, set));
    }

    private static async Task AssertNothingWrittenAsync(AppDbContext db, Channel channel, string set)
    {
        Assert.False((await EmoteAsync(db, channel, Reported)).IsArchived);
        Assert.Equal(0, await LeaveRowsAsync(db, channel, set));
        Assert.Empty(await AuditChannelsAsync(db, set));
    }

    private static async Task AssertPurgedAsync(AppDbContext db, Channel channel)
    {
        Assert.False(await db.Channels.AnyAsync(c => c.Id == channel.Id));
        Assert.False(await db.Emotes.AnyAsync(e => e.ChannelId == channel.Id));
        Assert.False(await db.EmoteSetLeaveObservations.AnyAsync(o => o.ChannelId == channel.Id));
    }

    private static Task<int> LeaveRowsAsync(AppDbContext db, Channel channel, string set) =>
        db.EmoteSetLeaveObservations.AsNoTracking().CountAsync(o => o.ChannelId == channel.Id && o.SevenTvEmoteSetId == set);

    private static async Task<List<string?>> AuditChannelsAsync(AppDbContext db, string set)
    {
        var names = await db.AuditLogEntries.AsNoTracking()
            .Where(a => a.TargetType == "emoteSet" && a.TargetId == set)
            .Select(a => a.ChannelName)
            .ToListAsync();
        return [.. names.Order(StringComparer.Ordinal)];
    }

    // The rank of the first command containing each fragment among those found, -1 when none ran.
    private static int[] Positions(CommandHook hook, params string[] fragments)
    {
        var firstIndex = fragments.Select(f => hook.IndexOf(t => t.Contains(f, StringComparison.Ordinal))).ToArray();
        var ranked = firstIndex.Where(i => i >= 0).Order().ToList();
        return [.. firstIndex.Select(i => i < 0 ? -1 : ranked.IndexOf(i))];
    }

    private static IReadOnlyList<string> ReportLines(RecordingLogger<EmoteService> logger, string fragment) =>
        [.. logger.Entries.Where(e => e.Message.Contains(fragment, StringComparison.Ordinal)).Select(e => e.Message)];

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
}
