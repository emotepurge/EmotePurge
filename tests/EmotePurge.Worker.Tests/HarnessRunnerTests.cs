using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Entities;
using EmotePurge.Core.Services;
using EmotePurge.Worker.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The run itself: preconditions, resume, the abort exit codes and the idempotent closing step. The
// archive client and both query services are substituted, so no test here opens a socket — what is
// under test is the decision sequence around them, which is exactly where a wrong exit code would
// turn "the harness stopped" into "the harness said the import is fine".
public class HarnessRunnerTests : IDisposable
{
    private const string ChannelId = "channel-guid";
    private const string TwitchChannelId = "12345";
    private const string ChannelName = "brudivoeller_tv";

    // The process start; the window therefore ends on 2026-09-04 (yesterday, the last complete UTC
    // day) and, at --days 3, starts on 2026-09-02.
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day1 = new(2026, 9, 2);
    private static readonly DateOnly Day2 = new(2026, 9, 3);
    private static readonly DateOnly Day3 = new(2026, 9, 4);

    // A read-side mirror of HarnessReportFile's private ValueListJsonConverterFactory, at the class
    // end (Worker has no InternalsVisibleTo to this test project): System.Text.Json cannot
    // deserialize ValueList<T> on its own — it satisfies none of the recognized collection shapes —
    // and .report.json is full of it (GateIneligibleReasons, KHistogram, the day-ratio lists, and
    // now Warnings).
    private static readonly JsonSerializerOptions ReadReportOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new ValueListJsonConverterFactory() } };

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "emotepurge-harness-run-" + Guid.NewGuid().ToString("N"));

    // One clock for the whole test, not one per Run(...) call: the resume tests need to move the
    // process start between two invocations of the same file.
    private readonly FakeClock _clock = new(Now);

    private readonly IChannelService _channels = Substitute.For<IChannelService>();
    private readonly IUsageStatQueryService _usage = Substitute.For<IUsageStatQueryService>();
    private readonly IChatLogArchiveClient _archive = Substitute.For<IChatLogArchiveClient>();
    private readonly IBotChatterDetector _bots = Substitute.For<IBotChatterDetector>();
    private readonly IExcludedChatterFilter _excludedChatters = Substitute.For<IExcludedChatterFilter>();

    public HarnessRunnerTests()
    {
        Directory.CreateDirectory(_directory);

        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>()).Returns(NewChannel());
        _usage.GetEmoteLifetimesAsync(ChannelId, Arg.Any<CancellationToken>()).Returns(Lifetimes());
        _usage.GetRowsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Rows());
        // Deliberately NOT the Run helper's default shared-chat cutover ("2026-09-01"): the two
        // cutovers land in adjacent markdown rows, and while they shared a value an assertion on
        // either date was satisfied by the other one's cell — hardcoding a cutover cell would have
        // gone unnoticed.
        _usage.GetEarliestBotUsageDateAsync(ChannelId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 8, 30));
        _bots.KnownBotAccountIds.Returns(new HashSet<string> { "19264788" });
        _bots.IsBot(Arg.Any<string?>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>()).Returns(false);
        _excludedChatters.IsExcluded(Arg.Any<string?>()).Returns(false);
        _excludedChatters.ExcludedChatterIds.Returns(new HashSet<string>());
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingTwitchChannelId_AbortsBeforeTheFirstFetch()
    {
        // #34/#44: no login fallback. Without the immutable id there is nothing safe to address.
        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>())
            .Returns(NewChannel(twitchChannelId: null));

        var exitCode = await Run(3);

        Assert.Equal(3, exitCode);
        Assert.Empty(Directory.GetFiles(_directory));
        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
    }

    [Fact]
    public async Task AnUnknownChannel_Aborts()
    {
        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>()).Returns((Channel?)null);

        Assert.Equal(3, await Run(3));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    // Fail-closed shared-chat cutover (D4, #73 Task 6): a missing, blank or unparsable cutover
    // must abort before any database or archive access, so a forgotten configuration value can
    // never be read weeks later as "the gate was fine". The precondition sits ahead of even the
    // channel lookup, which is why these mock the channel service too — a passing test here must
    // never have exercised it.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingOrBlankSharedChatCutover_WithoutDiagnostic_AbortsBeforeAnyAccess(string? cutover)
    {
        var exitCode = await Run(3, sharedChatCutover: cutover);

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Empty(Directory.GetFiles(_directory));
        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
        await _channels.DidNotReceiveWithAnyArgs().GetByNameAsync(default!, default);
    }

    [Fact]
    public async Task AnUnparsableSharedChatCutover_AbortsEvenWithoutTheChannelLookup()
    {
        var exitCode = await Run(3, sharedChatCutover: "2026-13-01");

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Empty(Directory.GetFiles(_directory));
        await _channels.DidNotReceiveWithAnyArgs().GetByNameAsync(default!, default);
    }

    [Fact]
    public async Task AnUnparsableSharedChatCutover_IsAnErrorEvenWithDiagnostic()
    {
        // The one case D4 insists on: a value that IS set but does not parse must never read as
        // "no cutover", diagnostic or not — a typo must not silently pass as an intentional
        // diagnostic run.
        var exitCode = await Run(3, sharedChatCutover: "2026-13-01", diagnostic: true);

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Empty(Directory.GetFiles(_directory));
        await _channels.DidNotReceiveWithAnyArgs().GetByNameAsync(default!, default);
    }

    [Fact]
    public async Task AMissingSharedChatCutover_WithDiagnostic_RunsAndReportsNoVerdictInsteadOfAborting()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        var exitCode = await Run(3, sharedChatCutover: null, diagnostic: true);

        Assert.Equal(0, exitCode);
        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("diagnostic-run", json);
        // The final .report.json (unlike the header line) does not ignore nulls, so the field is
        // present but null here — no cutover was ever configured for this run.
        Assert.Contains("\"sharedChatCutover\": null", json);

        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.Contains("| Shared-Chat-Stichtag | keiner (Diagnoselauf) |", markdown);
        // The run-mode cell has its own wording ("Diagnose"), and only this assertion reaches it:
        // "Diagnoselauf" above is the cutover row's text and would stay green even if the run-mode
        // cell were hardcoded to "bindend" — a diagnostic report claiming to be binding.
        Assert.Contains("| Lauf-Modus | Diagnose |", markdown);
    }

    [Fact]
    public async Task ADifferentSharedChatCutover_StartsANewFileAndLeavesTheOldOneUnfinished()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3, sharedChatCutover: "2026-09-01"));
        var firstFile = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3, sharedChatCutover: "2026-09-02"));

        var jsonlFiles = Directory.GetFiles(_directory, "*.jsonl");
        Assert.Equal(2, jsonlFiles.Length);
        Assert.Contains(firstFile, jsonlFiles);
        // The first (interrupted) file never got its final report; only the second run's identity
        // produced one.
        Assert.Single(Directory.GetFiles(_directory, "*.report.json"));
    }

    [Fact]
    public async Task AMeasurementShorterThanTheWindow_Aborts()
    {
        // Joined on 2026-09-02, so the first fully tracked day is 2026-09-03 — two days, not three.
        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>())
            .Returns(NewChannel(createdAt: new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(3, await Run(3));
        Assert.Empty(Directory.GetFiles(_directory));
        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
    }

    [Fact]
    public async Task AWindowWithoutASingleLogDay_AbortsButKeepsTheFileThatProvesTheRequests()
    {
        RespondWith(_ => NoLogDay());

        Assert.Equal(3, await Run(3));

        var file = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        Assert.Equal(3, File.ReadAllLines(file).Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Empty(Directory.GetFiles(_directory, "*.report.json"));
    }

    [Fact]
    public async Task LogsWithoutBadgesAndWithoutUserIds_EndTheRunAsUndecidable()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(new ChatLogMessage(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), null, [], "12345", null, false, "PogChamp"));
            return CompleteDay(1);
        });

        var exitCode = await Run(3);

        Assert.Equal(5, exitCode);
        // Only the first day was fetched; the verdict is "reassess the approach", not "fetch more".
        await _archive.Received(1).ReadDayAsync(
            TwitchChannelId, Arg.Any<DateOnly>(), Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        var file = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        Assert.DoesNotContain("\"kind\":\"day\"", File.ReadAllText(file));
    }

    [Fact]
    public async Task ANoBadgesNoUserIdsAbort_SpendsFromTheCapUsingTheReceivedBytes()
    {
        // The day was read to completion (Complete, MessageCount > 0) before the fallback fired, so
        // it already cost the archive real bytes — the same accounting the TransportFailure/
        // ByteCapExceeded paths get via the `default:` branch (see
        // ADayThatAbortedWithoutABodyStillSpendsFromTheCap...). Befund: this call dropped the
        // `bytes` argument, so the event line read 0 and a resume would see the full cap again
        // instead of what this already-spent request actually cost.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(new ChatLogMessage(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), null, [], "12345", null, false, "PogChamp"));
            return CompleteDay(1, bytes: 900_000);
        });

        Assert.Equal(5, await Run(3, maxMegabytes: 1));

        var path = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var noBadgesEvent = Assert.Single(
            new HarnessReportFile(path).ReadDays().Events, e => e.Status == "NoBadgesNoUserIds");
        Assert.Equal(900_000, noBadgesEvent.Bytes);

        // Second run: a compliant day now answers, but the cap must already reflect the 900 KB the
        // first, undecidable attempt spent — not the full 1 MB again.
        var offeredOnResume = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        }, offeredOnResume);

        Assert.Equal(0, await Run(3, maxMegabytes: 1));

        Assert.Equal((1L * 1024 * 1024) - 900_000, offeredOnResume[0]);
    }

    [Fact]
    public async Task ARateLimitedDay_StopsWithAResumePoint_AndTheNextRunContinuesThere()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(4, await Run(3));

        var path = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"event\"")));
        Assert.Empty(Directory.GetFiles(_directory, "*.report.json"));

        // Second run, same substitutes, but the archive answers day 3 now.
        _archive.ClearReceivedCalls();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        await _archive.Received(1).ReadDayAsync(
            TwitchChannelId, Day3, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ReadDayAsync(
            TwitchChannelId, Day1, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        await _archive.DidNotReceive().ReadDayAsync(
            TwitchChannelId, Day2, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());

        var reportPath = Assert.Single(Directory.GetFiles(_directory, "*.report.json"));
        var afterSecondRun = File.ReadAllBytes(reportPath);

        // The 429 of the first run survives in the file and reaches both reports as a *number*.
        // Asserting on the string "429" alone would also match the label of the markdown row and
        // pass with zero throttled requests.
        Assert.Contains("\"rateLimitedDays\": 1", File.ReadAllText(reportPath));
        Assert.Contains("\"resumePoint\": \"2026-09-04\"", File.ReadAllText(reportPath));
        Assert.Contains("| HTTP 429 | 1 |", File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md"))));

        // Third run: everything is on disk, nothing is fetched, and the machine-readable report is
        // byte-identical — that is the "closing step is repeatable" promise of the failure-mode table.
        _archive.ClearReceivedCalls();
        Assert.Equal(0, await Run(3));

        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
        Assert.Equal(afterSecondRun, File.ReadAllBytes(reportPath));
    }

    [Fact]
    public async Task ATransportFailure_StopsWithExitFourAndWithoutADayLine()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 17, null, 0, 0, 0, 502);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(4, await Run(3));

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"event\"")));
    }

    [Fact]
    public async Task ACancelledRun_StopsWithExitFourAndKeepsTheDaysItHad()
    {
        using var cts = new CancellationTokenSource();
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                await cts.CancelAsync();
                throw new OperationCanceledException(cts.Token);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(4, await Run(3, ct: cts.Token));

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"day\"")));
    }

    [Fact]
    public async Task TheByteCap_ShrinksWithEveryDayAndStopsTheRunWhenItIsSpent()
    {
        var offered = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 600_000);
        }, offered);

        // 1 MB cap, 600 KB per day: day 1 fits, day 2 fits (the cap is checked before the request,
        // not predicted), day 3 finds nothing left.
        Assert.Equal(4, await Run(3, maxMegabytes: 1));

        Assert.Equal(2, offered.Count);
        Assert.Equal(1L * 1024 * 1024, offered[0]);
        Assert.Equal(1L * 1024 * 1024 - 600_000, offered[1]);
    }

    [Fact]
    public async Task ADayThatAbortedWithoutABodyStillSpendsFromTheCap_AndTheNextRunKnowsIt()
    {
        // Day 1 fails via TransportFailure after receiving 900 KB — a wasted transfer that produces
        // only an event line, never a day line. If those bytes were forgotten on resume (Befund 1),
        // the second run would see the full 1 MB cap again instead of the roughly 148 KB actually
        // left; five such resumes could each burn a fresh cap's worth against a service that never
        // agreed to any of it.
        RespondWith(_ => new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 900_000, null, 0, 0, 0, 502));

        Assert.Equal(4, await Run(3, maxMegabytes: 1));
        Assert.DoesNotContain(
            Directory.GetFiles(_directory, "*.jsonl").SelectMany(File.ReadAllLines),
            l => l.Contains("\"kind\":\"day\""));

        var offeredOnResume = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        }, offeredOnResume);

        Assert.Equal(0, await Run(3, maxMegabytes: 1));

        // 1 MB minus the 900 KB already wasted by the aborted first attempt, not the full 1 MB again.
        Assert.Equal((1L * 1024 * 1024) - 900_000, offeredOnResume[0]);
    }

    [Fact]
    public async Task ACompleteRun_ReportsTotalBytesIncludingBytesBookedByAnAbortedAttempt()
    {
        // Befund B (external review): a TransportFailure never becomes a day line, only an event
        // line with its own Bytes — same case as
        // ADayThatAbortedWithoutABodyStillSpendsFromTheCap_AndTheNextRunKnowsIt above, but carried
        // all the way to a finished report. ReplayFidelityCalculator.Compute must not derive
        // run.totalBytes from the day lines alone, or the 900 KB this attempt cost the archive
        // silently vanishes from the machine-readable report even though the byte-cap bookkeeping
        // (bytesUsed) already counts it.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                return new ChatLogDayResult(ChatLogDayStatus.TransportFailure, 900_000, null, 0, 0, 0, 502);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        });
        Assert.Equal(4, await Run(3));

        // Resume: day 1's line is already on disk, so only day 2 and day 3 are fetched again.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 5_000);
        });
        Assert.Equal(0, await Run(3));

        // 10,000 (day 1, first run) + 900,000 (day 2's aborted attempt, event line only) + 5,000
        // (day 2, second run) + 5,000 (day 3, second run) = 920,000. The buggy calculation
        // (days.Sum(d => d.Bytes)) would report only 20,000 — the three day lines without the event.
        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"totalBytes\": 920000", json);
    }

    [Fact]
    public async Task ACompleteRun_WritesBothReportsAndTheGateFieldsOfTheCalculator()
    {
        // Day 2 alone carries a SourceRoomId distinct from RoomId, so exactly one of the three
        // messages is a shared-chat hit — this is the third seam of Befund 2 (Abschluss-Review): a
        // mapping bug that swapped RoomId and SourceRoomId in the `counter.Count(...)` call at the
        // HarnessRunner call site would mark every message as shared chat instead of exactly one,
        // which the asserted sharedChatMessages below catches. A day where both fields were the same
        // (or both null, as the previous fixture had it) could not tell the two apart.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp", sourceRoomId: day == Day2 ? "other-room" : null));
            return CompleteDay(1);
        });
        // The live side saw the same foreign hit on the same day (#73 Task 7): both sides report one
        // shared-chat hit, so the symmetry condition is satisfied and the run stays certifiable.
        _usage.GetRowsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Rows(day2SharedChatUseCount: 1));

        Assert.Equal(0, await Run(3));

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"gateEligible\"", json);
        Assert.Contains("\"totalDeviation\"", json);
        Assert.Contains("\"top20Recall\"", json);
        // A three-day run can never be a gate run — the pre-registration fixes the window at 30.
        Assert.Contains("window-not-30-days", json);
        // The window length the run was started with reaches the report unchanged; a report that
        // claimed 30 here would read as a gate run.
        Assert.Contains("\"windowDays\": 3", json);
        Assert.Contains("\"windowFrom\": \"2026-09-02\"", json);
        Assert.Contains("\"windowTo\": \"2026-09-04\"", json);
        Assert.Contains("\"runComplete\": true", json);
        // All three days are rated (harness-2, #73 Task 7). Day 2's PogChamp hit sits in
        // SharedChatCounts rather than HumanCounts, but the *day total* counts all three components
        // on both sides, so day 2 reads 1 against 8 (UseCount 0 + BotUseCount 7 +
        // SharedChatUseCount 1) — the same total as days 1 and 3, so the ratio lands exactly on the
        // median and CoverageQuestionable stays clear. Under the two-component day total this task
        // replaced, day 2's log total was 0 and the coverage check dropped it, which is exactly the
        // interaction that would have cost a heavily-shared channel its rated days.
        Assert.Contains("\"ratedDays\": 3", json);
        // Befund 2 (Abschluss-Review): these values, not just their key names, pin the mapping seams
        // between the query DTOs and the harness's own replay types. Each was verified to fail under
        // its corresponding one-line mutation at the HarnessRunner call sites (see the final-fix
        // report) before this test was written this way.
        Assert.Contains("\"humanLogTotal\": 2", json); // day 2's hit is foreign, so only days 1 and 3 contribute
        Assert.Contains("\"humanLiveTotal\": 2", json); // UseCount 1 + 0 + 1 over the rated days; BotUseCount=7 must not leak in
        Assert.Contains("\"sharedChatMessages\": 1", json); // only day 2's message carries a foreign SourceRoomId
        // Both sides of the split, reported apart so a reader sees it instead of inferring it.
        Assert.Contains("\"sharedChatLogTotal\": 1", json);
        Assert.Contains("\"sharedChatLiveTotal\": 1", json);
        Assert.Contains("\"sharedChatByDay\"", json);
        Assert.DoesNotContain(ReplayGateIneligibleReasons.SharedChatAsymmetric, json);
        // #97: the tie-safe quartile sets and the top-20 tie counts round-trip through the JSON
        // report. A single-emote population makes every one of these trivially 1 — the point here is
        // that the new fields exist and serialize, not their value on this particular fixture (that
        // is covered in depth by ReplayFidelityCalculatorTests).
        Assert.Contains("\"bottomQuartileLiveSize\": 1", json);
        Assert.Contains("\"bottomQuartileLogSize\": 1", json);
        Assert.Contains("\"top20LiveTieCount\": 1", json);
        Assert.Contains("\"top20LogTieCount\": 1", json);
        Assert.Contains("\"tailDeviation\"", json);
        // The Run helper's default cutover ("2026-09-01", before Day1) reaches the identity and the
        // report unchanged — the fixture setting it, not a derived value, is what "ratedDays": 3
        // above stands on now that HumanOnly keys off this field instead of BotSplitCutover.
        Assert.Contains("\"sharedChatCutover\": \"2026-09-01\"", json);

        var jsonl = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Contains("\"algorithmVersion\":\"harness-3\"", jsonl);
        // Day 2's foreign hit lands in the day line's own dictionary, not just the aggregated report.
        Assert.Contains("\"sharedChatCounts\":{\"e1\":1}", jsonl);
        Assert.Contains("\"sharedChatCutover\":\"2026-09-01\"", jsonl);

        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.Contains("Replay-Treue", markdown);
        Assert.Contains("Uptime Kuma", markdown);
        Assert.Contains(ChannelName, markdown);
        // Both cutover cells carry distinct dates, so neither assertion can be satisfied by the
        // other row (see the fixture remark at GetEarliestBotUsageDateAsync).
        Assert.Contains("| Shared-Chat-Stichtag | 2026-09-01 |", markdown);
        Assert.Contains("| Bot-Split-Stichtag | 2026-08-30 |", markdown);
        Assert.Contains("| Lauf-Modus | bindend |", markdown);
        // The human reads the split off the markdown, not only off the JSON.
        Assert.Contains("Shared Chat ΣLog / ΣLive (bewertete Tage) | 1 / 1", markdown);
        Assert.Contains("Shared Chat (Log)", markdown);
        Assert.Contains("Shared Chat (Live)", markdown);
        // Day 2's row: the foreign hit on both sides, next to the human columns that stay at 0.
        Assert.Contains("| 2026-09-03 | Complete | 1024 | 1 | 0 | 0 | 0 | 1 | 1 |", markdown);
        // #97: the quartile's tie-safe set sizes and the tail-deviation row, explicitly marked as
        // not a gate figure.
        Assert.Contains("Quartilsgröße (nominal)", markdown);
        Assert.Contains("keine Gate-Kennzahl, nur Bericht", markdown);
    }

    // GDPR Art. 21 objection gate (issue #252/#260): the harness must honour
    // Twitch:ExcludedChatterIds exactly like the live worker does, so a replay of the archive cannot
    // resurface what an objecting chatter's live traffic no longer produces.
    [Fact]
    public async Task AnExcludedChatterId_MessageIsNotCounted()
    {
        _excludedChatters.IsExcluded("objector-1").Returns(true);

        RespondWith(async (day, onMessage) =>
        {
            // Two chatters hit the same emote on the same day; only the non-excluded one may reach
            // the counter. The non-excluded message also keeps sawUserId/sawBadges set, so the run
            // does not fall back to ExitUndecidable for lack of either signal.
            await onMessage(Message(day, "objector-1", "PogChamp"));
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(2);
        });

        Assert.Equal(0, await Run(3));

        var jsonl = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        // One hit per day, not two: the excluded chatter's message never reached ReplayDayCounter.
        Assert.Contains("\"humanCounts\":{\"e1\":1}", jsonl);
        Assert.DoesNotContain("\"humanCounts\":{\"e1\":2}", jsonl);
    }

    // P2 Codex finding (issue #260, this revision): before the fix, the NoBadgesNoUserIds fallback
    // keyed off the archive's raw MessageCount, which stayed positive even when every one of that
    // day's messages was dropped by the exclusion gate above — sawUserId/sawBadges never got set for
    // a message that never reached the bookkeeping, so the run wrongly concluded the *logs* carried
    // no badges or user-ids and aborted with ExitUndecidable. Day 1 here is exactly that case: its
    // only chatter is excluded. Days 2 and 3 carry an ordinary message each, so a passing run proves
    // both that day 1 became a valid zero-count day and that the fallback still fires correctly once
    // a later day actually has a gated message to check.
    [Fact]
    public async Task ADayWhereEveryMessageIsFromAnExcludedChatter_RecordsAZeroCountDayInsteadOfAborting()
    {
        _excludedChatters.IsExcluded("objector-1").Returns(true);

        RespondWith(async (day, onMessage) =>
        {
            if (day == Day1)
            {
                await onMessage(Message(day, "objector-1", "PogChamp"));
            }
            else
            {
                await onMessage(Message(day, "chatter-1", "PogChamp"));
            }

            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var path = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var content = new HarnessReportFile(path).ReadDays();
        Assert.DoesNotContain(content.Events, e => e.Status == "NoBadgesNoUserIds");
        Assert.Equal(3, content.Days.Count);
        var day1Line = Assert.Single(content.Days, d => d.Day == Day1);
        Assert.Equal(ReplayDayStatuses.Complete, day1Line.Status);
        Assert.Empty(day1Line.HumanCounts);
    }

    [Fact]
    public async Task NoExcludedChatterIds_MessagesAreCountedExactlyAsBefore()
    {
        // The constructor's default IsExcluded(...) => false fixture stands in for an empty/missing
        // Twitch:ExcludedChatterIds: the gate must change nothing about the counting path when it has
        // nothing to exclude.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var jsonl = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Contains("\"humanCounts\":{\"e1\":1}", jsonl);
    }

    [Fact]
    public async Task ACompleteRun_WithoutTheLiveSideOfTheSharedChatSplit_IsNotCertifiable()
    {
        // The negative of the run above and the whole point of the condition (D3): the replay side
        // classified one hit as foreign, the live side classified none. That disagreement is about
        // the classification #73 introduced — the one thing this run exists to certify — so the run
        // is refused rather than being handed a fidelity number computed on top of it. The default
        // Rows() fixture carries SharedChatUseCount = 0 throughout, which is exactly the pre-deploy
        // signature of D3.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp", sourceRoomId: day == Day2 ? "other-room" : null));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"sharedChatLogTotal\": 1", json);
        Assert.Contains("\"sharedChatLiveTotal\": 0", json);
        Assert.Contains(ReplayGateIneligibleReasons.SharedChatAsymmetric, json);

        // The two shared-chat day columns are asserted here rather than in the symmetric run above:
        // there both carry 1, so a swap of the two would be invisible. Day 2 is the only day with a
        // foreign hit, and only on the log side.
        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.Contains("Shared Chat ΣLog / ΣLive (bewertete Tage) | 1 / 0", markdown);
        Assert.Contains("| 2026-09-02 | Complete | 1024 | 1 | 0 | 1 | 1 | 0 | 0 |", markdown);
        Assert.Contains("| 2026-09-03 | Complete | 1024 | 1 | 0 | 0 | 0 | 1 | 0 |", markdown);
    }

    [Fact]
    public async Task AFileWithTheOldAlgorithmVersion_IsNotResumed()
    {
        // Regression guard for the harness-2 bump (#73): a "harness-1" file left over from before the
        // shared-chat rule counted every message as own. FindFrozenWindow's AlgorithmVersion
        // comparison must exclude it from window discovery.
        //
        // The leftover's own file identity can never be the file this run ends up writing to —
        // AlgorithmVersion is itself part of the identity BuildFileName hashes, so a "harness-1"
        // header always produces a different digest/path than this ("harness-2") run computes for
        // itself, whether or not the version check exists. Asserting on file count or fetch count
        // alone would therefore prove nothing (a run without a single day line to inherit fetches
        // every day again regardless of whether a window was "inherited").
        //
        // What the version check actually guards is FindFrozenWindow's separate WINDOW discovery: it
        // only borrows (WindowFrom, WindowTo) from a matching candidate, not the whole identity. So
        // the leftover's window is set one day earlier than the window this run would freshly derive
        // (2026-09-01..2026-09-03 instead of 2026-09-02..2026-09-04) — still a same-length, still
        // resumable-age candidate. If the AlgorithmVersion comparison were ever removed, this stale
        // window would be adopted and the report would carry "windowFrom": "2026-09-01"; with the
        // check intact, the run must derive its own fresh window instead.
        var staleWindowFrom = Day1.AddDays(-1);
        var staleWindowTo = Day3.AddDays(-1);
        var leftoverIdentity = new HarnessRunIdentity(
            ChannelId, TwitchChannelId, ChannelName, staleWindowFrom, staleWindowTo, new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 1), ["19264788"], ExcludedChatterIdsDigest.Compute([]), "harness-1", new string('a', 64));
        var leftover = new HarnessReportFile(Path.Combine(_directory, "leftover-harness-1.jsonl"));
        leftover.WriteHeader(new HarnessReportHeader(leftoverIdentity, DateTime.UtcNow));

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"windowFrom\": \"2026-09-02\"", json);
        Assert.Contains("\"windowTo\": \"2026-09-04\"", json);

        Assert.True(File.Exists(leftover.Path));
        Assert.Equal(2, Directory.GetFiles(_directory, "*.jsonl").Length);
        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
    }

    // RecomputeReportAsync accepts "harness-2" (see HarnessRunner.PriorRecomputableAlgorithmVersion),
    // but RunAsync/ExecuteAsync must not: FindFrozenWindow only ever compares against
    // HarnessRunner.AlgorithmVersion ("harness-3"), so a "harness-2" leftover is exactly as invisible
    // to window discovery as a "harness-1" one above — a fetching run only ever resumes its own
    // current algorithm, never an older one, no matter how close. Mirrors
    // AFileWithTheOldAlgorithmVersion_IsNotResumed's proof technique for the same reason: the file
    // name digest already differs regardless of the check, so only the adopted-window assertion below
    // actually exercises it.
    [Fact]
    public async Task AFileWithTheHarnessTwoAlgorithmVersion_IsNotResumed()
    {
        var staleWindowFrom = Day1.AddDays(-1);
        var staleWindowTo = Day3.AddDays(-1);
        var leftoverIdentity = new HarnessRunIdentity(
            ChannelId, TwitchChannelId, ChannelName, staleWindowFrom, staleWindowTo, new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 1), ["19264788"], null!, HarnessRunner.PriorRecomputableAlgorithmVersion, new string('a', 64));
        var leftover = new HarnessReportFile(Path.Combine(_directory, "leftover-harness-2.jsonl"));
        leftover.WriteHeader(new HarnessReportHeader(leftoverIdentity, DateTime.UtcNow));

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"windowFrom\": \"2026-09-02\"", json);
        Assert.Contains("\"windowTo\": \"2026-09-04\"", json);

        Assert.True(File.Exists(leftover.Path));
        Assert.Equal(2, Directory.GetFiles(_directory, "*.jsonl").Length);
        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
    }

    [Fact]
    public async Task TheWindowWideChatterCount_CountsOnlyOwnHumans()
    {
        // Spec B5: the window-wide "distinct chatters" figure follows the same own/foreign rule as
        // the per-day counter. Three chatters, three messages, one foreign and one bot — only the
        // remaining own human counts.
        _bots.IsBot(Arg.Any<string?>(), Arg.Any<IReadOnlyList<KeyValuePair<string, string>>?>())
            .Returns(call => call.ArgAt<string?>(0) == "bot-1");

        RespondWith(async (day, onMessage) =>
        {
            if (day == Day1)
            {
                await onMessage(Message(day, "chatter-1", "PogChamp"));
            }
            else if (day == Day2)
            {
                await onMessage(Message(day, "chatter-2", "PogChamp", sourceRoomId: "other-room"));
            }
            else
            {
                await onMessage(Message(day, "bot-1", "PogChamp"));
            }

            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.Contains("| Distinkte Chatter im Fenster | 1 |", markdown);
    }

    [Fact]
    public async Task AnIndeterminateMessage_IsMappedThroughFromTheArchiveMessageToTheReport()
    {
        // Pins the HasOtherSourceMarkers wiring at the runner boundary, not just inside
        // ReplayDayCounter: the callback in HarnessRunner.RunAsync must pass
        // message.HasOtherSourceMarkers through to counter.Count(...) rather than a literal false.
        // A hardcoded false would silently reclassify every indeterminate message as this channel's
        // own usage — every test elsewhere in this class leaves HasOtherSourceMarkers at Message()'s
        // default of false, so nothing but this case would catch that regression.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                await onMessage(Message(day, "chatter-1", "PogChamp", hasOtherSourceMarkers: true));
            }
            else
            {
                await onMessage(Message(day, "chatter-1", "PogChamp"));
            }

            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var jsonl = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Contains("\"indeterminateMessageCount\":1", jsonl);

        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"indeterminateMessages\": 1", json);
    }

    [Fact]
    public async Task AResumedRun_ReportsTheDistinctChatterCountAsUnavailable()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.Contains("wiederaufgenommen", markdown);
    }

    [Fact]
    public async Task EveryDayOfTheWindow_IsFetchedExactlyOnce()
    {
        // Not a claim about the client instance — the runner takes that once through its constructor,
        // which is structural and needs no test. This is about the loop: three days, three requests,
        // no day asked for twice.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        await Run(3);

        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
        foreach (var day in new[] { Day1, Day2, Day3 })
        {
            await _archive.Received(1).ReadDayAsync(
                TwitchChannelId, day, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task AChangedDataSnapshot_StartsANewFileInsteadOfContinuingTheOldOne()
    {
        // The Codex-adversarial finding the input hash exists for: the live worker keeps writing
        // while the harness runs, and a resume against a different snapshot would mix day counts
        // taken against two different databases.
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));
        var firstFile = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        // One live usage row changes; everything else stays.
        _usage.GetRowsAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<UsageStatRowDto>>([new("e1", Day1, 99, 0, 0), new("e1", Day2, 1, 0, 0), new("e1", Day3, 1, 0, 0)]);
        _archive.ClearReceivedCalls();

        Assert.Equal(0, await Run(3));

        var files = Directory.GetFiles(_directory, "*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.Contains(firstFile, files);
        // And the new run really fetched all three days again rather than inheriting them.
        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
    }

    // GDPR Art. 21 objection gate (issue #252/#260, P1 Codex finding): saved day lines from before
    // an exclusion-list change must not be reused — the days a run already wrote under the old
    // policy would otherwise keep an excluded chatter's counts even after the policy changed,
    // silently mixed into the same report as days counted under the new one. Mirrors
    // AChangedDataSnapshot_StartsANewFileInsteadOfContinuingTheOldOne, but with an *interrupted*
    // first run — a completed run always starts its own new file regardless of resume logic, so
    // only a resume candidate actually exercises FindFrozenWindow/ReadHeader's identity comparison.
    [Fact]
    public async Task AChangedExclusionList_DoesNotResumeDaysSavedUnderTheOldOne()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(HarnessRunner.ExitAbortedWithResumePoint, await Run(3));
        var firstFile = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        // The operator adds an id to Twitch:ExcludedChatterIds between the two invocations.
        _excludedChatters.ExcludedChatterIds.Returns(new HashSet<string> { "objector-1" });
        _archive.ClearReceivedCalls();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(HarnessRunner.ExitSuccess, await Run(3));

        var files = Directory.GetFiles(_directory, "*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.Contains(firstFile, files);
        // A resume would have refetched only Day3 (the rate-limited one); a fresh run under the new
        // identity refetches the whole window instead.
        Assert.Equal(3, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
    }

    [Fact]
    public async Task ADamagedReportFile_EndsAsAViolatedPreconditionRatherThanAStackTrace()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));

        // A damaged line in the *middle* — not a truncated last line, which is a dropped unfinished
        // day and stays legal.
        var path = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var lines = File.ReadAllLines(path).ToList();
        lines.Insert(2, "{\"kind\":\"day\",\"day\":{\"day\":\"2026-0");
        File.WriteAllLines(path, lines);

        Assert.Equal(3, await Run(3));
    }

    [Fact]
    public async Task AConfiguredWindowOutsideTheAllowedRange_Aborts()
    {
        // --days is clamped by the parser; Harness:WindowDays reaches the runner unchecked, and
        // Harness__WindowDays=500 in a compose file would otherwise buy a 500-day run.
        Assert.Equal(3, await Run(500));
        Assert.Equal(3, await Run(0));
        Assert.Empty(Directory.GetFiles(_directory));
        await _archive.DidNotReceiveWithAnyArgs().ReadDayAsync(default!, default, default, default!, default);
    }

    [Fact]
    public async Task AFailingCountingCallback_EndsWithItsOwnExitCodeAndKeepsTheFinishedDays()
    {
        // Exceptions out of onMessage propagate by the archive client's contract (T3). They are our
        // bug, not the archive's, so they must not be dressed up as "aborted, just run it again" —
        // but they must not reach the operator as a stack trace either.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day2)
            {
                await onMessage(new ChatLogMessage(
                    day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), "chatter-1", null!, TwitchChannelId, null, false, "PogChamp"));
            }
            else
            {
                await onMessage(Message(day, "chatter-1", "PogChamp"));
            }

            return CompleteDay(1);
        });

        Assert.Equal(6, await Run(3));

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        Assert.Equal(1, lines.Count(l => l.Contains("\"kind\":\"day\"")));
        Assert.Empty(Directory.GetFiles(_directory, "*.report.json"));
    }

    [Fact]
    public async Task ARunContinuedOnTheNextUtcDay_KeepsItsFrozenWindowAndItsFile()
    {
        // The window is derived from the process start, so before this test every invocation on a
        // new UTC day produced a new window, a new identity and therefore a new file — the finished
        // days and the bytes already spent were silently abandoned. That is not an exotic case: 30
        // days of a large channel are ~490 MB against a 200 MB cap, so the binding run *has* to be
        // invoked several times and will cross a midnight.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 900_000, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        });

        Assert.Equal(4, await Run(3, maxMegabytes: 1));
        var firstFile = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        // Next day, same command line.
        _clock.Now = Now.AddDays(1);
        _archive.ClearReceivedCalls();
        var offeredOnResume = new List<long>();
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1, bytes: 10_000);
        }, offeredOnResume);

        Assert.Equal(0, await Run(3, maxMegabytes: 1));

        // Same file, and the window did not slide to 2026-09-03..2026-09-05.
        Assert.Equal(firstFile, Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.Contains("\"windowFrom\": \"2026-09-02\"", json);
        Assert.Contains("\"windowTo\": \"2026-09-04\"", json);

        // Only the throttled day is fetched again; the two finished ones are not.
        Assert.Equal(1, _archive.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IChatLogArchiveClient.ReadDayAsync)));
        await _archive.Received(1).ReadDayAsync(
            TwitchChannelId, Day3, Arg.Any<long>(), Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>());

        // And the cap still knows what the first run spent: 2 x 10 KB of day lines plus the 900 KB
        // the throttled attempt cost, not a fresh megabyte.
        Assert.Equal((1L * 1024 * 1024) - 920_000, offeredOnResume[0]);
    }

    [Fact]
    public async Task AnUnfinishedRunOlderThanTheResumeLimit_IsLeftAloneAndANewMeasurementStarts()
    {
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));
        var abandoned = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));

        // Eight days later the frozen window no longer describes anything the operator asked about;
        // a report dated today would answer for a window nobody chose.
        _clock.Now = Now.AddDays(8);
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        var files = Directory.GetFiles(_directory, "*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.Contains(abandoned, files);
        Assert.Contains(
            "\"windowTo\": \"2026-09-12\"",
            File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json"))));
    }

    [Fact]
    public async Task AForeignHeaderWithoutAnIdentity_IsSkippedRatherThanCrashingTheRun()
    {
        // Regression: TryReadHeader used to hand back a header object even when its Identity was
        // null — a syntactically valid but unusable envelope, reachable via a foreign or damaged file
        // in the output directory. FindFrozenWindow dereferences header.Identity right away, so one
        // such file turned the whole run into ExitUnexpectedError instead of simply not being a resume
        // candidate.
        File.WriteAllText(
            Path.Combine(_directory, "foreign.jsonl"),
            "{\"kind\":\"header\",\"header\":{\"identity\":null}}\n");

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });

        Assert.Equal(0, await Run(3));

        // The run completed normally and produced its own report next to the untouched foreign file.
        Assert.True(File.Exists(Path.Combine(_directory, "foreign.jsonl")));
        Assert.Single(Directory.GetFiles(_directory, "*.report.json"));
    }

    [Fact]
    public async Task AResumeWithADifferentWindowLength_StartsANewFile()
    {
        // The frozen window carries its own length, so a --days that no longer matches it cannot be
        // continued — it is a different measurement and gets its own file.
        RespondWith(async (day, onMessage) =>
        {
            if (day == Day3)
            {
                return new ChatLogDayResult(ChatLogDayStatus.RateLimited, 0, null, 0, 0, 0, 429);
            }

            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(4, await Run(3));

        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(2));

        Assert.Equal(2, Directory.GetFiles(_directory, "*.jsonl").Length);
        Assert.Contains(
            "\"windowFrom\": \"2026-09-03\"",
            File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json"))));
    }

    // P2-1 of the #119 review round: ReplayFinalReport.Recomputation must be invisible on the wire
    // for every ordinary run, not merely null in memory — ReportOptions carries no
    // DefaultIgnoreCondition, so without [JsonIgnore(Condition = WhenWritingNull)] a null property
    // still serializes as a literal "recomputation": null, moving bytes an unrelated run never had
    // before this feature existed.
    [Fact]
    public async Task ANormalRun_WritesNoRecomputationKeyToTheReportJson()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var reportJson = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.DoesNotContain("recomputation", reportJson, StringComparison.Ordinal);
    }

    // #119: report-only recompute of an existing, complete run. No test here touches the archive
    // substitute except to assert it was never called — that is the one property this feature exists
    // for — and none of them run through RunAsync a second time; every recompute goes through
    // RecomputeReportAsync directly, via the Recompute(...) helper below.
    [Fact]
    public async Task ReportOnly_OnAnUnchangedRun_ReproducesTheOriginalNumbersWithoutTouchingTheArchive()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);
        var reportJsonPath = Assert.Single(Directory.GetFiles(_directory, "*.report.json"));
        var reportMdPath = Assert.Single(Directory.GetFiles(_directory, "*.report.md"));

        var jsonlBefore = File.ReadAllBytes(jsonlPath);
        var reportJsonBefore = File.ReadAllBytes(reportJsonPath);
        var reportMdBefore = File.ReadAllBytes(reportMdPath);
        var originalReport = ReadReport(reportJsonPath);

        // The run above already exercised the archive substitute; only calls made by the recompute
        // itself are relevant to the "zero archive requests" claim.
        _archive.ClearReceivedCalls();

        Assert.Equal(0, await Recompute(fileName));

        // Every call, not just ReadDayAsync — a stronger claim than "the one method we expected
        // wasn't called".
        Assert.Empty(_archive.ReceivedCalls());
        Assert.Equal(jsonlBefore, File.ReadAllBytes(jsonlPath));
        Assert.Equal(reportJsonBefore, File.ReadAllBytes(reportJsonPath));
        Assert.Equal(reportMdBefore, File.ReadAllBytes(reportMdPath));

        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));
        Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.md"));

        Assert.Equal(originalReport.Gate, recomputedReport.Gate);
        Assert.Equal(originalReport.Plausibility, recomputedReport.Plausibility);
        Assert.Equal(originalReport.Diagnostics, recomputedReport.Diagnostics);
        Assert.Equal(originalReport.Run.WindowFrom, recomputedReport.Run.WindowFrom);
        Assert.Equal(originalReport.Run.WindowTo, recomputedReport.Run.WindowTo);
        Assert.Equal(originalReport.Run.WindowDays, recomputedReport.Run.WindowDays);
        Assert.Equal(originalReport.Run.BotSplitCutover, recomputedReport.Run.BotSplitCutover);
        Assert.Equal(originalReport.Run.SharedChatCutover, recomputedReport.Run.SharedChatCutover);
        Assert.Equal(originalReport.Run.Diagnostic, recomputedReport.Run.Diagnostic);
        Assert.True(recomputedReport.Run.RunComplete);

        Assert.NotNull(recomputedReport.Recomputation);
        Assert.True(recomputedReport.Recomputation!.InputHashMatches);
        Assert.True(recomputedReport.Recomputation.BotSplitCutoverMatches);
        Assert.Equal("inherited", recomputedReport.Recomputation.DiagnosticSource);
        Assert.Empty(recomputedReport.Recomputation.Warnings);
        Assert.Null(originalReport.Recomputation);
    }

    // P2-2 of the #119 review round: an inherited diagnostic=true original run carries its verdict
    // through unchanged.
    [Fact]
    public async Task ReportOnly_WithAnInheritedDiagnosticRun_KeepsTheDiagnosticRunReason()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3, diagnostic: true));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var originalReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        Assert.True(originalReport.Run.Diagnostic);
        Assert.Contains(ReplayGateIneligibleReasons.DiagnosticRun, originalReport.Gate.GateIneligibleReasons);

        Assert.Equal(0, await Recompute(fileName));

        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));
        Assert.True(recomputedReport.Run.Diagnostic);
        Assert.Equal("inherited", recomputedReport.Recomputation!.DiagnosticSource);
        Assert.Contains(ReplayGateIneligibleReasons.DiagnosticRun, recomputedReport.Gate.GateIneligibleReasons);
    }

    // P2-2: with no original report.json to inherit from (missing, unparsable, or otherwise
    // unreadable), the recompute must default to true (no gate verdict) — the fail-closed direction —
    // never to false, which would silently manufacture a binding verdict nobody asked for.
    [Fact]
    public async Task ReportOnly_WithNoOriginalReportToInheritFrom_DefaultsDiagnosticToTrueAndIsNotGateEligible()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        File.Delete(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));

        Assert.Equal(0, await Recompute(fileName));

        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));
        Assert.True(recomputedReport.Run.Diagnostic);
        Assert.Equal("defaulted", recomputedReport.Recomputation!.DiagnosticSource);
        Assert.False(recomputedReport.Gate.GateEligible);
        Assert.Contains(ReplayGateIneligibleReasons.DiagnosticRun, recomputedReport.Gate.GateIneligibleReasons);
    }

    // P2-4 of the #119 second review round: TryReadExistingReport must be fail-closed about what
    // "readable" means, not merely tolerant of I/O and JSON errors. A pre-#97 report — or any report
    // missing or misshaping Run.Diagnostic — must read the same as no report at all (defaulted to
    // true), never let JsonSerializer's own default-for-an-absent-bool quietly hand back `false`.
    [Fact]
    public async Task ReportOnly_WhenTheOriginalReportsRunDiagnosticIsMissing_DefaultsDiagnosticToTrue()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var reportJsonPath = Assert.Single(Directory.GetFiles(_directory, "*.report.json"));

        var root = JsonNode.Parse(File.ReadAllText(reportJsonPath))!.AsObject();
        ((JsonObject)root["run"]!).Remove("diagnostic");
        File.WriteAllText(reportJsonPath, root.ToJsonString());

        Assert.Equal(0, await Recompute(fileName));

        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));
        Assert.True(recomputedReport.Run.Diagnostic);
        Assert.Equal("defaulted", recomputedReport.Recomputation!.DiagnosticSource);
        Assert.Contains(ReplayGateIneligibleReasons.DiagnosticRun, recomputedReport.Gate.GateIneligibleReasons);
    }

    // P2-4: a report missing the whole "run" object (more damaged than the case above, or simply
    // foreign) must default the same way — exit 0, not the unexpected-error exit 6 a bare
    // NullReferenceException from a null Run would otherwise have produced before this fix.
    [Fact]
    public async Task ReportOnly_WhenTheOriginalReportsRunObjectIsMissing_DefaultsDiagnosticToTrue()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var reportJsonPath = Assert.Single(Directory.GetFiles(_directory, "*.report.json"));

        var root = JsonNode.Parse(File.ReadAllText(reportJsonPath))!.AsObject();
        root.Remove("run");
        File.WriteAllText(reportJsonPath, root.ToJsonString());

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitSuccess, exitCode);
        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));
        Assert.True(recomputedReport.Run.Diagnostic);
        Assert.Equal("defaulted", recomputedReport.Recomputation!.DiagnosticSource);
        Assert.Contains(ReplayGateIneligibleReasons.DiagnosticRun, recomputedReport.Gate.GateIneligibleReasons);
    }

    [Fact]
    public async Task ReportOnly_WhenLiveRowsChangedSinceTheRun_WarnsOnInputHashMismatchButStillSucceeds()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));

        // The live worker kept flushing after the run: the comparison snapshot has moved.
        _usage.GetRowsAsync(
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Rows(day2SharedChatUseCount: 3));

        Assert.Equal(0, await Recompute(fileName));

        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));

        Assert.False(recomputedReport.Recomputation!.InputHashMatches);
        Assert.Contains("input-hash-mismatch", recomputedReport.Recomputation.Warnings);
        Assert.NotEqual(recomputedReport.Recomputation.OriginalInputHash, recomputedReport.Recomputation.CurrentInputHash);

        var markdown = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.md")));
        Assert.Contains(recomputedReport.Recomputation.OriginalInputHash, markdown, StringComparison.Ordinal);
        Assert.Contains(recomputedReport.Recomputation.CurrentInputHash, markdown, StringComparison.Ordinal);

        // P3-4: the warning has to be seen before a reader could reach the first metric table, not
        // buried below it.
        var warningIndex = markdown.IndexOf('⚠');
        var metricsTableIndex = markdown.IndexOf("## Präregistrierte Kennzahlen", StringComparison.Ordinal);
        Assert.True(warningIndex >= 0, "Expected a warning marker in the recompute Markdown.");
        Assert.True(metricsTableIndex >= 0, "Expected the pre-registered metrics table in the recompute Markdown.");
        Assert.True(warningIndex < metricsTableIndex, "Expected the warning banner before the first metric table.");
    }

    [Fact]
    public async Task ReportOnly_OnACompleteButUnclosedRun_WritesASidecarWithoutClosingTheOriginalRun()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);

        // Simulate a process that wrote every day line but died before WriteFinalReportAtomically —
        // the run is complete on disk, but never closed.
        File.Delete(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        File.Delete(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        Assert.False(new HarnessReportFile(jsonlPath).IsClosed);

        Assert.Equal(0, await Recompute(fileName));

        Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json"));
        Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.md"));

        // Still not closed: the recompute never wrote <stem>.report.json/.md. Exact paths, not a
        // glob — "*.report.json" would also match the just-written "*.recompute-….report.json".
        var reportFile = new HarnessReportFile(jsonlPath);
        Assert.False(reportFile.IsClosed);
        Assert.False(File.Exists(reportFile.ReportJsonPath));
        Assert.False(File.Exists(reportFile.ReportMarkdownPath));
    }

    [Fact]
    public async Task ReportOnly_WithAMissingFile_RefusesAndWritesNothing()
    {
        var exitCode = await Recompute("does-not-exist-2026-09-02-2026-09-04-abc123.jsonl");

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Empty(_archive.ReceivedCalls());
    }

    // P3-1 of the #119 review round: a renamed channel (#34/#44) must stay recomputable — the header
    // identifies the run by ChannelId, not by name, exactly like FindFrozenWindow's own resume match
    // ("The channel name is not part of the match", just above in this file).
    [Fact]
    public async Task ReportOnly_WithARenamedChannel_SameIdDifferentName_IsAccepted()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));

        // Same channel row (same Id as the header names), looked up under its new login.
        const string RenamedChannelName = "renamed_channel";
        var renamedChannel = NewChannel();
        renamedChannel.ChannelName = RenamedChannelName;
        _channels.GetByNameAsync(RenamedChannelName, Arg.Any<CancellationToken>()).Returns(renamedChannel);

        Assert.Equal(0, await Recompute(fileName, channelName: RenamedChannelName));

        Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json"));
        Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.md"));
    }

    [Fact]
    public async Task ReportOnly_WithAChannelThatNoLongerResolvesToTheHeaderId_RefusesAndWritesNothing()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));
        var filesBefore = ListFiles();

        // Same login, a different id — e.g. the channel row was purged and rejoined since the run.
        var recreatedChannel = NewChannel();
        recreatedChannel.Id = "a-different-channel-guid";
        _channels.GetByNameAsync(ChannelName, Arg.Any<CancellationToken>()).Returns(recreatedChannel);

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Equal(filesBefore, ListFiles());
    }

    // P2-1 (Codex "MUST" #1) of the #119 second review round: a foreign AlgorithmVersion's day lines
    // have a different shape (harness-1 predates SharedChatCounts, #73), and
    // ReplayFidelityCalculator reads that dictionary unconditionally — recomputing such a file used
    // to NRE (exit 6) instead of refusing cleanly, and it must refuse before touching the database at
    // all, since there is no migration between versions to make the DB round trip worthwhile.
    [Fact]
    public async Task ReportOnly_WithAForeignAlgorithmVersion_RefusesBeforeAnyDatabaseAccess()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);

        // Rewrite the header to a foreign AlgorithmVersion, through the public protocol API.
        var original = new HarnessReportFile(jsonlPath);
        var header = original.TryReadHeader() ?? throw new InvalidOperationException("Header must exist.");
        var content = original.ReadDays();
        File.Delete(jsonlPath);
        var rebuilt = new HarnessReportFile(jsonlPath);
        rebuilt.WriteHeader(header with { Identity = header.Identity with { AlgorithmVersion = "harness-1" } });
        foreach (var day in content.Days)
        {
            rebuilt.AppendDay(day);
        }

        var filesBefore = ListFiles();
        _archive.ClearReceivedCalls();
        _usage.ClearReceivedCalls();
        _channels.ClearReceivedCalls();

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Equal(filesBefore, ListFiles());
        Assert.Empty(_archive.ReceivedCalls());
        Assert.Empty(_usage.ReceivedCalls());
    }

    // P2 Codex finding (issue #260, third review round): the algorithm-version refusal above only
    // catches a foreign counting rule — it says nothing about TWITCH_EXCLUDED_CHATTER_IDS having
    // changed since the file was written. A *resumed run* already refuses that drift for free,
    // because ExecuteAsync always rebuilds a fresh HarnessRunIdentity and ReadHeader compares it to
    // the file's byte for byte (ExcludedChatterIdsDigest is part of that identity, see the class
    // remark on AlgorithmVersion). A *recompute* only ever reads the header off disk with
    // TryReadHeader and never ran that comparison — recomputing day lines counted under one
    // exclusion list against today's different one could issue a binding report that misrepresents
    // what a fresh run would count today, exactly the gap AlgorithmVersion's own bump closed for a
    // changed counting rule.
    [Fact]
    public async Task ReportOnly_WhenTheExclusionListDriftedSinceTheRun_RefusesBeforeAnyDatabaseAccess()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));

        // The original run's identity carries the digest of the empty list (the fixture's default,
        // see the constructor). Simulate an operator having added an id to
        // TWITCH_EXCLUDED_CHATTER_IDS since — the exact drift the digest exists to catch.
        _excludedChatters.ExcludedChatterIds.Returns(new HashSet<string> { "999999" });

        var filesBefore = ListFiles();
        _archive.ClearReceivedCalls();
        _usage.ClearReceivedCalls();
        _channels.ClearReceivedCalls();

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitExclusionListChanged, exitCode);
        Assert.NotEqual(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Equal(filesBefore, ListFiles());
        // Refuses before touching the database, like the algorithm-version check right above it —
        // there is nothing a DB round trip could add to a decision the header alone already settles.
        Assert.Empty(_archive.ReceivedCalls());
        Assert.Empty(_usage.ReceivedCalls());
    }

    // Golden-master check for the harness-3 branch (issue #260/harness-exclusion): a "harness-2"
    // file carries no ExcludedChatterIdsDigest at all — the field did not exist under that version —
    // and "harness-2" itself predates the objection gate, so its day lines already equal what an
    // empty-list run produces today. RecomputeReportAsync must therefore accept it under today's
    // empty Twitch:ExcludedChatterIds and, because ReplayFidelityCalculator/ReplayDayCounter are
    // untouched by the exclusion feature, produce the exact numbers the pre-harness-3 algorithm would
    // have. GoldenMainRecomputeReportJson below was captured by running this same fixture (three
    // days, one "chatter-1"/"PogChamp" message each, the default empty exclusion list) through
    // origin/main's HarnessRunner.RecomputeReportAsync (commit 52a857a7, the harness-2 algorithm, no
    // exclusion feature at all) in a throwaway worktree — see the branch's DECISIONS entry for how.
    // The only field expected to differ is Recomputation.SourceFile (an absolute path under this
    // test's own random temp directory); RecomputedAtUtc matches because both runs share this test
    // class's fixed clock, and both input hashes match because HarnessInputHash and the Lifetimes/
    // Rows/NewChannel fixtures are byte-for-byte identical to main's.
    [Fact]
    public async Task ReportOnly_WithAHarnessTwoFileAndAnEmptyExclusionList_RecomputesIdenticallyToMain()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);
        RewriteHeaderAsHarnessTwo(jsonlPath);

        Assert.Equal(HarnessRunner.ExitSuccess, await Recompute(fileName));

        var reportPath = Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json"));

        // Normalize the one field that legitimately differs (this test's own random temp path)
        // before comparing the rest of the document field for field.
        Assert.Equal(
            NormalizeSourceFile(GoldenMainRecomputeReportJson),
            NormalizeSourceFile(File.ReadAllText(reportPath)));
    }

    // Counterpart to the golden test above: a "harness-2" file's day lines are only known-safe
    // against the empty-list baseline (harness-2 never honoured any exclusion list at all), so a
    // currently non-empty Twitch:ExcludedChatterIds must refuse exactly like a drifted "harness-3"
    // list does — the day lines could hold a since-excluded chatter's messages that a fresh run would
    // no longer count.
    [Fact]
    public async Task ReportOnly_WithAHarnessTwoFileAndANonEmptyExclusionList_RefusesWithExclusionListChanged()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);
        RewriteHeaderAsHarnessTwo(jsonlPath);

        _excludedChatters.ExcludedChatterIds.Returns(new HashSet<string> { "999999" });
        var filesBefore = ListFiles();
        _archive.ClearReceivedCalls();
        _usage.ClearReceivedCalls();

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitExclusionListChanged, exitCode);
        Assert.Equal(filesBefore, ListFiles());
        Assert.Empty(_archive.ReceivedCalls());
        Assert.Empty(_usage.ReceivedCalls());
    }

    // P2-2 (Codex "MUST" #2) of the #119 second review round: an ordinary run can never write a
    // duplicated day line itself (its in-memory dayLines dictionary makes a second write for the
    // same day impossible, and a *resumed* run with one already on disk throws on ToDictionary before
    // it ever reaches Compute) — but a report-only recompute reads whatever file it is pointed at,
    // and Compute has no duplicate-day contract of its own: left unchecked, it would silently sum
    // both copies into every total instead of refusing.
    [Fact]
    public async Task ReportOnly_WithADuplicatedDayLine_RefusesAndWritesNothing()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);

        // Append Day1's line a second time, through the public protocol API — the day-completeness
        // check's HashSet alone would not notice this, only a dedicated duplicate check would.
        var file = new HarnessReportFile(jsonlPath);
        var day1Line = file.ReadDays().Days.Single(d => d.Day == Day1);
        file.AppendDay(day1Line);

        var filesBefore = ListFiles();

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Equal(filesBefore, ListFiles());
    }

    [Fact]
    public async Task ReportOnly_WithAMissingDayLine_RefusesAndWritesNothing()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);

        // Drop Day2's line, as if that day's write never reached disk — rebuilt through the public
        // protocol API rather than by hand-editing JSON, so the test does not depend on its exact
        // on-disk shape.
        var original = new HarnessReportFile(jsonlPath);
        var header = original.TryReadHeader() ?? throw new InvalidOperationException("Header must exist.");
        var content = original.ReadDays();
        File.Delete(jsonlPath);
        var rebuilt = new HarnessReportFile(jsonlPath);
        rebuilt.WriteHeader(header);
        foreach (var day in content.Days.Where(d => d.Day != Day2))
        {
            rebuilt.AppendDay(day);
        }

        File.Delete(Assert.Single(Directory.GetFiles(_directory, "*.report.json")));
        File.Delete(Assert.Single(Directory.GetFiles(_directory, "*.report.md")));
        var filesBefore = ListFiles();

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Equal(filesBefore, ListFiles());
    }

    [Fact]
    public async Task ReportOnly_WhenTheBotSplitCutoverHasMoved_Warns()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));

        _usage.GetEarliestBotUsageDateAsync(ChannelId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 8, 20));

        Assert.Equal(0, await Recompute(fileName));

        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));

        Assert.False(recomputedReport.Recomputation!.BotSplitCutoverMatches);
        Assert.Contains("bot-split-cutover-drift", recomputedReport.Recomputation.Warnings);
        Assert.Equal(new DateOnly(2026, 8, 30), recomputedReport.Recomputation.OriginalBotSplitCutover);
        Assert.Equal(new DateOnly(2026, 8, 20), recomputedReport.Recomputation.CurrentBotSplitCutover);
    }

    [Fact]
    public async Task ReportOnly_TakesTheSharedChatCutoverFromTheHeaderEvenIfConfigDiffers()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3, sharedChatCutover: "2026-09-01"));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));

        var runner = new HarnessRunner(
            _channels,
            _usage,
            _archive,
            _bots,
            _excludedChatters,
            new HarnessOptions
            {
                OutputDirectory = _directory,
                MaxMegabytesPerRun = 200,
                WindowDays = 30,
                // Deliberately different from the header's cutover: the recompute must ignore this.
                SharedChatCutover = "2026-09-04"
            },
            _clock,
            NullLogger<HarnessRunner>.Instance);

        Assert.Equal(0, await runner.RecomputeReportAsync(ChannelName, fileName, default));

        var recomputedReport = ReadReport(Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json")));
        Assert.Equal(new DateOnly(2026, 9, 1), recomputedReport.Run.SharedChatCutover);
    }

    [Fact]
    public async Task ReportOnly_ASecondRecomputeAtADifferentClockTime_CreatesASecondPair_SameTimestampRefuses()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(_directory, "*.jsonl")));

        Assert.Equal(0, await Recompute(fileName));
        Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json"));

        // Same instant again: refuses rather than silently overwriting the first recompute.
        Assert.Equal(HarnessRunner.ExitPreconditionViolated, await Recompute(fileName));
        Assert.Single(Directory.GetFiles(_directory, "*.recompute-*.report.json"));

        _clock.Now = _clock.Now.AddSeconds(5);
        Assert.Equal(0, await Recompute(fileName));
        Assert.Equal(2, Directory.GetFiles(_directory, "*.recompute-*.report.json").Length);
    }

    // P2-3 of the #119 second review round: the pre-check above (File.Exists on both targets) closes
    // the ordinary case, but not a TOCTOU race — something else creating the exact target between
    // that check and the write a moment later. WriteReportPairAtomically's own rename now never
    // overwrites either, so even a target that appears out of nowhere right before the write is
    // refused rather than silently replaced, and its .tmp sibling is cleaned up rather than left
    // behind. Simulated here by creating the target before Recompute(...) even runs — indistinguishable
    // from the file system's point of view.
    [Fact]
    public async Task ReportOnly_WhenTheRecomputeJsonAlreadyExistsAtWriteTime_RefusesAndLeavesNoTmpFile()
    {
        RespondWith(async (day, onMessage) =>
        {
            await onMessage(Message(day, "chatter-1", "PogChamp"));
            return CompleteDay(1);
        });
        Assert.Equal(0, await Run(3));

        var jsonlPath = Assert.Single(Directory.GetFiles(_directory, "*.jsonl"));
        var fileName = Path.GetFileName(jsonlPath);

        var timestamp = _clock.Now.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var stem = Path.ChangeExtension(jsonlPath, null);
        var recomputeJsonPath = $"{stem}.recompute-{timestamp}.report.json";
        File.WriteAllText(recomputeJsonPath, "not a report");

        var exitCode = await Recompute(fileName);

        Assert.Equal(HarnessRunner.ExitPreconditionViolated, exitCode);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    // SharedChatCutover defaults to a day before Day1 (D4): almost every test in this file predates
    // #73 and asserts on behaviour the fail-closed precondition would otherwise block outright. The
    // handful of tests about the precondition itself override it explicitly.
    private Task<int> Run(
        int days,
        int maxMegabytes = 200,
        string? sharedChatCutover = "2026-09-01",
        bool diagnostic = false,
        CancellationToken ct = default)
    {
        var runner = new HarnessRunner(
            _channels,
            _usage,
            _archive,
            _bots,
            _excludedChatters,
            new HarnessOptions
            {
                OutputDirectory = _directory,
                MaxMegabytesPerRun = maxMegabytes,
                WindowDays = 30,
                SharedChatCutover = sharedChatCutover
            },
            _clock,
            NullLogger<HarnessRunner>.Instance);

        return runner.RunAsync(ChannelName, days, diagnostic, ct);
    }

    // The Harness:SharedChatCutover config passed here is deliberately irrelevant to every
    // RecomputeReportAsync test above except the one that says so explicitly — a recompute takes
    // its cutovers from the header, not from this options instance.
    private Task<int> Recompute(string reportOnlyFileName, string? channelName = null, CancellationToken ct = default)
    {
        var runner = new HarnessRunner(
            _channels,
            _usage,
            _archive,
            _bots,
            _excludedChatters,
            new HarnessOptions
            {
                OutputDirectory = _directory,
                MaxMegabytesPerRun = 200,
                WindowDays = 30,
                SharedChatCutover = "2026-09-01"
            },
            _clock,
            NullLogger<HarnessRunner>.Instance);

        return runner.RecomputeReportAsync(channelName ?? ChannelName, reportOnlyFileName, ct);
    }

    private string[] ListFiles() => [.. Directory.GetFiles(_directory).OrderBy(f => f, StringComparer.Ordinal)];

    private static ReplayFinalReport ReadReport(string path) =>
        JsonSerializer.Deserialize<ReplayFinalReport>(File.ReadAllText(path), ReadReportOptions)
        ?? throw new InvalidOperationException($"'{path}' did not deserialize to a report.");

    // Turns a freshly written "harness-3" file into the shape a real "harness-2" file has: the
    // AlgorithmVersion string, and no ExcludedChatterIdsDigest property at all — LineOptions'
    // DefaultIgnoreCondition = WhenWritingNull drops a null identity field from the JSON entirely,
    // exactly like the field's plain absence in a file written before it existed. Day lines are
    // copied unchanged: harness-2's day-line shape (SharedChatCounts included, #73) is the one the
    // harness-3 bump left untouched — see HarnessRunner.PriorRecomputableAlgorithmVersion.
    private static void RewriteHeaderAsHarnessTwo(string jsonlPath)
    {
        var original = new HarnessReportFile(jsonlPath);
        var header = original.TryReadHeader() ?? throw new InvalidOperationException("Header must exist.");
        var content = original.ReadDays();
        File.Delete(jsonlPath);
        var rebuilt = new HarnessReportFile(jsonlPath);
        rebuilt.WriteHeader(header with
        {
            Identity = header.Identity with
            {
                AlgorithmVersion = HarnessRunner.PriorRecomputableAlgorithmVersion,
                ExcludedChatterIdsDigest = null!
            }
        });
        foreach (var day in content.Days)
        {
            rebuilt.AppendDay(day);
        }
    }

    // The one field a recompute of the same fixture legitimately differs on between two separate
    // runs: an absolute path under whichever process's own random temp directory.
    private static string NormalizeSourceFile(string reportJson)
    {
        var root = JsonNode.Parse(reportJson)!.AsObject();
        ((JsonObject)root["recomputation"]!)["sourceFile"] = "<normalized>";
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // Captured by running the fixture built in ReportOnly_WithAHarnessTwoFileAndAnEmptyExclusionList_
    // RecomputesIdenticallyToMain — three days, one "chatter-1"/"PogChamp" message each, the default
    // empty exclusion list, --days 3, the class's fixed clock (2026-09-05T12:00:00Z) — through origin/
    // main's HarnessRunner.RecomputeReportAsync (commit 52a857a7, the pre-harness-exclusion "harness-2"
    // algorithm) in a throwaway `git worktree add --detach` checkout, with a throwaway test appended
    // that wrote the resulting .report.json to a file. See this branch's DECISIONS entry.
    private const string GoldenMainRecomputeReportJson =
        """
        {
          "run": {
            "windowFrom": "2026-09-02",
            "windowTo": "2026-09-04",
            "botSplitCutover": "2026-08-30",
            "sharedChatCutover": "2026-09-01",
            "windowDays": 3,
            "dayLineCount": 3,
            "totalBytes": 3072,
            "rateLimitedDays": 0,
            "resumePoint": "2026-09-04",
            "runComplete": true,
            "diagnostic": false
          },
          "gate": {
            "ratedDays": 3,
            "populationSize": 1,
            "humanLogTotal": 3,
            "humanLiveTotal": 2,
            "sharedChatLogTotal": 0,
            "sharedChatLiveTotal": 0,
            "totalDeviation": 0.5,
            "top20Recall": 1,
            "top20Size": 1,
            "top20LiveTieCount": 1,
            "top20LogTieCount": 1,
            "bottomQuartilePrecision": 1,
            "bottomQuartileSize": 1,
            "bottomQuartileLiveSize": 1,
            "bottomQuartileLogSize": 1,
            "bottomQuartileLiveTieCount": 1,
            "bottomQuartileLogTieCount": 1,
            "tailDeviation": 0.5,
            "gateEligible": false,
            "gateIneligibleReasons": [
              "window-not-30-days",
              "rated-days-below-20"
            ]
          },
          "plausibility": {
            "logTotalWithBots": 3,
            "liveTotalWithBots": 23,
            "ratio": 0.1304,
            "difference": -20
          },
          "diagnostics": {
            "stableSubsetSize": 0,
            "qualifiedEmoteCount": 0,
            "minLiveUsesN": 5,
            "requiredQualifiedM": 30,
            "stableSubsetDecisive": false,
            "stableSubsetMedianDeviation": null,
            "stableSubsetP90Deviation": null,
            "stableSubsetSpearman": null,
            "stableSubsetZeroLogCount": 0,
            "allEmotesQualifiedCount": 0,
            "allEmotesMedianDeviation": null,
            "allEmotesP90Deviation": null,
            "allEmotesSpearman": null,
            "allEmotesZeroLogCount": 0,
            "logOnlyEmoteCount": 0,
            "liveOnlyEmoteCount": 0,
            "logOnlyShareOfLogTotal": 0,
            "liveOnlyShareOfLiveTotal": 0,
            "totalDeviationIncludingFlaggedDays": 0.5,
            "flaggedIncludedDays": 3,
            "dailyDeviation": 0.5,
            "dailyDeviationWithOneDayTolerance": 0.5,
            "unknownNameHits": 0,
            "ambiguousNameHits": 0,
            "beforeFirstSeenHits": 0,
            "afterArchivedHits": 0,
            "firstSeenUnknownHits": 0,
            "ambiguousNameCount": 0,
            "archivedWithoutDateCount": 0,
            "totalMessages": 3,
            "botMessages": 0,
            "sharedChatMessages": 0,
            "indeterminateMessages": 0,
            "outsideDayCount": 0,
            "nonPrivmsgLines": 0,
            "malformedLines": 0,
            "singleChatterCellShare": 1,
            "kHistogram": [
              0,
              3,
              0,
              0,
              0,
              0,
              0,
              0,
              0,
              0,
              0
            ],
            "cellCount": 3,
            "distinctChatterDaySum": 3,
            "humanOnlyDays": 3,
            "logDays": 3,
            "noLogDays": 0,
            "signallessRatedDays": 0,
            "dayRatioMedian": 0.125,
            "liveGapDays": [],
            "coverageQuestionableDays": [],
            "sharedChatByDay": [
              {
                "day": "2026-09-02",
                "logTotal": 0,
                "liveTotal": 0,
                "ratio": null
              },
              {
                "day": "2026-09-03",
                "logTotal": 0,
                "liveTotal": 0,
                "ratio": null
              },
              {
                "day": "2026-09-04",
                "logTotal": 0,
                "liveTotal": 0,
                "ratio": null
              }
            ]
          },
          "recomputation": {
            "sourceFile": "<normalized>",
            "recomputedAtUtc": "2026-09-05T12:00:00Z",
            "originalInputHash": "a034de5dd53fce434cd639bea71a2787161dbcc719158152f3ce59f81e0e100b",
            "currentInputHash": "a034de5dd53fce434cd639bea71a2787161dbcc719158152f3ce59f81e0e100b",
            "inputHashMatches": true,
            "originalBotSplitCutover": "2026-08-30",
            "currentBotSplitCutover": "2026-08-30",
            "botSplitCutoverMatches": true,
            "diagnosticSource": "inherited",
            "warnings": []
          }
        }
        """;

    private void RespondWith(
        Func<DateOnly, Func<ChatLogMessage, ValueTask>, Task<ChatLogDayResult>> respond,
        List<long>? offeredMaxBytes = null)
    {
        _archive.ReadDayAsync(
                Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<long>(),
                Arg.Any<Func<ChatLogMessage, ValueTask>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                offeredMaxBytes?.Add(call.ArgAt<long>(2));
                return respond(call.ArgAt<DateOnly>(1), call.ArgAt<Func<ChatLogMessage, ValueTask>>(3));
            });
    }

    private void RespondWith(Func<DateOnly, ChatLogDayResult> respond) =>
        RespondWith((day, _) => Task.FromResult(respond(day)));

    private static ChatLogDayResult NoLogDay() => new(ChatLogDayStatus.NoLogDay, 0, null, 0, 0, 0, 404);

    private static ChatLogDayResult CompleteDay(int messageCount, long bytes = 1024) =>
        new(ChatLogDayStatus.Complete, bytes, "deadbeef", messageCount, 0, 0, 200);

    private static ChatLogMessage Message(
        DateOnly day, string userId, string text, string? sourceRoomId = null, bool hasOtherSourceMarkers = false) =>
        new(
            day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            userId,
            [new KeyValuePair<string, string>("subscriber", "1")],
            TwitchChannelId,
            sourceRoomId,
            hasOtherSourceMarkers,
            text);

    private static Channel NewChannel(
        string? twitchChannelId = TwitchChannelId,
        DateTime? createdAt = null) =>
        new()
        {
            Id = ChannelId,
            TwitchChannelId = twitchChannelId,
            ChannelName = ChannelName,
            IsBotActive = true,
            CreatedAt = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    // Asymmetric on purpose (Befund 2 of the Abschluss-Review): FirstSeenAt and ArchivedAt are both
    // DateTime? and, before this fixture, both took the same-looking round value, so a mapping bug
    // that swapped them at the HarnessRunner call site (`new ReplayEmote(e.Id, e.Name, e.IsArchived,
    // e.FirstSeenAt, e.ArchivedAt, e.LastSyncedAt)`) was invisible to every test — CoversDay would
    // fall to false for every window day, matching zero hits, and ACompleteRun_ below asserted only
    // that "totalDeviation" existed as a key, never its value. With FirstSeenAt well before the
    // window and ArchivedAt well after it, the correct mapping still covers every window day (same
    // behaviour as before); the swapped mapping excludes every window day instead, which the
    // asserted humanLogTotal below then catches. LastSyncedAt stays a third, distinct value so a
    // three-way rotation of the same three fields would be caught too.
    private static IReadOnlyList<EmoteLifetimeDto> Lifetimes() =>
    [
        new(
            "e1",
            "PogChamp",
            false,
            Day1.AddDays(-5).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Day3.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    ];

    // BotUseCount = 7, not 0 (Befund 2): a mapping bug that swapped UseCount and BotUseCount at the
    // HarnessRunner call site (`new ReplayUsageRow(r.EmoteId, r.Date, r.UseCount, r.BotUseCount)`)
    // used to be invisible — zeroing an already-zero BotUseCount changes nothing a test can see.
    // With BotUseCount nonzero, the swap inflates the human-live side (the gate's denominator) from
    // 2 to 21 over the three rated days, which the asserted humanLiveTotal below catches.
    private static IReadOnlyList<UsageStatRowDto> Rows(int day2SharedChatUseCount = 0) =>
    [
        new("e1", Day1, 1, 7, 0),
        new("e1", Day2, 0, 7, day2SharedChatUseCount),
        new("e1", Day3, 1, 7, 0)
    ];

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ValueListJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueList<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var itemType = typeToConvert.GetGenericArguments()[0];
            var converterType = typeof(ValueListConverter<>).MakeGenericType(itemType);
            return (JsonConverter)Activator.CreateInstance(converterType)!;
        }

        private sealed class ValueListConverter<T> : JsonConverter<ValueList<T>>
        {
            public override ValueList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
                new(JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? []);

            public override void Write(Utf8JsonWriter writer, ValueList<T> value, JsonSerializerOptions options) =>
                JsonSerializer.Serialize(writer, (IReadOnlyList<T>)value, options);
        }
    }
}
