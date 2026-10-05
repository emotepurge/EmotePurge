# Emote-Tags: kanalgebundene Tags, die ein Set zeitweise erweitern und wieder aufräumen — Spec

**Datum:** 2026-10-04 · **Status:** **Vierte Fassung** — nach der Opus-Verifikation der dritten Fassung gegen `2db77d26` (ein neuer P2: der REST-Resync ist keine glaubwürdige Quelle für ein Verlassen; dazu Lese-Zeit-Regel statt Sync-Sperre, Uhrquelle, Deadlock-Ordnung, sechs innere Widersprüche) — E8/E33 „rev. 4", E34 neu, Gegenbeispiele 8/9, R4; davor: **Dritte Fassung** — die Entscheidungen E1–E25 des Brainstormings vom 2026-10-04 sind eingearbeitet; die acht Befunde der ersten adversarialen Runde (Codex Sol, gpt-6.1-sol, 2026-10-04: vier P1, vier P2) sind in E2, E5, E7–E11, E15 (markiert „rev. 2") und E26–E32 (neu) umgesetzt; die fünf P2-Befunde der **zweiten Runde** (Platzierungs-Revision, verspätete Einspiel-Meldung gegen die Sync-Invalidierung, überlappende Ausräum-Vorschauen, verlorene Set-ID im Import-Flow, Meldebefugnis ohne 7TV-Schreibrecht) sind mit dem Betreiber entschieden und in E8/E9/E26/E27/E29 (markiert „rev. 3") und E33 (neu) umgesetzt, die Gegenbeispiele stehen nachgerechnet in 5.5; die fünf offenen Punkte der ersten Fassung sind entschieden (Abschnitt 13.0); Restrisiken stehen in 13.1 · **Issue:** #201 (ersetzt die Lesart „Liste als Auswahl", s. [Konzept-Liste-als-Auswahl-2026-10-03.md](../../Konzept-Liste-als-Auswahl-2026-10-03.md)) · **Epic:** #200 (Basis) · **Berührt:** #245 (Selbstbereinigung), #243/#244 (Aufbewahrung), #304 (Set-Lesung), #149 (Duplikat-Push) · **Belegt gegen:** `feat/emote-sets-200` @ `2db77d26` (Hauptworktree `/home/dev/projects/EmotePurge`); der Branch dieser Spec, `feat/201-list-as-selection`, steht auf `767b1e3e` und wird vor der Umsetzung auf den Epic-Stand gezogen.

Diese Spec ist ein Denkwerkzeug des Betreibers und deshalb deutsch; Bezeichner, Routen und
Wire-Felder bleiben englisch. Sie enthält keinen fertigen Code — Verträge, Verhalten, Grenzfälle,
Fallen und Testpflichten. Zeilenangaben sind am 2026-10-04 gegen den oben genannten Stand
nachgeprüft (Pfade ohne Präfix liegen unter `web/src/app/`; C#-Pfade unter `src/`). Kleine,
eindeutige Präzisierungen, die das Brainstorming offen ließ, sind als **Festlegung** markiert und
einzeln kippbar. Die Lieferung ist in drei getrennt geplante und geprüfte Teile geschnitten
(T-A, T-B, T-C — Abschnitt 12).

---

## 0. Auftrag in einem Satz

Ein Kanal bekommt **Tags** (z. B. „Stronghold"), denen Manager im Nutzungsraster Emotes zuweisen;
ein Tag lässt sich mit einem Klick **einspielen** (alle Einträge, die noch nicht im aktiven Set sind,
werden über den vorhandenen Import-Lauf hinzugefügt) und später **ausräumen** (vorgeschlagen wird nur,
was nachweislich dieses Tag hinzugefügt hat; entfernt wird über den vorhandenen Lösch-Lauf) —
gespeichert in EmotePurge, kanalgebunden, für alle Manager des Kanals sichtbar, ohne Datei auf einem
Mod-PC und ohne zweites 7TV-Set.

## 0a. Sicherheitsmodell (Leitprinzip dieser Spec)

**Platzierungen sind ein gut gepflegter Vorschlag, keine Garantie.** Die 7TV-Schreibvorgänge liegen
im Browser (Zero-Knowledge-Token, `Architectur.md`); der Server erfährt von ihnen nur durch Meldungen,
die verloren gehen, verzögert ankommen oder — von einem Berechtigten — falsch sein können. Daraus folgt
für jede Stelle dieser Spec:

1. **Im Zweifel ist eine Zeile unangehakt.** Jede Unsicherheit — fehlende Meldung, unvollständige
   Set-Lesung, fremder Halter, unklare Herkunft — kippt in Richtung „zu wenig entfernen", nie „zu viel".
2. **Der Server räumt Platzierungen aktiv weg, wenn er beobachtet, dass ein Emote das Set verlassen
   hat** (E8 rev. 2): was er nicht mehr beweisen kann, schlägt er nicht vor.
3. **Die letzte Sicherung ist der Mensch:** eine Vorschau mit Grund je Zeile und Einspieldatum, die
   bestätigt werden muss, danach der Rückweg über das Purge-Protokoll (Download, „Wiederherstellen").
   Eine Platzierung ist nie Löschbefugnis — sie ist der Haken, den der Mensch bestätigt oder entfernt.

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

**Ziel (v1, in drei Teilen — Abschnitt 12):**

- Tags je Kanal anlegen, umbenennen, löschen; Emotes aus der Rasterauswahl einem oder mehreren Tags
  zuweisen und wieder herausnehmen (T-B).
- Ein Tag **einspielen**: alle Einträge, die laut vollständiger Live-Lesung nicht im aktiven Set
  sind, in einem Import-Lauf hinzufügen; schon vorhandene werden übersprungen und **nicht** als vom
  Tag hinzugefügt gezählt; das Tag gilt danach in diesem Set als **eingespielt** (T-C).
- Ein Tag **ausräumen**: die vom Tag platzierten Emotes, die laut Live-Lesung noch im Set sind,
  vorschlagen und nach Bestätigung in einem Lösch-Lauf entfernen; alles andere wird gezeigt, aber nicht
  vorgeschlagen; das Tag gilt danach als **nicht eingespielt** — auch wenn nichts zu entfernen war (T-C).
- Zwei Oberflächen: Tag-Filter in der Filterzeile des Nutzungsrasters **und** eine Tags-Seite je
  Kanal (Lesen/Pflegen in T-B, Läufe in T-C).
- Sauberer Lebenszyklus: Kanal-Purge (Admin, Aufbewahrung, später #245) nimmt die Tags mit; der
  7TV-Sync räumt Platzierungen weg, deren Emote das Set verlassen hat; die Kontolöschung (#243) ist
  nicht berührt, weil Tags keinen Kontobezug tragen (Abschnitt 10 sagt ehrlich, was sie tragen).

**Nicht-Ziele (v1, ausdrücklich):**

- Persönliche, kontogebundene Tags (späterer Schritt; die Datenform lässt ihn offen, s. 5.6).
- Tag-Export/-Import als Datei (die Kanal-übergreifende Brücke).
- Emotes taggen, die gerade **nicht** im aktiven Set sind — erst einspielen, dann taggen (E3).
- Tag-Chips auf den Rasterzellen; Tag-Zeile im Sidecar.
- Live-Aktualisierung über SSE, wenn ein anderer Mod Tags ändert (9.6).
- Eine Platzierung durch „Wiederherstellen" (13.2).

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
- **Der REST-Vollsync liest eine veraltete Quelle.** `SevenTvSyncService` holt den Kanalzustand über
  `sevenTvApiClient.GetChannelStateForTwitchUserAsync` (`SevenTvSyncService.cs:103`; REST v3
  `users/twitch/{id}`, `SevenTvApiClient.cs:233/1140`), dessen Cache 10–30 min hinterherhinken kann
  (SevenTV/SevenTV#81; eigene Messung: ein Test-Emote kam erst nach ~10 min an,
  `docs/Untersuchung-7TV-WebSocket-2026-07-30.md:94,98`). Der Code **weiß** das und misst es bereits:
  `ReconcileAsync` loggt „REST-Resync archiviert Emote …, das vor weniger als 15 Minuten synchronisiert
  wurde — möglicher 7TV-REST-Cache-Lag" (`SevenTvSyncService.cs:765-773`, Schwelle 15 min, „measurement
  only (no protection)") und archiviert trotzdem. Ein per EventAPI frisch hinzugefügtes Emote wird vom
  nächsten Vollsync also regelmäßig **fälschlich** archiviert und vom darauffolgenden wieder entarchiviert.
  Das ist der Grund für E34.
- **Archiviert wird an drei Stellen**, alle in `EmotePurge.Infrastructure`: (a) der EventAPI-Delta-Pfad
  `SevenTvSyncService.cs:285-290` (`delta.PulledIds` → `IsArchived = true`, `ArchivedAt`); (b) der
  REST-Vollsync `ReconcileAsync` `:750-790` (Emotes, die im Live-Set fehlen, `:780-781`), aufgerufen aus
  dem Vollsync `:410`; (c) die set-zentrische Meldung `EmoteService.MarkInSetAsync` (`:208ff`,
  Richtung Delete, für jeden Kanal mit `ActiveEmoteSetId == emoteSetId`, `:221`). Entarchiviert wird in
  `UpsertEmote` (`SevenTvSyncService.cs:833`) und in `MarkInSetAsync` Richtung Restore. (a) und (b)
  **laufen im Worker-Prozess** (`EmotePurge.Worker/SevenTvPeriodicResyncWorker.cs:53`, `Worker.cs:196,275`,
  Takt `SevenTv:ResyncIntervalSeconds` Default 60, `SevenTvPeriodicResyncWorker.cs:28`; EventAPI hinter
  `SevenTv:EventApi:Enabled`, `SevenTvEventWorker.cs:19-22`), (c) im Api-Prozess. **Emote-Zeilen
  werden nirgends gelöscht** außer über die Kanal-Kaskade (grep über `src/` ohne Migrationen: kein
  `Emotes.Remove`/`ExecuteDelete`). **`ArchivedAt` wird beim Entarchivieren auf `null` gesetzt**
  (`EmoteService.cs:272`: `emote.ArchivedAt = archive ? now : null`; `SevenTvSyncService.cs:833`) —
  die Spalte sagt nur „ist gerade archiviert seit", nicht „hat das Set je verlassen"; für die Frage
  „wurde nach Zeitpunkt t ein Verlassen beobachtet" taugt sie nicht (E33).
- **Uhrquelle:** jeder Zeitstempel im Sync und in den Services ist `DateTime.UtcNow` der Anwendung
  (`SevenTvSyncService.cs:277,290-291,401,664`; `EmoteService.cs:272`), nie Postgres `now()`
  (Transaktionsbeginn). Api und Worker laufen als zwei Container auf **demselben** VPS-Host und teilen
  dessen Uhr; ein Vergleich eines Api-Zeitstempels (`RegisteredAtUtc`) mit einem Worker-Zeitstempel
  (`LastObservedAtUtc`) ist deshalb ein Vergleich derselben Uhr. Diese Annahme steht in 12.4; wer die
  beiden Prozesse je auf verschiedene Hosts verteilt, braucht NTP auf beiden.
- **Der Sync nimmt keine Sperre und keine explizite Transaktion:** `SevenTvSyncService` und
  `EmoteService.MarkInSetAsync` schreiben mit einem einfachen `SaveChangesAsync` (implizite
  Transaktion, `READ COMMITTED`), ohne `FOR UPDATE` und ohne `BeginTransactionAsync` — anders als
  `PurgeIfInactiveSinceAsync`/`MergeAsync` (3.3). Der Vollsync aktualisiert dabei **auch die
  Kanalzeile** (`ActiveEmoteSetId`, `ActiveEmoteSetCapacity`, `LastSyncedAtUtc`, `LastSyncAttemptAtUtc`,
  `LastSyncFailureReason`; `SevenTvSyncService.cs:388-405`). Beides ist für 5.5 Regel 5/6 maßgeblich:
  eine Sicherheitsregel, die darauf baut, dass der Sync eine gerade laufende Meldung „sieht", hält nicht.
- Das aktive Set eines Kanals ist `Channel.ActiveEmoteSetId` (`EmotePurge.Core/Entities/Channel.cs`,
  `string`, leer = kein Sync bisher), daneben `ActiveEmoteSetCapacity`. Welche Sets der Kanal wann
  aktiv hatte, hält `ChannelEmoteSetObservation` (FK auf `Channel`, Cascade,
  `AppDbContext.cs:74-93`). Ein Set kann in **mehreren** Kanälen aktiv sein (geteilte Sets —
  `MarkInSetAsync` schreibt deshalb je Kanal). **Der Sync beobachtet nur das aktive Set** eines
  Kanals; was in einem nicht-aktiven Set geschieht, sieht er nicht.

### 3.2 Kanal-Purge, Aufbewahrung, Kontolöschung

- Der Admin-Purge `DELETE /api/channels/{channelName}/purge` (`EmotePurge.Api/Endpoints/ChannelEndpoints.cs:292-317`,
  `GlobalAdminAuthorizationFilter`) ruft `ChannelService.PurgeAsync`
  (`EmotePurge.Infrastructure/Services/ChannelService.cs:127-155`): LEAVE publizieren,
  Audit-Eintrag mit **Snapshot-String** `ChannelName` (kein FK — „an FK would have cascaded this
  row away too"), `db.Channels.Remove(channel)`, ein `SaveChangesAsync`. **Die Kaskade ist reine
  FK-Kaskade** über `OnDelete(DeleteBehavior.Cascade)`, keine Liste expliziter Löschungen. Der
  Aufbewahrungs-Purge `PurgeIfInactiveSinceAsync` (`:158ff`) macht dasselbe unter Zeilensperre.
- Die **Zählung** je Tabelle existiert nur für den Trockenlauf der Aufbewahrung:
  `DataRetentionService.CountChannelCascadeAsync`
  (`EmotePurge.Infrastructure/Services/DataRetentionService.cs:396-412`) → Struct `ChannelCascade`
  (`:462ff`) → `ChannelRetentionCounts` (`EmotePurge.Core/Services/IDataRetentionService.cs:117-128`)
  → formatiert im Worker `EmotePurge.Worker/RetentionRunSummaryFormatter.cs:31-33`.
- `RetentionPolicy` (`EmotePurge.Core/Services/RetentionPolicy.cs`) sind Codekonstanten, weil die
  Datenschutzerklärung sie zitiert: Tokens 30 Tage, inaktive Konten 365, beendete Abstimmungen 365,
  Audit-Log 365, deaktivierte Kanäle 180.
- Die Kontolöschung `AccountDeletionService.DeleteAsync` (Infrastructure; Aufrufer `DELETE /api/auth/me`,
  `AuthEndpoints.cs:156ff`, und der Aufbewahrungsdienst) löscht Votes des Kontos, pseudonymisiert
  Audit-Einträge (Actor-Spalten, Target `user`, die Details-Schlüssel `login`, `targetOwnerTwitchLogin`,
  `targetOwnerSevenTvUserId`) und entfernt die `User`-Zeile. **Sie fasst `DetailsJson` nur an den
  bekannten Schlüsseln an** — ein Freitext in einem anderen Details-Feld (etwa ein Tag-Name) bliebe
  stehen. Das ist der Grund für E30.
- Die Datenschutzerklärung liegt **außerhalb des Repos** (`Legal:ContentPath`, read-only gemountet,
  `docs/Operations.md` „Legal pages").
- Kanalzusammenführung: `ChannelIdentityService.MergeAsync`
  (`EmotePurge.Infrastructure/Services/ChannelIdentityService.cs:555-600`) verweigert, wenn der
  Verlierer Emotes hat (`:650-660`); **aufgerufen aus dem Worker** (`TwitchIdentityReconcileWorker.cs:61`).

### 3.3 Routen, Filter, Fehlercodes

- Kanalgebundene Gruppen heißen `/api/channels/{channelName}/…` — der Routenwert heißt
  **`channelName`**, nicht `name` (`UsageStatsEndpoints.cs:19`, `EmoteEndpoints.cs:25`,
  `VoteSessionEndpoints.cs:18`). Jede Gruppe beginnt mit `RequireAuthorization()`, dann
  `ChannelNameValidationFilter` (**zuerst**, 400 `invalid_channel_name` statt 403/404 —
  `EmotePurge.Api/Validation/ChannelNameValidationFilter.cs`), dann der Autorisierungsfilter, dann
  `RequireRateLimiting(...)`. `EmoteSetIdValidationFilter` prüft je Route einen Routen- **oder**
  Query-Wert `emoteSetId` (400 `invalid_emote_set_id`). Normalisiert wird **nicht** im Filter, sondern
  im Service bzw. in `ChannelQueries.LoadChannelAsync` über `ChannelName.Normalize`
  (`EmotePurge.Core/Entities/ChannelName.cs`: `Trim().ToLowerInvariant()`) — Regel 9.
- `UsageStatsAccessAuthorizationFilter` (`EmotePurge.Api/Auth/`): Admin, Broadcaster, Live-Mod
  **oder 7TV-Editor** (`CanViewUsageStatsAsync`); `ChannelManagementAuthorizationFilter`: ohne den
  Editor (`CanManageChannelAsync`). Beide antworten 400 bei ungültigem Namen, 401 ohne Twitch-Principal,
  **403 ohne Body** (`Results.Forbid()`). 404 kommt nie aus einem Filter, nur aus Handlern.
  **Beide Filter sagen nichts über 7TV-Schreibrechte:** `CanViewUsageStatsAsync` gibt für Broadcaster
  und Live-Mod über `CanManageChannelAsync` `true` zurück, **bevor** irgendein 7TV-Grant geprüft wird
  (`EmotePurge.Infrastructure/Services/ChannelAccessService.cs:31,36-40`); ein Twitch-Mod ohne
  7TV-Editor-Recht passiert sie. Das 7TV-Schreibrecht an einem **Set** prüft allein
  `IImportTargetOwnershipService.CheckAsync(twitchUserId, login, emoteSetId, ct, ownerHint)` — die
  Leiter der set-zentrischen Meldungen (`SevenTvEndpoints.PassSyncInSetLadderAsync`, `:663-675`:
  `SetNotFound` → 404 `emote_set_not_found`, `Forbidden` → 403 ohne Body, `Unavailable` → 503
  `foreign_channel_seventv_unavailable`; Hint aus `request.TargetOwnerTwitchId` über `BuildOwnerHint`).
  Das ist der Grund für E9 rev. 3.
- **Serialisierung im Bestand:** wo zwei Schreiber dieselbe Kanalwahrheit ändern, nimmt der Code die
  Kanalzeile mit `SELECT … FOR UPDATE` in einer expliziten Transaktion
  (`ChannelQueries.LoadChannelForUpdateAsync`, `Persistence/ChannelQueries.cs:116-133`; Nutzer
  `PurgeIfInactiveSinceAsync`, `MergeAsync`, `AccountDeletionService` analog auf der User-Zeile) — kein
  `Serializable`-Isolationslevel, keine Anwendungs-Mutexe. Dieselbe Idiomatik übernimmt 6.4.
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
  meldet eine Verweigerung über `noteRefusedStart`. **Es gibt kein `tryAcquire`/`release`**; „laufender
  Lauf" heißt hier `startLocked`.
- **Set-Lesung (#304):** `loadSevenTvSetEntries(httpClient, setId)`
  (`core/seven-tv/seven-tv-set-entries.ts:152`) liest das Set seitenweise direkt bei 7TV (ohne Token)
  und liefert `aliasesById`, `aliaslessIds`, `defaultNameById`, `animatedById`, `occupiedSlots` und
  **`complete`**. `complete` ist `false`, wenn Seitenzahl, `totalCount` oder Mitgliedschaft zwischen
  den Seiten nicht zusammenpassen oder die Verifikations-Zweitlesung abweicht — die
  Verschiebungserkennung aus #304 (`docs/DECISIONS.md:13`, Eintrag 2026-10-03). Wer löscht, verlangt
  `complete` (`mass-delete-panel.ts`, `readLiveAliasesThenDelete`: „a list that only knows half must
  not delete"). **`aliasesById.has(id) || aliaslessIds.has(id)`** ist die Frage „ist diese ID im Set,
  unter irgendeinem Alias" (Doku `:56-78`).
- **7TV dedupliziert ADDs nicht nach ID:** `addEmote` weist nur einen kollidierenden **Alias-String**
  ab und hängt sonst blind an — ein Emote, das unter einem anderen Alias schon im Set ist, wird ein
  zweites Mal gepusht, und 7TV behält das Duplikat (`shared/seven-tv/already-present-filter.ts:31-35`,
  #149). Umgekehrt nimmt **ein `removeEmote` jeden Eintrag der ID** mit, alle Aliasse zugleich
  (`core/seven-tv/seven-tv-delete.service.ts:91-100`). Ein „harmlos scheiternder Doppel-ADD" existiert
  nicht; die erste Fassung dieser Spec hatte das falsch (Codex-Befund 2).
- **Import-Lauf:** `startImportFlow(deps, source, target)` (`shared/seven-tv/import-flow.ts:321`)
  lädt das Ziel, zeigt den Bestätigungsdialog (`import-confirm-dialog.ts`, Vorlesung
  `import-preview.ts`, Konfliktschritt `import-conflict-resolution-step.ts` mit `RowDecision =
  skip | renameSource | replaceTarget | adoptSourceName`, `shared/seven-tv/conflict-resolution.ts:30`),
  holt ggf. das Token, filtert Duplikate live (`filterAlreadyPresent`, `already-present-filter.ts:78`)
  und startet `SevenTvImportService.startImport(target, origin, plan, …)`
  (`core/seven-tv/seven-tv-import.service.ts:472`). **Der Flow verliert eine gewählte Set-ID, wenn sie
  die aktive ist:** `toTargetSelection` bildet eine `'chosen'`-Wahl mit `emoteSetId ===
  activeEmoteSetId` auf `{ kind: 'trackedActive', channelName }` ab — ohne Set-ID
  (`import-flow.ts:143-157`) —, und `loadImportTarget` liest in diesem Zweig den **jetzt** aktiven Set
  des Servers (`core/emotes/import-target-loader.ts:150-174`, `setId: status.value.activeEmoteSetId`);
  weder `start()` noch `recheckTransferPlan` vergleichen gegen eine vom Aufrufer eingefrorene ID. Ein
  Set-Wechsel zwischen Klick und Start landet so still im neuen Set (Codex Runde 2, Befund 4 → E29
  rev. 3). Eingabe ist `ImportSource = { origin: ImportOrigin;
  rows: ImportRow[]; duplicatesCollapsed; discardedRows }` mit `ImportRow = { sevenTvEmoteId; name;
  imageUrl: string | null }` (`core/seven-tv/import-source.ts:9,127`). `ImportOrigin` ist eine
  **erschöpfende** Union (`:45-55`) mit mehreren `switch`-Verbrauchern.
- **Kapazität:** `projectSlots(occupied, capacity, delta)` (`shared/seven-tv/slot-projection.ts:27`)
  liefert eine Projektion samt `overflow`; der Bestätigungsdialog **warnt** damit, **blockt aber nie**
  — ein ADD über die Kapazität scheitert erst bei 7TV als `failed`-Zeile. Ein im Set vergebener Alias
  wird in der Vorlesung als `NameCollisionRow` erkannt und im Konfliktschritt entschieden; ein erst beim
  Schreiben auftretender Konflikt kommt als GQL-409 zurück (`import.errors.nameTakenNow`).
- **Ergebnis eines Laufs:** `RunResult { doneKeys; items; startedAt; finishedAt }`
  (`core/seven-tv/seven-tv-run-engine.ts:244`), Zeilenstatus `pending | in-progress | done | failed |
  cancelled | unknown` (`:76`). Der Import meldet nach dem **Settlement** die Schlüssel aller
  `done`-Zeilen, die ein Emote hinzugefügt haben (`importedKeys`, `seven-tv-import.service.ts:1084`),
  über `reportImported` (`:850`). Ein Abbruch lässt die restlichen Zeilen `cancelled`; `doneKeys`
  enthält genau das bis dahin Gelungene.
- **Meldungen am Lauf:** Jeder Lauf ist ein Record mit `phase: running | settling | reporting |
  closed` (`core/seven-tv/seven-tv-run-lifecycle.ts`); `closed` erst, wenn **jede** Meldung des Laufs
  einen Endzustand hat (`SyncReportState`, `core/seven-tv/sync-report-outcome.ts:19`). Zeitbudget je
  Versuch `REPORT_TIMEOUT_MS` (30 s), bis zu drei Versuche; der Dock bietet bei `failed`/`partial`
  einen Retry. Der Undo-Lauf (#254) hat **zwei** Meldungen am selben Record — der Präzedenzfall für
  eine weitere. **Ein Retry sendet denselben Body noch einmal**; die bestehenden Meldungen sind
  idempotent, weil sie Zeilen in einen Zielzustand setzen — eine Meldung, die „den Rest" bereinigt,
  wäre es nicht (Codex-Befund 5, E27).
- **Lösch-Lauf:** `SevenTvDeleteService.startDelete(setId, channelName, emotes: DeleteQueueEmote[],
  expectedChannelName, targetOwnerTwitchId)` (`core/seven-tv/seven-tv-delete.service.ts:131,188ff`).
  Der Weg davor liegt heute **in der Komponente** `MassDeletePanel` (`shared/seven-tv/mass-delete-panel.ts`):
  `openConfirm()` (Host-Lock, `startLocked`, Token) → `openConfirmDialog()` (Set-Prüfung
  `resolveEditableSet`, Set-Wechsel-Abbruch `massDelete.setChangedDuringConfirm`) →
  `openDeleteConfirmDialog` (`shared/seven-tv/delete-confirm-dialog.ts:231`, Daten
  `DeleteConfirmDialogData` `:22-50`: **Namenslisten als Signale, Warnung, Set-Name — keine Zeilen,
  keine Checkboxen**) → `readLiveAliasesThenDelete` (Live-Lesung, `complete` Pflicht) → `startDelete`.
  Anders als Import, Restore und Undo hat der Delete **keine** `*-flow.ts`-Datei.
- **Protokoll und Rückweg:** `buildPurgeRunProtocol` (`shared/export/purge-run-export.ts`,
  `PURGE_RUN_FORMAT_VERSION = 3`) nimmt jede Zeile des Laufs; angeboten nur als Download aus dem
  Lauf-Panel (`MassDeletePanel.openProtocolExport`). „Wiederherstellen" am beendeten Lauf
  (`mass-delete-panel.ts:299-317`) startet `SevenTvRestoreService.startRestore`; später über die
  Datei-Tür (`FileImportStep` → `parsePurgeRunProtocol` → `startRestoreFlow`). Die Meldung des Delete
  ist `reportDeletedInSet(setId, { sevenTvEmoteIds: doneKeys, expectedChannelName, targetOwnerTwitchId })`
  (`seven-tv-emote-set.service.ts:371`).
- **Lauf-Oberflächen je Seite:** Nutzungsseite: `app-mass-delete-panel` im Dock
  (`usage-stats-page.html:1133-1203`) plus `app-import-progress-section`,
  `app-restore-progress-section`, `app-undo-progress-section`; Vote-Detailseite: dasselbe Panel
  (`vote-session-detail-page.html:173-184`) plus `app-restore-progress-section` (`:193`).
  Fortschritt, Protokoll und Restore-Knopf des Delete stecken **im** `MassDeletePanel`; der Restore
  wurde für #253 (T9) in eine eigene `RestoreProgressSection` gezogen — Präzedenzfall für T-A.

### 3.5 Nutzungsraster, Filterzeile, Dock, Routing

- Filterzeile: `usage-stats-page.html:165-330` — Zeile eins (Zeitraum, Set-Menü nur bei
  `activeEmoteSetId()`, Sortierung), Zeile zwei (Nutzungsbereich, Namensfilter, „Beobachtete
  ausblenden", „Filter zurücksetzen" nur bei `usageFilter.isAnyActive()`, „alle markieren" nur bei
  `showMarkAll()`, Zählzeile `role="status"` mit `ml-auto`, dann vergängliche Meldungen als Paar aus
  dauerhaft gemounteter `sr-only`-Region und `aria-hidden`-Zwilling). Filterzustand
  `EmoteUsageFilter<T>` (`shared/emotes/emote-usage-filter.ts`: `min`, `max`, `nameQuery`,
  `isHideObservedActive`, `isAnyActive`, `apply(items)`, `reset()`).
- Dock: inline `@if (dockVisible() && !isCoarse()) { <div class="app-dock"> … }`
  (`usage-stats-page.html:1073ff`); Markierungshälfte innerhalb `@if (selectedEmoteSetId())`,
  Aktionen projiziert in `[selection-actions]` (`:1161-1203`). §8.7: konstruktiv vor destruktiv.
- `importScopeCurrent()` (`usage-stats-page.ts:1696-1700`) = geladene Zeilen gehören zum Kanal der
  URL **und** `shownSetId() === selectedEmoteSetId()`.
- Auswahl: `new ListSelection(atlasOrder, (emote) => emote.sevenTvEmoteId, emotes)`
  (`usage-stats-page.ts:1238`), Schlüssel = 7TV-ID, `selectMany` add-only; `selectedForDelete`
  (`:1515-1529`) nimmt nur `membership === 'live'`.
- „Nicht mehr im Set" (`membership === 'left'`): Zelle `app-sprite-cell-void` (`:716`), Sprite
  `[dimmed]` (`:763`, `EmoteSprite.dimmed` → `opacity-40`, `shared/emotes/emote-sprite.ts:120`), Badge
  `usageStats.setView.leftBadge`.
- Routing: `channels/:channelName` → `ChannelWorkspaceLayout`; Kinder `usage-stats` (`loadChildren`
  → `usage-stats.routes.ts`, #264; `usageStatsAccessGuard`), `vote-sessions`, `activity`
  (`channelManageGuard`), `vote-sessions/:sessionId` (`app.routes.ts:96-132`). Reiterleiste
  `features/channel-workspace/channel-workspace-layout.ts:103-114`; Rechte aus
  `GET /api/channels/{channelName}/permissions` → `ChannelPermissions { canManage; canViewUsageStats;
  isGlobalAdmin; isTracked; isBotActive }`.
- Kachel-Primitive: `EmoteSprite`, `EmoteSpriteAnimated`, Layout `shared/grid/atlas-grid.ts`
  (`ATLAS_CELL_PX`, `atlasColumns`, `packAtlasRows`), `shared/grid/grid-columns.ts` (`chunkIntoRows`).
  Rasteraufbau ist auf Nutzungs- und Vote-Detailseite inline.

### 3.6 Was aus dem Konzept vom 2026-10-03 trägt

- **Schlüssel ist die 7TV-ID, die Guid ist Nutzlast** (Konzept 2 Punkt 4; Spec #200 6.5/7.2): Tags
  schlüsseln nach `SevenTvEmoteId`, nie nach `Emote.Id` (E22).
- **Invariante `selectedKeys ⊆ keys(Grundmenge)`, `selectMany` add-only** (Konzept 2 Punkt 3): „Tag
  zuweisen" liest `selection.selectedItems()` und ändert die Auswahl nicht.
- **Namensvettern** (Konzept 5, 9/3): ein Name ist bei 7TV keine Identität (192 Fälle im
  Halloween-Set). Tag-Einträge tragen die ID als Schlüssel und den Alias nur als Snapshot; der
  Einspiel-Abgleich läuft über die ID gegen die vollständige Live-Lesung (E28).
- **Die Parser** des Konzepts werden nicht gebraucht; die Abgleichslogik lebt in zwei reinen
  Funktionen (7.1/7.2) mit derselben Haltung: ordinaler ID-Vergleich, Eingabereihenfolge bleibt,
  jede Zahl aus derselben Liste wie die Namen.

---

## 4. Entscheidungen

Jede Zeile: Entscheidung, Begründung in einem Satz, verworfene Alternative. E1–E10 sind
Betreiberentscheidungen aus dem Brainstorming (ggf. „rev. 2" nach der Zweitmeinung); E11–E25 sind
Festlegungen der ersten Fassung; E26–E32 sind die Beschlüsse zur ersten Zweitmeinung, E33 der zur
zweiten, E34 der zur Verifikation der dritten Fassung (Betreiber, 2026-10-04).

| Nr. | Entscheidung | Begründung | Verworfen |
|---|---|---|---|
| **E1** | Tags gehören dem **Kanal**; alle Manager sehen und nutzen sie. Es wird **kein Ersteller** gespeichert. | Der Vertretungs-Mod muss sie finden; ohne Ersteller tragen Tags keinen Kontobezug — die Kontolöschung bleibt unberührt (was sie *sonst* tragen, sagt Abschnitt 10). | Persönliche Tags (v1), Ersteller-Spalte „für später". |
| **E2 rev. 2** | **Ausräumen schlägt nur vor, was nachweislich dieses Tag eingespielt hat; garantiert wird es nicht, weil die 7TV-Schreibvorgänge im Browser liegen.** Vorher schon im Set Vorhandenes (KEKW) wird nicht vorgeschlagen; die Vorschau zeigt je Zeile Grund und Einspieldatum; der Mensch bestätigt. | Platzierungen sind Vorschlag, nicht Löschbefugnis (0a). | „Alle getaggten entfernen"; Platzierung als harter Löschbefehl. |
| **E3** | Getaggt wird **nur aus der Rasterauswahl** des aktiven Sets; ein Emote, das nicht im aktiven Set ist, lässt sich nicht taggen. | Ein Einstieg, eine Grundmenge; die Rasterabfrage kennt ohnehin nur unarchivierte Zeilen (3.1). Preis akzeptiert: erst einspielen, dann taggen. | Taggen aus Datei, aus fremdem Kanal, aus freier ID-Eingabe. |
| **E4** | **Beide** Oberflächen: Tag-Filter in der Filterzeile **und** Tags-Seite je Kanal. | Filter = schneller Weg in der Aufräumsitzung; Seite = Ort, an dem man Tags versteht und pflegt, auch Einträge außerhalb des Sets. | Nur Filter; nur Seite. |
| **E5 rev. 2** | **Variante A: Platzierungen werden explizit gespeichert.** Nach dem Einspiel-Lauf meldet der Browser die **tatsächlich** hinzugefügten 7TV-IDs plus Set-ID **plus Operations-ID** (E27). Eine verlorene Meldung ist fail-safe: die Emotes erscheinen beim Ausräumen unangehakt. | Dasselbe Muster wie `sync-imported` (3.3/3.4); der Server behauptet nichts, was er nicht gemeldet bekam. | **Variante B:** Ableiten aus `FirstSeenAt`. |
| **E6** | Platzierungen und Aktivierungen tragen die **Set-ID**; die eines nicht-aktiven Sets ruhen und gelten wieder, wenn es aktiv ist. Ausräumen wirkt immer auf das **aktive** Set. | Set-Wechsel (Halloween 2026-10-01) ohne Set-ID würde den falschen Stand beschreiben. | Löschen beim Set-Wechsel; set-unabhängig. |
| **E7 rev. 2** | **Übertragung auf Basis der Aktivierung (E26):** Ein von A platziertes Emote X, das ein **anderes, im selben Set eingespieltes** Tag B enthält, bleibt beim Ausräumen von A im Set, wird unangehakt „wird noch von B gebraucht" gezeigt, und seine Platzierung **wandert** zu B. | „Wird noch gebraucht" muss das System wissen; die Aktivierung sagt, welche Tags gerade gelten — auch eines, dessen Einspielen nichts hinzugefügt hat. Die Gegenbeispiele der Zweitmeinung lösen sich damit (5.5). | Übertragung nur an Tags mit eigener Platzierung in S (ließ B ohne Spur, wenn A schon alles hinzugefügt hatte). |
| **E8 rev. 4** | **Der Sync merkt sich ein glaubwürdiges Verlassen — und löscht selbst nichts.** Beobachtet das System glaubwürdig (E34), dass ein Emote das aktive Set verlassen hat, schreibt es im selben `SaveChangesAsync` wie die Archivierung `EmoteSetLeaveObservation (Kanal, 7TV-ID, Set).LastObservedAtUtc = jetzt` (E33). **Platzierungen werden vom Sync nicht mehr gelöscht**: ob eine Platzierung gilt, entscheidet jede **Lesung** (E33 rev. 4); verfallene Zeilen räumt die nächste Einspiel-Meldung (Upsert) oder der Sweep der nächsten Ausräumung ab. **Verhaltensänderung des Worker-Prozesses** (Beobachtung schreiben, Eintrittszeit stempeln). Restlücke: Entfernen + Wiederhinzufügen zwischen zwei Beobachtungen bleibt unsichtbar (13.1 R1). | Ein Sync ohne Sperre kann eine gleichzeitig entstehende Platzierung nicht sehen (3.1); eine Löschung im Sync wäre deshalb nie die Sicherung, nur Aufräumen — und sie zwänge eine Sperrreihenfolge mit der Kanalzeile, die der Sync ohnehin schreibt (Deadlock 40P01). Die Lese-Zeit-Regel braucht beides nicht. | Löschung im Sync (zweite/dritte Fassung); Kanalsperre an den Archivierungsstellen (Kosten im Worker-Hot-Path: jeder Vollsync jedes Kanals nähme eine Zeilensperre und wartete hinter laufenden Meldungen). |
| **E9 rev. 3** | **Rechte:** Lesen hinter `UsageStatsAccessAuthorizationFilter`; Pflegen hinter `ChannelManagementAuthorizationFilter`; **Melden und Registrieren** hinter `UsageStatsAccessAuthorizationFilter` **plus** — im Handler, wie `sync-deleted` — der 7TV-Besitzprüfung `IImportTargetOwnershipService.CheckAsync` für das gemeldete Set (3.3). Folge: melden kann nur, wer das Set bei 7TV auch wirklich schreiben darf; ein Twitch-Mod ohne 7TV-Editor-Recht kann keine Platzierungen erzeugen. | Die Kanalfilter prüfen kein 7TV-Recht (3.3); ohne die Besitzprüfung könnte ein Mod ohne Schreibrecht Haken in die Vorschau eines echten Editors setzen (Runde 2 Befund 5). Mit ihr gilt R3 wieder wörtlich: fälschen kann nur, wer ohnehin direkt löschen könnte. | Nur Kanalfilter (zweite Fassung); Melden hinter Management (Editor-Lauf ohne Papierspur). |
| **E10 rev. 3** | **Meldungen sind idempotent über eine Operations-ID** (E27): eine Wiederholung derselben Operation ändert nichts; eine Ausräum-Meldung berührt nur die in ihrer Vorschau erfassten Platzierungen **in der erfassten Revision**, nie „den Rest". | Retries nach Timeout sind Normalfall (3.4); eine Meldung, die zwischenzeitlich entstandene Platzierungen anderer Operationen löscht, wäre falsch (Runde 1 Befund 5, Runde 2 Befund 1). | Idempotenz nur über Unique-Index (erste Fassung). |
| **E11 rev. 2** | **„Im Set"-Status serverseitig** = unarchivierte `Emote`-Zeile des Kanals (3.1), **immer bezogen auf eine explizit genannte Set-ID** (E29); dient Zählern und Tags-Seite. Der **Vorschlag beim Ausräumen** kommt aus der **Live-Lesung** desselben Sets. | Eine Zahl, eine Quelle; vor einem Löschen zählt nur, was 7TV jetzt sagt. | Status aus der Set-Vorschau-Route (#220). |
| **E12** | **Einspielen ist ein Import-Lauf** über `startImportFlow`/`SevenTvImportService` mit neuem `ImportOrigin` `{ kind: 'tag'; tagId; tagName; channelName }` und neuem `SourceKind` `"tag"` (3.3). Kein neuer `SevenTvRunKind`. | Vorlesung, Konfliktschritt, Kapazitätswarnung, Token, Arbiter, Settlement, Dock existieren. | Eigener Lauf; Origin `'channel'` mit falschem Namen. |
| **E13** | **Ausräumen ist ein Lösch-Lauf** über `SevenTvDeleteService.startDelete`; Purge-Protokoll, Download und „Wiederherstellen" wie bei jedem Delete. | Unwiderruflichkeit ist Produktversprechen (`PRODUCT.md` Prinzip 4); der Rückweg ist gebaut. | Eigener REMOVE-Lauf ohne Protokoll. |
| **E14** | **Die Platzierungsmeldungen sind Meldungen am Lauf-Record** (optional, nur für Tag-Läufe): `tagPlacementReport` (Import), `tagRemovalReport` (Delete), je `SyncReportState`; `closed` wartet, der Dock zeigt Fehler und Retry. Läufe ohne 7TV-Queue (7.1/5, 7.2/7) melden direkt aus dem Flow mit sichtbarem Retry. | „Dasselbe Muster wie die Buchführung" wörtlich; der Undo trägt zwei Meldungen am Record (3.4). | Lose Nachmeldung ohne Retry-Oberfläche. |
| **E15 rev. 2** | **Worker: Quelltext der `EmotePurge.Worker`-Projektdateien unverändert, Verhalten und Binary nicht.** E8 (Beobachtungen und Eintrittsstempel im Sync) und 5.5/8 (Merge-Guard) liegen in `EmotePurge.Infrastructure`, das der Worker ausführt (3.1, 3.2). **Worker-Image muss neu gebaut und zusammen mit der Api deployt werden** (12.4, 12.5). Die Trockenlauf-Zählung wird **nicht** um Tags erweitert (13.0). | Die erste Fassung sagte „kein Worker-Change" und meinte nur den Quelltext; falsch war die Folgerung, der laufende Worker bleibe gleich (Codex-Befund 7). | Zählfelder + Formatter-Zeile. |
| **E16** | **Tag löschen mit Platzierungen ist erlaubt**, mit Hinweis „12 Emotes dieses Tags sind noch eingespielt — vorher ausräumen?"; danach sind sie gewöhnliche Set-Emotes. | Ein Tag ist Organisationsmittel, kein Besitz. | Löschen sperren. |
| **E17** | **Emote aus Tag herausnehmen** löscht auch dessen Platzierungen (alle Sets); das Emote bleibt im Set. | Was nicht mehr zum Tag gehört, kann das Tag nicht mehr ausräumen. | Platzierung behalten. |
| **E18** | **Tag-Filter als Dimension von `EmoteUsageFilter`** (`tagId` + Schlüsselmenge); zählt in `isAnyActive()`, „Filter zurücksetzen" löscht ihn mit. Select nur, wenn der Kanal ≥ 1 Tag hat. | Ein Filter unter Filtern; kein Dauer-Control bei Kanälen ohne Tags. | Eigener Zustand; immer sichtbar. |
| **E19** | **Die Ausräum-Vorschau ist selbst die Bestätigung** (bestätigt 13.0): eigener Dialog aus den Bausteinen des Delete-Dialogs (Set-Zeile, Shared-Set-Warnung, leise Sätze, `NamePreviewList`), mit angehakten/unangehakten Zeilen, Grund und Einspieldatum; kein zweiter Dialog. | §4.2 kennt eine Bestätigung; `DeleteConfirmDialogData` kennt keine Zeilen (3.4). | Doppelbestätigung; Vorschau inline. |
| **E20** | **Grenzen:** 50 Tags je Kanal, Name max. 40 Zeichen, 1000 Einträge je Tag; 409 mit Code; Grund am gesperrten Knopf (§10). | Schutz vor Versehen; HandOfBlood (~900 Emotes) passt in ein Tag. | Keine Grenzen. |
| **E21** | **Namen** je Kanal eindeutig, groß/klein-unabhängig, getrimmt: `Name` + `NormalizedName`, Unique-Index `(ChannelId, NormalizedName)`. Erlaubt: nicht-leer, keine Steuerzeichen, ≤ 40. | Normalisierungshaltung wie Regel 9. | `citext`, Collation. |
| **E22** | **Tags schlüsseln nach `SevenTvEmoteId`**, FK nur auf `Channel`/`EmoteTag` — **kein FK auf `Emote`**. | Regel 8; ein Eintrag lebt länger als die Sichtbarkeit im Raster. | FK auf `Emote.Id`. |
| **E23** | **Alias- und Bild-Snapshot** kommen beim Zuweisen **vom Server** aus der unarchivierten Zeile; IDs ohne Zeile werden ausgelassen und zurückgemeldet (`skippedNotInSetIds`). | Server prüft E3 selbst; Teilerfolg ist ehrlicher als Alles-oder-nichts. | Client liefert; 400. |
| **E24** | **Rate-Limits:** Lesen `InteractiveRead`, Pflegen und Melden `Bookkeeping`. | Melden darf nie verloren gehen — wie `sync-deleted`. | Alles `InteractiveRead`. |
| **E25** | **Tags-Seite als vierter Reiter `tags`**, Guard `usageStatsAccessGuard`, lazy (`tags.routes.ts`, #264). | Gleiche Hierarchiestufe wie Nutzung/Votings (§8.1/§8.6); zieht den Import-Graphen. | Unterseite; Dialog. |
| **E26 rev. 3** | **Aktivierungsmodell:** je `(Tag, Set)` gibt es den Zustand **„eingespielt"** (`EmoteTagActivation`), gesetzt bei **jedem** Einspielen — auch einem, das nichts hinzufügt —, gelöscht bei **jedem** abgeschlossenen Ausräumen — auch einem mit leerer REMOVE-Queue. Ausräumen ist nur für ein eingespieltes Tag anbietbar und **immer abschließbar**. **Invariante, serverseitig erzwungen: ein inaktives Tag hält in diesem Set keine Platzierung** — die Deaktivierung räumt **alle** Platzierungen `(Tag, ·, Set)` ab, auch solche, die nach der Vorschau hereingewandert sind (sie durchlaufen dieselbe Übertragungsregel). | Platzierungen allein bilden den geteilten Lebenszyklus nicht ab (Runde 1 Befund 6); zwei überlappende Ausräumungen hinterließen sonst Platzierungen bei einem inaktiven Halter (Runde 2 Befund 3; 5.5 Gegenbeispiel 7). | Nur Platzierungen; „eingespielt" = `placedCount > 0`; Deaktivierung nur über den Snapshot (zweite Fassung). |
| **E27 rev. 3** | **Operations-ID, vorab registriert:** jeder Einspiel- und Ausräum-Vorgang erzeugt im Browser eine UUID (`crypto.randomUUID()`) und **registriert sie vor dem Lauf** beim Server (`POST …/operations`: Art, eingefrorene Set-ID; der Server stempelt `RegisteredAtUtc`). Jede Meldung muss sich auf eine registrierte, noch nicht angewandte Operation **desselben Sets** beziehen; Wiederholungen werden ignoriert. Die Ausräum-Meldung nennt die erfassten Platzierungen explizit **mit ihrer Revision** (5.3). | Erst die Registrierung gibt dem Server einen Zeitpunkt, gegen den er eine verspätete Einspiel-Meldung mit einem danach beobachteten Verlassen vergleichen kann (E33); zugleich entfällt das nachträgliche Anlegen der Operationszeile und der Set-Bezug ist vor dem ersten 7TV-Schreiben fest (Runde 2 Befunde 1, 2, 4). | Operation erst mit der Meldung anlegen (zweite Fassung); Sequenznummern. |
| **E28 neu** | **Einspielen partitioniert nach 7TV-ID gegen eine `complete` Live-Lesung**; eine unvollständige Lesung **blockt** das Einspielen. Eine ID, die unter irgendeinem Alias im Set ist (auch aliaslos), wird übersprungen und bekommt keine Platzierung. | 7TV dedupliziert ADDs nicht nach ID (3.4); ein Doppel-ADD wäre ein zweiter Eintrag, den ein REMOVE später mitnimmt. Restrisiko: Fremdeditor zwischen Lesung und Schreiben (13.1 R2). | Partition über `inSet` des Servers; Warnen statt Blocken (erste Fassung). |
| **E29 rev. 3** | **Set-Bindung von Anfang bis Ende:** Einträge- und Platzierungslesungen nehmen die Ziel-Set-ID **explizit** (`emoteSetId`) und geben sie zurück; beide Flows frieren sie beim Klick ein und registrieren sie mit der Operation (E27). **Der Import-Flow bekommt für Tag-Läufe eine angepinnte Set-ID** (`ImportFlowTarget` `{ kind: 'chosen', choice, pinSetId: true }`): `toTargetSelection` nimmt dann **nie** den `trackedActive`-Schnellweg, sondern `trackedSet` mit genau dieser ID; nach der Zielauflösung **und** unmittelbar vor dem Start vergleicht der Flow `targetState.setId`, `activeEmoteSetId()` der Seite und die eingefrorene ID und bricht bei Abweichung ab. Beide Meldungen tragen die registrierte ID; der Server weist eine Meldung mit anderer ID zurück. | Flags ohne Set-Bezug könnten im falschen Set gelesen werden (Runde 1 Befund 4); der heutige Schnellweg wirft die ID weg und liest den jetzt aktiven Set (3.4; Runde 2 Befund 4). | „Aktives Set des Servers" implizit (erste Fassung); `trackedActive`-Schnellweg für Tag-Läufe (zweite Fassung — falsch beschrieben als „Flow prüft sein Ziel selbst"). |
| **E30 neu** | **Audit-Details tragen nur `tagId`, nie `tagName`.** | Tag-Namen sind Freitext, der eine Person benennen kann; eine Kopie in actor-gebundenen Audit-Zeilen fiele unter die 365-Tage-Frist und würde von der Kontolöschung nicht angefasst (3.2). Mit nur der ID ist die Audit-Zeile nach dem Tag-Löschen inhaltsfrei. | `tagName` in den Details (erste Fassung). |
| **E31 neu** | **Freigabevoraussetzung:** Der Betreiber prüft vor dem Release von T-B seine Datenschutzerklärung darauf, ob Kanal-Inhalte dieser Art (vom Manager eingegebene Bezeichner, 180-Tage-Bindung an den Kanal) abgedeckt sind, und ergänzt sie ggf. | Die erste Fassung behauptete „keine Personendaten"; richtig ist „kein Kontobezug, aber Freitext". | Prüfpunkt ohne Verbindlichkeit. |
| **E32 neu** | **Lieferung in drei Teilen** (12.1): T-A Extraktion `DeleteProgressSection`/`delete-flow` (reiner Refactor), T-B Tags CRUD + Zuweisen + Tags-Seite lesend + Filter + Aufbewahrung/Datenschutz, T-C Einspielen/Ausräumen mit Aktivierung, Platzierungen, Sync-Beobachtungen, Operations-IDs, Worker-Rollout. | Jeder Teil ist allein prüfbar und mergebar; T-C ist der einzige mit Sync-Verhaltensänderung. | Ein Plan, ein PR. |
| **E33 rev. 4** | **Beobachtetes Verlassen wird gespeichert und bei jeder Lesung angewandt:** `EmoteSetLeaveObservation (ChannelId, SevenTvEmoteId, SevenTvEmoteSetId, LastObservedAtUtc)`, Upsert an den glaubwürdigen Archivierungsstellen (E34). **Lese-Zeit-Regel:** eine Platzierung **gilt** nur, wenn keine Beobachtung `(Kanal, X, Set)` jünger ist als `RegisteredAtUtc` der Operation, die die Platzierung zuletzt anlegte oder übertrug (`Placement.OperationId → Operation.RegisteredAtUtc`); alle Leserouten, Zähler und die Ausräum-Vorschau sehen nur geltende Platzierungen. Eine Einspiel-Meldung legt eine Platzierung für X **nur** an, wenn keine Beobachtung jünger als ihre `RegisteredAtUtc` existiert; sonst wird X **endgültig** verworfen (`discardedStaleIds`). | Mit der Regel an der Lesung gewinnt die Beobachtung **ohne** dass der Sync etwas sehen oder sperren muss (Runde 2 Befund 2; Verifikation: Rennen Sync ↔ Meldung unter `READ COMMITTED`, 5.5 Gegenbeispiel 9). `ArchivedAt` scheidet aus (wird beim Entarchivieren gelöscht, 3.1); eine Spalte auf `Emote` scheidet aus (set-bezogene Frage). | Löschung im Sync als Sicherung (dritte Fassung); `ArchivedAt` als Marke; Spalte auf `Emote`; Tombstones. |
| **E34 neu** | **Glaubwürdigkeit eines Verlassens:** EventAPI-Delta (3.1 a) und unsere eigene set-zentrische Lösch-Meldung (3.1 c) sind **immer** glaubwürdig. Der **REST-Vollsync** (3.1 b) ist es **nur**, wenn die Zeile das aktive Set nicht innerhalb der letzten **30 Minuten** betreten hat: neue Spalte `Emote.LastEnteredSetAtUtc` (gestempelt beim Anlegen der Zeile und bei jedem Entarchivieren — `UpsertEmote`, `MarkInSetAsync` Richtung Restore); ist sie jünger als `now − 30 min`, archiviert der Vollsync die Zeile wie heute, schreibt aber **keine** Beobachtung. | Der REST-Cache hinkt dokumentiert 10–30 min (SevenTV#81, 3.1); die bestehende 15-min-Schwelle ist ausdrücklich nur Messung. Die Standardnutzung (wöchentlich: archivierte Stronghold-Zeilen → Einspielen → EventAPI entarchiviert → stale Vollsync archiviert erneut) würde sonst jede Platzierung sofort verfallen lassen — fail-safe, aber das Feature wäre im Normalfall wertlos. 30 min ist das dokumentierte obere Ende: ein zu kurzes Fenster kostet das Feature, ein zu langes verzögert nur die Erkennung einer echten manuellen Entfernung bei ausgefallener EventAPI (R1). | 15 min (die Messschwelle — untere Kante, würde den belegten ~10-min-Fall gerade so, den 30-min-Fall nicht abdecken); `LastSyncedAt` als Marke (ändert sich auch bei Umbenennungen, weitet R1 unnötig); REST-Verlassen ganz ignorieren (dann sähe ein Kanal ohne EventAPI nie ein Verlassen). |

---

## 5. Datenmodell

**Fünf Tag-Tabellen** (`EmoteTag`, `EmoteTagEntry`, `EmoteTagPlacement`, `EmoteTagActivation`,
`EmoteTagOperation`) **plus eine Kanal-Tabelle** (`EmoteSetLeaveObservation`, geschrieben für jeden
Kanal, ob er Tags hat oder nicht) **plus eine Spalte** (`Emote.LastEnteredSetAtUtc`, E34) — alle
kanalgebunden, alle über FK-Kaskade am Kanal (3.2). Zwei EF-Migrationen (`AddEmoteTags` in T-B: 5.1,
5.2; `AddEmoteTagPlacements` in T-C: 5.3, 5.4, `EmoteSetLeaveObservation`, `Emote.LastEnteredSetAtUtc`),
in Produktion von Hand vor dem jeweiligen Deploy (CLAUDE.md „Prod-Migration").

### 5.1 `EmoteTag` (T-B)

| Spalte | Typ | Bedeutung |
|---|---|---|
| `Id` | `long` (Identity) | PK. Erscheint in Routen (`{tagId}`) und als einzige Tag-Referenz im Audit (E30). |
| `ChannelId` | `string` | FK → `Channel.Id`, `OnDelete(Cascade)`. |
| `Name` | `string(40)` | Anzeigename, getrimmt. **Freitext** (Abschnitt 10). |
| `NormalizedName` | `string(40)` | `Trim().ToLowerInvariant()` von `Name`; Unique-Index `(ChannelId, NormalizedName)` (E21). |
| `CreatedAtUtc` | `timestamptz` | Reihenfolge auf der Seite und Tie-Break (5.5 Regel 3). **Kein** Ersteller (E1). |

Limit 50 je Kanal (E20), geprüft im Service unter der Zeilensperre des Kanals (`LoadChannelForUpdateAsync`,
Muster `PurgeIfInactiveSinceAsync`).

### 5.2 `EmoteTagEntry` (T-B)

| Spalte | Typ | Bedeutung |
|---|---|---|
| `TagId` | `long` | FK → `EmoteTag.Id`, `OnDelete(Cascade)`. |
| `SevenTvEmoteId` | `string(32)` | Schlüssel (E22). 7TV-IDs sind 26-stellige ULIDs, nicht 24 Zeichen; T-B legt die Spalte als `varchar(32)` an (Betreiberentscheidung 2026-10-05). |
| `Alias` | `string` | Snapshot von `Emote.Name` beim Zuweisen (E23); beim Einspielen der ADD-Alias. |
| `ImageUrl` | `string` | Snapshot von `Emote.ImageUrl`, damit die Tags-Seite Einträge ohne Rasterzeile zeichnet. |
| `AddedAtUtc` | `timestamptz` | Reihenfolge auf der Tags-Seite. |

PK/Unique `(TagId, SevenTvEmoteId)`. Limit 1000 je Tag (E20). Kein FK auf `Emote` (E22).

### 5.3 `EmoteTagPlacement` (T-C)

„Dieses Tag hat dieses Emote in dieses Set gebracht — soweit uns gemeldet."

| Spalte | Typ | Bedeutung |
|---|---|---|
| `TagId` | `long` | FK → `EmoteTag.Id`, `OnDelete(Cascade)`. |
| `SevenTvEmoteId` | `string(32)` | Das Emote (26-stellige ULID, daher 32 wie in 5.2). |
| `SevenTvEmoteSetId` | `string(24)` | Das Set (E6). Formprüfung wie `EmoteSetIdValidation`. |
| `PlacedAtUtc` | `timestamptz` | Zeitpunkt der Einspiel-Meldung, die das Emote **zuletzt hinzugefügt** hat; in der Vorschau als „eingespielt am" sichtbar (E2 rev. 2). **Bei einer Übertragung bleibt er stehen** — der Mensch fragt „seit wann ist X wegen eines Tags im Set", und das ändert ein Besitzerwechsel nicht; die Gültigkeitsprüfung (E33) läuft nicht über diese Spalte, sondern über die Registrierung der Operation in `OperationId`. **Bei einer Einspiel-Meldung wird er immer überschrieben**, auch wenn die Platzierung schon existierte (5.5 Regel 10). |
| `OperationId` | `uuid` | **Die Revision der Platzierung:** die Operation, die sie zuletzt anlegte **oder übertrug** (E27 rev. 3). Wird bei jeder Übertragung auf die ausräumende Operation gesetzt. Eine Ausräum-Meldung berührt eine Platzierung nur, wenn deren `OperationId` der in der Vorschau gelesenen entspricht (6.4). |

PK/Unique `(TagId, SevenTvEmoteId, SevenTvEmoteSetId)`. Index `(SevenTvEmoteSetId, SevenTvEmoteId)`
für „wer hält X in S" und für die Lese-Zeit-Regel (5.5 Regel 5). **Kein FK auf Entry** (E17
löscht explizit im Service). Eine gesonderte Revisionsspalte ist unnötig: jede Änderung einer
Platzierung geschieht durch genau eine Operation, deren ID ohnehin gespeichert wird — sie **ist** die
Revision (Festlegung).

### 5.4 `EmoteTagActivation` und `EmoteTagOperation` (T-C)

**`EmoteTagActivation`** — „Tag T gilt in Set S als eingespielt" (E26):

| Spalte | Typ | Bedeutung |
|---|---|---|
| `TagId` | `long` | FK → `EmoteTag.Id`, Cascade. |
| `SevenTvEmoteSetId` | `string(24)` | |
| `ActivatedAtUtc` | `timestamptz` | Letztes Einspielen. |
| `OperationId` | `uuid` | Die Operation des letzten Einspielens. |

PK `(TagId, SevenTvEmoteSetId)`. Existenz der Zeile = eingespielt; Löschung = ausgeräumt.

**`EmoteTagOperation`** — registrierte und angewandte Operationen (E27 rev. 3):

| Spalte | Typ | Bedeutung |
|---|---|---|
| `OperationId` | `uuid` | PK, vom Browser erzeugt. |
| `TagId` | `long` | FK → `EmoteTag.Id`, Cascade. |
| `Kind` | `string` | `"playIn"` oder `"removal"` (Konstanten wie `ChannelEmoteSetObservationClosedBy`). |
| `SevenTvEmoteSetId` | `string(24)` | Die beim Registrieren eingefrorene Set-ID (E29). |
| `RegisteredAtUtc` | `timestamptz` | **Serverzeit** der Registrierung — der Vergleichspunkt für E33. Nicht die Browserzeit. |
| `AppliedAtUtc` | `timestamptz?` | `null`, bis die Meldung angewandt wurde. |

Eine Meldung ohne registrierte Operation wird abgewiesen, eine mit `AppliedAtUtc ≠ null` nicht noch
einmal angewandt (6.4). Operationen leben so lange wie ihr Tag (Kaskade); nie angewandte Registrierungen
(Lauf abgebrochen vor dem ersten Schreiben, Tab geschlossen) bleiben als Zeilen stehen — eine Zeile je
Klick ist vernachlässigbar, und eine Frist für „verwaist" brächte eine neue Aufräumpflicht ohne Nutzen
(Festlegung).

**`EmoteSetLeaveObservation`** — „zuletzt beobachtet, dass X das Set S des Kanals verlassen hat" (E33):

| Spalte | Typ | Bedeutung |
|---|---|---|
| `ChannelId` | `string` | FK → `Channel.Id`, Cascade. |
| `SevenTvEmoteId` | `string(32)` | 26-stellige ULID, daher 32 wie in 5.2. |
| `SevenTvEmoteSetId` | `string(24)` | Das Set, in dem das Verlassen beobachtet wurde (5.5 Regel 5). |
| `LastObservedAtUtc` | `timestamptz` | Serverzeit der Beobachtung; Upsert überschreibt. |

PK `(ChannelId, SevenTvEmoteId, SevenTvEmoteSetId)`. Geschrieben an den **glaubwürdigen**
Archivierungsstellen (E34: a und c immer, b nur außerhalb des 30-min-Fensters) als Entity-Upsert im
selben `SaveChangesAsync` wie die Archivierung, **unabhängig davon, ob der Kanal Tags hat** — die
Zeile muss schon existieren, wenn eine verspätete Meldung kommt, und der Sync kann nicht wissen, welche
Meldung noch unterwegs ist. **Keine Tag-Tabelle**, sondern Kanal-Beobachtung: sie fällt mit dem
Kanal-Purge (Kaskade), nicht mit einem Tag. Gelesen von jeder Platzierungslesung (6.2) und von der
Einspiel-Meldung (6.4). **Keine Pflege nötig:** der Schlüssel ist eindeutig je `(Kanal, Emote, Set)`,
ein Upsert überschreibt — die Tabelle wächst nicht mit jedem Archivierungsereignis, sondern ist durch
die Zahl der Emotes mal je besuchter Sets des Kanals beschränkt (HandOfBlood: einige tausend Zeilen) und
kaskadiert mit dem Kanal. Eine Frist wäre eine zweite Aufräumpflicht ohne Wirkung auf die Größe
(Festlegung). Kein FK auf `Emote` (E22-Haltung: die Frage überlebt die Rasterzeile).

**`Emote.LastEnteredSetAtUtc`** (`timestamptz?`, E34): wann die Zeile zuletzt als Mitglied des aktiven
Sets **begonnen** hat — gestempelt beim Anlegen (`UpsertEmote`, neue Zeile) und bei jedem Entarchivieren
(`UpsertEmote` `SevenTvSyncService.cs:833`, `MarkInSetAsync` Richtung Restore), nie gelöscht. `null`
für Bestandszeilen vor der Migration = „unbekannt" → ein REST-Verlassen gilt dann als glaubwürdig (die
Zeile ist sicher älter als 30 min). Gelesen nur in `ReconcileAsync` (3.1 b).

### 5.5 Invarianten, Regeln, Gegenbeispiele

1. **Ein Eintrag ohne Zeile ist erlaubt.** Ein getaggtes Emote kann aus dem Set verschwinden; der
   Eintrag bleibt und ist „nicht im Set" (9.4).
2. **Eine Platzierung ohne Eintrag ist nicht erlaubt** (Service-Regel: Einspiel-Meldungen platzieren
   nur IDs mit Eintrag; E17 löscht Platzierungen mit dem Eintrag).
3. **Mehrere Halter einer Platzierung `(·, X, S)`** sind möglich (zwei Einspiel-Läufe fast
   gleichzeitig; 7TV lässt beide ADDs durch — 3.4). Der Vorschlag behandelt sie deterministisch: als
   **Besitzer** gilt das älteste Tag (`CreatedAtUtc`, dann `Id`); die anderen gelten als „wird noch
   gebraucht". Hinweis: in diesem Fall liegt X **zweimal** im Set, ein REMOVE nimmt beide.
4. **Übertragung (E7 rev. 2) und Deaktivierungs-Sweep (E26 rev. 3):** Beim Ausräumen von T in S
   wandert eine nicht entfernte, laut Live-Lesung noch vorhandene Platzierung `(T, X, S)` zu dem
   anderen Tag T′ des Kanals, das (a) **aktiv in S** ist (`EmoteTagActivation (T′, S)` existiert) und
   (b) einen Eintrag für X hat; bei mehreren T′ das älteste (Regel 3). Hat T′ bereits `(T′, X, S)`,
   wird `(T, X, S)` nur gelöscht. **Gibt es kein T′, wird die Platzierung gelöscht** — ausgeräumt heißt:
   T hält in S nichts mehr. Die Regel gilt **für jede Platzierung von T in S, die beim Anwenden der
   Meldung existiert** — die im Snapshot erfassten nach dem Urteil der Vorschau (entfernt / behalten /
   veraltet), die **nicht** erfassten (nach der Vorschau hereingewandert oder neu platziert) wie
   „behalten": Übertragung an ein aktives T′ oder Löschung. Nach der Deaktivierung gilt
   **`(T, S) inaktiv ⇒ keine Platzierung (T, ·, S)`** als serverseitig geprüfte Invariante (Test 11).
   Der Mensch hat X behalten; es ist ab jetzt „war schon vorher im Set". **Festlegung** (13.4/2).
5. **Beobachtung statt Invalidierung (E8 rev. 4, E33 rev. 4, E34):** An den glaubwürdigen
   Archivierungsstellen schreibt der Sync im selben `SaveChangesAsync` wie die Archivierung
   `EmoteSetLeaveObservation (Kanal, SevenTvEmoteId, S) = jetzt`, wobei S = das Set, in dem das
   Verlassen beobachtet wurde: bei (a) und (b) `Channel.ActiveEmoteSetId` des gerade synchronisierten
   Kanals, bei (c) die `emoteSetId` der Meldung. **Er löscht keine Platzierung und nimmt keine Sperre.**
   Glaubwürdig ist (a) immer, (c) immer, (b) nur für Zeilen mit `LastEnteredSetAtUtc` älter als 30 min
   oder `null` (E34). **Lese-Zeit-Regel:** eine Platzierung `(T, X, S)` **gilt**, wenn keine
   Beobachtung `(Kanal, X, S)` mit `LastObservedAtUtc > RegisteredAtUtc` der Operation in
   `Placement.OperationId` existiert. Nur geltende Platzierungen erscheinen in 6.2 (`placedByThisTag`,
   `placedByOtherTags`, `placedCount`), nur geltende kommen in einen Snapshot; eine nicht geltende Zeile
   ist **verfallen** und wird vom nächsten Einspiel-Upsert überschrieben oder vom Sweep der nächsten
   Deaktivierung abgeräumt (6.4). Die **Aktivierung** bleibt von Beobachtungen unberührt. Platzierungen
   **nicht-aktiver** Sets bekommen vom Sync nie eine Beobachtung (3.1) — sie werden erst beim nächsten
   Ausräumen gegen die Live-Lesung geprüft (R1). **Warum die Regel an der Lesung hängt:** der Sync läuft
   ohne Sperre unter `READ COMMITTED` (3.1); eine Meldung, die gleichzeitig eine Platzierung einfügt oder
   überträgt, ist für ihn unsichtbar — eine Löschung im Sync könnte sie verfehlen (Gegenbeispiel 9). An
   der Lesung ist das egal: wer immer die Platzierung schrieb, die Beobachtung ist jünger als deren
   Registrierung und gewinnt. **Uhr:** beide Zeitstempel sind `DateTime.UtcNow` von Api bzw. Worker auf
   demselben Host (3.1).
6. **Serialisierung der Meldungen, Sperrreihenfolge:** Jede Meldung (Einspielen, Ausräumen) und jede
   Tag-Löschung läuft in einer Transaktion, die zuerst die **Kanalzeile** mit `FOR UPDATE` nimmt
   (`LoadChannelForUpdateAsync`, 3.3) und danach nur Tag-Tabellen **schreibt** und
   `EmoteSetLeaveObservation` **liest**. Der Sync schreibt `Emotes`, `EmoteSetLeaveObservation` und die
   Kanalzeile (als gewöhnliches `UPDATE`, 3.1) und schreibt **keine** Tag-Tabelle. Damit gibt es keinen
   Zyklus: der Sync wartet höchstens auf die Kanalzeile einer laufenden Meldung, nie umgekehrt — das
   Deadlock-Muster 40P01 (Meldung hält Kanal und will eine Zeile, die der Sync hält, während der Sync die
   Kanalzeile will) ist konstruktiv ausgeschlossen, weil der Sync keine Zeile hält, die eine Meldung
   braucht. Zwei überlappende Ausräumungen (Gegenbeispiel 7) sehen einander über die Kanalsperre. **Retry-
   Regel:** schlägt eine Meldung trotzdem mit einem Serialisierungs- oder Deadlock-Fehler fehl (SQLSTATE
   40001/40P01, etwa durch eine künftige Codeänderung), ist das ein transienter Fehler wie ein Timeout —
   der Lauf-Record zeigt `failed` mit Retry, die Wiederholung trägt dieselbe `operationId` und ist durch
   E27 harmlos. Eine Sperre je `(Kanal, Set)` wäre feiner, bräuchte aber eine eigene Sperrzeile; die
   Kanalzeile ist die bestehende Idiomatik und die Meldungen sind selten (zwei je Stream). **Festlegung.**
7. **Regel 10** bei den Zählern (6.2): Gruppieren nur über die FK-Spalte einer Tabelle, Filter über
   skalare ID-Listen; der „im Set"-Join läuft über eine vorher materialisierte Menge der unarchivierten
   `SevenTvEmoteId`s des Kanals.
8. **Kanalzusammenführung** (3.2): Tags zählen beim Merge-Guard wie Emotes — hat der Verlierer Tags,
   wird verweigert (eine Bedingung in `MergeAsync`; **läuft im Worker**, E15 rev. 2). **Festlegung.**
9. **Set-Wechsel ändert keine Tabelle:** Aktivierungen und Platzierungen des alten Sets bleiben
   („ruhen", E6).
10. **Ein Einspiel-Upsert überschreibt immer** `OperationId` **und** `PlacedAtUtc` — auch dann, wenn die
   Platzierung schon existierte (`alreadyRecordedCount`). Sonst hielte eine alte, verfallene Zeile ihre
   alte Revision, ein verspäteter alter Ausräum-Bericht träfe sie noch (Gegenbeispiel 5 in der Variante
   „Verlassen vor der Beobachtung, Wiederhinzufügen, dann die alte Meldung"), und die Lese-Zeit-Regel
   hielte sie weiter für verfallen, obwohl sie gerade neu gemeldet wurde. Die Übertragung setzt dagegen
   nur `OperationId` (5.3).

**Gegenbeispiele der Zweitmeinung, nachgerechnet mit E26/E7 rev. 2** (A und B sind Tags desselben
Kanals, S das aktive Set, X/Y Emotes):

- *1. „B spielt nur Emotes ein, die A schon hinzugefügt hat — B hinterlässt keine Spur."* A = {X, Y}
  eingespielt → Platzierungen (A, X, S), (A, Y, S), Aktivierung (A, S). B = {X} eingespielt →
  Live-Lesung sagt X ist im Set → `toAdd` leer → **kein Lauf, aber Aktivierung (B, S)** und
  Operation. Ausräumen von A: X ist von A platziert, B ist aktiv in S und enthält X → **unangehakt
  „wird noch von B gebraucht"**, Y angehakt. Nach Bestätigung: Y entfernt, (A, X, S) wandert zu
  (B, X, S), (A, S) gelöscht. Ausräumen von B: X von B platziert, kein anderes aktives Tag → angehakt →
  entfernt, (B, S) gelöscht. **Gelöst.**
- *2. „A und B beide {X, Y}; A eingespielt, B eingespielt (no-op) — beim Ausräumen null angehakte
  Zeilen, Sackgasse."* Ausräumen von A: beide Zeilen unangehakt „wird noch von B gebraucht" → Knopf
  „Ausräumen" bleibt **aktiv** (E26: mit leerer REMOVE-Queue abschließbar) → kein Lösch-Lauf,
  Meldung direkt: beide Platzierungen wandern zu B, (A, S) gelöscht. Ausräumen von B: X, Y von B
  platziert, kein anderes aktives Tag → beide angehakt → entfernt. **Gelöst, keine Sackgasse.**
- *3. „Verzögerte Ausräum-Meldung löscht Platzierungen eines neueren Einspielens (anderes Emote Z)."* Ausräumen von A
  (Snapshot {X, Y}) hängt im Retry; inzwischen spielt jemand A erneut ein → Platzierung (A, Z, S),
  Aktivierung (A, S) erneuert mit neuer Operation. Die verspätete Meldung berührt **nur** {X, Y} (E27)
  und löscht die Aktivierung (A, S) nur, wenn deren `OperationId` noch die ist, die die Vorschau gelesen
  hat — **Festlegung:** die Meldung trägt die beim Vorschau-Laden gelesene `activationOperationId` mit,
  und der Server deaktiviert nur bei Übereinstimmung. Z bleibt platziert, A bleibt aktiv. **Gelöst.**
- *4. „Wiederholte Einspiel-Meldung legt Platzierungen neu an."* Zweite Meldung mit derselben
  `OperationId` → `AppliedAtUtc` gesetzt → ignoriert (`replayed: true`). **Gelöst.**

**Gegenbeispiele der zweiten Runde, nachgerechnet mit E26/E27/E29 rev. 3 und E33:**

- *5. „Verspätete Ausräum-Meldung löscht eine neue Platzierung desselben Emotes X."* Ausräumen von
  A (Operation R₁, Snapshot enthält X mit Revision P₁ = Operation des Einspielens) hängt im Retry;
  inzwischen: X wird entfernt (Sync löscht (A, X, S) und merkt das Verlassen), A wird erneut
  eingespielt (Operation P₂, X wird hinzugefügt → neue Platzierung (A, X, S) mit `OperationId = P₂`,
  Aktivierung mit P₂). Die verspätete Meldung R₁ nennt X **mit Revision P₁**; die vorhandene
  Platzierung trägt P₂ → **kein Treffer, unberührt**; Deaktivierung nur bei `activationOperationId =
  P₁` → trägt P₂ → **bleibt aktiv**. Der Sweep (Regel 4) läuft **nur**, wenn deaktiviert wird — hier
  nicht. **Gelöst** (die zweite Fassung hatte in Gegenbeispiel 3 nur ein anderes Emote Z betrachtet).
- *6. „Verspätete Einspiel-Meldung unterläuft die Sync-Invalidierung."* Einspielen (Operation P,
  registriert um t₀); X wird hinzugefügt; die Meldung hängt; X wird von Hand entfernt, der Sync
  beobachtet das um t₁ > t₀ (Platzierung gibt es noch keine; `EmoteSetLeaveObservation (Kanal, X, S)
  = t₁`); X wird von Hand wieder hinzugefügt. Die verspätete Meldung kommt: für X existiert eine
  Beobachtung mit t₁ > `RegisteredAtUtc` = t₀ → X wird **verworfen** (`discardedStaleIds`), keine
  Platzierung, kein frisches Datum. Y (nie entfernt) wird platziert. Aktivierung wird gesetzt (das
  Tag wurde eingespielt). **Gelöst.** — Grenze: wurde X entfernt und wieder hinzugefügt, **ohne** dass
  der Sync das Verlassen sah (Fenster aus R1), gibt es keine Beobachtung und X wird platziert — das ist
  R1, nicht mehr.
- *7. „Überlappende Ausräum-Vorschauen hinterlassen Platzierungen bei einem inaktiven Halter."* A und
  B aktiv in S, beide enthalten X, nur A hält (A, X, S). Beide Vorschauen werden gleichzeitig gelesen:
  A sieht X eigen mit `heldByActiveTags = [B]`; B sieht X fremd („wird noch von A gebraucht"), eigener
  Snapshot leer. A meldet zuerst (Kanalsperre): X behalten → wandert zu (B, X, S) mit Revision R_A,
  (A, S) deaktiviert, Sweep findet nichts weiter. B meldet danach (wartet auf die Sperre): Snapshot
  leer, `activationOperationId` passt → Deaktivierung von (B, S) → **Sweep über alle Platzierungen
  (B, ·, S)** findet (B, X, S), prüft Übertragung: kein aktives T′ mehr (A ist inaktiv) → **gelöscht**.
  Invariante hält: beide inaktiv, keine Platzierung. X bleibt im Set als „war schon vorher im Set" —
  genau das, was zwei Menschen soeben zweimal bestätigt haben (behalten). Sequenziell statt gleichzeitig
  ändert nichts: B's Vorschau nach A's Meldung zeigt X als eigen (übertragen), angehakt. **Gelöst.**

**Gegenbeispiele der Verifikation der dritten Fassung, nachgerechnet mit E8/E33 rev. 4 und E34:**

- *8. „Der stale REST-Resync ist eine falsche Quelle für ein Verlassen — Standardfall Stronghold."*
  Wöchentlich: die Stronghold-Zeilen sind seit dem letzten Stream archiviert. Einspielen (Operation P,
  registriert t₀) fügt sie hinzu; der EventAPI-Push entarchiviert jede Zeile und stempelt
  `LastEnteredSetAtUtc = t₁`. Der nächste REST-Vollsync (t₂, Minuten später) liest den 10–30 min alten
  Cache, sieht die Emotes nicht, **archiviert die Zeilen erneut** (wie heute, `:765-773` loggt es) —
  aber `LastEnteredSetAtUtc = t₁ > t₂ − 30 min` → **keine Beobachtung** (E34). Die Einspiel-Meldung
  (ggf. verspätet) findet keine Beobachtung > t₀ → Platzierungen werden angelegt. Der Vollsync nach
  dem Cache-Lag entarchiviert wieder (Stempel erneuert). Ausräumen nach dem Stream: Live-Lesung zeigt die
  Emotes, Platzierungen gelten → angehakt. **Gelöst.** Vorher (dritte Fassung): Platzierungen gelöscht,
  Beobachtung > t₀, Meldung verworfen — das Feature wäre im Normalfall leer gelaufen. Rest: eine echte
  manuelle Entfernung innerhalb von 30 min nach dem Eintritt bei ausgefallener EventAPI wird erst vom
  ersten Vollsync **nach** dem Fenster als Beobachtung geschrieben (R1, Fehlrichtung: Platzierung gilt
  bis dahin weiter — der Mensch sieht in der Vorschau ein Emote, das die Live-Lesung ohnehin nicht mehr
  zeigt; war es inzwischen wieder hinzugefügt, ist es angehakt mit Datum).
- *9. „Rennen zwischen Sync und Meldung unter `READ COMMITTED`."* Eine Einspiel-Meldung (Operation P,
  registriert t₀) hält die Kanalsperre und fügt gerade `(A, X, S)` ein, noch nicht committed; parallel
  beobachtet der EventAPI-Delta-Pfad, dass X das Set verlassen hat (t₁ > t₀), und schreibt die
  Beobachtung — er sieht die uncommittete Platzierung nicht und hätte sie (dritte Fassung) nicht
  gelöscht. Die Meldung prüft Beobachtungen **vor** ihrem Insert und sah t₁ ggf. noch nicht → Platzierung
  wird committed. **Jetzt:** jede spätere Lesung wendet die Regel an: Beobachtung t₁ > `RegisteredAtUtc`
  t₀ → Platzierung **gilt nicht**, erscheint nirgends, kommt in keinen Snapshot; der nächste Einspiel-
  Upsert oder Sweep räumt die Zeile weg. Umgekehrte Reihenfolge (Beobachtung committed, dann Meldung):
  die Meldung sieht t₁ > t₀ und verwirft X (`discardedStaleIds`). Beide Reihenfolgen enden gleich.
  **Gelöst** — ohne Sperre im Sync.

### 5.6 Warum die Form später persönliche Tags zulässt

Ein optionales `OwnerTwitchUserId` (null = Kanal-Tag) auf `EmoteTag` würde reichen; der Unique-Index
bekäme den Eigentümer dazu, Lesen filterte nach „Kanal-Tags ∪ eigene". **Nicht** Teil von v1, nicht
vorbereitet. Sobald eine solche Spalte existiert, ist die Tabelle Teil der Kontolöschung (3.2).

---

## 6. API-Vertrag

Neue Datei `EmotePurge.Api/Endpoints/EmoteTagEndpoints.cs` mit `MapEmoteTagEndpoints(this WebApplication app)`.
Service-Interface `IEmoteTagService` in `EmotePurge.Core/Services/`, Implementierung `EmoteTagService`
in `EmotePurge.Infrastructure/Services/`, `AddScoped` in `AddEmotePurgeInfrastructure` (Regeln 4/5/6).
Handler bleiben dünn. T-B liefert 6.1–6.3 (ohne die Platzierungsfelder), T-C ergänzt 6.2 um
Platzierungs-/Aktivierungsfelder und liefert 6.4.

### 6.1 Gruppen und Filterketten

| Gruppe | Präfix | Kette |
|---|---|---|
| **Lesen** | `/api/channels/{channelName}/tags` | `RequireAuthorization()` → `ChannelNameValidationFilter` → `UsageStatsAccessAuthorizationFilter` → `RequireRateLimiting(InteractiveRead)`; je Leseroute zusätzlich `EmoteSetIdValidationFilter` (Query-Wert `emoteSetId`). |
| **Pflegen** | `/api/channels/{channelName}/tags` | `RequireAuthorization()` → `ChannelNameValidationFilter` → `ChannelManagementAuthorizationFilter` → `RequireRateLimiting(Bookkeeping)` |
| **Registrieren und Melden** | `/api/channels/{channelName}/tags/{tagId}/operations` und `…/placements` | `RequireAuthorization()` → `ChannelNameValidationFilter` → `UsageStatsAccessAuthorizationFilter` → `RequireRateLimiting(Bookkeeping)`; **im Handler zusätzlich die 7TV-Besitzprüfung** `IImportTargetOwnershipService.CheckAsync(actor.TwitchUserId, actor.Login, emoteSetId, ct, BuildOwnerHint(request.TargetOwnerTwitchId))` mit exakt der Leiter von `sync-deleted` (3.3): `SetNotFound` → 404 `emote_set_not_found`, `Forbidden` → 403 ohne Body, `Unavailable` → 503 `foreign_channel_seventv_unavailable`, erst dann der Service-Aufruf (E9 rev. 3). |

`{tagId}` ist `long`; ein Tag, das es nicht gibt **oder das einem anderen Kanal gehört**, ist 404
`tag_not_found` — der Service sucht immer `(ChannelId, Id)`. Ein nicht getrackter Kanal ist 404
`channel_not_found`. Die Besitzprüfung sitzt im Handler und nicht in einem Filter, weil sie den Body
(`emoteSetId`, `targetOwnerTwitchId`) braucht — dieselbe Begründung, aus der `sync-deleted` sie als
Leiter im Handler führt. Der Kanalfilter bleibt davor: ohne Leserecht am Kanal gibt es kein 403 aus der
Besitzprüfung, das verrät, ob das Set existiert.

### 6.2 Lesen

Beide Leserouten nehmen den Query-Parameter **`emoteSetId`** (E29). Fehlt er, gilt
`Channel.ActiveEmoteSetId`; die Antwort nennt **immer** `emoteSetId` (die tatsächlich verwendete ID)
und `isActiveSet`. Ist weder ein Parameter noch ein aktives Set vorhanden, sind die set-bezogenen Felder
`0`/`false`/leer und `emoteSetId` ist `null`. **Jedes Platzierungsfeld unten meint nur geltende
Platzierungen** (Lese-Zeit-Regel, 5.5 Regel 5): der Service joint `EmoteTagPlacement → EmoteTagOperation`
(für `RegisteredAtUtc`) und schließt Zeilen aus, zu denen eine `EmoteSetLeaveObservation` des Kanals für
dieselbe `(SevenTvEmoteId, emoteSetId)` mit jüngerem `LastObservedAtUtc` existiert — Regel 10 (5.5/7):
beide Joins laufen über skalare Schlüssel, nicht über Navigationen in ein `GroupBy`.

**`GET …/tags?emoteSetId=`** → 200 `{ emoteSetId, isActiveSet, tags: [...] }`, Tags in
`CreatedAtUtc`-Reihenfolge:

| Feld | Bedeutung |
|---|---|
| `id`, `name` | |
| `entryCount` | Einträge. |
| `inSetCount` | Einträge mit unarchivierter Zeile (E11). Hinweis: dieser Status ist kanal-, nicht setbezogen (3.1); er ist deshalb nur für das aktive Set aussagekräftig und wird für ein anderes `emoteSetId` als `null` geliefert. |
| `placedCount` | Platzierungen dieses Tags in `emoteSetId` (T-C). |
| `active` | Aktivierung `(tag, emoteSetId)` existiert (T-C). |
| `activatedAtUtc` | Zeitpunkt der Aktivierung oder `null` (T-C). |

**`GET …/tags/{tagId}/entries?emoteSetId=`** → 200 `{ emoteSetId, isActiveSet, activationOperationId,
entries: [...] }`, Einträge in `AddedAtUtc`-Reihenfolge:

| Feld | Bedeutung |
|---|---|
| `sevenTvEmoteId`, `alias`, `imageUrl` | Snapshot (5.2). |
| `inSet` | unarchivierte Zeile vorhanden (E11; `null` für ein nicht-aktives Set, s. o.). |
| `currentName` | `Emote.Name` der Zeile, wenn `inSet`, sonst `null`. |
| `placedByThisTag` | Platzierung `(tagId, id, emoteSetId)` existiert (T-C). |
| `placedAtUtc` | deren `PlacedAtUtc` oder `null` — die Vorschau zeigt „eingespielt am" (T-C). |
| `placementOperationId` | deren `OperationId` (Revision, 5.3) oder `null` — der Ausräum-Flow reicht sie je Snapshot-Eintrag zurück (T-C). |
| `heldByActiveTags` | `{ id, name }[]` der **anderen** Tags des Kanals, die in `emoteSetId` **aktiv** sind und einen Eintrag für dieses Emote haben, sortiert nach 5.5 Regel 3 (T-C). |
| `placedByOtherTags` | `{ id, name }[]` der anderen Tags mit Platzierung `(·, id, emoteSetId)` (T-C). |

`activationOperationId` ist die `OperationId` der Aktivierung `(tagId, emoteSetId)` oder `null`; der
Ausräum-Flow reicht sie in seiner Meldung zurück (5.5 Gegenbeispiel 3). Regel 10 (5.5/7) gilt für beide
Abfragen.

### 6.3 Pflegen (T-B)

| Route | Body | Antwort | Fehler |
|---|---|---|---|
| **`POST …/tags`** | `{ name }` | 201 `{ id, name }` | 400 `tag_name_invalid` · 409 `tag_name_taken` · 409 `tag_limit_reached` |
| **`PATCH …/tags/{tagId}`** | `{ name }` | 200 `{ id, name }` | wie oben außer Limit · 404 `tag_not_found` |
| **`DELETE …/tags/{tagId}`** | — | 204 | 404 `tag_not_found`. Kaskade löscht Einträge, Platzierungen, Aktivierungen, Operationen (E16). |
| **`POST …/tags/{tagId}/entries`** | `{ sevenTvEmoteIds }` | 200 `{ addedCount, alreadyTaggedCount, skippedNotInSetIds }` | 400 `emote_ids_empty`/`emote_ids_invalid` · 404 `tag_not_found` · 409 `tag_entry_limit_reached` (geprüft **vor** dem Schreiben) |
| **`POST …/tags/{tagId}/entries/remove`** | `{ sevenTvEmoteIds }` | 200 `{ removedCount }` | 400/404 wie oben. Löscht Eintrag **und** alle Platzierungen `(tagId, id, ·)` (E17). |

`entries/remove` ist ein `POST` mit Body (ein `DELETE`-Body ist in Clients und Proxys nicht
verlässlich — Festlegung). Zuweisen (E23): Tag und Kanal laden → IDs deduplizieren → unarchivierte
Zeilen zu den IDs laden → ohne Zeile: `skippedNotInSetIds`; bestehender Eintrag: `alreadyTaggedCount`;
Rest anlegen mit `Alias = Name`, `ImageUrl` der Zeile. Ein Unique-Verstoß durch einen parallelen
Zuweiser zählt als `alreadyTagged`.

**Audit** (`AuditLogEntry`, Snapshot-String `ChannelName`): `tag.create`, `tag.rename`, `tag.delete`
mit Details **nur `{ tagId }`** (bei `delete` zusätzlich `{ entryCount, placementCount }`) — **kein
`tagName`** (E30). Zuweisen und Herausnehmen schreiben keinen Audit-Eintrag (Festlegung: Dutzende
Zeilen ohne Erkenntnis; die Meldungen in 6.4 tragen die Papierspur des 7TV-Schreibens).

### 6.4 Registrieren und Melden (T-C)

| Route | Body | Antwort |
|---|---|---|
| **`POST …/tags/{tagId}/operations`** (Registrieren, **vor** dem ersten 7TV-Schreiben, E27 rev. 3) | `{ operationId, kind: "playIn" \| "removal", emoteSetId, targetOwnerTwitchId }` | 201 `{ registeredAtUtc }`; dieselbe `operationId` noch einmal → 200 mit demselben Wert (idempotent); `operationId` bereits für ein anderes Tag/Set registriert → 409 `tag_operation_conflict` |
| **`POST …/tags/{tagId}/placements`** (Einspielen) | `{ operationId, emoteSetId, targetOwnerTwitchId, sevenTvEmoteIds }` — die **tatsächlich hinzugefügten** IDs (`importedKeys`), **leer erlaubt** (No-op-Einspielen, E26) | 200 `{ replayed, recordedCount, alreadyRecordedCount, notTaggedIds, discardedStaleIds }` |
| **`POST …/tags/{tagId}/placements/removed`** (Ausräumen) | `{ operationId, emoteSetId, targetOwnerTwitchId, activationOperationId, snapshot: { sevenTvEmoteId, placementOperationId }[], removedIds, keptIds }` — `snapshot`: die **eigenen Platzierungen, die die Vorschau erfasst hat, je mit der gelesenen Revision**; `removedIds`: `doneKeys` des Lösch-Laufs (leer ohne Lauf); `keptIds`: eigene Platzierungen, die im Set bleiben (unangehakt oder Zeile nicht `done`) | 200 `{ replayed, deletedCount, transferredCount, droppedCount, sweptCount, deactivated }` |

Alle drei: `operationId` ist eine UUID (400 `tag_operation_id_invalid` sonst); `emoteSetId` wird mit
`EmoteSetIdValidation.IsValid` geprüft (400 `invalid_emote_set_id`); sie **muss nicht** das aktive Set
sein (E6: gemeldet wird, wohin geschrieben wurde). Vor dem Service-Aufruf die 7TV-Besitzprüfung
(6.1, E9 rev. 3). Eine Meldung zu einer **nicht registrierten** Operation → 404 `tag_operation_unknown`;
zu einer Operation mit **anderem `emoteSetId`, anderem `kind` oder anderem Tag** → 409
`tag_operation_conflict` (dieselben drei Fälle wie bei der Registrierung); zu einer bereits angewandten →
200 `replayed: true`, alle Zähler 0, nichts geschrieben (E27). Jede Meldung läuft in **einer
Transaktion**, die zuerst die Kanalzeile mit `FOR UPDATE` nimmt (5.5 Regel 6) und am Ende `AppliedAtUtc`
setzt; ein Serialisierungs-/Deadlock-Fehler ist transient (5.5 Regel 6, Retry-Regel).

**Einspielen:** je ID mit Eintrag: existiert `EmoteSetLeaveObservation (Kanal, id, emoteSetId)` mit
`LastObservedAtUtc > RegisteredAtUtc` der Operation → **verworfen**, `discardedStaleIds` (E33); sonst
Upsert `(tagId, id, emoteSetId)` — **neu oder bestehend, in beiden Fällen** `OperationId = operationId`
und `PlacedAtUtc = jetzt` (5.5 Regel 10; `recordedCount` zählt neue, `alreadyRecordedCount` bestehende
Zeilen). IDs ohne Eintrag → `notTaggedIds`, nicht platziert. **Kausal gelesen:** verworfen wird nur, was
**nach** der Registrierung als Verlassen beobachtet wurde; ein Emote, das tatsächlich vor der
Registrierung entfernt, aber erst danach vom Sync gesehen wurde, wird ebenfalls verworfen — fail-safe,
die Platzierung fehlt dann für ein Emote, das der Lauf wirklich hinzugefügt hat (Fehlrichtung: zu wenig
vorgeschlagen). Dann Upsert
`EmoteTagActivation (tagId, emoteSetId)` mit dieser `operationId` — **auch bei leerer ID-Liste** und
auch, wenn alles verworfen wurde (das Tag wurde eingespielt; was davon noch da ist, sagt die nächste
Live-Lesung).

**Ausräumen.** Sei P = die Platzierungen `(tagId, ·, emoteSetId)`, die **jetzt** existieren
(geltende und verfallene — der Sweep räumt beide); S = der `snapshot`. Ein Snapshot-Eintrag **trifft**
eine Platzierung in P nur, wenn ID **und** `placementOperationId` übereinstimmen (Revision, 5.3) — ein
Eintrag ohne Treffer wird übersprungen. Zuerst Schritt 4 entscheiden, dann die übrigen:

4. Deaktivieren: `EmoteTagActivation (tagId, emoteSetId)` wird gelöscht, **wenn** ihre `OperationId`
   gleich `activationOperationId` ist (`deactivated: true`); sonst bleibt sie (`deactivated: false` —
   ein neueres Einspielen hat das Tag erneut aktiviert).
1. Getroffene Platzierungen mit ID in `removedIds` → löschen (`deletedCount`) — in beiden Fällen von 4.
2. **Nur bei `deactivated: true`:** getroffene mit ID in `keptIds` → Übertragung nach 5.5 Regel 4
   (`transferredCount`; die übertragene Platzierung bekommt `OperationId = operationId`, behält
   `PlacedAtUtc`), ohne aktives T′ → löschen (`droppedCount`). Bei `deactivated: false` bleiben sie
   unberührt: das Tag ist wieder aktiv und hält sie zu Recht (eine Übertragung würde einem aktiven Tag
   seine Platzierungen nehmen — die dritte Fassung hatte das übersehen).
3. Getroffene, deren ID weder in `removedIds` noch in `keptIds` steht → laut Vorschau nicht mehr im Set
   → löschen (`droppedCount`) — in beiden Fällen von 4 (die Vorschau hat gesehen, dass sie weg sind; ein
   neueres Einspielen hätte sie mit neuer Revision überschrieben, dann gäbe es keinen Treffer).
5. **Sweep, nur bei `deactivated: true`** (E26 rev. 3): jede **weitere** Platzierung in P — nicht
   getroffen, weil nach der Vorschau hereingewandert, neu platziert, in anderer Revision oder
   **verfallen** (5.5 Regel 5) — wird wie „behalten" behandelt: Übertragung an ein aktives T′ oder
   Löschung (`sweptCount`); eine verfallene Zeile wird nie übertragen, nur gelöscht. Danach gilt: keine
   Platzierung `(tagId, ·, emoteSetId)` mehr.
6. IDs in `removedIds`/`keptIds` ohne Treffer sind kein Fehler (ein fremdes, unplatziertes oder
   inzwischen anders revidiertes Emote — erlaubt, 7.2/6).

Beide Meldungen schreiben einen Audit-Eintrag `tag.playedIn` / `tag.removed` mit
`{ tagId, emoteSetId, operationId, emoteCount }` (E30: ohne Namen); die Registrierung schreibt keinen
(sie ist Absicht, kein Ereignis). **Kein Resync, kein Live-Event** aus diesen Meldungen — die Läufe
lösen ihren Resync über `sync-imported`/`sync-deleted` aus (3.4); die Platzierungsmeldung ist
Buchführung über die Buchführung.

### 6.5 Neue Fehlercodes (Regel 7)

T-B: `tag_name_invalid`, `tag_name_taken`, `tag_limit_reached`, `tag_not_found`,
`tag_entry_limit_reached`. T-C: `tag_operation_id_invalid`, `tag_operation_unknown`,
`tag_operation_conflict`. Je ein Eintrag in `ApiErrorCodes.cs`, `api-error.ts` und beiden
Locale-Dateien. Wiederverwendet: `emote_ids_empty`, `emote_ids_invalid`, `invalid_emote_set_id`,
`channel_not_found`, `invalid_channel_name`, `invalid_source_kind`, `emote_set_not_found`,
`foreign_channel_seventv_unavailable` (Besitzprüfung, 6.1).
**T-C ergänzt** `ValidateSyncImportedVocabulary` um `"tag"` (ohne `SourceChannelName`, ohne
`LeaderboardSort`, wie `"file"`) — sonst endet jeder Einspiel-Lauf mit 400 **nach** dem 7TV-Schreiben.

### 6.6 Test-Substitution

`tests/EmotePurge.Api.Tests/ApiFactory.cs:252-274` ersetzt jeden Handler-Service, weil
`RequestDelegateFactory` die Handler-Services **vor** der Filter-Pipeline auflöst. `IEmoteTagService`
kommt dort als Substitute dazu; `IImportTargetOwnershipService` ist dort bereits ersetzt (die
Besitzprüfungs-Fälle der Melde-Routen stellen seinen Status je Test).

---

## 7. Abläufe

Drei Einstiege ins Schreiben: **Tag zuweisen** (nur Api, T-B), **Einspielen** (Import-Lauf, T-C),
**Ausräumen** (Lösch-Lauf, T-C). Die beiden 7TV-Läufe laufen von der Tags-Seite und aus der
Filterzeile **identisch**; nur die Anzeigefläche unterscheidet sich (9.5).

### 7.0 Tag zuweisen (Dock, T-B)

1. Knopf „Tag zuweisen…" im `[selection-actions]`-Slot unter: `!isCoarse()`, `canManage()` (E9),
   `importScopeCurrent()` **und** `shownSetId() === activeEmoteSetId()` (E3; bestätigt 13.0),
   Auswahl nicht leer. Kein `startLocked`-Bezug (kein 7TV-Schreiben).
2. Klick → kleiner Dialog (`TagAssignDialog`, `openTagAssignDialog`): Tags des Kanals als Checkboxen
   (`GET …/tags`, Skeleton §6.1), Feld „Neuer Tag" mit Anlegen (Feldfehler §5.3: `tag_name_taken`,
   `tag_name_invalid`, `tag_limit_reached`). **Add-only** (§2.5); keine Vorbelegung „schon getaggt".
3. Bestätigen „n Emotes zuweisen" (n = `selectedItems().length`) → je Tag ein
   `POST …/tags/{tagId}/entries` **nacheinander** → Dialog schließt mit Summe.
4. Vergängliche Statusmeldung auf der **Zählzeile** (§4.5, eigenes Regionspaar): „n Emotes zu
   Stronghold hinzugefügt" / „… zu 3 Tags"; bei `skippedNotInSetIds` > 0 zweiter Satz. Auswahl bleibt.
   Tag-Filter (falls aktiv) lädt seine Schlüsselmenge neu.

**7.0a Aus Tag entfernen (Dock, nur bei aktivem Tag-Filter):** „Aus ‚Stronghold' entfernen (n)",
`canManage()`, keine Bestätigung (nichts verlässt 7TV) → `POST …/entries/remove` → Statusmeldung →
Tags neu laden. `title`: Platzierungen gehen mit (E17).

### 7.1 Einspielen (T-C)

**Voraussetzungen:** `!isCoarse()`, `startLocked() === false`, aktives Set bekannt, Token vorhanden
oder Prompt (nach der Bestätigung, wie der Import). Einspielen ist **auch für ein bereits
eingespieltes Tag** erlaubt (nachgetaggte Emotes nachziehen).

**Schritte:**

1. `operationId = crypto.randomUUID()`; `emoteSetId = activeEmoteSetId()` einfrieren (E29).
2. `GET …/tags/{tagId}/entries?emoteSetId=` frisch laden; stimmt `emoteSetId` der Antwort nicht mit
   der eingefrorenen überein oder ist `isActiveSet === false` → Abbruch mit Notiz „Das aktive Set hat
   gewechselt — Seite neu laden" (Familie `tags.errors.setChanged`), kein Lauf.
2a. **Registrieren** `POST …/tags/{tagId}/operations` `{ operationId, kind: "playIn", emoteSetId,
   targetOwnerTwitchId }` (E27 rev. 3). 403 → der Nutzer darf dieses Set bei 7TV nicht schreiben →
   Banner `tags.errors.noWriteRight`, kein Lauf (das hätte sonst erst der erste ADD gesagt); 503 →
   `tags.errors.ownershipUnavailable`, erneut versuchen. `targetOwnerTwitchId` kommt aus
   `resolveEditableSet` (Schritt 2b), das der Import-Flow für Tag-Läufe ohnehin braucht — Reihenfolge
   im Plan: 2b vor 2a.
2b. **Set-Prüfung** `resolveEditableSet(emoteSetId)` (liefert `ownerTwitchChannelId`; Muster
   `MassDeletePanel.openConfirmDialog`).
3. **Live-Lesung** `loadSevenTvSetEntries(httpClient, emoteSetId)`; **`complete === false` oder Fehler
   blockt** (E28), Grund als Banner am Knopf (`tags.errors.setReadIncomplete` / `setReadUnavailable`,
   Muster `massDelete.errors.*`), erneut versuchen möglich.
4. **Partition** (reine Funktion `partitionTagPlayIn(entries, live)`, `shared/tags/tag-play-in.ts`):
   `alreadyInSet` = Einträge mit `live.aliasesById.has(id) || live.aliaslessIds.has(id)` → übersprungen,
   **keine Platzierung**; `toAdd` = Rest, Eintragsreihenfolge, als `ImportRow { sevenTvEmoteId,
   name: alias, imageUrl }`; `source: ImportSource` mit `origin = { kind: 'tag', tagId, tagName,
   channelName }`. **Kein** Fallback auf `inSet` des Servers (E28).
5. **`toAdd.length === 0`** → kein Lauf; der Flow sendet **direkt** die Einspiel-Meldung
   `POST …/placements` mit `{ operationId, emoteSetId, targetOwnerTwitchId, sevenTvEmoteIds: [] }`
   (Aktivierung, E26); Erfolg → vergängliche Meldung „Alle 100 Emotes von Stronghold sind schon im Set
   — Tag gilt als eingespielt"; Fehler → `NoticeBanner error` mit „Erneut versuchen" (derselbe
   `operationId`). Fertig.
6. Sonst **`startImportFlow(deps, source, { kind: 'chosen', choice, pinSetId: true })`** (E29 rev. 3)
   mit `choice` = das eingefrorene Set (gebaut wie `import-trigger.ts`'s `toImportTarget`). **Mit
   `pinSetId`** nimmt `toTargetSelection` **nicht** den `trackedActive`-Schnellweg, auch wenn die ID die
   aktive ist, sondern `{ kind: 'trackedSet', channelName, emoteSetId }` — der Loader liest dann genau
   dieses Set (Preis: der `trackedSet`-Zweig liest die Mitgliederliste über die #220-Vorschau-Route und
   kostet ein Permit aus `TrackedEmoteSetPreview`; für zwei Läufe je Stream hinnehmbar). Der
   `trackedSet`-Zweig gibt **per Konstruktion** die angefragte ID zurück
   (`import-target-loader.ts:219`, `setId: loaded.value.emoteSetId`) — ein Vergleich nach der
   Zielauflösung kann dort nie fehlschlagen und entfällt deshalb (die dritte Fassung hatte ihn samt
   Test gefordert). Ab hier der bestehende Import: Bestätigungsdialog mit
   Origin-Zeile „aus Tag Stronghold" und Übersprungen-Zeile „3 sind schon im Set"
   (`skippedDuplicates`-Vorgabe aus Schritt 4), Slot-Projektion (warnt), Namenskonflikte im
   Konfliktschritt, Token, `filterAlreadyPresent` (zweite Live-Lesung — bewusst doppelt: sie ist die
   letzte Verteidigung gegen den Doppel-Push, 3.4), `recheckTransferPlan`. **Unmittelbar vor
   `startImport`** (in `start()`, nach dem Arbiter-Check) ein zweiter Vergleich: `targetState.setId`,
   die aktuelle `activeEmoteSetId()` der Seite (vom Flow als Signal mitgegeben) und die eingefrorene ID
   müssen gleich sein — sonst `noteRefusedStart`-artige Notiz `tags.errors.setChanged`, kein Lauf.
   **Das ist der eine wirksame Wächter im Browser**; der zweite sitzt im Server (6.4: eine Meldung mit
   anderer Set-ID als registriert ist 409). **Grenze:** hat der Server den Set-Wechsel noch nicht
   bemerkt (`ActiveEmoteSetId` hinkt bis zum nächsten Sync hinterher, #253 Spec F13), zeigt auch das
   Seitensignal noch das alte Set — der Lauf schreibt dann in das alte, nicht mehr aktive Set, die
   Meldung trägt dieselbe ID, die Platzierungen ruhen dort (E6). Das ist ein Komfortverlust (der Mod
   muss nach dem Sync erneut einspielen), **keine** falsche Buchführung. Der
   Flow reicht `operationId`, `tagId` und `emoteSetId` in `startImport` hinein (neue optionale
   `tag`-Felder am Ziel-Objekt), damit der Lauf-Record sie trägt; `run.targetSetId` ist damit
   **per Konstruktion** die registrierte ID.
7. **Lauf** über `SevenTvImportService`; Dock, Abbruch, Settlement, `sync-imported` mit
   `sourceKind: 'tag'`, Resync wie jeder Import ins aktive Set.
8. **Einspiel-Meldung am Record** (E14): nach `settled` parallel zu `reportImported`:
   `POST …/placements` mit `{ operationId, emoteSetId: run.targetSetId, targetOwnerTwitchId,
   sevenTvEmoteIds: importedKeys(run) }` — nur `done`-Zeilen, die hinzugefügt haben (`add`,
   `renameSource`, `replace`). Bei Abbruch das bis dahin Gelungene. `tagPlacementReport: SyncReportState`
   am `ImportRunInfo`, `reportsPending` zählt sie, Dock-Zeile `sevenTvRun.tagReport.*` mit Retry
   (gleiche `operationId`). Ein `discardedStaleIds` > 0 in der Antwort wird als Dock-Zeile genannt
   („k Emotes wurden inzwischen entfernt und nicht vermerkt"). Verlorene Meldung → fail-safe: Emotes
   erscheinen beim Ausräumen unter „war schon vorher im Set" **und** das Tag gilt nicht als eingespielt
   (Aktivierung fehlt) — der Mensch sieht es am Zustand.
9. Nach `closed`: `GET …/tags` neu (Zähler, `active`).

**Fehlerpfade:** Token fehlt, Prompt abgebrochen → kein Lauf; die registrierte Operation bleibt als
nie angewandte Zeile (5.4). 7TV 401/403 mitten im Lauf → Engine-`abortOn`, Token gelöscht, Rest
`cancelled`, Meldung nennt das Gelungene. Set wechselt zwischen Schritt 1 und Start → einer der beiden
Vergleiche in Schritt 6 bricht ab; wechselt es **während** des Laufs, schreibt der Lauf weiter ins
eingefrorene Set (7TV adressiert Mutationen über die Set-ID, nicht über „aktiv") und die Meldung trägt
dieselbe ID — die Platzierungen ruhen dann in einem nicht mehr aktiven Set (E6).

### 7.2 Ausräumen (T-C)

**Voraussetzungen** wie 7.1 **plus** `active` für `(tag, aktives Set)` (E26); ohne Aktivierung fehlt
der Knopf (nicht gesperrt — es gibt nichts zu erklären als „nicht eingespielt", und das steht als
Zustand daneben).

**Schritte:**

1. `operationId = crypto.randomUUID()`; `emoteSetId = activeEmoteSetId()` einfrieren (E29).
2. `GET …/tags/{tagId}/entries?emoteSetId=` frisch laden → `activationOperationId`,
   `placedByThisTag`, `placedAtUtc`, `placementOperationId`, `heldByActiveTags`, `placedByOtherTags`.
   Antwort-`emoteSetId` ≠ eingefroren oder `isActiveSet === false` → Abbruch „aktives Set hat
   gewechselt".
3. **Set-Prüfung** `resolveEditableSet(emoteSetId)` mit Timeout; `notEditable`/`notSelectable`/
   `unavailable` → kein Dialog, Grund als Banner (Familie `massDelete.errors.*`). Dann
   **Registrieren** `POST …/operations` `{ operationId, kind: "removal", emoteSetId,
   targetOwnerTwitchId }` (E27 rev. 3); 403/503 wie 7.1/2a.
4. **Live-Lesung** `loadSevenTvSetEntries(emoteSetId)`; **`complete === false` oder Fehler blockt**
   (3.4). Liefert zugleich die Aliasse für Queue und Protokoll.
5. **Vorschlag** (reine Funktion `proposeTagRemoval(entries, live)`, `shared/tags/tag-removal.ts`),
   über alle Einträge des Tags, Eintragsreihenfolge; `inLive(id) = aliasesById.has(id) || aliaslessIds.has(id)`:
   - nicht `inLive` → **nicht gelistet**, gezählt („k Einträge sind nicht im Set").
   - `inLive`, `placedByThisTag`, `heldByActiveTags` leer → **angehakt**; Zeile zeigt „eingespielt am
     {{placedAtUtc}}".
   - `inLive`, `placedByThisTag`, `heldByActiveTags` nicht leer → **unangehakt**, „wird noch von <T′>
     gebraucht" (E7 rev. 2), ebenfalls mit Datum.
   - `inLive`, nicht `placedByThisTag`, `placedByOtherTags` nicht leer → **unangehakt**, „wird noch
     von <T′> gebraucht".
   - `inLive`, nicht `placedByThisTag`, niemand platziert → **unangehakt**, „war schon vorher im Set"
     (E2 rev. 2; auch der Fall einer verlorenen Einspiel-Meldung).
   Ergebnis `{ rows: { sevenTvEmoteId, aliases, checked, reason, placedAtUtc }[], notInSetCount,
   snapshot }` mit `snapshot` = **alle** `placedByThisTag`-Einträge als `{ sevenTvEmoteId,
   placementOperationId }`, sichtbar oder nicht (die nicht-sichtbaren gehören zum Snapshot, damit der
   Server sie als `dropped` bereinigt, 6.4/3; die Revision, damit er nur die gelesene Platzierung
   anfasst, 6.4).
6. **Bestätigung = Vorschau** (E19, `TagRemovalConfirmDialog`, `openTagRemovalConfirmDialog`, §7):
   Titel „Stronghold ausräumen"; Zusammenfassungszeile „n Emotes werden entfernt, k bleiben im Set"
   (folgt den Haken live); Set-Zeile (`massDelete.confirmSetLine`); Shared-Set-Warnung aus
   `EmoteAdminService.getSetWarning(channelName, emoteSetId)` als `error`-Banner; **zuerst die
   angehakten Zeilen** (Checkbox, Sprite-Still, Alias, „eingespielt am"), **dann die unangehakten mit
   Grund** unter „Nicht vorgeschlagen"; Haken sind **in beide Richtungen** umschaltbar (`PRODUCT.md`
   Prinzip 1: Entscheidung beim Menschen); die leisen Sätze des Delete (unwiderruflich, Protokoll);
   bei n = 0 zusätzlich der Satz „Es wird nichts bei 7TV gelöscht; das Tag gilt danach als nicht mehr
   eingespielt". `danger-solid`-Knopf mit **konstantem** Label „Ausräumen" (§9), **immer aktiv** (E26);
   bei n > 0 die angehakten Namen zusätzlich ungecappt (`NamePreviewList [cap]="null"`). Liste > 50
   Zeilen virtuell gescrollt.
7. **Bestätigen** → Abbruchprüfung (`activeEmoteSetId()` ≠ eingefroren → `massDelete.setChangedDuringConfirm`;
   `arbiter.activeRun() !== null` → `noteRefusedStart('delete')`). Dann:
   - **n = 0:** kein Lösch-Lauf. Der Flow sendet **direkt** `POST …/placements/removed` mit
     `{ operationId, emoteSetId, targetOwnerTwitchId, activationOperationId, snapshot, removedIds: [],
     keptIds: <alle eigenen Platzierungen, die inLive sind> }`; Erfolg → vergängliche Meldung
     „Stronghold ausgeräumt — nichts zu entfernen"; Fehler → Banner mit Retry (gleiche ID).
   - **n > 0:** **`SevenTvDeleteService.startDelete(emoteSetId, channelName, emotes,
     expectedChannelName: channelName, targetOwnerTwitchId)`** mit `emotes` = angehakte Zeilen als
     `DeleteQueueEmote { sevenTvEmoteId, name, aliases }`; der Flow reicht `operationId`, `tagId`,
     `activationOperationId`, `snapshot` und die unangehakten eigenen IDs in den Record. Ab hier
     der bestehende Delete: Fortschritt, Abbruch, Settlement, Purge-Protokoll, `sync-deleted`, Resync,
     „Wiederherstellen" (E13).
8. **Ausräum-Meldung am Record** (E14, nur n > 0): nach `settled` parallel zu `reportDeleted`:
   `POST …/placements/removed` mit `{ operationId, emoteSetId: run.setId, targetOwnerTwitchId,
   activationOperationId, snapshot, removedIds: run.result.doneKeys, keptIds }`, `keptIds` =
   unangehakte eigene Platzierungen **plus** angehakte eigene, deren Zeile `failed`/`cancelled`/`unknown`
   endete (sie stehen noch im Set; nach 6.4/2 wandern sie oder werden gelöscht — ausgeräumt heißt
   ausgeräumt, E26). `tagRemovalReport` am `DeleteRunInfo`, Dock-Zeile, Retry.
9. Nach `closed`: Zähler neu laden; `active` ist nun `false`, der Knopf „Ausräumen" verschwindet,
   „Einspielen" bleibt.

**Fehlerpfade:** Live-Lesung unvollständig → blockt, nichts passiert. Lauf abgebrochen → `doneKeys`
ist das Entfernte, der Rest wird per `keptIds` übertragen oder gelöscht, Tag deaktiviert.
Meldung endgültig verloren → Platzierungen und Aktivierung bleiben; der Sync räumt die Platzierungen
der entfernten Emotes beim nächsten Vollsync/Delta weg (E8 rev. 2); das Tag gilt weiter als
eingespielt und sein nächstes Ausräumen (leere Queue) deaktiviert es. „Wiederherstellen" nach einem
Ausräumen → Emotes wieder im Set ohne Platzierung → „war schon vorher im Set" (13.2).

---

## 8. Randfälle

| Fall | Verhalten |
|---|---|
| **Aktives Set wechselt** (Halloween 2026-10-01) | Platzierungen und Aktivierungen des alten Sets ruhen (E6). Zähler beziehen sich auf die angefragte Set-ID (6.2); die Seite nennt sie im Kopf. Einspielen schreibt ins neue Set. Zurückwechseln → alte Zustände gelten wieder; was dort inzwischen von Hand entfernt wurde, hat der Sync **nicht** gesehen (5.5 Regel 5) — die Live-Lesung des nächsten Ausräumens zeigt es und der Server bereinigt es als `dropped`. |
| **Tag löschen mit Platzierungen** | Erlaubt (E16); `ConfirmDialog` „12 Emotes dieses Tags sind noch eingespielt — vorher ausräumen?" mit „Trotzdem löschen" (`danger-solid`) und Abbrechen. Danach gewöhnliche Set-Emotes. |
| **Emote aus Tag herausnehmen** | Platzierungen `(tag, id, ·)` gehen mit (E17); Emote bleibt im Set. |
| **Zwei Tags, ein Emote; beide eingespielt** | Besitzer = ältestes Tag (5.5 Regel 3). Ausräumen des Besitzers → „wird noch von T′ gebraucht", Platzierung wandert. Ausräumen des anderen → „wird noch von T gebraucht". |
| **Emote von Hand aus dem Set entfernt** | Sync archiviert und löscht die Platzierungen im selben Commit (E8 rev. 2). Tags-Seite zeigt es dimm „nicht im Set"; Aktivierung bleibt. |
| **Emote von Hand entfernt und wieder hinzugefügt, bevor der Sync es sah** | Platzierung bleibt → beim Ausräumen **angehakt** mit altem Einspieldatum. **Restrisiko R1** (13.1): der Mensch sieht Datum und Haken, bestätigt oder entfernt ihn; Rückweg über das Protokoll. |
| **Emote von Hand wieder hinzugefügt, nachdem das Tag es ausgeräumt hat** | Keine Platzierung → „war schon vorher im Set" (E2). |
| **Eintrag mit veraltetem Alias** | Im Set → `currentName` abweichend, Hinweis „heute: NeuerName". Nicht im Set → Einspielen nutzt den Snapshot-Alias; vergeben → Konfliktschritt. |
| **Duplikatzelle (#74, zwei Aliasse, eine ID)** | Ein Eintrag (ID). Einspielen: ID ist im Set → übersprungen (E28). Ausräumen: ein REMOVE nimmt beide Einträge; Protokoll trägt alle Aliasse. |
| **Einspiel-Lauf abgebrochen** | Meldung nennt `importedKeys` (Gelungenes); Aktivierung wird gesetzt. |
| **Tab geschlossen während des Laufs** | `beforeunload`-Wächter bei destruktiven Läufen; verlorene Meldung → fail-safe (E5). Einspielen ohne Meldung → Tag nicht aktiviert, Emotes „war schon vorher im Set"; der Mod sieht „nicht eingespielt" und kann erneut einspielen (No-op → Aktivierung). |
| **Zwei Mods räumen gleichzeitig aus** (dasselbe Tag) | Der zweite Lauf sieht `failed`-Zeilen oder in der Live-Lesung die Emotes nicht mehr. Beide Meldungen tragen eigene `operationId`s und warten nacheinander auf die Kanalsperre (5.5 Regel 6); die zweite findet ihre Snapshot-Einträge schon gelöscht oder in anderer Revision (kein Treffer), `activationOperationId` passt noch (gleiche Aktivierung) → deaktiviert erneut ins Leere, Sweep findet nichts. |
| **Zwei Tags, die dasselbe Emote enthalten, werden überlappend ausgeräumt** | Gegenbeispiel 7 in 5.5: der Sweep der zweiten Meldung räumt die hereingewanderte Platzierung ab; beide inaktiv, keine Platzierung, X bleibt im Set. |
| **Einspiel-Meldung kommt nach einem beobachteten Verlassen** | Gegenbeispiel 6 in 5.5: betroffene IDs werden endgültig verworfen (`discardedStaleIds`), die Dock-Zeile nennt die Zahl; Aktivierung wird trotzdem gesetzt. |
| **Mod ohne 7TV-Schreibrecht ruft Melden/Registrieren auf** | Kanalfilter lässt durch (3.3), die Besitzprüfung im Handler antwortet 403 (E9 rev. 3). Der Knopf „Einspielen"/„Ausräumen" bleibt für ihn sichtbar — ob er ein Token hat, weiß die Seite erst beim Klick; die Registrierung (7.1/2a, 7.2/3) sagt es ihm **vor** dem ersten 7TV-Schreiben. |
| **Zwei Mods spielen gleichzeitig ein** | Beide Live-Lesungen sehen X nicht; beide ADDs gehen durch (3.4) → X zweimal im Set, zwei Platzierungen. **Restrisiko R2** (13.1). Ausräumen: ein REMOVE nimmt beide Einträge. |
| **Kanal ohne aktives Set** | Tags lesbar/pflegbar; Einspielen/Ausräumen fehlen; Dock-Knopf fehlt (§2.5). |
| **Shown set ≠ aktives Set** im Raster | Tag-Filter funktioniert; „Tag zuweisen" und die Filterzeilen-Knöpfe **fehlen** (E3/E6/E29); stattdessen der Satz „Einspielen und Ausräumen wirken auf das aktive Set". |
| **Grober Zeiger** | Dock, Zuweisen, Einspielen, Ausräumen fehlen (§2.5). Tags-Seite lesbar; Umbenennen und Löschen bleiben (Festlegung). „Aus Tag entfernen" ist nur mit feinem Zeiger (Maus) möglich, wie in 9.4: ohne Rasterauswahl gibt es auf grobem Zeiger nichts zu entfernen (Betreiberentscheidung 2026-10-05). |
| **Kanal-Purge (Admin, Aufbewahrung, #245)** | FK-Kaskade nimmt die fünf Tag-Tabellen **und** die Kanal-Tabelle `EmoteSetLeaveObservation` mit (3.2); `Emote.LastEnteredSetAtUtc` fällt mit der Emote-Zeile. Keine Codeänderung an `PurgeAsync`; Trockenlauf-Zählung ohne Tags (13.0). **#245 muss in seiner Spec/seinen Tests ausweisen, dass alle sechs Tabellen kaskadieren** (Abschnitt 10). |
| **Set-Wechsel, den der Server noch nicht bemerkt hat** | Lauf und Meldung gehen ins alte Set, Platzierungen ruhen dort (7.1/6, E6). Komfortverlust, Buchführung korrekt. |
| **A's Vorschau sieht B inaktiv; B wird dazwischen eingespielt (X schon da, übersprungen); A's Lauf entfernt X** | B ist „eingespielt" ohne X. Der Schaden entsteht bei 7TV, **vor** jeder Meldung — keine Buchführung kann ihn verhindern. **Restrisiko R4** (13.1): B's Tags-Seite zeigt X dimm „nicht im Set"; ein erneutes Einspielen von B holt X zurück. |
| **Kanalzusammenführung** | Verlierer mit Tags wird verweigert (5.5 Regel 8; Worker, E15 rev. 2). |
| **Limit erreicht** | 409 mit Code; Grund am gesperrten Knopf. |
| **Rennen Zuweisen ↔ Sync** | Emote zwischen Rasterladen und Klick archiviert → `skippedNotInSetIds` (E23). |
| **Meldung für ein Set, das nicht mehr aktiv ist** | Angenommen (E6). Platzierungen/Aktivierung landen beim gemeldeten Set und ruhen. |
| **Gefälschte Meldung durch einen Leseberechtigten** | Erzeugt Platzierungen/Aktivierung → beim Ausräumen Haken mit Einspieldatum, die der bestätigende Mensch sieht. **Restrisiko R3** (13.1). |

---

## 9. Oberfläche

Die Spec beschreibt **was** und **wo**, nicht das Pixelbild; der Feinschliff läuft als Design-Pass im
Plan nach `docs/UI-Designsprache.md` (Checkliste §11). Zwänge: keine neuen Dauer-Controls, Drilldown
statt Dauerblock, kein Layoutsprung, Primitive aus `shared/ui/`, Farben nur aus Tokens, Labels konstant
(§9), jede Sperre erklärt sich (§10). Alle Texte de/en.

### 9.1 Raster: Dock (T-B)

Im `[selection-actions]`-Slot (konstruktive Gruppe, vor der Lücke zum „Löschen", §8.7): **„Tag
zuweisen…"** (7.0); bei aktivem Tag-Filter zusätzlich **„Aus ‚Stronghold' entfernen (n)"** (7.0a).
Beide `appButton="neutral"`. Keine Tag-Chips auf den Zellen; keine Sidecar-Zeile in v1 (13.3).

### 9.2 Raster: Tag-Filter in der Filterzeile (T-B; Knöpfe T-C)

In **Zeile zwei** (3.5), zwischen „Beobachtete ausblenden" und „Filter zurücksetzen": natives
`<select class="app-input-sm">` mit `aria-label` „Tag" und Option „Alle Tags", **nur gerendert, wenn der
Kanal ≥ 1 Tag hat** (E18). Mit gewähltem Tag **in derselben Zeile** eine Inline-Gruppe:
`Stronghold · eingespielt · 3 im Set · 97 nicht im Set` (`active`, `inSetCount`, `entryCount −
inSetCount`), dann `[Einspielen]` (`neutral`, immer) und `[Ausräumen]` (`danger`, nur bei `active`),
dann Link „→ Übersicht" (`routerLink` `../tags`, `?tag=<id>`). Die Zeile darf umbrechen (`flex-wrap`,
wie heute); keine zweite Leiste. Knöpfe nur bei `shownSetId() === activeEmoteSetId()` (8), bei
`startLocked()` gesperrt mit Grund. In T-B stehen Select, Zahlen (ohne „eingespielt") und Link; die
Knöpfe kommen mit T-C.

### 9.3 Tags-Seite: Ort und Navigation (T-B)

Vierter Reiter **„Tags"** (`channelWorkspace.tabs.tags`) nach „Nutzung", sichtbar unter
`canViewUsageStats()`; Route `channels/:channelName/tags`, `usageStatsAccessGuard`, lazy (E25). Up-Link
vom Layout geerbt (§8.6). Kopf: „Tags", Satz „Zahlen beziehen sich auf das aktive Set <Name>" (Fallback
ID), rechts **„Neuer Tag"** (`primary`) nur bei `canManage()`.

### 9.4 Tags-Seite: Aufbau

**Ab `lg`: zwei Spalten.** Links Tag-Liste als geregelte Zeilen (§2.1, Stretched-Link §2.3): Name,
Micro-Zeile `100 Einträge · 3 im Set · eingespielt (97 platziert)` bzw. `… · nicht eingespielt`. Wahl
in der URL (`?tag=<id>`). Rechts der gewählte Tag:

- **Kopfzeile** mit Name, Zustand („eingespielt seit <Datum>" / „nicht eingespielt") und Aktionen
  **Einspielen** (`neutral`), **Ausräumen** (`danger`, nur bei `active`), **Umbenennen**, **Löschen**
  (`danger`) — Umbenennen/Löschen nur `canManage()`; Einspielen/Ausräumen `!isCoarse()` und mit
  aktivem Set (T-C). Reihenfolge §8.7.
- **Kachelraster** der Einträge, Zellen `app-sprite-cell` 64 px mit `EmoteSprite` (`ATLAS_CELL_PX`,
  `atlasColumns`, `chunkIntoRows`; virtuell gescrollt mit `scrollWindow` ab > 200 Einträgen), ohne
  Bänder. „Nicht im Set" = `app-sprite-cell-void` + `[dimmed]` (3.5). Eine platzierte Zelle
  (`placedByThisTag`) trägt eine kleine Marke unten links (`aria-hidden`, Wort im `aria-label`:
  „eingespielt") — eine Marke, keine Farbe. Hover/Fokus zeigt Alias, „eingespielt am" und ggf.
  „heute: <currentName>" in einer Meta-Zeile unter dem Raster.
- **Auswahl im Raster** (`ListSelection` nach 7TV-ID) → kleines Dock **im Fluss unter dem Raster**
  (§8.7, kein `.app-dock`): „Aus Tag entfernen (n)" (`canManage()`), „Auswahl aufheben".
- **Lauf-Fläche** (9.5) und `DockOutcomeAnnouncer`-Mount (§4.5) — T-C.
- **Leerzustände** (`EmptyState`, §6.2): ohne Tags „Noch keine Tags. Wähle im Nutzungsraster Emotes
  aus und weise ihnen einen Tag zu." mit CTA „Zum Nutzungsraster"; ohne gewählten Tag „Wähle links
  einen Tag"; Tag ohne Einträge „Dieser Tag hat noch keine Emotes" + Link.

**Unter `lg`: Liste → Detail als Drilldown** (`?tag=` gesetzt, `BackLink` „Tags", §8.6). Auf grobem
Zeiger fehlen Einspielen/Ausräumen und die Rasterauswahl; eine Zelle öffnet nichts (Festlegung, 13.3).

### 9.5 Wo die Läufe erscheinen (T-A als Vorbedingung, T-C)

- **Aus der Filterzeile:** im Dock der Nutzungsseite — Import im `app-import-progress-section`, Delete
  in der (durch T-A extrahierten) `app-delete-progress-section`; der Dock mountet über
  `actionDockHasContent` (`importShown`/`deleteShown`).
- **Von der Tags-Seite:** eigener `.app-dock` (§2.5, `DockClearanceService`) mit
  `app-import-progress-section` und `app-delete-progress-section`. **T-A** zieht Fortschritt,
  Protokoll-Download und Restore-Knopf des Delete aus `MassDeletePanel` in eine `DeleteProgressSection`
  (Präzedenz `RestoreProgressSection`, #253 T9) und die Vorprüfkette in `shared/seven-tv/delete-flow.ts`
  (`openConfirm`-Lock, `resolveEditableSet`, Live-Lesung mit `complete`-Pflicht, Set-Wechsel-Abbruch,
  `startDelete`); das Panel behält Knopf und Dialogaufruf. Beide Hostseiten mounten die Section unter
  dem Panel. **Reiner Refactor ohne Verhaltensänderung**, eigener PR, bestehende Specs/E2E bleiben grün.
- `usageStatsLeaveGuard` → seitenneutraler `sevenTvRunLeaveGuard`, den `tags.routes.ts` ebenfalls
  setzt (Festlegung; der Guard liest nur den Arbiter).

### 9.6 Aktualität

Nach jeder eigenen Aktion lädt die Seite `GET …/tags` (und ggf. `…/entries`) neu. Fremde Änderungen
erscheinen beim nächsten Laden; kein Live-Event `tags.changed` in v1. Eine Beobachtung des Syncs (E8)
wird so sichtbar: nach einem `channel.synced`-Reload der Nutzungsseite wendet das nächste `GET …/tags`
die Lese-Zeit-Regel an und die Zähler stimmen.

### 9.7 i18n-Familien (de Referenz, en gleichlautend; Wortlaute Vorschlag)

`channelWorkspace.tabs.tags` „Tags" · `tags.page.*` (Überschrift, Set-Satz, Leerzustände, Zähler,
`state.active` „eingespielt seit {{date}}", `state.inactive` „nicht eingespielt") · `tags.actions.*`
(`assign` „Tag zuweisen…", `unassign` „Aus {{tag}} entfernen", `playIn` „Einspielen", `remove`
„Ausräumen", `rename`, `delete`, `create` „Neuer Tag") · `tags.assignDialog.*` ·
`tags.removalDialog.*` (`title` „{{tag}} ausräumen", `summary` „{{removeCount}} Emotes werden
entfernt, {{keepCount}} bleiben im Set", `placedAt` „eingespielt am {{date}}", `reason.alreadyPresent`
„war schon vorher im Set", `reason.heldBy` „wird noch von {{tag}} gebraucht", `notProposedHeading`
„Nicht vorgeschlagen", `notInSet.{one,other}`, `nothingToDelete` „Es wird nichts bei 7TV gelöscht; das
Tag gilt danach als nicht mehr eingespielt", `confirm` „Ausräumen") · `tags.deleteDialog.placedHint.{one,other}`
· `tags.feedback.*` (zugewiesen, entfernt, skipped, `allPresent` „Alle {{count}} Emotes von {{tag}}
sind schon im Set — Tag gilt als eingespielt", `removedNothing` „{{tag}} ausgeräumt — nichts zu
entfernen") · `tags.errors.*` (`setChanged`, `setReadIncomplete`, `setReadUnavailable`,
`reportFailed` + `retry`, `noWriteRight` „Du darfst dieses Set bei 7TV nicht bearbeiten",
`ownershipUnavailable`) · `sevenTvRun.tagReport.discardedStale.{one,other}` „{{count}} Emotes wurden
inzwischen entfernt und nicht vermerkt" · `tags.filter.*` (`label`, `all`, `active` „eingespielt", `inSet`, `notInSet`,
`overview`, `activeSetOnly`) · `import.origin.tag` „aus Tag {{tag}}" · `sevenTvRun.tagReport.{pending,failed,retry}`
· `errors.api.tag_*` (6.5). Button-Labels bleiben konstant (§9).

---

## 10. Datenschutz und Aufbewahrung

**Ehrliche Einordnung (E30/E31; ersetzt die Aussage „Tags tragen keine Personendaten" der ersten
Fassung):**

- **Was Tags tragen:** 7TV-Emote-IDs, Aliasse, Bild-URLs, Set-IDs, Zeitstempel, Operations-IDs und
  **Tag-Namen als Freitext**. Ein Manager kann in einen Tag-Namen beliebigen Text tippen, auch den Namen
  einer Person. Tags sind damit **Kanal-Inhalt** derselben Kategorie wie Vote-Session-Titel — vom
  Betreiber des Kanals verantwortete Eingaben, nicht Systemdaten über ein Konto.
- **Was Tags nicht tragen:** keinen Ersteller, keine Twitch-ID, keinen FK auf `User` (E1). Die
  Kontolöschung (`AccountDeletionService`, 3.2) ist deshalb **nicht** zu erweitern; sie bleibt
  vollständig, weil nichts in den Tag-Tabellen ein Konto identifiziert.
- **Keine Kopie des Freitexts in actor-gebundene Zeilen (E30):** Audit-Einträge (`tag.create`,
  `tag.rename`, `tag.delete`, `tag.playedIn`, `tag.removed`) tragen nur `tagId` (und Set-/Operations-ID,
  Zähler). Damit existiert ein Tag-Name genau einmal, in `EmoteTag.Name`, und ist mit dem Löschen des
  Tags vollständig weg. Die Audit-Zeile sagt danach nur noch „Actor hat Tag #12 angelegt". Die
  Pseudonymisierung der Kontolöschung behandelt die Actor-Spalten dieser Zeilen wie jede andere.
- **Löschung und Fristen für Tag-Namen:** (1) jederzeit durch jeden Manager des Kanals (Umbenennen,
  Löschen); (2) mit dem Kanal — Admin-Purge, künftige Selbstbereinigung (#245), Aufbewahrungs-Purge
  180 Tage nach Deaktivierung (`RetentionPolicy`, FK-Kaskade 3.2). Eine eigene Frist für Tags gibt
  es nicht; sie sind Betriebsdaten des Kanals wie Emote-Zeilen. Operationen und Platzierungen leben
  mit ihrem Tag.
- **#245 (Selbstbereinigung des Broadcasters):** die Umsetzung existiert noch nicht. Wer #245 nach
  dieser Spec umsetzt, **muss** in Konzept und Tests ausweisen, dass die fünf Tag-Tabellen **und** die
  Kanal-Tabelle `EmoteSetLeaveObservation` (geschrieben für jeden Kanal, auch ohne Tags) über die
  Kaskade mitgehen (der Mechanismus ist derselbe wie beim Admin-Purge; die Aussage gehört trotzdem in
  #245s Liste, s. `EmotePurge-245/docs/Konzept-Broadcaster-Selbstbereinigung-2026-10-03.md:50-57`).
  Landet #201 zweiter, trägt diese Spec die Pflicht (8: Kanal-Purge).
- **Datenschutzerklärung (E31, Freigabevoraussetzung für T-B):** der Betreiber prüft seinen Text (er
  liegt außerhalb des Repos, 3.2) darauf, ob vom Kanal-Manager eingegebene Bezeichner als Kanal-Inhalt
  mit Bindung an die Kanal-Aufbewahrung (180 Tage nach Deaktivierung) genannt sind, und ergänzt ihn
  ggf. — vor dem Release, nicht danach. Keine Frist in `RetentionPolicy` ändert sich.

## 11. Tests (Regeln 11/12)

**Backend — `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteTagServiceTests.cs`
(Testcontainers):**

- *T-B:* Anlegen/Umbenennen (Eindeutigkeit groß/klein/getrimmt, Länge 40/41, Limit 50/51, fremder
  Kanal → `tag_not_found`); Zuweisen (Snapshot aus Zeile, `skippedNotInSetIds`, `alreadyTaggedCount`,
  Limit 1000/1001 vor dem Schreiben, Dedupe); Herausnehmen (Eintrag + Platzierungen aller Sets weg);
  Lesen mit explizitem `emoteSetId` (Zähler, `isActiveSet`, `inSetCount null` für Fremd-Set); Audit ohne
  `tagName`; Kaskade beim Kanal-Purge; Merge-Guard (`ChannelIdentityServiceTests`).
- *T-C:* Registrierung (idempotent; Konflikt bei anderem Tag/Set/Kind → `tag_operation_conflict`;
  `RegisteredAtUtc` ist Serverzeit); Einspiel-Meldung (nicht registriert → `tag_operation_unknown`;
  anderes Set → Konflikt; Upsert mit `OperationId`; leere Liste aktiviert; `notTaggedIds`;
  **`discardedStaleIds` bei Verlassen nach der Registrierung, nicht bei Verlassen davor**; Replay →
  `replayed`, nichts geschrieben); Ausräum-Meldung (Treffer nur bei ID **und** Revision; `removedIds`
  löscht; `keptIds` wandert zum ältesten aktiven T′ mit Eintrag und bekommt die neue Revision / wird
  gelöscht ohne T′; Rest `dropped`; **Sweep** räumt nach Deaktivierung alle übrigen Platzierungen des
  Tags im Set ab — übertragen oder gelöscht; kein Sweep bei `deactivated: false`; Replay); die sieben
  Gegenbeispiele aus 5.5 als je ein Test-Szenario (5 bis 7 mit zwei verschachtelten Operationen, 7
  zusätzlich mit zwei gleichzeitig startenden Transaktionen gegen die Kanalsperre); **Invariante**
  „inaktiv ⇒ keine Platzierung" nach jedem Szenario als gemeinsame Assertion; `heldByActiveTags` nur
  bei Aktivierung.
- *T-C, `SevenTvSyncServiceTests`:* `ReconcileAsync` archiviert eine fehlende Zeile **und** schreibt
  `EmoteSetLeaveObservation (Kanal, ID, ActiveEmoteSetId)` im selben Commit (auch für einen Kanal ohne
  Tags) — **nur** wenn `LastEnteredSetAtUtc` älter als 30 min oder `null` ist; mit `LastEnteredSetAtUtc`
  = jetzt − 5 min wird archiviert, aber **keine** Beobachtung geschrieben (E34); der Delta-Pfad
  (`PulledIds`) schreibt sie **immer**; `UpsertEmote` stempelt `LastEnteredSetAtUtc` beim Anlegen und
  beim Entarchivieren, nicht bei einer Umbenennung; **Platzierungen bleiben vom Sync unberührt** (keine
  Löschung, E8 rev. 4). `EmoteServiceTests`: `MarkInSetAsync` Richtung Delete schreibt die Beobachtung
  (immer), Richtung Restore stempelt `LastEnteredSetAtUtc` und lässt eine bestehende Beobachtung stehen.
- *T-C, Lese-Zeit-Regel (`EmoteTagServiceTests`):* eine Platzierung mit Beobachtung **jünger** als die
  Registrierung ihrer Operation fehlt in `placedByThisTag`/`placedByOtherTags`/`placedCount`; eine
  Beobachtung **älter** als die Registrierung ändert nichts; nach einem erneuten Einspiel-Upsert (neue
  Registrierung, jünger als die Beobachtung) gilt sie wieder; der Upsert überschreibt `OperationId` und
  `PlacedAtUtc` auch für eine bestehende Zeile (5.5 Regel 10); **kausal:** eine Beobachtung, die **vor**
  der Registrierung gestempelt wurde, verwirft nicht, eine danach gestempelte verwirft — unabhängig
  davon, wann das Verlassen bei 7TV tatsächlich geschah; `deactivated: false` lässt `keptIds` unberührt
  und löst keinen Sweep aus; eine Übertragung behält `PlacedAtUtc`; Gegenbeispiele 8 und 9 aus 5.5 als
  Szenarien (9 mit zwei Transaktionen: Beobachtung committed vor/nach der Meldung).

**Backend — `tests/EmotePurge.Api.Tests`:** `AuthFilterMatrixTests` je Route `InlineData` für 401
anonym/unvollständig; je Gruppe 403-Fact (`CanViewUsageStatsAsync false` → Lesen/Registrieren/Melden
403; `CanManageChannelAsync false` → Pflegen 403, Registrieren/Melden **nicht** 403 — E9); **die
Besitzleiter auf allen drei Registrier-/Melde-Routen** (`IImportTargetOwnershipService` →
`Forbidden` ⇒ 403 und `IEmoteTagService` **nicht** aufgerufen; `SetNotFound` ⇒ 404
`emote_set_not_found`; `Unavailable` ⇒ 503 `foreign_channel_seventv_unavailable`; Reihenfolge:
Kanalfilter vor Leiter — ein Nicht-Leseberechtigter bekommt 403 ohne dass die Leiter läuft); 400
`invalid_channel_name` vor Autorisierung; `invalid_emote_set_id` am Query- und Body-Wert;
`tag_operation_id_invalid`; Policy je Route; `ApiFactory` substituiert `IEmoteTagService`.
`ValidateSyncImportedVocabulary`: `"tag"` ohne Namen ok, mit Namen `invalid_source_kind`.

**Frontend — Vitest:**

- `shared/tags/tag-play-in.spec.ts`: Partition gegen `aliasesById`/`aliaslessIds` (aliaslose ID zählt
  als im Set), Reihenfolge, `ImportRow`-Abbildung, leeres `toAdd`, Origin-Form; kein Fallback-Pfad.
- `shared/tags/tag-removal.spec.ts`: die fünf Zeilenfälle; nicht-im-Set gezählt; `snapshot`
  enthält auch nicht-sichtbare eigene Platzierungen, je mit Revision; `keptIds`-Ableitung inkl. `failed`/`unknown`.
- `emote-usage-filter.spec.ts`: Tag-Dimension (`isAnyActive`, `reset`, `apply`).
- `tag-assign-dialog.spec.ts`, `tag-removal-confirm-dialog.spec.ts` (Zusammenfassung folgt den Haken,
  Knopf bei n = 0 aktiv mit Zusatzsatz, Reihenfolge angehakt → nicht vorgeschlagen, Schließwert
  `{ checkedIds, keptIds, snapshot }` mit Revisionen), `tag-play-in-flow.spec.ts`/`tag-removal-flow.spec.ts`
  (Set-Einfrieren, Registrierung **vor** jeder 7TV-Mutation und vor dem Dialog, 403 der Registrierung
  → kein Lauf, Abbruch bei Abweichung, `complete`-Block, No-op-Pfade senden direkt, Retry mit derselben
  `operationId`).
- `import-flow.spec.ts` (E29 rev. 3): `pinSetId: true` erzwingt `trackedSet` auch bei `emoteSetId ===
  `activeEmoteSetId` (ohne Flag unverändert `trackedActive` — bestehende Fälle bleiben); der Loader wird
  mit genau der angepinnten ID aufgerufen und `targetState.setId` ist diese ID (kein eigener
  Abbruchfall — `import-target-loader.ts:219`); **Set-Wechsel während der Bestätigung** (Signal
  `activeEmoteSetId` ändert sich, Dialog bestätigt danach) → kein `startImport`, Notiz; unveränderter
  Fall → `startImport` mit `targetSetId` = eingefrorene ID.
- `seven-tv-import.service.spec.ts`/`seven-tv-delete.service.spec.ts`: optionaler Tag-Report hält
  `closed` auf, `failed` bietet Retry mit gleicher ID, ohne Tag-Origin `null`; Body-Form.
- `tags-page.spec.ts`: Sperren samt Grund; Knopf „Ausräumen" nur bei `active`; URL-Wahl; Leerzustände.
- `api-error-locales.spec.ts` deckt die acht Codes (6.5: fünf T-B, drei T-C) automatisch.

**Frontend — E2E (`web/e2e/emote-tags.e2e.spec.ts`, `/api/**` gemockt, 7TV über `mockSevenTvGql`
mit `setRead`/`addEmote`/`removeEmote`; CDN über die `test.ts`-Fixture; `page.clock.install()` vor
`goto`):**

1. *T-B:* Markieren → „Tag zuweisen…" → neuer Tag → Statusmeldung; Select erscheint; Tags-Seite
   zeigt Liste/Detail, dimm „nicht im Set", Umbenennen, Löschen; Mobile-Drilldown.
2. *T-C:* Tag-Filter → „Einspielen" → `setRead` (vollständig) → Bestätigungsdialog „1 wird
   hinzugefügt, 1 ist schon im Set" → Lauf → `sync-imported` mit `sourceKind: 'tag'` **und**
   `POST …/placements` mit genau der einen ID und einer UUID.
3. *T-C:* „Einspielen" eines vollständig vorhandenen Tags → kein Lauf, `POST …/placements` mit leerer
   Liste → Zustand „eingespielt".
4. *T-C:* „Ausräumen" → Vorschau: eine Zeile angehakt mit Datum, eine unangehakt „war schon vorher im
   Set" → „Ausräumen" → `removeEmote` → `sync-deleted` → `POST …/placements/removed` mit
   `snapshot` (ID + Revision), `removedIds`, `keptIds: []`; Protokoll-Download angeboten; Knopf verschwindet.
5. *T-C:* Zwei Tags mit gemeinsamem Emote (Gegenbeispiel 2): Ausräumen mit null angehakten Zeilen →
   kein `removeEmote`, Meldung direkt, Zustand „nicht eingespielt".
6. *T-C:* Unvollständige `setRead` (Mock: `totalCount` ≠ Items) → Einspielen **und** Ausräumen
   blocken mit Grund, kein `addEmote`/`removeEmote`.
7. *T-C:* `POST …/operations` antwortet 403 → Banner „kein Schreibrecht", kein `setRead`, kein
   `addEmote`, kein Dialog.
8. *T-C:* Set-Wechsel zwischen Klick und Bestätigung (Mock: `active-set` wechselt nach dem ersten
   Aufruf die ID, Live-Event `channel.synced` ausgelöst) → kein `addEmote`, Notiz „aktives Set hat
   gewechselt".

## 12. Lieferung in drei Teilen, Abhängigkeiten, Rollout

### 12.1 Schnitt (E32)

| Teil | Inhalt | Prüft | Deploy |
|---|---|---|---|
| **T-A** | `DeleteProgressSection` + `delete-flow.ts` aus `MassDeletePanel` extrahieren (9.5); `sevenTvRunLeaveGuard`. **Reiner Refactor, keine Verhaltensänderung**; bestehende Specs/E2E unverändert grün. | Frontend-Suiten, E2E, Browser-Blick auf beide Hostseiten. | Nur Api-Image (Frontend liegt im Api-Image). Keine Migration. |
| **T-B** | Migration `AddEmoteTags` (5.1, 5.2); `IEmoteTagService` CRUD + Zuweisen; Endpoints Lesen/Pflegen (ohne Platzierungsfelder); Fehlercodes; Tags-Seite lesend/pflegend; Tag-Filter mit Zahlen und „→ Übersicht" (ohne Lauf-Knöpfe); Dock „Tag zuweisen…"/„Aus Tag entfernen"; Reiter; Merge-Guard (5.5/8); Abschnitt 10 inkl. **E31 als Freigabevoraussetzung**. | Infrastructure-/Api-Tests, Vitest, E2E Szenario 1, Codex-Review. | Migration von Hand → **Api- und Worker-Image zusammen** (Merge-Guard und EF-Modell laufen im Worker). |
| **T-C** | Migration `AddEmoteTagPlacements` (5.3, 5.4 inkl. `EmoteSetLeaveObservation` und `Emote.LastEnteredSetAtUtc`); Platzierungs-/Aktivierungsfelder in 6.2 mit Lese-Zeit-Regel; Registrier- und Melde-Endpoints 6.4 mit Operations-IDs, Revision, Sweep und 7TV-Besitzprüfung; `SourceKind "tag"`; `ImportOrigin 'tag'`; `ImportFlowTarget.pinSetId` und der Set-Vergleich vor dem Start (E29 rev. 3); Beobachtungen und Eintrittsstempel im Sync (5.5/5, E34) an drei Stellen; dritte Meldung am Lauf-Record (E14); Einspiel-/Ausräum-Flows und Vorschau-Dialog; Filterzeilen-Knöpfe; Lauf-Dock der Tags-Seite. | Alles aus 11 für T-C, Codex-Review, Live-Verifikation gegen einen Testkanal (Regel 16: Einspielen, Ausräumen, No-op, Set-Wechsel). | Migration von Hand → **Api- und Worker-Image zusammen** (Beobachtungen und Eintrittsstempel schreibt der Worker) → **erst danach** Tag-Läufe freigeben (Feature-Flag `Tags:RunsEnabled`, Default `false` im ersten Deploy — Festlegung: die Knöpfe Einspielen/Ausräumen rendern nur, wenn das Flag über `GET /api/channels/{channelName}/permissions` oder einen kleinen Config-Endpoint `true` meldet; der Plan wählt den Weg, 13.4/1). |

T-B wird zusammen mit T-C ausgeliefert, nicht allein (Betreiberentscheidung 2026-10-05); T-B trägt deshalb keine eigenständige Formulierung zu Läufen, und seine Texte zu Einspielen/Ausräumen sind zulässig.

Jeder Teil hat seinen eigenen Plan und PR (gegen `main`, nach dem Epic-Merge). Reihenfolge: T-A →
T-B → T-C; T-A und T-B sind unabhängig voneinander und können parallel geplant werden, T-C braucht
beide.

### 12.2 Zeitpunkt

Umsetzung **erst** nach dem bindenden Messlauf (ab 2026-10-08) **und** nach dem Merge von Epic #303
(`feat/emote-sets-200`) auf `main`. Der Branch `feat/201-list-as-selection` steht auf `767b1e3e` und
wird vorher auf den Epic-Stand gezogen. Keine Berührung der Dev-Datenbank oder eines zweiten Workers
vor dem Messfenster (Memory #69/#73).

### 12.3 Vorbedingungen im Bestand (Plan-Tasks)

1. T-A (s. o.).
2. `ImportOrigin` um `'tag'` erweitern und alle erschöpfenden Verbraucher nachziehen (Memory „6
   Bruchstellen"): `importOriginSourceChannelName`, `importOriginLeaderboardSort`, Origin-Zeile in
   `import-preview.ts`/`import-confirm-dialog.ts`, Dock-Zusammenfassung, Protokoll-Envelope,
   Server-Vokabular (T-C).
2a. `ImportFlowTarget` um `pinSetId` erweitern; `toTargetSelection` respektiert es; `startImportFlow`
   nimmt optional ein `activeEmoteSetId`-Signal und die eingefrorene ID und vergleicht nach der
   Zielauflösung und in `start()` (E29 rev. 3). Vertragsänderung an `import-flow.ts`/`import-target-loader.ts`
   nur additiv: ohne `pinSetId` verhält sich der Flow byte-identisch (T-C).
3. Optionale dritte Meldung am Lauf-Record in `SevenTvImportService`/`SevenTvDeleteService`, inkl.
   `reportsPending`, Dock-Zeile, Retry (T-C).
4. `EmoteUsageFilter`-Dimension Tag (T-B).
5. `ApiFactory`-Substitution `IEmoteTagService` (T-B).

### 12.4 Worker — Quelltext, Verhalten, Binary (E15 rev. 2)

- **Quelltext der `EmotePurge.Worker`-Projektdateien:** unverändert in allen drei Teilen.
- **Verhalten des Worker-Prozesses:** **ändert sich in T-B und T-C**, weil der Worker
  `EmotePurge.Infrastructure` ausführt: T-B über den Merge-Guard in `ChannelIdentityService.MergeAsync`
  (`TwitchIdentityReconcileWorker.cs:61`) und über das erweiterte EF-Modell (`AppDbContext` ist
  geteilt — ein alter Worker gegen die neue Datenbank liefe mit einem veralteten Modell-Snapshot);
  T-C über die Beobachtungen und den Eintrittsstempel in `SevenTvSyncService`
  (`SevenTvPeriodicResyncWorker.cs:53`, `Worker.cs:196,275`; EventAPI-Delta über `SevenTvEventClient`;
  E8 rev. 4, E34). **Kein Sperren, keine Löschung im Sync** — der Hot-Path bekommt je archivierter Zeile
  einen Upsert und je entarchivierter Zeile einen Stempel, sonst nichts.
- **Uhr-Annahme:** Api und Worker stempeln mit `DateTime.UtcNow` (3.1) und laufen heute als zwei
  Container auf demselben VPS-Host, also mit derselben Uhr; die Vergleiche in 5.5 Regel 5 und 6.4 setzen
  das voraus. Wer die Prozesse je auf getrennte Hosts legt, hält beide per NTP synchron — ein Versatz
  wirkt wie eine Verschiebung des 30-min-Fensters bzw. der Registrierungsgrenze, in beide Richtungen
  fail-safe erst ab Minuten, nicht ab Millisekunden.
- **Folge:** Worker-Image neu bauen und **zusammen mit** dem Api-Image deployen (T-B und T-C). Die
  Trockenlauf-Zählung bleibt unverändert (13.0).
- **Messfenster:** Deshalb kein Teil vor dem bindenden Lauf (12.2).

### 12.5 Rollout-Reihenfolge je Teil mit Migration

1. Migration von Hand über den Tunnel (`dotnet ef migrations list` → `update` → `list`; CLAUDE.md
   „Prod-Migration"; Befehle bereitet der Umsetzer vor, ausgeführt werden sie vom Betreiber). Beide
   Migrationen sind rein additiv — das alte Image ignoriert die neuen Tabellen.
2. Api- **und** Worker-Image zusammen aktualisieren (Portainer-Stack).
3. Nur T-C: Tag-Läufe freigeben (`Tags:RunsEnabled=true`), nachdem ein Vollsync mit dem neuen Worker
   gelaufen ist (Log: Resync-Zusammenfassung) — vorher könnte ein Ausräumen Platzierungen vorschlagen,
   die ein alter Worker nicht invalidiert hätte. Für T-B gibt es nichts freizugeben.

### 12.6 Zweitmeinung

Vor jedem Plan: `/codex:adversarial-review --model gpt-6.1-sol` über den jeweiligen Teil dieser Spec;
für T-C mit Fokus auf 5.5 (Aktivierung, Übertragung, Sweep, Gegenbeispiele 1–7), 6.4
(Revisionstreffer, `discardedStaleIds`, Kanalsperre), 7.1/6 (angepinnte Set-ID im Import-Flow) und
12.5 (Rollout). Vor jedem Merge `/codex:review`.

---

## 13. Entschiedenes, Restrisiken, Offenes

### 13.0 Die fünf offenen Punkte der ersten Fassung — entschieden (Betreiber, 2026-10-04)

| Nr. (alt) | Entscheidung |
|---|---|
| 13.1 Bestätigungsdialog | **E19 bestätigt:** eigener Dialog = Vorschau aus den Bausteinen des Delete-Dialogs; kein zweiter Dialog. |
| 13.2 Taggen nur im aktiven Set | **Bestätigt:** Gate `shownSetId() === activeEmoteSetId()` plus Server-Prüfung (E3/E23). |
| 13.3 Trockenlauf-Zählung | **Bestätigt: keine neuen Zähler.** Die Kaskade ist vollständig; der Worker wird aus anderen Gründen ohnehin neu gebaut (12.4), die Zählung bleibt trotzdem draußen. |
| 13.4 Undo-Lücke | **Für v1 hingenommen** (13.2 unten). |
| 13.6 Datenschutzerklärung | **Jetzt Freigabevoraussetzung** für T-B (E31, Abschnitt 10). |

### 13.1 Restrisiken (bewusst getragen, durch 0a abgefedert)

| Nr. | Restrisiko | Warum es bleibt | Abfederung |
|---|---|---|---|
| **R1** | **Entfernen + Wiederhinzufügen zwischen zwei glaubwürdigen Beobachtungen** bleibt unsichtbar: die Platzierung gilt weiter (oder eine verspätete Einspiel-Meldung legt sie an, weil keine Beobachtung existiert — 5.5 Gegenbeispiel 6, Grenze), das von Hand neu hinzugefügte Emote wird beim Ausräumen vorgeschlagen. Fenster: bei aktiver EventAPI Sekunden; bei deaktivierter oder getrennter EventAPI bis zum ersten Vollsync, der **sowohl** den REST-Cache-Lag (10–30 min, SevenTV#81) **als auch** das 30-min-Glaubwürdigkeitsfenster nach dem Eintritt der Zeile hinter sich hat (E34, Gegenbeispiel 8) — in der Praxis also bis zu ~30 min nach dem Einspielen plus Cache-Lag; in **nicht-aktiven** Sets unbegrenzt (5.5 Regel 5). | Der Server kann nur beobachten, was 7TV ihm zeigt; eine lückenlose Historie gibt es nicht (kein Resume/Replay der EventAPI), und die REST-Quelle ist dokumentiert veraltet. | Vorschau mit Haken und **Einspieldatum** je Zeile; Mensch bestätigt; Protokoll + „Wiederherstellen". |
| **R4** | **Vorschau-Rennen zwischen zwei Tags:** A's Vorschau sieht B als nicht eingespielt; zwischen Vorschau und Lauf wird B eingespielt (X schon im Set → übersprungen, keine Platzierung); A's Lauf entfernt X. Ergebnis: B gilt als eingespielt, X fehlt im Set. | Der Schaden entsteht bei 7TV **vor** jeder Meldung; keine Buchführung und keine Sperre im Server kann einen bereits gesendeten REMOVE zurückhalten. Zwei Mods, die dasselbe Set in derselben Minute umbauen, sind nicht die Zielgruppe (`PRODUCT.md`: einzelpersoniger Betrieb). | Fehlrichtung: ein Emote zu wenig im Set, nichts zu viel entfernt (X gehörte A). B's Tags-Seite zeigt X dimm „nicht im Set"; ein erneutes Einspielen von B (Live-Lesung zeigt X fehlend) holt es zurück; A's Purge-Protokoll hat den Rückweg. |
| **R2** | **Fremdeditor zwischen Live-Lesung und Schreiben** beim Einspielen (oder zwei Mods gleichzeitig): ein Emote wird doppelt gepusht (3.4: 7TV dedupliziert nicht nach ID); beide Einträge erhalten Platzierungen; ein späteres REMOVE nimmt beide. | Keine Transaktion über 7TV hinweg; `complete` schützt vor Verschiebung **innerhalb** der Lesung, nicht vor Änderungen danach. | Zweite Live-Lesung im Import-Flow (`filterAlreadyPresent`) unmittelbar vor dem Start verkleinert das Fenster auf Sekunden; Duplikate sind in der Set-Ansicht als `slotCount`-Fälle sichtbar (#74). |
| **R3** | **Gefälschte Meldung durch jemanden mit 7TV-Schreibrecht am Set** (Besitzer oder 7TV-Editor des Set-Kontos): kann Platzierungen/Aktivierungen für beliebige getaggte Emotes erzeugen und so Zeilen in der Ausräum-Vorschau vorab anhaken. Ein Twitch-Mod **ohne** 7TV-Schreibrecht kann das seit E9 rev. 3 **nicht** mehr — die Besitzprüfung (`IImportTargetOwnershipService`, 6.1) weist ihn ab, bevor der Service läuft. | Wer die Besitzprüfung besteht, kann bei 7TV direkt löschen — die Meldung verschafft keine Fähigkeit, die er nicht hat; sie beeinflusst nur einen Vorschlag. Die Besitzprüfung ist bis zu 10 min gecacht (#253 Spec F12) — ein soeben entzogenes Editor-Recht meldet noch kurz weiter; dieselbe Grenze gilt für `sync-deleted` heute. | Haken sind Vorschlag; Einspieldatum sichtbar; Mensch bestätigt; Audit `tag.playedIn` mit Actor; Rückweg über das Protokoll. |

### 13.2 Bewusste Lücke: „Wiederherstellen" erzeugt keine Platzierung

Der Restore ist ein eigener Lauf ohne Tag-Bezug (3.4). Nach einem Ausräumen per „Wiederherstellen"
zurückgeholte Emotes sind im Set, aber unplatziert → beim nächsten Ausräumen „war schon vorher im Set",
von Hand anhakbar (7.2/6). Mit E8/E33 rev. 4 ist das konsistent: die Beobachtung des Entfernens hätte die
Platzierungen ohnehin verfallen lassen. Nachläufer: Platzierungs-Rückmeldung am Restore (vierte
Meldung am Lauf) — in v1 hingenommen; der Hinweistext am Restore-Knopf nach einem Tag-Lauf benennt es.

### 13.3 Nachläufer (nicht v1, nicht offen — nur notiert)

Tag-Zeile im Sidecar (9.1); Drilldown-Dialog auf der Tags-Seite (9.4); persönliche Tags (5.6);
Tag-Export/-Import als Datei; Live-Event `tags.changed` (9.6); Platzierung am Restore (13.2);
Trockenlauf-Zähler für Tags (13.0).

### 13.4 Offen für den Betreiber

1. **Feature-Flag-Weg für T-C** (12.1): Flag in der `permissions`-Antwort oder eigener
   Config-Endpoint — der Plan entscheidet, die Spec verlangt nur, dass die Lauf-Knöpfe im ersten
   Deploy aus sind, bis ein Vollsync mit dem neuen Worker gelaufen ist (12.5/3). Alternativ: Flag
   weglassen und das Freigabefenster organisatorisch halten (Deploy abends, erster Lauf am nächsten
   Tag). **Empfehlung:** Flag — es kostet eine Konfigurationszeile und macht die Reihenfolge erzwingbar.
2. **Löschung ohne T′ bei behaltenen Platzierungen** (5.5 Regel 4, Festlegung): „ausgeräumt heißt
   keine Platzierungen" — die behaltenen Emotes werden damit „war schon vorher im Set". Alternative:
   behalten und beim nächsten Ausräumen wieder anhaken (verlangte, dass ein inaktives Tag Platzierungen
   hält, was E26 widerspräche — und seit E26 rev. 3 ist das eine serverseitig geprüfte Invariante).
   **Empfehlung:** wie festgelegt.
3. **Kanalsperre statt `(Kanal, Set)`-Sperre** für die Meldungen (5.5 Regel 6, Festlegung): zwei
   Meldungen in **verschiedenen** Sets desselben Kanals warten unnötig aufeinander. Bei zwei Läufen je
   Stream ist das kein Problem; eine eigene Sperrzeile je Set wäre eine weitere Tabelle ohne messbaren
   Nutzen. **Empfehlung:** Kanalsperre.
4. **30 Minuten Glaubwürdigkeitsfenster** (E34, Festlegung): das dokumentierte obere Ende des
   REST-Cache-Lags. Alternative 15 min (die bestehende Messschwelle) deckt den belegten ~10-min-Fall,
   aber nicht den dokumentierten 30-min-Fall; Alternative 60 min weitet R1 ohne belegten Nutzen. Die
   Zahl ist eine Konstante im Sync (`TagLeaveCredibilityWindow`), kein Konfigurationswert — eine
   Konfiguration würde einladen, R1 unbemerkt zu vergrößern. **Empfehlung:** 30 min; nach dem ersten
   Betriebsmonat gegen das bestehende Lag-Log (`:765-773`) nachmessen.

### 13.5 Nachtrag vom Plan T-C (2026-10-05)

Kein Umschreiben des Spec-Texts; eine Zeile je Punkt, die Spec und Plan lesbar nebeneinander hält.

- **5.3:** Vom Plan T-C am 2026-10-05 überholt: Kein FK Platzierung → Eintrag (5.3); die Platzierung hat einen zusammengesetzten FK auf den Eintrag mit Cascade — Begründung in docs/DECISIONS.md (#201 T-C).
- **3.1, 12.4:** Vom Plan T-C am 2026-10-05 überholt: Der Sync nimmt je Speicherversuch eine explizite Transaktion; ein Upsert je `PulledId` (nicht je archivierter Zeile) plus eine Beobachtungslesung je REST-Takt für die Nachbetrachtung — Begründung in docs/DECISIONS.md (#201 T-C).
- **5.3, 5.4:** Vom Plan T-C am 2026-10-05 ergänzt: Alle T-C-Id-Spalten sind `varchar(32)`, Set-Ids eingeschlossen (statt 24; F1) — Begründung in docs/DECISIONS.md (#201 T-C).
- **5.3, 5.5:** Vom Plan T-C am 2026-10-05 ergänzt: Die Platzierung trägt eine eigene `RegisteredAtUtc` als Gültigkeitsanker (Registrierungszeit der Operation, die sie zuletzt schrieb; bei Übertragung die der Entfernen-Operation); `OperationId` ist nur Herkunft/Revision (F30) — Begründung in docs/DECISIONS.md (#201 T-C).
- **5.5:** Vom Plan T-C am 2026-10-05 ergänzt: Eine abgelaufene Zielzeile gilt bei der Übertragung als nicht vorhanden und wird umgeschrieben — Begründung in docs/DECISIONS.md (#201 T-C).
- **13.1:** Vom Plan T-C am 2026-10-05 ergänzt, neben R1: Die Nachbetrachtung vergleicht gegen den einen `LastEnteredSetAtUtc` der Zeile, egal in welches Set sie eintrat; ein Einspielen in ein frisch gewechseltes Set mit verpasstem PUSH und veraltetem REST-Cache kann seine Platzierung binnen eines Taktes verlieren (eine Platzierung zu wenig, nie zu viel; F28) — Begründung in docs/DECISIONS.md (#201 T-C).
- **9.6:** Vom Plan T-C am 2026-10-05 überholt: Die Tags-Seite lädt bei `channel.synced` für ihren Kanal neu (Set-Status, Tags, Einträge) — schließt T-B-Codex C2 (F3) — Begründung in docs/DECISIONS.md (#201 T-C).
- **7.2/7:** Vom Plan T-C am 2026-10-05 überholt: Die Bestätigungskette des Ausräumens ist die des Delete-Flows (Arbiter-Anspruch plus Token-Prüfung, ohne `noteRefusedStart('delete')`), nicht der Wortlaut „Arbiter-Anspruch → `noteRefusedStart('delete')`“ (F4) — Begründung in docs/DECISIONS.md (#201 T-C).
- **9.4:** Vom Plan T-C am 2026-10-05 ergänzt: Die Kopfzeile der Tags-Seite (Einspielen · Ausräumen · Umbenennen · [Lücke] · Löschen) ist eine benannte Ausnahme in UI-Designsprache §8.7: das Laufpaar kommt aus einer Komponente, das unumkehrbare Löschen bleibt nach der Lücke zuletzt (F39) — Begründung in docs/DECISIONS.md (#201 T-C).
- **12.5:** Vom Plan T-C am 2026-10-05 überholt: Statt „Log: Resync-Zusammenfassung“ (die es nicht gibt) prüft der Betreiber die Bereitschaft mit zwei ausführbaren `psql`-Listen in docs/Operations.md (Liste A muss leer sein, Liste B nur zur Information) — Begründung in docs/DECISIONS.md (#201 T-C).
- **5.5:** Vom Plan T-C am 2026-10-05 überholt: Ein abgelaufener behaltener Treffer wird beim Übertragen verworfen, nicht übertragen (Task 5) — Begründung in docs/DECISIONS.md (#201 T-C).
