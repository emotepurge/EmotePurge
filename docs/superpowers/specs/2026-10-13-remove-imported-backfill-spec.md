# Importierte Backfill-Daten im UI entfernen (#384)

**Stand:** 2026-10-13
**Status:** Umsetzungsspec, kein Code. Entschieden: F1–F3 per Nutzerentscheid vom 2026-10-13, F4 nach Empfehlung (§14). Codex-Sol-Prüfung (adversarial, 2026-10-13) eingearbeitet (§14.5).
**Epic:** #346 (Folge-Issue nach Abschluss) · **Bezug:** #365/#379 (Slots, Teil B), Backfill-Spec `2026-10-09-chat-log-backfill-spec.md`, Slot-Spec `2026-10-10-emote-slots-spec.md`
**Ziel:** fertig, bevor `ChatLogBackfill:Enabled` auf Prod angeht. Dort ist der Backfill aus und bleibt es mindestens bis zum Ende der Bewährung von #379 Phase 1 (≥ 72 h nach 2026-10-13 17:27 UTC) und bis Teil B entschieden ist.
**Basis:** Zielzustand **nach #379 Teil B** (Flag-aus-Zweige entfernt, `SnapshotChanged` weg, Sweep-Konstante neben `EmoteSlotRollbackSql`). Der #384-Code muss nur für „Slot-Flag an“ korrekt sein (§3.4).
**Migration:** keine (§3.3).

---

## 1. Problem und Ziel

Ein Kanal-Manager kann einen Backfill starten, aber nicht zurücknehmen. `DELETE /api/channels/{c}/backfill` bricht nur einen aktiven Lauf ab; bereits importierte Wochen bleiben (B11). Der einzige Weg, importierte Zahlen zu entfernen, ist die Betreiberprozedur in `docs/Operations.md` „Chat-log backfill“ → „Rolling back“ (psql über den Tunnel).

**Ziel:** Im Settings-Tab des Kanal-Workspace entfernt ein Manager nach ausdrücklicher Bestätigung alle importierten Chat-Archiv-Daten seines Kanals — genau die, die ihm der Dialog gezeigt hat. Danach zeigt die Nutzungsseite den Zustand ohne Import (Beschriftung weg, Summen neu), live gezählte Zeilen (`Source = 0`) sind bytegleich, andere Kanäle unberührt, und es bleibt kein zeilenloser unsettled Seed zurück, den diese Aktion verursacht hat.

**Erfolgskriterium:** AK 1–21 (§12) grün, Live-Ablauf §13 auf der Dev-DB bestanden.

## 2. Geprüfter Bestand (main @ `b11bf239`, 2026-10-13)

### 2.1 Was importiert wird und wo es liegt

| Tabelle | Was der Backfill schreibt | FK / Kaskade | Kennung „aus dem Backfill“ |
|---|---|---|---|
| `UsageStats` | Zeilen mit `Source = 1` (`UsageStatSource.ChatLogArchive`), `SlotId` + `EmoteId` aus dem Snapshot, `EmoteSetId` = Set des Laufs, nur Tage `< CreatedAt`-Datum | `EmoteId → Emotes` cascade, `SlotId → EmoteSlots` cascade | `Source = 1`. **Keine `RunId`.** |
| `ChatLogBackfillCoverage` | eine Zeile je abgedecktem Tag, PK `(ChannelId, Day)`, mit `EmoteSetId`, `ArchiveHost`, `RunId` | `ChannelId → Channels` cascade, `RunId → Runs` **SET NULL** | ganze Tabelle |
| `ChatLogBackfillRuns` | ein Lauf je Anforderung (Status-Maschine, Zähler) | `ChannelId` cascade | ganze Tabelle |
| `ChatLogBackfillRunEmotes` | Snapshot je Slot, PK `(RunId, SlotId)` seit M3, `CreatedRow`, `CreatedSlot` | `RunId` cascade; **`SlotId`/`EmoteId` ohne FK** (D25) | `CreatedRow`/`CreatedSlot` |
| `Emotes` | archivierte Platzhalter für Set-Mitglieder ohne Zeile (`IsPlaceholder = true`, `ArchivedAt = NULL`) | Kinder: `EmoteSlots`, `UsageStats`, `Votes`, `VoteSessionEmotes` (alle cascade) | nur über Snapshot `CreatedRow = true` |
| `EmoteSlots` | Flag an: neue autoritative Slots archiviert, `ArchivedAt = NULL` (aktive-Set-Vorschau: vom nächsten REST-Abgleich übernommen); Settle eines Seeds des Vorschau-Sets. Flag aus (Altbestand): ein **unsettled Basis-Seed** je neuem Paar, archiviert, `ArchivedAt = NULL`, im Snapshot mit `CreatedSlot = true` | einzige eingehende FK: `UsageStats.SlotId` | nur über Snapshot `CreatedSlot = true` |

Code: `ChatLogBackfillService.ReplaceBlockAsync` (`src/EmotePurge.Infrastructure/Services/ChatLogBackfillService.cs:444-586`) löscht je Block **alle** `Source = 1`-Zeilen des Kanals im Blockfenster, jedes Set (OD-A), fügt ohne `ON CONFLICT` ein, upsertet die Coverage und schaltet den Lauf bedingt weiter — alles eine Transaktion. Daraus folgen die Invarianten, auf die diese Spec baut:

> **I1:** Für jeden Tag mit Coverage-Zeile tragen alle `Source = 1`-Zeilen des Kanals an diesem Tag das `EmoteSetId` der Coverage-Zeile und stammen vom Lauf, dessen `RunId` dort steht (solange der Lauf nicht von der Retention gelöscht ist). `Source = 1`-Zeilen ohne Coverage-Tag gibt es nur nach Handarbeit (Operations Schritt 4 halb ausgeführt).
>
> **I2:** Eine Zelle ist live **oder** importiert (D3, per Slot seit M2). Importierte Tage liegen vor dem Zählbeginn, Live-Zeilen ab dem Zählbeginn; `MoveSeedUsageAsync` (`SeedSettleWrites.cs:31-61`) kann deshalb keine importierte mit einer Live-Zeile verschmelzen.
>
> **I3:** Coverage-Zeilen eines Kanals entstehen und ändern sich nur im Block-Commit eines **aktiven** Laufs. Ohne aktiven Lauf ist die Coverage eines Kanals unveränderlich, bis jemand sie löscht.

### 2.2 Wege, die es heute gibt

- `DELETE /api/channels/{c}/backfill` → `CancelAsync` (`ChatLogBackfillService.cs:291-326`): bedingtes Update aktiv → `cancelled`, Audit `backfill.cancel`, `backfill.progress`. Nimmt **keine** Kanalsperre.
- Betreiberprozedur „Rolling back“ (`docs/Operations.md:975-1086`): Schritt 1 abbrechen, 2 Flag aus, 3 Platzhalter (D41, global), 3b `CreatedSlot`-Slots (`EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql`, global), 4 Zeilen + Coverage (global **oder je Kanal**), 5 offene Läufe schließen, 6 Image/Down.
- Status-Lesemodell `GET …/backfill` liefert `coverage` als zusammengefasste Intervalle je `(EmoteSetId, ArchiveHost)` mit Set-Namen, `importedFrom/To`, `activeRun`, `lastRun` (`GetStatusAsync`, `:87-130`).
- Die Nutzungsseite lädt die Offenlegung über `GET …/usage-stats/import-coverage` und lädt sie bei `backfill.progress` neu; die Zeilen bei `usage.flushed` (`usage-stats-page.ts:2952-3010`).
- Frontend nach einem Abbruch (`backfill-section.ts:480-511`, `:670-692`): die 204-Antwort patcht `activeRun` sofort auf `null`, die angezeigte Coverage ist aber noch der Stand **vor** dem Abbruch (ein Block kann kurz davor committet haben). `canStart` wartet deshalb über `awaitingPostCancelRead` auf den nächsten abgeschlossenen Status-Read. Der Start-Dialog bindet die Bestätigung an `startPlanKey()` und verwirft sie, wenn sich der Plan hinter dem Dialog geändert hat (`:579-596`).

### 2.3 Sperren, mit denen sich die Aktion vertragen muss

| Pfad | Kanalzeile | Emote-Zeilen | Quelle |
|---|---|---|---|
| Enqueue | `FOR NO KEY UPDATE` by id, dann User `FOR SHARE`, Aktiv-Lauf-Prüfung **unter** der Sperre | `FOR NO KEY UPDATE` (Bindung) | `EnqueueUnderLocksAsync :774-900` |
| Block-Commit (Worker) | `FOR KEY SHARE` | `FOR SHARE` (`LockEmotesForShareByIdsAsync`) | `ReplaceBlockAsync :483-498`, `:1279-1327` |
| Leave/Deaktivierung | `FOR NO KEY UPDATE`, dann Lauf | — | D16 |
| REST-Abgleich/Settle | `FOR NO KEY UPDATE` (`LockChannelsByIdsAsync`) | `FOR NO KEY UPDATE` | `SevenTvSyncService.cs:831, :1033, :1641` |
| Purge, Join, Merge | `FOR UPDATE` | — | D16/D35 |
| Live-Flush | — (FK `KEY SHARE`) | — (FK `KEY SHARE`) | `UsageStatFlushService` |
| Ballot-Anlage (Set-Session) | — | — (FK `KEY SHARE` beim Einfügen in `VoteSessionEmotes`) | `VoteSessionService.UpsertSetSessionEmotesAsync :474ff.` |

`FOR NO KEY UPDATE` kollidiert mit sich selbst, `FOR SHARE` und `FOR UPDATE`, nicht mit `FOR KEY SHARE`. Sperrreihenfolge im Projekt: `Channels` → `Emotes` → (Rest) (`RowLocks.cs:26-28`).

### 2.4 Seeds nach #379 Teil B

- Unsettled Seeds (`EmoteSlots.SeedUnsettled = true`) entstehen nach Teil B **nicht mehr** (M1 und die Flag-aus-Schreiber Flush/Enqueue sind die einzigen Erzeuger, `BaseSlotRule.GetOrCreate*` fällt weg). Bestehende bleiben, bis REST oder eine datierte Vorschau sie settelt.
- Der Sweep aus Teil B (`SeedUnsettled AND NOT EXISTS UsageStats on SlotId`, jedes Set, jede `Source`) ist ein **einmaliger Runbook-Schritt je Datenbank**, kein Dauerjob (Plan-379 §3/§5). Er räumt nach #384 nicht auf.
- Mit Flag an landen importierte Zeilen nie auf einem unsettled Seed: die aktive Vorschau wird mit `ActiveSetSeedUnsettled` abgewiesen (`EmoteSlotReconcilePlanner.Backfill.cs:48-50`), die nicht aktive settelt die Seeds ihres Sets vor der Bindung (`:95-111`, `EmoteSlotReconcilePlanner.cs:274-319`, `SeedUnsettled = false`).
- **Aber:** M1 hat Seeds je `(EmoteId, EmoteSetId)` über **alle** vorhandenen Zeilen angelegt, importierte eingeschlossen, und Flag-aus-Enqueues haben neue Basis-Seeds angelegt und gebunden. Ein solcher Enqueue-Seed ist zugleich `SeedUnsettled = true`, archiviert mit `ArchivedAt = NULL` **und** im Snapshot `CreatedSlot = true`; ein echter Block schreibt seine importierten Zeilen genau darauf (geprüft: `ChatLogBackfillServiceTests.Slots.cs:25-68`, AK 56). Auf jeder Datenbank mit Importen aus der Flag-aus-Zeit (Dev-DB, fremde Betreiber) gibt es deshalb nach Teil B **unsettled Seeds, die nur `Source = 1`-Zeilen tragen** — mit oder ohne `CreatedSlot`. Prod hat laut Plan-375 §0 Punkt 2 keine Läufe und keine Snapshot-Zeilen (`ChatLogBackfill:Enabled` dort nie an); dort ist dieser Fall nach heutigem Stand leer — eine Aussage aus der Doku, keine Messung.

**Folge für #384:** Wer `Source = 1`-Zeilen löscht, kann einen solchen Seed zeilenlos machen. #384 räumt genau diese Seeds selbst ab (§5.3 Schritt 8), ausdrücklich auch die mit `CreatedSlot = true` (D7).

**Nebenbefund (nicht #384):** `ReplaceBlockAsync` Schritt 1 löscht kanalweit importierte Zeilen jedes Sets im Blockfenster und kann auf denselben Datenbanken ebenso einen unsettled Seed zeilenlos machen — nach dem einmaligen Sweep. Gehört zu #379 Teil B bzw. einem Folge-Issue, nicht hierher.

## 3. Rahmen

### 3.1 Drin
- Backend-Fähigkeit „importierte Daten eines Kanals entfernen“ (eigenes Service-Interface), Route, zwei Fehlercodes, Audit, SSE.
- Bindung der Löschung an den bestätigten Umfang (Coverage-Erwartung, D15).
- Abbau der durch die Aktion zeilenlos gewordenen unsettled Seeds.
- UI im Settings-Tab: eigener Abschnitt „Importierte Daten“ unter dem Backfill-Abschnitt, Bestätigungsdialog, Sperren.
- Operations-/DECISIONS-Text, Tests, Live-Ablauf.

### 3.2 Draußen
- Entfernen je Set oder je Lauf (§14 F1).
- Löschen von Platzhaltern und gesettelten `CreatedSlot`-Slots (§14 F2).
- Änderungen an Lauf-Zeilen, Snapshots, Retention, Worker, Status-DTO.
- Fremde SQL-Konstanten (`EmoteSlotRollbackSql.*`, Sweep aus Teil B) — unverändert.
- Der Nebenbefund aus §2.4.

### 3.3 Keine Migration
Alles, was die Aktion braucht, steht im Schema: `UsageStats.Source`, `UsageStats.SlotId`, `ChatLogBackfillCoverage.(ChannelId, Day, EmoteSetId, ArchiveHost)`, `EmoteSlots.SeedUnsettled`. Die Coverage-Erwartung (D15) wird gegen vorhandene Spalten geprüft. Auch das verworfene Entfernen **je Lauf** bräuchte keine: der Besitz eines Tages steht in `Coverage.RunId` (I1). Migrationspflichtig wäre nur eine Spalte „Daten entfernt am“ am Lauf (§14 F4, verworfen).

### 3.4 Flag-Zustand
Die Aktion ist nur erreichbar, wenn `ChatLogBackfill:Enabled = true` (D3). Nach der Zulassungsfolge (Operations „Switching the slot flag on“, Plan-379 §2.1) ist der Backfill nur an, wenn das Slot-Flag an ist; nach Teil B gibt es kein Slot-Flag mehr. Ein Rückweg auf ein Image mit Slot-Flag aus verlangt vorher Backfill aus → die Route antwortet 404. Deshalb muss #384 nur den Flag-an-Zustand korrekt behandeln, und es liest `SlotReconcileOptions` **nicht** (Teil B entfernt den Typ).

## 4. Entscheidungen

Bindend aus dem Issue: Rollen wie beim Start (B1), Bestätigung, eine Transaktion, Lauf aktiv → verweigert mit neuem Code (Regel 7), idempotent, Audit, Nutzungsseite ohne Import danach, Testcontainers-Nachweis. Bindend per Nutzerentscheid 2026-10-13: §14. Die folgenden D1 ff. sind die Entscheidungen dieser Spec.

| # | Entscheidung | Verworfene Alternative / Begründung |
|---|---|---|
| **D1** | **Granularität = ganzer Kanal**, jedes Set, jeder Lauf (Nutzerentscheid F1). | Je Set / je Lauf: §14 F1. |
| **D2** | **Eigener Service** `IChatLogImportRemovalService` (Core `Services/`), Implementierung `ChatLogImportRemovalService` (Infrastructure `Services/`), eigene SQL-Texte in einer neuen internen Klasse `ChatLogImportRemovalSql` (Infrastructure `Services/`). | Methode an `IChatLogBackfillService`: genau diese Datei baut #379 Teil B gerade um; ein eigener Service hält beide Zweige konfliktarm und den Vertrag lesbar. Regel 4/5. |
| **D3** | **Hinter `ChatLogBackfill:Enabled`** (Nutzerentscheid F3): aus → 404 `backfill_disabled` nach dem Autorisierungsfilter, wie die drei bestehenden Routen. | §14 F3. |
| **D4** | **Lauf aktiv (`queued`/`running`/`paused`) → 409 `backfill_remove_run_active`**, geprüft unter der Kanalsperre; nichts wird geschrieben. Erst abbrechen. | Den Lauf mit abbrechen: zwei destruktive Wirkungen hinter einem Klick. `backfill_already_active` wiederverwenden: sein Text spricht vom Start. |
| **D5** | **Gelöscht wird:** alle `UsageStats`-Zeilen mit `Source = 1` der Emotes des Kanals (jedes Set, jedes Datum, mit oder ohne Coverage), alle `ChatLogBackfillCoverage`-Zeilen des Kanals, und jeder unsettled Seed, den genau dieses Löschen zeilenlos gemacht hat (D7). | Nur Zeilen an Coverage-Tagen: Zeilen ohne Coverage (Handarbeit) blieben unsichtbar und unbeschriftet stehen. |
| **D6** | **Nicht gelöscht** (Nutzerentscheid F2 (A)): Live-Zeilen (`Source = 0`), Lauf-Zeilen, Snapshot-Zeilen, Platzhalter-Emotes, **gesettelte** Slots — mit oder ohne `CreatedSlot`, auch wenn sie jetzt zeilenlos sind —, unsettled Seeds, die schon vorher zeilenlos waren, unsettled Seeds, die noch Zeilen tragen, Provider-Cooldown, Audit. **Ausnahme zu „`CreatedSlot` bleibt“:** ein unsettled Seed, den D7 trifft, fällt auch mit `CreatedSlot = true`; seine Snapshot-Zeile im beendeten Lauf bleibt stehen. | Läufe löschen: verlöre die Provenienz (`CreatedRow`/`CreatedSlot`), die Operations Schritt 3/3b braucht. Platzhalter mitlöschen: §14 F2 (B). |
| **D7** | **Seed-Abbau nur für selbst verursachte zeilenlose Seeds** (Kandidaten = die `SlotId`s der in Schritt 7 gelöschten Zeilen), Prädikat wie der Sweep aus Teil B (`SeedUnsettled AND NOT EXISTS UsageStats`), eingeschränkt auf die Kandidaten, **ohne** Blick auf `CreatedSlot`. | Kanalweit alle zeilenlosen unsettled Seeds: wäre die Arbeit des Sweeps. Gar kein Abbau oder `CreatedSlot` ausnehmen: hinterließe genau die zeilenlosen Seeds, die Teil B einmalig beseitigt — und der Sweep läuft nicht wieder (Sol-Befund 3). |
| **D8** | **Gesettelte Slots bleiben, auch zeilenlos** (`SeedUnsettled = false`, z. B. ein in place gesettelter Seed eines nicht aktiven Sets, ein umgebundener leerer Seed, ein Flag-an-`CreatedSlot`-Slot). | Der Sweep lässt sie ebenfalls stehen (Plan-379 §3); sie sind autoritative Identitäten, die REST oder eine spätere Vorschau wiederverwendet. |
| **D9** | **Idempotent:** hat der Kanal weder `Source = 1`-Zeilen noch Coverage → 200 mit Nullen, **kein** Audit, **keine** Events — **unabhängig von der mitgeschickten Erwartung** (geprüft vor D15). | Den No-op an die Erwartung binden: ein Doppelklick oder ein zweiter Manager mit derselben Anzeige bekäme 409 statt „schon erledigt“. |
| **D10** | **Sichtbarkeit im UI nach `status.coverage`** (nicht leer). Kein neues Feld am Status-DTO. | Neues `hasImportedData`: hieße `ChatLogBackfillStatusDto`/`GetStatusAsync` anfassen, also die Teil-B-Datei. Zeilen ohne Coverage sind Handarbeit (I1) und ohnehin unbeschriftet; dafür bleibt Operations. |
| **D11** | **Auch für nicht mehr beobachtete oder ausgeschlossene Kanäle** erlaubt (`IsBotActive`, `ExcludedChannelIds` werden nicht geprüft). | Löschen eigener Daten an das Tracking zu binden, wäre widersinnig; ein Art.-21-Ausschluss ist eher ein Grund mehr. |
| **D12** | **Kein Sperren des Antragsteller-Users** (anders als Enqueue). | Präzedenz `CancelAsync`: ein reiner Audit-Schreiber; der Audit-Eintrag wird bei Account-Löschung pseudonymisiert wie alle anderen. |
| **D13** | **Events nach Commit:** `backfill.progress` und `usage.flushed` für den Kanal, über `IRedisPublisher`, fehlertolerant wie beim Cancel. Kein neuer Event-Typ. | Neuer Typ `backfill.removed`: die bestehenden lösen genau die nötigen Refetches aus (§9). |
| **D14** | **Lauf-Historie bleibt unverändert sichtbar** (F4 nach Empfehlung); der Abschnitt „Importierte Daten“ verschwindet nach dem Entfernen. Das Wer/Wann steht im Aktivitäts-Tab (Audit). | §14 F4. |
| **D15** | **Die Bestätigung ist an den gezeigten Umfang gebunden — clientseitig und serverseitig** (Sol-Befund 2). Der Request trägt die Coverage, die der Dialog gezeigt hat (`expectedCoverage`, Intervalle wie im Status); der Server vergleicht sie unter der Kanalsperre **tagesgenau** mit der aktuellen Coverage des Kanals und antwortet bei jeder Abweichung 409 `backfill_remove_scope_changed`, ohne zu schreiben. Das UI bindet die Bestätigung zusätzlich an einen Schlüssel derselben Intervalle, wartet nach einem Abbruch auf den abgeschlossenen Status-Read und verlangt bei jeder Änderung eine neue Bestätigung (§10.4). | **Nur clientseitig** (Re-Read vor dem Senden): bleibt offen, wenn ein SSE-Event verloren geht oder ein Read zwischen Prüfung und Commit überholt wird — die gezeigte Coverage ist dann älter als die gelöschte. **Fingerprint-Token im Status-DTO**: bräuchte `GetStatusAsync` (Teil-B-Datei) und eine zweite, synchron zu haltende Zusammenfassungsregel. Die Tagesmenge braucht keine von beiden: Intervalle sind `[from, to)` mit Set und Host, der Server expandiert sie und vergleicht Mengen. |

## 5. Backend-Vertrag

### 5.1 Core-Interface

`IChatLogImportRemovalService.RemoveImportedAsync(string channelName, IReadOnlyList<ExpectedCoverageInterval> expectedCoverage, AuditActor actor, CancellationToken)` → `ChatLogImportRemovalResult`.

`ExpectedCoverageInterval(DateOnly From, DateOnly ToExclusive, string EmoteSetId, string ArchiveHost)` — dieselben Felder wie `ChatLogBackfillCoverageInterval` ohne den Namen.

| Status | Bedeutung | Felder |
|---|---|---|
| `Removed` | mindestens eine Zeile oder ein Coverage-Tag entfernt | `RemovedUsageRows` (int), `RemovedCoverageDays` (int), `RemovedSeeds` (int) |
| `NothingToRemove` | Kanal ohne `Source = 1`-Zeilen und ohne Coverage | alle 0 |
| `RunActive` | aktiver Lauf des Kanals; nichts geschrieben | — |
| `ScopeChanged` | aktuelle Coverage ≠ Erwartung; nichts geschrieben | — |
| `NotFound` | Kanal unbekannt (auch: während des Wartens auf die Sperre gepurgt) | — |

### 5.2 Route

`POST /api/channels/{channelName}/backfill/imported/remove` in `ChannelEndpoints.cs`, in der `/api/channels`-Gruppe (`RequireAuthorization` + `ChannelNameValidationFilter`), dazu `AddEndpointFilter<ChannelManagementAuthorizationFilter>()` (B1: Admin, Broadcaster, Live-Mod; kein 7TV-Editor) und `RequireRateLimiting(RateLimitPolicyNames.Bookkeeping)` (wie Cancel). `POST` statt `DELETE`, weil die Anfrage einen Body trägt (D15); `DELETE` mit Body wird von Zwischenstationen nicht verlässlich durchgereicht.

Body: `{ "expectedCoverage": [ { "from": "2026-04-08", "to": "2026-10-08", "emoteSetId": "01HQ…", "archiveHost": "logs.cyex.app" } ] }` — die Intervalle genau so, wie sie im Status unter `coverage` standen (Daten `yyyy-MM-dd`, `to` exklusiv). Eine leere Liste ist erlaubt.

| Status | Body | Wann |
|---|---|---|
| 200 | `{ "removedUsageRows": n, "removedCoverageDays": d }` | `Removed` und `NothingToRemove` (dann Nullen). `RemovedSeeds` geht nur ins Log. |
| 400 | `{ errorCode: "invalid_request_body" }` | Body fehlt, ist nicht bindbar, `expectedCoverage` fehlt oder ist `null`, ein Intervall mit `from ≥ to`, mehr als 400 Intervalle (die Coverage liegt höchstens in den 6 Monaten vor dem Zählbeginn, das sind < 190 Tage). Wiederverwendeter Code; die bindbaren Fälle prüft der Handler, die nicht bindbaren der globale Handler (`Program.cs:316`). |
| 401 | — | nicht angemeldet; oder Session ohne baubaren Audit-Akteur (`TryBuildAuditActor` = null), wie die übrigen Backfill-Routen |
| 403 | — | kein Manager (Filter) |
| 404 | `{ errorCode: "backfill_disabled" }` | Flag aus — **nach** dem Filter, damit ein 7TV-Editor 403 bekommt; vor der Body-Prüfung (Präzedenz `FlagOff_ChecksTheFlag_BeforeTheBody`) |
| 404 | `{ errorCode: "channel_not_found" }` | `NotFound` |
| 409 | `{ errorCode: "backfill_remove_run_active" }` | `RunActive` |
| 409 | `{ errorCode: "backfill_remove_scope_changed" }` | `ScopeChanged` |

Reihenfolge im Handler: Flag → Akteur → Body → Service. Der Handler bleibt dünn (Regel 11).

### 5.3 Transaktion, Sperren, Reihenfolge

Eine Transaktion (READ COMMITTED), als Wiederholungseinheit bei Deadlock (40P01) mit höchstens drei Versuchen und kurzer Zufallspause — Präzedenz Enqueue/Broadcaster-Purge. Ein erschöpfter Deadlock wird 500 (Problem), nichts ist geschrieben.

1. **Kanal sperren:** `LoadChannelForNoKeyUpdateAsync(channelName)` (die Leave-Ladefunktion, `ChannelQueries.cs:168`). `null` → `NotFound`. Damit serialisiert die Aktion mit Enqueue, Leave, REST-Abgleich und jedem `FOR UPDATE`-Pfad (Purge, Join, Merge), aber nicht mit FK-Einfügungen (Flush).
2. **Aktiver Lauf?** `EXISTS ChatLogBackfillRuns WHERE ChannelId = @id AND Status IN (queued, running, paused)` → `RunActive`. Der Zustand „kein aktiver Lauf“ ist unter dieser Sperre stabil: nur der Enqueue erzeugt einen aktiven Lauf, und der braucht dieselbe Sperre; alle Worker-Übergänge führen aktiv → aktiv oder aktiv → terminal. Mit I3 ist damit auch die Coverage für den Rest der Transaktion stabil.
3. **Betroffene Emotes bestimmen:** die `Id`s der Emotes des Kanals, die mindestens eine `Source = 1`-Zeile tragen.
4. **Nichts da?** Emote-Menge leer **und** keine Coverage-Zeile des Kanals → `NothingToRemove` (Rollback, kein Audit), ohne die Erwartung anzusehen (D9).
5. **Erwartung prüfen (D15):** die Erwartung wird in Tage expandiert (`From` bis `ToExclusive − 1`, je Tag `(Day, EmoteSetId, ArchiveHost)`); überlappende Intervalle in der Erwartung zählen als Abweichung. Die aktuelle Coverage des Kanals als Menge derselben Tripel lesen. Ungleich (ein Tag mehr, weniger, anderes Set, anderer Host) → `ScopeChanged`, Rollback. Gleich → weiter. Zeilen ohne Coverage-Tag (Handarbeit, I1) sind nicht Teil der Erwartung und fallen trotzdem (D5).
6. **Emote-Zeilen sperren:** `RowLocks.LockEmotesByIdsAsync` (`FOR NO KEY UPDATE`, sortiert mit `COLLATE "C"`). Zweck: Serialisierung mit dem Settle (`MoveSeedUsageAsync`/`MoveSeedSnapshotEntriesAsync` unter denselben Emote-Sperren) und mit einem noch laufenden Block-Commit, der die Emotes `FOR SHARE` hält (§6.3). Die Menge aus Schritt 3 ist stabil: importierte Zeilen schreibt nur der Block-Commit eines **aktiven** Laufs, und ein Settle verschiebt Zeilen nur zwischen Slots **desselben** Emotes.
7. **Zeilen löschen:** ein Statement `DELETE FROM "UsageStats" u USING "Emotes" e WHERE u."EmoteId" = e."Id" AND e."ChannelId" = @channelId AND u."Source" = 1 RETURNING u."SlotId"`, im selben Statement je `SlotId` gezählt (datenverändernde CTE + `GROUP BY`). Ergebnis: Summe = `RemovedUsageRows`, die Schlüsselmenge = Seed-Kandidaten. Der Kanalfilter ist maßgeblich, nicht die Sperrmenge aus 3.
8. **Zeilenlos gewordene unsettled Seeds löschen:** **eigenes, zweites Statement** (eine CTE im selben Statement sähe die Löschung aus 7 nicht — alle Teile eines Statements lesen denselben Snapshot): `DELETE FROM "EmoteSlots" s WHERE s."Id" = ANY(@candidates) AND s."SeedUnsettled" AND NOT EXISTS (SELECT 1 FROM "UsageStats" x WHERE x."SlotId" = s."Id")` → `RemovedSeeds`. **Kein** Prädikat auf `CreatedSlot`, `IsArchived`, `ArchivedAt` (D7). Kaskade: keine (der Slot hat keine Zeilen mehr). Snapshot-Zeilen beendeter Läufe, die auf ihn zeigen — auch eine mit `CreatedSlot = true` —, bleiben ohne Ziel stehen: zulässig, `SlotId` hat keine FK (Plan-379 §3); gelesen wird sie nur für aktive Läufe und von Operations 3b, das den Slot voraussetzt (findet ihn dann nicht mehr — er ist ja schon weg).
9. **Coverage löschen:** `DELETE FROM "ChatLogBackfillCoverage" WHERE "ChannelId" = @channelId` → `RemovedCoverageDays`.
10. **Audit** (§7) nur, wenn `RemovedUsageRows + RemovedCoverageDays > 0`; `SaveChanges`; Commit.
11. **Nach dem Commit:** `backfill.progress` und `usage.flushed` für den normalisierten Kanalnamen (D13), nur bei `Removed`. Eine Redis-Störung macht das Ergebnis nicht zum Fehler (Präzedenz `PublishSafelyAsync`).
12. **Log** (englisch): eine Info-Zeile mit Kanal, `RemovedUsageRows`, `RemovedCoverageDays`, `RemovedSeeds`; bei `ScopeChanged` eine Info-Zeile mit Kanal und den beiden Tageszahlen (erwartet/aktuell).

Die Texte aus 7–9 stehen als Konstanten in `ChatLogImportRemovalSql` (D2) — keine Änderung an `EmoteSlotRollbackSql` oder der Teil-B-Sweep-Konstante. Ein Test hält fest, dass das Prädikat aus 8 auf den Kandidaten dieselbe Menge trifft wie der Teil-B-Sweep (§11.1 S6).

### 5.4 Was in welcher Reihenfolge fällt

| Schritt | Zeilen | Kaskaden |
|---|---|---|
| 7 | `UsageStats` `Source = 1` des Kanals | keine (Blatt) |
| 8 | `EmoteSlots` mit `SeedUnsettled`, deren letzte Zeile in 7 fiel — `CreatedSlot` egal | `UsageStats.SlotId` (leer) |
| 9 | `ChatLogBackfillCoverage` des Kanals | keine |
| — | **bleibt:** `Source = 0`, Läufe, Snapshots (auch solche, deren `SlotId` nach 8 ins Leere zeigt), `Emotes` (Platzhalter inklusive), alle gesettelten `EmoteSlots`, unsettled Seeds mit verbliebenen Zeilen oder schon vorher zeilenlos, `ChatLogBackfillProviderState`, Votes/Ballots, Tags | — |

### 5.5 Rückfrage an die eigene Invariante

Nach Commit gilt für den Kanal: `count(UsageStats Source=1) = 0`, `count(Coverage) = 0`, und die Zahl der `EmoteSlots` des Kanals mit `SeedUnsettled AND NOT EXISTS UsageStats` ist **nicht größer** als vorher (AK 7). Ein zeilenloser unsettled Seed, der danach existiert, existierte schon vorher und gehört dem Sweep.

## 6. Nebenläufigkeit

### 6.1 Gegen einen startenden Lauf
Enqueue und Entfernen nehmen dieselbe Kanalsperre (`FOR NO KEY UPDATE`) und prüfen beide unter ihr. Entweder committet der Enqueue zuerst → das Entfernen sieht den aktiven Lauf → 409 `backfill_remove_run_active`. Oder das Entfernen committet zuerst → der Enqueue läuft danach gegen einen Kanal ohne Import. Der partielle Unique-Index bleibt das letzte Wort für „ein aktiver Lauf je Kanal“; das Entfernen schreibt keinen Lauf.

### 6.2 Gegen einen Lauf, der hinter dem Dialog fertig wird (Sol-Befund 2)
Ein zweiter Manager startet einen Lauf, während der Dialog offen ist, und der Lauf ist fertig, bevor bestätigt wird. Die Coverage ist dann größer als gezeigt. Erreicht der Client die Änderung (SSE `backfill.progress` → Refetch), verlangt er eine neue Bestätigung (§10.4). Erreicht sie ihn nicht (Event verloren, Read überholt), schickt er die alte Erwartung; der Server findet mehr Tage und antwortet 409 `backfill_remove_scope_changed`, ohne zu löschen. Läuft der Lauf noch, antwortet er 409 `backfill_remove_run_active`.

### 6.3 Gegen den letzten Block eines gerade abgebrochenen Laufs
Ablauf: Abbruch committet → Manager klickt „Entfernen“ → der Worker steckt noch in `ReplaceBlockAsync` (Kanal `KEY SHARE`, Emotes `FOR SHARE`, ggf. schon Schritt 1–3).
- Das UI sendet erst nach dem abgeschlossenen Post-Cancel-Read (§10.4); die gezeigte Coverage enthält dann jeden Block, der vor dem Abbruch committet hat.
- Ein Block, dessen Schritt 4 **vor** dem Abbruch lief, hat committet, bevor der Abbruch committen konnte (Zeilensperre auf dem Lauf). Fehlt er in der gezeigten Coverage (das UI hätte zu früh gelesen), fängt Schritt 5 das als `ScopeChanged` ab.
- Ein Block nach dem Abbruch scheitert an seinem Schritt 4 (`Status = 'running'`) und rollt zurück. Hält er die Emote-Sperren schon, wartet das Entfernen in Schritt 6 auf sein Ende. Hat das Entfernen die Emote-Sperren zuerst, wartet der Block in `LockEmotesForShareByIdsAsync`, das Entfernen committet, der Block rollt danach zurück.
- Teilen sich Block-Aggregate und importierte Emotes keine Emote-Zeile, kommt das Entfernen bis Schritt 7 und wartet dort auf die Zeilensperren aus Block-Schritt 1; der Block braucht nichts, was das Entfernen hält, scheitert an Schritt 4 und gibt frei. Seine nicht committeten Coverage-Zeilen sieht Schritt 5 nicht.
- Kein Zyklus in keiner Reihenfolge: Das Einzige, worauf der Block beim Entfernen warten kann, sind dessen Emote-Sperren — und die verlangt der Block nur in seinem Sperrschritt, **bevor** er eine `UsageStats`-Zeile anfasst. Das Entfernen fasst `UsageStats` erst an, wenn es alle Emote-Sperren hat. FK-`KEY SHARE` (Block) verträgt sich mit `NO KEY UPDATE` (Entfernen), auf dem Kanal wie auf den Emotes.
- Sieht das Entfernen den Lauf noch als `running` (Block hat Schritt 4 geschrieben, nicht committet), antwortet es konservativ 409 `backfill_remove_run_active`; ein erneuter Versuch Sekunden später geht durch.

### 6.4 Gegen den REST-Abgleich und das Settle
Der REST-Abgleich sperrt den Kanal `FOR NO KEY UPDATE` (`SevenTvSyncService.cs:831, :1033`) → vollständige Serialisierung über Schritt 1. Zusätzlich serialisieren die Emote-Sperren aus Schritt 6 jeden Schreiber, der Emotes ohne vorherige Kanalsperre sperrt. Fällt ein Settle davor, hat es Zeilen verschoben (Quelle-Seed → Ziel) und den Seed auf `SeedUnsettled = false` gesetzt; das Entfernen löscht die verschobenen `Source = 1`-Zeilen am Ziel und lässt den gesettelten Seed stehen (D8). Fällt es danach, findet es keine importierten Zeilen mehr zu verschieben.

### 6.5 Gegen den Live-Flush
Der Flush schreibt nur `Source = 0` mit dem heutigen Datum (I2) und nur auf gesettelte aktive Slots der Match-Map — nie auf einen unsettled Seed (nach Teil B). Seine FK-`KEY SHARE` kollidiert mit keiner Sperre des Entfernens. Ein Flush während des Entfernens ist unabhängig; seine Zeilen sind nach dem Commit bytegleich da.

### 6.6 Gegen Leave, Purge, Rename, Account-Löschung
- Leave: dieselbe Kanalsperre → seriell; danach ist Entfernen weiter erlaubt (D11).
- Purge: `FOR UPDATE` → seriell; gewinnt der Purge, ist das Entfernen `NotFound` (404).
- Rename während des Dialogs: die Route adressiert per Name; der alte Name ist 404, die UI lädt neu.
- Account-Löschung des Antragstellers: wie beim Cancel (D12).

### 6.7 Zwei Manager gleichzeitig
Seriell über Schritt 1; der zweite sieht `NothingToRemove` → 200 mit Nullen, kein zweites Audit (D9 vor D15).

### 6.8 Dauer und Sperrzeit
Die Aktion hält die Kanalsperre für die Dauer der Löschung. Der größte denkbare Kanal (6 Monate, ~900 Slots) hat höchstens einige 100 000 importierte Zeilen; gemessen wird das im Live-Ablauf (§13) auf der Dev-DB und als Dev-Zahl berichtet. Während der Sperre wartet der REST-Abgleich dieses Kanals (er holt im nächsten Takt auf); Flush und Lesepfade warten nicht. Kein eigener `lock_timeout` (Präzedenz Enqueue/Leave).

## 7. Audit

- Neue Aktion `AuditActions.BackfillRemoveImported = "backfill.removeImported"` (Core `Entities/AuditLogEntry.cs`, neben `BackfillRequest`/`BackfillCancel`).
- Eintrag: `channelName`, kein Ziel, Details `{ backfillRemovedDays: d }` über eine neue Detail-Art `AuditLogDetail.Kinds.BackfillRemovedDays = "backfillRemovedDays"` (Count-Form wie `backfillMonths`), projiziert in `AuditLogQueryService`.
- Die Zeilenzahl kommt **nicht** ins Audit (Tage sind die Einheit, die der Nutzer versteht und die Coverage zählt).
- Frontend: `audit-actions.ts` + `audit.actions.backfillRemoveImported` („Importierte Daten entfernt“ / „Imported data removed“), `audit.details.backfillRemovedDays` („{{count}} Tage“ mit Pluralformen) in de/en; der Aktivitätsfilter des Kanals listet die Aktion.
- Kein Audit bei No-op (D9) und bei 409.
- Pseudonymisierung bei Account-Löschung und Retention (365 Tage) greifen ohne Änderung.

## 8. Fehlercodes (Regel 7)

Zwei neue Codes in `ApiErrorCodes.cs` (Block „Chat-log backfill“), in `web/src/app/core/i18n/api-error.ts` und in beiden Locale-Dateien unter `errors.api.*`:

| Code | de | en |
|---|---|---|
| `backfill_remove_run_active` (409) | „Für diesen Channel wartet oder läuft gerade ein Backfill. Brich ihn zuerst ab, dann kannst du die importierten Daten entfernen.“ | „A backfill is queued or running for this channel. Cancel it first, then you can remove the imported data.“ |
| `backfill_remove_scope_changed` (409) | „Die importierten Daten haben sich inzwischen geändert. Sieh dir den neuen Stand an und bestätige erneut.“ | „The imported data has changed in the meantime. Check the new state and confirm again.“ |

Wiederverwendet: `backfill_disabled`, `channel_not_found`, `invalid_request_body`.

## 9. SSE und Wirkung auf offene Seiten

| Event | Empfänger | Wirkung |
|---|---|---|
| `backfill.progress` | Settings-Tab (`backfill-section.ts:557-561`) | Status-Refetch → `coverage` leer → Abschnitt „Importierte Daten“ verschwindet |
| `backfill.progress` | Nutzungsseite (`usage-stats-page.ts:2962-2964`) | `importCoverageResource.reload()` → Beschriftung fällt auf „Wir zählen seit …“ zurück; `coverageStart` = Zählbeginn, also kommen Vor-Zählbeginn-Warnung und Trend-Unterdrückung wie ohne Import zurück; das „Gesamt“-Preset beginnt wieder am Zählbeginn |
| `usage.flushed` | Nutzungsseite | Zeilen/Summen neu ohne importierte Tage |

Nicht nötig: `channel.synced` (ein gelöschter Seed war zeilenlos und taucht in keiner Ansicht auf). Rollen-Cache: unberührt. Live-Badge, Worker-Health: unberührt. Ballots und Export lesen `UsageStats` live (D30) und zeigen danach Summen ohne importierte Tage.

## 10. Frontend

### 10.1 Service und Modell
`BackfillService.removeImported(channelName, expectedCoverage)` → `POST /api/channels/{normalized}/backfill/imported/remove` mit `{ expectedCoverage }`; `expectedCoverage` sind die Intervalle aus `status.coverage` ohne `emoteSetName` (`from`, `to`, `emoteSetId`, `archiveHost`). Antworttyp `BackfillRemovalResult { removedUsageRows: number; removedCoverageDays: number }` in `backfill.model.ts`.

### 10.2 Platz im UI (Nutzerentscheid 2026-10-13)
Ein **eigener Abschnitt „Importierte Daten“** im Settings-Tab des Kanal-Workspace, **unterhalb** des Backfill-Abschnitts (nach „Letzter Lauf“), mit eigener Überschrift (`<section aria-labelledby>`, `h3` wie „Backfill aus dem Chat-Archiv“). Sichtbar nur, wenn `status.coverage.length > 0` (D10). Er wird von `BackfillSection` gerendert und teilt dessen Status-Resource, Post-Cancel-Gate und Request-Token — ein zweiter Status-Read oder eine zweite Quelle für `activeRun` wäre genau die Lücke aus Sol-Befund 2. Der Tab selbst bleibt Managern mit Flag an vorbehalten (D17, Guard unverändert).

Inhalt:
- Eine Zeile je Coverage-Intervall: Zeitraum (`to` exklusiv → Anzeige `to − 1 Tag`, wie die Lauf-Fenster) und Set (`emoteSetName ?? emoteSetId`), z. B. „08.04.2026 – 07.10.2026 · Normal“.
- Ein Satz, dass diese Tage aus dem Chat-Archiv stammen und auf der Nutzungsseite so beschriftet sind.
- Knopf „Importierte Daten entfernen“ (`appButton="danger"`).
- Gesperrt, solange `status.activeRun` existiert, mit sichtbarem Grund darunter, per `aria-describedby` verbunden: „Brich zuerst den laufenden Backfill ab.“ Gesperrt ohne eigenen Grundtext, solange der Post-Cancel-Read aussteht (kurz) oder eine Start-/Entfernen-Anfrage läuft.
- Fehler der Anfrage als `app-notice-banner variant="error"` direkt unter dem Knopf (Muster Cancel), geleert beim nächsten Versuch.
- Nach Erfolg: Ansage im vorhandenen `role="status"`-Bereich („Importierte Daten entfernt: {{days}} Tage.“), dann verschwindet der Abschnitt mit dem Refetch.

Die Lauf-Historie bleibt (D14).

### 10.3 Bestätigungsdialog
`openConfirmDialog` (der `DialogActionRow`-Weg der destruktiven Aktionen; Text mit `whitespace-pre-line`). Der Text wird aus **denselben** Intervallen gebaut, die als `expectedCoverage` gesendet werden:

> **Importierte Daten dieses Channels entfernen?**
> Entfernt werden die aus dem Chat-Archiv importierten Zahlen für:
> · 08.04.2026 – 07.07.2026 · Halloween
> · 08.07.2026 – 07.10.2026 · Normal
> und damit der Hinweis „aus dem Chat-Archiv“ auf der Nutzungsseite.
> Bleiben: alle Zahlen, die wir selbst seit dem {{countingSince}} zählen, und die Liste der bisherigen Läufe.
> Das lässt sich nicht rückgängig machen. Ein neuer Backfill liest das Archiv neu; seine Zahlen können abweichen.

Bestätigen-Beschriftung: „Importierte Daten entfernen“. Abbrechen sendet nichts.

### 10.4 Entscheidungslogik (Regel 12, `computed()`)
- `importedIntervals` = `status.coverage` mit Anzeige-Ende.
- `removalScopeKey` = Kanalname + kanonische Form der Intervalle (`from|to|emoteSetId|archiveHost`, in Status-Reihenfolge). Ist zugleich die Quelle von Dialogtext und `expectedCoverage`.
- `removeVisible` = `importedIntervals.length > 0`.
- `canRemove` = `removeVisible && !activeRun && !awaitingPostCancelRead && !removing && !starting`. Das Post-Cancel-Gate ist dasselbe, das `canStart` schon nutzt (`backfill-section.ts:494-511`).
- `removeBlockedReasonKey` = `activeRun ? 'backfill.remove.blockedByRun' : null`.
- `removing` als Request-Token wie `cancelRequest` (Schlüssel Kanal|aktiver Lauf), damit eine späte Antwort für einen anderen Kanal nichts anwendet.
- `canStart` wird um `!removing` erweitert (ein Start während des Entfernens würde gegen einen sich ändernden Zustand starten).
- **Bindung der Bestätigung (Muster `startPlanKey`, `:587-596`):** beim Öffnen `removalScopeKey` festhalten; nach „Bestätigen“ nur senden, wenn `canRemove` noch gilt **und** der Schlüssel gleich ist. Sonst nichts senden und unter dem Knopf `errors.api.backfill_remove_scope_changed` zeigen; der Nutzer sieht den neuen Stand und klickt erneut.
- Antworten: 200 → Ansage, `statusResource.reload()`; 409 `backfill_remove_run_active` oder `backfill_remove_scope_changed` → Fehlertext + Refetch; 404 `backfill_disabled`/`channel_not_found`, 0/5xx → Fehlertext + Refetch (Ausgang unbekannt, die Antwort kann nach dem Commit verloren sein).

### 10.5 i18n (de/en)
`backfill.remove.title`, `.intro`, `.interval`, `.action`, `.blockedByRun`, `.confirm` (Kopf, Intervallzeile, Rest mit Parametern), `.announceDone` (Plural über `pluralKey`), dazu `errors.api.backfill_remove_run_active`, `errors.api.backfill_remove_scope_changed`, `audit.actions.backfillRemoveImported`, `audit.details.backfillRemovedDays`. Deutsch duzt wie der Bestand.

### 10.6 Nutzungsseite
Kein Code-Eingriff erwartet: die vorhandenen Refetch-Pfade (§9) tragen. Geprüft wird es in E2E und live (AK 15). Bricht ein Pfad bei leerer Coverage nach vorher gefüllter (z. B. das „Gesamt“-Preset), ist das ein Fehler dieser Spec und wird im selben PR behoben.

## 11. Teststrategie

### 11.1 Infrastructure.Tests (Testcontainers, `Integration/ChatLogImportRemovalServiceTests.cs`)
Eigene Testklasse, eigene Seeding-Helfer über `Fixtures/` (die `ChatLogBackfillServiceTests`-Teilklassen baut Teil B gerade um; keine Kopplung daran). Fälle:

- **S1 Live bytegleich:** Kanal mit Live- und Importzeilen auf denselben Emotes, mehreren Slots und zwei Sets → alle `Source = 0`-Zeilen (sämtliche Spalten inkl. `Id`, `SlotId`) vorher = nachher.
- **S2 Andere Kanäle:** zweiter Kanal mit Import, Coverage, Läufen, Seeds → vollständig unverändert.
- **S3 Ergebnis:** `Source = 1` des Kanals = 0, Coverage = 0, Zähler im Ergebnis stimmen, `GetCoverageAsync(AllSets)` und `(Set)` leer.
- **S4 Aktiver Lauf:** je `queued`, `running`, `paused` → `RunActive`, nichts verändert, kein Audit, kein Publish.
- **S5 Idempotenz:** zweiter Aufruf mit **derselben, jetzt veralteten** Erwartung → `NothingToRemove`, kein Audit, kein Publish; Kanal ohne je Import mit leerer Erwartung ebenso.
- **S6 Seeds:** (a) unsettled Seed nur mit `Source = 1` → gelöscht; (b) unsettled Seed mit `Source = 0` und `1` → bleibt mit seinen Live-Zeilen; (c) gesettelter Slot, jetzt zeilenlos → bleibt; (d) **gesettelter** `CreatedSlot`-Slot (Flag-an-Enqueue, archiviert, `ArchivedAt` null, `SeedUnsettled = false`) → bleibt; (e) unsettled Seed, schon vorher zeilenlos → bleibt; (f) Seed eines anderen Kanals mit nur importierten Zeilen → bleibt. Zusätzlich: auf den Kandidaten trifft das Prädikat dieselbe Menge wie die Teil-B-Sweep-Konstante (sobald sie auf main ist; bis dahin gegen das in Plan-379 §3 festgeschriebene Prädikat).
- **S16 Alt-Seed mit `CreatedSlot` (Sol-Befund 3):** Zustand wie `ChatLogBackfillServiceTests.Slots.cs:25-68` — Flag-aus-Enqueue legt Platzhalter + unsettled Basis-Seed an (`SeedUnsettled`, archiviert, `ArchivedAt` null, Snapshot `CreatedSlot = true`, `CreatedRow = true`), ein Block schreibt importierte Zeilen darauf, der Lauf endet terminal. Seeding direkt in der DB (nach Teil B gibt es den Flag-aus-Pfad nicht mehr). Nach dem Entfernen: der Seed ist **weg**, die Snapshot-Zeile des beendeten Laufs **bleibt** unverändert (inkl. `CreatedSlot`/`CreatedRow`, `SlotId` zeigt ins Leere), der Platzhalter bleibt, Operations Schritt 3 findet ihn weiterhin.
- **S7 Inventar bleibt:** Platzhalter (`IsPlaceholder`, `CreatedRow`), Lauf- und Snapshot-Zeilen unverändert; danach finden das Operations-Schritt-3-Prädikat die Platzhalter und `EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql` die gesettelten `CreatedSlot`-Slots weiterhin (Provenienz intakt).
- **S8 Audit:** genau ein Eintrag `backfill.removeImported` mit Kanal und `backfillRemovedDays = d`; projiziert durch `AuditLogQueryService`.
- **S9 Publish:** nach Commit `backfill.progress` und `usage.flushed` mit normalisiertem Namen; Redis-Ausfall → Ergebnis trotzdem `Removed`.
- **S10 Rennen gegen Enqueue** (Barriere wie `ChatLogBackfillServiceTests.Races`): beide Reihenfolgen; entweder 409 ohne Schreiben oder Lauf entsteht nach dem Entfernen; nie Lauf + gelöschte Daten des neuen Laufs.
- **S11 Rennen gegen den Block eines abgebrochenen Laufs:** Block hält Emote-`FOR SHARE` und hat Schritt 1–3 geschrieben, Abbruch committet, Entfernen startet mit der Coverage vor dem Block → kein Deadlock, nach beiden Enden keine `Source = 1`-Zeile des Blocks, Lauf `cancelled`. Variante: der Block hat vor dem Abbruch committet, die Erwartung kennt ihn nicht → `ScopeChanged`, nichts gelöscht.
- **S12 Rennen gegen Settle:** `SeedSettleWrites.MoveSeedUsageAsync` auf einem Seed mit importierten Zeilen parallel → keine `Source = 1`-Zeile übrig, kein neuer zeilenloser unsettled Seed, kein 40P01 nach außen.
- **S13 Neuer Lauf danach:** Enqueue desselben Sets/Fensters nach dem Entfernen → bindet die vorhandenen Slots und Platzhalter (keine doppelte `Emote`-Zeile, Unique-Index), ein Block committet, Coverage neu.
- **S14 Nicht aktiv / ausgeschlossen:** `IsBotActive = false` bzw. Twitch-ID auf der Ausschlussliste → Entfernen funktioniert (D11).
- **S15 Coverage ohne Zeilen / Zeilen ohne Coverage:** beide werden entfernt (Erwartung = die vorhandene Coverage bzw. leer), `Removed`.
- **S17 Erwartung (D15):** gegen eine Coverage aus zwei Sets mit Lücke — exakt gleiche Erwartung → `Removed`; ein Tag mehr, ein Tag weniger, anderes Set an einem Tag, anderer Host, überlappende Intervalle, leere Erwartung bei vorhandener Coverage → je `ScopeChanged`, nichts verändert, kein Audit, kein Publish. Gleiche Tagesmenge in anderer Intervallzerlegung (z. B. zwei angrenzende Intervalle statt eines) → `Removed` (verglichen werden Tage, nicht Intervalle).

### 11.2 Api.Tests
- `ApiFactory`: `IChatLogImportRemovalService` substituieren (die `RequestDelegateFactory` löst Handler-Services **vor** den Filtern auf).
- `AuthFilterMatrixTests`: `POST /api/channels/{0}/backfill/imported/remove` in die Theorien für 401 (anonym) und 403 (Nicht-Manager inkl. 7TV-Editor) und in die Erfolgs-Tabelle (→ 200).
- `EmoteRoutePolicyTests`: Route → `Bookkeeping`.
- Neue `ChatLogImportRemovalEndpointTests`: Flag aus → 404 `backfill_disabled` ohne Service-Aufruf und vor der Body-Prüfung, aber 403 für Nicht-Manager; 400 `invalid_request_body` für fehlenden Body, `expectedCoverage: null`, `from ≥ to`, > 400 Intervalle — ohne Service-Aufruf; Erwartung wird unverändert durchgereicht (Datumsformat, `to` exklusiv); Abbildung `Removed`/`NothingToRemove` → 200 mit Drahtform (camelCase, zwei Felder, kein `removedSeeds`); `RunActive`/`ScopeChanged` → 409 mit dem jeweiligen Code; `NotFound` → 404 Code; Akteur wird durchgereicht.
- Kein neuer `IEndpointFilter`, keine geänderte Filterreihenfolge (Regel 11 nur über die Matrix).

### 11.3 Web (Vitest, Regel 12: Verhalten, keine Vorlage)
- `backfill.service.spec.ts`: `removeImported` sendet `POST` auf die normalisierte URL mit `expectedCoverage` ohne `emoteSetName` (HttpTestingController).
- `backfill-section.spec.ts`:
  - `removeVisible` aus `coverage`; `canRemove` falsch bei aktivem Lauf, während `removing`, während `starting`; Sperrgrund-Schlüssel.
  - **Verzögerter Post-Cancel-Read:** nach Cancel-204 ist `activeRun` null, `canRemove` bleibt falsch, bis der nächste Status-Read abgeschlossen ist (Antwort oder Fehler); erst dann wahr.
  - **Coverage ändert sich hinter dem Dialog:** Dialog öffnen, Status-Refetch liefert mehr Coverage, bestätigen → keine Anfrage, `scope_changed`-Schlüssel sichtbar; neuer Klick → Dialog mit neuem Text, Bestätigung sendet die neue Erwartung.
  - Dialog abgebrochen → keine Anfrage; bestätigt → genau eine Anfrage, deren `expectedCoverage` den Dialogintervallen entspricht.
  - 409 `backfill_remove_run_active` / `backfill_remove_scope_changed` → jeweiliger Fehler-Schlüssel und Status-Refetch; 200 → Ansage-Schlüssel mit Tageszahl und Refetch; späte Antwort nach Kanalwechsel → verworfen; `canStart` falsch während `removing`.
- `api-error-locales.spec.ts` deckt beide neuen Codes automatisch ab; Audit-Schlüssel in beiden Locales.

### 11.4 E2E (Playwright, `/api/**` gemockt, `web/e2e/channel-settings.e2e.spec.ts`)
- Settings-Tab mit Coverage → Abschnitt „Importierte Daten“ unter dem Backfill-Abschnitt sichtbar → Entfernen → Dialog → bestätigen → gemocktes 200 (Request-Body geprüft) → nächster Status ohne Coverage → Abschnitt weg, Ansage im Status-Bereich.
- Aktiver Lauf → Knopf gesperrt, Grund sichtbar.
- 409 `backfill_remove_scope_changed` → Fehlerbanner, Status neu geladen.
- Nutzungsseite: `import-coverage` erst mit, nach `backfill.progress` (gemocktes SSE oder Neuladen) ohne Import → Beschriftung „seit …“ ohne Archiv-Satz.
- Mocks in `web/e2e/support/mocks.ts` (Teil B überarbeitet dort Kommentare `:1244-1254` — Merge-Stelle, §15).

### 11.5 Gates
`dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (ohne Api auf `:5151`), `node scripts/coverage-local.mjs`, Format/Lint. Dazu Live-Ablauf §13 (Regel 16).

## 12. Akzeptanzkriterien (pass/fail)

1. `POST /api/channels/{c}/backfill/imported/remove`: anonym 401; angemeldet ohne Managerrecht (auch 7TV-Editor) 403; Flag aus 404 `backfill_disabled` (nach dem Filter, vor dem Body); unbekannter Kanal 404 `channel_not_found`; ungültiger Body 400 `invalid_request_body`.
2. Erfolg mit der aktuellen Coverage als Erwartung: 200 `{ removedUsageRows, removedCoverageDays }`; danach für den Kanal 0 Zeilen `Source = 1` und 0 Coverage-Zeilen.
3. Alle `Source = 0`-Zeilen des Kanals sind in allen Spalten unverändert.
4. Andere Kanäle: `UsageStats`, Coverage, `EmoteSlots`, Läufe, Snapshots unverändert.
5. Aktiver Lauf (`queued`/`running`/`paused`) → 409 `backfill_remove_run_active`; nichts geschrieben, kein Audit, kein Event.
6. Zweiter Aufruf (auch mit der alten Erwartung) → 200 mit Nullen; kein Audit, kein Event.
7. Kein neuer zeilenloser unsettled Seed: die Zahl `SeedUnsettled AND NOT EXISTS UsageStats` der Slots des Kanals ist danach ≤ vorher; jeder unsettled Seed, der nur importierte Zeilen trug, ist weg — **auch mit `CreatedSlot = true`**; ein unsettled Seed mit Live-Zeilen bleibt.
8. Platzhalter-Emotes, **gesettelte** Slots (mit oder ohne `CreatedSlot`), Lauf- und Snapshot-Zeilen bleiben; Operations Schritt 3/3b finden die Platzhalter und die gesettelten `CreatedSlot`-Slots weiter.
9. Alt-Seed (S16): ein unsettled Seed mit `CreatedSlot = true` und nur importierten Zeilen wird gelöscht; die Snapshot-Zeile seines beendeten Laufs bleibt unverändert stehen.
10. Genau ein Audit-Eintrag `backfill.removeImported` mit Kanal und `backfillRemovedDays`; im Aktivitäts-Tab de/en beschriftet.
11. Nach Commit `backfill.progress` und `usage.flushed` für den Kanal.
12. Nebenläufigkeit S10–S12 grün; kein 40P01 erreicht den Client (Wiederholung).
13. Ein neuer Lauf desselben Sets/Fensters nach dem Entfernen läuft ohne doppelte `Emote`-Zeile durch (S13).
14. Erwartung ≠ aktuelle Coverage (Tag mehr/weniger, Set, Host, Überlappung) → 409 `backfill_remove_scope_changed`, nichts geschrieben; gleiche Tagesmenge in anderer Zerlegung → Erfolg (S17).
15. Nutzungsseite ohne Neuladen: Beschriftung zurück auf „Wir zählen seit …“, Summen ohne importierte Tage, Vor-Zählbeginn-Warnung und „Gesamt“-Preset folgen wieder dem Zählbeginn.
16. Settings-Tab: Abschnitt „Importierte Daten“ unter dem Backfill-Abschnitt, nur mit Coverage; gesperrt mit Grund bei aktivem Lauf; gesperrt bis zum abgeschlossenen Post-Cancel-Read; Dialog nennt Intervalle mit Sets und das Bleibende; Abbrechen sendet nichts; eine Coverage-Änderung hinter dem Dialog verlangt eine neue Bestätigung; 409 zeigt den jeweiligen Fehlertext; Erfolg wird angesagt und der Abschnitt verschwindet; Start und Entfernen sperren sich gegenseitig, solange eine Anfrage läuft.
17. Regel 7: beide Codes in `ApiErrorCodes`, `api-error.ts`, de, en; `api-error-locales.spec.ts` grün.
18. Ballots und Export zeigen danach die Summen ohne importierte Tage (D30 liest `UsageStats` live; kein Code, nur Prüfung).
19. Keine Migration: `dotnet ef migrations list` zeigt nach dem PR dieselbe Liste wie davor; kein neuer Eintrag unter `Migrations/`.
20. Operations- und DECISIONS-Text wie §16.
21. Alle Gates aus §11.5 grün, `coverage-local.mjs` ≥ 80 % auf den neuen Dateien.

## 13. Live-Verifikation (Regel 16, lokal, Dev-Stack)

Voraussetzung: PR B (#379 Teil B) auf dem Branch bzw. darunter, Dev-DB durch die Zulassungsfolge und den einmaligen Sweep gegangen, `ChatLogBackfill:Enabled = true` lokal in `.env` (vor dem Start prüfen). Keine Prod-Verbindung. Alle Zahlen sind Dev-Zahlen und werden so berichtet.

1. Kanal mit vorhandenem Import auf der Dev-DB wählen, möglichst einen mit Importen aus der Flag-aus-Zeit (unsettled `CreatedSlot`-Seeds, S16 live) — oder einen 1-Monats-Lauf auf einem eigenen Testkanal fahren und abwarten.
2. Vorher per psql festhalten: `count(*)` `Source = 1` des Kanals; Coverage-Zeilen des Kanals; `md5(string_agg(... ORDER BY "Id"))` über alle `Source = 0`-Zeilen des Kanals; derselbe Hash über **alle** `UsageStats` aller anderen Kanäle; Zahl der zeilenlosen unsettled Seeds des Kanals und global; Zahl der unsettled Seeds des Kanals, die nur `Source = 1`-Zeilen tragen, davon mit `CreatedSlot`.
3. Nutzungsseite des Kanals in einem zweiten Tab offen lassen (Beschriftung mit Archiv-Link sichtbar).
4. 409-Pfade: einen Lauf starten, „Entfernen“ ist gesperrt; per `curl` mit Session-Cookie und der aktuellen Coverage als Erwartung → 409 `backfill_remove_run_active`. Lauf abbrechen; direkt danach ist der Knopf bis zum Post-Cancel-Read gesperrt. Per `curl` mit einer um einen Tag gekürzten Erwartung → 409 `backfill_remove_scope_changed`, Zählungen unverändert.
5. Im Settings-Tab entfernen, Dialog bestätigen. Zeit bis zur Antwort notieren (Dev-Zahl).
6. Nachher: `Source = 1` des Kanals = 0; Coverage = 0; beide Hashes unverändert; zeilenlose unsettled Seeds des Kanals ≤ vorher; die Seeds mit nur importierten Zeilen aus Schritt 2 sind weg, auch die mit `CreatedSlot` (Zahl = `RemovedSeeds` aus der Log-Zeile); Platzhalter, Läufe, Snapshots unverändert.
7. Zweiter Tab: Beschriftung ohne Archiv-Satz, Summen ohne importierte Tage, ohne Neuladen.
8. Aktivitäts-Tab: Eintrag „Importierte Daten entfernt · n Tage“.
9. Zweiter Aufruf per `curl` mit der alten Erwartung → 200 mit Nullen, kein zweiter Audit-Eintrag.
10. Dialog offen lassen, in einem zweiten Browser (anderer Manager) einen 1-Monats-Lauf bis zum Ende laufen lassen, dann bestätigen → keine Löschung, Hinweis auf geänderten Stand; neuer Klick zeigt den neuen Umfang.
11. Neuer 1-Monats-Lauf auf demselben Set → läuft durch, Beschriftung kommt zurück (S13 live).

## 14. Entscheidungen zu den offenen Fragen (2026-10-13)

### F1 — Granularität: **ganzer Kanal** (Nutzerentscheid 2026-10-13)
Verworfen: (b) je Set — Coverage und Zeilen tragen das Set schon (I1), aber mehr UI-Zustände bei kleinem Nutzen, weil ein Tag ohnehin nur einem Set gehört (OD-A). (c) je Lauf — migrationsfrei über `Coverage.RunId`, aber mit Laufliste im Status, erklärungsbedürftiger Semantik („die Tage, die der Lauf noch besitzt“) und nach der Retention des Laufs (365 Tage) nicht mehr adressierbar.

### F2 — Platzhalter und vom Backfill angelegte Slots: **(A) stehen lassen** (Nutzerentscheid 2026-10-13)
Platzhalter-Emotes und **gesettelte** `CreatedSlot`-Slots bleiben. Ohne Zeilen sind sie unsichtbar (archiviert; Tags, Match-Map, aktive Ansicht und Belegung lesen nur aktive Zeilen/Slots; eine nicht aktive Ansicht zeigt nur Zeilen mit Zählung), ein neuer Lauf und der REST-Abgleich verwenden sie wieder, und die Provenienz für Operations 3/3b bleibt erhalten. Preis: sie zählen weiter in `archivedEmoteCount` der Admin-Kanalliste. **Unsettled Seeds, die nur importierte Zeilen trugen, fallen dagegen — ausdrücklich auch mit `CreatedSlot = true`** (D6/D7, Sol-Befund 3).

Verworfen: (B) kanalbezogenes Mitlöschen nach dem Muster von Operations Schritt 3/3b. **Grund (Sol-Befund 1):** Die Anlage eines Set-Session-Ballots (`VoteSessionService.UpsertSetSessionEmotesAsync`) sperrt weder Kanal noch Emote-Zeile, sondern nimmt beim Einfügen in `VoteSessionEmotes` nur die FK-`KEY SHARE`. Committet sie, während das `DELETE` des Platzhalters auf dessen Zeile wartet, prüft Postgres unter READ COMMITTED die `NOT EXISTS`-Bedingungen auf `VoteSessionEmotes`/`Votes` mit dem alten Snapshot nach — die neue Ballot-Zeile ist unsichtbar, der Platzhalter fällt, und die Kaskade löscht die frische Ballot-Zeile mit. Der Schutz aus D41 („die Vote-Prädikate halten Ballots intakt“) gilt in der Betreiberprozedur nur, weil dort Api und Worker gestoppt sind. Ein sicherer Online-Weg bräuchte eine zusätzliche Sperrdisziplin im Ballot-Pfad — außerhalb von #384.

### F3 — Flag: **hinter `ChatLogBackfill:Enabled`** (Nutzerentscheid 2026-10-13)
Ohne Flag gibt es keinen Settings-Tab (D17), und „Flag aus“ ist der Betreiber-Rückbau, für den Operations gilt. Verworfen: Route immer erreichbar (praktisch nur per `curl` nutzbar); Aktion auf der Nutzungsseite (widerspricht B7 und legt eine destruktive Aktion auf eine Leseseite).

### F4 — Anzeige nach dem Entfernen: **Läufe unverändert** (nach Empfehlung; vom Nutzer nicht ausdrücklich entschieden)
„Letzter Lauf“ zeigt weiter z. B. „Abgeschlossen, 27 von 27 Wochen“; der Abschnitt „Importierte Daten“ ist weg; Wer/Wann im Aktivitäts-Tab. Verworfen: ein Hinweis „entfernt am … von …“ aus dem Audit-Log (Produktzustand aus dem Audit lesen, Status-DTO in der Teil-B-Datei ändern) und eine Spalte am Lauf (**Migration**, während der Bewährung ausgeschlossen).

### 14.5 Codex-Sol-Prüfung (adversarial, 2026-10-13)

| Befund | Prüfung am Code | Einarbeitung |
|---|---|---|
| 1 [high] F2 (B): Ballot-Kaskaden-Rennen | bestätigt: `VoteSessionService` sperrt beim Ballot-Einfügen nichts außer der FK-`KEY SHARE` | (B) verworfen, Grund in §14 F2 |
| 2 [medium] Bestätigung nicht an den gezeigten Umfang gebunden; Abbruch leert `activeRun` vor dem Post-Cancel-Read | bestätigt: `backfill-section.ts:480-511`, `:670-692`; Start-Dialog bindet bereits per `startPlanKey` (`:587-596`) | D15: Erwartung im Request, tagesgenauer Vergleich unter der Kanalsperre, neuer Code `backfill_remove_scope_changed`; Client: Post-Cancel-Gate + Schlüsselbindung; Tests S11-Variante, S17, Vitest „verzögerter Read“ und „Änderung hinter dem Dialog“; AK 14, 16 |
| 3 [medium] AK 8 widerspricht Schritt 6/AK 7: Flag-aus-Seeds sind `SeedUnsettled` **und** `CreatedSlot` | bestätigt: `ChatLogBackfillServiceTests.Slots.cs:25-68` | D6/D7: Ausnahme für unsettled Seeds; S6 (d) auf gesettelte Slots eingeschränkt; neuer Fall S16 und AK 9 (Seed weg, Snapshot-Zeile bleibt) |

## 15. Überschneidung mit #379 Teil B

Teil B (Branch `feat/379-flag-removal`; zum Zeitpunkt der Spec ein Doku-Commit, Testumbau uncommittet, `src` noch unverändert) fasst laut Plan-379 §4/§5 `ChatLogBackfillService`, `IChatLogBackfillService`, `ChatLogBackfillWorker`/`SnapshotMap`, `SevenTvSyncService`, `SevenTvApiClient`, `EmoteService`, `EmoteSetStatusService`, `UsageStatQueryService`/`UsageViewRows`, `VoteSessionQueryService`, `UsageStatFlushService`, `UsageFlushWorker`, `PendingSlotKey`, `BaseSlotRule`, `ServiceCollectionExtensions`/`SlotReconcileOptions`, appsettings/`.env.example`/Compose, viele Tests, Web-Kommentare/Shims, Operations (Slot-Abschnitt, 3b-Hinweis), Architectur, DECISIONS (Child 9) und drei Zeilen der Backfill-Spec an.

**#384 fasst voraussichtlich an — Überschneidung fett:**

| Datei | Art |
|---|---|
| `src/EmotePurge.Core/Services/IChatLogImportRemovalService.cs` | neu (Interface, Ergebnis, `ExpectedCoverageInterval`) |
| `src/EmotePurge.Infrastructure/Services/ChatLogImportRemovalService.cs`, `ChatLogImportRemovalSql.cs` | neu |
| **`src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs`** | eine Registrierungszeile; Teil B entfernt dort die `SlotReconcileOptions`-Bindung |
| `src/EmotePurge.Core/Entities/AuditLogEntry.cs`, `src/EmotePurge.Core/Services/IAuditLogQueryService.cs`, `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs` | Aktion + Detail-Art |
| `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs`, `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | Route, Request-Typ, zwei Codes |
| **`tests/EmotePurge.Api.Tests/ApiFactory.cs`** | Substitution; Teil B kann dort Flag-Reste entfernen (Plan nennt `SevenTvEmoteSetSyncBookkeepingEndpointTests`, nicht die Factory — prüfen) |
| `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`, `EmoteRoutePolicyTests.cs`, neue Endpoint-Tests | Matrix |
| `tests/EmotePurge.Infrastructure.Tests/Integration/ChatLogImportRemovalServiceTests.cs`, ggf. `Fixtures/*` | neu; **keine** Änderung an `ChatLogBackfillServiceTests.*` (Teil B) |
| `web/src/app/core/channels/backfill.service.ts`/`.model.ts`/`.spec.ts`, `web/src/app/features/channel-workspace/backfill-section.ts`/`.spec.ts`, `web/src/app/core/i18n/api-error.ts`, `web/src/app/shared/audit/audit-actions.ts`, `web/public/i18n/de.json`/`en.json` | UI, i18n |
| **`web/e2e/support/mocks.ts`**, `web/e2e/channel-settings.e2e.spec.ts` | Mocks; Teil B fasst Kommentare `mocks.ts:1244-1254` an |
| **`docs/Operations.md`** | „Chat-log backfill“: neuer Unterabschnitt + „Rolling back“ Einleitung/Schritt 4; Teil B ändert den 3b-Hinweis (~`:1037`) und den Slot-Abschnitt — benachbarte Stellen |
| **`docs/DECISIONS.md`** | neuer Eintrag oben; Teil B ergänzt Child 9 — nur Kopf-Konflikt |
| `docs/Feature-Ideen-2026-08-01.md` | Statuszeile A17 |

Nicht angefasst: `ChatLogBackfillService.cs`, `IChatLogBackfillService.cs`, der Worker, `EmoteSlotRollbackSql.cs`, die Backfill-Spec (Teil B markiert dort Zeilen historisch).

**Reihenfolge:** #384 setzt auf PR B auf (Rebase nach dessen Merge). Fachlich hängt #384 nicht an PR B (§3.4: korrekt für Flag an, das auf Prod seit 13.10. gilt), nur die Tests von S6 gegen die Sweep-Konstante und die Aussage „keine neuen unsettled Seeds“ (§2.4) setzen Teil B voraus.

## 16. Doku bei Umsetzung

- **`docs/Operations.md` „Chat-log backfill“**: neuer Unterabschnitt „Removing imported data (channel managers)“ (englisch): was die Aktion löscht (§5.4), was nicht, dass sie hinter dem Flag liegt, bei aktivem Lauf und bei geändertem Umfang 409 antwortet, dass sie zeilenlos gewordene unsettled Seeds selbst entfernt (auch `CreatedSlot`-Seeds aus der Flag-aus-Zeit), Audit-Aktion.
- **„Rolling back“** (Nutzerentscheid 2026-10-13): die Prozedur bleibt der **globale Rückfall** und der einzige Weg für Schritt 3/3b/5, bei Flag aus und für mehrere Kanäle. Die Einleitung bekommt einen Satz: „For a single channel, a manager can remove the imported rows and the coverage from the Settings tab (#384); that replaces the per-channel variant of step 4 but not steps 3, 3b and 5.“ Schritt 4, je-Kanal-Variante: Verweis auf die UI-Aktion und ein Seed-Hinweis — auf einer Datenbank mit Importen aus der Flag-aus-Zeit kann die Variante unsettled Seeds zeilenlos machen; danach die Sweep-Konstante aus #379 Teil B laufen lassen oder die UI-Aktion verwenden, die das selbst erledigt. Schritt 3b: Hinweis, dass ein `CreatedSlot`-Seed, den die UI-Aktion schon entfernt hat, dort nicht mehr auftaucht.
- **`docs/DECISIONS.md`**: ein englischer Eintrag „Channel managers remove a channel's imported backfill data; the confirmed coverage is checked on the server, seeds left rowless go with it, inventory and run history stay (#384)“ mit `**Betrifft:**`-Zeile; Kern: D2, D4–D9, D15, die Sperrreihenfolge, warum keine Migration (§3.3) und warum F2 (B) online nicht sicher ist.
- **`docs/Feature-Ideen-2026-08-01.md`**: Statuszeile A17 um „Entfernen im UI (#384)“ ergänzen.
- **`CLAUDE.md`** Umsetzungsstand, Zeile Chat-Log-Backfill: ein Halbsatz zum Entfernen.

## 17. Aufwand

| Teil | Tage |
|---|---|
| Service + SQL + Erwartungsvergleich + Infrastructure-Tests inkl. Rennen | 1,5 |
| Route, Body-Validierung, zwei Codes, Audit, Api.Tests | 0,5 |
| UI-Abschnitt, Bindung der Bestätigung, i18n, Vitest, E2E | 1,25 |
| Doku, Live-Ablauf | 0,5 |
| **Summe** | **≈ 3,75** |
