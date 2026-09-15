using EmotePurge.Worker.Harness;
using Xunit;

namespace EmotePurge.Worker.Tests;

// The fail-open finding of the Codex adversarial review lives exactly here: the worker image has
// two entry points, and the only thing that keeps a mistyped argument list from starting a second
// IRC counter next to the production worker is this parser. It runs before any host is built, so
// it is testable without a service graph — and every case below is a case that must NOT reach
// Host.CreateApplicationBuilder.
public class HarnessCommandLineTests
{
    [Fact]
    public void NoArguments_RunsTheWorkerUnchanged()
    {
        Assert.IsType<HarnessCommandLineResult.RunWorker>(HarnessCommandLine.Parse([]));
    }

    [Fact]
    public void HarnessWithChannel_RunsTheHarnessWithTheConfiguredDefaultWindow()
    {
        var result = Assert.IsType<HarnessCommandLineResult.RunHarness>(HarnessCommandLine.Parse(["harness", "foo"]));

        Assert.Equal("foo", result.ChannelName);
        // null = "--days was not given"; the entry point then falls back to Harness:WindowDays,
        // which the parser cannot see because it runs before the configuration exists.
        Assert.Null(result.Days);
        Assert.False(result.Diagnostic);
    }

    [Fact]
    public void HarnessWithDays_TakesTheGivenWindow()
    {
        var result = Assert.IsType<HarnessCommandLineResult.RunHarness>(
            HarnessCommandLine.Parse(["harness", "foo", "--days", "3"]));

        Assert.Equal("foo", result.ChannelName);
        Assert.Equal(3, result.Days);
        Assert.False(result.Diagnostic);
    }

    [Fact]
    public void DiagnosticAlone_SetsDiagnosticWithoutAWindow()
    {
        var result = Assert.IsType<HarnessCommandLineResult.RunHarness>(
            HarnessCommandLine.Parse(["harness", "foo", "--diagnostic"]));

        Assert.Equal("foo", result.ChannelName);
        Assert.Null(result.Days);
        Assert.True(result.Diagnostic);
    }

    [Theory]
    [InlineData("harness", "foo", "--days", "3", "--diagnostic")]
    [InlineData("harness", "foo", "--diagnostic", "--days", "3")]
    public void DaysAndDiagnostic_CanBeCombinedInEitherOrder(params string[] args)
    {
        var result = Assert.IsType<HarnessCommandLineResult.RunHarness>(HarnessCommandLine.Parse(args));

        Assert.Equal("foo", result.ChannelName);
        Assert.Equal(3, result.Days);
        Assert.True(result.Diagnostic);
    }

    [Fact]
    public void ChannelName_IsPassedThroughUnnormalized()
    {
        // Regel 9: normalizing is the service's job, and doing it twice is how the two spellings
        // drift apart.
        var result = Assert.IsType<HarnessCommandLineResult.RunHarness>(
            HarnessCommandLine.Parse(["harness", "HandOfBlood"]));

        Assert.Equal("HandOfBlood", result.ChannelName);
    }

    // #119: report-only recompute of an existing run. The accepted form and every rejection the
    // parser owns for it — everything downstream (file existence, header, channel identity, day
    // coverage) is HarnessRunner's job, not the parser's.
    [Fact]
    public void ReportOnly_WithAPlainFileName_SelectsTheRecompute()
    {
        var result = Assert.IsType<HarnessCommandLineResult.RunHarness>(
            HarnessCommandLine.Parse(["harness", "foo", "--report-only", "foo-2026-09-02-2026-09-04-abc123.jsonl"]));

        Assert.Equal("foo", result.ChannelName);
        Assert.Equal("foo-2026-09-02-2026-09-04-abc123.jsonl", result.ReportOnlyFile);
        Assert.Null(result.Days);
        Assert.False(result.Diagnostic);
    }

    [Theory]
    [InlineData("harness", "foo", "--report-only")]
    [InlineData("harness", "foo", "--report-only", "sub/dir.jsonl")]
    [InlineData("harness", "foo", "--report-only", "sub\\dir.jsonl")]
    [InlineData("harness", "foo", "--report-only", ".")]
    [InlineData("harness", "foo", "--report-only", "..")]
    [InlineData("harness", "foo", "--report-only", "report.json")]
    [InlineData("harness", "foo", "--report-only", "report.txt")]
    [InlineData("harness", "foo", "--report-only", "-report.jsonl")]
    [InlineData("harness", "foo", "--report-only", "report.jsonl", "--report-only", "report.jsonl")]
    [InlineData("harness", "foo", "--report-only", "report.jsonl", "--days", "3")]
    [InlineData("harness", "foo", "--days", "3", "--report-only", "report.jsonl")]
    [InlineData("harness", "foo", "--report-only", "report.jsonl", "--diagnostic")]
    [InlineData("harness", "foo", "--diagnostic", "--report-only", "report.jsonl")]
    public void ReportOnly_EveryRejection_IsInvalidWithAnEnglishMessage(params string[] args)
    {
        var result = Assert.IsType<HarnessCommandLineResult.Invalid>(HarnessCommandLine.Parse(args));

        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        // New messages are English since #152 — the one thing this theory adds over the German
        // catch-all below, which only asserts non-empty.
        Assert.Contains("--report-only", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("harness")]
    [InlineData("harness", "foo", "bar")]
    [InlineData("foo")]
    [InlineData("--help")]
    [InlineData("harness", "foo", "--days", "0")]
    [InlineData("harness", "foo", "--days", "91")]
    [InlineData("harness", "foo", "--days", "-1")]
    [InlineData("harness", "foo", "--days", "x")]
    [InlineData("harness", "foo", "--days")]
    [InlineData("harness", "foo", "--days", "3", "extra")]
    [InlineData("harness", "--days", "3")]
    [InlineData("harness", "")]
    [InlineData("harness", "foo", "--diagnostic", "--diagnostic")]
    [InlineData("harness", "foo", "--diagnostic", "5")]
    [InlineData("harness", "foo", "--diagnose")]
    [InlineData("harness", "foo", "--days", "3", "--days", "3")]
    public void EverythingElse_IsInvalidWithAGermanMessage(params string[] args)
    {
        var result = Assert.IsType<HarnessCommandLineResult.Invalid>(HarnessCommandLine.Parse(args));

        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void TheDayBounds_AreTheOnesThePlanFixed()
    {
        Assert.IsType<HarnessCommandLineResult.RunHarness>(HarnessCommandLine.Parse(["harness", "foo", "--days", "1"]));
        Assert.IsType<HarnessCommandLineResult.RunHarness>(HarnessCommandLine.Parse(["harness", "foo", "--days", "90"]));
    }
}
