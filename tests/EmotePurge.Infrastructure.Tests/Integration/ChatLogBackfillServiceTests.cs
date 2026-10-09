using System.Text.Json;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Core.SevenTv;
using EmotePurge.Infrastructure.ChatLogArchive;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.SevenTv;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Chat-log backfill spec, child 3 (#349): IChatLogBackfillService against real Postgres.
//
// Its own database, not the collection's shared one: the worker side works on the global queue (the
// lowest-id active run of *every* channel), the single provider-state row and a global queue position,
// so rows other suites seed would change what is claimed and counted here. Created and migrated once,
// emptied before every test. The clock is fixed at a whole second, so stamped times read back exactly
// (Postgres keeps microseconds).
//
// This file: setup, helpers and the Api side (enqueue, cancel, status, coverage). The lock races live in
// ChatLogBackfillServiceTests.Races.cs, the worker side in ChatLogBackfillServiceTests.Worker.cs.
[Collection("Postgres")]
public partial class ChatLogBackfillServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "chatlog_backfill_service_tests";
    private const string ArchiveUrl = "https://logs.cyex.app/";

    // 26-character ULID-shaped set ids, like 7TV's.
    private const string ActiveSetId = "01J9BFACTIVESET00000000001";
    private const string SetB = "01J9BFCHOSENSETB0000000002";
    private const string SetC = "01J9BFCHOSENSETC0000000003";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NowUtc = Now.UtcDateTime;
    private static readonly DateOnly Today = DateOnly.FromDateTime(NowUtc);
    private static readonly AuditActor Actor = new("4242", "backfillmod");

    private readonly HandWoundTimeProvider _clock = new(Now);
    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();
    private readonly IExcludedChannelFilter _excluded = Substitute.For<IExcludedChannelFilter>();
    private readonly ITrackedEmoteSetMembershipService _membership = Substitute.For<ITrackedEmoteSetMembershipService>();
    private readonly IForeignEmoteSetService _foreign = Substitute.For<IForeignEmoteSetService>();

    public async Task InitializeAsync()
    {
        await DatabaseGate.WaitAsync();
        try
        {
            if (!_databaseReady)
            {
                await using (var admin = fixture.CreateDbContext())
                {
                    // A constant name, not user input; CREATE DATABASE cannot be parameterised.
#pragma warning disable EF1002
                    await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE {DatabaseName}");
#pragma warning restore EF1002
                }

                await using var db = CreateDbContext();
                await db.Database.MigrateAsync();
                _databaseReady = true;
            }
        }
        finally
        {
            DatabaseGate.Release();
        }

        await using var reset = CreateDbContext();
        await reset.Database.ExecuteSqlRawAsync(
            """
            TRUNCATE "Channels", "Users", "AuditLogEntries" RESTART IDENTITY CASCADE;
            UPDATE "ChatLogBackfillProviderState" SET "CooldownUntilUtc" = NULL, "LastRateLimitedAtUtc" = NULL;
            """);

        _membership.CheckAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TrackedEmoteSetMembership.Member);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- Enqueue (AC 2, AC 3, AC 1 pre-check)

    // Child 3 AC 3 (+ EPIC AC 31's second half): set B lists 5 members; 2 have rows (one active, one
    // archived), 3 have none. Five snapshot rows with B's aliases and entry days, exactly 3 placeholders,
    // CreatedRow only on those, the 2 existing rows byte-identical, the run row and its audit entry.
    [Fact]
    public async Task Enqueue_SnapshotsTheChosenSet_CreatesPlaceholdersForMissingMembers_AndLeavesExistingRowsAlone()
    {
        var channel = await SeedChannelAsync("bfenqueue");
        await SeedUserAsync();
        var active = await SeedEmoteAsync(channel, "e-active", "ActiveName");
        var archived = await SeedEmoteAsync(channel, "e-archived", "ArchivedName", archived: true, archivedAt: NowUtc.AddDays(-40));
        var before = await EmoteRowsJsonAsync(channel, "e-active", "e-archived");

        var addedN1 = new DateTime(2026, 5, 2, 18, 30, 0, DateTimeKind.Utc);
        var addedN2 = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        PreviewReturns(SetB, "Set B",
            Member("e-active", "BActive", new DateTime(2026, 4, 20, 9, 0, 0, DateTimeKind.Utc)),
            Member("e-archived", "BArchived", null),
            Member("n1", "BNew1", addedN1),
            Member("n2", "BNew2", addedN2),
            Member("n3", "BNew3", null));

        var result = await EnqueueAsync(channel, SetB, months: 6);

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, result.Status);
        var dto = result.Run!;
        Assert.Equal(("queued", 1, 5, SetB, "Set B"), (dto.Status, dto.QueuePosition, dto.EmoteCount, dto.EmoteSetId, dto.EmoteSetName));
        Assert.Equal((Today.AddMonths(-6), DateOnly.FromDateTime(channel.CreatedAt)), (dto.WindowFrom, dto.WindowTo));
        Assert.Equal(ChatLogBackfillWindow.WeeksFor(dto.WindowTo.DayNumber - dto.WindowFrom.DayNumber), dto.WeeksTotal);

        await using var verify = CreateDbContext();
        var run = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync();
        Assert.Equal((ArchiveUrl, Actor.TwitchUserId, Actor.Login, NowUtc, 6), (run.ArchiveBaseUrl, run.RequestedByTwitchUserId, run.RequestedByLogin, run.RequestedAtUtc, run.RequestedMonths));

        var emotes = await verify.Emotes.AsNoTracking().Where(e => e.ChannelId == channel.Id).ToDictionaryAsync(e => e.SevenTvEmoteId);
        Assert.Equal(5, emotes.Count);
        foreach (var (id, alias, addedAt) in new[] { ("n1", "BNew1", (DateTime?)addedN1), ("n2", "BNew2", addedN2), ("n3", "BNew3", null) })
        {
            var row = emotes[id];
            Assert.Equal((true, true, (DateTime?)null), (row.IsArchived, row.IsPlaceholder, row.ArchivedAt));
            Assert.Equal((addedAt, NowUtc, (DateTime?)NowUtc), (row.FirstSeenAt, row.LastSyncedAt, row.LastEnteredSetAtUtc));
            Assert.Equal((alias, ImageUrl(id)), (row.Name, row.ImageUrl));
        }

        Assert.Equal(before, await EmoteRowsJsonAsync(channel, "e-active", "e-archived"));

        var snapshot = await verify.ChatLogBackfillRunEmotes.AsNoTracking().Where(e => e.RunId == run.Id).ToDictionaryAsync(e => e.SevenTvEmoteId);
        Assert.Equal(
            new Dictionary<string, (string, string, DateOnly?, bool)>
            {
                ["e-active"] = (active.Id, "BActive", new DateOnly(2026, 4, 20), false),
                ["e-archived"] = (archived.Id, "BArchived", null, false),
                ["n1"] = (emotes["n1"].Id, "BNew1", new DateOnly(2026, 5, 2), true),
                ["n2"] = (emotes["n2"].Id, "BNew2", new DateOnly(2026, 6, 1), true),
                ["n3"] = (emotes["n3"].Id, "BNew3", null, true),
            },
            snapshot.ToDictionary(kv => kv.Key, kv => (kv.Value.EmoteId, kv.Value.Name, kv.Value.AddedToSetDay, kv.Value.CreatedRow)));

        var audit = await verify.AuditLogEntries.AsNoTracking().SingleAsync();
        Assert.Equal((AuditActions.BackfillRequest, channel.ChannelName, "emoteSet", SetB), (audit.Action, audit.ChannelName, audit.TargetType, audit.TargetId));
        Assert.Equal(6, JsonDocument.Parse(audit.DetailsJson!).RootElement.GetProperty(AuditLogDetail.Kinds.BackfillMonths).GetInt32());

        // Committed, then the nudge and the progress event — in that order.
        Received.InOrder(() =>
        {
            _publisher.PublishAsync(BotCommands.Channel, $"{BotCommands.BackfillPrefix}{channel.ChannelName}", Arg.Any<CancellationToken>());
            _publisher.PublishAsync(LiveEvents.Channel, Arg.Is<string>(m => m.Contains(LiveEvents.BackfillProgress)), Arg.Any<CancellationToken>());
        });
    }

    // Child 3 AC 3, second half: what happens on 7TV or in our sync after the click changes nothing about
    // what the run counts.
    [Fact]
    public async Task Snapshot_IsUnchanged_AfterTheChannelsEmotesAndActiveSetChange()
    {
        var channel = await SeedChannelAsync("bfsnapshotfrozen");
        await SeedUserAsync();
        await SeedEmoteAsync(channel, "e-active", "ActiveName");
        PreviewReturns(SetB, "Set B", Member("e-active", "BActive", new DateTime(2026, 4, 20, 0, 0, 0, DateTimeKind.Utc)), Member("n1", "BNew1", null));
        var runId = (await EnqueueAsync(channel, SetB, months: 6)).Run!.Id;

        ChatLogBackfillSnapshot before;
        await using (var db = CreateDbContext())
        {
            before = await CreateService(db).GetSnapshotAsync(runId);
        }

        await using (var db = CreateDbContext())
        {
            await db.Emotes.Where(e => e.ChannelId == channel.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, "Renamed").SetProperty(e => e.IsArchived, true).SetProperty(e => e.FirstSeenAt, NowUtc));
            await db.Channels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ActiveEmoteSetId, SetC));
        }

        await using (var db = CreateDbContext())
        {
            var after = await CreateService(db).GetSnapshotAsync(runId);
            Assert.Equal(SetB, after.EmoteSetId);
            Assert.Equal(before.Emotes, after.Emotes);
            Assert.Equal(before.Emotes.OrderBy(e => e.EmoteId, StringComparer.Ordinal), after.Emotes);
            Assert.Contains(after.Emotes, e => e is { Name: "BActive", AddedToSetDay: not null });
        }
    }

    // Child 3 AC 3, refusals: none of these writes a run, a snapshot row, an emote row or an audit entry.
    [Theory]
    [InlineData("SetNotMember")]
    [InlineData("SetNotOnSevenTv")]
    [InlineData("SetEmpty")]
    [InlineData("SetTruncated")]
    [InlineData("MembershipUnavailable")]
    [InlineData("PreviewRateLimited")]
    [InlineData("PreviewBudgetExhausted")]
    [InlineData("ChannelExcluded")]
    [InlineData("ChannelExcludedUnderTheLock")]
    [InlineData("RequesterGone")]
    public async Task Enqueue_Refusals_WriteNothing(string refusal)
    {
        var channel = await SeedChannelAsync("bfrefuse");
        await SeedEmoteAsync(channel, "e-active", "ActiveName");
        if (refusal != "RequesterGone")
        {
            await SeedUserAsync();
        }

        PreviewReturns(SetB, "Set B", Member("e-active", "BActive", null), Member("n1", "BNew1", null));
        ChatLogBackfillEnqueueStatus expected;
        switch (refusal)
        {
            case "SetNotMember":
                _membership.CheckAsync(Arg.Any<string>(), SetB, Arg.Any<CancellationToken>()).Returns(TrackedEmoteSetMembership.NotMember);
                expected = ChatLogBackfillEnqueueStatus.SetNotMember;
                break;
            case "SetNotOnSevenTv":
                PreviewFails(ForeignEmoteSetLookupStatus.NoActiveEmoteSet);
                expected = ChatLogBackfillEnqueueStatus.SetNotMember;
                break;
            case "SetEmpty":
                PreviewReturns(SetB, "Set B");
                expected = ChatLogBackfillEnqueueStatus.SetEmpty;
                break;
            case "SetTruncated":
                PreviewReturns(SetB, "Set B", truncated: true, Member("n1", "BNew1", null));
                expected = ChatLogBackfillEnqueueStatus.SetTruncated;
                break;
            case "MembershipUnavailable":
                _membership.CheckAsync(Arg.Any<string>(), SetB, Arg.Any<CancellationToken>()).Returns(TrackedEmoteSetMembership.SevenTvUnavailable);
                expected = ChatLogBackfillEnqueueStatus.SevenTvUnavailable;
                break;
            case "PreviewRateLimited":
                PreviewFails(ForeignEmoteSetLookupStatus.SevenTvRateLimited, TimeSpan.FromSeconds(30));
                expected = ChatLogBackfillEnqueueStatus.SevenTvUnavailable;
                break;
            case "PreviewBudgetExhausted":
                PreviewFails(ForeignEmoteSetLookupStatus.ProviderBudgetExhausted);
                expected = ChatLogBackfillEnqueueStatus.SevenTvUnavailable;
                break;
            case "ChannelExcluded":
                _excluded.IsExcluded(channel.TwitchChannelId).Returns(true);
                expected = ChatLogBackfillEnqueueStatus.ChannelExcluded;
                break;
            case "ChannelExcludedUnderTheLock":
                // The pre-check passes, the check under the lock refuses (an operator edit is read once at
                // startup, so this stands for an Api whose two reads disagree — the lock decides).
                _excluded.IsExcluded(channel.TwitchChannelId).Returns(false, true);
                expected = ChatLogBackfillEnqueueStatus.ChannelExcluded;
                break;
            case "RequesterGone":
                expected = ChatLogBackfillEnqueueStatus.RequesterGone;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(refusal));
        }

        var before = await StateFingerprintAsync();
        var result = await EnqueueAsync(channel, SetB, months: 6);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Run);
        Assert.Equal(refusal == "PreviewRateLimited" ? TimeSpan.FromSeconds(30) : null, result.RetryAfter);
        Assert.Equal(before, await StateFingerprintAsync());
        await _publisher.DidNotReceiveWithAnyArgs().PublishAsync(default!, default!, default);
    }

    [Fact]
    public async Task Enqueue_PreChecks_RefuseUnknownInactiveIdlessChannelsAndInvalidMonths()
    {
        await SeedUserAsync();
        var inactive = await SeedChannelAsync("bfinactive", isBotActive: false);
        var idless = await SeedChannelAsync("bfidless", twitchChannelId: null);
        var fine = await SeedChannelAsync("bfmonths");

        await using var db = CreateDbContext();
        var service = CreateService(db);
        Assert.Equal(ChatLogBackfillEnqueueStatus.NotFound, (await service.EnqueueAsync("bfnobody", SetB, 6, Today, Actor)).Status);
        Assert.Equal(ChatLogBackfillEnqueueStatus.NotActive, (await service.EnqueueAsync(inactive.ChannelName, SetB, 6, Today, Actor)).Status);
        Assert.Equal(ChatLogBackfillEnqueueStatus.TwitchIdUnknown, (await service.EnqueueAsync(idless.ChannelName, SetB, 6, Today, Actor)).Status);
        Assert.Equal(ChatLogBackfillEnqueueStatus.MonthsInvalid, (await service.EnqueueAsync(fine.ChannelName, SetB, 2, Today, Actor)).Status);
        await _foreign.DidNotReceiveWithAnyArgs().GetForeignEmoteSetBySetIdAsync(default!, default!, default, default);
    }

    // Child 3 AC 2: CreatedAt = today − 60 days. One month has no day before the counting start; three
    // months end at the counting start; six months are at most 27 planner weeks.
    [Fact]
    public async Task Enqueue_WindowMath_FollowsTheCountingStart()
    {
        await SeedUserAsync();
        var createdAt = NowUtc.AddDays(-60);
        var countingStart = DateOnly.FromDateTime(createdAt);
        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", null));

        var oneMonth = await SeedChannelAsync("bfwindow1", createdAt: createdAt);
        Assert.Equal(ChatLogBackfillEnqueueStatus.WindowEmpty, (await EnqueueAsync(oneMonth, SetB, months: 1)).Status);

        var threeMonths = await SeedChannelAsync("bfwindow3", createdAt: createdAt);
        var three = (await EnqueueAsync(threeMonths, SetB, months: 3)).Run!;
        Assert.Equal((Today.AddMonths(-3), countingStart), (three.WindowFrom, three.WindowTo));
        Assert.Equal((countingStart.DayNumber - Today.AddMonths(-3).DayNumber + 6) / 7, three.WeeksTotal);

        var sixMonths = await SeedChannelAsync("bfwindow6", createdAt: createdAt);
        var six = (await EnqueueAsync(sixMonths, SetB, months: 6)).Run!;
        Assert.Equal((Today.AddMonths(-6), countingStart), (six.WindowFrom, six.WindowTo));
        Assert.InRange(six.WeeksTotal, 1, 27);
        Assert.Equal(ChatLogBackfillWindow.WeeksFor(countingStart.DayNumber - Today.AddMonths(-6).DayNumber), six.WeeksTotal);

        // The spec's own example (§4.1): created 2026-10-08, today 2026-10-08, six months = 183 days, 27 blocks.
        var example = ChatLogBackfillWindow.Option(6, new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 8));
        Assert.Equal((new DateOnly(2026, 4, 8), 183, 27, true), (example.WindowFrom, example.Days, example.Weeks, example.Available));
        var disabled = ChatLogBackfillWindow.Option(1, new DateOnly(2026, 10, 8), new DateOnly(2026, 7, 10));
        Assert.Equal((0, 0, false, "no_days_before_counting"), (disabled.Days, disabled.Weeks, disabled.Available, disabled.Reason));
    }

    // Child 3 AC 1, the pre-check: a channel with an active run answers AlreadyActive before 7TV is asked.
    [Fact]
    public async Task Enqueue_WithAnActiveRun_IsAlreadyActive_WithoutAskingSevenTv()
    {
        var channel = await SeedChannelAsync("bfalready");
        await SeedUserAsync();
        await SeedRunAsync(channel, ChatLogBackfillRunStatus.Paused);

        var result = await EnqueueAsync(channel, SetB, months: 6);

        Assert.Equal(ChatLogBackfillEnqueueStatus.AlreadyActive, result.Status);
        await _foreign.DidNotReceiveWithAnyArgs().GetForeignEmoteSetBySetIdAsync(default!, default!, default, default);
    }

    // EPIC AC 26: the same 7TV id under two aliases — one snapshot row and one Emote row with the LAST
    // alias, the name the reconcile itself stores for the same list (#341).
    [Fact]
    public async Task Enqueue_DuplicateSevenTvId_KeepsTheLastAlias_LikeTheReconcile()
    {
        var channel = await SeedChannelAsync("bfdupe", activeSetId: SetB);
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("dup1", "FirstAlias", null), Member("other1", "Other", null), Member("dup1", "LastAlias", null));

        var result = await EnqueueAsync(channel, SetB, months: 6);

        Assert.Equal(2, result.Run!.EmoteCount);
        await using (var verify = CreateDbContext())
        {
            Assert.Equal("LastAlias", (await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync(e => e.SevenTvEmoteId == "dup1")).Name);
            Assert.Equal("LastAlias", (await verify.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "dup1")).Name);
        }

        await SyncAsync(channel, SetB, Live("dup1", "FirstAlias"), Live("other1", "Other"), Live("dup1", "LastAlias"));

        await using (var verify = CreateDbContext())
        {
            var row = await verify.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "dup1");
            Assert.Equal(("LastAlias", false), (row.Name, row.IsArchived));
        }
    }

    // Child 3 AC 4 / EPIC AC 21: a row the enqueue created is found by the reconcile when its set becomes
    // active and is un-archived in place — same id, marker cleared, name and FirstSeenAt corrected.
    [Fact]
    public async Task CreatedPlaceholder_IsUnarchivedInPlace_WhenTheChosenSetBecomesActive()
    {
        var channel = await SeedChannelAsync("bfactivate");
        await SeedUserAsync();
        await SeedEmoteAsync(channel, "a-only", "AOnly");
        PreviewReturns(SetB, "Set B", Member("b1", "BAlias", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        await EnqueueAsync(channel, SetB, months: 6);

        string createdId;
        await using (var read = CreateDbContext())
        {
            createdId = (await read.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "b1")).Id;
        }

        var liveAddedAt = new DateTime(2026, 10, 9, 11, 0, 0, DateTimeKind.Utc);
        await SyncAsync(channel, SetB, Live("b1", "LiveName", liveAddedAt));

        await using var verify = CreateDbContext();
        var row = await verify.Emotes.AsNoTracking().SingleAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "b1");
        Assert.Equal((createdId, false, false, (DateTime?)null), (row.Id, row.IsArchived, row.IsPlaceholder, row.ArchivedAt));
        Assert.Equal(("LiveName", (DateTime?)liveAddedAt), (row.Name, row.FirstSeenAt));
        Assert.NotEqual((DateTime?)NowUtc, row.LastEnteredSetAtUtc);
        Assert.Equal(1, await verify.Emotes.CountAsync(e => e.ChannelId == channel.Id && e.SevenTvEmoteId == "b1"));
        Assert.True((await verify.Emotes.AsNoTracking().SingleAsync(e => e.SevenTvEmoteId == "a-only" && e.ChannelId == channel.Id)).IsArchived);
    }

    // A purge committing between the enqueue's commit and its answer cascades the run away; the request
    // still answers with the run it stored instead of failing after the fact. The purge runs inside the
    // first publish, which comes after the commit.
    [Fact]
    public async Task Enqueue_AnswersWithTheStoredRun_EvenWhenAPurgeRemovesItRightAfterTheCommit()
    {
        var channel = await SeedChannelAsync("bfpurgedafter");
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", null), Member("n2", "BNew2", null));
        _publisher.PublishAsync(BotCommands.Channel, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await using var purge = CreateDbContext();
                await purge.Channels.Where(c => c.Id == channel.Id).ExecuteDeleteAsync();
            });

        var result = await EnqueueAsync(channel, SetB, months: 6);

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, result.Status);
        Assert.Equal(("queued", 2, SetB, "Set B", 1), (result.Run!.Status, result.Run.EmoteCount, result.Run.EmoteSetId, result.Run.EmoteSetName, result.Run.QueuePosition));
        await using var verify = CreateDbContext();
        Assert.False(await verify.ChatLogBackfillRuns.AnyAsync());
    }

    // A Redis outage after the commit costs only the acceleration: the run is stored, the answer is
    // Enqueued, and the failure is a warning.
    [Fact]
    public async Task Enqueue_WhenPublishingFails_StillAnswersEnqueued_AndLogs()
    {
        var channel = await SeedChannelAsync("bfpublishfails");
        await SeedUserAsync();
        PreviewReturns(SetB, "Set B", Member("n1", "BNew1", null));
        _publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("redis down")));
        var logger = new RecordingLogger<ChatLogBackfillService>();

        await using var db = CreateDbContext();
        var result = await CreateService(db, logger: logger).EnqueueAsync(channel.ChannelName, SetB, 6, Today, Actor);

        Assert.Equal(ChatLogBackfillEnqueueStatus.Enqueued, result.Status);
        await using var verify = CreateDbContext();
        Assert.Equal(ChatLogBackfillRunStatus.Queued, (await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("publishing", StringComparison.Ordinal)));
    }

    // ---------------------------------------------------------------- Cancel

    [Fact]
    public async Task Cancel_FlipsTheActiveRun_AuditsAndAnnounces_AndAnswersTheOtherCases()
    {
        var channel = await SeedChannelAsync("bfcancel");
        var paused = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Paused, pausedUntilUtc: NowUtc.AddMinutes(5));

        await using (var db = CreateDbContext())
        {
            var service = CreateService(db);
            Assert.Equal(ChatLogBackfillCancelResult.NotFound, await service.CancelAsync("bfnobody", Actor));
            Assert.Equal(ChatLogBackfillCancelResult.Cancelled, await service.CancelAsync(channel.ChannelName, Actor));
            Assert.Equal(ChatLogBackfillCancelResult.NoActiveRun, await service.CancelAsync(channel.ChannelName, Actor));
        }

        await using var verify = CreateDbContext();
        var row = await verify.ChatLogBackfillRuns.AsNoTracking().SingleAsync(r => r.Id == paused.Id);
        Assert.Equal((ChatLogBackfillRunStatus.Cancelled, (DateTime?)NowUtc, (DateTime?)null, (string?)null), (row.Status, row.FinishedAtUtc, row.PausedUntilUtc, row.ErrorCode));
        var audit = await verify.AuditLogEntries.AsNoTracking().SingleAsync();
        Assert.Equal((AuditActions.BackfillCancel, channel.ChannelName, (string?)null), (audit.Action, audit.ChannelName, audit.DetailsJson));
        await _publisher.Received(1).PublishAsync(LiveEvents.Channel, Arg.Is<string>(m => m.Contains(LiveEvents.BackfillProgress)), Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------- Status

    [Fact]
    public async Task Status_CarriesOptionsRunsCoverageAndCooldown()
    {
        await using (var none = CreateDbContext())
        {
            Assert.Null(await CreateService(none).GetStatusAsync("bfnobody", Today));
        }

        var createdAt = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        var other = await SeedChannelAsync("bfstatusother");
        var channel = await SeedChannelAsync("bfstatus", createdAt: createdAt);
        var countingStart = new DateOnly(2026, 7, 10);

        // Another channel's active run is ahead in the global queue: this channel's queued run is second.
        await SeedRunAsync(other, ChatLogBackfillRunStatus.Running);
        var older = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Completed, emoteSetId: SetB, emoteSetName: "Old name", finishedAtUtc: NowUtc.AddDays(-2));
        var failed = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Failed, emoteSetId: SetC, emoteSetName: null, finishedAtUtc: NowUtc.AddDays(-1));
        var queued = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Queued, emoteSetId: SetB, emoteSetName: "Set B", snapshotSize: 3);

        // Coverage: Jul 1–Jul 4 by set B (run "older"), Jul 5–Jul 9 by set C (run "failed") — adjacent
        // days of two sets, contiguous for the channel up to the counting start.
        await AddCoverageAsync(channel, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5), SetB, older.Id);
        await AddCoverageAsync(channel, new DateOnly(2026, 7, 5), countingStart, SetC, failed.Id);
        await SetCooldownAsync(NowUtc.AddMinutes(3));

        await using var db = CreateDbContext();
        var status = await CreateService(db).GetStatusAsync(channel.ChannelName, Today);

        Assert.NotNull(status);
        Assert.Equal((countingStart, "logs.cyex.app", ArchiveUrl, 10, ActiveSetId), (status.CountingSince, status.Archive.Name, status.Archive.Url, status.RequestDelaySeconds, status.ActiveEmoteSetId));
        Assert.Equal(
            [(1, false, 0, "no_days_before_counting"), (3, true, 1, null), (6, true, 92, null)],
            status.Options.Select(o => (o.Months, o.Available, o.Days, o.Reason)).ToArray());
        Assert.Equal((queued.Id, "queued", 2, 3, "Set B"), (status.ActiveRun!.Id, status.ActiveRun.Status, status.ActiveRun.QueuePosition, status.ActiveRun.EmoteCount, status.ActiveRun.EmoteSetName));
        Assert.Equal((failed.Id, "failed", (int?)null), (status.LastRun!.Id, status.LastRun.Status, status.LastRun.QueuePosition));
        Assert.Equal(
            [
                new ChatLogBackfillCoverageInterval(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5), SetB, "logs.cyex.app", "Old name"),
                new ChatLogBackfillCoverageInterval(new DateOnly(2026, 7, 5), countingStart, SetC, "logs.cyex.app", null),
            ],
            status.Coverage);
        Assert.Equal(((DateOnly?)new DateOnly(2026, 7, 1), (DateOnly?)countingStart, true), (status.ImportedFrom, status.ImportedTo, status.ImportedContiguous));
        Assert.Equal(NowUtc.AddMinutes(3), status.CooldownUntilUtc);

        // A cooldown in the past is no cooldown.
        await SetCooldownAsync(NowUtc.AddMinutes(-1));
        await using var later = CreateDbContext();
        Assert.Null((await CreateService(later).GetStatusAsync(channel.ChannelName, Today))!.CooldownUntilUtc);
    }

    // ---------------------------------------------------------------- Coverage (child 3 AC 7, D34, D49)

    [Fact]
    public async Task Coverage_PerSetAndAllSets_AdjacentDaysOfTwoSets()
    {
        var channel = await SeedChannelAsync("bfcovadjacent", createdAt: new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));
        var countingStart = new DateOnly(2026, 7, 10);
        await AddCoverageAsync(channel, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5), SetB, runId: null);
        await AddCoverageAsync(channel, new DateOnly(2026, 7, 5), countingStart, SetC, runId: null);

        await using var db = CreateDbContext();
        var service = CreateService(db);

        var setB = await service.GetCoverageAsync(channel.Id, EmoteSetScope.Set(SetB));
        Assert.Equal((SetB, D(2026, 7, 1), D(2026, 7, 5), false, (DateOnly?)null), Shape(setB));

        var setC = await service.GetCoverageAsync(channel.Id, EmoteSetScope.Set(SetC));
        Assert.Equal((SetC, D(2026, 7, 5), countingStart, false, D(2026, 7, 5)), Shape(setC));

        var all = await service.GetCoverageAsync(channel.Id, EmoteSetScope.AllSets);
        Assert.Equal(((string?)null, D(2026, 7, 1), countingStart, false, D(2026, 7, 1)), Shape(all));
        Assert.Equal(2, all.Intervals.Count);

        // The active set (here neither B nor C) has nothing of its own.
        var active = await service.GetCoverageAsync(channel.Id, EmoteSetScope.ActiveSet);
        Assert.Equal((ActiveSetId, (DateOnly?)null, (DateOnly?)null, false, (DateOnly?)null), Shape(active));
        Assert.Empty(active.Intervals);
    }

    // D49, the spec's worked example: Apr 1–Apr 10 and Jun 1–Oct 7, counting start Oct 8.
    [Fact]
    public async Task Coverage_GappedWithAContiguousSuffix_MatchesTheWorkedExample()
    {
        var channel = await SeedChannelAsync("bfcovgapped", createdAt: new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc));
        await AddCoverageAsync(channel, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 11), SetB, runId: null);
        await AddCoverageAsync(channel, new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 8), SetB, runId: null);

        await using var db = CreateDbContext();
        var coverage = await CreateService(db).GetCoverageAsync(channel.Id, EmoteSetScope.Set(SetB));

        Assert.Equal((SetB, D(2026, 4, 1), D(2026, 10, 8), true, D(2026, 6, 1)), Shape(coverage));
        Assert.Equal(
            [(D(2026, 4, 1), D(2026, 4, 11)), (D(2026, 6, 1), D(2026, 10, 8))],
            coverage.Intervals.Select(i => ((DateOnly?)i.From, (DateOnly?)i.To)).ToArray());
    }

    // A failed run that covered its first 3 of 27 weeks, and a rejoin: the coverage is anchored at the
    // counting start (CreatedAt), never at a later TrackingResumedAt — the page decides about rejoin gaps.
    [Fact]
    public async Task Coverage_PartialRunAndRejoin_AreNotContiguous_UnlessTheyReachTheCountingStart()
    {
        var partial = await SeedChannelAsync("bfcovpartial", createdAt: new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));
        await AddCoverageAsync(partial, new DateOnly(2026, 4, 8), new DateOnly(2026, 4, 29), SetB, runId: null);

        var rejoined = await SeedChannelAsync("bfcovrejoin", createdAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        await using (var db = CreateDbContext())
        {
            await db.Channels.Where(c => c.Id == rejoined.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.TrackingResumedAt, new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc)));
        }

        await AddCoverageAsync(rejoined, new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1), SetB, runId: null);

        await using var read = CreateDbContext();
        var service = CreateService(read);
        Assert.Equal((SetB, D(2026, 4, 8), D(2026, 4, 29), false, (DateOnly?)null), Shape(await service.GetCoverageAsync(partial.Id, EmoteSetScope.Set(SetB))));
        Assert.Equal((SetB, D(2026, 7, 1), D(2026, 8, 1), false, D(2026, 7, 1)), Shape(await service.GetCoverageAsync(rejoined.Id, EmoteSetScope.Set(SetB))));
        Assert.Equal(((string?)null, (DateOnly?)null, (DateOnly?)null, false, (DateOnly?)null), Shape(await service.GetCoverageAsync("no-such-channel", EmoteSetScope.AllSets)));
    }

    // Coverage outlives the run row: deleting the run (retention) sets RunId to NULL, the days stay.
    [Fact]
    public async Task Coverage_SurvivesTheRunRowsDeletion_WithRunIdNull()
    {
        var channel = await SeedChannelAsync("bfcovrunid", createdAt: new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc));
        var run = await SeedRunAsync(channel, ChatLogBackfillRunStatus.Completed, finishedAtUtc: NowUtc.AddDays(-400));
        await AddCoverageAsync(channel, new DateOnly(2026, 7, 3), new DateOnly(2026, 7, 10), SetB, run.Id);

        await using (var db = CreateDbContext())
        {
            await db.ChatLogBackfillRuns.Where(r => r.Id == run.Id).ExecuteDeleteAsync();
        }

        await using var verify = CreateDbContext();
        var days = await verify.ChatLogBackfillCoverage.AsNoTracking().Where(d => d.ChannelId == channel.Id).ToListAsync();
        Assert.Equal(7, days.Count);
        Assert.All(days, d => Assert.Null(d.RunId));
        Assert.Equal((SetB, D(2026, 7, 3), D(2026, 7, 10), false, D(2026, 7, 3)), Shape(await CreateService(verify).GetCoverageAsync(channel.Id, EmoteSetScope.Set(SetB))));
    }

    // ---------------------------------------------------------------- Helpers

    private AppDbContext CreateDbContext() => fixture.CreateDbContext(DatabaseName);

    private ChatLogBackfillService CreateService(
        AppDbContext db,
        ChatLogArchiveOptions? archiveOptions = null,
        ILogger<ChatLogBackfillService>? logger = null,
        ChatLogBackfillOptions? backfillOptions = null) =>
        new(
            db,
            _publisher,
            _excluded,
            _membership,
            _foreign,
            backfillOptions ?? new ChatLogBackfillOptions(),
            archiveOptions ?? new ChatLogArchiveOptions { BaseUrl = ArchiveUrl },
            _clock,
            logger ?? NullLogger<ChatLogBackfillService>.Instance);

    private async Task<ChatLogBackfillEnqueueResult> EnqueueAsync(Channel channel, string emoteSetId, int months)
    {
        await using var db = CreateDbContext();
        return await CreateService(db).EnqueueAsync(channel.ChannelName, emoteSetId, months, Today, Actor);
    }

    private void PreviewReturns(string emoteSetId, string? name, params ForeignEmoteRow[] members) =>
        PreviewReturns(emoteSetId, name, truncated: false, members);

    private void PreviewReturns(string emoteSetId, string? name, bool truncated, params ForeignEmoteRow[] members) =>
        _foreign.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), emoteSetId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Ok(new ForeignEmoteSet("ignored", null, emoteSetId, members.Length, truncated, members, name, 1000)));

    private void PreviewFails(ForeignEmoteSetLookupStatus status, TimeSpan? retryAfter = null) =>
        _foreign.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ForeignEmoteSetLookupResult.Failed(status, retryAfter));

    // The 7TV read parks until the test releases it — the window in which the races of AC 25 happen.
    private (TaskCompletionSource Entered, TaskCompletionSource<ForeignEmoteSetLookupResult> Release) BlockPreview()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<ForeignEmoteSetLookupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _foreign.GetForeignEmoteSetBySetIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                entered.TrySetResult();
                return release.Task;
            });
        return (entered, release);
    }

    private static ForeignEmoteSetLookupResult PreviewOf(string emoteSetId, params ForeignEmoteRow[] members) =>
        ForeignEmoteSetLookupResult.Ok(new ForeignEmoteSet("ignored", null, emoteSetId, members.Length, false, members, "Set", 1000));

    private static ForeignEmoteRow Member(string sevenTvId, string alias, DateTime? addedAt) =>
        new(sevenTvId, alias, $"{alias}Default", ImageUrl(sevenTvId), null, null, addedAt);

    private static string ImageUrl(string sevenTvId) => $"https://cdn.7tv.app/emote/{sevenTvId}/2x.webp";

    private static SevenTvEmote Live(string sevenTvId, string name, DateTime? addedToSetAt = null) =>
        new(sevenTvId, name, ImageUrl(sevenTvId), addedToSetAt);

    private static DateOnly? D(int year, int month, int day) => new DateOnly(year, month, day);

    private static (string?, DateOnly?, DateOnly?, bool, DateOnly?) Shape(ChatLogBackfillCoverageDto c) =>
        (c.EmoteSetId, c.ImportedFrom, c.ImportedTo, c.HasGaps, c.ContiguousFrom);

    private async Task<Channel> SeedChannelAsync(
        string name,
        DateTime? createdAt = null,
        string? twitchChannelId = "",
        bool isBotActive = true,
        string activeSetId = ActiveSetId)
    {
        await using var db = CreateDbContext();
        var channel = new Channel
        {
            ChannelName = name,
            TwitchChannelId = twitchChannelId == "" ? $"tw_{name}" : twitchChannelId,
            IsBotActive = isBotActive,
            ActiveEmoteSetId = activeSetId,
            CreatedAt = createdAt ?? NowUtc.AddDays(-30),
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }

    private async Task SeedUserAsync()
    {
        await using var db = CreateDbContext();
        db.Users.Add(new User { Id = Actor.TwitchUserId, TwitchUsername = Actor.Login, DisplayName = Actor.Login });
        await db.SaveChangesAsync();
    }

    private async Task<Emote> SeedEmoteAsync(
        Channel channel, string sevenTvId, string name, bool archived = false, DateTime? archivedAt = null)
    {
        await using var db = CreateDbContext();
        var emote = new Emote
        {
            ChannelId = channel.Id,
            SevenTvEmoteId = sevenTvId,
            Name = name,
            ImageUrl = ImageUrl(sevenTvId),
            IsArchived = archived,
            ArchivedAt = archivedAt,
            FirstSeenAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSyncedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            LastEnteredSetAtUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Emotes.Add(emote);
        await db.SaveChangesAsync();
        return emote;
    }

    private async Task<ChatLogBackfillRun> SeedRunAsync(
        Channel channel,
        ChatLogBackfillRunStatus status,
        string emoteSetId = SetB,
        string? emoteSetName = "Set B",
        DateTime? finishedAtUtc = null,
        DateTime? pausedUntilUtc = null,
        int weeksDone = 0,
        int weeksTotal = 3,
        int pauseCount = 0,
        int blockAttempts = 0,
        string archiveBaseUrl = ArchiveUrl,
        int snapshotSize = 0,
        DateOnly? windowFrom = null)
    {
        await using var db = CreateDbContext();
        var from = windowFrom ?? new DateOnly(2026, 6, 1);
        var run = new ChatLogBackfillRun
        {
            ChannelId = channel.Id,
            Status = status,
            RequestedMonths = 6,
            WindowFrom = from,
            WindowTo = from.AddDays(weeksTotal * 7),
            WeeksTotal = weeksTotal,
            WeeksDone = weeksDone,
            EmoteSetId = emoteSetId,
            EmoteSetName = emoteSetName,
            ArchiveBaseUrl = archiveBaseUrl,
            RequestedByTwitchUserId = Actor.TwitchUserId,
            RequestedByLogin = Actor.Login,
            RequestedAtUtc = NowUtc.AddHours(-1),
            FinishedAtUtc = finishedAtUtc,
            PausedUntilUtc = pausedUntilUtc,
            PauseCount = pauseCount,
            BlockAttempts = blockAttempts,
        };
        for (var i = 0; i < snapshotSize; i++)
        {
            run.Emotes.Add(new ChatLogBackfillRunEmote { EmoteId = $"seed-{i}", SevenTvEmoteId = $"seed7tv-{i}", Name = $"Seed{i}" });
        }

        db.ChatLogBackfillRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task AddCoverageAsync(Channel channel, DateOnly from, DateOnly toExclusive, string emoteSetId, long? runId, string host = "logs.cyex.app")
    {
        await using var db = CreateDbContext();
        for (var day = from; day < toExclusive; day = day.AddDays(1))
        {
            db.ChatLogBackfillCoverage.Add(new ChatLogBackfillCoverageDay
            {
                ChannelId = channel.Id,
                Day = day,
                EmoteSetId = emoteSetId,
                ArchiveHost = host,
                RunId = runId,
                CompletedAtUtc = NowUtc,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task SetCooldownAsync(DateTime? until)
    {
        await using var db = CreateDbContext();
        await db.ChatLogBackfillProviderState.Where(s => s.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.CooldownUntilUtc, until));
    }

    // Everything an enqueue may write, as one comparable string.
    private async Task<string> StateFingerprintAsync()
    {
        await using var db = CreateDbContext();
        var emotes = await db.Emotes.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        return JsonSerializer.Serialize(new
        {
            Runs = await db.ChatLogBackfillRuns.CountAsync(),
            Snapshot = await db.ChatLogBackfillRunEmotes.CountAsync(),
            Audit = await db.AuditLogEntries.CountAsync(),
            Emotes = emotes.Select(e => new { e.Id, e.SevenTvEmoteId, e.Name, e.IsArchived, e.IsPlaceholder, e.ArchivedAt, e.FirstSeenAt, e.LastSyncedAt, e.LastEnteredSetAtUtc }),
        });
    }

    private async Task<string> EmoteRowsJsonAsync(Channel channel, params string[] sevenTvIds)
    {
        await using var db = CreateDbContext();
        var rows = await db.Emotes.AsNoTracking()
            .Where(e => e.ChannelId == channel.Id && sevenTvIds.Contains(e.SevenTvEmoteId))
            .OrderBy(e => e.SevenTvEmoteId)
            .ToListAsync();
        return JsonSerializer.Serialize(rows.Select(e => new
        {
            e.Id,
            e.SevenTvEmoteId,
            e.Name,
            e.ImageUrl,
            e.IsArchived,
            e.ArchivedAt,
            e.FirstSeenAt,
            e.LastSyncedAt,
            e.LastEnteredSetAtUtc,
            e.IsPlaceholder,
        }));
    }

    // A REST resync of the channel against the given live set, through the real SevenTvSyncService.
    private async Task SyncAsync(Channel channel, string emoteSetId, params SevenTvEmote[] liveEmotes)
    {
        await using var db = CreateDbContext();
        var result = await CreateSyncService(db, RestAnswering(channel, emoteSetId, liveEmotes)).SyncChannelAsync(channel.ChannelName);
        Assert.NotNull(result);
    }

    private static SevenTvSyncService CreateSyncService(AppDbContext db, ISevenTvApiClient apiClient) =>
        new(db, apiClient, new EmoteMatchCache(), new DuplicateEmoteNameTracker(),
            new ChannelEmoteSetObservationService(db), new ChannelSyncGate(), Substitute.For<IExcludedChannelFilter>(),
            new BroadcasterChannelLockService(db), new RecordingSevenTvSearchBudget(), new TwitchIdResolutionBackoff(new SevenTvSearchBudgetOptions(), TimeProvider.System),
            new EmptySetConfirmationTracker(new EmptySetConfirmationOptions(), TimeProvider.System),
            NullLogger<SevenTvSyncService>.Instance);

    private static ISevenTvApiClient RestAnswering(Channel channel, string emoteSetId, params SevenTvEmote[] liveEmotes)
    {
        var apiClient = Substitute.For<ISevenTvApiClient>();
        apiClient.GetChannelStateForTwitchUserAsync(channel.TwitchChannelId!, Arg.Any<CancellationToken>())
            .Returns(SevenTvChannelStateResult.Ok(new SevenTvChannelState("7tv-user", new SevenTvEmoteSet(emoteSetId, liveEmotes))));
        return apiClient;
    }
}
