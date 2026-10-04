# Eine Liste als Auswahl laden (#201, Variante a) — Umsetzungsplan

> **Abgelöst am 2026-10-04 durch [docs/superpowers/specs/2026-10-04-emote-tags-design.md](../specs/2026-10-04-emote-tags-design.md) — das Mod-Team wollte kanalgebundene Tags, keine Datei als Auswahl. Bleibt als Herleitung stehen.**

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan, das Konzept
> `docs/Konzept-Liste-als-Auswahl-2026-10-03.md` und den Issue-Text von #201 samt Kommentar vom
> 2026-09-19; er argumentiert daraus und rollt die Betreiberentscheidungen (Abschnitt 0) **nicht**
> neu auf. Schritte sind als Checkbox (`- [ ]`) geführt. **Kein fertiger Code in diesem Plan** —
> Verträge, Namen, Grenzfälle und Reihenfolge ja, Rümpfe nein.
>
> Arbeitsort: Worktree `/home/dev/projects/EmotePurge-201`, Branch `feat/201-list-as-selection`,
> Basis `origin/feat/emote-sets-200`. Commits und Push auf den Feature-Branch sind erlaubt, der Merge
> nicht (Regel 1). Der PR geht gegen `feat/emote-sets-200`, solange das Epic nicht auf `main` ist
> (Deploy frühestens nach dem 2026-10-08). Kein `docker compose` aus dem Worktree, kein zweiter
> Worker gegen die Dev-Datenbank (Messfenster #69/#73). Reines Frontend: kein Backend-Task, keine
> Migration, kein neuer `ApiErrorCode`.

**Ziel:** Im Datei-Zweig des einen Import-Dialogs lässt sich eine Datei (Emote-Liste, Nutzungs-Export,
Purge-Protokoll, Abstimmungs-Export) **als Auswahl** in das Nutzungsraster laden: Treffer werden
add-only markiert, Nicht-Treffer in einer Vorschau benannt, danach wirken die vorhandenen
Auswahl-Aktionen. Kein 7TV-Schreibzugriff, kein Token, keine Zielprüfung auf diesem Pfad.

---

## 0. Angenommene Betreiberentscheidungen (Konzept Abschnitt 9)

Der Plan setzt die Empfehlungen voraus. Tasks, die bei anderer Entscheidung umzuplanen wären, sind
mit **[E-n]** markiert.

| Nr. | Annahme |
|---|---|
| E-1 | Einstieg über die Radiogruppe „Was soll mit der Datei geschehen?" im `FileImportStep`; sie erscheint nur mit `selectionHost`. Vorgabe „Importieren oder wiederherstellen" (heutiges Verhalten). |
| E-2 | Laden ist add-only; keine Ersetzen-Option. |
| E-3 | Nur ID-Treffer werden markiert; Namensvettern werden gezählt und benannt. |
| E-4 | `transfer-run`/`transfer-undo` werden im Markieren-Modus mit eigenem Schlüssel abgelehnt. |
| E-5 | Abstimmungs-Export: alle Zeilen, keine Schwelle. |
| E-6 | Vorschau im Dialog mit Primärknopf „n markieren"; nicht sofort markieren. |

## 1. Verträge

### 1.1 Reine Bausteine (neu)

- **`web/src/app/shared/export/selection-list-parser.ts`** — `parseSelectionList(envelope: ExportEnvelope<unknown>, fileName: string): ParseSelectionListResult`. Ergebnis `{ ok: true; list: SelectionList } | { ok: false; errorKey: string }`. `SelectionList = { origin: SelectionListOrigin; rows: SelectionListRow[]; duplicatesCollapsed: number; discardedRows: number }`, `SelectionListRow = { sevenTvEmoteId: string; name: string }`, `SelectionListOrigin = { fileName; envelopeKind: 'emote-list' | 'usage' | 'purge-run' | 'voting'; exportedAt: string | null; channelName: string | null; emoteSetId: string | null }`. Abbildung je Sorte nach Konzept Abschnitt 4; `emote-list`/`usage` laufen durch `parseImportSource` und werden auf `SelectionListRow` abgebildet (keine zweite Zeilenprüfung); `purge-run` liest **eigene** Zeilen (ID + `name` als nicht-leere Strings, Status ignoriert, Version 1–3 — nicht `parsePurgeRunProtocol`, das nur `done`/`unknown` liefert und ein Ziel auflöst); `voting` liest `sevenTvEmoteId` + `emoteName`, Version 1. Dedupe über `dedupeImportRows` (Zeilen vorher auf `ImportRow`-Form mit `imageUrl: null` bringen oder eine typgleiche Hilfsfunktion daneben — der Task entscheidet, der Spec pinnt „erste Fundstelle gewinnt"). Fehlerschlüssel: bestehende `restore.import.errors.*` (`wrongKind`, `wrongVersion`, `noRows`, `votingExport` **nicht** — Voting ist hier gültig) plus neu `restore.import.errors.transferNotSelectable`.
- **`web/src/app/shared/selection/selection-list-match.ts`** — `SelectionCandidate = { sevenTvEmoteId: string; emoteName: string; aliases: readonly string[]; membership: 'live' | 'left' }`; `matchSelectionList(rows: readonly SelectionListRow[], candidates: readonly SelectionCandidate[], selectedKeys: ReadonlySet<string> | readonly string[]): SelectionMatch` mit `SelectionMatch = { matchedKeys: string[]; alreadyMarked: number; notInSet: SelectionListRow[]; nameOnly: SelectionListRow[] }`. `matchedKeys` in Dateireihenfolge, nur `membership === 'live'`; `'left'` zählt zu `notInSet`; `nameOnly ⊆ notInSet` (exakter Vergleich gegen `emoteName` und jeden Alias, ordinal, case-sensitive). Abgeleitet, nicht gespeichert: `toMark = matchedKeys.length − alreadyMarked`. Liegt in `shared/selection/`, weil es Auswahl-Mechanik ist, keine Export-Lesung.

### 1.2 Dialog und Trigger

- **`ImportSourceDialogData`** (`import-source-dialog.ts`): neues Feld `selectionHost: SelectionHost | null`; `SelectionHost = { setId: string; candidates: Signal<readonly SelectionCandidate[]>; selectedKeys: Signal<readonly string[]> }`. Der Typ lebt neben `SelectionCandidate` in `selection-list-match.ts` (Signal-Import aus `@angular/core` ist dort zulässig — `list-selection.ts` tut dasselbe). `setId` ist der beim Klick eingefrorene gewählte Set; die beiden Signale sind **lebend** (Präzedenz #132: `create-vote-session-dialog.ts` nimmt `emoteIds` als Signal).
- **`ImportSourceDialogResult`**: neuer Zweig `{ kind: 'selection'; setId: string; keys: string[]; summary: SelectionMatch }`. `keys` = `matchedKeys` **ohne** die bereits markierten (reine Ergänzung).
- **`FileImportStep`**: neuer Input `selectionHost: SelectionHost | null` (required, wie `hostSelectedSetId`); neues Signal `mode: 'write' | 'select'` (Vorgabe `'write'`); `selectionPreview: Signal<{ list: SelectionList; match: SelectionMatch } | null>` — die Datei bleibt als `SelectionList` im Schritt, das `match` ist ein `computed` über `selectionHost.candidates()`/`selectedKeys()`; öffentliche Methode `confirmSelection()` (vom Dialog aufgerufen) emittiert `picked` mit dem `'selection'`-Zweig, nur wenn `toMark > 0`. `onFileSelected` verzweigt **vor** `readRestoreSort` nach `mode`: im Modus `'select'` läuft ausschließlich `readEnvelope → parseSelectionList → Vorschau`; `checkTarget`/`resolveEditableSet` werden nie berührt. Moduswechsel und ein neuer Dateiwahl-Versuch setzen `selectionPreview`, `errorKey` und `choice` zurück. Der `FileImportResult`-Typ bekommt den `'selection'`-Zweig; `ImportSourceDialogResult` erbt ihn damit.
- **`ImportSourceDialog`**: reicht `data.selectionHost` an den Schritt; `selectionPreview = computed(() => fileStep()?.selectionPreview() ?? null)`; in der Aktionszeile ein Primärknopf `restore.import.selection.confirm` (`{{count}} markieren`, `count = toMark`) **nur** wenn `selectionPreview() !== null` — gleiche Stelle und gleiches `@if`-Muster wie „Weiter"; `[disabled]` bei `toMark === 0`, dann steht der Grund `restore.import.selection.nothingToMark` als Text mit `mr-auto` in der Aktionszeile und der Knopf trägt `aria-describedby` darauf (Muster: gesperrter Bestätigen-Knopf in `delete-confirm-dialog`). Die Pane-Breite ändert sich **nicht** (kein Raster in dieser Vorschau). „Zurück" verwirft die Vorschau, weil der `@switch` den Schritt zerstört — kein eigener Code nötig, aber ein Spec-Fall.
- **`ImportTrigger`**: neuer Input `selectionHost = input<SelectionHost | null>(null)`, eingefroren beim Klick wie die übrigen; neuer Output `selectionLoaded = output<SelectionLoadResult>()` mit `SelectionLoadResult = { setId: string; keys: string[]; summary: SelectionMatch }`. Ein `'selection'`-Ergebnis startet **keine** Kette; es wird vor dem `setId === null`-Guard behandelt (es braucht kein Ziel). Die Sperre `disabled` bleibt unverändert (E-1 nimmt in Kauf, dass ein laufender 7TV-Lauf auch das Laden sperrt).

### 1.3 Nutzungsseite

- `selectionHost = computed<SelectionHost | null>()`: nicht `null` nur wenn `importScopeCurrent()` und `shownSetId() !== null` und das Raster die Zeilen zeigt (`sheetShowsRows()`-Bedingung, die das Template für den `@else`-Zweig benutzt — die genaue Signalnamen stehen um `usage-stats-page.ts:1740-1770`); `candidates` ist ein `computed` über `emotes()` (Abbildung `EmoteUsageTotal → SelectionCandidate`), `selectedKeys` ist `selection.selectedKeys`.
- `onSelectionLoaded(result)`: verwirft still, wenn `result.setId !== this.shownSetId()` (Konzept Abschnitt 5); löst `result.keys` gegen `emotes()` auf (Map nach `sevenTvEmoteId`), ruft `selection.selectMany(resolved)`, dann `recordBulkMarkAnnouncement()` (der Kommentar dort nennt künftig **drei** Aufrufer), dann die vergängliche Meldung `usageStats.selectionLoaded.{one,other}` mit `resolved.length` — eigenes Signal `selectionLoadedFeedback: { key; count } | null` plus Timeout nach dem Muster von `selectionPrunedFeedback` (§4.5: `…_FEEDBACK_MS = 4000`, Timer vorher löschen, Cleanup bei Destroy). Bei `resolved.length === 0` keine Meldung und kein Snapshot.
- Template: `<app-import-trigger [selectionHost]="selectionHost()" (selectionLoaded)="onSelectionLoaded($event)">`; auf der Emote-Zähl-Zeile neben `selectionPrunedFeedback` ein zweites Paar aus dauerhaft gemounteter `sr-only`-`role="status"`-Region und `aria-hidden`-Zwilling — **nicht** dieselbe Region wiederverwenden (sie ist implizit `aria-atomic`; zwei Meldungen in einer Region bräuchten `aria-atomic="false"`, und die beiden Fälle treten nie gleichzeitig auf, aber eine getrennte Region ist das einfachere, bereits mehrfach belegte Muster).

### 1.4 Vorlage des Schritts (Reihenfolge ist Vertrag, Designsprache §7.3)

Im Modus `'write'` unverändert. Mit Host: **zuerst** die Radiogruppe (Legende + zwei `<label>`-Zeilen mit Hinweis in der zweiten Zeile, Muster `export-dialog.ts`), dann die Sortenliste — im Modus `'select'` die vier Schlüssel `restore.import.sortsForSelection.*` statt der fünf heutigen —, dann der Datei-Knopf, dann (nur `'select'` nach erfolgreicher Lesung) der Vorschau-Block in der Reihenfolge aus Konzept 3.3 (Herkunft · Treffer · bereits markiert · nicht im Set + `NamePreviewList` · Namensvettern · verworfen/doppelt), dann das Fehlerbanner. Der Vorschau-Block ist Absatz-Text (`text-fg-secondary`), **kein** `NoticeBanner` und keine Live-Region (§4.5, Modal). Nach dem Lesen wandert der Fokus per `afterNextRender` auf den Bestätigen-Knopf der Aktionszeile — der Dialog stellt dafür eine Methode bereit oder der Schritt emittiert ein `previewReady`-Ereignis; der Task wählt die Variante, die ohne `viewChild` über Komponentengrenzen auskommt.

### 1.5 i18n (de Referenz, en gleichlautend; beide Dateien in jedem Task, der Schlüssel einführt)

Schlüssel aus Konzept Abschnitt 7. Pluralformen `.one`/`.other` über `pluralKey`. Wortlaut-Vorschläge: Legende „Was soll mit der Datei geschehen?"; `write.label` „Importieren oder wiederherstellen", `write.hint` „Schreibt in ein 7TV-Set — wie bisher"; `select.label` „Nur im Raster markieren", `select.hint` „Markiert die Emotes der Datei, die in diesem Set sind. Es wird nichts geschrieben."; `matched.other` „{{count}} von {{total}} Einträgen sind in diesem Set"; `nothingToMark` „Kein Eintrag der Datei ist in diesem Set — es gibt nichts zu markieren."; `transferNotSelectable` „Übertragungs- und Rückweg-Protokolle lassen sich nicht als Auswahl laden — sie beschreiben Quelle und Ziel zugleich."; `usageStats.selectionLoaded.other` „{{count}} Emotes aus der Datei markiert". Englische Fassung in derselben Länge oder kürzer (Deutsch ist die Referenz für Wortlängen).

---

## 2. Tasks

Reihenfolge: T1 ∥ T2 → T3 → T4 → T5 → T6 → T7. T1 und T2 sind unabhängig und laufen parallel.

### Task 1 — `parseSelectionList` (rein) **[E-4, E-5]**

**Modell:** sonnet. **Kontext für den Subagent:** Konzept Abschnitte 2 und 4; `shared/export/export-envelope.ts`, `read-envelope.ts`, `import-source-parser.ts` (+ Spec als Muster), `purge-run-export.ts:1-140` (Zeilen- und Versionsvertrag, `PURGE_RUN_FORMAT_VERSION`), `voting-export.ts` (Zeilenform, `withheld`), `core/seven-tv/import-source.ts` (`dedupeImportRows`); `web/.claude/CLAUDE.md` (Member-Reihenfolge, Regel 12).

- [ ] Datei anlegen mit den Typen und der Funktion aus 1.1; Kopfkommentar nennt, warum `purge-run` nicht durch `parsePurgeRunProtocol` läuft und warum Transfer-Sorten abgelehnt werden.
- [ ] i18n: `restore.import.errors.transferNotSelectable` in de/en.
- [ ] Spec `selection-list-parser.spec.ts`: je Sorte eine gültige Datei → Zeilen und Herkunft; Purge-Protokoll mit `done`/`failed`/`cancelled`/`unknown` → alle Zeilen, Versionen 1, 2 und 3 akzeptiert, 4 → `wrongVersion`; Voting mit `withheld: ['keepVotes', …]` → gültig; `transfer-run` (beide Stufen) und `transfer-undo` → `transferNotSelectable`; unbekannter `kind` → `wrongKind`; `emote-list` mit `formatVersion: 2` → `wrongVersion`; Zeilen ohne ID oder Name → `discardedRows`; doppelte ID → `duplicatesCollapsed` und Dateireihenfolge; alles ungültig → `noRows`; `meta.emoteSetId`/`exportedAt`/`channelName` fehlend → `null`, nie ein Ratewert.
- [ ] Gates: `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run format:check`.
- [ ] Ein Commit: `feat(web): parse an export file into a selection list`.

**Abnahme:** Der Spec pinnt jede Sorte, jede Version und jeden Fehlerschlüssel; kein Netzwerk, kein Angular-Import außer Typen.

### Task 2 — `matchSelectionList` und `SelectionHost` (rein) **[E-3]**

**Modell:** sonnet. **Kontext für den Subagent:** Konzept Abschnitte 2, 4 und 5; `shared/selection/list-selection.ts` (+ Spec, Schlüsselsemantik), `core/usage-stats/usage-stat.model.ts` (`EmoteUsageTotal.aliases`, `membership`), `core/usage-stats/merge-set-view.ts` (woher `'left'` und Klasse-3-Zeilen kommen); Task-1-Typen.

- [ ] Datei anlegen mit `SelectionCandidate`, `SelectionHost`, `SelectionMatch`, `matchSelectionList` (1.1/1.2). Kommentar: warum `'left'` nicht markiert wird (eine Markierung, die keine Aktion trägt, verfälscht „eine Zahl, eine Quelle") und warum Namensvettern nur gezählt werden.
- [ ] Spec `selection-list-match.spec.ts`: Treffer in Dateireihenfolge, nicht in Kandidatenreihenfolge; `alreadyMarked` zählt nur Treffer; `'left'` → `notInSet`; Klasse-3-Zeile (nur ID, kein Guid — die Funktion kennt gar keine Guid) → Treffer; Namensvetter über `emoteName` und über zweiten Alias → `nameOnly` und `notInSet`; Groß-/Kleinschreibung unterscheidet; leere Eingaben; `selectedKeys` als Array und als Set.
- [ ] Gates wie Task 1. Ein Commit: `feat(web): match a selection list against the shown set`.

**Abnahme:** Jede Ergebnismenge ist durch mindestens einen Fall gepinnt; `nameOnly ⊆ notInSet` ist ein eigener Fall.

### Task 3 — `FileImportStep`: Modus, Lesung, Vorschau **[E-1, E-2, E-6]**

**Modell:** opus (der Schritt ist die zustandsreichste Komponente des Import-Pfads: Async-Lesung, Zielprüfung mit Sperre, Weiche, Fokusvertrag; Spec 1.062 Zeilen). **Kontext für den Subagent:** Konzept Abschnitte 3, 5, 6, 7; Plan 1.2, 1.4, 1.5; `shared/seven-tv/file-import-step.ts` (+ Spec vollständig — bestehende Fälle müssen unverändert grün bleiben), `import-source-dialog.ts:146-230` (Vorlage, Fokusvertrag), `shared/export/export-dialog.ts` (zweizeilige Radio-Labels, §7.4), `shared/ui/name-preview-list.ts`, `shared/seven-tv/delete-confirm-dialog.ts` (Grund neben gesperrtem Knopf), `docs/UI-Designsprache.md` §4.5, §7, §7.3, §9, §10; Ergebnisse von Task 1 und 2.

- [ ] Input `selectionHost`, Signal `mode`, Signal für die gelesene `SelectionList`, `computed` `selectionPreview` (1.2). Radiogruppe nur bei `selectionHost !== null`; Vorgabe `'write'`.
- [ ] `onFileSelected`: Modusverzweigung **vor** `readRestoreSort`; im Modus `'select'` keine Zielprüfung, kein `checking`-Lock; Fehler landen wie heute in `errorKey`.
- [ ] Vorlage nach 1.4; Vorschau-Zeilen nur, wenn ihr Wert > 0 (Frontend-Zurückhaltung); Herkunftszeile aus `SelectionListOrigin`.
- [ ] `confirmSelection()`; Fokus nach der Lesung (1.4, Variante begründen).
- [ ] `FileImportResult` um `'selection'` erweitern; alle `switch`/`if`-Verbraucher des Typs kompilieren lassen — `import-trigger.ts` erst in Task 4 (hier nur so weit, dass der Build grün ist; ein `default`/`return` ohne Verhalten ist zulässig und wird in Task 4 ersetzt).
- [ ] i18n de/en: alle Schlüssel aus 1.5 außer `usageStats.selectionLoaded.*`.
- [ ] Spec-Fälle (Verhalten, nicht Vorlage): ohne Host keine Radiogruppe und identischer Dispatch für jede Restore-/Copy-Sorte (bestehende Fälle bleiben); mit Host Vorgabe `'write'` und identischer Dispatch; Modus `'select'` + Emote-Liste → `selectionPreview` mit erwarteten Zahlen, **kein** Aufruf von `resolveEditableSet`, kein `picked`; `confirmSelection()` → `picked` mit `kind: 'selection'`, `setId` aus dem Host, `keys` ohne bereits markierte; `toMark === 0` → kein Emit; Purge-Protokoll im Modus `'select'` → Vorschau statt Zielprüfung; Transfer-Datei → `transferNotSelectable`; Voting-Datei im Modus `'write'` weiterhin `votingExport`-Fehler, im Modus `'select'` gültig; Moduswechsel nach Lesung → Vorschau und Fehler leer; lebende Kandidaten: Signal ändern → Vorschau-Zahlen folgen; Radiogruppe per Rolle und zugänglichem Namen (Legende) auffindbar.
- [ ] Gates: Vitest, Lint, Format. Ein Commit: `feat(web): read an export file as a selection preview in the file import step`.

**Abnahme:** `git diff` der bestehenden Spec-Fälle zeigt nur Fixture-Ergänzungen (`selectionHost: null`), keine geänderten Erwartungen; der neue Pfad erzeugt keine HTTP-Anfrage (HttpTestingController `verify()` ohne Erwartung).

### Task 4 — Dialog-Aktionszeile und Trigger-Output **[E-6]**

**Modell:** sonnet. **Kontext für den Subagent:** Plan 1.2; `import-source-dialog.ts` (+ Spec: `DIALOG_DATA`-Fixtures `:176`, `:571`), `import-trigger.ts` (+ Spec: `dialogOpen`-Mock, `closedAt`-Helfer `:319`), `docs/UI-Designsprache.md` §7 („Aktionszeile", gesperrter Knopf mit Grund), §10; Task-3-Ergebnis.

- [ ] `ImportSourceDialogData.selectionHost`; Durchreichen an `<app-file-import-step>`; `selectionPreview`-Computed; Primärknopf „n markieren" in der Aktionszeile mit Sperre + Grund + `aria-describedby`; Klick ruft `fileStep().confirmSelection()`. Kein `WIDE_PANEL_CLASS`-Wechsel.
- [ ] `ImportTrigger`: Input `selectionHost`, Output `selectionLoaded`; `'selection'`-Zweig vor dem `setId === null`-Guard; Klassenkommentar um den vierten Ausgang ergänzen („startet keine Kette, meldet an die Seite").
- [ ] Spec `import-source-dialog.spec.ts`: Knopf fehlt ohne Vorschau; erscheint mit Vorschau und trägt die Zahl im zugänglichen Namen; gesperrt mit Grund bei 0; Klick schließt mit dem `'selection'`-Ergebnis; „Zurück" entfernt Vorschau und Knopf; Fixtures der bestehenden Fälle um `selectionHost: null`.
- [ ] Spec `import-trigger.spec.ts`: `'selection'`-Ergebnis → `selectionLoaded` emittiert, **keine** der drei Ketten gestartet, kein Token-Prompt, kein zweiter Dialog; `selectionHost` wird beim Klick eingefroren mit übergeben; Ergebnis bei `setId: null`-Dialog trotzdem gemeldet (der Host bringt seine eigene `setId`).
- [ ] Gates: Vitest, Lint, Format. Ein Commit: `feat(web): hand a loaded selection back to the page instead of starting a run`.

**Abnahme:** Trigger-Spec beweist „keine Kette" per `not.toHaveBeenCalled` auf allen drei Flow-Startern.

### Task 5 — Nutzungsseite, Dock-Ansage, Statusmeldung, Doku **[E-2]**

**Modell:** sonnet. **Kontext für den Subagent:** Konzept Abschnitte 3.4–3.6, 5, 6; Plan 1.3; `usage-stats-page.ts` (Stellen: `selection` `:1238`, `selectedForDelete` `:1512`, `selectionPrunedFeedback` `:1054`, `recordBulkMarkAnnouncement` `:2729`, `markAll` `:2331`, `importScopeCurrent` `:1700`, `reconcileSelection` `:3092`), `usage-stats-page.html` (`app-import-trigger` `:139`, Zähl-Zeile mit `role="status"` `:295-325`), `usage-stats-page.spec.ts` (Muster für Bulk-Snapshot und Pruned-Feedback), `shared/seven-tv/dock-outcome-announcer.ts`, `docs/UI-Designsprache.md` §4.5, §7.3, §8.7; `docs/DECISIONS.md:1-15` (Format, neue Einträge englisch, `**Betrifft:**`-Zeile); CLAUDE.md Regeln 3, 12, 14.

- [ ] `selectionHost`-Computed, `onSelectionLoaded`, `selectionLoadedFeedback` + Timer + Cleanup (1.3); Kommentar an `recordBulkMarkAnnouncement` auf drei Aufrufer.
- [ ] Template: Trigger-Bindung; zweites Status-Paar auf der Zähl-Zeile.
- [ ] i18n de/en: `usageStats.selectionLoaded.{one,other}`.
- [ ] Spec `usage-stats-page.spec.ts`: Host ist `null` ohne Set/außerhalb `importScopeCurrent`, sonst Kandidaten aus `emotes()` mit `membership`; Handler ergänzt add-only (vorher markierte bleiben); unauflösbarer Schlüssel fällt weg und zählt nicht; Meldung mit Zahl und Timer-Ablauf (`vi.useFakeTimers`); `dockMarkedCount` liefert die neue Gesamtzahl nach dem Laden (Snapshot); Ergebnis mit fremdem `setId` → nichts passiert; mit aktivem Namensfilter → `hiddenSelectedCount` steigt, `selectedForDelete` trägt `hidden: true` für die verdeckten.
- [ ] `docs/DECISIONS.md`: neuer Eintrag **oben**, englisch, Inhalt nach Abschnitt 3.
- [ ] `docs/UI-Designsprache.md` §7.3: Absatz (englisch) zum Markieren-Modus des Datei-Zweigs — Radiogruppe nur mit Host, Vorschau als Absätze, Bestätigen in der Aktionszeile, keine Zielprüfung; §8.7: Satz, dass „aus einer Datei markieren" bewusst **nicht** in der Filterzeile steht, mit Verweis auf den DECISIONS-Eintrag. `DESIGN.md` bleibt unberührt (kein Token, keine Regel).
- [ ] `docs/Feature-Ideen-2026-08-01.md`: Statuszeile nur, falls #201 dort geführt wird (prüfen per `grep 201`); sonst nichts.
- [ ] Gates: Vitest, Lint, Format. Zwei Commits: `feat(web): mark the emotes of a loaded file in the usage grid` und `docs: record the list-as-selection entry point` (DECISIONS + Designsprache; Regel 3 erlaubt auch einen gemeinsamen Commit — der Task entscheidet, nennt aber im Commit-Body den Vertrag).

**Abnahme:** `dockMarkedCount` ist nach `onSelectionLoaded` gleich der Gesamtzahl; `selectionPrunedFeedback` bleibt vom neuen Pfad unberührt.

### Task 6 — E2E, Audit-Szenario, Gates, PR-Vorbereitung

**Modell:** sonnet. **Kontext für den Subagent:** Konzept Abschnitt 8; `web/e2e/emote-import.e2e.spec.ts:212-247` (`openFileImportDialog`, `setInputFiles`-Muster, Falle „Dialog bleibt bis `file.text()` offen"), `web/e2e/usage-atlas.e2e.spec.ts:51-80` (`openAtlas`, Mocks), `web/e2e/support/mocks.ts` (`mockUsageTotals`, `mockActiveEmoteSet`), `web/e2e/support/test.ts` (Pflicht-Import), `web/e2e/audit/` (Szenarienliste, §12); CLAUDE.md „Tests" (E2E nur ohne Api auf `:5151`; `page.clock` für Timer).

- [ ] Neue Datei `web/e2e/selection-from-file.e2e.spec.ts`: die fünf Abläufe aus Konzept Abschnitt 8 (Treffer/Nicht-Treffer mit Namen; Namensfilter → Dock-Nebenzeile; Purge-Protokoll gemischt; 0 Treffer → gesperrter Knopf mit Grund; Abbrechen unberührt). Die vergängliche Meldung per `page.clock` prüfen (`install()` vor `goto`, `pauseAt`, `runFor`).
- [ ] Audit-Szenario (§12) für den Datei-Zweig mit sichtbarer Radiogruppe und Vorschau (`afterLoad`), Gates `contrastViolations` 0.
- [ ] Gates vollständig: `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e`, `npm --prefix web run lint`, `npm --prefix web run format:check`, `node scripts/coverage-local.mjs --frontend-only` (Schwelle 80 % auf neuem Code; die beiden reinen Module und die Specs aus T3–T5 sollten weit darüber liegen — Ergebnis im PR-Text nennen).
- [ ] Ein Commit: `test(web): cover loading a file as a grid selection end to end`. Danach Push und PR-Entwurf gegen `feat/emote-sets-200` (Titel englisch, Body mit Gate-Zahlen, Verweis auf Konzept und Plan).

**Abnahme:** E2E grün in einem Lauf ohne laufende Api; Coverage-Schätzung ≥ 80 %.

### Task 7 — Zweitmeinung und Browser-Abnahme (Orchestrator + Betreiber)

**Modell:** Orchestrator (Opus); Codex Sol per `/codex:review --model gpt-6.1-sol` einmal über den Branch (Regel 22; Fallen aus Memory „Codex-Review-Fallen": `--scope branch`, Session-CWD im Worktree). Widerspruch Opus ↔ Sol → Fable als Schiedsrichter, nur mit den strittigen Findings.

- [ ] Browser-Blick des Betreibers auf `:4200` gegen die Dev-Api (kein 7TV-Token nötig — der Pfad schreibt nicht): Datei laden, Vorschau lesen, markieren, Filter setzen, Dock-Nebenzeile, Löschdialog öffnen und **abbrechen**, Übertragen-Dialog Bereich „Auswahl (n)" prüfen. Beide Farbmodi.
- [ ] Offene Entscheidungen aus Konzept Abschnitt 9, die im Lauf anders ausgefallen sind, als Nachtrag ins Konzept und in den DECISIONS-Eintrag.

---

## 3. Inhalt des DECISIONS-Eintrags (Task 5, englisch)

Titel: `### 2026-10-03 — A file can be loaded into the usage grid as a selection; the file step gains a non-writing mode (#201)`. `**Betrifft:**` die neuen und geänderten Dateien aus 1.1–1.3 plus `docs/UI-Designsprache.md`. Punkte: (1) the gap — every file import ended in a 7TV write run; (2) entry point inside the one import dialog's file branch rather than a toolbar control or a header button, with the §8.7 tension named and resolved (restraint over a literal reading; the dialog is the one door for files); (3) no network, no token, no target check on this path; (4) matching by `sevenTvEmoteId` against the **shown** set's universe, `'left'` rows and name-only twins reported but never marked, additive like every group gesture; (5) accepted kinds and the explicit refusal of transfer files; (6) preview-then-confirm with the count in the button, the reason text when zero; (7) the frozen `setId` check on the page and the transient status on the count row; (8) known limits: a running 7TV run locks the trigger and therefore the load; no scroll-to-first-match; voting threshold deliberately absent.

## 4. Offene Punkte für den Betreiber

Siehe Konzept Abschnitt 9 (sechs Entscheidungen mit Empfehlung). Der Plan ist auf die Empfehlungen geschrieben; bei abweichender Wahl sind die mit **[E-n]** markierten Tasks vor dem Start umzuplanen — E-1 (b) ersetzt Task 3/4 durch eine neue Toolbar-Komponente mit eigenem Dialog, E-6 (b) streicht Vorschau und Aktionsknopf aus Task 3/4 und verlagert die Rückmeldung ganz in Task 5.
