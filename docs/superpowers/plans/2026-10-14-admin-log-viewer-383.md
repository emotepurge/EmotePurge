# Admin-Log-Ansicht (#383) — Umsetzungsplan

> **Für ausführende Agenten:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (empfohlen)
> oder superpowers:executing-plans. Jeder Task läuft als **eigener Subagent mit frischem Kontext**
> (Regel 21) und bekommt diesen Plan plus die Spec. Die Spec ist freigegeben; ihre Entscheidungen
> (§19: A1–A37, O1–O4, S1–S10, R1–R7) werden **nicht** neu aufgerollt. **Kein fertiger Code in
> diesem Plan** — Verträge, Namen, Signaturen, gepinnte Werte, Testfälle und Reihenfolge ja, Rümpfe
> nein. Pfade relativ zu `/home/dev/projects/EmotePurge/`; `cd` hält nicht zwischen Shell-Aufrufen,
> also absolute Pfade oder `--project`/`--prefix`.
>
> **Kein Prod-Zugriff.** Keine Verbindung zu `vps`/`nas`, auch kein Tunnel (globale Regel
> „Fernzugriff"). Prod-Schritte stehen nur als vorbereitete Befehle für den Betreiber im letzten Task.
>
> **Parallele Sessions teilen den Git-Index:** nur die eigenen Dateien exakt stagen (`git add <pfad>`),
> nie `git add -A`; das untracked `le.cs` am Repo-Root gehört niemandem in diesem Plan.
>
> **`:5151`-Falle:** E2E und Audit-Harness laufen nur ohne Api auf `:5151`. Wer live verifiziert,
> beendet `dotnet run` vor dem E2E-Lauf. E2E-Port 4300 hat `reuseExistingServer` — bei parallelen
> Worktrees den Lauf per `flock` auf eine gemeinsame Lockdatei serialisieren.

**Goal:** Gespeicherte, serverseitig filterbare Logs aus Api und Worker unter `/admin/logs` plus eine
Fehlerzeile auf `/admin/monitoring`, ohne dass Logging Api oder Worker je bremst oder blockiert.

**Architecture:** Ein eigener `ILoggerProvider` (Sink) in Infrastructure reiht Einträge nicht
blockierend in einen begrenzten Puffer; ein Hosted Service (Schreiber) schreibt sie idempotent über
eine eigene, logging-freie `NpgsqlDataSource` in drei neue Tabellen und meldet `logs.appended` als
dünnen Admin-SSE-Hinweis. Ein serverseitiger Parser übersetzt eine kleine Filtersprache in
parametrisiertes SQL; die Angular-Seite gleicht ihr geladenes Intervall periodisch per REST ab.
Vorher räumt T0 das Rauschen an der Quelle auf und schließt drei bestehende #246-Lücken.

**Tech Stack:** .NET 10, Microsoft.Extensions.Logging, Npgsql/EF Core (Postgres 16 Alpine),
StackExchange.Redis, xUnit + Testcontainers + NSubstitute + `FakeTimeProvider`, Angular 22
(Signals, Transloco, CDK), Vitest, Playwright.

**Spec:** [`docs/superpowers/specs/2026-10-14-admin-log-viewer-383-spec.md`](../specs/2026-10-14-admin-log-viewer-383-spec.md)

**Stand:** 2026-10-14, geplant gegen `main` @ `92608b70`. Letzte Migration dort:
`20261013092051_SwitchBackfillSnapshotToSlotKey`.

## Global Constraints

- Schichten (CLAUDE.md „Schichtentreue"): Core nur BCL (`CoreAssemblyReferenceTests`); kein
  `AppDbContext`/`IConnectionMultiplexer` in Handlern (Regel 4); neue Fähigkeiten = Interface in
  `Core/Services/` + Implementierung in Infrastructure; Minimal API in `Endpoints/*.cs` (Regel 6).
- **`AddLogCapture` wird nie in `AddEmotePurgeInfrastructure` und nie in `AddHarness` aufgerufen** (A2, §2.7).
- Untergrenze Information, fest im Code (B4). Debug/Trace werden nie gespeichert.
- Ausgeschlossene Kategorien (fest): `Microsoft.EntityFrameworkCore.Database.Command` < Warning,
  `Npgsql`/`Npgsql.*` < Warning, `EmotePurge.Infrastructure.Logging.*` auf jedem Level.
- Puffer 5.000 Einträge **und** 8 MiB geschätzt (Zeichen × 2); Tagesbudget `LogCapture:DailyEntryBudget`
  Default 20.000 je Prozess und UTC-Tag, Information bis 80 % (16.000).
- Feldgrenzen: Kategorie 256, EventName 128, Message 4.096 (+ `…[truncated]`), Exception 16.384,
  Exception-Message je Glied 1.024, Kette 8 Ebenen, Properties ≤ 16 Schlüssel / Wert ≤ 256 / ≤ 4 KiB,
  Sammlungen ≤ 20 Elemente, State-Indizes 0–16.
- Schreiber: Batch ≤ 500 oder ≤ 1 s, ein Statement, 5 s Command-Timeout, eine idempotente Wiederholung
  nach 2 s, Backoff 1/2/4 … ≤ 30 s, Halbierung ≤ 64 Statements je Ursprungsbatch, Herzschlag ≥ alle 60 s,
  Drop-Eintrag ≤ 1 je 60 s, Shutdown-Drain ≤ 3 s, eigener Pool ≤ 2 Verbindungen,
  `Application Name=emotepurge-logcapture`.
- `logs.appended`: Admin-Typ, ohne Kanal/Payload, ≤ 1 je 2 s und Prozess, fire and forget.
- Retention: `RetentionPolicy.LogEntry` = 7 Tage, stündlicher isolierter Lauf, erster Lauf 1 min nach
  Start, Batches zu 5.000; `overdue` ab 7 Tage + 2 h.
- Abfragen: 5 s Zeitlimit → `503 log_query_timeout`; ≤ 2 parallel je Api-Instanz über `logs`,
  `logs/summary`, `logs/{id}`, Wartezeit ≤ 5 s → `503` + `Retry-After: 5` ohne Body; `logs/status` 30 s
  gecacht mit Single-Flight. **Keine Rate-Limit-Policy** auf der Admin-Gruppe.
- Zeitraum: `range` ∈ `1h|6h|24h|7d` (Default `24h`) **oder** `from`+`to`; Spanne ≤ 8 Tage, `to` ≤ jetzt + 12 h.
  Listen mit exakten Grenzen, nur das Histogramm rastet ein; Bucket-Leiter 10 s, 30 s, 1, 2, 5, 10,
  15, 30 min, 1, 2, 3, 6, 12 h; ≤ 100 Buckets.
- Filtergrenzen: ≤ 512 Zeichen, ≤ 16 Terme, ≤ 16 Werte je Liste, Wert ≤ 200. Gespeicherte Filter:
  Name ≤ 60 (getrimmt, Unikat ohne Groß-/Klein), Filter ≤ 512, höchstens 30.
- Client: Seite 100, Nachladen bis 2.000 Zeilen; Abgleich alle 30 s, ≥ 5 s Abstand, ≤ 2.500 Schlüssel,
  ≤ 3 × 200 Id-Nachladen, > 500 fehlende → „Liste neu laden"; Summary ≤ 1 je 30 s; „Sichtbare kopieren" ≤ 500.
- Sprache (CLAUDE.md „Sprache"): Bezeichner, Kommentare, Log-/`throw`-Messages, Commit-Messages und
  neue DECISIONS-Einträge **englisch**; Spec/Plan deutsch. **Keine Issue-/Ticketnummern in
  Commit-Messages oder PR-Titeln** (Repo-Präferenz). Conventional Commits, mehrere logische Commits.
- Regel 3: Jeder Commit, der Konvention/Vertrag/Topologie ändert, trägt seinen DECISIONS-Absatz
  (Format: `### 2026-10-xx — <Titel> (#383)` + `**Betrifft:**`-Zeile, oben unter `---`).
- Regel 7: jeder neue `ApiErrorCodes`-Eintrag auch in `web/src/app/core/i18n/api-error.ts`
  (`KNOWN_API_ERROR_CODES`) und `web/public/i18n/de.json` + `en.json` (`errors.api.*`).
- Gates je Task (nur berührte Suiten): `dotnet build EmotePurge.slnx` (0 Warnungen), betroffene
  `dotnet test`-Projekte, `dotnet format EmotePurge.slnx --verify-no-changes`; Frontend
  `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run format:check`.

## Review Focus

Fünf Eingaben/Zustände, die die Spec impliziert, aber keine ihrer Testlisten abdeckt — jeweils mit
Test im besitzenden Task:

1. **Shutdown bei unerreichbarem Postgres.** Ein Batch hängt im 5-s-Command-Timeout, während `StopAsync`
   kommt; erwartet: der Stop ist nach ≤ 3 s + Toleranz zurück (Docker-Grace 10 s bleibt gewahrt), der
   Rest wird in der Console gezählt. → Test `Stop_WithPostgresPaused_ReturnsWithinDrainBudget` in **B6**.
2. **Überlappende Instanzen derselben Quelle** (Restart/Deploy: alte Instanz schreibt noch Herzschläge,
   neue startet). Erwartet: `status` und `instance:latest` meinen nur die Instanz mit jüngstem
   `ProcessStartedUtc`; die Übergabe übernimmt keinen Marker einer Instanz ohne `CleanShutdownAtUtc`.
   → Tests `Status_TwoLiveInstancesOfOneSource_ReportsNewest`, `InstanceLatest_IgnoresOlderInstanceStillWriting` in **B10**.
3. **Lang offene Live-Seite mit `7d`:** das eingefrorene `from_L` wandert über die 8-Tage-Spanne. Erwartet:
   die Seite wechselt in den Hinweis „Zu viele neue Einträge — Liste neu laden", statt mit `400` in „pausiert"
   zu kippen oder still zu stehen. → Vitest `reconcile_spanBeyondEightDays_requestsReloadWithoutRequest` in **B13** (s. Lücke L3).
4. **Kaputter Deep-Link** (`?range=2d`, `?from=…` ohne `to`, `q` > 512 Zeichen). Erwartet: sichtbarer Fehler
   (Feldfehler bzw. Banner), Zeitwahl bedienbar, **keine** Anfrageschleife. → E2E
   `malformed deep link shows an error and settles` in **B15**.
5. **Namen gespeicherter Filter mit Leerraum/Großschreibung und Seeds mit Zitaten im Roundtrip.**
   Erwartet: `"  alle fehler "` → `409 log_filter_name_taken`; Umbenennen des eigenen Filters nur in der
   Schreibweise ist erlaubt; ein unverändert gespeicherter Seed „Twitch-Reconnects" behält seinen String
   bytegleich. → Tests in **B10** (`Create_TrimmedCaseVariant_IsNameTaken`, `Update_OwnNameDifferentCase_Succeeds`,
   `Update_SeedUnchanged_KeepsFilterByteIdentical`).

---

## Teil A — T0: Rauschen an der Quelle und #246-Lücken (eigener PR, zuerst)

**Branch:** `fix/twitchlib-log-redaction-noise` von `origin/main`. **Ohne Migration**, unabhängig von
Teil B, sofort deploybar (nur Images). PR-Titel z. B. „Fix TwitchLib log redaction gaps and remove log
noise at the source".

**Warum Teil B danach von `main` abzweigt (Entscheidung):** Teil B prüft in AK 4 die Redaction bis in
`LogEntries` und baut auf `WithheldMessageException` (A2) und der Allowlist (A3) auf. T0 geht vorher
eigenständig auf Prod. Ein gestapelter Branch trüge T0s Diff in B's PR und machte die Codex-Zweitmeinung
„einmal je Branch" unscharf. **Teil B startet daher von `main` nach dem Merge von Teil A.** Verzögert sich
der Merge, dürfen B1–B3 und B8 (berühren T0-Code nicht) gestapelt auf `fix/twitchlib-log-redaction-noise`
beginnen; B4 wartet auf den Merge, danach Rebase auf `main`.

### A1 — Api-EF auf Warning, USERNOTICE-Herabstufung (`sonnet`)

**Files:**
- Modify: `src/EmotePurge.Api/appsettings.json:2-8` (`"Microsoft.EntityFrameworkCore": "Warning"`, A1: ganzer Namensraum)
- Modify: `src/EmotePurge.Worker/TwitchLibRawLineRedaction.cs` (Kommando als eigener Wert)
- Modify: `src/EmotePurge.Worker/RedactingTwitchClientLoggerFactory.cs:70-83` (`RedactingLogger.Log`)
- Test: `tests/EmotePurge.Worker.Tests/RedactingTwitchClientLoggerFactoryTests.cs`, `tests/EmotePurge.Worker.Tests/TwitchLibRawLineRedactionTests.cs`
- Modify: `docs/DECISIONS.md` (Eintrag 1, §17)

**Interfaces:**
- Produces: `public readonly record struct RedactedRawLine(string Text, string Command)` und
  `public static RedactedRawLine TwitchLibRawLineRedaction.RedactLine(string? rawLine)`; `Command` ist
  `"<none>"`, `"<unparsed>"` oder ein Wort aus `KnownIrcCommands`/drei Ziffern. `Redact(raw)` bleibt und
  liefert `RedactLine(raw).Text` **bytegleich** zu heute.
- Produces (Verhalten): `RedactingLogger.Log` reicht `LogUnaccountedFor` + `Command == "USERNOTICE"`
  mit `LogLevel.Debug` an den inneren Logger, Message und EventId unverändert. Alles andere behält sein Level.

- [ ] **Tests schreiben (rot):** `UnaccountedFor_Usernotice_ForwardsAtDebugWithSameRedactedMessage`
  (innerer Recording-Logger sieht Debug, Message identisch zur heutigen Redaction);
  `UnaccountedFor_Notice_StaysWarning`, `…_UnknownCommand_StaysWarning`, `…_Unparsed_StaysWarning`
  (das „SECRET"-Beispiel aus der Klassen-Doku), `…_NoCommand_StaysWarning`;
  `ParsingError_KeepsError`, `LeavingChannel_KeepsInformation`;
  `UnaccountedFor_Usernotice_ReachesNoProviderAtInformationMinimum` (echte `LoggerFactory`, Minimum
  Information, erfassender Provider bleibt leer); `RedactLine_TextEqualsRedact_ForEveryExistingCase`
  (über die bestehenden Testzeilen von `TwitchLibRawLineRedactionTests`).
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Worker.Tests --filter "FullyQualifiedName~RedactingTwitchClientLoggerFactoryTests|FullyQualifiedName~TwitchLibRawLineRedactionTests"` — erwartet: Kompilierfehler (`RedactLine` fehlt).
- [ ] **Umsetzen:** `RedactLine` liefert dieselbe Allow-List-Erkennung wie heute, `Redact` delegiert;
  `TryRedact` gibt das Kommando mit heraus, ohne den Ausgabe-String zu parsen (§4.2, §2.7). Api-Setting setzen.
- [ ] **Lauf:** derselbe Filter grün, dann `dotnet test tests/EmotePurge.Worker.Tests` grün.
- [ ] **DECISIONS-Eintrag 1** (englisch): Messzahlen aus §2.6 (13.156 EF-Zeilen/20 h in der Api; 312
  Worker-Warnungen, davon ~308 USERNOTICE), beide Änderungen, Grundsatz „noise is removed at the source,
  not hidden in the viewer", NOTICE bleibt Warning. `**Betrifft:**` `src/EmotePurge.Api/appsettings.json` · `RedactingTwitchClientLoggerFactory.cs` · `TwitchLibRawLineRedaction.cs`.
- [ ] **Commit:** `git add src/EmotePurge.Api/appsettings.json src/EmotePurge.Worker/TwitchLibRawLineRedaction.cs src/EmotePurge.Worker/RedactingTwitchClientLoggerFactory.cs tests/EmotePurge.Worker.Tests/RedactingTwitchClientLoggerFactoryTests.cs tests/EmotePurge.Worker.Tests/TwitchLibRawLineRedactionTests.cs docs/DECISIONS.md` —
  `fix(worker): log unhandled USERNOTICE lines at debug and quiet EF Core in the api`

### A2 — Exceptions der redigierten Ereignisse ersetzen (O3) (`sonnet`)

**Files:**
- Create: `src/EmotePurge.Core/Logging/WithheldMessageException.cs`
- Modify: `src/EmotePurge.Worker/RedactingTwitchClientLoggerFactory.cs` (Exception-Ersatz bei den drei ersetzten Ereignissen)
- Test: `tests/EmotePurge.Worker.Tests/RedactingTwitchClientLoggerFactoryTests.cs`
- Modify: `docs/DECISIONS.md` (Eintrag 1 fortschreiben: Absatz O3)

**Interfaces:**
- Produces (Core, BCL-only — **im Core, damit der Sink in B4/B5 ihn erkennt**, s. Lücke L9):
  `public sealed class WithheldMessageException : Exception` mit
  `public string OriginalTypeName { get; }`, `public IReadOnlyList<WithheldExceptionLink> Chain { get; }`,
  `public static WithheldMessageException From(Exception original)`;
  `public sealed record WithheldExceptionLink(string TypeName, string? StackTrace)`.
  `Message` = `"<OriginalTypeName>: message withheld (#246)"`; `ToString()` = Typen und Stacks der ganzen
  Kette; nie `Message`, `Data` oder `ToString()` eines Originals; `InnerException` ist `null`.
  Kette: `InnerException` bzw. bei `AggregateException` alle `InnerExceptions`, höchstens 8 Ebenen tief.

- [ ] **Tests (rot):** `ParsingError_WithException_ForwardsWithheldReplacement` — präparierte Exception,
  deren `Message` und Inner-`Message` Chattext und `display-name=EvilName` tragen: beim inneren Logger
  kommt `WithheldMessageException` an; weder `Message` noch `ToString()` enthält einen präparierten
  Text; beide enthalten beide Originaltypen und ≥ 1 Stackframe (Exception vorher werfen/fangen, damit
  Stacks existieren). `UnaccountedFor_WithException_IsReplacedToo`, `LeavingChannel_WithException_IsReplacedToo`.
  `OtherEvent_WithException_PassesOriginalThrough` (Referenzgleichheit). `From_AggregateException_ListsAllInnerTypes`,
  `From_DeepChain_StopsAtEightLevels`.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Worker.Tests --filter "FullyQualifiedName~RedactingTwitchClientLoggerFactoryTests"` — rot.
- [ ] **Umsetzen:** In `RedactingLogger.Log` ersetzt der Zweig, der den State ersetzt, auch `exception`
  durch `WithheldMessageException.From(exception)` (wenn nicht `null`).
- [ ] **Lauf:** grün; `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~CoreAssemblyReferenceTests"` grün.
- [ ] **DECISIONS:** Absatz O3 im Eintrag aus A1 (Ort Wrapper, Ersatzform, Console profitiert mit; Typ liegt in Core, damit der künftige Sink den Originaltyp lesen kann).
- [ ] **Commit:** `git add src/EmotePurge.Core/Logging/WithheldMessageException.cs src/EmotePurge.Worker/RedactingTwitchClientLoggerFactory.cs tests/EmotePurge.Worker.Tests/RedactingTwitchClientLoggerFactoryTests.cs docs/DECISIONS.md` —
  `fix(worker): withhold exception messages of redacted TwitchLib events`

### A3 — Tag-Schlüssel-Allowlist, Splice-Zeile nur mit Schlüsseln, RoomId (S2, R1) (`opus`)

**Files:**
- Create: `src/EmotePurge.Worker/TwitchTagKeyAllowlist.cs`
- Modify: `src/EmotePurge.Worker/TwitchLibRawLineRedaction.cs:132-150` (`ExtractKeys`)
- Modify: `src/EmotePurge.Worker/IrcLineSpliceRule.cs` (`TagBlockForLog`, `RedactValues`, `AppendTagWithEmbeddedRedaction`; `IdentifyingKeys` darf entfallen)
- Modify: `src/EmotePurge.Worker/TwitchChatManager.cs:1075-1088` (Warning über die neue Hilfsfunktion)
- Test: Create `tests/EmotePurge.Worker.Tests/TwitchTagKeyAllowlistTests.cs`; Modify `TwitchLibRawLineRedactionTests.cs`, `IrcLineSpliceRuleTests.cs`, `IrcLineSpliceRuleTwitchLibTests.cs`
- Modify: `docs/DECISIONS.md` (Eintrag 6, §17)

**Interfaces:**
- Produces: `internal static class TwitchTagKeyAllowlist` mit `public static bool IsAllowed(ReadOnlySpan<char> key)`
  — Grammatik (optional `+`, optional `vendor/`, dann `[A-Za-z0-9-]`, ≤ 64) **und** feste Namen bzw.
  Präfixe aus §4.2b (A30) mit Rest `[a-z0-9-]{1,48}`; **eine** Konstante für beide Stellen.
- Produces: `Redact`-Ausgabe `command=<…>, tagKeys=[a,b,…]`, nur bei n > 0 zusätzlich `, withheldTagKeys=<n>`;
  Fragment ohne `=` gilt als zurückgehalten. Ohne zurückgehaltene Schlüssel bytegleich zu heute.
- Produces: `TagBlockForLog` gibt **nur Schlüssel** aus; jeder Wert (oben und eingebettet nach `@`) wird
  `<entfernt>`, außer `tmi-sent-ts`/`room-id` (nur Ziffern, ≤ 20) und `subscriber`, `mod`, `turbo`, `vip`,
  `first-msg`, `returning-chatter`, `emote-only` (genau `0`/`1`); zurückgehaltenes Fragment → `<withheld>`.
- Produces: `public sealed record SpliceWarningFields(string Channel, string RoomId, string TagBlock)` und
  `public static SpliceWarningFields IrcLineSpliceRule.ForSpliceWarning(string channel, string? roomId, string? rawIrc)`
  — `RoomId` nur bei ≤ 20 Ziffern, sonst `<entfernt>`; TwitchLib-frei, damit testbar.

- [ ] **Twitch-Referenz abgleichen** (Regel „Fremd-APIs live prüfen"): Allowlist gegen
  `https://dev.twitch.tv/docs/chat/irc/` (Tags-Referenz) prüfen; Abweichungen von A30 im DECISIONS-Absatz nennen.
- [ ] **Tests (rot)**, je für `Redact` **und** `TagBlockForLog` (§4.3): `@reply-parent-msg-body=hello;SECRET PRIVMSG #room :more`
  → `tagKeys=[reply-parent-msg-body], withheldTagKeys=1`, kein „SECRET"; `@badges=;SomeDisplayName;color=#fff …`
  hält das nackte Fragment zurück; `@chatwort=x …` zurückgehalten; Grammatikverstoß zurückgehalten;
  `AllAllowlistKeysAndPrefixes_PassThrough` (Theory über jede Konstante und jeden Präfix);
  `RecordedTwitchLines_HaveNoWithheldKeys` (PRIVMSG, USERNOTICE, NOTICE, CLEARCHAT, ROOMSTATE,
  USERSTATE — Zeilen aus vorhandenen Fixtures bzw. Twitch-Doku, mit synthetischen Namen, Herkunft im
  Kommentar). R1 für `TagBlockForLog` mit gespleißter Zeile, oben und eingebettet: `system-msg=SomeUser\ssubscribed…`,
  `msg-param-recipient-display-name=SomeDisplayName`, `msg-param-sender-login=someone`, `msg-param-login=someone`,
  `display-name=X`, `reply-parent-display-name=Y`, `color=#ABCDEF`, `id=<uuid>` → kein Wert, jeder Schlüssel;
  `tmi-sent-ts`, `room-id`, Flags behalten den Wert; `room-id=abc` wird ersetzt.
  3.1 in `IrcLineSpliceRuleTwitchLibTests`: Zeile durch TwitchLibs Parser zu `ChatMessage`, Felder durch
  `ForSpliceWarning` in einen erfassenden Logger mit dem Original-Template; gerenderte Message und **jede**
  Property (`channel`, `roomid`, `tagblock`) enthalten weder „EvilName" noch „444444444".
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Worker.Tests --filter "FullyQualifiedName~TwitchTagKeyAllowlistTests|FullyQualifiedName~TwitchLibRawLineRedactionTests|FullyQualifiedName~IrcLineSpliceRule"` — rot.
- [ ] **Umsetzen:** Allowlist + Zählung in beiden Stellen; `IsSpliced` bleibt unverändert (Sentinel-Semantik);
  `TwitchChatManager` loggt die Warning mit unverändertem Template, aber mit `ForSpliceWarning`-Feldern; keine weiteren `ChatMessage`-Felder.
- [ ] **Lauf:** grün, dann `dotnet test tests/EmotePurge.Worker.Tests` grün.
- [ ] **DECISIONS-Eintrag 6** (englisch): Allowlist in beiden Redaction-Stellen (S2), Splice-Zeile nur
  Schlüssel plus formatgeprüfte Werte (R1), `RoomId`-Regel, `withheldTagKeys`/`<withheld>`.
- [ ] **Commit:** `git add src/EmotePurge.Worker/TwitchTagKeyAllowlist.cs src/EmotePurge.Worker/TwitchLibRawLineRedaction.cs src/EmotePurge.Worker/IrcLineSpliceRule.cs src/EmotePurge.Worker/TwitchChatManager.cs tests/EmotePurge.Worker.Tests/TwitchTagKeyAllowlistTests.cs tests/EmotePurge.Worker.Tests/TwitchLibRawLineRedactionTests.cs tests/EmotePurge.Worker.Tests/IrcLineSpliceRuleTests.cs tests/EmotePurge.Worker.Tests/IrcLineSpliceRuleTwitchLibTests.cs docs/DECISIONS.md` —
  `fix(worker): log only allowlisted tag keys and no splice tag values`

### A4 — Gates, Live-Verifikation, PR Teil A (`sonnet`)

- [ ] `dotnet build EmotePurge.slnx` (0 Warnungen), `dotnet test EmotePurge.slnx` (Docker), `dotnet format EmotePurge.slnx --verify-no-changes`, `node scripts/coverage-local.mjs --backend-only` (≥ 80 % auf neuem Code, Näherung).
- [ ] **Live (Regel 16, lokal):** `docker compose up -d postgres redis`; Worker mit `dotnet run --project src/EmotePurge.Worker`
  an einem Kanal mit Sub-/Raid-/Watch-Streak-Aufkommen (z. B. `handofblood` per Join über die lokale Api):
  ≥ 30 min keine `Unaccounted for … command=USERNOTICE` in der Console (AK 1). Api mit `dotnet run --project src/EmotePurge.Api`,
  Seitenaufruf: keine `Executed DbCommand`-Zeilen. Eine gespleißte Zeile tritt live selten auf — dann
  genügt der TwitchLib-Parser-Test (AK 27 bleibt für Teil B offen).
- [ ] Push `fix/twitchlib-log-redaction-noise`, PR öffnen (Body englisch, Attributionszeile). **Orchestrator:**
  Codex-Zweitmeinung `/codex:review --model gpt-6.1-sol` über den Branch (Regel 22), Ergebnis dem Nutzer vorlegen; Merge macht der Nutzer.
- [ ] **Übergabe an den Betreiber (Deploy nur Images, keine Migration):** nach dem Deploy
  `sudo docker logs --since 20m <WORKER_CONTAINER> 2>&1 | grep -c 'command=USERNOTICE'` → `0` und
  `sudo docker logs --since 20m <API_CONTAINER> 2>&1 | grep -c 'Executed DbCommand'` → `0`. Rollback: Revert.

---

## Teil B — T1–T5: Erfassung, Abfragen, Seite (Branch `feat/admin-log-viewer`)

`git switch -c feat/admin-log-viewer origin/main` nach dem Merge von Teil A. Baseline vor B1:
`docker info`, `ss -ltn | grep -E ':5151|:4200|:4300'`, `dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`
(nichts Pending), Testzahlen melden. Jeder Task committet selbst und pusht nicht; B20 pusht und öffnet den PR.

DECISIONS-Verteilung (Spec §17): Eintrag **2** (In-App-Erfassung) legt B1 an, B3/B5/B6/B7 schreiben fort;
Eintrag **7** (Log-Ablauf) B2; Eintrag **3** (`logs.appended`, Idempotenz, Normalisierung, Zulassung) legt B4 an,
B5/B6/B7 schreiben fort; Eintrag **4** (Filtersprache, Endpunktverträge) legt B8 an, B9/B10/B11 schreiben fort;
Eintrag **5** (Histogramm-Primitiv) B14.

### B1 — Schema, Entitäten, Migration `AddLogCapture` mit Seeds (T1) (`sonnet`)

**Files:**
- Create: `src/EmotePurge.Core/Entities/LogSource.cs`, `LogEntryLevel.cs`, `LogEntry.cs`, `LogCaptureStatus.cs`, `LogFilter.cs`
- Modify: `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs` (DbSets `LogEntries`, `LogCaptureStatuses`, `LogFilters`; Mapping)
- Create: Migration `AddLogCapture` per `dotnet ef migrations add AddLogCapture --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`
- Test: Create `tests/EmotePurge.Infrastructure.Tests/Integration/AddLogCaptureMigrationTests.cs` (Muster `AddChatLogBackfillMigrationTests.cs`)
- Modify: `docs/DECISIONS.md` (Eintrag 2 anlegen)

**Interfaces (Produces):**
- `public enum LogSource : short { Api = 1, Worker = 2 }` (bewusst nicht `SystemLoadSource`).
- `public enum LogEntryLevel : short { Information = 2, Warning = 3, Error = 4, Critical = 5 }` (MEL-Zahlwerte; Core kennt MEL nicht).
- `LogEntry`: `long Id`, `Guid CaptureId`, `DateTime TimestampUtc`, `LogSource Source`, `string InstanceId`,
  `DateTime ProcessStartedUtc`, `LogEntryLevel Level`, `string Category`, `int? EventId`, `string? EventName`,
  `string Message`, `string? ExceptionType`, `string? Exception`, `string? Properties` (jsonb), `bool Truncated`. Typen/Längen exakt §6.1.
- `LogCaptureStatus`: PK `(Source, InstanceId)`, `DateTime ProcessStartedUtc`, `DateTime LastFlushUtc`,
  `bool CaptureEnabled`, `DateTime LastIncompleteAtUtc` (nicht null, beim Start gesetzt — L15),
  `DateTime? CleanShutdownAtUtc`. Tabelle `LogCaptureStatus`.
- `LogFilter`: `int Id`, `string Name` (`varchar(60)`), `string Filter` (`varchar(512)`), `int SortOrder`, `DateTime CreatedAtUtc`, `DateTime UpdatedAtUtc`.
- Indizes (A11): `(TimestampUtc DESC, Id DESC)`; unique `(CaptureId)`; partiell `(TimestampUtc) WHERE "Level" >= 4`;
  `(Source, ProcessStartedUtc DESC)`. Für `LogFilters` ein funktionaler Unique-Index per `migrationBuilder.Sql`:
  `CREATE UNIQUE INDEX "IX_LogFilters_Name_Lower" ON "LogFilters" (lower("Name"));` (Plan-Entscheidung: Unikat ohne Groß-/Klein in der DB, nicht nur im Service).
- Seeds **nur** per `migrationBuilder.InsertData` in der Migration, **nicht** per `HasData` im Modell (A13),
  `SortOrder` 1–4, Zeitstempel fest `2026-10-14T00:00:00Z`. Exakte Strings (§7.5):

```
1  Start nach Deploy     instance:latest uptime<15m
2  Twitch-Reconnects     source:worker cat:TwitchConnectionWatchdog,TwitchChatManager,TwitchLib.* -"angestoßen (Quelle" -" gejoint."
3  7TV-Sync/EventAPI     source:worker cat:SevenTv*
4  Alle Fehler           level>=error
```

- [ ] **Tests (rot):** `Migration_CreatesThreeTablesWithPlannedIndexes` (Indexnamen/-definitionen aus `pg_indexes`, partieller Filter `"Level" >= 4`);
  `Migration_SeedsFourFiltersExactly` (Name, Filter bytegleich, SortOrder); `SeedDeleted_StaysDeleted_AfterReapplyingContext`
  (Zeile löschen, neuen `AppDbContext` erzeugen, `Database.MigrateAsync()` erneut → 3 Zeilen); `CaptureId_IsUnique`;
  `FilterName_UniqueIgnoringCase` (`"Alle Fehler"` neben `"alle fehler"` → 23505).
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~AddLogCaptureMigrationTests"` — rot.
- [ ] **Umsetzen:** Entitäten, Mapping (`Properties` → `jsonb`, Enums als `smallint`), Migration erzeugen und um Seed + funktionalen Index ergänzen; `Down` entfernt alles.
- [ ] **Lauf:** grün; `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~CoreAssemblyReferenceTests|FullyQualifiedName~PendingMigrationGuardTests"` grün.
- [ ] **DECISIONS-Eintrag 2 anlegen** („In-app log capture in Postgres"): drei Tabellen, 7 Tage, globale gespeicherte Filter mit einmaligem Migrations-Seed (A12/A13), Indizes ohne GIN/Trigramm (A11). Weitere Absätze folgen in B3/B5/B6/B7.
- [ ] **Commit:** Entitäten, `AppDbContext.cs`, Migration + Designer + `AppDbContextModelSnapshot.cs`, Test, DECISIONS — `feat(infrastructure): add log capture tables with seeded saved filters`

### B2 — Isolierter stündlicher Log-Ablauf (T1, S5) (`sonnet`)

**Files:**
- Modify: `src/EmotePurge.Core/Services/RetentionPolicy.cs` (`public static readonly TimeSpan LogEntry = TimeSpan.FromDays(7);`)
- Modify: `src/EmotePurge.Core/Services/IDataRetentionService.cs` (neue Operation + Record)
- Modify: `src/EmotePurge.Infrastructure/Services/DataRetentionService.cs` (Implementierung, **nicht** in `RunAsync`)
- Modify: `src/EmotePurge.Worker/DataRetentionWorker.cs` (zweite Schleife vor dem Boot-Gate; `TimeProvider` injizieren)
- Test: Modify `tests/EmotePurge.Infrastructure.Tests/Integration/DataRetentionServiceTests.cs`; Create `tests/EmotePurge.Worker.Tests/DataRetentionWorkerLogExpiryTests.cs`
- Modify: `docs/Operations.md` („Data retention": Tabellenzeile Log entries 7 Tage, `LogCaptureStatus` nach `LastFlushUtc`, `LogFilters` nie; die neuen Log-Ablauf-Zeilen), `docs/DECISIONS.md` (Eintrag 7)

**Interfaces:**
- Consumes: `LogEntry`, `LogCaptureStatus` (B1).
- Produces: `Task<LogRetentionSummary> IDataRetentionService.ExpireLogsAsync(bool enforce, CancellationToken cancellationToken = default)`;
  `public sealed record LogRetentionSummary(bool Enforced, DateTime ReferenceTimeUtc, int LogEntriesDeleted, int LogCaptureStatusRowsDeleted)`
  (im Trockenlauf „würde löschen"). Grenze exklusiv: gelöscht wird `TimestampUtc < now − 7 d` bzw. `LastFlushUtc < now − 7 d`; Batches zu 5.000.
- Produces: `DataRetentionWorker(ILogger<…>, IServiceScopeFactory, RetentionOptions, BootRecoveryGate, TimeProvider)`;
  Log-Schleife startet in `ExecuteAsync` **vor** `bootRecoveryGate.Completed`: erster Lauf 1 min nach Start, dann
  stündlich (`Task.Delay`/`PeriodicTimer` mit `TimeProvider`). Jeder Durchlauf eigenes `try/catch` (Warning mit
  `DescribeFailure`, nie Message). Information nur bei > 0 Gelöschten (enforced); Trockenlauf: höchstens eine
  Warning je UTC-Tag mit den Zählwerten. `RetentionRunSummary` und Formatter unverändert.

- [ ] **Tests (rot):** Infrastructure: `ExpireLogs_JustUnderSevenDays_Stays_JustOver_Goes` (±1 s), `ExpireLogs_DryRun_CountsOnly`,
  `ExpireLogs_StatusFollowsLastFlush_FiltersStay`, `ExpireLogs_DeletesInBatchesUntilDone` (12.001 Zeilen → alle weg),
  `ExpireLogs_RunsEvenWhenDailyPassThrowsInTokenCategory` (Token-Kategorie per Hook werfen lassen, danach `ExpireLogsAsync` erfolgreich).
  Worker (`FakeTimeProvider`, substituierter `IDataRetentionService` im Scope): `LogLoop_RunsWhileBootGateNeverCompletes`
  (Advance 1 min → 1 Aufruf, +1 h → 2), `LogLoop_FailureDoesNotStopNextPassOrDailyPass`, `DailyPass_FailureDoesNotStopLogLoop`,
  `LogLoop_DryRun_WarnsAtMostOncePerUtcDay`.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Worker.Tests --filter "FullyQualifiedName~DataRetentionWorkerLogExpiryTests"` und `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~DataRetentionServiceTests"` — rot.
- [ ] **Umsetzen** wie in Interfaces; DI unverändert (`TimeProvider` ist per `TryAddSingleton` registriert).
- [ ] **Lauf:** beide grün, bestehende `DataRetentionServiceTests` unverändert grün.
- [ ] **DECISIONS-Eintrag 7** („Log expiry runs hourly, isolated, outside RunAsync and the boot gate"), Datenschutztext-Bezug (A32).
- [ ] **Commit:** genannte Dateien — `feat(worker): expire log capture rows hourly outside the daily retention pass`

### B3 — Backup ohne Logdaten (A14, S10) (`sonnet`)

**Files:**
- Modify: `scripts/backup-postgres.sh:86` (`pg_dump … --exclude-table-data='public."LogEntries"' --exclude-table-data='public."LogCaptureStatus"'`)
- Modify: `docs/Operations.md` („Database backup and restore" + „Restore drill": ausgeschlossene Logdaten, Logs nach Restore leer, **Betreiberschritt** aus §16 Stufe 3 als Voraussetzung für `LOG_CAPTURE_ENABLED=true`, jede spätere Skriptänderung braucht denselben Schritt)
- Modify: `docs/DECISIONS.md` (Eintrag 2: Absatz Backup)

- [ ] **Prüfung (rot vor der Änderung):** lokal `docker compose up -d postgres`, Migration angewandt (`dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`),
  Skript in der Aufrufform aus `docs/Operations.md` gegen den lokalen Container in ein Scratch-Verzeichnis;
  `zcat <DUMP> | grep -c '^COPY public."LogEntries"'` → heute `1`.
- [ ] **Umsetzen**, dann erneut: beide `COPY`-Zählungen `0`, `zcat <DUMP> | grep -c 'CREATE TABLE public."LogEntries"'` → `1` (AK 9). `bash -n scripts/backup-postgres.sh`.
- [ ] **Commit:** `git add scripts/backup-postgres.sh docs/Operations.md docs/DECISIONS.md` — `feat(scripts): exclude log capture data from database backups`

### B4 — Eintragsdarstellung: Properties, Exceptions, Normalisierung, Scrubber (T2) (`sonnet`)

**Files:**
- Create: `src/EmotePurge.Infrastructure/Logging/LogEntryRendering.cs`, `src/EmotePurge.Infrastructure/Logging/SecretScrubber.cs`
- Test: Create `tests/EmotePurge.Infrastructure.Tests/Unit/LogEntryRenderingTests.cs`, `tests/EmotePurge.Infrastructure.Tests/Unit/SecretScrubberTests.cs`
- Modify: `docs/DECISIONS.md` (Eintrag 3 anlegen)

**Interfaces (Produces, alle `internal static`, Namespace `EmotePurge.Infrastructure.Logging`):**
- `string LogEntryRendering.NormalizeForPostgres(string value)` — `U+0000` → `U+2400`, unpaariges Surrogat → `U+FFFD`.
- `(string Text, bool Truncated) LogEntryRendering.TruncateMessage(string message)` — > 4.096 → gekürzt + `…[truncated]`.
- `RenderedException LogEntryRendering.RenderException(Exception exception, bool withholdMessages)` →
  `internal sealed record RenderedException(string Type, string Text, bool Truncated)`. Je Glied Typ, Message (≤ 1.024) und
  `StackTrace`, ≤ 8 Ebenen (`AggregateException`: alle Inner), Abbruch bei 16.384 Zeichen → `Truncated`. **Nie `ToString()`.**
  `WithheldMessageException` (A2): `Type` = `OriginalTypeName`, Text aus `Chain`, keine Message. `withholdMessages = true` (zweite Linie): nur Typen und Stacks.
- `RenderedProperties LogEntryRendering.RenderProperties<TState>(TState state, string? requestPath, string? traceId)` →
  `internal sealed record RenderedProperties(string? Json, bool Truncated)`. Nur `IReadOnlyList<KeyValuePair<string, object?>>`
  (auch `object` statt `object?`), Indizes 0–16 per Indexer unabhängig von `Count`, ohne `{OriginalFormat}`, Schlüssel invariant klein,
  Typ-Allowlist aus §5.2 (Sol 3), endliche Sammlungen (Array, `ICollection`, `IReadOnlyCollection`) ≤ 20 Elemente `", "`-verbunden,
  sonst `"<VollerTypname>"` ohne `ToString()`/Enumeration; `DateTime`/`DateTimeOffset` ISO-8601 UTC; Grenzen 256/16/4 KiB.
  `requestpath`/`traceid` aus Scopes (A4) zählen zu den 16. Doppelter Schlüssel nach Kleinschreibung: erster gewinnt (Plan-Entscheidung).
- `string SecretScrubber.Scrub(string text)` — genau die vier Muster aus A5 → `[redacted]`.

- [ ] **Tests (rot):** Normalisierung (NUL, einzelnes High-/Low-Surrogat, gültiges Emoji bleibt); Kürzung an 4.096 mit Marke;
  Exceptions: 50-Ebenen-Kette und 1-MiB-Message < 16.384 Zeichen mit `Truncated`; werfendes/rekursives `ToString()` einer
  Exception-Unterklasse wird nie aufgerufen; `WithheldMessageException` → Originaltyp, kein Message-Text; zweite Linie
  (`withholdMessages`) ohne Message; Properties: Kleinschreibung, ISO-Zeit, Enum/Guid/TimeSpan invariant, Array mit 25 → 20,
  lazy `yield`-Endlosaufzählung → Typname und Rückkehr < 1 ms, State mit `Count` = 1.000.000 liest nur Index 0–16
  (zählender Fake), Objekt mit werfendem `ToString()` → Typname, 17. Schlüssel fällt weg + `Truncated`, 4-KiB-Grenze;
  Scrubber: `Password=abc;Host=x` → `Password=[redacted];Host=x`, `oauth:abc123`, `Bearer eyJ…`, `access_token=…`/`refresh_token=…`/`client_secret=…`;
  Nicht-Treffer: „password reset failed", „Bearer of bad news" ohne Token-Zeichen bleiben, Kanal-/Kategorie-Texte unverändert.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~Unit.LogEntryRenderingTests|FullyQualifiedName~Unit.SecretScrubberTests"` — rot.
- [ ] **Umsetzen** gemäß Signaturen. **Lauf:** grün.
- [ ] **DECISIONS-Eintrag 3 anlegen** (englisch): Typ-Allowlist der Properties, begrenzte Exception-Darstellung ohne `ToString()`, Postgres-Normalisierung, Scrubber (A5).
- [ ] **Commit:** genannte Dateien — `feat(infrastructure): add bounded log entry rendering with postgres-safe strings`

### B5 — Sink: Provider, Zulassung, Puffer, Budget, Drops, Sperren (T2) (`opus`)

**Files:**
- Create in `src/EmotePurge.Infrastructure/Logging/`: `LogCaptureOptions.cs`, `LogCaptureIdentity.cs`, `LogCaptureSuppression.cs`,
  `CapturedLogEntry.cs`, `LogCaptureBuffer.cs`, `LogCaptureBudget.cs`, `LogCaptureDropCounters.cs`, `LogCaptureCoverage.cs`,
  `LogCaptureProvider.cs` (inkl. privatem Logger)
- Test: Create `tests/EmotePurge.Infrastructure.Tests/Unit/LogCaptureProviderTests.cs`, `LogCaptureBudgetTests.cs`, `LogCaptureBufferTests.cs`
- Modify: `docs/DECISIONS.md` (Einträge 2 und 3 fortschreiben)

**Interfaces:**
- Consumes: `LogEntryRendering`, `SecretScrubber` (B4); `WithheldMessageException` (A2); `LogSource` (B1).
- Produces:
  - `public sealed class LogCaptureOptions { bool Enabled = true; int DailyEntryBudget = 20000; }` (Sektion `LogCapture`).
  - `public sealed class LogCaptureIdentity(LogSource source)`: `LogSource Source`, `string InstanceId` (8 Hex, Format `WorkerIdentity.InstanceId`), `DateTime ProcessStartedUtc` (`Process.GetCurrentProcess().StartTime`, UTC). `WorkerIdentity` bleibt (A3).
  - `public static class LogCaptureSuppression { static IDisposable Enter(); static bool IsActive { get; } }` — `AsyncLocal`, verschachtelbar.
  - `internal sealed record CapturedLogEntry(Guid CaptureId, DateTime TimestampUtc, LogEntryLevel Level, string Category, int? EventId, string? EventName, string Message, string? ExceptionType, string? Exception, string? PropertiesJson, bool Truncated, bool IsSynthetic)`.
  - `internal enum LogDropReason { QueueFull, Budget, CaptureFailed, Reentrant, Rejected, WriteUnconfirmed }`; Property-/Wire-Namen `queuefull`/`queue_full` usw. laut §5.3.
  - `internal sealed class LogCaptureBuffer` — `bool TryReserveSlot()` (≤ 5.000), `bool TryCommit(CapturedLogEntry entry, long estimatedBytes)` (≤ 8 MiB; sonst Slot frei, `false`), `void ReleaseSlot()`, `ChannelReader<CapturedLogEntry> Reader`, `void Complete()`; Bytes werden beim Lesen frei.
  - `internal sealed class LogCaptureBudget(int dailyEntryBudget, TimeProvider time)` — `bool TryAdmit(LogEntryLevel level)`; Information nur solange verbraucht < 80 %, sonst bis 100 %; Reset an UTC-Mitternacht. Plan-Entscheidung: ein nach der Byte-Prüfung verworfener Eintrag erstattet das Budget nicht.
  - `internal sealed class LogCaptureDropCounters` — `void Record(LogDropReason reason, DateTime nowUtc)`, `long PendingTotal`, `LogDropSnapshot? CutPending()` → `internal sealed record LogDropSnapshot(long QueueFull, long Budget, long CaptureFailed, long Reentrant, long Rejected, long WriteUnconfirmed, DateTime FirstDropUtc, DateTime LastDropUtc)`; atomar.
  - `internal sealed class LogCaptureCoverage` — `DateTime LastIncompleteAtUtc`, `void Initialize(DateTime value)`, `void MarkIncomplete(DateTime nowUtc)` (nur steigend).
  - `[ProviderAlias("LogCapture")] public sealed class LogCaptureProvider : ILoggerProvider, ISupportExternalScope` mit `internal` Konstruktor `(LogCaptureBuffer, LogCaptureBudget, LogCaptureDropCounters, LogCaptureCoverage, TimeProvider)` und `internal void Close()` (danach still verwerfen).
- Verhalten von `Log()` (feste Reihenfolge, §5.3): (1) Level ≥ je Kategorie vorab berechnetes Minimum (`IsEnabled`); (2) `LogCaptureSuppression.IsActive` → still, ungezählt; (3) `[ThreadStatic]`-Wiedereintritt → `Reentrant`; (4) Budget → `Budget`; (5) Slot → `QueueFull`. Erst dann `CaptureId` (`Guid.CreateVersion7`), Formatter, Rendering, Scrubber (Message, Exception, Property-Werte), Normalisierung, Byte-Schätzung, `TryCommit`. Jede Ausnahme → `CaptureFailed`, Reservierungen frei, nie werfen. Drop eines Eintrags ≥ Error (jeder Grund) → `coverage.MarkIncomplete(now)`. Zweite Linie: Kategorie `TwitchLib.Client.TwitchClient` mit EventName `LogParsingError`/`LogUnaccountedFor`/`LogLeavingChannel` und Exception ≠ `WithheldMessageException` → `withholdMessages: true`. Scopes: nur `RequestPath`, `TraceId`.

- [ ] **Tests (rot)** — `LoggerFactory` mit **nur** unserem Provider, Minimum Trace: `Floor_DebugAndTrace_NeverCaptured`;
  `EfCommand_BelowWarning_NotCaptured_WarningCaptured`; `Npgsql_BelowWarning_NotCaptured`; `OwnCategory_NeverCaptured_AnyLevel`;
  `ProviderAlias_CanRaise_CannotLower` (Konfiguration `Logging:LogCapture:LogLevel:Foo=Error` hebt; `…:Default=Debug` senkt nicht);
  `Scopes_OnlyRequestPathAndTraceId`; `QueueFull_ReturnsImmediately_CountsQueueFull`; `ByteLimit_HitsBeforeCountLimit`
  (5 × große Exceptions füllen 8 MiB vor 5.000); `Budget_InfoStopsAt80Percent_WarningsUntil100_ResetAtUtcMidnight` (`FakeTimeProvider`;
  25.000 Info → 16.000 zugelassen, danach Warnungen bis 20.000, AK 7); `FullBufferOrBudget_FormatterNeverCalled` (zählender Formatter);
  `FormatterThatLogs_CountsReentrant_OuterEntryCaptured`; `FormatterThrows_CountsCaptureFailed_DoesNotThrow`;
  `Suppressed_DroppedSilently_NotCounted_OutsideCaptured`; `ErrorDrop_MarksCoverageIncomplete`, `InfoDrop_DoesNotMark`;
  `BypassedWrapper_TwitchClientParsingError_StoresTypesAndStacksOnly`; `AfterClose_DropsSilently`; `CaptureIds_AreUniqueAndVersion7`.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~Unit.LogCapture"` — rot.
- [ ] **Umsetzen.** Provider/Puffer dürfen keinen `ILogger` injizieren (DI-Zyklus über die `LoggerFactory`).
- [ ] **Lauf:** grün.
- [ ] **DECISIONS:** Eintrag 2 + Untergrenze, Ausschlussregeln, Puffer-/Budgetgrenzen (A6/A7), Rekursionsschichten 2–3; Eintrag 3 + Zulassungsreihenfolge, Wiedereintritts-Sperre (S3).
- [ ] **Commit:** genannte Dateien — `feat(infrastructure): add a non-blocking log capture provider with bounded admission`

### B6 — Schreiber: Store, Batches, Idempotenz, Isolierung, Herzschlag, Drain (T2) (`opus`)

**Files:**
- Create in `src/EmotePurge.Infrastructure/Logging/`: `ILogCaptureStore.cs`, `PostgresLogCaptureStore.cs`, `LogCaptureDataSource.cs`, `LogCaptureWriter.cs`, `LogCaptureHeartbeat.cs`, `LogsAppendedNotifier.cs`
- Test: Create `tests/EmotePurge.Infrastructure.Tests/Integration/LogCaptureWriterTests.cs` (eigene DB im `PostgresFixture`; für Ausfälle ein **eigener** `PostgreSqlContainer`, damit das Pausieren die Collection nicht stört), `tests/EmotePurge.Infrastructure.Tests/Unit/LogsAppendedNotifierTests.cs`
- Modify: `docs/DECISIONS.md` (Einträge 2 und 3 fortschreiben)

**Interfaces:**
- Consumes: B5-Typen; `IRedisPublisher` (lazy aus dem `IServiceProvider` beim ersten Publish); `LiveEvents.LogsAppended` (Konstante legt **dieser** Task in `src/EmotePurge.Core/Messaging/LiveEvents.cs` an, Aufnahme in `AdminTypes` folgt in B7).
- Produces:
  - `internal interface ILogCaptureStore { Task InsertAsync(IReadOnlyList<CapturedLogEntry> batch, CancellationToken ct); Task UpsertStatusAsync(LogCaptureStatusWrite status, CancellationToken ct); Task<LogCaptureStatusWrite?> ReadLatestOtherInstanceAsync(LogSource source, string ownInstanceId, CancellationToken ct); }`
    mit `internal sealed record LogCaptureStatusWrite(LogSource Source, string InstanceId, DateTime ProcessStartedUtc, DateTime LastFlushUtc, bool CaptureEnabled, DateTime LastIncompleteAtUtc, DateTime? CleanShutdownAtUtc)`.
  - `PostgresLogCaptureStore`: ein Mehrzeilen-`INSERT … ON CONFLICT ("CaptureId") DO NOTHING` je Batch (kein Binary-COPY, S4), 5 s Command-Timeout;
    Status-Upsert mit `"LastIncompleteAtUtc" = GREATEST(existing, excluded)`.
  - `LogCaptureDataSource`: `NpgsqlDataSource` aus `ConnectionStrings:DefaultConnection` **ohne** Logger-Factory, `MaxPoolSize = 2`, `ApplicationName = emotepurge-logcapture`.
  - `public sealed class LogCaptureWriter : BackgroundService` — ganze Schleife unter `LogCaptureSuppression.Enter()`. Batch ≤ 500/≤ 1 s;
    deterministisch = SQLSTATE-Klasse `22`/`54` oder Kodierungsfehler vor dem Senden → Halbierung bis Einzelsatz (≤ 64 Statements), Rest `Rejected`;
    sonst eine Wiederholung nach 2 s, dann `WriteUnconfirmed` je Eintrag; danach Backoff 1/2/4 … 30 s. Nach Erholung zuerst Drop-Eintrag, dann Puffer.
    Drop-Eintrag: `CutPending()` → fester Datensatz mit eigener `CaptureId` (bis geschrieben gehalten), Kategorie `EmotePurge.LogCapture`, Warning,
    Message `"{n} log entries were dropped ({reason breakdown})"` — Breakdown = nur Gründe > 0 als `queue_full=3, budget=2` (L12) —, Properties
    `dropped`, `queuefull`, `budget`, `writeunconfirmed`, `capturefailed`, `reentrant`, `rejected`, `firstdroputc`, `lastdroputc`; ≤ 1 je 60 s.
    Herzschlag nach jedem Erfolg, sonst ≥ alle 60 s. Start: Marker = `ProcessStartedUtc`, außer die jüngste andere Instanz der Quelle hat
    `CaptureEnabled` und `CleanShutdownAtUtc ≥ ownStart − 5 min` → deren Marker übernehmen (A37). Gescheiterter Batch/Herzschlag, unbestätigter Batch mit Error → Marker auf jetzt.
    `StopAsync`: Puffer ≤ 3 s leeren, letzter Herzschlag mit `CleanShutdownAtUtc = now` nur ohne verlorenen Error; Restzahl einmal in die Console (eigene Kategorie, gedrosselt ≤ 1 je 5 min je Grund im Betrieb).
  - `public sealed class LogCaptureHeartbeat : BackgroundService` — nur bei `Enabled = false`: alle 60 s Status mit `CaptureEnabled = false` (S9).
  - `internal sealed class LogsAppendedNotifier(IServiceProvider services, TimeProvider time)` — `void OnBatchCommitted(IReadOnlyList<CapturedLogEntry> batch)`: publiziert `LiveEvent(LiveEvents.LogsAppended)` auf `live:events`
    nur bei ≥ 1 nicht synthetischem Eintrag außerhalb der Zustellkategorien `EmotePurge.Infrastructure.Redis.RedisLiveEventStream` und `EmotePurge.Api.Endpoints.LiveEndpoints` (A10), ≤ 1 je 2 s, fire and forget, Fehler verworfen und gedrosselt geloggt.

- [ ] **Tests (rot), Integration:** `Batch_LandsCompletely`; `LostAck_RetryWritesNoDuplicate` (Store-Decorator: Insert ausführen, dann werfen) — genau eine Zeile je `CaptureId`,
  auch für einen Drop-Eintrag; `TwoFailures_CountWriteUnconfirmed_NoDuplicates`; `Isolation_499ValidOneRejected` (Test-Seam: Kategorie > 256 an der Kürzung vorbei) → 499 gespeichert,
  Drop-Eintrag `rejected=1`; `TransientError_Retries_DoesNotBisect`; `NulAndLoneSurrogate_StoredAsReplacementChars`; `PostgresPaused_LogP99StaysUnder1ms_ThenDropEntryFirst`
  (eigener Container pausiert; paralleler Logger misst `Log()`-Latenz; nach Unpause erst Drop-Eintrag mit korrekter Aufschlüsselung, dann Puffer); `Heartbeat_WithoutEntries_UpdatesLastFlush`;
  `Shutdown_DrainsWithin3Seconds`; **Review Focus 1:** `Stop_WithPostgresPaused_ReturnsWithinDrainBudget` (≤ 3,5 s); `WriterSql_NeverCaptured_EvenWithEfAndNpgsqlAtDebug`
  (EF und Npgsql per Konfiguration auf Debug, Schreiber + EF-Abfragen im selben Prozess: weder `LogEntries` noch erfassender Test-Provider sieht Schreiber-SQL — erfüllt die Umsetzungsauflage zu §5.4/1);
  Abdeckung: `BudgetDroppedError_MarksIncomplete`, `FailedErrorBatchAndFailedDropReport_MarkerSurvives` (3.3), `MarkerNeverDecreases`, `CleanHandover_Within5Min_AdoptsMarker`,
  `CrashOrSixMinutePause_UsesProcessStart`, `DisabledHeartbeat_WritesCaptureEnabledFalse`.
  **Unit:** `Notifier_ThrottlesToOnePer2Seconds`, `Notifier_SkipsSyntheticOnlyBatch`, `Notifier_SkipsDeliveryCategoryOnlyBatch`, `Notifier_RedisThrows_DoesNotPropagate`.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~LogCaptureWriterTests|FullyQualifiedName~LogsAppendedNotifierTests"` — rot.
- [ ] **Umsetzen.** **Lauf:** grün (Docker nötig).
- [ ] **DECISIONS:** Eintrag 2 + synthetische Drop-Einträge (A8), Herzschlag (A9), eigener Datenweg (Schicht 1); Eintrag 3 + `CaptureId`/`ON CONFLICT`, Datensatz-Isolierung, `logs.appended` als Hinweis mit Rekursionsbremse.
- [ ] **Commit:** genannte Dateien + `LiveEvents.cs` — `feat(infrastructure): add the idempotent log capture writer and heartbeat`

### B7 — Registrierung in Api und Worker, `logs.appended` im Admin-Stream (T2) (`sonnet`)

**Files:**
- Create: `src/EmotePurge.Infrastructure/Logging/LogCaptureRegistration.cs`
- Modify: `src/EmotePurge.Api/Program.cs:36` (Aufruf **vor** `AddEmotePurgeInfrastructure`), `src/EmotePurge.Worker/WorkerServiceRegistration.cs:64-81` (erste Zeile von `AddWorkerHostedServices`, Doku „fourteen"), `src/EmotePurge.Worker/Program.cs` (`AddWorkerHostedServices(builder.Configuration)`)
- Modify: `src/EmotePurge.Core/Messaging/LiveEvents.cs:60` (`AdminTypes` + `LogsAppended`)
- Modify: `docker-compose.yml`, `docker-compose.prod.yml` (`LogCapture__Enabled=${LOG_CAPTURE_ENABLED:-true}` an `api` und `worker`), `.env.example` (`LOG_CAPTURE_ENABLED=true` neben `SYSTEM_LOAD_ENABLED`)
- Test: Modify `tests/EmotePurge.Worker.Tests/WorkerServiceRegistrationTests.cs`, `tests/EmotePurge.Api.Tests/ApiFactory.cs:252-258` (`UseSetting("LogCapture:Enabled", "false")`), `tests/EmotePurge.Infrastructure.Tests/Unit/LiveEventTests.cs`; Create `tests/EmotePurge.Api.Tests/LogCaptureRegistrationTests.cs`, `tests/EmotePurge.Infrastructure.Tests/Unit/LogCaptureRegistrationTests.cs`
- Modify: `CLAUDE.md` (Worker-Hosted-Services: vierzehn, Schreiber vorn; Api-Hosted-Services), `docs/Architectur.md:84` (fourteen), `docs/DECISIONS.md` (Einträge 2 und 3)

**Interfaces:**
- Produces: `public static IServiceCollection AddLogCapture(this IServiceCollection services, IConfiguration configuration, LogSource source)`
  (L6: braucht die Konfiguration, weil `Enabled` die Registrierung entscheidet). `Enabled = true`: Options, Identity, Puffer, Budget,
  Zähler, Coverage, Provider als `ILoggerProvider` (`TryAddEnumerable`), Store, Notifier, `LogCaptureWriter` als Hosted Service.
  `Enabled = false`: Identity, Store und `LogCaptureHeartbeat`, **kein** Provider/Puffer. Ruft `TryAddSingleton(TimeProvider.System)` selbst.
- Produces: `public static IServiceCollection AddWorkerHostedServices(this IServiceCollection services, IConfiguration configuration)` — Signaturänderung, erster Aufruf `AddLogCapture(configuration, LogSource.Worker)`.
- Produces: `public const string LiveEvents.LogsAppended = "logs.appended";` in `AdminTypes`, nicht in `ChannelTypes`.

- [ ] **Tests (rot):** Worker: `AddWorkerHostedServices_RegistersExactlyTheFourteen` (umbenannt; mit `Enabled` true und false je 14), `FirstHostedService_IsLogCaptureWriter`
  bzw. `…_IsLogCaptureHeartbeat_WhenDisabled`, `AddHarness_RegistersNoLogCaptureProviderWriterOrHeartbeat` (A2), `AddWorkerCore_AloneRegistersNoHostedService` bleibt.
  Api: `ApiHost_FirstOwnHostedService_IsLogCapture` (erster `IHostedService` aus `EmotePurge.*` ist Heartbeat, Factory hat `Enabled=false`).
  Infrastructure: `AddLogCapture_Disabled_RegistersNoProviderAndNoBuffer`, `AddLogCapture_Enabled_ProviderReachesLoggerFactory`. `LiveEventTests`: `LogsAppended_IsAdminType_NotChannelType`.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Worker.Tests --filter "FullyQualifiedName~WorkerServiceRegistrationTests"`; `dotnet test tests/EmotePurge.Api.Tests --filter "FullyQualifiedName~LogCaptureRegistrationTests"`; `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~LogCaptureRegistrationTests|FullyQualifiedName~LiveEventTests"` — rot.
- [ ] **Umsetzen.** **Lauf:** grün; dann `dotnet test EmotePurge.slnx` grün.
- [ ] **Live (Regel 16, 15):** `docker compose up -d --build`; binnen 5 s `Microsoft.Hosting.Lifetime`-Zeilen beider Prozesse in `LogEntries` mit `Source`, `InstanceId`, `ProcessStartedUtc` (AK 2, per `docker compose exec postgres psql …`);
  mit `Logging__LogLevel__Default=Debug` an beiden Diensten keine Debug-/Trace- und keine EF-Command-Zeile unter Warning (AK 3);
  10 min Ruhe: Wachstum nur durch periodische Zeilen, nach Kategorie gezählt (AK 6); `docker compose stop postgres` 2 min: `GET /api/health` antwortet, kein Absturz, nach Start binnen 60 s ein `EmotePurge.LogCapture`-Eintrag mit `dropped > 0` (AK 5);
  `docker compose --profile harness run --rm harness <kanal> --days 1` → keine Einträge mit der Harness-Instanz (AK 18).
- [ ] **DECISIONS:** Eintrag 2 + Registrierung außerhalb von `AddEmotePurgeInfrastructure` und ohne Harness, Schreiber als erster Hosted Service, `LOG_CAPTURE_ENABLED`; Eintrag 3 + `AdminTypes`-Erweiterung.
- [ ] **Commit:** genannte Dateien — `feat: capture api and worker logs into postgres`

### B8 — Filterparser und Zeitraum/Buckets (T3) (`sonnet`)

**Files:**
- Create: `src/EmotePurge.Core/Services/LogQueryErrors.cs`, `src/EmotePurge.Infrastructure/Logging/Query/LogFilterParser.cs`, `LogFilterExpression.cs`, `LogTimeRange.cs`, `LogHistogramBuckets.cs`
- Test: Create `tests/EmotePurge.Infrastructure.Tests/Unit/LogFilterParserTests.cs`, `LogTimeRangeTests.cs`, `LogHistogramBucketsTests.cs`, `LogFilterSeedTests.cs`
- Modify: `docs/DECISIONS.md` (Eintrag 4 anlegen)

**Interfaces:**
- Produces (Core): `public sealed class LogFilterSyntaxException(string reason, int position, int length) : Exception` (`Reason`, `Position`, `Length` als UTF-16-Indizes),
  `public sealed class LogRangeException : Exception`, `public sealed class LogQueryTimeoutException : Exception`;
  `public static class LogFilterErrorReasons` mit den zwölf Konstanten aus §7.3; `public sealed record LogRangeRequest(string? Range, DateTime? FromUtc, DateTime? ToUtc)`.
- Produces (Infrastructure, `internal`): `LogFilterParseResult LogFilterParser.Parse(string? filter)` (Erfolg mit `LogFilterExpression` oder Fehler mit Reason/Position/Length; kein Teil-Ergebnis);
  AST `LogFilterExpression(IReadOnlyList<LogFilterTerm> Terms)` mit Termen `LevelTerm(LogFilterOperator, levels)`, `SourceTerm`, `CategoryTerm`, `EventTerm`,
  `InstanceTerm(bool Latest, ids)`, `UptimeTerm(LogFilterOperator, TimeSpan)`, `HasExceptionTerm`, `PropertyTerm(string Key, patterns)`, `FreeTextTerm(pattern)`, je `bool Negated`;
  `LogTextPattern(string Value, bool HasWildcard)` mit `string ToIlikePattern()` (`%`, `_`, `\` maskiert, `*` → `%`).
- Produces: `LogExactRange LogTimeRange.Resolve(LogRangeRequest request, DateTime nowUtc)` → `(DateTime FromUtc, DateTime ToUtc, bool IsRelative)`; wirft `LogRangeException`
  (gemischt, unbekanntes Preset, `from ≥ to`, > 8 Tage, `to` > jetzt + 12 h). `LogExactRange LogTimeRange.ResolveKeys(DateTime fromUtc, DateTime? toUtc, DateTime nowUtc)` für `keysOnly` (L2: `to` optional = jetzt, gleiche Grenzen).
- Produces: `LogBucketing LogHistogramBuckets.Compute(DateTime fromUtc, DateTime toUtc)` → `(TimeSpan Width, DateTime BucketFromUtc, DateTime BucketToUtc, int Count)`; Leiter und ≤ 100 aus §8.2.

- [ ] **Tests (rot):** Parser — jeder Schlüssel und Operator aus §7.2, Level-Aliase, Kommalisten, Negation ganzer Terme, Zitate mit `\"`, Wildcards inkl. Maskierung,
  jede `reason` mit genauer `position`/`length` (auch nach `ä`, Emoji/Surrogatpaar in UTF-16), Grenzen 512/16/16/200, leerer und reiner Leerraum-Filter gültig,
  `http://x` → Property `http` mit Wert `//x`, `"http://x"` → Freitext, `Fehler:` → `empty_value`, `debug` → `invalid_level`, `*` bei `level`/`uptime`/`has` → `invalid_value`, `channel:*` → „vorhanden".
  Bucket-Fälle **exakt** aus §13.1 (Presets 24h/1h/6h/7d mit den genannten Uhrzeiten und Zählungen, unausgerichtet 10:03:01–10:17:01 → 10 s/85, Schwellen 50:00/50:01, 1 h nie 30 s, Eingrenzen 15 min → 10 s/90, 2 h → 2 min/60),
  Idempotenz je Fall, Eigenschaftstest über zufällige gültige Bereiche (beide Grenzen abgedeckt, ≤ 100, Breite minimal, jeder Bucket zurückgeschickt gültig).
  Zeitraum (R4): um 12:01 mit `7d` exakte Grenzen; 8 Tage angenommen, 8 Tage + 1 s abgelehnt; `to` = jetzt + 12 h angenommen, + 12 h 1 s abgelehnt; gemischt → Fehler.
  Seeds: `AllFourSeeds_Parse`; `SeedCategories_ExistInSource` — **Quelltext-Scan** über `src/` (Muster `AdminDashboardQueryServiceStructureTests`), weil Infrastructure.Tests den Worker nicht referenziert (L4):
  `TwitchConnectionWatchdog`, `TwitchChatManager` existieren als Klassen, `SevenTv*`-Klassen im Worker und in Infrastructure; `SeedPhrases_StillInSource` — „angestoßen (Quelle" und „ gejoint." stehen in `TwitchChatManager.cs`.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~Unit.LogFilterParserTests|FullyQualifiedName~Unit.LogTimeRangeTests|FullyQualifiedName~Unit.LogHistogramBucketsTests|FullyQualifiedName~Unit.LogFilterSeedTests"` — rot.
- [ ] **Umsetzen.** **Lauf:** grün.
- [ ] **DECISIONS-Eintrag 4 anlegen** („Log filter language and log viewer endpoint contracts"): Grammatik (A16), ein Parser serverseitig (A17), Fehlervokabular, exakte Listengrenzen, Einrasten nur fürs Histogramm, ≤ 100 Buckets.
- [ ] **Commit:** genannte Dateien — `feat(infrastructure): add the log filter parser and histogram bucketing`

### B9 — `ILogQueryService`: Seite, Abgleich, Detail, Summary (T3) (`opus`)

**Files:**
- Create: `src/EmotePurge.Core/Services/ILogQueryService.cs`, `src/EmotePurge.Infrastructure/Logging/Query/LogQueryService.cs`, `LogFilterSql.cs`, `LogViewerOptions.cs`
- Modify: `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (Scoped `ILogQueryService`, Options-Bindung `LogViewer`)
- Test: Create `tests/EmotePurge.Infrastructure.Tests/Integration/LogQueryServiceTests.cs`, `LogQueryReconcileTests.cs`, `LogViewerSuppressionTests.cs`; Fixture-Helfer `tests/EmotePurge.Infrastructure.Tests/Fixtures/LogEntrySeed.cs`
- Modify: `docs/DECISIONS.md` (Eintrag 4)

**Interfaces:**
- Consumes: B8 (Parser, `LogTimeRange`, `LogHistogramBuckets`, Core-Exceptions); `LogCaptureSuppression` (B5); Writer (B6) für den S1-Test.
- Produces (Core):
  - `Task<LogPage> GetPageAsync(string? filter, LogRangeRequest range, string? before, int limit, CancellationToken ct)` — `limit` auf 1–200 geklemmt (Hausstil `PagingQuery`); kaputter Cursor → `LogRangeException` (L8).
  - `Task<LogKeyPage> GetKeysAsync(string? filter, DateTime fromUtc, DateTime? toUtc, CancellationToken ct)` — ≤ 2.500, sonst `Truncated`.
  - `Task<IReadOnlyList<LogEntryView>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct)` — ≤ 200, ohne Zeitraum und **ohne Filter** (L10).
  - `Task<LogEntryDetailView?> GetEntryAsync(long id, CancellationToken ct)`.
  - `Task<LogSummary> GetSummaryAsync(string? filter, LogRangeRequest range, CancellationToken ct)`.
  - Records: `LogEntryView(long Id, DateTime TimestampUtc, LogEntryLevel Level, LogSource Source, string InstanceId, string Category, int? EventId, string? EventName, string Message, bool HasException, string? ExceptionType, string? PropertiesJson, bool Truncated)`;
    `LogEntryDetailView(LogEntryView Entry, string? Exception, DateTime ProcessStartedUtc, long UptimeSeconds)`; `LogPage(IReadOnlyList<LogEntryView> Entries, string? NextCursor, DateTime RangeFromUtc, DateTime RangeToUtc, bool Truncated)`;
    `LogKeyPage(IReadOnlyList<LogEntryKey> Keys, DateTime RangeFromUtc, DateTime RangeToUtc, bool Truncated)`, `LogEntryKey(long Id, DateTime TimestampUtc)`;
    `LogSummary(DateTime RangeFromUtc, DateTime RangeToUtc, DateTime BucketFromUtc, DateTime BucketToUtc, int BucketSeconds, IReadOnlyList<DateTime> BucketStartsUtc, IReadOnlyList<int> Info, IReadOnlyList<int> Warn, IReadOnlyList<int> Error, int Total, LogFacets Facets, LogDropTotals Dropped, IReadOnlyList<LogProcessStart> ProcessStarts)`;
    `LogFacets(IReadOnlyList<LogFacetValue> Source, IReadOnlyList<LogFacetValue> Level, IReadOnlyList<LogCategoryFacetValue> Category, int CategoryOther)`;
    `LogDropTotals(long Total, long QueueFull, long Budget, long WriteUnconfirmed, long CaptureFailed, long Reentrant, long Rejected)` (L1: `Rejected` ergänzt); `LogProcessStart(LogSource Source, string InstanceId, DateTime StartedUtc)`.
  - Cursor: opak, base64url aus `(TimestampUtc, Id)`; Keyset `(TimestampUtc, Id) < cursor` im selben Zeitraum.
- Verhalten: Werte nur als `NpgsqlParameter`; Ausführung über die Verbindung des `AppDbContext` in einer Transaktion mit `SET LOCAL statement_timeout` =
  `LogViewer:QueryTimeoutMilliseconds` (Default 5000; L7: nur für Testkonfiguration, nicht in Compose); SQLSTATE `57014` → `LogQueryTimeoutException`.
  `cat` ohne `*`: gleich **oder** endet auf `.` + Wert; mit `*`: über vollen Namen oder jedes Suffix nach `.`. `instance:latest` je Quelle über `LogCaptureStatus` (jüngstes `ProcessStartedUtc`), ersatzweise `LogEntries`.
  `dropped` summiert die Properties der `EmotePurge.LogCapture`-Einträge im Zeitraum **unabhängig vom Filter**. `counts` nur innerhalb der exakten Grenzen; leere Buckets 0.

- [ ] **Tests (rot):** jede Filterart gegen gesetzte Zeilen (inkl. `cat:TwitchChatManager`, `cat:SevenTv*`, `cat:TwitchLib.*`, Property `channel:handofblood`, `channel:*`, Negation, Freitext in Message und Exception, `uptime<15m`, `has:exception`, `event:` Name und Zahl);
  `Seed_TwitchReconnects_KeepsReconnectLines_DropsJoinLines` (O1: die vier Templates mit Beispielwerten gerendert; `:218`/`:998` bleiben, `:540`/`:1010` fallen);
  Keyset ohne Lücken/Dubletten bei gleichen Zeitstempeln; eingefrorene Grenzen beim Nachladen; `instance:latest` mit zwei Instanzen je Quelle;
  Summary: Bucket-Zählung je Levelklasse, Facetten Top 15 + `CategoryOther`, `short` = letztes Segment, `dropped` unabhängig vom Filter, `processStarts`; AK 24: 10:03:01–10:17:01 zählt Eintrag 10:03:05;
  `Timeout_MapsToLogQueryTimeoutException` (`pg_sleep`-Hook über `CommandHook`). Abgleich (R2/R7): verzögerter Commit 30 s zwischen t40/t41 (Hook hält die Schreibtransaktion offen, parallele Abgleiche) → erster Abgleich danach liefert die Id;
  `keysOnly` ab `rangeFromUtc` bei leerer Liste; nach Paginierungsende (11:59, 11:58) verspäteter 11:57 → geliefert; > 2.500 → `Truncated`; `ids=` genau die angefragten, ohne Zeitraum.
  **S1:** `ViewerTimeouts_UnderSuppression_AddNoEntriesAndNoNotification` — laufender Schreiber (B6) + Redis-Fixture, 20 Timeouts im `LogCaptureSuppression.Enter()`-Bereich (wie der Endpoint-Filter) → `LogEntries` ohne neue `Microsoft.EntityFrameworkCore.*`/`Npgsql*`/Ansichtskategorien, kein `logs.appended` darauf.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~LogQueryServiceTests|FullyQualifiedName~LogQueryReconcileTests|FullyQualifiedName~LogViewerSuppressionTests"` — rot.
- [ ] **Umsetzen.** **Lauf:** grün.
- [ ] **DECISIONS:** Eintrag 4 + periodischer REST-Abgleich statt `afterId`/Kopfseite, `keysOnly`/`ids`, Unterdrückungsbereich der Lesepfade (S1/A31), `LogViewer:QueryTimeoutMilliseconds` als Test-Schalter.
- [ ] **Commit:** genannte Dateien — `feat(infrastructure): add the log query service with keyset paging and reconciliation keys`

### B10 — Status, Abdeckung, `TtlCache` herausziehen, gespeicherte Filter (T3) (`sonnet`)

**Files:**
- Create: `src/EmotePurge.Infrastructure/Services/TtlCache.cs` (aus `AdminDashboardReader.cs:151` herausgezogen, `internal sealed`, Verhalten unverändert)
- Modify: `src/EmotePurge.Infrastructure/Services/AdminDashboardReader.cs`
- Create: `src/EmotePurge.Core/Services/ILogStatusReader.cs`, `src/EmotePurge.Core/Services/ILogFilterService.cs`, `src/EmotePurge.Infrastructure/Logging/Query/LogStatusReader.cs`, `LogFilterService.cs`
- Modify: `src/EmotePurge.Core/Services/ILogQueryService.cs` (+ `ComputeStatusAsync`), `LogQueryService.cs`, `ServiceCollectionExtensions.cs` (Singleton `ILogStatusReader`, Scoped `ILogFilterService`)
- Test: Create `tests/EmotePurge.Infrastructure.Tests/Integration/LogStatusTests.cs`, `LogFilterServiceTests.cs`; bestehende `Unit/AdminDashboardReaderTests.cs` bleiben grün
- Modify: `docs/DECISIONS.md` (Eintrag 4)

**Interfaces:**
- Produces: `Task<LogCaptureStatusView> ILogQueryService.ComputeStatusAsync(CancellationToken ct)`; `Task<LogCaptureStatusView> ILogStatusReader.ReadAsync(CancellationToken ct)` (30 s `TtlCache`, Single-Flight, Fehler nicht gecacht, eigener Scope je Füllung).
  `LogCaptureStatusView(DateTime GeneratedAtUtc, IReadOnlyList<LogSourceStatus> Sources, LogCoverage Coverage24h, LogRetentionStatus Retention)`;
  `LogSourceStatus(LogSource Source, LogSourceState State, string? InstanceId, DateTime? StartedUtc, DateTime? LastFlushUtc)`; `enum LogSourceState { Healthy, Stale, Disabled, Missing }`;
  `LogCoverage(bool Complete, int RecordedErrors, IReadOnlyList<LogIncompleteSince> IncompleteSince)`, `LogIncompleteSince(LogSource Source, DateTime LastIncompleteAtUtc)`; `LogRetentionStatus(DateTime? OldestEntryUtc, bool Overdue)`.
  Regeln: `Sources` immer genau `api`, `worker`; je Quelle zählt die Zeile mit jüngstem `ProcessStartedUtc`; `Missing` ohne Zeile, `Stale` bei `LastFlushUtc` < jetzt − 5 min, `Disabled` frisch + `CaptureEnabled = false`.
  `Complete` = für beide Quellen Marker der jüngsten Instanz < jetzt − 24 h; `RecordedErrors` = `Level ≥ 4` in 24 h; `Overdue` = ältester < jetzt − (7 d + 2 h).
- Produces: `ILogFilterService`: `Task<IReadOnlyList<LogFilterView>> ListAsync(CancellationToken)`, `Task<LogFilterWriteResult> CreateAsync(string name, string filter, CancellationToken)`,
  `Task<LogFilterWriteResult> UpdateAsync(int id, string name, string filter, CancellationToken)`, `Task<bool> DeleteAsync(int id, CancellationToken)`;
  `LogFilterView(int Id, string Name, string Filter, int SortOrder)`; `LogFilterWriteResult` mit `LogFilterWriteStatus { Ok, InvalidName, InvalidFilter, NameTaken, LimitReached, NotFound }`, `LogFilterView? Filter`, Parser-Fehler (`Reason`, `Position`, `Length`) bei `InvalidFilter`.
  Name getrimmt, 1–60; Filter über `LogFilterParser`; ≤ 30; neue `SortOrder` = max + 1; 23505 auf dem Namensindex → `NameTaken`. Kein Audit (A12). Filterstring wird **nicht** normalisiert gespeichert.

- [ ] **Tests (rot):** Status (S9): Kaltstart → beide `missing`, `Complete = false`; erster Herzschlag → `healthy`; geleerte Tabelle → `missing`; nach 7 Tagen abgelaufene letzte Zeile → `missing`, bleibt in der Liste;
  `CaptureEnabled = false` → `disabled`; `LastFlushUtc` 6 min alt → `stale`. Abdeckung (R5): Budget-Drop-Error → `Complete = false` trotz `healthy`; simulierter Restore → 24 h unvollständig; `overdue` bei 7 d + 2 h + 1 s.
  **Review Focus 2:** `Status_TwoLiveInstancesOfOneSource_ReportsNewest`, `InstanceLatest_IgnoresOlderInstanceStillWriting` (zweite Instanz schreibt weiter Herzschläge).
  Cache: zwei Aufrufe binnen 30 s → eine Füllung. Filter: CRUD, `log_filter_limit_reached` bei 31., Parser-Validierung; **Review Focus 5:** `Create_TrimmedCaseVariant_IsNameTaken`, `Update_OwnNameDifferentCase_Succeeds`, `Update_SeedUnchanged_KeepsFilterByteIdentical`.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~LogStatusTests|FullyQualifiedName~LogFilterServiceTests|FullyQualifiedName~AdminDashboardReaderTests"` — rot (nur die neuen).
- [ ] **Umsetzen.** **Lauf:** grün.
- [ ] **DECISIONS:** Eintrag 4 + vier Lebendigkeitszustände, getrennte 24-h-Abdeckung über `LastIncompleteAtUtc`, globale gespeicherte Filter; `TtlCache` als gemeinsame `internal`-Klasse.
- [ ] **Commit:** genannte Dateien — `feat(infrastructure): add log capture status, coverage and saved log filters`

### B11 — Api: Routen, Unterdrückungsfilter, Parallelitäts-Gate, Fehlercodes (T3) (`sonnet`)

**Files:**
- Create: `src/EmotePurge.Api/Endpoints/AdminLogEndpoints.cs`, `src/EmotePurge.Api/LogViewer/LogViewerQueryScopeFilter.cs`, `src/EmotePurge.Api/LogViewer/LogQueryConcurrencyGate.cs`, `src/EmotePurge.Api/LogViewer/LogQueryConcurrencyFilter.cs`
- Modify: `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs:34-36` (`group.MapAdminLogEndpoints()`), `src/EmotePurge.Api/Program.cs` (Singleton `LogQueryConcurrencyGate`)
- Modify: `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`, `web/src/app/core/i18n/api-error.ts`, `web/public/i18n/de.json`, `web/public/i18n/en.json` (`errors.api.*`)
- Modify: `tests/EmotePurge.Api.Tests/ApiFactory.cs` (Substitute `ILogQueryService`, `ILogStatusReader`, `ILogFilterService`), `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs:98-106` (alle neuen Routen in die 401/403-Matrix); Create `tests/EmotePurge.Api.Tests/AdminLogEndpointTests.cs`
- Modify: `docs/DECISIONS.md` (Eintrag 4)

**Interfaces:**
- Consumes: `ILogQueryService`, `ILogStatusReader`, `ILogFilterService`, Core-Exceptions (B8–B10), `LogCaptureSuppression` (B5).
- Produces (Routen exakt §8.1): Untergruppe `/logs` und `/log-filters` je mit `LogViewerQueryScopeFilter`; `LogQueryConcurrencyFilter` nur an `GET /logs`, `GET /logs/summary`, `GET /logs/{id}`.
  Filterreihenfolge: `GlobalAdminAuthorizationFilter` (Gruppe) → Scope-Filter → Gate.
  - `LogViewerQueryScopeFilter`: ganzer Handler in `LogCaptureSuppression.Enter()`; fängt innerhalb: `LogFilterSyntaxException` → `400 { errorCode: "invalid_log_filter", reason, position, length }`;
    `LogRangeException` → `400 invalid_log_range`; `LogQueryTimeoutException` → `503 log_query_timeout`; sonst `500 unexpected_error`; Console-Zeile über Kategorie `EmotePurge.Infrastructure.Logging.ViewerQueries`, ≤ 1 je Minute.
  - `internal sealed class LogQueryConcurrencyGate(int maxConcurrent = 2, TimeSpan? wait = null)` (Default 5 s); Überlauf → `503`, `Retry-After: 5`, kein Body.
  - Wire: Level `info|warn|error|crit`, Quelle `api|worker`, Zustand `healthy|stale|disabled|missing`, `properties` als JSON-Objekt aus `PropertiesJson` (leer → `{}`), Zeiten ISO-8601 UTC;
    `GET /logs` mit `keysOnly=true` → `{ entries: [{id, timestampUtc}], nextCursor: null, rangeFromUtc, rangeToUtc, truncated }`; `ids` = kommagetrennt, ≤ 200, ungültiges Token → `400 invalid_log_range` (L8);
    `POST`/`PUT` `/log-filters` → `201`/`200` + Filter, Statusabbildung nach §8.1; unlesbarer Body → bestehendes `invalid_request_body`.
- Neue Codes (Regel 7, je Kommentar wie im Bestand): `invalid_log_filter`, `invalid_log_range`, `invalid_log_filter_name`, `log_filter_name_taken`, `log_filter_limit_reached`, `log_filter_not_found`, `log_entry_not_found`, `log_query_timeout`.

- [ ] **Tests (rot):** Matrix: jede neue Route `401` ohne Session, `403` Nicht-Admin; `400`-Vertrag an den echten Routen (substituierter Service wirft) mit `reason`/`position`/`length`, `invalid_log_range`, `invalid_log_filter_name`;
  `Gate_ThirdConcurrentQuery_Gets503WithRetryAfter5AndNoBody` (Gate mit 200 ms Wartezeit per `WithWebHostBuilder` ersetzt, zwei Anfragen hängen an `TaskCompletionSource`); `Status_NotGated`;
  `ScopeFilter_HandlerSeesSuppressionActive`; `ScopeFilter_Timeout_Is503LogQueryTimeout_WithoutMiddlewareLog`, `ScopeFilter_OtherException_Is500UnexpectedError_WithoutMiddlewareLog` (erfassender Test-Provider bleibt leer);
  `FilterOrder_AdminBeforeScope` (Nicht-Admin erreicht den Scope-Filter nie); `Status_AlwaysListsApiAndWorker`; `KeysOnly_ShapeAndLimit`; `api-error-locales.spec.ts` grün.
- [ ] **Lauf:** `dotnet test tests/EmotePurge.Api.Tests --filter "FullyQualifiedName~AdminLogEndpointTests|FullyQualifiedName~AuthFilterMatrixTests"` und `npm --prefix web test -- --watch=false --include src/app/core/i18n/api-error-locales.spec.ts` — rot.
- [ ] **Umsetzen.** Handler bleiben dünn (Regel 11: keine eigenen Handler-Tests). **Lauf:** grün; dann `dotnet test tests/EmotePurge.Api.Tests` grün.
- [ ] **Live:** lokale Api, Admin-Cookie, je ein `curl` auf jede Route; ungültiger Filter `?q=level:debug` → `400` mit `position`.
- [ ] **DECISIONS:** Eintrag 4 + Routen, Zeitlimit und Gate statt Rate-Limit-Policy (A22), Fehlercodes.
- [ ] **Commit:** genannte Dateien — `feat(api): add admin log viewer routes with suppression scope and query gate`

### B12 — Performance-Gate (§13.3, vor dem PR) (`sonnet`)

**Files:**
- Create: `tests/EmotePurge.Infrastructure.Tests/Integration/LogQueryPerformanceGateTests.cs` (`[PerformanceGateFact]` aus `AdminDashboardLoadPerformanceGateTests.cs`, opt-in `EMOTEPURGE_PERF_GATES=1`, `[Trait("Category", "PerformanceGate")]`)

- [ ] **Test:** Wegwerf-Postgres 16 Alpine mit Migrationsschema; 320.000 Zeilen über 8 Tage per einem `INSERT … SELECT generate_series` (90/9/1 % Info/Warn/Error, Properties bei 60 %, Exceptions bei 2 %); das Statement wird in den Test-Output geschrieben.
  Warm (2 Aufwärmläufe, Median aus 5): erste Seite 24 h und 7 d — ohne Filter, `level>=warn`, `cat:SevenTv*`, Freitext, `channel:x`, Negation — ≤ 200 ms; Summary 24 h ≤ 300 ms; Summary 7 d mit Freitext ≤ 800 ms; `status` (Fehlerzahl 24 h, `instance:latest`) ≤ 50 ms; `keysOnly` 7 d ≤ 200 ms. Jeder Fall loggt `EXPLAIN (ANALYZE, BUFFERS)`.
- [ ] **Lauf:** `EMOTEPURGE_PERF_GATES=1 dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~LogQueryPerformanceGateTests" --logger "console;verbosity=detailed"`.
- [ ] **Ende-zu-Ende:** dasselbe Statement gegen die lokale Dev-DB, lokale Api, `curl -s -o /dev/null -w '%{time_total}'` mit Admin-Cookie je Fall; danach lokal `TRUNCATE "LogEntries";`. Ergebnisse für den PR-Text notieren. Bei realem Volumen (~25k) einmal mitmessen.
- [ ] **Reißt eine Grenze:** nicht still ändern — CTE/`GROUPING SETS` versuchen; `pg_trgm` nur nach Betreiberentscheidung (§6.1). Orchestrator informieren.
- [ ] **Commit:** `git add tests/EmotePurge.Infrastructure.Tests/Integration/LogQueryPerformanceGateTests.cs` — `test(infrastructure): add the opt-in log query performance gate`

### B13 — Frontend-Kern: Modelle, Service, Filterwerkzeuge, Abgleich (T4) (`opus`)

**Files:**
- Create: `web/src/app/core/admin/admin-logs.model.ts`, `admin-logs.service.ts` (+ `.spec.ts`), `log-filter-text.ts` (+ `.spec.ts`), `log-reconcile.ts` (+ `.spec.ts`)
- Modify: `web/src/app/core/live/live-event.model.ts:16` (`LIVE_EVENT_TYPES.logsAppended = 'logs.appended'`)

**Interfaces:**
- Consumes: Wire-Vertrag aus B11.
- Produces: Typen `LogLevelKey`, `LogSourceKey`, `LogRangePreset = '1h'|'6h'|'24h'|'7d'`, `LogRangeQuery = { range: LogRangePreset } | { from: string; to: string }`,
  `LogEntry`, `LogEntryDetail`, `LogPage`, `LogKeyPage`, `LogSummary` (inkl. `dropped.rejected`), `LogStatus`, `SavedLogFilter`, `LogFilterApiError { errorCode; reason; position; length }`.
- Produces: `AdminLogsService` — `getPage(q, range, before?, limit?)`, `getKeys(q, fromUtc)`, `getByIds(ids)`, `getEntry(id)`, `getSummary(q, range)`, `getStatus()`, `listFilters()`, `createFilter(name, filter)`, `updateFilter(id, name, filter)`, `deleteFilter(id)` (alle `Observable`).
- Produces: `quoteFilterValue(value: string): string`, `appendFilterTerm(q: string, term: string): string` (kein Anhängen, wenn der Term schon als ganzer Term vorkommt), `filterTerm(key: string, value: string, exclude: boolean): string`, `normalizeFilterWhitespace(q: string): string`, `filterErrorSelection(error: LogFilterApiError): { start: number; end: number }`.
- Produces: `mergeLogEntries(loaded: readonly LogEntry[], incoming: readonly LogEntry[]): LogEntry[]` (sortiert `(timestampUtc desc, id desc)`, Dedupe per `id`); `reconcileFromUtc(loaded, rangeFromUtc, nextCursor): string`;
  `planReconcile(keys: LogKeyPage, loadedIds: ReadonlySet<number>): { kind: 'none' } | { kind: 'fetch'; idBatches: number[][] } | { kind: 'reload' }` (> 500 fehlend oder `truncated` → `reload`, sonst Batches ≤ 200, ≤ 3);
  `class LogReconcileScheduler(now: () => number, run: () => Promise<void>)` — `trigger(): void`, `pause()`, `resume()`; ≤ 1 gleichzeitig, ≥ 5.000 ms Abstand, genau ein nachlaufender Abgleich.

- [ ] **Tests (rot):** Service mit `HttpTestingController`: Parameter, Cursor, eingefrorene exakte Grenzen als `from`/`to` (R4), `keysOnly`-Aufruf ohne `to`. Werkzeuge: Quoten bei Leerraum/Komma/Doppelpunkt/`"`, Anhängen ohne Dublette, Selektion aus `position`/`length` inkl. Emoji.
  Abgleich: `from_L = rangeFromUtc` bei leerer Liste und nach `nextCursor = null`, sonst ältester geladener; Einfügen mitten in geladene Seiten; Dedupe; `reload` bei > 500 bzw. `truncated`; Scheduler: Mindestabstand mit genau einem Nachläufer, `pause` unterdrückt alle Auslöser bis `resume`.
  **Review Focus 3:** `reconcile_spanBeyondEightDays_requestsReloadWithoutRequest` — liegt `from_L` mehr als 8 Tage zurück, liefert der Planer `reload`, ohne Anfrage (L3).
- [ ] **Lauf:** `npm --prefix web test -- --watch=false --include src/app/core/admin/admin-logs.service.spec.ts --include src/app/core/admin/log-filter-text.spec.ts --include src/app/core/admin/log-reconcile.spec.ts` — rot.
- [ ] **Umsetzen.** **Lauf:** grün; `npm --prefix web run lint`.
- [ ] **Commit:** genannte Dateien — `feat(web): add the admin logs service, filter text tools and reconciliation`

### B14 — Histogramm-Primitiv `app-time-histogram` (A27) (`sonnet`)

**Files:**
- Create: `web/src/app/shared/charts/time-histogram.ts` (+ `.spec.ts`); gemeinsame Geometrie aus `shared/charts/chart-series.ts` nutzen, wo sie passt (`niceTicks`, `formatBucketTime`)
- Modify: `docs/UI-Designsprache.md` §2.6 (neuer Unterabschnitt, „one chart primitive" korrigieren), `DESIGN.md` falls dort gespiegelt, `docs/DECISIONS.md` (Eintrag 5) — **im selben Commit** (§17/5)

**Interfaces:**
- Produces: `<app-time-histogram>` mit Inputs `bucketStartsUtc: readonly string[]`, `info/warn/error: readonly number[]`, `bucketSeconds: number`, `processStarts: readonly { source: string; startedUtc: string }[]`,
  `lastBucketPartial: boolean`, `compact: boolean` (32 px Streifen statt 96 px), `label: string`; Output `bucketSelected: { fromUtc: string; toUtc: string }`.
- Regeln §9.6: gestapelt (Info neutral, Warning Warnton + Schraffur, Error/Critical `danger`), ganzzahlige y-Achse, Prozessstart-Marken, leerer Bereich zeichnet Achsen + „keine Einträge in diesem Zeitraum",
  Lesecursor per Pfeiltasten mit `aria-live`-Zeile (Bucketzeit + drei Zahlen), Enter/Klick grenzt ein, `role="img"` mit Summe, Maximum, Zahl der Warning- und Error-Buckets; „Zeiten in UTC".

- [ ] **Tests (rot):** `bucketSelected` liefert Start und Start + `bucketSeconds` (Klick und Enter); Pfeiltasten bewegen den Cursor, Live-Text nennt die drei Zahlen; Textalternative (Summe, Maximum, Bucket-Zahlen); leer → Text „keine Einträge…" vorhanden, kein Wurf. Keine CSS-Assertions (Regel 12).
- [ ] **Lauf:** `npm --prefix web test -- --watch=false --include src/app/shared/charts/time-histogram.spec.ts` — rot. **Umsetzen**, **Lauf** grün, `npm --prefix web run lint` (inkl. `lint:colors`).
- [ ] **Commit:** genannte Dateien — `feat(web): add the time histogram chart primitive`

### B15 — Seite `/admin/logs`: Route, Tab, Filterleiste, Liste, Details, Facetten, Zustände (T4) (`sonnet`)

**Files:**
- Create: `web/src/app/features/admin/admin-logs-page.ts` (+ `.spec.ts`), `admin-log-list.ts` (+ `.spec.ts`), `admin-log-facets.ts` (+ `.spec.ts`)
- Modify: `web/src/app/app.routes.ts:57-95` (Kind `logs`), `web/src/app/features/admin/admin-layout.ts:30` (Tab „Logs" zwischen Monitoring und Kanäle), `web/public/i18n/de.json`, `en.json` (`admin.tabs.logs`, `admin.logs.*`, `admin.logs.filterErrors.<reason>` für alle zwölf)
- Create: `web/e2e/admin-logs.e2e.spec.ts`; Modify `web/e2e/support/mocks.ts` (Mock-Helfer `mockAdminLogs`)

**Interfaces:**
- Consumes: B13 (Service, Werkzeuge), B14 (Histogramm), `listQueryState` (`core/routing/list-query-state.ts`), `SegmentedControl`, `HealthMarker`, `NoticeBanner`, `EmptyState`, `SkeletonRows`, `appButton`.
- Verhalten: URL `?q=&range=` bzw. `?q=&from=&to=` über `listQueryState` (Defaults entfernt, `replaceUrl`); Filter angewandt auf Enter/„Anwenden" (A23), Facetten/Balken sofort; „Zurück zu 24 h" (bzw. vorigem relativem Bereich) nach Eingrenzen.
  Desktop- und Telefon-Aufbau exakt §9.2/§9.3 (sticky Leiste `app-sticky-bar top-24 py-2`, reservierter „n neue"-Platz auch bei 0, Facetten ab `lg` links ~14 rem, sonst hinter „Filter" im Dialog/Sheet; Liste dicht, Tagestrennzeile, Disclosure `aria-expanded`/`aria-controls`; Details mit Properties „filtern"/„ausschließen", Exception lazy mit Skeleton, „Kopieren" im `docker logs`-Stil, „Sichtbare kopieren" ≤ 500).
  Kurzname der Kategorie-Facette nur, wenn unter den Facettenwerten eindeutig. Nachladen per Sentinel 100er-Schritte, Stopp bei 2.000 mit „Zeitraum oder Filter eingrenzen" (A25). Zustände §9.7 inklusive Drop-Banner mit allen Gründen > 0 (L1), Abdeckungs-, Stale-/Disabled-/Missing- und Overdue-Banner, `log_query_timeout`/`503` als Fehlerbanner mit stehenbleibender letzter Antwort.
  Filterfehler: `aria-invalid`, Meldung per `aria-describedby` aus `admin.logs.filterErrors.<reason>`, Selektion auf `position`/`length`.
- Copy: deutsche Texte wörtlich aus Spec §9 und §11 („Keine Einträge für diesen Filter im Zeitraum", „Abfrage zu aufwendig — Zeitraum oder Filter eingrenzen", „Zeiten in UTC", „Die Log-Erfassung von {source} hat seit {hh:mm} UTC nichts geschrieben — neuere Einträge fehlen hier, `docker logs` hat sie" usw.); englische Entsprechung sinngleich. Log-Inhalte nie übersetzen. Hilfe-Popover „?" mit Schlüsseln aus §7.2 und drei Beispielen (inkl. Stolperstein `Fehler:`).

- [ ] **Tests (rot), Vitest (Verhalten, keine Vorlage):** Chip-unabhängige Seitenlogik: Kurzname-Wahl der Kategorie-Facette; Facetten-Klick hängt `key:wert` bzw. `-key:wert` an; Histogramm-Auswahl setzt `from`/`to` und bietet „Zurück zu 24 h"; Disclosure- und Accessible-Name-Semantik der Facetten-Aktionen; 2.000er-Stopp.
  **E2E:** Deep-Link `?q=…` öffnet vorgefiltert (AK 11, Beispiel-Link aus §9.1); ungültiger Filter markiert die Stelle; Facette hinzufügen und ausschließen; Balken-Klick grenzt ein, „Zurück zu 24 h"; Scroll-Nachladen bis 2.000 mit Hinweis; Drop-, Stale- und Timeout-Banner;
  mobiler Viewport 390 × 844: Facetten hinter „Filter", Streifen, Tippen klappt Details auf, Dokumentbreite ≤ Viewport (AK 16).
  **Review Focus 4:** `malformed deep link shows an error and settles` — `?range=2d`, `?from=…` ohne `to`, `q` mit 513 Zeichen: sichtbarer Fehler, Zeitwahl bedienbar, Anzahl der `/api/admin/logs*`-Requests nach 5 s (`page.clock`) unverändert.
- [ ] **Lauf:** `npm --prefix web test -- --watch=false --include src/app/features/admin/admin-logs-page.spec.ts --include src/app/features/admin/admin-log-list.spec.ts --include src/app/features/admin/admin-log-facets.spec.ts`; `npm --prefix web run e2e -- admin-logs.e2e.spec.ts` (ohne Api auf `:5151`) — rot.
- [ ] **Umsetzen.** **Lauf:** grün; `npm --prefix web run lint`, `npm --prefix web run format:check`.
- [ ] **Commit:** genannte Dateien — `feat(web): add the admin logs page`

### B16 — Gespeicherte Filter: Chips, Dialog, Menü (T4, B2) (`sonnet`)

**Files:**
- Create: `web/src/app/features/admin/admin-log-filter-chips.ts` (+ `.spec.ts`), `admin-log-filter-dialog.ts` (+ `.spec.ts`, mit `openLogFilterDialog(dialog, data)`)
- Modify: `admin-logs-page.ts`, `web/public/i18n/de.json`, `en.json`, `web/e2e/admin-logs.e2e.spec.ts`, `web/e2e/support/mocks.ts`

**Interfaces:**
- Consumes: `AdminLogsService` Filter-Methoden, `normalizeFilterWhitespace` (B13), `DialogShell`/`openAppDialog`, `ConfirmDialog`/`openConfirmDialog`, `app-popover`.
- Verhalten §9.5: Chip aktiv bei `normalizeFilterWhitespace(chip.filter) === normalizeFilterWhitespace(q)`; Klick ersetzt `q`, behält den Zeitraum; „+ Filter speichern" gesperrt bei leerem Filter mit sichtbarer Begründung;
  Menü „Bearbeiten" / „Mit aktuellem Filter überschreiben" / „Löschen" (Confirm); Server-Fehler als Feldfehler (`log_filter_name_taken` am Namen, `invalid_log_filter` am Filter); ungültiger gespeicherter Filter bleibt klickbar und zeigt den Filterfehler.

- [ ] **Tests (rot):** Vitest: Aktiv-Erkennung mit Whitespace-Normalisierung; Dialog-Rückgabe; Sperrgrund bei leerem Filter; Feldfehler-Zuordnung. E2E: Chip-Klick ersetzt `q`; Speichern, Bearbeiten, Löschen (mit Confirm) eines Filters; Monitoring-Seed „Alle Fehler" erscheint aktiv bei `q=level>=error`.
- [ ] **Lauf:** `npm --prefix web test -- --watch=false --include src/app/features/admin/admin-log-filter-chips.spec.ts --include src/app/features/admin/admin-log-filter-dialog.spec.ts`; `npm --prefix web run e2e -- admin-logs.e2e.spec.ts` — rot. **Umsetzen**, **Lauf** grün.
- [ ] **Commit:** genannte Dateien — `feat(web): add saved log filter chips`

### B17 — Live ohne Sprünge, Pause nach Fehler, Audit (T4, B8) (`opus`)

**Files:**
- Modify: `web/src/app/features/admin/admin-logs-page.ts` (+ `.spec.ts`), `admin-log-list.ts`, `web/e2e/admin-logs.e2e.spec.ts`, `web/e2e/audit/ui-audit.audit.ts` (Route `/admin/logs`, `afterLoad` mit geöffnetem Facetten-Sheet und aufgeklappter Zeile; geänderte Monitoring-Seite folgt in B18)

**Interfaces:**
- Consumes: `LogReconcileScheduler`, `planReconcile`, `mergeLogEntries`, `reconcileFromUtc` (B13); `liveEvents(ADMIN_LIVE_URL, [LIVE_EVENT_TYPES.logsAppended])` (`core/live/live-reload.ts`); `pollWhileVisible`.
- Verhalten §8.3/§9.4: Auslöser `logs.appended`, Tab wieder sichtbar, Stream (wieder) offen, alle 30 s — nur sichtbar, nur bei relativem `range`, nur ungepaust; Summary ≤ 1 je 30 s.
  „Oben" = erste Zeile sichtbar unter den Sticky-Leisten **und** keine Zeile aufgeklappt → einfügen an sortierter Position, ~2 s Akzent-Wash (bei `prefers-reduced-motion` ohne Animation), eine höfliche Live-Meldung je Nachladen („3 neue Einträge").
  Sonst nichts einfügen, zählen, „n neue ↑" im reservierten Platz; Klick scrollt hoch und fügt ein. Jede Nicht-2xx-Antwort eines automatischen Abrufs → `pause()`, Live-Status „pausiert", „Live pausiert nach Fehler — Erneut versuchen"; erst ein erfolgreicher Nutzerabruf `resume()`.
  `reload` aus dem Planer → „Zu viele neue Einträge — Liste neu laden". Absoluter Bereich: kein Nachladen, Hinweis sagt das. Stream geschlossen → Status wie Monitoring-Seite + „Neu laden", kein Polling-Fallback.

- [ ] **Tests (rot):** Vitest: Entscheidung „oben vs. gelesen" inkl. aufgeklappter Zeile; eine Live-Meldung je Nachladen; nach Fehlerantwort lösen weitere Ereignisse keine Anfrage aus, bis ein manueller Abruf gelingt.
  E2E (`installLiveStub`/`__emitLive`, `page.clock.install()` vor `goto`, `runFor`): `logs.appended` oben fügt ein und hebt hervor; nach Scrollen „n neue" ohne Verschiebung (Ankerelement-Position vor/nach gleich, AK 12); verspäteter Eintrag mitten in einer Seite erscheint beim nächsten Abgleich an der richtigen Stelle (R2);
  leere „Alle Fehler"-Liste: erster Eintrag ohne Hinweis nach ≤ 30 s, mit Hinweis sofort (R7/R3, AK 23); „Zu viele neue Einträge" bei `truncated`; Fehlerantwort pausiert, weitere `__emitLive` und Takte erzeugen keine Requests (S1, AK 20).
- [ ] **Lauf:** `npm --prefix web test -- --watch=false --include src/app/features/admin/admin-logs-page.spec.ts`; `npm --prefix web run e2e -- admin-logs.e2e.spec.ts` — rot. **Umsetzen**, **Lauf** grün.
- [ ] **Audit:** UI-Audit-Harness (Designsprache §12) für `/admin/logs`, beide Themes, beide Viewports, Axe WCAG AA ohne Befund.
- [ ] **Commit:** genannte Dateien — `feat(web): keep the admin logs list live without layout jumps`

### B18 — Zeile „Fehler der letzten 24 h" auf `/admin/monitoring` (T5, §11) (`sonnet`)

**Files:**
- Create: `web/src/app/features/admin/admin-log-errors-row.ts` (+ `.spec.ts`)
- Modify: `web/src/app/features/admin/admin-monitoring-page.ts:101-127` (Zeile direkt unter dem Seitenkopf, vor `<app-admin-load-section />`; Refresh am bestehenden `pollWhileVisible(DASHBOARD_POLL_INTERVAL_MS)`, kein `logs.appended`-Abo, A29),
  `web/public/i18n/de.json`, `en.json`, `web/e2e/admin-monitoring.e2e.spec.ts`, `web/e2e/support/mocks.ts`, `web/e2e/audit/ui-audit.audit.ts`

**Interfaces:**
- Consumes: `AdminLogsService.getStatus()` (B13).
- Verhalten: vollständig nur bei beiden `healthy` **und** `coverage24h.complete`; dann n = 0 neutral „keine", n > 0 `StatusBadge tone="danger"`; sonst `warning` „mindestens n" bzw. „0 erfasst, unvollständig" plus Grund in derselben Zeile
  („Log-Erfassung worker steht seit {hh:mm} UTC", „… abgeschaltet", „… keine Statusmeldung", „Erfassung von {source} unvollständig seit {Zeit}"); `overdue` → „Log-Löschung überfällig". „keine" erscheint nie ohne Vollständigkeit.
  Link „Ansehen →" exakt `/admin/logs?q=level%3E%3Derror&range=24h`. Laden: Skeleton in Zeilenhöhe; Fehler: „nicht verfügbar", übrige Bereiche unabhängig.

- [ ] **Tests (rot):** Vitest: `missing`/`disabled`/`stale` und `complete = false` → „mindestens n" bzw. „0 erfasst, unvollständig", nie „keine"; vollständig mit 0 → „keine", mit 3 → Danger-Badge; Link-Ziel. E2E: `missing` zeigt „unvollständig" (S9); die Zeile verlinkt auf den Seed und der Chip „Alle Fehler" ist dort aktiv (AK 14 Teil 2).
- [ ] **Lauf:** `npm --prefix web test -- --watch=false --include src/app/features/admin/admin-log-errors-row.spec.ts`; `npm --prefix web run e2e -- admin-monitoring.e2e.spec.ts` — rot. **Umsetzen**, **Lauf** grün; Audit-Harness für `/admin/monitoring` (beide Themes/Viewports).
- [ ] **Commit:** genannte Dateien — `feat(web): show recorded errors of the last 24 hours on the monitoring page`

### B19 — Doku: Operations „Log viewer", Architektur, CLAUDE.md, Datenschutz-Hinweis (T5) (`sonnet`)

**Files:**
- Modify: `docs/Operations.md` (neuer Abschnitt „Log viewer": was erfasst wird und was nicht, 7 Tage + ~1 h, `LogCapture:*`, `LOG_CAPTURE_ENABLED`, Budget und Drops, `stale`/`disabled`/`missing`/`overdue`, `docker logs` als Quelle bei „capture stale", Abschalten als Rollback, Rollback-Ablauf §18 mit `TRUNCATE` vor alten Images;
  „Legal pages": Datenschutzerklärung muss die Log-Erfassung nennen), `docs/Architectur.md` (Absatz zur Log-Erfassung neben dem SSE-Fluss, `:60`), `CLAUDE.md` (Admin-Zeile in „Umsetzungsstand")

- [ ] Inhalte gegen den Code abgleichen (Konfigschlüssel, Defaults, Zustände), keine Vorgriffe auf nicht Gebautes.
- [ ] **Commit:** `git add docs/Operations.md docs/Architectur.md CLAUDE.md` — `docs: document the admin log viewer`

### B20 — Gesamt-Gates, Live-Verifikation, PR, Betreiber-Übergabe (`sonnet`)

- [ ] **Gates (§13.4):** `dotnet build EmotePurge.slnx` (0 Warnungen), `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (**ohne** Api auf `:5151`), `node scripts/coverage-local.mjs` (≥ 80 % auf neuem Code, Näherung),
  `dotnet format EmotePurge.slnx --verify-no-changes`, `npm --prefix web run lint`, `npm --prefix web run format:check`. Rote E2E unter Speicherdruck: Suite allein wiederholen.
- [ ] **Live (§15, lokal, `docker compose up -d --build`, echtes Twitch/7TV):** AK 2, 5, 6, 10, 12, 14 von Hand; `docker compose restart worker` → „Start nach Deploy" zeigt die Boot-Recovery der neuen Instanz, keine Zeile der alten (AK 10);
  `docker compose stop redis` 1 min bei offener Seite: Einträge werden gespeichert, ein Eintrag aus dem Ausfall erscheint ≤ 30 s später ohne Neuladen (AK 12, R3); AK 14 mit provoziertem Error (7TV-Sync gegen erfundenen Kanal): Zeile ≤ 91 s ab Log-Aufruf „Fehler der letzten 24 h: 1",
  `LogCapture__Enabled=false` nur am Worker → „Log-Erfassung worker steht seit …" frühestens 5 min, spätestens 5 min + 90 s nach dem letzten Herzschlag; AK 20 mit `LogViewer__QueryTimeoutMilliseconds=1` an der Api: genau eine Fehlerantwort, danach 2 min keine Anfrage, kein `logs.appended`, kein `Microsoft.EntityFrameworkCore.*` in `LogEntries`;
  AK 25 (Statuszeilen leeren bei gestopptem Worker → „worker: keine Statusmeldung"; Budget-Drop; Restore 24 h unvollständig; Deploy mit sauberer Übergabe bleibt vollständig); AK 22: `docker compose restart postgres` unter Last → keine doppelte `CaptureId`;
  AK 26: Rollback-Ablauf §18 einmal lokal (Erfassung aus, `TRUNCATE "LogEntries", "LogCaptureStatus";`, dann altes Image); AK 27: Splice-Zeile in Console und `LogEntries` nur mit erlaubten Werten (falls live auftretend); AK 13: Filter auf zweitem Gerät (LAN-Weg) gleich, gelöschter Seed bleibt nach Neustart weg; AK 16 am Gerät.
- [ ] Push `feat/admin-log-viewer`, PR (englisch, Performance-Protokoll aus B12, Live-Ergebnisse, Attributionszeile). **Orchestrator:** Codex-Zweitmeinung `/codex:review --model gpt-6.1-sol` über den Branch, dem Nutzer vorlegen (Regel 22); Merge macht der Nutzer.
- [ ] **Betreiber-Übergabe** (vorbereitet, nie von der Session ausgeführt; jede Stufe ist Voraussetzung der nächsten, §16):
  1. **Migration vor den Images.** Tunnel `ssh -N -L 15432:127.0.0.1:5433 vps`, dann in zweiter Shell
     `dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'`
     (**alle** `(Pending)` prüfen — nur `AddLogCapture` erwartet), dann `dotnet ef database update …` mit derselben `--connection`, dann erneut `list`.
  2. **Images mit `LOG_CAPTURE_ENABLED=false`** in der Prod-`.env`; Stack aktualisieren; `/admin/monitoring` zeigt „abgeschaltet" für beide Quellen.
  3. **Host-Backup-Skript:** neue Fassung nach `<HOST_BACKUP_SCRIPT>` (Pfad in `infra-docs`); `sha256sum <HOST_BACKUP_SCRIPT>` gegen `sha256sum scripts/backup-postgres.sh` des gemergten Stands; Skript einmal von Hand;
     `zcat <DUMP> | grep -c '^COPY public."LogEntries"'` → `0`, `zcat <DUMP> | grep -c '^COPY public."LogCaptureStatus"'` → `0`, `zcat <DUMP> | grep -c 'CREATE TABLE public."LogEntries"'` → `1`.
  4. **Datenschutztext** aus Spec §12 (de maßgeblich, en) in `privacy.de.md`/`privacy.en.md` unter `Legal:ContentPath` eintragen.
  5. **Einschalten:** `LOG_CAPTURE_ENABLED=true`, Container neu erstellen; Prüfung: „Start nach Deploy" gegen `sudo docker logs --since 15m <API_CONTAINER>` bzw. `<WORKER_CONTAINER>`, Monitoring-Zeile vollständig.
  Fehlt Stufe 3 oder 4, bleibt die Erfassung aus. **Rollback** (§18): `LOG_CAPTURE_ENABLED=false` + neu erstellen → über den Tunnel `psql "host=localhost port=15432 dbname=emotepurge user=emotepurge password=<PROD-PW>" -c 'TRUNCATE "LogEntries", "LogCaptureStatus";'`
  (oder `dotnet ef database update <Migration vor AddLogCapture laut list> … --connection '…<PROD-PW>'`) → erst dann alte Images. Nach dem ersten Prod-Tag: Kategorie-Facette des Seeds „Twitch-Reconnects" prüfen (§21).

---

## Lücken und Widersprüche der Spec (beim Planen gefunden, nicht still gelöst)

Plan-Annahmen sind im Text mit „L<n>" markiert; der Orchestrator bestätigt oder entscheidet um.

- **L1** `summary.dropped` (§8.1) nennt kein `rejected`, obwohl `rejected` Drop-Grund und Property ist (§5.3, R6, AK 28); das Banner (§9.7) nennt weder `capture_failed` noch `rejected`. Plan: `dropped.rejected` ergänzt, Banner listet alle Gründe > 0.
- **L2** §8.3 schickt den Abgleich mit `from` **ohne** `to`; §8.2 erklärt gemischte Angaben für `400 invalid_log_range`. Plan: nur im `keysOnly`-Modus ist `to` optional (= Anfragezeit), Spanne- und Zukunftsgrenze gelten weiter.
- **L3** Bei offener `7d`-Seite wandert das eingefrorene `from_L` nach ~1 Tag über die 8-Tage-Spanne; der Abgleich bekäme `400` und die Seite pausierte (S1) — die Spec sagt dazu nichts. Plan: Client erkennt das vorab und zeigt „Zu viele neue Einträge — Liste neu laden" (Review Focus 3).
- **L4** Der Seed-Kategorietest soll in Infrastructure.Tests „im Code existieren" prüfen; das Projekt referenziert den Worker nicht. Plan: Quelltext-Scan über `src/` wie `AdminDashboardQueryServiceStructureTests`.
- **L5** §13.1 verortet den Stream-Vertragstest in Api.Tests; der bestehende `LiveEventTests` liegt in `tests/EmotePurge.Infrastructure.Tests/Unit/` (wie schon bei #381). Plan: dort erweitert.
- **L6** §5.1 skizziert `AddLogCapture(LogSource source)`; `LogCapture:Enabled` entscheidet aber die Registrierung, also braucht die Methode `IConfiguration`, und `AddWorkerHostedServices` bekommt dafür einen `IConfiguration`-Parameter.
- **L7** AK 20 verlangt ein „künstlich kurzes Abfragelimit (Testkonfiguration)", §8.4 legt 5 s fest. Plan: `LogViewer:QueryTimeoutMilliseconds` (Default 5000), nur als Test-Schalter dokumentiert, nicht in Compose.
- **L8** Ungültiger Cursor, ungültige `ids`-Token und `limit` außerhalb 1–200 sind nicht spezifiziert. Plan: `limit` klemmen (Hausstil `PagingQuery`), kaputter Cursor bzw. `ids`-Token → `400 invalid_log_range`.
- **L9** Der Sink (Infrastructure) soll den Originaltyp aus dem Wrapper-Ersatz (Worker) lesen; Infrastructure kann Worker-Typen nicht sehen. Plan: `WithheldMessageException` liegt in Core (`EmotePurge.Core/Logging/`), schon in T0.
- **L10** Ob `ids=` den Filter `q` anwendet, ist offen. Plan: nein — die Ids stammen aus der gefilterten Schlüsselabfrage.
- **L11** A10 nennt die Zustellkategorie `EmotePurge.Api.Endpoints.LiveEndpoints`; die statische Klasse loggt heute nicht. Plan: Name bleibt in der Bremse (harmlos), die wirksame Kategorie ist `RedisLiveEventStream`.
- **L12** Das Format von `({reason breakdown})` in der Drop-Message ist nicht gepinnt. Plan: nur Gründe > 0, `reason=n` snake_case, kommagetrennt.
- **L13** Ob ein Eintrag, der nach der Zulassung an der Byte-Grenze scheitert, Budget zurückbekommt, ist offen. Plan: nein (nur der Pufferplatz wird frei).
- **L14** Kosmetik: AK-Nummern 26–28 und DECISIONS-Nummern 1–7 in §17 stehen außer Reihe; der Plan referenziert die Nummern, wie sie in der Spec stehen.
- **L15** `LastIncompleteAtUtc` ist in §6.2 ohne Nullbarkeit; da er beim Start auf den Prozessstart gesetzt wird, ist er im Plan `not null`.

## Selbstprüfung

- **Spec-Abdeckung:** §4/AK 1, 4 (Teil T0), 27 → A1–A4; §5.1–5.6 → B4–B7; §6.1–6.3 → B1; §6.4/AK 8, 26 → B2, B3, B20; AK 9 → B3; §7 → B8, B9; §8.1–8.4 → B9–B11; §9 → B13–B17; §10/AK 15 → B11, B15; §11/AK 14, 25 → B10, B18; §12 → B20 Stufe 4, B19;
  §13.1 → B2–B11; §13.2 → B13–B18; §13.3/AK 17 → B12; §13.4/AK 19 → B20; §15 → B7, B20; §16 → Teil A/B, B20; §17 → Verteilung oben, B19; §18 → B19, B20; AK 2, 3, 5, 6, 18 → B7; AK 7, 21 → B5, B6; AK 10, 11, 13 → B9, B15, B16, B20; AK 12, 20, 23 → B9, B13, B17; AK 22, 28 → B6; AK 24 → B8, B9.
- **Typkonsistenz:** `LogSource`, `LogEntryLevel`, `CapturedLogEntry`, `LogCaptureSuppression`, `ILogCaptureStore`, `LogRangeRequest`, `LogDropTotals.Rejected`, `LIVE_EVENT_TYPES.logsAppended` sind je einmal definiert und in den konsumierenden Tasks gleich benannt.
- **Proportion:** keine Methodenrümpfe; Codeblock nur für die gepinnten Seed-Strings.
