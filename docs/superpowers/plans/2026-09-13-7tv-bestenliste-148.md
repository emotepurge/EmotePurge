# 7TVs Bestenliste als Import-Quelle — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan **und** die Spec; er argumentiert aus der Spec und
> wiederholt sie nicht. Schritte sind als Checkbox (`- [ ]`) geführt.

**Ziel:** Ein eingeloggter Nutzer sieht 7TVs netzwerkweite Bestenliste („Trend heute" /
„Top insgesamt", je bis 500 Emotes) als dritte Option im bestehenden Import-Dialog, wählt daraus aus
und überträgt in ein Set mit 7TV-Rechten; der Server ruft 7TV dafür höchstens 10-mal je rollender
Stunde und Prozesslauf an.

**Spec (Quelle der Wahrheit):**
[`docs/superpowers/specs/2026-09-13-7tv-bestenliste-import-quelle-spec.md`](../specs/2026-09-13-7tv-bestenliste-import-quelle-spec.md)
(Commit `57aeba1`, AK 36 und Risikomaßstab korrigiert in `e9e7f0a`; 36 AK in §9, Entscheidungen
E1–E16 in §2, Fallen F1–F7 in §3). **Entwurf:**
[`docs/designs/7TV-Bestenliste-als-Import-Quelle-2026-09-13.md`](../../designs/7TV-Bestenliste-als-Import-Quelle-2026-09-13.md)
(Commit `9b0f87b`, APPROVED). **Issue:** #148 · **Folge-Issue:** #165 (gemeinsame Bremse, nach dem
Messfenster — Hygiene, kein Kontingent-Notfall) · **Epic:** #118 (messungsneutral).

**Korrektur gegenüber Commit `57aeba1`, in Spec und Plan eingearbeitet (`e9e7f0a`):** AK 36 lautet
neu „0 **aktive Kanäle mit leerer Twitch-ID**, geprüft auf der Admin-Detailseite je Kanal" — die
Vorauswahl über den Sync-Fehlergrund entfällt (Wortlaut und Grund im Abschnitt
„Rollout-Vorbedingung").

**Architektur in vier Sätzen:** Ein neuer Endpunkt `GET /api/seventv/leaderboard?sortBy=…` liest
aus einem **In-Process-Vorrat** (`SevenTvLeaderboardStore`, ein Eintrag je Sortierung, 1 h
haltbar, negative Ausgänge mit vier Haltbarkeiten), der bei Bedarf über einen **harten
Fensterbudget-Zähler** (`SevenTvLeaderboardRequestBudget`, 10 je 60 min je Prozesslauf, Wartezeit 0)
und einen **eigenen Keyed-Breaker** je Upstream-Seite `emotes.search` auf `v4/gql` ruft — bis zwei
Seiten à 250, nach Id dedupliziert, atomar ersetzt. Der Client bekommt dafür einen **gemeinsamen
privaten v4-Seitenabruf** (E11), den auch die #147-Vorschau nutzt — abgesichert durch 21 bestehende
plus drei neue Charakterisierungstests. Die Herkunft reist über ein **neues Feld `leaderboardSort`**
in `sync-imported` (E8) bis in die Audit-Zeile (`ImportedFromLeaderboard`, E9). Im Frontend trägt
ein eigener `LeaderboardStep` das bestehende `ForeignEmoteGrid` mit vier additiven Inputs (E12);
die UI paginiert nicht und kennt weder `page` noch `refresh`.

## Globale Randbedingungen

- **Kein fertiger Code in diesem Plan.** Verträge, Namen und Grenzfälle ja, Rümpfe nein.
- **Die Entscheidungen des Betreibers (Spec §2, E1–E16) werden nicht neu aufgerollt** und nicht als
  Option dargestellt. Wer einen Befund dagegen hat, meldet ihn dem Orchestrator, statt zu entscheiden.
- **Messfenster Epic #118 (AK 29, F6):** nichts unter `src/EmotePurge.Worker/**`, keine Änderung an
  `SevenTvSyncService.cs`, `ResolveTwitchUserIdAsync`/`GqlUsersQuery` (`SevenTvApiClient.cs:18`,
  `:72-118`), `EmoteMatchCache`, `UsageFlushWorker`, Matching; kein Join, keine Migration. Dass das
  Worker-Image trotzdem neu gebaut wird (Infrastructure/Core sind geändert, `publish.yml:160-170`),
  ist akzeptiert und kommt in den PR-Text.
- **Nicht angefasst (Spec §13, Ende):** `ForeignEmoteSetProviderBudget`, `ForeignEmoteSetCache`,
  `ForeignEmoteSetRequestCoalescer`, `HardenedForeignEmoteSetService`, `ForeignEmoteSetService`,
  `ForeignChannelStep`, `ImportTargetDialog`, `usage-stats-page.*`, `already-present-filter.ts`,
  `import-preview.ts`, `GetChannelStateForTwitchUserAsync`.
- **Regel 3:** Der `DECISIONS.md`-Eintrag steht im Commit, der die Konvention ändert (Task 8).
- **Regel 7:** ein neuer Fehlercode (`invalid_leaderboard_sort`), an drei Stellen (Task 5b).
- **Regel 12:** Verhalten ja, Vorlage nein. **Regel 18:** `dotnet format` bzw.
  `npm --prefix web run format` + `lint` vor jedem Commit — die CI prüft beides.
- **Sprache:** Bezeichner, Kommentare, Log-/Throw-Messages und Commits englisch; Conventional
  Commits; **keine Ticketnummern in Git-Metadaten**.
- **Fertig-Gates:** `dotnet test EmotePurge.slnx` (braucht Docker), `npm --prefix web test --
  --watch=false`, bei UI-Änderungen `npm --prefix web run e2e` (**nur wenn auf `:5151` keine Api
  lauscht** — vorher prüfen, sonst fällt rund die halbe Suite irreführend rot), vor dem PR
  `node scripts/coverage-local.mjs` (misst nur Committetes; Exit 0 ist keine Entwarnung; 80 % auf
  neuem Code). Einzelspecs im Frontend über `ng test --include`, nicht `npx vitest` (überspringt die
  Typprüfung). Backend-Warnungen nur mit `--no-incremental` sichtbar.

---

## Task-Übersicht

| Nr. | Titel | Spec-Task | AK | Abhängigkeit | Modell | Welle |
|---|---|---|---|---|---|---|
| 1 | Prüfliste §14 am Code nachprüfen | — | (Vorbedingung) | keine | `sonnet` | A, allein |
| 2 | Charakterisierungstests #147-Vorschaupfad | T0 | 28, 34 | 1 | `sonnet` | B |
| 4 | Pure Klassen: Vorrat, Fensterbudget, Haltbarkeitsregel | T2 | 4, 6–11, 33 | 1 | `opus` | B |
| 7 | Raster: vier Inputs, instanzeindeutige id | T5 | 19, 20, 23 | 1 | `sonnet` | B |
| 3 | Client: gemeinsamer v4-Seitenabruf + `SearchEmotesAsync` | T1 | 13, 14, 28 | 1, 2 | `sonnet` | C (neben laufendem 4/7) |
| 5a | Core-Vertrag + `SevenTvLeaderboardService` (Füllpfad) | T3 (Teil) | 1, 3–6, 8, 12, 13, 33, 35 | 3, 4 | `opus` | D |
| 5b | Endpunkt, Filter, Policy, Fehlercode, Api.Tests | T3 (Teil) | 1, 2, 3, 24, 26 | 5a (Vertrag-Commit) | `sonnet` | D (neben 5a-Rest) |
| 6a | Herkunft Backend: `leaderboardSort`, Vokabeltabelle, Audit-Projektion | T4 (Teil) | 15, 16, 26 | 3, 5b | `sonnet` | E |
| 6b | Herkunft Frontend: `ImportOrigin`, Leitung, Bestätigung, Audit-Anzeige | T4 (Teil) | 17, 27 | 5b (Locale-Reihe) | `sonnet` | E |
| 8 | Verdrahtung: `LeaderboardStep`, Dialog, Fluss, `DECISIONS.md` | T6 | 18, 21, 22, 23, 25, 27 | 5a **komplett**, 5b, 6a, 6b, 7 | `sonnet` | F, allein |
| — | Live-Verifikation gegen echtes 7TV (Betreiber + Orchestrator) | — | 14, 16, 30, 31 (lokal), 33 | 8 | — | G |
| 9 | E2E, Gesamt-Gates, Messfenster-Diff, Coverage — **nach dem letzten Code-Commit** | — | 29, 32 | Live-Verifikation | `sonnet` | H, allein |
| — | Codex-Zweitmeinung, PR | — | — | 9 | Orchestrator | I |
| — | Rollout-Vorbedingung (AK 36) und Rollout-Abnahme (AK 31 Prod) | — | 31, 36 | Merge, Stack-Update (Nutzer) | Betreiber | nach dem Plan |

**Kritischer Pfad:** 1 → 2 → 3 → 5a → 5b → 6a/6b → 8 → Live → 9. Welle B (2, 4, 7) und C laufen
parallel; alles Weitere ist serialisiert, s. „Parallelität und geteilter Checkout".

## Parallelität und geteilter Checkout

Alle Tasks arbeiten im **Haupt-Checkout** `/home/dev/projects/EmotePurge`, nicht in Worktrees:
`docker compose` aus einem Worktree reißt den Dev-Stack ab (fehlende `.env`), und Testcontainers
brauchen den Stack nicht, wohl aber Speicher. Deshalb:

- **Eigentümerschaft geteilter Dateien.** `web/public/i18n/{de,en}.json`: zu jedem Zeitpunkt
  bearbeitet **höchstens ein** Task — Reihenfolge 3 (`callSources`) → 5b (`errors.api`) → 6b
  (`import.confirm`, `audit.details`) → 8 (`import.source`, `import.leaderboard`).
  `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` und `web/src/app/core/i18n/api-error.ts`:
  **nur 5b**; 6a nutzt die Konstante und läuft deshalb nach 5b. `src/EmotePurge.Core/SevenTv/SevenTvLeaderboardSort.cs`:
  **nur 3**; 6a läuft nach 3. `ServiceCollectionExtensions.cs`: **nur 5a**. `ApiFactory.cs`:
  **nur 5b**. `import-source-dialog.ts`, `foreign-import-flow.ts`, `import-trigger.ts`: **nur 8**.
- **Parallel committende Agenten stagen exakte Dateipfade, nie Verzeichnisse**, prüfen vor dem
  Commit `git status` auf fremde Hunks und committen `add` + `commit` in einem Zug (Lehre vom
  2026-07-31: `git add web/public` hat die i18n-Änderungen einer anderen Session mitgenommen).
- **Höchstens zwei Backend-Testläufe (`dotnet test`, Testcontainers) gleichzeitig.** Welle B hat
  mit 2 und 4 genau zwei; Task 7 ist Frontend.
- Ein Task, der eine fremde Datei doch anfassen müsste, bricht ab und meldet es dem Orchestrator,
  statt sie mit zu committen.

## AK-Abdeckungsmatrix (35 vor dem PR, AK 31 als Rollout-Abnahme)

| AK | Task(s) | AK | Task(s) | AK | Task(s) |
|---|---|---|---|---|---|
| 1 | 5a, 5b, Live | 13 | 3, 5a | 25 | 8 |
| 2 | 5b | 14 | 3, Live | 26 | 5b, 6a |
| 3 | 5a, 5b | 15 | 6a | 27 | 6b, 7, 8 |
| 4 | 4, 5a (Tests); Live nur AK 14/Log | 16 | 6a, Live | 28 | 2, 3 |
| 5 | 5a | 17 | 6b | 29 | 9 (+ jeder Task als Grenze) |
| 6 | 4, 5a | 18 | 8 | 30 | Live |
| 7 | 4 | 19 | 7 | 31 | **Rollout-Abnahme auf Prod** (Betreiber, nach Stack-Update; lokales Äquivalent in „Live") |
| 8 | 4, 5a | 20 | 7 | 32 | 9 |
| 9 | 4 | 21 | 8 | 33 | 4, 5a |
| 10 | 4 | 22 | 8 | 34 | 2 |
| 11 | 4 | 23 | 7, 8 | 35 | 5a |
| 12 | 5a | 24 | 5b | 36 | Rollout-Vorbedingung |

## Grenzen dieser Runde

- **Messfenster:** s. Randbedingungen; Task 9 prüft es am Diff, jeder Backend-Task nennt es.
- **Nicht in dieser Runde (Spec §15, verbindlich):** Suchfeld/Tags/Filter (Stufe 3),
  `TRENDING_WEEKLY`, Blättern über 500 hinaus, `refresh`-Bypass, eifrig gewärmter oder
  Redis-Vorrat, `Emote.channels.totalCount`, gemeinsames Api/Worker-Budget (E1 → #165),
  Overlay-Kennzeichnung (E4), Idee C (E7), gespeichertes Header-Minimum (E15), Messung (b).
- **Rollback:** Spec §12 — Revert des PR genügt; einzige Nachwirkung sind bereits geschriebene
  Audit-Zeilen mit `seventv-leaderboard`, die nach einem Revert auf den nackten `EmoteCount`-Zweig
  fallen. Deshalb stehen Vokabel und Feldname vor dem ersten Produktionslauf fest.

## Abweichungen von Spec §8

- **Task 1 (Prüfliste §14) ist neu und steht allein vorne**, wie die Spec es selbst verlangt
  („zuerst abgearbeitet, nicht am Ende") — auch vor T0, weil §14 Annahmen zum Vorschau-Client und
  zur Handler-Suppression enthält, die den Umfang der drei Charakterisierungstests bestimmen.
- **Die Live-Verifikation liegt vor Task 9**, nicht danach: sie kann einen Nachtrag auslösen
  (Ranking-Reihenfolge, Task 7), und die Gates müssen den letzten Code-Commit sehen.
- **T3 ist in 5a/5b geteilt:** der Füllpfad (Vorrat + Budget + Breaker + Nebenläufigkeit) verdient
  `opus` und eigene Aufmerksamkeit; Endpunkt/Filter/Policy/Fehlercode sind normale Umsetzung in
  anderen Dateien. 5b braucht nur den Core-Vertrag, den 5a als **ersten** Commit ablegt.
- **T4 ist in 6a/6b geteilt:** Backend und Frontend berühren keine gemeinsame Datei; der Vertrag
  zwischen ihnen (Vokabel, Feldname, Kind-Name, Codes) steht vollständig in der Spec. Damit laufen
  beide parallel. `leaderboard.model.ts` entsteht in 6b, weil `ImportOrigin` den Typ
  `LeaderboardSort` braucht.
- **Die zwei Playwright-Fälle stehen in Task 9**, nicht in T6: sie brauchen den fertigen Fluss
  und laufen im selben Task wie die Gesamt-Gates.

---

### Task 1: Prüfliste aus Spec §14 am Code nachprüfen

**Ziel:** Jede Zeile der Wiederverwendungs-Prüfliste (Spec §14, 14 Zeilen) am heutigen Code
belegen oder widerlegen — als Befundliste, nicht als Bestätigung. Bei #147 saßen drei von fünf
Review-Befunden in „unverändertem" Code.

**Dateien (nur lesen):** alle in §14 genannten, mit den korrigierten Pfaden:
`src/EmotePurge.Infrastructure/SevenTv/HardenedForeignEmoteSetService.cs` (nicht `Services/`),
`src/EmotePurge.Infrastructure/Telemetry/ProviderRequestTelemetryHandler.cs`,
`src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs:99-116`,
`src/EmotePurge.Infrastructure/SevenTv/ForeignSevenTvBreakerPolicy.cs`,
`src/EmotePurge.Infrastructure/Redis/RateLimitTelemetryStore.cs`,
`web/src/app/shared/seven-tv/{foreign-emote-grid,foreign-import-flow,import-trigger,import-preview,already-present-filter,list-selection}.ts`,
`web/src/app/shared/seven-tv/foreign-emote-grid.spec.ts`.

**Zu prüfen, je Zeile pass/fail mit Zeilenbeleg:**

- Die als „Belegt" geführten Zeilen: stimmt der Beleg heute noch (Zeilenbezüge der Spec sind vom
  2026-09-13; seither ist #164 gemergt — Drift möglich)?
- Die drei „Prüfaufgaben" (T5-Raster, T3-Breaker, T6-Fluss) so weit, wie es **ohne** Umbau geht:
  hält `ListSelection` Schlüssel über einen Listenwechsel (`list-selection.ts:39-42`)? Hält der
  Breaker seinen Zustand ausschließlich in Instanzfeldern, und bezieht
  `HardenedForeignEmoteSetService` die **typisierte** Instanz über die Factory-Lambda in
  `ServiceCollectionExtensions.cs:113`? Liest `buildForeignImportSource` Felder, die eine
  Bestenlisten-Auswahl nicht hat?
- **Zusätzlich, weil beim Planen aufgefallen:** `seventv-foreign-preview` hat in **keiner** Locale
  einen `admin.rateLimits.providers.callSources.*`-Schlüssel (`de.json:350-354`, `en.json`); die
  Admin-Monitoring-Seite (`admin-monitoring-page.ts:524`) zeigt diese #147-Call-Source heute
  vermutlich als rohen Schlüssel. Bestätigen oder widerlegen (gibt es einen Fallback?) — Befund an
  den Orchestrator, **nicht** fixen (s. Offene Punkte).
- Die Handler-Suppression je Request (`ProviderRequestTelemetryHandler.cs`, Options-Key pro
  Request, nicht pro Client) — Voraussetzung für Task 3.

**Abnahme:** Befundliste als Antwort an den Orchestrator (Zeile · Anspruch · Befund · Beleg ·
betroffener Task). **Ein Befund, der einen späteren Task ändert, geht an den Orchestrator zurück,
bevor der Task startet.** Kein Code, kein Commit.

**Abhängigkeiten:** keine. **Modell:** `sonnet` (Urteil über Verhalten, nicht nur Inventur).
**Commit:** keiner.

---

### Task 2: Charakterisierungstests für den #147-Vorschaupfad (Spec T0)

**Ziel:** Drei Tests gegen den **unveränderten** `SevenTvApiClient`, die das festhalten, was die
21 bestehenden Client-Tests nicht decken (AK 34) — damit „unverändert grün" nach Task 3 etwas
beweist.

**Dateien:** Neu oder erweitert in `tests/EmotePurge.Infrastructure.Tests/Unit/` — Vorbild
`SevenTvApiClientForeignTelemetryTests.cs` (Handler-Stub, `RecordingRateLimitTelemetry` aus
`Fakes/`). Empfohlen: eigene Datei `SevenTvApiClientPreviewCharacterizationTests.cs`, damit die
drei nach Task 3 erkennbar unverändert bleiben. **Nur Testdateien.**

**Die drei Fälle (Spec AK 34, F3):**

1. Kaputtes JSON hinter HTTP 200 → genau **eine** Beobachtung unter `SevenTvForeignPreview`,
   Ergebnis `SevenTvUnavailable` (heute `SevenTvApiClient.cs:440-451`).
2. `Ratelimit-Limit/-Remaining/-Reset` der Antwort landen in `RateLimitLimit/Remaining/Reset` der
   Beobachtung (`:468-476`) — Twitchs Schreibweise, die heute gelesen wird.
3. Ein **HTTP-Response-Header** `x-ratelimit-search-reset` auf dem Vorschaupfad ändert weder
   `RetryAfterSeconds` der Beobachtung noch das `RetryAfter` des Ergebnisses (heute wird der Hinweis
   nur aus `errors[].extensions.headers` gelesen, `:669-699`).

**Tests:** Regel 11, Unit, container-frei. **Abnahme:** die drei Tests sind gegen den heutigen
Code grün; `dotnet test EmotePurge.slnx` grün; keine Produktivdatei im Diff.

**Abhängigkeiten:** Task 1 (Befunde zu Vorschau-Client und Handler-Suppression können die drei
Fälle verschieben). **Modell:** `sonnet`.
**Commit (eigener, muss vor Task 3 liegen):** `test(seventv): characterize the foreign preview client path`

---

### Task 3: Client — gemeinsamer v4-Seitenabruf und `SearchEmotesAsync` (Spec T1)

**Ziel:** Die 429-Semantik aus `FetchPreviewPageAsync` (E11, F3) einmal herausziehen, darauf die
Bestenlisten-Suche aufsetzen (F7), die 7TV-Such-Header erfassen und im Ergebnis mitliefern, neue
Call-Source samt Locale-Schlüssel und Store-Integrationsfall. Das Log-Ereignis je Upstream-Request
gehört dem Service (Task 5a), nicht dem Client.

**Dateien:**
- `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:405-476` (gemeinsamer privater
  v4-Seitenabruf mit Call-Source-Parameter; `RecordForeignPreviewObservation` bekommt die
  Call-Source als Parameter; `BuildForeignImageUrl :750-753` wiederverwenden; `V4GqlPath` wird an
  `:417` **und** `:563` benutzt). **`:18` und `:72-118` nicht anfassen** (Messfenster).
- `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiDtos.cs` (Such-DTOs: flaches `Emote` ohne
  `alias`, `flags.animated`, `scores`, `totalCount`, `pageCount`).
- `src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs` (neue Methode), `SevenTvModels.cs`
  (Ergebnistyp), **neu** `src/EmotePurge.Core/SevenTv/SevenTvLeaderboardSort.cs`.
- `src/EmotePurge.Core/Services/IRateLimitTelemetry.cs:88-109` (neue Call-Source).
- `web/public/i18n/de.json:350-354`, `en.json` (Schlüssel `admin.rateLimits.providers.callSources.seventv-leaderboard`, F5).

**Verträge, die spätere Tasks benutzen (Namen sind bindend):**

- `SevenTvLeaderboardSort` (Core, Enum) mit genau zwei Werten und einer Zuordnung zu den
  ordinalen Wire-Codes `TRENDING_DAILY`/`TOP_ALL_TIME` in beide Richtungen (`TryParse` strikt
  ordinal — `trending_daily` ist ungültig). Dieselben Strings gehen als GraphQL-`sort`-Variable an
  7TV (Variablenform aus der Sonde vom 2026-09-13 übernehmen, nicht raten).
- `ISevenTvApiClient.SearchEmotesAsync(SevenTvLeaderboardSort sortBy, int page, CancellationToken)`
  → `SevenTvEmoteSearchPageResult`: Status `Ok | Unavailable | RateLimited`, `RetryAfter` (nur bei
  `RateLimited`, sonst `null`), und bei `Ok` eine Seite `SevenTvEmoteSearchPage { TotalCount,
  PageCount, Items }`. `Items` als `SevenTvEmoteSetPreviewItem` mit `Alias = DefaultName` — so
  bleibt die Zeilenabbildung auf `ForeignEmoteRow` dieselbe wie in `ForeignEmoteSetService.cs:129`.
- `perPage` ist im Client fest 250 (E5); `query`, `filters`, `tags`, `defaultZeroWidth` werden
  **nicht** gesendet/geholt.
- `RateLimitCallSources.SevenTvLeaderboard = "seventv-leaderboard"`.
- Beobachtung: genau eine je Upstream-Request unter der neuen Call-Source; **HTTP-Header**
  `x-ratelimit-search-limit/-remaining/-reset` → `RateLimitLimit/Remaining/Reset`; `RetryAfter`
  in dieser Reihenfolge: `x-ratelimit-search-reset` → GraphQL-Hinweis → `Retry-After` → nichts.
  **Der Vorschaupfad liest den neuen Header weiterhin nicht** (Task 2, Fall 3).
- **Ein maßgebliches Log-Ereignis je Upstream-Request, und es gehört dem Service (Task 5a).**
  Der Client schreibt für Suchanfragen **keine** eigene Information-Zeile; stattdessen trägt
  `SevenTvEmoteSearchPageResult` die drei Such-Header-Werte (`RateLimitLimit/Remaining/Reset`,
  Strings wie in der Beobachtung, `null` wenn nicht gesendet) **in jedem Ausgang**, auch bei
  `Unavailable`/`RateLimited`. Grund: Spec §6 verlangt in **einer** Zeile `sortBy`, `page`,
  `remaining`, `reset` **und** die Requests im Budgetfenster — Letzteres kennt nur der Service, und
  ein Logging-Scope käme in der Console-Ausgabe nicht an (kein `IncludeScopes` konfiguriert).
  Zwei Zeilen würden die Zählung für AK 31 mehrdeutig machen.

**Grenzfälle, die der Parser trennen muss:** HTTP 429; HTTP 200 mit `extensions.status: 429`
(sonst sieht ein Fehlschlag wie eine leere Liste aus und würde 1 h gecacht — teuerster
Einzelfehler); Validierungsablehnung (z. B. `perPage`-Fehler, **ohne** Such-Header) → `Unavailable`;
5xx/Timeout/Parsefehler → `Unavailable`.

**Tests (Regel 11, Unit, `tests/EmotePurge.Infrastructure.Tests/Unit/`):** +8 nach Spec §10
(Seite parsen inkl. `animated` → Bildadresse mit `4x_static.webp` nur bei `true`; literales 429;
429 hinter 200; Such-Header erfasst; `RetryAfter`-Reihenfolge; Validierungsablehnung; genau eine
Beobachtung je Request; keine Beobachtung unter `seventv-foreign-preview`/`seventv-rest` für diese
Requests; Header-Werte im Ergebnis auch bei Fehlausgängen). Dazu **+1 Integrationsfall** in
`tests/EmotePurge.Infrastructure.Tests/Integration/RateLimitTelemetryStoreTests.cs` (Redis-Fixture):
eine Beobachtung unter `RateLimitCallSources.SevenTvLeaderboard` erscheint mit Minuten- und
24-h-Zähler und Header-Sample, getrennt von `seventv-foreign-preview` (Spec §10, §14-Zeile
`RateLimitTelemetryStore`).

**Abnahme:** die 21 bestehenden Client-Tests **und** die drei aus Task 2 ohne Änderung an den
Testdateien grün (`git diff --stat` über `tests/` zeigt nur die neuen Dateien); Sichtprüfung, dass
der Vorschaupfad weiter `SevenTvForeignPreview` meldet; `dotnet build --no-incremental` ohne neue
Warnungen; `dotnet test EmotePurge.slnx` grün (Docker läuft — der Store-Fall braucht Redis);
`dotnet format` sauber.

**Messfenster:** kein `src/EmotePurge.Worker/**`, `GqlUsersQuery`/`ResolveTwitchUserIdAsync`
unangetastet. **Abhängigkeiten:** Task 1 (Befunde), Task 2 (committet). **Modell:** `sonnet`.
**Commits:** `refactor(seventv): share the v4 page fetch between preview and search` (nur das
Herausziehen, alle 24 Tests grün) · `feat(seventv): add the leaderboard search to the 7TV client`
(neue Methode, DTOs, Header, Call-Source, Locale-Schlüssel, Tests).

---

### Task 4: Pure Klassen — Vorrat, Fensterbudget, Haltbarkeitsregel (Spec T2)

**Ziel:** Die drei TimeProvider-getriebenen Bausteine des Sicherheitsarguments („zwei Dinge tragen
die Sicherheit", Spec §6) als pure Klassen, **Tests vor dem Service**. Hier sitzt der
wahrscheinlichste Fehler (Doppel-Fill beim Ablauf).

**Dateien (alle neu):** `src/EmotePurge.Infrastructure/SevenTv/SevenTvLeaderboardStore.cs`,
`…/SevenTvLeaderboardRequestBudget.cs`, `…/SevenTvLeaderboardTtlPolicy.cs` (Haltbarkeitsregel;
Name frei, muss aber in 5a referenziert werden), Tests in
`tests/EmotePurge.Infrastructure.Tests/Unit/`. Für den Fake-Zeitgeber: heute hat jede Testdatei
ihre eigene private `FakeClock`; einen wiederverwendbaren unter `Fakes/` anlegen ist erlaubt,
bestehende Tests **nicht** umziehen.

**Verträge (bindend für 5a):**

- `SevenTvLeaderboardRequestBudget(int maxRequests, TimeSpan window, TimeProvider)`, synchron
  `bool TryCharge(out int usedInWindow)`, Wartezeit 0, gleitendes Fenster, Verweigerung verbraucht
  nichts. **Kleine Schwester**, nicht Umbau von `ForeignEmoteSetProviderBudget` (E10).
- `SevenTvLeaderboardStore<TValue>`: Schlüssel `string` (der Wire-Code), Wertetyp generisch, damit
  der Store nichts aus 5a kennt. Ein Aufruf `GetOrFillAsync(key, factory, callerToken)`; die
  Factory liefert Wert **plus** Haltbarkeit, läuft **unter `CancellationToken.None`**, der Aufrufer
  wartet mit **seinem** Token auf den geteilten Task. Eintrag = `Lazy<Task<…>>` plus `ExpiresAt`,
  das die Factory **beim Abschluss** setzt; ein laufender Eintrag läuft nie ab. Ersatz per
  `TryUpdate` gegen den gelesenen Eintrag (Verlierer nimmt den Gewinner). `Faulted`/`Canceled`
  gilt als sofort abgelaufen und wird beim nächsten Leser **ersetzt, nie entfernt**. Kein
  Eviction (2 Einträge).
- Haltbarkeitsregel als pure Funktion: Eingabe Ausgangsart (Treffer/leer · RateLimited mit
  optionalem `RetryAfter` · Unavailable · Breaker offen mit `RemainingOpenTime` · Budget
  verweigert) → `TimeSpan` nach Spec §6-Tabelle (1 h · Klemme [60 s, 1 h], ohne Hinweis 60 s ·
  60 s · Klemme [1 s, 1 h] · 30 s).

**Tests (Regel 11, Unit, container-frei), nach Spec §10:** Store +7 (Treffer bis Ablauf; Ablauf →
eine Füllung; **Rennen zweier Leser genau beim Ablauf → eine Factory** (AK 9); `Faulted` und
`Canceled` ersetzt statt entfernt, Größe bleibt 2 (AK 10); Füllung überlebt Aufrufer-Abbruch (AK
11); laufender Eintrag läuft nie ab; leere Liste wie Treffer (AK 8)). Budget +6 (10 ok, 11.
verweigert ohne Wartezeit, Fenster gleitet, Verweigerung verbraucht nichts, `usedInWindow`, **zwei
Instanzen auf demselben `TimeProvider` je ≤ 10** (AK 33)). Haltbarkeitsregel +7 (AK 7 inkl. 30 s →
60 s, 5 h → 1 h, ohne Hinweis → 60 s).

**Abnahme:** alle neuen Tests grün, `dotnet test EmotePurge.slnx` grün, `dotnet format` sauber.
Keine bestehende Datei geändert. **Abhängigkeiten:** Task 1. **Modell:** `opus` (Nebenläufigkeit,
Single-Flight, negatives Cachen). **Commit:** `feat(seventv): add the leaderboard stock, request budget and ttl rule`

---

### Task 5a: Core-Vertrag und `SevenTvLeaderboardService` — der Füllpfad (Spec T3, Teil 1)

**Ziel:** Der Service komponiert Vorrat, Budget, Keyed-Breaker und Client nach dem Füllpfad in
Spec §6, mit Alarm bei ≥ 6 und dem Neustart-Vertrag.

**Dateien:** **neu** `src/EmotePurge.Core/Services/ISevenTvLeaderboardService.cs` (Regel 4/5),
**neu** `src/EmotePurge.Infrastructure/Services/SevenTvLeaderboardService.cs`,
`src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs:99-116` (Keyed-Breaker
`AddKeyedSingleton<ForeignSevenTvBreakerPolicy>` mit einem Schlüssel-Konstanten, Store, Budget als
Singletons, Service), Tests unter `tests/EmotePurge.Infrastructure.Tests/Unit/`.

**Core-Vertrag (erster Commit, damit 5b starten kann):**
`ISevenTvLeaderboardService.GetLeaderboardAsync(SevenTvLeaderboardSort, CancellationToken)` →
`SevenTvLeaderboardResult` mit Status `Ok | SevenTvUnavailable | SevenTvRateLimited |
BudgetRefused` und bei `Ok` `SevenTvLeaderboardResponse { SortBy (Wire-Code als **string**, damit
das JSON `"TRENDING_DAILY"` trägt), TotalCount, Truncated, Emotes: ForeignEmoteRow[] }`.
`ForeignEmoteRow` wird wiederverwendet (`IForeignEmoteSetService.cs:135-141`), `Name =
DefaultName`.

**Füllpfad, verbindlich (Spec §6 „Füllpfad, in Reihenfolge"):** Vorrat-Treffer → fertig. Sonst ein
Factory-Lauf je Sortier-Schlüssel, je Upstream-Seite ab Seite 1: Breaker `TryAcquire` → Budget
`TryCharge` (Alarm-Warnung einmal je Fenster bei `usedInWindow ≥ 6`, **vor** dem Deckel) → Client
→ Breaker-Rückmeldung mit Generation. Budget verweigert: `BudgetRefused`, 30 s, **kein**
Upstream, `ReleaseProbeWithoutOutcome`, keine Breaker-Meldung. Seite 2 nur bei `pageCount ≥ 2`, im
selben Lauf, erneut belastet. Zusammensetzen: 7TV-Reihenfolge, nach Id dedupliziert (erstes
Vorkommen gewinnt), `totalCount` von Seite 1, `truncated = totalCount > Count`. **Fehler einer
Seite → der ganze Eintrag übernimmt Ausgang und Haltbarkeit; Seite 1 wird verworfen.** Backstop
(unerwartete Exception) meldet dem Breaker `OtherFailure`. Breaker-Rolle: Ausbreitung über die
zwei Schlüssel — offen → `Failed` nach Öffnungsgrund mit `RemainingOpenTime` als Haltbarkeit, ohne
Upstream. **Das eine Log-Ereignis je Upstream-Request** (Information, englisch, s. Task 3)
schreibt der Service unmittelbar nach jedem Client-Aufruf — auch bei `Unavailable`/`RateLimited`
— mit `sortBy`, `page`, Ausgang, `remaining`, `reset` (aus dem Client-Ergebnis) und
`usedInWindow` (aus `TryCharge`). Genau eine Zeile je belastetem Request, keine bei
Budget-Verweigerung oder offenem Breaker (dort gab es keinen Request; Debug reicht). Auf diese
Zeile stützen sich AK 31 und die Live-Verifikation.

**Breaker-Probe-Vertrag (`ForeignSevenTvBreakerPolicy.cs:112-118`):** jede mit `Allowed: true`
erhaltene Entscheidung wird **genau einmal** mit ihrer Generation beantwortet — `RecordSuccess`,
`RecordFailure` oder `ReleaseProbeWithoutOutcome` —, sonst bleibt `_probeInFlight` stehen und der
Breaker klemmt über seine Offenzeit hinaus. Der Backstop im `finally` (wie
`HardenedForeignEmoteSetService.cs:146-151`) deckt unerwartete Exceptions **je Seite** ab, nicht
nur je Factory-Lauf: eine Exception nach Seite 1 darf Seite 1's Entscheidung nicht ein zweites Mal
beantworten und Seite 2's nicht unbeantwortet lassen.

**Tests (Regel 11, Unit mit Client-Ersatz, NSubstitute):** +12 nach Spec §10 — zwei Seiten
zusammengesetzt + dedupliziert; `truncated`; Seite 2 nur bei `pageCount ≥ 2`; Fehlschlag Seite 2 →
ganzer Eintrag Fehler; **Breaker-Ausbreitung** über die Sortier-Schlüssel bei **geschlossenem**
#147-Breaker (AK 12 — zwei getrennte Instanzen im Test); Budget verweigert → kein Upstream + 30 s
+ keine Breaker-Meldung; Alarm genau einmal bei 6 (AK 6); **20 Füllversuche in 60 min → genau 10
Beobachtungen, der 11. ohne Beobachtung und ohne Breaker-Meldung** (AK 4); 1.000 Aufrufe über
beide Sortierungen in einer simulierten Stunde → 4 Upstream-Requests (AK 5); Kaltstart = genau 4
Freigaben (AK 33); verschobene Seitengrenze nach Seite-2-Fehler und nach Ablauf → keine doppelte
Id, keine Mischung zweier Füllvorgänge, Seite 1 neu geholt (AK 35 a/b); Seite 1 gewährt, Seite 2
verweigert → `BudgetRefused`, Freigabe verbraucht (AK 35); `refresh`/`page` haben keine Entsprechung
im Vertrag (AK 3 auf Service-Ebene: zwei Aufrufe in einer Stunde → ein Satz Upstream-Requests).
**Dazu +2 für den Probe-Vertrag:** (a) der Client wirft nach einem `Allowed`-Probe unerwartet →
genau **ein** `RecordFailure(OtherFailure)` mit der Generation dieser Entscheidung, der Eintrag im
Vorrat ist ein sofort abgelaufener `Faulted`, und nach Ablauf der Offenzeit ist **erneut** eine
Probe möglich (`TryAcquire` liefert wieder `Allowed`); (b) dieselbe Exception auf Seite 2 nach
erfolgreicher Seite 1 → Seite 1 wurde genau einmal, Seite 2 genau einmal beantwortet. Die
+1 Log-Zeilen-Prüfung: ein Fehlausgang des Clients erzeugt trotzdem genau eine Zeile mit
`usedInWindow`.

**Prüfaufgabe aus §14 (Breaker):** nach der Keyed-Registration bezieht
`HardenedForeignEmoteSetService` weiter die **typisierte** Instanz (`ServiceCollectionExtensions.cs:113`) —
im Diff belegen.

**Abnahme:** neue Tests grün; die 9 + 8 + 12 Tests von `HardenedForeignEmoteSetServiceTests`,
`ForeignEmoteSetProviderBudgetTests`, `ForeignSevenTvBreakerPolicyTests` unverändert grün;
`dotnet test EmotePurge.slnx` grün; `--no-incremental` ohne neue Warnungen; `dotnet format`.

**Messfenster:** keine Worker-Datei; DI-Änderung nur additiv. **Abhängigkeiten:** Task 3 (Client),
Task 4 (pure Klassen). **Modell:** `opus`. **Commits:** `feat(seventv): define the leaderboard
service contract` (Core-Datei allein — Startsignal für 5b) · `feat(seventv): serve the 7TV
leaderboard from a guarded in-process stock` (Service, DI, Tests).

---

### Task 5b: Endpunkt, Validierungsfilter, Policy, Fehlercode, Api.Tests (Spec T3, Teil 2)

**Ziel:** `GET /api/seventv/leaderboard?sortBy=…` nach Spec §4/§5 mit getesteter Filter-Reihenfolge.

**Dateien:** `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` (zweite `MapGroup` neben
`:25-28`: nur `RequireAuthorization()` + `RequireRateLimiting(SevenTvLeaderboard)` +
`AddEndpointFilter<LeaderboardSortValidationFilter>`; **kein** `ChannelNameValidationFilter`, kein
`UsageStatsAccessAuthorizationFilter`); **neu**
`src/EmotePurge.Api/Validation/LeaderboardSortValidationFilter.cs` (Muster
`ChannelNameValidationFilter`, liest den Query-Wert `sortBy`, parst strikt ordinal über den
Core-Typ aus Task 3, antwortet 400 `invalid_leaderboard_sort` ohne Service-Aufruf);
`src/EmotePurge.Api/Validation/ApiErrorCodes.cs` (`InvalidLeaderboardSort`);
`src/EmotePurge.Api/RateLimiting/RateLimitPolicyNames.cs`, `RateLimitingOptions.cs:56` + `:64-72`
(Policy `SevenTvLeaderboard`, 20/min je Nutzer, **und** ihr `Validate()`-Aufruf — Fail-Fast),
`src/EmotePurge.Api/Program.cs:188` (`AddFixedWindowPolicy` daneben);
`web/src/app/core/i18n/api-error.ts` (`KNOWN_API_ERROR_CODES`), `web/public/i18n/de.json:1097`-Nachbar,
`en.json` (`errors.api.invalid_leaderboard_sort`); `tests/EmotePurge.Api.Tests/ApiFactory.cs`
(Substitut `ISevenTvLeaderboardService`, Muster `ForeignEmoteSet :69-74`), **neu**
`tests/EmotePurge.Api.Tests/SevenTvLeaderboardEndpointTests.cs`.

**Vertrag:** Handler dünn — Status → HTTP nach Spec §5-Tabelle: `Ok` 200 mit Antwortform aus
5a; `SevenTvUnavailable`, `SevenTvRateLimited`, `BudgetRefused` → 503 **vorhandener** Code
`foreign_channel_seventv_unavailable` (E13). Handler kennt **keine** Parameter `page`, `perPage`,
`refresh`, `query`, `tags`, `filters`, `Cache-Control`; unbekannte Query-Parameter werden ignoriert.
Handler bezieht nichts direkt aus `AppDbContext`/`IConnectionMultiplexer` (Regel 4).

**Tests (Regel 11, `tests/EmotePurge.Api.Tests`, container-frei):** +8 — 401 anonym; 200 für
beide Sortierungen mit `sortBy`-Echo; 400 für fehlenden, kleingeschriebenen und fremden
(`TRENDING_WEEKLY`) `sortBy` **ohne** Service-Aufruf (`Received(0)`); **429 bei ungültigem `sortBy`
über Budget** (Middleware vor Filter — Vorbild `SevenTvForeignEmoteSetEndpointTests`); 503-Abbildung
aller drei Fehlstatus; `?refresh=true&page=3` ändert nichts am Aufruf des Substituts (AK 3 auf
Endpunkt-Ebene). `RateLimitPolicyBudgetTests` enumeriert Policies **nicht** (zwei `[Fact]` gegen
Routen) — der AK-26-Vorbehalt greift also nicht; die Per-Nutzer-Deckelung ist durch den 429-Fall
belegt. `api-error-locales.spec.ts` grün (AK 24).

**Abnahme:** `dotnet test EmotePurge.slnx` grün; `npm --prefix web test -- --watch=false` grün
(wegen `api-error.ts` + Locales); `dotnet format`, `npm --prefix web run format` + `lint`.

**Abhängigkeiten:** 5a's Vertrag-Commit (`ISevenTvLeaderboardService.cs` auf dem Branch); der
Rest von 5a kann parallel laufen. **Modell:** `sonnet`. **Commits:** `feat(api): add the 7TV
leaderboard endpoint with its sort validation and rate-limit policy` · Fehlercode + Locales dürfen
in denselben Commit (Regel 7: alle drei Stellen zusammen).

---

### Task 6a: Herkunft im Backend — `leaderboardSort`, Vokabeltabelle, Audit-Projektion (Spec T4, Teil 1)

**Ziel:** Die sechs Stationen der Herkunftskette (F1) auf der Backend-Seite: die vierte Vokabel
darf `sync-imported` passieren und im Audit-Log nicht verloren gehen.

**Dateien:** `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs:142` (Vokabel
`"seventv-leaderboard"`), `:164` (Binärabgleich → **Vokabeltabelle** aus F1 Station 3: `channel`/
`seventv-channel` Name Pflicht + `leaderboardSort` null; `file` beide null; `seventv-leaderboard`
Name null + `leaderboardSort` Pflicht aus der Allowlist; Abweichung 400 `invalid_source_kind`, Wert
außerhalb der Allowlist 400 `invalid_leaderboard_sort`), `:175-176` (Weitergabe), `:262`
(`SyncImportedRequest` um optionales `string? LeaderboardSort`);
`src/EmotePurge.Core/Services/IEmoteService.cs:39` + `src/EmotePurge.Infrastructure/Services/EmoteService.cs:107`, `:129`
(`MarkImportedAsync` um `leaderboardSort`, in `DetailsJson` neben `sourceKind`);
`src/EmotePurge.Core/Services/IAuditLogQueryService.cs:20-30` (`Kinds.ImportedFromLeaderboard`);
`src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs:30-32`, `:155-185` (vierte Vokabel,
Zweig **vor** der `source`-Prüfung: `Text` = Sortiercode, `Count` = Emote-Zahl; unbekannter Code →
nackter `EmoteCount`); Tests: `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs:387-447`
(bestehende Fälle bleiben; neue daneben oder in eigener Datei),
`tests/EmotePurge.Infrastructure.Tests/Integration/AuditLogQueryServiceTests.cs`.

**Eigentum:** `ApiErrorCodes.InvalidLeaderboardSort` gehört Task 5b, der Core-Typ
`SevenTvLeaderboardSort` Task 3 — 6a läuft nach beiden und legt keins davon selbst an. Der
Sortiercode bleibt im Audit **sprachneutral** (E9).

**Tests:** `Api.Tests` +6 (Vokabeltabelle in allen Richtungen, AK 15: akzeptiert
`{seventv-leaderboard, null, TRENDING_DAILY}`; 400 für Vokabel mit Name, ohne `leaderboardSort`,
mit fremdem Wert, und für jede andere Vokabel **mit** `leaderboardSort`; Weitergabe des Feldes an
`MarkImportedAsync` über das `IEmoteService`-Substitut). `AuditLogQueryServiceTests` +2
(Integration, Postgres: Zeile mit Code → `ImportedFromLeaderboard` mit Text; unbekannter Code →
`EmoteCount`; `seventv-channel` unverändert).

**Abnahme:** `dotnet test EmotePurge.slnx` grün inkl. der unveränderten Fälle
`AuthFilterMatrixTests.cs:387-447`; `dotnet format`. **Messfenster:** `EmoteService` ist
Api-Pfad; keine Migration (`DetailsJson` ist jsonb, additiv). **Abhängigkeiten:** Task 3, Task 5b
(Eigentum, s. o.); parallel zu 6b (keine gemeinsame Datei).
**Modell:** `sonnet`. **Commit:** `feat(audit): carry the leaderboard sort as the import origin`

---

### Task 6b: Herkunft im Frontend — `ImportOrigin`, Leitung, Bestätigung, Audit-Anzeige (Spec T4, Teil 2)

**Ziel:** Der vierte `ImportOrigin`-Zweig, erschöpfend zerlegt, so dass `reportImported` nach den
7TV-Mutationen **beide** Felder korrekt sendet (F1 Station 6, F6-Klasse aus #147).

**Dateien:** **neu** `web/src/app/core/seven-tv/leaderboard.model.ts` (`LeaderboardSort =
'TRENDING_DAILY' | 'TOP_ALL_TIME'`, `SevenTvLeaderboardResponse`, `LeaderboardImportResult {
sortBy; rows }` — Vertrag für Task 8); `web/src/app/core/seven-tv/import-source.ts:12-38`, `:53-61`
(vierter Zweig `{ kind: 'seventv-leaderboard'; sortBy }`; `importOriginSourceChannelName` → `null`;
**neue** erschöpfende Hilfsfunktion `importOriginLeaderboardSort` mit `never`-Arm; Docstring);
`web/src/app/core/emotes/emote-admin.service.ts:37-38` (Union, `SyncImportedBody.leaderboardSort:
LeaderboardSort | null`); `web/src/app/core/seven-tv/seven-tv-import.service.ts:303-304`
(beide Felder über die beiden Hilfsfunktionen, nie `=== 'seventv-leaderboard'`);
`web/src/app/shared/seven-tv/import-confirm-dialog.ts:76-92`, `:290-298` (dritter Herkunftszweig
`leaderboardOrigin()` → `import.confirm.originLeaderboard` mit übersetztem Sortierlabel, E2);
`web/src/app/core/audit/audit.model.ts:32-33`, `web/src/app/shared/audit/audit-actions.ts:51-57`,
`audit-row.ts:53-68` (`renderDetail` übersetzt für **diese eine** Kind den `text`-Code über
`audit.details.leaderboardSort.<code>` in `title`; unbekannter Code → kein Absturz, Verhalten wie
heute für fehlende Schlüssel); `web/public/i18n/de.json`, `en.json` (`import.confirm.originLeaderboard`,
`audit.details.importedFromLeaderboard`, `audit.details.leaderboardSort.{TRENDING_DAILY,TOP_ALL_TIME}`
— Labels „7TV Trend heute"/„7TV Top insgesamt" bzw. englisches Pendant).

**Tests (Regel 12, co-located Specs):** `import-source.spec.ts` (`importOriginSourceChannelName`
→ `null`; `importOriginLeaderboardSort` beide Werte + `null` für die drei alten);
`seven-tv-import.service.spec.ts` (`reportImported` sendet beide Felder, `HttpTestingController`);
`import-confirm-dialog.spec.ts` (Herkunftszeile nennt die Sortierung; Datei/Kanal unverändert — AK
17); Audit-Anzeige (`renderDetail` liefert Schlüssel + `title` mit übersetztem Code; unbekannter
Code degradiert). Verhalten, keine Vorlage.

**Abnahme:** `npm --prefix web test -- --watch=false` grün — **die Union wird an `:53-61`
erschöpfend zerlegt, der Build ist bis zur Ergänzung rot; das ist die laute Stelle und gewollt**;
`format` + `lint`. **Abhängigkeiten:** Task 5b abgeschlossen (Locale-Reihe — 6b ist der nächste
Bearbeiter von `de.json`/`en.json`); parallel zu 6a. **Modell:** `sonnet`.
**Commit:** `feat(web): route the leaderboard origin through the import report and the audit view`

---

### Task 7: Raster — vier additive Inputs, instanzeindeutige Select-id (Spec T5)

**Ziel:** `ForeignEmoteGrid` trägt die Bestenliste ohne Regression an den 13 #147-Fällen (E12, F2).

**Dateien:** `web/src/app/shared/seven-tv/foreign-emote-grid.ts:145-150` (`<select>` nur ohne
`forcedSortMode`), `:171`, `:208` (Erklärsatz, Score-Kachel folgen dem **effektiven** Modus),
`:269-272` (`forcedSortMode: null | 'topAllTime' | 'trending'`, Default `null`; effektiver Modus
= `forcedSortMode ?? sortMode()`; `sortSelectId` über einen Modulzähler instanzeindeutig),
`:280-317` (`sortedEmotes` nach effektivem Modus — darüber auch `ListSelection`), `:367-395`
(`cellLabel`/`spokenScore` nach effektivem Modus); drei Inputs `emptyMessageKey`,
`truncatedMessageKey`, `scoreHintKey` mit den heutigen `import.foreignChannel.*`-Schlüsseln als
Default; `foreign-emote-grid.spec.ts` (nur **neue** Fälle; die 13 bestehenden bleiben unangetastet).

**Regel 14 beachten:** der effektive Modus ist ein `computed()` über Signale (Input + `sortMode`),
kein Feld.

**Tests (Regel 12):** +6 — mit `forcedSortMode`: kein `<select>`, Score-Kachel und Erklärsatz
sichtbar, Score im `aria-label` jeder Zelle, `sortedEmotes` nach erzwungenem Modus (AK 19); die
drei Caption-Inputs wirken und fallen ohne Angabe auf die heutigen Schlüssel zurück (AK 20, 23);
zwei Instanzen haben verschiedene Select-ids (AK 20).

**Abnahme:** `ng test --include` für die Spec, dann `npm --prefix web test -- --watch=false`
gesamt grün, **`git diff` zeigt keine geänderte Zeile in den 13 bestehenden Fällen**; `format` +
`lint`. **Risikohinweis aus Spec §17 (letzte Zeile):** weicht die Score-Sortierung live von 7TVs
Rangfolge ab (AK 30), wird `sortedEmotes` bei `forcedSortMode` auf die gelieferte Reihenfolge
festgelegt — kleiner Nachtrag hier, kein Vertragsbruch. **Abhängigkeiten:** Task 1.
**Modell:** `sonnet`. **Commit:** `feat(web): let a caller force the emote grid's sort mode and captions`

---

### Task 8: Verdrahtung — `LeaderboardStep`, Dialog, Fluss, `DECISIONS.md` (Spec T6)

**Ziel:** Die dritte Option im Dialog, der neue Schritt, der HTTP-Service, der Import-Fluss — und
der `DECISIONS.md`-Eintrag im selben Commit, weil hier die Konvention (Sortierung nach Score als
Vorbelegung auf der Bestenliste) tatsächlich ausgeliefert wird (E3, AK 25, Regel 3).

**Dateien:** **neu** `web/src/app/core/seven-tv/seven-tv-leaderboard.service.ts` (+ spec;
`load(sortBy)` → `GET /api/seventv/leaderboard?sortBy=…`, **ohne** `refresh`-Option), **neu**
`web/src/app/shared/seven-tv/leaderboard-step.ts` (+ spec), `import-source-dialog.ts:37-66`,
`:110-180`, `:192-252` (dritter `SourceOption` `step: 'leaderboard'`, dritter `@case`, `titleKey`
→ `import.leaderboard.title`, `viewChild(LeaderboardStep)`, `gridVisible` = Kanal-Grid **oder**
Bestenlisten-Grid, `ImportSourceDialogResult` um `{ kind: 'leaderboard'; picked }`,
`continueWithLeaderboard` — F4), `foreign-import-flow.ts` (**daneben** `startLeaderboardImportFlow`
+ `buildLeaderboardImportSource`: `name = defaultName`, `dedupeImportRows`, `discardedRows: 0`;
**kein** Feld aus `ForeignChannelImportResult` — §14-Prüfaufgabe), `import-trigger.ts:120-125`
(Zweig `result.kind === 'leaderboard'`, Ziel = Kanal der Seite), `web/public/i18n/de.json`,
`en.json` (`import.source.leaderboard.{label,hint}`, `import.leaderboard.{title,sortLabel,sort.TRENDING_DAILY,sort.TOP_ALL_TIME,empty,truncated.TRENDING_DAILY,truncated.TOP_ALL_TIME,scoreHint}`
— Score-Beschriftung netzwerkweit, nie bloß „Beliebtheit"), `docs/DECISIONS.md` (neuer Eintrag
**ganz oben**, Format der Nachbarn: `### 2026-09-13 — <Satz>`, `**Betrifft:**`-Zeile, `---`;
**englisch**, weil neu).

**Verhalten des Steps (Spec §7):** Sortier-`<select>` als **Server**-Dimension, Default
`TRENDING_DAILY`; lädt beim Betreten und bei jedem Wechsel; Zustände `loading`/`error`/`loaded` wie
`ForeignChannelStep` (`:67-91`); bettet das Raster mit `forcedSortMode` (`TRENDING_DAILY` →
`'trending'`, `TOP_ALL_TIME` → `'topAllTime'`) und den drei Caption-Schlüsseln ein; `showsGrid`,
`result()` (`null` ohne Auswahl), `focusFirstControl` nach dem Vertrag von
`foreign-channel-step.ts:191-209`. **Sortierwechsel leert die Auswahl sichtbar (E14):** das Raster
wird für die neue Liste **neu instanziiert** (Zustand `loading` dazwischen), `result()` → `null`,
„Weiter" gesperrt. `truncated` mit **zwei** Schlüsseln nach `sortBy`, beide mit `loaded` und
`totalCount` (AK 23). Fehler (503) → Fehlerzustand mit `errors.api.*`-Text, Retry lädt nur den
Vorrat. Kein `page`, kein `refresh`, keine Paginierung.

**`DECISIONS.md`-Inhalt (Pflicht):** Punkt 5 und P5' (2026-09-10) auf die **Beschriftung**
eingegrenzt — Sortieren nach Score ist auf der Bestenliste zulässig, weil die Rangfolge der Inhalt
ist; Fremdkanal-Ansicht unverändert (`'none'`); Vermerk, dass die Label-Angabe in Punkt 5 (`:507`)
von Punkt 10 (`:538-546`) überholt ist; In-Process-Vorrat mit **Replica-Bedingung** (zweite
Api-Replica → Redis fail-closed **und** verteiltes Fensterbudget, sonst Deckel `Replicas × 10`);
vierte Vokabel `seventv-leaderboard` + Feld `leaderboardSort` als dauerhafter Audit-Vertrag.

**Tests (Regel 12):** `LeaderboardStep` +7 (lädt beim Betreten; Sortierwechsel leert Auswahl +
`result()` `null` + Sperrgrund; Fehler-, Leerzustand; `truncated`-Schlüssel je Sortierung; Fokus);
Dialog +4 (dritte Option, Titel, `gridVisible` aus dem Bestenlisten-Step, „Weiter" gesperrt/frei,
Schließen mit `leaderboard`-Ergebnis — AK 22); `seven-tv-leaderboard.service.spec.ts`
(`HttpTestingController`, sendet nur `sortBy`); `foreign-import-flow.spec.ts`
(`buildLeaderboardImportSource`), `import-trigger.spec.ts` (Zweig); **Regressionsfall
`nameCollisions`** mit Bestenlisten-Zeilen (AK 18 — Hinweis erscheint, blockiert nichts).

**Abnahme:** `npm --prefix web test -- --watch=false` grün; `format` + `lint`; im Browser gegen
die laufende Api (`docker compose up -d postgres redis`, `dotnet run --project src/EmotePurge.Api`,
`npm --prefix web start`) beide Sortierungen laden, Sortierwechsel, Weiter, Bestätigungsdialog mit
Sortierlabel — **noch kein** Import (das ist die Live-Verifikation unten). **Abhängigkeiten
(hart, Spec §8 „T3 + T4 + T5 → T6"):** 5a **komplett** (Service und DI — ohne sie läuft die
Browser-Abnahme gegen einen Endpunkt, der nichts liefert), 5b, 6a (Audit-Pfad — sonst schreibt der
erste echte Durchlauf eine Zeile ohne Herkunft), 6b, 7. **Modell:** `sonnet` (größter Frontend-Task;
`opus` vertretbar, wenn Task 1 Befunde am Dialog gemeldet hat). **Commits:** `feat(web): add the
7TV leaderboard as the third import source` (Step, Dialog, Service, Fluss, Locales, **und**
`DECISIONS.md`) — ein Commit, weil der Eintrag zur Konventionsänderung gehört.

---

## Live-Verifikation gegen echtes 7TV (Welle G — Betreiber + Orchestrator, vor Task 9)

Regel 16; AK 30, AK 14, AK 16, AK 33 und das **lokale** Äquivalent von AK 31. Liegt **vor**
Task 9, weil sie einen Nachtrag auslösen kann (Ranking-Reihenfolge, Task 7 — Spec §17 letzte
Zeile); jeder solche Nachtrag ist ein eigener kleiner Commit, und Task 9 läuft erst danach.

**Aufbau:** `docker compose up -d postgres redis` · `dotnet run --project src/EmotePurge.Api`
(`:5151`, braucht User-Secrets) · `npm --prefix web start` · Login im Browser durch den
Betreiber. **Die Devbox teilt die Egress-IP mit dem Browser des Betreibers**: keine Messung, die
die Such-Sperre absichtlich auslöst, und **kein** Versuch, den Budget-Deckel live zu provozieren
(20 Füllungen wären 20 Suchanfragen aus demselben Anschluss) — Messung (b) bleibt offen (#165).

**Zu beobachten, mit Beleg (Log-Zeilen des Service bzw. Admin-Ansicht):**

- [ ] Beide Sortierungen laden; die ersten zehn Positionen stimmen mit `7tv.app` überein (sonst
      Nachtrag in Task 7).
- [ ] **Vorrat-Treffer (AK 33):** der Kaltstart erzeugt genau 4 Log-Zeilen (`usedInWindow` 1–4);
      zweites Öffnen und Sortierwechsel innerhalb der Stunde erzeugen **keine** weitere.
- [ ] **Request-Zählung gegen den Eimer:** je Zeile `remaining`/`reset`; `remaining` bei jeder
      Stichprobe der Bestenliste > 90 (E15; **keine** Aussage über das Eimer-Minimum aller
      Verbraucher).
- [ ] **Deckel und Erwartungswert:** belegt durch die kontrollierten Tests zu AK 4/33 (Task 4, 5a),
      **nicht** live. Die Admin-Rate-Limit-Ansicht zählt je `(provider, callSource)` in Redis
      **ohne Prozesslauf-Dimension** (`RateLimitTelemetryStore.cs:347`) — sie zeigt eine
      laufübergreifende Summe und ist deshalb kein Beleg für „≤ 10 je Prozesslauf"; die
      Per-Lauf-Zahl steht in `usedInWindow` der Log-Zeile. Live nur: die Call-Source
      `seventv-leaderboard` erscheint dort mit übersetztem Label in beiden Sprachen (AK 14) und mit
      Minuten-/24-h-Zählern, die zu den Log-Zeilen passen.
- [ ] Sortierwechsel leert die Auswahl sichtbar (AK 21).
- [ ] **Vollständiger Import von mindestens zwei Emotes, eines davon mit Namenskollision im
      Zielset** (Betreiber, echter 7TV-Token, eigenes Testset): Kollisionshinweis erscheint (AK 18),
      `sync-imported` antwortet 2xx **nach** den Mutationen (AK 16, F1/F6-Klasse), Audit-Zeile zeigt
      `importedFromLeaderboard` mit Emote-Zahl **und** Sortierlabel — nicht die nackte Zahl.
- [ ] **AK 31, lokales Äquivalent:** Api mindestens eine volle Stunde laufen lassen, zwischendurch
      mehrfach öffnen/wechseln; die Log-Zeilen zeigen ≤ 4 Upstream-Requests in der Stunde und
      `usedInWindow` ≤ 10 durchgehend. Der Prod-Teil ist die Rollout-Abnahme unten.

Danach `dotnet run` **beenden** — Task 9 braucht `:5151` frei.

---

### Task 9: E2E, Gesamt-Gates, Messfenster-Diff, Coverage — nach dem letzten Code-Commit

**Ziel:** Der fertige Stand gegen alle Gates, plus die zwei Playwright-Fälle und die AK-29-Prüfung.
**Läuft erst, wenn kein weiterer Code-Commit mehr aussteht** — löst die Live-Verifikation oder
das Codex-Review danach noch eine Änderung aus, läuft dieser Task **vollständig** erneut.

**Dateien:** `web/e2e/emote-import.e2e.spec.ts` (neue `describe`-Gruppe neben „push flow: picker
to confirmation dialog", `:167`; Mocks über `web/e2e/support/mocks.ts`): (1) Bestenliste → Auswahl
→ Bestätigung → `sync-imported` mit `sourceKind: 'seventv-leaderboard'`, `sourceChannelName:
null`, `leaderboardSort` im Body (gemocktes `/api/**`, inkl. 7TV-Mutationen wie im Fremdkanal-Fall);
(2) 503 vom Endpunkt → Fehlerzustand im Step, „Weiter" gesperrt.

**Gates, in dieser Reihenfolge:**

- [ ] Port-Probe: **keine Api auf `:5151`** (sonst erst `dotnet run` beenden)
- [ ] `npm --prefix web test -- --watch=false`
- [ ] `npm --prefix web run e2e` (Stand 2026-08-30: 96 Fälle + 2; rote Fälle zuerst auf
      Speicherdruck prüfen, Suite allein wiederholen, bevor eine Regression behauptet wird)
- [ ] `dotnet test EmotePurge.slnx` (Docker läuft)
- [ ] `dotnet build EmotePurge.slnx --no-incremental` — keine neuen Warnungen
- [ ] `dotnet format EmotePurge.slnx --verify-no-changes`, `npm --prefix web run format` + `lint`
- [ ] **AK 29:** Dateiliste des Branch-Diffs gegen `origin/main` prüfen — keine Datei unter
      `src/EmotePurge.Worker/`, keine unter `src/EmotePurge.Infrastructure/Migrations/`, keine
      Änderung an `SevenTvSyncService.cs`, `EmoteMatchCache*`, `UsageFlushWorker*`; im
      Client-Diff sind `GqlUsersQuery` und `ResolveTwitchUserIdAsync` unberührt. Ergebnis in den
      PR-Text (samt „Worker-Image wird neu gebaut, F6").
- [ ] **AK 28:** Diff über `tests/` zeigt an `SevenTvApiClientEmoteSetPreviewTests`,
      `SevenTvApiClientForeignTelemetryTests`, `SevenTvApiClientForeignTelemetryStoreTests`,
      `HardenedForeignEmoteSetServiceTests`, `ForeignEmoteSetProviderBudgetTests`,
      `ForeignSevenTvBreakerPolicyTests`, `foreign-emote-grid.spec.ts` (bestehende Fälle) **keine**
      Änderung; die Task-2-Datei ist seit ihrem Commit unverändert.
- [ ] `node scripts/coverage-local.mjs` **nach dem letzten Commit** — Ergebnis als Anlass
      hinzusehen: neue Dateien (Store, Budget, Service, Step) sind dort nah an Sonar; der
      `SevenTvApiClient.cs`-Eingriff ist die schwache Stelle der Näherung (Spec §10).

**Abhängigkeiten:** Live-Verifikation abgeschlossen, alle Nachträge committet. **Modell:**
`sonnet`. **Commit:** `test(web): cover the leaderboard import flow end to end`

---

## Nach dem letzten Task (Orchestrator und Betreiber)

### Zweitmeinung, PR (Regel 1, 22)

- [ ] `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` — **einmal je Branch**,
      `--scope` und `--base` explizit (sonst Working Tree bzw. `main` gegen `origin/main`).
      Gehört dem Orchestrator, nicht einem Implementer. Ergebnis dem Nutzer **unverändert** vorlegen;
      widersprechen sich Opus-Review und Codex, entscheidet Fable als Schiedsrichter (nur die
      strittigen Findings). Führt das Review zu Code-Änderungen: Task 9 erneut, dann kein zweites
      Codex-Review für dasselbe Diff.
- [ ] Branch pushen, PR gegen `main` (englisch, keine Ticketnummern in Git-Metadaten; im Text:
      AK-29-Befund, Worker-Image-Rebuild F6, E6 Deploy-Bündelung, **„35 von 36 AK vor dem PR
      belegt; AK 31 ist Rollout-Abnahme auf Prod"**, Vorab-Kontrolle AK 36 als
      Rollout-Vorbedingung). **Kein Merge, kein Deploy** — beides gehört dem Nutzer.

### Rollout-Vorbedingung (AK 36) — Handprüfung des Betreibers, kein Code, kein Subagent

**AK 36 in der korrigierten Fassung vom 2026-09-13 (Spec `e9e7f0a`):** Vor dem Stack-Update, im
Admin-Bereich auf Prod (Browser des Betreibers; **kein** Task verbindet sich mit `vps`/`nas`):
**jeden aktiven Kanal** auf seiner Admin-Detailseite öffnen
(`web/src/app/features/admin/admin-channel-detail-page.ts`, Abschnitt ~183–189) und die Twitch-ID
prüfen. **Die Anzahl aktiver Kanäle mit leerer Twitch-ID muss 0 sein.** Sonst Rollout erst nach
dem 2026-10-07. Die frühere Vorauswahl über den Sync-Fehlergrund in der Kanalliste **entfällt**:
ein Umbenennungs-Duplikat mit verweigertem Merge (`MergesRefused`) überspringt den Sync ohne
Fehlergrund und wäre über die Liste unsichtbar. Bei rund 15 getrackten Kanälen ist das eine
Durchsicht von Minuten.

**Maßstab, damit niemand Dringlichkeit hineinliest:** Ein hängender Kanal zieht eine Suchabfrage
pro Minute bei 100 pro Minute Limit; leer wäre der Eimer erst bei rund 90 solchen Kanälen,
getrackt sind etwa 15. Die Vorab-Kontrolle ist eine Ordnungsprüfung, kein Notfall — und #165 ist
vor allem Hygiene, echter Schutz erst bei wachsender Kanalzahl. An den Betreiber-Entscheidungen
(E1: keine gemeinsame Bremse in dieser Runde) ändert das nichts.

### Rollout-Abnahme (AK 31 auf Prod) — nach dem Stack-Update, Eigentümer Betreiber

Die einzige AK, die dieser Plan nicht vor dem PR belegen kann: sie verlangt einen Abnahmelauf von
mindestens einer vollen Stunde **auf Prod** (Spec §9, AK 31).

- **Eigentümer:** der Betreiber. Kein Agent verbindet sich mit `vps`; die Log-Zeilen holt der
  Betreiber selbst (Portainer-Log der Api oder `docker logs` auf dem VPS) und übergibt sie zur
  Auswertung — vorbereitete Befehle liefert der Orchestrator mit Platzhaltern, führt sie nicht aus.
- **Beleg:** die Service-Log-Zeilen der Bestenliste (Task 5a) über ≥ 1 h Prod-Betrieb mit echter
  Nutzung: ≤ 4 Upstream-Requests je Stunde, `usedInWindow` ≤ 10 durchgehend, `remaining` > 90 bei
  jeder Stichprobe. Plus die Admin-Ansicht als laufübergreifende Plausibilität (≤ 96 im 24-h-Fenster).
- **Stopp-Bedingung:** `usedInWindow` erreicht 10 (Deckel greift — heißt: Vorrat oder Budget
  arbeiten nicht wie getestet), `remaining` ≤ 90 bei einer Bestenlisten-Stichprobe, oder eine
  429-Beobachtung unter `seventv-leaderboard`. Dann: Stack auf das vorige Image zurück (Rollback
  nach Spec §12 — kein persistierter Zustand, Audit-Zeilen bleiben und degradieren nur in der
  Anzeige), Ursache mit den Log-Zeilen klären, bevor das Feature erneut ausgerollt wird.
- **Ergebnis:** eine Notiz im Issue #148 (Datum, Stunde, die drei Zahlen) schließt AK 31.

---

## Offene Punkte für den Betreiber

1. **`seventv-foreign-preview` ohne Locale-Schlüssel (Bestandslücke aus #147).** Weder `de.json`
   noch `en.json` führen `admin.rateLimits.providers.callSources.seventv-foreign-preview`; die
   Admin-Monitoring-Seite rendert den Schlüssel roh. Die Spec (F5, AK 14) sichert das nur für die
   **neue** Call-Source. Task 1 bestätigt den Befund; zu entscheiden: die zwei Zeilen als eigenen
   `fix(web):`-Commit in Task 3 mitnehmen (Empfehlung — gleiche Datei, gleiche Tabelle, Task 3 ist
   ohnehin der erste Locale-Bearbeiter) oder als eigenes Issue.
2. **AK 31 wird nicht vor dem PR belegt, sondern als Rollout-Abnahme auf Prod** (Abschnitt oben:
   Eigentümer Betreiber, Beleg, Stopp- und Rollback-Bedingung). Der PR sagt deshalb „35 von 36 AK
   vor dem PR, AK 31 nach dem Stack-Update". Bitte bestätigen, dass der Merge unter dieser
   Bedingung freigegeben ist — oder festlegen, dass AK 31 vor dem Merge anders zu erbringen ist.
3. **Log-Zeile: Spec E15 sagt „der Client loggt", §6 verlangt in derselben Zeile die
   Budgetfenster-Zählung.** Beides zusammen geht nur, wenn eine Seite die Daten der anderen kennt.
   Der Plan legt das Ereignis in den **Service** (Task 5a) und lässt das Client-Ergebnis die
   Header-Werte tragen — eine Zeile, auch bei Fehlausgängen. Kein Widerspruch zu einer
   E-Entscheidung, aber eine Präzisierung von E15, die beim nächsten Spec-Nachzug mitgehen sollte.
4. **Haltbarkeiten: „vier" gegenüber fünf Tabellenzeilen.** Die Entscheidung nennt vier
   Haltbarkeiten (1 h / geklemmtes `RetryAfter` / 60 s / 30 s); Spec §6 führt als fünfte Zeile
   den offenen Breaker mit `RemainingOpenTime` (geklemmt auf [1 s, 1 h]). Das ist ein abgeleiteter
   Wert, kein neuer Festwert — der Plan behandelt ihn so. Nur zur Kenntnis, keine Entscheidung nötig.
