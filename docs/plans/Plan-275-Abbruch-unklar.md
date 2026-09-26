# Plan #275 — Abbruch mit Request in der Luft: Delete und Restore lesen nach, statt „abgebrochen" zu sagen

Erstellt am 2026-09-26 gegen `fix/275-cancel-unknown` = `408f07d3` (= `origin/feat/emote-sets-200`);
überarbeitet am 2026-09-27 nach dem Codex-Adversarial-Review und den Nutzerentscheidungen N1–N5.
Quellen: Issue #275, die Analyse mit den Nutzerentscheidungen D1–D9 (Scratchpad `analyse-275.md`,
2026-09-26) plus N1–N5 (2026-09-27) — **beide verbindlich, hier nicht neu verhandelt**; wo N einer
D-Entscheidung widerspricht, gilt N. Dazu Plan-256 Abschnitt 8 Nr. 3 und 8.3, Plan-254, `CLAUDE.md`
(Regeln 3, 7, 11, 12, 13, 14, 16, 18, 22), `web/.claude/CLAUDE.md`, `docs/DECISIONS.md` (2026-09-26
„run-bound", 2026-09-23 #230, 2026-09-22 K5) und der Code auf `408f07d3` — jede in Abschnitt 6
genannte Datei ist gelesen, nicht vermutet.

**Kein Code im Plan.** Namen stehen nur, wo sie ein Vertrag zwischen zwei Tasks sind. Findet ein
Task eine Abweichung zwischen diesem Plan und dem Code, gilt der Plan; der Fund kommt in den
Task-Bericht, nicht still in den Diff. **Leitplanke wie in Plan-256: fail-closed.** Eine Mutation,
die bei 7TV angekommen ist, bleibt nie ungemeldet; eine Zeile, deren Ausgang niemand kennt, heißt
`unknown` und nie „abgebrochen" — und **ein Read bestätigt nur, er entlastet nie** (N1).

---

## 1. Ziel und Nicht-Ziele

**Ziel.** Ein Delete- oder Restore-Lauf, dessen Request bei 7TV in der Luft war, als der Nutzer
„Abbrechen" klickte — oder dessen Antwort im Transport verloren ging (Status 0, 5xx) —, markiert
diese Zeile `unknown`, liest das Set nach dem Lauf **einmal** live nach, setzt die Zeile auf `done`,
wenn der Read die Mutation **positiv** belegt, lässt sie sonst `unknown`, meldet erst danach, und
sagt im Dock und im Protokoll ehrlich, was unklar geblieben ist. Das Protokoll trägt `unknown`
durch; ein Restore aus dem Protokoll bietet solche Zeilen mit an, aber nur, wenn die Live-Prüfung
sie vollständig gegen das Set halten konnte (N2).

**Nicht-Ziele.**

- **Import und Undo bleiben unverändert** — die Wartezeit nach einem Abbruch (D5) bekommen sie in
  #284 (Epic #200), nicht hier. Ihre eigenen Read-Timeout-Konstanten bleiben stehen (Festlegung 4).
- **Kein Backend-Diff.** `sync-deleted`, `sync-restored`, `resync` und ihre Antworten bleiben, wie
  sie sind; es ändert sich nur, *wann* und mit *welchen* Ids gemeldet wird.
- **Arbiter und Lifecycle bleiben generisch** — `settling` existiert dort schon; kein neuer
  Zustand, keine neue Kante.
- **Keine Änderung an der Engine-Semantik von `transportLossIsUnknown`** (Status 0 und 5xx ⇒
  `unknown`, `seven-tv-run-engine.ts:166-179`). **Die Engine-Lücke „HTTP 200 ohne `errors` oder mit
  leerem Body ⇒ `done`" (`:622-625`) ist #285**, nicht Teil dieses Plans — ein solcher Lauf endet
  auch nach diesem Plan `done`, ohne Read; nur nicht-JSON-Bodies erreichen den Fehlerpfad der Engine.
- **Keine Wortlaut-Runde.** Alle neuen Schlüssel sind vorläufig (D3) und keine Prüfgegenstände
  (Regel 12).

---

## 2. Festlegungen

Nummeriert; jede mit Task. Wo sie eine Nutzerentscheidung nur umsetzt, steht die D-/N-Nummer.
Gestrichene Nummern bleiben als Lücke stehen, damit Verweise aus der Analyse weiter stimmen.

| # | Festlegung | Grund / Beleg | Task |
|---|---|---|---|
| 1 | **Beide Operationen setzen `transportLossIsUnknown: true`** — `REMOVE_OPERATION` (Delete) und `addOperation` (Restore). Damit werden Status 0, 5xx **und** ein `cancel()` mit Request in der Luft `unknown`; 4xx und GraphQL-Fehler bleiben `failed`, ein Abbruch zwischen zwei Zeilen oder in einer Rate-Limit-Pause bleibt `cancelled` (`inFlight` ist dann `null`, Engine `:617-620`, `:874-896`). | D1, D7 | T2, T3 |
| 2 | **Neue Phase `settling` für Delete und Restore** — hebt Plan-256 Festlegung 5 auf. `onRunComplete` setzt `phase: 'settling'` genau dann, wenn das Ergebnis mindestens eine `unknown`-Zeile hat, sonst wie heute direkt `reporting`/`closed`. `SevenTvRunLifecycle` und `SevenTvRunArbiter` werden **nicht** angefasst: `isSettling` und `activeClaim.phase === 'settling'` greifen automatisch; das Panel zeigt `<prefix>.settling` schon (`run-progress-panel.ts:43-49`). | D1; Lifecycle `:50-54`, Arbiter `:99-113` | T2, T3 |
| 3 | **Gestrichen (N1): kein `unknownCause`-Feld an der Engine-Zeile.** Nach N1 gibt es keinen Ast mehr, der die Ursache braucht: der Settle verzweigt nicht danach (Festlegung 7/8), der Zeilentext ignoriert sie schon heute bewusst (`run-progress-panel.ts:242-247`: „the point for the user is not *why*"), das Protokoll trägt sie nicht, und die Handlung des Nutzers — Set prüfen, ggf. aus dem Protokoll wiederherstellen — ist bei „abgebrochen unterwegs" und „keine Antwort" dieselbe. Ein Transporttext steht bei Transportverlust ohnehin in `errorMessage` (Konsole). Der einzige verbliebene Unterschied, die Wartezeit, braucht kein Zeilenfeld (Festlegung 5). | N1; weniger ist besser | — |
| 4 | **Zwei Konstanten, gemeinsam für Delete und Restore, in einem neuen puren Modul** `web/src/app/core/seven-tv/seven-tv-run-settlement.ts`: `SET_ENTRIES_READ_TIMEOUT_MS = 20_000` und `CANCEL_SETTLE_GRACE_MS = 3_000`. **Import und Undo nutzen sie in diesem PR nicht** — `SETTLE_READ_TIMEOUT_MS` (Import) und `UNDO_SETTLE_READ_TIMEOUT_MS`/`RECHECK_READ_TIMEOUT_MS` (Undo) bleiben; die Vereinheitlichung gehört zu #284. Der Doc-Kommentar nennt die drei als wertgleich. | D8 | T1 |
| 5 | **Wartezeit vor dem Nachlese-Read nur nach einem Nutzer-Abbruch:** `CANCEL_SETTLE_GRACE_MS`, einmal je Lauf, genau dann, wenn `onRunComplete` aus dem eigenen `cancel()` des Dienstes heraus lief — `engine.cancel()` wird nur dort gerufen (Delete `:329`, Restore `:352`) und ruft `onRunComplete` **synchron** (`finish()` `:426`); der Dienst merkt sich das dienstlokal je Lauf, kein Engine-Feld. **Nicht** nach reinem Transportverlust: ein 5xx kommt erst, wenn 7TV mit dem Request fertig ist; bei Status 0 gibt es keinen Zeitpunkt, auf den ein Warten zielt. Beim Abbruch dagegen verarbeitet 7TV den Request derweil zu Ende; ein sofortiger Read sähe den alten Stand und die Zeile bliebe unnötig `unknown` — die Wartezeit erhöht die Trefferquote der Bestätigung, entscheidet aber nichts (N1). Ein Abbruch zwischen Zeilen mit älteren Transportverlust-Zeilen wartet dann ebenfalls 3 s: harmlos, nicht vermieden. 3 s ist ein Vielfaches der gemessenen Rundlaufzeit (`averageRoundTripMs`, T7 protokolliert den Wert) und kürzer als jede Rate-Limit-Pause. Läuft **innerhalb** von `settling` (Dock „Wird abgeschlossen…", Arbiter busy, Delete-Guard scharf). | D5, N1 | T1 (Konstante), T2, T3 |
| 6 | **Nachlese-Read:** `loadSevenTvSetEntries(httpClient, setId)` (Delete: `run.setId`, Restore: `run.targetSetId`), Pipe `timeout(SET_ENTRIES_READ_TIMEOUT_MS)` + `catchError(() => of(null))`, danach **immer** `settleRun`. Nur genau **ein** Read je Lauf, kein Retry. | D1; Import `:672-697` | T2, T3 |
| 7 | **Klärtabelle Delete — nur positiv (N1):** Read `!== null && complete` **und** Id nicht in `aliasesById` (die Map enthält auch aliaslose Ids, `seven-tv-set-entries.ts:53-58`) ⇒ `done`, Key in `doneKeys`. **Alles andere bleibt `unknown`:** Id noch da (ganz oder teilweise, egal welcher Alias — auch wenn ein Dritter sie nach unserem `REMOVE` neu hinzugefügt hat, Codex-Finding 4), Read `null`, Timeout, `complete: false`. „Id fehlt" kann auch ein Dritter verursacht haben; nicht unterscheidbar, das Set *ist* ohne die Id (Import akzeptiert dasselbe, `:1128-1131`). | N1 | T1 |
| 8 | **Klärtabelle Restore — nur positiv (N1)** (Zeile = `${id}#${alias}`): Read vollständig **und** Alias `string` in `aliasesById.get(id)` ⇒ `done`; Alias `null` (nur aus transfer-run-Datei) **und** (`aliaslessIds.has(id)` **oder** `aliasesById.get(id)` enthält `defaultName` aus der Datei, sonst `entries.defaultNameById.get(id)`, sofern nicht leer) ⇒ `done` (Spec #254 F5). **Alles andere bleibt `unknown`:** Id fehlt, Id nur unter anderen Aliasen, kein `defaultName` bekannt, Read `null`/Timeout/`complete: false`. Der Dienst hält `aliasByKey` und `defaultNameByKey` je Lauf im Closure von `onRunComplete`. Zwei Aliase einer Id sind zwei Zeilen; `doneSevenTvEmoteIds` dedupliziert wie heute. `complete: false` blockt bewusst auch die positive Bestätigung — eine Regel für beide Läufe, der Fall ist selten (10-Seiten-Schutz, `totalCount`-Abweichung). | N1, Spec #254 F5 | T1, T3 |
| 9 | **Gestrichen (N1): kein „nicht passiert".** Es gibt aus dem Read weder `cancelled` noch `failed`; damit entfallen `massDelete.errors.unknownOutcome` und `restore.errors.unknownOutcome` — sie hätten nur diesen Fall beschriftet. Verbleibende `unknown`-Zeilen zeigen weiter `massDelete.unknownOutcome`/`restore.unknownOutcome` (vorhanden, ursachenneutral). | N1 | — |
| 10 | **Veröffentlichung erst nach dem Settle:** `result` am Datensatz bleibt `null`, bis `settleRun` das geklärte Ergebnis schreibt — **keine** Vorab-Veröffentlichung wie beim Import. Folge ohne weiteren Code: `lastRun` (Delete) und `watchRunSettle` (Seite `:2872-2875`) sehen genau einen Stand, der Protokoll-Download kennt nur das geklärte Ergebnis, kein Doppel-Reload. `settleRun` schreibt `result`, `phase: 'reporting'` und die `syncReport`-Felder **in einem** `lifecycle.update` (Lifecycle-Guard `:140-142` schließt sofort, wenn nichts zu melden ist). | Analyse §2 | T2, T3 |
| 11 | **`queue` wird die Zeilenprojektion** (statt eines Alias auf `engine.queue`): geklärte `run().result.items`, sobald vorhanden, sonst die Engine-Queue. Als `linkedSignal` (schreibbar), weil `restore-progress-section.spec.ts` `queue.set` an fünf Stellen ruft (Plan-256 Festlegung 14). Kein Konsument ändert sich (`mass-delete-panel.ts:251-253`, `restore-progress-section.ts:65, :89`, `usage-stats-page.ts:1527, :1532`). | Analyse §2 | T2, T3 |
| 12 | **Berichte unverändert, nur später:** `reportDeleted(runId, doneKeys)` / `reportRestored(runId, result)` laufen aus `settleRun` mit den **neu berechneten** `doneKeys`; `retrySyncReport` liest `result.doneKeys` und ist damit automatisch richtig. Verbleibende `unknown`-Zeilen werden **nicht** gemeldet (wie E10 bei #254). | Analyse §4 | T2, T3 |
| 13 | **D6 für jeden Lauf, der nach dem Settle `unknown`-Zeilen behält** (N1) — geprüft, ohne Doppel-Resync: **(a) `doneKeys` leer** ⇒ kein Bericht, Client-Resync des **aktiven** Sets (`expectedChannelName !== null`; Delete über das vorhandene `fallbackResync`, Restore über `triggerResync` mit Dock-Zeile), Lauf direkt `closed`. Delete-Panel emittiert `reloadRequested` genau einmal, wenn der gezeigte Lauf `closed` ist, `syncReport === 'idle'` und `lastRun().result.items` eine `unknown`-Zeile enthält (heute endet der Effekt bei `idle` still, `mass-delete-panel.ts:530`); das Restore-Dock emittiert schon am Engine-Ende (`:552-560`). **(b) `doneKeys` nicht leer** ⇒ **bereits abgedeckt, kein Client-Resync:** der Bericht stößt im Backend einen geschützten Resync jedes Kanals an, der das Set führt (`SevenTvEndpoints.cs:308`/`:340`, `PublishAndResyncAfterSyncInSetAsync`) — das aktive Set eingeschlossen —, und der heilt auch die unklaren Zeilen; schlägt der Bericht endgültig fehl, greift der bestehende N1-Fallback (Delete `fallbackResync`, Restore `resyncAfterReport`). Backstop, nicht Mechanismus: der Cooldown pro Kanal ist zwischen Melde-Resync (`TryTriggerGuardedResyncAsync`) und `POST /channels/{name}/resync` geteilt, ein Doppel endete als 429/„cooldown". **Nicht-aktives oder untracked Ziel** (`expectedChannelName === null`): kein Client-Resync (wie #255); die Zeilen bleiben über `unknownRows` und das Protokoll sichtbar. | D6, N1 | T2, T3, T4 |
| 14 | **`deleteRunActive`** (Seite `:1502-1504`) erweitert um `run()?.phase === 'settling'`: der Set-Dropdown bleibt mit `emoteSetMenu.lockedDuringDelete` gesperrt, solange nachgelesen wird. | Analyse §2 | T4 |
| 15 | **Protokoll `purge-run` v3:** `PURGE_RUN_FORMAT_VERSION` 2 → 3; `meta.counts` bekommt `unknown` (Summe = `requested`); Zeilen tragen `status: 'unknown'` wie jeden anderen Status. Parser akzeptiert `formatVersion` 1, 2 **und** 3 und liefert Zeilen mit `status` `done` **oder** `unknown` — eine `unknown`-Zeile als `RestoreRow` mit Marker `uncertain: true` (neues optionales Feld; `done`-Zeilen ohne). Versionssprung nach der K5-Regel: ein v2-Leser würde die `unknown`-Zeilen still verwerfen. CSV bekommt keine neue Spalte. | D2; K5, `purge-run-export.ts:14-28` | T5a |
| 16 | **Restore bietet `done` + `unknown` an, `unknown` fail-closed (N2).** Beide Einstiege — Datei-Weg über den Parser, Dock-Weg (`mass-delete-panel.ts:669-672` filtert heute `done`; Knopf-Sichtbarkeit `:267`) — nehmen `unknown` dazu, der Dock-Weg setzt den Marker aus `item.status`. **`filterAlreadyPresentForRestore` verwirft jede `uncertain`-Zeile, wenn der Read scheitert (`available: false`, inkl. Timeout-Pfad `restoreConfirmPreviewUnavailable`) oder `complete: false` ist**, und zählt sie in einem neuen Ergebnisfeld `uncertainDropped`; `done`-Zeilen verhalten sich wie heute (fail-open). Gilt an **beiden** Prüfpunkten, Open-Time und Confirm-Time (`clipToShown` schneidet ohnehin auf das Gezeigte). **Am Code geprüft, bei erfolgreichem vollständigem Read:** eine `unknown`-Zeile, deren Id unter einem **anderen** Alias im Set steht, fällt unter Regel 2 (`missingAliases` liefert leer ⇒ ganze Zeile verworfen) — kein Duplikat; steht die Id nur unter Aliasen, die die Zeile selbst nennt, greift Regel 3 (#74-Teilwiederholung, so gewollt). Die Aussage „schlimmstenfalls 409" ist gestrichen: 7TV weist nur kollidierende Aliase ab, nie eine zweite Id. **Bleibt als Grenzfall:** das Fenster zwischen Filter-Read und `ADD` (Abschnitt 4). | D2, N2; `already-present-filter.ts:127-133`, `:368-395` | T5a (Marker), T5b |
| 17 | **Restore-Bestätigungsdialog bekommt genau einen Hinweis** (ersetzt „kein Hinweis"): `RestoreConfirmDialogData.uncertainDropped: number`, eine Zeile nur bei `> 0` (`restore.confirm.uncertainDropped.{one,other}`, vorläufig). Der Dialog öffnet **immer**, wenn `uncertainDropped > 0` — auch wenn keine Zeile übrig ist (der Kurzweg „alles schon da" darf dann nicht greifen, sein Wortlaut wäre falsch); bei `addCount === 0` ist der Bestätigen-Knopf deaktiviert. Ein Confirm-Time-Verwurf nach dem Dialog ist nur an der kleineren Queue und am vorhandenen `restore.duplicateCheckUnavailable` sichtbar (Offener Punkt 1). Kein weiterer Marker im Dialog: Zeilen, die die Prüfung passiert haben, fehlen im Set, egal ob sie `done` oder `unknown` hießen. | N2 | T5b |
| 18 | **Dock-Texte** (D3, Regel 7 gilt nicht): neue Schlüssel `massDelete.summary.unknownRows.{one,other}`, `restore.summary.unknownRows.{one,other}`, `massDelete.summary.unknownInProtocol.{one,other}`, je de/en, Wortlaut aus der Analyse D3. `unknownRows` erscheint im `run-actions`-Slot (Delete-Panel, Restore-Section) und zählt aus dem **geklärten** Ergebnis; `unknownInProtocol` nur im Delete-Panel bei `unknown > 0`. `summary.counts` bleibt ohne `unknown`. Muster `import-progress-section.ts:165-172`, `:297-308`. `*.errors.unknownOutcome` entfällt (Festlegung 9). | D3 | T4 |
| 19 | **P6 „busy löst sich immer auf"** gilt für die neue Phase: `timer(grace oder 0) → Read mit timeout → catchError → settleRun`, kein Ast ohne `settleRun`; `settleRun` erreicht `reporting` (mit `REPORT_TIMEOUT_MS`-Kette) oder `closed`. `reset()` während `settling` löst nur die Anzeige; der Datensatz settelt, meldet und schließt weiter; eine nicht erfolgreiche Meldung wird wie heute wiederangezeigt (`endReport`). Kanalwechsel: `resetIfChannelChanged` löst nur `closed` — unverändert. | Analyse P6 | T2, T3 |
| 20 | **Delete-Guard hält bis `closed`, Restore ohne Guard.** Nichts zu tun: `destructiveOpen = destructive && phase !== 'closed'` deckt `settling` ab. **Worst-Case-Dauer des Guards nach dem letzten Klick:** 3 s Wartezeit + 20 s Read + 3 × 30 s Meldeversuche + 2 s + 4 s Pausen ≈ **119 s** (heute ≈ 96 s); unverändert durch N1/N2, steht im DECISIONS-Eintrag. | D9; Lifecycle `:57-59` | T2 (Doku) |
| 21 | **Doku-Kommentare mitziehen** (englisch, im selben Commit): `DeleteRunInfo`/`RestoreRunInfo` („never sees settling"), `isSettling`-Kommentare beider Dienste, Lifecycle `RunPhase`-Doku (`:7-8` „import only"), Engine-Doku `:176-177` („every existing run keeps failed"), Parser-Doku `purge-run-export.ts:177-181` („returns only `done`"), Filter-Doku („fails open exactly like…"). | Regel 3, Sprache | T2, T3, T5a, T5b |

---

## 3. Klärregel in einem Satz

`entries !== null && entries.complete` **und** der Read zeigt die gewünschte Wirkung (Delete: Id weg; Restore: Alias bzw. Standardname bei dieser Id da) ⇒ `done`; sonst bleibt `unknown`. Zeilen mit anderem Status werden nie angefasst. Beide Klärfunktionen liefern ein neues `RunResult` mit neu berechneten `doneKeys` in Queue-Reihenfolge.

---

## 4. Grenzfälle

| Fall | Verhalten | Wo geprüft |
|---|---|---|
| Abbruch **zwischen** zwei Zeilen | `inFlight === null` ⇒ Restzeilen `cancelled`, keine `unknown`, kein Settle, kein Read; wie heute | Bestehende Specs (Delete `:872-884`, `:902-924`; Restore `:1303`) bleiben grün |
| Abbruch **während einer Rate-Limit-Pause** | `inFlight` schon geleert ⇒ `cancelled`, kein Read, Countdown weg | Delete-Spec „cancel() during a rate-limit pause" unverändert |
| Abbruch mit Request in der Luft, Request hatte 7TV erreicht | `unknown`; `settling`; 3 s Wartezeit; ein Read; Wirkung sichtbar ⇒ `done`; Meldung | T2/T3 Unit, T6 E2E (1)/(4), T7 live |
| Abbruch mit Request in der Luft, Request hatte 7TV **nicht** erreicht | wie oben, Read zeigt alten Stand ⇒ **bleibt `unknown`**; kein Bericht; D6 (a) | T2/T3 Unit, T6 E2E (2), T7 live |
| Abbruch in der Luft **nach** schon bestätigten Zeilen | Bestätigte bleiben `done`; **eine** Meldung nach dem Settle mit der Vereinigung; bleibt die Zeile `unknown`, deckt der Backend-Resync der Meldung sie (D6 (b)) | T2/T3 Unit |
| 5xx / Status 0 ohne Abbruch | `unknown`; Lauf läuft weiter (kein `abortOn`); Settle **ohne** Wartezeit; Read entscheidet positiv oder gar nicht | T2/T3 Unit |
| HTTP 200 ohne `errors`/leerer Body | `done` ohne Read — **#285**, hier unverändert | — |
| 4xx, GraphQL-Fehler, Rate-Limit-Aufgabe | `failed` wie heute, nie `unknown` | Engine-Spec; Bestands-Specs 503/500 treffen nach grep nur den Melde-Endpunkt (T2/T3 bestätigen) |
| Read scheitert / Timeout / `complete: false` | Alle `unknown` bleiben; Meldung nur für vorher `done`; sonst D6 (a) | T2/T3 Unit, T6 E2E (3) |
| Dritter entfernt die Id vor dem Read (Delete) | `done` — nicht unterscheidbar, wie Import | Doku an der Klärfunktion |
| Dritter fügt die Id nach unserem `REMOVE` wieder hinzu (Delete) | bleibt `unknown` ⇒ D6-Resync statt falschem „nicht passiert" | T1 Klärspec (Id da ⇒ unknown) |
| `ADD` wird bei 7TV erst **nach** dem Read wirksam (Restore) | bleibt `unknown`, Alias ist aber da; ein späterer Restore aus demselben Protokoll überspringt ihn (Filter „bereits vorhanden"); D6-Resync holt den Stand | Doku; T6 E2E (2) sinngemäß |
| `reset()` / Kanalwechsel / Navigation während `settling` | Anzeige weg, Datensatz settelt und meldet weiter; Guard bis `closed` | T2/T3 Unit |
| Programmatischer Start während `settling` | Arbiter sperrt (`notStarted.settling`) | Plan-256 T8 E2E; kein neuer Fall |
| Protokoll-Download während `settling` | Kein Knopf (`lastRun === null`); erst nach dem Settle | T4 Panel-Spec |
| Restore aus v2-Datei | Nur `done` vorhanden ⇒ identisch zu heute | T5a Parser-Spec |
| v3-Datei, `unknown`-Zeile, Filter-Read vollständig, Emote noch da (gleicher oder anderer Alias) | Regel 1–3 verwerfen die Zeile bzw. den vorhandenen Alias — kein Duplikat | Filter-Bestandsspec + T5b-Fall mit Marker |
| v3-Datei, `unknown`-Zeile, Filter-Read scheitert oder `complete: false` | Zeile verworfen, `uncertainDropped` im Dialog; `done`-Zeilen wie heute | T5b Filter-/Dialog-Spec, T6 E2E (5) |
| Fenster zwischen Filter-Read und `ADD` | Unverändert offen (Filter-Doku „residual race"); ein Dritter kann die Id dazwischen einsetzen ⇒ Duplikat möglich, wie bei jeder Restore-Zeile heute | Doku, nicht behandelt |
| v3-Datei in einem alten Tab (v2-Leser) | `wrongVersion` — beabsichtigt (K5) | T5a Parser-Spec |

---

## 5. DECISIONS (Regel 3)

**Betroffene Bestandseinträge** (nicht umgeschrieben, der neue Eintrag benennt sie): 2026-09-26 „7TV runs complete run-bound" (`:378`; „Neither ever sees settling" `:459-460`, `reset()`-Absatz `:426-428`), 2026-09-23 #230 (`:1975-1982`, „ADD-only keeps `failed`"), 2026-09-22 K5 (`:3674-3690`, purge-run v2), 2026-09-25 #253 (`:963`) und #255 (`:680`) als Kontext für D6. Plan-256 Festlegung 5 wird aufgehoben; Plan-256 bleibt unverändert.

**Zwei neue Einträge, englisch, oben in der Datei:**

| Eintrag (Titel) | Kernaussage | Commit |
|---|---|---|
| *Delete and restore runs settle a lost answer by one re-read that only ever confirms — a cancel mid-request is `unknown`, never `cancelled`* | Beide Läufe setzen `transportLossIsUnknown`, durchlaufen `settling` mit einem Read (20 s; nach einem Nutzer-Abbruch erst nach 3 s Wartezeit), veröffentlichen und melden erst danach. **Der Read bestätigt nur positiv** (Id weg / Alias da ⇒ `done`); jeder andere Befund — auch „noch da"/„fehlt" — bleibt `unknown`, weil ein Read einen noch laufenden Request nicht entlasten kann (Codex 2026-09-27). Verbleibende `unknown`-Zeilen ziehen einen Resync des aktiven Sets nach — direkt, wenn nichts zu melden ist, sonst über den Backend-Resync der Meldung. Hebt Plan-256 Festlegung 5 auf und für den Restore die #230-Regel „ADD-only bleibt `failed`" — der Import-add-only-Fall bleibt die bewusste Ausnahme (#284). Nennt die 119-s-Worst-Case-Dauer des Guards und #285 als bekannte Lücke. | T2 (`Betrifft:` in T3 um den Restore-Dienst ergänzt) |
| *The purge-run protocol carries `unknown` rows — format version 3, restorable alongside `done`, fail-closed when the live check cannot vouch* | `meta.counts.unknown`, Version 3 nach der K5-Regel, Parser liest 1/2/3 und liefert `done` + `unknown` (Marker `uncertain`). Der Live-Filter ist die Sicherung — und für `uncertain`-Zeilen **fail-closed**: kein vollständiger Read, keine Zeile; der Dialog nennt die Zahl. Regel 2 des Filters verhindert bei vollständigem Read die zweite Id; das Fenster Read→`ADD` bleibt. | T5a (Eintrag), T5b (`Betrifft:` ergänzt) |

---

## 6. Tasks

Branch `fix/275-cancel-unknown`, PR gegen `feat/emote-sets-200`. Ein Commit je Task (Conventional Commits, englisch, keine `#`-Referenzen in Git-Metadaten — Memory). Jeder Task fährt seine gefilterten Specs plus `npm --prefix web run build`, `cd web && npx tsc -p tsconfig.spec.json --noEmit`, Lint und Prettier.

**Reihenfolge (N4): kein Commit erzeugt `unknown`-Delete-Ergebnisse, bevor Protokoll v3, Parser und beide Restore-Einstiege damit umgehen** — T5a und T5b liegen deshalb **vor** T2. Für Restore geprüft: `unknown`-Restore-Zeilen hat außer dem Dock (Zeilentext `restore.unknownOutcome` vorhanden) keinen Konsumenten — kein eigenes Protokoll, kein Restore-aus-Restore —, T3 darf also vor T4 liegen; bis T4 fehlt nur die Summenzeile, und zwischen T2 und T4 ist der Set-Dropdown während `settling` noch nicht gesperrt (Festlegung 14, reine UX).

```
Welle 1   T1 ∥ T5a   (eigene Worktrees; keine gemeinsame Datei)
Welle 2   T5b        (nach T5a; kein Dienst setzt das Flag, noch keine unknown-Zeile möglich)
Welle 3   T2         (schaltet unknown für Delete scharf; DECISIONS-Eintrag 1)
Welle 4   T3         (spiegelt T2)
Welle 5   T4
Welle 6   T6
Welle 7   T7
```

### T1 — Pures Klärmodul und Konstanten

**Ziel:** Alles Pure, das T2/T3 brauchen; kein Dienst setzt das Flag, die Engine bleibt unangetastet.

**Dateien:** **Neu** `web/src/app/core/seven-tv/seven-tv-run-settlement.ts` (+ `.spec.ts`): Konstanten (Festlegung 4), `settleDeleteResult(result, entries): RunResult`, `settleRestoreResult(result, entries, aliasByKey, defaultNameByKey): RunResult`, `unknownCount(items): number`.

**Akzeptanzfälle (Unit):** je Klärfunktion — Wirkung sichtbar ⇒ `done` und Key in `doneKeys`; Id noch da (ganz, teilweise, unter anderem Alias) ⇒ bleibt `unknown`; Restore: Id fehlt / Id unter fremdem Alias / `null`-Alias ohne `defaultName` ⇒ bleibt `unknown`; `null`-Alias mit `defaultName` aus Map und aus `entries` ⇒ `done`; `entries === null` und `complete: false` lassen alles stehen; Zeilen mit anderem Status referenzgleich unverändert; `doneKeys` in Queue-Reihenfolge; Ergebnis ist ein neues Objekt, das alte unverändert.

**Abhängigkeiten:** keine. **Modell:** `sonnet`.
**Commit:** `feat(seventv): add the pure delete/restore settlement that only ever confirms`.

### T5a — Protokoll `purge-run` v3 und Parser

**Ziel:** Festlegung 15, Doku-Anteil von 21, DECISIONS-Eintrag 2.

**Dateien:** `web/src/app/shared/export/purge-run-export.ts` (+ `.spec.ts`), `docs/DECISIONS.md`. Fixtures in `file-import-step.spec.ts` und den E2E-Mocks mit `formatVersion: 2` bleiben lesbar — nur prüfen, nichts hochziehen.

**Akzeptanzfälle (Unit):** `buildPurgeRunProtocol` mit `unknown`-Zeile ⇒ `counts.unknown = 1`, Summe = `requested`, `formatVersion 3`; Parser — v1/v2/v3 akzeptiert, v4 `wrongVersion`; v3 mit `done` + `unknown` + `failed` + `cancelled` ⇒ genau `done` (ohne Marker) und `unknown` (`uncertain: true`) zurück; v2-Datei unverändert; nur `unknown` ⇒ `ok: true`; CSV-Spalten unverändert.

**Abhängigkeiten:** keine. **Modell:** `sonnet`.
**Commit:** `feat(export): carry unknown rows through the purge-run protocol as format version 3`.

### T5b — Restore-Einstiege: `unknown` anbieten, fail-closed filtern, Dialog-Hinweis

**Ziel:** Festlegungen 16, 17; `Betrifft:` von DECISIONS-Eintrag 2.

**Dateien:** `web/src/app/shared/seven-tv/already-present-filter.ts` (+ `.spec.ts`): `RestoreFilterRow.uncertain?`, Verwurf + `uncertainDropped` an beiden Ergebnisformen inkl. `restoreConfirmPreviewUnavailable`; `restore-confirm-dialog.ts` (+ `.spec.ts`): Feld, Zeile, Knopf bei `addCount === 0`; `restore-flow.ts` und `mass-delete-panel.ts` (+ Specs): Dock-Weg nimmt `done` + `unknown` (`:669-672`, `:267`) und setzt den Marker, Kurzweg „alles schon da" nur bei `uncertainDropped === 0`, Dialog-Daten; `web/public/i18n/{de,en}.json`: `restore.confirm.uncertainDropped.{one,other}`.

**Akzeptanzfälle (Unit):** Filter — Read ok/vollständig: `uncertain`-Zeile mit Id weg ⇒ bleibt, Id unter anderem Alias ⇒ verworfen (Regel 2), Id unter eigenem Alias ⇒ übersprungen wie heute, `uncertainDropped = 0`; Read scheitert / Timeout-Form / `complete: false` ⇒ `uncertain`-Zeilen fehlen in `rows`, Zahl in `uncertainDropped`, `done`-Zeilen unverändert (fail-open). Dialog — Zeile bei `> 0`, keine bei 0, Knopf deaktiviert bei `addCount 0`. Panel — Restore-Knopf sichtbar bei nur `unknown`; `openRestoreConfirm` reicht `done` + `unknown` mit Marker; alle Zeilen `uncertain` + Read scheitert ⇒ Dialog öffnet, kein Kurzweg. Flow — dasselbe für den Datei-Weg.

**Abhängigkeiten:** T5a. **Modell:** `opus` — der Filter ist die Sicherung gegen Duplikate.
**Commit:** `feat(seventv): offer unclear protocol rows for restore, dropped whenever the live check cannot vouch`.

### T2 — Delete-Dienst: Flag, `settling`, Wartezeit, Read, Settle, Meldung danach

**Ziel:** Festlegungen 1, 2, 5, 6, 10, 11, 12, 13 (Dienstanteil), 19, 20, 21 für den Delete.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-delete.service.ts` (+ `.spec.ts`), `seven-tv-run-lifecycle.ts` (nur Doku `:7-8`), `seven-tv-run-engine.ts` (nur Doku `:176-177`), `docs/DECISIONS.md` (Eintrag 1). Bestands-Specs ohne `unknown` bleiben byte-gleich grün.

**Verträge:** `onRunComplete(runId, result)`: `unknown` vorhanden ⇒ `phase: 'settling'`, `result` bleibt `null`; Wartezeit nach Festlegung 5 (dienstlokaler Abbruch-Merker); Read nach Festlegung 6; `settleRun(runId, entries)` schreibt in einem Update `result` (geklärt), `phase: 'reporting'`, `syncReport: 'pending'` bei `doneKeys.length > 0`; danach `reportDeleted` wie heute bzw. D6 (a) bei leeren `doneKeys`. Ohne `unknown` ⇒ Verhalten exakt wie heute. `queue` als `linkedSignal`-Projektion.

**Akzeptanzfälle (Unit, `vi.useFakeTimers`, `HttpTestingController`; Read-Requests am Query-Text unterscheiden wie in der Import-Spec):** Abbruch in der Luft ⇒ `isSettling`, `destructiveOpen`, `lastRun() === null`, **kein** Read vor 3 000 ms, dann genau einer; Read „Id fehlt" ⇒ `done`, `sync-deleted` mit ihrer Id, `closed` nach Antwort · Read „Id da" ⇒ **bleibt `unknown`**, kein Bericht, `fallbackResync` für `expectedChannelName`, keiner bei `null`, `closed` · Read-Fehler / Timeout 20 s / `complete: false` ⇒ dasselbe · 503 mitten im Lauf ⇒ Lauf läuft weiter, Read **ohne** Wartezeit · gemischt (2 `done`, 1 `unknown` → `done`) ⇒ eine Meldung mit drei Ids · gemischt (2 `done`, 1 bleibt `unknown`) ⇒ Meldung mit zwei Ids, **kein** `fallbackResync` · `reset()` während `settling` ⇒ `run() === null`, `queue()` leer, Settle und Meldung laufen, `failed`-Meldung ⇒ Wiederanzeige · `queue()` zeigt während `settling` die Engine-Zeilen, danach die geklärten · `lastRun().result` referenzgleich über spätere Report-Patches · Abbruch zwischen Zeilen ⇒ kein Read.

**Abnahme:** `grep -n "transportLossIsUnknown" seven-tv-delete.service.ts` trifft `REMOVE_OPERATION`; kein `result:` vor `settleRun` außer `null`; DECISIONS-Diff von der Hauptsession gelesen.
**Abhängigkeiten:** T1, T5b. **Modell:** `opus`.
**Commit:** `feat(seventv): settle a delete run's unknown rows by one confirming re-read before reporting`.

### T3 — Restore-Dienst: dasselbe, ohne Guard

**Ziel:** Spiegel von T2 (Festlegungen 1, 2, 5, 6, 8, 10, 11, 12, 13, 19, 21), `destructive: false` unverändert.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-restore.service.ts` (+ `.spec.ts`), `docs/DECISIONS.md` (`Betrifft:` von Eintrag 1). Konstanten aus dem Klärmodul, nichts aus `seven-tv-delete.service.ts`.

**Verträge:** wie T2; `aliasByKey`/`defaultNameByKey` je Lauf im Closure von `onRunComplete`; D6 (a) über `triggerResync(runId, expectedChannelName)`, nie für `null`; D6 (b) nichts Neues (`resyncAfterReport` bleibt).

**Akzeptanzfälle (Unit):** die T2-Liste sinngemäß, plus: zwei Aliase einer Id, eine in der Luft ⇒ nur diese geklärt, Bericht dedupliziert · `null`-Alias mit `defaultName` ⇒ `done`, wenn der Read den Standardnamen führt · Id fehlt / Id unter fremdem Alias ⇒ bleibt `unknown` · `destructiveOpen` bleibt während `settling` falsch · Bestandsfall „503 am Melde-Endpunkt" unverändert grün.

**Abhängigkeiten:** T2 gemergt. **Modell:** `sonnet`.
**Commit:** `feat(seventv): settle a restore run's unknown rows by one confirming re-read before reporting`.

### T4 — Dock und Seite: Texte, `deleteRunActive`, D6-Reload

**Ziel:** Festlegungen 13 (Panelanteil), 14, 18.

**Dateien:** `web/src/app/shared/seven-tv/mass-delete-panel.ts` (+ `.spec.ts`): `unknownRows`- und `unknownInProtocol`-Zeilen im `run-actions`-Slot, `reloadRequested` für den Nur-`unknown`-Fall; `restore-progress-section.ts` (+ `.spec.ts`): `unknownRows`; `web/src/app/features/usage-stats/usage-stats-page.ts` (+ `.spec.ts`): `deleteRunActive`; `web/public/i18n/{de,en}.json`: die `summary.*`-Schlüssel.

**Akzeptanzfälle (Unit):** Panel — `lastRun` mit 1 `unknown` ⇒ beide Zeilen, mit 0 ⇒ keine; `reloadRequested` genau einmal bei `closed`/`idle`/`unknown > 0`, nicht bei nur `cancelled`; kein Download-Knopf während `settling`. Section — `unknownRows` bei `run().result` mit `unknown`. Seite — `deleteRunActive` wahr bei `settling` (bestehende Fälle unverändert).

**Abhängigkeiten:** T3. **Modell:** `sonnet`.
**Commit:** `feat(seventv): say in the docks what stayed unclear and keep the set locked while settling`.

### T6 — E2E: beide Richtungen, Read-Ausfall, Guard, zwei Restore-Fälle

**Ziel:** Fünf Fälle in `web/e2e/emote-import.e2e.spec.ts` (neue `test.describe('delete/restore: a cancelled request is settled (#275)')`, damit `holdRoute` `:2382-2407`, `unloadPrevented` `:2415`, `startReportingDeleteRun` `:2631-2672`, `cell`, `deleteDock` ohne Extraktion nutzbar sind).

**Muster:** Der 7TV-Mock des Blocks führt einen kleinen Set-Zustand (Menge der Ids/Aliase), aus dem `setRead` antwortet. **Richtung „angekommen":** der `removeEmote`-/`addEmote`-Handler wendet die Mutation beim **Eintreffen** auf den Zustand an und hält nur die Antwort (`holdRoute` mit Prädikat `sevenTvGqlRequestKind(...)`); **Richtung „nie angekommen":** der Handler hält, ohne den Zustand zu ändern. „Abbrechen" klicken, während gehalten wird; danach `holdRoute` auf `setRead` (Prädikat „erst nach dem Abbruch", wie `:2497-2503`), Read-Fehler per `route.abort()` (`:2486-2491`). **`route.fallback()` nach seitenseitigem Abbruch kann werfen** — im Halte-Handler in `try/catch`. Wartezeit: `page.clock.install()` **vor** `goto`, nach dem Klick `runFor(3_000)` (nie `fastForward`); kämpft die Uhr mit den Route-Holds, sind 3 s Echtzeit je Fall zulässig — im Bericht nennen.

**Fälle:** (1) Delete, angekommen, Read ohne Id ⇒ „Wird abgeschlossen…", `unloadPrevented` bis zur `sync-deleted`-Antwort, „Schließen", Zeile ohne Fehler, Reihe verschwindet aus dem Grid. (2) Delete, **nie angekommen**, Read mit Id ⇒ `unknownRows`- und `unknownInProtocol`-Text, kein `sync-deleted`, `resync`-Request für den Kanal, „Schließen", Reihe bleibt im Grid. (3) Delete, angekommen, Read abgebrochen ⇒ wie (2). (4) Restore über den Datei-Weg (v3-Fixture mit `done`-Zeile), `addEmote` angekommen, Abbruch, Read „Alias da" ⇒ `sync-restored` mit der Id, kein Unload-Schutz. (5) Restore über den Datei-Weg, v3-Fixture mit einer `done`- und einer `unknown`-Zeile, Open-Time-Read abgebrochen ⇒ Dialog nennt 1 verworfene Zeile, `addCount` 1.

**Abhängigkeiten:** T4. **Modell:** `opus`. **Vorbedingung:** `:5151`, `:4200`, `:4300` frei.
**Commit:** `test(e2e): settle a delete or restore cancelled mid-request in both directions`.

### T7 — Abnahme: Gates, Coverage, Live-Test, Zweitmeinung

**Gates, alle grün, in dieser Reihenfolge:** `dotnet test EmotePurge.slnx` (Docker; Backend unverändert, das Gate ist trotzdem die Fertigmeldung) · `npm --prefix web run build` · `cd web && npx tsc -p tsconfig.spec.json --noEmit` · `npm --prefix web test -- --watch=false` · `npm --prefix web run lint` + `npm --prefix web run format` · `npm --prefix web run e2e` (nur wenn `:5151`/`:4200`/`:4300` frei; rote Läufe zuerst als Speicherdruck verdächtigen — Memory) · `node scripts/coverage-local.mjs --frontend-only` (Schwelle 80 % neuer Code, Näherung).

**Live-Test gegen echtes 7TV (Regel 16, Betreiber-Handgriff, Subagent protokolliert; N5):** Api per `dotnet run`, `npm start`, Playwright mit `page.route` per Skript (nicht per MCP-Klick allein); Testkonto und Sets wie in Plan-254 0.1 (`olaf_olaf_son`, `tttt`/`test`). **Vorher** das Set sichern. **(a) Angekommen:** `removeEmote` per `route.fetch()` sofort an 7TV durchreichen, nur die **Antwort** ~5 s zurückhalten, währenddessen „Abbrechen" ⇒ „Wird abgeschlossen…", nach ≥ 3 s ein Read, Zeile `done`, `sync-deleted`, Audit `emotes.syncDeleted`, Guard bis „Schließen". **(b) Nie angekommen:** `removeEmote` **vor** dem Weiterreichen halten (kein `fetch`), „Abbrechen", dann den gehaltenen Request verwerfen ⇒ Zeile bleibt `unknown`, `unknownRows`-Text, kein Bericht, `resync`; das Emote ist bei 7TV noch da (Set-Read gegen die Sicherung). **(c)** wie (a) mit `route.abort()` auf den Read ⇒ `unknown`, Resync. **Danach exakt wiederherstellen** über „Wiederherstellen" aus dem Protokoll — den Restore-Fall gleich mitnehmen (`addEmote` nach (a)-Muster verzögert, Abbruch, Read, `sync-restored`) — und den Set-Stand gegen die Sicherung vergleichen. `averageRoundTripMs` aus der Konsole ins Ledger (Festlegung 5).

**Zweitmeinung (Regel 22):** `/codex:review --model gpt-6-sol` mit `--scope branch --base origin/feat/emote-sets-200` aus dem Checkout (Memory: Codex-Review-Fallen); Findings dem Nutzer vorlegen, nicht umsetzen; Widerspruch zu Opus-Review ⇒ Fable als Schiedsrichter (global).

**PR-Text:** positiv-only (N1), fail-closed für `unknown`-Restore-Zeilen (N2), #285 als bekannte Lücke, 3 s, 119 s, die vorläufigen Schlüssel gesammelt, das Live-Test-Protokoll mit (a)/(b)/(c).

**Abhängigkeiten:** T6. **Modell:** `haiku` für die Gate-Läufe, `sonnet` als Protokollant des Live-Tests.

---

## 7. Offene Punkte

1. **Confirm-Time-Verwurf ohne Dialog (Festlegung 17):** N2 nennt die Zahl verworfener `unknown`-Zeilen im Dialog; scheitert erst der Confirm-Time-Read, ist der Dialog schon zu, und der Plan legt fest, dass die Zeilen trotzdem fallen (fail-closed an beiden Prüfpunkten) — sichtbar nur an der kleineren Queue und am vorhandenen `restore.duplicateCheckUnavailable`. Alternative wäre, beim Confirm-Time-Ausfall auf das Open-Time-Ergebnis zurückzufallen (wie heute `fallOnOpenTime`), was das Fenster für `unknown`-Zeilen um die Dialog-Verweilzeit weitet. Veto trifft T5b.
2. **Festlegung 4 (Import/Undo behalten ihre Konstanten)** ist die Minimal-Diff-Lesart von D8; wer die Vereinheitlichung schon hier will, ergänzt T1 um zwei Import-Zeilen und drei Undo-Zeilen — dann laufen auch deren Specs mit.
3. **Wertwahl 3 000 ms (Festlegung 5)** ist innerhalb der D5-Spanne gesetzt, nicht gemessen; T7 liefert die Rundlaufzeit nach, ein anderer Wert ist eine Konstante.
4. **`reloadRequested` für den Nur-`unknown`-Fall (Festlegung 13 (a))** ist in der Analyse als D6 nur für den aktiven Set-Resync genannt; das Panel-Signal ergänzt der Plan, weil die Seite sonst bis zum `channel.synced` einen Stand zeigt, den der Resync gerade ändert. Veto trifft T4.
