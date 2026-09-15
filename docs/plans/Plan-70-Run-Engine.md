# Plan #70 — Run-Engine: Zeilen-Key, `abortOn`-Hook, Run-Arbiter

Umsetzungsplan für Issue [#70](https://github.com/emotepurge/EmotePurge/issues/70) (Kind K1 von #38).
Erstellt 2026-09-05 auf `feat/emote-import-38` gegen den Code-Stand `06b474d`. Quellen: Issue-Text,
`docs/designs/Emote-Import-38-2026-09-05.md` (Abschnitte „Constraints" und „Ausführung"),
`CLAUDE.md`, `web/.claude/CLAUDE.md`, der betroffene Code.

**Der Plan enthält keinen Code.** Signaturen stehen als einzeilige Verträge, alles andere ist
Absicht, Grenzfall und Reihenfolge. Jeder Task ist für einen Subagent mit frischem Kontext
geschrieben; er liest vor der Arbeit die unter „Betroffene Dateien" genannten Dateien ganz.

Reihenfolge der Arbeit in jedem Task: **Tests zuerst** (rot), dann Umsetzung (grün), dann die im
Task genannte Doku, dann die Gates aus Abschnitt 4.

---

## 1. Verifikation des Ist-Zustands

Gegen `web/src/app/core/seven-tv/seven-tv-run-engine.ts` (505 Zeilen),
`web/src/app/shared/seven-tv/mass-delete-panel.ts` (399), `restore-panel.ts` (132),
`run-progress-panel.ts` (133), `web/src/app/features/usage-stats/usage-stats-page.ts`,
`src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs`.

| Behauptung des Issues | Stimmt? | Tatsächlicher Befund |
|---|---|---|
| `RunQueueEmote { emoteId; sevenTvEmoteId; name }` (`:47-51`) | ja | Zeilen 47–51, `emoteId` ist Pflichtfeld mit Kommentar „internal id — used for the closing bookkeeping". |
| `setStatus` matcht `item.emoteId === emoteId` und mappt alle Treffer (`:484-488`) | ja | Zeilen 484–488. **Ergänzung:** zwei Aufrufstellen (`:207` in-progress, `:211` done/failed), beide mit `emote.emoteId`. Außerdem baut `finish()` die `doneIds` aus `item.emoteId` (`:498`). Sonst matcht nichts in der Engine auf Identität — `cancel()` und `progress` arbeiten rein über `status`. |
| `RunResult.doneIds` speist `sync-deleted`/`sync-restored` (`EmoteEndpoints.cs:50`, `:88`) | ja | `:50` = `MarkDeletedAsync`, `:88` = `MarkRestoredAsync`. Die Ids kommen aus `seven-tv-delete.service.ts:142-144` und `seven-tv-restore.service.ts:130-137`. |
| `RunResult.doneIds` speist `deleted.emit` (`mass-delete-panel.ts:211-216`) | **teilweise** | Zeilen stimmen, aber der `effect` liest **nicht** `RunResult.doneIds`, sondern rechnet aus `deleteService.queue()` nach (`status === 'done'` → `item.emoteId`). Gleiche Liste, anderer Pfad — und dieser Pfad wird ein Kompilierfehler, sobald `emoteId` optional ist (`output<string[]>`). `RunResult.doneIds` liest das Panel nur in `:93` (`run.result.doneIds.length > 0`). |
| `RunOperation = { label, buildRequest }` (`:62-72`) | ja | Zeilen 62–72. |
| Fehlerklassifikation privat (`:475-482`) | **ungenau** | `describeHttpError` ist 466–482 als Ganzes; 475–482 sind nur die Zweige 0/429/generisch. Die GQL-seitige Klassifikation ist davon getrennt: `isRateLimitError` (`:116-118`) und die Message-Durchreichung (`:304-317`). Substanz stimmt: alles privat. |
| 401/403 löschen den Token (`:466-472`) | ja | 467–472, innerhalb `describeHttpError`, also **innerhalb** `runOne` (`catchError`, `:331-334`) — bevor das Ergebnis die Queue erreicht. |
| GQL-Fehler liefern `message` roh (`:304-317`) | ja, mit Folge | Der rohe 7TV-Text ist heute schon das, was auf der Queue-Zeile und in der Fehlerliste steht (`seven-tv-delete.service.spec.ts` pinnt `errorMessage === 'emote not found'`). Eine „Übersetzung" gibt es nur für HTTP-Fehler (`describeHttpError`) und für das Rate-Limit-Aufgeben (`:278`). **Folge für den Hook:** für den GQL-Fall ist nichts umzuleiten; was fehlt, ist der HTTP-Status, den `describeHttpError` heute konsumiert, ohne ihn weiterzugeben. |
| Ausschluss: `mass-delete-panel.ts:58`, `:93`, `:292-294`, `restore-panel.ts:31` | ja | Alle vier Stellen stimmen zeilengenau. `:58` prüft nur `deleteService.isRunning()` — ein laufender Restore sperrt den Delete-Start tatsächlich nicht. **Ergänzung:** `:66` (Button „Auswahl aufheben") prüft ebenfalls nur `deleteService.isRunning()`, ist aber keine Start-Stelle (s. R8). |
| `dockVisible()` (`usage-stats-page.ts:615-621`) | ja | 615–622. Für K1 unverändert; der Import kommt erst in K2/K3 dazu. |
| „Die Start-Stelle zeigt den Bestand-Hinweis ‚ein Lauf ist aktiv'" | **nein** | Es gibt keinen solchen Hinweis: kein i18n-Key (`massDelete.*`, `restore.*` in `web/public/i18n/de.json` geprüft), kein Text im Template. Die Buttons sind heute schlicht `[disabled]`. AC 7 („kein neuer i18n-Key nötig") und AC 5 („deaktiviert") passen zu **disabled ohne Text**; der Plan bleibt dabei (Offene Frage 2). |
| „Delete- und Restore-Service setzen `key = emoteId` beim Aufbau der Queue" | **so nicht** | Die Queue-Zeilen bauen heute die **Aufrufer** (`mass-delete-panel.ts:392-396` und `:338-342`, `restore-panel.ts:123-127`) als `RunQueueEmote`-Literale; die Services reichen sie unverändert an `engine.start` (`seven-tv-delete.service.ts:85`, `seven-tv-restore.service.ts:81`). Damit die Services den Key setzen können, muss ihr Eingabetyp vom Engine-Typ abweichen (Entscheidung R3). |
| Dateitabelle: `run-engine`, `arbiter`, beide Services, beide Panels, DECISIONS | **unvollständig** | Fehlen und brechen beim Kompilieren, sobald `emoteId` optional ist: `web/src/app/shared/seven-tv/run-progress-panel.ts:179` (`@for … track item.emoteId`) und `web/src/app/shared/export/purge-run-export.ts:55` (`emoteId: item.emoteId` in das Pflichtfeld `PurgeRunRow.emoteId`). Außerdem `mass-delete-panel.ts:297/:314` (`doneItems` aus `run.result.items` als `{ emoteId: string … }[]` getypt) und `:211-216` (s. o.). Issue-Punkt 4 („`purge-run-export.ts` bleibt unverändert") ist damit nur für Dateiformat und Parser haltbar, nicht für die Eingabetypen von `buildPurgeRunProtocol` (Entscheidung R3). |
| `DeleteQueueEmote`/`DeleteQueueItem` sind historische Aliase auf die Engine-Typen (`seven-tv-delete.service.ts:47-49`) | (nicht behauptet, relevant) | `DeleteQueueEmote = RunQueueEmote` ist heute ein Alias. Genau dieser Alias wird in R3 zum eigenständigen Eingabetyp der Services. |
| E2E: „bestehende Delete-/Restore-Szenarien bleiben grün" | **nichts Spezifisches** | Es gibt keinen E2E-Fall, der einen Delete- oder Restore-Lauf startet. Berührt sind nur `vote-ballot.e2e.spec.ts` (Button „Löschen vorschlagen" — ein anderer Button), `touch-mobile.e2e.spec.ts:66` (Restore-Panel fehlt auf `coarse`) und das Audit-Szenario `usage-stats-restore-import-error` (`ui-audit.audit.ts:663`, Fehlerbanner ohne Lauf). Das Gate ist daher „Suite bleibt grün", nicht „Szenario X". |
| Mass-Delete-Panel auch auf der Voting-Seite | (Design, relevant) | Bestätigt: `vote-session-detail-page.html:150-157`. Panel-Änderungen wirken dort automatisch (R5). |

---

## 2. Risiken und Grenzfälle — mit Entscheidung

### R1 — Verklemmter Arbiter. **Entschieden: strukturell ausgeschlossen.**

Ursprüngliches Risiko: ein Arbiter mit Handbuchführung (`tryAcquire`/`release`) sperrt **alle drei**
Start-Buttons app-weit bis zum Reload, sobald ein Pfad `activeRun` gesetzt lässt, während keine
Engine mehr läuft. Der realistischste Pfad war: `tryAcquire` gewinnt, `engine.start` liefert `false`,
weil ein 401 im *vorigen* Lauf den Token gelöscht hat (`:470`), nachdem das Panel `hasToken()` schon
geprüft hatte.

**Entscheidung des Betreibers (2026-09-05): der Arbiter führt keinen Lock, er leitet ab.**
`activeRun` ist ein `computed` über die `isRunning`-Signale der Läufe. Damit gibt es nichts zu
halten und nichts freizugeben; ein verklemmter Zustand ist nicht „unwahrscheinlich", sondern
nicht darstellbar. Die Belege, die diese Form tragen (im Code geprüft, nicht angenommen):

- `SevenTvRunEngine.start` setzt `isRunning` **synchron** (`:201`) und **erst nachdem** alle drei
  Ablehnungsgründe passiert sind (laufende Engine, leere Liste, fehlender Token; `:189-196`).
  Ein abgelehnter Start hinterlässt also gar keine Spur — genau der Fall, der die Handbuchführung
  verklemmt hätte.
- `finish()` (`:490-491`) ist der **einzige** Ausgang und setzt `isRunning` synchron zurück; er wird
  vom normalen Ende, von `cancel()` und vom RxJS-Fehlerpfad (`:224`) erreicht.
- `SevenTvDeleteService.isRunning` und `SevenTvRestoreService.isRunning` sind keine eigenen Signale,
  sondern die durchgereichten Engine-Signale (`seven-tv-delete.service.ts:71`,
  `seven-tv-restore.service.ts:71`). Der Arbiter liest damit die Wahrheit der Engine, keine Kopie.

**Folge für die übrigen Pfade dieses Risikos:** die Punkte „`onRunComplete` wirft vor `release`",
„Fehlerpfad gibt nicht frei", „werfender Hook lässt den Lock stehen", „Freigabe nach `cancel()`"
entfallen ersatzlos — sie waren alle Ausprägungen derselben Handbuchführung. Zwei Beobachtungen
aus der Untersuchung bleiben trotzdem gültig und stehen weiter im Plan:

- Der **RxJS-Fehlerpfad** (`error: () => this.finish()`, `:224`) lässt Restzeilen `pending`/
  `in-progress` bei `isRunning === false` zurück — eine **bestehende** Schwäche. Der
  `abortOn`-Abbruch darf deshalb nicht über diesen Pfad laufen (s. R2); die Schwäche selbst bleibt
  außerhalb von #70.
- Ein **werfender `abortOn`-Hook** würde in genau diesen Fehlerpfad laufen. Die Engine ruft den Hook
  deshalb abgesichert auf; eine Ausnahme gilt als `false` (weiterlaufen) und wird per `console.error`
  gemeldet. Ein Testfall (Task 2).

**Invariante, die die Tests belegen:** nach jedem terminalen Pfad — normales Ende, `cancel()`,
`abortOn`-Abbruch, abgelehnter Start — ist `activeRun()` `null`. Sie wird jetzt von der Struktur
getragen, die Tests pinnen sie trotzdem, weil sie der Vertrag für K3 ist.

**Doppelklick / zwei Start-Stellen im selben Tick.** Weiterhin sicher: `isRunning` wird synchron in
`start()` gesetzt, ein zweiter Handler im selben Tick liest bereits `true`. Signale werden in
Angular ohne Verzögerung gelesen, es gibt kein Batching-Fenster.

### R2 — `abortOn`: Platz, Rohtext, Status, Abbruchpfad

- **Was der Hook sieht.** `RunOneResult` (`:81`) trägt im Fehlerfall heute nur `errorMessage`.
  Der HTTP-Status wird in `describeHttpError` (`:466`) verbraucht. **Entscheidung:** Die
  Fehlervariante von `RunOneResult` bekommt `httpStatus: number | null`, gesetzt in `runOne`
  (`:331-334`) aus `HttpErrorResponse.status`, `null` im GQL-Zweig (`:314-317`) und im
  Aufgeben-Zweig (`:274-280`). `describeHttpError` bleibt, wo es ist — **der Token wird also vor dem
  Hook gelöscht**, unabhängig davon, was der Hook zurückgibt (Issue und Design sagen dasselbe).
- **Vertrag des Arguments.** `message`: bei GQL-Fehlern der rohe 7TV-Text (`gqlError.message`,
  leer statt undefined), bei HTTP-Fehlern der bisher gebaute übersetzte Text, beim
  Rate-Limit-Aufgeben der übersetzte `rateLimitedGaveUp`-Text. `httpStatus`: der HTTP-Status bei
  Transportfehlern — **einschließlich Angulars `0` für Netzwerkfehler** —, `null` sonst. Das gehört
  in die JSDoc des Hooks, weil K2 darauf matcht („insufficient privileges" ist GQL, `null`).
- **Wann er gerufen wird.** Genau einmal je Zeile, die den Status `failed` erhält — **nach** dem
  Setzen des Status, **vor** dem `delayWhen` (`:218`). Nie für erfolgreiche Zeilen, nie für
  Rate-Limit-Wiederholungen (die tauchen erst nach dem Aufgeben als `failed` auf), nie für
  `cancelled`-Zeilen.
- **Wie abgebrochen wird.** Bei `true` endet der Lauf **synchron** (keine 275 ms Wartezeit):
  Restzeilen (`pending`/`in-progress`) → `cancelled`, Countdown aus, `finish()` genau einmal,
  `onComplete` genau einmal, `isRunning()` danach `false`. Die `complete:`-Zuweisung der Kette
  (`:223`) darf **nicht** zusätzlich feuern — das ist die Falle beim Abbruch auf der **letzten**
  Zeile. Ob der Implementer dafür `cancel()` aus der Kette heraus wiederverwendet oder ein
  Kontrollfluss-Signal wirft und einen eigenen Abbruchpfad baut, ist frei; die Tests pinnen das
  Ergebnis, nicht den Mechanismus. Die schon `failed` gesetzte Zeile bleibt `failed`.
- **`abortOn` nicht gesetzt** = Bestandsverhalten. Die acht bestehenden Engine-Fälle laufen
  unverändert (einzige erlaubte Änderung: die Fixture bekommt `key`, s. R10).

### R3 — Typwelle von `emoteId?: string`

Optional gemachtes `emoteId` bricht: `run-progress-panel.ts:179` (Track-Ausdruck),
`purge-run-export.ts:55` (Pflichtfeld), `mass-delete-panel.ts:211-216` (`output<string[]>`),
`:297/:314` (`doneItems`-Typ), und alle drei Aufrufer, die `RunQueueEmote`-Literale ohne `key`
bauen. **Entscheidungen:**

- **Eingabetyp der Services ≠ Engine-Typ.** `DeleteQueueEmote` (heute Alias) wird ein
  eigenständiges Interface `{ emoteId: string; sevenTvEmoteId: string; name: string }`.
  `startDelete` **und** `startRestore` nehmen diesen Typ (der Restore-Service importiert ihn wie
  schon `SyncReportState` aus dem Delete-Service) und mappen ihn auf `RunQueueEmote` mit
  `key = emoteId`. Die drei Aufrufstellen in den Panels bleiben **unverändert** — ihre Literale
  passen auf den Eingabetyp. Die Service-Spec-Fixtures (`EMOTES: DeleteQueueEmote[]`,
  `RunQueueEmote[]` im Restore-Spec — dort auf den neuen Typ umstellen) bleiben inhaltlich gleich.
- **`run-progress-panel.ts`:** Track über `item.key`. `key` ist pro Lauf eindeutig, `emoteId`
  wäre beim Import für alle Zeilen `undefined` (Angular meldet NG0955 und recycelt falsch).
- **`purge-run-export.ts`:** `buildPurgeRunProtocol` verlangt an seiner `items`-Eingabe Zeilen
  **mit** `emoteId` (Typverengung an der Signatur; kein `?? ''`, das schriebe stille Löcher ins
  Protokoll). Dateiformat, `PurgeRunRow`, Parser: unverändert. Das ist die einzige Abweichung von
  Issue-Punkt 4 und nur eine Typ-, keine Verhaltensänderung.
- **`mass-delete-panel.ts`:** `deleted.emit` liefert weiterhin nur Strings — entweder aus
  `deleteService.lastRun()?.result.doneIds` (dann stimmt die Issue-Formulierung nachträglich) oder
  durch Filtern der Queue auf vorhandene `emoteId`. Für `doneItems` (Restore aus dem Post-Run-Button)
  und für `openProtocolExport` werden `run.result.items` auf Zeilen mit `emoteId` verengt — für
  Delete-Läufe ein No-op zur Laufzeit.
- **Der `key` verlässt die Engine nicht:** nicht im Protokoll (`PurgeRunRow` unverändert), nicht in
  den Backend-Calls.

### R4 — Doppelte Keys in einer Queue

`setStatus` aktualisiert **alle** Treffer, genau wie heute bei `emoteId`. Eindeutigkeit des Keys
ist eine Vorbedingung des Aufrufers: Delete (Selektion ist per `emoteId` geschlüsselt) und Restore
aus dem Protokoll (Zeilen stammen aus einer eindeutig geschlüsselten Queue; eine handeditierte
Datei kann heute genauso Duplikate tragen) ändern sich nicht. Die Engine dedupliziert **nicht**
(Import-Dedupe ist Sache des Parsers in K2). AC 1 pinnt, dass der Vergleich Gleichheit ist und
kein Präfix-Match (Keys `abc`/`abcd`).

### R5 — Voting-Seite

`app-mass-delete-panel` ist dort montiert (`vote-session-detail-page.html:150`). Die
Arbiter-Prüfung im Panel wirkt automatisch: ein auf der Usage-Seite laufender Restore deaktiviert
den Delete-Button auch auf der Voting-Seite — **gewollt**, das ist die eine Verhaltensänderung. Der
Restore-Fortschritt erscheint dort heute schon (`:111`). Keine Änderung an Voting-Dateien.

### R6 — DI-Zirkularität

Keine. Der Arbiter hat **null** Abhängigkeiten (reiner Signal-Halter). Kanten: Service → Arbiter,
Panel → Arbiter + Service. **Entscheidung:** Die Panels injizieren den Arbiter **direkt** statt
über eine Durchreiche-API der Services — sonst müsste jeder Service `activeRun` re-exportieren.

### R7 — Decorator des Arbiters

`@Service` existiert in der installierten Angular-Version (22.1.5, `types/core.d.ts:1322`), wird
im Repo aber noch nirgends benutzt; `web/.claude/CLAUDE.md` schreibt ihn für neue Singletons vor.
**Entscheidung:** `@Service()` — es wäre die erste Verwendung. Stolpert Lint oder Vitest darüber,
ist `@Injectable({ providedIn: 'root' })` der Rückfall (18 bestehende Verwendungen), mit Hinweis im
Ergebnisbericht. Das Issue meint mit „`providedIn: 'root'`" den Scope, nicht den Decorator.

### R8 — Umfang der Verhaltensänderung

Genau eine: Start-Stellen prüfen `activeRun() !== null`. **Nicht** umgestellt: der Button
„Auswahl aufheben" (`mass-delete-panel.ts:66`, verbirgt sich während des *eigenen* Delete-Laufs —
während eines Restores ist Aufheben harmlos), `dockVisible()`, `resetIfChannelChanged`, die
Sichtbarkeitsbedingungen der beiden Fortschrittssektionen (`:78`, `:111`).

### R9 — `progress` und abgebrochene Läufe

`progress` zählt `done + failed` (`:167-173`); nach `abortOn` bleiben `cancelled`-Zeilen, also
`finished < total` — identisch zum heutigen `cancel()`. Keine Änderung.

### R10 — Snapshot der Engine-Spec (AC 4)

Die acht bestehenden Fälle in `seven-tv-run-engine.spec.ts` bleiben **wortgleich**, bis auf die
Fixture `EMOTES` (`:41-44`), die `key` bekommt. Eine weitere Änderung an einem Bestandsfall ist ein
Review-Befund, kein Handgriff.

### R11 — Rate-Limit-Aufgeben zählt als `failed`

Der Hook wird dafür gerufen (übersetzter Text, `httpStatus: null`). Ein Import-Hook (K2) darf
darauf nicht anspringen; die JSDoc des Hooks nennt den Fall ausdrücklich.

### R12 — Doku-Pflichten

Regel 3: der Commit, der einen Vertrag ändert, enthält seinen DECISIONS-Eintrag. Da die Tasks
einzeln committet werden, wächst **ein** Eintrag „Run-Engine: Zeilen-Key, Abbruch-Hook und
Run-Arbiter (#70)" über drei Tasks (1, 2, 4) um je einen Absatz. Die Verhaltensänderung
(wechselseitiger Ausschluss) ist eine Interaktionsänderung → ein Satz in
`docs/UI-Designsprache.md` (Codex-P3-Lehre aus #40), in Task 4.

---

## 3. Tasks

Abhängigkeiten: **Task 3 läuft parallel zu Task 1 und 2** (nur neue Dateien). Task 2 nach Task 1
(dieselben zwei Dateien). Task 4 nach Task 1 **und** 3. Task 5 nach Task 4.

### Task 1 — Zeilen-Key in Engine, Services und Konsumenten (atomar)

**Ziel.** Queue-Identität über `key`; `emoteId` optional; `RunResult.doneKeys`; die Typwelle aus R3
in **einem** Schritt, damit der Build zwischen den Tasks nie rot ist.

**Betroffene Dateien.**
`web/src/app/core/seven-tv/seven-tv-run-engine.ts` (+ `.spec.ts`),
`seven-tv-delete.service.ts` (+ `.spec.ts`), `seven-tv-restore.service.ts` (+ `.spec.ts`),
`web/src/app/shared/seven-tv/run-progress-panel.ts`, `mass-delete-panel.ts`,
`web/src/app/shared/export/purge-run-export.ts` (nur Eingabetyp), `docs/DECISIONS.md`.
Nicht anfassen: `restore-panel.ts` (Literale passen auf den neuen Eingabetyp), `purge-run-export.spec.ts`,
alles unter `features/`.

**Vertrag.**
- `RunQueueEmote`: `key: string` (Pflicht, Identität innerhalb eines Laufs, Eindeutigkeit ist
  Aufrufersache), `emoteId?: string` (interner Guid; vorhanden bei Delete/Restore, fehlt beim
  Import), `sevenTvEmoteId`, `name` unverändert. Kommentar am Feld sagt, wer den Key setzt.
- `RunResult`: neu `doneKeys: string[]` (Keys der `done`-Zeilen in Queue-Reihenfolge);
  `doneIds: string[]` bleibt die Guid-Liste (aus `emoteId` der `done`-Zeilen, Zeilen ohne `emoteId`
  tragen nichts bei — keine `undefined`-Einträge).
- `setStatus` und beide Aufrufstellen in `start` laufen über `key`.
- `DeleteQueueEmote` wird eigenständiges Interface (s. R3) und ist der Eingabetyp von
  `startDelete(setId, channelName, emotes: DeleteQueueEmote[])` **und**
  `startRestore(setId, channelName, emotes: DeleteQueueEmote[])`; beide Services mappen auf
  `RunQueueEmote` mit `key = emoteId`. `DeleteItemStatus`/`DeleteQueueItem` bleiben Aliase.
- `run-progress-panel.ts`: Track über `key`.
- `buildPurgeRunProtocol`: `items` verlangt Zeilen mit vorhandener `emoteId` (Typebene).
- `mass-delete-panel.ts`: `deleted.emit` emittiert nur Strings; `doneItems` und der
  Protokoll-Export arbeiten auf Zeilen mit `emoteId` (R3).

**Grenzfälle.** Zwei Zeilen mit Keys, von denen einer Präfix des anderen ist (`abc`/`abcd`) —
`setStatus` trifft genau eine. Zeile ohne `emoteId` — läuft durch, erscheint in `doneKeys`, nicht in
`doneIds`. Gemischte Queue (mit/ohne `emoteId`) — `doneIds` enthält nur die vorhandenen Guids.

**Tests (vor der Umsetzung schreiben).**
- `seven-tv-run-engine.spec.ts`, neue Fälle: (a) Zeile ohne `emoteId` läuft durch, `doneKeys`
  enthält ihren Key, `doneIds` ist leer; (b) Präfix-Keys — nach dem ersten Flush ist genau die erste
  Zeile `done`, die zweite `pending`; (c) gemischte Queue — `doneIds` nur die vorhandenen Guids,
  `doneKeys` alle. Bestehender Fall „completes with the done ids" zusätzlich um `doneKeys` ergänzen.
  Fixture `EMOTES` bekommt `key`; sonst kein Bestandsfall angefasst (R10).
- `seven-tv-delete.service.spec.ts` und `seven-tv-restore.service.spec.ts`, je ein Fall: nach dem
  Start trägt jede Queue-Zeile `key === emoteId`; der Body des Bookkeeping-Calls bleibt
  `{ emoteIds: [...] }` (die bestehenden Fälle belegen das schon — nur den Key-Fall ergänzen).

**Doku.** Neuer DECISIONS-Eintrag (Datum des Commits, Titel s. R12, `**Betrifft:**`-Zeile mit allen
Dateien dieses Tasks) mit dem Absatz zum Zeilen-Key: warum `key` statt `emoteId`, warum `doneIds`
bleibt, warum der Eingabetyp der Services vom Engine-Typ abweicht, warum `buildPurgeRunProtocol`
seine Eingabe verengt statt `purge-run-export.ts` unangetastet zu lassen.

**Definition of Done.** Vitest grün (bestehende 603 + 5), Lint, Format-Check; `ng build` fehlerfrei;
Diff an `restore-panel.ts` und `features/**` ist leer; kein `undefined` kann in `deleted.emit`
oder in einen Bookkeeping-Body gelangen (Review-Punkt).

### Task 2 — `abortOn`-Hook der Engine

**Ziel.** Optionaler Hook auf `RunOperation`, der je fehlgeschlagener Zeile entscheidet, ob der Lauf
abgebrochen wird. Delete und Restore setzen ihn nicht.

**Betroffene Dateien.** `seven-tv-run-engine.ts` (+ `.spec.ts`), `docs/DECISIONS.md`.

**Vertrag.**
- `RunOperation.abortOn?(failure: { message: string; httpStatus: number | null }): boolean`.
- Interne Fehlervariante von `RunOneResult` trägt zusätzlich `httpStatus: number | null` (R2).
- Argumentsemantik, Aufrufzeitpunkt und Abbruchverhalten exakt wie in R2; alles davon steht als
  JSDoc am Hook, einschließlich der Fälle „Netzwerkfehler = `0`" und „Rate-Limit-Aufgeben ruft den
  Hook mit übersetztem Text und `null`" (R11).
- Ein werfender Hook gilt als `false` und wird per `console.error` gemeldet (R1.4).
- `describeHttpError` und die Token-Löschung bleiben unverändert an ihrer Stelle.

**Grenzfälle.** Abbruch auf der **letzten** Zeile (kein doppeltes `finish`). Abbruch während die
nächste Zeile noch `pending` ist (kein weiterer Request nach dem Abbruch, auch nicht nach
Timer-Vorlauf). Hook liefert `false` → Lauf identisch zum Bestand. Hook nicht gesetzt → Bestand.
401/403: Token ist bereits gelöscht, wenn der Hook läuft, Rückgabewert ändert daran nichts.

**Tests (vor der Umsetzung).** In `seven-tv-run-engine.spec.ts`:
1. Drei Zeilen, zweite scheitert mit GQL-Fehler, Hook → `true`: Status `['done','failed','cancelled']`,
   `isRunning()` **ohne** Timer-Vorlauf `false`, genau ein `RunResult`, `doneKeys` nur die erste,
   `rateLimitPauseSeconds()` null, nach Timer-Vorlauf kein weiterer Request.
2. Hook → `false`: Ergebnis identisch zu einem Lauf ohne Hook (Status, Requests, `RunResult`).
3. Hook-Argumente: GQL-Fehler → roher Text und `httpStatus: null`; HTTP 403 → übersetzter
   `tokenInvalid`-Text, `httpStatus: 403`, Token danach gelöscht — jeweils per Spy geprüft.
4. Hook wird nicht für erfolgreiche Zeilen gerufen und nicht während einer Rate-Limit-Wiederholung;
   nach dem Aufgeben genau einmal mit `null`.
5. Abbruch auf der letzten Zeile → `onComplete` genau einmal.
6. Werfender Hook → Lauf läuft weiter, `console.error` einmal.

**Doku.** DECISIONS-Absatz „Abbruch-Hook": warum ein Hook statt einer festen Statusliste in der
Engine, warum der Token vor dem Hook gelöscht bleibt, warum das Rate-Limit-Aufgeben den Hook sieht.

**Definition of Done.** Vitest grün (+6), die acht Bestandsfälle unverändert grün, Lint, Format.

### Task 3 — `SevenTvRunArbiter` (parallel zu Task 1/2)

**Ziel.** Root-Singleton, der genau eine Frage beantwortet: läuft gerade ein 7TV-Lauf, und welcher?
Der Zustand wird **abgeleitet, nicht geführt** (R1).

**Betroffene Dateien.** Neu: `web/src/app/core/seven-tv/seven-tv-run-arbiter.ts`,
`seven-tv-run-arbiter.spec.ts`. Sonst nichts — insbesondere **keine** Änderung an den beiden
Services (das ist der Unterschied zur ursprünglichen Issue-Fassung).

**Vertrag.**
- `export type SevenTvRunKind = 'delete' | 'restore' | 'import'`.
- `readonly activeRun: Signal<SevenTvRunKind | null>` — ein `computed`, das die Läufe in fester
  Reihenfolge prüft (`delete`, `restore`, später `import`) und die erste laufende Sorte nennt,
  sonst `null`. Es gibt **kein** `tryAcquire` und **kein** `release`.
- **Richtung der Abhängigkeit:** Der Arbiter injiziert `SevenTvDeleteService` und
  `SevenTvRestoreService` und liest deren `isRunning`. Die Services kennen den Arbiter **nicht**.
  Damit ist die DI-Kante einseitig und R6 (Zirkularität) entfällt: Panels → Arbiter → Services.
  `SevenTvImportService` kommt in K3 als dritter Zweig dazu; das ist eine additive Zeile.
- Decorator: `@Service()` (R7).
- Klassenkommentar: warum der Arbiter außerhalb der Engine liegt (zwei — später drei — Engine-
  Instanzen, ein gemeinsamer Ausschluss), **und warum er ableitet statt zu sperren**: `start()`
  setzt `isRunning` synchron erst nach allen Ablehnungsgründen, `finish()` ist der einzige Ausgang,
  also ist die Engine bereits die verlässliche Quelle — ein zweiter, handgeführter Zustand könnte
  von ihr nur abweichen. Verweis auf den DECISIONS-Eintrag.

**Grenzfall, der in den Test gehört.** Die Reihenfolge im `computed` ist eine Anzeigeentscheidung,
keine Ausschlussentscheidung: es kann konstruktionsbedingt nie mehr als ein Lauf aktiv sein, weil
die Panels vor dem Start `activeRun() !== null` prüfen. Der Test pinnt trotzdem, was bei zwei
gleichzeitig laufenden Engines herauskäme (die erste in der Reihenfolge), damit ein späterer
Leser die Reihenfolge nicht für Zufall hält.

**Tests (vor der Umsetzung), `seven-tv-run-arbiter.spec.ts`.** Die Service-`isRunning`-Signale
werden über die Engines der echten Services gestellt oder die Services per `TestBed` mit
Stub-Signalen überschrieben — welcher Weg, entscheidet der Umsetzer nach dem Muster der
bestehenden Service-Specs. Fälle: (1) kein Lauf → `null`; (2) Delete läuft → `'delete'`, danach
wieder `null`; (3) Restore läuft → `'restore'`; (4) beide laufen (konstruiert) → `'delete'`
(Reihenfolge gepinnt).

**Definition of Done.** Vitest grün (+3 oder mehr), Lint, Format; Datei ohne Import aus `shared/`
oder `features/` (Schichtentreue `core/`). Kein `release`, kein `tryAcquire`, kein `console.warn`
im Ergebnis.

### Task 4 — Arbiter verdrahten: Services, Panels, Doku

**Ziel.** Beide Services melden ihre Läufe am Arbiter; die vier Start-Stellen fragen den Arbiter.
Danach gilt AC 5.

**Betroffene Dateien.** `web/src/app/shared/seven-tv/mass-delete-panel.ts` (`:58`, `:93`, `:294`),
`restore-panel.ts` (`:31`), `docs/DECISIONS.md`, `docs/UI-Designsprache.md`, sowie
`seven-tv-delete.service.spec.ts` und `seven-tv-restore.service.spec.ts` (nur neue Testfälle).
**Die beiden Service-Dateien selbst werden nicht angefasst** — der Arbiter liest sie, sie kennen
ihn nicht (Task 3).

**Vertrag Services.** Keiner — die Services bleiben unverändert. Ihr `isRunning` ist bereits das
durchgereichte Engine-Signal (`:71` in beiden), und mehr braucht der Arbiter nicht. Damit entfällt
die gesamte Freigabe-Disziplin, die die ursprüngliche Issue-Fassung verlangt hätte.

**Vertrag Panels.** `mass-delete-panel.ts:58` disabled zusätzlich bei `arbiter.activeRun() !== null`;
`:93` zeigt den Restore-Button nur bei `activeRun() === null`; `:294` bricht bei
`activeRun() !== null` ab; `restore-panel.ts:31` disabled bei `activeRun() !== null`. Der Arbiter
wird in beiden Panels direkt injiziert (`protected`, weil das Template ihn liest; Member-Reihenfolge
nach `web/.claude/CLAUDE.md`). `:66` und die Sichtbarkeit der Fortschrittssektionen bleiben (R8).
Kein neuer i18n-Key, kein Hinweistext (Offene Frage 2).

**Grenzfälle.** Start abgelehnt, weil die andere Sorte läuft → der Button ist erst gar nicht
betätigbar; wird `startDelete` im Test trotzdem gerufen, lehnt die Engine wie heute ab (`:189`),
keine GQL-Anfrage, eigener State unberührt (`syncReport`, `lastRun`). Start von der Engine abgelehnt
(Token fehlt) → `activeRun()` ist `null`, weil `isRunning` nie gesetzt wurde. Nach `cancel()` →
`null`. Nach normalem Ende → `null`. Voting-Seite: keine Dateiänderung, Verhalten folgt dem
Panel (R5).

**Tests (vor der Umsetzung).** In beiden Service-Specs (Arbiter per `TestBed.inject`):
(1) während des Laufs `activeRun() === '<kind>'`, nach dem Ende `null`; (2) nach `cancel()` `null`;
(3) Token gelöscht → Start abgelehnt und `activeRun()` `null` (die Invariante, die die
Handbuchführung gebrochen hätte). Zusätzlich ein Kreuzfall in einer der beiden Specs: echter
Restore-Lauf aktiv → `activeRun()` ist `'restore'`, nach dessen Ende `null` und ein Delete-Start
gelingt. Kein Komponententest für die Panels (Konvention Regel 12).

**Doku.** DECISIONS-Absatz „Run-Arbiter": warum außerhalb der Engine, und warum er **ableitet
statt zu sperren** — die Handbuchführung aus der Issue-Fassung hätte einen app-weit verklemmten
Zustand ermöglichen können (abgelehnter `engine.start` nach gewonnenem `tryAcquire`), während
`isRunning` synchron erst nach allen Ablehnungsgründen gesetzt und in `finish()` als einzigem
Ausgang zurückgesetzt wird. Die eine Verhaltensänderung (wechselseitiger Ausschluss) ausdrücklich
benannt. `docs/UI-Designsprache.md`: ein Satz bei den
Mass-Delete-Stufen (Umfeld Zeile 177–182) — während eines 7TV-Laufs beliebiger Sorte sind alle
7TV-Start-Buttons deaktiviert, ohne Hinweistext, weil der laufende Fortschritt selbst der Hinweis ist.

**Definition of Done.** Vitest grün, Lint, Format, `ng build`; E2E-Suite grün (UI-Änderung, nur ohne
Api auf `:5151`); Review-Punkt: `grep -rn "tryAcquire\|release(" web/src/app/core/seven-tv` liefert
nichts — es gibt keinen Lock, der vergessen werden könnte.

### Task 5 — Live-Verifikation (Regel 16, AC 6) — durch den Betreiber

Nicht delegierbar: braucht das 7TV-Schreib-Token im Browser. Vorbereitung durch den Orchestrator:
Api lokal (`dotnet run --project src/EmotePurge.Api`), `npm --prefix web start`, Testkanal mit
aktivem Set.

Checkliste: (1) Delete-Lauf mit ≥ 3 Emotes — Fortschritt, Zusammenfassung, Protokoll-Download,
`sync-deleted` erfolgreich, Konsole `[EmotePurge] 7TV mass delete finished` mit plausiblen Zahlen;
(2) Restore aus dem Post-Run-Button — während des Laufs ist der Delete-Button **deaktiviert**
(die Verhaltensänderung), `sync-restored` und Resync-Hinweis wie vorher; (3) Restore aus dem
Protokoll-Import — während des Laufs ist der Delete-Button deaktiviert, der Import-Button ebenfalls;
(4) nach jedem Lauf sind alle Start-Buttons wieder aktiv (Arbiter frei); (5) Voting-Seite parallel
offen: ihr Delete-Button folgt demselben Zustand; (6) Vergleich der Protokolldatei eines Delete-Laufs
mit einer vor der Änderung erzeugten — gleiche Felder, kein `key`.

---

## 4. Gates

Am Ende der Arbeit grün, in dieser Reihenfolge:

1. `npm --prefix web test -- --watch=false` — Baseline 603 Fälle in 62 Dateien; erwartet ≥ 603 + 20
   (Task 1: 5, Task 2: 6, Task 3: 3, Task 4: ≥ 9). Die acht Bestandsfälle der Engine-Spec unverändert.
2. `npm --prefix web run lint` und `npm --prefix web run format:check`.
3. `npm --prefix web run e2e` — 103 Fälle, **nur ohne Api auf `:5151`**; rote Fälle bei Laufzeit
   > 2 min zuerst als Speicherdruck lesen und die Suite allein wiederholen.
4. `dotnet test EmotePurge.slnx` — kein Backend-File berührt; läuft in der CI ohnehin, lokal nur
   als Gegenprobe, dass die Solution unberührt ist (`git diff --stat -- src tests` leer).
5. Live-Verifikation (Task 5) vor dem Commit der Panel-Änderung.
6. Codex-Zweitmeinung vor dem Merge (`/codex:review --model gpt-5.6-sol --scope branch --base origin/main`),
   Fokus: die drei Freigabepunkte des Arbiters und der Abbruchpfad auf der letzten Zeile.

Commits nach Task, Conventional Commits, DECISIONS-Absatz jeweils im Commit des Tasks (Regel 3):
`feat(web): key 7TV run rows by an explicit queue key` · `feat(web): let a run operation abort the
queue on a failed row` · `feat(web): add the 7TV run arbiter` · `feat(web): gate all 7TV run starts
through the arbiter`. Vor jedem Commit den Nutzer fragen (Regel 1).

---

## 5. Entscheidungen des Betreibers (2026-09-05)

Beide vormals offenen Fragen sind entschieden; der Plan oben ist bereits danach geschrieben.

1. **Arbiter: abgeleitetes Signal statt Handbuchführung.** `activeRun` ist ein `computed` über die
   `isRunning`-Signale; `tryAcquire`/`release` entfallen. Begründung im Code belegt, s. R1 und
   Task 3. **Folge für K3 (#72):** Punkt 7 des Issues („Meldet Start/Ende an den Arbiter") wird
   gegenstandslos — der Import-Service meldet nichts, der Arbiter bekommt in K3 einen dritten
   Zweig auf `SevenTvImportService.isRunning`. Die Akzeptanzkriterien von #70 (AC 5) und #72
   ändern sich nicht. Das gehört in den DECISIONS-Eintrag und als Kommentar an #70/#72.
2. **Kein Hinweistext bei gesperrtem Start.** Die Buttons bleiben wie heute nur `disabled`, ohne
   Text (AC 5, AC 7 — „kein neuer i18n-Key"). Der im Issue erwähnte Bestand-Hinweis „ein Lauf ist
   aktiv" existiert nicht und wird nicht erfunden: der laufende Fortschritt steht sichtbar im
   selben Dock und ist selbst der Hinweis. Ein Satz dazu in `docs/UI-Designsprache.md` (Task 4).
