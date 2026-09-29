# Epic #200 (PR #303): Fix-Paket aus dem Schiedsspruch zu den Reviews — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan und die Betreiberentscheidungen aus Abschnitt 1; er rollt
> sie **nicht** neu auf. Schritte sind als Checkbox (`- [ ]`) geführt. **Kein fertiger Code in
> diesem Plan** — Verträge, Namen, Grenzfälle, Testfälle und Reihenfolge ja, Rümpfe nein.
>
> Arbeitsort: Worktree `/home/dev/projects/EmotePurge-200fix`, Branch `fix/epic-200-review`,
> Basis `origin/feat/emote-sets-200` @ `e1262e5e`. Kein Upstream, nicht pushen, bis der Orchestrator
> es sagt. Ziel-PR: `fix/epic-200-review` → `feat/emote-sets-200` (Integrationsbranch-Modell; PR #303
> aktualisiert sich nach dem Merge selbst). Kein `docker compose` aus dem Worktree, kein zweiter
> Worker gegen die Dev-Datenbank (Messfenster #69/#73, Memory „Dev-Worker + :8080 gehören #73").
> `cd` hält nicht zwischen Shell-Aufrufen — immer absolute Pfade.
>
> **`web/node_modules` fehlt im Worktree** (verifiziert 2026-09-29). Task T0 legt es an; jeder
> spätere Task prüft `ls /home/dev/projects/EmotePurge-200fix/web/node_modules/.bin/ng`, bevor er
> Frontend-Gates fährt.

**Sprache:** Plan deutsch (Denkwerkzeug). Neuer Code, Kommentare, Log-/`throw`-Meldungen,
Commit-Messages und **neue** `docs/DECISIONS.md`-Einträge englisch (CLAUDE.md „Sprache").

**Quelle der Wahrheit:** der Schiedsspruch (Fable) zu den Reviews von Codex Sol und Opus mit den
Betreiberentscheidungen vom 2026-09-29, wiedergegeben in Abschnitt 1. Was dort steht, ist
entschieden; was ihm widerspricht, gehört in Abschnitt 5 „Offene Punkte für den Betreiber", nicht
in eine stille Entscheidung des Tasks.

**Nicht im Umfang:** C1 (Seitenverschiebung beim Set-Read — Folge-Issue), die übrigen Opus-P3
(Retry bei 400/404, Import-Report-Klassifikation, Session-Art im Vote-Dialog, Harness
`ambiguousNames` — „Known limits" in PR #303), `foreign_channel_no_active_emote_set` für ein
verschwundenes Set (Spec 6.4 so entschieden), jede Änderung an den Verträgen aus #216/#220/#280,
Bestandsdateien außerhalb des Epic-Diffs (`origin/main...origin/feat/emote-sets-200`).

---

## 0. Befund (verifiziert am 2026-09-29 im Worktree)

Jede Zeilenangabe unten ist gegen `e1262e5e` geprüft. Wo die Reviews abwichen, steht es unter
„Korrekturen an den Review-Angaben" am Ende dieses Abschnitts.

### 0.1 O1 — Null-Session-Nutzung nach Set-Wechsel

| Schicht | Stelle | Befund |
|---|---|---|
| Infrastructure | `src/EmotePurge.Infrastructure/Services/VoteSessionQueryService.cs:116-121` | Kommentar „a null-session reads the channel's active set, exactly as before set-sessions existed" und Aufruf `GetTotalsByEmoteIdsAsync(…, session.EmoteSetId ?? channel.ActiveEmoteSetId, …)` — der **einzige** Produktiv-Aufrufer der Methode (grep über `src/`) |
| Infrastructure | dito `:211-224` | `useCount`-Regel: Null-Session → `GetValueOrDefault(emote.Id, 0)` für unarchivierte Emotes (`null` nur für archivierte); Set-Session → `TryGetValue` sonst `null` |
| Infrastructure | `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs:285-318` | `GetTotalsByEmoteIdsAsync(ids, from, to, string emoteSetId)`: `WHERE ids.Contains(EmoteId) && u.EmoteSetId == emoteSetId`, Datumsfenster als bedingte Summe (Zeile 313-316) — „Zeile vorhanden, Summe 0" bleibt von „nie beobachtet" unterscheidbar |
| Infrastructure | dito `:135-208` `GetDailySeriesAsync` | `setId = emoteSetId ?? emote.ActiveEmoteSetId`; Tages-Query (`:170`) **und** First/Last-Bounds (`:180`) filtern `u.EmoteSetId == setId`; `IsArchived` bewusst nicht gefiltert |
| Core | `src/EmotePurge.Core/Services/IUsageStatQueryService.cs:208-220` (Daily), `:238-259` (Totals) | Daily: `null` = aktives Set. Totals: Parameter **required, nicht nullable** — „the caller holds the session and decides which set its ballot is about" |
| Core | `src/EmotePurge.Core/Entities/Channel.cs:8` | `ActiveEmoteSetId` ist `string`, Default `string.Empty` — ein Kanal ohne erfolgreichen Sync trägt `""` |
| Migration | `src/EmotePurge.Infrastructure/Migrations/20260920191131_AddUsageStatEmoteSetId.cs:164-180` (Schritt 7b) | Zeilen mit `Date < boundary_utc` → altes Set; Grenztag selbst → neues Set (bekannte Ein-Tages-Unschärfe, Spec 4.2) |
| Flush | `src/EmotePurge.Infrastructure/Services/UsageStatFlushService.cs:81-84` | `INSERT … ON CONFLICT ("EmoteId","EmoteSetId","Date")` — genau eine Zeile je (Emote, Set, Tag), Set = das zur Chatzeit aktive. **Summieren über Sets zählt nichts doppelt** |
| Api | `src/EmotePurge.Api/Endpoints/UsageStatsEndpoints.cs:62-88` | `GET /daily` (Gruppe `/api/channels/{channelName}/usage-stats`): `string? emoteSetId` als Query, `EmoteSetIdValidationFilter` als Route-Filter, Handler dünn (400 `EmoteIdEmpty`, Range-Validierung, 404 bei `null`) |
| Api | `src/EmotePurge.Api/Validation/EmoteSetIdValidationFilter.cs` | prüft `emoteSetId` aus Route **oder** Query, wenn vorhanden; ein **fehlender** Query-Parameter passiert ungeprüft; ein vorhandener, leerer → 400 `invalid_emote_set_id` |
| Frontend | `web/src/app/features/voting/vote-session-detail-page.ts:819-846` `openDrilldown` | übergibt `emoteSetId: results.emoteSetId` — für eine Null-Session also `null` |
| Frontend | `web/src/app/shared/emotes/emote-drilldown-dialog.ts:41-48` (`EmoteDrilldownData.emoteSetId`), `:327-335` | `null`/fehlend = „the channel's active set"; Konstruktor ruft `getDailySeries(…, this.data.emoteSetId ?? null)` |
| Frontend | `web/src/app/core/usage-stats/usage-stat.service.ts:53-70` `getDailySeries` | `null` lässt den Parameter weg; Cache-Schlüssel `channel|setId|emote|from|to` |
| Frontend | `web/src/app/features/usage-stats/usage-stats-page.ts` `openDrilldown` | übergibt `emoteSetId: this.shownSetId()` (konkretes Set der Seite; `null` nur, solange keine Zeilen geladen sind) — **bleibt unverändert** |
| Tests | `tests/EmotePurge.Infrastructure.Tests/Integration/VoteSessionQueryServiceTests.cs:292-360` | Set-Session-Fälle (Scoping, `null` bei nie beobachtet, 0 außerhalb des Fensters); Null-Session-Nutzungsfall über mehrere Sets **fehlt** |
| Tests | `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs:253,360,379,390,404` (Totals mit `channel.ActiveEmoteSetId` bzw. `"irrelevant-set"`), `:1179-1200` (Daily je Set), `:1224-1240` (Totals je Set) | Aufrufstellen, die die Signaturänderung mitmachen müssen |
| Tests | `web/src/app/features/voting/vote-session-detail-page.spec.ts:627-655` | „openDrilldown charts the SESSION's own set" und „omits emoteSetId for a null-session" — Letzterer kippt |
| Tests | `web/src/app/shared/emotes/emote-drilldown-dialog.spec.ts:214-237` | „asks for the series of the set frozen into its data" / „asks for the channel's active set when its data carries no set" |
| Tests | `tests/EmotePurge.Api.Tests/ChannelUsageSeriesWireFormatTests.cs` | Präzedenz: der Drahtvertrag einer Usage-Stats-Route wird per `WebApplicationFactory` gepinnt — das Muster für den neuen `/daily`-Scope-Test |

**Der Fehler, konkret:** Eine Null-Session mit Fenster StartedAt–EndedAt vor einem Set-Wechsel
fragt heute nach `channel.ActiveEmoteSetId` (dem *neuen* Set). Ihre Nutzungszeilen liegen nach
Migration 7b bzw. nach dem Flush unter dem *alten* Set. Jedes unarchivierte Emote bekommt
`GetValueOrDefault(…, 0)` → 0 — falsche Zahl, nicht „unbekannt". Dasselbe Bild für
`ActiveEmoteSetId == ""` (Kanal ohne Sync): Filter auf `""` → nichts.

**Doppelzählung ausgeschlossen:** Der Flush schreibt je (EmoteId, EmoteSetId, Date) genau eine
Zeile unter dem zur Chatzeit aktiven Set; dieselbe Chatnachricht landet nie unter zwei Sets. Eine
setagnostische Summe zählt also jede Nutzung genau einmal.

**Andere Stellen mit `""`-Relevanz:** `EmoteSetStatusService.cs:27` (`Length == 0` → „kein Set",
korrekt behandelt), `TrackedEmoteSetMembershipService.cs:24` (`!IsNullOrEmpty`, korrekt),
`GetUsageContextAsync`/`GetChannelSeriesAsync` (`setId = "" → isActiveSet = true → Filter auf ""`
→ leer — für einen Kanal ohne Sync gibt es aber auch keine Emote-Zeilen; kein Fehlbild, keine
Änderung nötig). Für die Null-Session erledigt Variante (a) den Fall.

### 0.2 O2 — `/totals` vor `/status` bei `channel.synced`

| Stelle (`web/src/app/features/usage-stats/usage-stats-page.ts`) | Befund |
|---|---|
| `:469-473` `activeEmoteSetId` | `setStatusChannel() === channelName() ? setStatus()?.activeEmoteSetId \|\| null : null` |
| `:564-578` `selectedEmoteSetId` | URL-Parameter `''` (oder Pin) → `active`; sonst Listen-Match auf `NORMAL`, Fallback `active` |
| `:651-657` `viewKindStale`, `:667-669` `viewSwitching` | Stale = „Zeilen wurden unter einer anderen Aktiv-ID geladen als jetzt bekannt"; `viewSwitching` sperrt Schreibwege mit Grund `usageStats.setView.lock.switching` (`:831-848`) |
| `:680-684` `setStatusOutcomeKnown`, `:691-706` `liveMembersParams`, `:760-762` `liveMembersState` | die #220-Logik (Commits `4b1e8ce5`, `49cc39cd`): Mitgliederliste erst nach bekanntem Status-Ausgang; `setStatusFailedChannel` wird nie gelöscht (akzeptierte Lücke, dokumentiert) |
| `:1288-1293` `setStatusChannel` / `setStatusFailedChannel` | Erfolg claimt; Fehler claimt **nicht** — Doku im Feldkommentar |
| `:1807-1815` Lade-Effekt | `load(channelName, from, to, rangeResolved, selectedEmoteSetId, awaitingEmoteSetId)` — läuft neu, sobald `selectedEmoteSetId()` sich ändert, **auch** wenn die Änderung von einer gelandeten Aktiv-ID kommt (Skeleton + Totals + Series unter der neuen ID; `preserveSelection` aus, aber `previousTotalsChannel === channelName` → Reconcile statt Clear, `:3117-3140`) |
| `:1858-1873` Stale-Effekt | reload `preserveSelection + silent`, nur wenn `shownSetId() === selectedEmoteSetId()` |
| `:2006-2054` Live-Subscription | **Reihenfolge heute:** (1) `loadTotals(selectedEmoteSetId(), preserve+silent)` für `usage.flushed` **und** `channel.synced`, (2) nur bei `synced`: `stopAwaitingSync()`, `refreshSetStatus()`, `emoteSetListResource.reload()`, `reloadLiveMembers()`; (3) bei `flushed` mit offenem Probe-Gate: `refreshSetStatus()` (`:2048`) |
| `:2743-2756` `refreshSetStatus` | Erfolg: `setStatus`/`setStatusChannel` schreiben (Kanal-Guard); **`error: () => undefined`** — ein Fehler lässt A stehen |
| `:2798-2836` `load()`-Statuszweig | **Vorbild für den Fehlerfall:** `error` setzt `setStatus(null)`, `setStatusFailedChannel(channel)` und **un-claimt** `setStatusChannel` — „nothing may still read as 'this channel's set is known'" |
| `:3082-3107` `loadTotals` | stempelt `totalsSetId = emoteSetId ?? activeAtRequest`, `totalsNonActive = emoteSetId !== null && emoteSetId !== activeAtRequest` — mit der **alten** Aktiv-ID A also `nonActive = false`, obwohl der Server A längst als nicht-aktiv auflöst (`UsageStatQueryService.cs:48-49,115`: Basis-Menge = nur Zeilen mit Zählung unter A, archivierte inklusive) |
| `:2911-2921` `reconcileSelection` → `selection.retainAmong(emotes())` | markierte Emotes ohne Nutzung unter A fallen aus der Auswahl (Löschkandidaten!) und kommen nicht zurück |
| `usage-stats-page.html:1085` | Dock/Panel-Zweig ist mit `@if (selectedEmoteSetId(); as setId)` gegated; `:1133-1145` bindet `[setId]="setId"`, `[activeSetId]="activeEmoteSetId()"`, `[deleteLockReasonKey]="deleteLockReasonKey()"` |
| `web/src/app/shared/seven-tv/mass-delete-panel.ts:451-461` | `effectiveActiveSetId`: `undefined` → `setId()`, **`null` bleibt „known unknown"** → `isActiveSet = false`; `:1153-1155` friert `frozenIsActiveSet` beim Öffnen des Dialogs ein |
| `web/src/app/core/usage-stats/usage-stat.service.ts:43-51` | `withEmoteSetId` sendet eine explizite ID, lässt bei `null` den Parameter weg |
| `web/public/i18n/de.json:795-799` / `en.json:795-799` | vorhandene Sperrgründe `usageStats.setView.lock.{membersUnavailable,truncated,switching}` |
| Tests `usage-stats-page.spec.ts` | Muster: `:245-500` (Status-Race mit `FakeEventSource`, `channelSynced`-Emit, `:457` „lets a channel.synced-triggered refreshSetStatus() claim the channel after the initial status fetch failed"), `:4519-4531` (Finding E: Status-Fehler un-claimt), `:4577` (Finding H: `viewKindStale`), `:3525-3560` (AK 52: `synced` lädt Mitgliederliste), die zehn #220-Fälle (`git show 49cc39cd 4b1e8ce5 -- …spec.ts`, Namen „(a) a deep link …", „reports 'loading', not 'unavailable' …", „a late answer for the channel left behind …" usw.) |

**Fall a (Race):** `synced` → `/totals?emoteSetId=A` (alte Aktiv-ID, explizit) läuft **vor** dem
Status. Landet A zuerst: Zeilen für ein nicht-aktives A, gestempelt als aktiv, Reconcile prunt die
Auswahl. Dann landet der Status mit B → `selectedEmoteSetId` A→B → Lade-Effekt lädt neu unter B
(Skeleton) — die Auswahl ist da schon weg. Landet B's Status zuerst: `latestOnly` verwirft A's
Antwort, kein Schaden — der Ausgang hängt an der Netzreihenfolge.

**Fall b (Status-Fehler):** `refreshSetStatus` verschluckt den Fehler; A bleibt „aktiv" bis zum
nächsten Event. Der Dock bekommt `setId=A`, `activeSetId=A` → `isActiveSet = true`; die
Bestätigung sagt „aktiv"; die 7TV-REMOVE-Aufrufe gehen Browser→7TV gegen A, serverseitig prüft
nichts.

### 0.3 C2 — GraphQL-Teilantwort → 404

| Stelle (`src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs`) | Befund |
|---|---|
| `:796-802` | `if (pageResult.Status == V4PageStatus.Ok && setsRoot is not null && setDto is null)` → `NotFound` — **ohne** Blick auf `pageResult.Dto.Errors`; Log `:798-800` deutsch |
| `:804-822` | generischer `Unavailable`-Zweig; Log `:815-817` deutsch; `:780-782` (429-Log) deutsch |
| `:900-906` `FetchV4PageAsync` | Nicht-429-Fehler → `V4PageResult(Ok, dto, …)` mit gefülltem `dto.Errors` — genau die Form, die `:796` als NotFound fehldeutet |
| `:682-690` Set-Listen-Pfad | **Vorbild (F17):** `userByConnection: null` **plus** `Errors { Count: > 0 }` → `Unavailable` mit Warn-Log, sonst `NoSevenTvAccount` |
| `src/EmotePurge.Infrastructure/Services/ForeignEmoteSetService.cs:121-124,174-177` | `NotFound` → `NoActiveEmoteSet` (404) |
| `src/EmotePurge.Infrastructure/SevenTv/HardenedForeignEmoteSetService.cs:262-270` | Breaker: `NoActiveEmoteSet` → `RecordSuccess`; `SevenTvUnavailable` → `RecordFailure(OtherFailure)`. **Folge des Bugs:** eine 7TV-Fehlerantwort wird heute als Breaker-*Erfolg* verbucht |
| `tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvApiClientEmoteSetPreviewTests.cs:124-135` | `GraphQlErrorWithoutRateLimitStatus_IsUnavailable` nutzt `{"data":null,"errors":[…]}` — trifft den generischen Zweig, nicht die Lücke; `:147-157` `UnknownSetId_WithAWellFormed200AndNoErrors_IsReportedAsNotFound_NotUnavailable` mit `{"data":{"emote_sets":{"emote_set":null}}}` — **snake_case**, nicht `emoteSets`/`emoteSet` |

### 0.4 C3 — Set-ID-Regex

`src/EmotePurge.Api/Validation/EmoteSetIdValidation.cs:15`: `new Regex("^[0-9A-Za-z]{1,32}$", Compiled | CultureInvariant)`, `IsValid` prüft `!IsNullOrEmpty`, `Length <= 32`, `IsMatch` — **kein Trim**, kein Normalize (Doku `:17-18` sagt das ausdrücklich). In .NET matcht `$` ohne `Multiline` auch **vor einem abschließenden `\n`**: `"abc\n"` ist heute gültig. `"abc\r\n"` ist heute schon ungültig (das `\r` gehört nicht zur Klasse) — der Testfall dokumentiert das, er reproduziert keinen Fehler. Tests: `tests/EmotePurge.Api.Tests/EmoteSetIdValidationTests.cs` (Theory-Muster `:48-56`). Die Inventur weiterer `$`-Regexe steht in T5 (Ergebnis der Vorab-Inventur dort eingetragen).

### 0.5 Deutsche Log-Meldungen im Epic-Diff

Inventur in T6 (Ergebnis der Vorab-Inventur dort eingetragen): elf vom Epic **hinzugefügte**
Zeilen in `ForeignEmoteSetService.cs`, `ForeignEmoteSetCache.cs`, `HardenedForeignEmoteSetService.cs`
und `SevenTvApiClient.cs`. Deutsche Logs, die schon auf `main` stehen
(`ForeignEmoteSetService.cs:100,110,115,130`, `SevenTvApiClient.cs:781,816`), sind Bestand und
**nicht** im Umfang — auch wenn sie in denselben Dateien liegen.

### 0.6 Flaky Test (Sonar-Job `analyze`)

Fakten aus dem CI-Log des Laufs 36604547004 (PR #303 @ `e1262e5e`, gespeichert unter dem
Scratchpad des Orchestrators; per `gh run view 36604547004 --log-failed` jederzeit erneut holbar):

- Fehlende Assertion: **`import-conflict-resolution-step.spec.ts:540:38`**
  `expect(document.activeElement).toBe(rowAt(199))` — Received ist **Zeile 1**
  (`data-resolve-index="1"`), Expected `null`. Heißt: `rowAt(199)` war nach `settle` **immer noch
  `null`** — Zeile 199 hat innerhalb der 200 Runden (≈ 2,3 s, Testdauer 2325 ms) nie gerendert;
  der Fokus blieb auf Zeile 1. Die Vermutung des Reviews („settle lief aus") stimmt; der Grund liegt
  aber davor: der simulierte Scroll (`simulateScrolling`, `:286-294`: `measureScrollOffset`-Mock +
  `scroll`-Event) hat in CI die Range-Aktualisierung des Viewports nicht ausgelöst.
- Schwesterfälle in derselben Datei laufen in CI **grün, aber langsam**: „lets a newer focus target
  win …" 1610 ms, „keeps focus on a row the user focuses while a keyboard target still waits …"
  1633 ms — beide enthalten `settle(() => rowAt(199) !== null)` nach `growViewportToFit`
  (`:561-562`, `:882-883`) oder `settle(() => document.activeElement === rowAt(0))` (`:552`).
  Eine Laufzeit von ~1,6 s bedeutet, dass auch dort eine Bedingung erst spät oder nie wahr wurde.
  Alle übrigen Fälle der Datei liegen bei 20–100 ms.
- Der Fehler trat in **zwei Läufen desselben SHA** identisch auf; lokal (16/16 mit Coverage,
  `taskset -c 0` plus Last; Gesamtsuite 5/5) nie. Er ist als **deterministisch in der CI-Umgebung**
  zu behandeln, nicht als Zufall.
- **Bekannte Unterschiede CI ↔ lokal** (verifiziert): `.github/workflows/sonarcloud.yml:55-58`
  `setup-node` mit `node-version: "22"` — lokal `node -v` = **v24.19.0** (`web/.nvmrc` sagt `22`,
  `engines.node >= 22`); Runner `ubuntu-latest` (4 vCPU) vs. Devbox 6 Kerne; Aufruf
  `npm test -- --watch=false --coverage --coverage-reporters=lcov` aus `web/` (`:69-71`) — Builder
  `@angular/build:unit-test` (`web/angular.json:81-86`, nur `setupFiles`, kein eigenes
  `vitest.config`), jsdom 30.0.1, vitest 4.1.11, CDK 22.1.6 (Versionen aus `web/package-lock.json`
  bzw. dem `node_modules` des Hauptrepos); `publish.yml:81` fährt dieselbe Suite **ohne** Coverage
  — dort ist der Fall nicht bekannt geworden.
- Mechanik, die der Test voraussetzt: `CdkVirtualScrollViewport` abonniert `scroll` per
  `auditTime(0, SCROLL_SCHEDULER)` mit `SCROLL_SCHEDULER = requestAnimationFrame vorhanden ?
  animationFrameScheduler : asapScheduler` (CDK `scrolling.mjs:574,663`), Subscription in
  `afterNextRender`; die Komponente landet den Fokus in `afterEveryRender` →
  `landPendingFocus` (`import-conflict-resolution-step.ts:755`, `:948-963`).

### Korrekturen an den Review-Angaben

| Review sagte | Verifiziert |
|---|---|
| `VoteSessionQueryService.cs:~109-121` | Aufruf `:120-121`, Kommentar `:116-117`; `useCount`-Regel `:211-224` |
| `UsageStatQueryService.cs:~313` filtert | `:313`, Datumsfenster als bedingte Summe `:316`; `GetValueOrDefault(…,0)` steht **nicht** dort, sondern in `VoteSessionQueryService.cs:224` |
| `20260920191131_…cs:~170-180` | Schritt 7b `:164-180`, das UPDATE `:171-180` |
| `usage-stats-page.ts:~2008-2011` / `~2025` | `loadTotals` `:2018-2021`, `refreshSetStatus()` `:2029`; zweiter Aufrufer `:2048` (Flush-Probe) |
| `~:564-578`, `~:651-657`, `~:1860`, `~:2753`, `~:3092-3107`, `~:3133` | `:564-578` ✔, `:651-657` ✔, Stale-Effekt `:1858-1873`, `error: () => undefined` `:2754`, Stempel `:3105-3107`, `reconcileSelection()` `:3140` → `retainAmong` `:2917` |
| `mass-delete-panel.ts:~451-461`, `~1155,1167` | `effectiveActiveSetId` `:451-454`, `isActiveSet` `:461-464`; `frozenIsActiveSet` `:1153-1155`, `isActiveSet:` im Dialog-Data `:1169` |
| `SevenTvApiClient.cs:~796`, `~799,816`, `~888-903`, `~682-690` | `:796` ✔, Logs `:781`, `:798-800`, `:815-817`; `FetchV4PageAsync` Nicht-429-Rückgabe `:905-906`; Set-Listen-Vorbild `:682-690` ✔ |
| Preview-Test „bestehend ~:127 nutzt data:null" | `:124-135` ✔; die Payloads sind **snake_case** (`emote_sets`/`emote_set`), nicht camelCase |
| `EmoteSetIdValidation.cs:~15` | `:15` ✔; `"abc\r\n"` ist schon heute ungültig |
| Flaky: „Assertion ~:540, settle ~:309, simulateScrolling ~:286-294, Komponente ~:949-963" | alle ✔; **Received ist Zeile 1**, d. h. Zeile 199 hat nie gerendert (nicht: der Fokus kam zu spät) |
| „`ForeignEmoteSetService.cs:~123,163,168,176,182`, `SevenTvApiClient.cs:~799,816`, ~11 Zeilen" | `:123,163,168,176,182` ✔ und `:799` ✔; **`:816` ist Bestand auf `main`** (ebenso `:781` und `ForeignEmoteSetService.cs:100,110,115,130`) — nicht im Umfang. Die elf Zeilen des Diffs liegen in vier Dateien (T6-Tabelle), darunter `ForeignEmoteSetCache.cs:64,79` und `HardenedForeignEmoteSetService.cs:201,217,293` |

---

## 1. Entscheidungen dieses Plans (innerhalb des freigegebenen Umfangs)

Die Betreiberentscheidungen (O1 Variante (a), Drilldown im Paket, C3, Logs, Flaky ohne
Produktänderung und ohne bloßes Hochsetzen) sind gesetzt. Innerhalb davon legt der Plan fest:

**E1 — „Alle Sets" ist ein benannter Wert, kein `null`.** In `IUsageStatQueryService` heißt `null`
bei `GetUsageContextAsync`, `GetDailySeriesAsync` und `GetChannelSeriesAsync` bereits „das aktive
Set". `null` für `GetTotalsByEmoteIdsAsync` mit der Bedeutung „kein Filter" würde derselben
Schnittstelle zwei Null-Semantiken geben — genau die Falle, die O1 erzeugt hat. Deshalb bekommt
Core einen kleinen Wertetyp für den Set-Bereich einer Nutzungsabfrage mit drei Zuständen: *aktives
Set*, *genau dieses Set (id)*, *alle Sets*. Sein `default` ist *aktives Set*, damit die bestehenden
Aufrufe mit weggelassenem Parameter unverändert richtig bleiben. `GetTotalsByEmoteIdsAsync` nimmt
ihn statt des `string` und lehnt *aktives Set* mit `ArgumentException` ab (die Methode hat keinen
Kanal, gegen den sie „aktiv" auflösen könnte — die heutige Doku `:253-257` sagt das schon).
`GetDailySeriesAsync` nimmt ihn zusätzlich zum bisherigen Verhalten (Default = aktiv). Die beiden
Channel-Scope-Methoden bleiben bei `string? emoteSetId` — sie brauchen kein „alle Sets" (Spec E16:
ein nicht-aktives Set ändert dort auch die Basismenge; „alle Sets" hätte keine definierte
Basismenge und ist für die Usage-Seite nicht im Umfang).

**E2 — Der Drahtvertrag von `/daily` bekommt einen ausdrücklichen Scope-Parameter, kein
Sentinel.** `?emoteSetId=all` würde `EmoteSetIdValidation` passieren (alphanumerisch) und ein
reserviertes Wort in einen ID-Parameter mischen. Stattdessen ein eigener Query-Parameter für den
Bereich (Name im Task festgelegt: `setScope`, Werte `active` | `all`; fehlend = `active`), mit
Regel: `setScope=all` **zusammen mit** `emoteSetId` → 400 mit dem bestehenden Code
`invalid_emote_set_id` (kein neuer `ApiErrorCodes`-Eintrag nötig — Regel 7 bleibt unberührt, ein
neuer Code hätte zwei Locale-Einträge und `api-error.ts` nach sich gezogen); ein unbekannter
`setScope`-Wert ebenfalls 400 mit demselben Code. Die Usage-Seite sendet den Parameter nie; ihre
Requests, Rate-Limit-Policy (`InteractiveRead` der Gruppe) und der
`UsageStatsAccessAuthorizationFilter` bleiben unverändert — „alle Sets" ist schlicht: **kein**
`EmoteSetId`-Prädikat in Tages-Query **und** First/Last-Bounds. Kein Sicherheitsunterschied: die
Route ist ohnehin auf den Kanal gebunden und autorisiert.

**E3 — O2: Reihenfolge + Sperre genügt; Server-Wahrheit in `/totals` ist nicht im Umfang.**
Begründung: (1) Der Schaden entsteht in beiden Fällen daraus, dass die Seite mit einer *eigenen*
Aktiv-ID arbeitet, die älter ist als der Server-Stand — Status zuerst holen schließt Fall a; der
Status-Fehler explizit als „unbekannt" behandeln schließt Fall b, und dafür gibt es in `load()`
`:2819-2835` bereits das getestete Vorbild (Finding E), das nur nachgezogen werden muss. (2) Ein
Envelope aus `/totals` (aufgelöste Set-ID + `isActiveSet`) wäre eine Änderung des Drahtvertrags
einer Bestandsroute mit Folgen in `usage-stat.service.ts`, allen `/totals`-Mocks der Vitest-Suite
(> 40 Stellen), `web/e2e/support/mocks.ts`, dem Export und den Wire-Format-Tests — ein
Refactoring-Paket, kein Review-Fix, und es würde Fall b **nicht** lösen (die Aktiv-ID des Docks
kommt aus dem Status, nicht aus `/totals`). (3) Das Restfenster — 7TV wechselt zwischen Status-
und Totals-Antwort erneut — ist dieselbe Klasse wie jede andere Live-Änderung und wird vom nächsten
`channel.synced` plus `viewKindStale` abgefangen. Die Server-Wahrheit-Variante steht als Vorschlag
für ein Folge-Issue in Abschnitt 5.

**E4 — Status-Fehler nach `synced`: un-claimen, nicht löschen.** Der Fehlerzweig in
`refreshSetStatus` übernimmt aus `load()` das Un-claimen von `setStatusChannel` und das Setzen von
`setStatusFailedChannel`, **behält aber das zuletzt bekannte `setStatus`-DTO** (anders als
`load()`, das `setStatus(null)` setzt). Grund: `trackedSince` (`:474`) liest `setStatus()` ohne
Kanal-Guard und trägt die „all time"-Range; ein `null` würde `from()` und damit den Lade-Effekt
umsonst neu anstoßen. `activeEmoteSetId()` wird durch das Un-claimen allein `null` (Guard
`:470`), und genau das ist die Sperre: `selectedEmoteSetId()` fällt für `''` auf `null`, der
Dock-/Panel-Zweig (`html:1085`) ist damit nicht mehr gerendert, `importScopeCurrent()` wird
`false` (Finding-E-Test), der Lade-Effekt lädt die Zeilen **ohne** Set-ID neu (der Server löst das
aktive Set selbst auf — ehrliche Zeilen statt Zeilen unter A). Für einen per URL gewählten Satz X
bleibt die Ansicht stehen und gilt als nicht-aktiv (die Mitgliederliste wird angefragt, ein Permit
im `TrackedEmoteSetPreview`-Bucket — akzeptiert, wie die dokumentierte #220-Lücke). **Sichtbarer
Grund:** kein neuer i18n-Text. Der Dock verschwindet zusammen mit der Set-Auswahl, wie heute schon
beim initialen Status-Fehler (Finding E) — dieselbe Erfahrung für denselben Zustand. Der Task
prüft, ob der bestehende Hinweis für „kein aktives Set" in dieser Situation erscheint; erscheint
keiner, ist das ein Punkt für Abschnitt 5, **kein** neuer Text ohne Rückfrage.

**E5 — Flaky: erst Ursache, dann Determinismus.** Kein Task darf `settle`-Runden erhöhen oder
den Test überspringen. Reihenfolge: CI-Umgebung lokal nachstellen (Node 22, `CI=true`, Coverage,
volle Suite), reproduzieren, Ursache benennen, dann den Test so umbauen, dass er nicht mehr auf
echte Frames wartet (Fake-Timer mit gezieltem Vorspulen von `requestAnimationFrame`/`setTimeout`
**oder** die Range-Aktualisierung des Viewports direkt anstoßen). Abbruchregel: zwei gescheiterte
Reproduktionsmethoden → Methode wechseln (Diagnoseausgabe im Spec auf einem Wegwerf-Branch, der
nirgends gepusht wird; Rückschluss aus dem CI-Log), nicht ein drittes Mal dasselbe.

---

## 2. Tasks

Gates je Task: nur die Suiten, die der Task berührt (Backend: `dotnet build` mit 0 Warnungen,
`dotnet test EmotePurge.slnx`, `dotnet format EmotePurge.slnx --verify-no-changes`; Frontend:
`npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run
format:check`). Der Schluss-Task T8 fährt alles inklusive E2E und Coverage. Jeder Task committet
selbst (Conventional Commits; DECISIONS im selben Commit wie die Vertragsänderung), pusht nicht.

### T0 — Worktree vorbereiten und Ausgangslage messen

**Kontext:** Der Worktree ist frisch; `web/node_modules` fehlt. Bevor irgendein Task eine Suite
fährt, muss die Ausgangslage grün bekannt sein — sonst wird eine rote Suite später einem Fix
zugeschrieben, der sie nicht verursacht hat.

- [ ] `npm --prefix /home/dev/projects/EmotePurge-200fix/web ci` (Lockfile-treu). Danach
      `ls /home/dev/projects/EmotePurge-200fix/web/node_modules/.bin/ng` als Nachweis.
- [ ] `docker info` (Testcontainers brauchen Docker); kein `docker compose up` aus dem Worktree.
- [ ] Prüfen, dass `:5151` und `:4200` frei sind (`ss -ltn | grep -E ':5151|:4200'`) — nur relevant
      für T8/E2E, aber jetzt notieren.
- [ ] Baseline: `dotnet build /home/dev/projects/EmotePurge-200fix/EmotePurge.slnx` (0 Warnungen
      erwartet), `npm --prefix /home/dev/projects/EmotePurge-200fix/web test -- --watch=false`
      (3207 Tests erwartet, alle grün — der Flaky-Fall ist lokal grün). Ergebnis als Zahlen an den
      Orchestrator melden. `dotnet test` erst in T1 (lang; dort ohnehin nötig).
- [ ] Kein Commit.

**Abnahme:** `node_modules` vorhanden, Build 0 Warnungen, Vitest-Zahl gemeldet.

### T1 — O1 Backend: Null-Session summiert über alle Sets

**Kontext:** Abschnitt 0.1, Entscheidung E1. Eine Null-Session (`VoteSession.EmoteSetId == null`,
dynamische oder kuratierte Ballot) hat kein Set; ihr Nutzungskontext ist „Nutzung dieses Emotes im
Kanal im Sessionfenster", über alle Sets summiert. Eine Set-Session bleibt auf ihr Set beschränkt
(unverändert). Der Flush garantiert eine Zeile je (Emote, Set, Tag) — Summieren zählt nichts
doppelt.

**Dateien:** `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` (Doku + Signaturen),
neuer Wertetyp unter `src/EmotePurge.Core/Services/` (Name: der Scope einer Nutzungsabfrage über
Emote-Sets; ein `readonly record struct` o. ä. mit statischen Fabriken für die drei Zustände,
`default` = aktives Set), `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs`
(`GetTotalsByEmoteIdsAsync`, `GetDailySeriesAsync`),
`src/EmotePurge.Infrastructure/Services/VoteSessionQueryService.cs` (Aufruf `:120-121`, Kommentare
`:116-117` und `:211-222`), `tests/EmotePurge.Infrastructure.Tests/Integration/
UsageStatQueryServiceTests.cs` (Aufrufstellen `:253,360,379,390,404,1235`; neue Fälle),
`tests/EmotePurge.Infrastructure.Tests/Integration/VoteSessionQueryServiceTests.cs` (neue Fälle),
`docs/DECISIONS.md` (neuer Eintrag, englisch, oben einsortiert).

**Vertrag (in die Interface-Doku schreiben):**
- Scope *Set(id)*: wie heute — `WHERE EmoteSetId == id`; „Zeile vorhanden, Summe 0" bleibt
  unterscheidbar von „nie beobachtet" (bedingte Summe im Select, kein WHERE-Datumsfilter).
- Scope *AllSets*: **kein** `EmoteSetId`-Prädikat; ein Emote ist im Ergebnis, sobald es unter
  irgendeinem Set irgendeine Zeile hat; die Summe läuft über alle Zeilen im Fenster. Ein leerer
  `ActiveEmoteSetId` (`""`) spielt keine Rolle mehr.
- Scope *ActiveSet* für `GetTotalsByEmoteIdsAsync`: `ArgumentException` (nicht auflösbar ohne
  Kanal — Begründung wie heute in `:253-257`).
- `GetDailySeriesAsync`: *ActiveSet* (Default) und *Set(id)* wie heute; *AllSets* lässt das
  Set-Prädikat in **beiden** Queries weg (Tage `:170` **und** Bounds `:180`) — „first used" heißt
  dann „zuerst im Kanal benutzt". Der Kommentar `:165-168` („the drilldown agrees with the row it
  was opened from") bekommt den Zusatz, dass eine Null-Session-Zeile setagnostisch ist und ihr
  Drilldown deshalb ebenso.
- `VoteSessionQueryService.GetResultsAsync`: `session.EmoteSetId is null` → *AllSets*; sonst
  *Set(session.EmoteSetId)*. `channel.ActiveEmoteSetId` wird hier **nicht mehr gelesen**. Die
  `useCount`-Regel (`:222-224`) bleibt: Null-Session → unarchiviert `GetValueOrDefault(0)`,
  archiviert `null`; Set-Session → `TryGetValue` sonst `null`. Kommentar `:116-117` neu formulieren
  („exactly as before" ist falsch und fällt weg).

**Grenzfälle, die die Tests abdecken (Integration, Testcontainers, `Integration/`):**
- `UsageStatQueryServiceTests`: (1) *AllSets* summiert Zeilen desselben Emotes unter zwei Sets
  innerhalb des Fensters; (2) *AllSets* + Zeilen nur außerhalb des Fensters → Emote präsent mit 0;
  (3) *Set(id)* unverändert (bestehender Fall `:1224` bleibt, nur die Aufrufform ändert sich);
  (4) *ActiveSet* → `ArgumentException`; (5) `GetDailySeriesAsync` *AllSets*: Tage beider Sets in
  aufsteigender Reihenfolge, `TotalUseCount` = Summe beider, First/Last über beide Sets; zwei
  Sets am **selben** Tag ergeben **einen** Tageseintrag mit der Tagessumme (Vertrag von
  `EmoteDailyUsageDto`: ein Eintrag je Tag — prüfen, dass die Query dafür gruppiert, sonst
  gruppieren); (6) die bestehenden Daily-Fälle laufen mit Default-Scope unverändert.
- `VoteSessionQueryServiceTests`: (7) Null-Session, Fenster **vor** einem Set-Wechsel, Zeilen unter
  dem alten Set, Kanal-`ActiveEmoteSetId` = neues Set → `TotalUseCount` = Summe (heute: 0);
  (8) Null-Session mit Zeilen unter zwei Sets im Fenster → Summe; (9) Set-Session mit denselben
  Zeilen → nur ihr Set (bestehender Fall `:292` deckt das, bleibt grün); (10) Null-Session, Kanal
  mit `ActiveEmoteSetId = ""` → Summe statt 0; (11) Fenstergrenzen: Zeile am `StartedAt`-Tag zählt,
  Zeile am Tag vor `StartedAt` nicht (DateOnly-Konvertierung `:106-107`).

**Schritte:**
- [ ] Wertetyp in Core anlegen (BCL-only; `CoreAssemblyReferenceTests` erzwingt das), mit Doku,
      warum `null` hier nicht als „alle" taugt (E1).
- [ ] Interface umstellen (Totals: Scope statt `string`; Daily: Scope-Parameter mit Default), Doku
      je Methode nachziehen.
- [ ] Implementierung in `UsageStatQueryService` — Regel 10 beachten (die Totals-Query bleibt
      Single-Table; kein Navigations-Join vor dem GroupBy).
- [ ] `VoteSessionQueryService` umstellen, Kommentare korrigieren.
- [ ] Tests anpassen und ergänzen (oben). Seed-Helfer der beiden Testklassen wiederverwenden
      (`SeedChannelAsync` dort nimmt eine Aktiv-Set-ID, `VoteSessionQueryServiceTests:605` ohne —
      ggf. Überladung ergänzen).
- [ ] DECISIONS-Eintrag (englisch): Titel im Stil der bestehenden (Datum — Aussage), `**Betrifft:**`
      mit allen berührten Dateien, Absätze: was eine Null-Session misst und warum setagnostisch;
      warum ein benannter Scope statt `null`; dass der Flush Doppelzählung ausschließt; dass die
      Drilldown-Route (T2) dieselbe Semantik bekommt (T2 ergänzt diesen Eintrag um den
      `/daily`-Absatz in seinem eigenen Commit).
- [ ] Gates: `dotnet build` (0 Warnungen), `dotnet test EmotePurge.slnx`, `dotnet format
      --verify-no-changes`.
- [ ] Commit: `fix(core): sum a null-session's usage across every emote set` (Core + Infrastructure
      + Tests + DECISIONS in **einem** Commit — Regel 3; der Interface-Vertrag ist die Änderung).

**Abnahme:** Fälle (1)–(11) grün, alle bestehenden grün, kein Aufrufer außer
`VoteSessionQueryService` (grep `GetTotalsByEmoteIdsAsync` über `src/` zeigt genau einen Treffer
außerhalb der Implementierung), DECISIONS-Eintrag vorhanden.

### T2 — O1 Drilldown: `/daily` kann „alle Sets", die Vote-Seite fragt es für Null-Sessions

**Kontext:** Abschnitt 0.1 (Frontend-Zeilen), Entscheidungen E1/E2. Die Tageskurve hinter einer
Null-Session-Zeile muss dieselbe Semantik haben wie die Zahl auf der Zeile — sonst zeigt die Karte
die Summe und der Drilldown 0. Die Usage-Seite bleibt unberührt: sie sendet immer ein konkretes
Set (`shownSetId()`) oder nichts (= aktiv). Setzt T1 voraus (Scope-Typ, `GetDailySeriesAsync`).

**Dateien:** `src/EmotePurge.Api/Endpoints/UsageStatsEndpoints.cs` (`/daily`-Handler `:62-88`),
neuer Api-Test (Muster `tests/EmotePurge.Api.Tests/ChannelUsageSeriesWireFormatTests.cs`;
`IUsageStatQueryService` wird dort substituiert — prüfen, wie `ApiFactory` das macht),
`web/src/app/core/usage-stats/usage-stat.service.ts` (`getDailySeries` `:53-70`, Cache-Schlüssel),
`web/src/app/shared/emotes/emote-drilldown-dialog.ts` (`EmoteDrilldownData` `:41-48`, Konstruktor
`:327-335`), `web/src/app/features/voting/vote-session-detail-page.ts` (`openDrilldown` `:819-846`
inkl. Kommentar `:819-825`), Specs: `usage-stat.service.spec.ts`, `emote-drilldown-dialog.spec.ts`
(`:214-237`), `vote-session-detail-page.spec.ts` (`:627-655`), `docs/DECISIONS.md` (Absatz im
T1-Eintrag ergänzen).

**Vertrag:**
- Route: `GET /api/channels/{channelName}/usage-stats/daily?emoteId=…&from=…&to=…` plus
  **entweder** `emoteSetId=<id>` **oder** `setScope=all` **oder** keins von beiden (= aktives Set).
  Beide zusammen → 400 `invalid_emote_set_id`; unbekannter `setScope`-Wert → 400
  `invalid_emote_set_id`. Der Handler bleibt dünn: Parameter → Scope-Wert, sonst nichts. Der
  bestehende `EmoteSetIdValidationFilter` bleibt am Endpoint (er prüft `emoteSetId`, wenn
  vorhanden); die Kombinationsregel gehört in den Handler, **nicht** in den Filter (der hängt an
  Routen ohne `setScope`).
- Frontend-Service: `getDailySeries` drückt die drei Zustände ausdrücklich aus (Typ im Task
  wählen — kein String-Sentinel; z. B. ein kleiner Union-Typ), sendet `setScope=all` nur für
  „alle Sets", und der **Cache-Schlüssel unterscheidet** „aktiv" von „alle" (heute `setId ?? ''`
  — beide würden auf `''` kollidieren).
- `EmoteDrilldownData`: „aktives Set" (Usage-Seite ohne geladenes Set), „dieses Set" (Usage-Seite,
  Set-Session), „alle Sets" (Null-Session) sind drei benannte Zustände; die Doku `:41-47` wird
  entsprechend umgeschrieben. Die Usage-Seite (`openDrilldown` → `shownSetId()`) ändert ihre
  Semantik nicht — nur ggf. die Form, in der sie sie übergibt.
- Vote-Seite: `results.emoteSetId === null` → „alle Sets"; sonst „dieses Set". Kommentar
  `:819-825` korrigieren („falls back to the channel's active set … exactly right for a
  null-session" ist genau die falsche Aussage).

**Tests:**
- Api (WebApplicationFactory, Muster Wire-Format-Test): `setScope=all` → Service wird mit *AllSets*
  gerufen (Substitut prüft den Scope); ohne beides → *ActiveSet*; `emoteSetId=x` → *Set(x)*;
  `setScope=all&emoteSetId=x` → 400 mit `invalid_emote_set_id`; `setScope=bogus` → 400. Regel 11
  („Handler bekommen keine Tests") gilt für dünne Delegation — hier wird ein **Drahtvertrag**
  gepinnt, wofür `ChannelUsageSeriesWireFormatTests` die Präzedenz ist; das im Test-Kommentar
  sagen.
- Vitest `usage-stat.service.spec.ts`: „alle Sets" sendet `setScope=all` und kein `emoteSetId`;
  Cache-Schlüssel trennt aktiv/alle (zwei Aufrufe → zwei Requests).
- Vitest `emote-drilldown-dialog.spec.ts`: dritter Fall neben `:214` und `:226` — Daten mit „alle
  Sets" → Service mit „alle Sets" gerufen.
- Vitest `vote-session-detail-page.spec.ts:645`: kippt zu „openDrilldown asks for every set for a
  null-session"; `:627` (Set-Session → eigenes Set) bleibt.

**Schritte:**
- [ ] Handler + Api-Test.
- [ ] Service, Dialog-Data, Vote-Seite, Specs.
- [ ] DECISIONS: Absatz zum `/daily`-Scope im T1-Eintrag (Betrifft-Zeile um die Dateien ergänzen).
- [ ] Gates Backend (Build, `dotnet test` — mindestens `tests/EmotePurge.Api.Tests`, Format) und
      Frontend (Vitest, Lint, Format).
- [ ] Commits: `feat(api): let the daily series be read across every emote set` (Api + Test +
      DECISIONS-Absatz), `fix(web): chart a null-session's drilldown across every emote set`
      (Service, Dialog, Vote-Seite, Specs).

**Abnahme:** Die vier Api-Fälle und die drei Vitest-Ergänzungen grün; die Usage-Seiten-Specs
(`usage-stats-page.spec.ts`) unverändert grün; kein `setScope` in irgendeinem Request der
Usage-Seite (grep der Specs/Mocks).

### T3 — O2: Status vor Totals bei `channel.synced`, Status-Fehler sperrt statt zu schweigen

**Kontext:** Abschnitt 0.2, Entscheidungen E3/E4. Die #220-Logik (`setStatusOutcomeKnown`,
`liveMembersParams`, Stale-Guards in `load()`/`awaitSync`, `setStatusFailedChannel`) und ihre zehn
Specs dürfen nicht brechen.

**Datei:** `web/src/app/features/usage-stats/usage-stats-page.ts` (Live-Subscription
`:2006-2054`, `refreshSetStatus` `:2743-2756`), `usage-stats-page.spec.ts` (neue Fälle; Muster
`:245-500`, `:4519-4531`, `:3525-3560`).

**Zielverhalten:**
1. `channel.synced` (mit oder ohne gleichzeitiges `usage.flushed`): **zuerst** `stopAwaitingSync()`
   und der Status-Request. `emoteSetListResource.reload()` darf sofort laufen (hängt nicht an der
   Aktiv-ID). **Erst im Erfolg** des Status: (i) wenn der Status `selectedEmoteSetId()` bewegt hat
   (vorher/nachher per `untracked` vergleichen), lädt der Lade-Effekt (`:1807`) die Zeilen unter
   der neuen ID selbst — dann **kein** zusätzlicher `loadTotals` (sonst zwei Requests für dieselbe
   ID; `latestOnly` würde den Doppelten zwar entwerten, aber der Permit ist gezahlt); (ii) sonst
   `loadTotals(selectedEmoteSetId(), preserve + silent)` wie heute; (iii) `reloadLiveMembers()`
   ebenfalls erst hier (vor dem Status könnte es die Mitgliederliste eines Sets anfragen, das gerade
   aktiv wurde — ein verschenkter Permit im `TrackedEmoteSetPreview`-Bucket). Die Guards
   `awaitingEmoteSetId` und der Kanal-Guard aus `refreshSetStatus` gelten weiter.
2. `usage.flushed` **ohne** `synced`: unverändert (Totals silent; Probe-Gate → Status).
3. `refreshSetStatus` bekommt einen Fehlerzweig nach dem Vorbild `load():2819-2835`, mit der
   Abweichung E4 (DTO behalten, `setStatusChannel` un-claimen, `setStatusFailedChannel` setzen,
   Kanal-Guard). Feldkommentare `:1284-1293` anpassen: `setStatusFailedChannel` wird jetzt auch von
   einem gescheiterten Refresh geschrieben. Der Aufrufer `:2048` (Flush-Probe) bekommt dasselbe
   Verhalten — bewusst: ein Status, den wir nicht lesen können, ist in beiden Fällen unbekannt.
4. Erholung: der nächste erfolgreiche Status (nächstes `synced`, Refresh-Button, der
   `requestedSetStatusFor` zurücksetzt) claimt wieder; die Sperre hebt sich damit, der Dock kommt
   zurück, die Auswahl bleibt (kein `clear()`, weil `previousTotalsChannel === channelName`).

**Zu prüfende Wechselwirkungen (im Task ausdrücklich durchgehen und in Kommentaren belegen):**
- `setStatusOutcomeKnown` bleibt nach dem Un-claimen wahr (Failed-Kanal = aktueller Kanal) — die
  Mitgliederliste eines per URL gewählten Sets bleibt geladen; für `''` wird `selectedEmoteSetId()`
  `null` → `liveMembersParams` `undefined` → Resource idle.
- `viewKindStale` nach dem Un-claimen: `shown !== null` und `activeEmoteSetId() === null` →
  `shown !== active` ist wahr, `totalsNonActive` war `false` → stale wird **wahr**; der
  Stale-Effekt lädt aber nur, wenn `shownSetId() === selectedEmoteSetId()` — für `''` ist selected
  `null`, also nicht. Der Lade-Effekt lädt stattdessen (selected hat sich geändert). Kein
  Doppelladen. Für URL-gewähltes X: selected bleibt X, shown ist X, stale → Reload silent unter X
  — akzeptabel und korrekt (X ist jetzt „nicht-aktiv, weil unbekannt").
- Der Skeleton beim Set-Wechsel durch den Lade-Effekt ist **Bestandsverhalten** (heute genauso
  nach jedem Status, der die Aktiv-ID bewegt) und nicht Teil dieses Fixes (Abschnitt 5).
- Ein `channel.synced` für einen Kanal, den die Seite verlassen hat: die Guards in
  `refreshSetStatus` (`:2749`) und die #220-Fälle „a late answer for the channel left behind …"
  bleiben unverändert wirksam — der neue Erfolgs-Kontinuationscode muss **hinter** dem Kanal-Guard
  stehen.

**Vitest-Fälle (in einem eigenen `describe` mit dem `FakeEventSource`-Muster von `:245`):**
- (1) Sync mit Set-Wechsel, Status landet zuerst: nach `channelSynced` gibt es **keinen**
  `/totals`-Request, bevor `/active-set` beantwortet ist (`httpMock.expectNone` auf die Totals-URL
  vor dem Flush des Status); nach dem Status mit B kommt genau ein `/totals` mit `emoteSetId`
  abwesend (URL `''` → aktiv, kein expliziter Parameter) bzw. — je nach heutiger Form von
  `withEmoteSetId` bei `selectedEmoteSetId() === B` — mit B; eine vorher markierte Auswahl (Emote
  ohne Nutzung unter A) ist danach **noch markiert**, sofern es in B's Zeilen vorkommt.
- (2) Sync ohne Set-Wechsel: Status → genau ein `/totals` silent (kein Skeleton: `isLoading()`
  bleibt `false`), Auswahl bleibt.
- (3) Status-Fehler nach Sync (`/active-set` → 503): `activeEmoteSetId()` ist `null`,
  `importScopeCurrent()` `false`, der Dock-Zweig ist nicht gerendert (kein
  `app-mass-delete-panel` im DOM) bzw. — falls die Auswahl leer ist und der Dock ohnehin fehlt —
  `selectedEmoteSetId()` ist `null`; **kein** `/totals`-Request mit `emoteSetId=A`.
- (4) Erholung: nächstes `channelSynced` → Status B ok → `activeEmoteSetId()` = B, Dock wieder da,
  Auswahl unverändert.
- (5) Regression #220: die zehn Fälle aus `49cc39cd`/`4b1e8ce5` und AK 52 (`:3525`) laufen
  unverändert (keine Änderung an ihren Erwartungen erlaubt; wenn einer rot wird, ist der Fix falsch,
  nicht der Test).
- (6) `usage.flushed` allein: wie heute genau ein `/totals` silent, kein Status (Gate geschlossen).

**Schritte:**
- [ ] Live-Subscription umbauen (Reihenfolge), `refreshSetStatus` mit Erfolgs-Kontinuation und
      Fehlerzweig; Kommentare (englisch) an `:2006-2016` und `:2735-2743` nachziehen.
- [ ] Specs (1)–(6).
- [ ] Gates Frontend (Vitest komplett, Lint, Format).
- [ ] Commit: `fix(web): read the set status before the rows on channel.synced and treat a failed
      status as unknown`.

**Abnahme:** (1)–(6) grün; gesamte `usage-stats-page.spec.ts` grün; keine neuen i18n-Schlüssel;
Prüfergebnis zu E4 („erscheint ein Hinweis?") im Abschlussbericht.

### T4 — C2: GraphQL-Teilantwort ist `Unavailable`, nicht `NotFound`

**Kontext:** Abschnitt 0.3. `FetchV4PageAsync` liefert für Nicht-429-Fehler `Ok` mit gefülltem
`Errors`; `:796` liest `emoteSet: null` daneben als „7TV kennt das Set nicht" und der Breaker
verbucht einen **Erfolg** für eine Fehlerantwort.

**Dateien:** `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:788-822`,
`tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvApiClientEmoteSetPreviewTests.cs`.

**Vertrag:** `NotFound` **nur**, wenn `Status == Ok`, `emoteSets` vorhanden, `emoteSet == null`
**und** `Errors` null oder leer. Mit Errors → `Unavailable` mit Warn-Log (Vorbild `:682-690`, F17,
Spec §32 für den Besitzer-Lookup). Der Kommentar `:790-795` (Vorentscheidung 4) bekommt den Zusatz
„… and no errors block". Das deutsche Log `:798-800` (vom Epic hinzugefügt) wird **hier**
übersetzt, weil T4 die Zeilen ohnehin anfasst — T6 hat es deshalb aus seiner Liste gestrichen.
`:781` und `:815-817` sind Bestand auf `main` und bleiben unangetastet. Neue Log-Zeile englisch.

**Tests (Unit, `PagedStubHandler`, snake_case-Payloads wie `:148`):**
- Partialform `{"data":{"emote_sets":{"emote_set":null}},"errors":[{"message":"…","extensions":{"code":"…","status":500}}]}` → `Unavailable`, `Preview == null`.
- Kontrolle: derselbe Body **ohne** `errors` → weiterhin `NotFound` (bestehender Fall `:147`).
- Kontrolle: `errors` mit `status: 429` in der Partialform → `RateLimited` (der 429-Zweig läuft
  vor dem NotFound-Check — belegen, nicht annehmen).
- Breaker-Folge: kein neuer Breaker-Test nötig, wenn die Zuordnung `Unavailable → SevenTvUnavailable
  → RecordFailure` schon in `HardenedForeignEmoteSetService`-Tests gepinnt ist (grep
  `RecordFailure` in `tests/…/HardenedForeignEmoteSetServiceTests*`); fehlt sie, einen Fall
  ergänzen, der eine `Unavailable`-Antwort des inneren Dienstes als Breaker-Fehlschlag verbucht.

**Schritte:**
- [ ] Bedingung + Log + Kommentar.
- [ ] Tests wie oben.
- [ ] Gates Backend (Build, `dotnet test` — mindestens `tests/EmotePurge.Infrastructure.Tests`
      mit Filter auf die Unit-Klasse plus die Hardened-Tests, Format).
- [ ] Commit: `fix(infra): treat a partial 7TV preview answer with errors as unavailable, not as an
      unknown set`.

**Abnahme:** die drei neuen Fälle grün; `UnknownSetId_…_IsReportedAsNotFound_NotUnavailable`
unverändert grün.

### T5 — C3: Set-ID-Regex endet am absoluten Ende

**Kontext:** Abschnitt 0.4. `$` ohne `Multiline` akzeptiert ein abschließendes `\n`. Ohne Trim
kommt `"abc\n"` als Query-Wert (`%0A`) durch die Validierung und landet ordinal-verglichen in
Cache-Schlüsseln, Coalescer-Schlüsseln und 7TV-Aufrufen.

**Dateien:** `src/EmotePurge.Api/Validation/EmoteSetIdValidation.cs:15`,
`tests/EmotePurge.Api.Tests/EmoteSetIdValidationTests.cs` (Theory `:48-56` erweitern), ggf. weitere
aus der Inventur.

**Inventur (Vorab-Ergebnis, im Task gegen den Stand verifizieren):**
| Datei | Zeile | Muster | `$` | Multiline | Wert getrimmt/normalisiert? | Entscheidung |
|---|---|---|---|---|---|---|
| `src/EmotePurge.Api/Validation/EmoteSetIdValidation.cs` | 15 | `^[0-9A-Za-z]{1,32}$` | ja | nein | **nein** | **mitnehmen** |
| `src/EmotePurge.Api/Validation/ChannelNameValidation.cs` | 8 | `^[a-z0-9_]{4,25}$` | ja | nein | ja — `ChannelName.Normalize()` (`Trim().ToLowerInvariant()`) läuft vorher; `Trim()` entfernt `\n` | nicht mitnehmen (Lücke durch Normalisierung geschlossen; Bestand außerhalb des Diffs) |
| `src/EmotePurge.Api/Program.cs` | 339 | `-[A-Za-z0-9_-]{8}\.(js\|css)$` auf `Request.Path` | ja | nein | nein | nicht mitnehmen: kein Validierungs-Gate, sondern die Cache-Header-Entscheidung für gehashte Assets; ein Fehlmatch kostet höchstens den `immutable`-Header, und die Zeile ist Bestand außerhalb des Epic-Diffs. Im Task per `git blame`/Diff bestätigen; ist sie wider Erwarten Teil des Diffs, in Abschnitt 5 melden statt still ändern |
| `web/src/app/core/channels/channel-name.ts:9`, `web/src/app/features/contact/contact-page.ts:48` | — | TypeScript | ja | — | — | **nicht betroffen** (JS-Semantik: `$` ohne `m` nur am absoluten Ende) |

**Regel für „mitnehmen":** nur Muster, die (a) mit `$` statt `\z` enden, (b) ohne
`RegexOptions.Multiline` laufen und (c) auf einen Wert treffen, der **nicht** vorher getrimmt oder
normalisiert wird. TypeScript-Regexe sind **nicht** betroffen (`$` ohne `m` matcht in JavaScript nur
am absoluten Ende) — nicht anfassen.

**Tests:** `"abc\n"` → ungültig (Regression); `"abc\r\n"` → ungültig (dokumentiert, war schon so);
`"abc\r"` → ungültig; die bestehenden Positivfälle unverändert.

**Schritte:**
- [ ] `$` → `\z` (nur dort, wo die Regel greift), Doku-Kommentar in einem Satz, warum `\z`.
- [ ] Tests.
- [ ] Gates Backend (Build, `tests/EmotePurge.Api.Tests`, Format).
- [ ] Commit: `fix(api): anchor the emote-set id pattern at the absolute end of the input`.

**Abnahme:** Theory-Fälle grün; grep zeigt kein `$"` mehr in einer Validierung, die die Regel
erfüllt.

### T6 — Deutsche Log-/`throw`-Meldungen im Epic-Diff → englisch

**Kontext:** CLAUDE.md „Sprache": Log- und `throw`-Messages sind seit #152 englisch. Nur Zeilen,
die der Epic-Diff (`git diff origin/main...HEAD -- 'src/*.cs'`) **hinzugefügt** hat; keine
Bestandsdateien außerhalb des Diffs; **Structured-Logging-Platzhalter (`{SetId}`, `{ChannelName}`,
…) unverändert lassen**, damit Log-Abfragen und ggf. Tests auf Property-Namen weiter greifen.
Kommentare sind **nicht** Gegenstand (deutsche Kommentare bleiben stehen).

**Inventur (Vorab-Ergebnis, im Task gegen den Stand verifizieren; Zeilen können sich durch T4
verschoben haben):**
Elf hinzugefügte Zeilen (Kriterium: `+`-Zeilen des Diffs mit deutschem Log-Text; verifiziert
gegen `origin/main`):

| Datei (`src/EmotePurge.Infrastructure/…`) | Zeile | Meldung (Platzhalter bleiben) |
|---|---|---|
| `Services/ForeignEmoteSetService.cs` | 123 | „Fremdkanal-Vorschau für {ChannelName}: 7TV kennt das zuvor aufgelöste Set nicht mehr." |
| dito | 163 | „Set-Vorschau für {SetId} (Kanal {ChannelName}): 7TV meldet Überlast (429)." |
| dito | 168 | „Set-Vorschau für {SetId} (Kanal {ChannelName}): 7TV-Set-Abruf fehlgeschlagen." |
| dito | 176 | „Set-Vorschau für {SetId} (Kanal {ChannelName}): 7TV kennt dieses Set nicht." |
| dito | 182 | „Set-Vorschau für {SetId} (Kanal {ChannelName}): providerweites Request-Budget während der Seitenabfrage erschöpft." |
| `SevenTv/ForeignEmoteSetCache.cs` | 64 | „Lesen des Fremdkanal-Vorschau-Caches für {Identifier} fehlgeschlagen — behandle als Miss." |
| dito | 79 | „Schreiben des Fremdkanal-Vorschau-Caches für {Identifier} fehlgeschlagen — Ergebnis wird nur für diesen Request verwendet." |
| `SevenTv/HardenedForeignEmoteSetService.cs` | 201 | „Fremdkanal-Vorschau für {Identifier}: Circuit-Breaker offen, kein Upstream-Aufruf (verbleibende Offenzeit {RemainingSeconds}s)." |
| dito | 217 | „Fremdkanal-Vorschau für {Identifier}: providerweites 7TV-Budget nach {TimeoutSeconds}s Wartezeit nicht verfügbar." |
| dito | 293 | „7TV-Circuit-Breaker für Fremdkanal-Vorschauen geöffnet (ausgelöst durch {Identifier}, Status {Status})." |
| `SevenTv/SevenTvApiClient.cs` | 799 | „7TV-Vorschau-Abruf für Set {SetId}: 7TV kennt dieses Set nicht (emoteSet: null), Seite {Page}." — **T4 fasst diese Zeile an; dort übersetzen, hier streichen** |

**Nicht im Umfang (Bestand auf `main`, nicht vom Epic hinzugefügt):**
`ForeignEmoteSetService.cs:100,110,115,130` (Login-Pfad der Fremdkanal-Vorschau),
`SevenTvApiClient.cs:781` (429-Log) und `:816` („lieferte keine verwertbaren Daten") — das Review
hatte `:816` genannt; die Zeile steht so schon auf `main`. Kein Test unter `tests/` prüft einen
dieser Meldungstexte per String-Assertion (grep-verifiziert) — im Task trotzdem gegenprüfen.

**Schritte:**
- [ ] Inventur mit `git diff origin/main...HEAD --unified=0 -- 'src/*.cs' | grep '^+'` und Suche
      nach Umlauten/deutschen Signalwörtern in `Log*(`/`throw new` erneuern; Liste im
      Abschlussbericht.
- [ ] Übersetzen — Bedeutung erhalten, Platzhalter und Reihenfolge der Argumente unverändert,
      Log-Level unverändert.
- [ ] Tests, die Meldungstexte per String prüfen (Inventur-Ergebnis; sonst grep nach markanten
      Teilstrings jeder Meldung unter `tests/`), anpassen.
- [ ] Gates Backend (Build, `dotnet test EmotePurge.slnx`, Format).
- [ ] Commit: `chore(infra): write the emote-set log messages in English`.

**Abnahme:** grep über den Diff findet keine deutschen Log-/throw-Meldungen mehr; Tests grün.

### T7 — Flaky Spec deterministisch machen (erst Ursache, dann Fix)

**Kontext:** Abschnitt 0.6, Entscheidung E5. Der Fall ist in CI **2/2 rot** auf demselben SHA und
lokal **16/16 grün** (mit Coverage, `taskset -c 0`, Last). Er ist in der CI-Umgebung
deterministisch — also gibt es einen benennbaren Unterschied, und der ist zuerst zu finden.
**Keine Produktänderung** an `import-conflict-resolution-step.ts`; kein Hochsetzen von `settle`.

**Dateien:** `web/src/app/shared/seven-tv/import-conflict-resolution-step.spec.ts` (Helfer
`:286-315`, Fälle `:514-567`, `:867-890`, ggf. `:587-640`), Vergleich: `.github/workflows/
sonarcloud.yml:55-71`, `web/angular.json:81-86`, `web/src/test-setup.ts`.

**Phase A — Unterschied finden (Reihenfolge, Abbruch nach zwei Fehlschlägen je Methode):**
- [ ] Umgebung spiegeln, in dieser Reihenfolge einzeln und kombiniert: (1) **Node 22** statt 24
      (`npx -y node@22 …` oder `nvm use 22` — `web/.nvmrc` sagt 22; der Aufruf muss `ng test` unter
      Node 22 fahren, nicht nur `npx`), (2) `CI=true` in der Umgebung, (3) exakt der CI-Befehl
      `npm test -- --watch=false --coverage --coverage-reporters=lcov` aus `web/`, (4) volle Suite
      statt Einzeldatei (die Datei läuft in CI als eine von 150 — Reihenfolge und Pool-Belegung
      unterscheiden sich), (5) `taskset -c 0-3` (4 vCPU wie der Runner) plus CPU-Last. Je Variante
      protokollieren: rot/grün und die Dauer der drei langsamen Fälle (`2325 / 1610 / 1633 ms` in
      CI) — auch lokal-grün mit ~1,6 s bei den Schwesterfällen ist ein Befund: dann wartet
      `settle` auch lokal auf eine Bedingung, die erst spät wahr wird.
- [ ] Wird es rot: Ursache eingrenzen — ist `requestAnimationFrame` in der jsdom-Umgebung
      definiert (sonst nimmt CDK den `asapScheduler`, s. 0.6)? Feuert das `scroll`-Event des Mocks
      **vor** oder **nach** der CDK-Subscription in `afterNextRender` (`scrolling.mjs:663`)? Liefert
      `measureScrollOffset` den gemockten Offset zum Zeitpunkt der Range-Berechnung? Ist die
      Viewport-Instanz, auf der gespyt wird, dieselbe, die das Event empfängt?
- [ ] Wird es mit keiner Methode rot: Diagnoseausgabe in den Spec (Wegwerf-Branch im Worktree, nie
      gepusht), die je `settle`-Runde `rowElements().length`, den Render-Range des Viewports und
      `typeof requestAnimationFrame` loggt; Rückschluss aus dem CI-Log-Ausschnitt in 0.6 (die
      Schwesterfälle brauchen in CI ~1,6 s — welche ihrer Bedingungen wird dort spät wahr?).
      Ergebnis: eine benannte Ursache oder ein benannter Verdacht mit Beleg — im Abschlussbericht,
      nicht in der Plandatei.

**Phase B — deterministisch machen (Produktcode unverändert):**
- [ ] Eine der beiden erlaubten Formen: (i) Fake-Timer (`vi.useFakeTimers` mit `toFake` für
      `setTimeout`, `requestAnimationFrame`, ggf. `queueMicrotask` nicht) und gezieltes Vorspulen
      (`advanceTimersByTime`/`advanceTimersToNextFrame`) statt `settle`-Echtzeit — dabei beachten,
      dass CDK den Scheduler **beim Modul-Laden** wählt (`SCROLL_SCHEDULER`), Fake-Timer also vor
      dem ersten Import wirken müssen oder der Test den `asapScheduler`-Pfad bewusst akzeptiert;
      oder (ii) die Range-Aktualisierung direkt anstoßen: nach dem simulierten Scroll die
      öffentliche CDK-API benutzen, die den Strategy-Callback auslöst (`checkViewportSize()` ruft
      `onContentScrolled` nicht; zu prüfen, ob `setRenderedRange`/`scrollToOffset` + Event
      synchron reicht oder ob der Test die Strategie über `viewport` erreicht) und danach nur noch
      `fixture.detectChanges()` + `whenStable()` — ohne Echtzeit-Schleife.
- [ ] `settle`-Helfer: wenn er nach dem Umbau noch Aufrufer hat, bleibt er; jeden verbliebenen
      Aufruf begründen (Fälle `:552`, `:562`, `:564`, `:606`, `:632-634`, `:797`, `:883` durchgehen —
      dieselbe Anfälligkeit haben alle, die auf `rowAt(199) !== null` oder auf einen Fokus warten,
      der erst nach einem Frame landet). `settle(() => false, 5)` (Negativ-Warten „nichts passiert
      mehr") durch ein deterministisches Äquivalent ersetzen (Fake-Timer vorspulen, dann prüfen).
- [ ] Der Test muss **ohne** Echtzeitwarten grün sein und unter der in Phase A gefundenen
      Umgebung (Node 22, CI=true, Coverage, volle Suite) ebenfalls.

**Abnahme:**
- Spec-Datei **20×** grün mit Coverage unter `taskset -c 0` plus Last (z. B. `stress`/`yes >
  /dev/null` auf demselben Kern); zusätzlich 3× volle Suite mit Coverage unter Node 22 und
  `CI=true`.
- Dauer der drei ehemals langsamen Fälle jeweils < 300 ms (Beleg im Bericht).
- Kein Diff unter `web/src/app/shared/seven-tv/import-conflict-resolution-step.ts`.
- Commit: `test(web): drive the conflict step's virtual-scroll cases with fake frames instead of
  real time` (Wortlaut nach tatsächlicher Form (i)/(ii) anpassen).

### T8 — Schluss-Gates, Coverage, Übergabe

**Kontext:** „Fertig" heißt in diesem Repo: alle Suiten grün (CLAUDE.md „Arbeitsweise"), Coverage
vorab geschätzt, Zweitmeinung eingeholt und **vorgelegt** (Regel 1/22).

- [ ] `dotnet build /home/dev/projects/EmotePurge-200fix/EmotePurge.slnx` — 0 Warnungen
      (`--no-incremental`, damit Warnungen nicht im Cache verschwinden; Memory #53/#54/#55).
- [ ] `dotnet test /home/dev/projects/EmotePurge-200fix/EmotePurge.slnx`.
- [ ] `dotnet format /home/dev/projects/EmotePurge-200fix/EmotePurge.slnx --verify-no-changes`.
- [ ] `npm --prefix /home/dev/projects/EmotePurge-200fix/web test -- --watch=false`, `run lint`,
      `run format:check`.
- [ ] `npm --prefix /home/dev/projects/EmotePurge-200fix/web run e2e` — **nur** wenn `:5151` und
      `:4200` frei sind; sonst begründet auslassen und dem Orchestrator melden. T2/T3 ändern keine
      E2E-Mocks; bricht ein E2E-Fall, ist er zuerst gegen Speicherdruck zu wiederholen (Memory
      „Rote E2E = Speicherdruck"), dann zu untersuchen.
- [ ] `node /home/dev/projects/EmotePurge-200fix/scripts/coverage-local.mjs --base
      origin/feat/emote-sets-200` — Ergebnis lesen als Anlass hinzusehen (Näherung), nicht als
      Urteil; unter 80 % je Datei: nachtesten, wo neue Zeilen wirklich ungedeckt sind.
- [ ] `git log --oneline origin/feat/emote-sets-200..HEAD` gegen die Commit-Liste der Tasks
      prüfen (ein Commit je Task, DECISIONS in T1/T2).
- [ ] **Kein Push, kein PR** aus diesem Task — der Orchestrator gibt den Push frei, holt danach die
      Zweitmeinung (`/codex:review --model gpt-6-sol`, `--scope branch`, Memory „Codex-Review-
      Fallen") und legt sie dem Betreiber vor.

**Abnahme:** alle Gates grün mit Zahlen im Bericht; Coverage-Schätzung je geänderter Datei im
Bericht.

### T9 — Live-Verifikation (Regel 16; führt der Orchestrator mit dem Betreiber durch)

**O1 — Null-Session mit Zeilen unter zwei Sets (lokale Dev-DB, kein Prod):**
Der Flush-Pfad steht nicht zur Verfügung (kein zweiter Worker gegen die Dev-DB, Messfenster
#69/#73). Vorschlag ohne Verbiegen echter Daten: auf dem eigenen Kanal `sensitron` (zwei
NORMAL-Sets) ein Emote wählen, das im Sessionfenster einer **neu angelegten** Null-Session liegt;
per SQL **eine** zusätzliche `UsageStats`-Zeile für dieses Emote unter der ID des **anderen**
NORMAL-Sets an einem Datum im Fenster einfügen (eindeutiger `UseCount`, z. B. 4711), Vote-Detail
laden: Zahl = bisherige Summe + 4711 (vorher: nur das aktive Set); Drilldown öffnen: Tageskurve
zeigt den Tag mit 4711, „zuerst benutzt" ggf. früher als vorher; danach die Zeile per SQL wieder
löschen (Schlüssel `(EmoteId, EmoteSetId, Date)`), Session beenden/löschen. Die Befehle bereitet der
Orchestrator vor (Platzhalter für Passwort; Dev-DB-Verbindung aus `appsettings.json`); der
Betreiber führt sie aus oder gibt sie frei. Alternativ: das echte Umschalten aus O2 unten erzeugt
mit dem **Prod**-Worker keine Dev-Zeilen — daher der SQL-Weg.

**O2 — Set-Wechsel per 7TV (eigener Kanal `sensitron`, Browser gegen `dotnet run` + `npm start`):**
(1) Usage-Seite öffnen, zwei Emotes markieren, die im **nicht** gleich aktiven Set keine Nutzung
haben; auf 7TV das andere NORMAL-Set aktivieren; nach `channel.synced`: Auswahl bleibt, Dock nennt
das neue Set, keine Pruned-Meldung; Netzwerk-Tab: `/active-set` **vor** `/totals`. (2)
Status-Fehler simulieren: Playwright-Node-Skript mit `web/node_modules/playwright` (der
Playwright-MCP hat hier kein Chrome — Memory „Playwright-MCP ohne Chrome"), das
`**/api/channels/sensitron/emotes/active-set` nach dem Login per `page.route` mit 503 beantwortet,
dann Set auf 7TV umschalten (oder `channel.synced` abwarten): Dock verschwindet, kein Löschdialog
mit „aktiv", `/totals` ohne `emoteSetId=A`; Route wieder freigeben, nächstes `synced` (Resync-Knopf
im Admin oder erneutes Umschalten): Dock zurück, Auswahl erhalten. Am Ende das ursprüngliche Set
wieder aktivieren. **Kein REMOVE auslösen** — die Verifikation endet vor dem Löschdialog.

**C2 — nicht live erzeugbar:** Eine 7TV-Teilantwort (`emote_set: null` **mit** `errors`) lässt
sich gegen die echte API nicht auf Kommando provozieren (Schema-Drift auf einem Nachbarfeld ist ein
Fehlerbild des Providers, keine Eingabe). Der Unit-Test mit dem exakten Body genügt; der
Breaker-Pfad ist über die Hardened-Tests gepinnt.

**C3, Logs, Flaky:** keine Live-Verifikation; Tests bzw. CI-Lauf des PRs sind der Nachweis. Für den
Flaky-Fall gilt zusätzlich: der Sonar-Job `analyze` auf dem PR `fix/epic-200-review` muss grün sein
— das ist die einzige Umgebung, in der er bisher rot war.

---

## 3. Reihenfolge und Abhängigkeiten

```
T0 ─┬─ T1 ─ T2
    ├─ T3
    ├─ T4 ─ T6        (T6 nach T4, damit die Zeilen in SevenTvApiClient.cs nicht zweimal angefasst werden)
    ├─ T5
    └─ T7
                ─ T8 ─ T9
```

T1→T2 sequenziell (Scope-Typ). T3, T4/T6, T5, T7 sind untereinander unabhängig und können parallel
in getrennten Subagents laufen — **aber alle im selben Worktree auf demselben Branch**: der
Orchestrator serialisiert die Commits (jeder Task staged nur seine Dateien; Memory „Parallel
sessions share git index"). Wer parallel laufen soll, bekommt das ausdrücklich gesagt; sonst
sequenziell in der Reihenfolge T1, T2, T4, T6, T5, T3, T7, T8.

## 4. Commit-Schnitt (Soll)

1. `fix(core): sum a null-session's usage across every emote set` — T1 (+ DECISIONS)
2. `feat(api): let the daily series be read across every emote set` — T2 (+ DECISIONS-Absatz)
3. `fix(web): chart a null-session's drilldown across every emote set` — T2
4. `fix(web): read the set status before the rows on channel.synced and treat a failed status as unknown` — T3
5. `fix(infra): treat a partial 7TV preview answer with errors as unavailable, not as an unknown set` — T4
6. `chore(infra): write the emote-set log messages in English` — T6
7. `fix(api): anchor the emote-set id pattern at the absolute end of the input` — T5
8. `test(web): …` — T7

Attribution-Zeile laut Session-Vorgabe ans Ende jeder Commit-Message.

## 5. Offene Punkte für den Betreiber

1. **Skeleton beim gepushten Set-Wechsel.** Nach T3 lädt der Lade-Effekt die Zeilen unter der
   neuen Aktiv-ID mit Skeleton (`isLoading`), obwohl der Nutzer nichts angefordert hat — das ist
   heutiges Verhalten (der Effekt lief auch bisher, nur *nach* dem falschen silent-Reload). Die
   Regel „neither the selection nor the skeleton may move under the user" gilt damit für den
   Set-Wechsel-Fall nicht. Fix wäre eine eigene Silent-Variante im Lade-Effekt für „Aktiv-ID hat
   sich bewegt" — außerhalb des Fix-Pakets. Empfehlung: Folge-Issue.
2. **Sichtbarer Sperrgrund bei Status-Fehler (E4).** Der Dock verschwindet mit der Set-Auswahl,
   ein Hinweistext dazu existiert nur, wenn T3 einen findet. Falls nicht: neuer i18n-Text (de/en)
   „Aktives Set unbekannt — Löschen gesperrt, bis der Status wieder gelesen werden kann" oder
   Akzeptanz des stillen Verschwindens. Entscheidung erbeten; T3 baut **keinen** Text ohne sie.
3. **Server-Wahrheit in `/totals` (E3).** Als Folge-Issue vorgeschlagen: `/totals` (und `/series`)
   liefern die aufgelöste Set-ID und `isActiveSet`; die Seite stempelt Server-Wahrheit statt
   `activeAtRequest`. Beseitigt die letzte Klasse von Stale-Stempeln, kostet einen
   Drahtvertrags-Wechsel mit > 40 Mock-Stellen.
4. **`/daily`-Scope für die Usage-Seite.** `setScope=all` existiert nach T2 auf der Route, die
   Usage-Seite nutzt es nicht. Soll ein Drilldown aus der Usage-Seite je „alle Sets" anbieten
   (z. B. als Umschalter im Dialog), ist das ein eigenes Feature.
5. **`setStatusFailedChannel` wird nie gelöscht** (dokumentierte #220-Lücke, `:677-678`). T3
   schreibt das Feld jetzt auch aus `refreshSetStatus`; die Lücke wird dadurch nicht größer, aber
   häufiger erreichbar. Kein Handlungsbedarf, nur Kenntnisnahme.
6. **Flaky-Ursache könnte außerhalb des Specs liegen** (z. B. `SCROLL_SCHEDULER`-Wahl unter Node 22
   / jsdom). Findet T7 eine Ursache, die ein Spec-Umbau nur umgeht (etwa ein fehlendes
   `requestAnimationFrame` in der Testumgebung), ist zu entscheiden, ob `web/src/test-setup.ts`
   einen Polyfill bekommt — das wäre eine Änderung an der Testumgebung aller Specs, nicht am
   Produkt, aber auch nicht „nur dieser Spec". T7 meldet, entscheidet nicht.
