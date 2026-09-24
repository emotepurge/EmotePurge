using EmotePurge.Worker.Harness;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The JSONL protocol is what makes a killed run cost one day instead of the whole window, and what
// makes the closing step repeatable. Both promises are file-level, so these tests use a real
// temporary directory rather than an abstraction over it — an in-memory file system would test the
// abstraction, not the promise.
public class HarnessReportFileTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "emotepurge-harness-tests-" + Guid.NewGuid().ToString("N"));

    public HarnessReportFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void AWrittenHeader_ReadsBackWithTheSameIdentity()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc)));

        var header = file.ReadHeader(identity);

        Assert.Equal(identity.InputHash, header.Identity.InputHash);
        Assert.Equal(identity.WindowFrom, header.Identity.WindowFrom);
        Assert.Equal(identity.WindowTo, header.Identity.WindowTo);
        Assert.Equal(identity.BotAccountIds, header.Identity.BotAccountIds);
        Assert.Equal(new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc), header.LoadedAtUtc);
    }

    [Fact]
    public void AWrittenHeader_WithASharedChatCutover_CarriesItLiterallyInTheHeaderLine()
    {
        var identity = Identity() with { SharedChatCutover = new DateOnly(2026, 9, 1) };
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));

        var rawText = File.ReadAllText(file.Path);
        Assert.Contains("\"sharedChatCutover\":\"2026-09-01\"", rawText);

        var header = file.ReadHeader(identity);
        Assert.Equal(new DateOnly(2026, 9, 1), header.Identity.SharedChatCutover);
    }

    [Fact]
    public void AWrittenHeader_WithoutASharedChatCutover_OmitsTheFieldButStillReadsBack()
    {
        var identity = Identity() with { SharedChatCutover = null };
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));

        // WhenWritingNull: the key itself is absent, not present with a null value — same contract
        // as every other optional field in this protocol.
        var rawText = File.ReadAllText(file.Path);
        Assert.DoesNotContain("sharedChatCutover", rawText);

        var header = file.ReadHeader(identity);
        Assert.Null(header.Identity.SharedChatCutover);
    }

    [Fact]
    public void TwoIdentities_DifferingOnlyInTheSharedChatCutover_ProduceDifferentFileNames()
    {
        var withCutover = Identity() with { SharedChatCutover = new DateOnly(2026, 9, 1) };
        var withoutCutover = Identity() with { SharedChatCutover = null };

        Assert.NotEqual(
            HarnessReportFile.BuildFileName(withCutover),
            HarnessReportFile.BuildFileName(withoutCutover));
    }

    [Fact]
    public void AForeignIdentity_IsRefused()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));

        // Same file name, different snapshot — only reachable by renaming a file by hand, which is
        // exactly the case the head comparison exists for.
        var foreign = identity with { InputHash = new string('b', 64) };

        Assert.Throws<HarnessReportIdentityMismatchException>(() => file.ReadHeader(foreign));
    }

    [Fact]
    public void ATruncatedLastLine_IsDiscardedAndTheCompleteOnesSurvive()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        file.AppendDay(Day(new DateOnly(2026, 9, 3)));
        File.AppendAllText(file.Path, "{\"kind\":\"day\",\"day\":{\"day\":\"2026-09-04\",\"stat");

        var content = file.ReadDays();

        Assert.Equal(2, content.Days.Count);
        Assert.Equal(new DateOnly(2026, 9, 2), content.Days[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 3), content.Days[1].Day);
    }

    [Fact]
    public void AnAppendAfterATruncatedLastLine_DropsTheFragmentInsteadOfGluingOntoIt()
    {
        // The crash this reproduces: the process died mid-Append, leaving a fragment with no
        // trailing newline. Before the fix, the next Append landed right after that fragment,
        // turning an ignorable dropped-last-line into corruption in the *middle* of the file — which
        // ADamagedLineInTheMiddle_IsRefusedRatherThanSilentlySkipped shows the reader refuses to run
        // past. This test walks the full path: truncated tail on disk, then a real append, then a
        // read that must see every complete day and nothing else.
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        File.AppendAllText(file.Path, "{\"kind\":\"day\",\"day\":{\"day\":\"2026-09-03\",\"stat");

        file.AppendDay(Day(new DateOnly(2026, 9, 4)));

        var content = file.ReadDays();
        Assert.Equal(2, content.Days.Count);
        Assert.Equal(new DateOnly(2026, 9, 2), content.Days[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 4), content.Days[1].Day);

        // The file itself is intact, not just readable by luck: every line up to and including the
        // last one is complete JSON, so a second read (and a third append) behaves the same way.
        var lines = File.ReadAllText(file.Path);
        Assert.EndsWith("\n", lines);
        Assert.DoesNotContain("2026-09-03", lines);
    }

    [Fact]
    public void ADayLine_KeepsItsCountsAcrossTheRoundTrip()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)) with
        {
            HumanCounts = new Dictionary<string, int> { ["e1"] = 4 },
            BotCounts = new Dictionary<string, int> { ["e1"] = 1 },
            SharedChatCounts = new Dictionary<string, int> { ["e1"] = 2 },
            IndeterminateMessageCount = 1,
            KHistogram = [0, 2, 1],
            Bytes = 1234,
            BodySha256Hex = "abc"
        });

        // Assert.Contains on the raw text, not just the round-trip below, because that is what
        // pins the camelCase JSON contract rather than only the symmetry of writing and reading.
        var rawText = File.ReadAllText(file.Path);
        Assert.Contains("\"sharedChatCounts\"", rawText);
        Assert.Contains("\"indeterminateMessageCount\"", rawText);

        var day = Assert.Single(file.ReadDays().Days);

        Assert.Equal(4, day.HumanCounts["e1"]);
        Assert.Equal(1, day.BotCounts["e1"]);
        Assert.Equal(2, day.SharedChatCounts["e1"]);
        Assert.Equal(1, day.IndeterminateMessageCount);
        Assert.Equal(new[] { 0, 2, 1 }, day.KHistogram);
        Assert.Equal(1234, day.Bytes);
        Assert.Equal("abc", day.BodySha256Hex);
    }

    [Fact]
    public void AValidButNotYetTerminatedLastDay_IsNeverReadAndNeverSilentlyLost()
    {
        // Regression: RemoveDanglingTail (the write side) and ReadDays (the read side) used to
        // disagree about what a finished record is. A process that flushed the closing "}" of a day
        // line but died before the trailing "\n" landed left valid JSON with no terminator — ReadDays
        // accepted it (it deserializes fine), but the next AppendDay erased it via RemoveDanglingTail
        // (it looks exactly like the crash case that method exists to undo). A caller who had already
        // read that day into memory then reported it in a final report whose own JSONL source no
        // longer contained it.
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        file.AppendDay(Day(new DateOnly(2026, 9, 3)));

        // Strip the trailing "\n" that AppendDay just wrote for the second day — valid JSON, but not
        // yet a complete record by the file's own rule.
        var bytes = File.ReadAllBytes(file.Path);
        Assert.Equal((byte)'\n', bytes[^1]);
        File.WriteAllBytes(file.Path, bytes[..^1]);

        var firstRead = file.ReadDays();
        var sawDay2Before = firstRead.Days.Any(d => d.Day == new DateOnly(2026, 9, 3));

        file.AppendDay(Day(new DateOnly(2026, 9, 4)));

        var secondRead = file.ReadDays();
        var day2SurvivesOnDisk = secondRead.Days.Any(d => d.Day == new DateOnly(2026, 9, 3));

        // The consistency property: a day that was read as present must still be present, or it must
        // never have been read in the first place. A state in between — read once, gone the next
        // time — is exactly the defect.
        Assert.False(sawDay2Before && !day2SurvivesOnDisk);

        // With the fix, the not-yet-terminated line is not accepted by the read side either, so day 1
        // and day 4 are the only ones that ever existed as far as any reader can tell.
        Assert.False(sawDay2Before);
        Assert.Equal(2, secondRead.Days.Count);
        Assert.Equal(new DateOnly(2026, 9, 2), secondRead.Days[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 4), secondRead.Days[1].Day);
    }

    [Fact]
    public void ADamagedLineInTheMiddle_IsRefusedRatherThanSilentlySkipped()
    {
        // Unlike a truncated last line, this is not an unfinished day — skipping it would leave a
        // gap that the final report would then present as a complete window.
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        File.AppendAllText(file.Path, "{\"kind\":\"day\",\"day\":{\"day\":\"2026-09-\n");
        file.AppendDay(Day(new DateOnly(2026, 9, 4)));

        Assert.Throws<HarnessReportCorruptException>(() => file.ReadDays());
    }

    [Fact]
    public void EventLines_AreReadBackNextToTheDayLines()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendDay(Day(new DateOnly(2026, 9, 2)));
        file.AppendEvent(new HarnessEventLine(
            DateTime.UtcNow, new DateOnly(2026, 9, 3), "RateLimited", 429, "gedrosselt"));

        var content = file.ReadDays();

        Assert.Single(content.Days);
        var line = Assert.Single(content.Events);
        Assert.Equal(429, line.HttpStatusCode);
        Assert.Equal(new DateOnly(2026, 9, 3), line.Day);
    }

    [Fact]
    public void AnEventLine_KeepsItsBytesAcrossTheRoundTrip()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        file.AppendEvent(new HarnessEventLine(
            DateTime.UtcNow, new DateOnly(2026, 9, 3), "TransportFailure", 502, "Transportfehler.", Bytes: 12_345));

        var line = Assert.Single(file.ReadDays().Events);

        Assert.Equal(12_345, line.Bytes);
    }

    [Fact]
    public void AnEventLineWrittenBeforeTheBytesField_StillDeserializesWithZeroBytes()
    {
        // A file from before this field existed carries no "bytes" property on its event lines at
        // all — not the record's default working out to 0 in memory, but the literal absence of the
        // key in the JSON on disk. Reading it must not throw, and the missing byte cost must read as
        // 0 rather than as "unknown" (an aborted day before this field existed truly cost nothing
        // towards the byte cap as far as the file can say).
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));
        File.AppendAllText(
            file.Path,
            "{\"kind\":\"event\",\"event\":{\"atUtc\":\"2026-09-03T00:00:00Z\",\"day\":\"2026-09-03\","
            + "\"status\":\"RateLimited\",\"httpStatusCode\":429,\"message\":\"gedrosselt\"}}\n");

        var line = Assert.Single(file.ReadDays().Events);

        Assert.Equal(0, line.Bytes);
        Assert.Equal(429, line.HttpStatusCode);
    }

    [Fact]
    public void TheFinalReport_IsWrittenAtomicallyAndLeavesNoTempFile()
    {
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));

        var report = ReplayFidelityCalculator.Compute(
            new ReplayWindow(identity.WindowFrom, identity.WindowTo, null, null), [], [], [], 3,
            runComplete: true, totalBytes: 0, rateLimitedDays: 0, resumePoint: null, diagnostic: false);
        file.WriteFinalReportAtomically(report, "# Bericht\n");

        Assert.True(File.Exists(file.ReportJsonPath));
        Assert.True(File.Exists(file.ReportMarkdownPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        var reportJson = File.ReadAllText(file.ReportJsonPath);
        Assert.Contains("\"run\"", reportJson);
        // #97: the new gate fields round-trip through the JSON report even for an empty population.
        Assert.Contains("\"bottomQuartileLiveSize\"", reportJson);
        Assert.Contains("\"bottomQuartileLogSize\"", reportJson);
        Assert.Contains("\"tailDeviation\"", reportJson);
        Assert.Contains("\"top20LiveTieCount\"", reportJson);
        Assert.Contains("\"top20LogTieCount\"", reportJson);
        Assert.Equal("# Bericht\n", File.ReadAllText(file.ReportMarkdownPath));
    }

    [Fact]
    public void IsClosed_RequiresBothFinalReportsNotJustTheJsonOne()
    {
        // Regression: WriteFinalReportAtomically writes two files, the ".report.json" and the
        // ".report.md". IsClosed used to check only the first one's existence, so a process that died
        // between the two renames — or whose second rename failed outright — read as a finished run:
        // the resume search would skip it forever and the missing Markdown report would never get
        // written.
        var identity = Identity();
        var file = NewFile(identity);
        file.WriteHeader(new HarnessReportHeader(identity, DateTime.UtcNow));

        File.WriteAllText(file.ReportJsonPath, "{}");
        Assert.False(file.IsClosed);

        File.WriteAllText(file.ReportMarkdownPath, "# Bericht\n");
        Assert.True(file.IsClosed);
    }

    [Fact]
    public void TheFileName_CarriesChannelWindowAndIdentityDigest()
    {
        var identity = Identity();

        var name = HarnessReportFile.BuildFileName(identity);

        Assert.StartsWith("brudivoeller_tv-2026-09-02-2026-09-04-", name);
        Assert.EndsWith(".jsonl", name);
        // Two snapshots that differ only in their input hash must never share a file.
        Assert.NotEqual(name, HarnessReportFile.BuildFileName(identity with { InputHash = new string('c', 64) }));
    }

    private HarnessReportFile NewFile(HarnessRunIdentity identity) =>
        new(Path.Combine(_directory, HarnessReportFile.BuildFileName(identity)));

    private static HarnessRunIdentity Identity() =>
        new(
            "channel-guid",
            "12345",
            "brudivoeller_tv",
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 4),
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 1),
            ["19264788", "402337290"],
            ExcludedChatterIdsDigest.Compute([]),
            HarnessRunner.AlgorithmVersion,
            new string('a', 64));

    private static ReplayDayLine Day(DateOnly day) =>
        new(
            day,
            ReplayDayStatuses.Complete,
            0,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new Dictionary<string, int>(), // HumanCounts
            new Dictionary<string, int>(), // BotCounts
            new Dictionary<string, int>(), // SharedChatCounts
            new Dictionary<string, int>(), // UnmatchedByReason
            0,
            [],
            0,
            0);
}
