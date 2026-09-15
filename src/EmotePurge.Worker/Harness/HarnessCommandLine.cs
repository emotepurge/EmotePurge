using System.Globalization;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// What the worker image was asked to be this time. A closed hierarchy — the private constructor
/// plus the nested cases mean no fourth case can be invented at a call site, so the switch in
/// <c>Program</c> stays exhaustive by construction.
/// </summary>
public abstract record HarnessCommandLineResult
{
    private HarnessCommandLineResult()
    {
    }

    /// <summary>No arguments: the normal worker, with all nine hosted services.</summary>
    public sealed record RunWorker : HarnessCommandLineResult;

    /// <summary>
    /// The accuracy harness for one channel.
    /// <para>
    /// <see cref="Days"/> is <c>null</c> when <c>--days</c> was omitted. It cannot be filled in
    /// here: argument checking deliberately runs before <c>Host.CreateApplicationBuilder</c>, so
    /// there is no configuration yet, and the configured default (<c>Harness:WindowDays</c>) is
    /// applied by the entry point instead.
    /// </para>
    /// <para>
    /// <see cref="Diagnostic"/> is the escape hatch from the shared-chat cutover's fail-closed rule
    /// (D4): a run without <c>--diagnostic</c> and without a parsable
    /// <c>Harness:SharedChatCutover</c> refuses to start at all, and only a diagnostic run may. It
    /// is not part of the run identity — see the remark at its use in <c>HarnessRunner</c>.
    /// </para>
    /// <para>
    /// <see cref="ReportOnlyFile"/> (issue #119) selects the report-only recompute instead of an
    /// ordinary run: <c>null</c> for the ordinary <c>[--days &lt;n&gt;] [--diagnostic]</c> form, a
    /// plain file name (validated by <see cref="HarnessCommandLine.Parse"/>, never a path) for
    /// <c>--report-only &lt;file&gt;</c>. The parser refuses to combine it with <c>--days</c> or
    /// <c>--diagnostic</c> — the window and the diagnostic flag are inherited from the frozen run a
    /// recompute reads, not chosen anew — so at most one of <see cref="Days"/>/<see cref="Diagnostic"/>
    /// and <see cref="ReportOnlyFile"/> is ever non-default on a given instance. A default of
    /// <c>null</c> keeps every existing positional call site (<c>Program</c>, the test suite)
    /// compiling unchanged.
    /// </para>
    /// </summary>
    public sealed record RunHarness(string ChannelName, int? Days, bool Diagnostic, string? ReportOnlyFile = null)
        : HarnessCommandLineResult;

    /// <summary>Anything else. <see cref="Message"/> is the single German line for stderr.</summary>
    public sealed record Invalid(string Message) : HarnessCommandLineResult;
}

/// <summary>
/// The whole grammar of the worker image's two entry points, as a pure function of
/// <c>args</c> — no configuration, no host, no I/O, so it can run as the very first statement of
/// <c>Program</c>.
/// <para>
/// That position is the point. <c>docker compose run</c> replaces a service's <c>command</c>, not
/// its <c>entrypoint</c>: a harness service whose verb sat in the <c>command</c> would hand
/// <c>Program</c> nothing but the channel name, and a lenient parser would then start the full
/// worker — a second IRC counter next to the production one, doubling every usage row through the
/// additive UPSERT (Codex-adversarial "Fail-open CLI"). Hence: no arguments means worker, exactly
/// <c>harness &lt;channel&gt; [--days &lt;n&gt;] [--diagnostic]</c> or
/// <c>harness &lt;channel&gt; --report-only &lt;file&gt;</c> means harness (the first form's two
/// options in either order, each at most once; the second form is mutually exclusive with both —
/// see <see cref="HarnessCommandLineResult.RunHarness.ReportOnlyFile"/>), and every other shape is
/// a refusal with exit code 2 rather than a guess.
/// </para>
/// </summary>
public static class HarnessCommandLine
{
    /// <summary>The verb that selects the harness. Belongs in the compose service's entrypoint.</summary>
    public const string HarnessVerb = "harness";

    /// <summary>Takes a value; everything else the harness needs comes from configuration.</summary>
    public const string DaysOption = "--days";

    /// <summary>
    /// A flag, no value. Runs the harness without an explicit shared-chat cutover and without a
    /// gate verdict (D4) — the only case in which a run may proceed without one.
    /// </summary>
    public const string DiagnosticOption = "--diagnostic";

    /// <summary>
    /// Takes a value: a plain file name inside <c>Harness:OutputDirectory</c> of an existing report
    /// (issue #119). Selects the report-only recompute instead of an ordinary run; see
    /// <see cref="HarnessCommandLineResult.RunHarness.ReportOnlyFile"/> and
    /// <c>HarnessRunner.RecomputeReportAsync</c>.
    /// </summary>
    public const string ReportOnlyOption = "--report-only";

    public const int MinDays = 1;

    /// <summary>
    /// User decision D3 of 2026-09-05: 90, not the pre-registration's 30. The binding run uses 30;
    /// a larger window is allowed so a later question about deeper history does not need a code
    /// change, and the report always names the window it actually used.
    /// </summary>
    public const int MaxDays = 90;

    private const string Usage =
        "Aufruf: 'harness <kanal> [--days <n>] [--diagnostic]', 'harness <kanal> --report-only <datei>' "
        + "oder gar kein Argument für den Worker.";

    /// <summary>
    /// English on purpose (Sprache, seit #152): every message below that only a
    /// <c>--report-only</c> call site can reach is new code, unlike <see cref="Usage"/>'s
    /// surrounding German clauses, which predate that rule and stay as they are.
    /// </summary>
    private const string ReportOnlyUsage = "Expected: 'harness <channel> --report-only <file>'.";

    public static HarnessCommandLineResult Parse(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return new HarnessCommandLineResult.RunWorker();
        }

        if (!string.Equals(args[0], HarnessVerb, StringComparison.Ordinal))
        {
            return Invalid($"Unbekanntes Argument '{args[0]}'. {Usage}");
        }

        if (args.Length < 2)
        {
            return Invalid($"Dem Kommando '{HarnessVerb}' fehlt der Kanalname. {Usage}");
        }

        var channelName = args[1];
        if (string.IsNullOrWhiteSpace(channelName) || channelName.StartsWith('-'))
        {
            return Invalid($"'{channelName}' ist kein Kanalname. {Usage}");
        }

        int? days = null;
        var diagnostic = false;
        string? reportOnlyFile = null;

        var index = 2;
        while (index < args.Length)
        {
            var token = args[index];

            if (string.Equals(token, DaysOption, StringComparison.Ordinal))
            {
                if (days is not null)
                {
                    return Invalid($"'{DaysOption}' darf nur einmal angegeben werden. {Usage}");
                }

                if (index + 1 >= args.Length)
                {
                    return Invalid($"'{DaysOption}' braucht eine Zahl von {MinDays} bis {MaxDays}. {Usage}");
                }

                var value = args[index + 1];
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDays)
                    || parsedDays < MinDays
                    || parsedDays > MaxDays)
                {
                    return Invalid($"'{value}' ist keine Fensterlänge von {MinDays} bis {MaxDays} Tagen. {Usage}");
                }

                days = parsedDays;
                index += 2;
                continue;
            }

            if (string.Equals(token, DiagnosticOption, StringComparison.Ordinal))
            {
                if (diagnostic)
                {
                    return Invalid($"'{DiagnosticOption}' darf nur einmal angegeben werden. {Usage}");
                }

                diagnostic = true;
                index += 1;
                continue;
            }

            if (string.Equals(token, ReportOnlyOption, StringComparison.Ordinal))
            {
                if (reportOnlyFile is not null)
                {
                    return Invalid($"'{ReportOnlyOption}' may be given at most once. {ReportOnlyUsage}");
                }

                if (index + 1 >= args.Length)
                {
                    return Invalid($"'{ReportOnlyOption}' needs a file name. {ReportOnlyUsage}");
                }

                var value = args[index + 1];
                if (!IsValidReportOnlyFileName(value))
                {
                    return Invalid(
                        $"'{value}' is not a valid '{ReportOnlyOption}' file name: it must be a plain file "
                        + $"name inside the harness output directory — no '/' or '\\', not '.' or '..', not "
                        + $"starting with '-', ending in '.jsonl'. {ReportOnlyUsage}");
                }

                reportOnlyFile = value;
                index += 2;
                continue;
            }

            return Invalid($"Unbekanntes Argument '{token}'. {Usage}");
        }

        if (reportOnlyFile is not null && (days is not null || diagnostic))
        {
            return Invalid(
                $"'{ReportOnlyOption}' cannot be combined with '{DaysOption}' or '{DiagnosticOption}': a "
                + $"recompute inherits its window and its diagnostic flag from the frozen run it reads, "
                + $"rather than choosing them anew. {ReportOnlyUsage}");
        }

        return new HarnessCommandLineResult.RunHarness(channelName, days, diagnostic, reportOnlyFile);
    }

    private static HarnessCommandLineResult.Invalid Invalid(string message) => new(message);

    /// <summary>
    /// Whether <paramref name="value"/> is safe to use as a bare file name under
    /// <c>Harness:OutputDirectory</c>: no directory separator (so it cannot address another
    /// directory), not exactly <c>.</c> or <c>..</c> (the two special path segments), not starting
    /// with <c>-</c> (so it can never be mistaken for a flag by a later parse), and ending in
    /// <c>.jsonl</c> (so it can only ever name a harness protocol file, never an arbitrary path the
    /// process happens to be able to read).
    /// </summary>
    private static bool IsValidReportOnlyFileName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith('-')
        && !value.Contains('/', StringComparison.Ordinal)
        && !value.Contains('\\', StringComparison.Ordinal)
        && value is not ("." or "..")
        && value.EndsWith(".jsonl", StringComparison.Ordinal);
}
