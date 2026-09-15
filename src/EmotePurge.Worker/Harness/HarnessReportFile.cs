using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// What a run is, reduced to the values that make two runs comparable. Everything in here goes into
/// the report file's name (via its digest), so a changed window, a changed bot list or a changed
/// data snapshot cannot continue an older file — it starts a new one.
/// <para>
/// Deliberately without the load timestamp: that changes on every run and would make every resume
/// impossible. It sits next to the identity in <see cref="HarnessReportHeader"/> instead, where it
/// documents when the comparison data was read without pretending to identify it.
/// </para>
/// </summary>
public sealed record HarnessRunIdentity(
    string ChannelId,
    string TwitchChannelId,
    string ChannelName,
    DateOnly WindowFrom,
    DateOnly WindowTo,
    DateOnly? BotSplitCutover,
    DateOnly? SharedChatCutover,
    IReadOnlyList<string> BotAccountIds,
    string AlgorithmVersion,
    string InputHash);

/// <summary>Line 1 of the protocol.</summary>
public sealed record HarnessReportHeader(HarnessRunIdentity Identity, DateTime LoadedAtUtc);

/// <summary>
/// Something that happened to the run but produced no day: a 429, a body timeout, an exhausted byte
/// budget, a cancellation. Written so the next run can carry the count into its final report — the
/// day lines alone would say nothing about how often the archive refused.
/// <para>
/// <paramref name="Bytes"/> defaults to 0 so a file written before this field existed keeps
/// deserializing: its event lines simply carry no byte cost, which is the correct reading for them
/// (see the resume accounting in <see cref="HarnessRunner"/>). Not an <see cref="HarnessRunner.AlgorithmVersion"/>
/// bump — the day-line shape this field guards did not change.
/// </para>
/// </summary>
public sealed record HarnessEventLine(
    DateTime AtUtc, DateOnly Day, string Status, int? HttpStatusCode, string Message, long Bytes = 0);

/// <summary>Everything the protocol holds below its header.</summary>
public sealed record HarnessReportContent(IReadOnlyList<ReplayDayLine> Days, IReadOnlyList<HarnessEventLine> Events);

/// <summary>
/// A report file that cannot be continued. One base type for the two ways that happens, so the
/// runner answers both the same way: a German line and exit code 3, never a stack trace.
/// </summary>
public class HarnessReportFileException(string message) : Exception(message);

/// <summary>
/// Thrown when a report file's head describes a different run than the one about to continue it.
/// Reachable only by renaming a file by hand — the file name carries the identity digest — which is
/// exactly why the head is compared anyway.
/// </summary>
public sealed class HarnessReportIdentityMismatchException(string message) : HarnessReportFileException(message);

/// <summary>
/// Thrown for a damaged line anywhere but at the very end of the file. The last line is a different
/// case and never lands here: a process that died mid-write leaves an unfinished day, which the
/// reader simply drops.
/// </summary>
public sealed class HarnessReportCorruptException(string message) : HarnessReportFileException(message);

/// <summary>
/// The JSONL protocol of one harness run: one header line, one line per finished archive day, one
/// line per event, and the two final reports beside it.
/// <para>
/// Append-per-line with an immediate flush is the whole crash story (design Failure Mode "Abschluss
/// / Prozesstod"): a killed process costs the day that was in flight and nothing else, and the
/// closing step is a pure function of the lines on disk, so running it twice produces the same
/// <c>.report.json</c> byte for byte.
/// </para>
/// <para>
/// No chatter id, no message text and no chatter login is ever written here — the day counter
/// collapses its per-cell chatter sets before it hands a line over, and nothing on this path can
/// reintroduce them.
/// </para>
/// </summary>
public sealed class HarnessReportFile
{
    private const string HeaderKind = "header";
    private const string DayKind = "day";
    private const string EventKind = "event";

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // The Converters entry is read-only new code (issue #119): before TryReadExistingReport, nothing
    // ever deserialized a .report.json — only wrote one — so ValueList<T>'s missing read support
    // (it satisfies none of System.Text.Json's recognized collection shapes: no accessible
    // constructor-plus-Add, no recognized immutable-collection factory) never mattered. Writing is
    // unaffected: ValueListJsonConverter.Write delegates to the exact same IReadOnlyList<T> array
    // serialization System.Text.Json already used for it before this converter existed, so an
    // untouched report's bytes are unchanged.
    private static readonly JsonSerializerOptions ReportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new ValueListJsonConverterFactory() }
    };

    public HarnessReportFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        var stem = System.IO.Path.ChangeExtension(path, null);
        ReportJsonPath = stem + ".report.json";
        ReportMarkdownPath = stem + ".report.md";
    }

    public string Path { get; }

    public string ReportJsonPath { get; }

    public string ReportMarkdownPath { get; }

    public bool Exists => File.Exists(Path);

    /// <summary>
    /// Whether this run reached its end. <see cref="WriteFinalReportAtomically"/> is the only thing
    /// that ever writes <see cref="ReportJsonPath"/> and <see cref="ReportMarkdownPath"/>, and only
    /// after every day of the window is on disk — so both files' presence is the closing signal. Both,
    /// not just the JSON one: a process that dies between the two renames (or a second rename that
    /// fails outright) must not read as closed, or the run is skipped by every future resume search
    /// and the missing Markdown report never gets written.
    /// </summary>
    public bool IsClosed => File.Exists(ReportJsonPath) && File.Exists(ReportMarkdownPath);

    /// <summary>
    /// The file name a run of this identity has to use. The digest at the end is what makes
    /// "same head ⇒ resume, different head ⇒ new file" a property of the file system rather than of
    /// a comparison someone has to remember to do (Plan-Entscheidung 8).
    /// </summary>
    public static string BuildFileName(HarnessRunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(SerializeIdentity(identity))))[..12];

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Sanitize(identity.ChannelName)}-{identity.WindowFrom:yyyy-MM-dd}-{identity.WindowTo:yyyy-MM-dd}-{digest}.jsonl");
    }

    /// <summary>The canonical text form the identity is compared and hashed by.</summary>
    public static string SerializeIdentity(HarnessRunIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonSerializer.Serialize(identity, LineOptions);
    }

    public void WriteHeader(HarnessReportHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(Path, Serialize(new HarnessJsonLine(HeaderKind, header, null, null)));
    }

    /// <summary>
    /// Reads line 1 and refuses anything that is not the expected run, byte for byte on the
    /// canonical identity form.
    /// </summary>
    public HarnessReportHeader ReadHeader(HarnessRunIdentity expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);

        var completeLines = ReadCompleteLines(Path);
        var firstLine = completeLines.Count > 0
            ? completeLines[0]
            : throw new HarnessReportIdentityMismatchException(
                $"Die Berichtsdatei '{Path}' trägt keinen vollständig geschriebenen Kopf.");

        HarnessJsonLine? line;
        try
        {
            line = JsonSerializer.Deserialize<HarnessJsonLine>(firstLine, LineOptions);
        }
        catch (JsonException ex)
        {
            throw new HarnessReportIdentityMismatchException(
                $"Die erste Zeile der Berichtsdatei '{Path}' ist kein lesbarer Kopf: {ex.Message}");
        }

        if (line?.Kind != HeaderKind || line.Header is null)
        {
            throw new HarnessReportIdentityMismatchException(
                $"Die erste Zeile der Berichtsdatei '{Path}' ist kein Kopf.");
        }

        var expected = SerializeIdentity(expectedIdentity);
        var actual = SerializeIdentity(line.Header.Identity);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new HarnessReportIdentityMismatchException(
                $"Die Berichtsdatei '{Path}' trägt eine fremde Identität; ein Lauf mit anderem Fenster, "
                + "anderer Bot-Liste oder anderem Datenstand darf sie nicht fortsetzen.");
        }

        return line.Header;
    }

    /// <summary>
    /// Line 1 as it stands, compared to nothing, or <c>null</c> if this file carries no readable
    /// head. The counterpart to <see cref="ReadHeader"/> for the one caller that cannot name the
    /// file it is looking for: the frozen window is part of the file name, so a resume has to read
    /// heads to find it. Unreadable is not an error here — a foreign or damaged file in the output
    /// directory simply is not a resume candidate.
    /// </summary>
    public HarnessReportHeader? TryReadHeader()
    {
        try
        {
            var completeLines = ReadCompleteLines(Path);
            if (completeLines.Count == 0)
            {
                return null;
            }

            var line = JsonSerializer.Deserialize<HarnessJsonLine>(completeLines[0], LineOptions);

            // A syntactically valid envelope is not the same as a usable header: a foreign or damaged
            // file can deserialize into a header whose Identity is null, and the one caller of this
            // method (HarnessRunner.FindFrozenWindow) dereferences header.Identity right away. This is
            // exactly the "unlesbar" case the method promises to fold into null, not evaluate.
            return line?.Kind == HeaderKind && line.Header?.Identity is not null ? line.Header : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every finished day and every event below the header. A truncated last line — the process
    /// died mid-write — is discarded, because that day was never finished; a truncated line
    /// anywhere else is corruption and throws, since silently skipping it would leave a gap the
    /// final report would then present as a complete window.
    /// </summary>
    public HarnessReportContent ReadDays()
    {
        var days = new List<ReplayDayLine>();
        var events = new List<HarnessEventLine>();

        if (!Exists)
        {
            return new HarnessReportContent(days, events);
        }

        // A truncated last line is filtered out by ReadCompleteLines before this loop ever sees it —
        // it is the trace of a process that died mid-write, the exact fragment RemoveDanglingTail
        // erases on the next append, and a day that was never finished. Every line reaching the loop
        // below is therefore complete by construction, so a deserialize failure anywhere in it is
        // genuine corruption regardless of position.
        var lines = ReadCompleteLines(Path);
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            HarnessJsonLine? line;
            try
            {
                line = JsonSerializer.Deserialize<HarnessJsonLine>(raw, LineOptions);
            }
            catch (JsonException ex)
            {
                throw new HarnessReportCorruptException(
                    $"Zeile {i + 1} der Berichtsdatei '{Path}' ist unlesbar; die Datei ist beschädigt: {ex.Message}");
            }

            switch (line?.Kind)
            {
                case HeaderKind:
                    break;
                case DayKind when line.Day is not null:
                    days.Add(line.Day);
                    break;
                case EventKind when line.Event is not null:
                    events.Add(line.Event);
                    break;
                default:
                    throw new HarnessReportCorruptException(
                        $"Zeile {i + 1} der Berichtsdatei '{Path}' ist unlesbar; die Datei ist beschädigt.");
            }
        }

        return new HarnessReportContent(days, events);
    }

    public void AppendDay(ReplayDayLine day)
    {
        ArgumentNullException.ThrowIfNull(day);
        Append(new HarnessJsonLine(DayKind, null, day, null));
    }

    public void AppendEvent(HarnessEventLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Append(new HarnessJsonLine(EventKind, null, null, line));
    }

    /// <summary>
    /// Writes both final reports through a temp file plus rename, so a reader never sees half a
    /// report and a crash mid-write leaves the previous one intact.
    /// </summary>
    public void WriteFinalReportAtomically(ReplayFinalReport report, string markdown)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(markdown);

        WriteAtomically(ReportJsonPath, JsonSerializer.Serialize(report, ReportOptions));
        WriteAtomically(ReportMarkdownPath, markdown);
    }

    /// <summary>
    /// The final report already sitting at <see cref="ReportJsonPath"/>, or <c>null</c> if it does
    /// not exist, cannot be read, or cannot be parsed — the same tolerance <see cref="TryReadHeader"/>
    /// gives a foreign or damaged file. The one caller is a report-only recompute (issue #119), which
    /// reads <c>Run.Diagnostic</c> off it: that flag lives only in a closed report, never in the
    /// header or the identity (Plan-Entscheidung 7, D4), so a recompute has nowhere else to inherit
    /// it from. Deliberately tolerant — a damaged or half-written <c>.report.json</c> must not block
    /// a recompute that only actually needs the <c>.jsonl</c> beside it; the caller falls back to the
    /// fail-closed default instead (D4's spirit: a missing, unparsable or unreadable original never
    /// silently upgrades a diagnostic run into a binding one — see <c>HarnessRunner.ExecuteRecomputeAsync</c>).
    /// </summary>
    public ReplayFinalReport? TryReadExistingReport()
    {
        if (!File.Exists(ReportJsonPath))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(ReportJsonPath);

            // Fail-closed rather than merely tolerant (issue #119, second review round): a
            // syntactically valid report can still lack a usable Run.Diagnostic — a pre-#97 report
            // predates that JSON property, and a hand-edited or foreign one could omit it or give it
            // the wrong shape. A missing or non-boolean run.diagnostic must read the same as a
            // missing report, not as `false` sneaking in through the deserializer's own default for
            // an absent bool. Checked with JsonDocument rather than by turning on
            // RespectRequiredConstructorParameters/RespectNullableAnnotations on ReportOptions:
            // either of those would make every report whose JSON predates a later-added property
            // (the #97 tie fields, this very Recomputation field) fail to deserialize at all —
            // downgrading it to "no report" for a reason that has nothing to do with the diagnostic
            // flag.
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("run", out var run)
                || run.ValueKind != JsonValueKind.Object
                || !run.TryGetProperty("diagnostic", out var diagnostic)
                || diagnostic.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            var report = JsonSerializer.Deserialize<ReplayFinalReport>(text, ReportOptions);
            return report?.Run is null ? null : report;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a final report pair the same temp-file-plus-rename way as
    /// <see cref="WriteFinalReportAtomically"/>, but to caller-supplied paths rather than this
    /// instance's own <see cref="ReportJsonPath"/>/<see cref="ReportMarkdownPath"/>, and — the one
    /// real difference — the rename never overwrites. The one caller is a report-only recompute
    /// (issue #119): its output sits beside the run under a timestamped name and must never land on
    /// the paths that close the original run (writing there would flip <see cref="IsClosed"/> for a
    /// run the recompute never actually finished fetching, and would silently discard whatever the
    /// original run had written), and must never silently replace another recompute either — unlike
    /// <see cref="WriteFinalReportAtomically"/>, which legitimately overwrites an ordinary run's own
    /// report on every resume, a recompute has no "own" file here to overwrite: a collision on these
    /// paths can only mean two recomputes landed on the same timestamp, and
    /// <c>HarnessRunner.ExecuteRecomputeAsync</c> already refuses that case up front — this is the
    /// same refusal, closing the gap between that check and the write a moment later.
    /// <see cref="WriteAtomically"/> itself stays untouched
    /// (overwriting), because the ordinary run's own resume genuinely needs it to.
    /// </summary>
    /// <exception cref="HarnessReportFileException">
    /// Either target already existed at the moment of the rename (a race past the caller's own
    /// existence check). The offending temp file is deleted before this throws, so no stray
    /// <c>.tmp</c> is left behind.
    /// </exception>
    public static void WriteReportPairAtomically(string jsonPath, string markdownPath, ReplayFinalReport report, string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(markdownPath);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(markdown);

        WriteWithoutOverwrite(jsonPath, JsonSerializer.Serialize(report, ReportOptions));
        WriteWithoutOverwrite(markdownPath, markdown);
    }

    private void Append(HarnessJsonLine line)
    {
        RemoveDanglingTail();
        File.AppendAllText(Path, Serialize(line));
    }

    /// <summary>
    /// Drops a truncated last line — the trace of a process that died mid-write — before the next
    /// line is appended. Left alone, the fragment would sit under the new line and turn what
    /// <see cref="ReadDays"/> can still shrug off as a dropped, unfinished day into corruption in the
    /// middle of the file, which it refuses to continue past.
    /// <para>
    /// Runs here, on the write path, rather than inside <see cref="ReadDays"/>, so a read never
    /// mutates the file on disk — that would be surprising for a method whose name promises only
    /// reading. Every complete line this class ever writes ends in <c>"\n"</c> (<see cref="Serialize"/>),
    /// so a file not ending in one can only be a write that was cut off mid-flight — the exact case
    /// <see cref="ReadDays"/> already treats as "the last line, drop it" — and truncating back to the
    /// last newline is enough to undo it.
    /// </para>
    /// </summary>
    private void RemoveDanglingTail()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(Path);
        if (bytes.Length == 0 || bytes[^1] == (byte)'\n')
        {
            return;
        }

        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Write);
        stream.SetLength(lastNewline + 1);
    }

    /// <summary>
    /// The lines this file currently holds that are actually complete — terminated by <c>"\n"</c>,
    /// the same rule <see cref="RemoveDanglingTail"/> enforces on the write side. Every reader in this
    /// class (<see cref="ReadHeader"/>, <see cref="TryReadHeader"/>, <see cref="ReadDays"/>) goes
    /// through here so the two sides can never disagree about what a finished record is: a line
    /// without its trailing newline can only be the trace of a process that died mid-write, and
    /// <see cref="RemoveDanglingTail"/> erases exactly that fragment before the next append — a reader
    /// that had already accepted it would then report a record the file no longer contains.
    /// </summary>
    private static IReadOnlyList<string> ReadCompleteLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var completeLength = bytes.Length > 0 && bytes[^1] == (byte)'\n'
            ? bytes.Length
            : Array.LastIndexOf(bytes, (byte)'\n') + 1;

        return completeLength == 0
            ? []
            : Encoding.UTF8.GetString(bytes, 0, completeLength).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Serialize(HarnessJsonLine line) => JsonSerializer.Serialize(line, LineOptions) + "\n";

    private static void WriteAtomically(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path, overwrite: true);
    }

    /// <summary>
    /// <see cref="WriteAtomically"/> with <c>overwrite: false</c> on the rename — see
    /// <see cref="WriteReportPairAtomically"/>, the only caller, for why. <see cref="File.Move(string, string, bool)"/>
    /// throws <see cref="IOException"/> when the destination already exists; that is turned into a
    /// <see cref="HarnessReportFileException"/> here, after cleaning up the temp file, so the one
    /// caller never has to know the collision surfaced as an I/O exception rather than a domain one.
    /// </summary>
    private static void WriteWithoutOverwrite(string path, string content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, content);
        try
        {
            File.Move(temporaryPath, path, overwrite: false);
        }
        catch (IOException ex)
        {
            File.Delete(temporaryPath);
            throw new HarnessReportFileException($"'{path}' already exists: {ex.Message}");
        }
    }

    private static string Sanitize(string channelName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(channelName.Length);
        foreach (var character in channelName)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The one envelope every protocol line is written as. One type rather than three keeps reading
    /// to a single deserialize call, and <c>kind</c> stays the first property so a line is
    /// recognizable by eye in the file.
    /// </summary>
    private sealed record HarnessJsonLine(
        string Kind,
        HarnessReportHeader? Header,
        ReplayDayLine? Day,
        HarnessEventLine? Event);

    /// <summary>
    /// Read/write support for <see cref="ValueList{T}"/> in <see cref="ReportOptions"/> (issue #119,
    /// see the remark there). <see cref="ValueListConverter{T}.Write"/> re-serializes the value as a
    /// plain <c>IReadOnlyList&lt;T&gt;</c> — the exact array System.Text.Json already produced for it
    /// without any converter — so an untouched report's bytes do not move.
    /// </summary>
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
