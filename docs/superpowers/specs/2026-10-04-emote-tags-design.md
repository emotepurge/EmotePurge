# Emote-Tags: kanalgebundene Tags, die ein Set zeitweise erweitern und wieder aufräumen — Spec

**Datum:** 2026-10-04 · **Status:** Erste Fassung nach dem Brainstorming mit dem Betreiber vom 2026-10-04 — die Entscheidungen E1–E25 sind getroffen und werden hier nicht neu aufgerollt; was der Code dagegen stellt, steht in Abschnitt 13 · **Issue:** #201 (ersetzt die Lesart „Liste als Auswahl", s. [Konzept-Liste-als-Auswahl-2026-10-03.md](../../Konzept-Liste-als-Auswahl-2026-10-03.md)) · **Epic:** #200 (Basis) · **Berührt:** #245 (Selbstbereinigung), #243/#244 (Aufbewahrung), #304 (Set-Lesung) · **Belegt gegen:** `feat/emote-sets-200` @ `2db77d26` (Hauptworktree `/home/dev/projects/EmotePurge`); der Branch dieser Spec, `feat/201-list-as-selection`, steht auf `767b1e3e` und wird vor der Umsetzung auf den Epic-Stand gezogen.

Diese Spec ist ein Denkwerkzeug des Betreibers und deshalb deutsch; Bezeichner, Routen und
Wire-Felder bleiben englisch. Sie enthält keinen fertigen Code — Verträge, Verhalten, Grenzfälle,
Fallen und Testpflichten. Zeilenangaben sind am 2026-10-04 gegen den oben genannten Stand
nachgeprüft (Pfade ohne Präfix liegen unter `web/src/app/`; C#-Pfade unter `src/`). Kleine,
eindeutige Präzisierungen, die das Brainstorming offen ließ, sind als **Festlegung** markiert und
einzeln kippbar. Stellen, an denen der Code einer Brainstorming-Entscheidung widerspricht, sind
**nicht** still umgebaut, sondern stehen in Abschnitt 13 mit Beleg.

---

## 0. Auftrag in einem Satz

Ein Kanal bekommt **Tags** (z. B. „Stronghold"), denen Manager im Nutzungsraster Emotes zuweisen;
ein Tag lässt sich mit einem Klick **einspielen** (alle Einträge, die noch nicht im aktiven Set sind,
werden über den vorhandenen Import-Lauf hinzugefügt) und später **ausräumen** (nur das, was dieses
Tag tatsächlich hinzugefügt hat, wird über den vorhandenen Lösch-Lauf entfernt) — gespeichert in
EmotePurge, kanalgebunden, für alle Manager des Kanals sichtbar, ohne Datei auf einem Mod-PC und
ohne zweites 7TV-Set.

---

## 1. Ausgangslage und Wunsch

HandOfBloods Stream hat spielbezogene Emotes (Beispiel: während „Stronghold" gespielt wird). Das
Mod-Team will diese Emotes **nur während des Streams** im 7TV-Set haben und danach wieder entfernen.
Heute geschieht beides von Hand: vor dem Stream werden die Emotes einzeln gesucht und hinzugefügt,
danach einzeln gesucht und gelöscht — jedes Mal von vorn. Der Wunsch (über den Betreiber, 2026-10-04):
Emotes einmal markieren („Stronghold"), vor dem Stream alle markierten auf einen Schlag hinzufügen,
danach alle auf einen Schlag entfernen.

Das frühere Konzept vom 2026-10-03 las #201 als „eine Datei als Auswahl laden". Das Mod-Team wollte
aber keine Datei, sondern etwas, das **im Kanal** liegt und auch einem Vertretungs-Mod zur Verfügung
steht. Das Konzept bleibt als Herleitung stehen; was davon trägt, wird hier wiederverwendet und
zitiert (Abschnitt 3.6).

## 2. Ziel und Nicht-Ziele

**Ziel (v1):**

- Tags je Kanal anlegen, umbenennen, löschen; Emotes aus der Rasterauswahl einem oder mehreren Tags
  zuweisen und wieder herausnehmen.
- Ein Tag **einspielen**: alle Einträge, die nicht im aktiven Set sind, in einem Import-Lauf
  hinzufügen; Einträge, die schon im Set sind, werden übersprungen und **nicht** als vom Tag
  hinzugefügt gezählt.
- Ein Tag **ausräumen**: nur die vom Tag eingespielten Emotes, die noch im Set sind, in einem
  Lösch-Lauf entfernen; alles andere wird gezeigt, aber nicht vorgeschlagen.
- Zwei Oberflächen: ein Tag-Filter in der Filterzeile des Nutzungsrasters **und** eine eigene
  Tags-Seite je Kanal.
- Sauberer Lebenszyklus mit dem Rest des Systems: Kanal-Purge (Admin, Aufbewahrung, später #245)
  nimmt die Tags mit; Kontolöschung (#243) ist nicht berührt, weil Tags keine Personendaten tragen.

**Nicht-Ziele (v1, ausdrücklich):**

- Persönliche, kontogebundene Tags (späterer Schritt; die Datenform lässt ihn offen, s. 5.5).
- Tag-Export/-Import als Datei (die Kanal-übergreifende Brücke).
- Emotes taggen, die gerade **nicht** im aktiven Set sind — erst einspielen, dann taggen (E3).
- Tag-Chips auf den Rasterzellen.
- Live-Aktualisierung über SSE, wenn ein anderer Mod Tags ändert — die Seite lädt nach eigenen
  Aktionen neu, fremde Änderungen erscheinen beim nächsten Laden (Festlegung, s. 9.6).
- Eine Änderung am Worker (Abschnitt 12.3 belegt, dass keine nötig ist).

## 3. Ist-Zustand — was am Code steht

Alles Folgende ist am Branch nachgeprüft; es ist die Begründung für die Verträge in Abschnitt 5 bis 7.

### 3.1 Emote-Zeilen, Archivierung, „im aktiven Set"

- `Emote` (`EmotePurge.Core/Entities/Emote.cs`) trägt `Id` (interner Guid, PK), `SevenTvEmoteId`
  (7TV-ObjectID, **nicht** PK — Regel 8), `ChannelId`, `Name`, `ImageUrl`, `IsArchived`,
  `ArchivedAt`, `FirstSeenAt`, `LastSyncedAt`. Es gibt **keine** Set-Spalte auf `Emote`: eine Zeile
  ist kanalgebunden, Set-Zugehörigkeit steckt nur in `UsageStat.EmoteSetId`
  (`EmotePurge.Infrastructure/Persistence/AppDbContext.cs:39-56`).
- Unique-Index `(ChannelId, SevenTvEmoteId)` und FK auf `Channel` mit `OnDelete(Cascade)`:
  `AppDbContext.cs:27-37`.
- **„Im aktiven Set" heißt am Code: eine unarchivierte Emote-Zeile des Kanals.** Die Rasterabfrage
  des aktiven Sets filtert `.Where(e => isActiveSet ? !e.IsArchived : aggregates.ContainsKey(e.Id))`
  (`EmotePurge.Infrastructure/Services/UsageStatQueryService.cs:115`, Methode `GetUsageContextAsync`),
  füllt also jede unarchivierte Zeile mit Nullen auf und lässt archivierte weg — die Behauptung aus
  dem Brainstorming („Archivierte sind auf Query-Ebene ausgeschlossen") trifft zu.
- Archiviert und entarchiviert wird ausschließlich in `SevenTvSyncService` (`ReconcileAsync`,
  `UpsertEmote`, Delta-Pfad) und — seit #253 — in `EmoteService.MarkInSetAsync` für die
  set-zentrischen Meldungen. **Emote-Zeilen werden nirgends gelöscht** außer über die Kanal-Kaskade
  (grep über `src/` ohne Migrationen: kein `Emotes.Remove`/`ExecuteDelete`).
- Das aktive Set eines Kanals ist `Channel.ActiveEmoteSetId` (`EmotePurge.Core/Entities/Channel.cs`,
  `string`, leer = kein Sync bisher), daneben `ActiveEmoteSetCapacity`. Welche Sets der Kanal wann
  aktiv hatte, hält `ChannelEmoteSetObservation` (FK auf `Channel`, Cascade,
  `AppDbContext.cs:74-93`). Ein Set kann in **mehreren** Kanälen aktiv sein (geteilte Sets —
  `MarkInSetAsync` schreibt deshalb je Kanal).

### 3.2 Kanal-Purge und Aufbewahrung

- Der Admin-Purge `DELETE /api/channels/{channelName}/purge` (`EmotePurge.Api/Endpoints/ChannelEndpoints.cs:292-317`,
  `GlobalAdminAuthorizationFilter`) ruft `ChannelService.PurgeAsync`
  (`EmotePurge.Infrastructure/Services/ChannelService.cs:127-155`): LEAVE publizieren,
  Audit-Eintrag mit **Snapshot-String** `ChannelName` (kein FK — „an FK would have cascaded this
  row away too"), `db.Channels.Remove(channel)`, ein `SaveChangesAsync`. **Die Kaskade ist reine
  FK-Kaskade** über `OnDelete(DeleteBehavior.Cascade)`, keine Liste expliziter Löschungen. Der
  Aufbewahrungs-Purge `PurgeIfInactiveSinceAsync` (`:158ff`) macht dasselbe unter Zeilensperre.
- Die **Zählung** je Tabelle existiert nur für den Trockenlauf der Aufbewahrung:
  `DataRetentionService.CountChannelCascadeAsync`
  (`EmotePurge.Infrastructure/Services/DataRetentionService.cs:396-412`) zählt Emotes, UsageStats,
  ChannelLiveDays, VoteSessions, Votes, ChannelEmoteSetObservations in das Struct `ChannelCascade`
  (`:462ff`) und von dort in `ChannelRetentionCounts`
  (`EmotePurge.Core/Services/IDataRetentionService.cs:117-128`). **Diese Zählwerte formatiert der
  Worker:** `EmotePurge.Worker/RetentionRunSummaryFormatter.cs:31-33` nennt jedes Feld einzeln.
  Eine neue Zähl-Spalte wäre also eine Worker-Änderung (Folge in 12.3).
- `RetentionPolicy` (`EmotePurge.Core/Services/RetentionPolicy.cs`) sind Codekonstanten, weil die
  Datenschutzerklärung sie zitiert; sie kennen Tokens, inaktive Konten, beendete Abstimmungen,
  Audit-Log und deaktivierte Kanäle — keine Kategorie „Nutzerinhalte".
- Die Kontolöschung `AccountDeletionService.DeleteAsync` (Infrastructure; Aufrufer `DELETE /api/auth/me`
  in `AuthEndpoints.cs:156ff` und der Aufbewahrungsdienst) löscht Votes des Kontos, pseudonymisiert
  Audit-Einträge (Actor, Target `user`, Owner-Details) und entfernt die `User`-Zeile. Sie kennt nur
  Tabellen mit `UserId`/Actor-Spalten. **Eine Tabelle ohne Ersteller-Spalte und ohne FK auf `User`
  liegt vollständig außerhalb dieses Pfads** — Beleg für E1.
- Die Datenschutzerklärung liegt **außerhalb des Repos** (`Legal:ContentPath`, read-only gemountet,
  `docs/Operations.md` „Legal pages").

### 3.3 Routen, Filter, Fehlercodes

- Kanalgebundene Gruppen heißen `/api/channels/{channelName}/…` — der Routenwert heißt
  **`channelName`**, nicht `name` (`UsageStatsEndpoints.cs:19`, `EmoteEndpoints.cs:25`,
  `VoteSessionEndpoints.cs:18`). Jede Gruppe beginnt mit `RequireAuthorization()`, dann
  `ChannelNameValidationFilter` (**zuerst**, 400 `invalid_channel_name` statt 403/404 —
  `EmotePurge.Api/Validation/ChannelNameValidationFilter.cs`), dann der Autorisierungsfilter, dann
  `RequireRateLimiting(...)`. Normalisiert wird **nicht** im Filter, sondern im Service bzw. in
  `ChannelQueries.LoadChannelAsync` über `ChannelName.Normalize`
  (`EmotePurge.Core/Entities/ChannelName.cs`: `Trim().ToLowerInvariant()`) — Regel 9.
- `UsageStatsAccessAuthorizationFilter` (`EmotePurge.Api/Auth/`): Admin, Broadcaster, Live-Mod
  **oder 7TV-Editor** (`CanViewUsageStatsAsync`); `ChannelManagementAuthorizationFilter`: ohne den
  Editor (`CanManageChannelAsync`). Beide antworten 400 bei ungültigem Namen, 401 ohne Twitch-Principal,
  **403 ohne Body** (`Results.Forbid()`). 404 kommt nie aus einem Filter, nur aus Handlern.
- Rate-Limit-Policies (`EmotePurge.Api/RateLimiting/RateLimitPolicyNames.cs`, Registrierung
  `Program.cs:172-252`): `InteractiveRead` für Lesen beim Navigieren, `Bookkeeping` für jeden
  Schreibzugriff, der nicht verloren gehen darf (sync-*, join, purge), `TrackedEmoteSetPreview`
  (#220) für die Set-Vorschau. Je Endpoint gilt genau eine Policy; eine Routen-Policy überschreibt die
  Gruppen-Policy.
- Fehler sind `{ errorCode = ApiErrorCodes.X }` ohne Prosa
  (`EmotePurge.Api/Validation/ApiErrorCodes.cs`, `internal static class`); 409 über
  `Results.Conflict(new { errorCode = … })` (`ChannelEndpoints.cs:180`), 404 mit Code über
  `Results.NotFound(new { errorCode = … })` (`EmoteEndpoints.cs:132`). Der Spiegel im Frontend ist
  `core/i18n/api-error.ts` (`KNOWN_API_ERROR_CODES`), die Texte unter `errors.api.<code>` in
  `web/public/i18n/de.json`/`en.json`; `api-error-locales.spec.ts` prüft Spiegel ↔ beide Locales, der
  Schritt C# → `api-error.ts` bleibt Disziplin (Regel 7).
- Bestehende Buchführung aus dem Browser: `POST /api/channels/{channelName}/emotes/sync-imported`
  (`EmoteEndpoints.cs:247ff`, Body `SyncImportedRequest(SevenTvEmoteIds, SourceChannelName,
  SourceKind, LeaderboardSort, TargetEmoteSetId)`, nur ein Audit-Eintrag, keine Zeilenänderung) und
  die set-zentrischen `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted|sync-restored|sync-imported`
  (`SevenTvEndpoints.cs:242-370`, Gruppe `EmoteSetIdValidationFilter` + `Bookkeeping`, Autorisierung
  über die 7TV-Besitzprüfung `IImportTargetOwnershipService`). Das Vokabular von `SourceKind` ist
  geschlossen: `"channel" | "file" | "seventv-channel" | "seventv-leaderboard"`
  (`EmoteEndpoints.ValidateSyncImportedVocabulary`, `:358`) — ein neuer Herkunftstyp braucht dort
  einen Eintrag.
- Regel 10 im Bestand: `AdminChannelQueryService.cs:57-76` („both aggregates group on the plain FK
  column of a single table and filter via a scalar ID list") und `UsageStatQueryService.cs:51-56,94-96`.

### 3.4 Die 7TV-Läufe im Frontend

- **Ein Arbiter für alle Läufe:** `SevenTvRunArbiter` (`core/seven-tv/seven-tv-run-arbiter.ts`),
  `SevenTvRunKind = 'delete' | 'restore' | 'import' | 'undo'`, abgeleitete Signale `activeRun`,
  `startPending`, `startLocked = activeRun() !== null || startPending()`. Jeder Start-Trigger bindet
  an `startLocked` (`usage-stats-page.ts:1725, 1869, 2677`; `import-trigger.ts:207-210`;
  `mass-delete-panel.ts`); jeder Flow prüft unmittelbar vor dem Start `activeRun() !== null` und
  meldet eine Verweigerung über `noteRefusedStart`. **Es gibt kein `tryAcquire`/`release`** und
  keinen Namen „Run-Lock"; „laufender Lauf" heißt hier `startLocked`.
- **Set-Lesung (#304):** `loadSevenTvSetEntries(httpClient, setId)`
  (`core/seven-tv/seven-tv-set-entries.ts:152`) liest das Set seitenweise direkt bei 7TV (ohne Token)
  und liefert `aliasesById`, `aliaslessIds`, `defaultNameById`, `animatedById`, `occupiedSlots` und
  **`complete`**. `complete` ist `false`, wenn Seitenzahl, `totalCount` oder Mitgliedschaft zwischen
  den Seiten nicht zusammenpassen oder die Verifikations-Zweitlesung abweicht — das ist die
  Verschiebungserkennung aus #304 (`docs/DECISIONS.md:13`, Eintrag 2026-10-03). Wer löscht, verlangt
  `complete` (`mass-delete-panel.ts`, `readLiveAliasesThenDelete`: „a list that only knows half must
  not delete"); wer nur warnt, nimmt `countIsUpperBound`.
- **Import-Lauf:** `startImportFlow(deps, source, target)` (`shared/seven-tv/import-flow.ts:321`)
  lädt das Ziel, zeigt den Bestätigungsdialog (`import-confirm-dialog.ts`, Vorlesung
  `import-preview.ts`, Konfliktschritt `import-conflict-resolution-step.ts` mit `RowDecision =
  skip | renameSource | replaceTarget | adoptSourceName`, `shared/seven-tv/conflict-resolution.ts:30`),
  holt ggf. das Token, filtert Duplikate live (`filterAlreadyPresent`,
  `shared/seven-tv/already-present-filter.ts:78`) und startet
  `SevenTvImportService.startImport(target, origin, plan, …)`
  (`core/seven-tv/seven-tv-import.service.ts:472`). Eingabe ist `ImportSource = { origin: ImportOrigin;
  rows: ImportRow[]; duplicatesCollapsed; discardedRows }` mit `ImportRow = { sevenTvEmoteId; name;
  imageUrl: string | null }` (`core/seven-tv/import-source.ts:9,127`). `ImportOrigin` ist eine
  **erschöpfende** Union (`:45-55`: `channel`, `seventv-channel`, `file`, `seventv-leaderboard`)
  mit mehreren `switch`-Verbrauchern (Memory „ImportOrigin-Union: Lücken — 6 Bruchstellen").
- **Kapazität:** `projectSlots(occupied, capacity, delta)` (`shared/seven-tv/slot-projection.ts:27`)
  liefert eine Projektion samt `overflow`; der Bestätigungsdialog **warnt** damit
  (`restore.capacityWarning`), **blockt aber nie** — ein ADD über die Kapazität scheitert erst bei
  7TV als `failed`-Zeile. Ein Alias, der im Set schon vergeben ist, wird in der Vorlesung als
  `NameCollisionRow` erkannt und im Konfliktschritt entschieden; ein erst beim Schreiben auftretender
  Konflikt kommt als GQL-409 zurück und endet als `import.errors.nameTakenNow`.
- **Ergebnis eines Laufs:** `RunResult { doneKeys; items; startedAt; finishedAt }`
  (`core/seven-tv/seven-tv-run-engine.ts:244`), Zeilenstatus `pending | in-progress | done | failed |
  cancelled | unknown` (`:76`). Der Import meldet nach dem **Settlement** (`ImportSettlement
  'settled'`, Zweitlesung bei `unknown`-Zeilen) die Schlüssel aller `done`-Zeilen, die ein Emote
  hinzugefügt haben (`importedKeys`, `seven-tv-import.service.ts:1084`), über `reportImported` (`:850`)
  an `EmoteAdminService.syncImported` bzw. `SevenTvEmoteSetService.reportImportedToSet`. Ein Abbruch
  (`cancel()`, Privilegienverlust) lässt die restlichen Zeilen `cancelled`; `doneKeys` enthält genau
  das bis dahin Gelungene.
- **Meldungen am Lauf:** Jeder Lauf ist ein Record mit `phase: running | settling | reporting |
  closed` (`core/seven-tv/seven-tv-run-lifecycle.ts`); `closed` erst, wenn **jede** Meldung des Laufs
  einen Endzustand hat (`SyncReportState = idle | pending | succeeded | partial | failed`,
  `core/seven-tv/sync-report-outcome.ts:19`). Zeitbudget je Versuch `REPORT_TIMEOUT_MS` (30 s), bis zu
  drei Versuche; der Dock bietet bei `failed`/`partial` einen Retry (`run-progress-panel.ts`). Der
  Undo-Lauf (#254) hat **zwei** Meldungen am selben Record — der Präzedenzfall für eine weitere.
- **Lösch-Lauf:** `SevenTvDeleteService.startDelete(setId, channelName, emotes: DeleteQueueEmote[],
  expectedChannelName, targetOwnerTwitchId)` (`core/seven-tv/seven-tv-delete.service.ts:131,188ff`),
  `DeleteQueueEmote = { emoteId?; sevenTvEmoteId; name; aliases? }`. Der Weg davor liegt heute **in
  der Komponente** `MassDeletePanel` (`shared/seven-tv/mass-delete-panel.ts`): `openConfirm()`
  (Host-Lock, `startLocked`, Token-Prompt) → `openConfirmDialog()` (Set-Prüfung
  `resolveEditableSet`, Set-Wechsel-Abbruch `massDelete.setChangedDuringConfirm`) →
  `openDeleteConfirmDialog` (`shared/seven-tv/delete-confirm-dialog.ts:231`, Daten
  `DeleteConfirmDialogData` `:22-50`: **Namenslisten als Signale, Warnung, Set-Name — keine
  Zeilen, keine Checkboxen**) → `readLiveAliasesThenDelete` (Live-Lesung, `complete` Pflicht) →
  `startDelete`. Anders als Import, Restore und Undo hat der Delete **keine** `*-flow.ts`-Datei.
- **Protokoll und Rückweg:** `buildPurgeRunProtocol` (`shared/export/purge-run-export.ts`,
  `PURGE_RUN_FORMAT_VERSION = 3`) nimmt jede Zeile des Laufs; angeboten wird es nur als Download aus
  dem Lauf-Panel (`MassDeletePanel.openProtocolExport`), nirgends gespeichert. „Wiederherstellen" am
  beendeten Lauf (`mass-delete-panel.ts:299-317`, `restore.button`) startet
  `SevenTvRestoreService.startRestore` über die `done`/`unknown`-Zeilen; später geht es über die
  Datei-Tür des Import-Dialogs (`FileImportStep` → `parsePurgeRunProtocol` → `startRestoreFlow`).
  Die Meldung des Delete ist `reportDeletedInSet(setId, { sevenTvEmoteIds: doneKeys,
  expectedChannelName, targetOwnerTwitchId })` (`seven-tv-emote-set.service.ts:371`).
- **Lauf-Oberflächen je Seite:** Die Nutzungsseite rendert den Delete im Dock über
  `app-mass-delete-panel` (`usage-stats-page.html:1133-1203`, `[selectedEmotes]="selectedForDelete()"`,
  `[readLiveAliasesFromActiveSet]="true"`) und darunter `app-import-progress-section`,
  `app-restore-progress-section`, `app-undo-progress-section`; die Vote-Detailseite dasselbe Panel
  mit `[readLiveAliasesFromSet]="true"` (`vote-session-detail-page.html:173-184`) plus
  `app-restore-progress-section` (`:193`). Lauf-Fortschritt, Protokoll und Restore-Knopf des Delete
  stecken **im** `MassDeletePanel`; der Restore wurde für #253 (T9) aus ihm in eine eigene
  `RestoreProgressSection` gezogen — der Präzedenzfall für eine Extraktion.

### 3.5 Nutzungsraster, Filterzeile, Dock, Routing

- Filterzeile: `usage-stats-page.html:165-330` — Zeile eins (Zeitraum, Set-Menü
  `app-emote-set-menu` nur bei `activeEmoteSetId()`, Sortierung), Zeile zwei (Nutzungsbereich,
  Namensfilter, „Beobachtete ausblenden", „Filter zurücksetzen" nur bei `usageFilter.isAnyActive()`,
  „alle markieren" nur bei `showMarkAll()`, dann die Zählzeile `role="status"` mit `ml-auto`, dann
  die beiden vergänglichen Meldungen als Paar aus dauerhaft gemounteter `sr-only`-Region und
  `aria-hidden`-Zwilling). Der Filterzustand ist `EmoteUsageFilter<T>`
  (`shared/emotes/emote-usage-filter.ts`: `min`, `max`, `nameQuery`, `isHideObservedActive`,
  `isAnyActive`, `apply(items)`, `reset()`).
- Dock: inline `@if (dockVisible() && !isCoarse()) { <div class="app-dock"> … }`
  (`usage-stats-page.html:1073ff`); die Markierungshälfte innerhalb `@if (selectedEmoteSetId())`,
  die Aktionen projiziert in den `[selection-actions]`-Slot des Panels (`:1161-1203`: „Übertragen",
  „Zur Abstimmung" nur `canManage()`, „Auswahl aufheben"; „Löschen (n)" ist der Knopf des Panels;
  Export sitzt im Seitenkopf). §8.7: konstruktiv vor destruktiv, Lücke davor.
- `importScopeCurrent()` (`usage-stats-page.ts:1696-1700`) = die geladenen Zeilen gehören zum Kanal
  der URL **und** `shownSetId() === selectedEmoteSetId()` — der Wächter für „das Raster zeigt
  wirklich dieses Set".
- Auswahl: `new ListSelection(atlasOrder, (emote) => emote.sevenTvEmoteId, emotes)`
  (`usage-stats-page.ts:1238`), Schlüssel = 7TV-ID (Spec #200 6.5/7.2), `selectMany` add-only,
  `selectedItems()` liefert die Zeilen aus der Grundmenge; `selectedForDelete` (`:1515-1529`) nimmt
  nur `membership === 'live'`.
- „Nicht mehr im Set" (`membership === 'left'`, nur in der Ansicht eines nicht-aktiven Sets): Zelle
  bekommt `app-sprite-cell-void` (`usage-stats-page.html:716`), Sprite `[dimmed]="…"`
  (`:763`, `EmoteSprite.dimmed` → `opacity-40`, `shared/emotes/emote-sprite.ts:120`), Badge
  `usageStats.setView.leftBadge`. Das ist die Darstellung, die 9.4 für „nicht im Set" wiederverwendet.
- Routing: `channels/:channelName` lädt `ChannelWorkspaceLayout`; Kinder `usage-stats`
  (`loadChildren` → `features/usage-stats/usage-stats.routes.ts`, eigener Chunk wegen des
  Import-Graphen, #264; `canActivate: [usageStatsAccessGuard]`), `vote-sessions` (`authGuard`),
  `activity` (`channelManageGuard`), `vote-sessions/:sessionId` (`app.routes.ts:96-132`). Die
  Reiterleiste steht in `features/channel-workspace/channel-workspace-layout.ts:103-114`
  (`<app-tab-link link="usage-stats"|"vote-sessions"|"activity">`, Schlüssel
  `channelWorkspace.tabs.*`, Rechte aus `GET /api/channels/{channelName}/permissions` →
  `ChannelPermissions { canManage; canViewUsageStats; isGlobalAdmin; isTracked; isBotActive }`).
- Eine wiederverwendbare Kachel gibt es als Primitive (`EmoteSprite`, `EmoteSpriteAnimated`) plus
  Layout-Helfer (`shared/grid/atlas-grid.ts`: `ATLAS_CELL_PX`, `atlasColumns`, `packAtlasRows`;
  `shared/grid/grid-columns.ts`: `chunkIntoRows`); das Raster selbst ist auf der Nutzungsseite und
  der Vote-Detailseite jeweils inline gebaut. Einzige eigenständige Rasterkomponente ist
  `ForeignEmoteGrid` (`shared/seven-tv/foreign-emote-grid.ts`), an `ForeignEmoteRow` gekoppelt.

### 3.6 Was aus dem Konzept vom 2026-10-03 trägt

- **Schlüssel ist die 7TV-ID, die Guid ist Nutzlast** (Konzept 2 Punkt 4; Spec #200 6.5/7.2):
  Tags schlüsseln nach `SevenTvEmoteId`, nie nach `Emote.Id` (E22).
- **Invariante `selectedKeys ⊆ keys(Grundmenge)`, `selectMany` add-only** (Konzept 2 Punkt 3):
  „Tag zuweisen" liest `selection.selectedItems()` und ändert die Auswahl nicht; der Tag-Filter
  verdeckt Markierungen wie jeder Filter, die Dock-Nebenzeile „n davon ausgeblendet" greift unverändert.
- **Namensvettern** (Konzept 5 und 9/Entscheidung 3): ein Name ist bei 7TV keine Identität (192
  Fälle im Halloween-Set). Tag-Einträge tragen deshalb die ID als Schlüssel und den Alias nur als
  Snapshot; der Einspiel-Abgleich „schon im Set" läuft über die ID, der Alias-Konflikt über die
  vorhandene Vorlesung (7.1 Schritt 5).
- **Die Parser** (`parseSelectionList`, `matchSelectionList`) werden **nicht** gebraucht — es gibt
  keine Datei. Die Abgleichslogik lebt stattdessen in zwei reinen Funktionen (7.1/7.2), die
  dieselbe Haltung haben: ordinaler ID-Vergleich, Reihenfolge der Eingabe bleibt, jede Zahl aus
  derselben Liste wie die Namen.

---

## 4. Entscheidungen

Jede Zeile: Entscheidung, Begründung in einem Satz, verworfene Alternative. E1–E8 und E9–E10 sind
Betreiberentscheidungen aus dem Brainstorming; die übrigen sind Festlegungen dieser Spec, die den
Rahmen ausfüllen und einzeln kippbar sind.

| Nr. | Entscheidung | Begründung | Verworfen |
|---|---|---|---|
| **E1** | Tags gehören dem **Kanal**; alle Manager sehen und nutzen sie. Es wird **kein Ersteller** gespeichert. | Der Vertretungs-Mod muss sie finden; ohne Ersteller tragen Tags keine Personendaten — Datenschutzerklärung, `RetentionPolicy` und #243 bleiben unberührt (3.2). | Persönliche Tags (v1), Ersteller-Spalte „für später". |
| **E2** | **Ausräumen entfernt nur, was dieses Tag eingespielt hat** (Platzierungen). Vorher schon im Set Vorhandenes (KEKW) bleibt. | Das Tag ist eine Zeitschaltung für *seine* Emotes, kein Löschfilter. | „Alle getaggten entfernen", mit Ausnahmeliste. |
| **E3** | Getaggt wird **nur aus der Rasterauswahl** des aktiven Sets; ein Emote, das nicht im aktiven Set ist, lässt sich nicht taggen. | Ein Einstieg, eine Grundmenge; die Rasterabfrage kennt ohnehin nur unarchivierte Zeilen (3.1). Preis akzeptiert: erst einspielen, dann taggen. | Taggen aus Datei, aus fremdem Kanal, aus einer freien ID-Eingabe. |
| **E4** | **Beide** Oberflächen: Tag-Filter in der Filterzeile **und** eine Tags-Seite je Kanal. | Der Filter ist der schnelle Weg in der Aufräumsitzung; die Seite ist der Ort, an dem man Tags versteht und pflegt — auch die Einträge, die gerade nicht im Set sind und im Raster deshalb unsichtbar wären. | Nur Filter; nur Seite. |
| **E5** | **Variante A: Platzierungen werden explizit gespeichert.** Nach dem Einspiel-Lauf meldet der Browser die **tatsächlich** hinzugefügten 7TV-IDs plus Set-ID. Eine verlorene Meldung ist fail-safe: beim Ausräumen erscheinen die Emotes dann **unangehakt** (zu wenig entfernt, nie zu viel). | Dasselbe Muster wie `sync-imported`/`sync-restored` (3.3, 3.4); der Server weiß nichts aus eigener Beobachtung, was er nicht beweisen kann. | **Variante B:** Ableiten aus `FirstSeenAt` — gehört dem Sync, Bedeutung bei Wieder-Hinzufügen unklar, Handzufügungen zählten als Tag-Zufügungen. |
| **E6** | Platzierungen tragen die **Set-ID**. Platzierungen eines nicht-aktiven Sets ruhen und gelten wieder, wenn dieses Set aktiv ist. Ausräumen wirkt immer auf das **aktive** Set. | Ein Kanal wechselt das aktive Set (Halloween-Umstellung 2026-10-01); ohne Set-ID würde ein Platzierungsstand das falsche Set beschreiben. | Platzierungen beim Set-Wechsel löschen; Platzierungen set-unabhängig. |
| **E7** | Zwei Tags teilen ein Emote X: die Platzierung gehört dem Tag, das X **wirklich** hinzugefügt hat. Wird dieses Tag ausgeräumt und ein anderes **eingespieltes** Tag desselben Sets enthält X, bleibt X im Set und seine Platzierung **wandert** zu diesem Tag. | „Wird noch gebraucht" muss das System wissen, nicht der Mod; nach dem Wandern räumt das zweite Tag X später korrekt aus. | Doppelte Platzierungen beim Einspielen anlegen (zählte „schon im Set" als Zufügung). |
| **E8** | **Veraltete Platzierungen** (Emote inzwischen von Hand entfernt) werden nicht aktiv korrigiert; sie fallen beim nächsten Ausräumen heraus, weil nur vorgeschlagen wird, was laut Live-Lesung im Set ist. | Kein eigener Abgleichjob, kein Worker; die Live-Lesung ist ohnehin Pflicht vor jedem Löschen (3.4). | Sync-Hook, der Platzierungen beim Archivieren löscht. |
| **E9** | **Rechte:** Lesen (Tags, Einträge, Status) hinter `UsageStatsAccessAuthorizationFilter` (7TV-Editoren dürfen sehen); Pflegen (anlegen, umbenennen, löschen, zuweisen, herausnehmen) hinter `ChannelManagementAuthorizationFilter`; **Melden** von Einspielen/Ausräumen hinter `UsageStatsAccessAuthorizationFilter`. | Wer mit eigenem 7TV-Token schreiben darf, muss melden dürfen — dieselbe Stufe wie `sync-deleted`/`sync-restored` (3.3). Folge: ein 7TV-Editor kann ein Tag einspielen und ausräumen, aber nicht ändern. | Melden hinter Management (ein Editor-Lauf hätte keine Papierspur); alles hinter Management. |
| **E10** | **Meldungen sind idempotent.** Zwei Mods gleichzeitig oder eine Doppelmeldung ändern nichts. | Die Meldungen werden mit Timeout und Retry gesendet (3.4); ein Retry darf nichts verdoppeln. | Sequenznummern je Lauf. |
| **E11** | **„Im Set"-Status serverseitig** = eine unarchivierte `Emote`-Zeile des Kanals mit dieser `SevenTvEmoteId` existiert (3.1). Er dient den Zählern und der Tags-Seite. Der **Vorschlag beim Ausräumen** kommt dagegen aus der **Live-Lesung** bei 7TV, nie aus diesem Status. | Dieselbe Quelle wie das Raster des aktiven Sets — eine Zahl, eine Quelle; vor einem Löschen zählt nur, was 7TV jetzt sagt (§4.2, Set-Lesung #304). | Status aus der Set-Vorschau-Route (#220, Rate-Limit-Eimer, 60-s-Cache); Live-Lesung für jede Zählung. |
| **E12** | **Einspielen ist ein Import-Lauf** über `startImportFlow`/`SevenTvImportService` mit neuem `ImportOrigin` `{ kind: 'tag'; tagId; tagName; channelName }` und neuem `SourceKind` `"tag"` im Server-Vokabular (3.3, 3.4). Kein neuer `SevenTvRunKind`. | Vorlesung, Konfliktschritt, Kapazitätswarnung, Token, Arbiter, Settlement und Dock existieren; ein eigener Lauf würde all das kopieren. | Eigener „Tag-Lauf"; Origin `'channel'` mit eigenem Kanalnamen (log wäre eine Lüge: „aus Kanal X"). |
| **E13** | **Ausräumen ist ein Lösch-Lauf** über `SevenTvDeleteService.startDelete`; Purge-Protokoll, Download und „Wiederherstellen" wie bei jedem Delete (3.4). | Unwiderruflichkeit ist ein Produktversprechen (`PRODUCT.md` Prinzip 4); der Rückweg des Delete ist gebaut und getestet. | Eigener REMOVE-Lauf ohne Protokoll. |
| **E14** | **Die Platzierungsmeldung ist eine Meldung am Lauf-Record** (optional, nur für Läufe, die aus einem Tag gestartet wurden): der Import bekommt `tagPlacementReport`, der Delete `tagRemovalReport`, beide mit `SyncReportState`; `closed` wartet auf sie, der Dock zeigt Fehler und Retry wie bei `syncReport`. | Das ist „dasselbe Muster wie die bestehende Buchführung" (E5) wörtlich genommen; der Undo trägt bereits zwei Meldungen am Record (3.4). Eine Meldung außerhalb des Records hätte weder Retry noch Sichtbarkeit und würde bei geschlossenem Dock still verloren gehen. | Meldung aus dem Tag-Flow nach dem Settlement, ohne Retry-Oberfläche. |
| **E15** | **Kein Worker-Change.** Der Kanal-Purge nimmt die drei Tabellen über FK-Kaskade mit (3.2); die **Trockenlauf-Zählung** (`CountChannelCascadeAsync`) wird in v1 **nicht** um Tags erweitert, weil jedes neue Zählfeld `RetentionRunSummaryFormatter` im Worker berührt. | Die Zählung ist Beobachtungskomfort, die Kaskade die Wahrheit; der Worker bleibt für den bindenden Messlauf unangetastet (12.3). | Zählfelder plus Worker-Formatter-Zeile (kleine, aber echte Worker-Änderung — für den Betreiber in 13.3 als Wahl notiert). |
| **E16** | **Tag löschen mit Platzierungen ist erlaubt**, mit Hinweis „12 Emotes dieses Tags sind noch eingespielt — vorher ausräumen?"; danach sind sie gewöhnliche Set-Emotes. | Ein Tag ist Organisationsmittel, kein Besitz; ein Löschverbot zwänge zum Ausräumen, auch wenn die Emotes bleiben sollen. | Löschen sperren, bis ausgeräumt ist. |
| **E17** | **Emote aus Tag herausnehmen** löscht auch dessen Platzierungen (alle Sets); das Emote bleibt im Set. | Was nicht mehr zum Tag gehört, kann das Tag nicht mehr ausräumen — sonst würde Ausräumen etwas entfernen, das das Tag nicht mehr enthält. | Platzierung behalten. |
| **E18** | **Tag-Filter als neue Dimension von `EmoteUsageFilter`** (`tagId` + Schlüsselmenge); zählt in `isAnyActive()`, wird von „Filter zurücksetzen" mitgelöscht, färbt die Zählzeile. Das Select erscheint nur, wenn der Kanal ≥ 1 Tag hat. | Ein Filter unter Filtern; kein zweites Zurücksetzen, kein Dauer-Control bei Kanälen ohne Tags. | Eigener Filterzustand neben `usageFilter`; Tag-Filter immer sichtbar. |
| **E19** | **Die Ausräum-Vorschau ist selbst die Bestätigung**: ein eigener Dialog mit angehakten/unangehakten Zeilen und Grund, Set-Zeile, Shared-Set-Warnung und `danger-solid`-„n entfernen"; danach **kein** zweiter Bestätigungsdialog. | §4.2 kennt genau eine Bestätigung; `DeleteConfirmDialogData` kennt keine Zeilen und keine Checkboxen (3.4) — s. **Widerspruch 13.1**. | Vorschau-Dialog **plus** bestehender `DeleteConfirmDialog` (Doppelbestätigung); Vorschau inline auf der Seite ohne Dialog. |
| **E20** | **Grenzen:** 50 Tags je Kanal, Name max. 40 Zeichen, 1000 Einträge je Tag. Überschreitung ist 409 mit Code; die Oberfläche zeigt den Grund am gesperrten Knopf (§10). | Schutz vor Versehen und Missbrauch; HandOfBlood hat ~900 Emotes, ein Tag kann also das ganze Set fassen. | Keine Grenzen; Konfiguration je Kanal. |
| **E21** | **Namen** sind je Kanal eindeutig, groß/klein-unabhängig, getrimmt: gespeichert werden `Name` (Anzeige) und `NormalizedName` (`Trim` + `ToLowerInvariant`, wie `ChannelName.Normalize`) mit Unique-Index `(ChannelId, NormalizedName)`. Erlaubt ist jeder nicht-leere Text ohne Steuerzeichen bis 40 Zeichen. | Dieselbe Normalisierungshaltung wie Regel 9; eine DB-Collation wäre ein zweites Normalisierungsgesetz. | `citext`, Collation, nur exakte Gleichheit. |
| **E22** | **Tags schlüsseln nach `SevenTvEmoteId`**, FK nur auf `Channel` bzw. `EmoteTag` — **kein FK auf `Emote`**. | Regel 8: `Emote.Id` ist intern; ein Eintrag kann länger leben als eine Zeile im Raster sichtbar ist (archiviert = nicht im Set, aber weiter getaggt). Der Unique-Index `(ChannelId, SevenTvEmoteId)` garantiert, dass ein Eintrag höchstens eine Zeile trifft. | FK auf `Emote.Id`. |
| **E23** | **Alias- und Bild-Snapshot** am Eintrag kommen beim Zuweisen **vom Server** aus der unarchivierten `Emote`-Zeile (`Name`, `ImageUrl`); IDs ohne solche Zeile werden **ausgelassen und zurückgemeldet** (`skippedNotInSetIds`), die übrigen werden angelegt. | Der Server prüft E3 selbst, statt dem Client zu glauben; ein Teilerfolg ist für eine Gruppengeste ehrlicher als ein Alles-oder-nichts (Muster `notFoundIds` der sync-Routen). | Client liefert Alias/Bild; Alles-oder-nichts-400. |
| **E24** | **Rate-Limits:** Lesen `InteractiveRead`, Pflegen und Melden `Bookkeeping` (3.3). | Zuweisen ist eine Nutzeraktion mit Datenverlust bei 429 nach erfolgtem Klick; Melden darf nie verloren gehen — dieselbe Begründung wie an `sync-deleted`. | Alles `InteractiveRead`. |
| **E25** | **Tags-Seite als vierter Reiter `tags`** im Kanal-Workspace, Guard `usageStatsAccessGuard`, lazy über eine eigene `tags.routes.ts` (wie `usage-stats.routes.ts`, #264). | Sie liegt neben Nutzung/Votings/Aktivität auf derselben Hierarchiestufe (§8.1/§8.6); sie zieht den Import-Graphen und gehört deshalb in einen eigenen Chunk. | Unterseite unter `usage-stats/tags`; Dialog statt Seite. |

---

## 5. Datenmodell

Drei neue Tabellen, alle kanalgebunden, alle über FK-Kaskade am Kanal (3.2). Eine EF-Migration
(`AddEmoteTags`), in Produktion von Hand vor dem Deploy (CLAUDE.md „Prod-Migration").

### 5.1 `EmoteTag`

| Spalte | Typ | Bedeutung |
|---|---|---|
| `Id` | `long` (Identity) | PK. Erscheint in Routen (`{tagId}`). |
| `ChannelId` | `string` | FK → `Channel.Id`, `OnDelete(Cascade)`. |
| `Name` | `string(40)` | Anzeigename, getrimmt. |
| `NormalizedName` | `string(40)` | `Trim().ToLowerInvariant()` von `Name`; Unique-Index `(ChannelId, NormalizedName)` (E21). |
| `CreatedAtUtc` | `timestamptz` | Reihenfolge auf der Seite und Tie-Break für E7 (5.4). **Kein** Ersteller (E1). |

Limit 50 je Kanal (E20), geprüft im Service unter der Zeilensperre des Kanals (`LoadChannelForUpdateAsync`,
Muster `PurgeIfInactiveSinceAsync`), damit zwei gleichzeitige Anlagen nicht beide durchgehen.

### 5.2 `EmoteTagEntry`

| Spalte | Typ | Bedeutung |
|---|---|---|
| `TagId` | `long` | FK → `EmoteTag.Id`, `OnDelete(Cascade)`. |
| `SevenTvEmoteId` | `string(24)` | Schlüssel (E22). |
| `Alias` | `string` | Snapshot von `Emote.Name` beim Zuweisen (E23). Wird **nicht** nachgeführt; beim Einspielen ist er der ADD-Alias (7.1). |
| `ImageUrl` | `string` | Snapshot von `Emote.ImageUrl`, damit die Tags-Seite Einträge ohne sichtbare Rasterzeile zeichnen kann. |
| `AddedAtUtc` | `timestamptz` | Reihenfolge auf der Tags-Seite. |

PK/Unique `(TagId, SevenTvEmoteId)`. Limit 1000 je Tag (E20). Kein FK auf `Emote` (E22); die
Zuordnung zur Zeile geschieht per Join über `(ChannelId des Tags, SevenTvEmoteId)`.

### 5.3 `EmoteTagPlacement`

„Dieses Tag hat dieses Emote in dieses Set gebracht."

| Spalte | Typ | Bedeutung |
|---|---|---|
| `TagId` | `long` | FK → `EmoteTag.Id`, `OnDelete(Cascade)`. |
| `SevenTvEmoteId` | `string(24)` | Das Emote. |
| `SevenTvEmoteSetId` | `string(24)` | Das Set, in das es kam (E6). Dieselbe Formprüfung wie `EmoteSetIdValidation`. |
| `PlacedAtUtc` | `timestamptz` | Zeitpunkt der Meldung (nicht des 7TV-Schreibens — „wann wir es erfuhren", wie `ChannelEmoteSetObservation`). |

PK/Unique `(TagId, SevenTvEmoteId, SevenTvEmoteSetId)` — macht die Einspiel-Meldung idempotent (E10).
Index `(SevenTvEmoteSetId, SevenTvEmoteId)` für die Frage „wer hält X in S" (5.4, 6.3). **Es gibt
keinen FK von Placement auf Entry:** E17 löscht Platzierungen beim Herausnehmen explizit im Service,
damit die Reihenfolge der Schreibvorgänge sichtbar bleibt und ein Eintrag sich nicht über eine zweite
Kaskade löscht.

### 5.4 Invarianten und Regeln

1. **Ein Eintrag ohne Zeile ist erlaubt.** Ein getaggtes Emote kann aus dem Set verschwinden (Hand,
   Ausräumen); der Eintrag bleibt und ist auf der Tags-Seite „nicht im Set" (9.4).
2. **Eine Platzierung ohne Eintrag ist nicht erlaubt** (E17 hält das im Service ein; kein FK, s. o.).
3. **Höchstens eine Platzierung je `(Emote, Set)` soll „gelten"** — das erzwingt die DB nicht (zwei
   Tags könnten dasselbe Emote melden, wenn zwei Einspiel-Läufe dasselbe Emote fast gleichzeitig
   hinzufügen; 7TV lässt nur einen ADD durch, aber der zweite Lauf könnte eine Zeile `done` haben,
   bevor er den 409 sieht). Der Ausräum-Vorschlag (7.2) behandelt mehrere Halter deterministisch:
   die Platzierung des **ältesten** Tags (`CreatedAtUtc`, dann `Id`) gilt als Besitzer; die anderen
   gelten als „wird noch gebraucht".
4. **Übertragung (E7):** Beim Ausräumen von Tag T im Set S wandert eine nicht entfernte, noch im Set
   stehende Platzierung `(T, X, S)` zu dem anderen Tag T′ des Kanals, das (a) einen Eintrag für X hat
   und (b) mindestens eine Platzierung in S besitzt; bei mehreren T′ das älteste (Regel 3). Hat T′
   bereits `(T′, X, S)`, wird `(T, X, S)` nur gelöscht. Gibt es kein T′, bleibt die Platzierung bei T
   (der Mod hat sie bewusst abgehakt) — **Festlegung**, s. 7.2 Schritt 8.
5. **Regel 10** bei den Zählern (6.2/6.3): Gruppieren nur über die FK-Spalte einer Tabelle, Filter
   über skalare ID-Listen; der „im Set"-Join läuft über eine vorher materialisierte Menge der
   unarchivierten `SevenTvEmoteId`s des Kanals, nicht über eine Navigation ins `GroupBy`.
6. **Kanalzusammenführung** (`ChannelIdentityService.MergeAsync`,
   `EmotePurge.Infrastructure/Services/ChannelIdentityService.cs:555-600`): der Verlierer muss
   emote-los sein, sonst wird verweigert — Tags am Verlierer würden mit ihm kaskadieren. **Festlegung:**
   Tags zählen bei dieser Prüfung wie Emotes: hat der Verlierer Tags, wird die Zusammenführung ebenso
   verweigert (eine Zeile in `MergeAsync`, kein Worker-Vertrag).

### 5.5 Warum die Form später persönliche Tags zulässt

Ein optionales `OwnerTwitchUserId` (null = Kanal-Tag) auf `EmoteTag` würde reichen; der Unique-Index
bekäme den Eigentümer dazu, Lesen filterte nach „Kanal-Tags ∪ eigene". Das ist **nicht** Teil von v1
und wird nicht vorbereitet (keine leere Spalte „für später") — nur festgehalten, dass E1 die Tür nicht
zuschlägt. Sobald eine solche Spalte existiert, ist die Tabelle Teil der Kontolöschung (3.2).

---

## 6. API-Vertrag

Neue Datei `EmotePurge.Api/Endpoints/EmoteTagEndpoints.cs` mit `MapEmoteTagEndpoints(this WebApplication app)`,
registriert in `Program.cs` wie die übrigen. Service-Interface `IEmoteTagService` in
`EmotePurge.Core/Services/`, Implementierung `EmoteTagService` in `EmotePurge.Infrastructure/Services/`,
Registrierung `AddScoped` in `ServiceCollectionExtensions.AddEmotePurgeInfrastructure` (Regeln 4/5/6).
Handler bleiben dünn: Body-Prüfung, Actor, Service-Aufruf, Ergebnis-Mapping.

### 6.1 Gruppen und Filterketten

Drei Gruppen unter demselben Präfix, weil die Autorisierung je Gruppe gilt (3.3):

| Gruppe | Präfix | Kette |
|---|---|---|
| **Lesen** | `/api/channels/{channelName}/tags` | `RequireAuthorization()` → `ChannelNameValidationFilter` → `UsageStatsAccessAuthorizationFilter` → `RequireRateLimiting(InteractiveRead)` |
| **Pflegen** | `/api/channels/{channelName}/tags` | `RequireAuthorization()` → `ChannelNameValidationFilter` → `ChannelManagementAuthorizationFilter` → `RequireRateLimiting(Bookkeeping)` |
| **Melden** | `/api/channels/{channelName}/tags/{tagId}/placements` | `RequireAuthorization()` → `ChannelNameValidationFilter` → `UsageStatsAccessAuthorizationFilter` → `RequireRateLimiting(Bookkeeping)` |

`{tagId}` ist `long`; ein Tag, das es nicht gibt **oder das einem anderen Kanal gehört**, ist 404
`tag_not_found` — der Service sucht immer `(ChannelId, Id)`, nie `Id` allein. Der Kanal selbst wird
über `LoadChannelAsync` (normalisiert, Regel 9) aufgelöst; ein nicht getrackter Kanal ist 404
`channel_not_found` (bestehender Code).

### 6.2 Lesen

**`GET …/tags`** → 200, Liste in `CreatedAtUtc`-Reihenfolge:

| Feld | Bedeutung |
|---|---|
| `id`, `name` | |
| `entryCount` | Einträge. |
| `inSetCount` | Einträge mit unarchivierter Zeile (E11). |
| `placedCount` | Platzierungen dieses Tags im **aktiven** Set (`Channel.ActiveEmoteSetId`); 0, wenn kein aktives Set. |
| `activeEmoteSetId` | Einmal am Umschlag, nicht je Tag — damit der Client weiß, auf welches Set sich `placedCount` bezieht (`null`/leer = kein Sync). |

**`GET …/tags/{tagId}/entries`** → 200, Einträge in `AddedAtUtc`-Reihenfolge:

| Feld | Bedeutung |
|---|---|
| `sevenTvEmoteId`, `alias`, `imageUrl` | Snapshot (5.2). |
| `inSet` | unarchivierte Zeile vorhanden (E11). |
| `currentName` | `Emote.Name` der Zeile, wenn `inSet`, sonst `null` — die Tags-Seite zeigt den aktuellen Namen, wenn er vom Snapshot abweicht (Hinweis, keine Korrektur). |
| `placedByThisTag` | Platzierung `(tagId, id, aktives Set)` existiert. |
| `heldByOtherTags` | `{ id, name }[]` der **anderen** Tags des Kanals, die (a) einen Eintrag für dieses Emote **und** (b) mindestens eine Platzierung im aktiven Set haben (5.4 Regel 4). Leer, wenn kein aktives Set. |
| `placedByOtherTags` | `{ id, name }[]` der anderen Tags mit Platzierung `(·, id, aktives Set)` — die Quelle für „wird noch von … gebraucht" in 7.2 für nicht-eigene Platzierungen. |

Beide Listen sind das, was der Ausräum-Vorschlag (7.2) neben der Live-Lesung braucht; er fragt
nichts Weiteres ab. Regel 10 (5.4 Regel 5) gilt für beide Abfragen.

### 6.3 Pflegen

| Route | Body | Antwort | Fehler |
|---|---|---|---|
| **`POST …/tags`** | `{ name }` | 201 `{ id, name }` | 400 `tag_name_invalid` (leer, > 40, Steuerzeichen) · 409 `tag_name_taken` · 409 `tag_limit_reached` |
| **`PATCH …/tags/{tagId}`** | `{ name }` | 200 `{ id, name }` | wie oben außer Limit · 404 `tag_not_found` |
| **`DELETE …/tags/{tagId}`** | — | 204 | 404 `tag_not_found`. Kaskade löscht Einträge und Platzierungen (E16: keine Sperre). |
| **`POST …/tags/{tagId}/entries`** | `{ sevenTvEmoteIds }` | 200 `{ addedCount, alreadyTaggedCount, skippedNotInSetIds }` | 400 `emote_ids_empty`/`emote_ids_invalid` (bestehend) · 404 `tag_not_found` · 409 `tag_entry_limit_reached` (geprüft **vor** dem Schreiben über `vorhandene + neue > 1000`; nichts wird geschrieben) |
| **`POST …/tags/{tagId}/entries/remove`** | `{ sevenTvEmoteIds }` | 200 `{ removedCount }` | 400/404 wie oben. Löscht Eintrag **und** alle Platzierungen `(tagId, id, ·)` (E17). Unbekannte IDs zählen nicht und sind kein Fehler. |

`entries/remove` ist ein `POST` mit Body, kein `DELETE` mit Body — ein `DELETE`-Body ist in
HTTP-Clients und Proxys nicht verlässlich (Festlegung).

Zuweisen (E23) läuft so: Tag und Kanal laden → IDs deduplizieren → unarchivierte `Emote`-Zeilen des
Kanals zu den IDs laden → für jede ID ohne Zeile: `skippedNotInSetIds` → für jede ID mit bestehendem
Eintrag: `alreadyTaggedCount` → Rest anlegen mit `Alias = Name`, `ImageUrl` der Zeile. Ein
`SaveChangesAsync`. Ein Unique-Verstoß durch einen parallelen Zuweiser wird als `alreadyTagged`
gezählt, nicht als Fehler (E10-Haltung).

Audit (`AuditLogEntry`, Snapshot-String `ChannelName`): `tag.create`, `tag.rename`, `tag.delete`
mit Details `{ tagId, tagName }` (bei `delete` zusätzlich `{ entryCount, placementCount }`).
**Zuweisen und Herausnehmen schreiben keinen Audit-Eintrag** (Festlegung: das wären bei einer
Aufräumsitzung Dutzende Zeilen ohne Erkenntnis; die Meldungen in 6.4 tragen die Papierspur des
7TV-Schreibens). Audit-Einträge tragen den Actor und werden bei Kontolöschung pseudonymisiert (3.2) —
die Tags selbst nicht, weil sie keinen tragen.

### 6.4 Melden (Platzierungen)

| Route | Body | Antwort |
|---|---|---|
| **`POST …/tags/{tagId}/placements`** (Einspielen) | `{ emoteSetId, sevenTvEmoteIds }` — die **tatsächlich hinzugefügten** IDs des Laufs (`importedKeys`, 3.4) | 200 `{ recordedCount, alreadyRecordedCount, notTaggedIds }` |
| **`POST …/tags/{tagId}/placements/removed`** (Ausräumen) | `{ emoteSetId, removedIds, keptIds }` — `removedIds`: `doneKeys` des Lösch-Laufs; `keptIds`: eigene Platzierungen, die laut Vorschau noch im Set stehen, aber nicht entfernt wurden (abgehakt oder „wird noch gebraucht") | 200 `{ deletedCount, transferredCount, droppedStaleCount, keptCount }` |

Server-Verhalten **Einspielen:** je ID ein Upsert `(tagId, id, emoteSetId)`; IDs ohne Eintrag im
Tag werden **nicht** platziert und als `notTaggedIds` zurückgegeben (Invariante 5.4/2). Zweite
identische Meldung: `alreadyRecordedCount = n`, nichts geschrieben (E10).

Server-Verhalten **Ausräumen**, über die Menge P = Platzierungen `(tagId, ·, emoteSetId)`:

1. `removedIds` ∩ P → löschen (`deletedCount`).
2. `keptIds` ∩ P → Übertragung nach 5.4 Regel 4: gibt es ein T′, wandert die Platzierung
   (`transferredCount`), sonst bleibt sie (`keptCount`).
3. P \ (`removedIds` ∪ `keptIds`) → veraltet (laut Live-Lesung nicht mehr im Set) → löschen
   (`droppedStaleCount`) (E8).
4. IDs in `removedIds`/`keptIds`, die nicht in P sind, sind kein Fehler (ein fremdes oder
   unplatziertes Emote wurde in der Vorschau angehakt — erlaubt, 7.2 Schritt 5).

Eine zweite identische Meldung findet P bereits bereinigt vor und schreibt nichts (E10). Beide
Meldungen schreiben je einen Audit-Eintrag `tag.playedIn` / `tag.removed` mit
`{ tagId, tagName, emoteSetId, emoteCount }` — die Papierspur des 7TV-Schreibens, analog
`emotes.syncImported`. `emoteSetId` wird mit `EmoteSetIdValidation.IsValid` geprüft (400
`invalid_emote_set_id`, bestehender Code); es **muss nicht** das aktive Set sein (E6: ein Lauf kann
in ein Set gemeldet werden, das kurz darauf nicht mehr aktiv ist — gemeldet wird, was geschrieben wurde).

**Kein Resync, kein Live-Event aus diesen Meldungen:** Der Import-Lauf löst seinen Resync über die
bestehende `sync-imported`-Kette aus, der Lösch-Lauf über `sync-deleted` (3.4). Die Platzierungsmeldung
ist Buchführung über die Buchführung — sie darf den Resync nicht ein zweites Mal anstoßen (Cooldown).

### 6.5 Neue Fehlercodes (Regel 7)

`tag_name_invalid`, `tag_name_taken`, `tag_limit_reached`, `tag_not_found`,
`tag_entry_limit_reached` — je ein Eintrag in `ApiErrorCodes.cs`, `api-error.ts` und beiden
Locale-Dateien unter `errors.api.*`. Wiederverwendet: `emote_ids_empty`, `emote_ids_invalid`,
`invalid_emote_set_id`, `channel_not_found`, `invalid_channel_name`, `invalid_source_kind`.
**Zusätzlich** nimmt `ValidateSyncImportedVocabulary` den Wert `"tag"` in das `SourceKind`-Vokabular
auf — ohne `SourceChannelName`, ohne `LeaderboardSort` (wie `"file"`); sonst endet jeder Einspiel-Lauf
mit einem 400 **nach** dem 7TV-Schreiben (3.3, Spec #147 F6). Die Audit-Details von
`emotes.syncImported` bekommen bei `"tag"` zusätzlich `tagId`/`tagName` **nicht** — der Lauf meldet
diese über 6.4; eine Verdopplung im Audit wäre zwei Wahrheiten.

### 6.6 Test-Substitution

`tests/EmotePurge.Api.Tests/ApiFactory.cs:252-274` ersetzt jeden Handler-Service, weil
`RequestDelegateFactory` die Handler-Services **vor** der Filter-Pipeline auflöst (CLAUDE.md „Tests").
`IEmoteTagService` kommt dort als Substitute dazu, sonst fährt die Filter-Matrix gegen die echte
Implementierung und braucht eine Datenbank.

---

## 7. Abläufe

Drei Einstiege ins Schreiben: **Tag zuweisen** (nur Api), **Einspielen** (Import-Lauf), **Ausräumen**
(Lösch-Lauf). Die beiden 7TV-Läufe laufen von der Tags-Seite und aus der Filterzeile **identisch**;
nur der Ort, an dem der Lauf angezeigt wird, unterscheidet sich (9.5).

### 7.0 Tag zuweisen (Dock)

1. Voraussetzungen für den Knopf „Tag zuweisen…" im `[selection-actions]`-Slot: `!isCoarse()`
   (Dock-Gate), `canManage()` (E9 — der Editor sieht den Knopf nicht), `importScopeCurrent()` **und**
   `shownSetId() === activeEmoteSetId()` (E3: nur das aktive Set), Auswahl nicht leer. Kein
   `startLocked`-Bezug — ein Zuweisen schreibt nicht bei 7TV und darf während eines Laufs passieren.
2. Klick → kleiner Dialog (`TagAssignDialog`, `openTagAssignDialog`): Liste der Tags des Kanals als
   Checkboxen (`GET …/tags`, bei Öffnen geladen; Skeleton §6.1), darunter ein Feld „Neuer Tag" mit
   Anlegen-Knopf (legt per `POST …/tags` an und hakt ihn an; Fehler als Feldfehler §5.3:
   `tag_name_taken`, `tag_name_invalid`, `tag_limit_reached`). Der Dialog **fügt nur hinzu**
   (add-only wie jede Gruppengeste, §2.5): ein bereits angehakter Tag lässt sich hier nicht abwählen —
   abgewählt wird über „Aus Tag entfernen" (7.0a). Die Checkboxen zeigen keinen Vorzustand „schon
   getaggt", weil die Auswahl gemischt sein kann; die Antwort nennt `alreadyTaggedCount`.
3. Bestätigen „n Emotes zuweisen" (n = `selectedItems().length`; die Zahl kommt aus derselben Liste
   wie die Namen, Konzept 2) → je angehaktem Tag ein `POST …/tags/{tagId}/entries` mit den
   `sevenTvEmoteId`s der Auswahl, **nacheinander** (Bookkeeping-Budget, keine Parallelflut) →
   Dialog schließt mit der Summe.
4. Rückmeldung als vergängliche Statusmeldung auf der **Zählzeile** (§4.5, Muster
   `selectionPrunedFeedback`, eigenes Regionspaar): „n Emotes zu Stronghold hinzugefügt" bzw. „… zu
   3 Tags hinzugefügt"; wenn `skippedNotInSetIds` > 0 (Rennen mit einem Sync): zweiter Satz „k davon
   sind nicht mehr im Set und wurden übersprungen". Die Auswahl bleibt bestehen. Der Tag-Filter
   (falls aktiv) rechnet seine Schlüsselmenge neu (Tags neu laden).

**7.0a Aus Tag entfernen (Dock, nur bei aktivem Tag-Filter):** Knopf „Aus ‚Stronghold' entfernen (n)"
am Ende der konstruktiven Gruppe; `canManage()`. Keine Bestätigung (nichts verlässt 7TV; Rückweg ist
erneutes Zuweisen) → `POST …/entries/remove` → Statusmeldung „n Emotes aus Stronghold entfernt" →
Tags neu laden; die Zellen fallen aus dem Filter, die Auswahl bleibt (die Nebenzeile „ausgeblendet"
zeigt sie). Hinweis im Knopf-`title`: Platzierungen gehen mit (E17).

### 7.1 Einspielen

**Voraussetzungen** (identisch zum heutigen Import): `!isCoarse()` (§2.5), `startLocked() === false`
(Arbiter, 3.4), aktives Set bekannt (`activeEmoteSetId()` nicht leer), Token vorhanden oder Prompt
(`openSevenTvTokenPromptDialog`, nach der Bestätigung wie beim Import — „do not align"). Der Knopf
ist bei `startLocked()` gesperrt und erklärt sich (§10) über denselben Grund wie der Import-Trigger.

**Schritte:**

1. `GET …/tags/{tagId}/entries` frisch laden (nicht aus einem Cache — die Partition in Schritt 3
   muss den Stand des Klicks beschreiben).
2. **Live-Lesung des aktiven Sets** über `loadSevenTvSetEntries(httpClient, activeEmoteSetId)`
   (3.4). `complete === false` oder Fehler → **warnen, nicht blocken**: die Partition läuft dann über
   `inSet` aus Schritt 1 und die Vorschau sagt „Set konnte nicht vollständig gelesen werden — die
   Zahlen sind ein Schätzwert" (Muster `countIsUpperBound` des Restore, 3.4). Begründung: Einspielen
   ist konstruktiv; ein doppelter ADD scheitert bei 7TV harmlos als `failed`-Zeile, der
   `filterAlreadyPresent`-Schritt des Import-Flows liest ohnehin noch einmal live.
3. **Partition** (reine Funktion `partitionTagPlayIn(entries, liveIds | null)`,
   `shared/tags/tag-play-in.ts`): `inSet` = Eintrag, dessen ID in `liveIds` ist (Fallback `inSet`
   des Servers) → **übersprungen, keine Platzierung** (E7: schützt KEKW); `toAdd` = Rest, in
   Eintragsreihenfolge, als `ImportRow { sevenTvEmoteId, name: alias, imageUrl }`. Ergebnis
   `{ toAdd, alreadyInSet, source: ImportSource }` mit `ImportSource.origin = { kind: 'tag', tagId,
   tagName, channelName }`, `duplicatesCollapsed = 0`, `discardedRows = 0`.
4. `toAdd.length === 0` → kein Lauf; vergängliche Meldung „Alle 100 Emotes von Stronghold sind
   schon im Set" (§4.5), fertig. Sonst weiter.
5. **`startImportFlow(deps, source, { kind: 'chosen', choice })`** mit `choice` = das aktive Set des
   Kanals, gebaut wie `import-trigger.ts`'s `toImportTarget` (3.4) — der Flow nimmt den
   `'trackedActive'`-Schnellweg. Von hier an ist alles der bestehende Import: Ziel laden,
   **Bestätigungsdialog** mit Titelzeile „Stronghold einspielen" (neue Origin-Zeile für `kind: 'tag'`
   in `import-preview.ts`/`import-confirm-dialog.ts`: „97 werden hinzugefügt, 3 sind schon im Set" —
   die 3 aus Schritt 3 reist als `skippedDuplicates`-Vorgabe mit, der Dialog zeigt sie in der
   bestehenden Übersprungen-Zeile), Slot-Projektion mit Kapazitätswarnung (`projectSlots`, warnt,
   blockt nicht), **Namenskonflikte** als `NameCollisionRow` mit Konfliktschritt (`skip` Vorgabe,
   `renameSource`, `replaceTarget`, `adoptSourceName`), Token, `filterAlreadyPresent` live,
   `recheckTransferPlan` bei `replace`-Zeilen, `startImport`.
6. **Lauf** über `SevenTvImportService`; Anzeige im Dock (`app-import-progress-section`), Abbruch über
   `cancel()`, Settlement bei `unknown`-Zeilen, Meldung `sync-imported` mit `sourceKind: 'tag'`,
   Resync des Kanals wie bei jedem Import ins aktive Set.
7. **Platzierungsmeldung** (E14): sobald der Lauf `settled` ist, sendet der Service parallel zu
   `reportImported` die Meldung `POST …/tags/{tagId}/placements` mit `{ emoteSetId: run.targetSetId,
   sevenTvEmoteIds: importedKeys(run) }` — **nur** `done`-Zeilen, die ein Emote hinzugefügt haben
   (`add`, `renameSource`, `replace`; `adoptSourceName` fügt nichts hinzu). Bei Abbruch ist das genau
   das bis dahin Gelungene. Timeout/Retry wie `syncReport` (`timeoutReportAttempt`,
   `retryTransientSyncFailures`); Zustand `tagPlacementReport: SyncReportState` am `ImportRunInfo`,
   nur gesetzt, wenn `origin.kind === 'tag'`; `reportsPending` des Lebenszyklus zählt sie mit, der
   Dock zeigt bei `failed` die Zeile „Einspiel-Vermerk konnte nicht gespeichert werden" mit Retry
   (Wortlaut-Familie `sevenTvRun.tagReport.*`). Ein endgültig verlorener Vermerk ist fail-safe (E5):
   die Emotes erscheinen beim Ausräumen unangehakt unter „war schon vorher im Set".
8. Nach `closed`: Tags-Seite/Filterzeile laden `GET …/tags` neu (Zähler).

**Fehlerpfade:** Token fehlt und Prompt abgebrochen → kein Lauf, kein Vermerk. 7TV 401/403 mitten im
Lauf → Abbruch durch den Engine-`abortOn` (Privilegien), Token wird gelöscht, Rest `cancelled`,
Vermerk nennt das bis dahin Gelungene. Set wechselt zwischen Schritt 1 und 5 → der Import-Flow prüft
sein Ziel selbst (`loadImportTarget`, `recheckTransferPlan`); die Meldung trägt `run.targetSetId`,
also das Set, in das wirklich geschrieben wurde (E6).

### 7.2 Ausräumen

**Voraussetzungen** wie 7.1 (`!isCoarse()`, `startLocked()`, Token, aktives Set).

**Schritte:**

1. `GET …/tags/{tagId}/entries` frisch laden (liefert `placedByThisTag`, `heldByOtherTags`,
   `placedByOtherTags` für das aktive Set, 6.2).
2. **Set-Prüfung** `resolveEditableSet(activeEmoteSetId)` mit Timeout (wie `MassDeletePanel.openConfirmDialog`);
   `notEditable`/`notSelectable`/`unavailable` → kein Dialog, Grund als Banner an der Stelle des
   Knopfs (Familie `massDelete.errors.*`).
3. **Live-Lesung** `loadSevenTvSetEntries(activeEmoteSetId)`; **`complete === false` oder Fehler
   blockt** (3.4: „a list that only knows half must not delete"), Grund
   `MEMBER_READ_TRUNCATED/UNAVAILABLE` wie beim Delete. Die Lesung liefert zugleich die Aliasse für
   das Protokoll (`aliasesById`) — eine Lesung, zwei Zwecke.
4. **Vorschlag** (reine Funktion `proposeTagRemoval(entries, live)`, `shared/tags/tag-removal.ts`),
   über alle Einträge des Tags, Reihenfolge der Einträge:
   - ID **nicht** in `live` → Zeile **nicht gelistet**, nur gezählt („k Einträge sind nicht im Set").
   - ID in `live`, `placedByThisTag`, `heldByOtherTags` leer → **angehakt** (die Löschkandidaten).
   - ID in `live`, `placedByThisTag`, `heldByOtherTags` nicht leer → **unangehakt**, Grund „wird noch
     von <T′> gebraucht" (E7; T′ = erstes Element, Server sortiert nach 5.4 Regel 3).
   - ID in `live`, nicht `placedByThisTag`, `placedByOtherTags` nicht leer → **unangehakt**, Grund
     „wird noch von <T′> gebraucht".
   - ID in `live`, nicht `placedByThisTag`, niemand hat sie platziert → **unangehakt**, Grund „war
     schon vorher im Set" (E2, KEKW; auch der Fall einer verlorenen Einspiel-Meldung, E5).
   Ergebnis: `{ rows: { sevenTvEmoteId, alias (aus live, alle Aliasse), checked, reason }[],
   notInSetCount }`.
5. **Bestätigung = Vorschau** (E19, `TagRemovalConfirmDialog`, `openTagRemovalConfirmDialog`,
   §7): Titel „Stronghold ausräumen — n Emotes entfernen"; Set-Zeile wie `massDelete.confirmSetLine`;
   Shared-Set-Warnung aus `EmoteAdminService.getSetWarning(channelName, setId)` als `error`-Banner
   (gleicher Wortlaut wie im Delete-Dialog); dann die Zeilen: **zuerst die angehakten** (Checkbox,
   Sprite-Still, Alias), **dann die unangehakten mit Grund** als eigener Block mit Zwischenüberschrift
   „Nicht vorgeschlagen"; Checkboxen sind **umschaltbar in beide Richtungen** (ein Mod darf KEKW doch
   mitnehmen oder ein Stronghold-Emote behalten — die Vorschau ist Vorschlag, die Entscheidung liegt
   beim Menschen, `PRODUCT.md` Prinzip 1), die Zahl im Knopf folgt live; unten die beiden leisen Sätze
   des Delete (unwiderruflich, Protokoll). `danger-solid`-Knopf „n entfernen", bei n = 0 gesperrt mit
   Grund (§10). Liste > 50 Zeilen: virtuell gescrollt wie der Konfliktschritt; die angehakten Namen
   zusätzlich **ungecappt** benannt (Haltung des Delete-Dialogs: jedes Löschziel ist vor dem
   unwiderruflichen Schritt mit Namen sichtbar).
6. Bestätigen → **Abbruchprüfung** wie `abortReasonBeforeStart`: ist `activeEmoteSetId()` nicht mehr
   die beim Klick eingefrorene Set-ID oder `arbiter.activeRun() !== null`, kein Lauf,
   `noteRefusedStart('delete')` bzw. Abbruchnotiz `massDelete.setChangedDuringConfirm` (3.4).
7. **`SevenTvDeleteService.startDelete(setId, channelName, emotes, expectedChannelName,
   targetOwnerTwitchId)`** mit `emotes` = angehakte Zeilen als `DeleteQueueEmote { sevenTvEmoteId,
   name, aliases (aus Schritt 3) }`, `expectedChannelName = channelName` (aktives Set),
   `targetOwnerTwitchId` aus Schritt 2. Von hier: der bestehende Delete — Fortschritt, Abbruch,
   Settlement, **Purge-Protokoll** (`buildPurgeRunProtocol`, Download), Meldung `sync-deleted`,
   Resync, **„Wiederherstellen"** am beendeten Lauf (E13).
8. **Ausräum-Vermerk** (E14): nach `settled` sendet der Service parallel zu `reportDeleted` die
   Meldung `POST …/tags/{tagId}/placements/removed` mit `{ emoteSetId: run.setId, removedIds:
   run.result.doneKeys, keptIds }`, wobei `keptIds` = IDs der Vorschlagszeilen mit
   `placedByThisTag && !checked` **plus** angehakte eigene Platzierungen, deren Zeile `failed`,
   `cancelled` oder `unknown` endete (sie stehen noch im Set; die Platzierung bleibt, damit das
   nächste Ausräumen sie wieder vorschlägt — **Festlegung**). Zustand `tagRemovalReport` am
   `DeleteRunInfo`, nur bei Tag-Läufen; Dock-Zeile und Retry wie 7.1 Schritt 7. Der Server
   überträgt, behält und bereinigt nach 6.4.
9. Nach `closed`: Zähler neu laden.

**Fehlerpfade:** Live-Lesung unvollständig → blockt (Schritt 3), nichts passiert; der Nutzer sieht den
Grund und kann es erneut versuchen. Lauf abgebrochen → `doneKeys` ist das Entfernte, der Rest bleibt
platziert (Schritt 8). Vermerk endgültig verloren → Platzierungen bleiben stehen; beim nächsten
Ausräumen sind die entfernten Emotes nicht in `live`, fallen also still heraus (E8) und werden bei
dieser Meldung als `droppedStale` bereinigt. „Wiederherstellen" nach einem Ausräumen fügt die Emotes
wieder hinzu, **ohne** Platzierung — sie sind danach „war schon vorher im Set"; das ist korrekt,
weil der Restore kein Tag ist, und wird in 13.4 als bewusste Lücke notiert.

---

## 8. Randfälle

| Fall | Verhalten |
|---|---|
| **Aktives Set wechselt** (z. B. Halloween, 2026-10-01) | Platzierungen des alten Sets ruhen (E6). `placedCount`/`placedByThisTag` beziehen sich auf das jetzt aktive Set; die Tags-Seite sagt im Kopf, auf welches Set sich die Zahlen beziehen (`activeEmoteSetId`, Name über die Set-Liste). Einspielen schreibt ins neue Set und meldet dessen ID. Zurückwechseln → die alten Platzierungen gelten wieder. |
| **Tag löschen mit Platzierungen** | Erlaubt (E16); Bestätigung `ConfirmDialog` mit „12 Emotes dieses Tags sind noch eingespielt — vorher ausräumen?" und zwei Knöpfen: „Trotzdem löschen" (`danger-solid`), Abbrechen. Kein „Ausräumen" als dritter Knopf (§7: eine Wahl gehört in den Body, nicht als zweiter Exit) — wer ausräumen will, bricht ab und tut es. Danach sind die Emotes gewöhnliche Set-Emotes. |
| **Emote aus Tag herausnehmen** | Platzierungen `(tag, id, ·)` gehen mit (E17); Emote bleibt im Set. |
| **Zwei Tags, ein Emote; beide eingespielt** | Besitzer ist, wer zuerst gemeldet hat (nur einer kann wirklich hinzugefügt haben). Ausräumen des Besitzers → Zeile unangehakt „wird noch von T′ gebraucht", Platzierung wandert (E7). Ausräumen des Nicht-Besitzers → Zeile unangehakt „wird noch von T gebraucht". |
| **Emote von Hand aus dem Set entfernt** | Platzierung veraltet (E8); beim nächsten Ausräumen nicht in `live`, nicht gelistet, als `droppedStale` bereinigt. Tags-Seite zeigt es dimm „nicht im Set" mit `placedByThisTag`-Marke, bis das geschieht. |
| **Emote von Hand wieder hinzugefügt, nachdem das Tag es ausgeräumt hat** | Es gibt keine Platzierung → „war schon vorher im Set" (E2). Richtig: das Tag hat es diesmal nicht gebracht. |
| **Eintrag mit veraltetem Alias** (Set-Alias wurde umbenannt) | Im Set → `currentName` abweichend, Tags-Seite zeigt „heute: NeuerName" als Hinweis. Nicht im Set → Einspielen nutzt den Snapshot-Alias; ist er vergeben → Konfliktschritt (7.1/5). |
| **Duplikatzelle (#74, zwei Aliasse, eine ID)** | Ein Eintrag (ID). Einspielen fügt **einen** ADD unter dem Snapshot-Alias hinzu. Ausräumen: ein REMOVE nimmt beide Einträge; Protokoll trägt alle Aliasse aus der Live-Lesung (7.2/3) — wie beim Delete. |
| **Einspiel-Lauf abgebrochen** | Vermerk nennt `importedKeys` (= Gelungenes bis zum Abbruch). |
| **Tab geschlossen während des Laufs** | Der `beforeunload`-Wächter des Arbiters greift bei destruktiven Läufen (Ausräumen, Einspielen mit `replace`-Zeilen); ein reiner Add-Lauf hat ihn nicht (3.4, §2.5). Verlorener Vermerk → fail-safe (E5). |
| **Zwei Mods räumen gleichzeitig aus** | Der zweite Lauf sieht beim `REMOVE` 7TV-Fehler (`failed`-Zeilen) oder in der Live-Lesung die Emotes nicht mehr. Beide Vermerke sind idempotent (E10): der erste löscht, der zweite findet nichts. |
| **Kanal ohne aktives Set** (noch kein Sync) | Tags-Seite: Tags und Einträge lesbar/pflegbar, Einspielen/Ausräumen fehlen (kein Set). Dock-Knopf „Tag zuweisen" fehlt (Markierungshälfte braucht ein Set, §2.5). |
| **Shown set ≠ aktives Set** im Raster | Tag-Filter funktioniert (filtert nach ID). „Tag zuweisen" und die Filterzeilen-Knöpfe Einspielen/Ausräumen **fehlen** (E3/E6: beide meinen das aktive Set; ein Knopf, der auf ein anderes Set wirkt als das gezeigte, wäre die Falle aus Spec #200 F5). Die Filterzeile sagt stattdessen „Einspielen und Ausräumen wirken auf das aktive Set" als Satz (§2.5: was ins Leere zeigt, wird erklärt). |
| **Grober Zeiger** | Dock, Zuweisen, Einspielen, Ausräumen fehlen (§2.5, kein 7TV-Token ohne Devtools). Tags-Seite ist lesbar; Umbenennen/Löschen/„Aus Tag entfernen" bleiben (keine 7TV-Schreibpfade) — **Festlegung**. |
| **Kanal-Purge (Admin, Aufbewahrung, #245)** | FK-Kaskade nimmt `EmoteTag` → `EmoteTagEntry`/`EmoteTagPlacement` mit (3.2). Keine Codeänderung an `PurgeAsync` nötig; die Trockenlauf-Zählung bleibt ohne Tags (E15). |
| **Kanalzusammenführung** | Verlierer mit Tags wird wie Verlierer mit Emotes verweigert (5.4 Regel 6). |
| **Limit erreicht** | 409 mit Code; Dialoge zeigen den Grund am gesperrten Knopf, nicht als Banner nach dem Klick, wo es vorhersehbar ist (Tag-Anlegen bei 50/50: Feld deaktiviert mit Satz). |
| **Rennen Zuweisen ↔ Sync** | Emote wurde zwischen Rasterladen und Klick archiviert → `skippedNotInSetIds` (E23), Meldung nennt es. |

---

## 9. Oberfläche

Die Spec beschreibt **was** und **wo**, nicht das Pixelbild; der Feinschliff läuft als Design-Pass im
Plan nach `docs/UI-Designsprache.md` (Checkliste §11). Verbindlich sind die Zwänge: keine neuen
Dauer-Controls, Drilldown statt Dauerblock, kein Layoutsprung (Memory „Keine Layout-Sprünge",
„Frontend-Zurückhaltung"), Primitive aus `shared/ui/` statt Utility-Ketten, Farben nur aus Tokens,
Labels konstant (§9), jede Sperre erklärt sich (§10). Alle Texte de/en.

### 9.1 Raster: Dock

Im `[selection-actions]`-Slot (konstruktive Gruppe, vor der Lücke zum „Löschen", §8.7):
**„Tag zuweisen…"** unter den Bedingungen aus 7.0; bei aktivem Tag-Filter zusätzlich **„Aus ‚Stronghold'
entfernen (n)"** (7.0a; die Zahl im Text ist §8.7-konform: eine Kurzform mit Zählwert). Beide
`appButton="neutral"`. Keine Tag-Chips auf den Zellen; keine Tag-Angabe im Sidecar in v1
(Festlegung: der Sidecar ist Nutzungs-Lupe; eine Zeile „Tags: Stronghold" wäre das erste Nicht-Nutzungs-
Datum darin — als Nachläufer notiert, 13.5).

### 9.2 Raster: Tag-Filter in der Filterzeile

In **Zeile zwei** der Filterzeile (3.5), zwischen „Beobachtete ausblenden" und „Filter zurücksetzen":
ein natives `<select class="app-input-sm">` mit `aria-label` „Tag" und Option „Alle Tags" (Vorgabe),
**nur gerendert, wenn der Kanal ≥ 1 Tag hat** (E18) — ohne Tags verändert sich die Zeile nicht um
einen Pixel. Die Wahl setzt `usageFilter.setTag(tagId, keys)`; die Zählzeile färbt sich wie bei jedem
aktiven Filter, „Filter zurücksetzen" erscheint und löscht den Tag mit.

Mit gewähltem Tag stehen **in derselben Zeile**, unmittelbar nach dem Select, als Inline-Gruppe:
`Stronghold · 3 im Set · 97 nicht im Set` (Zahlen aus `GET …/tags`: `inSetCount`, `entryCount −
inSetCount`), dann `[Einspielen]` `[Ausräumen]` (`appButton="neutral"` bzw. `"danger"` — der Trigger
eines destruktiven Flusses ist `danger`, die Ausführung im Dialog `danger-solid`, §4.2), dann ein
Link „→ Übersicht" auf die Tags-Seite (`routerLink` `../tags`, Fragment `#tag-<id>`). Die Zeile darf
**umbrechen** (sie ist `flex-wrap`, wie heute) — das ist kein Layoutsprung, sondern ihr vorgesehenes
Verhalten; eine zweite Leiste gibt es nicht. Bedingungen für die zwei Knöpfe: 7.1/7.2 plus
`shownSetId() === activeEmoteSetId()` (8); sonst der Satz aus 8 statt der Knöpfe. Beide sind bei
`startLocked()` gesperrt mit Grund (`title` + sichtbarer Text wie `importTriggerDisabled`).

Die Zellen im Filter sind die Einträge des Tags, die im gezeigten Set liegen — nichts anderes als
heute ein Namensfilter. Verdeckte Markierungen laufen über die bestehende Dock-Nebenzeile.

### 9.3 Tags-Seite: Ort und Navigation

Vierter Reiter **„Tags"** (`channelWorkspace.tabs.tags`) in `ChannelWorkspaceLayout` nach
„Nutzung", sichtbar unter `canViewUsageStats()` (E9: Editoren sehen); Route `channels/:channelName/tags`
mit `usageStatsAccessGuard`, lazy (E25). Die Seite erbt den Up-Link des Layouts (§8.6: kein zweiter).
Kopf: Überschrift „Tags", Satz „Zahlen beziehen sich auf das aktive Set <Name>" (Set-Name aus der
Set-Liste, Fallback ID), rechts **„Neuer Tag"** (`appButton="primary"`, öffnet ein kleines
Namensfeld-Dialog mit Feldfehlern §5.3) nur bei `canManage()`.

### 9.4 Tags-Seite: Aufbau

**Ab `lg`: zwei Spalten.** Links die Tag-Liste als geregelte Zeilen (§2.1, Stretched-Link §2.3 —
die Zeile wählt den Tag): Name, darunter Micro-Zeile `100 Einträge · 3 im Set · 97 eingespielt`
(`entryCount`, `inSetCount`, `placedCount`). Die Wahl steht in der URL (`?tag=<id>` oder
Fragment), damit „→ Übersicht" aus der Filterzeile und ein Reload landen. Rechts der gewählte Tag:

- **Kopfzeile** mit Name und den Aktionen **Einspielen** (`neutral`), **Ausräumen** (`danger`),
  **Umbenennen**, **Löschen** (`danger`) — Umbenennen/Löschen nur `canManage()`; Einspielen/Ausräumen
  `!isCoarse()` und mit aktivem Set; alle Sperren erklären sich. Reihenfolge §8.7: konstruktiv,
  Lücke, destruktiv.
- **Kachelraster** der Einträge in Eintragsreihenfolge, Zellen `app-sprite-cell` 64 px mit
  `EmoteSprite` (`ATLAS_CELL_PX`, `atlasColumns`, `chunkIntoRows`; virtuell gescrollt mit
  `scrollWindow` ab > 200 Einträgen, §8.5), **ohne** Bänder (keine Nutzung hier). „Nicht im Set" =
  `app-sprite-cell-void` + `[dimmed]` wie `membership === 'left'` heute (3.5). Eine eingespielte
  Zelle (`placedByThisTag`) trägt eine kleine Marke unten links (`aria-hidden`, Wort im
  `aria-label`: „eingespielt") — **eine** Marke, keine Farbe (Festlegung; die Farbe würde ein zweites
  Statussystem neben dem Dimmen aufmachen). Hover/Fokus zeigt Alias und ggf. „heute: <currentName>"
  in einer Meta-Zeile unter dem Raster (kein per-Zelle-Label, §2.5).
- **Auswahl im Raster** (`ListSelection` nach 7TV-ID, add-only-Gesten, Shift-Bereich, Tastatur wie
  §2.5) → ein kleines Dock **unter dem Raster, im Fluss** (kein `.app-dock`, keine fixierte Leiste —
  §8.7: ohne Dock stehen auswahlgebundene Kommandos im Fluss wie auf der Vote-Detailseite) mit
  „Aus Tag entfernen (n)" (`canManage()`) und „Auswahl aufheben".
- **Lauf-Fläche** (9.5) und ein `DockOutcomeAnnouncer`-Mount für die Dock-Meldungen (§4.5).
- **Leerzustand ohne Tags** (`EmptyState`, §6.2): „Noch keine Tags. Wähle im Nutzungsraster Emotes
  aus und weise ihnen einen Tag zu." mit CTA „Zum Nutzungsraster" (Link) — die Aufforderung erklärt
  den einzigen Einstieg (E3). **Leerzustand ohne gewählten Tag**: Satz „Wähle links einen Tag".
  **Tag ohne Einträge**: „Dieser Tag hat noch keine Emotes" + derselbe Link.

**Unter `lg`: Liste → Detail als Drilldown.** Die Tag-Liste füllt die Breite; ein Tap öffnet das
Detail als eigene Ansicht derselben Route (`?tag=` gesetzt) mit `BackLink` „Tags" (§8.6, derselbe
Schlüssel wie der Reiter). Keine zwei Spalten, keine Akkordeons. Auf grobem Zeiger fehlen
Einspielen/Ausräumen und die Rasterauswahl (8); eine Zelle öffnet nichts (kein Drilldown-Dialog in
v1 — Festlegung, Nachläufer 13.5).

### 9.5 Wo die Läufe erscheinen

- **Aus der Filterzeile** gestartet: im Dock der Nutzungsseite wie heute — Import im
  `app-import-progress-section`, Delete im `app-mass-delete-panel` (Fortschritt, Protokoll,
  Wiederherstellen). Der Dock mountet über `actionDockHasContent`, der durch `importShown`/`deleteShown`
  bereits anspringt, wenn ein Lauf läuft — auch ohne Markierung (3.5).
- **Von der Tags-Seite** gestartet: die Seite hostet dieselben Lauf-Flächen in einem eigenen
  `.app-dock` (Dock-Vertrag §2.5: erscheint nur, solange es etwas zu tun oder zu lesen gibt;
  `DockClearanceService` für den Fußbereich, §8.5): `app-import-progress-section` **und** eine
  Delete-Fläche. **Voraussetzung:** Fortschritt, Protokoll-Download und Restore-Knopf des Delete
  liegen heute **in** `MassDeletePanel` (3.4). Der Plan zieht sie — analog `RestoreProgressSection`
  (#253 T9) — in eine `DeleteProgressSection`, die beide Hosts verwenden (die Nutzungs- und die
  Vote-Detailseite mounten sie dann unter dem Panel; das Panel behält nur den Knopf und die
  Vorprüfungen). Das ist die eine echte Umbaustelle am Bestand und steht in 12.2 als Vorbedingung.
  **Verworfen:** Einspielen/Ausräumen von der Tags-Seite auf das Raster umleiten (Navigation mitten im
  Fluss = Sprung; und auf dem Raster ist der Tag erst zu filtern, bevor der Lauf sichtbar wird).
- Der `usageStatsLeaveGuard` (Bestätigung beim Verlassen während eines Laufs) wird zu einem
  seitenneutralen `sevenTvRunLeaveGuard` verallgemeinert, den `tags.routes.ts` ebenfalls setzt
  (Festlegung; der Guard liest ohnehin nur den Arbiter).

### 9.6 Aktualität

Nach jeder eigenen Aktion lädt die Seite `GET …/tags` (und bei gewähltem Tag `…/entries`) neu.
Fremde Änderungen (zweiter Mod) erscheinen beim nächsten Laden; es gibt in v1 **kein** Live-Event
`tags.changed` (Nicht-Ziel). Die Set-Status-Reloads der Nutzungsseite (`liveReload` auf
`channel.synced`) lassen den Tag-Filter stehen; ändert ein Sync den `inSet`-Stand, ändern sich die
Zahlen beim nächsten `GET …/tags`, das die Seite nach einem Lauf-`closed` ohnehin anstößt.

### 9.7 i18n-Familien (de Referenz, en gleichlautend; Wortlaute Vorschlag)

`channelWorkspace.tabs.tags` „Tags" · `tags.page.*` (Überschrift, Set-Satz, Leerzustände, Zähler
`.counts.entries/.inSet/.placed` mit Plural) · `tags.actions.*` (`assign` „Tag zuweisen…",
`unassign` „Aus {{tag}} entfernen", `playIn` „Einspielen", `remove` „Ausräumen", `rename`, `delete`,
`create` „Neuer Tag") · `tags.assignDialog.*` · `tags.removalDialog.*` (`title`, `reason.alreadyPresent`
„war schon vorher im Set", `reason.heldBy` „wird noch von {{tag}} gebraucht", `notProposedHeading`
„Nicht vorgeschlagen", `notInSet.{one,other}`, `confirm` „{{count}} entfernen", `nothingToRemove`) ·
`tags.deleteDialog.placedHint.{one,other}` „{{count}} Emotes dieses Tags sind noch eingespielt —
vorher ausräumen?" · `tags.feedback.*` (zugewiesen, entfernt, skipped, alleSchonImSet) ·
`tags.filter.*` (`label` „Tag", `all` „Alle Tags", `inSet.{one,other}`, `notInSet.{one,other}`,
`overview` „Übersicht", `activeSetOnly` „Einspielen und Ausräumen wirken auf das aktive Set") ·
`import.origin.tag` „aus Tag {{tag}}" (Origin-Zeile im Bestätigungsdialog, §7.2-Reihenfolge) ·
`sevenTvRun.tagReport.{pending,failed,retry}` · `errors.api.tag_*` (6.5). Button-Labels bleiben
konstant; Zustände über `aria-pressed`/Sperre (§9).

---

## 10. Datenschutz und Aufbewahrung

- **Tags tragen keine Personendaten** (E1): kein Ersteller, kein Zeitstempel je Person, keine
  Twitch-ID. Inhalt sind 7TV-Emote-IDs, Aliasse, Bild-URLs, Set-IDs und frei gewählte Tag-Namen.
  Die Kontolöschung (`AccountDeletionService`) ist deshalb nicht zu erweitern (3.2 belegt, dass sie
  nur Tabellen mit Konto-Bezug kennt); `AccountRetentionQueries` bleibt unberührt.
- **Audit-Einträge** (6.3/6.4) tragen den Actor wie jeder Audit-Eintrag und fallen unter die
  bestehende Pseudonymisierung und die 365-Tage-Frist aus `RetentionPolicy` — nichts Neues.
- **Aufbewahrung der Tags selbst:** gebunden an den Kanal. Ein deaktivierter Kanal wird nach 180
  Tagen gepurgt (`RetentionPolicy`), die Kaskade nimmt Tags mit. Eine eigene Frist für Tags gibt es
  nicht (sie sind Betriebsdaten des Kanals, wie Emote-Zeilen).
- **Datenschutzerklärung:** Weil keine neue Kategorie personenbezogener Daten entsteht und keine Frist
  sich ändert, ist **keine** Textänderung nötig. Der Betreiber prüft das einmal selbst gegen seinen
  Text (er liegt außerhalb des Repos) — in 13.6 als Prüfpunkt, nicht als Aufgabe dieser Spec.
- **Tag-Namen als Freitext:** ein Mod könnte einen Personennamen eintippen. Das ist kein
  Systemdatum, sondern Nutzereingabe im Kanalkontext — wie Vote-Session-Titel heute; keine
  Sonderbehandlung (Festlegung).

## 11. Tests (Regeln 11/12)

**Backend — `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteTagServiceTests.cs`
(Testcontainers, echte DB):**

- Anlegen/Umbenennen: Eindeutigkeit groß/klein-unabhängig und getrimmt (`" Stronghold "` ↔
  `"stronghold"` → `tag_name_taken`); Länge 40 ok, 41 → `tag_name_invalid`; 50 Tags ok, 51 →
  `tag_limit_reached`; Tag eines anderen Kanals ist `tag_not_found`.
- Zuweisen: unarchivierte Zeile → Eintrag mit Alias/Bild-Snapshot; archivierte oder unbekannte ID →
  `skippedNotInSetIds`; bestehender Eintrag → `alreadyTaggedCount`; 1000/1001 → Limit **vor** dem
  Schreiben, nichts geschrieben; Dedupe der Eingabe.
- Herausnehmen: Eintrag weg, Platzierungen aller Sets weg, Zeile unberührt.
- Einspiel-Meldung: Upsert idempotent (zweimal → `alreadyRecordedCount`), ID ohne Eintrag →
  `notTaggedIds`, zwei Sets getrennt.
- Ausräum-Meldung: `removedIds` löscht; `keptIds` wandert zum ältesten qualifizierten T′ (Eintrag +
  Platzierung in S), bei vorhandenem `(T′, X, S)` nur Löschung; ohne T′ bleibt; Rest `droppedStale`;
  zweite identische Meldung ändert nichts; Platzierungen anderer Sets unberührt.
- Lesen: Zähler `entryCount/inSetCount/placedCount` gegen das aktive Set; `heldByOtherTags` nur mit
  Platzierung in S; `placedByOtherTags`; Reihenfolge nach `CreatedAtUtc`/`Id`; Kanal ohne aktives
  Set → `placedCount 0`, Listen leer.
- Kaskade: Kanal-Purge nimmt Tag, Einträge, Platzierungen mit (Zähl-Assert nach `PurgeAsync`).
- Merge-Guard: Verlierer mit Tag wird verweigert (`ChannelIdentityServiceTests` erweitern).

**Backend — `tests/EmotePurge.Api.Tests`:** `AuthFilterMatrixTests` bekommt je Route eine
`InlineData`-Zeile für 401 anonym und 401 unvollständige Claims; je Gruppe ein Fact für 403
(`CanViewUsageStatsAsync false` → Lesen/Melden 403; `CanManageChannelAsync false` →
Pflegen 403, **Melden aber 200/404**, der Editor-Fall aus E9); 400 `invalid_channel_name` vor jeder
Autorisierung; `invalid_emote_set_id` am Meldungs-Body; Rate-Limit-Policy je Route in
`EmoteRoutePolicyTests`-Manier. `ApiFactory` substituiert `IEmoteTagService` (6.6).
`ValidateSyncImportedVocabulary`-Test: `"tag"` ohne Namen ok, mit Namen `invalid_source_kind`.

**Frontend — Vitest (reine Funktionen und Entscheidungslogik):**

- `shared/tags/tag-play-in.spec.ts`: Partition mit Live-Menge und mit Fallback `inSet`;
  Reihenfolge; `ImportRow`-Abbildung (`name = alias`); leeres `toAdd`; Origin-Form.
- `shared/tags/tag-removal.spec.ts`: alle fünf Zeilenfälle aus 7.2/4 einzeln gepinnt; nicht-im-Set
  nur gezählt; `heldByOtherTags`-Erstes als Grund; `keptIds`-Ableitung inkl. `failed`/`unknown`-Zeilen.
- `emote-usage-filter.spec.ts`: Tag-Dimension zählt in `isAnyActive`, `reset()` löscht sie,
  `apply` filtert nach Schlüsselmenge, Wechsel der Menge wirkt.
- `tag-assign-dialog.spec.ts`: add-only (keine Abwahl), Anlegen hakt an, Feldfehler je Code,
  sequenzielle Aufrufe, Schließwert = Summe. `tag-removal-confirm-dialog.spec.ts`: Zahl im Knopf
  folgt den Checkboxen, 0 → gesperrt mit Grund (`aria-describedby`), Reihenfolge angehakt → nicht
  vorgeschlagen (als dokumentierter Vertrag dieser Spec), Schließwert = `{ checkedIds, keptIds }`.
- `seven-tv-import.service.spec.ts`/`seven-tv-delete.service.spec.ts`: der optionale
  Tag-Report hält `closed` auf, `failed` bietet Retry, ohne Tag-Origin existiert er nicht (`null`);
  Body = `importedKeys`/`doneKeys`; `importOriginSourceChannelName({kind:'tag'})` → `null`.
- `tags-page.spec.ts`: Sperrentscheidungen samt Grund (kein Set, `startLocked`, grober Zeiger,
  kein `canManage`), URL-Wahl des Tags, Leerzustände nach Datenlage.
- `api-error-locales.spec.ts` deckt die fünf Codes automatisch.

**Frontend — E2E (`web/e2e/emote-tags.e2e.spec.ts`, `/api/**` gemockt, 7TV über `mockSevenTvGql`
mit `sevenTvGqlRequestKind` `setRead`/`addEmote`/`removeEmote`; CDN über die `test.ts`-Fixture):**

1. Raster → zwei Zellen markieren → „Tag zuweisen…" → neuen Tag „Stronghold" anlegen → Statusmeldung
   „2 Emotes zu Stronghold hinzugefügt"; Tag-Select erscheint erst jetzt.
2. Tag-Filter wählen → Inline-Zahlen → „Einspielen" → Bestätigungsdialog nennt „1 wird hinzugefügt,
   1 ist schon im Set" (Mock: ein Eintrag nicht im Set) → Lauf → `sync-imported` mit
   `sourceKind: 'tag'` **und** `POST …/placements` mit genau der einen ID.
3. „Ausräumen" → Vorschau: eine Zeile angehakt, eine unangehakt „war schon vorher im Set" → „1
   entfernen" → `removeEmote` → `sync-deleted` → `POST …/placements/removed` mit `removedIds=[…]`,
   `keptIds=[]`; Protokoll-Download angeboten.
4. Tags-Seite: Liste, Detail, dimm „nicht im Set", Umbenennen, Löschen mit Platzierungs-Hinweis.
5. Mobile-Viewport (`touch-mobile`-Muster): Drilldown Liste → Detail → BackLink; keine Lauf-Knöpfe.

Alle Läufe mit `page.clock.install()` **vor** `goto` und `runFor` (CLAUDE.md „Tests"). Kein Test
wartet real. Audit-Szenario für die Tags-Seite (§12) mit `contrastViolations` 0.

## 12. Abhängigkeiten und Reihenfolge

### 12.1 Zeitpunkt

Umsetzung **erst** nach dem bindenden Messlauf (ab 2026-10-08) **und** nach dem Merge von Epic #303
(`feat/emote-sets-200`) auf `main`. Der Branch `feat/201-list-as-selection` steht auf `767b1e3e` und
wird vorher auf den Epic-Stand rebased/gemerged. Keine Berührung der Dev-Datenbank oder eines
zweiten Workers vor dem Messfenster (Memory #69/#73).

### 12.2 Vorbedingungen im Bestand (Plan-Tasks vor dem Feature)

1. **`DeleteProgressSection` extrahieren** aus `MassDeletePanel` (Fortschritt, Protokoll, Restore;
   Präzedenz `RestoreProgressSection`, #253 T9), beide Hostseiten umstellen, bestehende Specs/E2E
   grün. Ohne das kann die Tags-Seite keinen Lösch-Lauf zeigen (9.5).
2. **Delete-Vorprüfungen als Flow** (`shared/seven-tv/delete-flow.ts`: Set-Prüfung, Live-Lesung mit
   `complete`-Pflicht, Set-Wechsel-Abbruch, `startDelete`), vom Panel und vom Tag-Ausräumen
   gemeinsam benutzt — sonst entsteht die zweite Kopie der gefährlichsten Kette der App.
3. **`ImportOrigin` um `'tag'`** erweitern und alle erschöpfenden Verbraucher nachziehen (Memory
   „6 Bruchstellen"): `importOriginSourceChannelName`, `importOriginLeaderboardSort`, Origin-Zeile in
   `import-preview.ts`/`import-confirm-dialog.ts`, Dock-Zusammenfassung, Protokoll-Envelope, Audit-Vokabular
   server-seitig.
4. **Optionale dritte Meldung am Lauf-Record** (E14) in `SevenTvImportService` und `SevenTvDeleteService`,
   inkl. `reportsPending`, Dock-Zeile, Retry, Spec-Fälle.
5. **`EmoteUsageFilter`-Dimension Tag** (E18).
6. **`sevenTvRunLeaveGuard`** aus `usageStatsLeaveGuard` verallgemeinern (9.5).

Danach: Backend (Migration, Entitäten, Service, Endpoints, Tests), dann Frontend (Dialoge, Filter,
Seite, Route, Reiter, E2E), dann DECISIONS-Eintrag (englisch, Regel 3, im Commit der Konvention:
neues `SourceKind`-Vokabular, dritte Meldung am Lauf, Tag-Tabellen ohne Ersteller) und
`docs/UI-Designsprache.md` (§8.7 Dock-Kommandos „Tag zuweisen"/„Aus Tag entfernen", §2.5 Tags-Seite
als zweiter Dock-Host neben Nutzung/Vote-Detail, §8.1 vierter Reiter). `docs/Feature-Ideen-2026-08-01.md`
führt #201 nicht als Idee (nur im Kopf als „berührt") — keine Statuszeile zu pflegen.

### 12.3 Worker

**Keine Worker-Änderung** (E15), nachgeprüft: Tags berühren weder `SevenTvSyncService` noch
`UsageFlushWorker` noch `DataRetentionWorker` — die Kaskade ist FK-basiert (3.2). **Die einzige
Stelle, an der ein Worker-Change entstünde,** ist die Erweiterung der Trockenlauf-Zählung:
`ChannelRetentionCounts` (Core) → `RetentionRunSummaryFormatter` (Worker, `:31-33`). Die Spec
verzichtet darauf; der Betreiber entscheidet in 13.3, ob er die Zählzeile will.

### 12.4 Migration

Eine Migration `AddEmoteTags` (drei Tabellen, Indizes aus 5.1–5.3, FKs mit Cascade). Rein additiv,
also deploysicher in der dokumentierten Reihenfolge (Migration von Hand über den Tunnel, dann
Images; CLAUDE.md „Prod-Migration" — die Befehle bereitet der Umsetzer vor, ausgeführt werden sie vom
Betreiber).

### 12.5 Zweitmeinung

Vor dem Plan: `/codex:adversarial-review --model gpt-6.1-sol` über diese Spec mit Fokus auf E7/5.4
(Besitz- und Übertragungsregel), E14 (Meldung am Lauf-Record) und 7.2/8 (`keptIds`-Ableitung).

---

## 13. Offene Punkte und Widersprüche zum Brainstorming

Nur, was wirklich offen ist oder dem Code widerspricht. Alles andere ist in E1–E25 und den
Festlegungen entschieden.

### 13.1 Widerspruch: „Bestätigen über den bestehenden Lösch-Bestätigungsdialog"

Das Brainstorming sah vor, den Ausräum-Vorschlag „über den bestehenden delete-confirm dialog" zu
bestätigen. **Der bestehende Dialog kann den Vorschlag nicht tragen:** `DeleteConfirmDialogData`
(`shared/seven-tv/delete-confirm-dialog.ts:22-50`) nimmt zwei Namenslisten als Signale, eine Warnung,
Set-Name und `isActiveSet` — keine Zeilen mit Haken, keine Gründe. Ihn vorzuschalten (Vorschau-Dialog,
dann Delete-Dialog) wäre eine Doppelbestätigung und verletzt die Stufung Auslösen → Bestätigen →
Vollziehen (§4.2, `PRODUCT.md` Prinzip 4: eine Bestätigung ist die Zusage). Die Spec legt deshalb
**E19** fest: ein eigener Bestätigungsdialog, der die Bausteine des Delete-Dialogs (Set-Zeile,
Shared-Set-Warnung, leise Sätze, `NamePreviewList` ungecappt für die angehakten) wiederverwendet.
**Zu entscheiden:** E19 bestätigen — oder den bestehenden Dialog um einen optionalen Zeilenmodus
erweitern (dann ändert sich eine Komponente, die zwei Hostseiten in jedem Delete benutzen).

### 13.2 Widerspruch: „Aus der Rasterauswahl taggen" trifft auch nicht-aktive Sets

Das Raster zeigt seit #200 auch nicht-aktive Sets mit Zeilen `live`/`left`/Klasse 3 (3.5). Eine
Auswahl dort ist nicht „im aktiven Set". Die Spec gatet „Tag zuweisen" deshalb auf
`shownSetId() === activeEmoteSetId()` (7.0, 8) und lässt den Server E3 unabhängig prüfen (E23).
**Zu bestätigen:** dieses Gate — die Alternative (Zuweisen aus jeder Set-Ansicht, Server lässt
nicht-aktive aus) würde im nicht-aktiven Set regelmäßig „0 zugewiesen, n übersprungen" produzieren.

### 13.3 Wahl: Trockenlauf-Zählung mit oder ohne Tags

E15 verzichtet auf Tag-Zähler im Aufbewahrungs-Trockenlauf, weil jedes Feld
`RetentionRunSummaryFormatter` im Worker berührt (3.2). Die Kaskade selbst ist unabhängig davon
vollständig. **Zu entscheiden:** so lassen (kein Worker-Change) oder die drei Zähler aufnehmen (eine
Core-Record-Erweiterung, eine Infrastructure-Zählung, eine Worker-Formatzeile — klein, aber ein
Worker-Deploy).

### 13.4 Bewusste Lücke: „Wiederherstellen" nach einem Ausräumen erzeugt keine Platzierung

Der Restore ist ein eigener Lauf ohne Tag-Bezug (3.4). Wer ein Ausräumen per „Wiederherstellen"
zurücknimmt, hat die Emotes wieder im Set, aber ohne Platzierung — beim nächsten Ausräumen stehen sie
unter „war schon vorher im Set" und müssen von Hand angehakt werden (7.2/5 erlaubt das). Eine
Platzierungs-Rückmeldung am Restore wäre eine vierte Meldung am Lauf. **Vorschlag:** in v1 hinnehmen
und im Hinweistext des Restore-Knopfs nach einem Tag-Lauf benennen; Nachläufer-Issue.

### 13.5 Nachläufer (nicht v1, nicht offen — nur notiert)

Tag-Zeile im Sidecar des Rasters (9.1); Drilldown-Dialog auf der Tags-Seite (9.4); persönliche Tags
(5.5); Tag-Export/-Import als Datei; Live-Event `tags.changed` (9.6); Platzierung am Restore (13.4).

### 13.6 Prüfpunkt Betreiber

Ein Blick in die eigene Datenschutzerklärung, ob sie Kanal-Konfigurationsdaten so allgemein nennt,
dass Tags darunter fallen (10). Nach der Herleitung dieser Spec ist keine Änderung nötig; die Prüfung
ist eine Minute und gehört dem, der den Text besitzt.
