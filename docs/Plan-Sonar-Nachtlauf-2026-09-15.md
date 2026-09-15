# Plan — Sonar-Nachtlauf 2026-09-15: mechanische Befunde in einem unbeaufsichtigten Lauf

**Zweck:** Einen Batch SonarCloud-Befunde beheben, die die Triage vom 2026-09-15 als mechanisch und
verhaltensneutral (K1) oder als eindeutig kleiner Fix (K2, ohne Live-Abnahme) eingestuft hat — in
einem unbeaufsichtigten Nachtlauf (`nacht --durchlauf`), ohne Rückfragen, ohne Push.

**Quelle:** SonarCloud-Projekt `emotepurge_EmotePurge` (Org `emotepurge`, Branch `main`), 218 offene
Befunde, gesammelt und triagiert am 2026-09-15 (`triage-backend.md`, `triage-frontend.md`,
`summary.tsv`). Zeilennummern in diesem Plan sind **Hinweise auf `main` = `3646d5b`** und driften,
sobald frühere Tasks gelandet sind — Datei + Symbol sind bindend, die Zeile nicht.

**Erwartete Wirkung:** 85 Befunde von 218. Der Rest ist bewusst ausgeschlossen (Abschnitt 1) und
wird tagsüber oder in SonarCloud selbst erledigt.

**Basis:** Der Worktree entsteht aus `main` = `3646d5b` (Merge von PR #191). Der Branch heißt, was
der Nachtlauf-Apparat vergibt (`nacht/<datum>-<kurzform>`). Am Ende liegen **lokale Commits** auf
diesem Branch und die Notizdatei des Apparats — sonst nichts.

**Der Plan enthält keinen Code.** Jeder Task ist Absicht, Vertrag, Ort, Grenzfall und Abnahme; jeder
Task ist genau ein Conventional Commit mit englischer Message und **ohne Issue-/Ticketnummern** in
Git-Metadaten. Der Lauf liest vor der Arbeit `CLAUDE.md` (Regeln 2, 7, 11, 12, 16, 18, 19; Abschnitte
„Tests" und „Sprache") und `web/.claude/CLAUDE.md` (Member-Reihenfolge).

---

## 1. Ausdrücklich **nicht** im Umfang

Der Lauf fasst nichts davon an — auch nicht „nebenbei", auch nicht, wenn es in derselben Datei
neben einem Task-Befund steht:

| Was | Warum nicht |
|---|---|
| **Alle K3-Refactorings**: `csharpsquid:S3776` (23), `csharpsquid:S107` (12, auch die 5 echten in `HarnessRunner`/`ReplayFidelityCalculator`), `javascript:S3776` (4), die 4 Produktions-`typescript:S3358` (`worker-health.service.ts`, `theme.service.ts`, `create-vote-session-dialog.ts:245`, `vote-session-list-page.ts:164` — inkl. der doppelten audience→roles-Abbildung) | Beurteilung nötig; Harness-Teile erst nach der bindenden Messung am 08.10. |
| **OAuth-State-Cookie** `csharpsquid:S2092` (`AuthEndpoints.cs:32`) | braucht echten Twitch-Login auf localhost/LAN/Prod (Regel 16) |
| **`switch`-Default in `LiveEndpoints.cs`** (`csharpsquid:S3928` + `external_roslyn:CA2208`, ~Z. 193) | der Default ist **erreichbar**: `LiveEventSubscribeResult` ist ein öffentlicher positionaler `record` (`ILiveEventStream.cs`), und `Failed(status)` prüft den Enum-Wert nicht — jede `ILiveEventStream`-Implementierung kann einen undefinierten Status liefern. Ein `UnreachableException` wäre dort eine falsche Behauptung; die richtige Form (Validierung in der Factory oder Enum-Prüfung im Handler) ist eine Tagesentscheidung |
| **`authHeaders` und der Regel-Key-Comparator in `scripts/sonar-to-sarif.mjs`** (`javascript:S4624` ~Z. 73, `javascript:S3358` ~Z. 471) | keiner der beiden Codepfade wird von den F5-Golden-Läufen erreicht (der Lauf ohne Umgebungsvariablen endet in `readEnv()` davor), die Funktionen sind nicht exportiert, und Helfer nur für einen Nachtlauf-Check zu exportieren ist nicht vorgesehen — tagsüber mit Token-Lauf |
| **Rückwärtslauf-anfällige Regexe** `javascript:S8786` ×2 (`scripts/sonar-to-sarif.mjs` `htmlToPlainText`, `<[^>]+>` ~Z. 247 und `[ \t]+\n` ~Z. 253) | ein anderes Muster braucht einen **echten** `htmlDesc`-Korpus als Golden-Referenz, und den liefert SonarCloud nur mit Token (`rules/show` und `rules/search` geben ohne Token kein `htmlDesc`/`mdDesc`/`descriptionSections`; das Skript dokumentiert das selbst) — tagsüber. F5 lässt die beiden **Muster unverändert** (höchstens der Aufruf wechselt in der Kette auf `replaceAll`) |
| **Dockerfile** `docker:S6505` (`npm ci --ignore-scripts`) + `docker:S7031` (RUN-Zusammenlegung) | tagsüber: braucht einen Container-Healthcheck; der Lauf darf **kein** `docker compose`, `docker run`, `docker exec` aufrufen (feste `container_name`s — ein `up`/`down` aus dem Worktree ersetzt den Dev-Stack des Betreibers) |
| **Regel-/Workflow-Filter und `.editorconfig`**: `csharpsquid:S2325`, `external_roslyn:CA1861` (6, Tests), `Web:S6819` (6), Prototyp-Ausschluss (`docs/superpowers/prototypes/**`, 7 JS-Befunde) | macht ein eigener Branch gerade; der Lauf ändert **nie** `.github/workflows/sonarcloud.yml`, `.editorconfig` oder `docs/DECISIONS.md` |
| **Alle K4-Befunde** (FP/Accepted): `S125` ×12, `S3265` ×4, `S5034`, `S6966` ×5 (`LiveEndpoints` 334/371/398, `HardenedForeignEmoteSetService:79`, `SevenTvEventClient:152`), `S6667` ×6, `S1075` ×3, `S3267` ×3 (`SevenTvLeaderboardService:128`, `RosterPrunePolicy:47`, `ReplayFidelityCalculator:504`), `S6444` ×2, `S127`, `ASP0018`, `S2699` ×2, `S7747` ×3, `Web:S6853`, `S2925` ×7, `typescript:S7755` ×5 (`usage-bands.ts` 66/67/184, `atlas-grid.ts:106`, `list-selection.ts:113`), `S2094`, `S3358` in `ui-audit.audit.ts` ×3 und `check-color-tokens.mjs:140` | werden per Skript **in SonarCloud** markiert — nicht durch Codeänderung, nicht durch `// NOSONAR` |
| Die Randnotiz „Label um `app-datetime-picker`" | kein Sonar-Befund, Browser-Check tagsüber |

---

## 2. Was der Lauf nicht kann und nicht beweisen kann

- **Gesperrt:** `git push`, PR, `gh`-Schreibzugriffe, CI, SonarCloud-Reanalyse, Deploy, Codex-Reviews,
  jede `docker`-Nutzung, `curl`, `ssh`. Das ist kein Scheitern — der Lauf endet mit lokalen
  Commits und der Notizdatei. Ob SonarCloud die Befunde wirklich schließt, prüft der Betreiber nach
  dem Merge anhand der Tabelle in Abschnitt 7.
- **Kein Netzzugriff, in keiner Form.** Nicht per `curl`, nicht per Node-`fetch`/`http` in einem
  Scratch-Skript, nicht per Interpreter — die Schranke des Apparats verbietet es. Es gibt **keine**
  Offline-Eingaben von außen; alles, was ein Task zum Vergleichen braucht, erzeugt der Lauf selbst
  im Scratch-Verzeichnis (F5: synthetischer Korpus). Ein Schritt, der scheinbar Netz braucht, ist
  **überspringen und melden**.
- **Werkzeuggrenzen der Sandbox (verifiziert):** kein `ss`/`lsof`/`ps`, kein `cmp`/`sha256sum`,
  kein `bash -c`, keine `VAR=wert befehl`-Präfixe, keine `cd`-Ketten. `npx`/`npm exec` nur für
  `ng`, `playwright`, `tsc`, `eslint`, `prettier`. Alles andere über `npm --prefix web run <script>`,
  `dotnet … EmotePurge.slnx` bzw. `--project`, absolute Pfade.
- **Byte-Vergleiche** laufen über `diff` auf Dateien im Scratch-Verzeichnis
  `/tmp/nacht-sonar-2026-09-15/` (Abschnitt 3). Kein Hash-Tool.
- **Ports kann der Lauf nicht prüfen.** Der Betreiber garantiert, dass nachts **nichts auf `:5151`
  und nichts auf `:4300`** lauscht. Fällt die E2E-Suite breit mit „element not found" quer über
  Dateien, die der Task nicht berührt hat (rund die Hälfte der Fälle rot, Laufzeit ein Vielfaches
  von ~1,5 min), ist das die `:5151`-Falle oder Speicherdruck: **einmal die Suite allein
  wiederholen**; bleibt das Bild, in der Notizdatei melden, **keine Tests „reparieren"**, Task als
  „Gate nicht beweisbar" führen und den Commit trotzdem setzen, wenn Vitest, Lint und Format grün sind.
- **Ein fehlendes Werkzeug ist ein Blocker** (melden), kein Anlass, es zusammenzubauen.
- **Kein Fragen möglich.** Jede Unklarheit löst dieser Plan auf; wo er es nicht tut, gilt:
  **überspringen und melden**, nicht improvisieren. Ein Befund, der sich nicht wie beschrieben
  beheben lässt (Test rot, Warnung neu, Narrowing bricht), wird an dieser Stelle **zurückgesetzt**
  — nach dem Verfahren in Abschnitt 3 („Stellenweise arbeiten"), der Rest des Tasks bleibt — und in
  der Notizdatei mit Regel-Key + Ort + Grund geführt.
- **Testcontainers laufen offline — geprüfte Vorbedingung (Betreiber, 2026-09-15):** die Fixtures
  in `tests/EmotePurge.Infrastructure.Tests/Fixtures/` pinnen `postgres:16-alpine` und
  `redis:7.2-alpine` (Testcontainers 4.15.0, keine `PullPolicy`-Änderung, also Vorgabe „nur ziehen,
  wenn nicht vorhanden"); diese beiden Images und `testcontainers/ryuk:0.14.0` liegen lokal, der
  Docker-Daemon ist erreichbar. Damit braucht `dotnet test EmotePurge.slnx` kein Netz. Der Lauf
  ändert **weder** Testcode **noch** `PullPolicy`. Fehlt ein Image trotzdem (Fehlerbild: Pull-Versuch
  oder Timeout beim Container-Start in Task 0), ist das ein Blocker nach Task-0-Regel — nicht
  nachziehen.

---

## 3. Globale Regeln für jeden Task

- **Ein Task = ein Commit.** Vor jedem Commit `git status --porcelain` lesen: es dürfen nur die
  im Task genannten Dateien geändert sein. Stagen **per Pfad** (`git add <datei> …`), nie `-A`,
  nie `.` — das Scratch-Verzeichnis und Coverage-Reports dürfen nie in einen Commit.
- **Scratch:** `/tmp/nacht-sonar-2026-09-15/` anlegen (Task 0), Vorher/Nachher-Dateien und
  Scratch-Skripte dort ablegen, Vergleich mit `diff -u`; **im Abschluss löschen**. Das Verzeichnis
  liegt außerhalb des Worktrees und taucht in `git status` nie auf — die Pfad-Stage-Regel oben gilt
  trotzdem.
- **Kein Netz** (Abschnitt 2): auch Scratch-Skripte öffnen keine Verbindung.
- **Stellenweise arbeiten, Rücknahme deterministisch.** Ein Task besteht aus mehreren Stellen
  (Datei + Symbol). Der Lauf bearbeitet sie **nacheinander**: eine Stelle ändern, ihre lokale Prüfung
  fahren (Build bzw. das im Task genannte Test-Teilset oder der Golden-Diff), und **sofort danach
  `git add <datei>`** — die Stelle ist damit als „gut" eingefroren. Schlägt die Prüfung einer Stelle
  fehl, wird sie mit `git checkout -- <datei>` zurückgenommen: das verwirft **nur** den ungestageten
  Stand seit der letzten guten Stelle, nichts Gestagtes. Danach `git diff --cached -- <datei>` lesen
  und bestätigen, dass genau die erwarteten übrigen Änderungen stehen (und `git diff -- <datei>` leer
  ist). Erlaubt sind `git add`, `git checkout -- <pfad>` und `git checkout HEAD -- <pfad>`;
  **nicht** erlaubt sind `git reset --hard`, `git clean`, `git stash`. Diese Regel ist die **einzige**
  Rücknahme-Vorschrift des Plans; wo ein Task „zurücknehmen"/„zurücksetzen" sagt, ist sie gemeint.
- **Gate rot am Task-Ende (Backend wie Frontend):** zuerst die Ausgabe mit der Basislinie aus
  Task 0 vergleichen (dieselben Tests rot mit derselben Meldung?). Ist der Fehler **schon in der
  Basislinie**, gehört er nicht dem Task: notieren, Commit setzen, weiter. Ist er neu, den
  **ganzen Task** zurücknehmen — `git checkout HEAD -- <jede Datei des Tasks>` (setzt Index und
  Arbeitsbaum auf `HEAD`), `git status --porcelain` muss danach leer sein — in der Notizdatei mit
  Regel-Keys, Orten und der Fehlermeldung führen und mit dem **nächsten Task** fortfahren. Kein
  Debuggen in fremdem Code, kein zweiter Versuch mit anderer Form.
- **Sprache:** Bezeichner, neue Kommentare, Log-/Throw-Messages englisch. Bestehende deutsche
  Texte, die ein Task **nicht** ersetzt, bleiben stehen (kein Übersetzen nebenbei).
- **Formatierung ist Werkzeugsache** (Regel 18): vor jedem Backend-Commit `dotnet format
  EmotePurge.slnx` und danach `dotnet format EmotePurge.slnx --verify-no-changes`; vor jedem
  `web/`-Commit `npm --prefix web run format` (schreibt) und `npm --prefix web run format:check`
  (prüft) sowie `npm --prefix web run lint`. Die Wurzel-Skripte `scripts/*.mjs` liegen **außerhalb**
  von Prettier — dort den vorhandenen Stil der Datei (doppelte Anführungszeichen, Semikolons) halten.
- **Warnungs-Basislinie:** `dotnet build EmotePurge.slnx --no-incremental` (inkrementell versteckt
  Warnungen). Task 0 hält die Zahl der `warning`-Zeilen fest; kein Backend-Task darf sie erhöhen.
- **Member-Reihenfolge** (Regel 19 / `web/.claude/CLAUDE.md`): neue `const`s in C# an den Anfang der
  Klasse (`const`/`static readonly` zuerst), neue `private static` Helfer ans Ende; in TypeScript
  Modul-`const`s und Helfer außerhalb der Klasse oben bzw. als Modulfunktion.
- **Gates je Task** stehen im Task. Repo-„fertig" ist: `dotnet test EmotePurge.slnx` (Docker läuft
  für Testcontainers — das ist erlaubt, es ist kein `docker`-Aufruf des Laufs),
  `npm --prefix web test -- --watch=false`, bei `web/`-Berührung `npm --prefix web run e2e`, plus
  Format/Lint wie oben. `dotnet build` allein ist keine Fertigmeldung.
- **Reihenfolge ist bindend:** B1 → B2 → B3 → B4 → B5 → B6 → F1 → F2 → F3 → F4 → F5 → F7 →
  Abschluss (ein Task F6 existiert nicht — S8786 ist ausgeschlossen, Abschnitt 1; die Nummer F7
  bleibt, damit Querverweise stimmen). Gründe: B3 und B4 ändern beide `HarnessRunner.BuildMarkdown`
  (byte-genaue Markdown-Tests wachen darüber), B4 ändert `ReplayFidelityCalculator` vor jedem
  späteren K3-Umbau; F1 ändert `emote-usage-filter.ts` Zeilen 18–20 als **eine** Kette in einem Zug.

---

## 4. Task 0: Basislinie (kein Commit)

- [ ] `/tmp/nacht-sonar-2026-09-15/` anlegen.
- [ ] `dotnet build EmotePurge.slnx --no-incremental` → Ausgabe nach `/tmp/nacht-sonar-2026-09-15/build-baseline.txt`;
      Zahl der `warning`-Zeilen notieren (erwartet: 0).
- [ ] `dotnet test EmotePurge.slnx` grün (Basiszahl der Tests notieren, zuletzt ~1025); volle
      Ausgabe nach `/tmp/nacht-sonar-2026-09-15/test-baseline.txt` (Vergleichsgrundlage für die
      Gate-rot-Regel in Abschnitt 3).
- [ ] `npm --prefix web test -- --watch=false` grün, `npm --prefix web run lint` und
      `npm --prefix web run format:check` sauber; Ausgaben ebenfalls ins Scratch-Verzeichnis.
- [ ] `npm --prefix web run e2e`; Laufzeit notieren (~1,5 min ist normal); Ausgabe ins
      Scratch-Verzeichnis.
- [ ] `git rev-parse HEAD` = `3646d5b…` bestätigen; sonst Blocker melden und abbrechen.
- [ ] **Stopp-Regel:** Ist eines der Gates oben nicht grün — `dotnet test` (Testcontainers), Vitest,
      Lint, Format oder der Build mit Warnungen ≠ 0 — **endet der Lauf hier**, vor der ersten
      Änderung und vor dem ersten Commit, und meldet die roten Tests/Meldungen wörtlich in der
      Notizdatei. Einzige Ausnahme ist die E2E-Suite: ist **nur** sie rot und zeigt sie das
      Umgebungsbild aus Abschnitt 2 (breit „element not found", Laufzeit ein Vielfaches), einmal
      allein wiederholen; bleibt sie rot, läuft der Lauf mit den Backend-Tasks weiter, die
      Frontend-Tasks führen ihr E2E-Gate dann als „nicht beweisbar" und die Notizdatei nennt es.

---

## 5. Tasks

### Task B1: Unerreichbare `switch`-Defaults werfen keine `ArgumentOutOfRangeException` mehr

**Befunde:** `csharpsquid:S3928` ×4 + `external_roslyn:CA2208` ×4 (dieselben 4 Stellen).
**Dateien:** `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` (zwei Defaults, ~Z. 66 über
`ForeignEmoteSetLookupStatus` und ~Z. 110 über `SevenTvLeaderboardStatus`) ·
`src/EmotePurge.Infrastructure/Services/ForeignEmoteSetService.cs` (`previewResult`-Switch über
`SevenTvPreviewLookupStatus`, ~Z. 123) · `src/EmotePurge.Infrastructure/Services/SevenTvLeaderboardService.cs`
(über `SevenTvEmoteSearchLookupStatus`, ~Z. 233).
**Nicht dabei:** `LiveEndpoints.cs` ~Z. 193 — dort ist der Default erreichbar (Abschnitt 1).

**Absicht:** `nameof(result)` bzw. `nameof(previewResult)` benennt eine lokale Variable, keinen
Parameter — der Konstruktor ist falsch benutzt. Die vier Stellen sind **nachweislich** unerreichbar:
die vier Ergebnistypen (`ForeignEmoteSetLookupResult`, `SevenTvLeaderboardResult`,
`SevenTvEmoteSetPreviewResult`, `SevenTvEmoteSearchPageResult`) sind `sealed` mit privatem
Konstruktor, ihre `Failed(...)`-Factories lehnen `Ok` und jeden nicht definierten Wert per
`Enum.IsDefined` ab, und jedes Enum-Mitglied hat einen eigenen Arm im jeweiligen Switch (am
2026-09-15 gegen `3646d5b` geprüft: 8/8, 4/4, 4/4, 3/3). Sie werfen künftig einheitlich
`System.Diagnostics.UnreachableException` mit einer **englischen** Message, die den Enum-Typ und den
erhaltenen Wert nennt (die zwei deutschen „Unbekannter …"-Texte werden dabei ersetzt, die zwei
englischen bleiben inhaltlich).

**Grenzfälle:** Kein Test behauptet an diesen vier Stellen `ArgumentOutOfRangeException` (die
bestehenden `Assert.Throws<ArgumentOutOfRangeException>` zielen auf Factories wie `TwitchUserLookup`,
`SevenTvDeltaResult`, `SevenTvLeaderboardTtlPolicy` — **nicht anfassen**). Andere
`ArgumentOutOfRangeException`-Würfe mit korrektem `nameof(parameter)` — auch die
`Enum.IsDefined`-Prüfungen in den Factories — bleiben unverändert. Fügt ein Task davor dem Enum ein
Mitglied ohne Switch-Arm hinzu, gilt das nicht (kein Task tut das); trotzdem vor dem Umbau je Stelle
kurz gegenprüfen, dass die Arm-Zählung noch stimmt.

- [ ] Vier Stellen umstellen, `using System.Diagnostics;` wo nötig
- [ ] `dotnet build EmotePurge.slnx --no-incremental` ohne neue Warnung; `dotnet format`
- [ ] `dotnet test EmotePurge.slnx` grün
- [ ] Commit: `refactor: throw UnreachableException from closed-enum switch defaults`

---

### Task B2: Redundantes `!` auf `RedisValue` entfernen

**Befunde:** `csharpsquid:S8969` ×7.
**Dateien:** `src/EmotePurge.Infrastructure/Redis/ModRoleCache.cs` (~Z. 31) ·
`Redis/RateLimitTelemetryStore.cs` (`Deserialize<T>`, ~Z. 374) · `Redis/TwitchLiveStatusStore.cs`
(~Z. 36) · `Redis/WorkerHealthReader.cs` (~Z. 38 und ~Z. 66) ·
`Services/ModeratedChannelsProvider.cs` (~Z. 113) · `SevenTv/ForeignEmoteSetCache.cs` (~Z. 37).

**Absicht:** `(string)value!` parst als `(string)(value!)` — das `!` auf dem Struct `RedisValue`
bewirkt nichts. Jede Stelle prüft direkt davor `IsNullOrEmpty`. Das `!` wird entfernt.

**Vertrag:** Der Build darf **keine** neue Nullable-Warnung (CS86xx) bekommen. Meldet
`--no-incremental` an einer Stelle CS8600/CS8604 (StackExchange.Redis 3.1.31 annotiert den
expliziten Operator als `string?`), wird an genau dieser Stelle `value.ToString()` verwendet — **nicht**
das `!` wieder angehängt. Beide Formen liefern nach der `IsNullOrEmpty`-Prüfung denselben String.

- [ ] Sieben Stellen; `dotnet build EmotePurge.slnx --no-incremental`, Warnungszahl = Basislinie
- [ ] `dotnet format`; `dotnet test EmotePurge.slnx` grün
- [ ] Commit: `refactor: drop null-forgiving operators on RedisValue reads`

---

### Task B3: Wiederholte String-Literale als Konstanten

**Befunde:** `csharpsquid:S1192` ×7.
**Dateien und Verträge:**

| Datei | Literal | Form |
|---|---|---|
| `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs` (`RateLimitPolicyDescriptors`, ~Z. 461) | `"twitch-user"` ×5 | `private const string PerUserPartition`; das Literal `"twitch-user+vote-session"` bleibt eigenständig |
| `src/EmotePurge.Api/RateLimiting/RateLimitRejection.cs` (`OnRejectedAsync`, ~Z. 169) | `"unknown"` ×4 | `private const string Unknown` (oder sprechender) |
| `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs` (~Z. 29/109/176/241) | Guard-Message `'from' must be less than or equal to 'to'.` ×4 | `private const string` für die Message; die vier `throw new ArgumentException(<const>, nameof(from))` bleiben an Ort und Stelle — Message-Text und `paramName` unverändert |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs` (~Z. 137) | `"TWITCH"` ×5 | `private const string TwitchPlatform` |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvDispatchParser.cs` (~Z. 30) | JSON-Key `"value"` ×5 | `private const string ValueKey` neben den `HasKey`-Helfern; **nur** das Literal ersetzen, die S3776-Methode `ParseUserSetChange` (~Z. 57) strukturell nicht anfassen |
| `src/EmotePurge.Infrastructure/Twitch/TwitchHelixClient.cs` (~Z. 21) | `"Bearer"` ×5 | `private const string BearerScheme` |
| `src/EmotePurge.Worker/Harness/HarnessRunner.cs` (~Z. 215, 822, 935, 936, 1023, 1026) | `"yyyy-MM-dd"` ×6 | `private const string IsoDateFormat`, benutzt in `TryParseExact` (~Z. 215) und in `Iso(DateOnly)` (~Z. 822); die Stellen 1023/1026 rufen `Iso(d.Day)` |

**Falle in `HarnessRunner`:** Zeilen ~935/936 haben die Form `X?.ToString("yyyy-MM-dd", …) ??
"keiner (…)"`. Die vorhandene Überladung `Iso(DateOnly?)` liefert für `null` den Text `"none"` —
sie darf dort **nicht** verwendet werden, sonst ändert sich das Markdown. Dort nur den Formatstring
durch die Konstante ersetzen (oder `is { } d ? Iso(d) : "keiner (…)"`), Null-Text unverändert.
`HarnessRunnerTests` vergleichen das Markdown byte-genau und sind das Gate.

- [ ] Sieben Dateien; `dotnet build … --no-incremental` ohne neue Warnung; `dotnet format`
- [ ] `dotnet test EmotePurge.slnx` grün — insbesondere `HarnessRunnerTests`, `SevenTvDispatchParserTests`
- [ ] Commit: `refactor: name repeated string literals as constants`

---

### Task B4: Kleine Syntax-Befunde (Ternäre, `sealed`, Shadowing, LINQ)

**Befunde:** `csharpsquid:S3358` ×4 · `csharpsquid:S3260` ×1 · `csharpsquid:S3218` ×1 ·
`csharpsquid:S3267` ×6.

**Stellen und Verträge:**

- `src/EmotePurge.Infrastructure/SevenTv/SevenTvLeaderboardTtlPolicy.cs` `Clamp` (~Z. 133): zwei
  `if`-Returns in **derselben Reihenfolge** (erst `< minimum`, dann `> maximum`), damit der Fall
  `minimum > maximum` identisch bleibt. Gate: `SevenTvLeaderboardTtlPolicyTests`.
- `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs` `BuildForeignImageUrl` (~Z. 926):
  Dateiname (`4x_static.webp` für animiert, sonst `4x.webp`) in eine lokale Variable, äußere
  Bedingung `emoteId.Length == 0` bleibt. URL-Form ist in Tests behauptet.
- `src/EmotePurge.Worker/Harness/HarnessRunner.cs` `BuildMarkdown` (~Z. 960, Zeile „Gate-tauglich"):
  den Grund-Text in eine lokale Variable ziehen; Zeichenketten byte-gleich (Gate:
  `HarnessRunnerTests`).
- `src/EmotePurge.Worker/UsageCategory.cs` `Resolve` (~Z. 44): `switch`-Ausdruck über
  `(origin, isBot)` mit drei Armen (`Own`+bot → `Bot`, `Own`+human → `Human`, sonst `SharedChat`).
  Der XML-Kommentar (D2, „vierter Zustand nicht darstellbar") bleibt. Gate: die zwei
  `UsageCategory`-Tests.
- `src/EmotePurge.Infrastructure/Redis/RateLimitTelemetryStore.cs` (~Z. 378):
  `private sealed record ProviderRateLimitIncident`.
- `src/EmotePurge.Worker/Harness/ReplayFidelityCalculator.cs` (~Z. 733 und Aufruf ~Z. 705): die
  **Methode** `Spearman(List<PopulationEntry>)` in `SpearmanRho` umbenennen, die Record-Property
  `SubsetSpread.Spearman` (~Z. 878) bleibt. Danach `grep -n "Spearman" ` über `src/` und `tests/`:
  kein `cref="Spearman"` und kein Aufruf des alten Namens darf übrig sein (der Build prüft `cref`
  nicht).
- `S3267`, genau diese sechs: `src/EmotePurge.Infrastructure/Redis/RedisLiveEventStream.cs`
  (~Z. 96 und ~Z. 128, Zählschleifen innerhalb des `lock` → `Count(...)` mit `string.Equals(…,
  StringComparison.Ordinal)`, der `lock` bleibt) · `SevenTvApiClient.cs` (~Z. 751,
  `foreach` mit `Where(entry => entry.Emote is not null && entry.AddedAt is not null)` im Kopf) ·
  `ReplayFidelityCalculator.cs` (~Z. 231, ~Z. 450, ~Z. 459: Filterbedingung in den
  `foreach`-Kopf). **Nicht anfassen:** `ReplayFidelityCalculator.cs:504`,
  `SevenTvLeaderboardService.cs:128`, `RosterPrunePolicy.cs:47` (K4, Abschnitt 1).

- [ ] Alle Stellen; `dotnet build … --no-incremental` ohne neue Warnung; `dotnet format`
- [ ] `dotnet test EmotePurge.slnx` grün
- [ ] Commit: `refactor: flatten nested ternaries and simplify loops flagged by Sonar`

---

### Task B5: Host-Start und `Program`-Deklaration aufräumen

**Befunde:** `csharpsquid:S6966` ×2 (nur `Api/Program.cs` `app.Run()` ~Z. 331 und
`Worker/Program.cs` `host.Run()` in `RunWorkerAsync` ~Z. 44) · `csharpsquid:S1118` +
`external_roslyn:ASP0027` (`Api/Program.cs` ~Z. 337) · `csharpsquid:S8949` + `external_roslyn:CA2016`
(`src/EmotePurge.Worker/TwitchLivePollWorker.cs` ~Z. 138).

**Absicht:**
- `app.Run()` → `await app.RunAsync()`; `host.Run()` → `await host.RunAsync()`. Beide stehen bereits
  in asynchronem Kontext (Top-Level-`await` bzw. `async Task RunWorkerAsync`).
- Die Zeile `public partial class Program;` samt ihrem vierzeiligen Kommentarblock löschen — das
  .NET-10-Web-SDK erzeugt die öffentliche `Program`-Klasse selbst. **Vertrag:**
  `tests/EmotePurge.Api.Tests` (`ApiFactory : WebApplicationFactory<Program>` und die
  `WebApplicationFactory<Program>`-Helfer in den Rate-Limit-/Live-Stream-Tests) müssen unverändert
  kompilieren und grün laufen. Reihenfolge deshalb: erst `app.Run()` → `RunAsync()` ändern, bauen,
  `git add` (Abschnitt 3); **dann** die `Program`-Zeile löschen und `dotnet build EmotePurge.slnx`
  (inkl. Testprojekte) fahren. Kompiliert das nicht, `git checkout -- src/EmotePurge.Api/Program.cs`
  (nimmt nur die ungestagte Löschung zurück), den Rest des Tasks behalten und melden.
- `TwitchLivePollWorker.ExecuteAsync`: das Token `ct` als dritten Parameter an
  `liveStatusWriter.PublishAsync(...)` durchreichen (die Implementierung ignoriert es heute — das
  ist bekannt und nicht Teil dieses Tasks).

**Nicht beweisbar, ausdrücklich:** Ein echter Worker-Start gegen den Dev-Stack unterbleibt (er würde
neben dem laufenden Dev-Worker zählen und die Messung aus Epic #118 verfälschen); ein lokaler
Api-Start unterbleibt (kein `curl`, keine saubere Prozesskontrolle in der Sandbox). `Run()` →
`RunAsync()` ist API-gleich; der Beleg ist Build + Api.Tests + Worker.Tests. Der Betreiber fährt den
Stack nach dem Merge aus dem Haupt-Checkout hoch.

- [ ] Änderungen; `dotnet build … --no-incremental` ohne neue Warnung; `dotnet format`
- [ ] `dotnet test EmotePurge.slnx` grün (Api.Tests sind das Gate für `Program`)
- [ ] Commit: `refactor: await host startup and drop the redundant Program declaration`

---

### Task B6: Test-Hygiene

**Befunde:** `external_roslyn:CA1068` ×2 · `external_roslyn:CA1806` ×1.
**Dateien:** `tests/EmotePurge.Worker.Tests/HarnessRunnerTests.cs` (Helfer `Run(int days,
CancellationToken ct = default, …)` ~Z. 1526 und `Recompute(string, CancellationToken ct = default,
string? channelName = null)` ~Z. 1554) · `tests/EmotePurge.Api.Tests/SevenTvLeaderboardEndpointTests.cs`
(`LoggedInCaller_Gets200_WithSortByEcho`, ~Z. 43).

**Absicht:** `ct` wandert in beiden Helfern ans Ende der Parameterliste; der eine positionale
Aufruf `Run(3, cts.Token)` (~Z. 374) wird auf benanntes Argument umgestellt, weitere findet der
Compiler. `TryParse` bekommt `Assert.True(...)` um sich — macht die Vorbedingung explizit.

- [ ] `dotnet build … --no-incremental` ohne neue Warnung; `dotnet format`
- [ ] `dotnet test EmotePurge.slnx` grün, Testanzahl unverändert
- [ ] Commit: `test: order cancellation tokens last and assert TryParse preconditions`

---

### Task F1: Mechanische Befunde im Produktivcode unter `web/src`

**Befunde:** `typescript:S6582` ×8 · `typescript:S7781` ×3 · `typescript:S7780` ×1 ·
`typescript:S1128` ×1 · `typescript:S7763` ×1 · `typescript:S7737` ×1 · `typescript:S6571` ×2 ·
`typescript:S3358` ×1 (`import-target-options.ts`).

**Die Optional-Chaining-Falle, bindend:** Sonars Quick-Fix `x?.result === null` für
`x === null || x.result === null` ist **falsch** — `undefined !== null`, der Guard würde nicht mehr
greifen. Die Zielform je Stelle steht hier; vor jeder Änderung die Semantik für `null`/`undefined`/
`0`/`''` prüfen. Bricht danach `tsc`s Narrowing hinter dem Guard (z. B. `stash.results`,
`envelope.kind`, `status.occupiedSlots`), wird **diese Stelle** zurückgesetzt (Verfahren Abschnitt 3:
je Stelle `npm --prefix web test -- --watch=false` als lokale Prüfung, dann `git add`) und gemeldet.

| Datei · Symbol | Zielform |
|---|---|
| `core/seven-tv/seven-tv-delete.service.ts` `retrySyncReport` (~Z. 166) · `seven-tv-import.service.ts` (~Z. 256) · `seven-tv-restore.service.ts` (~Z. 199) | `!current?.result` an Stelle der zwei `=== null`-Klauseln; die `doneIds.length === 0`/`doneKeys.length === 0`-Klausel bleibt **ausgeschrieben** (nicht über Falsy-0 falten) |
| `core/voting/vote-session.service.ts` `takeGuardResults` (~Z. 113) | `stash?.channelName !== channelName` (channelName ist nicht-leerer String, also äquivalent); die `sessionId`-Klausel bleibt |
| `features/usage-stats/usage-stats-page.ts` `projectedSlots` (~Z. 821) | `status?.capacity == null` — **lose** Gleichheit, ausdrücklich nicht `=== null` (kein `eqeqeq` im Lint) |
| `shared/export/read-envelope.ts` (~Z. 27) | `envelope?.source !== 'emotepurge'` |
| `shared/seven-tv/delete-confirm-dialog.ts` `hasSharedSetWarning` (~Z. 118) · `import-confirm-dialog.ts` `sharedSetWarning` (~Z. 404) | `!w?.available` bzw. `!warning?.available` |

**Übrige Stellen:**
- `shared/emotes/emote-usage-filter.ts` `globToRegExp` (Z. 18–20, **eine** Kette): das
  Escape-Replacement als `String.raw`-Template (enthält kein `${`), die beiden `/\*/g`- und
  `/\?/g`-Ersetzungen als `replaceAll` mit demselben Regex oder dem Literal. Gate:
  `emote-usage-filter.spec.ts`.
- `shared/export/csv.ts` (~Z. 49): `replaceAll` für das Verdoppeln der Anführungszeichen. Gate: `csv.spec.ts`.
- `features/usage-stats/usage-stats-page.ts` (~Z. 40): Import `VoteSessionSummary` entfernen (einzige Fundstelle in der Datei).
- `core/seven-tv/seven-tv-delete.service.ts` (~Z. 19): `export { RUN_DELAY_MS as DELETE_DELAY_MS } from
  './seven-tv-run-engine'`, `RUN_DELAY_MS` aus der Import-Liste streichen (keine weitere Verwendung
  in der Datei), den Doc-Kommentar behalten. Delete- und Restore-Specs importieren `DELETE_DELAY_MS` weiter.
- `features/voting/vote-session-detail-page.ts` `load` (~Z. 744): destrukturierter Parameter
  `{ freeze = true }` mit Default `{}`, an `loadResults` als `{ freeze }` weitergereicht. Die vier
  Aufrufer (`load()` ×3, `load({ freeze: false })`) bleiben unverändert. **Kein** positionaler Boolean.
- `core/audit/audit.model.ts` (~Z. 50 und ~Z. 66): `AuditDetailKind | (string & {})` bzw.
  `AuditAction | (string & {})` — behält Autovervollständigung und die dokumentierte Absicht; den
  vorhandenen Doc-Kommentar um einen Satz zum Idiom ergänzen (englisch).
- `shared/seven-tv/import-target-options.ts` (~Z. 32): Modulfunktion `compareChannelNames(a, b)`
  mit `if`/`return` über `<`/`>` (Code-Unit-Vergleich). **Nicht** `localeCompare` — das änderte die
  Reihenfolge. Gate: `import-target-options.spec.ts`.

- [ ] `npm --prefix web run format`, `format:check`, `lint`
- [ ] `npm --prefix web test -- --watch=false` grün (Typprüfung inklusive — `ng test`, nicht `npx vitest`)
- [ ] `npm --prefix web run e2e` grün (berührt Vote-Detailseite, Dialoge, Usage-Stats)
- [ ] Commit: `refactor(web): apply Sonar's mechanical fixes without changing guard semantics`

---

### Task F2: `withFetch` entfernen (eigener Commit)

**Befunde:** `typescript:S1874` ×2 (`web/src/app/app.config.ts`, Import ~Z. 14 und Aufruf ~Z. 40).
**Absicht:** `withFetch` ist in Angular 22.1.5 deprecated, `FetchBackend` ist Default. Import und
Aufruf entfernen, `provideHttpClient(withInterceptors([...]))` bleibt. Allein im Commit, damit ein
Revert ein Klick ist.

- [ ] `format:check`, `lint`, `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` grün
- [ ] Commit: `refactor(web): drop the deprecated withFetch call`

---

### Task F3: Spezifischere Assertions in Specs

**Befunde:** `typescript:S5906` ×4 — `core/seven-tv/seven-tv-delete.service.spec.ts` (~Z. 447, 457,
510) · `shared/seven-tv/foreign-emote-grid.spec.ts` (~Z. 753).
**Absicht:** `expect(x.length).toBe(n)` → `expect(x).toHaveLength(n)`. Nur diese vier Stellen.

- [ ] `format:check`, `lint`, `npm --prefix web test -- --watch=false` grün
- [ ] Commit: `test(web): use toHaveLength for length assertions`

---

### Task F4: E2E-Support und Mess-Harness

**Befunde:** `typescript:S4043` ×2 · `typescript:S7755` ×4 · `typescript:S5443` ×1.
**Dateien:** `web/e2e/support/mocks.ts` (~Z. 406, ~Z. 476, ~Z. 672) ·
`web/e2e/atlas-image-loading.measure.ts` (~Z. 53–54, Header-Beispiele ~Z. 33/37, ~Z. 266/271/278).

**Absicht:**
- `mocks.ts` ~Z. 406/476: `pageNumbers.sort(...)` sortiert bei jeder Anfrage ein Closure-Array in
  place. Einmal bei der Definition in eine eigene sortierte Konstante (Kopie + `sort`, **nicht**
  `toSorted` — ES2022-Lib hat es nicht) und die Kette darauf aufsetzen.
- `mocks.ts` ~Z. 672: `days.at(-1)?.date ?? null`. `measure.ts` ~Z. 266/271/278: `.at(-1) ?? -1`
  — S7755 nur dort, wo der `??`-Fallback schon existiert (die fünf K4-Stellen in `web/src` bleiben).
- `measure.ts` ~Z. 53: Default-Ausgabeverzeichnis von `/tmp/webp-measure` auf das repo-lokale,
  gitignorierte `test-results/webp-measure` (relativ zu `web/`, Playwright läuft dort; `/test-results`
  steht in `web/.gitignore`). `EMOTES_FILE` folgt daraus automatisch; die zwei Beispielpfade im
  Header-Kommentar nachziehen. Das Skript legt das Verzeichnis bereits per `mkdirSync(…, { recursive: true })` an.

**Abnahme:** `mocks.ts` lädt jede E2E-Spec → die Suite ist das Gate. `measure.ts` prüft keine
tsconfig; Beleg ist `npm --prefix web run lint` (ESLint parst `e2e/`) plus
`npm --prefix web exec playwright -- test --list --config <abs>/web/playwright.measure.config.ts`,
das **erwartet** mit `No emote fixture at test-results/webp-measure/emotes.json …` abbricht — das
beweist Modul-Auswertung und den neuen Default. Ein echter Messlauf ist nicht möglich (braucht die
DB-Fixture) und wird nicht versucht.

- [ ] `format:check`, `lint`; `npm --prefix web run e2e` grün; Playwright-`--list` mit dem erwarteten Fehler
- [ ] Commit: `refactor(e2e): sort mock pages once and keep measurement output inside the repo`

---

### Task F5: Wurzel-Skripte und `check-color-tokens`

**Befunde:** `javascript:S7781` ×7 · `javascript:S7785` ×2 · `javascript:S7780` ×1
(`check-color-tokens.mjs` ~Z. 36).
**Nicht dabei** (Abschnitt 1): `javascript:S4624` (`authHeaders`, ~Z. 73) und `javascript:S3358`
(Comparator in `main`, ~Z. 471) — ihre Codepfade erreicht kein Golden-Lauf, und sie sind nicht
exportiert.
**Dateien:** `scripts/sonar-to-sarif.mjs` · `scripts/coverage-local.mjs` · `web/scripts/check-color-tokens.mjs`.
Diese Skripte haben **keine Tests**, und die Wurzel-Skripte liegen **außerhalb von ESLint und
Prettier** (`eslint .`/`prettier` laufen in `web/`) — Lint/Format beweisen dort nichts. Die
Abnahme ist allein der Vorher/Nachher-Vergleich, und **jede** geänderte Stelle muss von mindestens
einem der Läufe unten tatsächlich durchlaufen werden (Zuordnung in der Tabelle).

**Vorher-Läufe (vor der ersten Änderung, alles nach `/tmp/nacht-sonar-2026-09-15/`):**
1. `node <abs>/scripts/coverage-local.mjs --help` → `cov-help-before.txt`.
2. Ein voller `node <abs>/scripts/coverage-local.mjs` (erzeugt die Reports; die Ausgabe selbst ist hier
   uninteressant), danach `node … --skip-tests` → `cov-skip-before.txt`.
3. `node <abs>/scripts/sonar-to-sarif.mjs` **ohne** Umgebungsvariablen → erwartet
   `Aborted with error: Missing environment variable: SONAR_PROJECT_KEY` und Exit 1; Ausgabe und
   `echo $?` → `sarif-noenv-before.txt`.
4. `npm --prefix web run lint:colors` → `colors-before.txt`.
5. **Korpus für `htmlToPlainText`:** ein Scratch-Skript `/tmp/nacht-sonar-2026-09-15/html-golden.mjs`,
   das `htmlToPlainText` aus `<abs>/scripts/sonar-to-sarif.mjs` importiert (der Import führt `main`
   nicht aus, `isDirectRun` ist dann falsch) und über einen Korpus die Ausgaben als JSON nach
   `html-before.txt` schreibt. **Kein Netz, keine externe Datei** — der Korpus ist **rein
   synthetisch**: ein festes Set, das der Lauf selbst in das Scratch-Skript schreibt und das die
   Formen echter Sonar-Regelbeschreibungen nachbildet. Pflichtinhalt (jedes Element mindestens
   einmal, gern kombiniert in längeren Fragmenten): `<h2>`/`<h3>`-Überschriften, `<p>`-Absätze,
   `<pre>`/`<code>`-Blöcke mit eingerückten Zeilen, inline `<code>`, verschachtelte `<ul>`/`<ol>`
   mit `<li>`, `<a href="…">` mit Attributen, `<strong>`/`<em>`, die Entitäten `&lt;` `&gt;` `&amp;`
   `&quot;` `&#39;` **und** `&nbsp;` (Letztere ersetzt die Funktion nicht — sie muss unverändert
   durchlaufen), `<br>`/`<br/>`/`<BR />`, CRLF-Zeilenenden, Tabs und Leerzeichenläufe vor `\n`,
   drei und mehr Leerzeilen, führender/nachlaufender Whitespace, ein Tag über mehrere Zeilen,
   `<div>` und `<h1>`–`<h6>`-Schlusstags, leerer String, `null` und `undefined`. Mindestens zwanzig
   Fragmente; die Ausgabe je Fragment als JSON-Array in fester Reihenfolge, damit `diff` Byte für
   Byte vergleicht. Das Skript und der Korpus bleiben im Scratch-Verzeichnis und kommen nie in
   den Worktree.
6. `node <abs>/scripts/coverage-local.mjs --bogus-option` → erwartet
   `Aborted with error: Unknown option: --bogus-option (see --help)` und Exit 1; Ausgabe und
   `echo $?` → `cov-bogus-before.txt` (deckt den Fehlerpfad des zweiten `isDirectRun`-Blocks).
7. **`normalizeToRepoRelativePosix`** (exportiert aus `coverage-local.mjs`): ein Scratch-Skript
   `/tmp/nacht-sonar-2026-09-15/paths-golden.mjs`, das die Funktion importiert und über ein festes
   Eingabeset die Ergebnisse als JSON nach `paths-before.txt` schreibt. Pflichtinhalt: Windows-Pfade
   mit Backslashes (mehrere, führend, gemischt mit `/`), ein absoluter POSIX-Pfad unter dem
   Repo-Root, ein Laufwerkspfad `C:/…` und `C:\…`, `./`-Präfix, Pfade relativ zu `web/`, ein
   Pfad ohne Backslash; `repoRoot` fest als absoluter String.

**Änderungen:**
- `S7781`: `replace(/…/g, …)` → `replaceAll` mit **demselben** Regex (Flag `g` bleibt Pflicht für
  `replaceAll`): `coverage-local.mjs` `normalizeToRepoRelativePosix` (~Z. 476/480, Backslash-Normalisierung),
  `sonar-to-sarif.mjs` `htmlToPlainText` Entitäten (~Z. 248–252). Die zwei S8786-Regexe
  (~Z. 247 `<[^>]+>` und ~Z. 253 `[ \t]+\n`) sind **kein** S7781-Befund und ausgeschlossen
  (Abschnitt 1): Muster **unverändert** lassen; ob sie in der Kette auf `replaceAll` mitgezogen
  werden, ist frei — das Muster selbst wird nicht angerührt.
- `S7785` (beide `isDirectRun`-Blöcke): `main().catch(...)` → Top-Level-`await main()` **innerhalb**
  des bestehenden `if (isDirectRun)`, in `try`/`catch` mit **derselben** Fehlermeldung
  (`Aborted with error: …`) und `process.exitCode = 1`. Ein nacktes `await` ohne `try` änderte
  Exit-Code und Ausgabe.
- `S7780` (`check-color-tokens.mjs` ~Z. 36): das `RegExp`-Quellmuster als `String.raw`-Template —
  jedes `\\` des alten Templates wird ein `\`, die `${PREFIXES}`/`${PALETTES}`-Interpolationen
  bleiben, Flag `'g'` bleibt.
- `authHeaders` und der Comparator in `main` bleiben **unverändert** (Abschnitt 1).

**Welcher Lauf welche Stelle beweist:**

| Stelle | Lauf |
|---|---|
| `coverage-local.mjs` `normalizeToRepoRelativePosix` (S7781 ×2) | 7 (direkt), 2 (indirekt über die Report-Pfade) |
| `coverage-local.mjs` `isDirectRun`-Block (S7785) | 1 (Erfolgspfad), 6 (Fehlerpfad, Exit 1), 2 |
| `sonar-to-sarif.mjs` `htmlToPlainText` Entitäten (S7781 ×5) | 5 |
| `sonar-to-sarif.mjs` `isDirectRun`-Block (S7785) | 3 (Fehlerpfad, Exit 1). Der Erfolgspfad ist ohne Token nicht erreichbar — das ist bekannt und wird in der Notizdatei als „nicht beweisbar" genannt, nicht als Lücke behandelt |
| `check-color-tokens.mjs` `VIOLATION` (S7780) | 4 (das Muster läuft über den ganzen `web/`-Baum) |

**Nachher-Läufe:** dieselben sieben Läufe nach `*-after.txt`; `diff -u` je Paar muss leer sein
(Exit 0). Bei `cov-skip` gilt: gleicher HEAD, gleiche Reports → identisch erwartet. Ist ein Paar
verschieden, die verursachende Stelle nach Abschnitt 3 zurücknehmen und melden.

- [ ] Vorher-Läufe · Änderungen (je Stelle: Golden-Lauf der Stelle, dann `git add`) · Nachher-Läufe · sieben leere Diffs
- [ ] `npm --prefix web run lint` (deckt nur `web/scripts/`)
- [ ] Commit: `chore(scripts): modernize string replacement and top-level await in the helper scripts`

---

### Task F7: ESLint-Konfiguration auf `defineConfig`

**Befunde:** `javascript:S1874` ×1 (`web/eslint.config.mjs`, ~Z. 15).
**Absicht:** `tseslint.config(...)` ist in typescript-eslint 8.69 deprecated. `defineConfig` aus
`eslint/config` (ESLint 10) importieren und **dasselbe** Array unverändert übergeben;
`tseslint.parser` und `tseslint.plugin` bleiben aus `typescript-eslint`. Der Kommentarblock über
der Konfiguration bleibt.

**Beweis:** vor der Änderung `npm --prefix web exec eslint -- --print-config <abs>/web/src/app/app.config.ts`
→ `/tmp/nacht-sonar-2026-09-15/eslint-ts-before.txt` und dasselbe für
`<abs>/web/src/app/features/voting/vote-session-list-page.html` → `eslint-html-before.txt`; nach der
Änderung erneut; beide `diff -u` leer. Zusätzlich `npm --prefix web run lint` vorher und nachher
grün. Ist ein Diff nicht leer, nach Abschnitt 3 zurücknehmen und melden — nicht an der
Konfiguration drehen, bis es passt.

- [ ] Vorher-Dumps · Änderung · Nachher-Dumps · leere Diffs · `lint` grün · `format:check`
- [ ] Commit: `chore(web): define the ESLint config with defineConfig`

---

## 6. Abschluss (kein eigener Commit außer dem Aufräumen)

- [ ] `/tmp/nacht-sonar-2026-09-15/` löschen; `git status --porcelain` muss leer sein (Coverage-Reports sind gitignored).
- [ ] Vollständige Gates ein letztes Mal gegen den fertigen Branch: `dotnet build EmotePurge.slnx
      --no-incremental` (Warnungen = Basislinie), `dotnet format EmotePurge.slnx --verify-no-changes`,
      `dotnet test EmotePurge.slnx`, `npm --prefix web run format:check`, `npm --prefix web run lint`,
      `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e`.
- [ ] `node <abs>/scripts/coverage-local.mjs` — **nach** dem letzten Commit (das Skript misst nur
      Committetes gegen `origin/main`). Exit 0 ist keine Entwarnung: die **Zahl** in die Notizdatei,
      dazu die Zeile „N geänderte Datei(en) …". Ein niedriger Wert ist hier erwartbar (die Skripte
      unter `scripts/` sind coverage-exempt, die C#-Änderungen sind Einzeiler in großen Dateien) und
      **kein** Anlass, Tests nachzuschreiben.
- [ ] `git log --oneline origin/main..HEAD` in die Notizdatei.
- [ ] **Notizdatei** (die des Apparats), je Task: Regel-Keys + Orte behoben (Tabelle unten als
      Vorlage, mit Ist-Zahlen), gefahrene Gates mit Ergebnis und Laufzeit, Übersprungenes mit
      Regel-Key, Ort und Grund, zurückgesetzte Einzelstellen, Umgebungsbefunde (E2E-Falle,
      Warnungen, fehlende Werkzeuge). Nicht gefahren, weil gesperrt: Push, PR, CI, SonarCloud,
      Deploy, Codex — das steht als Satz drin, nicht als Fehlschlag.

**Tagsüber, durch den Betreiber:** Gates nachfahren, Diff lesen, `/codex:review --model gpt-5.6-sol
--scope branch --base origin/main`, Push, PR, Merge; nach der Sonar-Analyse die Tabelle prüfen; Stack
aus dem Haupt-Checkout neu bauen (B5 hat den Host-Start berührt).

---

## 7. Abgleichtabelle Task → Regel-Keys → Anzahl

| Task | Regel-Keys | Anzahl |
|---|---|---|
| B1 | `csharpsquid:S3928` ×4, `external_roslyn:CA2208` ×4 (ohne `LiveEndpoints.cs`) | 8 |
| B2 | `csharpsquid:S8969` ×7 | 7 |
| B3 | `csharpsquid:S1192` ×7 | 7 |
| B4 | `csharpsquid:S3358` ×4, `csharpsquid:S3260` ×1, `csharpsquid:S3218` ×1, `csharpsquid:S3267` ×6 | 12 |
| B5 | `csharpsquid:S6966` ×2, `csharpsquid:S1118` ×1, `external_roslyn:ASP0027` ×1, `csharpsquid:S8949` ×1, `external_roslyn:CA2016` ×1 | 6 |
| B6 | `external_roslyn:CA1068` ×2, `external_roslyn:CA1806` ×1 | 3 |
| F1 | `typescript:S6582` ×8, `typescript:S7781` ×3, `typescript:S7780` ×1, `typescript:S1128` ×1, `typescript:S7763` ×1, `typescript:S7737` ×1, `typescript:S6571` ×2, `typescript:S3358` ×1 | 18 |
| F2 | `typescript:S1874` ×2 | 2 |
| F3 | `typescript:S5906` ×4 | 4 |
| F4 | `typescript:S4043` ×2, `typescript:S7755` ×4, `typescript:S5443` ×1 | 7 |
| F5 | `javascript:S7781` ×7, `javascript:S7785` ×2, `javascript:S7780` ×1 (ohne S4624 und S3358 in `sonar-to-sarif.mjs`) | 10 |
| F7 | `javascript:S1874` ×1 | 1 |
| **Summe** | | **85** |

Nach dem Merge erwartet SonarCloud auf `main` entsprechend 218 − 85 = 133 offene Befunde,
**bevor** die K4-Markierungen per Skript und der Filter-Branch greifen. Bleibt ein Key aus dieser
Tabelle offen, ist der Ort in der Notizdatei nachzuschlagen: entweder „übersprungen" oder Sonar
bewertet die neue Form anders — dann ist das ein Tagesbefund, kein Nachtlauf-Fehler.
