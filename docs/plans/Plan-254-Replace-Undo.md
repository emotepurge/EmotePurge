# Plan #254 — Ersetzung rückgängig: ein geglücktes „Ziel ersetzen" aus seiner Übertragungsdatei vollständig zurücknehmen

Erstellt am 2026-09-25 gegen `feat/254-replace-undo` = `0591b1a4` (Basis `992eef14`, Merge von
#270). **Der Epic-Branch `feat/emote-sets-200` steht inzwischen auf `9e380cc4`** (PR #274, #264:
Routen-Split) — der Branch enthält #264 noch **nicht**, und #256 Punkt 1 ist noch auf keinem Branch
gemergt; beides ist Gegenstand von T0, nicht dieses Plans. Quellen:
[die Spec](../superpowers/specs/2026-09-25-replace-undo-254-design.md) — dritte Fassung, vom
Betreiber am 2026-09-25 freigegeben; Abschnitte werden als „Spec N" zitiert, Entscheidungen als
E1–E24, Fallen als F1–F20, Akzeptanzkriterien als AK n —, Issue #254 (Hauptissue), #256 (Punkt 1
ist Vorbedingung, Vertrag in Spec 11.1 und im Issue-Kommentar), #255 (läuft parallel, Berührung in
Spec 11.4), #264 (gemergt: `usage-stats.routes.ts`, Leave-Guard nur dort), das Epic #200,
[Plan-253](Plan-253-Restore-pro-Set.md) und [Plan-230](Plan-230-Namenskonflikte.md) als
Formvorbilder, `CLAUDE.md` (Regeln 1–22, Tests, Gates, Sprache), `web/.claude/CLAUDE.md`,
`docs/UI-Designsprache.md` (§7.3), und der Code auf `0591b1a4` plus die drei #264-Dateien auf
`9e380cc4` — jede Datei aus Spec 14 ist gelesen, nicht vermutet.

**Die Spec ist die einzige Vertragsquelle. Dieser Plan trägt Schnitt, Reihenfolge, Nahtstellen,
Testerwartung je Task, Modellwahl und Gates.** Kein Wire-Format, kein Shape, keine Zustandsmatrix
steht hier noch einmal — an ihrer Stelle steht ein Verweis der Form „Vertrag: Spec 6.2, E21". Der
Plan enthält keinen Code: Namen stehen nur, wo sie ein Vertrag zwischen zwei Tasks sind und die
Spec sie der Plansache überlässt (E19 „wie die Prüfung eingehängt wird, ist Plansache", 6.5 „was
der Plan als gemeinsamen Baustein herauszieht, ist Plansache"). Findet ein Task eine Abweichung
zwischen Spec und Code, gilt die Spec, und der Fund wird im Task-Bericht gemeldet, nicht still
aufgelöst. Was der Plan über die Spec hinaus festlegt, steht gesammelt in Abschnitt 6 als
**Festlegung des Plans mit Betreiber-Veto**; was er in der Spec als Widerspruch oder Lücke gefunden
hat, steht in Abschnitt 7 — in der ersten Fassung als **Klärungsbedarf** mit Empfehlung, seit
Fassung 2 als **Entscheidung** (K1, K2, K4 vom Betreiber, K3 vom Orchestrator, alle 2026-09-25),
in die Tasks eingearbeitet und als Nachtrag „Klärungen zum Plan (2026-09-25)" am Ende der Spec
(Spec 17) festgehalten.

**Fassung 2 (2026-09-25).** Die erste Fassung (`a0946d71`) ist um die drei Befunde des
adversarialen Codex-Reviews (gpt-6-sol, alle `high`, Abschnitt 10) und die vier Entscheidungen zu
K1–K4 überarbeitet: die Herkunftssperre wird im Dienst ein zweites Mal geprüft, das Dialog-Ergebnis
übergibt laufende und übersprungene Zeilen explizit, und eine gemischte `planned`-Datei wird bis in
den Dienst verfolgt (T4, T5, T6, T8); das `finished`-Format bekommt eine eigene Zeilenart für
übersprungene Kandidaten, festgelegt **vor** T1 (T1, T4); die Quelle bekommt ihr Bild aus
`flags.animated` des Live-Reads nach der Backend-Regel, das Ziel einen Platzhalter (T5); der erste
Read liegt im Flow (T5, T6); T9 ruft den Codex-Companion direkt im Worktree mit
`--base origin/feat/emote-sets-200`. Eine weitere Codex-Runde auf den Plan gibt es nicht.

---

## 0. Ausgangslage

### 0.1 Was feststeht

- **Die Spec ist freigegeben** (2026-09-25, Kopfzeile): sechs Betreiberentscheidungen (Spec 13),
  zwei nach der Zweitmeinung (Spec 15 A/B), zwei adversariale Codex-Runden (Spec 16, 7 + 5
  Befunde) sind eingearbeitet. Keine offene Betreiberfrage aus der Spec selbst.
- **Kein Backend-Diff** (Spec 5, AK 24): beide Meldewege, Audit, Resync-Stufe, Zielliste mit
  `editable` existieren seit #253. Der Rückweg ist ein reiner Frontend-Revert (Spec 12).
- **#256 Punkt 1 ist Vorbedingung** (Spec 11.1, 15 B; Issue-Kommentar in #256): der Arbiter kennt
  „läuft oder settelt", der Unload-Schutz ist die Vereinigung über alle offenen destruktiven
  Läufe, ein vierter Lauf-Typ registriert sich an **einer** Stelle, und jeder Dienst schließt
  seinen Lauf lauf-gebunden. Reihenfolge laut Betreiber: #255 → #256 → #254. **Dieser Plan baut
  den Vertrag nicht, er setzt ihn voraus** — T0 prüft ihn am Epic-Stand, bevor ein Feature-Task
  startet.
- **#264 ist gemergt** (`9e380cc4`): die Nutzungs-Route ist ein `loadChildren`-Kind mit
  `features/usage-stats/usage-stats.routes.ts`, der Leave-Guard hängt nur dort. Der Undo-Dienst
  darf nichts ins Initial-Bundle ziehen; Build-Gate < 500 kB (heute 404 kB), Spec F9.
- **#255 läuft parallel.** Jeder `undo.*`- und `restore.import.choice.*`-Schlüssel ist vorläufig
  (Spec 6.7, 11.4); die Resync-Regel für nicht-aktive Ziele ist in der Spec bereits die
  #255-Fassung (E16). Wortlaute sind in keinem Task Prüfgegenstand (Regel 12).
- **Zwei DECISIONS-Einträge** (Spec 14), englisch, je im ersten Commit, der die Konvention
  einführt: Eintrag 2 (Datei-Grundsatz) in **T1**, Eintrag 1 (vierter destruktiver Lauf) in **T4**
  — Zuordnung in 0.4.
- **Der PR geht gegen `feat/emote-sets-200`**; Zweitmeinung über den Codex-Companion direkt im
  Worktree mit `--scope branch --base origin/feat/emote-sets-200` einmal je Branch (Regel 22,
  Aufruf in T9). Deploy hinter dem 2026-10-08 mit dem Rest von #200.
- **Testkonten für die Live-Verifikation** (Spec 9.5, T10): Broadcaster `olaf_olaf_son` mit den
  Sets `tttt` (aktiv) und `test`, Mod `definitiv_nicht_sensitron` ohne 7TV-Editor-Recht. Die Api
  läuft dafür per `dotnet run` **aus dem Worktree** (eine Worktree-Api verwirft ein fremdes Cookie,
  Memory — also dort neu einloggen, nicht das Cookie des Haupt-Checkouts erwarten). Zugangsdaten
  stellt der Betreiber, wenn es so weit ist.

### 0.2 Was der Plan beim Nachprüfen am Code gefunden hat

| Befund | Beleg | Folge |
|---|---|---|
| **Der Branch hat #264 nicht.** `features/usage-stats/usage-stats.routes.ts` fehlt auf `0591b1a4`; auf `9e380cc4` existiert sie (25 Zeilen, `canDeactivate: [usageStatsLeaveGuard]`), `app.routes.ts` lädt sie per `loadChildren`, `usage-stats.routes.spec.ts` (316 Zeilen) pinnt `leadsToSameRoute` gegen die echte Router-Konfiguration | `git log HEAD..origin/feat/emote-sets-200` = drei Commits (`e2c76f05`, `19f3334d`, `9e380cc4`) | T0 bringt den Branch auf den Epic-Stand, **bevor** T7 den Guard anfasst |
| **Der Arbiter ist noch der alte.** `SevenTvRunArbiter.activeRun` ist ein `computed` über drei `isRunning`-Signale, injiziert die drei Dienste hart, kennt kein `isSettling`, keinen Grund, keinen Unload-Schutz | `core/seven-tv/seven-tv-run-arbiter.ts` (Union `'delete' \| 'restore' \| 'import'`, drei `inject`) | #256 Punkt 1 ist nicht gemergt; T0 prüft den Vertrag aus Spec 11.1 Punkt für Punkt am dann aktuellen Epic-Stand und hält die **tatsächlichen Namen** der Registrierung, des Grunds und des Schutz-Signals im Ledger fest — T4 und T6 bekommen sie im Brief, nicht die Spec-Platzhalter |
| **Die Engine hat keinen Vor-Schritt-Hook, und `buildRequest` ist synchron.** `runWithBackoff` ruft `buildRequest` je Versuch (auch je Rate-Limit-Wiederholung) — der Read vor dem REMOVE ist aber asynchron (`loadSevenTvSetEntries`, paginiert) | `seven-tv-run-engine.ts:105-171` (`RunOperation`), `:387-425` (`runRowFrom`), Spec E19 „Plansache" | T3 gibt der Engine einen optionalen asynchronen Hook je Versuch (0.5); ohne Hook bleiben Delete, Restore und Import byte-identisch |
| **Kein Produzent liefert dem Undo-Dialog eine Bild-URL.** `TransferRunRow` trägt keine `imageUrl`; der Live-Read fragt nur `alias`, `emote.id`, `emote.defaultName` (`GQL_EMOTE_SET_ENTRIES_QUERY`); das Ziel steht nach dem Replace in keinem Set; der Auflösungsschritt bekommt seine URLs aus `ImportRow.imageUrl`/`EmoteListItem.imageUrl` (Postgres bzw. Set-Vorschau), und Plan-230 T1 verbietet die Ableitung aus der ID (`_static`-Befund) | `transfer-run-export.ts:56-72`, `seven-tv-set-entries.ts:16-19`, `import-conflict-resolution-step.ts:186, :215` | **K1, entschieden (Betreiber 2026-09-25, Abschnitt 7, Spec 17):** der Live-Read liest `flags { animated }` mit — genau das Feld, das die Vorschau-Query in `SevenTvApiClient.cs:71` liest; `Emote.images` liest sie absichtlich **nicht** (Codex-Befund 3) —, und die Quelle bekommt ihre URL nach der Backend-Regel `BuildForeignImageUrl` (`SevenTvApiClient.cs:1272-1280`: `4x_static.webp` nur bei `animated`, sonst `4x.webp`; fehlendes Flag ⇒ `false`). Das Ziel bekommt den Platzhalter. Im Frontend gibt es heute **keine** Ableitung aus der ID — `emote-url.ts` (`animatedEmoteUrl`, `isAnimatedEmoteUrl`) und `emote-image-loader.ts` schreiben nur gespeicherte URLs um; das Verbot aus Plan-230 T1 galt der Ableitung **ohne** Kenntnis der Animiertheit und bleibt dafür bestehen. T5 legt die Ableitung als reine Funktion neben `animatedEmoteUrl` an (0.5) |
| **`RunProgressPanel` trägt genau eine Meldung** (`syncReport`, `syncReportReason`, `syncRetryRequested`) und einen dreiwertigen `labelPrefix`; die Import-Section zeigt ihre **zweite** Meldung (`removalReport`) als eigene Zeile neben dem Panel | `run-progress-panel.ts:125-145`, `import-progress-section.ts:124-195` | Festlegung Nr. 3: das Panel trägt die Löschmeldung (die destruktive Tatsache, F8), die Section die Wiederherstellungsmeldung — Spiegel der Import-Section |
| **`readEnvelope` prüft `kind` nur als String; der Dispatch je Sorte liegt im Datei-Schritt**, `parseImportSource` weist `transfer-run` namentlich mit eigenem Schlüssel ab | `read-envelope.ts:30-33`, `file-import-step.ts:183-219`, `import-source-parser.ts:35-48` | `transfer-undo` bekommt dieselben drei Stellen (F7): Abweisung in T1, Dispatch in T6 |
| **Die Seite hat einen generischen Settle-Beobachter** (`watchRunSettle(source, settledTarget)`), der je Dienst einmal aufgerufen wird und `result.doneKeys` liest | `usage-stats-page.ts:2829-2870` | T7 = vierter Aufruf; der Laufdatensatz des Undo muss ein `RunResult`-kompatibles `result` tragen |
| **`restoreStartTarget()` ist die Ableitung, die der Undo braucht** — minus `resyncChannelName` (Spec 6.5, E16) | `restore-flow.ts:188-198` | T6 leitet `UndoRunTarget` daraus ab, ohne die Restore-Funktion zu ändern |
| **Settling, Nachlesen und Meldungen des Imports sind modulprivate Funktionen an `ImportRunItem`** (`settleRunResult`, `settleUnknownRow`, `sendFollowUp`, `applyIfCurrent`), und `onRunComplete` hat den Frühausstieg aus F20 | `seven-tv-import.service.ts:583-700`, `:960-1043` | Der Undo-Dienst **kopiert das Muster mit Verweis** statt zu extrahieren (Spec 6.5 erlaubt es ausdrücklich; die Extraktion ist #256 Punkt 5/6) und baut den lauf-gebundenen Abschluss von Anfang an ohne Frühausstieg (F20, AK 39) |
| **Die Import-Spec stubbt die Engine über `HttpTestingController` und `vi.useFakeTimers()`**, `expectOne(GQL_ENDPOINT)` je Schritt, `advanceTimersByTime(RUN_DELAY_MS)` dazwischen | `seven-tv-import.service.spec.ts:153-215` | Dasselbe Muster für den Undo-Dienst; die Read-Zählung je REMOVE (AK 26, 38) unterscheidet Reads von Mutationen am `query`-Text (`httpMock.match`) |
| **Drei E2E-Fälle und vier Spec-Fälle lesen heute eine Übertragungsdatei ein** und erwarten sofort den Restore; ein E2E-Fall zählt die Datei-Sorten im Datei-Schritt | `emote-import.e2e.spec.ts:3542, :3728, :3878` (Übertragungsdatei), `:1495` („lists the three acceptable file sorts"); `file-import-step.spec.ts:388, :501, :569, :600` | F10: T6 passt sie **im selben Commit** an (Weiche klicken; vier Sorten) |
| **Die E2E-Mocks kennen die Request-Arten** (`sevenTvGqlRequestKind`: `setRead`, `addEmote`, `removeEmote`, …) und beide set-zentrischen Meldungen (`mockSyncDeletedInSet`, `mockSyncRestoredInSet`) | `e2e/support/mocks.ts:1429-1508`, `:881-897` | Die Read-Zählung „ein `setRead` unmittelbar vor jedem `removeEmote`" (AK 38) ist im E2E ohne neuen Helfer prüfbar |
| **`channel-workspace-layout` ruft `resetIfChannelChanged` für Delete und Restore**, nicht für den Import (R9) | `channel-workspace-layout.ts:152-160` | Der Undo ist an `hostChannelName` gebunden wie der Restore (Spec 4.7, 6.6) — T7 fügt den Aufruf hinzu |
| **`dockVisible()` ist eine reine Funktion `actionDockHasContent`** mit je Dienst zwei Flags | `usage-stats-page.ts:1493-1512`, `shared/seven-tv/action-dock.ts` | T7 erweitert Funktion und Spec um `undoShown`/`undoNoticePending` (Spec 6.6) |
| **Die transiente Notiz** (`duplicateNoticePending`, 4000 ms, `showDuplicateNotice`) ist die Mechanik für „alles übersprungen, kein Lauf" (Spec 6.3) | `seven-tv-import.service.ts:329-334, :805-815`; Restore analog | T4 übernimmt die Mechanik für den Undo-Dienst; T7 zeigt die Gründe je Zeile (Spec 4.7) |

### 0.3 Modelle je Task

`opus` an sechs Stellen mit Begründung (T0 Vertragsprüfung, T2 Klassifikation, T4 Dienst, T5
Dialog, T6 Weiche/Flow, T8 E2E); `sonnet` für die klar spezifizierten Teile (T1 Dateiformate, T3
Engine-Hook, T7 Dock und Seite); `haiku` nur für die mechanischen Gate-Läufe in T9. T10 ist ein
Handgriff des Betreibers mit einem `sonnet`-Subagent als Protokollant. Fable wird **nicht** als
Implementer eingesetzt (global: nicht für reguläre Implementierung); die zwei Stellen, an denen ein
Fehler gehebelt wäre — die Klassifikation (T2) und der Dienst (T4) — bekommen stattdessen die
vollständige AK-4-Tabelle als Testvertrag, die Codex-Zweitmeinung (T9) und die Live-Verifikation
(T10) als Beurteilungs-Checkpoints; widersprechen sich Opus-Review und Codex dort, entscheidet
Fable als Schiedsrichter (global).

### 0.4 Branch, Worktrees, Commits

- **Branch `feat/254-replace-undo`**, Worktree `/home/dev/projects/EmotePurge-254`; T0 bringt ihn
  per Merge von `origin/feat/emote-sets-200` auf den dann aktuellen Epic-Stand (mit #264 **und**
  #256 Punkt 1). Der PR geht gegen `feat/emote-sets-200`.
- **Lanes** (Abschnitt 5): Welle 1 zwei Lanes (T1 → T2 ∥ T3), Welle 2 zwei Lanes (T4 ∥ T5), Welle
  3 zwei Lanes (T6 ∥ T7); alles andere seriell. Parallele Lanes laufen in eigenen Worktrees;
  gemeinsame Dateien je Welle stehen in Abschnitt 5 — nur `web/public/i18n/{de,en}.json`
  (Textkonflikt, trivial). Worktree-Fallen aus Plan-200 0.4 und dem Plan-230-Ledger gelten: aus
  dem Worktree nur bauen, nie `docker compose up`; E2E je Worktree auf eigenem Port; die
  Live-Verifikation (T10) läuft mit einer Api **aus diesem Worktree** (0.1).
- **Ein Commit je Task**, Conventional Commits, englisch, ohne `#`-Referenzen in Commit-Metadaten
  (Memory). Ein Task, der einen Bestandstest bricht (F10: Weiche, Sorten-Liste, Parser-Fixtures),
  repariert ihn **im selben Commit** — keine rote Zwischenstufe.
- **Aufrufer-Regel** (Plan-253 0.4, Codex-Befunde 2/4 dort): die Dateiliste jedes Tasks nennt jeden
  Aufrufer, jede typisierte Fixture und jeden Host-Import der Signaturen, die er ändert (Inventur
  in 0.6). Jeder Frontend-Task fährt neben seinen gefilterten Specs `npm --prefix web run build` als
  Typprüfung — `npx vitest` prüft keine Typen (Memory).
- **Regel 3 — die zwei Einträge (Spec 14):** Eintrag 2 („Every recovery file restores what its own
  run removed — the transfer-undo file", E3, E12, F7, mit dem Satz zu #230-Entscheidung 4
  gespiegelt und #230-Entscheidung 6 nicht wiederkehrend, E4) in **T1**, weil dort die Datei und
  ihr Restore-Parser entstehen; Eintrag 1 („A replace can be undone from its transfer file — a
  fourth destructive run with the replace's safeguards", E1, E2, E5–E7, E9–E11, E15–E17) in
  **T4**, weil dort der Lauf mit seinen Sicherungen entsteht — Festlegung Nr. 1 (Abschnitt 6) zu
  „erster betroffener Commit" gegen „Erreichbarkeit". Beide Einträge nennen in `Betrifft:` auch die
  Dateien der Tasks, die den Vertrag konsumieren. Die Hauptsession liest jeden DECISIONS-Diff selbst
  (Memory: „Grüne Suiten sind keine Fertigmeldung").
- **Regel 16 gilt trotz „kein Backend-Diff":** der Undo schreibt gegen 7TV und meldet an die Api;
  T10 ist die Live-Verifikation, keine Suite ersetzt sie.

### 0.5 Vom Plan benannte Bausteine (Namen als Vertrag zwischen Tasks)

Die Spec nennt die meisten Namen selbst (6.1–6.6); die Tabelle sammelt sie mit Ort und Task und
ergänzt nur, was die Spec offenlässt. Was ein Name trägt, steht in der Spec.

| Baustein | Name | Ort | Spec | erzeugt in | konsumiert in |
|---|---|---|---|---|---|
| Kandidat aus der Übertragungsdatei, **inklusive `provenance`** (Festlegung Nr. 2) | `UndoCandidate`, `UndoSourceFileInfo`, `parseTransferRunForUndo(text)`, `TransferRunUndoParseResult` | `shared/export/transfer-run-export.ts` (neben `parseTransferRunForRestore`; wie `RestoreRow` in `purge-run-export.ts` beim Parser liegt) | 6.1, E2, F17, AK 3 | T1 | T2, T5, T6 |
| Die vierte Einlesesorte | `ExportKind += 'transfer-undo'`, `TRANSFER_UNDO_FORMAT_VERSION`, `TransferUndoRow = TransferUndoExecutedRow \| TransferUndoSkippedRow` (Diskriminator `kind: 'executed' \| 'skipped'`; die übersprungene Zeilenart nach Spec 17 K4: Quell-ID, `sourceName`, `alias`, Ziel-ID, `provenance`, `skippedReason`, sonst nichts), `TransferUndoMetaPlanned`/`…Finished` (`counts.requested` nur gelaufene, `counts.skipped` dazu; `acknowledgedUnproven`), `TransferUndoPlanRecord`, `TransferUndoProtocol`, `buildTransferUndoPlanRecord`, `buildTransferUndoProtocol`, `transferUndoJson`, `transferUndoCsv`, `transferUndoPlanFilename`, `transferUndoFilename`, `parseTransferUndoForRestore` | `shared/export/export-envelope.ts`, `shared/export/transfer-undo-export.ts` (neu) | 6.4, 17 (K2, K4), E3, E12, F6, F7, F13, AK 18, 19 | T1 | T4 (Builder), T5 (`planned`-Datei), T6 (Restore-Parser im Dispatch), T7 (`finished`-Download) |
| Animiertheit im Live-Read | `SevenTvSetEntries.animatedById: Map<string, boolean>`; `GQL_EMOTE_SET_ENTRIES_QUERY` += `flags { animated }` (fehlendes Flag ⇒ `false`, wie `BuildForeignImageUrl`) | `core/seven-tv/seven-tv-set-entries.ts` | 17 (K1) | T5 | T5 (Dialog) |
| Bild-URL aus ID und Flag | `emoteStillUrl(sevenTvEmoteId, animated)` — bytegleich mit `BuildForeignImageUrl` (`SevenTvApiClient.cs:1272-1280`): `4x_static.webp` nur bei `animated`, sonst `4x.webp`; die **einzige** Ableitung aus der ID im Frontend, Doku nennt die Regel, die 404-Messung (Memory 2026-09-09) und Plan-230 T1 | `shared/emotes/emote-url.ts` (neben `animatedEmoteUrl`) | 17 (K1) | T5 | T5 (Dialog: Quelle) |
| Reine Klassifikation | `classifyUndoRows`, `classifyUndoRow`, `normalizeTargetEntries`, `diffUndoPlans`, `sameClassification`, `summarizeUndoPlan`; Typen `UndoPlan`, `UndoPlanRow` (`adds: { alias: string }[]`, nie `null` — 6.2 letzte Zeile gilt), `UndoSkippedRow`, `UndoSkipReason` | `shared/seven-tv/undo-plan.ts` (neu) | 6.2, 4.3, E5–E8, E20, E21, E23, AK 4, 29–31, 33, 35 | T2 | T4, T5, T6 |
| Engine-Hook je Versuch (Festlegung Nr. 4) | `RunOperation.beforeStep?(setId, emote, step): Observable<StepGate>`, `StepGate = { kind: 'proceed' } \| { kind: 'skip'; errorMessage: string }` | `core/seven-tv/seven-tv-run-engine.ts` | E19, 4.4 Nr. 10a, F13, AK 26, 27, 38 | T3 | T4 |
| Der Dienst | `SevenTvUndoService`, `UndoRunTarget` (6.5), `UndoRunInfo`, `UndoRunItem` (Engine-Zeile plus `mode`, `adds`, `provenance`, `omittedEntries`, `notes`, `undoStatus`, `skippedReason`, `sourceEntriesAtRemove`), `UndoRunResult` (`RunResult`-kompatibel, 0.2), `UndoSettlement`, `startUndo(target, runnable: UndoPlanRow[], skipped: UndoSkippedRow[], acknowledgedUnproven: boolean)` (Spec 17 K2 — 6.3 und 6.5 vereinheitlicht; die Drift-Zeilen des Frischchecks stehen in `skipped` mit `skippedDrift`; der Dienst prüft die Herkunftssperre selbst noch einmal), Signale `run`, `isRunning`, `isSettling`, `destructiveOpen`, `removalReport`/`removalReportReason`, `restoreReport`/`restoreReportReason`, `resyncTrigger`, `protocolSaved`, `noticePending`, Methoden `retryRemovalReport()`, `retryRestoreReport()`, `markProtocolSaved()`, `cancel()`, `reset()`, `resetIfChannelChanged()` | `core/seven-tv/seven-tv-undo.service.ts` (neu) | 6.5, 4.4–4.6, 4.9, E9–E11, E13–E16, E22–E24, F8, F19, F20, AK 9–17, 26, 27, 32, 34, 36–39 | T4 | T6, T7 |
| Flow | `startUndoFlow(deps: UndoFlowDeps, result)`, `undoRunTarget(target: ResolvedRestoreTarget): UndoRunTarget` | `shared/seven-tv/undo-flow.ts` (neu) | 6.3, 4.2 Nr. 5, E13, E14, E18, E22, AK 5, 8, 21 | T6 | T6 (Trigger) |
| Bestätigungsdialog | `UndoConfirmDialogData` (Kandidaten, Ziel, `sourceFile`, **`initialRead: SevenTvSetEntries \| null`** — `null` = Read des Flows gescheitert ⇒ Fehlerzustand, Spec 17 K3), `UndoConfirmOutcome { runnable: UndoPlanRow[]; skipped: UndoSkippedRow[]; acknowledgedUnproven: boolean; read: SevenTvSetEntries }` (effektiver Plan, explizit — Spec 17 K2), `openUndoConfirmDialog(dialog, data)` | `shared/seven-tv/undo-confirm-dialog.ts` (neu) | 6.3, 4.2 Nr. 6–9, 17 (K1–K3), E14, E17, F17, AK 6, 7, 28 | T5 | T6 |
| Dock-Abschnitt | `UndoProgressSection`, Selector `app-undo-progress-section`; `RunProgressPanel.labelPrefix += 'undo'` | `shared/seven-tv/undo-progress-section.ts` (neu), `run-progress-panel.ts` | 4.7, 6.6, E9, E23, AK 13, 17, 18 | T7 | Seite (T7) |
| Dritte Ergebnisart des Datei-Schritts | `FileImportResult` += `{ kind: 'transfer-undo'; candidates; target; sourceFile }` | `shared/seven-tv/file-import-step.ts` | 6.1, 4.1 Nr. 4, AK 1 | T6 | T6 (Trigger), Fixtures |
| Locale-Familien (Wortlaut #255) | `restore.import.choice.*` (Weiche), `restore.import.sorts.transferUndo`, `restore.import.errors.{transferUndo, transferUndoNoRows}` · `undo.confirm.*` · `undo.summary.*` · `undo.removalSync*`, `undo.restoreSync*`, `undo.resync.*`, `undo.settling`, `undo.progress*`, `undo.leaveWhileRunning.*`, `undo.errors.{removedButNotRestored, cancelledMidRow}` und die Übersprungen-Gründe · geteilt: `syncReportReason.*`, `massDelete.errors.*` (Engine) | `web/public/i18n/{de,en}.json` | 6.7 | der Task, der den Text erstmals zeigt (T1, T4, T5, T6, T7) | — |

### 0.6 Aufrufer-Inventur je geänderter Signatur (grep, 2026-09-25)

| Signatur / Typ, geändert durch | Aufrufer, typisierte Fixtures, Host-Imports | landet in |
|---|---|---|
| `ExportKind` — vierter Wert (T1) | `export-envelope.spec.ts`, `read-envelope.spec.ts` (nur, falls ein Fall die Sortenliste erschöpfend prüft); `import-source-parser.ts` (Abweisung); `file-import-step.ts` (`COPY_ENVELOPE_KINDS` bleibt) | T1 (Abweisung), T6 (Dispatch) |
| `FileImportResult` — dritte Art (T6) | `import-source-dialog.ts` (`dialogRef.close($event)`, Union `ImportSourceDialogResult`), `import-trigger.ts` (`result.kind`-Verzweigung), `file-import-step.spec.ts` (4 `transfer-run`-Fälle), `import-source-dialog.spec.ts`, `import-trigger.spec.ts` (6 `kind: 'restore'`-Fixtures — unverändert gültig, prüfen, dass die Verzweigung erschöpfend bleibt) | T6 |
| `RunOperation` — optionaler Hook (T3) | keine Aufruferänderung (optional); `seven-tv-run-engine.spec.ts` (+Fälle); Delete/Restore/Import setzen ihn nicht | T3 |
| `RunProgressPanel.labelPrefix` — vierter Wert (T7) | `import-progress-section.ts`, `restore-progress-section.ts`, `mass-delete-panel.ts` (Bestand, unverändert); `run-progress-panel.spec.ts` | T7 |
| `actionDockHasContent` — zwei neue Flags (T7) | `usage-stats-page.ts:1493`, `action-dock.spec.ts`, `usage-stats-page.spec.ts` | T7 |
| `usageStatsLeaveGuard` — zweiter Dienst (T7) | `usage-stats.routes.ts` (einziger Referent nach #264), `usage-stats-leave.guard.spec.ts` (Provider-Stub `{ isRunning }` für `SevenTvImportService` — der Undo bekommt denselben Stub), `usage-stats.routes.spec.ts` (bleibt grün) | T7 |
| Arbiter aus #256 — Registrierung `'undo'` (T4) | `seven-tv-run-arbiter.spec.ts` (+3 nach Spec 9.3), `import-trigger-gate.ts` (unverändert, sieht den Arbiter), `mass-delete-panel.ts` (Sperre über `activeRun()`, unverändert) — **die genauen Namen liefert T0** | T4 |
| `channel-workspace-layout.ts` — vierter `resetIfChannelChanged` (T7) | `channel-workspace-layout.spec.ts` (Provider-Stubs der Dienste) | T7 |
| `usage-stats-page.ts` — vierter `watchRunSettle`, `dockVisible` (T7) | `usage-stats-page.spec.ts` (Provider-Stubs: `SevenTvUndoService` muss dort gestubbt werden, sonst zieht der Test den echten Dienst samt Engine) | T7 |

---

## 1. Verträge — wo sie stehen, welcher Task sie trägt

| Vertrag | Spec | legt fest | konsumiert | Plan-Zusatz |
|---|---|---|---|---|
| Kandidatenwahl aus beiden Stufen, `provenance` je Stufe, `transferRunNoRows`; Abweisung `transfer-undo` als Undo-Eingabe (`wrongKind`) und als Import-Quelle (namentlich) | 6.1, E2, F7, F17, AK 3, 19, 23 | T1 | T2, T5, T6 | `UndoCandidate.provenance` (Festlegung Nr. 2) |
| Datei `transfer-undo` in zwei Stufen, Zeilenform, **Zeilenart „übersprungen" im `finished`** (ohne `mode`/`restoredTarget`), `counts.requested` nur gelaufene, `meta.undoneFile`, `acknowledgedUnproven` in `meta`, Dateinamen, CSV, Restore-Parser (Quellen unter ihrem Alias; übersprungene Zeilen ausdrücklich ausgelassen) | 6.4, 4.8, 17 (K2, K4), E3, E12, F6, F13, AK 6 (Dateiinhalt), 18, 19, 20 | T1 | T4, T5, T6, T7 | DECISIONS-Eintrag 2 in T1; K4 vor T1 entschieden (Codex-Befund 2) |
| Klassifikation nach Tabelle 4.3 (Reihenfolge ist Vertrag), Drift-Vergleich, Zusammenfassung mit Slot-Delta | 4.3, 6.2, E5–E8, E17, E20, E21, E23, AK 4, 29–31, 33, 35 | T2 | T4, T5, T6 | — |
| Prüfung vor **jedem** REMOVE-Versuch mit eigenem vollständigem Read, Read-Fehler fail-closed, drei in Folge ⇒ Rest `cancelled` | E19, 4.4 Nr. 10a, F13, AK 26, 27, 38 | T3 (Hook), T4 (Read + Entscheidung) | — | Hook je Versuch (Festlegung Nr. 4) |
| Lauf: Schrittfolge je Modus, `transportLossIsUnknown`, `abortOn`, `cancel()`-Semantik, Settling mit Nachlesen nach Tabelle 4.6, `partial` nur statt `done`, zwei Meldungen in Reihenfolge mit ID-Mengen, N4-Retry, kein Client-Resync, N1-Fallback nur bei zwei endgültigen Fehlschlägen | 4.4–4.6, 4.9, 6.5, E6, E9–E11, E13, E16, E23, E24, F8, F19, AK 9–14, 17, 34, 36, 37 | T4 | T7 | Kopie des Import-Musters mit Verweis (0.2) |
| Lauf-gebundener Abschluss, `isRunning`/`isSettling`/`destructiveOpen`, Registrierung als `'undo'`, keine eigene Sperre | 6.5, 11.1, E15, E22, F16, F20, AK 15, 16, 32, 39 | T4 | T6 (nur Arbiter), T7 (Guard) | Namen aus T0 |
| **Herkunftssperre doppelt:** ohne `acknowledgedUnproven` läuft keine `full`-Zeile mit `provenance: 'unproven'` — im Dialog (effektiver Plan) **und** im Dienst (vor dem Queue-Aufbau, Zeile ⇒ `skippedUnproven`, kein Engine-Row, kein REMOVE) | 17 (K2), 6.3, F17, AK 28 | T5 (Dialog), T4 (Dienst) | T6 (reicht durch), T8 (E2E) | Codex-Befund 1: der Test verfolgt eine gemischte `planned`-Datei bis in den Dienst (T6 Unit, T8 E2E) |
| Weiche im Datei-Schritt nach der Vorprüfung, nur für `transfer-run`; `transfer-undo` ⇒ Restore ohne Weiche; dritte Ergebnisart; Trigger-Verzweigung | 4.1, 6.1, E1, E4, F10, AK 1, 2, 22 | T6 | — | F10-Anpassungen im selben Commit |
| Flow: Arbiter → Token → **erster Read + Klassifikation** → (nichts Laufendes ⇒ Notiz, kein Dialog · Read-Fehler ⇒ Dialog im Fehlerzustand · sonst Dialog mit Read) → Arbiter → Frischcheck → Start; keine Settling-Prüfung im Flow | 4.2 Nr. 5, 6.3, 17 (K3), E13, E14, E18, E22, AK 5, 7, 8, 21, 32 | T6 | — | — |
| Dialog: Zustandsmaschine, Read als Eingabe (neu lesen nur bei „Ziel neu laden"), Kennzeichnung/Bestätigung `unproven` mit effektivem Plan, Rückweg-Datei nur mit `full`-Zeile, Slot-Warnung, Zielzeile, Fremd-Hinweis, Quelle mit Bild aus `flags.animated`, Ziel als Platzhalter | 4.2 Nr. 6–9, 6.3, 17 (K1–K3), E3, E14, E17, F17, AK 6, 7, 28 | T5 | T6 | — |
| Dock-Abschnitt mit den Zählern aus 4.7, zwei Meldungszeilen, `unknownRecordedIn`, Protokoll-Download ab `settled`; Seite: `dockVisible`, N2, `resetIfChannelChanged`; Guard mit eigener Textfamilie | 4.7, 6.6, 11.2, E9, E15, E23, AK 13, 15, 17, 18 | T7 | — | Panel/Section-Aufteilung (Festlegung Nr. 3) |
| Kein Import aus eagerly geladenen Dateien; Initial-Bundle < 500 kB | F9, 6.5 letzter Absatz | T4 (Dienst), T7 (Guard) | T9 (Gate) | Bundle-Baseline in T0 |
| E2E-Fälle aus 9.4 | 9.4, AK 1, 6, 9, 11, 12, 17, 18, 21, 26, 28–30, 35–38, 40 | T8 (neu), T6 (Anpassungen) | — | — |
| Doku: UI-Designsprache §7.3 (Weiche, fünfte Einlesesorte); Issues | 6.1 letzter Punkt, 14 | T8 (Doku), T10 (Issues) | — | — |

---

## 2. Nahtstellen

### 2.1 Kandidat ↔ Klassifikation ↔ Dialog

**Festlegend:** T1 (`UndoCandidate` mit `provenance`, `UndoSourceFileInfo`). **Konsumierend:** T2
(`classifyUndoRows(candidates, read)` liest `provenance` je Kandidat durch), T5 (zeigt die
Kennzeichnung), T6 (baut die dritte Ergebnisart). **Prüfung:** `undo-plan.spec.ts` (T2) baut
Kandidaten mit demselben Typ, den `transfer-run-export.spec.ts` (T1) aus einer Datei liest; die
Hauptsession vergleicht nach Welle 1, dass kein Feld aus Spec 6.1 fehlt und keins dazukam.

### 2.2 Engine-Hook ↔ Dienst

**Festlegend:** T3 (Hook je Versuch, `skip` ⇒ Zeile `cancelled` mit Text, kein Request, kein
`abortOn`, Takt unverändert; Hook-Fehler ⇒ wie `skip` mit generischem Text, fail-closed).
**Konsumierend:** T4 (der Read, die Neuklassifikation, der Zähler für aufeinanderfolgende
Read-Fehler, `skippedReason` je Zeile im eigenen Laufdatensatz). **Prüfung:**
`seven-tv-run-engine.spec.ts` (T3) belegt „ein Hook-Aufruf je Versuch, auch je Rate-Limit-
Wiederholung, und nie ein Request nach `skip`"; `seven-tv-undo.service.spec.ts` (T4) belegt AK 26,
27, 38 über den Hook hinweg. Ein Hook, der die Engine für Delete/Restore/Import verändert, ist ein
Planfehler — Regressionsschutz: deren drei Spec-Dateien laufen in T3 mit.

### 2.3 Dienst ↔ Arbiter aus #256

**Festlegend:** #256 Punkt 1 (Vertrag Spec 11.1) — **nicht dieser Plan**. **Konsumierend:** T4
(Registrierung, drei Signale), T6 (Startpunkt fragt nur den Arbiter, zeigt seinen Grund als
Notiz), T7 (Unload-Schutz ist bereits die Vereinigung; der Guard fragt `isRunning` beider Dienste).
**Prüfung:** T0 hält die tatsächlichen Namen fest; `seven-tv-run-arbiter.spec.ts` (+3, T4) belegt
AK 16/32 am echten Arbiter; `grep -rn "settlement\|isSettling" web/src/app/shared/seven-tv/undo-flow.ts web/src/app/shared/seven-tv/import-flow.ts`
ist leer (AK 32, Lesart in Festlegung Nr. 6).

### 2.4 Laufdatensatz ↔ Dock ↔ Seite

**Festlegend:** T4 (`UndoRunInfo` mit allen Zählern aus 4.7 als abgeleitete Werte oder Felder,
`result` `RunResult`-kompatibel, `settlement`, beide Meldungszustände je Lauf, `protocolSaved`).
**Konsumierend:** T7 (Section, `watchRunSettle`, `dockVisible`, Announcer). **Prüfung:**
`undo-progress-section.spec.ts` (T7) stubbt den Dienst mit genau den Signalen, die T4 exportiert;
`usage-stats-page.spec.ts` (+2) belegt Section und N2-Effekt. Die Hauptsession vergleicht nach
Welle 2 die Signal-Liste des Dienstes mit dem Brief für T7.

### 2.5 Dialog ↔ Flow ↔ Dienst

**Festlegend:** T5 (`UndoConfirmOutcome` = `runnable`, `skipped`, `acknowledgedUnproven`, `read`
— der effektive Plan, explizit, Spec 17 K2) und T4 (`startUndo(target, runnable, skipped,
acknowledgedUnproven)` mit der zweiten Herkunftssperre). **Konsumierend:** T6 (Frischcheck
vergleicht `runnable` gegen die frische Klassifikation, faltet Drift-Zeilen als `skippedDrift` in
`skipped`, ruft `startUndo`). **Prüfung:** `undo-flow.spec.ts` (T6) stubbt `openUndoConfirmDialog`
mit genau dem Rückgabetyp aus T5 **und** fährt einen Fall mit dem echten Dienst (gemischte
`planned`-Datei ohne Bestätigung ⇒ kein `removeEmote`); `undo-confirm-dialog.spec.ts` (T5) prüft
die Rückgabe; `seven-tv-undo.service.spec.ts` (T4) prüft die Sperre am Dienst. Welle 2 baut T5
gegen Spec 6.3/17, T4 gegen Spec 6.5/17 — die Naht schließt T6 in Welle 3; die Hauptsession prüft
vor Welle 3 die beiden Signaturen gegeneinander.

### 2.6 Datei-Grundsatz ↔ Restore

**Festlegend:** T1 (`parseTransferUndoForRestore` liefert `RestoreRow`s der Quellen, E12).
**Konsumierend:** T6 (Dispatch `transfer-undo` → Restore-Parser → Vorprüfung → `picked` mit
`kind: 'restore'`, ohne Weiche). **Prüfung:** AK 20 — der bestehende Restore-Filter (Regel 4) sagt
nach geglücktem Undo „Name belegt"; der Test in `restore-flow.spec.ts` (T6) belegt das mit einer
Undo-Datei, ohne den Filter zu ändern (F3, F5 a, F14 sind Restore-Fragen für #256).

---

## 3. Tasks

Jeder Task läuft als eigener Subagent mit frischem Kontext und bekommt diesen Abschnitt, 0.5, 0.6,
seine Spec-Abschnitte, die genannten Dateien und — ab T4 — den T0-Bericht mit den Arbiter-Namen.
„Fertig" je Task: die gezielten Gates des Tasks **plus** Formatter (`npm --prefix web run format`)
und `npm --prefix web run lint` grün; die vollen Gates aus `CLAUDE.md` fährt T9. Regel 12:
Verhalten ja, Vorlage nein — kein Test prüft Tailwind-Ketten oder einen Wortlaut als Selbstzweck;
ein Übersetzungsschlüssel darf eine Meldung identifizieren. Vitest-Gates laufen über
`npm --prefix web test -- --watch=false --include='<spec-pfad>'`. Jeder Task-Bericht nennt
Abweichungen von der Spec ausdrücklich und meldet jede Stelle, an der er eine Entscheidung aus
Abschnitt 7 / Spec 17 nicht wie festgelegt umsetzen konnte.

### T0 — Vorbedingungen: Epic-Stand, Vertrag 11.1, Bundle-Baseline

**Ziel:** Bevor ein Feature-Task startet, ist belegt, dass `feat/emote-sets-200` den Vertrag aus
Spec 11.1 erfüllt, der Branch auf diesem Stand steht (mit #264 und #256 Punkt 1), und die
Bundle-Baseline gemessen ist.

**Vertrag:** Spec 11.1 Punkte 1–6, 15 B, F9, F16, F20, AK 16, 24, 32 (Vertragshälfte); Issue #256
(Kommentar „Point 1 is now a precondition of #254").

**Schritte:**

1. `git fetch origin`; feststellen, ob #256 Punkt 1 in `origin/feat/emote-sets-200` gemergt ist
   (`git log --oneline 9e380cc4..origin/feat/emote-sets-200`, Diff an
   `core/seven-tv/seven-tv-run-arbiter.ts` und den drei Diensten). **Ist es nicht gemergt, endet
   der Task hier mit Befund** — kein Feature-Task startet gegen den alten Arbiter (Spec 15 B).
2. Den Vertrag Punkt für Punkt am Code prüfen und je Punkt Datei/Zeile und Testfall nennen:
   - (1) jeder Lauf-Dienst liefert `isRunning`, `isSettling`, `destructiveOpen`; `isSettling` deckt
     Nachlesen **und** offene Meldungen (Import: `settlement === 'pending'` allein reicht nicht).
   - (2) der Arbiter meldet „belegt" bei laufend **oder** settelnd und liefert einen Grund
     (mindestens `running | settling` je Dienst); jeder Startpunkt (Delete-Panel, Restore-Flow,
     Import-Flow) fragt nur den Arbiter und zeigt den Grund als transiente Notiz — welche Mechanik.
   - (3) der `beforeunload` ist die Vereinigung über alle Dienste und hängt an offenen Läufen, nicht
     an `run()`; wo er registriert ist (Dienst, Arbiter, App-Shell).
   - (4) ein vierter Dienst registriert sich an **einer** Stelle plus einem Union-Wert; den
     konkreten Mechanismus (Injection-Token, `registerRunService(...)`, Multi-Provider) benennen.
   - (5) der Arbiter-Spec hat die zwei Stub-Fälle (`isSettling: true` ⇒ Startpunkte weisen ab;
     `destructiveOpen: true` ⇒ Schutz scharf bei `run() === null`).
   - (6) `onRunComplete` in Import, Delete und Restore hat keinen Frühausstieg mehr; je Dienst die
     drei `reset()`-Fälle (`running`, `settling`, Meldung unterwegs) als Test; Retry öffnet den
     Lauf nicht wieder.
   Jeder nicht erfüllte Punkt ist ein Befund an den Betreiber, kein Reparaturauftrag dieses Plans.
3. `git merge origin/feat/emote-sets-200` in `feat/254-replace-undo` (Konflikte sind nur in
   `docs/` möglich; `docs/DECISIONS.md` beide Seiten behalten, neuere oben). Danach `npm --prefix
   web run build` und `npm --prefix web test -- --watch=false` auf dem gemergten Stand — beides
   grün ist die Freigabe für Welle 1.
4. Bundle-Baseline: aus der Build-Ausgabe die Zeile „Initial total" ins Ledger (Erwartung ≈ 404 kB,
   Budget-Warnung 500 kB laut `web/angular.json`); dazu `ng build --stats-json` und die Liste der
   Initial-Chunks, damit T9 den Vergleich hat.
5. Ins Ledger: die tatsächlichen Namen für T4/T6/T7 — Registrierung, Union-Typ, Grund-Signal,
   Notiz-Mechanik, Name des Schutz-Signals (`destructiveOpen` oder anders).

**Fertig-Kriterium:** Bericht mit sechs Punkten je „erfüllt (Datei:Zeile, Test)" oder „nicht
erfüllt (was fehlt)"; Merge-Commit auf dem Branch; Build und Vitest grün; Baseline im Ledger.
**Kein Feature-Commit.** **Abhängigkeiten:** #256 Punkt 1 gemergt. **Modell:** `opus` — ein
falsches „erfüllt" vergiftet T4, T6 und T7; die Prüfung ist Beurteilung, keine Mechanik.

### T1 — Dateiformate: die vierte Einlesesorte, der Undo-Parser der Übertragungsdatei, die Abweisungen

**Ziel:** Alles, was auf der Platte liegt oder von ihr kommt: `parseTransferRunForUndo` liest
Kandidaten aus beiden Stufen; `transfer-undo-export.ts` baut beide Stufen der Undo-Datei, JSON und
CSV, Dateinamen, und liest die Datei für den Restore; `parseImportSource` weist `transfer-undo`
namentlich ab, der Undo-Parser mit `wrongKind`.

**Vertrag:** Spec 6.1 (Kandidat, Parser), 6.4 (vollständig), **17 K4** (Zeilenart „übersprungen",
`counts.requested`, Restore-Parser lässt sie aus) und **17 K2** (`acknowledgedUnproven` in `meta`),
4.8, E2, E3, E12, E21 (Datei-`D`), F6, F7, F13 (`removedSource.entries` = letzter Read), F17
(`provenance`), AK 3, 18 (Form), 19, 23; DECISIONS-Eintrag 2 (Spec 14) in diesem Commit. K4 ist
**vor** diesem Task entschieden (Codex-Befund 2) — der Builder kennt die Zeilenart von Anfang an.

**Dateien:** `web/src/app/shared/export/export-envelope.ts` (`ExportKind`);
`web/src/app/shared/export/transfer-run-export.ts` (`UndoCandidate` mit `provenance`,
`UndoSourceFileInfo`, `parseTransferRunForUndo` — dieselbe Leiter wie der Restore-Parser, die
Kandidatenwahl nach E2; `readEnvelope` bleibt unberührt) + `.spec.ts`;
`web/src/app/shared/export/transfer-undo-export.ts` (neu, + `.spec.ts`: Typen — `TransferUndoRow`
als Union aus `TransferUndoExecutedRow` (Form aus 6.4, `kind: 'executed'`) und
`TransferUndoSkippedRow` (Spec 17 K4: `kind: 'skipped'`, Quell-ID, `sourceName`, `alias`, Ziel-ID,
`provenance`, `skippedReason`, sonst nichts) —, beide Builder (der `finished`-Builder nimmt gelaufene
Zeilen **und** übersprungene Kandidaten; `counts.requested` = gelaufene, `counts.skipped` =
übersprungene; `meta.acknowledgedUnproven`), `transferUndoJson`, `transferUndoCsv` mit den Spalten
aus 6.4 plus `kind` und `skipped_reason`, beide Dateinamen mit `<kanal-oder-setid>`,
`parseTransferUndoForRestore` — lässt `kind: 'skipped'` ausdrücklich aus); `web/src/app/shared/export/import-source-parser.ts`
(Abweisung `transfer-undo`, Muster `transfer-run`) + `.spec.ts`; `web/public/i18n/{de,en}.json`
(`restore.import.sorts.transferUndo`, `restore.import.errors.{transferUndo, transferUndoNoRows}`);
`docs/DECISIONS.md` (Eintrag 2).

**Grenzfälle (alle als Testfall):** `planned` ⇒ jede Replace-Zeile, `provenance: 'unproven'`, auch
Zeilen ohne `confirmed` · `finished` ⇒ nur `removedTarget.confirmed === true`, `'confirmed'`;
`unknown` mit `confirmed: true` bleibt Kandidat; `failed@0` fällt; ADD-, Rename-, Adopt-Zeilen fallen
· `defaultName` `null`/leer wird als `null` durchgereicht (die Klassifikation entscheidet, E21) ·
`entries` leer, `aliases` gefüllt ⇒ dieselbe Rückfalllogik wie der Restore-Parser (`readEntryAliases`)
· keine Kandidaten ⇒ `transferRunNoRows` · `transfer-undo` als Undo-Eingabe ⇒ `wrongKind` ·
Undo-Datei `planned` aus Plan + Read: nur laufende Zeilen, `removedSource.entries = [{ alias }]` aus
dem Read, `addOnly` ⇒ `removedSource: null`, `restoredTarget.entries[].alias` nie `null` (E21),
`added: false`, `status: 'pending'`, `counts { planned, removals, additions }`, `meta.undoneFile`
mit Stufe/Zeitstempel/`origin` der Übertragungsdatei · Undo-Datei `finished` aus einem gemischten
Lauf (`done`, `partial`, `failed@1`, `unknown`, `cancelled` mit `skippedReason`): alle Zeilen
ungefiltert, `confirmed = completedSteps >= 1` nur bei `full`, `added` je Eintrag aus
`completedSteps`, `removedSource.entries` = die Quell-Einträge des letzten Reads vor dem REMOVE
(F13), `omittedEntries`/`notes` orthogonal zum Status (E23), `counts { requested, succeeded,
failed, cancelled, unknown, removed, added, skipped }` · **übersprungene Kandidaten im `finished`
(Spec 17 K4):** je ein `kind: 'skipped'` mit Grund — `duplicateInFile` (Schritt 0, vor jeder
Klassifikation), `nothingToDo`, `sourceUnderOtherName`, `targetNameTaken` (Dialog), `skippedDrift`
(Frischcheck), `skippedUnproven` (Dialog **oder** Dienst) —, ohne `mode`, `restoredTarget`,
`removedSource`, `status`, `completedSteps`; `counts.requested` zählt sie nicht, `counts.skipped`
schon; **im Lauf** übersprungene Zeilen (Hook, `skippedDrift`/`recheckUnavailable`) bleiben
`kind: 'executed'` mit `status: 'cancelled'` und `skippedReason` (6.4 unverändert) · eine Zeile
ohne `kind` ⇒ `wrongKind` (die Sorte ist neu, es gibt keinen Altbestand) · Duplikat-Zelle mit
drei Einträgen (zwei Aliase + `null` ⇒ `D`) · CSV-Spalten und `|`-Trennung, `null` als leer;
`kind`/`skipped_reason` gefüllt, übrige Spalten einer übersprungenen Zeile leer · Dateinamen
getrackt (Kanal) und ungetrackt (Set-ID) · `parseTransferUndoForRestore`: `planned` ⇒ jede
`full`-Zeile, `finished` ⇒ nur `removedSource.confirmed`, **`kind: 'skipped'` nie** (auch mit
manipuliertem `removedSource` daneben), je Zeile `RestoreRow { emoteId: null, sevenTvEmoteId: S,
name: sourceName, aliases: [alias], defaultName: null }`; keine Zeile ⇒ `transferUndoNoRows`;
falsche `formatVersion` ⇒ `wrongVersion` · bestehende `parseTransferRunForRestore`-Fälle **0
geändert** (AK 23).

**Tests:** `transfer-run-export.spec.ts` **+6** (Spec 9.2 nennt +5; dazu `provenance` je Stufe),
`transfer-undo-export.spec.ts` (neu) **+16** (Spec 9.2 nennt +10; dazu `wrongVersion`, die
übersprungene Zeilenart mit `duplicateInFile` und je einem Dialog-, Frischcheck- und Dienst-Grund,
`counts.requested`/`counts.skipped`, Restore-Parser lässt sie aus, Zeile ohne `kind`),
`import-source-parser.spec.ts` **+1**, `export-envelope.spec.ts` **+1**, falls dort die Sortenliste
geprüft wird (sonst 0 — im Bericht sagen).

**Abnahme:** `grep -rn "'transfer-undo'" web/src` findet die Sorte in Envelope, beiden Parsern und
der Abweisung — nirgends sonst (Dispatch kommt in T6); DECISIONS-Eintrag 2 steht oben und nennt in
`Betrifft:` auch `file-import-step.ts` (T6) und `undo-confirm-dialog.ts` (T5). **AK 3, 19, 23;
AK 18 (Form).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/export/*.spec.ts'`;
`npm --prefix web run build`; Lint, Format.

**Commit:** `feat(export): read transfer files as undo candidates and add the transfer-undo file`.
**Abhängigkeiten:** T0. **Modell:** `sonnet` — Spec 6.4 ist bis auf die Spalte vollständig; das
Risiko ist Vollständigkeit, und die trägt die Fall-Liste.

### T2 — Reine Klassifikation: `undo-plan.ts`

**Ziel:** Die eine Funktion, die aus Kandidaten und einem Live-Read entscheidet, was der Undo tut —
je Zeile `full`, `addOnly` oder übersprungen mit Grund, mit Normalisierung vor jeder Zielprüfung,
Alles-oder-nichts für `full`, eintragsweise für `addOnly` — plus Drift-Vergleich und
Zusammenfassung. Kein Angular, kein DOM, kein Request.

**Vertrag:** Spec 4.3 (die Tabelle ist der Vertrag, Reihenfolge inklusive), 6.2, E5–E8, E17
(Slot-Delta), E20, E21, E23, F2, F3, F5, F11, F18, AK 4, 8 (Vergleichshälfte), 29–31 (Logikhälfte),
33, 35.

**Dateien (neu):** `web/src/app/shared/seven-tv/undo-plan.ts` + `.spec.ts`. Importiert
`UndoCandidate` aus `transfer-run-export.ts` (T1) und `SevenTvSetEntries` aus
`core/seven-tv/seven-tv-set-entries.ts`; ändert keine bestehende Datei (`already-present-filter.ts`
bleibt unberührt, F3).

**Grenzfälle (alle als Testfall — je Tabellenzeile aus 4.3 mindestens einer):** Schritt 0
Datei-Doppel (gleiche Quell-ID; gleiche Ziel-ID) ⇒ beide `duplicateInFile` · Schritt 1: exakt
`{ A }` ⇒ REMOVE; leer ⇒ keiner; zweiter Alias ⇒ `sourceHasMoreEntries`; aliasloser Eintrag der
Quelle ⇒ `sourceHasMoreEntries`; anderer Name ⇒ `sourceUnderOtherName`; Groß-/Kleinschreibung ⇒
`sourceUnderOtherName` (ordinal, E5); in allen Übersprungen-Fällen `live.sourceEntries` gefüllt
und **keine** Zielprüfung · Schritt 2: `null`-Eintrag mit `D` ⇒ `N ∋ D`; `null` ohne `D` ⇒ bei
`full` ganze Zeile `targetNameUnverifiable`, bei `addOnly` nur der Eintrag in `omittedEntries`;
`normalizeTargetEntries` für sich (AK 35) · Schritt 3: fremder benannter Alias auf T ⇒ `full`
übersprungen `targetHasForeignEntries`, `addOnly` läuft mit `notes` (AK 29); aliasloser
Live-Eintrag auf T, `E ∌ null` ⇒ fremd; `E ∋ null` ⇒ eigen (F5) · Schritt 4: vorhanden über
`aliasesById` **oder** — nur für `null`-`e` — `aliaslessIds` ⇒ `alreadyPresent` gezählt je Eintrag;
zurückgeholtes `D` ist `alreadyPresent`, nicht fremd (AK 35) · Schritt 5: Name frei ⇒ ADD mit
explizitem Alias; Name bei S ⇒ ADD (REMOVE gibt frei); Name bei dritter ID ⇒ `full` ganze Zeile
`targetNameTaken`, `addOnly` Eintrag in `omittedEntries` (AK 33) · Schritt 6: REMOVE + ≥ 1 ADD ⇒
`full`, `stepCount = 1 + ADDs`; REMOVE + 0 ADDs ⇒ `inconsistent` (Wächter, F11 — mit einem
konstruierten Read erreichbar machen); keiner + ≥ 1 ⇒ `addOnly`; keiner + 0 ⇒ `nothingToDo` ·
`provenance` je Kandidat durchgereicht · Duplikat-Zelle (zwei Aliase + `null`) ⇒ drei ADDs, Delta
+2 · jeder ADD trägt einen nicht-leeren Alias (AK 31) · `diffUndoPlans`: gleicher Modus + gleiche
ADD-Liste (Namen **und** Reihenfolge) ⇒ `runnable`; Moduswechsel, ADD-Liste geändert, Zeile jetzt
übersprungen ⇒ `drifted`; `notes` ohne Einfluss auf `sameClassification` · `summarizeUndoPlan`:
`removeCount` = `full`-Zeilen, `addCount` = alle ADDs, `slotDelta = addCount − removeCount`,
`omittedEntryCount`, `foreignNotedRows` · die Codex-Gegenbeispiele: nie gestartete `planned`-Datei
mit nachgebildetem Stand ⇒ `full` mit `provenance: 'unproven'` (AK 28, Logikhälfte); Zeile nach
Teilerfolg (S weg, A da, B fehlt) ⇒ `addOnly [B]` (AK 30); nach Teilerfolg mit fremdem `C` ⇒
`addOnly [B]` mit `notes` (AK 36, Logikhälfte); `C` hält B, S weg, A vorhanden ⇒ 0 ADDs und ein
ausgelassener Eintrag ⇒ übersprungen `nothingToDo`, die Auslassung sichtbar im Übersprungen-Grund
(Festlegung Nr. 7) — der Task belegt genau diese Lesart und meldet sie im Bericht.

**Tests:** `undo-plan.spec.ts` (neu) **≥ 40 Fälle** (die Liste oben; die Tabellenzeilen als
`it.each` über beide Modi, wo die Spec beide nennt).

**Abnahme:** Modul pur (keine Angular-Importe); jede Zeile der Tabelle 4.3 hat einen Fall mit
Verweis auf ihre Schrittnummer im Testnamen; `grep -n "aliasesById\|aliaslessIds\|defaultNameById"
undo-plan.ts` zeigt, dass `defaultNameById` **nicht** für `D` gelesen wird (E21: `D` kommt aus der
Datei). **AK 4, 35; Logikhälften von AK 8, 28–31, 33, 36.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/undo-plan.spec.ts'`;
`npm --prefix web run build`; Lint, Format.

**Commit:** `feat(undo): classify undo candidates against the live set, fail-closed for removals`.
**Abhängigkeiten:** T1 (`UndoCandidate`). **Modell:** `opus` — die Funktion autorisiert jedes
REMOVE; ein Fehler löscht Emotes, die keine Datei kennt (F2). Fable ist hier bewusst nicht der
Implementer (0.3): der Testvertrag ist die Tabelle, und T9/T10 sind die Checkpoints.

### T3 — Engine: ein optionaler Hook vor jedem Versuch eines Schritts

**Ziel:** `SevenTvRunEngine` kann vor **jedem** Versuch eines Schritts — auch vor einer
Rate-Limit-Wiederholung desselben Schritts — eine asynchrone Vorbedingung der Operation fragen und
die Zeile ohne Request beenden, wenn sie `skip` sagt. Delete, Restore und Import bleiben
byte-identisch, weil sie den Hook nicht setzen.

**Vertrag:** Spec E19, 4.4 Nr. 10a, F13, AK 26 (Engine-Hälfte: ein Hook-Aufruf je Versuch, nie
null), 27 (Zeile läuft nicht), 38 (ein Read je REMOVE — die Engine belegt „ein Hook je Versuch",
T4 belegt „ein Read je Hook"); Festlegung Nr. 4 (Abschnitt 6).

**Dateien:** `web/src/app/core/seven-tv/seven-tv-run-engine.ts` (`RunOperation.beforeStep?`,
`StepGate`; Aufruf in `runWithBackoff` vor jedem Versuch, Ergebnis `skip` ⇒ Zeile `cancelled` mit
`errorMessage`, `completedSteps` unverändert, kein Request, kein `abortOn`, kein `unknown`; die
Engine paust danach wie nach jeder Zeile; ein Hook, der wirft oder mit Fehler endet, zählt als
`skip` mit generischem Text — fail-closed —, geloggt wie ein werfender `abortOn`-Hook) + `.spec.ts`.

**Grenzfälle (alle als Testfall):** Hook `proceed` ⇒ Request wie heute · Hook `skip` beim ersten
Schritt ⇒ Zeile `cancelled`, kein Request, nächste Zeile läuft nach `RUN_DELAY_MS` · Hook `skip` bei
Schritt ≥ 1 einer mehrschrittigen Zeile ⇒ `cancelled`, `completedSteps` bleibt (die Zeile ist damit
eine benannte Lücke — Spec 4.4 Nr. 11 gilt für `cancel()`, nicht für `skip`; im Bericht nennen,
falls T4 diesen Fall braucht) · 429 auf dem Versuch, Wiederholung ⇒ **zweiter** Hook-Aufruf vor der
Wiederholung (AK 26 letzter Satz) · Hook wirft ⇒ `skip`, `console.error`, Lauf geht weiter · Hook
endet mit Observable-Fehler ⇒ dito · `cancel()` während der Hook läuft ⇒ Zeile `cancelled`, der
verspätete Hook-Wert ändert nichts · Operation ohne Hook ⇒ `runWithBackoff` sendet ohne Umweg
(Regressionsschutz: die drei Dienst-Specs laufen mit).

**Tests:** `seven-tv-run-engine.spec.ts` **+8**; `seven-tv-delete.service.spec.ts`,
`seven-tv-restore.service.spec.ts`, `seven-tv-import.service.spec.ts` **0 geändert, alle grün**.

**Abnahme:** `git diff --stat` berührt nur die Engine und ihren Spec; `grep -n "beforeStep"
web/src/app/core/seven-tv/seven-tv-*.service.ts` leer. **AK 26 (Engine), 27 (Engine), 38 (Engine).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/core/seven-tv/**/*.spec.ts'`;
`npm --prefix web run build`; Lint, Format.

**Commit:** `feat(seventv): let a run operation gate every step attempt before its request`.
**Abhängigkeiten:** T0 (nur derselbe Stand; kein Symbol aus T1/T2). **Modell:** `sonnet` — ein
optionaler Hook in einer gut getesteten Klasse; das Risiko ist die Wiederholungs-Semantik, und die
ist als Fall benannt.

### T4 — `SevenTvUndoService`: der vierte destruktive Lauf

**Ziel:** Der Dienst führt einen gestempelten Undo-Plan aus — je `full`-Zeile REMOVE dann ADDs,
je `addOnly`-Zeile nur ADDs, vor jedem REMOVE-Versuch ein eigener vollständiger Read und die
Neuklassifikation genau dieser Zeile —, settelt mit Nachlesen nach Tabelle 4.6, setzt `partial`
nur statt `done`, meldet zuerst `sync-deleted` dann `sync-restored`, stößt keinen Client-Resync an
außer dem N1-Fallback, registriert sich beim Arbiter aus #256 als `'undo'`, schließt jeden Lauf
lauf-gebunden, und bietet dem Dock und der Seite alle Zähler aus 4.7.

**Vertrag:** Spec 4.4–4.6, 4.9 Nr. 23–24, 6.5, **17 K2** (Signatur; zweite Herkunftssperre im
Dienst) und **17 K4** (übersprungene Kandidaten im Laufdatensatz für das Protokoll), 11.1
(Nutzungshälfte), E6, E9–E11, E13–E16, E19, E22–E24, F8, F13, F19, F20, AK 9–17, 26, 27, 28
(Diensthälfte), 32, 34, 36–39; DECISIONS-Eintrag 1 (Spec 14) in diesem Commit.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-undo.service.ts` (neu, + `.spec.ts`; Bausteine
aus 0.5: `UndoRunTarget` ohne `resyncChannelName`, `UndoRunInfo` (trägt `skipped` und
`acknowledgedUnproven` für das Protokoll), `UndoRunItem`, `UndoRunResult`;
`startUndo(target, runnable, skipped, acknowledgedUnproven)`; **zweite Herkunftssperre (Spec 17
K2, Codex-Befund 1):** vor dem Aufbau der Queue fällt jede `full`-Zeile mit `provenance:
'unproven'` heraus, wenn `acknowledgedUnproven` falsch ist — sie wandert als `skippedUnproven` in
`skipped` des Laufdatensatzes, es entsteht kein Engine-Row und kein REMOVE, gleich was der Dialog
geliefert hat; Operation mit `stepCount` je Modus, `buildRequest` mit `REMOVE_EMOTE_MUTATION` aus dem Delete-Dienst
und `ADD_EMOTE_MUTATION` **immer** mit `$alias` (E21), `transportLossIsUnknown: true`, `abortOn`
wie der Import, `beforeStep` aus T3 für Schritt 0 jeder `full`-Zeile: `loadSevenTvSetEntries`
(tokenlos, kein Wiederverwenden, kein Cache), `classifyUndoRow` + `sameClassification` gegen die
gestempelte Zeile, Zähler aufeinanderfolgender Read-Fehler, ab drei ⇒ jede weitere `full`-Zeile
`skip` mit `recheckUnavailable`, `addOnly`-Zeilen ohne Hook; `sourceEntriesAtRemove` je Zeile
(F13); Settling wie `onRunComplete`/`settleRunResult` des Imports, **ohne** den Frühausstieg (F20),
Nachlesen nach Operation des Schritts (E24), `partial` nur für sonst `done`-`addOnly`-Zeilen mit
`omittedEntries` (F19); `sendFollowUp` mit zwei Meldungen in Reihenfolge, je Meldung
`SyncReportState` + Grund über `classifySyncInSetResponse`/`classifySyncInSetFailure`, Retries wie
der Restore, N4-Retry-Methoden, die `channelMismatch` abweisen; `backendTriggered`, sobald eine
Antwort den Kanal nennt; N1-Fallback nur, wenn **beide** Meldungen endgültig `failed` und
`expectedChannelName !== null`; Satz offener Läufe für `isSettling`/`destructiveOpen`; `reset()`
und `resetIfChannelChanged()` lösen nur die Anzeige; transiente Notiz nach dem Muster
`showDuplicateNotice`; Protokollgatter `protocolSaved`); `web/src/app/core/seven-tv/seven-tv-run-arbiter.ts`
(Registrierung `'undo'` nach dem Mechanismus aus T0 — eine Stelle, ein Union-Wert) + `.spec.ts`
(+3); `web/public/i18n/{de,en}.json` (`undo.errors.{removedButNotRestored, cancelledMidRow}`, die
Übersprungen-Gründe für `skip`-Texte, `undo.settling`); `docs/DECISIONS.md` (Eintrag 1).

**Grenzfälle (alle als Spec-Fall):** Schrittfolge `full` ⇒ REMOVE, ADD…; `addOnly` ⇒ ADD…; nie ein
ADD vor dem REMOVE derselben Zeile (AK 9) · **gemischter Plan** (eine `full`-Zeile mit
`provenance: 'unproven'`, eine `addOnly`-Zeile) mit `acknowledgedUnproven: false` ⇒ genau die ADDs
der `addOnly`-Zeile, **kein** `removeEmote`, die `full`-Zeile im Laufdatensatz als
`skippedUnproven`, das Protokoll führt sie als `kind: 'skipped'`; dieselbe Eingabe mit `true` ⇒
REMOVE läuft; eine `full`-Zeile mit `provenance: 'confirmed'` läuft ohne Häkchen (AK 28,
Diensthälfte) · HTTP 0 / 503 ⇒ `unknown`; 401/403/`LACKING_PRIVILEGES`
⇒ Abbruch, Token gelöscht, Rest `cancelled`; GraphQL-Ablehnung (409) ⇒ `failed` (AK 10) · ein Read
je REMOVE-Versuch, gezählt am `query`-Text; Drift der zweiten Quelle 275 ms nach dem ersten REMOVE
⇒ zweiter Read, `skippedDrift`, kein REMOVE, kein ADD, dritte Zeile läuft (AK 26); 429 auf dem REMOVE
⇒ neuer Read vor der Wiederholung (AK 26); Read-Fehler ⇒ `recheckUnavailable`; drei in Folge ⇒ Rest
`full` `cancelled`, `addOnly` läuft (AK 27); N `full`-Zeilen ⇒ genau N Reads zwischen Start und
Ende (AK 38) · Nachlesen: REMOVE `unknown` + Quelle weg ⇒ `failed@1` `removedButNotRestored`, S in
Meldung 13; Quelle `{ A }` ⇒ `failed@0`, nicht gemeldet; ADD `unknown` + `n` auf T ⇒ Schritt
bestätigt, Folge-ADDs nie gelaufen ⇒ `failed@(k+1)` oder `done`/`partial`; `addOnly` an Schritt 0
`unknown` ⇒ als ADD geklärt, nie in `sync-deleted` (AK 11, 34); unlesbar ⇒ `unknown` bleibt;
`completedSteps` nie gesenkt · `partial` nur statt `done`; `unknown`/`failed` mit `omittedEntries`
bleiben und zählen in `unknownCount`/`failed` **und** `omittedEntryCount`, nie in `partialRows`
(AK 33, 37) · `addOnly` neben fremdem `C` nach mittendrin gescheiterter `full`-Zeile ⇒ genau
`addEmote(T, B)`, `notes` (AK 36) · Meldungen: `sync-deleted` = Quell-IDs aller `full` mit
`completedSteps >= 1` (auch bei späterem `unknown`), dann `sync-restored` = Ziel-IDs mit ≥ 1
bestätigtem ADD (`full` ≥ 2, `addOnly` ≥ 1), dedupliziert, `expectedChannelName` nach #253, leere
Liste ⇒ kein Request; kein Request an eine kanalgebundene Route (AK 12) · Dreiwertigkeit mit Grund
je Meldung; Retry bei `failed` und `partial`/`shortfall`, abgewiesen bei `channelMismatch` (AK 13) ·
kein `POST /resync` im Erfolgsfall für aktives, nicht-aktives, ungetracktes Ziel; `backendTriggered`
aus der ersten **oder** zweiten Antwort; N1-Fallback genau einmal, nur wenn beide endgültig scheitern
und `expectedChannelName !== null`; scheitert nur eine ⇒ keiner (AK 14) · `destructiveOpen` wahr
solange ein Lauf mit `full`-Zeile läuft, settelt oder eine Meldung offen ist — auch nach `reset()`
und nach einem neuen Lauf; reiner `addOnly`-Lauf ⇒ nie (AK 15) · `reset()` während `running`,
`settling`, Meldung unterwegs ⇒ beide Meldungen gehen genau einmal raus, Endzustand im Laufdatensatz,
erst dann fallen `isSettling`/`destructiveOpen` (AK 39) · `cancel()` zwischen REMOVE und erstem ADD
⇒ `failed@1` `cancelledMidRow`, gemeldet, Rest `cancelled`; `cancel()` während ein Request unterwegs
ist ⇒ `unknown` (4.4 Nr. 11) · `resetIfChannelChanged` mit fremdem Kanal ⇒ fertiger Lauf weg,
laufender bleibt, offene Meldungen laufen weiter · ungetracktes Ziel ⇒ beide Meldungen mit
`expectedChannelName: null`, keine Resync-Zeile (AK 22, Diensthälfte) · Arbiter: Undo läuft ⇒
belegt; Undo settelt ⇒ belegt; Import settelt ⇒ Undo-Start abgewiesen (AK 16, 32).

**Tests:** `seven-tv-undo.service.spec.ts` (neu) **≥ 48 Fälle** nach Spec 9.3 (die Liste oben,
die drei Herkunftssperre-Fälle eingeschlossen; Muster 0.2: `HttpTestingController`,
`vi.useFakeTimers()`, `advanceTimersByTime(RUN_DELAY_MS)`); `seven-tv-run-arbiter.spec.ts` **+3**.

**Abnahme:** `grep -n "this.run() !== started" seven-tv-undo.service.ts` leer (F20); `grep -rn
"seven-tv-undo.service" web/src/app --include=*.ts -l` nennt nur `core/seven-tv/` und Specs (T6/T7
kommen später, F9); jeder `addEmote`-Aufruf im Spec trägt einen nicht-leeren `alias` (AK 31,
Diensthälfte); die Herkunftssperre steht im Dienst, nicht nur im Dialog (`grep -n "unproven"
seven-tv-undo.service.ts` trifft vor dem Queue-Aufbau); DECISIONS-Eintrag 1 steht oben und nennt in
`Betrifft:` die Dateien aus T5–T7. **AK 9–17, 26, 27, 32, 34, 36–39; Diensthälften von AK 22, 28,
31.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/core/seven-tv/**/*.spec.ts'`;
`npm --prefix web run build`; Lint, Format.

**Commit:** `feat(undo): run a replace undo with a fresh read before every removal and two reports`.
**Abhängigkeiten:** T2 (Klassifikation), T3 (Hook), T0 (Arbiter-Namen). **Modell:** `opus` —
der Dienst entscheidet, was gelöscht und was gemeldet wird; jeder Fehler ist still.

### T5 — `UndoConfirmDialog`: Live-Read, Klassifikation sichtbar, Rückweg-Datei, Herkunfts-Bestätigung

**Ziel:** Der Bestätigungsdialog zeigt je Kandidat Quelle (mit Bild) und Ziel (Platzhalter mit
Name und Aliasen) nebeneinander mit Klassifikation und Live-Gegenstück, die Zusammenfassung mit
Löschzahl und Slot-Warnung, die Zielzeile und den Fremd-Hinweis; hält die Zustandsmaschine `idle
→ verifying → saved` auf dem Read, den der Flow mitgibt, und liest nur bei „Ziel neu laden" neu;
lädt die Rückweg-Datei aus dem Read; sperrt ohne Datei; kennzeichnet unbelegte `full`-Zeilen und
verlangt die Datei-Bestätigung, wobei die Aktionszeile dem effektiven Plan folgt; gibt laufende
und übersprungene Zeilen explizit zurück.

**Vertrag:** Spec 4.2 Nr. 6–9, 6.3, **17 K1** (Bilder), **17 K2** (effektiver Plan, explizites
Ergebnis), **17 K3** (Read als Eingabe), E3, E14 (Dialog-Read = der Read des Flows), E17, F17,
AK 6, 7, 28 (Dialoghälfte); #253 4.3 Nr. 8 (Slot-Gabel), 4.2 Nr. 7 (Zielzeile, „nicht aktiv",
Fremd-Hinweis nach #253 E21).

**Dateien:** `web/src/app/shared/seven-tv/undo-confirm-dialog.ts` (neu, + `.spec.ts`;
`app-dialog-panel-wide` wie der Auflösungsschritt; `UndoConfirmDialogData.initialRead` — `null`
⇒ Fehlerzustand mit Banner „Ziel neu laden", sonst sofort klassifiziert (K3); Zustandsmaschine nach
dem Muster `import-confirm-dialog.ts:139-154, :977-992, :1204-1279` — R15-Disziplin: nur der
neueste Read zählt; „Ziel neu laden" liest neu und setzt auf `idle`; Klassifikation über
`classifyUndoRows`; **effektiver Plan (K2):** ohne Häkchen sind unbelegte `full`-Zeilen
`skippedUnproven` (sichtbar, mit Grund), die Aktionszeile folgt dem Rest; „Rückweg sichern" nur bei
≥ 1 effektiver `full`-Zeile, baut `buildTransferUndoPlanRecord` aus dem Read und lädt per
`downloadFile`; Download-Fehler ⇒ `idle` mit Notiz; ohne effektive `full`-Zeile direkt „Starten";
keine laufende Zeile ⇒ Aktionszeile gesperrt mit Grund; Rückgabe `{ runnable, skipped,
acknowledgedUnproven, read }`; Slot-Vorschau über `loadRestoreSlotPreview` mit `projectSlots`,
Überschreitung als Warnung; **Bilder (K1):** Quelle über `emoteStillUrl(S, read.animatedById.get(S)
?? false)`, Ziel als Platzhalter des Auflösungsschritts (`targetImageUrl: null`-Idiom) mit Name
und Aliasen, keine weitere 7TV-Anfrage); die Zustandsmaschine wird **nicht** aus dem Import-Dialog
extrahiert (11.3: #256 Punkt 5 ist nicht gelandet — minimal eigen, mit Verweis auf
`import-confirm-dialog.ts` und #256 in der Klassendoku);
`web/src/app/core/seven-tv/seven-tv-set-entries.ts` (`GQL_EMOTE_SET_ENTRIES_QUERY` += `flags {
animated }` — das Feld der Vorschau-Query in `SevenTvApiClient.cs:71`; `animatedById`; fehlendes
Flag ⇒ `false`) + `.spec.ts`; `web/src/app/shared/emotes/emote-url.ts` (`emoteStillUrl` — bytegleich
mit `BuildForeignImageUrl`, `SevenTvApiClient.cs:1272-1280`; die Doku nennt die Regel, die
404-Messung vom 2026-09-09 und dass das Verbot aus Plan-230 T1 der Ableitung ohne Flag galt) +
`.spec.ts`; `web/public/i18n/{de,en}.json` (`undo.confirm.*`).

**Grenzfälle (alle als Spec-Fall, Verhalten statt Vorlage):** `initialRead` gesetzt ⇒ sofort
klassifiziert, kein eigener Request (K3) · `initialRead: null` ⇒ Banner mit „Ziel neu laden",
nichts freigegeben (AK 7); Neuladen ⇒ genau ein `loadSevenTvSetEntries` · Neuladen scheitert /
`complete: false` ⇒ Banner, nichts frei · Read ok, ≥ 1 `full` ⇒ „Rückweg sichern"; Klick ⇒
Download mit genau den laufenden Zeilen, `removedSource.entries` aus dem Read; danach „Starten"
(AK 6) · Neuladen nach `saved` ⇒ `idle` (AK 6) · nur `addOnly` ⇒ direkt „Starten", keine Datei
(AK 6) · Download wirft ⇒ `idle` mit Notiz · **`planned`-Datei, gemischt (K2):** unbelegte
`full`-Zeilen gekennzeichnet, Checkbox; ohne Häkchen sind sie `skippedUnproven` und die
Aktionszeile zeigt „Starten" für die `addOnly`-Zeilen; ohne Häkchen und ohne `addOnly` ⇒
Aktionszeile gesperrt mit Grund; mit Häkchen ⇒ `full`, „Rückweg sichern" (AK 28) ·
`finished`-Datei ⇒ weder Kennzeichnung noch Checkbox, `acknowledgedUnproven: false` in der
Rückgabe ohne Wirkung · Löschzahl = Anzahl effektiver `full`; ADD-Zahl; Slot-Delta für Einzel- und
Duplikat-Zelle; Überschreitung ⇒ Warnung, kein Sperren (E17) · **Bilder (K1):** statische Quelle
⇒ `…/4x.webp`, animierte Quelle ⇒ `…/4x_static.webp`, Quelle ohne Flag im Read ⇒ `4x.webp`; Ziel
⇒ Platzhalter mit Name und Aliasen, nie eine URL · Zielzeile: getrackt-aktiv /
getrackt-nicht-aktiv („nicht aktiv"-Zeile) / ungetrackt (Besitzer, Slot über `twitchLogin`) ·
`foreignToView` bei `target.emoteSetId !== hostSelectedSetId` · Rückgabe = `runnable` und
`skipped` des Reads, aus dem die Datei entstand, plus `acknowledgedUnproven` und `read`; „Abbrechen"
⇒ `null` · Zeilenreihenfolge im Dialog (übersprungen mit Grund sichtbar, nicht ausgeblendet — §10
„Disabled explains itself").

**Tests:** `undo-confirm-dialog.spec.ts` (neu) **≥ 18 Fälle** (Spec 9.3; Zustandsübergänge,
Sperrgründe, Rückgaben, effektiver Plan in beiden Häkchen-Zuständen, Bilder statisch/animiert,
Accessibility-Bezüge — Regel 12); `seven-tv-set-entries.spec.ts` **+2** (`animatedById` aus dem
Flag; fehlendes Flag ⇒ `false`); `emote-url.spec.ts` **+2** (`emoteStillUrl` statisch/animiert,
bytegleich mit den zwei Formen, die `SevenTvEmoteJsonMapper` speichert).

**Abnahme:** der Dialog macht keinen Request außer `loadSevenTvSetEntries` (nur beim Neuladen) und
der Slot-Vorschau; `grep -n "cdn.7tv.app\|_static" undo-confirm-dialog.ts` leer — die Ableitung
liegt allein in `emote-url.ts`; `grep -rn "emoteStillUrl" web/src/app` trifft nur den Dialog und
die Specs. **AK 6, 7, 28 (Dialog).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/undo-confirm-dialog.spec.ts' --include='src/app/core/seven-tv/seven-tv-set-entries.spec.ts' --include='src/app/shared/emotes/emote-url.spec.ts'`;
`npm --prefix web run build`; Lint, Format.

**Commit:** `feat(undo): confirm an undo against a live read and save the recovery file first`.
**Abhängigkeiten:** T1 (Datei-Builder), T2 (Klassifikation); Spec 17. **Modell:**
`opus` — ein asynchroner Dialog mit Zustandsmaschine, der eine Löschung freigibt; Plan-253 hat den
Datei-Schritt aus demselben Grund auf `opus` gesetzt.

### T6 — Die Weiche im Datei-Schritt, `startUndoFlow`, Trigger-Verzweigung, Restore aus `transfer-undo`

**Ziel:** Eine Übertragungsdatei endet nach der Vorprüfung mit der Wahl der Richtung; „Lücken
schließen" bleibt der heutige Restore, „Ersetzungen rückgängig machen" führt in `startUndoFlow`
(Arbiter → Token → Dialog → Arbiter → Frischcheck → `startUndo`); eine `transfer-undo`-Datei geht
ohne Weiche in den Restore; Purge-Protokolle bleiben unberührt.

**Vertrag:** Spec 4.1, 4.2 Nr. 5, 6.1, 6.3, **17 K2** (Ergebnis durchreichen, gemischte Datei bis
in den Dienst) und **17 K3** (erster Read im Flow), E1, E4, E13, E14 (Frischcheck), E18, E22, F9,
F10, AK 1, 2, 5, 7 (Fehlerzustand), 8, 20, 21, 22 (Weiche), 28 (Flow-Hälfte), 32 (Flow-Hälfte);
UI-Designsprache §7.3 kommt in T8.

**Dateien:** `web/src/app/shared/seven-tv/file-import-step.ts` (Dispatch: `transfer-run` ⇒ beide
Parser ⇒ Vorprüfung ⇒ Weiche ⇒ `picked` je Wahl; `transfer-undo` ⇒ `parseTransferUndoForRestore`
⇒ Vorprüfung ⇒ `picked` `'restore'`; vierte Sorte in der Sortenliste; dritte Ergebnisart) +
`.spec.ts` (die vier `transfer-run`-Fälle klicken die Weiche — F10); `web/src/app/shared/seven-tv/import-source-dialog.ts`
(Union `ImportSourceDialogResult` deckt die dritte Art — prüfen, ob eine Änderung nötig ist) +
`.spec.ts`; `web/src/app/shared/seven-tv/import-trigger.ts` (Verzweigung `'transfer-undo'` ⇒
`startUndoFlow` mit `UndoFlowDeps`; injiziert `SevenTvUndoService` — der Trigger liegt im
Lazy-Chunk der Nutzungsseite, F9) + `.spec.ts`; `web/src/app/shared/seven-tv/undo-flow.ts` (neu,
+ `.spec.ts`: `UndoFlowDeps` nach dem Muster `RestoreFlowDeps`; `undoRunTarget()` aus
`ResolvedRestoreTarget` — `expectedChannelName` wie `restoreStartTarget`, kein
`resyncChannelName`; **erster Read und erste Klassifikation im Flow (K3):** nichts Laufendes ⇒
transiente Notiz des Dienstes, kein Dialog; Read-Fehler/`complete: false` ⇒ Dialog mit
`initialRead: null`; sonst Dialog mit Read; Frischcheck = Read + `classifyUndoRows` +
`diffUndoPlans` gegen `runnable`, Drift-Zeilen als `skippedDrift` nach `skipped`; `available:
false`/`complete: false` ⇒ keine `full`-Zeile, `addOnly` läuft; `startUndo(target, runnable,
skipped, acknowledgedUnproven)` — das Flag wird durchgereicht, nie hier neu entschieden;
Arbiter-Grund als Notiz nach T0); `web/src/app/shared/seven-tv/restore-flow.spec.ts`
(+2, AK 20: Undo-Datei nach geglücktem Undo ⇒ Regel 4 „Name belegt", kein `startRestore` mit
Zeilen; nach gescheiterter `full`-Zeile ⇒ genau der ADD der Quelle — der Filter ist unverändert,
`already-present-filter.spec.ts` bleibt 0 geändert); `web/public/i18n/{de,en}.json`
(`restore.import.choice.*`); `web/e2e/emote-import.e2e.spec.ts` (**F10, im selben Commit:** die
drei Fälle, die eine Übertragungsdatei einlesen — `:3542`, `:3728`, `:3878` — klicken „Lücken
schließen"; der Sorten-Fall `:1495` erwartet vier Sorten).

**Grenzfälle:** `transfer-run` beider Stufen ⇒ Weiche; `purge-run` ⇒ keine; `transfer-undo` ⇒ keine
(AK 1) · Vorprüfung `notEditable`/`notSelectable`/`unavailable` ⇒ Banner **vor** der Weiche, kein
Request an 7TV (AK 2) · Weiche „Lücken schließen" ⇒ `picked.kind === 'restore'` mit denselben
Zeilen wie heute; „rückgängig" ⇒ `'transfer-undo'` mit Kandidaten, Ziel (inkl. `host*`),
`sourceFile` · Undo-Parser scheitert, Restore-Parser nicht (nur bei manipulierter Datei denkbar) ⇒
Weiche zeigt nur den Restore — im Bericht nennen · Flow-Reihenfolge: Arbiter belegt ⇒ Notiz mit
Grund, kein Token-Prompt; Token fehlt ⇒ Prompt vor dem Dialog; Dialog `null` ⇒ nichts; Arbiter
zwischen Dialog und Frischcheck belegt ⇒ nichts startet; Frischcheck-Drift ⇒ Zeile `skippedDrift`
in `counts`, Rest startet (AK 8); Read scheitert ⇒ keine `full`-Zeile (AK 8); erster Read ohne
laufende Zeile ⇒ Notiz, kein Dialog, kein zweiter Request (AK 5, 21) · kein `settlement`-Lesen im
Flow (AK 32) · ungetracktes Ziel läuft durch die Weiche wie ein getracktes (AK 22).

**Tests:** `file-import-step.spec.ts` **+4 / ±4** (AK 1, 2; F10), `import-trigger.spec.ts` **+2**
(Verzweigung; Deps vollständig), `undo-flow.spec.ts` (neu) **≥ 12** (Spec 9.3; dazu die drei
K3-Ausgänge des ersten Reads, und — **Codex-Befund 1, Spec 17 K2** — ein Fall mit dem **echten**
`SevenTvUndoService` hinter `HttpTestingController`: gemischte `planned`-Datei, Dialog-Stub gibt
`runnable = [addOnly]`, `skipped = [full unproven]`, `acknowledgedUnproven: false` ⇒ nach „Starten"
genau die ADDs der `addOnly`-Zeile, **kein** `removeEmote`; und die Gegenprobe: Dialog-Stub
liefert die unbelegte `full`-Zeile fälschlich in `runnable` bei `acknowledgedUnproven: false` ⇒ der
Dienst sendet trotzdem kein REMOVE), `restore-flow.spec.ts` **+2** (AK 20),
`import-source-dialog.spec.ts` **±1**, falls die Union sie berührt. E2E: die vier angepassten
Fälle.

**Abnahme:** `grep -rn "seven-tv-undo.service\|undo-flow" web/src/app/app.routes.ts
web/src/app/app.config.ts web/src/app/core/channels web/src/app/shared/ui` leer (F9);
`grep -n "settlement\|isSettling" undo-flow.ts import-flow.ts` leer (AK 32); `grep -n "unproven"
undo-flow.ts` trifft nur das Durchreichen, keine Entscheidung; `npm --prefix web run build` meldet
„Initial total" ≤ Baseline aus T0 + 1 kB. **AK 1, 2, 5, 7, 8, 20, 21, 22 (Weiche), 28 (Flow), 32
(Flow).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/**/*.spec.ts'`;
`npm --prefix web run build`; `npm --prefix web run e2e -- e2e/emote-import.e2e.spec.ts` (nur ohne
Api auf `:5151`); Lint, Format.

**Commit:** `feat(undo): offer the undo next to the restore when a transfer file is read`.
**Abhängigkeiten:** T4 (`startUndo`, Notiz), T5 (`openUndoConfirmDialog`), T1 (Restore-Parser).
**Modell:** `opus` — die Weiche ist die eine Stelle, an der aus einer nicht vertrauenswürdigen Datei
eine destruktive Aktion wird, und der Flow trägt die Reihenfolge der Sicherungen.

### T7 — Dock-Abschnitt, Seite, Guard, Announcer

**Ziel:** `UndoProgressSection` zeigt den Lauf mit allen Zählern aus 4.7, beiden Meldungszeilen mit
Grund und Retry, `unknownRecordedIn`, der Resync-Zeile nur als `backendTriggered`/Fallback, der
Zielzeile und dem Protokoll-Download ab `settled`; die Nutzungsseite bindet sie außerhalb des
Set-Gates ein, zählt sie in `dockVisible()`, beobachtet den Settle (N2) und setzt den Lauf beim
Kanalwechsel zurück; der Leave-Guard fragt bei laufendem Undo mit eigener Textfamilie; der
Bildschirmleser-Zwilling spricht die Notizen.

**Vertrag:** Spec 4.7, 6.6, 11.2, E9, E15 (Guard-Hälfte), E16 (Anzeige), E23 (Zähler), F9, AK 13
(Dock-Zeilen), 15 (Guard), 17 (Hinweis), 18 (Download); Festlegung Nr. 3.

**Dateien:** `web/src/app/shared/seven-tv/undo-progress-section.ts` (neu, + `.spec.ts`; Muster
`import-progress-section.ts`: keine Inputs, liest den Dienst; `RunProgressPanel` mit
`labelPrefix="undo"` und der **Löschmeldung** als `syncReport`/`syncReportReason`/Retry, die
**Wiederherstellungsmeldung** als eigene Zeile mit Grund und Retry nach N4 (Festlegung Nr. 3);
Zähler `removedCount`, `restoredCount`, `unknownCount`, `gapCount` + Hinweis (E9), `partialRows`,
`omittedEntryCount` + Hinweis, `foreignNotedRows`, die transienten Übersprungen-Zeilen je Grund,
`unknownRecordedIn` (11.2), Resync-Zeile, Zielzeile aus `setName`/`ownerOrChannelLabel`,
„Ergebnisprotokoll speichern" (JSON/CSV über `openExportDialog` wie die Import-Section) ab
`settled` mit `protocolNotSaved`, `insufficientPrivileges`); `web/src/app/shared/seven-tv/run-progress-panel.ts`
(`labelPrefix` += `'undo'`) + `.spec.ts`; `web/src/app/shared/seven-tv/action-dock.ts`
(`undoShown`, `undoNoticePending`) + `.spec.ts`; `web/src/app/features/usage-stats/usage-stats-page.html`
(Section neben Import- und Restore-Section, außerhalb des Set-Gates) und `.ts` (`imports`-Eintrag,
`dockVisible`, vierter `watchRunSettle`-Aufruf — `partial` zählt als geändert, wenn `doneKeys` es
trägt) + `.spec.ts` (+2; Provider-Stub für den Undo-Dienst); `web/src/app/features/usage-stats/usage-stats-leave.guard.ts`
(`importService.isRunning() || undoService.isRunning()`, Familie `undo.leaveWhileRunning.*`, wenn der
Undo läuft — laufen beide, gewinnt der Import-Text, Festlegung Nr. 8) + `.spec.ts` (+1);
`web/src/app/features/channel-workspace/channel-workspace-layout.ts` (`undoService.resetIfChannelChanged`)
+ `.spec.ts`; `web/src/app/shared/seven-tv/dock-outcome-announcer.ts` (Zwilling der Undo-Notizen,
`resyncNoticeKey` mit Familie `'undo'`) + `.spec.ts`; `web/public/i18n/{de,en}.json`
(`undo.summary.*`, `undo.removalSync*`, `undo.restoreSync*`, `undo.resync.*`, `undo.progress*`,
`undo.leaveWhileRunning.*`); **nicht** `vote-session-detail-page` (6.6: dort keine Datei-Tür).

**Grenzfälle:** kein Lauf, keine Notiz ⇒ Section rendert nichts · laufend ⇒ Fortschritt, kein
Download · `settled` ⇒ Download, `protocolNotSaved` bis `markProtocolSaved()` (AK 18) · `gapCount`
> 0 ⇒ Hinweis „Undo aus derselben Datei erneut" (AK 17) · `partialRows`/`omittedEntryCount`
getrennt; `unknown`-Zeile mit Auslassung zählt nur in `unknownCount` + `omittedEntryCount` (AK 37,
Dock) · `unknownCount` > 0 ⇒ `unknownRecordedIn` · Meldungszeilen: Löschung `partial`/`shortfall`
⇒ Retry; `channelMismatch` ⇒ kein Retry; Wiederherstellung dito (AK 13) · Resync-Zeile nur bei
`backendTriggered` oder Fallback-Zuständen; nicht-aktives Ziel ⇒ keine · `dockVisible` bei nur
Notiz (`undoNoticePending`) ⇒ sichtbar · N2: Settle mit `doneKeys.length > 0` und Ziel ≠ aktives
Set ⇒ `refresh` bzw. Vormerkung; bereits gesettelter Lauf beim Mount ⇒ nicht erneut · Kanalwechsel
⇒ fertiger Lauf weg, laufender bleibt · Guard: Undo läuft ⇒ Dialog, Kanalwechsel ohne Dialog
(`leadsToSameRoute`).

**Tests:** `undo-progress-section.spec.ts` (neu) **≥ 8** (Spec 9.3 — Struktur, Zähler, Hinweise,
Zeilen mit/ohne Retry, Download ab `settled`), `run-progress-panel.spec.ts` **+1**,
`action-dock.spec.ts` **+2**, `usage-stats-page.spec.ts` **+2**, `usage-stats-leave.guard.spec.ts`
**+1**, `channel-workspace-layout.spec.ts` **+1**, `dock-outcome-announcer.spec.ts` **+1**.

**Abnahme:** `grep -rn "SevenTvUndoService\|seven-tv-undo.service" web/src/app --include=*.ts -l`
nennt nur Dateien im Lazy-Chunk (`features/usage-stats/**`, `features/channel-workspace/**`,
`shared/seven-tv/**`, `core/seven-tv/**`) — **nicht** `app.routes.ts`, `app.config.ts`; `npm
--prefix web run build` „Initial total" ≤ Baseline + 1 kB; `usage-stats.routes.spec.ts` unverändert
grün. **AK 13 (Dock), 15 (Guard), 17, 18.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/**/*.spec.ts' --include='src/app/features/**/*.spec.ts'`;
`npm --prefix web run build`; Lint, Format.

**Commit:** `feat(undo): dock the undo run with both reports and guard the page while it runs`.
**Abhängigkeiten:** T4 (Signale), T0 (#264 im Branch). **Modell:** `sonnet` — Muster liegt in zwei
Sections vor; das Risiko ist Vollständigkeit der Zähler, und die trägt die 4.7-Liste.

### T8 — E2E über den ganzen Weg, Doku

**Ziel:** Die neuen Fälle aus Spec 9.4 durch den Browser mit 7TV-Stub, beiden Meldungs-Mocks und
Set-Read-Zählung; die Doku-Stellen, die kein Code-Task trug.

**Vertrag:** Spec 9.4, AK 1, 6, 9, 11, 12, 17, 18, 21, 26, 28, 29, 30, 35, 36, 37, 38, 40
(E2E-Hälften); Spec 6.1 letzter Punkt und 14 (UI-Designsprache §7.3: Weiche als Vertrag, fünfte
Einlesesorte); `docs/Feature-Ideen-2026-08-01.md` nur prüfen (kein Treffer erwartet).

**Dateien:** `web/e2e/emote-import.e2e.spec.ts` (neuer `describe`-Block „replace undo (#254)"),
`web/e2e/support/mocks.ts` (nur, falls ein Helfer fehlt — die Set-Read-Zählung geht über
`sevenTvGqlRequestKind`), `web/e2e/audit/ui-audit.audit.ts` (prüfen, ob ein Szenario die
Datei-Tür ohne Weiche erwartet), `docs/UI-Designsprache.md` (§7.3).

**E2E (+12, jeder Fall nach Spec 9.4 und 17 mit `mockEmoteSetTargets` `editable: true` und
`mockSyncDeletedInSet`/`mockSyncRestoredInSet`):** (1) Hauptweg: zwei `done`-Replace-Zeilen, eine
Duplikat-Zelle ⇒ Weiche ⇒ Undo ⇒ Token ⇒ Dialog (zwei `full`, Löschzahl 2, Slot +1) ⇒ Datei ⇒
Starten ⇒ Stub-Reihenfolge `setRead, removeEmote(S1), addEmote(T1,a), setRead, removeEmote(S2),
addEmote(T2,a), addEmote(T2,b), addEmote(T2,D)` ⇒ `sync-deleted [S1,S2]`, dann `sync-restored
[T1,T2]` mit `expectedChannelName` ⇒ Dock, Download (AK 1, 6, 9, 12, 18, 38, 40). (2) Dieselbe Datei
erneut ⇒ Notiz, kein Dialog (AK 21). (3) 409 auf dem zweiten ADD ⇒ `gapCount 1` ⇒ erneut ⇒ `addOnly
[B]` ⇒ ein ADD (AK 17, 30). (4) Zweiter Alias der zweiten Quelle unmittelbar nach dem ersten REMOVE
⇒ zweiter Read, `skippedDrift`, kein zweites REMOVE (AK 26). (5) `planned`-Datei ohne Bestätigung
⇒ „Rückweg sichern" gesperrt (AK 28). (6) T unter `C`, S hält A ⇒ übersprungen, kein Request; S weg
⇒ `addOnly` neben `C` (AK 29). (7) `C` nach `addEmote(T, A)`, 409 auf B ⇒ `failed@2` ⇒ zweiter Lauf
`addOnly` mit `notes`, `addEmote(T, B)` (AK 36). (8) Zweiter Lauf nach zurückgeholtem `D` ⇒
`nothingToDo` (AK 35). (9) `addOnly` mit Auslassung, 503 auf dem ADD ⇒ `unknown`, nicht `partial`
(AK 37). (10) 503 auf einem REMOVE ⇒ Re-Read zeigt Quelle weg ⇒ `failed@1`, Quelle in
`sync-deleted` (AK 11). (11) Weiche → „Lücken schließen" ⇒ wie der bestehende Restore-Fall.
(12) **Gemischte `planned`-Datei ohne Häkchen (Codex-Befund 1, Spec 17 K2/K4):** zwei
Replace-Zeilen, Stub-Set: Quelle 1 hält ihren Namen (⇒ `full`, unbelegt), Quelle 2 ist weg (⇒
`addOnly`) ⇒ Dialog zeigt die erste als `skippedUnproven` gekennzeichnet, die zweite als `addOnly`,
Aktionszeile „Starten" ohne „Rückweg sichern" ⇒ Starten ⇒ Stub sieht genau `addEmote(T2, …)` und
**kein** `removeEmote`, kein `sync-deleted`, `sync-restored [T2]` ⇒ Ergebnisprotokoll führt die
erste Zeile als `kind: 'skipped'` mit `skippedUnproven`, `counts.requested 1`, `counts.skipped 1`
(AK 28). `page.clock` vor `goto`, `runFor` statt `fastForward`, `pauseAt` vor einer Phase, in der Echtzeit
keinen Timer auslösen darf (CLAUDE.md Tests). Zählung der Suite im Bericht (heute 181 `test(`).

**Abnahme:** volle E2E-Suite grün ohne Api auf `:5151`; jeder Undo-Fall belegt, dass **kein**
`addEmote` `alias: null` trägt (AK 40) und dass vor jedem `removeEmote` ein `setRead` steht
(AK 38); §7.3 nennt die Weiche und die fünfte Sorte.

**Gates:** `npm --prefix web run e2e` (voll); Lint, Format.

**Commit:** `test(e2e): undo a replace from its transfer file end to end`; Doku als eigener
`docs(design):`-Commit, wenn der Task es sauber trennen kann. **Abhängigkeiten:** T1–T7. **Modell:**
`opus` — die Fälle schneiden durch Weiche, Dialog, Lauf, Nachlesen und Meldungen; ein E2E-Fall, der
an der falschen Stelle grün ist, ist teurer als jeder Unit-Test (Memory).

### T9 — Volle Gates, Coverage, Bundle-Gate, Zweitmeinung

**Ziel:** Die Fertigmeldung nach `CLAUDE.md` über den ganzen Branch, das Bundle-Gate aus F9, und
die unabhängige Zweitmeinung vor dem PR.

**Schritte:** (1) `dotnet test EmotePurge.slnx` (Docker läuft — unverändertes Backend, AK 24: der
Lauf belegt „kein neuer Backend-Fall nötig"), `npm --prefix web test -- --watch=false`,
`npm --prefix web run e2e` — **nur ohne Api auf `:5151`** (wer T10 vorbereitet hat, beendet
`dotnet run` erst); `npm --prefix web run lint`; beide Formatter. (2) `npm --prefix web run build`:
„Initial total" < 500 kB und ≤ Baseline aus T0 + 1 kB; `ng build --stats-json` ⇒ kein Initial-Chunk
enthält `seven-tv-undo.service`, `undo-flow`, `undo-confirm-dialog`, `undo-progress-section`
(F9). (3) `node scripts/coverage-local.mjs` — Näherung, in beide Richtungen unscharf (Memory: misst
nur Committetes; von den Sonar-Ausschlüssen abgedriftet); bei < 80 % nachsehen, ob es
`undo-confirm-dialog.ts` oder `undo-progress-section.ts` sind (neue Dateien messen nah an Sonar).
(4) Die Zweitmeinung über den Codex-Companion **direkt im Worktree**, damit der Lauf gegen den
richtigen Checkout und die richtige Basis geht (Memory: `/codex:review` läuft im Session-CWD;
`--scope branch` erzwingen): `node ~/.claude/plugins/cache/openai-codex/codex/1.0.6/scripts/codex-companion.mjs review "--wait --model gpt-6-sol --scope branch --base origin/feat/emote-sets-200"`
— von der Hauptsession gestartet, ohne Rückfrage, einmal je Branch, Ergebnis unverändert dem
Betreiber vorlegen (Regel 22); Widersprüche zu Opus-Review ⇒ Fable als Schiedsrichter (global).
(5) Ergebnisse ins Ledger.

**Commit:** keiner, außer ein Befund verlangt einen `fix:`; dann die betroffene Suite wiederholen
und Codex nur bei geändertem Diff erneut. **Abhängigkeiten:** T8. **Modell:** `haiku` für die
Gate-Läufe; Codex ruft die Hauptsession. **AK 24, 25 (Gate-Hälfte).**

### T10 — Live-Verifikation (Regel 16, Betreiber-Handgriff) und Abschluss

**Ziel:** Die Punkte 1–10 (mit 6a–6d) aus Spec 9.5 mit Zahlen im PR-Text; danach die Issue-Pflege.

**Aufbau:** Api per `dotnet run --project src/EmotePurge.Api` **aus diesem Worktree** (`:5151`,
User-Secrets gesetzt — Memory; dort neu einloggen, das Cookie des Haupt-Checkouts gilt hier nicht),
`npm --prefix web start`; danach `dotnet run` beenden, bevor E2E läuft. Konten aus 0.1:
`olaf_olaf_son` (Sets `tttt` aktiv, `test`), `definitiv_nicht_sensitron` (Punkt 8). Zugangsdaten
und 7TV-Token stellt der Betreiber, wenn es so weit ist. Für Punkt 6d (Settling erzwingen) ein
Netzwerk-Throttle oder eine Dev-Tools-Blockierung des REMOVE — **nie** den Prod-Worker; kein
Handgriff dieses Tasks verbindet sich mit `vps` oder `nas`. Punkt 6a misst zusätzlich den Takt der
tokenlosen Reads (Spec 10: Budget ungemessen; ein 429 dort ist ein Befund, kein Fehler des Plans).

**Zu belegen:** Spec 9.5, Punkte 1–10 wortgleich — der Plan wiederholt sie nicht. Zusätzlich als
Regressionsprobe: ein Restore aus einem Purge-Protokoll (Datei-Tür ohne Weiche) und ein
Add-only-Import (Trigger-Sperre und Dock unverändert).

**Danach (Betreiber):** PR gegen `feat/emote-sets-200` mit den Zahlen; #254 schließt mit dem Merge
in den Epic-Branch (von Hand); **Epic #200** bekommt die Kind-Zeile „Replace-Undo — #254, PR #…"
mit Spec und Plan (Memory: unvollständiges Epic macht seine Zusage kaputt); **#256** erhält den
Hinweis auf Spec 11.1/11.3, F5 (a) und F14; **#255** den auf Spec 11.4 (Token-Prompt, Zählung
„zurückgeholt"/„entfernt", zwei Sätze der Weiche). `gh issue edit` scheitert an Projects-Classic —
Body per `--json` lesen, per REST-`PATCH` schreiben (Memory). Keine Ticketnummern in
Commit-Metadaten.

Der Betreiber führt die Handgriffe aus; ein Subagent (`sonnet`) bereitet Read-back-Abfragen,
Erwartungswerte je Punkt, die Netzwerktab-Reihenfolge und die Issue-Kommandos vor und schreibt die
Befunde in den PR-Text. **Kein Commit**, außer ein Fund verlangt einen `fix(…)`. **AK 25
(Live-Hälfte); Spec 9.5 deckt AK 1, 2, 6, 9, 11, 12, 14, 15, 16, 17, 20, 21, 22, 26–29 live.**

---

## 4. Nachverfolgung der Akzeptanzkriterien

| AK | Inhalt (Kurzform) | Tasks |
|---|---|---|
| 1 | Weiche für `transfer-run` beider Stufen; `picked.kind` je Wahl; kein Purge-Protokoll | T6, T8 (1, 11), T10 (2) |
| 2 | Vorprüfung scheitert ⇒ Banner, keine Weiche, kein 7TV-Request | T6, T10 (8) |
| 3 | `parseTransferRunForUndo` je Stufe; `transferRunNoRows` | T1 |
| 4 | `classifyUndoRows` nach Tabelle 4.3, je Zeile ein Fall | T2 |
| 5 | Dialog nur bei ≥ 1 laufender Zeile, sonst Notiz; nur ein Read | T6, T8 (2), T10 (3) |
| 6 | „Rückweg sichern" ⇒ `planned`-Datei ⇒ „Starten"; ohne `full` direkt „Starten" | T5, T1 (Form), T8 (1), T10 (2) |
| 7 | Lesefehler ⇒ nichts frei, „Ziel neu laden" | T5 |
| 8 | Frischcheck: Drift ⇒ `skippedDrift`; Read weg ⇒ keine `full`-Zeile | T2 (Vergleich), T6 |
| 9 | Schrittfolge je Modus, nie ADD vor REMOVE | T4, T8 (1) |
| 10 | HTTP 0/5xx ⇒ `unknown`; 401/403/`LACKING_PRIVILEGES` ⇒ Abbruch + Token weg; GQL ⇒ `failed` | T4 |
| 11 | Ein Re-Read, Tabelle 4.6, `completedSteps` nie gesenkt, `unknownRecordedIn` | T4, T7, T8 (10) |
| 12 | `sync-deleted` dann `sync-restored`, IDs nach Regel, `expectedChannelName`, keine Kanalroute | T4, T8 (1), T10 (2) |
| 13 | Dreiwertigkeit je Meldung, Retry nach N4 | T4, T7 |
| 14 | Kein Client-Resync; `backendTriggered`; N1 nur bei zwei endgültigen Fehlschlägen | T4, T10 (2) |
| 15 | `beforeunload` genau solange ein `full`-Lauf offen ist; Guard bei laufendem Undo | T4 (`destructiveOpen`), T7 (Guard), T10 (9) |
| 16 | Arbiter kennt `'undo'`; gegenseitige Sperre inkl. Settling | T0 (Vertrag), T4, T10 (6d) |
| 17 | Lücke ⇒ `gapCount` + Hinweis; zweiter Lauf ⇒ `addOnly` mit genau den fehlenden ADDs | T4, T7, T8 (3), T10 (6b) |
| 18 | `finished`-Protokoll ab `settled`, Felder, übersprungene Kandidaten als eigene Zeilenart, `meta.undoneFile`, `protocolNotSaved` | T1 (Form, `kind: 'skipped'`), T4, T7, T8 (1, 12) |
| 19 | Abweisungen `transfer-undo`; Restore-Parser der Undo-Datei | T1 |
| 20 | Restore aus Undo-Datei: „Name belegt" bzw. genau der Quell-ADD | T6, T10 (4) |
| 21 | Dieselbe Datei zweimal ⇒ `nothingToDo`, Notiz | T6, T8 (2), T10 (3) |
| 22 | Ungetracktes Ziel: gleicher Weg, Papier, kein Resync | T4, T6, T10 (10) |
| 23 | Bestehende Übertragungsdateien zeilen-identisch gelesen | T1 |
| 24 | Kein Backend-Diff; `dotnet test` grün | T9 |
| 25 | Vier Gates + Live-Verifikation | T9, T10 |
| 26 | Neuer Read je REMOVE-Versuch, auch nach Rate-Limit; Drift ⇒ `skippedDrift` | T3, T4, T8 (4), T10 (6a) |
| 27 | Read-Fehler ⇒ Zeile läuft nicht; drei ⇒ Rest `cancelled`; Protokoll trägt letzten Read | T3, T4, T1 (Form) |
| 28 | `provenance`; Kennzeichnung + Bestätigung; ohne sie `skippedUnproven` — im Dialog **und** im Dienst; gemischte Datei sendet kein REMOVE | T1, T2, T5 (Dialog), T4 (Dienst), T6 (Unit bis in den Dienst), T8 (5, 12), T10 (6c) |
| 29 | Fremde Ziel-Einträge: `full` gesperrt an drei Stellen, `addOnly` läuft mit `notes` | T2, T4, T8 (6) |
| 30 | Zweiter Lauf nach `ADD B`-Fehlschlag ⇒ `addOnly [B]`; Restore aus Undo-Datei ⇒ „Name belegt" | T2, T8 (3), T10 (6b) |
| 31 | Jeder ADD mit Alias; `null` ⇒ `D`; ohne `D` `targetNameUnverifiable`/`omittedEntries` | T1, T2, T4 |
| 32 | Arbiter „belegt" bei `isSettling`; keine eigene Settling-Prüfung in Flows | T0, T4, T6, T10 (6d) |
| 33 | `targetNameTaken`: `full` ganze Zeile; `addOnly` ⇒ `partial` + `omittedEntries` | T2, T4, T7 |
| 34 | `addOnly` an Schritt 0 `unknown` ⇒ als ADD geklärt, nie in `sync-deleted` | T4 |
| 35 | Zurückgeholtes `D` ⇒ `alreadyPresent`; `normalizeTargetEntries` | T2, T8 (8) |
| 36 | `C` mitten in der Zeile ⇒ `failed@2` ⇒ zweiter Lauf `addOnly` mit `notes` | T2, T4, T8 (7) |
| 37 | `addOnly` mit Auslassung und `unknown`-ADD ⇒ `unknown`, nicht `partial` | T4, T7, T8 (9) |
| 38 | N `full`-Zeilen ⇒ N Reads, keiner ohne unmittelbaren Read | T3, T4, T8 (1) |
| 39 | `reset()` in allen drei Phasen ⇒ Meldungen trotzdem, Lauf schließt | T4 |
| 40 | E2E: `addEmote(T2, D)`, nie `alias: null` | T8 (1) |

**Spec-Abschnitte ↔ Tasks:** 4.1 → T6 · 4.2 → T5 (Nr. 6–9), T6 (Nr. 5) · 4.3 → T2 · 4.4 → T3, T4 ·
4.5 → T4 · 4.6 → T4 · 4.7 → T7 · 4.8 → T1 (Form), T5 (`planned`), T7 (`finished`) · 4.9 → T4, T6 ·
5 → T9 (AK 24) · 6.1 → T1, T6 · 6.2 → T2 · 6.3 → T5, T6 · 6.4 → T1 · 6.5 → T3, T4 · 6.6 → T7 ·
6.7 → T1, T4, T5, T6, T7 · 7 → als Grenzfälle auf T2, T4, T5, T6 verteilt · 9.2 → T1 · 9.3 → T2–T7
· 9.4 → T6 (Anpassungen), T8 (neu) · 9.5 → T10 · 10 → keiner (ausdrücklich nicht Teil) · 11.1 → T0
· 11.2 → T7 · 11.3 → T5 (minimal eigen) · 11.4 → T4 (Regel), T10 (Issue-Hinweis) · 12 → Abschnitt
8 · 13, 15 → keine Aufgabe (entschieden) · 14 → T1, T4 (DECISIONS), T8 (Doku), T10 (Issues) · 16 →
in den AKs enthalten.

---

## 5. Reihenfolge und Abhängigkeiten

```
Welle 0                          T0 (Epic-Stand, Vertrag 11.1, Baseline)
                                  │
Welle 1   Lane A: T1 ──► T2       │      Lane B: T3
                          │       │               │
Welle 2                   ├───────┴───────────────┤
          Lane A: T5 (T1, T2)              Lane B: T4 (T2, T3, T0)
                          │                       │
Welle 3   Lane A: T6 (T4, T5, T1)          Lane B: T7 (T4, T0)
                          └──────────┬────────────┘
Welle 4                              T8
Welle 5                              T9
Welle 6                              T10
```

- **Welle 0:** T0 allein. Endet er mit „nicht erfüllt", steht der Plan, bis #256 Punkt 1 gemergt
  ist — kein Feature-Task startet gegen den alten Arbiter (Spec 15 B).
- **Welle 1 (parallel, zwei Worktrees):** Lane A T1 → T2 seriell (T2 braucht `UndoCandidate`);
  Lane B T3 (Engine, kein Symbol aus Lane A). Gemeinsame Dateien: keine. Preflight vor Welle 2: die
  Naht 2.1 (Kandidat ↔ Klassifikation) und 2.2 (Hook-Semantik ↔ T4-Brief).
- **Welle 2 (parallel, zwei Worktrees):** T5 (Dialog, gegen Spec 6.3/6.5-Signatur) ∥ T4 (Dienst,
  DECISIONS-Eintrag 1). Gemeinsame Dateien: `de.json`/`en.json` — beide legen `undo.*`-Schlüssel
  an (`undo.confirm.*` gegen `undo.errors.*`/`undo.settling`); wer zweiter mergt, löst den
  JSON-Konflikt. Preflight vor Welle 3: Naht 2.5 (`UndoConfirmOutcome` ↔ `startUndo`), Naht 2.4
  (Signal-Liste des Dienstes ↔ T7-Brief).
- **Welle 3 (parallel, zwei Worktrees):** T6 (Weiche, Flow, F10-Anpassungen an `emote-import.e2e.spec.ts`
  und `file-import-step.spec.ts`) ∥ T7 (Dock, Seite, Guard). Gemeinsame Dateien: nur die Locales.
  T7 fasst keine E2E-Datei an, T6 keine Seite.
- **Welle 4:** T8. **Welle 5:** T9. **Welle 6:** T10.

**Grüne Zwischenstände:** nach T1 gibt es eine Datei-Sorte, die nirgends erzeugt wird — harmlos;
nach T3 einen Hook, den niemand setzt; nach T4 einen Dienst, den niemand ruft (nur seine Specs);
nach T5 einen Dialog ohne Öffner. Erst T6 verbindet Weiche, Dialog und Dienst; bis dahin laufen
Übertragungsdateien unverändert in den Restore. Es gibt keinen Zwischenstand, in dem ein Nutzer
eine halbe Aktion erreichen könnte.

---

## 6. Festlegungen des Plans, wo die Spec schweigt oder es der Plansache überlässt — Betreiber-Veto möglich

| # | Stelle | Befund | Festlegung des Plans | Task |
|---|---|---|---|---|
| 1 | Spec 14 „im jeweils ersten betroffenen Commit" | Plan-230 setzte den Eintrag dort, wo der Weg für den Nutzer erreichbar wird (T8), Plan-253 dort, wo die Regel entsteht | Wie Plan-253: Eintrag 2 in T1 (Datei entsteht), Eintrag 1 in T4 (Lauf entsteht); T6 macht den Lauf erreichbar und ändert den Eintrag nicht, sein Commit verweist in der Beschreibung darauf | T1, T4 |
| 2 | Spec 6.1 (`UndoCandidate` ohne `provenance`) vs. 6.2 (`UndoPlanRow += provenance … aus der Datei`) und E2 („Kandidaten … als unbelegt markiert") | `classifyUndoRows(candidates, read)` hat keinen Stufen-Parameter; die Herkunft muss am Kandidaten hängen | `UndoCandidate.provenance: 'confirmed' \| 'unproven'`, gesetzt vom Parser nach Stufe | T1, T2 |
| 3 | Spec 4.7 „zwei Meldungszeilen", 6.7 „`RunProgressPanel` bildet … über `labelPrefix`" | Das Panel trägt genau eine Meldung | Panel trägt die Löschmeldung (die destruktive Tatsache, F8), die Section die Wiederherstellungsmeldung als eigene Zeile — Spiegel der Import-Section (Panel `sync-imported`, Section `removalReport`) | T7 |
| 4 | Spec E19/6.5 „wie die Prüfung in die Engine kommt, ist Plansache" | `buildRequest` ist synchron und läuft je Versuch; der Read ist asynchron | Optionaler Hook `beforeStep` je Versuch (auch je Rate-Limit-Wiederholung), `skip` ⇒ Zeile `cancelled` ohne Request; Hook-Fehler fail-closed; die drei bestehenden Dienste setzen ihn nicht | T3, T4 |
| 5 | Spec 6.4 `status: RunItemStatus \| 'partial'` vs. `RunProgressPanel` zählt `RunQueueItem`-Status | `partial` ist kein Engine-Status | Der Laufdatensatz hält die Engine-Zeile unverändert und `partial` als eigenes `undoStatus`-Feld je Zeile; Datei und Section lesen `undoStatus`, das Panel bekommt eine Projektion, in der `partial` als `done` zählt, und `partialRows` steht daneben (4.7) | T4, T7 |
| 6 | AK 32 „`grep` findet keine `settlement`-Abfrage außerhalb des Arbiters" | Der Undo-Dienst selbst hält `settlement` und leitet `isSettling` daraus ab — das ist der Vertrag aus 11.1 Punkt 1 | Das Grep-Kriterium gilt für die Flows (`undo-flow.ts`, `import-flow.ts`) und die Startpunkte, nicht für die Dienste | T4, T6 |
| 7 | Spec 4.3 Schritt 6 „Quellteil keiner, 0 ADDs ⇒ `nothingToDo`" für eine `addOnly`-Zeile, deren einziger Eintrag ausgelassen wurde (`targetNameTaken`) | Die Zeile ist weder laufend noch „nichts zu tun" | Sie ist übersprungen `nothingToDo`, und die Auslassung steht sichtbar im Übersprungen-Grund (Dialog) und in `counts.skippedByReason`; es gibt keine `addOnly`-Zeile mit 0 ADDs im Lauf | T2, T5 |
| 8 | Spec 6.5 „`usageStatsLeaveGuard` fragt `importService.isRunning() \|\| undoService.isRunning()` mit eigener Textfamilie" | Laufen beide (nur konstruiert möglich, der Arbiter schließt es aus), ist die Familie unbestimmt | Import-Text gewinnt; der Fall ist ein Spec-Fall am Guard, kein Produktfall | T7 |
| 9 | Spec 11.3 (Zustandsmaschine: konsumieren, wenn extrahiert) | #256 Punkt 5 ist nicht gelandet (T0 belegt es) | Der Undo-Dialog trägt eine eigene, minimale Maschine mit Verweis auf `import-confirm-dialog.ts` und #256, damit #256 beide Stellen sieht | T5 |
| 10 | Spec 6.5 „Kopieren ist erlaubt, wenn Extrahieren #256 vorgreift" | Settling/Nachlesen/Meldungen des Imports sind modulprivat und an `ImportRunItem` gebunden | Der Undo-Dienst kopiert das Muster mit Verweis; keine Änderung an Import, Delete, Restore in diesem Plan (11.1 letzter Absatz) | T4 |

---

## 7. Klärungen K1–K4 — entschieden (Betreiber: K1, K2, K4 · Orchestrator: K3 · 2026-09-25)

Die erste Fassung hatte hier vier Widersprüche bzw. Lücken der Spec mit je einer Empfehlung. Alle
vier sind am 2026-09-25 entschieden — nach dem adversarialen Codex-Review des Plans (Abschnitt
10), der K1, K2 und K4 je um einen Punkt geschärft hat —, in die Tasks eingearbeitet und als
Nachtrag „Klärungen zum Plan (2026-09-25)" am Ende der Spec festgehalten (Spec 17). Der Befund
bleibt je Punkt stehen, damit die Entscheidung lesbar bleibt; was gilt, steht unter
*Entscheidung*.

**K1 — Bilder im Bestätigungsdialog haben keine Quelle (Spec 4.2 Nr. 7, 6.3 vs. Plan-230 T1 und
Memory „7TVs `_static` nur bei animierten Emotes").** Die Spec verlangt „Quelle und Ziel
nebeneinander als Bild … über dieselbe Bild-URL-Ableitung wie der Auflösungsschritt (Plan-230
T1)". Der Auflösungsschritt leitet keine URL ab — er bekommt sie von den Zeilen
(`ImportRow.imageUrl`, `EmoteListItem.imageUrl`, aus Postgres bzw. der Set-Vorschau), und Plan-230
T1 verbietet die Ableitung aus der ID ausdrücklich (statische Emotes 404en unter `_static`). Die
Übertragungsdatei trägt keine URL (`TransferRunRow`), der Live-Read liest nur `alias`, `emote.id`,
`emote.defaultName`, und das **Ziel** steht nach einem geglückten Replace in keinem Set — keine
Vorschau kennt es. Ein Einzel-Emote-Lookup existiert im Client nicht (Spec E21 sagt es selbst).
*Entscheidung (Betreiber, nach Codex-Befund 3):* die **Quelle** zeigt ihr echtes Bild, das
**Ziel** einen Platzhalter. Die Quelle bekommt ihr Bild aus dem Live-Read — erweitert **nicht** um
ein Bildfeld (die erste Empfehlung war falsch: die Vorschau-Query in `SevenTvApiClient.cs:71`
liest `flags { animated }` und baut die URL getrennt; `Emote.images` liest sie absichtlich nicht),
sondern um `flags { animated }`. Die URL entsteht nach derselben Regel wie im Backend
(`BuildForeignImageUrl`, `SevenTvApiClient.cs:1272-1280`): `4x_static.webp` nur bei `animated`,
sonst `4x.webp`, fehlendes Flag ⇒ `4x.webp` — statische Emotes 404en unter `_static` (Memory
2026-09-09, DECISIONS zu `d42a242`). Im Frontend gibt es heute keine Ableitung aus der ID
(`emote-url.ts` und `emote-image-loader.ts` schreiben nur gespeicherte URLs um); `emoteStillUrl`
(0.5) wird die erste und einzige, bytegleich mit dem Backend. Das Verbot aus Plan-230 T1 galt der
Ableitung **ohne** Kenntnis der Animiertheit und bleibt dafür bestehen. Das Ziel bekommt den
Platzhalter aus dem Auflösungsschritt (`targetImageUrl: null`) mit Name und Aliasen daneben;
keine neue 7TV-Anfrage. Tests für statisches und animiertes Quell-Emote. (T5; Spec 17 K1.)

**K2 — Aktionszeile einer unbestätigten `planned`-Datei, die auch `addOnly`-Zeilen hat (Spec 6.3
vs. 4.2 Nr. 8, AK 28).** 6.3: „ohne gesetzte Bestätigung bleibt „Rückweg sichern" gesperrt mit
Grund, und `full`-Zeilen laufen nicht (`skippedUnproven`); `addOnly`-Zeilen sind davon unberührt."
4.2 Nr. 8: „Ein Plan ohne `full`-Zeile … zeigt direkt „Starten"." Wenn ohne Häkchen jede `full`-Zeile
`skippedUnproven` ist, hat der effektive Plan keine `full`-Zeile mehr — dann müsste nach 4.2 Nr. 8
„Starten" erscheinen und die `addOnly`-Zeilen laufen; nach 6.3 bleibt aber „Rückweg sichern"
gesperrt, und nichts läuft, auch nicht die unberührten `addOnly`-Zeilen. Dazu nennt 6.3
`startUndo(target, runnable, drifted, counts, acknowledgedUnproven)` und 6.5 `startUndo(target,
rows, drifted, counts)` — eine Arität mit, eine ohne das Flag. *Entscheidung (Betreiber, nach Codex-Befund 1):* Lückenzeilen laufen, **doppelt gesichert** — die
Lesart „effektiver Plan" plus die Codex-Forderung. Ohne Häkchen sind unbelegte `full`-Zeilen im
Dialog als `skippedUnproven` übersprungen (sichtbar, mit Grund), die Aktionszeile folgt dem
effektiven Plan (nur `addOnly` ⇒ „Starten"; nichts ⇒ Aktionszeile gesperrt mit Grund); mit
Häkchen sind sie `full`, und „Rückweg sichern" erscheint. Das Dialog-Ergebnis übergibt die
laufenden und die übersprungenen Zeilen **explizit** (`runnable`, `skipped`,
`acknowledgedUnproven`, `read`). Der Dienst prüft die Herkunftssperre **selbst noch einmal**: ohne
`acknowledgedUnproven` läuft keine `full`-Zeile mit `provenance: 'unproven'`, gleich was er
bekommt — so steht es im Vertrag (Spec 17 K2), und T4 testet es. Ein Test verfolgt eine gemischte
`planned`-Datei über „Starten" bis in den Dienst und belegt, dass kein REMOVE gesendet wird — als
Unit-Test mit dem echten Dienst (T6) und als E2E (T8, Fall 12). Die Signatur ist vereinheitlicht:
`startUndo(target, runnable, skipped, acknowledgedUnproven)` in 6.3 **und** 6.5.
`acknowledgedUnproven` steht im Laufdatensatz und in `meta` beider Undo-Datei-Stufen (Papierspur,
F6). (T4, T5, T6, T8; Spec 17 K2.)

**K3 — Wer macht den ersten Read: Flow oder Dialog (Spec 4.2 Nr. 6 vs. E18, 4.2 Nr. 7, AK 5).**
4.2 Nr. 6: „Der Bestätigungsdialog liest beim Öffnen das Ziel-Set live". E18/4.2 Nr. 7/AK 5: „Ein
Dialog ohne eine einzige laufende Zeile öffnet **nicht** … transiente Notiz … kein Request an 7TV
außer dem einen Read." Ob eine Zeile läuft, weiß man erst nach dem Read — liest der Dialog beim
Öffnen, ist er schon offen, wenn es nichts zu tun gibt; liest der Flow vorher, ist „beim Öffnen"
ein zweiter Read (zwei Requests, gegen AK 5). *Entscheidung (Orchestrator, wie empfohlen):* der **Flow** macht den ersten Read und die erste
Klassifikation; nichts Laufendes ⇒ Notiz, kein Dialog; Read-Fehler oder `complete: false` ⇒ der
Dialog öffnet **im Fehlerzustand** mit „Ziel neu laden" (`initialRead: null`, AK 7 bleibt wahr);
Erfolg ⇒ der Dialog öffnet mit Read und Plan als Eingabe und liest **nur** bei „Ziel neu laden"
neu. E14s „ein Read beim Öffnen des Dialogs" ist dann der Read des Flows; die drei Prüfstellen
bleiben drei. (T5, T6; Spec 17 K3.)

**K4 — Im Dialog übersprungene Zeilen im Ergebnisprotokoll (Spec 6.4 vs. 6.3/6.5).** 6.4 sagt für
`finished`: „alle Zeilen ungefiltert" und nennt als `skippedReason` auch `duplicateInFile` — einen
Grund, der nur im Dialog entsteht, nie im Lauf. `startUndo` bekommt nach 6.3/6.5 aber nur
`runnable`, `drifted` und `counts` — die im Dialog übersprungenen Zeilen (`nothingToDo`,
`sourceUnderOtherName`, `targetNameTaken`, `duplicateInFile`, `skippedUnproven`, …) erreichen den
Dienst nur als Zähler und können nicht in der Datei stehen. *Entscheidung (Betreiber, nach Codex-Befund 2):* übersprungene Kandidaten bekommen eine **eigene
Zeilenart**. Die erste Empfehlung („als `cancelled` mit `skippedReason`") ließ sich in der
freigegebenen Zeilenform nicht abbilden — `TransferUndoRow` verlangt `mode` und `restoredTarget`,
ein vor der Klassifikation übersprungener Kandidat hat beides nicht. Das `finished`-Format bekommt
deshalb `kind: 'skipped'`: Quell-ID, `sourceName`, `alias`, Ziel-ID, `provenance`,
`skippedReason`, **ohne** `mode`, `restoredTarget`, `removedSource`, `status`, `completedSteps`;
gelaufene Zeilen sind `kind: 'executed'` in der Form aus 6.4 (Zeilen, die erst im Lauf über den
Hook übersprungen werden, bleiben `executed` mit `status: 'cancelled'` und `skippedReason`). Der
Restore-Parser der `transfer-undo`-Datei überspringt `kind: 'skipped'` ausdrücklich.
`counts.requested` zählt nur Zeilen, die tatsächlich gelaufen sind; `counts.skipped` kommt dazu.
Der Dienst bekommt dafür `runnable` **und** `skipped` (K2). Die Festlegung steht **vor** T1 in
dessen Vertrag, mit Tests für `duplicateInFile` und je einen Dialog-, Frischcheck- und
Dienst-Grund. Die Rückweg-Datei (`planned`) bleibt bei den laufenden Zeilen. (T1, T4, T5, T6;
Spec 17 K4.)

---

## 8. Rückweg

Wie Spec 12: kein Schema, keine Migration, kein Backend-Diff; der Revert ist ein Frontend-Revert
des PR. Der Engine-Hook (T3) ist optional und geht mit zurück, ohne die drei bestehenden Dienste zu
berühren. Bereits heruntergeladene `transfer-undo`-Dateien bleiben als JSON lesbar, sind in einem
revertierten Build aber mit `wrongKind` nicht mehr einlesbar — wer revertiert, revertiert nicht
zwischen einem Undo-Lauf und dem Restore aus seiner Datei. Die Weiche verschwindet mit dem Revert;
Übertragungsdateien laufen dann wieder direkt in den Restore. Die Registrierung beim Arbiter aus
#256 geht mit zurück; der Arbiter selbst bleibt, wie #256 ihn gebaut hat.

---

## 9. Ledger

`.superpowers/sdd/Plan-254-Replace-Undo/progress.md` (gitignoriert wie bei Plan-230 und Plan-253).
Je Welle: Preflight der Nähte aus Abschnitt 2, Dispatch mit BASE-SHA und Modell, Bericht, Review,
Rulings mit „cost if wrong", Merge-SHA. Der T0-Bericht (Arbiter-Namen, Baseline) steht dort zuerst
und ist Teil jedes Briefs ab T4; die Entscheidungen zu K1–K4 stehen in Spec 17 und in Abschnitt 7;
die Codex-Zweitmeinung (T9) und die Live-Befunde (T10) landen dort, bevor sie in den PR-Text
wandern.

---

## 10. Nachtrag: Codex-Adversarial-Review über die erste Fassung (gpt-6-sol, 2026-09-25)

Urteil „needs-attention", drei Befunde `high`, alle gegen den Code geprüft, keiner widerlegt, alle
in Fassung 2 eingearbeitet. Eine weitere Codex-Runde auf den Plan gibt es nicht (Betreiber).

| # | Schwere | Befund | Prüfung | Lösung | Wo |
|---|---|---|---|---|---|
| 1 | high | Über den Mischdatei-Pfad kann eine unbelegte `planned`-Löschung durchrutschen: K2 ließ `addOnly` ohne Häkchen starten, T5 gab nur „Plan plus Flag" zurück, weder T6 noch T4 verlangten das Herausfiltern unbelegter `full`-Zeilen an der Grenze, T8 prüfte nur den gesperrten Download | Zutreffend: die erste Fassung hatte die Sperre nur im Dialog und keinen Test über die Naht Dialog → Flow → Dienst | Dialog-Ergebnis mit expliziten `runnable`/`skipped`; zweite Herkunftssperre im Dienst vor dem Queue-Aufbau; Unit-Test mit dem echten Dienst (T6) und E2E (T8, Fall 12) verfolgen eine gemischte `planned`-Datei über „Starten" und belegen null REMOVEs — **Betreiber: K2 so entschieden** | 0.5, 1, 2.5, T4, T5, T6, T8, Abschnitt 7 K2, Spec 17 K2 |
| 2 | high | K4 lässt sich in der freigegebenen Zeilenform nicht abbilden: `TransferUndoRow` verlangt `mode` und `restoredTarget`, ein vor der Klassifikation übersprungener Kandidat hat beides nicht; T1 hätte den Builder vor der Entscheidung gebaut | Zutreffend: 6.4 kennt nur gelaufene Zeilen; `duplicateInFile` entsteht in Schritt 0 vor jedem Modus | Eigene Zeilenart `kind: 'skipped'` (ohne `mode`/`restoredTarget`), Restore-Parser lässt sie aus, `counts.requested` nur gelaufene; **vor T1** festgelegt, mit Tests für `duplicateInFile` und je einen Dialog-, Frischcheck- und Dienst-Grund — **Betreiber: K4 so entschieden** | 0.5, 1, T1, T4, Abschnitt 7 K4, Spec 17 K4 |
| 3 | high | K1 schlug ein Bildfeld vor, das die Vorschau-Query nicht liest: `SevenTvApiClient.cs` liest `flags.animated` und baut die URL getrennt, `Emote.images` absichtlich nicht; ein falsches GraphQL-Feld bräche jeden Set-Read; T5 verbot die Ableitung und gab dem Ziel nur einen Platzhalter | Zutreffend: `SevenTvApiClient.cs:71` (`flags { animated }`), `:1272-1280` (`BuildForeignImageUrl`), `emote-url.ts` (keine Ableitung aus der ID im Frontend) | Live-Read += `flags { animated }`; Quelle über `emoteStillUrl(id, animated)` nach der Backend-Regel (`4x_static.webp` nur bei `animated`, sonst `4x.webp`); Ziel Platzhalter mit Name und Aliasen; Tests für statisch und animiert — **Betreiber: K1 so entschieden** | 0.2, 0.5, T5, Abschnitt 7 K1, Spec 17 K1 |

---

## 11. Nachtrag: Stand nach #256 (T0, 2026-09-26)

T0 hat `origin/feat/emote-sets-200` (`ac8c2ec5`, mit #264, #255 und #256) per Merge in den Branch
geholt und den Vertrag aus Spec 11.1 am Code geprüft: **P1–P6 sind erfüllt.** Belege je Punkt,
die tatsächlichen Namen und die Bundle-Baseline (Initial total 404.08 kB) stehen im Ledger
(Abschnitt 9). Dieser Nachtrag hält fest, was #256 (und #255) an diesem Plan ändert. Wo er einer
früheren Stelle widerspricht, gilt der Nachtrag; die Spec bleibt die Vertragsquelle.

**Festlegung Nr. 9 entfällt.** #256 Punkt 5 ist gelandet: die Maschine `idle → verifying → saved`
liegt als `RecoveryFileGate` in `shared/seven-tv/recovery-file-gate.ts`, mit `initialRead`,
`reload(target)`, `lastRead` und `verifyRead` eigens für den Undo-Dialog (Klassendoku dort). Spec
11.3 „konsumieren, wenn extrahiert" greift. Der Undo-Dialog baut keine eigene Maschine.

**Die Platzhalter aus 0.2, 0.5, 0.6 und 2.3 sind aufgelöst.** Registrierung im Konstruktor über
`register` mit `kind`, `isRunning`, `isSettling`, `destructiveOpen`; Union `SevenTvRunKind`; Grund
`activeClaim` mit Sorte und Phase; Notiz über `noteRefusedStart` und `refusedStart`, die die
Nutzungsseite schon zeigt; Schutz-Signal `destructiveOpen`, Union und einziger `beforeunload` im
Arbiter. Zwei Pflichten, die der Vertrag nicht nennt: der Dienst muss **root** sein (es gibt keine
Abmeldung), und der Compiler verlangt einen Eintrag in `SEVEN_TV_RUN_KIND_LABEL_KEY` samt
`sevenTvRun.kind.undo` in beiden Locales.

**Der „Satz offener Läufe" aus T4 ist `SevenTvRunLifecycle`.** Import, Delete und Restore bauen
darauf; `isSettling` und `destructiveOpen` sind dessen Projektionen. Der Undo-Dienst baut ebenfalls
darauf, statt eine eigene Buchführung zu führen. Die Verweise in 0.2 auf modulprivate Funktionen
des Imports sind teilweise veraltet: `applyIfCurrent` gibt es nicht mehr, an seine Stelle treten
`update` und `patchRun` am Lifecycle.

| Task | Änderung durch #256 / #255 | Was der Brief anders sagen muss |
|---|---|---|
| T1 | keine inhaltliche | typisierte Live-Reads brauchen `occupiedSlots` (#255) |
| T2 | keine inhaltliche | dito |
| T3 | Engine hat `showFinishedRows` und den `reset()`-Vorbehalt | Hook berührt beides nicht; `seven-tv-run-lifecycle.spec.ts` läuft als Regressionsschutz mit |
| T4 | Lifecycle, Registrierung, Meldungs-Zeitrahmen, `reshow`, geänderte `resetIfChannelChanged`-Semantik, geteilter `channelMismatch` | Laufdatensatz erweitert den Lifecycle-Basistyp, `destructive` = mindestens eine `full`-Zeile nach der Herkunftssperre; Muster aus #256 T1/T2 übernehmen: `open` vor dem Engine-Start und Rücknahme bei Ablehnung, Klassifizierung vor dem Retry, Zeitrahmen je Meldungsversuch, Queue erst nach dem Lauf leeren, aus `closed` keine Phasenänderung, abgelösten Lauf bei gescheiterter Meldung wieder zeigen; `resetIfChannelChanged` löst nur einen geschlossenen Lauf, ein meldender folgt dem Kanalwechsel (Plan-256 Festlegung 13); Retry-Sperre über `isChannelMismatch`; REMOVE und ADD mit `transportLossIsUnknown`, ein Request in der Luft beim Abbruch ist `unknown` (Spec 4.4 Nr. 11) — der offene Fehler aus #275 darf nicht übernommen werden; Arbiter-Spec +3 gegen den Stub-Block bzw. die echten Dienste |
| T5 | `RecoveryFileGate` statt eigener Maschine; `occupiedSlots` im Read | Gate mit dem Read des Flows als `initialRead`, „Ziel neu laden" über `reload`, Klassifikation aus `lastRead`, Sichern ohne zweiten Request über `verifyRead` aus dem gehaltenen Read, keine Drift-Prüfung im Gate. **Planidentität:** das Gate vergleicht Pläne per Identität — der effektive Plan ist ein `computed` ohne eigenen Gleichheitsvergleich und liest den Read **und** das Häkchen, sonst bleibt `saved` nach Neuladen oder Umschalten stehen (AK 6, K2). Slot-Belegung nach Neuladen aus dem Read wie im Import-Dialog. Der Dialog-Spec prüft die Verdrahtung, nicht die Maschine (das Gate hat eigene Fälle); die Zahl „≥ 18" darf sinken, wenn Maschinenfälle entfallen — im Bericht nennen |
| T6 | Notiz-Mechanik des Arbiters; Arbiter-Stubs ohne `register` | Ablehnung über `noteRefusedStart('undo')` wie der Import-Flow, keine eigene Notiz; Specs mit Arbiter-Stub (z. B. `import-trigger.spec.ts`) brauchen einen Stub des Undo-Dienstes oder `register` am Stub; F10-Stellen sind verschoben — per Suche nach der Übertragungsdatei statt nach Zeilennummern |
| T7 | Schließen erst bei `closed`; Partial-Texte am Panel; `untracked`-Block im Layout; `unknownRecordedIn` des Imports | Section bietet Schließen nur für einen geschlossenen Lauf; Panel-Familie braucht die Partial-Schlüssel neben den Failed-Schlüsseln; `resetIfChannelChanged` des Undo **innerhalb** des `untracked`-Blocks; `undo.summary.unknownRecordedIn` spiegelt Aufbau und Stelle des Import-Hinweises; keine eigene Notiz für abgewiesene Starts; Leave-Guard bleibt bei `isRunning` |
| T8 | E2E-Bestand 194 Fälle; #256-Fälle zu Settling-Fenster und Unload-Schutz | die #256-Fälle sind das Muster für einen Undo-Fall „Start während der Undo settelt" und „Tab-Schutz bis zur zweiten Meldung" |
| T9 | Baseline 404.08 kB | unverändert |

**Nebenbefund, zu entscheiden vor Welle 2.** T5 macht den Live-Read um ein Pflichtfeld für die
Animiertheit reicher (K1), während T4 parallel Specs mit typisierten Reads schreibt; T2 tut es
davor. Nach dem Zusammenführen bräche die Typprüfung der Specs. Empfehlung: die Read-Erweiterung
samt Fixture-Nachzug vor Welle 2 ziehen (an T2 oder als eigener kleiner Schritt), sodass T4 und T5
auf dem erweiterten Typ bauen.

**Nachtrag 2026-09-26 (Merge T5 + Schichten-Fix).** Die in T4 notierte Schichten-Ausnahme ist beim
Zusammenführen von T5 aufgelöst worden: `undo-plan.ts` liegt jetzt unter
`web/src/app/core/seven-tv/undo-plan.ts`, die Kandidaten-/Dateitypen (`UndoCandidate`,
`UndoCandidateTargetEntry`, `UndoSourceFileInfo`) in einer neuen Datei
`web/src/app/core/seven-tv/undo-candidate.ts`, und `buildUndoRunProtocol` samt seinen beiden
Hilfsfunktionen ist aus dem Dienst heraus nach `web/src/app/shared/export/transfer-undo-export.ts`
gewandert. `web/src/app/shared/export/transfer-run-export.ts` importiert die Kandidatentypen jetzt
per `import type` aus `core/` und reicht sie per `export type` an ihre bisherigen Importierer weiter,
sodass an deren Stellen nichts geändert werden musste.

**Nachtrag 2026-09-26 (T8, Wortlaut).** Der Dock-Knopf, den T7 unter „Ergebnisprotokoll
speichern" plant (Dateiliste von T7), heißt in der Umsetzung „Ergebnisprotokoll herunterladen" —
wie beim Löschen und beim Import. Der stille Hinweis `protocolNotSaved` bleibt beim „gespeichert".
Die Abweichungen der Umsetzung vom Spec-Text stehen gesammelt in Spec Abschnitt 18.
