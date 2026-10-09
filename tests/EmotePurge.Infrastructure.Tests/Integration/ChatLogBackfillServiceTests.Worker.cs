using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// The worker side of IChatLogBackfillService (spec section 3, second region): block replace, the
// counter-carrying transitions, the claim and the loop lock.
public partial class ChatLogBackfillServiceTests
{
    private static readonly DateOnly BlockFrom = new(2026, 6, 1);
    private static readonly DateOnly BlockTo = new(2026, 6, 8);

    // ---------------------------------------------------------------- ReplaceBlockAsync (child 3 AC 5, AC 6; EPIC AC 4, AC 5)

    [Fact]
    public async Task ReplaceBlock_OnARunningRun_ReplacesTheChannelsImportedDays_AndAdvancesTheRun()
    {
        var channel = await SeedChannelAsync("bfreplace", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var other = await SeedChannelAsync("bfreplaceother");
        var e1 = await SeedEmoteAsync(channel, "e1", "E1");
        var e2 = await SeedEmoteAsync(channel, "e2", "E2", archived: true);
        var e3 = await SeedEmoteAsync(channel, "e3", "E3");
        var foreignEmote = await SeedEmoteAsync(other, "o1", "O1");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 3, pauseCount: 2, blockAttempts: 1, windowFrom: BlockFrom);
        await using (var db = CreateDbContext())
        {
            await db.ChatLogBackfillRuns.Where(r => r.Id == run.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.BytesReceived, 100L).SetProperty(r => r.MessagesRead, 10L));
            db.UsageStats.AddRange(
                Imported(e1, SetC, new DateOnly(2026, 6, 4), 9),            // in the block, another set: replaced
                Imported(e2, SetC, new DateOnly(2026, 6, 12), 5),           // next block: kept
                Imported(foreignEmote, SetC, new DateOnly(2026, 6, 4), 7),  // another channel: kept
                new UsageStat { EmoteId = e1.Id, EmoteSetId = ActiveSetId, Date = new DateOnly(2026, 7, 2), UseCount = 11 }); // live
            await db.SaveChangesAsync();
        }

        await AddCoverageAsync(channel, new DateOnly(2026, 6, 4), new DateOnly(2026, 6, 5), SetC, runId: null, host: "logs.zonian.dev");
        var liveBefore = await LiveFingerprintAsync(channel);
        var logger = new RecordingLogger<ChatLogBackfillService>();

        ChatLogBackfillBlockResult result;
        await using (var db = CreateDbContext())
        {
            result = await CreateService(db, logger: logger).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo,
                [
                    new ChatLogBackfillAggregate(e1.Id, new DateOnly(2026, 6, 1), 2, 1, 0),
                    new ChatLogBackfillAggregate(e2.Id, new DateOnly(2026, 6, 7), 0, 0, 4),
                    new ChatLogBackfillAggregate(e3.Id, new DateOnly(2026, 6, 3), 0, 0, 0),         // all zero: not written
                    new ChatLogBackfillAggregate("hard-deleted-emote", new DateOnly(2026, 6, 3), 1, 0, 0), // dropped
                ],
                bytes: 1000, messages: 50, isLastBlock: false);
        }

        Assert.Equal(new ChatLogBackfillBlockResult.Committed(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Running, 0, 0, null)), result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1 aggregates", StringComparison.Ordinal));

        await using var verify = CreateDbContext();
        var imported = await verify.UsageStats.AsNoTracking()
            .Where(u => u.Source == UsageStatSource.ChatLogArchive && u.Emote.ChannelId == channel.Id)
            .OrderBy(u => u.Date)
            .Select(u => new { u.EmoteId, u.EmoteSetId, u.Date, u.UseCount, u.BotUseCount, u.SharedChatUseCount })
            .ToListAsync();
        Assert.Equal(
            [
                new { EmoteId = e1.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 6, 1), UseCount = 2, BotUseCount = 1, SharedChatUseCount = 0 },
                new { EmoteId = e2.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 6, 7), UseCount = 0, BotUseCount = 0, SharedChatUseCount = 4 },
                new { EmoteId = e2.Id, EmoteSetId = SetC, Date = new DateOnly(2026, 6, 12), UseCount = 5, BotUseCount = 0, SharedChatUseCount = 0 },
            ],
            imported);
        Assert.Equal(7, (await verify.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == foreignEmote.Id)).UseCount);
        Assert.Equal(liveBefore, await LiveFingerprintAsync(channel));

        var coverage = await verify.ChatLogBackfillCoverage.AsNoTracking().Where(d => d.ChannelId == channel.Id).OrderBy(d => d.Day).ToListAsync();
        Assert.Equal(7, coverage.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => BlockFrom.AddDays(i)), coverage.Select(d => d.Day));
        Assert.All(coverage, d => Assert.Equal((SetB, "logs.cyex.app", (long?)run.Id, NowUtc), (d.EmoteSetId, d.ArchiveHost, d.RunId, d.CompletedAtUtc)));

        var after = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        Assert.Equal(
            (ChatLogBackfillRunStatus.Running, 1, 0, 0, 1100L, 60L, (DateTime?)null),
            (after.Status, after.WeeksDone, after.PauseCount, after.BlockAttempts, after.BytesReceived, after.MessagesRead, after.FinishedAtUtc));
    }

    // The last block completes the run; EPIC AC 5: a second run over the same days (another set) replaces
    // the first run's rows and coverage — no day carries two sets, no count doubles.
    [Fact]
    public async Task ReplaceBlock_LastBlockCompletes_AndARerunReplacesInsteadOfAdding()
    {
        var channel = await SeedChannelAsync("bfrerun", createdAt: new DateTime(2026, 6, 8, 0, 0, 0, DateTimeKind.Utc));
        var e1 = await SeedEmoteAsync(channel, "e1", "E1");
        var first = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, emoteSetId: SetB, weeksTotal: 1, windowFrom: BlockFrom);
        var liveBefore = await LiveFingerprintAsync(channel);

        await using (var db = CreateDbContext())
        {
            var committed = await CreateService(db).ReplaceBlockAsync(
                first.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(e1.Id, BlockFrom, 4, 0, 0)], 10, 1, isLastBlock: true);
            Assert.Equal(new ChatLogBackfillBlockResult.Committed(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Completed, 0, 0, null)), committed);
        }

        var second = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, emoteSetId: SetC, weeksTotal: 1, windowFrom: BlockFrom);
        await using (var db = CreateDbContext())
        {
            await CreateService(db).ReplaceBlockAsync(
                second.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(e1.Id, BlockFrom, 4, 0, 0)], 10, 1, isLastBlock: true);
        }

        await using var verify = CreateDbContext();
        var row = await verify.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == e1.Id && u.Source == UsageStatSource.ChatLogArchive);
        Assert.Equal((SetC, 4), (row.EmoteSetId, row.UseCount));
        Assert.All(await verify.ChatLogBackfillCoverage.AsNoTracking().Where(d => d.ChannelId == channel.Id).ToListAsync(),
            d => Assert.Equal((SetC, (long?)second.Id), (d.EmoteSetId, d.RunId)));
        var firstAfter = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == first.Id);
        Assert.Equal((ChatLogBackfillRunStatus.Completed, 1, (DateTime?)NowUtc), (firstAfter.Status, firstAfter.WeeksDone, firstAfter.FinishedAtUtc));
        Assert.Equal(liveBefore, await LiveFingerprintAsync(channel));
    }

    // Child 3 AC 5: cancelled between the read and the commit — the progress update finds no running row
    // and the whole block rolls back: the old imported rows stay, no coverage day is written.
    [Fact]
    public async Task ReplaceBlock_OnACancelledRun_IsRunNotActive_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfreplacecancel", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var e1 = await SeedEmoteAsync(channel, "e1", "E1");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Cancelled, windowFrom: BlockFrom);
        await using (var db = CreateDbContext())
        {
            db.UsageStats.Add(Imported(e1, SetC, new DateOnly(2026, 6, 2), 9));
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.RunNotActive>(await CreateService(db).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(e1.Id, BlockFrom, 1, 0, 0)], 10, 1, isLastBlock: false));
        }

        await using var verify = CreateDbContext();
        var row = await verify.UsageStats.AsNoTracking().SingleAsync(u => u.EmoteId == e1.Id);
        Assert.Equal((SetC, new DateOnly(2026, 6, 2), 9), (row.EmoteSetId, row.Date, row.UseCount));
        Assert.False(await verify.ChatLogBackfillCoverage.AnyAsync(d => d.ChannelId == channel.Id));
        Assert.Equal(0, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).WeeksDone);
    }

    // Child 3 AC 6 / D3: a live row on an imported cell breaks the invariant — nothing is written.
    [Fact]
    public async Task ReplaceBlock_WithALiveRowOnAnImportedCell_IsLiveRowConflict_AndWritesNothing()
    {
        var channel = await SeedChannelAsync("bfreplacelive", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var e1 = await SeedEmoteAsync(channel, "e1", "E1");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, windowFrom: BlockFrom);
        await using (var db = CreateDbContext())
        {
            db.UsageStats.AddRange(
                new UsageStat { EmoteId = e1.Id, EmoteSetId = SetB, Date = new DateOnly(2026, 6, 3), UseCount = 1 },
                Imported(e1, SetC, new DateOnly(2026, 6, 2), 9));
            await db.SaveChangesAsync();
        }

        var before = await UsageFingerprintAsync(channel);
        await using (var db = CreateDbContext())
        {
            Assert.IsType<ChatLogBackfillBlockResult.LiveRowConflict>(await CreateService(db).ReplaceBlockAsync(
                run.Id, BlockFrom, BlockTo, [new ChatLogBackfillAggregate(e1.Id, new DateOnly(2026, 6, 3), 2, 0, 0)], 10, 1, isLastBlock: false));
        }

        Assert.Equal(before, await UsageFingerprintAsync(channel));
        await using var verify = CreateDbContext();
        Assert.False(await verify.ChatLogBackfillCoverage.AnyAsync(d => d.ChannelId == channel.Id));
        var after = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        Assert.Equal((ChatLogBackfillRunStatus.Running, 0, 0L), (after.Status, after.WeeksDone, after.BytesReceived));
    }

    [Fact]
    public async Task ReplaceBlock_ForAPurgedChannel_IsChannelGone()
    {
        var channel = await SeedChannelAsync("bfreplacegone");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, windowFrom: BlockFrom);
        await using (var db = CreateDbContext())
        {
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
        }

        await using var service = CreateDbContext();
        Assert.IsType<ChatLogBackfillBlockResult.ChannelGone>(
            await CreateService(service).ReplaceBlockAsync(run.Id, BlockFrom, BlockTo, [], 0, 0, isLastBlock: false));
    }

    [Fact]
    public async Task ReplaceBlock_RefusesAggregatesOutsideTheBlockOrTwicePerCell()
    {
        await using var db = CreateDbContext();
        var service = CreateService(db);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ReplaceBlockAsync(1, BlockFrom, BlockTo, [new ChatLogBackfillAggregate("e", BlockTo, 1, 0, 0)], 0, 0, false));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReplaceBlockAsync(
            1, BlockFrom, BlockTo, [new ChatLogBackfillAggregate("e", BlockFrom, 1, 0, 0), new ChatLogBackfillAggregate("e", BlockFrom, 2, 0, 0)], 0, 0, false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReplaceBlockAsync(1, BlockFrom, BlockFrom, [], 0, 0, false));
    }

    // ---------------------------------------------------------------- Pause, attempts, failure (EPIC AC 28, AC 30)

    // AC 28: the delay follows the persisted PauseCount across claims and a worker restart — the 4th
    // pause is 480 s — and a committed block resets it, so the next 429 pauses 60 s again.
    [Fact]
    public async Task Pause_DelayFollowsThePersistedCount_AcrossRestarts_AndACommitResetsIt()
    {
        var channel = await SeedChannelAsync("bfpause", createdAt: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksTotal: 3, windowFrom: BlockFrom);
        var now = NowUtc;

        foreach (var (expectedCount, seconds) in new[] { (1, 60), (2, 120), (3, 240) })
        {
            await using var db = CreateDbContext();
            var service = CreateService(db);
            var paused = await service.PauseAsync(run.Id, null, now);
            Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Paused, expectedCount, 0, now.AddSeconds(seconds)), paused);
            now = now.AddSeconds(seconds);
            Assert.Null(await service.ClaimNextAsync(now.AddSeconds(-1)));
            Assert.Equal(expectedCount, (await service.ClaimNextAsync(now))!.PauseCount);
        }

        // A restart: the running row goes back to queued, a new service instance claims it again.
        await using (var db = CreateDbContext())
        {
            Assert.Equal(1, await CreateService(db).ResetInterruptedRunsAsync());
        }

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            var claim = await service.ClaimNextAsync(now);
            Assert.Equal((run.Id, 3, 0), (claim!.RunId, claim.PauseCount, claim.WeeksDone));
            Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Paused, 4, 0, now.AddSeconds(480)), await service.PauseAsync(run.Id, null, now));
            Assert.Equal(now.AddSeconds(480), await service.GetCooldownUntilAsync());
            now = now.AddSeconds(480);
            await service.ClaimNextAsync(now);

            var committed = await service.ReplaceBlockAsync(run.Id, BlockFrom, BlockTo, [], 0, 0, isLastBlock: false);
            Assert.Equal(new ChatLogBackfillBlockResult.Committed(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Running, 0, 0, null)), committed);
            Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Paused, 1, 0, now.AddSeconds(60)), await service.PauseAsync(run.Id, null, now));
        }
    }

    // Retry-After wins over the back-off, both are capped; the 11th consecutive 429 fails the run.
    [Fact]
    public async Task Pause_HonoursRetryAfter_CapsIt_AndFailsTheEleventh()
    {
        var channel = await SeedChannelAsync("bfpausecap");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, pauseCount: 9);

        await using var db = CreateDbContext();
        var service = CreateService(db, backfillOptions: new ChatLogBackfillOptions());
        Assert.Equal(
            new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Paused, 10, 0, NowUtc.AddSeconds(900)),
            await service.PauseAsync(run.Id, TimeSpan.FromSeconds(2000), NowUtc));
        await service.ClaimNextAsync(NowUtc.AddSeconds(900));

        var failed = await service.PauseAsync(run.Id, TimeSpan.FromSeconds(30), NowUtc.AddSeconds(900));
        Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Failed, 10, 0, null), failed);

        await using var verify = CreateDbContext();
        var row = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        Assert.Equal(("rate_limited", (int?)429, (DateTime?)NowUtc.AddSeconds(900)), (row.ErrorCode, row.ErrorHttpStatus, row.FinishedAtUtc));
        // The archive asked to wait for that one too.
        Assert.Equal(NowUtc.AddSeconds(930), await CreateService(verify).GetCooldownUntilAsync());

        var shortRun = await SeedRunAsync(await SeedChannelAsync("bfpauseshort"), ChatLogBackfillRunStatus.Running);
        Assert.Equal(NowUtc.AddSeconds(45), (await CreateService(verify).PauseAsync(shortRun.Id, TimeSpan.FromSeconds(45), NowUtc)).PausedUntilUtc);
    }

    // EPIC AC 30, service side: the cooldown is written with the pause and outlives cancel, leave and
    // purge of the run that received the 429; only time clears it.
    [Fact]
    public async Task Cooldown_SurvivesCancelLeaveAndPurgeOfThePausedRun()
    {
        var channel = await SeedChannelAsync("bfcooldown");
        await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running);
        var until = NowUtc.AddSeconds(120);

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            var run = await db.ChatLogBackfillRuns.AsNoTracking().SingleAsync();
            Assert.Equal(until, (await service.PauseAsync(run.Id, TimeSpan.FromSeconds(120), NowUtc)).PausedUntilUtc);
            Assert.Equal(ChatLogBackfillCancelResult.Cancelled, await service.CancelAsync(channel.ChannelName, Actor));
            Assert.Equal(until, await service.GetCooldownUntilAsync());
        }

        await using (var db = CreateDbContext())
        {
            await SeedRunAsync(channel, ChatLogBackfillRunStatus.Paused, pausedUntilUtc: until);
            Assert.True(await CreateChannelService(db).LeaveAsync(channel.ChannelName, Actor));
            Assert.Equal(until, await CreateService(db).GetCooldownUntilAsync());
        }

        await using (var db = CreateDbContext())
        {
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
            Assert.Equal(until, await CreateService(db).GetCooldownUntilAsync());
        }

        // A 429 for a run cancelled meanwhile still extends the cooldown, never shortens it.
        var other = await SeedChannelAsync("bfcooldown2");
        var cancelled = await SeedRunAsync(other, ChatLogBackfillRunStatus.Cancelled);
        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.Equal(ChatLogBackfillRunStatus.Cancelled, (await service.PauseAsync(cancelled.Id, TimeSpan.FromSeconds(10), NowUtc)).Status);
            Assert.Equal(until, await service.GetCooldownUntilAsync());
            await service.PauseAsync(cancelled.Id, TimeSpan.FromSeconds(600), NowUtc);
            Assert.Equal(NowUtc.AddSeconds(600), await service.GetCooldownUntilAsync());
        }
    }

    // AC 28's second half: BlockAttempts survive a restart; every counter call reports the row's state and
    // changes nothing on a row that is no longer running; FailAsync books the failed attempt's bytes.
    [Fact]
    public async Task Attempts_SurviveARestart_AndCounterCallsRefuseANonRunningRow()
    {
        var channel = await SeedChannelAsync("bfattempts");
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running);

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Running, 0, 1, null), await service.RecordBlockAttemptAsync(run.Id, 500));
            await service.ResetInterruptedRunsAsync();
        }

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.Equal(1, (await service.ClaimNextAsync(NowUtc))!.BlockAttempts);
            Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Running, 0, 2, null), await service.RecordBlockAttemptAsync(run.Id, 300));
            await service.FailAsync(run.Id, "transport_failure", 503, 200);
            await service.FailAsync(run.Id, "worker_error", null, 999);
            Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Failed, 0, 2, null), await service.RecordBlockAttemptAsync(run.Id, 77));
            Assert.Equal(ChatLogBackfillRunStatus.Failed, (await service.PauseAsync(run.Id, null, NowUtc)).Status);
            Assert.Equal(new ChatLogBackfillTransition(ChatLogBackfillRunStatus.Cancelled, 0, 0, null), await service.RecordBlockAttemptAsync(424242, 1));
        }

        await using var verify = CreateDbContext();
        var row = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        Assert.Equal(
            (ChatLogBackfillRunStatus.Failed, "transport_failure", (int?)503, 1000L, (DateTime?)NowUtc, 2),
            (row.Status, row.ErrorCode, row.ErrorHttpStatus, row.BytesReceived, row.FinishedAtUtc, row.BlockAttempts));
    }

    // ---------------------------------------------------------------- Claim, reset, liveness

    // Strict FIFO (D31): a paused head that is not due blocks every younger run; when due it is resumed,
    // not overtaken. A queued head starts with StartedAtUtc stamped once.
    [Fact]
    public async Task Claim_IsStrictFifo_APausedHeadBlocksYoungerRunsUntilDue()
    {
        var first = await SeedChannelAsync("bfclaim1");
        var second = await SeedChannelAsync("bfclaim2");
        var head = await SeedRunAsync(first, ChatLogBackfillRunStatus.Paused, pausedUntilUtc: NowUtc.AddMinutes(2), weeksDone: 2);
        var younger = await SeedRunAsync(second, ChatLogBackfillRunStatus.Queued, emoteSetId: SetC);

        await using var db = CreateDbContext();
        var service = CreateService(db);
        Assert.Null(await service.ClaimNextAsync(NowUtc));

        var claim = await service.ClaimNextAsync(NowUtc.AddMinutes(2));
        Assert.Equal(
            new ChatLogBackfillClaim(head.Id, first.Id, first.ChannelName, first.TwitchChannelId!, head.WindowFrom, head.WindowTo, 2, 3, 0, 0, SetB),
            claim);
        Assert.True(await service.IsRunActiveAsync(head.Id));
        Assert.False(await service.IsRunActiveAsync(younger.Id));

        // The running head is resumed again (it is still the head); the younger run waits behind it.
        Assert.Equal(head.Id, (await service.ClaimNextAsync(NowUtc.AddMinutes(3)))!.RunId);

        await service.FailAsync(head.Id, "worker_error", null, 0);
        var next = await service.ClaimNextAsync(NowUtc.AddMinutes(4));
        Assert.Equal((younger.Id, SetC), (next!.RunId, next.EmoteSetId));
        await using var verify = CreateDbContext();
        Assert.Equal(NowUtc.AddMinutes(4), (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == younger.Id)).StartedAtUtc);
    }

    // The claim-time checks with this process's configuration (EPIC AC 23 claim half, AC 34): each failing
    // head is failed with its code and announced, and the claim moves on to the next head. The excluded
    // channel is never named in the log.
    [Fact]
    public async Task Claim_FailsHeadsThatNoLongerQualify_AndMovesOn()
    {
        var inactive = await SeedChannelAsync("bfclaiminactive", isBotActive: false);
        var idless = await SeedChannelAsync("bfclaimidless", twitchChannelId: null);
        var excluded = await SeedChannelAsync("bfclaimexcluded");
        var mismatch = await SeedChannelAsync("bfclaimmismatch");
        var fine = await SeedChannelAsync("bfclaimfine");
        var runs = new[]
        {
            await SeedRunAsync(inactive, ChatLogBackfillRunStatus.Queued),
            await SeedRunAsync(idless, ChatLogBackfillRunStatus.Queued),
            await SeedRunAsync(excluded, ChatLogBackfillRunStatus.Queued),
            await SeedRunAsync(mismatch, ChatLogBackfillRunStatus.Queued, archiveBaseUrl: "https://logs.zonian.dev/"),
        };
        var good = await SeedRunAsync(fine, ChatLogBackfillRunStatus.Queued, archiveBaseUrl: "https://LOGS.cyex.app");
        _excluded.IsExcluded(excluded.TwitchChannelId).Returns(true);
        var logger = new RecordingLogger<ChatLogBackfillService>();

        await using (var db = CreateDbContext())
        {
            var claim = await CreateService(db, logger: logger).ClaimNextAsync(NowUtc);
            Assert.Equal(good.Id, claim!.RunId);
        }

        await using var verify = CreateDbContext();
        var failed = await verify.ChatLogBackfillRuns.AsNoTracking().Where(r => r.Status == ChatLogBackfillRunStatus.Failed).OrderBy(r => r.Id).ToListAsync();
        Assert.Equal(runs.Select(r => r.Id), failed.Select(r => r.Id));
        Assert.Equal(["channel_not_active", "twitch_id_unknown", "channel_excluded", "archive_mismatch"], failed.Select(r => r.ErrorCode));
        Assert.All(failed, r => Assert.Equal(NowUtc, r.FinishedAtUtc));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("bfclaimexcluded", StringComparison.Ordinal)
            || e.Message.Contains(excluded.TwitchChannelId!, StringComparison.Ordinal));
        await _publisher.Received(4).PublishAsync(LiveEvents.Channel, Arg.Is<string>(m => m.Contains(LiveEvents.BackfillProgress)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_PutsOnlyRunningRowsBackIntoTheQueue_KeepingTheirProgress()
    {
        var channel = await SeedChannelAsync("bfreset");
        var other = await SeedChannelAsync("bfreset2");
        var running = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Running, weeksDone: 2);
        var paused = await SeedRunAsync(other, ChatLogBackfillRunStatus.Paused, pausedUntilUtc: NowUtc);

        await using var db = CreateDbContext();
        Assert.Equal(1, await CreateService(db).ResetInterruptedRunsAsync());

        var rows = await db.ChatLogBackfillRuns.AsNoTracking().ToDictionaryAsync(r => r.Id);
        Assert.Equal((ChatLogBackfillRunStatus.Queued, 2), (rows[running.Id].Status, rows[running.Id].WeeksDone));
        Assert.Equal(ChatLogBackfillRunStatus.Paused, rows[paused.Id].Status);
    }

    [Fact]
    public async Task Snapshot_OfAVanishedRun_IsEmpty()
    {
        await using var db = CreateDbContext();
        var snapshot = await CreateService(db).GetSnapshotAsync(987654);
        Assert.Equal((string.Empty, 0), (snapshot.EmoteSetId, snapshot.Emotes.Count));
    }

    // D46: one holder at a time; the lock dies with the holder's connection.
    [Fact]
    public async Task LoopLock_HasOneHolder_AndIsFreedWhenTheHolderGoesAway()
    {
        await using var dbA = CreateDbContext();
        await using var dbB = CreateDbContext();
        var holderA = CreateService(dbA);
        var holderB = CreateService(dbB);

        Assert.True(await holderA.TryAcquireLoopLockAsync());
        Assert.True(await holderA.TryAcquireLoopLockAsync());
        Assert.False(await holderB.TryAcquireLoopLockAsync());

        await holderA.DisposeAsync();
        Assert.True(await holderB.TryAcquireLoopLockAsync());
        await holderB.DisposeAsync();
    }

    // ---------------------------------------------------------------- Helpers

    private static UsageStat Imported(Emote emote, string emoteSetId, DateOnly date, int useCount) =>
        new() { EmoteId = emote.Id, EmoteSetId = emoteSetId, Date = date, UseCount = useCount, Source = UsageStatSource.ChatLogArchive };

    // EPIC AC 4: the channel's live rows, as one digest.
    private async Task<string?> LiveFingerprintAsync(Channel channel) => await UsageDigestAsync(channel, liveOnly: true);

    private async Task<string?> UsageFingerprintAsync(Channel channel) => await UsageDigestAsync(channel, liveOnly: false);

    private async Task<string?> UsageDigestAsync(Channel channel, bool liveOnly)
    {
        await using var db = CreateDbContext();
        return await db.Database.SqlQuery<string?>(
                $"""
                 SELECT md5(string_agg(concat_ws(':', u."Id", u."EmoteId", u."EmoteSetId", u."Date", u."UseCount", u."BotUseCount", u."SharedChatUseCount", u."Source"), ',' ORDER BY u."Id")) AS "Value"
                 FROM "UsageStats" u JOIN "Emotes" e ON e."Id" = u."EmoteId"
                 WHERE e."ChannelId" = {channel.Id} AND (NOT {liveOnly} OR u."Source" = 0)
                 """)
            .SingleAsync();
    }
}
