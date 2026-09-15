# Nachtlauf 2026-09-15 — Sonar-Nachtlauf (Plan `docs/Plan-Sonar-Nachtlauf-2026-09-15.md`)

**Modus:** `durchlauf` · **Branch:** `nacht/2026-09-15-sonar` · **Basis:** `7e4be52` (Plan-Commits auf
`main` nach PR #192; `3646d5b` ist Vorfahr — `git merge-base --is-ancestor` Exit 0).

## Was ist passiert

**Erledigt: der ganze Plan.** Task 0 (Basislinie) plus alle zwölf Tasks in der bindenden Reihenfolge
B1 → B2 → B3 → B4 → B5 → B6 → F1 → F2 → F3 → F4 → F5 → F7, je ein Conventional Commit, **85 von 85
geplanten Befunden** behoben. **Keine Stelle musste zurückgenommen werden**, kein Task lief in die
Gate-rot-Regel, kein Blocker.

**Nicht erledigt, weil gesperrt:** `git push`, PR, CI, SonarCloud-Reanalyse, Deploy, Codex-Reviews.
Das ist der Auftrag, kein Fehlschlag — der Lauf endet mit lokalen Commits auf
`nacht/2026-09-15-sonar` und dieser Datei.

**Offen für den Tag:** ein einziger inhaltlicher Punkt, **A8** weiter unten bei F5 — drei weitere
`.replace(/…/g)`-Aufrufe in `htmlToPlainText`, die nach derselben Regel eigentlich S7781-Befunde sein
müssten, in der Plan-Zählung von 7 aber nicht auftauchen. Der Lauf hat die Plan-Zahl eingehalten und
sie nicht angefasst. Dazu die drei Korrekturen/Annahmen A1, A2 und A6, die unten je an ihrer Stelle
stehen.

### Abschluss-Gates gegen den fertigen Branch (alle im Vordergrund, Exit-Code abgewartet)

| Gate | Exit | Ergebnis |
|---|---|---|
| `dotnet build EmotePurge.slnx --no-incremental` | 0 | **0 Warnungen** — Basislinie gehalten |
| `dotnet format EmotePurge.slnx --verify-no-changes` | 0 | sauber |
| `dotnet test EmotePurge.slnx` | 0 | **1327 Tests** grün (355 · 150 · 822) |
| `npm --prefix web run format:check` | 0 | sauber |
| `npm --prefix web run lint` | 0 | inkl. `lint:colors` |
| `npm --prefix web test -- --watch=false` | 0 | **1278 Tests** in 112 Dateien |
| `npm --prefix web run e2e` | 0 | **135 Tests** in **52,4 s** — normale Laufzeit |

### `node scripts/coverage-local.mjs` nach dem letzten Commit

Exit **1**. Die Zahlen, wie vom Plan verlangt:

> `48 changed file(s) total, 37 of which relevant (.cs/.ts, no .spec.ts, not excluded).`
> `Overall percentage across 36 measured file(s): 5192/6689 (3975/4987 lines, 1217/1702 branches) = 77.6%.`
> `Additionally 1 unmeasured file(s)` — das ist `web/src/app/core/audit/audit.model.ts`, eine reine
> Interface-Datei ohne ausführbaren Code.

**Exit 1 ist hier erwartbar und kein Anlass, Tests nachzuschreiben** — der Plan sagt das ausdrücklich.
Der Grund: die Skripte unter `scripts/` sind coverage-exempt, und die C#-Änderungen sind Einzeiler in
großen Bestandsdateien, deren Gesamtdeckung die dateigenaue Näherung in den Nenner zieht. Sonar misst
zeilengenau auf **neuem** Code; die wenigen geänderten Zeilen sind durchweg von bestehenden Tests
gedeckt. **Die Zahl ist ein Anlass hinzusehen, kein Urteil** — beurteilen kann das erst der
PR-Befund.

### Commits (`git log --oneline origin/main..HEAD`)

```
b26db0c chore(web): define the ESLint config with defineConfig
a3e7796 chore(scripts): modernize string replacement and top-level await in the helper scripts
0b22094 refactor(e2e): sort mock pages once and keep measurement output inside the repo
7898de7 test(web): use toHaveLength for length assertions
c9fcf32 refactor(web): drop the deprecated withFetch call
03eb031 refactor(web): apply Sonar's mechanical fixes without changing guard semantics
6c1fa97 test: order cancellation tokens last and assert TryParse preconditions
fa634e8 refactor: await host startup and drop the redundant Program declaration
8df1602 refactor: flatten nested ternaries and simplify loops flagged by Sonar
66a0554 refactor: name repeated string literals as constants
3797336 refactor: drop null-forgiving operators on RedisValue reads
319f0e6 refactor: throw UnreachableException from closed-enum switch defaults
```
(darunter die drei `docs:`-Plan-Commits, die schon vor dem Lauf auf dem Branch lagen)

### Abgleichtabelle Task → Regel-Keys → Ist-Zahl

| Task | Regel-Keys | Soll | Ist |
|---|---|---|---|
| B1 | `csharpsquid:S3928` ×4, `external_roslyn:CA2208` ×4 | 8 | **8** |
| B2 | `csharpsquid:S8969` ×7 | 7 | **7** |
| B3 | `csharpsquid:S1192` ×7 | 7 | **7** |
| B4 | `csharpsquid:S3358` ×4, `S3260` ×1, `S3218` ×1, `S3267` ×6 | 12 | **12** |
| B5 | `csharpsquid:S6966` ×2, `S1118` ×1, `external_roslyn:ASP0027` ×1, `csharpsquid:S8949` ×1, `external_roslyn:CA2016` ×1 | 6 | **6** |
| B6 | `external_roslyn:CA1068` ×2, `CA1806` ×1 | 3 | **3** |
| F1 | `typescript:S6582` ×8, `S7781` ×3, `S7780` ×1, `S1128` ×1, `S7763` ×1, `S7737` ×1, `S6571` ×2, `S3358` ×1 | 18 | **18** |
| F2 | `typescript:S1874` ×2 | 2 | **2** |
| F3 | `typescript:S5906` ×4 | 4 | **4** |
| F4 | `typescript:S4043` ×2, `S7755` ×4, `S5443` ×1 | 7 | **7** |
| F5 | `javascript:S7781` ×7, `S7785` ×2, `S7780` ×1 | 10 | **10** |
| F7 | `javascript:S1874` ×1 | 1 | **1** |
| **Summe** | | **85** | **85** |

Nach dem Merge erwartet SonarCloud auf `main` entsprechend 218 − 85 = **133** offene Befunde, bevor
die K4-Markierungen und der Filter-Branch greifen.

### Was der Betreiber tagsüber tun muss

1. Gates nachfahren, Diff lesen.
2. `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` — **nicht gelaufen**, das
   Kontingent gehört dem wachen Merge.
3. Push, PR, Merge. Nach der Sonar-Analyse die Abgleichtabelle prüfen.
4. **Stack aus dem Haupt-Checkout neu bauen** — B5 hat den Host-Start berührt (`app.Run()` →
   `await app.RunAsync()`, dieselbe Änderung im Worker).
5. A8 entscheiden (s. F5).

---

## Abweichungen vom Plan, die vor der ersten Änderung feststanden

### A1 — Scratch-Verzeichnis liegt woanders als im Plan

Der Plan schreibt `/tmp/nacht-sonar-2026-09-15/` vor. `mkdir -p /tmp/nacht-sonar-2026-09-15` wird von
der Berechtigungsschranke des Apparats abgelehnt (zweimal versucht, einmal als Teil eines
zusammengesetzten Befehls, einmal allein). Die Schranke ist laut Auftrag keine Verhandlungssache;
ein Umweg über einen anderen Interpreter kommt nicht in Frage.

**Stattdessen** dient das Session-Scratchpad als Scratch-Verzeichnis:

```
/tmp/claude-1000/-home-dev-nachtlaeufe-EmotePurge-2026-09-15-setze-den-plan-docs-plan-sonar-nachtlauf/65a509b9-985b-4586-acd3-0205577e32b4/scratchpad
```

Es erfüllt denselben Zweck (liegt außerhalb des Worktrees, taucht in `git status` nie auf, nimmt
Vorher/Nachher-Dateien und Scratch-Skripte auf). Dateinamen sind die aus dem Plan
(`build-baseline.txt`, `cov-help-before.txt`, …). Das Aufräumen am Ende entfällt: das Verzeichnis ist
session-lokal und wird vom Apparat verwaltet.

### A2 — Die `verify`-Kommandos aus der Auftragsvorlage gibt es hier nicht

Die Nachtlauf-Vorlage nennt `pwsh server/scripts/verify.ps1` und `npm run verify` in `spa/`. Beides
sind Pfade eines anderen Projekts (Homeport); in diesem Repository existieren weder `server/` noch
`spa/`. Bindend sind die Gates, die `CLAUDE.md` und der Plan nennen: `dotnet build EmotePurge.slnx
--no-incremental`, `dotnet format EmotePurge.slnx --verify-no-changes`, `dotnet test
EmotePurge.slnx`, `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`,
`npm --prefix web run format:check`, `npm --prefix web run e2e`. Ebenso entfällt der Satz über
`Homeport.Api` und `npm run api:generate` — dieses Repository hat keinen generierten OpenAPI-Client.

---

## Task 0 — Basislinie

Alle Gates grün, die Stopp-Regel greift nicht. Gefahren im Vordergrund, Exit-Code jeweils abgewartet:

| Gate | Befehl | Exit | Ergebnis |
|---|---|---|---|
| Ancestry | `git merge-base --is-ancestor 3646d5b HEAD` | 0 | `3646d5b` ist Vorfahr von `7e4be52` |
| Arbeitsbaum | `git status --porcelain` | — | leer |
| Build | `dotnet build EmotePurge.slnx --no-incremental` | 0 | **0 Warnungen**, 12,6 s |
| Backend-Tests | `dotnet test EmotePurge.slnx` | 0 | **1327 Tests** grün (Worker 355 · Api 150 · Infrastructure 822), Testcontainers liefen offline |
| Vitest | `npm --prefix web test -- --watch=false` | 0 | **1278 Tests** in 112 Dateien, 4,0 s (+ 5,8 s Bundle) |
| Lint | `npm --prefix web run lint` | 0 | inkl. `lint:colors` („clean, no exemptions left") |
| Format | `npm --prefix web run format:check` | 0 | „All matched files use Prettier code style!" |
| E2E | `npm --prefix web run e2e` | 0 | **135 Tests** grün in **52,0 s** — normale Laufzeit, die `:5151`-Falle liegt nicht vor |

Die Warnungs-Basislinie ist damit **0**; kein Backend-Task darf sie erhöhen. Die Testzahl 1327 liegt
über der Planangabe (~1025) — der Plan war an dieser Stelle veraltet, kein Befund.

Im E2E-Log stehen `ECONNREFUSED`-Zeilen des Dev-Proxy für nicht gemockte `/api`-Routen. Das ist das
**erwartete** Bild bei freiem `:5151` (Abschnitt „Tests" der `CLAUDE.md`): die Anfrage scheitert
sofort, die Seite rendert trotzdem. Es ist kein Fehler.

---

## Tasks

### B1 — `UnreachableException` statt `ArgumentOutOfRangeException` · Commit `319f0e6`

**Behoben:** `csharpsquid:S3928` ×4 + `external_roslyn:CA2208` ×4 = **8 Befunde**, an genau den vier
Plan-Stellen:

| Datei · Symbol | Enum | Arm-Zählung (geprüft) |
|---|---|---|
| `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` (Foreign-Lookup) | `ForeignEmoteSetLookupStatus` | 8/8 |
| `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` (Leaderboard) | `SevenTvLeaderboardStatus` | 4/4 |
| `src/EmotePurge.Infrastructure/Services/ForeignEmoteSetService.cs` (`previewResult`) | `SevenTvPreviewLookupStatus` | 4/4 |
| `src/EmotePurge.Infrastructure/Services/SevenTvLeaderboardService.cs` | `SevenTvEmoteSearchLookupStatus` | 3/3 |

Alle vier Zählungen entsprechen der Plan-Erwartung; keine Stelle ausgelassen. `LiveEndpoints.cs`
(erreichbarer Default) blieb unberührt.

**Annahme A3 — Wortlaut der Message.** Der Plan verlangt „englisch, nennt Enum-Typ und erhaltenen
Wert", legt den Wortlaut aber nicht fest. Gewählt:
`$"Unexpected {nameof(<EnumTyp>)} value: {result.Status}."` — einheitlich an allen vier Stellen. Die
zwei deutschen „Unbekannter …"-Texte sind damit ersetzt, die zwei englischen inhaltlich gleich
geblieben.

**Annahme A4 — zwei weitere Switches in `SevenTvLeaderboardService.cs` nicht angefasst.** Die Datei
enthält in `ApplyBreakerFeedback` (~Z. 305) und bei `ForeignSevenTvBreakerTransition` (~Z. 330)
weitere Switches über dasselbe Enum. Sie stehen **nicht** in der Liste der vier Sonar-Befunde und
sind deshalb unverändert geblieben (Regel „nichts nebenbei"). Falls SonarCloud sie nach dem Merge
doch meldet, ist das ein Tagesbefund.

**Gates:** `dotnet build EmotePurge.slnx --no-incremental` Exit 0 / **0 Warnungen** ·
`dotnet format EmotePurge.slnx --verify-no-changes` Exit 0 · `dotnet test EmotePurge.slnx` **Exit 0,
1327 Tests grün** (355/150/822 — unverändert gegenüber der Basislinie).

### B2 — Kein `!` mehr auf `RedisValue` · Commit `3797336`

**Behoben:** `csharpsquid:S8969` ×7, an allen sieben Plan-Stellen (`ModRoleCache`,
`RateLimitTelemetryStore.Deserialize<T>`, `TwitchLiveStatusStore`, `WorkerHealthReader` ×2,
`ModeratedChannelsProvider`, `ForeignEmoteSetCache`). Jede Stelle prüft direkt davor `IsNullOrEmpty`.

**Der im Plan vorgesehene Ausweichpfad wurde gezogen, und zwar an allen sieben Stellen.** Das bloße
Entfernen des `!` (also `(string)value`) erzeugte an genau diesen sieben Stellen CS8600/CS8604 —
StackExchange.Redis annotiert den expliziten `string`-Operator als `string?`. Der Plan schreibt für
diesen Fall `value.ToString()` vor und verbietet ausdrücklich, das `!` wieder anzuhängen. Genau so
steht es jetzt. Die Warnungszahl bleibt damit **0**.

Anmerkung für die Nachschau in SonarCloud: `value.ToString()` enthält gar keinen Cast mehr, der
S8969-Befund ist damit sicher weg — aber es ist eine **andere** Form als das im Plan skizzierte
`(string)value`. Wer den Diff liest, sieht sieben `ToString()`-Aufrufe und nicht sieben gelöschte
Ausrufezeichen; das ist Absicht und vom Plan gedeckt, kein Abweichen.

**Gates:** `dotnet build --no-incremental` Exit 0 / **0 Warnungen** ·
`dotnet format --verify-no-changes` Exit 0 · `dotnet test EmotePurge.slnx` **Exit 0, 1327 Tests grün**.

### B3 — Wiederholte String-Literale als Konstanten · Commit `66a0554`

**Behoben:** `csharpsquid:S1192` ×7, eine Konstante je Datei:

| Datei | Konstante | ersetzte Vorkommen |
|---|---|---|
| `AdminEndpoints.cs` | `PerUserPartition = "twitch-user"` | 5 |
| `RateLimitRejection.cs` | `Unknown = "unknown"` | 4 |
| `UsageStatQueryService.cs` | `FromMustPrecedeToMessage` | 4 |
| `SevenTvApiClient.cs` | `TwitchPlatform = "TWITCH"` | 5 |
| `SevenTvDispatchParser.cs` | `ValueKey = "value"` | 5 |
| `TwitchHelixClient.cs` | `BearerScheme = "Bearer"` | 5 |
| `HarnessRunner.cs` | `IsoDateFormat = "yyyy-MM-dd"` | 6 |

**Die `HarnessRunner`-Falle ist wie vorgeschrieben umschifft.** Die Zeilen „Bot-Split-Stichtag" und
„Shared-Chat-Stichtag" haben nur ihren Formatstring-Parameter getauscht; die Überladung
`Iso(DateOnly?)` (die für `null` „none" schreibt) wurde dort **nicht** verwendet, die Null-Texte
„keiner (kein Bot je gesehen)" und „keiner (Diagnoselauf)" sind wortgleich geblieben. Die
byte-genauen `HarnessRunnerTests` sind grün.

Nicht angefasst, wie im Plan verlangt: `"twitch-user+vote-session"` (eigenständiges Literal, **nicht**
aus der Konstante zusammengesetzt), die interpolierten Formatangaben `{…:yyyy-MM-dd}` und
`{…:yyyy-MM-dd HH:mm:ss}`, und die zwei deutschen Meldungstexte, die die erwartete Datumsform in
Prosa nennen. In `UsageStatQueryService` gibt es eine fünfte, deutschsprachige Guard-Message — sie
ist ein anderes Literal und blieb unverändert.

**Annahme A5 — Konstantennamen.** Der Plan nennt sechs Namen vor und lässt einen frei
(„`private const string Unknown` (oder sprechender)"). Gewählt: `Unknown` wie vorgeschlagen,
`FromMustPrecedeToMessage` für die Guard-Message. Alle Konstanten stehen nach Regel 19 am
Klassenanfang.

**Gates:** `dotnet build --no-incremental` Exit 0 / **0 Warnungen** ·
`dotnet format --verify-no-changes` Exit 0 · `dotnet test EmotePurge.slnx` **Exit 0, 1327 Tests grün**.

### B4 — Ternäre, `sealed`, Shadowing, LINQ · Commit `8df1602`

**Behoben:** `csharpsquid:S3358` ×4, `S3260` ×1, `S3218` ×1, `S3267` ×6 = **12 Befunde** in 6 Dateien.
Alle zwölf Stellen umgesetzt, keine musste zurückgenommen werden. Die ausgeschlossenen Stellen
(`ReplayFidelityCalculator.cs` Histogramm-Schleife ~504 und die `continue`-Schleife ~217,
`SevenTvLeaderboardService.cs:128`, `RosterPrunePolicy.cs:47`) blieben unberührt.

`Clamp` behielt die Reihenfolge seiner beiden Grenzprüfungen (erst `< minimum`), `BuildForeignImageUrl`
liefert eine zeichengleiche URL, die „Gate-tauglich"-Zeile ist byte-gleich, `Resolve` ist ein
`switch` über `(origin, isBot)` mit drei Armen und unverändertem XML-Kommentar. Die beiden
Live-Stream-Zählungen stehen weiterhin **innerhalb** des `lock`.

**Korrektur A6 durch die Hauptsession — zwei überflüssige `using System.Linq;` entfernt.** Der
Subagent hatte für die neuen `.Where`/`.Count`-Aufrufe je ein explizites `using System.Linq;` in
`RedisLiveEventStream.cs` und `SevenTvApiClient.cs` ergänzt und im Bericht behauptet, es sei „nötig"
gewesen. Das stimmt nicht: `EmotePurge.Infrastructure` hat `ImplicitUsings=enable`, `System.Linq`
steht dort in der generierten `GlobalUsings.g.cs`, und **keine** andere Datei unter `src/` trägt ein
explizites `using System.Linq;`. Beide Zeilen wären damit ein **neuer** `csharpsquid:S1128`-Befund
gewesen — der Task hätte Befunde erzeugt statt sie zu schließen. Die Hauptsession hat die zwei Zeilen
entfernt und danach Build (Exit 0, 0 Warnungen), Format und Tests erneut gefahren. Der Commit enthält
sie nicht.

**Anmerkung A7 — zwei neue `!`-Operatoren in `SevenTvApiClient`.** Die vom Plan vorgeschriebene Form
`foreach (… in entryPage.Items.Where(entry => entry.Emote is not null && entry.AddedAt is not null))`
nimmt dem Compiler das Narrowing über die Lambda-Grenze; der Rumpf braucht deshalb `entry.Emote!.Id`
und `entry.AddedAt!.Value`. Das sind **keine** redundanten `!` im Sinn von B2 (dort saßen sie auf
einem Struct, hier auf echten nullable Referenztypen), aber es steht neben dem Commit, der sieben
solcher Operatoren entfernt hat. Wer den Diff liest, sollte das wissen. Eine Alternative ohne `!`
(z. B. `.Where(...).Select(e => (e.Emote!, e.AddedAt!.Value))`) hätte die Stelle stärker umgebaut, als
der Plan erlaubt.

**Gates:** `dotnet build --no-incremental` Exit 0 / **0 Warnungen** ·
`dotnet format --verify-no-changes` Exit 0 · `dotnet test EmotePurge.slnx` **Exit 0, 1327 Tests grün**.
`grep -rn "Spearman" src/ tests/` zeigt nur noch die Property, Prosa und Testnamen — kein
`cref="Spearman"`, kein Aufruf des alten Methodennamens.

### B5 — Host-Start und `Program`-Deklaration · Commit `fa634e8`

**Behoben:** `csharpsquid:S6966` ×2, `csharpsquid:S1118` ×1, `external_roslyn:ASP0027` ×1,
`csharpsquid:S8949` ×1, `external_roslyn:CA2016` ×1 = **6 Befunde**.

Die im Plan vorgeschriebene Zwei-Schritt-Reihenfolge wurde eingehalten: erst `app.Run()` →
`await app.RunAsync()` und `host.Run()` → `await host.RunAsync()` plus das durchgereichte `ct` an
`liveStatusWriter.PublishAsync`, bauen, `git add` — **dann** erst die Löschung von
`public partial class Program;`. **Schritt 2 hat kompiliert**, die Rücknahme war nicht nötig.
`tests/EmotePurge.Api.Tests` sind unverändert und mit 150 Tests grün; das .NET-10-Web-SDK erzeugt die
öffentliche `Program`-Klasse selbst, `WebApplicationFactory<Program>` löst weiterhin auf.

**Nicht beweisbar, ausdrücklich (Plan Abschnitt B5):** Ein echter Worker- oder Api-Start ist nicht
gelaufen. Der Worker würde neben dem Dev-Worker des Betreibers zählen und die Messung aus Epic #118
verfälschen; für einen Api-Start fehlt in der Sandbox saubere Prozesskontrolle, und `curl` ist
gesperrt. `Run()` → `RunAsync()` ist API-gleich; der Beleg dieses Laufs ist Build + Api.Tests +
Worker.Tests. **Der Betreiber sollte den Stack nach dem Merge aus dem Haupt-Checkout neu bauen** —
dieser Commit hat den Host-Start berührt.

Das `ct` an `PublishAsync` wird von der heutigen Implementierung ignoriert. Das war schon vor dem
Lauf so, steht so im Plan und ist **nicht** Teil dieses Tasks.

**Gates:** `dotnet build --no-incremental` Exit 0 / **0 Warnungen** ·
`dotnet format --verify-no-changes` Exit 0 · `dotnet test EmotePurge.slnx` **Exit 0, 1327 Tests grün**.

### B6 — Test-Hygiene · Commit `6c1fa97`

**Behoben:** `external_roslyn:CA1068` ×2, `external_roslyn:CA1806` ×1 = **3 Befunde**.

`ct` steht in beiden Harness-Helfern jetzt am Ende der Parameterliste; genau **eine** Aufrufstelle war
positional (`Run(3, cts.Token)` → `Run(3, ct: cts.Token)`), alle anderen nutzten schon benannte
Argumente. `TryParse` im Leaderboard-Endpoint-Test steht jetzt in `Assert.True(...)`.

**Gates:** `dotnet build --no-incremental` Exit 0 / **0 Warnungen** ·
`dotnet format --verify-no-changes` Exit 0 · `dotnet test EmotePurge.slnx` **Exit 0, 1327 Tests grün —
Testanzahl unverändert** wie vom Plan verlangt.

### F1 — Mechanische Befunde unter `web/src` · Commit `03eb031`

**Behoben:** `typescript:S6582` ×8, `S7781` ×3, `S7780` ×1, `S1128` ×1, `S7763` ×1, `S7737` ×1,
`S6571` ×2, `S3358` ×1 = **18 Befunde** in 13 Dateien. **Keine Stelle musste zurückgesetzt werden.**

**Die Optional-Chaining-Falle hat nicht zugeschlagen.** Alle acht S6582-Stellen haben die vom Plan
vorgegebene Zielform bekommen, nicht Sonars Quick-Fix — `!current?.result`, `!w?.available`,
`status?.capacity == null` (lose Gleichheit, kein `eqeqeq` im Lint), `envelope?.source !== 'emotepurge'`,
`stash?.channelName !== channelName`. Die `length === 0`-Klauseln stehen weiterhin ausgeschrieben da
und wurden **nicht** über Falsy-0 gefaltet. TypeScript hat hinter jedem dieser Guards korrekt
genarrowt; keine Stelle brauchte einen Cast oder ein `!`.

Die übrigen sieben Punkte wie im Plan: `String.raw` für das Escape-Replacement, `replaceAll` mit
demselben Regex, der gelöschte `VoteSessionSummary`-Import, `export { RUN_DELAY_MS as DELETE_DELAY_MS }`
statt Re-Zuweisung (Doc-Kommentar erhalten), destrukturierter `load({ freeze = true } = {})` mit
unveränderten vier Aufrufern, `(string & {})` in `audit.model.ts` mit je einem erklärenden Satz am
bestehenden Doc-Kommentar, und `compareChannelNames` als Modulfunktion mit Code-Unit-Vergleich —
**nicht** `localeCompare`, das hätte die Reihenfolge geändert.

**Gates:** `format:check` Exit 0 · `lint` Exit 0 (inkl. `lint:colors`) ·
`npm --prefix web test -- --watch=false` **Exit 0, 1278 Tests in 112 Dateien** (exakt Basislinie) ·
`npm --prefix web run e2e` **Exit 0, 135 Tests grün in 51,3 s** — normale Laufzeit, die
`:5151`-Falle lag auch hier nicht vor.

### F2 — `withFetch` entfernt · Commit `c9fcf32`

**Behoben:** `typescript:S1874` ×2 (Import und Aufruf in `web/src/app/app.config.ts`). Allein im
Commit, wie der Plan es verlangt — ein Revert ist ein Klick.

**Gates:** `format:check` Exit 0 · `lint` Exit 0 · Vitest **Exit 0, 1278 Tests** ·
`npm --prefix web run e2e` **Exit 0, 135 Tests grün in 52,4 s**.

### F3 — `toHaveLength` · Commit `7898de7`

**Behoben:** `typescript:S5906` ×4, genau an den vier Plan-Stellen
(`seven-tv-delete.service.spec.ts` 447/457/510, `foreign-emote-grid.spec.ts` 753).

**Gates:** `format:check` Exit 0 · `lint` Exit 0 · Vitest **Exit 0, 1278 Tests** (Basislinie).
E2E war für diesen Task nicht gefordert (nur Spec-Dateien berührt) und ist nicht gelaufen.

### F4 — E2E-Support und Mess-Harness · Commit `0b22094`

**Behoben:** `typescript:S4043` ×2, `S7755` ×4, `S5443` ×1 = **7 Befunde**.

Die zwei Audit-Log-Mocks sortieren ihre Seitenzahlen jetzt **einmal bei der Definition** in eine
eigene Konstante (`[...pageNumbers].sort(...)`, **nicht** `toSorted` — die ES2022-Lib kennt es nicht),
statt bei jeder abgefangenen Anfrage ein Closure-Array in place zu mutieren. Drei `at(-1)`-Umstellungen
im Mess-Harness und eine in `mocks.ts`, jeweils nur dort, wo der `??`-Fallback schon existierte —
die **fünf** gleichartigen Stellen unter `web/src` sind K4 und blieben unberührt.

Das Mess-Harness schreibt nicht mehr nach `/tmp/webp-measure` (weltschreibbar, S5443), sondern nach
`test-results/webp-measure` relativ zu `web/` (steht in `web/.gitignore`); die zwei Beispielpfade im
Header-Kommentar sind nachgezogen.

**Beleg für den neuen Default** (der Plan verlangt genau diesen):
`npm --prefix web exec playwright -- test --list --config <abs>/web/playwright.measure.config.ts`
bricht **erwartungsgemäß** ab mit
`Error: No emote fixture at test-results/webp-measure/emotes.json. Set MEASURE_EMOTES, or generate one
with the psql snippet in the header of this file.` — das beweist Modul-Auswertung **und** den neuen
Pfad. Ein echter Messlauf braucht die DB-Fixture und wurde nicht versucht.

**Gates:** `format:check` Exit 0 · `lint` Exit 0 · `npm --prefix web run e2e` **Exit 0, 135 Tests
grün in 51,3 s**.

### F5 — Wurzel-Skripte und `check-color-tokens` · Commit `a3e7796`

**Behoben:** `javascript:S7781` ×7, `S7785` ×2, `S7780` ×1 = **10 Befunde**. `authHeaders` und der
Regel-Key-Comparator in `sonar-to-sarif.mjs` blieben unberührt (Abschnitt 1).

**Die beiden S8786-Regexe sind buchstabengleich geblieben** — `/<[^>]+>/g` und `/[ \t]+\n/g` behalten
ihren `.replace`-Aufruf und ihr Muster; ebenso `<br…>`, `</(p|div|li|h[1-6])>` und `\n{3,}`. Nur die
fünf Entitäten-Ersetzungen sind auf `replaceAll` gewechselt.

**Beide `isDirectRun`-Blöcke** haben Top-Level-`await main()` **innerhalb** von `try`/`catch` mit
wortgleicher Meldung (`Aborted with error: …`) und unverändertem `process.exitCode = 1`. Ein nacktes
`await` hätte Exit-Code und Ausgabe geändert — genau das verbietet der Plan, und die Golden-Läufe
hätten es gefunden.

**Die Abnahme ist der Vorher/Nachher-Vergleich — alle sieben Diffs sind leer (Exit 0):**

| # | Lauf | beweist | `diff -u` |
|---|---|---|---|
| 1 | `coverage-local.mjs --help` | `isDirectRun`-Erfolgspfad | leer |
| 2 | voller Lauf + `--skip-tests` | Report-Pfade über `normalizeToRepoRelativePosix` | leer |
| 3 | `sonar-to-sarif.mjs` ohne Env | `isDirectRun`-Fehlerpfad, Exit 1 | leer |
| 4 | `npm --prefix web run lint:colors` | `VIOLATION`-Muster über den ganzen `web/`-Baum | leer |
| 5 | Scratch-Harness über 28 synthetische HTML-Fragmente | die fünf Entitäten-Ersetzungen | leer |
| 6 | `coverage-local.mjs --bogus-option` | Fehlerpfad des zweiten `isDirectRun`-Blocks, Exit 1 | leer |
| 7 | Scratch-Harness über 21 feste Pfade | `normalizeToRepoRelativePosix` direkt | leer |

Der Korpus in Lauf 5 ist **rein synthetisch** und vom Lauf selbst erzeugt (Überschriften, `<pre>`/
`<code>`, verschachtelte Listen, Anker mit Attributen, alle fünf ersetzten Entitäten **plus** `&nbsp;`
zur Gegenprobe, `<br>`/`<br/>`/`<BR />`, CRLF, Tabs vor `\n`, ≥3 Leerzeilen, ein mehrzeiliges Tag,
leerer String, `null`, `undefined`). Kein Netz, keine externe Datei.

**Nicht beweisbar, ausdrücklich:** Der **Erfolgspfad** von `sonar-to-sarif.mjs` braucht ein
SonarCloud-Token und ist ohne Netz nicht erreichbar. Das ist im Plan so vorgesehen und keine Lücke —
der Fehlerpfad deckt den geänderten `isDirectRun`-Block ab.

**Offener Punkt für den Tag (A8):** Der Plan zählt `javascript:S7781` mit **7**, das sind die zwei in
`coverage-local.mjs` plus die fünf Entitäten. In derselben `htmlToPlainText`-Kette stehen aber noch
drei weitere `.replace`-Aufrufe mit `/g`- bzw. `/gi`-Regex (`<br…>`, `</(p|div|li|h[1-6])>`,
`\n{3,}`), die Sonar nach derselben Regel eigentlich ebenfalls melden müsste. Entweder dedupliziert
die Triage sie gegen die S8786-Befunde auf denselben Zeilen, oder sie fehlen in der Zählung. Der Lauf
hat sich an die Plan-Zahl gehalten und sie **nicht** angefasst. Bleiben sie nach dem Merge offen, ist
das ein Tagesbefund, kein Nachtlauf-Fehler.

**Gates:** `npm --prefix web run lint` Exit 0 (deckt nur `web/scripts/`, nicht `scripts/`).

---

### F7 — ESLint-Konfiguration auf `defineConfig` · Commit `b26db0c`

**Behoben:** `javascript:S1874` ×1. `defineConfig` aus `eslint/config` ersetzt das deprecated
`tseslint.config(...)`; das Array ist **buchstabengleich** geblieben, `tseslint.parser` und
`tseslint.plugin` kommen weiter aus `typescript-eslint` (deshalb bleibt auch dessen Import stehen),
Kommentarblock und `// @ts-check` unverändert.

**Beleg:** `eslint --print-config` für `app.config.ts` **und** für `vote-session-list-page.html`,
vorher wie nachher — beide `diff -u` leer (Exit 0). Die Hauptsession hat zusätzlich gegen ihre
**eigenen** Vorher-Dumps verglichen, die schon vor dem ersten Commit dieser Nacht entstanden sind:
ebenfalls beide leer. Die aufgelöste Konfiguration ist damit über den ganzen Lauf hinweg identisch.

**Gates:** `lint` Exit 0 · `format:check` Exit 0. E2E/Vitest waren für diesen Task nicht gefordert
(die Konfiguration wirkt nur auf den Lint-Lauf) und sind im Abschluss ohnehin grün gelaufen.

---

## Annahmen und Strittiges — Sammelstelle

Die Annahmen stehen jeweils an ihrer Stelle oben. Hier die Liste zum Nachschlagen:

| # | Wo | Worum es geht |
|---|---|---|
| **A1** | oben | Scratch-Verzeichnis liegt im Session-Scratchpad, nicht in `/tmp/nacht-sonar-2026-09-15/` — `mkdir` dorthin wird von der Berechtigungsschranke abgelehnt |
| **A2** | oben | Die `verify`-Kommandos der Auftragsvorlage (`server/scripts/verify.ps1`, `spa/`) gehören zu einem anderen Projekt; bindend sind die Gates aus `CLAUDE.md` und dem Plan |
| **A3** | B1 | Wortlaut der `UnreachableException`-Message frei gewählt: `Unexpected {EnumTyp} value: {Wert}.` |
| **A4** | B1 | Zwei weitere Switches über dasselbe Enum in `SevenTvLeaderboardService.cs` (~305, ~330) nicht angefasst — nicht Teil der vier Befunde |
| **A5** | B3 | Konstantennamen `Unknown` und `FromMustPrecedeToMessage` gewählt, wo der Plan sie freigestellt hat |
| **A6** | B4 | **Korrektur der Hauptsession:** zwei überflüssige `using System.Linq;` des Subagents entfernt — sie wären neue S1128-Befunde gewesen |
| **A7** | B4 | Zwei **neue** `!`-Operatoren in `SevenTvApiClient` als unvermeidbare Folge der vom Plan vorgeschriebenen `.Where(...)`-Form |
| **A8** | F5 | **Offener Punkt:** drei weitere `/g`-`replace`-Aufrufe in `htmlToPlainText`, die Sonar nach S7781 eigentlich melden müsste, die aber nicht in der Plan-Zählung von 7 stehen — unangetastet gelassen |

### Umgebungsbefunde

- **Die `:5151`-Falle ist nie eingetreten.** Alle fünf E2E-Läufe dieser Nacht (Basislinie, F1, F2, F4,
  Abschluss) liefen in **51–52 s** mit **135/135** grün. Die `ECONNREFUSED`-Zeilen des Dev-Proxy im
  Log sind das erwartete Bild bei freiem `:5151`, kein Fehler. Auch Speicherdruck hat sich nicht
  gezeigt.
- **Testcontainers liefen offline durch**, wie der Betreiber zugesichert hatte — kein Pull-Versuch,
  kein Timeout, in keinem der sieben `dotnet test`-Läufe.
- **Zwei Sandbox-Schranken sind aufgetreten** und wurden **nicht** umgangen: `mkdir` außerhalb des
  Session-Scratchpads (→ A1) und `cd`-Ketten bzw. `VAR=wert`-Präfixe in Shell-Zeilen. Beides wurde
  durch absolute Pfade und Einzelbefehle ersetzt. Kein Werkzeug hat gefehlt.
- **Kein `docker compose`, `docker run` oder `docker exec`** wurde aufgerufen; der Dev-Stack auf
  `:8080` blieb unberührt. Kein Netzzugriff in irgendeiner Form, auch nicht aus den beiden
  Scratch-Skripten.

### Arbeitsweise dieses Laufs

Jeder der zwölf Tasks lief als eigener Subagent (`model: sonnet`) mit frischem Kontext. Die
Hauptsession hat **jeden** Diff selbst gelesen und **jedes** Gate selbst gefahren, bevor sie
committet hat — die Test- und E2E-Läufe oben stammen alle aus der Hauptsession, nicht aus den
Subagent-Berichten. In einem Fall (A6) hat das einen Fehler im Subagent-Ergebnis gefunden und
korrigiert.
