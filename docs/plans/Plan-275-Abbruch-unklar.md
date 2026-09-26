# Plan #275 — Abbruch mit Request in der Luft: Delete und Restore lesen nach, statt „abgebrochen" zu sagen

Erstellt am 2026-09-26 gegen `fix/275-cancel-unknown` = `408f07d3` (= `origin/feat/emote-sets-200`).
Quellen: Issue #275, die Analyse mit den Nutzerentscheidungen D1–D9 (Scratchpad `analyse-275.md`,
2026-09-26 — **verbindlich, hier nicht neu verhandelt**), Plan-256 Abschnitt 8 Nr. 3 und 8.3 (die
Herkunft des Issues), Plan-254 (Undo als zweites Settling-Vorbild), `CLAUDE.md` (Regeln 3, 7, 11,
12, 13, 14, 16, 18, 22), `web/.claude/CLAUDE.md`, `docs/DECISIONS.md` (Einträge vom 2026-09-26
„run-bound", 2026-09-23 #230, 2026-09-22 K5) und der Code auf `408f07d3` — jede in Abschnitt 5
genannte Datei ist gelesen, nicht vermutet.

**Kein Code im Plan.** Namen stehen nur, wo sie ein Vertrag zwischen zwei Tasks sind. Findet ein
Task eine Abweichung zwischen diesem Plan und dem Code, gilt der Plan; der Fund kommt in den
Task-Bericht, nicht still in den Diff. **Leitplanke wie in Plan-256: fail-closed.** Eine Mutation,
die bei 7TV angekommen ist, bleibt nie ungemeldet; eine Zeile, deren Ausgang niemand kennt, heißt
`unknown` und nie „abgebrochen".

---

## 1. Ziel und Nicht-Ziele

**Ziel.** Ein Delete- oder Restore-Lauf, dessen Request bei 7TV in der Luft war, als der Nutzer
„Abbrechen" klickte — oder dessen Antwort im Transport verloren ging (Status 0, 5xx, kein
GraphQL-Body) —, markiert diese Zeile `unknown`, liest das Set nach dem Lauf **einmal** live nach,
klärt die Zeile daran (`done` / „nicht passiert" / bleibt `unknown`), meldet erst danach, und sagt
im Dock und im Protokoll ehrlich, was unklar geblieben ist. Das Protokoll trägt `unknown` durch,
und ein Restore aus dem Protokoll bietet solche Zeilen mit an.

**Nicht-Ziele.**

- **Import und Undo bleiben unverändert** — die Wartezeit nach einem Abbruch (D5) bekommen sie in
  #284 (Epic #200), nicht hier. Ihre eigenen Read-Timeout-Konstanten bleiben stehen (Festlegung 4).
- **Kein Backend-Diff.** `sync-deleted`, `sync-restored`, `resync` und ihre Antworten bleiben, wie
  sie sind; es ändert sich nur, *wann* und mit *welchen* Ids gemeldet wird.
- **Arbiter und Lifecycle bleiben generisch** — `settling` existiert dort schon; kein neuer
  Zustand, keine neue Kante.
- **Keine Änderung an der Engine-Semantik von `transportLossIsUnknown`** (welche HTTP-Ausgänge
  `unknown` sind, steht in `seven-tv-run-engine.ts:166-179` und bleibt).
- **Keine Wortlaut-Runde.** Alle neuen Schlüssel sind vorläufig (D3) und keine Prüfgegenstände
  (Regel 12).

---

## 2. Festlegungen

Nummeriert; jede mit Task. Wo sie eine Nutzerentscheidung nur umsetzt, steht die D-Nummer.

| # | Festlegung | Grund / Beleg | Task |
|---|---|---|---|
| 1 | **Beide Operationen setzen `transportLossIsUnknown: true`** — `REMOVE_OPERATION` (Delete) und `addOperation` (Restore). Damit werden Status 0, 5xx, Antwort ohne GraphQL-Body **und** ein `cancel()` mit Request in der Luft `unknown`; 4xx und GraphQL-Fehler bleiben `failed`, ein Abbruch zwischen zwei Zeilen oder in einer Rate-Limit-Pause bleibt `cancelled` (`inFlight` ist dann `null`, Engine `:617-620`, `:874-896`). | D1, D7 | T2, T3 |
| 2 | **Neue Phase `settling` für Delete und Restore** — hebt Plan-256 Festlegung 5 auf. `onRunComplete` setzt `phase: 'settling'` genau dann, wenn das Ergebnis mindestens eine `unknown`-Zeile hat, sonst wie heute direkt `reporting`/`closed`. `SevenTvRunLifecycle` und `SevenTvRunArbiter` werden **nicht** angefasst: `isSettling` (`settling \| reporting`) und `activeClaim.phase === 'settling'` greifen automatisch; das Panel zeigt `<prefix>.settling` schon (`run-progress-panel.ts:43-49`, Schlüssel de/en vorhanden). | D1; Lifecycle `:50-54`, Arbiter `:99-113` | T2, T3 |
| 3 | **Ursache einer `unknown`-Zeile explizit am Engine-Zeilentyp:** `RunQueueItem` bekommt ein optionales Feld `unknownCause: 'cancelled' \| 'transportLoss'`, gesetzt nur zusammen mit `status: 'unknown'` — `'transportLoss'` in `runRowFrom` für das `unknown`-Ergebnis aus `runOne`, `'cancelled'` in `cancelRemainingRows` für die Zeile in der Luft. Nirgends aus leerem `errorMessage` abgeleitet. Engine statt dienstlokal, weil nur die Engine weiß, *welche* Zeile in der Luft war, und ein Lauf beide Ursachen zugleich enthalten kann (früher 5xx, später Abbruch). Import und Undo ignorieren das Feld; es wird in kein Dateiformat geschrieben. | D4 | T1 |
| 4 | **Zwei Konstanten, gemeinsam für Delete und Restore, in einem neuen puren Modul** `web/src/app/core/seven-tv/seven-tv-run-settlement.ts`: `SET_ENTRIES_READ_TIMEOUT_MS = 20_000` (Read-Budget) und `CANCEL_SETTLE_GRACE_MS = 3_000` (Festlegung 5). **Import und Undo nutzen sie in diesem PR nicht** — `SETTLE_READ_TIMEOUT_MS` (Import, modulprivat) und `UNDO_SETTLE_READ_TIMEOUT_MS`/`RECHECK_READ_TIMEOUT_MS` (Undo, exportiert, in Specs referenziert) bleiben, minimaler Diff; die Vereinheitlichung gehört zu #284. Der Doc-Kommentar der neuen Konstante nennt die drei bestehenden als wertgleich. | D8 | T1 |
| 5 | **Wartezeit vor dem Nachlese-Read nur nach einem Nutzer-Abbruch:** `CANCEL_SETTLE_GRACE_MS = 3_000`, einmal je Lauf, genau dann, wenn mindestens eine Zeile `unknownCause === 'cancelled'` trägt. **Nicht** nach reinem Transportverlust — geprüft und so festgelegt: ein 5xx ist eine Antwort, die erst kommt, wenn 7TV mit dem Request fertig ist; bei Status 0 gibt es keinen Zeitpunkt, auf den ein Warten zielen könnte. Beim Abbruch dagegen läuft `onRunComplete` **synchron aus `cancel()` heraus** (`finish()` `:426`), also bevor der Browser die Verbindung überhaupt abgebaut hat — 7TV verarbeitet den Request derweil normal zu Ende, ein sofortiger Read sähe den alten Stand und der Plan sagte fälschlich „nicht passiert". 3 s: die Engine misst Rundlaufzeiten je Lauf (`averageRoundTripMs`, im Bereich weniger hundert Millisekunden; T7 protokolliert den Wert des Live-Tests), 3 s ist ein Vielfaches davon und kürzer als jede Rate-Limit-Pause, die der Nutzer heute schon hinnimmt. Die Wartezeit läuft **innerhalb** von `settling` (Dock zeigt „Wird abgeschlossen…", Arbiter busy, Delete-Guard scharf). | D5 | T1 (Konstante), T2, T3 |
| 6 | **Nachlese-Read:** `loadSevenTvSetEntries(httpClient, setId)` (Delete: `run.setId`, Restore: `run.targetSetId`), Pipe `timeout(SET_ENTRIES_READ_TIMEOUT_MS)` + `catchError(() => of(null))`, danach **immer** `settleRun`. Read gescheitert, Timeout **oder `complete: false`** ⇒ die `unknown`-Zeilen bleiben `unknown` (wie Import, `settleRunResult`: ein unvollständiger Read zählt als kein Read). Nur genau **ein** Read je Lauf, kein Retry. | D1; Import `:672-697`, `:1082` | T2, T3 |
| 7 | **Klärtabelle Delete** (Zeile = 7TV-Id, ein `REMOVE` nimmt alle Einträge der Id): siehe Abschnitt 3.1. | D1 | T1 |
| 8 | **Klärtabelle Restore** (Zeile = `${id}#${alias}`, inkl. `alias === null`): siehe Abschnitt 3.2. Der Restore-Dienst hält `aliasByKey` und einen `defaultNameByKey` (aus `RestoreQueueEmote.defaultName`) je Lauf im Closure von `onRunComplete` — die Klärung braucht beides, `RunQueueItem` trägt den Alias nicht. | D1, Spec #254 F5 | T1, T3 |
| 9 | **„Nicht passiert" heißt:** `unknownCause === 'cancelled'` ⇒ `status: 'cancelled'`, `failedStep: null`, `errorMessage` unverändert (leer); `'transportLoss'` ⇒ `status: 'failed'`, `failedStep` bleibt, `errorMessage` = Übersetzung von `massDelete.errors.unknownOutcome` bzw. `restore.errors.unknownOutcome`, mit dem bisherigen Transporttext in Klammern, wenn vorhanden (Muster `import.errors.unknownOutcome`, Import `:1149-1155`). Wortlaut wie `import.errors.unknownOutcome` (de/en). | D4 | T1 |
| 10 | **Veröffentlichung erst nach dem Settle:** `result` am Datensatz bleibt `null`, bis `settleRun` das geklärte Ergebnis schreibt — **keine** Vorab-Veröffentlichung wie beim Import. Folge ohne weiteren Code: `lastRun` (Delete, Projektion `shown.result !== null`) und `watchRunSettle` (Seite `:2872-2875`, dedupliziert auf die `result`-Identität) sehen genau einen Stand, der Protokoll-Download kennt nur das geklärte Ergebnis, kein Doppel-Reload. `settleRun` schreibt `result`, `phase: 'reporting'` und die `syncReport`-Felder **in einem** `lifecycle.update` (Lifecycle-Guard `:140-142` schließt sofort, wenn nichts zu melden ist). | Analyse §2 | T2, T3 |
| 11 | **`queue` wird die Zeilenprojektion** (statt eines Alias auf `engine.queue`): geklärte `run().result.items`, sobald vorhanden, sonst die Engine-Queue (während `running` und `settling`). Als `linkedSignal` (schreibbar), weil `restore-progress-section.spec.ts` `queue.set` an fünf Stellen ruft (Plan-256 Festlegung 14). Kein Konsument ändert sich: `mass-delete-panel.ts:251-253`, `restore-progress-section.ts:65, :89`, `usage-stats-page.ts:1527, :1532` lesen weiter `queue()`. `engine.reset()`/`showFinishedRows` bleiben, wie sie sind (redundant, aber harmlos). | Analyse §2 „items-Projektion" | T2, T3 |
| 12 | **Berichte unverändert, nur später:** `reportDeleted(runId, doneKeys)` / `reportRestored(runId, result)` laufen aus `settleRun` mit den **neu berechneten** `doneKeys`; `retrySyncReport` liest `result.doneKeys` und ist damit automatisch richtig. Verbleibende `unknown`-Zeilen werden **nicht** gemeldet (wie E10 bei #254). | Analyse §4 | T2, T3 |
| 13 | **D6 — nur `unknown`, kein `done`:** Bleiben nach dem Settle `unknown`-Zeilen und `doneKeys` ist leer, stößt der Dienst einen Client-Resync des **aktiven** Sets an (`expectedChannelName !== null`; Delete über das vorhandene `fallbackResync`, Restore über `triggerResync` mit Dock-Zeile) und der Lauf geht direkt `closed`. Für das Delete-Dock zusätzlich: das Panel emittiert `reloadRequested` genau einmal, wenn der gezeigte Lauf `closed` ist, `syncReport === 'idle'` und `lastRun().result.items` eine `unknown`-Zeile enthält (heute endet der Effekt bei `idle` still, `mass-delete-panel.ts:530`). Das Restore-Dock emittiert `reloadRequested` schon am Engine-Ende (`:552-560`) — dort ist nur der Resync neu. Gemischte Läufe (done + unknown): kein Client-Resync, die Meldung stößt den Backend-Resync an, der auch die unklaren Zeilen heilt. | D6 | T2, T3, T4 |
| 14 | **`deleteRunActive`** (Seite `:1502-1504`) erweitert um `run()?.phase === 'settling'`: der Set-Dropdown bleibt mit `emoteSetMenu.lockedDuringDelete` gesperrt, solange nachgelesen wird. | Analyse §2 | T4 |
| 15 | **Protokoll `purge-run` v3:** `PURGE_RUN_FORMAT_VERSION` 2 → 3; `meta.counts` bekommt `unknown` (Summe = `requested`); Zeilen tragen `status: 'unknown'` wie jeden anderen Status (die Zeilenform ändert sich nicht). Parser akzeptiert `formatVersion` 1, 2 **und** 3 und liefert Zeilen mit `status` `done` **oder** `unknown` (aus jeder Version — eine v2-Datei enthält faktisch keine, verhält sich also unverändert). Versionssprung nach der K5-Regel: ein v2-Leser würde die `unknown`-Zeilen still verwerfen und zu wenig wiederherstellen. CSV bekommt keine neue Spalte (`status` steht schon drin). | D2; DECISIONS 2026-09-22 K5, `purge-run-export.ts:14-28` | T5 |
| 16 | **Restore bietet `done` + `unknown` an** — Datei-Weg über den Parser (Festlegung 15) und Panel-Weg (`mass-delete-panel.ts:669-672` filtert `done`; Knopf-Sichtbarkeit `:267` prüft `doneKeys.length > 0`): beide nehmen `unknown` dazu. Sicherung ist der bestehende Live-Filter `filterAlreadyPresentForRestore` (Regeln 2/3: ein noch vorhandener Eintrag wird als „bereits vorhanden" verworfen); schlägt dessen Read fehl, geht der `ADD` raus und endet höchstens als 409-Zeile (7TV weist den kollidierenden Alias ab) — keine zweite Kopie. | D2 | T4, T5 |
| 17 | **Kein Hinweis im Restore-Bestätigungsdialog.** Die Analyse nennt einen; der Plan legt fest: entfällt. Grund: der Open-Time-Check läuft **vor** dem Dialog und entfernt jede noch vorhandene Zeile; was den Dialog erreicht, fehlt im Set — unabhängig davon, ob es im Protokoll `done` oder `unknown` hieß. Ein Marker je Zeile müsste `RestoreRow` und den ganzen Flow verbreitern, um etwas zu sagen, das keine Entscheidung ändert. Der Hinweis lebt stattdessen im Delete-Dock (`massDelete.summary.unknownInProtocol`, D3), wo die Entscheidung für den Restore fällt. **Betreiber-Veto möglich** (Abschnitt 7). | Ableitung aus D2/D3 | T4 |
| 18 | **Dock-Texte** (D3, Regel 7 gilt nicht — keine `ApiErrorCodes`): neue Schlüssel `massDelete.summary.unknownRows.{one,other}`, `restore.summary.unknownRows.{one,other}`, `massDelete.summary.unknownInProtocol.{one,other}`, `massDelete.errors.unknownOutcome`, `restore.errors.unknownOutcome`, je de/en, Wortlaut wörtlich aus der Analyse D3 (`unknownInProtocol` Plural sinngemäß). Zeilentext für eine `unknown`-Zeile bleibt `massDelete.unknownOutcome`/`restore.unknownOutcome` (schon eingebunden, `run-progress-panel.ts:245-248`). `unknownRows` erscheint im `run-actions`-Slot (Delete-Panel, Restore-Section) und zählt aus dem **geklärten** Ergebnis, also erst nach dem Settle; `unknownInProtocol` nur im Delete-Panel und nur bei `unknown > 0`. `summary.counts` bleibt ohne `unknown` (wie beim Import). Einbindung nach dem Muster `import-progress-section.ts:165-172`, `:297-308`. | D3 | T1 (`errors.*`), T4 (`summary.*`) |
| 19 | **P6 „busy löst sich immer auf"** gilt für die neue Phase: der Settle-Pfad ist `timer(grace oder 0) → Read mit timeout → catchError → settleRun`, kein Ast ohne `settleRun`; `settleRun` erreicht `reporting` (mit `REPORT_TIMEOUT_MS`-Kette) oder `closed`. `reset()` während `settling` löst nur die Anzeige (Lifecycle `detach`, `engine.reset()` weil die Engine frei ist); der Datensatz settelt, meldet und schließt weiter; eine nicht erfolgreiche Meldung wird wie heute wiederangezeigt (Festlegung 13 aus Plan-256, `endReport`). Navigation/Kanalwechsel: Dienste sind Root-Singletons, `resetIfChannelChanged` löst nur `closed` — unverändert. | Analyse P6 | T2, T3 |
| 20 | **Delete-Guard hält bis `closed`, Restore ohne Guard.** Nichts zu tun: `destructiveOpen = destructive && phase !== 'closed'` deckt `settling` ab; Restore ist `destructive: false`. **Worst-Case-Dauer des Guards nach dem letzten Klick:** 3 s Wartezeit + 20 s Read + 3 × 30 s Meldeversuche + 2 s + 4 s Pausen ≈ **119 s** (heute ≈ 96 s). Steht so im DECISIONS-Eintrag. | D9; Lifecycle `:57-59` | T2 (Doku) |
| 21 | **Doku-Kommentare mitziehen** (englisch, im selben Commit): `DeleteRunInfo`/`RestoreRunInfo` („never sees settling" → gestrichen), `isSettling`-Kommentare beider Dienste, Lifecycle `RunPhase`-Doku (`:7-8` „import only"), Engine-Doku zu `transportLossIsUnknown` (`:176-177` „every existing run keeps failed" ist dann falsch). | Regel 3, Sprache | T1, T2, T3 |

---

## 3. Klärtabellen

`entries` ist das Ergebnis des Nachlese-Reads. Geklärt wird nur, wenn `entries !== null && entries.complete`; sonst bleibt jede `unknown`-Zeile `unknown`. Zeilen mit anderem Status werden nie angefasst. „Nicht passiert" ⇒ Festlegung 9.

### 3.1 Delete (Zeile = 7TV-Id; `REMOVE` nimmt alle Einträge der Id)

| Befund im Read | Ergebnis | Begründung |
|---|---|---|
| Id in `aliasesById` **nicht** vorhanden (die Map enthält auch aliaslose Ids mit leerer Liste, `seven-tv-set-entries.ts:53-58`; `aliaslessIds` zusätzlich zu prüfen ist redundant, aber erlaubt) | `done`, Key in `doneKeys` | Das `REMOVE` ist wirksam — oder ein Dritter war schneller; nicht unterscheidbar, das Set *ist* ohne die Id (Import akzeptiert dasselbe, `:1128-1131`) |
| Id vorhanden — egal unter welchem Alias, auch wenn ein Teil der Aliase fehlt | nicht passiert | Ein `REMOVE` nimmt alle Einträge atomar; ein Teilbestand ist fremde Änderung, die Id ist aber noch da |
| Read `null` oder `complete: false` | bleibt `unknown` | Eine Liste, die nur die Hälfte kennt, beweist kein Fehlen (Spec #200 8.3) |

### 3.2 Restore (Zeile = `${id}#${alias}`; Klärung braucht `aliasByKey` und `defaultNameByKey`)

| Zeilen-Alias | Befund im Read | Ergebnis |
|---|---|---|
| `string` | `aliasesById.get(id)` enthält den Alias | `done` |
| `string` | Id nicht im Set | nicht passiert |
| `string` | Id im Set, aber nur unter anderen Aliasen (dazu zählt: der Alias sitzt an einer anderen Id) | bleibt `unknown` (Import `settleAdd`, `:1141-1146` — fremder Eintrag nicht unterscheidbar) |
| `null` (nur aus einer transfer-run-Datei) | `aliaslessIds.has(id)` **oder** `aliasesById.get(id)` enthält `defaultName` (aus der Datei, sonst `entries.defaultNameById.get(id)`, sofern nicht leer) | `done` (Spec #254 F5: 7TV legt aliaslose Einträge als benannten Eintrag unter dem Standardnamen an) |
| `null` | Id nicht im Set | nicht passiert |
| `null` | Id im Set unter anderem Alias; oder kein `defaultName` bekannt und nicht in `aliaslessIds` | bleibt `unknown` |
| beliebig | Read `null` oder `complete: false` | bleibt `unknown` |

Zwei Aliase derselben Id sind zwei Zeilen und werden einzeln geklärt; `doneSevenTvEmoteIds` dedupliziert für den Bericht wie heute.

---

## 4. Grenzfälle

| Fall | Verhalten | Wo geprüft |
|---|---|---|
| Abbruch **zwischen** zwei Zeilen | `inFlight === null` ⇒ Restzeilen `cancelled`, keine `unknown`, kein Settle, kein Read; wie heute | Bestehende Specs (Delete `:872-884`, `:902-924`; Restore `:1303`) bleiben grün |
| Abbruch **während einer Rate-Limit-Pause** | Die abgelehnte Antwort hat `inFlight` schon geleert ⇒ `cancelled`, kein Read, Countdown weg | Delete-Spec „cancel() during a rate-limit pause" unverändert |
| Abbruch mit Request in der Luft | Zeile `unknown`/`cancelled`-Ursache; `settling`; 3 s Wartezeit; ein Read; Klärung nach 3.1/3.2; Meldung nur für `done` | T2/T3 Unit, T6 E2E |
| Abbruch mit Request in der Luft **nach** schon bestätigten Zeilen | Bestätigte bleiben `done`, die Zeile in der Luft wird geklärt; **eine** Meldung nach dem Settle mit der Vereinigung | T2/T3 Unit |
| 5xx / Status 0 ohne Abbruch | `unknown`/`transportLoss`; Lauf läuft weiter (kein `abortOn`); Settle **ohne** Wartezeit; „nicht passiert" ⇒ `failed` mit `*.errors.unknownOutcome` | T2/T3 Unit |
| 4xx, GraphQL-Fehler, Rate-Limit-Aufgabe | `failed` wie heute, nie `unknown` | Engine-Spec deckt es; Bestands-Specs 503/500 treffen nach grep nur den **Melde**-Endpunkt (T2/T3 bestätigen) |
| Read scheitert / Timeout / `complete: false` | Alle `unknown` bleiben; Meldung nur für vorher `done`; sonst D6 | T2/T3 Unit, T6 E2E (Read-Abbruch) |
| Fremde gleichzeitige Änderung | Nicht unterscheidbar — der Read entscheidet, wie beim Import; dokumentiert, nicht behandelt | Doku-Kommentar an der Klärfunktion |
| `reset()` / Kanalwechsel / Navigation während `settling` | Anzeige weg, Datensatz settelt und meldet weiter; Guard bleibt bis `closed`; nicht erfolgreiche Meldung ⇒ Wiederanzeige | T2/T3 Unit (Spec-Fall je Dienst) |
| Programmatischer Start während `settling` | Vom Arbiter für alle UI-Startpunkte gesperrt (`notStarted.settling`); der alte Datensatz läuft unabhängig zu Ende | Plan-256 T8 E2E deckt die Sperre; kein neuer Fall |
| Lauf mit `done` + `unknown` | Meldung für `done`, `unknownRows`-Zeile, `unknownInProtocol`-Zeile; kein Client-Resync (Backend-Resync aus der Meldung) | T2 Unit, T4 Panel-Spec |
| Lauf nur mit `unknown` | Kein Bericht, `closed` sofort nach dem Settle; Client-Resync des aktiven Sets (falls `expectedChannelName`), Delete-Panel `reloadRequested` | T2/T3 Unit, T4 Panel-Spec |
| Retry der Meldung | liest `result.doneKeys` des geklärten Ergebnisses — unverändert | Bestands-Specs |
| Protokoll-Download während `settling` | Kein Knopf (`lastRun === null`); erst nach dem Settle | T4 Panel-Spec |
| Restore aus v2-Datei | Nur `done` vorhanden ⇒ identisch zu heute | T5 Parser-Spec |
| Restore aus v3-Datei, `unknown`-Zeile, Emote noch da | Live-Filter verwirft sie als „bereits vorhanden" (`skippedDuplicates`); Filter-Read scheitert ⇒ `ADD` geht raus, endet als 409-Zeile | T5 Parser-Spec, Filter-Bestandsspec |
| v3-Datei in einem alten Tab (v2-Leser) | `wrongVersion` — beabsichtigt (K5) | T5 Parser-Spec |
| Zeile `unknown` im Restore mit `alias === null` | Tabelle 3.2 | T1 Klärspec |

---

## 5. DECISIONS (Regel 3)

**Betroffene Bestandseinträge** (werden nicht umgeschrieben, der neue Eintrag benennt sie): 2026-09-26 „7TV runs complete run-bound" (`:378`; „Neither ever sees settling", `:459-460`, und der `reset()`-Absatz `:426-428`), 2026-09-23 #230 (`:1975-1982`, „a plan without any replace row keeps `failed`"), 2026-09-22 K5 (`:3674-3690`, purge-run v2), 2026-09-25 #253 (`:963`) und #255 (`:680`) nur als Kontext für D6. Plan-256 Festlegung 5 wird aufgehoben; Plan-256 selbst bleibt unverändert.

**Zwei neue Einträge, englisch, oben in der Datei:**

| Eintrag (Titel) | Kernaussage | Commit |
|---|---|---|
| *Delete and restore runs settle a lost answer by one re-read — a cancel mid-request is `unknown`, never `cancelled`* | Beide Läufe setzen `transportLossIsUnknown`, durchlaufen `settling` mit einem Read (20 s, nach einem Nutzer-Abbruch erst nach 3 s Wartezeit), veröffentlichen und melden erst danach; das hebt Plan-256 Festlegung 5 auf und für den Restore ausdrücklich die #230-Regel „ADD-only bleibt `failed`" — der Import-add-only-Fall bleibt die bewusste Ausnahme, weil ein Import ohne Replace nichts löscht und sein Nachlesen in #284 entschieden wird. Nennt die 119-s-Worst-Case-Dauer des Guards. | T2 (`Betrifft:` in T3 um den Restore-Dienst ergänzt) |
| *The purge-run protocol carries `unknown` rows — format version 3, restorable alongside `done`* | `meta.counts.unknown`, Version 3 nach der K5-Regel, Parser liest 1/2/3 und liefert `done` + `unknown`; der Live-Filter ist die Sicherung. | T5 |

---

## 6. Tasks

Branch `fix/275-cancel-unknown`, PR gegen `feat/emote-sets-200`. Ein Commit je Task (Conventional Commits, englisch, keine `#`-Referenzen in Git-Metadaten — Memory). Jeder Task fährt seine gefilterten Specs plus `npm --prefix web run build`, `cd web && npx tsc -p tsconfig.spec.json --noEmit`, Lint und Prettier. **Reihenfolge:** T1 → T2 ∥ T5 → T3 → T4 → T6 → T7.

```
Welle 1   T1
Welle 2   T2 ─────┐   T5 (parallel, eigener Worktree; gemeinsame Datei nur docs/DECISIONS.md, zwei Einträge oben — neuer Eintrag über den vorhandenen)
Welle 3   T3 (nach T2 gemergt — spiegelt T2)
Welle 4   T4 (nach T3 und T5)
Welle 5   T6
Welle 6   T7
```

### T1 — Engine-Feld, pures Klärmodul, Konstanten, Fehlertexte

**Ziel:** Alles Pure, das T2/T3 brauchen, in einem Commit; noch kein Dienst setzt das Flag.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-run-engine.ts` (+ `.spec.ts`): Feld `unknownCause` (Festlegung 3), Doku `:71-75`, `:166-179`, `:857-873`. **Neu** `web/src/app/core/seven-tv/seven-tv-run-settlement.ts` (+ `.spec.ts`): Konstanten (Festlegung 4, 5), `settleDeleteResult(result, entries, translate): RunResult`, `settleRestoreResult(result, entries, aliasByKey, defaultNameByKey, translate): RunResult` (beide liefern ein neues `RunResult` mit neu berechneten `doneKeys`, Reihenfolge der Zeilen unverändert), `hasCancelledUnknown(result): boolean`, `unknownCount(items): number`. `web/public/i18n/{de,en}.json`: `massDelete.errors.unknownOutcome`, `restore.errors.unknownOutcome`.

**Akzeptanzfälle (Unit):** Engine — Abbruch mit Request in der Luft und Flag ⇒ `unknown` + `'cancelled'`; 503 mit Flag ⇒ `unknown` + `'transportLoss'`; ohne Flag kein Feld; `done`/`failed`/`cancelled` tragen nie eine Ursache. Klärmodul — jede Zeile aus 3.1 und 3.2 als eigener Fall; `entries === null` und `complete: false` lassen alles stehen; Zeilen mit anderem Status sind referenzgleich unverändert; „nicht passiert" nach Festlegung 9 (beide Ursachen, mit und ohne vorhandenen Transporttext); `doneKeys` enthält genau die `done`-Keys in Queue-Reihenfolge; Restore-`null`-Alias mit `defaultName` aus Map und aus `entries`.

**Abhängigkeiten:** keine. **Modell:** `sonnet`.
**Commit:** `feat(seventv): name the cause of an unknown row and add the pure delete/restore settlement`.

### T2 — Delete-Dienst: Flag, `settling`, Wartezeit, Read, Settle, Meldung danach

**Ziel:** Festlegungen 1, 2, 5, 6, 10, 11, 12, 13 (Dienstanteil), 19, 20, 21 für den Delete.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-delete.service.ts` (+ `.spec.ts`), `web/src/app/core/seven-tv/seven-tv-run-lifecycle.ts` (nur Doku `:7-8`), `docs/DECISIONS.md` (Eintrag 1). Bestands-Specs, die den sofortigen Übergang `running → reporting` belegen, bekommen die neue Erwartung nur dort, wo `unknown` im Spiel ist; alles ohne `unknown` bleibt byte-gleich grün.

**Verträge:** `onRunComplete(runId, result)`: `unknown` vorhanden ⇒ `phase: 'settling'`, `result` bleibt `null`; Wartezeit nach Festlegung 5; Read nach Festlegung 6; `settleRun(runId, entries)` schreibt in einem Update `result` (geklärt), `phase: 'reporting'`, `syncReport: 'pending'` bei `doneKeys.length > 0`; danach `reportDeleted` wie heute bzw. D6-Resync bei nur `unknown`. Ohne `unknown` ⇒ Verhalten exakt wie heute (ein Update, direkt `reporting`/`closed`). `queue` als `linkedSignal`-Projektion (Festlegung 11). `engine.reset()` für einen nicht gezeigten Lauf bleibt an seiner Stelle.

**Akzeptanzfälle (Unit, `vi.useFakeTimers`, `HttpTestingController`; Read-Requests am Query-Text von Mutationen unterscheiden wie in der Import-Spec):** Abbruch in der Luft ⇒ `isSettling` wahr, `destructiveOpen` wahr, `lastRun() === null`, **kein** Read vor 3 000 ms, dann genau ein Read; Read „Id fehlt" ⇒ Zeile `done`, `sync-deleted` mit ihrer Id, `closed` nach Antwort · Read „Id da" ⇒ `cancelled`, kein Bericht, `closed` · Read-Fehler / Timeout 20 s / `complete: false` ⇒ bleibt `unknown`, kein Bericht, `fallbackResync` für `expectedChannelName`, keiner bei `null` · 503 mitten im Lauf ⇒ Lauf läuft weiter, Read **ohne** Wartezeit, „nicht passiert" ⇒ `failed` mit Grund · gemischt (2 done, 1 unknown geklärt zu done) ⇒ eine Meldung mit drei Ids · `reset()` während `settling` ⇒ `run() === null`, `queue()` leer, Settle und Meldung laufen, `failed`-Meldung ⇒ Wiederanzeige · `queue()` zeigt während `settling` die Engine-Zeilen (`unknown`), nach dem Settle die geklärten · `lastRun().result` ist referenzgleich über spätere Report-Patches · Abbruch zwischen Zeilen ⇒ kein Read, wie heute.

**Abnahme:** `grep -n "transportLossIsUnknown" seven-tv-delete.service.ts` trifft `REMOVE_OPERATION`; kein `result:` vor `settleRun` außer `null`; DECISIONS-Diff von der Hauptsession gelesen.
**Abhängigkeiten:** T1. **Modell:** `opus` — Reihenfolge von Wartezeit, Read, Veröffentlichung und Meldung ist der gehebelte Teil.
**Commit:** `feat(seventv): settle a delete run's unknown rows by one re-read before reporting`.

### T3 — Restore-Dienst: dasselbe, ohne Guard

**Ziel:** Spiegel von T2 für den Restore (Festlegungen 1, 2, 5, 6, 8, 10, 11, 12, 13, 19, 21), `destructive: false` unverändert.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-restore.service.ts` (+ `.spec.ts`), `docs/DECISIONS.md` (`Betrifft:` von Eintrag 1). Kein Import aus `seven-tv-delete.service.ts` für die neuen Konstanten (sie liegen im Klärmodul), damit T3 keine T2-Datei berührt.

**Verträge:** wie T2; zusätzlich `aliasByKey`/`defaultNameByKey` je Lauf im Closure von `onRunComplete` (Festlegung 8); D6 über `triggerResync(runId, expectedChannelName)` (Dock-Zeile „wird abgeglichen"), nie für `null`; `reportRestored` mit dem geklärten `result`.

**Akzeptanzfälle (Unit):** die T2-Liste sinngemäß, plus: zwei Aliase einer Id, eine in der Luft ⇒ nur diese geklärt, Bericht dedupliziert die Id · `null`-Alias mit `defaultName` aus der Datei ⇒ `done`, wenn der Read den Standardnamen führt · Id unter fremdem Alias ⇒ bleibt `unknown` · `destructiveOpen` bleibt während `settling` falsch · Bestandsfall „503 am Melde-Endpunkt" unverändert grün.

**Abhängigkeiten:** T2 gemergt (Muster). **Modell:** `sonnet`.
**Commit:** `feat(seventv): settle a restore run's unknown rows by one re-read before reporting`.

### T4 — Dock und Seite: Texte, Restore-Angebot, `deleteRunActive`, D6-Reload

**Ziel:** Festlegungen 13 (Panelanteil), 14, 16 (Panel-Weg), 17, 18.

**Dateien:** `web/src/app/shared/seven-tv/mass-delete-panel.ts` (+ `.spec.ts`): `unknownRows`- und `unknownInProtocol`-Zeilen im `run-actions`-Slot, Restore-Knopf und `openRestoreConfirm` nehmen `unknown` dazu, Delete-Effekt emittiert `reloadRequested` für den Nur-`unknown`-Fall; `web/src/app/shared/seven-tv/restore-progress-section.ts` (+ `.spec.ts`): `unknownRows`-Zeile; `web/src/app/features/usage-stats/usage-stats-page.ts` (+ `.spec.ts`): `deleteRunActive`; `web/public/i18n/{de,en}.json`: die `summary.*`-Schlüssel aus Festlegung 18. Kein Wortlaut ist Prüfgegenstand; Schlüssel identifizieren die Zeile.

**Akzeptanzfälle (Unit):** Panel — `lastRun` mit 1 `unknown` ⇒ beide Zeilen vorhanden, mit 0 ⇒ keine; Restore-Knopf sichtbar bei `doneKeys` leer und 1 `unknown`; `openRestoreConfirm` reicht `done` + `unknown` an den Flow; `reloadRequested` genau einmal bei `closed`/`idle`/`unknown > 0`, nicht bei `closed`/`idle`/nur `cancelled`; kein Download-Knopf während `settling`. Section — `unknownRows` bei `run().result` mit `unknown`. Seite — `deleteRunActive` wahr bei `phase === 'settling'` (bestehende Fälle für `isRunning`/`pending` unverändert).

**Abhängigkeiten:** T3, T5. **Modell:** `sonnet`.
**Commit:** `feat(seventv): say in the docks what stayed unclear and offer unclear rows for restore`.

### T5 — Protokoll `purge-run` v3

**Ziel:** Festlegung 15, Datei-Weg von Festlegung 16.

**Dateien:** `web/src/app/shared/export/purge-run-export.ts` (+ `.spec.ts`), `docs/DECISIONS.md` (Eintrag 2). Fixtures in `file-import-step.spec.ts` und den E2E-Mocks, die `formatVersion: 2` hart tragen, bleiben lesbar (2 wird weiter akzeptiert) — nur prüfen, nichts hochziehen.

**Akzeptanzfälle (Unit):** `buildPurgeRunProtocol` mit `unknown`-Zeile ⇒ `counts.unknown = 1`, Summe = `requested`, `formatVersion 3`, Zeile mit `status: 'unknown'`; Parser — v1/v2/v3 akzeptiert, v4 `wrongVersion`; v3 mit `done` + `unknown` + `failed` + `cancelled` ⇒ genau `done` und `unknown` zurück; v2-Datei unverändert; nur `unknown` ⇒ `ok: true` (nicht `noRestorableRows`); CSV-Spalten unverändert.

**Abhängigkeiten:** keine (parallel zu T2). **Modell:** `sonnet`.
**Commit:** `feat(export): carry unknown rows through the purge-run protocol as format version 3`.

### T6 — E2E: Abbruch in der Luft, Read-Ausgänge, Guard, ein Restore-Fall

**Ziel:** Vier Fälle in `web/e2e/emote-import.e2e.spec.ts` (neue `test.describe('delete/restore: a cancelled request is settled (#275)')`, damit `holdRoute` `:2382-2407`, `unloadPrevented` `:2415`, `startReportingDeleteRun` `:2631-2672`, `cell`, `deleteDock` ohne Extraktion nutzbar sind; eine Extraktion nach `e2e/support/` ist erlaubt, aber kein Ziel).

**Muster:** `holdRoute` mit Prädikat `sevenTvGqlRequestKind(...) === 'removeEmote'` hält den REMOVE; „Abbrechen" klicken, während er hängt; danach `holdRoute` auf `setRead` (Prädikat „erst nach dem Abbruch", wie `:2497-2503` mit `addAborted`) für den Nachlese-Read, dessen Antwort den Fall bestimmt; Transportverlust per `route.fulfill({ status: 503 })`, Read-Fehler per `route.abort()` (`:2486-2491`). **`route.fallback()` nach seitenseitigem Abbruch kann werfen** (der Request existiert nicht mehr) — im Halte-Handler in `try/catch`. Wartezeit: `page.clock.install()` **vor** `goto`, nach dem Klick `runFor(3_000)` (CLAUDE.md: nie `fastForward`); kämpft die Uhr mit den Route-Holds, sind 3 s Echtzeit je Fall zulässig — im Bericht nennen.

**Fälle:** (1) Abbruch in der Luft, Read „weg" ⇒ „Wird abgeschlossen…" sichtbar, `unloadPrevented` wahr bis zur `sync-deleted`-Antwort, danach „Schließen", Zeile im Dock ohne Fehler, Reihe verschwindet aus dem Grid. (2) Read „noch da" ⇒ kein `sync-deleted`-Request, „Schließen", Zeile bleibt im Grid. (3) Read abgebrochen ⇒ `unknownRows`-Text sichtbar, `unknownInProtocol`-Text sichtbar, kein Bericht, `resync`-Request für den Kanal, „Schließen". (4) Restore über den Datei-Weg (Fixture wie `:3542`/`:4081`), `addEmote` gehalten, Abbruch, Read „Alias da" ⇒ `sync-restored` mit der Id, kein Unload-Schutz während des Settles.

**Abhängigkeiten:** T4. **Modell:** `opus` (Route-Holds plus Uhr sind fehlerträchtig). **Vorbedingung:** `:5151`, `:4200`, `:4300` frei.
**Commit:** `test(e2e): settle a delete or restore cancelled mid-request`.

### T7 — Abnahme: Gates, Coverage, Live-Test, Zweitmeinung

**Gates, alle grün, in dieser Reihenfolge:** `dotnet test EmotePurge.slnx` (Docker; Backend unverändert, das Gate ist trotzdem die Fertigmeldung des Repos) · `npm --prefix web run build` · `cd web && npx tsc -p tsconfig.spec.json --noEmit` · `npm --prefix web test -- --watch=false` · `npm --prefix web run lint` + `npm --prefix web run format` · `npm --prefix web run e2e` (nur wenn `:5151`/`:4200`/`:4300` frei; rote Läufe zuerst als Speicherdruck verdächtigen — Memory) · `node scripts/coverage-local.mjs` (`--frontend-only` halbiert die Laufzeit, es gibt keinen Backend-Diff; Schwelle 80 % neuer Code, Näherung).

**Live-Test gegen echtes 7TV (Regel 16, Betreiber-Handgriff, Subagent protokolliert):** Api per `dotnet run` aus dem Checkout, `npm start`, Playwright-MCP-Browser; Testkonto und Sets wie in Plan-254 0.1 (`olaf_olaf_son`, `tttt`/`test`). **Vorher** das Set sichern (Protokoll-Export bzw. Set-Read notieren). Ablauf: ein Delete mit 1–2 Emotes starten, per `page.route` den `removeEmote` um ~5 s verzögern (`route.continue` nach Delay), währenddessen „Abbrechen" klicken; erwartet: „Wird abgeschlossen…", nach ≥ 3 s ein Read, Zeile `done` (7TV hat den Request verarbeitet), `sync-deleted` mit der Id, Audit-Eintrag `emotes.syncDeleted`, Guard bis „Schließen". Dann derselbe Handgriff mit `route.abort()` auf den Read ⇒ `unknown`-Texte, kein Bericht, Resync. **Danach exakt wiederherstellen** über „Wiederherstellen" aus dem Protokoll (Restore-Fall gleich mit prüfen: `addEmote` verzögert, Abbruch, Read, `sync-restored`) und den Set-Stand gegen die Sicherung vergleichen. Den `averageRoundTripMs`-Wert aus der Konsole ins Ledger (Festlegung 5).

**Zweitmeinung (Regel 22):** `/codex:review --model gpt-6-sol` mit `--scope branch --base origin/feat/emote-sets-200` aus dem Checkout (Memory: Codex-Review-Fallen); Findings dem Nutzer vorlegen, nicht umsetzen; Widerspruch zu Opus-Review ⇒ Fable als Schiedsrichter (global).

**PR-Text:** Festlegung 5 (3 s), Festlegung 17 (kein Dialog-Hinweis), die 119-s-Dauer, die vorläufigen Schlüssel gesammelt, das Live-Test-Protokoll.

**Abhängigkeiten:** T6. **Modell:** `haiku` für die Gate-Läufe, `sonnet` als Protokollant des Live-Tests.

---

## 7. Offene Punkte

1. **Festlegung 17 (kein Hinweis im Restore-Bestätigungsdialog)** weicht von der Analyse ab; Veto trifft nur T4 (ein `uncertainCount` an `RestoreConfirmDialogData` plus eine Zeile im Dialog wären der Weg).
2. **Festlegung 4 (Import/Undo behalten ihre Konstanten)** ist die Minimal-Diff-Lesart von D8; wer die Vereinheitlichung schon hier will, ergänzt T1 um zwei Import-Zeilen und drei Undo-Zeilen — dann laufen auch deren Specs mit.
3. **Wertwahl 3 000 ms (Festlegung 5)** ist innerhalb der D5-Spanne gesetzt, nicht gemessen; T7 liefert die Rundlaufzeit nach, ein anderer Wert ist eine Konstante.
4. **`reloadRequested` für den Nur-`unknown`-Fall (Festlegung 13)** ist in der Analyse als D6 nur für den aktiven Set-Resync genannt; das Panel-Signal ergänzt der Plan, weil die Seite sonst bis zum `channel.synced` einen Stand zeigt, den der Resync gerade ändert. Veto trifft T4.
