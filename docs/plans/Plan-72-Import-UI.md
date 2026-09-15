# Plan #72 — Import-UI: In Kanal kopieren, Ziel-Picker, Bestätigungsdialog, Datei-Weg

Umsetzungsplan für Issue [#72](https://github.com/emotepurge/EmotePurge/issues/72) (Kind K3 von #38).
Erstellt 2026-09-05 auf `feat/import-ui-72` (Worktree `/home/dev/projects/EmotePurge-import-ui`) gegen
den Code-Stand `ac239d7` (= lokal `main`, enthält #70 und #71). Quellen: Issue-Text,
[`docs/designs/Emote-Import-38-2026-09-05.md`](../designs/Emote-Import-38-2026-09-05.md) (verbindlich),
[`Plan-70-Run-Engine.md`](Plan-70-Run-Engine.md), [`Plan-71-Import-Backend.md`](Plan-71-Import-Backend.md),
die beiden DECISIONS-Einträge vom 2026-09-05 zu #70/#71, `CLAUDE.md`, `web/.claude/CLAUDE.md`,
`docs/UI-Designsprache.md` (§4.2, §6.1, §7, §9, §12), der betroffene Code.

**Der Plan enthält keinen Code.** Signaturen stehen als einzeilige Verträge, i18n-Schlüssel wörtlich
(damit niemand rät), alles andere ist Absicht, Grenzfall und Reihenfolge. Jeder Task ist für einen
Subagent mit frischem Kontext geschrieben; er liest vor der Arbeit die unter „Betroffene Dateien"
genannten Dateien **ganz** und dazu Abschnitt 2 (Verträge) dieses Plans.

Reihenfolge in jedem Task: **Tests zuerst** (rot), dann Umsetzung (grün), dann die im Task genannte
Doku, dann die im Task genannten Specs — **nie die volle Suite**, die läuft am Ende (Abschnitt 5).

---

## 1. Verifikation des Ist-Zustands

Jede Zeile des Issue-Abschnitts „Ist-Zustand (geprüft 2026-09-05)" sowie jede Datei-/Zeilenangabe
aus „Änderung" und der Dateitabelle gegen `ac239d7` geprüft. Zeilenverschiebungen um wenige Zeilen
stammen aus #70/#71 (Kommentare, Arbiter-Injektion) und sind als „kosmetisch" markiert; nur die
fett gesetzten Zeilen ändern den Plan.

| Behauptung des Issues | Stimmt? | Tatsächlicher Befund |
|---|---|---|
| Header `usage-stats-page.html:8-19`: Export-Button, `disabled` bei `atlasOrder().length === 0`, nicht `coarse`-gegated | ja | Aktionszeile `:8-30`, Export-Button `:11-20`, `[disabled]="atlasOrder().length === 0"` `:15`; kein `isCoarse()` im Header. Der Button steht **vor** „Aktualisieren" — der neue Button kommt zwischen beide. |
| Dock/Restore-Panel unter `@if (activeEmoteSetId(); as setId)` (`:684`) und `@if (!isCoarse())` (`:688`, `:698`) | ja | Restore-Panel `:684-691`, Dock `:697-751` mit `dockVisible() && !isCoarse()` `:698`. Zusätzlich relevant: das Dock trägt oben die Zeile „N markiert" + Slot-Projektion (`:701-716`) **unabhängig** davon, warum es sichtbar ist. |
| Auswahl auf `coarse` geleert (`usage-stats-page.ts:643-646`) | ja | `:643-646`, `effect` im Konstruktor. |
| `dockVisible()` `:615-621` | ja (kosm.) | `:615-622`; liest `deleteService`/`restoreService` `isRunning()` und `queue().length`. |
| `capacity === null`-Prüfung `:624-633` | ja (kosm.) | `projectedSlots` `:625-634`. |
| Export-Aufruf `:1029-1059`, Zeilen tragen `emoteId`, `sevenTvEmoteId`, `name`, `imageUrl`, `totalUseCount` | **fast** | `openExport()` `:1029-1058`. Die Zeilen sind `EmoteUsageTotal` — das Namensfeld heißt **`emoteName`**, nicht `name` (`usage-stat.model.ts:1-6`); dazu `lastUsedDate`, `previousWindowUseCount`, `firstSeenAt`. Die Abbildung auf `ImportRow.name` ist eine Zeile, aber sie muss explizit sein. |
| Export-Dialog-Radio-Muster (`role="radiogroup"`, `<input type="radio">`), Dialoge nur über `openAppDialog` | ja | `export-dialog.ts:52-106`; `openAppDialog` in `shared/ui/dialog.ts:32-43`; jede Dialogdatei exportiert `open…()`. |
| Restore-Panel `restore-panel.ts:54-132`: `file.text()` → `parsePurgeRunProtocol()` → Token-Prompt → `openRestoreConfirmDialog` | ja (kosm.) | Klasse `:54-136` (Datei ist seit #70 136 Zeilen; Arbiter-Injektion `:62`, Trigger `disabled` bei `arbiter.activeRun() !== null` `:32`). Reihenfolge wie beschrieben: **Token vor Bestätigung**. |
| Texte `restore.import.trigger/hint/fileLabel` (`:29-45`), Fehlerkeys `de.json:820-835` | ja (kosm.) | Texte `:36`, `:38`, `:44`; `restore.import` in `de.json` ab `:823`, `errors` darunter. |
| Parser `purge-run-export.ts:104-167`: JSON-Vorprüfung `:115-130`, `kind`-Fehler `:94-97`, Kanal `:139-141`, Set `:146-148` | ja (+5) | `parsePurgeRunProtocol` `:116-172`; Vorprüfung `:120-130`; `FOREIGN_KIND_ERROR_KEYS` `:99-102` (`usage`, `voting`); Kanal `:144-146`; Set `:151-153`. |
| Restore-Dialog `restore-confirm-dialog.ts:12-18`, Projektion `:84-89` (null bei `capacity <= 0`) | ja | Data `:12-18`, `projection` `:84-91`. |
| `SevenTvRestoreService`: `resetIfChannelChanged` `:104-115`, Nachlauf-Gate `doneIds.length === 0` `:130-133`, `ResyncTriggerState` inkl. `'cooldown'` `:37-40`, `:139-145` | ja (+3) | `:108-117`, `:133-136`, `:41`, `:141-148`. **Ergänzung aus #70:** `startRestore` nimmt `DeleteQueueEmote[]` und setzt `key = emoteId` selbst (`:131`); der Import-Service muss seinen `key` (= `sevenTvEmoteId`) ebenso selbst prägen, die Engine tut es nicht. |
| `listMine()` → `/api/channels/mine` (`channel.service.ts:123`), `MyChannelDto`, `MyChannelsResult` | ja | Exakt; `listMine` ist **bewusst ungecacht** (`:43-44`) — ein Aufruf je Picker-Öffnung ist korrekt. |
| `EmoteSetStatus`, `EmoteSetWarning` | ja | `emote-set-status.model.ts`, `emote-admin.service.ts:19-24`. |
| `app-mass-delete-panel` auch auf der Voting-Seite (`vote-session-detail-page.html:150`) | ja | `:149-157`. |
| Kein Route-Leave-Guard im Bestand | ja | `grep -rn "canDeactivate\|CanDeactivateFn" web/src/app` ist leer. Alle Guards liegen in `core/` und öffnen keine Dialoge. |
| Shared UI: `confirm-dialog`, `dialog-shell`, `name-preview-list`, `notice-banner`, `segmented-control`, `utilization-tone` | ja | Dazu vorhanden und relevant: `skeleton-rows.ts`, `skeleton-sections.ts` (Muster für §6.1), `back-link.ts` (einziger `RouterLink`-Konsument in `shared/ui`). |
| Punkt 7: „Meldet Start/Ende an den Arbiter (#70)" | **nein** | `seven-tv-run-arbiter.ts:39-47`: `activeRun` ist ein `computed` über `deleteService.isRunning()` / `restoreService.isRunning()`; kein `tryAcquire`, kein `release`. Der Import-Service meldet **nichts**; der Arbiter bekommt einen dritten Zweig (R1). |
| Punkt 7: `abortOn`-Hook „aus #70" | ja | `RunOperation.abortOn?(failure: { message; httpStatus: number \| null })` (`seven-tv-run-engine.ts:103`), JSDoc `:79-102` nennt: GQL-Fehler → Rohtext + `null`; HTTP-Fehler → übersetzter Text + Status (Netzwerk = `0`); Rate-Limit-Aufgeben → übersetzter Text + `null`; Token-Löschung bei 401/403 **vor** dem Hook. |
| Punkt 8: `syncImported(targetChannelName, { sevenTvEmoteIds, sourceChannelName, sourceKind })` | ja, **mit Vertrag** | `emote-admin.service.ts:94-96`, `SyncImportedBody :27-31`. Backend `EmoteEndpoints.cs:153-156`: `string.IsNullOrWhiteSpace(SourceChannelName) != (SourceKind == "file")` → **`file` mit Namen und `channel` ohne Namen sind beide `400 invalid_source_kind`**. Der Datei-Weg sendet also `sourceChannelName: null` (R3). Antwort `204`, unbekannter Zielkanal `404`. |
| Punkt 4: `getSetStatus`, `getSetWarning`, `listEmotes` | ja | Alle drei in `EmoteAdminService` (`:63`, `:70`, `:86`); `listEmotes` packt `{ emotes }` aus, liefert `EmoteListItem[]` (`emote-list-item.model.ts`, nur `sevenTvEmoteId` + `name`). Testtabelle „`listEmotes/syncImported` (falls nicht in K2)": **sind in K2 getestet** (3 Fälle) — hier +0. |
| Punkt 9: `run-progress-panel` wiederverwenden | ja, **mit Erweiterung** | `labelPrefix` ist die Union `'massDelete' \| 'restore'` (`run-progress-panel.ts:92`); der Import braucht `'import'` und damit die acht Schlüssel `<prefix>.progress`, `.rateLimitPaused`, `.deleteFailedFallback`, `.summary.counts`, `.syncFailedTitle`, `.syncFailed`, `.syncRetry`, `.syncRetrySucceeded`. Track läuft seit #70 über `item.key` (`:47`). |
| Punkt 10: `confirm-dialog` für den Leave-Guard | ja | `openConfirmDialog(dialog, { message, confirmLabel })` nimmt fertig übersetzte Strings, Bestätigen ist `danger-solid`. |
| Punkt 5: `massDelete.notOwnSet/knownAffected/moderatedAffected/ownershipCheckUnavailable` | ja | Alle vier in beiden Locales; die Anzeigelogik dafür steht in `delete-confirm-dialog.ts:116-133` (`hasSharedSetWarning`, `ownershipCheckUnavailable`). |
| Punkt 5: `duplicatesCollapsed` | **gibt es nicht** | Weder Parser noch Auswahl liefern heute eine solche Zahl. Sie entsteht in K3 als `ImportSource.duplicatesCollapsed` (R6). |
| Punkt 12: `usage` liefert `emoteName` (`usage-export.ts:26-35`) | ja | `UsageExportRow` `:26-35`. |
| Punkt 13: `restore.import.errors.usageExport` samt Spec-Fall entfernen | ja | Key in beiden Locales; Spec-Fall `purge-run-export.spec.ts:90-101` („names the other export kinds…") prüft `usage` **und** `voting`. |
| Punkt 3 / AK 5: Envelope über `buildPurgeRunProtocol`? | **nicht möglich** | `buildPurgeRunProtocol` verlangt seit #70 `items: (RunQueueItem & { emoteId: string })[]` (`purge-run-export.ts:80`), bewusst (DECISIONS #70). Die `emote-list`-Envelope braucht ihren eigenen Builder (R4). |
| Dateitabelle: `export-envelope.ts:9` (`ExportKind`) | ja | `:9`; `FOREIGN_KIND_ERROR_KEYS` in `purge-run-export.ts:99` ist `Partial<Record<ExportKind, string>>` und zieht die Union-Erweiterung ohne Änderung mit. |
| Dateitabelle: `web/src/app/shared/seven-tv/import-source.ts` | **falscher Ort** | `SevenTvImportService` liegt in `core/` und braucht den Typ; `core/` darf nichts aus `shared/` importieren (Schichtentreue). `ImportSource`/`ImportOrigin`/`ImportRow` gehören nach **`core/seven-tv/import-source.ts`** (R5). |
| Dateitabelle: `features/usage-stats/usage-stats-leave.guard.ts` | ja, bewusst | Der Guard öffnet `ConfirmDialog` aus `shared/ui` und kann deshalb **nicht** in `core/` liegen wie die anderen Guards. `features/` darf `shared/` importieren. |
| Dateitabelle: `import-preview.ts` mit `projectSlots()` | **getrennt** | Zwei pure Funktionen mit verschiedenen Konsumenten (Restore-Dialog braucht nur die Projektion) — zwei Dateien (R7). |
| „Kein Bestand-Hinweis bei gesperrtem Start" | (aus #70) | Bestätigt: Buttons sind nur `disabled`, kein Text; `UI-Designsprache.md:182` hält das fest. Gilt auch für den neuen Header-Button (R2). |
| `ApiErrorCodes.ValidationFailed` | existiert nicht | Bekannte Abweichung 4 — kommt in K3 nirgends vor; der einzige Backend-Fehlercode, den die UI sieht, ist `invalid_source_kind` (bereits in `api-error.ts:28` und beiden Locales). |
| Arbiter-Decorator | (relevant) | `@Service()` (`seven-tv-run-arbiter.ts:34`) — erste und einzige Verwendung im Repo; `web/.claude/CLAUDE.md` schreibt ihn für neue Singletons vor. Der Import-Service nutzt ihn ebenso (Rückfall wie in Plan-70 R7). |
| Locale-Parität | (relevant) | `api-error-locales.spec.ts:38` prüft **identische Schlüsselmengen** in `de.json` und `en.json`. Jeder Task, der eine Locale anfasst, fasst beide an — und nur **ein** Task je Welle darf es (R10). |

---

## 2. Verträge

Alle Typen, die mehr als ein Task berührt, stehen hier einmal. Die Tasks verweisen darauf.

### 2.1 Quelle: `ImportSource` (core)

Datei `web/src/app/core/seven-tv/import-source.ts`, reine Typen und eine pure Funktion, keine Angular-Importe.

- `ImportRow = { sevenTvEmoteId: string; name: string }`
- `ImportOrigin = { kind: 'channel'; channelName: string } | { kind: 'file'; fileName: string; exportedAt: string | null; channelName: string | null; envelopeKind: 'emote-list' | 'usage' }`
- `ImportSource = { origin: ImportOrigin; rows: ImportRow[]; duplicatesCollapsed: number; discardedRows: number }` — `rows` sind nach `sevenTvEmoteId` dedupliziert (erste Zeile gewinnt), `duplicatesCollapsed` = Anzahl weggefallener Zeilen. `discardedRows` = Anzahl von der Quelle verworfener (ungültiger) Zeilen, gezählt **vor** dem Dedup; nur der Parser (Datei-Weg) kann das liefern — für `origin.kind === 'channel'` ist es konstruktionsbedingt `0` (die Auswahl liefert nur valide `EmoteListItem`-Zeilen).
- `dedupeImportRows(rows: ImportRow[]): { rows: ImportRow[]; duplicatesCollapsed: number }` — die eine Deduplizierung für beide Herkünfte (Datei **und** Auswahl); ordinaler Vergleich auf `sevenTvEmoteId`.

### 2.2 Zieldaten: `ImportTargetLoadState` (core)

Datei `web/src/app/core/emotes/import-target-loader.ts`.

- `loadImportTarget(emoteAdminService, channelName): Observable<ImportTargetLoadState>` — genau **eine** Emission, nie ein Fehler (R8).
- `ImportTargetLoadState = { status: 'loading' } | { status: 'failed' } | { status: 'no-set' } | { status: 'ready'; setId: string; occupiedSlots: number; capacity: number | null; syncFailureReason: SevenTvSyncFailureReason | null; emotes: EmoteListItem[]; warning: EmoteSetWarning }`
- `warning.available === false`, wenn `getSetWarning` scheiterte (gleiche Form wie `mass-delete-panel.ts:402-407`).

### 2.3 Vorschau und Projektion (shared/seven-tv)

- `import-preview.ts`: `buildImportPreview(source: ImportSource, targetEmotes: EmoteListItem[]): ImportPreview` mit `ImportPreview = { toAdd: ImportRow[]; alreadyPresent: number; nameCollisions: string[] }` — `alreadyPresent` zählt Quellzeilen, deren `sevenTvEmoteId` im Ziel steht (sie fehlen in `toAdd`); `nameCollisions` sind die Namen aus `toAdd`, die ordinal (`===`) mit einem Zielnamen übereinstimmen (bleiben in `toAdd`).
- `slot-projection.ts`: `projectSlots(occupied: number, capacity: number | null, toAdd: number): { projected: number; capacity: number; overflow: boolean } | null` — `null` bei `capacity === null` oder `capacity <= 0`; exakt die heutige Logik aus `restore-confirm-dialog.ts:84-91`.

### 2.4 Ziel-Picker (shared/seven-tv)

- `import-target-options.ts`: `importTargetOptions(result: MyChannelsResult, currentChannelName: string): ImportTargetOption[]` mit `ImportTargetOption = { channelName: string; disabled: boolean }` — Filter `isBroadcaster || isSevenTvEditor`, ohne den aktuellen Kanal (normalisierter Vergleich über `normalizeChannelName`), sortiert ordinal aufsteigend; `disabled = !isTracked`.
- `import-target-dialog.ts`: `openImportTargetDialog(dialog, data: ImportTargetDialogData): DialogRef<ImportTargetChoice | undefined>` mit `ImportTargetDialogData = { currentChannelName: string; visibleCount: number; selectionCount: number }` und `ImportTargetChoice = { scope: ExportScope; target: { kind: 'channel'; channelName: string } | { kind: 'file' } }`. Der Dialog lädt `listMine()` selbst beim Öffnen.

### 2.5 Bestätigungsdialog (shared/seven-tv)

- `import-confirm-dialog.ts`: `openImportConfirmDialog(dialog, data: ImportConfirmDialogData): DialogRef<ImportConfirmOutcome | undefined>` mit `ImportConfirmDialogData = { source: ImportSource; targetChannelName: string; target: Signal<ImportTargetLoadState>; retry: () => void; runBlocked: Signal<boolean> }` (`runBlocked` = `arbiter.activeRun() !== null`, live) und `ImportConfirmOutcome = { targetSetId: string; rows: ImportRow[] }` (die `toAdd`-Zeilen der Vorschau zum Zeitpunkt des Klicks). Der Dialog liest
  `source.discardedRows` (2.1) für seine Hinweiszeile — die genaue Position in der Zeilenreihenfolge
  steht als Vertrag in T5, nicht hier.

### 2.6 Import-Lauf (core)

Datei `web/src/app/core/seven-tv/seven-tv-import.service.ts`, `@Service()`.

- `startImport(target: { setId: string; channelName: string }, origin: ImportOrigin, rows: ImportRow[]): void` — prägt `key = sevenTvEmoteId`, kein `emoteId`.
- `cancel()`, `reset()` wie im Restore-Service. `retrySyncReport()` **nicht** wie im Restore-Service
  (dort gegen die losen Felder `currentChannelName`/`lastReportedIds`) — hier ausschließlich gegen den
  aktuellen Laufdatensatz `run()`: Zielkanal und die gemeldeten Keys (`result.doneKeys`) stehen an
  **demselben** Objekt, nie verteilt auf ein Feld daneben (R15).
- Signale: `queue`, `isRunning`, `rateLimitPauseSeconds`, `progress` (durchgereicht), `syncReport: Signal<SyncReportState>`, `resyncTrigger: Signal<ResyncTriggerState>`, `abortedForPrivileges: Signal<boolean>`, `run: Signal<ImportRunInfo | null>` mit `ImportRunInfo = { targetChannelName: string; targetSetId: string; origin: ImportOrigin; result: RunResult | null }` — gesetzt beim Start (`result: null`), vervollständigt bei `onComplete`, `null` nach `reset()`.
- **Nachlauf ans Laufobjekt gebunden** (R15): `onRunComplete` schließt über den beim Start angelegten
  `ImportRunInfo`; jeder asynchrone Schreibzugriff auf `syncReport`/`resyncTrigger` prüft vorher, ob
  dieses Objekt noch `run()` ist, und verwirft die Antwort sonst kommentarlos, ohne Fehlerzustand —
  das schließt das Fenster zwischen `finish()` (setzt `isRunning` synchron) und dem Ende des
  asynchronen Nachlaufs, in dem ein zweiter Lauf schon gestartet sein kann.
- **Kein** `resetIfChannelChanged` (R9).

### 2.7 Datei-Format (shared/export)

- `export-envelope.ts`: `ExportKind` um `'emote-list'` erweitert.
- `read-envelope.ts`: `readEnvelope(text: string): { ok: true; envelope: ExportEnvelope<unknown> } | { ok: false; errorKey: string }` — besitzt `JSON.parse`, `notJson`, `csvInsteadOfJson`, `source !== 'emotepurge'` → `wrongKind`, und prüft, dass `kind` ein String ist (sonst `wrongKind`). Mehr nicht — Sorte, Version, Zeilen prüfen die Parser.
- `emote-list-export.ts`: `EmoteListRow = ImportRow`, `EmoteListMeta = { sourceEmoteSetId: string; rowCount: number; scope: ExportScope }`, `buildEmoteListEnvelope(input: { channelName; emoteSetId; scope; rows: ImportRow[] })`, `emoteListJson(envelope)`, `emoteListFilename(channelName, exportedAt): string` → `emotepurge_<kanal>_emote-list_<yyyy-mm-dd>.json`.
- `import-source-parser.ts`: `parseImportSource(envelope: ExportEnvelope<unknown>, fileName: string): { ok: true; source: ImportSource } | { ok: false; errorKey: string }` — nimmt `kind ∈ {'emote-list', 'usage'}`; `voting` → `restore.import.errors.votingExport`; anderes (auch `purge-run`, falls je hier ankommend) → `wrongKind`; `formatVersion !== 1` → `wrongVersion`; `rows` kein Array → `wrongKind`; je Zeile `sevenTvEmoteId` nicht-leerer String und `name` (`emote-list`) bzw. `emoteName` (`usage`) String, sonst Zeile verworfen; nach `dedupeImportRows` leer → `noRows`. `origin.exportedAt`/`channelName` sind `null`, wenn nicht String. `discardedRows` = Sollzahl minus Anzahl valider Zeilen **vor** `dedupeImportRows` (also unabhängig von `duplicatesCollapsed`, das erst danach entsteht): Sollzahl ist `envelope.meta.rowCount`, falls dort ein `number` steht, sonst die Länge des rohen `rows`-Arrays.

### 2.8 Arbiter

`SevenTvRunArbiter.activeRun` prüft in der Reihenfolge `delete`, `restore`, **`import`** (R1).

### 2.9 i18n — vollständige Schlüsselliste

Neue Top-Level-Gruppe `import` (existiert noch nicht) in **beiden** Locales, plus Änderungen unter `restore.import`. Plural-Schlüssel nach `pluralKey` (`.one`/`.other`).

| Schlüssel | de | en |
|---|---|---|
| `import.copyButton` | In Kanal kopieren… | Copy to channel… |
| `import.target.title` | Emotes in einen Kanal kopieren | Copy emotes to a channel |
| `import.target.label` | Zielkanal | Target channel |
| `import.target.notTracked` | Kanal muss erst beitreten | Channel has to join first |
| `import.target.saveAsFile` | … als Datei speichern | … save as a file |
| `import.target.none` | Kein weiterer Kanal, in dem du Broadcaster oder 7TV-Editor bist. | No other channel where you are broadcaster or 7TV editor. |
| `import.target.listIncomplete` | Die Kanalliste ist gerade unvollständig — fehlende Kanäle erscheinen nach einem erneuten Laden. | The channel list is incomplete right now — missing channels appear after a reload. |
| `import.target.reauthRequired` | Deine Twitch-Anmeldung ist abgelaufen oder wurde widerrufen — Zielkanäle gibt es erst nach einem neuen Login. | Your Twitch login has expired or was revoked — target channels appear after a fresh login. |
| `import.target.loadFailed` | Die Kanalliste konnte nicht geladen werden. | The channel list could not be loaded. |
| `import.target.retry` | Erneut laden | Reload |
| `import.target.submit` | Weiter | Continue |
| `import.confirm.title.one` | {{ count }} Emote nach {{ channel }} kopieren? | Copy {{ count }} emote to {{ channel }}? |
| `import.confirm.title.other` | {{ count }} Emotes nach {{ channel }} kopieren? | Copy {{ count }} emotes to {{ channel }}? |
| `import.confirm.originChannel` | Aus Kanal {{ channel }} | From channel {{ channel }} |
| `import.confirm.originFile` | Aus Datei {{ fileName }} | From file {{ fileName }} |
| `import.confirm.originFileDetails` | Export aus {{ channel }}, {{ date }} | Exported from {{ channel }}, {{ date }} |
| `import.confirm.dateUnknown` | Datum unbekannt | date unknown |
| `import.confirm.channelUnknown` | Kanal unbekannt | channel unknown |
| `import.confirm.target` | Ziel: {{ channel }} · Set {{ setId }} | Target: {{ channel }} · set {{ setId }} |
| `import.confirm.loadingHint` | Zieldaten werden geladen… | Loading target data… |
| `import.confirm.noTargetSet` | Der Zielkanal hat noch kein aktives 7TV-Set — der erste Abgleich läuft noch oder ist fehlgeschlagen. | The target channel has no active 7TV set yet — its first sync is still running or has failed. |
| `import.confirm.loadFailed` | Die Daten des Zielkanals konnten nicht geladen werden. | The target channel's data could not be loaded. |
| `import.confirm.retry` | Erneut laden | Reload |
| `import.confirm.staleHint` | Der letzte Abgleich des Zielkanals ist fehlgeschlagen — unser Abbild kann veraltet sein, 7TV entscheidet. | The target channel's last sync failed — our copy may be stale, 7TV decides. |
| `import.confirm.alreadyPresent.one` | {{ count }} Emote ist bereits im Zielset und wird übersprungen. | {{ count }} emote is already in the target set and will be skipped. |
| `import.confirm.alreadyPresent.other` | {{ count }} Emotes sind bereits im Zielset und werden übersprungen. | {{ count }} emotes are already in the target set and will be skipped. |
| `import.confirm.nameCollisions.one` | {{ count }} Name ist im Zielset schon vergeben — 7TV wird dieses Emote ablehnen: | {{ count }} name is already taken in the target set — 7TV will reject this emote: |
| `import.confirm.nameCollisions.other` | {{ count }} Namen sind im Zielset schon vergeben — 7TV wird diese Emotes ablehnen: | {{ count }} names are already taken in the target set — 7TV will reject these emotes: |
| `import.confirm.discardedRows.one` | {{ count }} ungültige Zeile in der Quelle verworfen. | {{ count }} invalid row in the source discarded. |
| `import.confirm.discardedRows.other` | {{ count }} ungültige Zeilen in der Quelle verworfen. | {{ count }} invalid rows in the source discarded. |
| `import.confirm.duplicatesCollapsed.one` | {{ count }} doppelte Zeile in der Quelle zusammengefasst. | {{ count }} duplicate row in the source collapsed. |
| `import.confirm.duplicatesCollapsed.other` | {{ count }} doppelte Zeilen in der Quelle zusammengefasst. | {{ count }} duplicate rows in the source collapsed. |
| `import.confirm.nothingToAdd` | Alle {{ count }} Emotes sind bereits im Zielset. | All {{ count }} emotes are already in the target set. |
| `import.confirm.sameChannelFile` | Diese Liste stammt aus diesem Kanal. | This list comes from this channel. |
| `import.confirm.runNotice` | Das Hinzufügen läuft danach automatisch nacheinander mit kurzer Verzögerung zwischen den Emotes. | Adding then runs automatically, one emote at a time, with a short delay between each. |
| `import.confirm.execute` | Kopieren | Copy |
| `import.progress` | {{ finished }} / {{ total }} kopiert | {{ finished }} / {{ total }} copied |
| `import.deleteFailedFallback` | Kopieren fehlgeschlagen | Copy failed |
| `import.rateLimitPaused` | 7TV-Rate-Limit erreicht — es geht in {{ seconds }} s automatisch weiter. | 7TV rate limit reached — resuming automatically in {{ seconds }}s. |
| `import.summary.counts` | {{done}} kopiert · {{failed}} fehlgeschlagen · {{cancelled}} abgebrochen | {{done}} copied · {{failed}} failed · {{cancelled}} cancelled |
| `import.summary.target` | Ziel: {{ channel }} | Target: {{ channel }} |
| `import.summary.openTarget` | Zielkanal öffnen | Open target channel |
| `import.summary.insufficientPrivileges` | Das 7TV-Token hat im Zielset kein Schreibrecht — der Lauf wurde nach der ersten Zeile abgebrochen. | The 7TV token has no write access to the target set — the run stopped after the first row. |
| `import.syncFailedTitle` | Rückmeldung an EmotePurge fehlgeschlagen | Reporting back to EmotePurge failed |
| `import.syncFailed` | Die Emotes sind bei 7TV hinzugefügt, aber EmotePurge konnte es nicht vermerken. Der Zielkanal übernimmt sie beim nächsten Abgleich trotzdem. | The emotes were added on 7TV, but EmotePurge could not record it. The target channel still picks them up on its next sync. |
| `import.syncRetry` | Erneut melden | Report again |
| `import.syncRetrySucceeded` | Rückmeldung erfolgreich. | Reported back successfully. |
| `import.resync.pending` | Abgleich des Zielkanals wird angestoßen… | Triggering the target channel's sync… |
| `import.resync.succeeded` | Abgleich angestoßen — der Zielkanal zeigt die Emotes gleich. | Sync triggered — the target channel shows the emotes in a moment. |
| `import.resync.cooldown` | Der Zielkanal übernimmt die Emotes beim nächsten Abgleich. | The target channel picks up the emotes on its next sync. |
| `import.resync.failed` | Abgleich konnte nicht angestoßen werden — der periodische Abgleich holt es innerhalb einer Minute nach. | Could not trigger the sync — the periodic sync catches up within a minute. |
| `import.leaveWhileRunning.message` | Der Kopierlauf läuft noch. Er läuft im Hintergrund weiter, sein Fortschritt ist aber nur auf der Emote-Nutzungsseite sichtbar. Seite trotzdem verlassen? | The copy run is still going. It continues in the background, but its progress is only visible on the emote-usage page. Leave the page anyway? |
| `import.leaveWhileRunning.confirm` | Verlassen | Leave |
| `restore.import.trigger` (geändert) | Datei importieren | Import file |
| `restore.import.hint` (geändert) | Ein Purge-Protokoll (Wiederherstellen), eine Emote-Liste oder einen Nutzungs-Export (Kopieren) als JSON einspielen. | Load a purge protocol (restore), an emote list or a usage export (copy) as JSON. |
| `restore.import.fileLabel` (geändert) | Protokoll, Emote-Liste oder Nutzungs-Export auswählen | Choose a protocol, emote list or usage export |
| `restore.import.errors.wrongKind` (geändert) | Die Datei ist kein EmotePurge-Export. Importierbar sind Purge-Protokolle, Emote-Listen und Nutzungs-Exporte im JSON-Format. | The file is not an EmotePurge export. Importable are purge protocols, emote lists and usage exports in JSON format. |
| `restore.import.errors.wrongVersion` (geändert) | Die Datei stammt aus einer neueren EmotePurge-Version. | The file comes from a newer EmotePurge version. |
| `restore.import.errors.noRows` (neu) | Die Datei enthält keine importierbaren Emotes. | The file contains no importable emotes. |
| `restore.import.errors.usageExport` | **entfernt** | **entfernt** |

Wiederverwendet ohne Änderung: `export.scopeLabel`, `export.scopeVisible`, `export.scopeSelection`,
`restore.capacityProjection`, `restore.capacityWarning`, `massDelete.sharedSetWarningTitle`,
`massDelete.notOwnSet`, `massDelete.knownAffected`, `massDelete.moderatedAffected`,
`massDelete.ownershipCheckUnavailable`, `common.cancel`, `common.close`, `common.loading`,
`errors.api.invalid_source_kind`. Die Schlüssel unter `import.*`, die `run-progress-panel` per
`labelPrefix + '.…'` bildet, **müssen** genau so heißen (auch `deleteFailedFallback` — der Name ist
Bestandsvertrag des Panels, nicht Geschmack).

---

## 3. Risiken und Grenzfälle — mit Entscheidung

Alles, was mit **„Entscheidung im Plan, nicht in der Issue"** markiert ist, legt der Orchestrator dem
Nutzer vor; der Plan ist bereits danach geschrieben.

### R1 — Arbiter: dritter Zweig statt Meldung; keine DI-Zirkularität

Punkt 7 der Issue („meldet Start/Ende an den Arbiter") ist gegenstandslos (Plan-70, Entscheidung 1).
Der Arbiter bekommt `if (importService.isRunning()) return 'import'` als dritten Zweig **nach**
`restore`. Kante: Arbiter → ImportService → { ChannelService, EmoteAdminService, HttpClient,
SevenTvTokenService, TranslocoService }; **keiner** davon kennt den Arbiter, genau wie Delete- und
Restore-Service ihn heute nicht kennen (Plan-70 R6: die Panels injizieren ihn direkt). Der
Import-Service injiziert den Arbiter deshalb **nicht** — die Sperrprüfung vor dem Start macht der
Aufrufer (Flow, R2).

**Verhalten bei Parallelität** (Entscheidung im Plan, nicht in der Issue): Der Header-Button „In Kanal
kopieren…" ist wie der Restore-Trigger `disabled`, solange `arbiter.activeRun() !== null` — auch wenn
Picker und Datei-Export lesend wären. Begründung: dieselbe Regel für alle vier Start-Stellen
(`UI-Designsprache.md:182`), und ein Picker, der am Ende einen gesperrten Dialog zeigt, ist die
schlechtere Erfahrung als ein grauer Knopf neben dem laufenden Fortschritt im selben Dock.
Zusätzlich prüft der Flow unmittelbar vor `startImport` noch einmal `activeRun() === null` und bricht
sonst still ab (die Engine würde einen fremden Lauf nicht abweisen — sie kennt nur ihren eigenen);
der Bestätigungsdialog bekommt `runBlocked` live und deaktiviert „Kopieren" mit Grundtext (§7).
Umgekehrt sperrt ein laufender Import automatisch den Delete-Button (beide Seiten) und den
Restore-Trigger — die vier Bestandsstellen lesen `activeRun()`, keine Änderung dort nötig.

### R2 — Reihenfolge: Picker → Zieldaten + Bestätigung → Token → Lauf

**Entscheidung im Plan, nicht in der Issue:** Der Token-Prompt kommt **nach** der Bestätigung (wie
Issue-Punkt 6 sagt) und damit **anders als bei Delete/Restore** (dort Token vor Bestätigung, was das
Design mit „wie beim Restore" meinte). Grund: Picker und Vorschau sind reine Lesevorgänge; ein
Secret zu verlangen, bevor der Nutzer überhaupt gesehen hat, was passieren würde, ist die falsche
Reihenfolge, und die Vorschau ist hier — anders als beim Restore — der Ort, an dem die eigentliche
Entscheidung fällt. `SevenTvTokenPromptDialog` schließt sich mit `true`, sobald ein Token gespeichert
ist, und kettet in `startImport`; Abbruch am Prompt = kein Lauf, nichts geschrieben. Delete/Restore
bleiben unverändert. Der Satz dazu gehört nach `UI-Designsprache.md` §7 (T10).

Der Flow ist eine Funktion, kein Service: `startImportFlow(deps, source, targetChannelName)` in
`shared/seven-tv/import-flow.ts` mit `deps = { dialog, emoteAdminService, tokenService,
importService, arbiter }` — beide Einstiege (Seite, Restore-Panel) rufen sie mit ihren injizierten
Instanzen. Ein Service ginge nicht: der Flow öffnet Dialoge aus `shared/ui`, und `core/` darf nichts
aus `shared/` importieren. Ablauf: Zieldaten-Signal anlegen und `loadImportTarget` starten (R8) →
`openImportConfirmDialog` sofort → bei `ImportConfirmOutcome`: Token vorhanden? sonst Prompt →
`activeRun() === null`? → `startImport`.

### R3 — `sourceChannelName` beim Datei-Weg ist `null`

Backend-Vertrag (verifiziert, `EmoteEndpoints.cs:153-156`): `file` **mit** Namen ist `400`. Der
Nachlauf sendet für `origin.kind === 'file'` also `{ sourceKind: 'file', sourceChannelName: null }`,
auch wenn die Datei einen Kanal trägt; für `channel` den Quellkanal. Folge: der Audit-Eintrag eines
Datei-Imports nennt keinen Herkunftskanal — das ist der K2-Vertrag, nicht K3s Sache; als bekannte
Grenze in den DECISIONS-Absatz (T1).

### R4 — Eigene Envelope für `emote-list`

`buildPurgeRunProtocol` verlangt `emoteId` (Plan-70 R3, absichtlich). Die `emote-list`-Envelope
entsteht in `shared/export/emote-list-export.ts` direkt über `buildEnvelope` aus `export-envelope.ts`
(das ist die geteilte Stelle: `source`, `formatVersion`, `exportedAt`), mit `kind: 'emote-list'`,
`withheld: []`, `meta { sourceEmoteSetId, rowCount, scope }`, `rows: ImportRow[]`. **Kein CSV**
(Design: CSV trägt keine Envelope). Dateiname `emotepurge_<kanal>_emote-list_<yyyy-mm-dd>.json` —
Datum, nicht Minute wie beim Purge-Protokoll (AK 5 sagt `<datum>`; eine Emote-Liste wird nicht
mehrmals täglich erzeugt, und der Browser nummeriert Kollisionen ohnehin).

### R5 — `ImportSource` liegt in `core/`

Der Import-Service (core) trägt `origin` als Teil von `run` (Zusammenfassung „aus Kanal X") und
braucht `sourceKind`/`sourceChannelName` für den Nachlauf. Der Typ steht deshalb in
`core/seven-tv/import-source.ts`, nicht in `shared/seven-tv/` (Dateitabelle der Issue). Parser
(shared/export) und Vorschau (shared/seven-tv) importieren ihn von dort — erlaubte Richtung.

### R6 — `duplicatesCollapsed` entsteht in `dedupeImportRows`

Es gibt die Zahl heute nirgends. Beide Herkünfte laufen durch dieselbe pure Funktion: die Datei im
Parser, die Auswahl/sichtbare Liste in `openImportTarget()` der Seite. Für den Kanal-Weg ist die Zahl
konstruktionsbedingt 0 (Unique-Index `(ChannelId, SevenTvEmoteId)`, Regel 8) — die Funktion läuft
trotzdem, damit der Vertrag „`rows` ist dedupliziert" an genau einer Stelle gilt. Die Dialogzeile
erscheint nur bei `> 0`.

### R7 — `projectSlots()` ist eine eigene Datei, `buildImportPreview` eine andere

Der Restore-Dialog braucht nur die Projektion, der Import-Dialog beides. Zwei Dateien, zwei Specs;
`restore-confirm-dialog.ts` ruft `projectSlots` statt seiner Inline-Logik (Verhalten identisch —
kein Bestandsfall ändert sich, es gibt für die Komponente keinen). `slotBudget()` aus
`shared/emotes/slot-budget.ts` ist **kein** Ersatz: es rechnet Entnahme mit Clamping, nicht Zugang
mit Überlauf.

### R8 — Skeleton-Choreografie: `forkJoin` darf nie fehlschlagen

Issue-Punkt 4 kollidiert mit sich selbst: `forkJoin` bricht beim ersten Fehler ab, „schlägt nur
`getSetWarning` fehl, bleibt der Lauf möglich" verlangt das Gegenteil. **Auflösung:** jede der drei
inneren Quellen fängt ihren Fehler selbst und liefert einen getaggten Wert; `forkJoin` kombiniert nur
noch Werte und `loadImportTarget` emittiert **genau einmal** einen `ImportTargetLoadState`:

- `getSetStatus` → `404` ⇒ `no-set`; anderer Fehler ⇒ `failed`; Erfolg mit leerem
  `activeEmoteSetId` ⇒ `no-set`.
- `listEmotes` → `404` ⇒ `no-set`; anderer Fehler ⇒ `failed`.
- `getSetWarning` → Fehler ⇒ `warning = { available: false, isOwnSet: false, …: [] }` (die Form aus
  `mass-delete-panel.ts:402-407`), **kein** `failed`.
- Präzedenz bei mehreren Ausfällen: `no-set` vor `failed` (ein 404 ist die endgültigere Aussage).

Der Dialog öffnet sofort mit dem Signal auf `loading` (handgerolltes Skeleton nach §6.1: **ein**
`role="status"` mit `aria-label` `common.loading`, Schimmerblöcke `aria-hidden`, Umriss = die drei
Textzeilen Herkunft/Ziel/Projektion). `retry()` setzt das Signal auf `loading` und lädt neu. Der
Ausführen-Knopf ist bei `loading`, `failed`, `no-set`, leerer Restliste und `runBlocked` gesperrt und
trägt seinen Grund als Text in der Aktionszeile (`mr-auto`, `aria-describedby`, Muster
`delete-confirm-dialog.ts:76-80`): `loadingHint`, `loadFailed` (+ Retry-Button daneben),
`noTargetSet`, `nothingToAdd`; bei `runBlocked` kein Text (§4.2-Satz aus #70 — der laufende
Fortschritt ist der Hinweis).

### R9 — Kein Kanal-Reset; das Dock zeigt den Lauf auf jeder Usage-Seite

`ChannelWorkspaceLayout` ruft `resetIfChannelChanged` für Delete/Restore (`:211-212`); der
Import-Service bekommt die Methode **nicht** und wird dort **nicht** aufgerufen. Der Lauf schreibt in
Kanal B, während die Seite an A hängt; ein Reset beim Wechsel nach B (dem Ziel!) wäre absurd. Damit
ein auf Kanal B oder C sichtbarer Lauf nicht wie ein Lauf *dieses* Kanals wirkt (genau die Verwechslung,
die den Reset bei Delete eingeführt hat), trägt die Import-Sektion in jeder Phase die Zeile
`import.summary.target` mit dem Zielkanal. Die Sektion sitzt **außerhalb** des `activeEmoteSetId()`-
Gates, das nur die Markier-Hälfte des Docks (Zählzeile, Mass-Delete-Panel) umschließt: auf einer
Usage-Seite ohne aktives Set verschwände sonst ein schreibender Lauf samt Abbrechen-Knopf, während
er weiterläuft. Bekannte Grenzen, bewusst: (b) die Voting-Seite montiert nur das Mass-Delete-Panel,
zeigt also keinen Import-Fortschritt, ihr Delete-Button ist aber gesperrt (R1); (c) über der Sektion
steht „0 markiert", wie heute nach einem Delete-Lauf — auf einer Seite ohne Set fehlt diese Zeile
ganz. Nichts davon wird in K3 umgebaut (Design, Offene Frage 1: seitenunabhängiger Indikator ist
Folgearbeit).

### R10 — Parallel laufende Subagents im selben Worktree

Die Tasks laufen ohne Isolation im selben Verzeichnis. Zwei Regeln:

- **Locale-Dateien** (`de.json`/`en.json`, Paritäts-Spec) fasst je Welle genau **ein** Task an: T0
  legt alle Schlüssel aus 2.9 an (außer der Entfernung von `usageExport`); T2 entfernt `usageExport`
  zusammen mit dem Code. Kein anderer Task berührt die Locales.
- **`docs/DECISIONS.md`** fasst je Welle genau **ein** Task an: T1 legt den Eintrag an. T2 (parallel zu
  T1) liefert seinen Absatz als Text im Abschlussbericht; der Orchestrator fügt ihn vor dem Commit
  von T2 ein (Regel 3 gilt für den Commit, nicht für den Subagent). Alle späteren Tasks laufen
  sequenziell und schreiben ihre Absätze selbst. **Vor jedem Schreiben `head -20 docs/DECISIONS.md`
  lesen** — die #69-Sitzung schreibt parallel dorthin; der Eintrag ist klein und additiv, oben unter
  der Trennlinie in absteigender Datumsordnung.

### R11 — Leave-Guard

`CanDeactivateFn` an der `usage-stats`-Route (`app.routes.ts:87-91`, neben `canActivate`). `true`,
wenn `importService.isRunning()` falsch ist; sonst `openConfirmDialog` mit den beiden übersetzten
Strings aus `import.leaveWhileRunning.*` (via `TranslocoService.translate`) und `closed.pipe(map(v => v === true))`.
Der Bestätigen-Knopf des `ConfirmDialog` ist `danger-solid` — für ein Verlassen, das nichts zerstört,
eine leichte Übertreibung, aber ein vierter Dialog für einen Satz wäre die größere Abweichung von §7
(Entscheidung im Plan, nicht in der Issue). **Zu verifizieren, nicht anzunehmen** (E2E, T9): ob der
Guard auch beim Kanalwechsel `/channels/a/usage-stats → /channels/b/usage-stats` feuert (Angular
führt `canDeactivate` bei `runGuardsAndResolvers: 'paramsChange'` erneut aus, die Komponente wird
dabei wiederverwendet). Ein Reload/Tab-Schließen deckt der Guard nicht (kein `beforeunload`; Design:
„Reload verliert den Lauf im Browser").

### R12 — Scope-Vorbelegung im Picker

**Entscheidung im Plan, nicht in der Issue:** Bei vorhandener Auswahl ist `selection` vorbelegt (der
Export-Dialog belegt `visible` vor). Begründung: beim Export ist stilles *Verengen* die Gefahr, beim
Kopieren in ein fremdes Set ist stilles *Verbreitern* auf 654 sichtbare Emotes die Gefahr — und der
Wedge des Designs ist „auswählen, dann kopieren". Ohne Auswahl gibt es kein Radio, Scope ist `visible`.

### R13 — Privilegien-Sonde

`abortOn` liefert `true` bei `httpStatus ∈ {401, 403}` oder wenn `message.toLowerCase()`
`insufficient privileges` oder `missing permission` enthält. Rate-Limit-Aufgeben (übersetzter Text,
`null`) und Netzwerkfehler (`0`) laufen weiter. Der Service setzt dabei `abortedForPrivileges` auf
`true` (die Zusammenfassung zeigt `import.summary.insufficientPrivileges`). Die beiden 7TV-Texte sind
aus dem Design übernommen und **nicht live belegt** — AK 8 der Live-Probe (T11) prüft sie mit einem
Token ohne Editor-Recht; weicht der Text ab, wird die Liste erweitert, nicht die Bedingung gelockert.

### R14 — Kein Komponententest, aber E2E mit gemocktem 7TV

Regel 12: keine isolierten Komponententests. Die Dialoge werden per E2E (`/api/**` gemockt) und
Audit-Harness gesehen. Für den Lauf selbst lässt sich `https://7tv.io/v3/gql` in Playwright routen
und das Token per `addInitScript` in `sessionStorage` (`ep_7tv_write_token`) legen — damit sind AK 8/9/12
ohne Live-Zugang prüfbar (T9, Fall 4).

### R15 — Nachlauf ans Laufobjekt binden, nicht an ein loses Feld

Befund eines adversarialen Codex-Reviews (Gegenprüfung + Schiedsspruch bestätigt): `finish()` setzt
`isRunning` **vor** dem Nachlauf (`seven-tv-run-engine.ts:591-592` ruft `finish()`, `onComplete`
startet erst danach die asynchronen Aufrufe) — der Arbiter leitet `activeRun` allein aus `isRunning`
ab, also kann ein zweiter Import starten, während der Nachlauf des ersten noch fliegt. Hielte der
Import-Service Zielkanal und gemeldete Keys wie der Restore-Service in verteilten losen Feldern
(`currentChannelName`, `lastReportedIds`), könnte `retrySyncReport()` die Keys von Lauf 1 (Ziel B) an
den Zielkanal von Lauf 2 (Ziel C) senden — falscher Audit-Eintrag plus Emote-Zeilen in einem dritten
Kanal, die der nächste Resync wieder archiviert. **Fix** (2.6, T1): Zielkanal, Herkunft und die
gemeldeten Keys stehen zusammen an genau einem Objekt, dem beim Start angelegten `ImportRunInfo`;
jeder asynchrone Schreibzugriff auf `syncReport`/`resyncTrigger` prüft vorher, ob dieses Objekt noch
`run()` ist, und verwirft die Antwort sonst kommentarlos; `retrySyncReport()` liest Ziel und Keys aus
demselben Objekt.

Delete und Restore haben dieselbe Struktur (`currentChannelName`/`lastReportedIds` neben dem Service,
kein Bezug zu einem Laufobjekt) und denselben theoretischen Fehler — dort bleibt er **folgenlos**,
weil die IDs beim Delete/Restore immer im selben Kanal landen und das Backend sie als `notFoundIds`
verschluckt. Das Nachziehen der beiden Bestandspfade wäre deshalb verzichtbar. Der Nutzer hat sich am
2026-09-05 dennoch dafür entschieden, es in denselben Branch zu legen, damit alle drei Nachläufe zum
Review-Zeitpunkt dieselbe Form haben — s. **T12** und Abschnitt 6, Punkt 14.

### R16 — Design fordert ein exportierbares Protokoll, Issue schließt „Datei" aus — Issue gewinnt

Das Design (`docs/designs/Emote-Import-38-2026-09-05.md:256`) sagt wörtlich: „Ergebnis als Protokoll
mit `done` / `failed` / `cancelled` je Zeile, exportierbar." Issue #72 führt unter „Out of Scope"
dagegen ausdrücklich „Import-Protokoll als Datei" — ein Widerspruch, den frühere Plan-Fassungen nicht
benannt haben. **Entscheidung:** die Issue gewinnt (spätere, engere Scoping-Entscheidung desselben
Autors; ein Import-Protokoll hat anders als das Purge-Protokoll keinen Konsumenten — es gibt keinen
Rückweg wie beim Restore). Die Bildschirm-Zusammenfassung mit `done`/`failed`/`cancelled` je Zeile
bleibt (2.9, `import.summary.counts`); ein Datei-Export dieses Protokolls ist **nicht** Teil von #72.
Kein `DECISIONS.md`-Eintrag dazu — das ist Umfang, weder Vertrag noch Konvention.

---

## 4. Tasks

Lanes: **T0 zuerst** (alle anderen bauen auf seinen Typen/Schlüsseln auf). Danach **Welle 1** mit
vier parallelen Lanes A–D (disjunkte Dateien). Dann **Welle 2** (T5, T6, T7, T8 — T8 parallel zu T5;
T6 und T7 nach T5, T6 ∥ T7). Dann T9, T10, T11 sequenziell.

```
T0 ─┬─ A: T1 ──────────┐
    ├─ B: T2 ──────────┤
    ├─ C: T3 ──────────┼─ T5 ─┬─ T6 ─┐
    └─ D: T4 ──────────┘      └─ T7 ─┼─ T9 ─ T10 ─ T11
                    T8 (∥ T5) ───────┘
```

Jeder Task nennt die Specs, die er laufen lässt (`npx vitest run <pfad>` aus `web/`), nie die volle Suite.

### T0 — Verträge und i18n-Schlüssel (Vorbedingung für alles) — `sonnet`

**Ziel.** Die Typen aus 2.1 und alle Schlüssel aus 2.9 existieren, damit vier Lanes parallel
starten können, ohne dieselben Dateien anzufassen.

**Betroffene Dateien.** Neu: `web/src/app/core/seven-tv/import-source.ts` (+ `import-source.spec.ts`).
Geändert: `web/public/i18n/de.json`, `en.json`; `web/src/app/shared/seven-tv/run-progress-panel.ts`
(nur die `labelPrefix`-Union um `'import'`); `web/src/app/core/seven-tv/seven-tv-run-arbiter.ts`
**nicht** (das ist T1).

**Vertrag.** 2.1 wörtlich. Locales: Gruppe `import` neu, `restore.import.*` geändert/ergänzt wie in
2.9 — **außer** der Entfernung von `usageExport` (macht T2 mit dem Code). Beide Dateien in derselben
Schlüsselreihenfolge.

**Grenzfälle.** `dedupeImportRows` behält die erste Zeile je ID und die Reihenfolge; leere Eingabe →
leer, 0.

**Tests.** `import-source.spec.ts`: (1) Duplikat wird gezählt und die erste Zeile behalten; (2) ohne
Duplikate `duplicatesCollapsed === 0` und identische Reihenfolge. Danach
`npx vitest run src/app/core/i18n/api-error-locales.spec.ts src/app/core/seven-tv/import-source.spec.ts`.

**DoD.** Beide Specs grün; `npm --prefix web run lint`; `format:check`; `grep -c '"import"' web/public/i18n/de.json` = 1 auf Top-Level.

### T1 — `SevenTvImportService` + Arbiter-Zweig (Lane A) — `opus`

**Ziel.** Der Lauf: eigene Engine, ADD-Mutation, Privilegien-Sonde, Nachlauf mit Gate auf
`doneKeys`, Resync-Cooldown, Zustand für die Zusammenfassung. Plus der dritte Arbiter-Zweig.

**Betroffene Dateien.** Neu: `web/src/app/core/seven-tv/seven-tv-import.service.ts` (+ `.spec.ts`).
Geändert: `seven-tv-run-arbiter.ts` (+ `.spec.ts`, ein Fall), `docs/DECISIONS.md` (Eintrag anlegen, R10).
Nicht anfassen: Delete-/Restore-Service, Engine, Panels.

**Vertrag.** 2.6 und 2.8. Im Einzelnen:
- Die ADD-Mutation ist dieselbe wie im Restore-Service (`seven-tv-restore.service.ts:17-28`, `name` =
  Quellname); `RunOperation.label = 'import'`, `abortOn` nach R13.
- `startImport` prägt `RunQueueEmote { key: sevenTvEmoteId, sevenTvEmoteId, name }` ohne `emoteId`;
  bei `engine.start === false` keinen eigenen Zustand anlegen (Muster `:135-137`).
- `onRunComplete`: schließt über den beim `startImport` angelegten `ImportRunInfo` (R15) und
  vervollständigt ihn mit `result`; **nur** bei `result.doneKeys.length > 0`:
  `syncImported(targetChannelName, { sevenTvEmoteIds: doneKeys, sourceChannelName: origin.kind === 'channel' ? origin.channelName : null, sourceKind: origin.kind })`
  mit derselben Retry-Policy wie der Restore (`MAX_AUTOMATIC_SYNC_RETRIES`, `SYNC_RETRY_DELAY_MS`,
  401/403 nicht wiederholen) und parallel `channelService.resync(targetChannelName)` mit
  `429 → 'cooldown'`, sonst `'failed'`/`'succeeded'`. `syncReport` kennt hier nur
  `idle/pending/succeeded/failed` (`204` ohne Body — kein `partial`). Beide Antworten schreiben in
  `syncReport`/`resyncTrigger` nur, solange der geschlossene `ImportRunInfo` noch `=== run()` ist
  (R15) — eine späte Antwort eines Vorgängerlaufs wird verworfen, ohne den Zustand des inzwischen
  laufenden (oder schon abgeschlossenen) nächsten Laufs zu berühren.
- `retrySyncReport()` **anders als im Restore** (dort gegen die losen Felder
  `currentChannelName`/`lastReportedIds`): liest Zielkanal und Keys aus `run()!.targetChannelName`
  und `run()!.result!.doneKeys` — demselben Objekt, nie aus einem Feld daneben (R15).
- `reset()` leert Engine, `run`, `syncReport`, `resyncTrigger`, `abortedForPrivileges`.
- Decorator `@Service()`; Rückfall `@Injectable({ providedIn: 'root' })` mit Hinweis im Bericht.
- Arbiter: dritter Zweig, Klassenkommentar-Satz „'import' is reserved for K3" ersetzen; JSDoc-Hinweis,
  dass der Import-Service den Arbiter nicht kennt (R1).

**Grenzfälle.** Abbruch durch `abortOn` auf der ersten Zeile → `doneKeys` leer → kein Nachlauf, aber
`run.result` gesetzt und `abortedForPrivileges === true`. `cancel()` mitten im Lauf mit 2 `done` →
Nachlauf für genau diese 2. Nachlauf-`404` (Zielkanal unbekannt — theoretisch, der Picker sperrt
ungetrackte) → `syncReport 'failed'`, Retry möglich. Datei-Herkunft → Body mit `sourceChannelName: null`
**auch wenn** `origin.channelName` gesetzt ist (R3). Verzögerter Nachlauf von Lauf 1, sofort
gestarteter Lauf 2 (anderer Zielkanal) → die späte Antwort von Lauf 1 darf `run()`, `syncReport` und
`resyncTrigger` von Lauf 2 nicht verändern (R15); ein Retry, während Lauf 2 sichtbar ist, sendet
dessen Keys, nie die von Lauf 1.

**Tests (vor der Umsetzung).** `seven-tv-import.service.spec.ts` nach dem Muster von
`seven-tv-restore.service.spec.ts` (TestBed, `HttpTestingController`, Fake-Timer, `RUN_DELAY_MS`):
1. Queue-Zeilen tragen `key === sevenTvEmoteId` und **kein** `emoteId`; die Mutation sendet `setId`,
   `emoteId` (= 7TV-ID) und `name`.
2. Nachlauf nach 2 `done`: `sync-imported`-Body exakt `{ sevenTvEmoteIds, sourceChannelName: 'brudivoeller_tv', sourceKind: 'channel' }`,
   danach genau ein `POST …/resync`; `syncReport 'succeeded'`, `resyncTrigger 'succeeded'`.
3. Datei-Herkunft mit gesetztem `channelName` → Body `sourceChannelName: null`, `sourceKind: 'file'`.
4. `abortOn`: GQL-Fehler `insufficient privileges` auf Zeile 1 von 3 → Status `['failed','cancelled','cancelled']`,
   `abortedForPrivileges === true`, **kein** `sync-imported`, **kein** `resync`.
5. `abortOn` bleibt ruhig bei GQL-Fehler `conflicting name` → Lauf geht weiter, Zeile `failed`, `abortedForPrivileges === false`.
6. Resync `429` → `resyncTrigger 'cooldown'`, `syncReport` unberührt.
7. `reset()` leert alles; `run()` ist `null`.
8. **Antwort eines Vorgängerlaufs wird ignoriert** (R15): Lauf 1 (Ziel B) beendet mit 2 `done`, sein
   `sync-imported`/`resync` bleibt am offenen `HttpTestingController`-Request hängen; sofort
   `startImport` für Lauf 2 (Ziel C, andere Zeilen) — läuft, weil `isRunning()` nach `finish()` schon
   `false` ist. Lauf 1s Antwort auflösen: `run()`, `syncReport`, `resyncTrigger` bleiben auf dem Stand
   von Lauf 2 unverändert. `retrySyncReport()` danach sendet die Keys **von Lauf 2** an **Kanal C**,
   nie die von Lauf 1.
`seven-tv-run-arbiter.spec.ts`: (9) Import läuft → `'import'`, danach `null`; Reihenfolge: Delete
**und** Import gleichzeitig → `'delete'`.

**Doku.** DECISIONS-Eintrag „Import-UI: Push aus dem Quellkanal, ein Dialog trägt die Last, und das
Restore-Panel nimmt drei Sorten an (#72)" mit `**Betrifft:**`-Zeile (wächst über die Tasks). Absatz
dieses Tasks: Import-Service ohne Kanal-Reset und warum (R9); dritter Arbiter-Zweig statt Meldung;
Datei-Import sendet `sourceChannelName: null` als Folge des K2-Vertrags (R3); Privilegien-Sonde als
`abortOn`-Konsument; Nachlauf ans Laufobjekt gebunden statt an lose Felder, und warum derselbe Fehler
bei Delete/Restore folgenlos bleibt (R15).

**DoD.** `npx vitest run src/app/core/seven-tv` grün (+9); Lint; Format; kein Import aus `shared/`
oder `features/` in `core/`.

### T2 — Datei-Format: `readEnvelope`, `parseImportSource`, `emote-list`-Envelope (Lane B) — `sonnet`

**Ziel.** Der Datei-Weg kann drei Sorten lesen und eine neue schreiben; `parsePurgeRunProtocol`
verhält sich unverändert.

**Betroffene Dateien.** Neu: `web/src/app/shared/export/read-envelope.ts` (+ spec),
`import-source-parser.ts` (+ spec), `emote-list-export.ts` (+ spec). Geändert:
`export-envelope.ts` (Union), `purge-run-export.ts` (nutzt `readEnvelope`, `FOREIGN_KIND_ERROR_KEYS`
verliert `usage`), `purge-run-export.spec.ts` (Fall `:90-101` nur noch `voting`), `de.json`/`en.json`
(**nur** `restore.import.errors.usageExport` entfernen — sonst nichts, R10).

**Vertrag.** 2.7 wörtlich. `readEnvelope` ist der einzige Ort mit `JSON.parse` und `looksLikeExportCsv`
(die Funktion zieht mit um). `parsePurgeRunProtocol` ruft `readEnvelope` und prüft danach wie heute
`kind`, Version, Kanal, Set, Zeilen — Rückgabetyp und alle `errorKey`s unverändert.

**Grenzfälle.** `usage`-Datei mit `emoteName` → `name`; `emote-list`-Datei ohne `exportedAt` →
`origin.exportedAt === null`; `rows` mit einer kaputten Zeile → verworfen, Rest bleibt; zwei Zeilen
gleicher ID → eine, `duplicatesCollapsed 1`; `kind: 'purge-run'` im Import-Parser → `wrongKind`
(der Dispatch verhindert das ohnehin); Builder: `rows` genau `{ sevenTvEmoteId, name }`, **kein**
`emoteId`, keine Nutzungszahlen; Dateiname aus `sanitizeFilenamePart`. `meta.rowCount` fehlt oder ist
kein `number` → Sollzahl für `discardedRows` ist `rows.length` (die Länge des rohen Arrays), nicht die
der überlebenden Zeilen; `discardedRows` und `duplicatesCollapsed` zählen unabhängig (eine kaputte
plus eine doppelte gültige Zeile → beide `1`, nicht `2` bzw. `0`).

**Tests (vor der Umsetzung).** `read-envelope.spec.ts`: (1) Nicht-JSON → `notJson`; (2) CSV mit
`seven_tv_emote_id` → `csvInsteadOfJson`; (3) fremde `source` → `wrongKind`; (4) gültig → `envelope`
mit `kind`. `import-source-parser.spec.ts`: (5) `emote-list` → `ImportSource` mit `origin.kind 'file'`,
`envelopeKind`, `channelName`, `exportedAt`; (6) `usage` mappt `emoteName`; (7) `voting` →
`votingExport`; (8) `formatVersion 2` → `wrongVersion`; (9) Dedup + Zählung; (10) nur kaputte
Zeilen → `noRows`; (11) fehlendes `exportedAt` → `null`; (12) `discardedRows`: `meta.rowCount 5` mit
2 validen und 1 kaputten Zeile → `discardedRows 3` (gegen die Sollzahl, nicht gegen `rows.length`);
fehlt `meta.rowCount` oder ist kein `number` → `discardedRows` gegen `rows.length` gerechnet.
`emote-list-export.spec.ts`: (13) Envelope-Form und `meta`; (14) Dateiname mit Datum; (15) Zeilen
ohne `emoteId` (Schlüsselmenge exakt).
`purge-run-export.spec.ts`: Fall `:90-101` auf `voting` reduziert; alle anderen unverändert grün.
Laufen: `npx vitest run src/app/shared/export src/app/core/i18n/api-error-locales.spec.ts`.

**Doku.** DECISIONS-Absatz als **Text im Abschlussbericht** (R10): `emote-list` als vierte Envelope-
Sorte ohne `emoteId` (Regel 8), `readEnvelope` als gemeinsamer Vorschritt, `usage` als angenommene
Quelle und der entfallene Fehlerkey; `discardedRows` als eigener Zähler neben `duplicatesCollapsed`
(echter Datenverlust vs. Konsolidierung, gegen `meta.rowCount` gerechnet, falls vorhanden). Der
Orchestrator fügt ihn vor dem Commit ein.

**DoD.** Specs grün (+13, −0; ein Bestandsfall verengt); Lint; Format; `git diff --stat` zeigt in den
Locales genau zwei entfernte Zeilen.

### T3 — Zieldaten-Loader, Vorschau, Slot-Projektion (Lane C) — `sonnet`

**Ziel.** Die drei puren/orchestrierenden Bausteine, die der Bestätigungsdialog konsumiert.

**Betroffene Dateien.** Neu: `web/src/app/core/emotes/import-target-loader.ts` (+ spec),
`web/src/app/shared/seven-tv/import-preview.ts` (+ spec), `slot-projection.ts` (+ spec). Geändert:
`restore-confirm-dialog.ts` (Inline-Projektion `:84-91` durch `projectSlots` ersetzt, sonst nichts).

**Vertrag.** 2.2 und 2.3 wörtlich; Fehlerabbildung nach R8.

**Grenzfälle.** Loader: alle drei erfolgreich, aber `activeEmoteSetId === ''` → `no-set`;
`getSetWarning` 500 + Rest ok → `ready` mit `warning.available false`; `listEmotes` 404 + Status 500
→ `no-set`. Vorschau: Ziel enthält dieselbe ID unter anderem Namen → zählt als `alreadyPresent`,
**nicht** als Kollision; Kollision ist case-sensitiv (`PogU` ≠ `pogu`); Zielnamen dürfen doppelt
vorkommen (Duplikat-Sets), `nameCollisions` nennt den Namen einmal. Projektion: `capacity null` →
`null`; `occupied + toAdd === capacity` → kein Überlauf; `+1` → Überlauf.

**Tests (vor der Umsetzung).** `import-target-loader.spec.ts` (TestBed + `HttpTestingController`,
drei Requests je Fall): (1) alle ok → `ready` mit allen Feldern; (2) Warnung scheitert → `ready`,
`available false`; (3) Status 404 → `no-set`; (4) Liste 500 → `failed`; (5) leere Set-ID → `no-set`.
`import-preview.spec.ts`: (6) vorhanden/Kollision/Rest korrekt getrennt; (7) case-sensitiv;
(8) leere Restliste. `slot-projection.spec.ts`: (9) `null`-Fälle; (10) Grenze exakt.
Laufen: `npx vitest run src/app/core/emotes/import-target-loader.spec.ts src/app/shared/seven-tv`.

**DoD.** Specs grün (+10); Lint; Format; Diff an `restore-confirm-dialog.ts` beschränkt sich auf die
`projection`-Berechnung und einen Import.

### T4 — Ziel-Picker (Lane D) — `sonnet`

**Ziel.** Der Dialog, in dem Scope und Ziel gewählt werden; die Optionsberechnung als pure Funktion.

**Betroffene Dateien.** Neu: `web/src/app/shared/seven-tv/import-target-options.ts` (+ spec),
`import-target-dialog.ts`. Sonst nichts.

**Vertrag.** 2.4 wörtlich. Aufbau nach `export-dialog.ts` und §7: `DialogShell` mit Titel
`import.target.title`; Scope-Radiogruppe **nur** bei `selectionCount > 0` (Labels
`export.scopeVisible/scopeSelection` mit Zahlen, `aria-label` `export.scopeLabel`), Vorbelegung nach
R12; Ziel-Radiogruppe (`aria-label` `import.target.label`) mit den Optionen aus
`importTargetOptions` — deaktivierte Einträge tragen `disabled` **und** den Text
`import.target.notTracked` im Label; letzter Eintrag `import.target.saveAsFile`, immer wählbar;
keine Vorbelegung des Ziels (Weiter ist gesperrt, bis eines gewählt ist — Grundtext nicht nötig,
das Radio selbst ist die Aufforderung). Zustände: während `listMine` läuft ein Skeleton nach §6.1
(`<app-skeleton-rows [count]="3" />` passt hier — der Inhalt *ist* eine geriffelte Liste);
`reauthRequired` → `NoticeBanner warning` mit `import.target.reauthRequired`, Kanalliste leer, Datei
bleibt; `helixUnavailable || sevenTvUnavailable` → `NoticeBanner info` mit `import.target.listIncomplete`,
Liste nutzbar; HTTP-Fehler → `NoticeBanner error` mit `import.target.loadFailed` + `notice-action`-Button
`import.target.retry`; keine wählbaren Kanäle → Zeile `import.target.none`. Aktionszeile: Abbrechen,
dann `import.target.submit` (`primary`). Schließt mit `ImportTargetChoice` oder `undefined`.

**Grenzfälle.** Aktueller Kanal in Großschreibung im Input (`HandOfBlood`) → trotzdem ausgeschlossen
(Regel 9). Kanal mit `isModerator` allein → nicht gelistet. Beide Radiogruppen brauchen eindeutige
`name`-Attribute (`import-scope`, `import-target`).

**Tests (vor der Umsetzung).** `import-target-options.spec.ts`: (1) filtert auf
Broadcaster/Editor, schließt aktuellen Kanal normalisiert aus, sortiert ordinal; (2) `isTracked false`
→ `disabled`; (3) leere Eingabe → leer. Laufen: `npx vitest run src/app/shared/seven-tv/import-target-options.spec.ts`.
Dialog selbst: kein Komponententest (Regel 12), Sichtprüfung in T9.

**DoD.** Spec grün (+3); Lint; Format; `ng build`-Check per `npx ng build --configuration development`
nur, wenn Lint die Template-Fehler nicht schon zeigt.

### T5 — Bestätigungsdialog + Flow (Welle 2, nach T1–T4) — `opus`

**Ziel.** Der Dialog, der die ganze Last trägt, und die Funktion, die beide Einstiege zum Lauf führt.

**Betroffene Dateien.** Neu: `web/src/app/shared/seven-tv/import-confirm-dialog.ts`, `import-flow.ts`.
Geändert: `docs/DECISIONS.md` (Absatz).

**Vertrag.** 2.5 und R2. Dialoginhalt **in dieser Reihenfolge** (sie ist Vertrag, T10 schreibt sie in
§7): Titel `import.confirm.title` (Plural, `count` = Restliste, `channel` = Ziel) → Herkunftszeile
(`originChannel` oder `originFile` + `originFileDetails` mit `dateUnknown`/`channelUnknown` als
Platzhalter; Datum über `toLocaleDateString(toLocale(lang))` wie in `usage-stats-page.ts`) →
Zielzeile `import.confirm.target` → **Zustandsblock**: bei `no-set` `NoticeBanner warning`
`noTargetSet`; bei `failed` `NoticeBanner error` `loadFailed`; bei `loading` Skeleton (R8) → bei
`ready`: Fremdset-Warnung exakt nach `delete-confirm-dialog.ts:36-65` (gleiche Schlüssel, gleiche
Farbregel: rot nur bei `available && flagged`, amber bei `!available`) → Slot-Projektion über
`projectSlots(occupiedSlots, capacity, toAdd.length)` mit `restore.capacityProjection` und bei
Überlauf `NoticeBanner warning` + `restore.capacityWarning`; `syncFailureReason !== null` ergänzt
die stille Zeile `staleHint` → `alreadyPresent` (nur `> 0`) → `nameCollisions` (nur `> 0`, Text +
`<app-name-preview-list>` mit den Namen) → `discardedRows` (nur `> 0`; steht **vor**
`duplicatesCollapsed`, weil sie echten Datenverlust meldet — eine ungültige Zeile ist weg, eine
doppelte ist nur zusammengefasst — und der schwerere Befund zuerst steht) → `duplicatesCollapsed`
(nur `> 0`) → `nothingToAdd`
(`NoticeBanner info`, wenn `toAdd` leer; `count` = Quellzeilen) → `sameChannelFile` (nur wenn
`origin.kind === 'file'` und `origin.channelName` normalisiert gleich Ziel) → stille Zeile
`runNotice`. Aktionszeile: Grundtext (R8), Abbrechen, `import.confirm.execute` als `primary`
(Stufe wie `restore.confirmExecute`: konstruktiv, nicht `danger`). Schließt mit `ImportConfirmOutcome`
(die `toAdd`-Zeilen und `setId` aus dem `ready`-Zustand).

`import-flow.ts`: `startImportFlow(deps, source, targetChannelName): void` — legt `signal<ImportTargetLoadState>('loading')`
an, startet `loadImportTarget` und schreibt das Ergebnis; `retry` setzt `loading` und lädt neu; öffnet
den Dialog sofort; nach `outcome`: bei `!tokenService.hasToken()` `openSevenTvTokenPromptDialog`,
bei `saved` weiter; `arbiter.activeRun() !== null` → still zurück; sonst
`importService.startImport({ setId, channelName }, source.origin, outcome.rows)`.

**Grenzfälle.** Nutzer klickt Ausführen während der Lauf woanders gerade startete → `runBlocked`
sperrt live; Race auf der letzten Millisekunde fängt der Flow (R1). `retry` während ein Laden noch
läuft → das ältere Ergebnis darf das neuere nicht überschreiben (Zähler oder `switchMap` über ein
Trigger-Subject — Mechanismus frei, Test pinnt das Ergebnis nicht, Review prüft es). Dialog wird
geschlossen, bevor das Laden fertig ist → keine Fehler in der Konsole, Ergebnis verworfen.
`discardedRows > 0` zeigt die Hinweiszeile, sperrt `import.confirm.execute` aber **nicht** — anders
als `nothingToAdd`; die Zahl kommt unverändert aus `source.discardedRows`, wird hier nicht neu
berechnet.

**Tests.** Keine Komponententests (Regel 12). Der Flow ist Glue über Dialoge und wird in T9 (E2E)
geprüft; die puren Teile sind in T3 getestet. Die neue `discardedRows`-Zeile (Position, kein Sperren)
prüft ebenfalls T9 (Fall 2, dritte Datei) — keine eigene Testdatei hier. Laufen lassen:
`npx vitest run src/app/shared/seven-tv` (nur, dass nichts Bestehendes bricht) und Lint.

**Doku.** DECISIONS-Absatz: Token-Prompt nach Bestätigung und warum das vom Delete/Restore
abweicht (R2); Dialogzeilen-Reihenfolge als Vertrag; Loader emittiert genau einmal und nie einen
Fehler (R8).

**DoD.** Lint, Format, `npx ng build --configuration development` fehlerfrei; keine Locale-Änderung.

### T6 — Seite: Header-Button, Dock-Sektion, `dockVisible` (nach T5, ∥ T7) — `sonnet`

**Ziel.** Der Push-Einstieg auf der Usage-Stats-Seite und die Sichtbarkeit des Laufs.

**Betroffene Dateien.** `web/src/app/features/usage-stats/usage-stats-page.ts`, `.html`;
neu `web/src/app/shared/seven-tv/import-progress-section.ts`. Nicht: Restore-Panel, Routen, Locales.

**Vertrag.**
- Header (`.html:8-30`): zwischen Export und Aktualisieren ein Button `appButton="neutral"` mit
  `import.copyButton`, gerendert nur unter `@if (!isCoarse() && activeEmoteSetId())`, `disabled` bei
  `atlasOrder().length === 0 || arbiter.activeRun() !== null` (R1). Die Seite injiziert
  `SevenTvRunArbiter`, `SevenTvImportService`, `SevenTvTokenService` (Member-Reihenfolge nach
  `web/.claude/CLAUDE.md`).
- `openImportTarget()`: `openImportTargetDialog(dialog, { currentChannelName, visibleCount: atlasOrder().length, selectionCount })`;
  bei `choice`: Zeilen nach Scope (`selection.selectedItems()` bzw. `atlasOrder()`) auf `ImportRow`
  abbilden (`emoteName → name`), `dedupeImportRows`, `ImportSource { origin: { kind: 'channel', channelName } }`;
  Ziel `file` → `buildEmoteListEnvelope` (Set-ID aus `activeEmoteSetId()`, `scope` aus der Wahl) +
  `downloadFile(emoteListFilename(...), emoteListJson(...), JSON_MIME)`, fertig; Ziel `channel` →
  `startImportFlow(deps, source, target.channelName)`.
- `dockVisible()`: zusätzlich `importService.isRunning() || importService.queue().length > 0`.
- Dock (`.html:697-751`): nach `<app-mass-delete-panel>` die Sektion `<app-import-progress-section />`
  — sie rendert sich selbst nur bei `importService.isRunning() || queue().length > 0`.
- `import-progress-section.ts` (`app-import-progress-section`, keine Inputs — liest den Root-Service):
  Zeile `import.summary.target` (immer, R9), `<app-run-progress-panel labelPrefix="import" …>` mit
  `cancelled → cancel()`, `dismissed → reset()`, `syncRetryRequested → retrySyncReport()`;
  in `run-actions`: `abortedForPrivileges()` → `NoticeBanner warning` `insufficientPrivileges`;
  Resync-Hinweis nach dem `resyncNoticeKey`-Muster (`mass-delete-panel.ts:177-190`) mit
  `import.resync.*`; Link `import.summary.openTarget` als `<a appButton="outline" [routerLink]="['/channels', target, 'usage-stats']">`
  (Muster `back-link.ts`). Fehlertext je Zeile liefert das Panel schon (`failedItems`).

**Grenzfälle.** Beide Scope-Zeilenlisten, die Set-ID und der Kanalname werden **vor** dem Öffnen des
Ziel-Pickers erfasst und in der Fortsetzung nur noch von dort gelesen (`CapturedImportScope`) — die
Seite lädt während des offenen Dialogs auf `usageFlushed`/`channel.synced` nach, und die Auswahl
überlebt das Nachladen, sodass die Zahlen im Dialog sonst still von den kopierten Zeilen abweichen
könnten. Auf `coarse` verschwindet der Button samt Dock — der Lauf läuft weiter
(Design). Ein Import-Lauf auf der Seite eines **anderen** Kanals zeigt die Sektion mit dem Ziel (R9).

**Tests.** Keine neuen Specs (Seite und Sektion sind Komponenten). Laufen: `npx vitest run src/app/features/usage-stats`
(Bestand), Lint, `npx ng build --configuration development`.

**DoD.** Build fehlerfrei; der Export-Button ist unverändert; `grep -n "import.copyButton" usage-stats-page.html` trifft genau einmal.

### T7 — Restore-Panel: Dispatch nach `kind` (nach T5, ∥ T6) — `sonnet`

**Ziel.** Das Panel nimmt drei Sorten an und führt zwei davon in den Import-Flow (Ziel = aktueller Kanal).

**Betroffene Dateien.** `web/src/app/shared/seven-tv/restore-panel.ts`, `docs/DECISIONS.md` (Absatz).

**Vertrag.** `onFileSelected`: `readEnvelope(text)` → Fehler ⇒ Banner wie heute; `kind === 'purge-run'`
⇒ **unveränderter** Bestand (`parsePurgeRunProtocol(text, …)` darf den Text ein zweites Mal lesen —
oder bekommt eine Überladung, die die Envelope nimmt; frei, Verhalten und alle Bestands-`errorKey`s
bleiben); sonst `parseImportSource(envelope, file.name)` → Fehler ⇒ Banner; ok ⇒
`startImportFlow(deps, source, channelName())`. Der Panel-Trigger bleibt `disabled` bei
`arbiter.activeRun() !== null`. Der Klassenkommentar (`:16-22`) wird auf die drei Sorten erweitert.
Token-Prompt für den Restore-Zweig bleibt **vor** der Bestätigung (Bestand), für den Import-Zweig
macht ihn der Flow **nach** der Bestätigung (R2) — beides in einer Datei, beides kommentiert.

**Grenzfälle.** `usage`-Export desselben Kanals → Flow zeigt `sameChannelFile` und `alreadyPresent`
für alles (AK 7). Purge-Protokoll eines fremden Sets → weiterhin `wrongSet` (AK 6). Datei-Input wird
in jedem Fall geleert (`:82`).

**Tests.** Kein Komponententest; E2E in T9 (Fälle 2, 3). Laufen: `npx vitest run src/app/shared/export`
(Parser-Bestand) + Lint + Build.

**Doku.** DECISIONS-Absatz: Annahmebedingung des Restore-Panels (`kind`-Dispatch; Kanal-/Set-Prüfung
bleibt **nur** beim Purge-Protokoll), Push-vor-Pull als Produktentscheidung (Verweis Design).

**DoD.** Build; `git diff` an `restore-panel.ts` enthält keine Änderung an der Restore-Kette außer
dem Einstieg über `readEnvelope`.

### T8 — Route-Leave-Guard (∥ T5, nach T1) — `sonnet`

**Ziel.** Wer die Usage-Seite während eines Import-Laufs verlässt, wird gefragt.

**Betroffene Dateien.** Neu: `web/src/app/features/usage-stats/usage-stats-leave.guard.ts` (+ spec).
Geändert: `web/src/app/app.routes.ts` (`canDeactivate` an der `usage-stats`-Route).

**Vertrag.** R11. Export `usageStatsLeaveGuard: CanDeactivateFn<unknown>`; injiziert
`SevenTvImportService`, `Dialog`, `TranslocoService`.

**Grenzfälle.** Lauf gerade beendet (`isRunning false`, Queue noch voll) → kein Dialog. Dialog per
Escape geschlossen → `false` (bleibt). Der Guard darf **keinen** Zustand am Service ändern.

**Tests (vor der Umsetzung).** `usage-stats-leave.guard.spec.ts` mit `TestBed.runInInjectionContext`
(Muster `usage-stats-access.guard.spec.ts`), `SevenTvImportService` per `useValue`-Stub mit
`isRunning`-Signal, `Dialog` per Spy: (1) nicht laufend → `true` ohne Dialog; (2) laufend, Dialog
schließt mit `true` → Observable emittiert `true`; (3) laufend, Dialog schließt mit `undefined` →
`false`. Laufen: `npx vitest run src/app/features/usage-stats/usage-stats-leave.guard.spec.ts`.

**DoD.** Spec grün (+3); Lint; Format; keine Änderung an anderen Routen.

### T9 — E2E und Audit-Harness (nach T6, T7, T8) — `sonnet`

**Ziel.** Die Flows im Browser, `/api/**` und `7tv.io` gemockt.

**Betroffene Dateien.** Neu: `web/e2e/emote-import.e2e.spec.ts`. Geändert: `web/e2e/support/mocks.ts`
(neue Helfer `mockEmoteList(page, channel, emotes)`, `mockSetWarning(page, channel, warning)`,
`mockSyncImported(page, channel)`, `mockSevenTvGql(page, handler)`), `web/e2e/audit/ui-audit.audit.ts`
(zwei Szenarien). Nicht: bestehende Spezifikationen.

**Vorbedingung.** Auf `:5151` lauscht **keine** Api (sonst rund die halbe Suite rot, s. `CLAUDE.md`).

**Fälle (Muster `usage-atlas.e2e.spec.ts`, `openAtlas`):**
1. **Push-Flow bis Dialog (AK 1–4 statisch):** `listMine` mit `sensitron` (aktuell, Broadcaster),
   `aatrociity` (Editor, getrackt), `untrackedbuddy` (Editor, ungetrackt), `modonly` (nur Mod).
   Zwei Zellen markieren → Button → Picker: genau `aatrociity` (aktiv), `untrackedbuddy` (disabled,
   Hinweistext), Datei-Eintrag; `modonly` und `sensitron` fehlen; Scope-Radio auf „Auswahl (2)"
   vorbelegt. `aatrociity` wählen → Weiter → Dialog: Zielset-Mocks so, dass eine der zwei IDs schon
   drin ist und die andere einen Namenskonflikt hat → Zeilen `alreadyPresent 1`, `nameCollisions 1`
   mit Namen, Projektion „4 von 1000" (occupied 3 + 1), Titel „1 Emote nach aatrociity kopieren?".
2. **Datei-Weg (AK 5–7):** `emote-list`-Datei (Buffer, `setInputFiles` wie `ui-audit.audit.ts:663-704`)
   aus `sensitron` im Kanal `sensitron` hochladen → Dialog mit `sameChannelFile` und `nothingToAdd`
   (alle IDs im Ziel); dann eine `usage`-Datei ohne `exportedAt` → Dialog mit „Datum unbekannt"; dann
   eine dritte `emote-list`-Datei mit `meta.rowCount 5` und zwei kaputten Zeilen (eine ohne
   `sevenTvEmoteId`, eine doppelte gültige ID) → Dialog zeigt `discardedRows 2` **vor**
   `duplicatesCollapsed 1`, Ausführen bleibt aktiv.
3. **Ablehnung:** `voting`-Datei → `role="alert"` mit dem `votingExport`-Text; fremdes Purge-Protokoll
   → `wrongSet`-Text (Regressionsschutz für den Restore-Zweig).
4. **Lauf, Abbruch durch Privilegien, Leave-Guard (AK 8, 12):** Token per `addInitScript` in
   `sessionStorage`; `7tv.io/v3/gql` antwortet auf die erste Mutation mit `errors[0].message: 'insufficient privileges'`
   → Dock zeigt `insufficientPrivileges`, Zusammenfassung „0 kopiert · 1 fehlgeschlagen · 1 abgebrochen",
   kein `sync-imported`-Request (Mock zählt). Zweiter Lauf im selben Test mit erfolgreicher Mutation,
   `page.clock` für `RUN_DELAY_MS`: während des Laufs auf „Abstimmungen"-Tab klicken → Bestätigungs-
   dialog, Abbrechen → URL unverändert; Bestätigen → Voting-Seite; zurück → Dock zeigt Zusammenfassung
   und den Link zum Ziel. **Zu klären in diesem Fall (R11):** feuert der Guard beim Kanalwechsel? Wenn
   nein, Befund im Bericht, kein Umbau.

**Audit-Harness:** Szenarien `usage-stats-import-target-dialog` (afterLoad: Button klicken, Dialog
abwarten) und `usage-stats-import-confirm-dialog` (afterLoad: Ziel wählen, Weiter, `#app-dialog-title`)
nach dem Muster `usage-stats-export-dialog` (`:759-776`); Locale-unabhängige Handles (Rolle + Regex
über beide Sprachen). Einmal laufen lassen (`npx playwright test --config=playwright.audit.config.ts`,
vorher `.audit-out/` leeren), Gates aus §12 prüfen: `horizontalOverflowPx 0`, keine neuen
`smallTargetsUnder24`, `contrastViolations` leer — in beiden Locales, Screenshots sichten.

**DoD.** `npm --prefix web run e2e` grün (103 + 4); Audit-Metriken ohne neue Befunde; Laufzeit der
E2E-Suite < 2 min (sonst Speicherdruck, Suite allein wiederholen).

### T10 — Dokumentation (nach T9) — `sonnet`

**Betroffene Dateien.** `docs/UI-Designsprache.md` (§7: neuer Unterabschnitt „Ziel-Picker und
Import-Dialog" — Reihenfolge der Dialogzeilen als Vertrag, Token-Prompt-Reihenfolge und ihre
Abweichung, Skeleton im Dialog, gesperrter Ausführen-Knopf mit Grundtext; §4.2-Satz um den
Header-Button ergänzen), `docs/Feature-Ideen-2026-08-01.md` (Tabellenzeile A16 → `✅ 2026-09-05`,
DECISIONS-Titel; **Statuszeile unter der A16-Überschrift anlegen**, es gibt keine), `docs/DECISIONS.md`
(Eintrag gegenlesen: `**Betrifft:**`-Zeile vollständig, alle Absätze aus T1/T2/T5/T7 vorhanden,
Datumsordnung stimmt), `CLAUDE.md` Umsetzungsstand-Tabelle **nicht** (kein Modul-Wechsel).

**DoD.** `grep -n "A16" docs/Feature-Ideen-2026-08-01.md` zeigt Tabellenzeile und Statuszeile;
`grep import-confirm-dialog docs/DECISIONS.md docs/UI-Designsprache.md` trifft beide.

### T11 — Live-Probe (Regel 16, AK 1–3, 8–10) — Betreiber, nicht delegierbar

**Vorbereitung durch den Orchestrator.**
- Postgres/Redis laufen bereits (`emotepurge-dev-postgres`, `emotepurge-dev-redis`, geteilt mit #69):
  **nicht** neu starten, **nie** `docker compose down`, `docker compose` nie ohne Service-Namen.
- Api aus dem Worktree: `dotnet run --project src/EmotePurge.Api` (Port `5151` gehört dieser Sitzung;
  User-Secrets hängen an der `UserSecretsId` des Projekts und gelten auch im Worktree — fehlende
  `ClientId` zeigt sich als `unexpected_error` beim Login).
- Frontend: `npm --prefix web start` (`:4200`, Proxy nach `:5151`).
- **Worker nicht starten** — er gehört der #69-Sitzung. Läuft ihrer gegen dieselbe Redis, verarbeitet
  er den Resync des Zielkanals; läuft keiner, bleibt der Zielkanal bis zum nächsten Worker-Lauf ohne
  die neuen Zeilen — die Probe ist trotzdem gültig (7TV-Schreiben, Audit-Eintrag, Dialoginhalte sind
  unabhängig davon), nur AK 2 („bereits im Zielset") braucht den vollzogenen Resync.
- Login mit dem Twitch-Konto, das in `brudivoeller_tv` (Quelle, 654 aktive Emotes) lesen und in
  `aatrociity` (Ziel) als Broadcaster/7TV-Editor schreiben darf; 7TV-Schreib-Token aus dem Local
  Storage von 7tv.app (Anleitung im Token-Prompt) — mit Editor-Recht am Zielset. Für AK 8 zusätzlich
  ein Token **ohne** Editor-Recht am Ziel (zweites 7TV-Konto oder Editor-Recht temporär entziehen).
- Für AK 3 vorher auf 7tv.app im Zielset ein Emote unter einem Namen anlegen, den eines der fünf
  Quell-Emotes trägt (andere 7TV-ID).

**Checkliste (Ergebnis je Zeile in den PR-Text).**
1. Quelle `brudivoeller_tv`, 5 Emotes markiert → „In Kanal kopieren…" → Picker zeigt `aatrociity`,
   nicht `brudivoeller_tv` → Dialog: Herkunft, Ziel + Set-ID, Projektion, `alreadyPresent 0`,
   `nameCollisions 1` (das vorbereitete) → Token-Prompt → Lauf: 4 × `done`, 1 × `failed` mit 7TV-Text
   „conflicting name" (AK 3) → `sync-imported` `204` → Admin-Audit-Log: Zeile „Emotes importiert" mit
   „4 Emotes aus brudivoeller_tv" → Resync-Hinweis (`succeeded` oder `cooldown`, AK 10 sobald zweimal
   hintereinander) → Link öffnet `/channels/aatrociity/usage-stats`.
2. Nach vollzogenem Resync: gleicher Lauf erneut → Dialog „alle 4 bereits im Zielset" (plus das
   kollidierende als Kollision) → Ausführen gesperrt bei leerer Restliste (AK 2; mit dem
   Kollisions-Emote ist die Restliste 1 — dann eine Auswahl ohne dieses Emote nehmen).
3. AK 8: Token ohne Editor-Recht → erste Zeile `failed`, Rest `cancelled`, Meldung
   `insufficientPrivileges`; **den echten 7TV-Fehlertext notieren** (R13).
4. AK 9: Lauf mit ≥ 10 Emotes, nach 3 abbrechen → Rest `cancelled`, `sync-imported` mit genau 3 IDs.
5. AK 11: DevTools-Emulation `pointer: coarse` → kein Button, kein Dock.
6. AK 12: während des Laufs auf einen anderen Kanal → Frage → zurück → Dock mit Lauf/Zusammenfassung.
7. Datei-Weg einmal echt: „… als Datei speichern" aus `brudivoeller_tv` → Datei in `aatrociity`
   hochladen → derselbe Dialog; `emotepurge_brudivoeller_tv_emote-list_<datum>.json`, `rows` ohne `emoteId`.
8. Api beenden, **bevor** E2E läuft.
9. **Nachlauf-Race Delete (T12):** In den DevTools `POST /api/channels/*/emotes/sync-deleted`
   verzögern (Request-Blocking mit Delay oder Throttling), zwei Emotes in Kanal A löschen, während
   der Call fliegt „Schließen" drücken bzw. nach Kanal B wechseln, dort einen zweiten Lauf starten.
   **Erwartung:** kein Report von Lauf 1 auf Lauf 2; Lauf 2 meldet normal; die Zeilen verschwinden
   nach Lauf 2 wie gewohnt (prüft die zweite Gefahrenstelle aus T12); das Audit-Log zeigt beide Läufe
   korrekt.
10. **Nachlauf-Race Restore (T12):** dasselbe, zusätzlich mit `POST .../resync`.

### T12 — Nachlauf-Zustand von Delete/Restore ans Laufobjekt binden (Nachzug, eigener Abschlusscommit) — `sonnet`

**Ziel.** Dieselbe Nachlauf-Semantik wie in T1/2.6 (R15) auch für die beiden bereits ausgelieferten
Services herstellen: `SevenTvDeleteService` und `SevenTvRestoreService` binden ihren Zustand nach dem
Lauf an den jeweiligen Laufdatensatz statt an lose Felder daneben, damit die späte Antwort eines
Vorgängerlaufs nicht in den Zustand eines inzwischen gestarteten zweiten Laufs schreibt. Heute ist der
Fehler folgenlos (R15) — bewusst trotzdem nachgezogen, weil alle drei Nachläufe zum Review-Zeitpunkt
dieselbe Form tragen sollen und der DECISIONS-Absatz eine Regel statt einer Ausnahme beschreibt.

**Betroffene Dateien.** Geändert: `web/src/app/core/seven-tv/seven-tv-delete.service.ts`
(+ `.spec.ts`), `web/src/app/core/seven-tv/seven-tv-restore.service.ts` (+ `.spec.ts`),
`docs/DECISIONS.md` (Absatz, s. u.). Nicht anfassen: `mass-delete-panel.ts`, `restore-panel.ts`,
`run-progress-panel.ts`, `usage-stats-page.ts`, `channel-workspace-layout.ts`,
`seven-tv-run-arbiter.ts` — keiner der sechs Konsumenten wird geändert; das ist Abnahmekriterium.

**Vertrag.** Analog 2.6/R15: Callbacks von `syncDeleted`/`syncRestored` und (nur Restore) `resync`
prüfen vor jedem Schreiben in `syncReport`/`resyncTrigger`, ob ihr beim Start festgehaltener
Laufdatensatz noch der aktuelle ist; sonst wird die Antwort kommentarlos verworfen, ohne
Fehlerzustand zu setzen. `retrySyncReport()` liest IDs **und** Kanal aus demselben Datensatz, nicht
aus `currentChannelName`/`lastReportedIds` daneben. Öffentliche Oberfläche (Signale, Methodennamen,
`lastRun`-Form `{ setId, channelName, result }`) bleibt unverändert — Abnahmekriterium.

**Keine gemeinsame Basisklasse** (bewusst, damit niemand „aufräumt"): die Engine ist bereits die
geteilte Basis; geteilt wären nur rund 15 Zeilen, während Endpunkt, Ergebnistyp und Zustandssignale je
Service verschieden sind und der Import-Fall (T1) mit `sourceKind`/Keys wieder anders liegt. Zweimal
dieselbe kleine Änderung ist ehrlicher als eine Abstraktion über drei ungleiche Nachläufe.

**Grenzfälle.**
- Späte Antwort, nachdem der Nutzer „Schließen" gedrückt hat oder per `resetIfChannelChanged` in
  einen anderen Kanal gewechselt ist: heute schreibt sie noch in `syncReport`, künftig wird sie
  verworfen — gewollte Richtungsänderung, aber kein Bestandstest deckt sie bisher ab.
- `mass-delete-panel.ts:199-227`: der Effect dort emittiert `deleted`/`reloadRequested` erst auf
  einer terminalen `syncReport`-Flanke. Unterdrückt der Wächter fälschlich einen Übergang,
  verschwindet still das optimistische Entfernen der Zeilen — bei der Umsetzung gegenprüfen, dass
  jede tatsächlich zum aktuellen Lauf gehörende Antwort weiterhin genau eine terminale Flanke erzeugt.

**Tests.** Mindestens 3 neue Fälle, **null** Bestandstests angefasst (Abnahmekriterium — fasst der
Implementierer einen Bestandsfall an, hat er die öffentliche Oberfläche verändert; keine Spec greift
auf private Felder zu, kein `as any`, kein Index-Zugriff):
1. `seven-tv-delete.service.spec.ts` (heute 30 Fälle): späte Antwort eines Vorgängerlaufs wird
   verworfen, der Zustand des inzwischen laufenden/schon abgeschlossenen zweiten Laufs bleibt
   unberührt.
2. `seven-tv-restore.service.spec.ts` (heute 13 Fälle): derselbe Fall für `syncReport`.
3. `seven-tv-restore.service.spec.ts`: derselbe Fall zusätzlich für `resyncTrigger`.
Laufen: `npx vitest run src/app/core/seven-tv/seven-tv-delete.service.spec.ts src/app/core/seven-tv/seven-tv-restore.service.spec.ts`.

**Doku.** Eigener `docs/DECISIONS.md`-Absatz, der die Regel für **alle drei** Nachläufe (Delete,
Restore, Import) beschreibt; `**Betrifft:**`-Zeile nennt beide Bestandsdateien plus einen Satz, warum
rückwirkend nachgezogen wird (dieselbe Form zum Review-Zeitpunkt, Regel statt Ausnahme). **Hinweis:**
`docs/DECISIONS.md` ist ein erwartbarer Merge-Konflikt mit einer parallelen Sitzung (#69) — vor dem
Schreiben `head -20 docs/DECISIONS.md` lesen, Absatz klein und additiv, absteigende Datumsordnung
einhalten.

**Commit.** Eigener, letzter Commit des Branches, separat revertierbar:
`fix(web): bind the post-run report state to its run`, DECISIONS-Absatz im selben Commit (Regel 3).

**Modell.** `sonnet`. **Aufwand.** 1,5–2,5 h. **Vorbedingung.** nach T1 (die Semantik entsteht dort
zuerst), sinnvollerweise ganz am Ende, nach T10.

**DoD.** Beide Specs grün (+3, −0 Bestandsfälle); Lint; Format; `git diff` an den beiden
Service-Dateien ändert keine öffentliche Methode/Signal-Signatur; keiner der sechs genannten
Konsumenten im Diff.

---

## 5. Gates und Ressourcen

**Ressourcen (gilt für jeden Task):** Postgres/Redis (`emotepurge-dev-postgres`, `emotepurge-dev-redis`)
werden mit der #69-Sitzung geteilt — nie herunterfahren, `docker compose` nie ohne Service-Namen.
Der Worker gehört der anderen Sitzung — nie selbst starten. Port `:5151` und die E2E-Suite gehören
dieser Sitzung; vor `npm run e2e` sicherstellen, dass dort keine Api lauscht.

**Gates am Ende, in dieser Reihenfolge:**

1. `npm --prefix web test -- --watch=false` — Baseline **635**; erwartet **≥ 678** (T0 +2, T1 +9,
   T2 +13, T3 +10, T4 +3, T8 +3, T12 +3; `purge-run-export.spec.ts` behält seine Fallzahl).
2. `npm --prefix web run lint`, `npm --prefix web run format:check`.
3. `npm --prefix web run e2e` — Baseline **103**, erwartet **107**; nur ohne Api auf `:5151`; Laufzeit
   > 2 min = Speicherdruck, Suite allein wiederholen, nicht debuggen.
4. `dotnet test EmotePurge.slnx` — **763**, unverändert (kein Backend-File berührt; `git diff --stat -- src tests` leer). Braucht laufendes Docker — die geteilten Dev-Container reichen nicht, Testcontainers startet eigene.
5. Audit-Harness (T9) ohne neue Befunde.
6. Live-Probe (T11) vor dem Commit der Seiten-Änderung — keine Suite ersetzt sie (Regel 16).
7. `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` vor dem Merge; Fokus:
   Loader-Präzedenz (R8), Race zwischen `runBlocked` und Start (R1), Dedup-Vertrag (R6), Datei-Weg
   sendet `null` (R3). Ohne `--scope` reviewt Codex den Working Tree und entwarnt falsch.

**Commits** (Conventional Commits, je Task, DECISIONS-Absatz im Commit des Tasks — Regel 3; vor jedem
Commit den Nutzer fragen — Regel 1):
`feat(web): add the import source contract and its i18n keys` (T0) ·
`feat(web): add the 7TV import run service` (T1) ·
`feat(web): read emote lists and usage exports as import sources` (T2) ·
`feat(web): load the import target and preview what would change` (T3) ·
`feat(web): add the import target picker` (T4) ·
`feat(web): add the import confirm dialog and the import flow` (T5) ·
`feat(web): copy emotes to another channel from the usage page` (T6) ·
`feat(web): accept emote lists and usage exports in the restore panel` (T7) ·
`feat(web): ask before leaving the usage page during an import run` (T8) ·
`test(web): cover the import flows end to end` (T9) ·
`docs: record the import UI in the design language, backlog and decision log` (T10).

---

## 6. Entscheidungen im Plan, nicht in der Issue (für den Nutzer)

1. **Header-Button gesperrt während jedes 7TV-Laufs** (R1) — auch Picker und Datei-Export sind dann
   nicht erreichbar; dafür eine Regel für alle Start-Stellen.
2. **Token-Prompt nach der Bestätigung, nicht davor** (R2) — abweichend von Delete/Restore, weil die
   Vorschau hier der Ort der Entscheidung ist. Delete/Restore bleiben.
3. **Scope-Vorbelegung `selection`** bei vorhandener Auswahl (R12) — der Export-Dialog belegt `visible` vor.
4. **`ImportSource` liegt in `core/`**, nicht in `shared/` (R5) — Schichtentreue.
5. **Loader statt `forkJoin` mit Abbruch** (R8): drei getaggte Ergebnisse, `no-set` schlägt `failed`.
6. **Leave-Guard nutzt `ConfirmDialog` mit rotem Bestätigen-Knopf** (R11) — bewusst kein vierter Dialog.
7. **Kein Kanal-Reset für den Import-Service; Ziel-Zeile in der Sektion** (R9) — die Sektion steht
   außerhalb des Set-Gates, damit sie auch auf einer Seite ohne aktives Set sichtbar bleibt; zwei
   bekannte Sichtbarkeitsgrenzen bleiben (Voting-Seite, „0 markiert").
8. **Datei-Import meldet `sourceChannelName: null`** (R3) — K2-Vertrag; der Audit-Eintrag nennt bei
   Dateien keinen Herkunftskanal.
9. **Dateiname mit Datum statt Minute** (R4).
10. **Parallel laufende Subagents fassen Locales und DECISIONS nicht gleichzeitig an** (R10) — T2
    liefert seinen DECISIONS-Absatz als Text.
11. **Nachlauf-Antworten ans Laufobjekt gebunden, nicht an lose Felder** (R15) — verhindert, dass ein
    zweiter Import die Rückmeldung eines noch fliegenden ersten übernimmt; Delete/Restore haben
    denselben theoretischen Fehler und bleiben folgenlos, werden aber dennoch mitgezogen (T12,
    Punkt 14).
12. **Design (`:256`) vs. Issue widersprechen sich beim Import-Protokoll als Datei** (R16) — die
    Issue gewinnt, kein Datei-Export des Protokolls in #72.
13. **`discardedRows` ist von `duplicatesCollapsed` zu unterscheiden und sperrt den Lauf nicht** —
    echter Datenverlust (ungültige Zeile) vs. bloße Konsolidierung (Duplikat); die Hinweiszeile steht
    deshalb vor `duplicatesCollapsed`.
14. **Die beiden ausgelieferten Pfade `SevenTvDeleteService`/`SevenTvRestoreService` werden bewusst
    im selben Branch nachgezogen statt per eigenem Issue** (T12) — Grund: zum Zeitpunkt dieses Reviews
    sollen alle drei Nachläufe (Delete, Restore, Import) dieselbe Form tragen, und der
    `DECISIONS.md`-Absatz beschreibt damit eine Regel statt einer nachträglichen Ausnahme; eingekaufter
    Preis: eine zweite Handprobe an bereits ausgelieferten Pfaden im selben Merge (T11, Punkte 9/10).
