using System.Diagnostics;
using System.Globalization;
using System.Text;
using EmotePurge.Core.ChatLogArchive;
using EmotePurge.Core.Services;
using Microsoft.Extensions.Logging;

namespace EmotePurge.Worker.Harness;

/// <summary>
/// One harness run of issue #69: read one channel's frozen 30-day window out of the chat-log
/// archive, count it with the live path's own matching rule and bot detector, and write the
/// pre-registered figures next to the live <c>UsageStat</c> numbers.
/// <para>
/// Read-only end to end. It touches Postgres exclusively through <see cref="IChannelService"/> and
/// <see cref="IUsageStatQueryService"/> (Regel 4), never writes a row, and never publishes or
/// subscribes on Redis itself. Everything it produces is a file.
/// </para>
/// <para>
/// <b>It still needs Redis reachable at startup, just not for anything of its own.</b>
/// <see cref="IChannelService"/> takes an <c>IRedisPublisher</c> constructor dependency, which sits
/// on the same eager-connecting <c>IConnectionMultiplexer</c> singleton the whole worker image
/// shares (<c>ServiceCollectionExtensions.AddEmotePurgeInfrastructure</c>, no
/// <c>abortConnect=false</c>) — resolving this class resolves that singleton, and
/// <c>ConnectionMultiplexer.Connect</c> is synchronous and throws if nothing answers. Not worth
/// carving a Redis-free construction path out of shared DI wiring for a property nobody needs;
/// <c>Program</c> wraps the resolution in a try/catch instead and returns
/// <see cref="ExitUnexpectedError"/> with a German line, so an unreachable Redis at startup gets a
/// documented exit code rather than a runtime-invented one.
/// </para>
/// <para>
/// <b>The archive client is taken once, through the constructor, and held for the whole day
/// loop.</b> Its minimum delay between two requests is instance state, and the typed-client
/// registration is transient: a runner that resolved a fresh client per channel-day would drop the
/// only self-imposed protection against hammering a free third-party service — silently, with no
/// exception and no log line (T3 review finding; see the registration comment in
/// <c>ServiceCollectionExtensions</c>).
/// </para>
/// <para>
/// The return value is the process exit code, and the four non-zero ones say different things on
/// purpose: a violated precondition (3) means the question could not be asked, an abort (4) means
/// it can be asked again from the resume point, and "undecidable" (5) means the approach itself has
/// to be reassessed. None of them is "the numbers were bad" — that verdict is a human reading the
/// report against the pre-registration in #69.
/// </para>
/// <para>
/// <b>Honours the same objection gate as the live worker (GDPR Art. 21, issue #252/#260).</b> A
/// message from an id in <c>Twitch:ExcludedChatterIds</c> is dropped in the counting callback before
/// <c>sawUserId</c>/<c>sawBadges</c> bookkeeping and before it reaches <see cref="ReplayDayCounter"/>
/// at all — the same ordering <c>TwitchChatManager.OnMessageReceived</c> uses for the live path, so a
/// replayed archive cannot resurface what an objecting chatter's live traffic no longer produces.
/// </para>
/// </summary>
public sealed class HarnessRunner(
    IChannelService channelService,
    IUsageStatQueryService usageStatQueryService,
    IChatLogArchiveClient archiveClient,
    IBotChatterDetector botChatterDetector,
    IExcludedChatterFilter excludedChatterFilter,
    HarnessOptions options,
    TimeProvider timeProvider,
    ILogger<HarnessRunner> logger)
{
    /// <summary>
    /// Part of the run identity: a changed counting rule must not resume a file counted by the old
    /// one. Bump it whenever the matching, the day boundaries or the day-line shape change — as it
    /// just did for the day-line shape and the shared-chat rule (#73): a message from a foreign or
    /// indeterminate room now moves <see cref="ReplayDayLine.SharedChatCounts"/> instead of
    /// <see cref="ReplayDayLine.HumanCounts"/> or <see cref="ReplayDayLine.BotCounts"/>, so a
    /// "harness-1" file must not be silently resumed under the new rule.
    /// <para>
    /// Bumped again to "harness-3" for the objection gate itself (issue #252/#260, P1 Codex
    /// finding): the counting callback below started dropping excluded chatters' messages before
    /// <see cref="HarnessRunIdentity"/> carried anything that depended on the exclusion list, so a
    /// run started before the gate existed could be resumed under it silently — the file's day
    /// lines would still hold the excluded chatter's counts from before the gate applied, mixed
    /// into the same report as fresh, gated days from the resumed run.
    /// <see cref="HarnessRunIdentity.ExcludedChatterIdsDigest"/> (added in the same change) covers
    /// every *later* change of the list the same way; this version bump is what invalidates
    /// everything written before either existed at all.
    /// </para>
    /// </summary>
    public const string AlgorithmVersion = "harness-3";

    /// <summary>The window covered completely; both final reports were written.</summary>
    public const int ExitSuccess = 0;

    /// <summary>
    /// The command line did not parse (see <see cref="HarnessCommandLine.Parse"/>): unknown verb,
    /// missing channel, malformed <c>--days</c>, or extra arguments. Returned by <c>Program</c>
    /// before a host is even built, so it lives here only as a named constant for the exit-code
    /// list to stay in one place — the value itself never runs through <see cref="RunAsync"/>.
    /// </summary>
    public const int ExitInvalidArguments = 2;

    /// <summary>A precondition was violated; the question could not be asked at all.</summary>
    public const int ExitPreconditionViolated = 3;

    /// <summary>Aborted with a resume point: 429, body timeout, byte ceiling, transport, cancellation.</summary>
    public const int ExitAbortedWithResumePoint = 4;

    /// <summary>The logs carry neither badges nor user ids, so the bot split cannot be made.</summary>
    public const int ExitUndecidable = 5;

    /// <summary>
    /// Something inside the harness itself failed — most plausibly the counting callback, whose
    /// exceptions the archive client propagates to us by contract (T3). Deliberately not folded into
    /// <see cref="ExitAbortedWithResumePoint"/>: that code invites "just run it again", and a defect
    /// in our own counting would meet the operator with the same failure a second time. The full
    /// exception is logged; the file on disk stays valid and resumable.
    /// </summary>
    public const int ExitUnexpectedError = 6;

    private const int BytesPerMegabyte = 1024 * 1024;

    /// <summary>
    /// How far behind today an unfinished run's frozen window may end and still be continued.
    /// <para>
    /// Crossing a UTC midnight is the normal case, not the exception: 30 days of a large channel are
    /// ~490 MB against a 200 MB cap, so the binding run has to be invoked several times. Weeks later
    /// it is a different question — the report would be dated today and answer for a window nobody
    /// asked about any more. Past this age the abandoned file is left where it is (it stays readable
    /// evidence of the requests it made) and a fresh measurement starts, with a log line saying so.
    /// </para>
    /// </summary>
    private const int MaxResumeAgeInDays = 7;

    // The one date format this report ever writes for a standalone date — both the day boundary
    // parsed from configuration and every rendered day in the Markdown below use it, so the two
    // sides can never spell it differently.
    private const string IsoDateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Runs once and returns the process exit code. Nothing escapes as an exception: the operator of
    /// a one-shot container gets a German line and a defined code, never a stack trace with an
    /// exit status invented by the runtime.
    /// </summary>
    public async Task<int> RunAsync(string channelName, int days, bool diagnostic, CancellationToken ct)
    {
        try
        {
            return await ExecuteAsync(channelName, days, diagnostic, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled outside the day loop — during a query, say. The loop has its own handler that
            // still gets an event line written; here there is no day in flight to record.
            logger.LogError("Der Harness-Lauf für Kanal '{Kanal}' wurde abgebrochen.", channelName);
            return ExitAbortedWithResumePoint;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Der Harness-Lauf für Kanal '{Kanal}' ist mit einem unerwarteten Fehler abgebrochen. Bereits geschriebene Tageszeilen bleiben gültig; ein erneuter Aufruf setzt dort fort.",
                channelName);
            return ExitUnexpectedError;
        }
    }

    /// <summary>
    /// Report-only recompute of an existing run (issue #119): reads the frozen <c>.jsonl</c> named
    /// by <paramref name="reportOnlyFileName"/> and the header's own window, reloads the live side
    /// through the exact same services <see cref="RunAsync"/> uses, and writes a second, timestamped
    /// report pair beside the original — never touching the original <c>.jsonl</c>,
    /// <c>.report.json</c> or <c>.report.md</c>, and never calling <c>IChatLogArchiveClient</c>.
    /// <para>
    /// Deliberately its own entry point rather than a branch inside <see cref="RunAsync"/>/
    /// <see cref="ExecuteAsync"/>: it skips the shared-chat-cutover fail-closed precondition (D4)
    /// and the configured <c>Harness:SharedChatCutover</c> entirely — a recompute's window, bot
    /// list and cutovers all come from the frozen header, never from today's configuration or
    /// today's clock, so none of that precondition applies to it.
    /// </para>
    /// <para>
    /// Exit codes are the existing ones: every refusal below — a missing file, an unreadable header,
    /// a channel that no longer matches, a window with a gap, a window with no log anywhere, an
    /// already-existing recompute pair at the same timestamp — is <see cref="ExitPreconditionViolated"/>,
    /// the same code an ordinary run uses for "the question could not be asked at all". A hash or
    /// cutover mismatch is deliberately <b>not</b> one of these: the recompute still runs to
    /// completion and reports the drift as a warning (see <see cref="HarnessRecomputation"/>) rather
    /// than refusing, because a stale snapshot is exactly the case this method exists to make
    /// visible instead of silently produce.
    /// </para>
    /// </summary>
    public async Task<int> RecomputeReportAsync(string channelName, string reportOnlyFileName, CancellationToken ct)
    {
        try
        {
            return await ExecuteRecomputeAsync(channelName, reportOnlyFileName, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Report-only recompute for channel '{Channel}' was cancelled.", channelName);
            return ExitAbortedWithResumePoint;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Report-only recompute for channel '{Channel}' failed unexpectedly.", channelName);
            return ExitUnexpectedError;
        }
    }

    private async Task<int> ExecuteAsync(string channelName, int days, bool diagnostic, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);

        // Both sources of this number end up here: the command line, which HarnessCommandLine has
        // already clamped, and Harness:WindowDays, which nothing else checks — an environment
        // variable in the compose file would otherwise buy a 500-day run past the same limit.
        if (days < HarnessCommandLine.MinDays || days > HarnessCommandLine.MaxDays)
        {
            logger.LogError(
                "Die Fensterlänge {Tage} liegt außerhalb der erlaubten {Min} bis {Max} Tage; per '--days' oder über 'Harness:WindowDays' korrigieren.",
                days, HarnessCommandLine.MinDays, HarnessCommandLine.MaxDays);
            return ExitPreconditionViolated;
        }

        // Fail-closed (D4, Plan-Entscheidung 6): missing, blank or unparsable are all the same
        // failure. Runs before any database or archive access — a forgotten configuration value and
        // a deliberate diagnostic run are otherwise indistinguishable once both end in exit 0, and
        // the binding run is read weeks later by someone who is not the person who started it.
        // '--diagnostic' is the only escape, and only when the value is genuinely absent: a value
        // that IS set but does not parse stays an error even in diagnostic mode, so a typo never
        // silently reads as "no cutover".
        DateOnly? sharedChatCutover = null;
        if (string.IsNullOrWhiteSpace(options.SharedChatCutover))
        {
            if (!diagnostic)
            {
                logger.LogError(
                    "'Harness:SharedChatCutover' fehlt oder ist leer; ohne diesen Stichtag kann kein Gate-Urteil gefällt werden. Erwartet ist ein UTC-Datum der Form 'yyyy-MM-dd' — der Tag nach dem Prod-Deploy, nicht der Deploy-Tag selbst —, oder '--diagnostic' für einen Lauf ohne Urteil.");
                return ExitPreconditionViolated;
            }

            logger.LogWarning(
                "'Harness:SharedChatCutover' fehlt oder ist leer; der Diagnoselauf misst ohne Stichtag und weist kein Gate-Urteil aus.");
        }
        else if (!DateOnly.TryParseExact(
            options.SharedChatCutover, IsoDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedCutover))
        {
            logger.LogError(
                "'Harness:SharedChatCutover' = '{Wert}' ist kein Datum der Form 'yyyy-MM-dd' (der Tag nach dem Prod-Deploy, nicht der Deploy-Tag selbst); ein Tippfehler darf nie still als 'kein Stichtag' durchgehen.",
                options.SharedChatCutover);
            return ExitPreconditionViolated;
        }
        else
        {
            sharedChatCutover = parsedCutover;
        }

        var stopwatch = Stopwatch.StartNew();
        var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        var channel = await channelService.GetByNameAsync(channelName, ct);
        if (channel is null)
        {
            logger.LogError("Kanal '{Kanal}' ist nicht erfasst; ohne Kanalzeile gibt es nichts zu vergleichen.", channelName);
            return ExitPreconditionViolated;
        }

        // No login fallback, deliberately (#34/#44): the archive is addressed by the immutable
        // Twitch id, and falling back to the login is exactly how a renamed channel ended up as two
        // rows from two sources.
        if (string.IsNullOrEmpty(channel.TwitchChannelId))
        {
            logger.LogError(
                "Kanal '{Kanal}' hat keine Twitch-ID; der Log-Abruf läuft ausschließlich über die unveränderliche ID (#34/#44).",
                channel.ChannelName);
            return ExitPreconditionViolated;
        }

        // A fresh window ends on the last fully finished UTC day (Codex-adversarial: today is still
        // being written by the live worker), and starts one day after tracking resumed, because the
        // join day itself is only partially measured. A run that is being continued does not derive
        // one at all — it inherits the frozen window of the file it continues (see FindFrozenWindow).
        var freshTo = DateOnly.FromDateTime(startedAtUtc).AddDays(-1);
        var trackedSince = TrackingCoverage.TrackedSince(channel.TrackingResumedAt, channel.CreatedAt);
        var firstFullyTrackedDay = DateOnly.FromDateTime(trackedSince).AddDays(1);

        var frozen = FindFrozenWindow(channel.Id, channel.ChannelName, days, freshTo);
        var to = frozen?.To ?? freshTo;
        var from = frozen?.From ?? Later(firstFullyTrackedDay, to.AddDays(-(days - 1)));

        // Two different failures, one message. A fresh window is short when tracking started too
        // late; an inherited one is short when tracking *restarted* into the window after it was
        // frozen — a channel that left and rejoined. Both mean the same thing to the operator.
        var availableDays = to.DayNumber - from.DayNumber + 1;
        var measuredDays = to.DayNumber - firstFullyTrackedDay.DayNumber + 1;
        if (availableDays != days || measuredDays < days)
        {
            logger.LogError(
                "Kanal '{Kanal}' hat ab dem ersten vollständig gemessenen Tag ({Start}) nur {Vorhanden} Tage Messung bis {Ende}; verlangt sind {Verlangt}.",
                channel.ChannelName, Iso(firstFullyTrackedDay), Math.Max(measuredDays, 0), Iso(to), days);
            return ExitPreconditionViolated;
        }

        var lifetimes = await usageStatQueryService.GetEmoteLifetimesAsync(channel.Id, ct);
        var liveRowDtos = await usageStatQueryService.GetRowsAsync([.. lifetimes.Select(e => e.Id)], from, to, ct);
        var botSplitCutover = await usageStatQueryService.GetEarliestBotUsageDateAsync(channel.Id, ct);
        var botAccountIds = botChatterDetector.KnownBotAccountIds;
        var loadedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        var identity = new HarnessRunIdentity(
            channel.Id,
            channel.TwitchChannelId,
            channel.ChannelName,
            from,
            to,
            botSplitCutover,
            sharedChatCutover,
            [.. botAccountIds.Order(StringComparer.Ordinal)],
            ExcludedChatterIdsDigest.Compute(excludedChatterFilter.ExcludedChatterIds),
            AlgorithmVersion,
            HarnessInputHash.Compute(lifetimes, liveRowDtos, botAccountIds, from));

        var file = new HarnessReportFile(Path.Combine(options.OutputDirectory, HarnessReportFile.BuildFileName(identity)));
        HarnessReportContent existing;
        bool resumed;
        if (file.Exists)
        {
            try
            {
                file.ReadHeader(identity);
                existing = file.ReadDays();
            }
            catch (HarnessReportFileException ex)
            {
                // A foreign identity or a damaged line in the middle of the file. Both mean the same
                // thing — this file cannot be continued — and both end the same way: a German line
                // and exit code 3, never a stack trace. (A truncated *last* line is not damage; the
                // reader drops it, because that day was never finished.)
                logger.LogError(ex, "Die vorhandene Berichtsdatei '{Datei}' kann nicht fortgesetzt werden.", file.Path);
                return ExitPreconditionViolated;
            }

            resumed = true;
        }
        else
        {
            file.WriteHeader(new HarnessReportHeader(identity, loadedAtUtc));
            existing = new HarnessReportContent([], []);
            resumed = false;
        }

        // Deliberately its own type, not the query DTO — see the remark on ReplayEmote.
        var emotes = lifetimes
            .Select(e => new ReplayEmote(e.Id, e.Name, e.IsArchived, e.FirstSeenAt, e.ArchivedAt, e.LastSyncedAt))
            .ToList();
        var liveRows = liveRowDtos
            .Select(r => new ReplayUsageRow(r.EmoteId, r.Date, r.UseCount, r.BotUseCount, r.SharedChatUseCount))
            .ToList();
        var window = new ReplayWindow(from, to, botSplitCutover, sharedChatCutover);

        var dayLines = existing.Days.ToDictionary(d => d.Day);
        // Both finished days and aborted-but-received attempts count against the cap: a day that
        // ended in ByteCapExceeded/BodyTimeout/TransportFailure still pulled bytes out of the archive,
        // it just never became a day line. Without the event half, five resumes could each burn a
        // fresh cap's worth of bytes against a service that never agreed to any of it (DECISIONS: the
        // cap is a foreign-load brake "über alle Tage und alle Resumes eines Laufs").
        var bytesUsed = existing.Days.Sum(d => d.Bytes) + existing.Events.Sum(e => e.Bytes);
        var capBytes = (long)options.MaxMegabytesPerRun * BytesPerMegabyte;

        // Plan-Entscheidung 13: the window-wide chatter set is transient and never written, so a
        // resumed run cannot know it. The per-(emote, day) k-distribution is unaffected — it is
        // closed inside each day line.
        var distinctChatters = resumed ? null : new HashSet<string>(StringComparer.Ordinal);
        var fallbackChecked = false;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (dayLines.ContainsKey(day))
            {
                continue;
            }

            var remainingBytes = capBytes - bytesUsed;
            if (remainingBytes <= 0)
            {
                AppendAbort(file, day, "ByteCapExceeded", null,
                    $"Byte-Obergrenze von {options.MaxMegabytesPerRun} MB erreicht.");
                LogResumePoint(dayLines, "Byte-Obergrenze erreicht", day);
                return ExitAbortedWithResumePoint;
            }

            var counter = new ReplayDayCounter(day, emotes, botChatterDetector.IsBot);
            var sawUserId = false;
            var sawBadges = false;

            ChatLogDayResult result;
            try
            {
                result = await archiveClient.ReadDayAsync(
                    channel.TwitchChannelId,
                    day,
                    remainingBytes,
                    message =>
                    {
                        // Objection gate (GDPR Art. 21, issue #252/#260): dropped before the
                        // sawUserId/sawBadges bookkeeping below and before the message reaches
                        // ReplayDayCounter at all — same ordering as the early return in
                        // TwitchChatManager.OnMessageReceived, so a replayed archive counts nothing
                        // an excluded chatter's live traffic would not count either. Matches only
                        // the immutable Twitch user id, never a login.
                        if (excludedChatterFilter.IsExcluded(message.UserId))
                        {
                            return ValueTask.CompletedTask;
                        }

                        if (!string.IsNullOrEmpty(message.UserId))
                        {
                            sawUserId = true;
                        }

                        if (message.Badges.Count > 0)
                        {
                            sawBadges = true;
                        }

                        // Classify first, add second (Spec B5): the window-wide chatter set has to
                        // follow the same own/foreign rule as the per-day counter's own chatter set,
                        // so the category must be known before the Add, not derived from it. Both
                        // "distinct chatters" numbers in the report mean own human chatters as of
                        // harness-2.
                        var category = counter.Count(
                            message.SentAtUtc, message.UserId, message.Badges, message.RoomId, message.SourceRoomId,
                            message.HasOtherSourceMarkers, message.Text);
                        if (category == UsageCategory.Human && !string.IsNullOrEmpty(message.UserId))
                        {
                            distinctChatters?.Add(message.UserId);
                        }

                        return ValueTask.CompletedTask;
                    },
                    ct);
            }
            catch (OperationCanceledException)
            {
                // docker stop / Ctrl-C. Everything up to the previous day is on disk already, so
                // this is an ordinary resume point rather than a loss.
                // This day's bytes are booked as 0 even though a cancellation mid-body did pull real
                // ones. Not a judgement that they are free: the archive client lets a caller
                // cancellation propagate bare (ChatLogArchiveClient class doc), so the count dies with
                // the stream and this caller cannot learn it. Followed up in #82.
                AppendAbort(file, day, "Cancelled", null, "Lauf abgebrochen.");
                LogResumePoint(dayLines, "abgebrochen", day);
                return ExitAbortedWithResumePoint;
            }

            switch (result.Status)
            {
                case ChatLogDayStatus.Complete:
                    // Design "Rückfall": logs without any badge and without any user id make the bot
                    // split impossible, and a report without it would answer a different question
                    // than the one #69 asks. No day line is written, so a later run re-fetches this
                    // day and reaches the same verdict instead of quietly building on it. The day was
                    // still read in full, though, so its bytes go on the event line — same as the
                    // `default:` branch below — or a resume would see the cap as untouched.
                    if (!fallbackChecked && result.MessageCount > 0)
                    {
                        fallbackChecked = true;
                        if (!sawUserId && !sawBadges)
                        {
                            AppendAbort(file, day, "NoBadgesNoUserIds", result.HttpStatusCode,
                                "Logs ohne Badges und ohne user-id.", result.BytesReceived);
                            logger.LogError(
                                "Die Logs für Kanal '{Kanal}', Tag {Tag} tragen weder Badges noch user-id; der Bot-Split ist damit nicht möglich und der Ansatz neu zu bewerten.",
                                channel.ChannelName, Iso(day));
                            return ExitUndecidable;
                        }
                    }

                    AppendDay(file, dayLines, counter.Finish(
                        ReplayDayStatuses.Complete,
                        result.BytesReceived,
                        result.BodySha256Hex,
                        result.NonPrivmsgLines,
                        result.MalformedLines));
                    bytesUsed += result.BytesReceived;
                    break;

                case ChatLogDayStatus.NoLogDay:
                    AppendDay(file, dayLines, counter.Finish(
                        ReplayDayStatuses.NoLog, result.BytesReceived, null, result.NonPrivmsgLines, result.MalformedLines));
                    bytesUsed += result.BytesReceived;
                    break;

                default:
                    AppendAbort(file, day, result.Status.ToString(), result.HttpStatusCode,
                        $"Log-Archiv-Abruf endete mit {result.Status}.", result.BytesReceived);
                    LogResumePoint(dayLines, result.Status.ToString(), day);
                    return ExitAbortedWithResumePoint;
            }
        }

        var allDays = dayLines.Values.OrderBy(d => d.Day).ToList();

        // Log coverage is checked here rather than before the first request, because the archive
        // offers no HEAD and a 404 is its normal answer for a quiet day (T8). The file stays: it is
        // the evidence that the requests were made.
        if (allDays.Count > 0 && allDays.TrueForAll(d => d.Status == ReplayDayStatuses.NoLog))
        {
            logger.LogError(
                "Für keinen der {Tage} Tage von {Von} bis {Bis} hat das Archiv ein Log; für Kanal '{Kanal}' ist der Vergleich nicht durchführbar.",
                allDays.Count, Iso(from), Iso(to), channel.ChannelName);
            return ExitPreconditionViolated;
        }

        // The 429 count lives in the event lines, never in the day lines: a throttled day gets no day
        // line at all, because a day line means "finished" and would make the resume skip the day
        // forever. That is why both this number and the resume point are handed to the calculator
        // instead of derived from the day lines — derived, they would read 0 and "window end" in
        // every report ever written.
        var rateLimitedDays = existing.Events.Count(e => e.HttpStatusCode == 429);
        var resumePoint = allDays.Count == 0 ? (DateOnly?)null : allDays[^1].Day;

        var report = ReplayFidelityCalculator.Compute(
            window, emotes, liveRows, allDays, days, runComplete: true, bytesUsed, rateLimitedDays, resumePoint, diagnostic);

        file.WriteFinalReportAtomically(report, BuildMarkdown(
            identity, report, allDays, liveRows, loadedAtUtc, timeProvider.GetUtcNow().UtcDateTime,
            stopwatch.Elapsed, bytesUsed, rateLimitedDays, distinctChatters?.Count));

        logger.LogInformation(
            "Harness-Lauf für Kanal '{Kanal}' abgeschlossen: {Tage} Tage, {Bytes} Bytes, Bericht in '{Datei}'.",
            channel.ChannelName, allDays.Count, bytesUsed, file.ReportMarkdownPath);
        return ExitSuccess;
    }

    /// <summary>See <see cref="RecomputeReportAsync"/> for the contract; this is its body.</summary>
    private async Task<int> ExecuteRecomputeAsync(string channelName, string reportOnlyFileName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportOnlyFileName);

        var stopwatch = Stopwatch.StartNew();
        var recomputedAtUtc = timeProvider.GetUtcNow().UtcDateTime;

        var sourceFile = new HarnessReportFile(Path.Combine(options.OutputDirectory, reportOnlyFileName));
        if (!sourceFile.Exists)
        {
            logger.LogError(
                "Report-only file '{File}' does not exist under '{Directory}'; nothing to recompute.",
                reportOnlyFileName, options.OutputDirectory);
            return ExitPreconditionViolated;
        }

        var header = sourceFile.TryReadHeader();
        if (header is null)
        {
            logger.LogError("Report-only file '{File}' has no readable header; it cannot be recomputed.", sourceFile.Path);
            return ExitPreconditionViolated;
        }

        var identity = header.Identity;

        // Refuses a foreign AlgorithmVersion before any DB access (issue #119, second review round):
        // ReplayDayCounter's day-line shape changed at "harness-2" (SharedChatCounts, #73), and
        // ReplayFidelityCalculator reads that dictionary unconditionally — recomputing a "harness-1"
        // file throws (a bare NullReferenceException today) rather than refusing cleanly. There is no
        // migration path between versions: a version bump means the counting rule itself changed, so
        // an old file's day lines cannot be reinterpreted under the new one, only refused.
        if (!string.Equals(identity.AlgorithmVersion, AlgorithmVersion, StringComparison.Ordinal))
        {
            logger.LogError(
                "Report-only file '{File}' was written by algorithm version '{FileVersion}', but this build only recomputes '{CurrentVersion}'; there is no migration between versions.",
                sourceFile.Path, identity.AlgorithmVersion, AlgorithmVersion);
            return ExitPreconditionViolated;
        }

        // The channel NAME is deliberately not compared — a rename (#34/#44) keeps the id and must
        // not make a run unrecomputable, exactly the reasoning FindFrozenWindow already applies when
        // matching a resume candidate by id alone. The id is what the header actually identifies the
        // run by (HarnessRunIdentity.ChannelId, not ChannelName), so that is the only thing checked.
        var channel = await channelService.GetByNameAsync(channelName, ct);
        if (channel is null || !string.Equals(channel.Id, identity.ChannelId, StringComparison.Ordinal))
        {
            logger.LogError(
                "Channel '{Channel}' does not resolve to the id '{HeaderChannelId}' that report-only file '{File}' was written for; the channel row may have changed since.",
                channelName, identity.ChannelId, sourceFile.Path);
            return ExitPreconditionViolated;
        }

        HarnessReportContent content;
        try
        {
            content = sourceFile.ReadDays();
        }
        catch (HarnessReportFileException ex)
        {
            logger.LogError(ex, "Report-only file '{File}' cannot be read.", sourceFile.Path);
            return ExitPreconditionViolated;
        }

        var allDays = content.Days.OrderBy(d => d.Day).ToList();

        // Refuses a duplicated day line before it can double-count (issue #119, second review
        // round): an ordinary run can never produce one — AppendDay's in-memory dayLines dictionary
        // makes a second write for the same day impossible — but this file did not necessarily come
        // from one. Left unchecked, a HashSet-based completeness check below would report full
        // coverage while allDays still carried the duplicate straight into
        // ReplayFidelityCalculator.Compute, which has no duplicate-day contract of its own and would
        // silently sum both copies into every total. Restricted to the window: a stray day line
        // outside [WindowFrom, WindowTo] is not this check's concern (it is never read by Compute).
        var duplicateDays = allDays
            .Where(d => d.Day >= identity.WindowFrom && d.Day <= identity.WindowTo)
            .GroupBy(d => d.Day)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateDays.Count > 0)
        {
            logger.LogError(
                "Report-only file '{File}' has more than one day line for {Days}; a duplicated day would be double-counted, so it cannot be recomputed.",
                sourceFile.Path, string.Join(", ", duplicateDays.Select(Iso)));
            return ExitPreconditionViolated;
        }

        var daysPresent = new HashSet<DateOnly>(allDays.Select(d => d.Day));
        for (var day = identity.WindowFrom; day <= identity.WindowTo; day = day.AddDays(1))
        {
            if (!daysPresent.Contains(day))
            {
                logger.LogError(
                    "Report-only file '{File}' is missing the day line for {Day}; the window {From}..{To} is not fully covered, so it cannot be recomputed.",
                    sourceFile.Path, Iso(day), Iso(identity.WindowFrom), Iso(identity.WindowTo));
                return ExitPreconditionViolated;
            }
        }

        // Same refusal an ordinary run gives for the same fact (ExecuteAsync, just below the day
        // loop): a window with no log on any day cannot answer the comparison question at all.
        if (allDays.Count > 0 && allDays.TrueForAll(d => d.Status == ReplayDayStatuses.NoLog))
        {
            logger.LogError(
                "None of the {Days} days from {From} to {To} in '{File}' has a log; the comparison is not possible for channel '{Channel}'.",
                allDays.Count, Iso(identity.WindowFrom), Iso(identity.WindowTo), sourceFile.Path, channel.ChannelName);
            return ExitPreconditionViolated;
        }

        var timestamp = recomputedAtUtc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var stem = Path.ChangeExtension(sourceFile.Path, null);
        var recomputeJsonPath = $"{stem}.recompute-{timestamp}.report.json";
        var recomputeMarkdownPath = $"{stem}.recompute-{timestamp}.report.md";
        if (File.Exists(recomputeJsonPath) || File.Exists(recomputeMarkdownPath))
        {
            logger.LogError(
                "A recompute of '{File}' already exists for timestamp '{Timestamp}'; refusing to overwrite '{JsonPath}' or '{MarkdownPath}'.",
                sourceFile.Path, timestamp, recomputeJsonPath, recomputeMarkdownPath);
            return ExitPreconditionViolated;
        }

        // From here on: exactly what ExecuteAsync loads for the input hash, against the header's
        // frozen window — never a freshly derived one, and never today's Harness:SharedChatCutover.
        var lifetimes = await usageStatQueryService.GetEmoteLifetimesAsync(channel.Id, ct);
        var liveRowDtos = await usageStatQueryService.GetRowsAsync(
            [.. lifetimes.Select(e => e.Id)], identity.WindowFrom, identity.WindowTo, ct);
        var currentBotSplitCutover = await usageStatQueryService.GetEarliestBotUsageDateAsync(channel.Id, ct);
        var botAccountIds = botChatterDetector.KnownBotAccountIds;

        var currentInputHash = HarnessInputHash.Compute(lifetimes, liveRowDtos, botAccountIds, identity.WindowFrom);
        var inputHashMatches = string.Equals(currentInputHash, identity.InputHash, StringComparison.Ordinal);
        var botSplitCutoverMatches = currentBotSplitCutover == identity.BotSplitCutover;

        var emotes = lifetimes
            .Select(e => new ReplayEmote(e.Id, e.Name, e.IsArchived, e.FirstSeenAt, e.ArchivedAt, e.LastSyncedAt))
            .ToList();
        var liveRows = liveRowDtos
            .Select(r => new ReplayUsageRow(r.EmoteId, r.Date, r.UseCount, r.BotUseCount, r.SharedChatUseCount))
            .ToList();

        // Both cutovers come from the frozen header, not from currentBotSplitCutover above and not
        // from options.SharedChatCutover: a recompute answers "what would this run's numbers be
        // under today's code and today's data", not "what would a fresh run measure today".
        var window = new ReplayWindow(identity.WindowFrom, identity.WindowTo, identity.BotSplitCutover, identity.SharedChatCutover);
        var windowDays = identity.WindowTo.DayNumber - identity.WindowFrom.DayNumber + 1;
        var totalBytes = allDays.Sum(d => d.Bytes) + content.Events.Sum(e => e.Bytes);
        var rateLimitedDays = content.Events.Count(e => e.HttpStatusCode == 429);
        var resumePoint = allDays.Count == 0 ? (DateOnly?)null : allDays[^1].Day;

        var originalReport = sourceFile.TryReadExistingReport();
        // Fail-closed, in D4's spirit: with no readable original to inherit Run.Diagnostic from, the
        // recompute defaults to true (no gate verdict) rather than false (a binding one) — a missing,
        // unparsable or otherwise unreadable .report.json must never silently upgrade a diagnostic
        // run into a binding verdict just because nothing was left to say it was one.
        var diagnostic = originalReport?.Run.Diagnostic ?? true;
        var diagnosticSource = originalReport is not null ? "inherited" : "defaulted";
        if (originalReport is null)
        {
            logger.LogInformation(
                "'{File}' has no readable '{JsonPath}' to inherit the diagnostic flag from; the recompute defaults it to true (no gate verdict), per the D4 fail-closed rule.",
                sourceFile.Path, sourceFile.ReportJsonPath);
        }

        var report = ReplayFidelityCalculator.Compute(
            window, emotes, liveRows, allDays, windowDays, runComplete: true, totalBytes, rateLimitedDays, resumePoint, diagnostic);

        var warnings = new List<string>();
        if (!inputHashMatches)
        {
            warnings.Add("input-hash-mismatch");
            logger.LogWarning(
                "Recompute of '{File}' was computed against a live snapshot different from the one the original run used (original input hash '{Original}', current '{Current}'); these numbers are not a re-evaluation of the same run.",
                sourceFile.Path, identity.InputHash, currentInputHash);
        }

        if (!botSplitCutoverMatches)
        {
            warnings.Add("bot-split-cutover-drift");
            logger.LogWarning(
                "Recompute of '{File}' sees a different bot-split cutover than the original run (original '{Original}', current '{Current}').",
                sourceFile.Path, Iso(identity.BotSplitCutover), Iso(currentBotSplitCutover));
        }

        var recomputation = new HarnessRecomputation(
            sourceFile.Path,
            recomputedAtUtc,
            identity.InputHash,
            currentInputHash,
            inputHashMatches,
            identity.BotSplitCutover,
            currentBotSplitCutover,
            botSplitCutoverMatches,
            diagnosticSource,
            new ValueList<string>(warnings));
        var reportWithRecomputation = report with { Recomputation = recomputation };

        var markdown = BuildRecomputeMarkdown(
            identity, header, reportWithRecomputation, allDays, liveRows, recomputedAtUtc, stopwatch.Elapsed, totalBytes, rateLimitedDays);

        try
        {
            // The pre-check above already refused an existing pair; this is the TOCTOU close, not the
            // primary defence — WriteReportPairAtomically's own rename is what actually cannot
            // overwrite (issue #119, second review round).
            HarnessReportFile.WriteReportPairAtomically(recomputeJsonPath, recomputeMarkdownPath, reportWithRecomputation, markdown);
        }
        catch (HarnessReportFileException ex)
        {
            logger.LogError(ex, "Recompute of '{File}' could not be written to '{JsonPath}'.", sourceFile.Path, recomputeJsonPath);
            return ExitPreconditionViolated;
        }

        logger.LogInformation(
            "Recompute of channel '{Channel}' from '{File}' written to '{JsonPath}' (input hash match: {HashMatch}, bot-split cutover match: {CutoverMatch}).",
            channel.ChannelName, sourceFile.Path, recomputeJsonPath, inputHashMatches, botSplitCutoverMatches);
        return ExitSuccess;
    }

    /// <summary>
    /// The frozen window this invocation should continue, or <c>null</c> if there is nothing to
    /// continue and a fresh window is to be derived.
    /// <para>
    /// The window is part of the report file's name, so the file cannot be addressed directly — the
    /// heads in the output directory are read instead. Only the window comes from the head; the
    /// identity is rebuilt around it (including the window-dependent <c>UsageStat</c> rows and the
    /// input hash) and still compared byte for byte by <see cref="HarnessReportFile.ReadHeader"/>, so
    /// a changed data snapshot starts a new file exactly as before.
    /// </para>
    /// <para>
    /// A candidate must be the same channel, the same <see cref="AlgorithmVersion"/> and the same
    /// window length — the last is how a different <c>--days</c> is noticed, since the length is the
    /// window and needs no field of its own. Several candidates are the normal state of the output
    /// directory, not an error: every changed snapshot leaves its predecessor unfinished forever, so
    /// refusing to choose would break the ordinary case. The newest window wins — it is the one a
    /// fresh derivation comes closest to and the one whose live comparison data is least stale.
    /// </para>
    /// </summary>
    private (DateOnly From, DateOnly To)? FindFrozenWindow(
        string channelId, string channelName, int days, DateOnly freshTo)
    {
        if (!Directory.Exists(options.OutputDirectory))
        {
            return null;
        }

        (HarnessRunIdentity Identity, DateTime LoadedAtUtc, string Path)? best = null;
        DateOnly? newestTooOld = null;

        foreach (var path in Directory.EnumerateFiles(options.OutputDirectory, "*.jsonl").Order(StringComparer.Ordinal))
        {
            var candidate = new HarnessReportFile(path);
            if (candidate.IsClosed)
            {
                continue;
            }

            // The channel name is not part of the match: a rename (#34/#44) keeps the id and must
            // not orphan the window, even though it does start a new file via the identity.
            var header = candidate.TryReadHeader();
            if (header is null
                || !string.Equals(header.Identity.ChannelId, channelId, StringComparison.Ordinal)
                || !string.Equals(header.Identity.AlgorithmVersion, AlgorithmVersion, StringComparison.Ordinal)
                || header.Identity.WindowTo.DayNumber - header.Identity.WindowFrom.DayNumber + 1 != days)
            {
                continue;
            }

            var age = freshTo.DayNumber - header.Identity.WindowTo.DayNumber;
            if (age < 0)
            {
                // A window ending after the last complete UTC day cannot have been frozen by this
                // machine's clock; whatever produced it, it is not this run.
                continue;
            }

            if (age > MaxResumeAgeInDays)
            {
                newestTooOld = newestTooOld is { } known && known > header.Identity.WindowTo
                    ? known
                    : header.Identity.WindowTo;
                continue;
            }

            if (best is null
                || header.Identity.WindowTo > best.Value.Identity.WindowTo
                || (header.Identity.WindowTo == best.Value.Identity.WindowTo && header.LoadedAtUtc > best.Value.LoadedAtUtc))
            {
                best = (header.Identity, header.LoadedAtUtc, path);
            }
        }

        if (best is null)
        {
            if (newestTooOld is { } abandoned)
            {
                logger.LogWarning(
                    "Der jüngste unabgeschlossene Lauf für Kanal '{Kanal}' endet am {Ende} und ist damit älter als {Grenze} Tage; er wird nicht fortgesetzt, dieser Aufruf beginnt eine neue Messung.",
                    channelName, Iso(abandoned), MaxResumeAgeInDays);
            }

            return null;
        }

        logger.LogInformation(
            "Übernehme das eingefrorene Fenster {Von} bis {Bis} aus dem unabgeschlossenen Lauf '{Datei}'; ob dieser Aufruf ihn tatsächlich fortsetzt, entscheidet erst der anschließende Identitätsabgleich.",
            Iso(best.Value.Identity.WindowFrom), Iso(best.Value.Identity.WindowTo), best.Value.Path);
        return (best.Value.Identity.WindowFrom, best.Value.Identity.WindowTo);
    }

    private void AppendAbort(
        HarnessReportFile file, DateOnly day, string status, int? httpStatusCode, string message, long bytes = 0) =>
        file.AppendEvent(new HarnessEventLine(timeProvider.GetUtcNow().UtcDateTime, day, status, httpStatusCode, message, bytes));

    private void LogResumePoint(Dictionary<DateOnly, ReplayDayLine> dayLines, string reason, DateOnly day)
    {
        var resumePoint = dayLines.Count == 0 ? (DateOnly?)null : dayLines.Keys.Max();
        logger.LogError(
            "Harness-Lauf bei Tag {Tag} beendet ({Grund}). Letzter vollständiger Tag: {Wiederaufnahme}. Ein erneuter Aufruf mit denselben Argumenten setzt dort fort.",
            Iso(day), reason, resumePoint is { } value ? Iso(value) : "keiner");
    }

    private static DateOnly Later(DateOnly left, DateOnly right) => left > right ? left : right;

    // ISO 8601 rather than DateOnly's culture-dependent default ToString(): a container's invariant
    // culture renders that as MM/dd/yyyy, which read as an ordinary (if odd) US date in a German
    // log line until Task 8's live verification (#69) actually compared it against the day the
    // archive itself had answered for.
    private static string Iso(DateOnly value) => value.ToString(IsoDateFormat, CultureInfo.InvariantCulture);

    /// <summary>"none" rather than a dash — the recompute's warning block reads two of these side by
    /// side and a missing value is a fact worth naming, not a blank.</summary>
    private static string Iso(DateOnly? value) => value is { } day ? Iso(day) : "none";

    private static void AppendDay(HarnessReportFile file, Dictionary<DateOnly, ReplayDayLine> dayLines, ReplayDayLine line)
    {
        file.AppendDay(line);
        dayLines[line.Day] = line;
    }

    /// <summary>
    /// The Markdown of a report-only recompute (issue #119): a provenance banner — what was
    /// recomputed, from which file, with zero archive requests, and (prominently, if either
    /// mismatches) how the current live snapshot and bot-split cutover compare to the ones the
    /// original run used — followed by the same body <see cref="BuildMarkdown"/> renders for an
    /// ordinary run, with three rows adjusted so the body does not itself contradict the banner:
    /// the "loaded at" row shows both the original run's snapshot time and this recompute's, the
    /// "Input-Hash" row is labelled as the original's and gets a second row for the current one, and
    /// the "distinct chatters" fallback text names a recompute rather than a resume (the window-wide
    /// chatter set is never persisted, so a recompute cannot have it either, same as a resumed run).
    /// </summary>
    private static string BuildRecomputeMarkdown(
        HarnessRunIdentity identity,
        HarnessReportHeader header,
        ReplayFinalReport report,
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        DateTime recomputedAtUtc,
        TimeSpan elapsed,
        long bytes,
        int rateLimitedDays)
    {
        var recomputation = report.Recomputation
            ?? throw new ArgumentException("Report carries no Recomputation; BuildRecomputeMarkdown is only for a recompute's own report.", nameof(report));

        var text = new StringBuilder();
        text.Append(Invariant($"# Report-only-Neuberechnung: {identity.ChannelName}\n\n"));
        text.Append(Invariant(
            $"> **Report-only-Neuberechnung von `{recomputation.SourceFile}`, erzeugt am {recomputedAtUtc:yyyy-MM-dd HH:mm:ss} UTC.**\n"));
        text.Append(
            "> Es wurden keine Archiv-Requests gestellt (#119) — jede Zahl unten ist aus den "
            + "vorhandenen `.jsonl`-Tageszeilen gegen den *aktuellen* Code und einen *aktuellen* "
            + "Live-Snapshot aus Postgres berechnet. Die `Algorithmus-Version` weiter unten ist ein "
            + "Etikett auf dem Zählpfad, der diese Tageszeilen erzeugt hat — kein Versprechen, dass "
            + "die Berechnung selbst seither unverändert ist (siehe docs/DECISIONS.md).\n\n");

        if (!recomputation.InputHashMatches || !recomputation.BotSplitCutoverMatches)
        {
            text.Append(
                "> **⚠ Achtung: Der Live-Snapshot hat sich seit dem ursprünglichen Lauf verschoben.** "
                + "Diese Zahlen sind KEINE Neubewertung desselben Laufs — sie vergleichen das "
                + "eingefrorene Log gegen einen neueren Datenbankstand.\n>\n");
            text.Append(Invariant(
                $"> - Input-Hash: ursprünglich `{recomputation.OriginalInputHash}`, aktuell `{recomputation.CurrentInputHash}` (Übereinstimmung: {(recomputation.InputHashMatches ? "ja" : "nein")})\n"));
            text.Append(Invariant(
                $"> - Bot-Split-Stichtag: ursprünglich `{Iso(recomputation.OriginalBotSplitCutover)}`, aktuell `{Iso(recomputation.CurrentBotSplitCutover)}` (Übereinstimmung: {(recomputation.BotSplitCutoverMatches ? "ja" : "nein")})\n\n"));
        }

        text.Append(Invariant(
            $"Herkunft des Diagnose-Kennzeichens: {(recomputation.DiagnosticSource == "inherited" ? "übernommen aus dem ursprünglichen Bericht" : "keiner vorhanden, fail-closed auf 'Diagnose' (kein Gate-Urteil) zurückgefallen")}.\n\n"));
        text.Append("---\n\n");

        text.Append(BuildMarkdown(
            identity, report, days, liveRows, header.LoadedAtUtc, recomputedAtUtc, elapsed, bytes, rateLimitedDays,
            distinctChatters: null,
            unavailableDistinctChattersLabel: "nicht verfügbar (Neuberechnung)",
            recomputeLoadedAtUtc: recomputedAtUtc,
            currentInputHash: recomputation.CurrentInputHash));

        return text.ToString();
    }

    /// <summary>
    /// The readable half of the report. The pre-registered figures come first and unaltered — the
    /// thresholds live in issue #69, this table only puts the numbers next to them so a reader
    /// needs no interpretation step (and this class computes no verdict of its own).
    /// </summary>
    private static string BuildMarkdown(
        HarnessRunIdentity identity,
        ReplayFinalReport report,
        IReadOnlyList<ReplayDayLine> days,
        IReadOnlyList<ReplayUsageRow> liveRows,
        DateTime loadedAtUtc,
        DateTime generatedAtUtc,
        TimeSpan elapsed,
        long bytes,
        int rateLimitedDays,
        int? distinctChatters,
        // The four parameters below default to the ordinary-run rendering, so ExecuteAsync's call
        // site (positional, ten arguments) is untouched and an ordinary run's Markdown does not move
        // a single byte. BuildRecomputeMarkdown is the only caller that ever supplies them.
        string unavailableDistinctChattersLabel = "nicht verfügbar (wiederaufgenommen)",
        DateTime? recomputeLoadedAtUtc = null,
        string? currentInputHash = null)
    {
        var gate = report.Gate;
        var diagnostics = report.Diagnostics;
        var text = new StringBuilder();

        text.Append(Invariant($"# Harness-Bericht: {identity.ChannelName}\n\n"));
        text.Append(
            "**Replay-Treue, nicht Zuordnungsgenauigkeit.** Beide Seiten benutzen dieselbe Namensregel; "
            + "Übereinstimmung belegt, dass ein Import die eigene Live-Messung reproduzieren würde — "
            + "nicht, dass ein Name dem richtigen historischen Emote gehört. Die Zuordnungsfrage "
            + "beantworten allein die Diagnostikzahlen weiter unten.\n\n");
        text.Append(
            "> Live-Lücken: unabhängiger Beleg sind Worker-Log und Uptime Kuma, nicht dieser Bericht.\n\n");

        text.Append("## Lauf\n\n| Feld | Wert |\n| --- | --- |\n");
        Row(text, "Kanal", Invariant($"{identity.ChannelName} (`{identity.ChannelId}`, Twitch-ID `{identity.TwitchChannelId}`)"));
        Row(text, "Fenster", Invariant($"{identity.WindowFrom:yyyy-MM-dd} bis {identity.WindowTo:yyyy-MM-dd} ({report.Run.WindowDays} Tage)"));
        Row(text, "Bot-Split-Stichtag", identity.BotSplitCutover?.ToString(IsoDateFormat, CultureInfo.InvariantCulture) ?? "keiner (kein Bot je gesehen)");
        Row(text, "Shared-Chat-Stichtag", identity.SharedChatCutover?.ToString(IsoDateFormat, CultureInfo.InvariantCulture) ?? "keiner (Diagnoselauf)");
        Row(text, "Lauf-Modus", report.Run.Diagnostic ? "Diagnose" : "bindend");
        Row(text, "Human-only-Tage im Fenster", Invariant($"{diagnostics.HumanOnlyDays}"));
        Row(text, "Tage mit Log / ohne Log", Invariant($"{diagnostics.LogDays} / {diagnostics.NoLogDays}"));
        Row(text, "Bot-IDs", identity.BotAccountIds.Count == 0 ? "keine" : string.Join(", ", identity.BotAccountIds.Select(id => "`" + id + "`")));
        Row(text, "Ladezeitpunkt der Vergleichsdaten (UTC)", recomputeLoadedAtUtc is { } recomputeLoaded
            ? Invariant($"ursprünglicher Lauf: {loadedAtUtc:yyyy-MM-dd HH:mm:ss} · diese Neuberechnung: {recomputeLoaded:yyyy-MM-dd HH:mm:ss}")
            : Invariant($"{loadedAtUtc:yyyy-MM-dd HH:mm:ss}"));
        Row(text, "Bericht erzeugt (UTC)", Invariant($"{generatedAtUtc:yyyy-MM-dd HH:mm:ss}"));
        Row(text, "Laufzeit dieses Laufs", Invariant($"{elapsed:hh\\:mm\\:ss}"));
        Row(text, "Übertragene Bytes (alle Läufe dieser Datei)", Invariant($"{bytes}"));
        Row(text, "HTTP 429", Invariant($"{rateLimitedDays}"));
        Row(text, "Distinkte Chatter im Fenster", distinctChatters is { } count
            ? Invariant($"{count}")
            : unavailableDistinctChattersLabel);
        Row(text, "Algorithmus-Version", "`" + identity.AlgorithmVersion + "`");
        Row(text, currentInputHash is null ? "Input-Hash" : "Input-Hash (ursprünglicher Lauf)", "`" + identity.InputHash + "`");
        if (currentInputHash is { } current)
        {
            Row(text, "Input-Hash (aktuell, diese Neuberechnung)", "`" + current + "`");
        }
        Row(text, "Lauf vollständig", report.Run.RunComplete ? "ja" : "nein");
        var ineligibleReason = gate.GateIneligibleReasons.Count == 0
            ? "ohne Grund"
            : string.Join(", ", gate.GateIneligibleReasons);
        Row(text, "Gate-tauglich", gate.GateEligible ? "ja" : "nein — " + ineligibleReason);

        text.Append("\n## Präregistrierte Kennzahlen (#69)\n\n| Kennzahl | Wert | Präregistrierte Schwelle |\n| --- | --- | --- |\n");
        Row(text, "Gesamtabweichung Σ\\|Log − Live\\| / ΣLive", Ratio(gate.TotalDeviation), "≤ 0.10");
        Row(text, "Top-20-Recall", Ratio(gate.Top20Recall), "≥ 0.90");
        Row(text, "Unteres-Quartil-Precision", Ratio(gate.BottomQuartilePrecision), "≥ 0.80");
        Row(text, "Gewertete Tage",
            Invariant($"{gate.RatedDays} (davon {diagnostics.SignallessRatedDays} signallos)"), "≥ 20");
        Row(text, "Fensterlänge", Invariant($"{report.Run.WindowDays}"), "= 30");
        // Below the pre-registered rows and without a threshold of its own: the two sides of the
        // #73 split are an eligibility condition (D3), not a fourth published figure.
        Row(text, "Shared Chat ΣLog / ΣLive (bewertete Tage)",
            Invariant($"{gate.SharedChatLogTotal} / {gate.SharedChatLiveTotal}"), "Eignungsbedingung, kein Gate");
        // Reporting-only (#97): sees the uniform tail loss a rank-based set comparison structurally
        // cannot, but has no calibrated threshold, so it is never read against a cutoff here.
        Row(text, "Schwanzabweichung Σ\\|Log − Live\\| / ΣLive (unteres Quartil, Live-Menge)",
            Ratio(gate.TailDeviation), "keine Gate-Kennzahl, nur Bericht");

        text.Append(Invariant($"\nImport-Population: {gate.PopulationSize} Emotes · ΣLog (human) {gate.HumanLogTotal} · ΣLive (human) {gate.HumanLiveTotal}"));
        text.Append(Invariant($" · Top-20-Größe {gate.Top20Size}"));
        text.Append(Invariant(
            $", davon {gate.Top20LiveTieCount} auf dem Live-Grenzwert und {gate.Top20LogTieCount} auf dem Log-Grenzwert"));
        // Since #97 the quartile's Live-/Log-Menge (the tie-safe cutoff sets) can exceed the nominal
        // Quartilsgröße when a plateau sits on the cutoff — the tie counts right after explain why.
        text.Append(Invariant(
            $" · Quartilsgröße (nominal) {gate.BottomQuartileSize}, Live-Menge {gate.BottomQuartileLiveSize}, Log-Menge {gate.BottomQuartileLogSize}"));
        text.Append(Invariant(
            $", davon {gate.BottomQuartileLiveTieCount} auf dem Live-Grenzwert und {gate.BottomQuartileLogTieCount} auf dem Log-Grenzwert"));
        text.Append(Invariant($" · davon signallose gewertete Tage {diagnostics.SignallessRatedDays}\n"));

        text.Append("\n## Plausibilität (mit Bots, alle Tage mit Log)\n\n| Feld | Wert |\n| --- | --- |\n");
        Row(text, "ΣLog inkl. Bots", Invariant($"{report.Plausibility.LogTotalWithBots}"));
        Row(text, "ΣLive inkl. Bots", Invariant($"{report.Plausibility.LiveTotalWithBots}"));
        Row(text, "Verhältnis", Ratio(report.Plausibility.Ratio));
        Row(text, "Differenz", Invariant($"{report.Plausibility.Difference}"));

        text.Append("\n## Diagnostik (nicht bindend)\n\n| Feld | Wert |\n| --- | --- |\n");
        Row(text, "Stabile Teilmenge: Größe / qualifiziert / entscheidungsfähig", Invariant(
            $"{diagnostics.StableSubsetSize} / {diagnostics.QualifiedEmoteCount} (N={diagnostics.MinLiveUsesN}, M={diagnostics.RequiredQualifiedM}) / {(diagnostics.StableSubsetDecisive ? "ja" : "nein")}"));
        Row(text, "Stabile Teilmenge: Median / p90 / Spearman", Invariant(
            $"{Ratio(diagnostics.StableSubsetMedianDeviation)} / {Ratio(diagnostics.StableSubsetP90Deviation)} / {Ratio(diagnostics.StableSubsetSpearman)}"));
        Row(text, "Alle Emotes: Median / p90 / Spearman", Invariant(
            $"{Ratio(diagnostics.AllEmotesMedianDeviation)} / {Ratio(diagnostics.AllEmotesP90Deviation)} / {Ratio(diagnostics.AllEmotesSpearman)}"));
        Row(text, "Nur im Log / nur live gesehen", Invariant(
            $"{diagnostics.LogOnlyEmoteCount} ({Ratio(diagnostics.LogOnlyShareOfLogTotal)} von ΣLog) / {diagnostics.LiveOnlyEmoteCount} ({Ratio(diagnostics.LiveOnlyShareOfLiveTotal)} von ΣLive)"));
        Row(text, "Gesamtabweichung inkl. markierter Tage", Invariant(
            $"{Ratio(diagnostics.TotalDeviationIncludingFlaggedDays)} über {diagnostics.FlaggedIncludedDays} Tage"));
        Row(text, "Tagesdiff / mit ±1-Tag-Toleranz", Invariant(
            $"{Ratio(diagnostics.DailyDeviation)} / {Ratio(diagnostics.DailyDeviationWithOneDayTolerance)}"));
        Row(text, "Nicht zuordenbar: unbekannt / mehrdeutig / vor FirstSeenAt / nach ArchivedAt", Invariant(
            $"{diagnostics.UnknownNameHits} / {diagnostics.AmbiguousNameHits} / {diagnostics.BeforeFirstSeenHits} / {diagnostics.AfterArchivedHits}"));
        Row(text, "FirstSeenAt unbekannt (gezählt und markiert) / mehrdeutige Namen / archiviert ohne Datum", Invariant(
            $"{diagnostics.FirstSeenUnknownHits} / {diagnostics.AmbiguousNameCount} / {diagnostics.ArchivedWithoutDateCount}"));
        Row(text, "Nachrichten gesamt / Bots / Shared Chat / unbestimmbar / außerhalb des Tages", Invariant(
            $"{diagnostics.TotalMessages} / {diagnostics.BotMessages} / {diagnostics.SharedChatMessages} / {diagnostics.IndeterminateMessages} / {diagnostics.OutsideDayCount}"));
        Row(text, "Nicht-PRIVMSG-Zeilen / unlesbare Zeilen", Invariant(
            $"{diagnostics.NonPrivmsgLines} / {diagnostics.MalformedLines}"));
        Row(text, "Datenschutz: Anteil (Emote, Tag)-Zellen mit k = 1", Invariant(
            $"{Ratio(diagnostics.SingleChatterCellShare)} von {diagnostics.CellCount} Zellen"));
        Row(text, "k-Verteilung (1 … 10+)", string.Join(", ", diagnostics.KHistogram.Skip(1).Select(v => v.ToString(CultureInfo.InvariantCulture))));
        Row(text, "Median des Tagesverhältnisses", Ratio(diagnostics.DayRatioMedian));
        Row(text, "Tage mit vermuteter Live-Lücke", diagnostics.LiveGapDays.Count == 0
            ? "keine"
            : string.Join(", ", diagnostics.LiveGapDays.Select(d => d.Day.ToString(IsoDateFormat, CultureInfo.InvariantCulture))));
        Row(text, "Tage mit fraglicher Abdeckung", diagnostics.CoverageQuestionableDays.Count == 0
            ? "keine"
            : string.Join(", ", diagnostics.CoverageQuestionableDays.Select(d => d.Day.ToString(IsoDateFormat, CultureInfo.InvariantCulture))));

        // Two day cards, not one: the human column stays UseCount alone (the target contract the
        // gate measures), and the shared-chat column is its own SharedChatUseCount sum, so the three
        // signatures of D3 can be read off the table day by day — a rollback day would show the
        // middle one.
        var liveByDay = new Dictionary<DateOnly, long>();
        var sharedChatLiveByDay = new Dictionary<DateOnly, long>();
        foreach (var row in liveRows)
        {
            liveByDay[row.Date] = liveByDay.GetValueOrDefault(row.Date) + row.UseCount;
            sharedChatLiveByDay[row.Date] = sharedChatLiveByDay.GetValueOrDefault(row.Date) + row.SharedChatUseCount;
        }

        text.Append(
            "\n## Tage\n\n| Tag | Status | Bytes | Nachrichten | davon Bots | Log-Treffer (human) | Live (human) "
            + "| Shared Chat (Log) | Shared Chat (Live) |\n"
            + "| --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (var day in days)
        {
            var logHits = day.HumanCounts.Values.Sum();
            text.Append(Invariant(
                $"| {day.Day:yyyy-MM-dd} | {day.Status} | {day.Bytes} | {day.MessageCount} | {day.BotMessageCount} | {logHits} | {liveByDay.GetValueOrDefault(day.Day)} | {day.SharedChatCounts.Values.Sum()} | {sharedChatLiveByDay.GetValueOrDefault(day.Day)} |\n"));
        }

        return text.ToString();
    }

    private static void Row(StringBuilder text, string label, string value) =>
        text.Append(Invariant($"| {label} | {value} |\n"));

    private static void Row(StringBuilder text, string label, string value, string threshold) =>
        text.Append(Invariant($"| {label} | {value} | {threshold} |\n"));

    /// <summary>
    /// Four decimals, invariant, and an explicit dash for "not computable" — a missing denominator
    /// is a statement of its own and must never render as 0.
    /// </summary>
    private static string Ratio(double? value) =>
        value is { } number ? number.ToString("0.0000", CultureInfo.InvariantCulture) : "—";

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
