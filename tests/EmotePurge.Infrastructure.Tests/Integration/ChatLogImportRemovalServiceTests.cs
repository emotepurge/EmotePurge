using System.Data.Common;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Messaging;
using EmotePurge.Core.Services;
using EmotePurge.Infrastructure.Migrations;
using EmotePurge.Infrastructure.Persistence;
using EmotePurge.Infrastructure.Services;
using EmotePurge.Infrastructure.Tests.Fakes;
using EmotePurge.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;
using static EmotePurge.Infrastructure.Tests.Fixtures.ImportedBackfillSeed;

namespace EmotePurge.Infrastructure.Tests.Integration;

// Removing a channel's imported backfill data (#384, spec 11.1) against real Postgres.
//
// Its own database, emptied before every test: S2 compares the neighbour channel table by table and the
// audit assertions count every entry, so rows other suites seed must not be visible. The states are
// seeded straight through EF (ImportedBackfillSeed), never through ChatLogBackfillService, whose
// constructor and block result #379 part B changes; the cases that need that service (S10, S11, S13,
// the coverage readers of S3, the sweep equivalence of S6) live in the .Backfill.cs part (task T7).
[Collection("Postgres")]
public partial class ChatLogImportRemovalServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DatabaseName = "chatlog_import_removal_tests";

    // 26-character ULID-shaped set ids, like 7TV's.
    private const string SetA = "01J9RMSETA0000000000000001";
    private const string SetB = "01J9RMSETB0000000000000002";

    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);
    private static bool _databaseReady;

    private static readonly AuditActor Actor = new("4242", "removalmod");

    // Imported days lie before the counting start (2026-05-01), live days from it on (I2).
    private static readonly DateOnly April1 = new(2026, 4, 1);
    private static readonly DateOnly May1 = new(2026, 5, 1);

    private readonly IRedisPublisher _publisher = Substitute.For<IRedisPublisher>();

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
        await reset.Database.ExecuteSqlRawAsync("""TRUNCATE "Channels", "Users", "AuditLogEntries" RESTART IDENTITY CASCADE;""");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---------------------------------------------------------------- Result, live rows, audit, events (S1, S3, S8, S9)

    // S3 (tables only; the GetCoverageAsync readers follow in T7) and AK 2: with the current coverage as
    // the expectation every imported row of every set and every coverage day goes; the counters match.
    [Fact]
    public async Task Remove_WithTheCurrentCoverage_DeletesEveryImportedRowAndDay_AndCountsThem()
    {
        var main = await SeedMainScenarioAsync("rmresult");

        var result = await RemoveAsync(main.Channel.ChannelName, main.Expectation);

        Assert.Equal(ChatLogImportRemovalStatus.Removed, result.Status);
        Assert.Equal((14, 11, 0), (result.RemovedUsageRows, result.RemovedCoverageDays, result.RemovedSeeds));
        await using var verify = CreateDbContext();
        Assert.Equal(0, await ImportedRowCountAsync(verify, main.Channel.Id));
        Assert.Equal(0, await CoverageDayCountAsync(verify, main.Channel.Id));
    }

    // S1 / AK 3: live rows on the same emotes and slots, in two sets, are byte-identical afterwards.
    [Fact]
    public async Task Remove_LeavesEveryLiveRowOfTheChannelByteIdentical()
    {
        var main = await SeedMainScenarioAsync("rmlive");
        string before;
        await using (var read = CreateDbContext())
        {
            before = await LiveRowsHashAsync(read, main.Channel.Id);
            Assert.Equal(5, await read.UsageStats.CountAsync(u => u.Emote.ChannelId == main.Channel.Id && u.Source == UsageStatSource.Live));
        }

        Assert.Equal(ChatLogImportRemovalStatus.Removed, (await RemoveAsync(main.Channel.ChannelName, main.Expectation)).Status);

        await using var verify = CreateDbContext();
        Assert.Equal(before, await LiveRowsHashAsync(verify, main.Channel.Id));
        Assert.Equal(5, await verify.UsageStats.CountAsync(u => u.Emote.ChannelId == main.Channel.Id));
    }

    // S8 / AK 10: exactly one entry with the channel and the removed days, projected by the reader.
    [Fact]
    public async Task Remove_WritesOneAuditEntryWithTheRemovedDays_ThatTheAuditReaderProjects()
    {
        var main = await SeedMainScenarioAsync("rmaudit");

        await RemoveAsync(main.Channel.ChannelName, main.Expectation);

        await using var verify = CreateDbContext();
        var entry = await verify.AuditLogEntries.AsNoTracking().SingleAsync();
        Assert.Equal(
            (AuditActions.BackfillRemoveImported, "rmaudit", Actor.TwitchUserId, Actor.Login, (string?)null, (string?)null),
            (entry.Action, entry.ChannelName, entry.ActorTwitchUserId, entry.ActorLogin, entry.TargetType, entry.TargetId));
        var page = await new AuditLogQueryService(verify).ListAsync(1, 100);
        var dto = Assert.Single(page.Items);
        Assert.Equal(new AuditLogDetail(AuditLogDetail.Kinds.BackfillRemovedDays, 11, null), dto.Detail);
    }

    // S9 / AK 11: after the commit, backfill.progress and then usage.flushed, for the normalized name of
    // the locked row whatever spelling the request used.
    [Fact]
    public async Task Remove_PublishesProgressThenFlushed_ForTheNormalizedChannelName()
    {
        var main = await SeedMainScenarioAsync("rmpublish");

        await RemoveAsync("  RmPublish ", main.Expectation);

        Received.InOrder(() =>
        {
            _publisher.PublishAsync(LiveEvents.Channel, new LiveEvent(LiveEvents.BackfillProgress, "rmpublish").Serialize(), Arg.Any<CancellationToken>());
            _publisher.PublishAsync(LiveEvents.Channel, new LiveEvent(LiveEvents.UsageFlushed, "rmpublish").Serialize(), Arg.Any<CancellationToken>());
        });
        Assert.Equal(2, _publisher.ReceivedCalls().Count());
    }

    // S9, Redis down: the removal is committed before the publish, so a failing publisher costs the
    // events and a warning, never the result.
    [Fact]
    public async Task Remove_WithRedisDown_StillReportsRemoved_AndTheDataIsGone()
    {
        var main = await SeedMainScenarioAsync("rmredis");
        _publisher.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));
        var logger = new RecordingLogger<ChatLogImportRemovalService>();

        var result = await RemoveAsync(main.Channel.ChannelName, main.Expectation, logger);

        Assert.Equal(ChatLogImportRemovalStatus.Removed, result.Status);
        await using var verify = CreateDbContext();
        Assert.Equal(0, await ImportedRowCountAsync(verify, main.Channel.Id));
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("14 usage rows", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Other channels (S2)

    // S2 / AK 4: a neighbour with its own import, coverage, runs, snapshot rows and seeds is untouched,
    // table by table.
    [Fact]
    public async Task Remove_LeavesAnotherChannelsUsageCoverageSlotsRunsAndSnapshotsByteIdentical()
    {
        var main = await SeedMainScenarioAsync("rmmain");
        var neighbour = await SeedMainScenarioAsync("rmneighbour");
        await using (var seed = CreateDbContext())
        {
            var emote = await AddEmoteAsync(seed, neighbour.Channel, "n-seed", isPlaceholder: true);
            var slot = await AddSlotAsync(seed, emote, "NSeed", seedUnsettled: true, archived: true);
            await AddUsageAsync(seed, slot, SetA, April1, April1.AddDays(3), UsageStatSource.ChatLogArchive);
            await AddRunAsync(seed, neighbour.Channel, ChatLogBackfillRunStatus.Completed, SetA, new SnapshotEntry(emote, slot, CreatedRow: true, CreatedSlot: true));
        }

        ChannelHashes before;
        await using (var read = CreateDbContext())
        {
            before = await ChannelHashesAsync(read, neighbour.Channel.Id);
        }

        Assert.Equal(ChatLogImportRemovalStatus.Removed, (await RemoveAsync(main.Channel.ChannelName, main.Expectation)).Status);

        await using var verify = CreateDbContext();
        Assert.Equal(before, await ChannelHashesAsync(verify, neighbour.Channel.Id));
        Assert.Equal(17, await ImportedRowCountAsync(verify, neighbour.Channel.Id));
    }

    // ---------------------------------------------------------------- Refusals (S4, S5, NotFound)

    // S4 / AK 5: an active run refuses, whatever the expectation; nothing changes, no audit, no event.
    [Theory]
    [InlineData(ChatLogBackfillRunStatus.Queued)]
    [InlineData(ChatLogBackfillRunStatus.Running)]
    [InlineData(ChatLogBackfillRunStatus.Paused)]
    public async Task Remove_WithAnActiveRun_IsRefused_AndChangesNothing(ChatLogBackfillRunStatus status)
    {
        var main = await SeedMainScenarioAsync("rmactive");
        await using (var seed = CreateDbContext())
        {
            await AddRunAsync(seed, main.Channel, status, SetB);
        }

        await AssertRefusedWithoutWritesAsync(main.Channel, main.Expectation, ChatLogImportRemovalStatus.RunActive);
    }

    // S5 / AK 6: a second call with the expectation that is now stale answers NothingToRemove — no
    // audit, no event — instead of a scope conflict (D9 is checked before D15).
    [Fact]
    public async Task Remove_Twice_WithTheSameStaleExpectation_AnswersNothingToRemove_WithoutAuditOrEvents()
    {
        var main = await SeedMainScenarioAsync("rmtwice");
        Assert.Equal(ChatLogImportRemovalStatus.Removed, (await RemoveAsync(main.Channel.ChannelName, main.Expectation)).Status);
        _publisher.ClearReceivedCalls();

        var second = await RemoveAsync(main.Channel.ChannelName, main.Expectation);

        Assert.Equal((ChatLogImportRemovalStatus.NothingToRemove, 0, 0, 0), (second.Status, second.RemovedUsageRows, second.RemovedCoverageDays, second.RemovedSeeds));
        await using var verify = CreateDbContext();
        Assert.Equal(1, await verify.AuditLogEntries.CountAsync());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // S5, second half: a channel that never imported anything, with an empty expectation.
    [Fact]
    public async Task Remove_OnAChannelWithoutImports_AnswersNothingToRemove()
    {
        Channel channel;
        await using (var seed = CreateDbContext())
        {
            channel = await AddChannelAsync(seed, "rmnothing", SetA);
            var emote = await AddEmoteAsync(seed, channel, "e1");
            var slot = await AddSlotAsync(seed, emote, "E1");
            await AddUsageAsync(seed, slot, SetA, May1, May1.AddDays(2), UsageStatSource.Live);
        }

        await AssertRefusedWithoutWritesAsync(channel, [], ChatLogImportRemovalStatus.NothingToRemove);
    }

    [Fact]
    public async Task Remove_OnAnUnknownChannel_AnswersNotFound()
    {
        var result = await RemoveAsync("rmnobody", []);

        Assert.Equal(ChatLogImportRemovalStatus.NotFound, result.Status);
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // ---------------------------------------------------------------- Expectation (S17)

    // S17 / AK 14 against a coverage of two sets with a gap (A: Apr 1–7, B: Apr 10–13): every day-level
    // deviation refuses without a write; the same days split differently is the same scope.
    [Theory]
    [InlineData("exact", true)]
    [InlineData("other split", true)]
    [InlineData("one day more", false)]
    [InlineData("one day fewer", false)]
    [InlineData("other set on a day", false)]
    [InlineData("other host", false)]
    [InlineData("overlapping", false)]
    [InlineData("empty", false)]
    public async Task Remove_ComparesTheExpectationWithTheCurrentCoverageByDay(string variant, bool matches)
    {
        var main = await SeedMainScenarioAsync("rmscope");
        var a = new ExpectedCoverageInterval(April1, April1.AddDays(7), SetA, ArchiveHost);
        var b = new ExpectedCoverageInterval(April1.AddDays(9), April1.AddDays(13), SetB, ArchiveHost);
        IReadOnlyList<ExpectedCoverageInterval> expectation = variant switch
        {
            "exact" => [a, b],
            "other split" => [b, a with { ToExclusive = April1.AddDays(3) }, a with { From = April1.AddDays(3) }],
            "one day more" => [a with { ToExclusive = April1.AddDays(8) }, b],
            "one day fewer" => [a with { ToExclusive = April1.AddDays(6) }, b],
            "other set on a day" => [a with { ToExclusive = April1.AddDays(6) }, a with { From = April1.AddDays(6), EmoteSetId = SetB }, b],
            "other host" => [a, b with { ArchiveHost = "logs.example.org" }],
            "overlapping" => [a, a with { From = April1.AddDays(6), ToExclusive = April1.AddDays(7) }, b],
            "empty" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        if (matches)
        {
            var result = await RemoveAsync(main.Channel.ChannelName, expectation);
            Assert.Equal((ChatLogImportRemovalStatus.Removed, 11), (result.Status, result.RemovedCoverageDays));
        }
        else
        {
            await AssertRefusedWithoutWritesAsync(main.Channel, expectation, ChatLogImportRemovalStatus.ScopeChanged);
        }
    }

    // ---------------------------------------------------------------- Seeds and inventory (S6, S7, S16)

    // S6 (a)–(f) / AK 7: only an unsettled seed that this removal left without rows goes. One with live
    // rows, a settled slot (rowless now), a settled CreatedSlot slot, a seed that was rowless before and
    // another channel's seed with only imported rows all stay.
    [Fact]
    public async Task Remove_DeletesOnlyTheUnsettledSeedsItLeftWithoutRows()
    {
        Channel channel;
        Channel other;
        EmoteSlot onlyImported, liveAndImported, settled, settledCreatedSlot, rowlessBefore, otherChannelSeed;
        await using (var seed = CreateDbContext())
        {
            channel = await AddChannelAsync(seed, "rmseeds", SetA);
            other = await AddChannelAsync(seed, "rmseedsother", SetA);
            var to = April1.AddDays(2);

            onlyImported = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "a"), "A", seedUnsettled: true, archived: true);
            await AddUsageAsync(seed, onlyImported, SetA, April1, to, UsageStatSource.ChatLogArchive);

            liveAndImported = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "b"), "B", seedUnsettled: true);
            await AddUsageAsync(seed, liveAndImported, SetA, April1, to, UsageStatSource.ChatLogArchive);
            await AddUsageAsync(seed, liveAndImported, SetA, May1, May1.AddDays(1), UsageStatSource.Live);

            settled = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "c"), "C");
            await AddUsageAsync(seed, settled, SetA, April1, to, UsageStatSource.ChatLogArchive);

            var createdEmote = await AddEmoteAsync(seed, channel, "d");
            settledCreatedSlot = await AddSlotAsync(seed, createdEmote, "D", archived: true);
            await AddUsageAsync(seed, settledCreatedSlot, SetA, April1, to, UsageStatSource.ChatLogArchive);
            await AddRunAsync(seed, channel, ChatLogBackfillRunStatus.Completed, SetA, new SnapshotEntry(createdEmote, settledCreatedSlot, CreatedSlot: true));

            rowlessBefore = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "e"), "E", seedUnsettled: true, archived: true);

            otherChannelSeed = await AddSlotAsync(seed, await AddEmoteAsync(seed, other, "f"), "F", seedUnsettled: true, archived: true);
            await AddUsageAsync(seed, otherChannelSeed, SetA, April1, to, UsageStatSource.ChatLogArchive);

            await AddCoverageAsync(seed, channel, April1, to, SetA);
        }

        int rowlessSeedsBefore;
        await using (var read = CreateDbContext())
        {
            rowlessSeedsBefore = await RowlessUnsettledSeedCountAsync(read, channel.Id);
        }

        var result = await RemoveAsync(channel.ChannelName, [new ExpectedCoverageInterval(April1, April1.AddDays(2), SetA, ArchiveHost)]);

        Assert.Equal((ChatLogImportRemovalStatus.Removed, 8, 2, 1), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays, result.RemovedSeeds));
        await using var verify = CreateDbContext();
        var remaining = await verify.EmoteSlots.AsNoTracking().Select(s => s.Id).ToListAsync();
        Assert.DoesNotContain(onlyImported.Id, remaining);
        Assert.Contains(liveAndImported.Id, remaining);
        Assert.Contains(settled.Id, remaining);
        Assert.Contains(settledCreatedSlot.Id, remaining);
        Assert.Contains(rowlessBefore.Id, remaining);
        Assert.Contains(otherChannelSeed.Id, remaining);
        Assert.Equal(1, await verify.UsageStats.CountAsync(u => u.SlotId == liveAndImported.Id && u.Source == UsageStatSource.Live));
        Assert.Equal(2, await verify.UsageStats.CountAsync(u => u.SlotId == otherChannelSeed.Id));
        Assert.Equal(1, rowlessSeedsBefore);
        Assert.True(await RowlessUnsettledSeedCountAsync(verify, channel.Id) <= rowlessSeedsBefore);
    }

    // S7 / AK 8: placeholder emotes, settled CreatedSlot slots, runs and snapshot rows stay exactly as
    // they were, and the rollback's provenance still finds them: the step-3 predicate the placeholder,
    // step 3b (EmoteSlotRollbackSql) the settled CreatedSlot slot.
    [Fact]
    public async Task Remove_LeavesPlaceholdersSettledCreatedSlotsRunsAndSnapshots_AndTheRollbackStillFindsThem()
    {
        Channel channel;
        Emote placeholder;
        EmoteSlot createdSlot;
        await using (var seed = CreateDbContext())
        {
            channel = await AddChannelAsync(seed, "rminventory", SetA);
            placeholder = await AddEmoteAsync(seed, channel, "p", isPlaceholder: true);
            createdSlot = await AddSlotAsync(seed, placeholder, "P", archived: true);
            await AddUsageAsync(seed, createdSlot, SetB, April1, April1.AddDays(4), UsageStatSource.ChatLogArchive);
            var run = await AddRunAsync(seed, channel, ChatLogBackfillRunStatus.Completed, SetB, new SnapshotEntry(placeholder, createdSlot, CreatedRow: true, CreatedSlot: true));
            await AddRunAsync(seed, channel, ChatLogBackfillRunStatus.Cancelled, SetB);
            await AddCoverageAsync(seed, channel, April1, April1.AddDays(4), SetB, runId: run.Id);
        }

        ChannelHashes before;
        await using (var read = CreateDbContext())
        {
            before = await ChannelHashesAsync(read, channel.Id);
        }

        var result = await RemoveAsync(channel.ChannelName, [new ExpectedCoverageInterval(April1, April1.AddDays(4), SetB, ArchiveHost)]);

        Assert.Equal((ChatLogImportRemovalStatus.Removed, 4, 4, 0), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays, result.RemovedSeeds));
        await using var verify = CreateDbContext();
        var after = await ChannelHashesAsync(verify, channel.Id);
        Assert.Equal(
            (before.Emotes, before.EmoteSlots, before.Runs, before.Snapshots),
            (after.Emotes, after.EmoteSlots, after.Runs, after.Snapshots));
        Assert.Equal([placeholder.Id], await PlaceholderCleanupCandidatesAsync(verify));
        Assert.Equal(1, await CountCreatedSlotCleanupAsync(verify));
    }

    // S16 / AK 9 (Sol finding 3): a flag-off enqueue seed — unsettled, archived without archive time,
    // CreatedSlot and CreatedRow in a finished run's snapshot — that carried only imported rows goes;
    // its snapshot row stays unchanged (pointing nowhere), and so does the placeholder, which the
    // step-3 predicate still finds.
    [Fact]
    public async Task Remove_DeletesAFlagOffEnqueueSeedWithOnlyImportedRows_AndKeepsItsSnapshotRow()
    {
        Channel channel;
        Emote placeholder;
        EmoteSlot enqueueSeed;
        await using (var seed = CreateDbContext())
        {
            channel = await AddChannelAsync(seed, "rmoldseed", SetA);
            placeholder = await AddEmoteAsync(seed, channel, "o", isPlaceholder: true);
            enqueueSeed = await AddSlotAsync(seed, placeholder, "O", seedUnsettled: true, archived: true);
            await AddUsageAsync(seed, enqueueSeed, SetB, April1, April1.AddDays(3), UsageStatSource.ChatLogArchive);
            var run = await AddRunAsync(seed, channel, ChatLogBackfillRunStatus.Completed, SetB, new SnapshotEntry(placeholder, enqueueSeed, CreatedRow: true, CreatedSlot: true));
            await AddCoverageAsync(seed, channel, April1, April1.AddDays(3), SetB, runId: run.Id);
        }

        ChannelHashes before;
        await using (var read = CreateDbContext())
        {
            before = await ChannelHashesAsync(read, channel.Id);
        }

        var result = await RemoveAsync(channel.ChannelName, [new ExpectedCoverageInterval(April1, April1.AddDays(3), SetB, ArchiveHost)]);

        Assert.Equal((ChatLogImportRemovalStatus.Removed, 3, 3, 1), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays, result.RemovedSeeds));
        await using var verify = CreateDbContext();
        Assert.False(await verify.EmoteSlots.AnyAsync(s => s.Id == enqueueSeed.Id));
        var after = await ChannelHashesAsync(verify, channel.Id);
        Assert.Equal((before.Emotes, before.Runs, before.Snapshots), (after.Emotes, after.Runs, after.Snapshots));
        var snapshot = await verify.ChatLogBackfillRunEmotes.AsNoTracking().SingleAsync();
        Assert.Equal((enqueueSeed.Id, true, true), (snapshot.SlotId, snapshot.CreatedRow, snapshot.CreatedSlot));
        Assert.Equal([placeholder.Id], await PlaceholderCleanupCandidatesAsync(verify));
    }

    // ---------------------------------------------------------------- Scope corners (S14, S15)

    // S14 / D11: a channel no longer observed can still remove its data. The exclusion list cannot get in
    // the way either: the service takes no IExcludedChannelFilter at all.
    [Fact]
    public async Task Remove_WorksForAChannelThatIsNoLongerObserved()
    {
        var main = await SeedMainScenarioAsync("rminactive", isBotActive: false);

        var result = await RemoveAsync(main.Channel.ChannelName, main.Expectation);

        Assert.Equal(ChatLogImportRemovalStatus.Removed, result.Status);
        await using var verify = CreateDbContext();
        Assert.Equal(0, await ImportedRowCountAsync(verify, main.Channel.Id));
    }

    // S15: coverage without a single imported row (the archive had no messages) goes.
    [Fact]
    public async Task Remove_CoverageWithoutRows_RemovesTheCoverage()
    {
        Channel channel;
        await using (var seed = CreateDbContext())
        {
            channel = await AddChannelAsync(seed, "rmcoverageonly", SetA);
            await AddCoverageAsync(seed, channel, April1, April1.AddDays(5), SetA);
        }

        var result = await RemoveAsync(channel.ChannelName, [new ExpectedCoverageInterval(April1, April1.AddDays(5), SetA, ArchiveHost)]);

        Assert.Equal((ChatLogImportRemovalStatus.Removed, 0, 5), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays));
        await using var verify = CreateDbContext();
        Assert.Equal(0, await CoverageDayCountAsync(verify, channel.Id));
        Assert.Equal(1, await verify.AuditLogEntries.CountAsync());
    }

    // S15: imported rows without any coverage day (a half-run operator step 4) go with an empty expectation.
    [Fact]
    public async Task Remove_RowsWithoutCoverage_RemovesTheRows_WithAnEmptyExpectation()
    {
        Channel channel;
        await using (var seed = CreateDbContext())
        {
            channel = await AddChannelAsync(seed, "rmrowsonly", SetA);
            var slot = await AddSlotAsync(seed, await AddEmoteAsync(seed, channel, "e1"), "E1");
            await AddUsageAsync(seed, slot, SetA, April1, April1.AddDays(4), UsageStatSource.ChatLogArchive);
        }

        var result = await RemoveAsync(channel.ChannelName, []);

        Assert.Equal((ChatLogImportRemovalStatus.Removed, 4, 0), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays));
        await using var verify = CreateDbContext();
        Assert.Equal(0, await ImportedRowCountAsync(verify, channel.Id));
    }

    // ---------------------------------------------------------------- Concurrency (S12, retry)

    // S12 / AK 12, the settle first: a REST settle holding the channel and the emote rows has moved a
    // seed's imported rows onto the authoritative slot and settled the seed. The removal waits — at the
    // channel row when the settle took it (its real lock order), at the emote rows when a writer locks
    // only those — and then deletes the moved rows at their new slot; the settled seed and the target
    // stay (D8), no unsettled seed is left rowless, no 40P01.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Race_SettleFirst_TheRemovalWaitsAndDeletesTheMovedRows(bool settleLocksTheChannel)
    {
        var state = await SeedSettleScenarioAsync(settleLocksTheChannel ? "rmsettlech" : "rmsettleem");

        await using var settleDb = fixture.CreateTaggedDbContext("rmsettle-settle", DatabaseName);
        await using (var settleTx = await settleDb.Database.BeginTransactionAsync())
        {
            if (settleLocksTheChannel)
            {
                Assert.NotNull(await settleDb.LockChannelByIdAsync(state.Channel.Id, CancellationToken.None));
            }

            await settleDb.LockEmotesByIdsAsync([state.Emote.Id], CancellationToken.None);
            await SeedSettleWrites.MoveSeedUsageAsync(settleDb, new SlotUsageRebind(state.Seed.Id, state.Target.Id), SetA, CancellationToken.None);
            await settleDb.EmoteSlots.Where(s => s.Id == state.Seed.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.SeedUnsettled, false));

            await using var removalDb = fixture.CreateTaggedDbContext("rmsettle-removal", DatabaseName);
            var removal = Task.Run(() => CreateService(removalDb).RemoveImportedAsync(state.Channel.ChannelName, state.Expectation, Actor));
            await fixture.WaitUntilBlockedOnLockAsync("rmsettle-removal", removal);

            await settleTx.CommitAsync();
            var result = await removal;
            Assert.Equal((ChatLogImportRemovalStatus.Removed, 3, 0), (result.Status, result.RemovedUsageRows, result.RemovedSeeds));
        }

        await using var verify = CreateDbContext();
        Assert.Equal(0, await ImportedRowCountAsync(verify, state.Channel.Id));
        Assert.Equal(0, await RowlessUnsettledSeedCountAsync(verify, state.Channel.Id));
        Assert.True(await verify.EmoteSlots.AnyAsync(s => s.Id == state.Seed.Id && !s.SeedUnsettled));
        Assert.Equal(1, await verify.UsageStats.CountAsync(u => u.SlotId == state.Target.Id && u.Source == UsageStatSource.Live));
    }

    // S12 / AK 12, the removal first: it holds the channel and emote rows and is about to delete the
    // rows; the settle waits at the channel row, then finds nothing imported to move and no seed to
    // settle — the removal has deleted the seed it left rowless.
    [Fact]
    public async Task Race_RemovalFirst_TheSettleWaitsAndFindsNothingToMove()
    {
        var state = await SeedSettleScenarioAsync("rmsettlerm");

        var atDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var removalDb = CreateDbContext(new PauseBeforeCommand("DELETE FROM \"UsageStats\"", atDelete, proceed));
        var removal = Task.Run(() => CreateService(removalDb).RemoveImportedAsync(state.Channel.ChannelName, state.Expectation, Actor));
        await atDelete.Task;

        await using var settleDb = fixture.CreateTaggedDbContext("rmsettlerm-settle", DatabaseName);
        var settle = Task.Run(async () =>
        {
            await using var tx = await settleDb.Database.BeginTransactionAsync();
            await settleDb.LockChannelByIdAsync(state.Channel.Id, CancellationToken.None);
            await settleDb.LockEmotesByIdsAsync([state.Emote.Id], CancellationToken.None);
            var (moved, merged) = await SeedSettleWrites.MoveSeedUsageAsync(
                settleDb, new SlotUsageRebind(state.Seed.Id, state.Target.Id), SetA, CancellationToken.None);
            var settled = await settleDb.EmoteSlots.Where(s => s.Id == state.Seed.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.SeedUnsettled, false));
            await tx.CommitAsync();
            return (moved, merged, settled);
        });
        await fixture.WaitUntilBlockedOnLockAsync("rmsettlerm-settle", settle);

        proceed.SetResult();
        var result = await removal;
        Assert.Equal((ChatLogImportRemovalStatus.Removed, 3, 1), (result.Status, result.RemovedUsageRows, result.RemovedSeeds));
        Assert.Equal((0, 0, 0), await settle);

        await using var verify = CreateDbContext();
        Assert.Equal(0, await ImportedRowCountAsync(verify, state.Channel.Id));
        Assert.Equal(0, await RowlessUnsettledSeedCountAsync(verify, state.Channel.Id));
        Assert.False(await verify.EmoteSlots.AnyAsync(s => s.Id == state.Seed.Id));
        Assert.Equal(1, await verify.UsageStats.CountAsync(u => u.SlotId == state.Target.Id));
    }

    // AK 12, retry: another transaction holds an emote row and then asks for the channel row the
    // removal holds while it waits for that emote row. Postgres aborts the removal (the first waiter)
    // with 40P01; the removal retries its whole transaction and succeeds once the other one is done.
    // Mutation probe: without the retry the 40P01 reaches the caller.
    [Fact]
    public async Task Race_ADeadlock_IsRetried_AndTheRemovalSucceeds()
    {
        var main = await SeedMainScenarioAsync("rmdeadlock");
        var emoteId = await FirstImportedEmoteIdAsync(main.Channel.Id);
        var logger = new RecordingLogger<ChatLogImportRemovalService>();

        await using var otherDb = fixture.CreateTaggedDbContext("rmdeadlock-other", DatabaseName);
        await using var otherTx = await otherDb.Database.BeginTransactionAsync();
        await otherDb.LockEmotesByIdsAsync([emoteId], CancellationToken.None);

        await using var removalDb = fixture.CreateTaggedDbContext("rmdeadlock-removal", DatabaseName);
        var removal = Task.Run(() => CreateService(removalDb, logger).RemoveImportedAsync(main.Channel.ChannelName, main.Expectation, Actor));
        await fixture.WaitUntilBlockedOnLockAsync("rmdeadlock-removal", removal);

        var other = Task.Run(async () =>
        {
            await otherDb.LockChannelByIdAsync(main.Channel.Id, CancellationToken.None);
            await otherTx.CommitAsync();
        });

        var result = await removal;
        await other;

        Assert.Equal((ChatLogImportRemovalStatus.Removed, 14, 11), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("deadlock", StringComparison.Ordinal));
        await using var verify = CreateDbContext();
        Assert.Equal(0, await ImportedRowCountAsync(verify, main.Channel.Id));
        Assert.Equal(1, await verify.AuditLogEntries.CountAsync());
    }

    // AK 12, retry limit: a deadlock on every attempt is retried twice and then reaches the caller. The
    // 40P01 is injected right after the coverage delete, so the rollback of the earlier deletes is what
    // keeps the channel unchanged. Mutation probe: a retry loop without the limit never returns.
    [Fact]
    public async Task Race_ADeadlockOnTheLastAttempt_ReachesTheCaller_AndNothingIsWritten()
    {
        var main = await SeedMainScenarioAsync("rmdeadlockfinal");
        var logger = new RecordingLogger<ChatLogImportRemovalService>();
        ChannelHashes before;
        await using (var read = CreateDbContext())
        {
            before = await ChannelHashesAsync(read, main.Channel.Id);
        }

        var attempts = 0;
        var hook = new CommandHook(
            c => c.Contains("DELETE", StringComparison.Ordinal) && c.Contains("ChatLogBackfillCoverage", StringComparison.Ordinal),
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected);
            });
        await using var removalDb = CreateDbContext(hook);

        var thrown = await Record.ExceptionAsync(
            () => CreateService(removalDb, logger).RemoveImportedAsync(main.Channel.ChannelName, main.Expectation, Actor));

        Assert.True(DatabaseErrors.IsDeadlock(thrown!), $"Expected a deadlock, got {thrown}");
        Assert.Equal(3, attempts);
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("deadlock", StringComparison.Ordinal)));
        await using var verify = CreateDbContext();
        Assert.Equal(before, await ChannelHashesAsync(verify, main.Channel.Id));
        Assert.False(await verify.AuditLogEntries.AnyAsync());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // ---------------------------------------------------------------- Helpers

    private AppDbContext CreateDbContext() => fixture.CreateDbContext(DatabaseName);

    private AppDbContext CreateDbContext(IInterceptor interceptor)
    {
        using var plain = CreateDbContext();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(plain.Database.GetConnectionString())
            .AddInterceptors(interceptor)
            .Options;
        return new AppDbContext(options);
    }

    private ChatLogImportRemovalService CreateService(AppDbContext db, ILogger<ChatLogImportRemovalService>? logger = null) =>
        new(db, _publisher, logger ?? NullLogger<ChatLogImportRemovalService>.Instance);

    private async Task<ChatLogImportRemovalResult> RemoveAsync(
        string channelName, IReadOnlyList<ExpectedCoverageInterval> expectation, ILogger<ChatLogImportRemovalService>? logger = null)
    {
        await using var db = CreateDbContext();
        return await CreateService(db, logger).RemoveImportedAsync(channelName, expectation, Actor);
    }

    // Refused or no-op: the answer, and every table of the channel, the audit log and the publisher unchanged.
    private async Task AssertRefusedWithoutWritesAsync(
        Channel channel, IReadOnlyList<ExpectedCoverageInterval> expectation, ChatLogImportRemovalStatus expected)
    {
        ChannelHashes before;
        await using (var read = CreateDbContext())
        {
            before = await ChannelHashesAsync(read, channel.Id);
        }

        var result = await RemoveAsync(channel.ChannelName, expectation);

        Assert.Equal((expected, 0, 0, 0), (result.Status, result.RemovedUsageRows, result.RemovedCoverageDays, result.RemovedSeeds));
        await using var verify = CreateDbContext();
        Assert.Equal(before, await ChannelHashesAsync(verify, channel.Id));
        Assert.False(await verify.AuditLogEntries.AnyAsync());
        Assert.Empty(_publisher.ReceivedCalls());
    }

    // The main scenario: two emotes, three settled slots, imported rows in two sets (A: Apr 1–7 on two
    // slots, B: Apr 10–13 on a third) under a coverage of two intervals with a gap, live rows from the
    // counting start on, on the same slots and in both sets, and the finished run the coverage names.
    // 14 imported rows, 11 coverage days, 5 live rows.
    private async Task<MainScenario> SeedMainScenarioAsync(string name, bool isBotActive = true)
    {
        await using var db = CreateDbContext();
        var channel = await AddChannelAsync(db, name, SetA, isBotActive);
        var e1 = await AddEmoteAsync(db, channel, $"{name}-1");
        var e2 = await AddEmoteAsync(db, channel, $"{name}-2");
        var s1a = await AddSlotAsync(db, e1, "OneA");
        var s1b = await AddSlotAsync(db, e1, "OneB");
        var s2 = await AddSlotAsync(db, e2, "Two");

        await AddUsageAsync(db, s1a, SetA, April1, April1.AddDays(7), UsageStatSource.ChatLogArchive);
        await AddUsageAsync(db, s2, SetA, April1, April1.AddDays(3), UsageStatSource.ChatLogArchive);
        await AddUsageAsync(db, s1b, SetB, April1.AddDays(9), April1.AddDays(13), UsageStatSource.ChatLogArchive);

        await AddUsageAsync(db, s1a, SetA, May1, May1.AddDays(2), UsageStatSource.Live, useCount: 7);
        await AddUsageAsync(db, s1b, SetA, May1.AddDays(1), May1.AddDays(2), UsageStatSource.Live, useCount: 5);
        await AddUsageAsync(db, s2, SetB, May1.AddDays(4), May1.AddDays(6), UsageStatSource.Live, useCount: 2);

        var run = await AddRunAsync(db, channel, ChatLogBackfillRunStatus.Completed, SetA, new SnapshotEntry(e1, s1a), new SnapshotEntry(e2, s2));
        await AddCoverageAsync(db, channel, April1, April1.AddDays(7), SetA, runId: run.Id);
        await AddCoverageAsync(db, channel, April1.AddDays(9), April1.AddDays(13), SetB, runId: run.Id);

        return new MainScenario(
            channel,
            [
                new ExpectedCoverageInterval(April1, April1.AddDays(7), SetA, ArchiveHost),
                new ExpectedCoverageInterval(April1.AddDays(9), April1.AddDays(13), SetB, ArchiveHost),
            ]);
    }

    // S12's state: one emote with an unsettled seed carrying three imported rows of set A, and the
    // authoritative active slot of the same emote with one live row (the settle target).
    private async Task<SettleScenario> SeedSettleScenarioAsync(string name)
    {
        await using var db = CreateDbContext();
        var channel = await AddChannelAsync(db, name, SetA);
        var emote = await AddEmoteAsync(db, channel, $"{name}-e");
        var seedSlot = await AddSlotAsync(db, emote, "Seed", seedUnsettled: true);
        var target = await AddSlotAsync(db, emote, "Target");
        await AddUsageAsync(db, seedSlot, SetA, April1, April1.AddDays(3), UsageStatSource.ChatLogArchive);
        await AddUsageAsync(db, target, SetA, May1, May1.AddDays(1), UsageStatSource.Live);
        await AddCoverageAsync(db, channel, April1, April1.AddDays(3), SetA);
        return new SettleScenario(channel, emote, seedSlot, target, [new ExpectedCoverageInterval(April1, April1.AddDays(3), SetA, ArchiveHost)]);
    }

    private async Task<string> FirstImportedEmoteIdAsync(string channelId)
    {
        await using var db = CreateDbContext();
        return await db.UsageStats
            .Where(u => u.Emote.ChannelId == channelId && u.Source == UsageStatSource.ChatLogArchive)
            .Select(u => u.EmoteId)
            .OrderBy(id => id)
            .FirstAsync();
    }

    // The predicate of docs/Operations.md "Rolling back", step 3, as a read.
    private static Task<List<string>> PlaceholderCleanupCandidatesAsync(AppDbContext db) =>
        db.Database.SqlQuery<string>(
            $"""
             SELECT e."Id" AS "Value" FROM "Emotes" e
             WHERE e."IsPlaceholder"
               AND EXISTS (SELECT 1 FROM "ChatLogBackfillRunEmotes" r WHERE r."EmoteId" = e."Id" AND r."CreatedRow")
               AND NOT EXISTS (SELECT 1 FROM "Votes" v WHERE v."EmoteId" = e."Id")
               AND NOT EXISTS (SELECT 1 FROM "VoteSessionEmotes" s WHERE s."EmoteId" = e."Id")
               AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."EmoteId" = e."Id" AND u."Source" = 0)
             """).ToListAsync();

    // Step 3b exactly as shipped, inside a transaction that is rolled back: how many slots it would delete.
    private static async Task<int> CountCreatedSlotCleanupAsync(AppDbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var deleted = await db.Database.ExecuteSqlRawAsync(EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql);
        await tx.RollbackAsync();
        return deleted;
    }

    private sealed record MainScenario(Channel Channel, IReadOnlyList<ExpectedCoverageInterval> Expectation);

    private sealed record SettleScenario(
        Channel Channel, Emote Emote, EmoteSlot Seed, EmoteSlot Target, IReadOnlyList<ExpectedCoverageInterval> Expectation);

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
