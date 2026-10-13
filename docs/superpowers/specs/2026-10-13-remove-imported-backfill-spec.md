# Importierte Backfill-Daten im UI entfernen (#384)

**Stand:** 2026-10-13
**Status:** Umsetzungsspec, kein Code. Vier offene Betreiberfragen (§14); jede nennt die Stellen, die sich mit der Antwort ändern.
**Epic:** #346 (Folge-Issue nach Abschluss) · **Bezug:** #365/#379 (Slots, Teil B), Backfill-Spec `2026-10-09-chat-log-backfill-spec.md`, Slot-Spec `2026-10-10-emote-slots-spec.md`
**Ziel:** fertig, bevor `ChatLogBackfill:Enabled` auf Prod angeht. Dort ist der Backfill aus und bleibt es mindestens bis zum Ende der Bewährung von #379 Phase 1 (≥ 72 h nach 2026-10-13 17:27 UTC) und bis Teil B entschieden ist.
**Basis:** Zielzustand **nach #379 Teil B** (Flag-aus-Zweige entfernt, `SnapshotChanged` weg, Sweep-Konstante neben `EmoteSlotRollbackSql`). Der #384-Code muss nur für „Slot-Flag an“ korrekt sein (§3.4).
**Migration:** keine (§3.3). Keine Variante in §14 braucht eine.

---

## 1. Problem und Ziel

Ein Kanal-Manager kann einen Backfill starten, aber nicht zurücknehmen. `DELETE /api/channels/{c}/backfill` bricht nur einen aktiven Lauf ab; bereits importierte Wochen bleiben (B11). Der einzige Weg, importierte Zahlen zu entfernen, ist die Betreiberprozedur in `docs/Operations.md` „Chat-log backfill“ → „Rolling back“ (psql über den Tunnel).

**Ziel:** Im Settings-Tab des Kanal-Workspace entfernt ein Manager nach ausdrücklicher Bestätigung alle importierten Chat-Archiv-Daten seines Kanals. Danach zeigt die Nutzungsseite den Zustand ohne Import (Beschriftung weg, Summen neu), live gezählte Zeilen (`Source = 0`) sind bytegleich, andere Kanäle unberührt, und es bleibt kein zeilenloser unsettled Seed zurück, den diese Aktion verursacht hat.

**Erfolgskriterium:** AK 1–19 (§12) grün, Live-Ablauf §13 auf der Dev-DB bestanden.

## 2. Geprüfter Bestand (main @ `b11bf239`, 2026-10-13)

### 2.1 Was importiert wird und wo es liegt

| Tabelle | Was der Backfill schreibt | FK / Kaskade | Kennung „aus dem Backfill“ |
|---|---|---|---|
| `UsageStats` | Zeilen mit `Source = 1` (`UsageStatSource.ChatLogArchive`), `SlotId` + `EmoteId` aus dem Snapshot, `EmoteSetId` = Set des Laufs, nur Tage `< CreatedAt`-Datum | `EmoteId → Emotes` cascade, `SlotId → EmoteSlots` cascade | `Source = 1`. **Keine `RunId`.** |
| `ChatLogBackfillCoverage` | eine Zeile je abgedecktem Tag, PK `(ChannelId, Day)`, mit `EmoteSetId`, `ArchiveHost`, `RunId` | `ChannelId → Channels` cascade, `RunId → Runs` **SET NULL** | ganze Tabelle |
| `ChatLogBackfillRuns` | ein Lauf je Anforderung (Status-Maschine, Zähler) | `ChannelId` cascade | ganze Tabelle |
| `ChatLogBackfillRunEmotes` | Snapshot je Slot, PK `(RunId, SlotId)` seit M3, `CreatedRow`, `CreatedSlot` | `RunId` cascade; **`SlotId`/`EmoteId` ohne FK** (D25) | `CreatedRow`/`CreatedSlot` |
| `Emotes` | archivierte Platzhalter für Set-Mitglieder ohne Zeile (`IsPlaceholder = true`, `ArchivedAt = NULL`) | Kinder: `EmoteSlots`, `UsageStats`, `Votes`, `VoteSessionEmotes` (alle cascade) | nur über Snapshot `CreatedRow = true` |
| `EmoteSlots` | Flag an: neue autoritative Slots archiviert, `ArchivedAt = NULL` (aktive-Set-Vorschau: vom nächsten REST-Abgleich übernommen); Settle eines Seeds des Vorschau-Sets | einzige eingehende FK: `UsageStats.SlotId` | nur über Snapshot `CreatedSlot = true` |

Code: `ChatLogBackfillService.ReplaceBlockAsync` (`src/EmotePurge.Infrastructure/Services/ChatLogBackfillService.cs:444-586`) löscht je Block **alle** `Source = 1`-Zeilen des Kanals im Blockfenster, jedes Set (OD-A), fügt ohne `ON CONFLICT` ein, upsertet die Coverage und schaltet den Lauf bedingt weiter — alles eine Transaktion. Daraus folgt die Invariante, auf die diese Spec baut:

> **I1:** Für jeden Tag mit Coverage-Zeile tragen alle `Source = 1`-Zeilen des Kanals an diesem Tag das `EmoteSetId` der Coverage-Zeile und stammen vom Lauf, dessen `RunId` dort steht (solange der Lauf nicht von der Retention gelöscht ist). `Source = 1`-Zeilen ohne Coverage-Tag gibt es nur nach Handarbeit (Operations Schritt 4 halb ausgeführt).
>
> **I2:** Eine Zelle ist live **oder** importiert (D3, per Slot seit M2). Importierte Tage liegen vor dem Zählbeginn, Live-Zeilen ab dem Zählbeginn; `MoveSeedUsageAsync` (`SeedSettleWrites.cs:31-61`) kann deshalb keine importierte mit einer Live-Zeile verschmelzen.

### 2.2 Wege, die es heute gibt

- `DELETE /api/channels/{c}/backfill` → `CancelAsync` (`ChatLogBackfillService.cs:291-326`): bedingtes Update aktiv → `cancelled`, Audit `backfill.cancel`, `backfill.progress`. Nimmt **keine** Kanalsperre.
- Betreiberprozedur „Rolling back“ (`docs/Operations.md:975-1086`): Schritt 1 abbrechen, 2 Flag aus, 3 Platzhalter (D41, global), 3b `CreatedSlot`-Slots (`EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql`, global), 4 Zeilen + Coverage (global **oder je Kanal**), 5 offene Läufe schließen, 6 Image/Down.
- Status-Lesemodell `GET …/backfill` liefert `coverage` als zusammengefasste Intervalle je `(EmoteSetId, ArchiveHost)` mit Set-Namen, `importedFrom/To`, `activeRun`, `lastRun` (`GetStatusAsync`, `:87-130`).
- Die Nutzungsseite lädt die Offenlegung über `GET …/usage-stats/import-coverage` und lädt sie bei `backfill.progress` neu; die Zeilen bei `usage.flushed` (`usage-stats-page.ts:2952-3010`).

### 2.3 Sperren, mit denen sich die Aktion vertragen muss

| Pfad | Kanalzeile | Emote-Zeilen | Quelle |
|---|---|---|---|
| Enqueue | `FOR NO KEY UPDATE` by id, dann User `FOR SHARE`, Aktiv-Lauf-Prüfung **unter** der Sperre | `FOR NO KEY UPDATE` (Bindung) | `EnqueueUnderLocksAsync :774-900` |
| Block-Commit (Worker) | `FOR KEY SHARE` | `FOR SHARE` (`LockEmotesForShareByIdsAsync`) | `ReplaceBlockAsync :483-498`, `:1279-1327` |
| Leave/Deaktivierung | `FOR NO KEY UPDATE`, dann Lauf | — | D16 |
| REST-Abgleich/Settle | `FOR NO KEY UPDATE` (`LockChannelsByIdsAsync`) | `FOR NO KEY UPDATE` | `SevenTvSyncService.cs:831, :1033, :1641` |
| Purge, Join, Merge | `FOR UPDATE` | — | D16/D35 |
| Live-Flush | — (FK `KEY SHARE`) | — (FK `KEY SHARE`) | `UsageStatFlushService` |

`FOR NO KEY UPDATE` kollidiert mit sich selbst, `FOR SHARE` und `FOR UPDATE`, nicht mit `FOR KEY SHARE`. Sperrreihenfolge im Projekt: `Channels` → `Emotes` → (Rest) (`RowLocks.cs:26-28`).

### 2.4 Seeds nach #379 Teil B

- Unsettled Seeds (`EmoteSlots.SeedUnsettled = true`) entstehen nach Teil B **nicht mehr** (M1 und die Flag-aus-Schreiber Flush/Enqueue sind die einzigen Erzeuger, `BaseSlotRule.GetOrCreate*` fällt weg). Bestehende bleiben, bis REST oder eine datierte Vorschau sie settelt.
- Der Sweep aus Teil B (`SeedUnsettled AND NOT EXISTS UsageStats on SlotId`, jedes Set, jede `Source`) ist ein **einmaliger Runbook-Schritt je Datenbank**, kein Dauerjob (Plan-379 §3/§5). Er räumt nach #384 nicht auf.
- Mit Flag an landen importierte Zeilen nie auf einem unsettled Seed: die aktive Vorschau wird mit `ActiveSetSeedUnsettled` abgewiesen (`EmoteSlotReconcilePlanner.Backfill.cs:48-50`), die nicht aktive settelt die Seeds ihres Sets vor der Bindung (`:95-111`, `EmoteSlotReconcilePlanner.cs:274-319`, `SeedUnsettled = false`).
- **Aber:** M1 hat Seeds je `(EmoteId, EmoteSetId)` über **alle** vorhandenen Zeilen angelegt, importierte eingeschlossen, und Flag-aus-Enqueues haben Seeds mit nur importierten Zeilen gebunden. Auf jeder Datenbank mit Importen aus der Flag-aus-Zeit (Dev-DB, fremde Betreiber) gibt es deshalb nach Teil B **unsettled Seeds, die nur `Source = 1`-Zeilen tragen** (nicht aktive Sets, nie mit Flag an vorgeschaut; Emotes, die das Set verlassen haben). Prod hat laut Plan-375 §0 Punkt 2 keine Läufe und keine Snapshot-Zeilen (`ChatLogBackfill:Enabled` dort nie an); dort ist dieser Fall nach heutigem Stand leer — das ist eine Aussage aus der Doku, keine Messung.

**Folge für #384:** Wer `Source = 1`-Zeilen löscht, kann einen solchen Seed zeilenlos machen. #384 räumt genau diese Seeds selbst ab (§5.3, Schritt 6).

**Nebenbefund (nicht #384):** `ReplaceBlockAsync` Schritt 1 löscht kanalweit importierte Zeilen jedes Sets im Blockfenster und kann auf denselben Datenbanken ebenso einen unsettled Seed zeilenlos machen — nach dem einmaligen Sweep. Gehört zu #379 Teil B bzw. einem Folge-Issue, nicht hierher.

## 3. Rahmen

### 3.1 Drin
- Backend-Fähigkeit „importierte Daten eines Kanals entfernen“ (eigenes Service-Interface), Route, Fehlercode, Audit, SSE.
- Abbau der durch die Aktion zeilenlos gewordenen unsettled Seeds.
- UI im Settings-Tab: Übersicht der importierten Bereiche, Entfernen-Knopf, Bestätigungsdialog, Sperre bei aktivem Lauf.
- Operations-/DECISIONS-Text, Tests, Live-Ablauf.

### 3.2 Draußen
- Entfernen je Lauf (§14 F1 (c), migrationsfrei möglich, aber nicht in V1).
- Löschen von Platzhaltern und `CreatedSlot`-Slots (§14 F2, Empfehlung: bleiben).
- Änderungen an Lauf-Zeilen, Snapshots, Retention, Worker.
- Fremde SQL-Konstanten (`EmoteSlotRollbackSql.*`, Sweep aus Teil B) — unverändert.
- Der Nebenbefund aus §2.4.

### 3.3 Keine Migration
Alles, was die Aktion braucht, steht im Schema: `UsageStats.Source`, `UsageStats.EmoteSetId`, `UsageStats.SlotId`, `ChatLogBackfillCoverage.(ChannelId, EmoteSetId, RunId)`, `EmoteSlots.SeedUnsettled`. Auch das Entfernen **je Lauf** braucht keine: der Besitz eines Tages steht in `Coverage.RunId` (I1), und die importierten Zeilen eines Tages gehören genau dem Lauf, der ihn zuletzt geschrieben hat. Eine Migration wäre nur für eine Zeilen-Provenienz `UsageStats.RunId` nötig — die braucht keine Variante, weil ersetzte Zeilen eines älteren Laufs nicht mehr existieren. Ebenfalls migrationspflichtig und deshalb verworfen: eine Spalte „Daten entfernt am“ am Lauf (§14 F4 (c)).

### 3.4 Flag-Zustand
Die Aktion ist nur erreichbar, wenn `ChatLogBackfill:Enabled = true` (D3). Nach der Zulassungsfolge (Operations „Switching the slot flag on“, Plan-379 §2.1) ist der Backfill nur an, wenn das Slot-Flag an ist; nach Teil B gibt es kein Slot-Flag mehr. Ein Rückweg auf ein Image mit Slot-Flag aus verlangt vorher Backfill aus → die Route antwortet 404. Deshalb muss #384 nur den Flag-an-Zustand korrekt behandeln, und es liest `SlotReconcileOptions` **nicht** (Teil B entfernt den Typ).

## 4. Entscheidungen

Bindend aus dem Issue: Rollen wie beim Start (B1), Bestätigung, eine Transaktion, Lauf aktiv → verweigert mit neuem Code (Regel 7), idempotent, Audit, Nutzungsseite ohne Import danach, Testcontainers-Nachweis. Die folgenden D1 ff. sind die Entscheidungen dieser Spec; die mit „→ F“ markierten hängen an einer offenen Frage.

| # | Entscheidung | Verworfene Alternative / Begründung |
|---|---|---|
| **D1** → F1 | **Granularität V1 = ganzer Kanal**, jedes Set, jeder Lauf. | Je Set / je Lauf: §14 F1. Der ganze Kanal deckt das Issue-Szenario („der Streamer will es nicht“) und braucht keine Auswahl-UI. |
| **D2** | **Eigener Service** `IChatLogImportRemovalService` (Core `Services/`), Implementierung `ChatLogImportRemovalService` (Infrastructure `Services/`), eigene SQL-Texte in einer neuen internen Klasse `ChatLogImportRemovalSql` (Infrastructure `Services/`). | Methode an `IChatLogBackfillService`: genau diese Datei baut #379 Teil B gerade um (Abschnitt Enqueue/ReplaceBlock/`SnapshotChanged`); ein eigener Service hält beide Zweige konfliktarm und den Vertrag lesbar. Regel 4/5. |
| **D3** → F3 | **Hinter `ChatLogBackfill:Enabled`**: aus → 404 `backfill_disabled` nach dem Autorisierungsfilter, wie die drei bestehenden Routen. | Ohne Flag erreichbar: der Settings-Tab existiert ohne Flag nicht (D17), und „Flag aus“ ist der Betreiber-Rückbaumodus, in dem Operations gilt. |
| **D4** | **Lauf aktiv (`queued`/`running`/`paused`) → 409 `backfill_remove_run_active`**, geprüft unter der Kanalsperre; nichts wird geschrieben. Erst abbrechen. | Den Lauf mit abbrechen: zwei destruktive Wirkungen hinter einem Klick, und ein laufender Block würde direkt danach wieder importieren wollen. `backfill_already_active` wiederverwenden: sein Text spricht vom Start. |
| **D5** | **Gelöscht wird:** alle `UsageStats`-Zeilen mit `Source = 1` der Emotes des Kanals (jedes Set, jedes Datum, mit oder ohne Coverage), alle `ChatLogBackfillCoverage`-Zeilen des Kanals, und jeder unsettled Seed, den genau dieses Löschen zeilenlos gemacht hat. | Nur Zeilen an Coverage-Tagen: Zeilen ohne Coverage (Handarbeit) blieben unsichtbar und unbeschriftet stehen — das schlechtere Ergebnis. |
| **D6** → F2 | **Nicht gelöscht:** Live-Zeilen (`Source = 0`), Lauf-Zeilen, Snapshot-Zeilen, Platzhalter-Emotes, `CreatedSlot`-Slots, gesettelte Slots (auch wenn jetzt zeilenlos), unsettled Seeds, die schon vorher zeilenlos waren, Provider-Cooldown, Audit. | Mitlöschen der Platzhalter/Slots: §14 F2. Läufe löschen: verlöre die Provenienz (`CreatedRow`/`CreatedSlot`), die Operations Schritt 3/3b braucht (D41-Einschränkung „nach Retention nicht mehr identifizierbar“ träte sofort ein). |
| **D7** | **Seed-Abbau nur für selbst verursachte zeilenlose Seeds** (Kandidaten = die `SlotId`s der gelöschten Zeilen), Prädikat wie der Sweep aus Teil B (`SeedUnsettled AND NOT EXISTS UsageStats`), eingeschränkt auf die Kandidaten. | Kanalweit alle zeilenlosen unsettled Seeds: wäre die Arbeit des Sweeps und überraschte bei einer Aktion, die „importierte Daten“ heißt. Gar kein Abbau: hinterließe genau das, was Teil B mit dem Sweep einmalig beseitigt — und der läuft nicht wieder. |
| **D8** | **Gesettelte Slots bleiben, auch zeilenlos** (`SeedUnsettled = false`, z. B. ein in place gesettelter Seed eines nicht aktiven Sets, ein umgebundener leerer Seed). | Der Sweep lässt sie ebenfalls stehen (Plan-379 §3); sie sind autoritative Identitäten, die REST oder eine spätere Vorschau wiederverwendet. |
| **D9** | **Idempotent:** nichts zu entfernen → 200 mit Nullen, **kein** Audit-Eintrag, **keine** Events. | 204 immer: die Zahlen machen Live-Test und UI-Ansage belastbar. Ein Audit für einen No-op wäre Rauschen. |
| **D10** | **Sichtbarkeit im UI nach `status.coverage`** (nicht leer). Kein neues Feld am Status-DTO. | Neues `hasImportedData`: hieße `ChatLogBackfillStatusDto`/`GetStatusAsync` anfassen, also die Teil-B-Datei. Zeilen ohne Coverage sind Handarbeit (I1) und ohnehin unbeschriftet; dafür bleibt Operations. |
| **D11** | **Auch für nicht mehr beobachtete oder ausgeschlossene Kanäle** erlaubt (`IsBotActive`, `ExcludedChannelIds` werden nicht geprüft). | Löschen eigener Daten an das Tracking zu binden, wäre widersinnig; ein Art.-21-Ausschluss ist eher ein Grund mehr. |
| **D12** | **Kein Sperren des Antragsteller-Users** (anders als Enqueue). | Präzedenz `CancelAsync`: ein reiner Audit-Schreiber; der Audit-Eintrag ist ein Snapshot, den die Account-Löschung pseudonymisiert wie alle anderen. |
| **D13** | **Events nach Commit:** `backfill.progress` und `usage.flushed` für den Kanal, über `IRedisPublisher`, fehlertolerant wie beim Cancel. Kein neuer Event-Typ. | Neuer Typ `backfill.removed`: die bestehenden lösen genau die nötigen Refetches aus (§9). |
| **D14** → F4 | **Lauf-Historie bleibt unverändert sichtbar**; die neue Übersicht „Importierte Daten“ sagt nach dem Entfernen „keine“. Das Wer/Wann steht im Aktivitäts-Tab (Audit). | §14 F4. |

## 5. Backend-Vertrag

### 5.1 Core-Interface

`IChatLogImportRemovalService.RemoveImportedAsync(string channelName, AuditActor actor, CancellationToken)` → `ChatLogImportRemovalResult`:

| Status | Bedeutung | Felder |
|---|---|---|
| `Removed` | mindestens eine Zeile oder ein Coverage-Tag entfernt | `RemovedUsageRows` (int), `RemovedCoverageDays` (int), `RemovedSeeds` (int) |
| `NothingToRemove` | Kanal ohne `Source = 1`-Zeilen und ohne Coverage | alle 0 |
| `RunActive` | aktiver Lauf des Kanals; nichts geschrieben | — |
| `NotFound` | Kanal unbekannt (auch: während des Wartens auf die Sperre gepurgt) | — |

Bei F1 (b) kommt ein optionaler Parameter `string? emoteSetId` hinzu (§14).

### 5.2 Route

`DELETE /api/channels/{channelName}/backfill/imported` in `ChannelEndpoints.cs`, in der `/api/channels`-Gruppe (`RequireAuthorization` + `ChannelNameValidationFilter`), dazu `AddEndpointFilter<ChannelManagementAuthorizationFilter>()` (B1: Admin, Broadcaster, Live-Mod; kein 7TV-Editor) und `RequireRateLimiting(RateLimitPolicyNames.Bookkeeping)` (wie Cancel). Kein Body.

| Status | Body | Wann |
|---|---|---|
| 200 | `{ "removedUsageRows": n, "removedCoverageDays": d }` | `Removed` und `NothingToRemove` (dann Nullen). `RemovedSeeds` geht nur ins Log, nicht auf den Draht. |
| 401 | — | nicht angemeldet; oder Session ohne baubaren Audit-Akteur (`TryBuildAuditActor` = null), wie die übrigen Backfill-Routen |
| 403 | — | kein Manager (Filter) |
| 404 | `{ errorCode: "backfill_disabled" }` | Flag aus — **nach** dem Filter, damit ein 7TV-Editor 403 bekommt |
| 404 | `{ errorCode: "channel_not_found" }` | `NotFound` |
| 409 | `{ errorCode: "backfill_remove_run_active" }` | `RunActive` |

Reihenfolge im Handler: Flag → Akteur → Service. Der Handler bleibt dünn (Regel 11).

### 5.3 Transaktion, Sperren, Reihenfolge

Eine Transaktion (READ COMMITTED), als Wiederholungseinheit bei Deadlock (40P01) mit höchstens drei Versuchen und kurzer Zufallspause — Präzedenz Enqueue/Broadcaster-Purge. Ein erschöpfter Deadlock wird 500 (Problem), nichts ist geschrieben.

1. **Kanal sperren:** `LoadChannelForNoKeyUpdateAsync(channelName)` (die Leave-Ladefunktion, `ChannelQueries.cs:168`). `null` → `NotFound`. Damit serialisiert die Aktion mit Enqueue, Leave, REST-Abgleich und jedem `FOR UPDATE`-Pfad (Purge, Join, Merge), aber nicht mit FK-Einfügungen (Flush).
2. **Aktiver Lauf?** `EXISTS ChatLogBackfillRuns WHERE ChannelId = @id AND Status IN (queued, running, paused)` → `RunActive`, Abbruch ohne Schreiben. Der Zustand „kein aktiver Lauf“ ist unter dieser Sperre stabil: nur der Enqueue erzeugt einen aktiven Lauf, und der braucht dieselbe Sperre; alle Worker-Übergänge führen aktiv → aktiv oder aktiv → terminal.
3. **Betroffene Emotes bestimmen:** die `Id`s der Emotes des Kanals, die mindestens eine `Source = 1`-Zeile tragen. Leer und keine Coverage-Zeile → `NothingToRemove` (Rollback, kein Audit).
4. **Emote-Zeilen sperren:** `RowLocks.LockEmotesByIdsAsync` (`FOR NO KEY UPDATE`, sortiert mit `COLLATE "C"`). Zweck: Serialisierung mit dem Settle (`MoveSeedUsageAsync`/`MoveSeedSnapshotEntriesAsync` unter denselben Emote-Sperren) und mit einem noch laufenden Block-Commit, der die Emotes `FOR SHARE` hält (§6.3). Die Menge aus Schritt 3 ist stabil: importierte Zeilen schreibt nur der Block-Commit eines **aktiven** Laufs, und ein Settle verschiebt Zeilen nur zwischen Slots **desselben** Emotes.
5. **Zeilen löschen:** ein Statement `DELETE FROM "UsageStats" u USING "Emotes" e WHERE u."EmoteId" = e."Id" AND e."ChannelId" = @channelId AND u."Source" = 1 RETURNING u."SlotId"`, im selben Statement je `SlotId` gezählt (datenverändernde CTE + `GROUP BY`). Ergebnis: Summe = `RemovedUsageRows`, die Schlüsselmenge = Seed-Kandidaten. Der Kanalfilter ist maßgeblich, nicht die Sperrmenge aus 3.
6. **Zeilenlos gewordene unsettled Seeds löschen:** **eigenes, zweites Statement** (eine CTE im selben Statement sähe die Löschung aus 5 nicht — alle Teile eines Statements lesen denselben Snapshot): `DELETE FROM "EmoteSlots" s WHERE s."Id" = ANY(@candidates) AND s."SeedUnsettled" AND NOT EXISTS (SELECT 1 FROM "UsageStats" x WHERE x."SlotId" = s."Id")` → `RemovedSeeds`. Kaskade: keine (der Slot hat keine Zeilen mehr). Snapshot-Zeilen beendeter Läufe, die auf ihn zeigen, bleiben ohne Ziel stehen — zulässig, `SlotId` hat keine FK (Plan-379 §3), gelesen wird sie nur für aktive Läufe und von 3b, das den Slot voraussetzt.
7. **Coverage löschen:** `DELETE FROM "ChatLogBackfillCoverage" WHERE "ChannelId" = @channelId` → `RemovedCoverageDays`.
8. **Audit** (§7) nur, wenn `RemovedUsageRows + RemovedCoverageDays > 0`; `SaveChanges`; Commit.
9. **Nach dem Commit:** `backfill.progress` und `usage.flushed` für den normalisierten Kanalnamen (D13), nur bei `Removed`. Eine Redis-Störung macht das Ergebnis nicht zum Fehler (Präzedenz `PublishSafelyAsync`).
10. **Log** (englisch, Regel „Außenwirkung“): eine Info-Zeile mit Kanal, `RemovedUsageRows`, `RemovedCoverageDays`, `RemovedSeeds`.

Die Texte aus 5–7 stehen als Konstanten in `ChatLogImportRemovalSql` (D2) — keine Änderung an `EmoteSlotRollbackSql` oder der Teil-B-Sweep-Konstante. Ein Test hält fest, dass das Prädikat aus 6 auf den Kandidaten dieselbe Menge trifft wie der Teil-B-Sweep (§11.1 Fall S6).

### 5.4 Was in welcher Reihenfolge fällt (Variante A, Empfehlung F2)

| Schritt | Zeilen | Kaskaden |
|---|---|---|
| 5 | `UsageStats` `Source = 1` des Kanals | keine (Blatt) |
| 6 | `EmoteSlots` mit `SeedUnsettled`, deren letzte Zeile in 5 fiel | `UsageStats.SlotId` (leer) |
| 7 | `ChatLogBackfillCoverage` des Kanals | keine |
| — | **bleibt:** `Source = 0`, Läufe, Snapshots, `Emotes` (Platzhalter inklusive), alle übrigen `EmoteSlots`, `ChatLogBackfillProviderState`, Votes/Ballots, Tags | — |

Variante B (F2) fügt zwischen 6 und 7 die kanalbezogenen Gegenstücke von Operations Schritt 3 und 3b ein (§14 F2 nennt Reihenfolge und Kaskaden).

### 5.5 Rückfrage an die eigene Invariante

Nach Commit gilt für den Kanal: `count(UsageStats Source=1) = 0`, `count(Coverage) = 0`, und die Zahl der `EmoteSlots` des Kanals mit `SeedUnsettled AND NOT EXISTS UsageStats` ist **nicht größer** als vorher (AK 7). Ein zeilenloser unsettled Seed, der danach existiert, existierte schon vorher und gehört dem Sweep.

## 6. Nebenläufigkeit

### 6.1 Gegen einen startenden Lauf
Enqueue und Entfernen nehmen dieselbe Kanalsperre (`FOR NO KEY UPDATE`) und prüfen beide unter ihr. Entweder committet der Enqueue zuerst → das Entfernen sieht den aktiven Lauf → 409. Oder das Entfernen committet zuerst → der Enqueue läuft danach gegen einen Kanal ohne Import (kein Ersetzen-Hinweis nötig, die Coverage ist leer). Der partielle Unique-Index bleibt das letzte Wort für „ein aktiver Lauf je Kanal“; das Entfernen schreibt keinen Lauf.

### 6.2 Gegen den Worker und den Advisory-Lock
Das Entfernen nimmt den Loop-Lock **nicht** (der Worker hält ihn für seine Lebensdauer, D46) und braucht ihn nicht: es verlangt „kein aktiver Lauf“, und ein terminaler Lauf kann nichts mehr committen (`ReplaceBlockAsync` Schritt 4 ist bedingt auf `Status = 'running'`).

### 6.3 Gegen den letzten Block eines gerade abgebrochenen Laufs
Ablauf: Abbruch committet → Manager klickt „Entfernen“ → der Worker steckt noch in `ReplaceBlockAsync` (Kanal `KEY SHARE`, Emotes `FOR SHARE`, ggf. schon Schritt 1–3).
- Hält der Block die Emote-Sperren schon, wartet das Entfernen in Schritt 4. Der Block scheitert an Schritt 4 (Status `cancelled`) und rollt zurück; danach läuft das Entfernen auf dem Stand ohne den Block.
- Hat das Entfernen die Emote-Sperren zuerst, wartet der Block in `LockEmotesForShareByIdsAsync`; das Entfernen committet; der Block läuft weiter und rollt an Schritt 4 zurück.
- Ein Block, dessen Schritt 4 **vor** dem Abbruch lief, hat committet, bevor der Abbruch committen konnte (Zeilensperre auf dem Lauf); das Entfernen sieht dann seine Zeilen.
- Teilen sich Block-Aggregate und importierte Emotes keine Emote-Zeile, kommt das Entfernen bis Schritt 5 und wartet dort auf die Zeilensperren aus Block-Schritt 1; der Block braucht nichts, was das Entfernen hält, scheitert an Schritt 4 und gibt frei.
- Kein Zyklus in keiner Reihenfolge: Das Einzige, worauf der Block beim Entfernen warten kann, sind dessen Emote-Sperren — und die verlangt der Block nur in seinem Sperrschritt, **bevor** er eine `UsageStats`-Zeile anfasst. Das Entfernen fasst `UsageStats` erst an, wenn es alle Emote-Sperren hat. FK-`KEY SHARE` (Block) verträgt sich mit `NO KEY UPDATE` (Entfernen), auf dem Kanal wie auf den Emotes.
- Sieht das Entfernen den Lauf noch als `running` (Block hat Schritt 4 geschrieben, nicht committet), antwortet es konservativ 409; ein erneuter Klick Sekunden später geht durch.

### 6.4 Gegen den REST-Abgleich und das Settle
Der REST-Abgleich sperrt den Kanal `FOR NO KEY UPDATE` (`SevenTvSyncService.cs:831, :1033`) → vollständige Serialisierung über Schritt 1. Zusätzlich serialisieren die Emote-Sperren aus Schritt 4 jeden Schreiber, der Emotes ohne vorherige Kanalsperre sperrt. Fällt ein Settle davor, hat es Zeilen verschoben (Quelle-Seed → Ziel) und den Seed auf `SeedUnsettled = false` gesetzt; das Entfernen löscht die verschobenen `Source = 1`-Zeilen am Ziel und lässt den gesettelten Seed stehen (D8). Fällt es danach, findet es keine importierten Zeilen mehr zu verschieben.

### 6.5 Gegen den Live-Flush
Der Flush schreibt nur `Source = 0` mit dem heutigen Datum (I2) und nur auf gesettelte aktive Slots der Match-Map — nie auf einen unsettled Seed (nach Teil B). Seine FK-`KEY SHARE` kollidiert mit keiner Sperre des Entfernens. Ein Flush während des Entfernens ist also unabhängig; seine Zeilen sind nach dem Commit bytegleich da.

### 6.6 Gegen Leave, Purge, Rename, Account-Löschung
- Leave: dieselbe Kanalsperre → seriell; danach ist Entfernen weiter erlaubt (D11).
- Purge: `FOR UPDATE` → seriell; gewinnt der Purge, ist das Entfernen `NotFound` (404).
- Rename während des Dialogs: die Route adressiert per Name; der alte Name ist 404, die UI lädt neu.
- Account-Löschung des Antragstellers: wie beim Cancel (D12).

### 6.7 Zwei Manager gleichzeitig
Seriell über Schritt 1; der zweite sieht `NothingToRemove` → 200 mit Nullen, kein zweites Audit.

### 6.8 Dauer und Sperrzeit
Die Aktion hält die Kanalsperre für die Dauer der Löschung. Der größte denkbare Kanal (6 Monate, ~900 Slots) hat höchstens einige 100 000 importierte Zeilen; gemessen wird das im Live-Ablauf (§13) auf der Dev-DB und als Dev-Zahl berichtet. Während der Sperre wartet der REST-Abgleich dieses Kanals (er holt im nächsten Takt auf); Flush und Lesepfade warten nicht. Kein eigener `lock_timeout` (Präzedenz Enqueue/Leave).

## 7. Audit

- Neue Aktion `AuditActions.BackfillRemoveImported = "backfill.removeImported"` (Core `Entities/AuditLogEntry.cs`, neben `BackfillRequest`/`BackfillCancel`).
- Eintrag: `channelName`, kein Ziel (`targetType`/`targetId` null; bei F1 (b) `emoteSet`/Set-Id), Details `{ backfillRemovedDays: d }` über eine neue Detail-Art `AuditLogDetail.Kinds.BackfillRemovedDays = "backfillRemovedDays"` (Count-Form wie `backfillMonths`), projiziert in `AuditLogQueryService`.
- Die Zeilenzahl kommt **nicht** ins Audit (Tage sind die Einheit, die der Nutzer versteht und die Coverage zählt).
- Frontend: `audit-actions.ts` + `audit.actions.backfillRemoveImported` („Importierte Daten entfernt“ / „Imported data removed“), `audit.details.backfillRemovedDays` („{{count}} Tage“ mit Pluralformen) in de/en; der Aktivitätsfilter des Kanals listet die Aktion.
- Kein Audit bei No-op (D9) und bei 409.
- Pseudonymisierung bei Account-Löschung und Retention (365 Tage) greifen ohne Änderung.

## 8. Fehlercode (Regel 7)

`backfill_remove_run_active` (409) in `ApiErrorCodes.cs` (Block „Chat-log backfill“), in `web/src/app/core/i18n/api-error.ts` und in beiden Locale-Dateien unter `errors.api.*`:

- de: „Für diesen Channel wartet oder läuft gerade ein Backfill. Brich ihn zuerst ab, dann kannst du die importierten Daten entfernen.“
- en: „A backfill is queued or running for this channel. Cancel it first, then you can remove the imported data.“

Wiederverwendet: `backfill_disabled`, `channel_not_found`.

## 9. SSE und Wirkung auf offene Seiten

| Event | Empfänger | Wirkung |
|---|---|---|
| `backfill.progress` | Settings-Tab (`backfill-section.ts:557-561`) | Status-Refetch → `coverage` leer → Block „Importierte Daten“ verschwindet |
| `backfill.progress` | Nutzungsseite (`usage-stats-page.ts:2962-2964`) | `importCoverageResource.reload()` → Beschriftung fällt auf „Wir zählen seit …“ zurück; `coverageStart` = Zählbeginn, also kommen Vor-Zählbeginn-Warnung und Trend-Unterdrückung wie ohne Import zurück; das „Gesamt“-Preset beginnt wieder am Zählbeginn |
| `usage.flushed` | Nutzungsseite | Zeilen/Summen neu ohne importierte Tage |

Nicht nötig: `channel.synced` (das Inventar ändert sich nicht sichtbar — ein gelöschter Seed war zeilenlos und taucht in keiner Ansicht auf). Rollen-Cache: unberührt, die Aktion ändert keine Rolle. Live-Badge, Worker-Health: unberührt.

## 10. Frontend

### 10.1 Service und Modell
`BackfillService.removeImported(channelName)` → `DELETE /api/channels/{normalized}/backfill/imported`, Antworttyp `BackfillRemovalResult { removedUsageRows: number; removedCoverageDays: number }` in `backfill.model.ts`.

### 10.2 Platz im UI
Im Settings-Tab, `BackfillSection`, als eigener Unterabschnitt **„Importierte Daten“** zwischen Auswahl/Start und „Letzter Lauf“ — er beschreibt den Ist-Zustand des Kanals, nicht einen Lauf. Sichtbar nur, wenn `status.coverage.length > 0` (D10). Der Tab selbst bleibt Managern mit Flag an vorbehalten (D17, Guard unverändert).

Inhalt:
- Eine Zeile je Coverage-Intervall: Zeitraum (`to` exklusiv → Anzeige `to − 1 Tag`, wie die Lauf-Fenster) und Set (`emoteSetName ?? emoteSetId`), z. B. „08.04.2026 – 07.10.2026 · Normal“.
- Ein Satz, dass diese Tage aus dem Chat-Archiv stammen und auf der Nutzungsseite so beschriftet sind.
- Knopf „Importierte Daten entfernen“ (`appButton="danger"`).
- Gesperrt, solange `status.activeRun` existiert, mit sichtbarem Grund darunter, per `aria-describedby` verbunden: „Brich zuerst den laufenden Backfill ab.“
- Fehler der Anfrage als `app-notice-banner variant="error"` direkt unter dem Knopf (Muster Cancel), geleert beim nächsten Versuch.
- Nach Erfolg: Ansage im vorhandenen `role="status"`-Bereich („Importierte Daten entfernt: {{days}} Tage.“), dann verschwindet der Unterabschnitt mit dem Refetch.

Nach dem Entfernen zeigt die Übersicht nichts mehr; der Block „Letzter Lauf“ bleibt (D14).

### 10.3 Bestätigungsdialog
`openConfirmDialog` (der `DialogActionRow`-Weg der destruktiven Aktionen; Text mit `whitespace-pre-line`):

> **Importierte Daten dieses Channels entfernen?**
> Entfernt werden: alle aus dem Chat-Archiv importierten Zahlen vom {{from}} bis {{to}} (alle Sets) und damit der Hinweis „aus dem Chat-Archiv“ auf der Nutzungsseite.
> Bleiben: alle Zahlen, die wir selbst seit dem {{countingSince}} zählen, und die Liste der bisherigen Läufe.
> Das lässt sich nicht rückgängig machen. Ein neuer Backfill liest das Archiv neu; seine Zahlen können abweichen.

Bestätigen-Beschriftung: „Importierte Daten entfernen“. `{{from}}`/`{{to}}` = `importedFrom`/`importedTo − 1` aus dem Status (AllSets). Abbrechen sendet nichts.

### 10.4 Entscheidungslogik (Regel 12, `computed()`)
- `importedIntervals` = `status.coverage` mit Anzeige-Ende.
- `removeVisible` = `importedIntervals.length > 0`.
- `canRemove` = `removeVisible && !activeRun && !removing && !starting`.
- `removeBlockedReasonKey` = `activeRun ? 'backfill.remove.blockedByRun' : null`.
- `removing` als Request-Token wie `cancelRequest` (Kanal|Status-Schlüssel), damit eine späte Antwort für einen anderen Kanal nichts anwendet.
- Gegenseitig: `canStart` wird um `!removing` erweitert (ein Start während des Entfernens würde gegen einen sich ändernden Zustand starten).
- Nach Bestätigung prüft die Komponente erneut `canRemove` (der Dialog war offen; inzwischen kann ein Lauf gestartet sein). Der Server entscheidet ohnehin (409).
- Antworten: 200 → Ansage, `statusResource.reload()`; 409 `backfill_remove_run_active` → Fehlertext + Refetch; 404 `backfill_disabled`/`channel_not_found`, 0/5xx → Fehlertext + Refetch (Ausgang unbekannt, die Antwort kann nach dem Commit verloren sein).

### 10.5 i18n (de/en)
`backfill.remove.title`, `.intro`, `.interval`, `.action`, `.blockedByRun`, `.confirm` (Dialogtext mit Parametern), `.announceDone` (mit Plural über `pluralKey`), dazu `errors.api.backfill_remove_run_active`, `audit.actions.backfillRemoveImported`, `audit.details.backfillRemovedDays`. Deutsch duzt wie der Bestand.

### 10.6 Nutzungsseite
Kein Code-Eingriff erwartet: die vorhandenen Refetch-Pfade (§9) tragen. Geprüft wird es in E2E und live (AK 15). Bricht ein Pfad bei leerer Coverage nach vorher gefüllter (z. B. das „Gesamt“-Preset), ist das ein Fehler dieser Spec und wird im selben PR behoben.

## 11. Teststrategie

### 11.1 Infrastructure.Tests (Testcontainers, `Integration/ChatLogImportRemovalServiceTests.cs`)
Eigene Testklasse, eigene Seeding-Helfer über `Fixtures/` (die `ChatLogBackfillServiceTests`-Teilklassen baut Teil B gerade um; keine Kopplung daran). Fälle:

- **S1 Live bytegleich:** Kanal mit Live- und Importzeilen auf denselben Emotes, mehreren Slots und zwei Sets → alle `Source = 0`-Zeilen (sämtliche Spalten inkl. `Id`, `SlotId`) vorher = nachher.
- **S2 Andere Kanäle:** zweiter Kanal mit Import, Coverage, Läufen, Seeds → vollständig unverändert.
- **S3 Ergebnis:** `Source = 1` des Kanals = 0, Coverage = 0, Zähler im Ergebnis stimmen, `GetCoverageAsync(AllSets)` und `(Set)` leer.
- **S4 Aktiver Lauf:** je `queued`, `running`, `paused` → `RunActive`, nichts verändert, kein Audit, kein Publish.
- **S5 Idempotenz:** zweiter Aufruf → `NothingToRemove`, kein Audit, kein Publish; Kanal ohne je Import ebenso.
- **S6 Seeds:** (a) unsettled Seed nur mit `Source = 1` → gelöscht; (b) unsettled Seed mit `Source = 0` und `1` → bleibt mit seinen Live-Zeilen; (c) gesettelter Slot, jetzt zeilenlos → bleibt; (d) `CreatedSlot`-Slot (archiviert, `ArchivedAt` null) → bleibt; (e) unsettled Seed, schon vorher zeilenlos → bleibt; (f) Seed eines anderen Kanals mit nur importierten Zeilen → bleibt. Zusätzlich: auf den Kandidaten trifft das Prädikat dieselbe Menge wie die Teil-B-Sweep-Konstante (sobald sie auf main ist; bis dahin gegen das in Plan-379 §3 festgeschriebene Prädikat).
- **S7 Inventar bleibt (Variante A):** Platzhalter (`IsPlaceholder`, `CreatedRow`), Lauf- und Snapshot-Zeilen unverändert; danach findet `EmoteSlotRollbackSql.DeleteBackfillCreatedSlotsSql` bzw. das Operations-Schritt-3-Prädikat sie weiterhin (Provenienz intakt).
- **S8 Audit:** genau ein Eintrag `backfill.removeImported` mit Kanal und `backfillRemovedDays = d`; projiziert durch `AuditLogQueryService`.
- **S9 Publish:** nach Commit `backfill.progress` und `usage.flushed` mit normalisiertem Namen; Redis-Ausfall → Ergebnis trotzdem `Removed`.
- **S10 Rennen gegen Enqueue** (Barriere wie `ChatLogBackfillServiceTests.Races`): beide Reihenfolgen; entweder 409 ohne Schreiben oder Lauf entsteht nach dem Entfernen; nie Lauf + gelöschte Daten des neuen Laufs.
- **S11 Rennen gegen den Block eines abgebrochenen Laufs:** Block hält Emote-`FOR SHARE` und hat Schritt 1–3 geschrieben, Abbruch committet, Entfernen startet → kein Deadlock, nach beiden Enden keine `Source = 1`-Zeile des Blocks, Lauf `cancelled`.
- **S12 Rennen gegen Settle:** `SeedSettleWrites.MoveSeedUsageAsync` auf einem Seed mit importierten Zeilen parallel → keine `Source = 1`-Zeile übrig, kein neuer zeilenloser unsettled Seed, kein 40P01 nach außen.
- **S13 Neuer Lauf danach:** Enqueue desselben Sets/Fensters nach dem Entfernen → bindet die vorhandenen Slots und Platzhalter (keine doppelte `Emote`-Zeile, Unique-Index), ein Block committet, Coverage neu.
- **S14 Nicht aktiv / ausgeschlossen:** `IsBotActive = false` bzw. Twitch-ID auf der Ausschlussliste → Entfernen funktioniert (D11).
- **S15 Coverage ohne Zeilen / Zeilen ohne Coverage:** beide werden entfernt, `Removed`.

### 11.2 Api.Tests
- `ApiFactory`: `IChatLogImportRemovalService` substituieren (die `RequestDelegateFactory` löst Handler-Services **vor** den Filtern auf).
- `AuthFilterMatrixTests`: die neue Route in die Theorien für 401 (anonym) und 403 (Nicht-Manager inkl. 7TV-Editor) und in die Erfolgs-Tabelle (`DELETE /api/channels/{0}/backfill/imported` → 200).
- `EmoteRoutePolicyTests`: Route → `Bookkeeping`.
- Neue `ChatLogImportRemovalEndpointTests` (oder Erweiterung `ChatLogBackfillEndpointsTests`): Flag aus → 404 `backfill_disabled` ohne Service-Aufruf, aber 403 für Nicht-Manager; Abbildung `Removed`/`NothingToRemove` → 200 mit Drahtform (camelCase, zwei Felder, kein `removedSeeds`); `RunActive` → 409 Code; `NotFound` → 404 Code; Akteur wird durchgereicht.
- Kein neuer `IEndpointFilter`, keine geänderte Filterreihenfolge (Regel 11 nur über die Matrix).

### 11.3 Web (Vitest, Regel 12: Verhalten, keine Vorlage)
- `backfill.service.spec.ts`: `removeImported` sendet `DELETE` auf die normalisierte URL (HttpTestingController).
- `backfill-section.spec.ts`: `removeVisible` aus `coverage`; `canRemove` falsch bei aktivem Lauf, während `removing` und während `starting`; Sperrgrund-Schlüssel; Dialog abgebrochen → keine Anfrage; bestätigt → genau eine Anfrage; 409 → Fehler-Schlüssel `errors.api.backfill_remove_run_active` und Status-Refetch; 200 → Ansage-Schlüssel mit Tageszahl und Refetch; späte Antwort nach Kanalwechsel → verworfen; `canStart` falsch während `removing`; Dialogtext enthält die Daten aus `importedFrom`/`importedTo − 1` (als Parameter, nicht als Wortlaut geprüft).
- `api-error-locales.spec.ts` deckt den neuen Code automatisch ab; Audit-Schlüssel in beiden Locales.

### 11.4 E2E (Playwright, `/api/**` gemockt, `web/e2e/channel-settings.e2e.spec.ts`)
- Settings-Tab mit Coverage → Unterabschnitt sichtbar → Entfernen → Dialog → bestätigen → gemocktes 200 → nächster Status ohne Coverage → Unterabschnitt weg, Ansage im Status-Bereich.
- Aktiver Lauf → Knopf gesperrt, Grund sichtbar.
- 409 → Fehlerbanner.
- Nutzungsseite: `import-coverage` erst mit, nach `backfill.progress` (gemocktes SSE oder Neuladen) ohne Import → Beschriftung „seit …“ ohne Archiv-Satz.
- Mocks in `web/e2e/support/mocks.ts` (Teil B überarbeitet dort Kommentare `:1244-1254` — Merge-Stelle, §15).

### 11.5 Gates
`dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (ohne Api auf `:5151`), `node scripts/coverage-local.mjs`, Format/Lint. Dazu Live-Ablauf §13 (Regel 16).

## 12. Akzeptanzkriterien (pass/fail)

1. `DELETE /api/channels/{c}/backfill/imported`: anonym 401; angemeldet ohne Managerrecht (auch 7TV-Editor) 403; Flag aus 404 `backfill_disabled` (nach dem Filter); unbekannter Kanal 404 `channel_not_found`.
2. Erfolg: 200 `{ removedUsageRows, removedCoverageDays }`; danach für den Kanal 0 Zeilen `Source = 1` und 0 Coverage-Zeilen.
3. Alle `Source = 0`-Zeilen des Kanals sind in allen Spalten unverändert.
4. Andere Kanäle: `UsageStats`, Coverage, `EmoteSlots`, Läufe, Snapshots unverändert.
5. Aktiver Lauf (`queued`/`running`/`paused`) → 409 `backfill_remove_run_active`; nichts geschrieben, kein Audit, kein Event.
6. Zweiter Aufruf → 200 mit Nullen; kein Audit, kein Event.
7. Kein neuer zeilenloser unsettled Seed: die Zahl `SeedUnsettled AND NOT EXISTS UsageStats` der Slots des Kanals ist danach ≤ vorher; ein unsettled Seed, der nur importierte Zeilen trug, ist weg; ein unsettled Seed mit Live-Zeilen bleibt.
8. Platzhalter-Emotes, `CreatedSlot`-Slots, gesettelte Slots, Lauf- und Snapshot-Zeilen bleiben (Variante A); Operations Schritt 3/3b finden sie weiter.
9. Genau ein Audit-Eintrag `backfill.removeImported` mit Kanal und `backfillRemovedDays`; im Aktivitäts-Tab de/en beschriftet.
10. Nach Commit `backfill.progress` und `usage.flushed` für den Kanal.
11. Nebenläufigkeit S10–S12 grün; kein 40P01 erreicht den Client (Wiederholung).
12. Ein neuer Lauf desselben Sets/Fensters nach dem Entfernen läuft ohne doppelte `Emote`-Zeile durch (S13).
13. Settings-Tab: Unterabschnitt nur mit Coverage; gesperrt mit Grund bei aktivem Lauf; Dialog nennt Entferntes und Bleibendes; Abbrechen sendet nichts; 409 zeigt den Fehlertext; Erfolg wird angesagt und der Unterabschnitt verschwindet; Start und Entfernen sperren sich gegenseitig, solange eine Anfrage läuft.
14. Regel 7: Code in `ApiErrorCodes`, `api-error.ts`, de, en; `api-error-locales.spec.ts` grün.
15. Nutzungsseite ohne Neuladen: Beschriftung zurück auf „Wir zählen seit …“, Summen ohne importierte Tage, Vor-Zählbeginn-Warnung und „Gesamt“-Preset folgen wieder dem Zählbeginn.
16. Ballots und Export zeigen danach die Summen ohne importierte Tage (D30 liest `UsageStats` live; kein Code, nur Prüfung).
17. Keine Migration: `dotnet ef migrations list` zeigt nach dem PR dieselbe Liste wie davor; kein neuer Eintrag unter `Migrations/`.
18. Operations- und DECISIONS-Text wie §16.
19. Alle Gates aus §11.5 grün, `coverage-local.mjs` ≥ 80 % auf den neuen Dateien.

## 13. Live-Verifikation (Regel 16, lokal, Dev-Stack)

Voraussetzung: PR B (#379 Teil B) auf dem Branch bzw. darunter, Dev-DB durch die Zulassungsfolge und den einmaligen Sweep gegangen, `ChatLogBackfill:Enabled = true` lokal in `.env` (vor dem Start prüfen). Keine Prod-Verbindung. Alle Zahlen sind Dev-Zahlen und werden so berichtet.

1. Kanal mit vorhandenem Import auf der Dev-DB wählen (oder einen 1-Monats-Lauf auf einem eigenen Testkanal fahren und abwarten).
2. Vorher per psql festhalten: `count(*)` `Source = 1` des Kanals; Coverage-Zeilen des Kanals; `md5(string_agg(... ORDER BY "Id"))` über alle `Source = 0`-Zeilen des Kanals; derselbe Hash über **alle** `UsageStats` aller anderen Kanäle; Zahl der zeilenlosen unsettled Seeds des Kanals und global; Zahl der unsettled Seeds des Kanals, die nur `Source = 1`-Zeilen tragen.
3. Nutzungsseite des Kanals in einem zweiten Tab offen lassen (Beschriftung mit Archiv-Link sichtbar).
4. 409-Pfad: einen Lauf starten, „Entfernen“ ist gesperrt; per `curl` mit Session-Cookie die Route aufrufen → 409 `backfill_remove_run_active`; Lauf abbrechen.
5. Im Settings-Tab entfernen, Dialog bestätigen. Zeit bis zur Antwort notieren (Dev-Zahl).
6. Nachher: `Source = 1` des Kanals = 0; Coverage = 0; beide Hashes unverändert; zeilenlose unsettled Seeds des Kanals ≤ vorher; die Seeds mit nur importierten Zeilen aus Schritt 2 sind weg (Zahl = `RemovedSeeds` aus der Log-Zeile); Platzhalter, Läufe, Snapshots unverändert.
7. Zweiter Tab: Beschriftung ohne Archiv-Satz, Summen ohne importierte Tage, ohne Neuladen.
8. Aktivitäts-Tab: Eintrag „Importierte Daten entfernt · n Tage“.
9. Zweiter Aufruf per `curl` → 200 mit Nullen, kein zweiter Audit-Eintrag.
10. Neuer 1-Monats-Lauf auf demselben Set → läuft durch, Beschriftung kommt zurück (S13 live).

## 14. Offene Fragen (Betreiber)

### F1 — Granularität

| Option | Was | Für | Gegen | Migration |
|---|---|---|---|---|
| **(a) Ganzer Kanal** (Empfehlung) | §5 wie geschrieben | einfachster Vertrag, keine Auswahl-UI, deckt „Streamer will es nicht“ und „Daten taugen nichts“ | wer nur ein Set loswerden will, entfernt alles und importiert das andere neu | nein |
| (b) zusätzlich je Set | optionaler Query-Parameter `?emoteSetId=` (validiert mit `EmoteSetIdValidation` → 400 `invalid_emote_set_id`); Schritt 3/5 filtern `u."EmoteSetId" = @set`, Schritt 7 `"EmoteSetId" = @set`; Audit mit Ziel `emoteSet`; UI: ein Knopf je Set-Gruppe der Übersicht plus „Alles entfernen“ | Coverage und Zeilen tragen das Set schon (I1); günstig | mehr UI-Zustände und Tests; der Nutzen ist klein, weil ein Tag ohnehin nur einem Set gehört (OD-A) | nein |
| (c) je Lauf | „die Tage, die Lauf R noch besitzt“ = Coverage mit `RunId = R`; Zeilen `Source = 1` des Kanals an genau diesen Tagen | migrationsfrei (I1) | braucht eine Laufliste im Status (heute nur `activeRun`/`lastRun`), eine erklärungsbedürftige Semantik (ein späterer Lauf hat Tage übernommen) und ist nach der Retention des Laufs (365 Tage, `RunId → NULL`) nicht mehr adressierbar | nein |

Ändert sich bei (b): §4 D1, §5.1 (Parameter), §5.2 (Query + 400), §5.3 Schritte 3/5/7, §7 (Ziel), §10.2–10.4 (Knopf je Gruppe, Dialogtext je Set), §11 (Fälle je Set), §12 AK 2/13. Bei (c) zusätzlich §2.1-Statusmodell (Laufliste) — dann als eigenes Folge-Issue.

### F2 — Platzhalter-Emotes und vom Backfill angelegte Slots

| Option | Was fällt zusätzlich | Für | Gegen |
|---|---|---|---|
| **(A) Stehen lassen** (Empfehlung) | nichts | Sie sind ohne Zeilen unsichtbar (archiviert; Tags, Match-Map, aktive Ansicht, Belegung lesen nur aktive Zeilen/Slots; eine nicht aktive Ansicht zeigt nur Zeilen mit Zählung). Ein neuer Lauf und der REST-Abgleich verwenden sie wieder (gleicher Schlüssel bzw. gleiches `added_at`). Die Provenienz für Operations 3/3b bleibt erhalten. Kein Rennen mit einem Un-Archivieren durch REST. | Sie zählen weiter in `archivedEmoteCount` der Admin-Kanalliste; die DB sieht nicht aus „wie nie importiert“. |
| (B) Kanalbezogen mitlöschen | nach Schritt 6, vor 7: **B1** Gegenstück zu Operations Schritt 3, eingeschränkt auf `e."ChannelId" = @channelId`: `Emotes` mit `IsPlaceholder`, Snapshot-Zeile `CreatedRow`, ohne `Votes`, ohne `VoteSessionEmotes`, ohne **jede** `UsageStats`-Zeile (nach Schritt 5 gibt es nur noch `Source = 0`) — Kaskade: deren `EmoteSlots` (zeilenlos); **B2** Gegenstück zu 3b auf die Slots der Kanal-Emotes: `IsArchived AND ArchivedAt IS NULL AND CreatedSlot AND NOT EXISTS UsageStats` — Kaskade: keine. Beide als eigene Konstanten in `ChatLogImportRemovalSql`, Prädikat textgleich zu Operations bis auf den Kanalfilter. Die Emote-Zeilen aus B1 werden durch das `DELETE` selbst gesperrt; die Serialisierung mit REST trägt die Kanalsperre aus Schritt 1, und ein REST-Un-Archivieren löscht `IsPlaceholder`, so dass das Prädikat nach dem Warten nicht mehr trifft. | DB nahe an „nie importiert“; der spätere globale Rückbau hat weniger zu tun | mehr Löschpfad mit Kaskade auf `Emotes` in einer Nutzeraktion; Snapshot-Zeilen zeigen danach ins Leere (zulässig, D25); ein Platzhalter, den ein **anderer** Lauf nur gesnapshottet hat (`CreatedRow = false` dort), fällt trotzdem, wenn **ein** Lauf ihn angelegt hat; zusätzliche Tests (Votes, Ballots, Live-Nutzung, fremder Kanal). |

Ändert sich bei (B): §4 D6, §5.3 (Schritte B1/B2), §5.4 (Tabelle), §11.1 S7 (umgekehrt) plus Fälle analog AK 32 der Backfill-Spec, §12 AK 8, §16 (Operations: Schritt 3/3b sind für diesen Kanal dann schon erledigt).

### F3 — Flag-Gating

| Option | Was | Empfehlung |
|---|---|---|
| **(a) Hinter `ChatLogBackfill:Enabled`** | wie die drei Backfill-Routen; aus → 404 | **ja** — ohne Flag gibt es keinen Settings-Tab, und „Flag aus“ ist der Betreiber-Rückbau, für den Operations gilt |
| (b) Route immer erreichbar, UI nur mit Flag | gleiche Route ohne Flag-Prüfung | praktisch nur per `curl` nutzbar; trennt Route und UI ohne Gewinn |
| (c) Route immer, UI zusätzlich auf der Nutzungsseite neben der Beschriftung | Entfernen auch bei Flag aus | widerspricht B7 „No link from there to the backfill UI“ und legt eine destruktive Aktion auf eine Leseseite |

Ändert sich bei (b)/(c): §4 D3, §5.2 (kein 404 `backfill_disabled`), §11.2, §12 AK 1, bei (c) §10.

### F4 — Anzeige nach dem Entfernen (Lauf-Historie)

| Option | Was | Für | Gegen |
|---|---|---|---|
| **(a) Läufe unverändert** (Empfehlung) | „Letzter Lauf“ zeigt weiter z. B. „Abgeschlossen, 27 von 27 Wochen“; die Übersicht „Importierte Daten“ ist leer bzw. weg; Wer/Wann im Aktivitäts-Tab | ehrlich (der Lauf ist so gelaufen), kein neues Feld | ein Leser kann „abgeschlossen“ und „keine Daten“ nebeneinander kurz irritierend finden |
| (b) Hinweis „Importierte Daten entfernt am … von …“ | Status-DTO bekommt `lastRemoval { atUtc, byLogin }`, gelesen aus dem neuesten `backfill.removeImported`-Audit-Eintrag des Kanals | erklärt den Zustand im Tab selbst | Produktzustand aus dem Audit-Log lesen (Retention 365 Tage, Pseudonymisierung); fasst `GetStatusAsync`/`ChatLogBackfillStatusDto` an — die Teil-B-Datei |
| (c) Spalte am Lauf | `ChatLogBackfillRuns.DataRemovedAtUtc` | genau | **Migration** — während der Bewährung ausgeschlossen |

Ändert sich bei (b): §4 D10/D14, §10.2, §11 (Status-DTO-Test), Überschneidung mit Teil B in `ChatLogBackfillService.cs` (§15).

## 15. Überschneidung mit #379 Teil B

Teil B (Branch `feat/379-flag-removal`, Stand jetzt: ein Doku-Commit, Testumbau uncommittet, `src` noch unverändert) fasst laut Plan-379 §4/§5 und Koordinator `ChatLogBackfillService`, `IChatLogBackfillService`, `ChatLogBackfillWorker`/`SnapshotMap`, `SevenTvSyncService`, `SevenTvApiClient`, `EmoteService`, `EmoteSetStatusService`, `UsageStatQueryService`/`UsageViewRows`, `VoteSessionQueryService`, `UsageStatFlushService`, `UsageFlushWorker`, `PendingSlotKey`, `BaseSlotRule`, `ServiceCollectionExtensions`/`SlotReconcileOptions`, appsettings/`.env.example`/Compose, viele Tests, Web-Kommentare/Shims, Operations (Slot-Abschnitt, 3b-Hinweis), Architectur, DECISIONS (Child 9), drei Zeilen der Backfill-Spec an.

**#384 fasst voraussichtlich an — Überschneidung fett:**

| Datei | Art |
|---|---|
| `src/EmotePurge.Core/Services/IChatLogImportRemovalService.cs` | neu |
| `src/EmotePurge.Infrastructure/Services/ChatLogImportRemovalService.cs`, `ChatLogImportRemovalSql.cs` | neu |
| **`src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs`** | eine Registrierungszeile; Teil B entfernt dort die `SlotReconcileOptions`-Bindung |
| `src/EmotePurge.Core/Entities/AuditLogEntry.cs`, `src/EmotePurge.Core/Services/IAuditLogQueryService.cs`, `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs` | Aktion + Detail-Art |
| `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs`, `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | Route, Code |
| **`tests/EmotePurge.Api.Tests/ApiFactory.cs`** | Substitution; Teil B kann dort Flag-Reste entfernen (Plan nennt `SevenTvEmoteSetSyncBookkeepingEndpointTests`, nicht die Factory — prüfen) |
| `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`, `EmoteRoutePolicyTests.cs`, neue Endpoint-Tests | Matrix |
| `tests/EmotePurge.Infrastructure.Tests/Integration/ChatLogImportRemovalServiceTests.cs`, ggf. `Fixtures/*` | neu; **keine** Änderung an `ChatLogBackfillServiceTests.*` (Teil B) |
| `web/src/app/core/channels/backfill.service.ts`/`.model.ts`/`.spec.ts`, `web/src/app/features/channel-workspace/backfill-section.ts`/`.spec.ts`, `web/src/app/core/i18n/api-error.ts`, `web/src/app/shared/audit/audit-actions.ts`, `web/public/i18n/de.json`/`en.json` | UI, i18n |
| **`web/e2e/support/mocks.ts`**, `web/e2e/channel-settings.e2e.spec.ts` | Mocks; Teil B fasst Kommentare `mocks.ts:1244-1254` an |
| **`docs/Operations.md`** | „Chat-log backfill“: neuer Unterabschnitt + „Rolling back“ Einleitung/Schritt 4; Teil B ändert den 3b-Hinweis (~`:1037`) und den Slot-Abschnitt — benachbarte Stellen |
| **`docs/DECISIONS.md`** | neuer Eintrag oben; Teil B ergänzt Child 9 — nur Kopf-Konflikt |
| `docs/Feature-Ideen-2026-08-01.md` | Statuszeile A17 |

Nicht angefasst: `ChatLogBackfillService.cs`, `IChatLogBackfillService.cs`, der Worker, `EmoteSlotRollbackSql.cs`, die Backfill-Spec (Teil B markiert dort Zeilen historisch). Bei F4 (b) käme `ChatLogBackfillService.cs`/`IChatLogBackfillService.cs` hinzu.

**Reihenfolge:** #384 setzt auf PR B auf (Rebase nach dessen Merge). Fachlich hängt #384 nicht an PR B (§3.4: korrekt für Flag an, das auf Prod seit 13.10. gilt), nur die Tests von S6 gegen die Sweep-Konstante und die Aussage „keine neuen unsettled Seeds“ (§2.4) setzen Teil B voraus.

## 16. Doku bei Umsetzung

- **`docs/Operations.md` „Chat-log backfill“**: neuer Unterabschnitt „Removing imported data (channel managers)“ (englisch): was die Aktion löscht (§5.4), was nicht, dass sie hinter dem Flag liegt und bei aktivem Lauf 409 antwortet, dass sie zeilenlos gewordene unsettled Seeds selbst entfernt, Audit-Aktion.
- **„Rolling back“**: die Prozedur bleibt der **globale Rückfall** und der einzige Weg für Schritt 3/3b/5, bei Flag aus und für mehrere Kanäle. Einleitung bekommt einen Satz: „For a single channel, a manager can remove the imported rows and the coverage from the Settings tab (#384); that replaces the per-channel variant of step 4 but not steps 3, 3b and 5.“ Schritt 4, je-Kanal-Variante: Hinweis, dass sie auf einer Datenbank mit Importen aus der Flag-aus-Zeit unsettled Seeds zeilenlos machen kann — danach die Sweep-Konstante aus #379 Teil B laufen lassen oder die UI-Aktion verwenden, die das selbst erledigt.
- **`docs/DECISIONS.md`**: ein englischer Eintrag „Channel managers remove a channel's imported backfill data; seeds left rowless go with it, inventory and run history stay (#384)“ mit `**Betrifft:**`-Zeile; Kern: D2, D4–D8, D10, D13, die Sperrreihenfolge und warum keine Migration (§3.3).
- **`docs/Feature-Ideen-2026-08-01.md`**: Statuszeile A17 um „Entfernen im UI (#384)“ ergänzen (Regel analog 3).
- **`CLAUDE.md`** Umsetzungsstand, Zeile Chat-Log-Backfill: ein Halbsatz zum Entfernen.

## 17. Aufwand

| Teil | Tage |
|---|---|
| Service + SQL + Infrastructure-Tests inkl. Rennen | 1,25 |
| Route, Code, Audit, Api.Tests | 0,5 |
| UI, i18n, Vitest, E2E | 1 |
| Doku, Live-Ablauf | 0,5 |
| **Summe** (Variante a/A/a/a) | **≈ 3,25** |

F2 (B) + 0,5; F1 (b) + 0,5; F4 (b) + 0,5.
