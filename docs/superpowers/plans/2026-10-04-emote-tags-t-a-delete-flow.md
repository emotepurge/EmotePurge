# Emote-Tags T-A: `DeleteProgressSection` und `delete-flow.ts` aus `MassDeletePanel` ziehen (#201) — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan, die Spec
> `docs/superpowers/specs/2026-10-04-emote-tags-design.md` (Abschnitte 3.4, 9.5, 12.1 Zeile T-A)
> und die hier zitierten Dateien; er rollt die Betreiberentscheidungen der Spec **nicht** neu auf.
> Schritte sind als Checkbox (`- [ ]`) geführt. **Kein fertiger Code in diesem Plan** — Verträge,
> Namen, Grenzfälle und Reihenfolge ja, Rümpfe nein. Zeilenangaben gelten für `2db77d26` und sind
> Orientierung, kein Vertrag.
>
> Arbeitsort: eigener Worktree auf einem Branch `refactor/201-t-a-delete-flow` **von `main`**,
> angelegt **nach** dem Merge von Epic #303 (`feat/emote-sets-200`) auf `main` **und nach** dem
> bindenden Messlauf (ab 2026-10-08, Spec 12.2). Commits und Push auf den Feature-Branch sind
> erlaubt, der Merge nicht (Regel 1); PR gegen `main`. Kein `docker compose` aus dem Worktree, kein
> zweiter Worker gegen die Dev-Datenbank. Reines Frontend: kein Backend-Task, keine Migration, kein
> neuer `ApiErrorCode`, kein Deploy-Schritt außer dem Api-Image (das Frontend liegt darin).

**Ziel:** Die Lauf-Oberfläche des Lösch-Laufs (Fortschritt, Protokoll-Download, „Wiederherstellen"
samt Vorprüfkette) und die Vorprüfkette vor dem Lösch-Start (Set-Prüfung, Bestätigungsdialog,
Live-Lesung mit `complete`-Pflicht, Set-Wechsel-Abbruch, `startDelete`) verlassen die Komponente
`MassDeletePanel` und werden als `DeleteProgressSection` bzw. `delete-flow.ts` wiederverwendbar —
damit T-C die Tags-Seite ohne Panel mit einem Lösch-Dock ausstatten und den Ausräum-Lauf über
dieselbe Vorprüfkette starten kann. **Reiner Refactor ohne Verhaltensänderung:** gleiche
Reihenfolge, gleiche Texte, gleiche Sperren, gleiche Live-Regionen; die bestehenden Erwartungen in
Vitest und E2E bleiben wörtlich bestehen.

**Architektur (2–3 Sätze):** `MassDeletePanel` behält den Knopf „Löschen (n)", die Host-Sperre mit
Grund, „Auswahl aufheben", den Token-Prompt, die Status-Region für Abbruchnotizen und die beiden
Latch-Effekte (`deleted`/`reloadRequested`) — und mountet in seiner Vorlage an der Stelle des
heutigen Fortschrittsblocks die neue `DeleteProgressSection`. Die Section ist eigenständig (keine
Inputs, liest `SevenTvDeleteService`/`SevenTvRestoreService` direkt) und meldet Notizen ihrer
Restore-Vorprüfkette über einen Output an den Host. Die Kette zwischen Klick und `startDelete` wird
zur reinen Funktion `startDeleteFlow(deps, request)` in `shared/seven-tv/delete-flow.ts` nach dem
Muster von `restore-flow.ts`/`undo-flow.ts`, mit zwei getrennt exportierten Bausteinen
(`resolveDeleteTarget`, `readLiveSetAliases`), die T-C wiederverwendet.

**Tech-Stack:** Angular 22 (Standalone, Signals, CDK Dialog), Vitest, Playwright, Transloco;
Designsprache `docs/UI-Designsprache.md` §2.5, §4.2, §4.5, §8.7, §12.

**Spec:** `docs/superpowers/specs/2026-10-04-emote-tags-design.md` — umgesetzt werden 9.5
(Absätze „Von der Tags-Seite" und der Guard-Satz), 12.1 Zeile **T-A**, 12.3 Punkt 1; Grundlage
ist der Befund in 3.4 („Lösch-Lauf", „Lauf-Oberflächen je Seite").

**Abhängigkeiten:** T-A ist von T-B unabhängig (beide können parallel laufen und in beliebiger
Reihenfolge mergen). **T-C setzt T-A und T-B voraus** und konsumiert aus T-A genau die unter
„Verträge" genannten Exporte.

---

## 0. Befund gegen `2db77d26` (verifiziert am 2026-10-04) und Abweichungen vom Spec

| Spec sagt | Code | Folge für diesen Plan |
|---|---|---|
| 3.4/9.5: „Fortschritt, Protokoll und Restore-Knopf des Delete stecken im `MassDeletePanel`; der Restore wurde für #253 (T9) in eine eigene `RestoreProgressSection` gezogen" | Teilweise. Die **Anzeige** des Restore-Laufs liegt in `restore-progress-section.ts`; der **Einstieg** (Knopf `restore.button`, `openRestoreConfirm`, `openRestoreConfirmDialog`, `handleRestoreConfirmPreview`, Restore-Latch-Effekt; ca. `mass-delete-panel.ts:730-1065`, `:638-648`) liegt weiter im Panel. `restore-progress-section.ts:14-18` sagt das selbst. | **Abweichung 1:** Der Restore-**Einstieg** wandert mit in die `DeleteProgressSection` (er hängt an `deleteService.lastRun()`, nicht an der Auswahl). Er wird **verschoben, nicht auf `startRestoreFlow` umgebaut** — `restore-flow.ts:~100-110` dokumentiert, dass das Panel die Kette bewusst eigenständig führt; eine Vereinheitlichung wäre eine Verhaltensfrage und ist Nachläufer. |
| 9.5: „`usageStatsLeaveGuard` → seitenneutraler `sevenTvRunLeaveGuard` … der Guard liest nur den Arbiter" | Falsch: `features/usage-stats/usage-stats-leave.guard.ts:46-47` liest `SevenTvImportService.isRunning()` und `SevenTvUndoService.isRunning()`; den Arbiter berührt er nicht, Delete/Restore fragen nie (Kommentar `:16-18`: der Arbiter-Unload-Guard deckt den Tab). | **Abweichung 2:** Umbenennen und verschieben **ohne** Verhaltensänderung; der Guard liest weiterhin die zwei Services. Er zieht nach `shared/seven-tv/`, **nicht** nach `core/` — er öffnet `shared/ui/confirm-dialog`, und `core/` darf nichts aus `shared/` importieren (Schichtentabelle). |
| 9.5: „Beide Hostseiten mounten die Section **unter** dem Panel" | Alle E2E-Zugriffe auf Fortschritt, „Schließen", „Abbrechen", „Erneut melden", „Protokoll herunterladen" sind auf `page.locator('app-mass-delete-panel')` gescopet (`web/e2e/emote-import.e2e.spec.ts:2540, 2915`, dazu `:3122`); die Vote-Detail-Seite hält das Panel hinter einem eigenen `@if`-Gate (`vote-session-detail-page.html:172-185`), die Restore-Section bewusst **außerhalb** (`:193-195`). Eine Geschwister-Section würde beide Verträge ändern. | **Abweichung 3:** Das **Panel** mountet die Section in seiner eigenen Vorlage an der Stelle des heutigen Fortschrittsblocks (DOM-Verschachtelung bleibt, Gates der Hostseiten bleiben, E2E-Locator bleiben). Die Tags-Seite (T-C) mountet `app-delete-progress-section` **standalone** — dafür ist die Section eigenständig. Hostseiten werden in T-A **nicht** angefasst. |
| 12.1: „bestehende Specs/E2E unverändert grün" | `mass-delete-panel.spec.ts` (4.584 Zeilen) treibt verschobene Member teils über die Komponenteninstanz (`openProtocolExport`, Restore-Kette) statt über das DOM. | **Abweichung 4 (Präzisierung):** E2E bleibt byte-unverändert. Vitest: **Erwartungen** bleiben wörtlich, aber die Fälle, die einen verschobenen Member über die Instanz treiben, wechseln das Fixture (Section statt Panel) und ziehen in `delete-progress-section.spec.ts`. Fälle, die über das DOM gehen, bleiben im Panel-Spec, weil die Section darin gerendert wird. |
| 3.4: „`mass-delete-panel.ts:299-317` … startet `SevenTvRestoreService.startRestore`" | Der Knopf (`:299-319`) ruft `openRestoreConfirm()`; `startRestore` fällt in `handleRestoreConfirmPreview` (`:945` Abkürzung „alles schon da", `:1056` nach Confirm-Prüfung). | Nur Zeilenkosmetik; keine Planfolge. |

Weitere Fakten, auf die die Tasks bauen (Dossier 2026-10-04):

- `MassDeletePanel` (`shared/seven-tv/mass-delete-panel.ts`, 1.539 Zeilen): Selector
  `app-mass-delete-panel`, kein `host:`-Objekt, keine `data-testid`. Inputs `setId`, `channelName`,
  `activeSetId`, `setName`, `selectedEmotes`, `deleteLockReasonKey`, `readLiveAliasesFromActiveSet`,
  `readLiveAliasesFromSet`, `leadingActionsPresent`; Outputs `deleted`, `reloadRequested`,
  `selectionCleared`. Öffentlich: `deleteLockReasonId` (von `usage-stats-page.html:1196` über
  `#massDeletePanel` gelesen; `usage-stats-page.spec.ts:5307,5470` selektiert
  `p[id^="mass-delete-lock-reason-"]`).
- Vorlage: Wrapper `div.flex.flex-col.gap-3` → Aktionszeile (`:191-247`) → Status-Paar
  (`:255-266`, sr-only `role="status"` + `aria-hidden`-Zwilling für `abortNotice`) →
  `@if (deleteService.isRunning() || deleteService.queue().length > 0)` mit
  `<app-run-progress-panel>` (`:268-329`), darin `run-actions` mit Protokoll-Knopf (`:283-285`),
  Unklar-Zeilen (`:286-298`), Restore-Knopf (`:299-319`), Protokoll-Hinweis (`:320-325`).
- Latch-Effekte im Konstruktor: Effekt 1 (`:585-633`, `deleted`/`reloadRequested`, setzt
  `protocolSaved` zurück), Effekt 2 (`:638-648`, Restore-Latch → `reloadRequested`).
- Vorprüfkette: `openConfirm` (`:651`) → `openConfirmDialog` (`:1086`, `resolveEditableSet` mit
  Timeout, Grund `massDelete.errors.*` via `deleteTargetCheckReasonKey`, `setChangedDuringConfirm`)
  → `openConfirmDialogAfterCheck` (`:1133`, friert Set/Aktivität/Kanal ein, `loadSetWarning`,
  `beginConfirmedRun`, `openDeleteConfirmDialog`, Auswahl-Snapshot, `selectionGoneDuringConfirm`)
  → `readLiveAliasesThenDelete` (`:1254`, `loadSevenTvSetEntries` + `LIVE_ALIAS_READ_TIMEOUT_MS`,
  `complete`-Pflicht, `endConfirmedRun` im `finalize`) → `startDelete` (`:1354`, Abbruchgründe,
  Arbiter-Claim, Token, fehlende Zeilen, `DeleteQueueEmote[]`, `deleteService.startDelete(...)`
  `:1457`). Hilfen: `abortReasonBeforeStart`, `wantsLiveAliasRead`, `missingRowsReasonParams`,
  `refusedStartNotice`; Modul-Helfer `deleteTargetCheckReasonKey`, `LiveAliasRead`,
  `MEMBER_READ_*_REASON_KEY`, `LIVE_ALIAS_READ_TIMEOUT_MS`, `DeleteAbortNotice`.
- `resolveEditableSet(emoteSetId, hint?)` liegt auf `SevenTvEmoteSetService`
  (`core/seven-tv/seven-tv-emote-set.service.ts:346`), nicht auf `EmoteAdminService`.
- Flow-Muster: `restore-flow.ts` (`RestoreFlowDeps` mit `previewPending: WritableSignal<boolean>`,
  `destroyRef`), `undo-flow.ts` (`UndoFlowDeps`), `import-flow.ts` (`ImportFlowDeps`).
- Section-Muster: `restore-progress-section.ts` (Selector `app-restore-progress-section`, keine
  Inputs, injiziert nur den Service, alle Notizen `aria-hidden`, Ansage über
  `DockOutcomeAnnouncer`).
- Guard: `usageStatsLeaveGuard: CanDeactivateFn<unknown>` (`usage-stats-leave.guard.ts:40`),
  registriert in `usage-stats.routes.ts:20-24`; Spec mit sechs Fällen; `usage-stats.routes.spec.ts`
  prüft `routeConfig`-Identität.

---

## 1. Globale Zwänge (gelten für jeden Task)

- **Schichtentreue `web/`:** `core/` importiert nichts aus `shared/`/`features/`; `shared/` nichts
  aus `features/`. Alle neuen Dateien dieses Plans liegen in `shared/seven-tv/`.
- **Regeln** 12 (Verhalten, nicht Vorlage: keine CSS-Klassen, keine Snapshots, keine Wortlaute
  als Prüfgegenstand), 13 (kein required Input im Konstruktor), 14 (`computed()` nur über Signale),
  18 (`npm --prefix web run format`, `lint`), 19/Member-Reihenfolge nach `web/.claude/CLAUDE.md`.
- **Sprache:** Bezeichner, Kommentare, Commit-Messages, DECISIONS-Eintrag englisch; dieser Plan
  deutsch. Conventional Commits; Regel 3: der Commit, der die Komponenten-Topologie ändert, trägt
  seinen DECISIONS-Eintrag (Task 4 fasst Doku und Topologie-Commit zusammen — s. dort).
- **Kein Verhaltenswechsel.** Jede Entscheidung, die das Nutzererleben ändern könnte (Reihenfolge,
  Texte, Sperren, Live-Regionen, Fokus), ist in T-A verboten und wird als Nachläufer notiert.
- **E2E** importiert `test`/`expect` nur aus `web/e2e/support/test.ts`; läuft nur ohne Api auf
  `:5151`; `page.clock` ist hier nicht nötig (keine neuen Timer).
- **Audit-Harness** (§12): nach Task 2 die Dock-Szenarien messen und gegen eine Basismessung von
  `main` vergleichen — der Beweis, dass der zusätzliche Host-Knoten der Section nichts verschiebt.

---

## 2. Dateikarte

| Datei (unter `web/src/app/` bzw. `web/`) | Art | Verantwortung |
|---|---|---|
| `shared/seven-tv/delete-flow.ts` | **neu** | Vorprüfkette Set-Prüfung → Dialog → Live-Lesung → `startDelete` als reine Funktionen; Modul-Helfer und Typen, die heute im Panel liegen. |
| `shared/seven-tv/delete-flow.spec.ts` | **neu** | Verhalten der exportierten Bausteine (`resolveDeleteTarget`, `readLiveSetAliases`, Grund-Abbildung), soweit nicht schon vom Panel-Spec über das DOM charakterisiert. |
| `shared/seven-tv/delete-progress-section.ts` | **neu** | Fortschritt, Protokoll-Download, Unklar-Zeilen, Restore-Einstieg samt Kette, `protocolSaved`; Output `notice`. |
| `shared/seven-tv/delete-progress-section.spec.ts` | **neu** | Aufnahme der Panel-Spec-Fälle, die verschobene Member über die Instanz treiben (Erwartungen unverändert). |
| `shared/seven-tv/mass-delete-panel.ts` | ändern | Behält Knopf, Sperre, Exit, Token-Prompt, Status-Paar, Latches; ruft `startDeleteFlow`; mountet die Section. |
| `shared/seven-tv/mass-delete-panel.spec.ts` | ändern | Verschobene Instanz-Fälle raus (in die Section-Spec), DOM-Fälle bleiben; Provider ggf. um die Section-Abhängigkeiten ergänzt. |
| `shared/seven-tv/seven-tv-run-leave.guard.ts` | **neu (verschoben)** | Ex-`usageStatsLeaveGuard`, unverändertes Verhalten. |
| `shared/seven-tv/seven-tv-run-leave.guard.spec.ts` | **neu (verschoben)** | Die sechs bestehenden Fälle. |
| `features/usage-stats/usage-stats-leave.guard.ts` + `.spec.ts` | **löschen** | Ersetzt durch den verschobenen Guard. |
| `features/usage-stats/usage-stats.routes.ts` (+ `.spec.ts`) | ändern | Import des Guards aus `shared/seven-tv/`. |
| `docs/DECISIONS.md` | ändern | Neuer Eintrag oben (englisch). |
| `docs/UI-Designsprache.md` | ändern | §4.2-, §8.7-Referenzen auf die Section; Satz zum Mount-Ort. |

Nicht angefasst: `usage-stats-page.html/.ts`, `vote-session-detail-page.html/.ts`, alle
`web/e2e/**`, `web/public/i18n/*.json` (keine neuen Schlüssel — ein Refactor braucht keine).

---

## 3. Verträge

### 3.1 `delete-flow.ts`

- `DeleteFlowDeps` — Objekt der bereits injizierten Kollaborateure, analog `RestoreFlowDeps`:
  `dialog`, `emoteAdminService`, `emoteSetService`, `httpClient`, `tokenService`, `deleteService`,
  `arbiter`, `translocoService`, `destroyRef`.
- `DeleteFlowRequest` — alles, was die Kette heute aus dem Panel liest, als **Signale** (damit die
  Kette an denselben Stellen wie heute erneut liest bzw. einfriert): `setId`, `activeSetId`
  (gefaltet wie `effectiveActiveSetId` heute), `channelName`, `setName`, `selectedEmotes`
  (`DeletableEmote[]`), `hostLockReasonKey`, `liveAliasRead: 'none' | 'activeSet' | 'set'`
  (Ableitung aus den beiden heutigen Booleans bleibt im Panel), plus die beiden **beschreibbaren**
  Signale, die das Panel besitzt und die Vorlage liest: `notice: WritableSignal<DeleteAbortNotice | null>`
  und `targetCheckPending: WritableSignal<boolean>`. `destroyed` wird über `deps.destroyRef`
  abgebildet, nicht als Flag übergeben.
- `startDeleteFlow(deps, request): void` — führt exakt die heutige Kette ab
  `openConfirmDialog` aus (die Schritte **vor** `openConfirmDialog` — `abortNotice` leeren,
  Host-Lock, `startLocked`, Token-Prompt — bleiben in `MassDeletePanel.openConfirm`, weil sie am
  Knopf hängen). Jede heutige Einfrier-, Prüf- und Abbruchstelle bleibt an derselben Position der
  Kette; `beginConfirmedRun`/`endConfirmedRun`/`clearConfirmedRun` werden an denselben Stellen
  gerufen.
- Exportierte Bausteine (T-C konsumiert sie direkt):
  - `resolveDeleteTarget(deps, setId, channelName): Observable<DeleteTargetResolution>` — die
    `resolveEditableSet`-Vorprüfung mit demselben Timeout und derselben Grund-Abbildung
    (`deleteTargetCheckReasonKey` → `massDelete.errors.*`). Ergebnis-Union:
    `{ status: 'editable'; ownerTwitchChannelId: string | null }` ·
    `{ status: 'blocked'; reasonKey: string }` (Timeout und Fehler fallen wie heute auf
    `targetCheckUnavailable`). Der Set-Wechsel-Vergleich gegen das aktuelle `setId()` bleibt
    **außerhalb** des Bausteins (Aufruferwissen), genau wie heute im Panel.
  - `readLiveSetAliases(httpClient, setId): Observable<LiveAliasReadResult>` — `loadSevenTvSetEntries`
    mit `LIVE_ALIAS_READ_TIMEOUT_MS`, `complete === false` und Fehler werden zu
    `{ status: 'blocked'; reasonKey }` mit den heutigen beiden Schlüsseln
    (`MEMBER_READ_UNAVAILABLE_REASON_KEY`, `MEMBER_READ_TRUNCATED_REASON_KEY`); Erfolg
    `{ status: 'ok'; entries: SevenTvSetEntries }`.
  - Typen und Konstanten, die heute Modul-Helfer des Panels sind, werden hier exportiert:
    `DeleteAbortNotice`, `LiveAliasRead` (falls nach dem Umbau noch gebraucht),
    `deleteTargetCheckReasonKey`, `LIVE_ALIAS_READ_TIMEOUT_MS`, `MEMBER_READ_*_REASON_KEY`.
- `DeletableEmote` bleibt im Panel exportiert (öffentlicher Input-Typ der Hostseiten); der Flow
  importiert ihn von dort (kein Zyklus: das Panel importiert den Flow, der Flow nur den Typ — wenn
  der Compiler das als Zyklus meldet, zieht `DeletableEmote` **mit Re-Export aus dem Panel** in den
  Flow, damit `usage-stats-page.ts`/`vote-session-detail-page.ts` nichts ändern müssen).

### 3.2 `DeleteProgressSection`

- Selector `app-delete-progress-section`, Imports `Button`, `RunProgressPanel`, `TranslocoPipe`.
  **Keine Inputs.** Injiziert `deleteService`, `restoreService`, `arbiter`, `tokenService`,
  `emoteSetService`, `httpClient`, `dialog`, `destroyRef`, `translocoService` — die Teilmenge, die
  der verschobene Code heute braucht.
- Output `notice = output<DeleteAbortNotice | null>()`: die Restore-Kette setzt heute
  `abortNotice` des Panels (Blockgründe `restore.errors.*`, Refused-Start-Notiz). Sie emittiert
  stattdessen; `null` heißt „leeren" (heute: Leeren beim Start des nächsten Versuchs). Das Panel
  bindet `(notice)="abortNotice.set($event)"` — Text, Ort und Live-Region bleiben identisch. T-C
  bindet den Output auf der Tags-Seite an deren eigene Status-Region.
- Vorlage = der heutige Block `@if (deleteService.isRunning() || deleteService.queue().length > 0)`
  bis zum schließenden `}` (`:268-329`) **wörtlich**, inklusive Reihenfolge der `run-actions`
  (Protokoll · Unklar-Zeilen · Restore · Protokoll-Hinweis). Alles darin bleibt `aria-hidden` wie
  heute bzw. unverändert, weil die Ansage weiterhin vom `DockOutcomeAnnouncer` der Hostseite kommt.
- Verschobene Member: `protocolSaved`, `restoreSlots`, `restoreOffered`, `restoreConfirmPending`,
  `unknownRowCount`, `unknownRowsKey`, `unknownInProtocolKey`, `openProtocolExport`,
  `openRestoreConfirm`, `openRestoreConfirmDialog`, `handleRestoreConfirmPreview`,
  `restorableItems`, `restoreTargetCheckReasonKey`, `RestoreCandidate`, der
  `refusedStartNotice`-Helfer (wird von beiden Ketten gebraucht → in `delete-flow.ts` exportiert
  und von Panel **und** Section importiert).
- **Was im Panel bleibt und warum:** die beiden Latch-Effekte (`:585-648`), weil sie die Outputs
  `deleted`/`reloadRequested` speisen, die beide Hostseiten binden — ein Umzug änderte den
  Host-Vertrag. Effekt 1 setzt `protocolSaved` zurück: dieser Reset wandert in die Section (eigener
  Effekt auf `deleteService.run()`-Phase `running`), damit die Section den Zustand allein hält.
  **Grenzfall:** Beide Effekte laufen nach dem Umbau in zwei Komponenten über denselben Lauf; die
  Reihenfolge ihrer Ausführung ist für das Ergebnis egal (sie schreiben verschiedene Signale).

### 3.3 `MassDeletePanel` nach dem Umbau

- Vorlage: Aktionszeile, Status-Paar, dann `<app-delete-progress-section (notice)="abortNotice.set($event)" />`
  an der Stelle des alten Blocks — als Kind des `div.flex.flex-col.gap-3`. Das Host-Element der
  Section ist ein Block-Element ohne eigene Maße; der innere Flow-Inhalt bleibt ein Flex-Kind. Die
  Section rendert nichts, wenn weder Lauf noch Queue existieren, also entsteht **kein** leerer
  `gap`-Beitrag — das ist derselbe Zustand wie das heutige `@if` ohne Inhalt. (Falls das
  Host-Element im Audit-Harness doch einen Abstand erzeugt, wird es per `host: { class: 'contents' }`
  entschärft — nach Vorbild des Tab-Links in §8.1 —, nicht durch Umbau der Hierarchie.)
- `openConfirm` bleibt bis einschließlich Token-Prompt; danach `startDeleteFlow(deps, request)`
  mit einem im Panel gebauten `deps`-Objekt und einem `request` aus den eigenen Signalen.
- Entfernt werden alle in 3.1/3.2 genannten Member; `abortNotice`, `deleteTargetCheckPending`
  (Alias auf das `targetCheckPending` im Request) und `deleteLockReasonId` bleiben.

### 3.4 `sevenTvRunLeaveGuard`

- Exportname `sevenTvRunLeaveGuard: CanDeactivateFn<unknown>` in
  `shared/seven-tv/seven-tv-run-leave.guard.ts`; Inhalt identisch zu `usageStatsLeaveGuard`
  (Import-/Undo-Service, `leadsToSameRoute`, Dialog-Wortlaute, Import gewinnt). Der
  Klassenkommentar wird um den Satz ergänzt, dass T-C ihn auch in `tags.routes.ts` setzt und dass
  er **bewusst** Delete/Restore nicht fragt (Arbiter-Unload-Guard deckt den Tab) — das ist
  Dokumentation des Ist-Zustands, kein neues Verhalten.

---

## 4. Tasks

Reihenfolge: T1 → T2 → T3 → T4. T3 ist von T1/T2 unabhängig und darf parallel zu T2 laufen.

### Task 1 — `delete-flow.ts`: Vorprüfkette als reine Funktion

**Modell:** opus (die Kette hält drei Einfrierpunkte, zwei Gates am `deleteService`, einen
Auswahl-Snapshot und vier Abbruchpfade; ~40 Spec-Fälle beschreiben sie). **Kontext für den
Subagent:** Spec 3.4 („Lösch-Lauf"), Plan 0, 1, 3.1, 3.3; `mass-delete-panel.ts` vollständig;
`restore-flow.ts:40-140` und `undo-flow.ts:30-120` (Deps-Muster); `mass-delete-panel.spec.ts`
Describe-Blöcke „Active-set delete records every alias from a live read" (ab `:2135`, inkl. „the
window before the start (#280)" `:2597`), „readLiveAliasesFromSet #227" (`:2968`), „Shared
pre-check before the delete confirmation #253 AK 31" (`:4400`), „The host lock is re-checked at
confirm time #200 K4" (`:1990`); `web/.claude/CLAUDE.md` (Member-Reihenfolge, Regel 12).

**Dateien:** neu `shared/seven-tv/delete-flow.ts`, `delete-flow.spec.ts`; ändern
`mass-delete-panel.ts`, ggf. `mass-delete-panel.spec.ts` (nur Provider/Imports, keine Erwartung).

**Schnittstellen — konsumiert:** `SevenTvEmoteSetService.resolveEditableSet(emoteSetId, hint?)`,
`loadSevenTvSetEntries(httpClient, setId)`, `openDeleteConfirmDialog(dialog, data)`,
`SevenTvDeleteService.startDelete/beginConfirmedRun/endConfirmedRun/clearConfirmedRun/startCheckPending`,
`SevenTvRunArbiter.activeClaim/activeRun/noteRefusedStart`, `refusedStartMessage`,
`EmoteAdminService.getSetWarning`. **Produziert:** `DeleteFlowDeps`, `DeleteFlowRequest`,
`startDeleteFlow`, `resolveDeleteTarget`, `DeleteTargetResolution`, `readLiveSetAliases`,
`LiveAliasReadResult`, `DeleteAbortNotice`, `deleteTargetCheckReasonKey`,
`LIVE_ALIAS_READ_TIMEOUT_MS`, `MEMBER_READ_UNAVAILABLE_REASON_KEY`, `MEMBER_READ_TRUNCATED_REASON_KEY`,
`refusedStartNotice`-Helfer (Name beibehalten).

- [ ] `delete-flow.ts` anlegen; Kopfkommentar nennt Herkunft (aus `MassDeletePanel`), dass das
      Panel die Schritte **vor** der Set-Prüfung behält, und dass `resolveDeleteTarget`/
      `readLiveSetAliases` getrennt exportiert sind, weil der Ausräum-Lauf (#201 T-C) dieselben
      zwei Prüfungen vor einem **eigenen** Dialog braucht.
- [ ] Code **verschieben**, nicht neu schreiben: `openConfirmDialog`, `openConfirmDialogAfterCheck`,
      `readLiveAliasesThenDelete`, `startDelete`, `abortReasonBeforeStart`, `wantsLiveAliasRead`,
      `missingRowsReasonParams`, `loadSetWarning`, `refusedStartNotice` und die Modul-Helfer. Jeder
      `this.`-Zugriff wird zu einem Zugriff auf `deps`/`request`; `destroyed` wird über
      `deps.destroyRef` (Flag im Flow-Scope) abgebildet; `takeUntilDestroyed(deps.destroyRef)` wo
      heute `takeUntilDestroyed(this.destroyRef)`.
- [ ] `resolveDeleteTarget` und `readLiveSetAliases` als eigene Funktionen herauslösen, die die
      Kette intern benutzt (keine zweite Implementierung).
- [ ] `MassDeletePanel.openConfirm` ruft nach dem Token-Prompt `startDeleteFlow`; das Panel baut
      `deps` einmal (Feld) und `request` aus seinen Signalen; `deleteTargetCheckPending` wird das
      Signal, das als `request.targetCheckPending` hineingeht.
- [ ] Grenzfälle, die der Umbau nicht verändern darf (jeder ist heute ein Spec-Fall): Host-Lock
      zur Confirm-Zeit; Set-Wechsel nach der Set-Prüfung **und** nach der Live-Lesung; leere
      Auswahl nach dem Dialog (`selectionGoneDuringConfirm`); Arbiter-Claim während der Lesung;
      Token weg hinter der Bestätigung; Dialog-Abbruch → `clearConfirmedRun`; Lese-Fehler/
      `complete === false`/Unterzählung → blockt mit Grund und `endConfirmedRun`; `destroyed`
      während der Lesung → nichts startet; Lesung nur bei Opt-in und im passenden Set.
- [ ] `delete-flow.spec.ts` (Vitest, Regel 12): `resolveDeleteTarget` — `editable` liefert
      `ownerTwitchChannelId`; `notEditable`/`notSelectable`/`unavailable` → `blocked` mit dem
      jeweiligen `massDelete.errors.*`-Schlüssel; Timeout → `targetCheckUnavailable`; Fehler →
      dito. `readLiveSetAliases` — `complete === false` → `blocked` mit dem Truncated-Schlüssel;
      Fehler/Timeout → Unavailable-Schlüssel; Erfolg reicht `entries` durch. **Nicht** doppelt
      prüfen, was der Panel-Spec über das DOM bereits charakterisiert.
- [ ] Panel-Spec: alle Fälle aus den vier genannten Describe-Blöcken bleiben **wörtlich** grün
      (sie treiben die Kette über Knopf/Dialog/Service-Mocks). Provider-Ergänzungen sind erlaubt,
      Erwartungsänderungen nicht; ein roter Fall ist ein Befund gegen den Umbau, nicht gegen den
      Test.
- [ ] Gates: `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`,
      `npm --prefix web run format:check`.
- [ ] Ein Commit: `refactor(web): extract the delete pre-check chain into delete-flow`.

**Abnahme:** `git diff --stat` zeigt für `mass-delete-panel.spec.ts` nur Import-/Provider-Zeilen;
`mass-delete-panel.ts` enthält keine der verschobenen Methodennamen mehr; die Zeilenzahl von
`delete-flow.ts` + Rest-Panel ≈ alte Panel-Zeilenzahl (Verschiebung, kein Neuschrieb).

### Task 2 — `DeleteProgressSection`: Fortschritt, Protokoll, Restore-Einstieg

**Modell:** opus (Restore-Kette ~420 Zeilen mit Gates, Timeouts, Snapshot; 21 + 16 Spec-Fälle).
**Kontext für den Subagent:** Spec 3.4, 9.5; Plan 0 (Abweichungen 1, 3, 4), 3.2, 3.3;
`mass-delete-panel.ts` (nach Task 1), `restore-progress-section.ts` vollständig (Muster),
`run-progress-panel.ts:28-80` (Inputs/Slots), `dock-outcome-announcer.ts:260-380`;
`mass-delete-panel.spec.ts` Blöcke „Protocol export choice handling #141" (`:296`), „Unknown rows
summary and settling gap #275 T4" (`:1372`), „Restore latch #89" (`:1575`), „Restore-confirm path
resolves target fresh …" (`:3322`), „Unclear rows offered for restore, fail-closed #275" (`:4057`,
inkl. „the confirm-time check holds the restore entry (#280)" `:4321`), „Schließen-Gate #256"
(`:1277`); `web/e2e/emote-import.e2e.spec.ts:2530-2560, 2905-2925, 3115-3130` (Locator-Scoping);
`docs/UI-Designsprache.md` §4.5 (Ansage aus dem Announcer), §8.7 („Dock trägt den Laufzustand"),
§12.

**Dateien:** neu `shared/seven-tv/delete-progress-section.ts`, `.spec.ts`; ändern
`mass-delete-panel.ts`, `mass-delete-panel.spec.ts`.

**Schnittstellen — konsumiert:** `SevenTvDeleteService.queue/isRunning/run/lastRun/syncReport/
syncReportReason/rateLimitPauseSeconds/cancel/reset/retrySyncReport`,
`SevenTvRestoreService.startRestore/restorePreCheckPending/startCheckPending/isRunning/queue`,
`loadRestoreSlotPreview`, `openRestoreConfirmDialog`, `restoreStartTarget`, `ResolvedRestoreTarget`,
`filterAlreadyPresent*`, `buildPurgeRunProtocol`/`purgeRunCsv`/`purgeRunJson`/`purgeRunFilename`,
`openExportDialog`, `downloadFile`, aus Task 1 `refusedStartNotice`, `DeleteAbortNotice`.
**Produziert:** `DeleteProgressSection` (Selector `app-delete-progress-section`, Output `notice`).

- [ ] Section anlegen (Vorlage und Member nach 3.2, Code verschieben). Kopfkommentar: Herkunft,
      warum der Restore-**Einstieg** hier liegt (er hängt am letzten Lösch-Lauf, nicht an der
      Auswahl), warum die Latches im Panel bleiben (Host-Outputs), warum `notice` ein Output ist
      (die Status-Region gehört dem Host — Panel oder Tags-Seite), und dass die Tags-Seite (T-C)
      die Section ohne Panel mountet.
- [ ] `protocolSaved`-Reset als eigener Effekt in der Section (3.2); Effekt 1 im Panel verliert
      nur diese eine Zeile.
- [ ] Panel: Block `:268-329` durch `<app-delete-progress-section (notice)="abortNotice.set($event)" />`
      ersetzen; nicht mehr gebrauchte Imports/Injektionen entfernen; `imports` um die Section
      ergänzen.
- [ ] Spec-Umzug nach Regel „Erwartungen unverändert, Fixture wechselt": Fälle, die
      `openProtocolExport`, Restore-Einstieg, `restoreOffered`, `unknownRowCount` o. ä. über die
      **Instanz** treiben, nach `delete-progress-section.spec.ts` (gleiche Titel, gleiche
      Assertions, Fixture = Section mit denselben Service-Mocks); Fälle, die über das DOM gehen,
      bleiben im Panel-Spec und müssen grün bleiben, weil die Section darin gerendert wird. Zwei
      neue Fälle für den Output: Restore-Kette blockiert → `notice` emittiert die
      `restore.errors.*`-Notiz; nächster Versuch → `notice` emittiert `null` zuerst.
- [ ] Panel-Spec, neuer Fall: eine über `notice` gemeldete Notiz steht in der **permanenten**
      `role="status"`-Region des Panels (Rolle + Text, nicht die Klasse) — der Beweis, dass der
      Ort der Ansage gleich geblieben ist.
- [ ] Audit-Harness (§12): Basismessung von `main` **vor** dem Umbau (Verzeichnis leeren,
      Lauf, Metriken der Szenarien mit `.app-dock` sichern — `ui-audit.audit.ts:751-768`), dann
      Lauf auf dem Branch; `horizontalOverflowPx`, `smallTargetsUnder24`, `beyondRightEdge`,
      `contrastViolations` je Dock-Szenario **identisch**; Screenshots der Dock-Szenarien
      nebeneinander ansehen (de/en, dark). Ergebnis im Commit-Body.
- [ ] Gates: Vitest, Lint, Format; `npm --prefix web run e2e` (UI-Änderung; **unverändert** grün,
      keine E2E-Datei angefasst).
- [ ] Ein Commit: `refactor(web): extract the delete run surface into DeleteProgressSection`.

**Abnahme:** `git diff web/e2e` ist leer; alle Fälle der genannten Describe-Blöcke existieren
weiterhin (Zahl der `it(` über Panel-Spec + Section-Spec ≥ alte Zahl im Panel-Spec); Audit-Metriken
der Dock-Szenarien ohne Differenz.

### Task 3 — `sevenTvRunLeaveGuard` (Verschiebung)

**Modell:** sonnet. **Kontext:** Spec 9.5 (Guard-Satz); Plan 0 (Abweichung 2), 3.4;
`features/usage-stats/usage-stats-leave.guard.ts` + `.spec.ts`, `usage-stats.routes.ts` +
`.spec.ts`, `app.routes.ts:96-145`; Schichtentabelle in `CLAUDE.md`.

**Dateien:** neu `shared/seven-tv/seven-tv-run-leave.guard.ts` + `.spec.ts`; löschen die beiden
Feature-Dateien; ändern `usage-stats.routes.ts` (+ `.spec.ts`, falls es den Guard importiert).

**Schnittstellen — produziert:** `sevenTvRunLeaveGuard: CanDeactivateFn<unknown>` (T-C setzt ihn
in `tags.routes.ts`).

- [ ] Datei per `git mv` verschieben und umbenennen (Blame bleibt), Export umbenennen, Importpfade
      anpassen; Kommentar nach 3.4 ergänzen (Ist-Zustand dokumentieren, kein Verhalten ändern).
- [ ] Spec verschieben, Titel und Assertions unverändert; ggf. `describe`-Name auf den neuen
      Export.
- [ ] Grep über `web/src` nach `usageStatsLeaveGuard` → null Treffer.
- [ ] Gates: Vitest, Lint, Format. Ein Commit:
      `refactor(web): move the run leave guard to shared/seven-tv as sevenTvRunLeaveGuard`.

**Abnahme:** `usage-stats.routes.spec.ts` grün; die sechs Guard-Fälle grün unter neuem Pfad; kein
Import aus `shared/` in `core/`.

### Task 4 — Doku, Gates, Zweitmeinung, PR

**Modell:** Orchestrator (Opus) für Codex-Lauf und PR; sonnet für die Doku-Änderungen.
**Kontext:** Plan 0, 3; `docs/DECISIONS.md:1-15` (Format, englisch, `**Betrifft:**`),
`docs/UI-Designsprache.md` §4.2 „Reference" (nennt `mass-delete-panel.ts` als Ausführung), §8.7
„Reference" und Satz „The dock also carries the run state", §12; Memory „Codex-Review-Fallen"
(`--scope branch --base origin/main`, Session-CWD im Worktree, Flags in **einem** String);
CLAUDE.md „Fertig heißt" und „Tests".

- [ ] `docs/DECISIONS.md`, neuer Eintrag oben (englisch), Titel
      `### <Commit-Datum> — The delete run surface and its pre-check chain leave MassDeletePanel (#201 T-A)`;
      `**Betrifft:**` alle Dateien aus der Dateikarte. Inhalt: (1) was wo lag und warum das für
      die Tags-Seite nicht reichte (ein Dock ohne Auswahl-Panel, ein Lösch-Lauf mit eigenem
      Dialog); (2) die Schnittlinie — Panel = Auswahl-bezogen + Latches, Section = Lauf-bezogen
      inkl. Restore-Einstieg, Flow = Kette ab Set-Prüfung; (3) Mount-Ort **im** Panel statt als
      Geschwister, mit den drei Gründen aus Abweichung 3; (4) `notice` als Output, Status-Region
      beim Host; (5) der Guard: umbenannt, in `shared/seven-tv/`, weiter nur Import/Undo —
      ausdrücklich als Ist-Zustand festgehalten; (6) Nachläufer: Restore-Einstieg auf
      `startRestoreFlow` vereinheitlichen; Delete/Restore im Leave-Guard.
- [ ] `docs/UI-Designsprache.md`: §4.2 Reference „Execution" ergänzt `delete-progress-section.ts`
      nicht (die Ausführung bleibt der Dialog) — stattdessen §8.7 „The dock also carries the run
      state" bekommt den Halbsatz, dass diese Fläche für den Lösch-Lauf die
      `DeleteProgressSection` ist, die im Panel **und** allein mountbar ist; §8.7 Reference nennt
      sie. Keine Regeländerung.
- [ ] Doku-Commit: `docs: record the delete run surface extraction` — **oder** mit Task 2
      zusammen, wenn der Orchestrator Regel 3 wörtlich nimmt (Topologie-Commit trägt den Eintrag);
      der Task entscheidet und nennt es im Body.
- [ ] Gates vollständig: `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e`
      (nur ohne Api auf `:5151`), `npm --prefix web run lint`, `npm --prefix web run format:check`,
      `dotnet format EmotePurge.slnx --verify-no-changes` (unberührt, aber CI prüft es);
      `dotnet test EmotePurge.slnx` ist nicht berührt und wird **nicht** als Fertigmeldung
      verlangt (kein Backend-Diff) — im PR-Text so benennen.
- [ ] `node scripts/coverage-local.mjs --frontend-only` **nach** dem letzten Commit (misst nur
      Committetes); Ergebnis im PR-Text als Näherung benennen. Erwartung: verschobener Code behält
      seine Fälle; die neuen Dateien sind durch Section-/Flow-Spec gedeckt.
- [ ] Browser-Blick (Betreiber oder Orchestrator mit `npm start` + lokaler Api): Nutzungsseite
      und Vote-Detail — Lösch-Lauf gegen ein **eigenes Test-Set** (nie ein fremdes), Protokoll
      herunterladen, „Wiederherstellen" blockiert/erfolgreich, Abbruchnotiz lesbar; beide
      Farbmodi. Kein Prod-Zugriff.
- [ ] Codex Sol (Regel 22): aus dem Worktree
      `node <plugin>/scripts/codex-companion.mjs review "--scope branch --base origin/main"`,
      vorher `git -C <worktree> diff --shortstat origin/main...HEAD` als Gegenprobe; Modell aus
      `~/.codex/config.toml` im Bericht nennen. Findings unverändert dem Nutzer vorlegen;
      Widerspruch Opus ↔ Sol → Fable als Schiedsrichter.
- [ ] PR gegen `main` (Titel englisch), Body-Checkliste: Verschiebung ohne Verhaltensänderung
      (Zahl der Spec-Fälle vorher/nachher, E2E-Diff leer, Audit-Metriken identisch), die vier
      Abweichungen aus Plan 0, Coverage-Näherung, Codex-Ergebnis, Hinweis „Deploy: nur Api-Image,
      keine Migration, kein Flag".

**Abnahme:** Alle Gates grün in einem Lauf; PR offen; Merge bleibt beim Nutzer.

---

## 5. Spec-Abdeckung

| Spec-Stelle | Task |
|---|---|
| 9.5 „T-A zieht Fortschritt, Protokoll-Download und Restore-Knopf … in eine `DeleteProgressSection`" | Task 2 |
| 9.5 „… und die Vorprüfkette in `shared/seven-tv/delete-flow.ts` (`openConfirm`-Lock, `resolveEditableSet`, Live-Lesung mit `complete`-Pflicht, Set-Wechsel-Abbruch, `startDelete`)" | Task 1 (Lock und Token bleiben am Knopf — Plan 3.1) |
| 9.5 „das Panel behält Knopf und Dialogaufruf" | Task 1, 2 |
| 9.5 „Beide Hostseiten mounten die Section unter dem Panel" | Task 2 — **Abweichung 3**: Panel mountet, Hostseiten unberührt |
| 9.5 „Reiner Refactor ohne Verhaltensänderung, eigener PR, bestehende Specs/E2E bleiben grün" | Task 1–4 — **Abweichung 4**: Erwartungen unverändert, Fixture darf wechseln |
| 9.5 „`usageStatsLeaveGuard` → seitenneutraler `sevenTvRunLeaveGuard`" | Task 3 — **Abweichung 2**: Ort `shared/seven-tv/`, liest weiter zwei Services |
| 12.1 T-A „Prüft: Frontend-Suiten, E2E, Browser-Blick auf beide Hostseiten" | Task 4 |
| 12.1 T-A „Deploy: nur Api-Image; keine Migration" | Task 4 (PR-Text) |
| 12.3/1 | gesamter Plan |
| 12.6 „Vor jedem Merge `/codex:review`" | Task 4 |

## 6. Offene Punkte für den Betreiber

1. **Restore-Einstieg wandert mit** (Abweichung 1) — bestätigen; Alternative wäre, den Einstieg
   im Panel zu lassen, dann hätte die Tags-Seite nach einem Ausräumen keinen
   „Wiederherstellen"-Knopf (13.2 sagt ohnehin, dass ein Restore keine Platzierung erzeugt).
2. **Mount-Ort im Panel statt als Geschwister** (Abweichung 3) — bestätigen; ändert für T-C
   nichts, weil die Tags-Seite die Section ohnehin standalone mountet.
