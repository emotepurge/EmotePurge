# Plan #371 — Emote-Slots, Kind 1: M1 `AddEmoteSlots` (additives Schema + Seeds)

Erstellt am 2026-10-11 im Worktree `EmotePurge-slots` (Branch `feat/371-emote-slot-schema`, Basis `origin/main` @ `32a46f73`).
**Status: Entwurf, noch nicht freigegeben.** Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md)
(§4 Datenmodell, §11.1 M1, §11.2 Fenster, §12.2 Rollback, §13 AK 1–4 und AK 44, §15 Tests, §16 Kind 1), Issue #371, Epic #365.
Formatvorlage: [Plan-346](Plan-346-Chat-Log-Backfill.md) (Aufbau, nicht Umfang).

**Was dieser Plan trägt:** nur, was die Spec offen lässt — Seed-Werte, Schrittfolge, Grenze der AK im Kind 1, Ort des Kollaps-SQL,
Tests, Live-Prüfung, Gates, Commits, Aufgabenfolge. Kein Code. Bei Abweichung gilt die Spec; der Fund wird gemeldet. Die
Betreiber-Entscheidungen D0–D33 werden nicht wieder aufgemacht.

**Lesehinweis:** Zeilen mit **[E]** sind Planer-Entscheidungen, kein Spec-Text — der Betreiber kann jede einzeln kippen. Was nur er
entscheiden kann, steht in Abschnitt 12.

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 1, D19)

1. Migration `AddEmoteSlots`, rein additiv: Tabelle `EmoteSlots` mit beiden Indizes, `UsageStats.SlotId` (nullable, FK),
   `ChatLogBackfillRunEmotes.SlotId` (nullable, **kein** FK) und `CreatedSlot`, `VoteSessionEmotes.NamesAtCreation text[] NULL`,
   `SET LOCAL lock_timeout = '5s'`.
2. Entität `EmoteSlot` in `EmotePurge.Core.Entities`, Navigation `Emote.Slots` / `EmoteSlot.Emote`, die neuen nullable Properties,
   das `AppDbContext`-Mapping, der neu erzeugte Model-Snapshot.
3. Seeds je distinct `(UsageStats.EmoteId, UsageStats.EmoteSetId)` mit vorhandenen Zeilen, als unsettled Seeds (D12); jede
   `UsageStats`-Zeile hängt nur am Seed ihres eigenen Paars.
4. Befüllte `Up`-/`Down`-Fixtures; das Kollaps-SQL aus §12.2 als Konstante **und** als Test-Fixture.
5. Kaskaden-Nachweis: Purge (Admin, Retention, Broadcaster-Selbstbereinigung #245), Account-Löschung, Merge brauchen keine
   Änderung; Nachweis in diesem Kind.
6. **Kein Schreiber ändert sich.** Flush, Zähler, Match-Map, Backfill-Insert, Snapshot-Erzeugung, `ArchivedEmoteRowUpsert`,
   Ballot-Erzeugung bleiben byteidentisch. Sie schreiben nach M1 Zeilen mit `SlotId = NULL`; das ist im additiven Stand gewollt.

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 2 (#372) | Settle je Set (6.3), `ReconcileAsync` je Slot, D7, D13, `ArchivedSlotUpsert`, `IsSlotKeyConflict`, SELECT FOR UPDATE |
| Kind 3 (#373) | M2 `SwitchUsageStatsToSlotKey`, alle `UsageStats`-Schreiber, M2-`Down`-Guard |
| Kind 5 (#375) | M3 `SwitchBackfillSnapshotToSlotKey`, M3-`Down`-Guard, Operations-Schritt 3b, `backfill_slots_unsettled` |
| Kind 9 (#379) | Operations.md (Rollback-Ablauf samt Kollaps-SQL-Text), Architectur.md, DECISIONS final, Runbook |

### 1.3 Vorgänger-Migration — Abweichung von der Spec

Die Spec (§12.2 Schritt 3) nennt `20261009102325_AddChatLogBackfill` als Rollback-Ziel. Seit #380 (`32a46f73`) ist die neueste
Migration `20261011113207_AddSystemLoadSampling`; `AddEmoteSlots` sortiert dahinter, und ein `database update AddChatLogBackfill`
würde die Lastmessung mit zurücknehmen.

- **[E1]** Rollback-Ziel von M1 ist **immer die Migration unmittelbar vor `AddEmoteSlots`, wie `dotnet ef migrations list` sie zeigt** —
  heute `AddSystemLoadSampling`. Der Test findet den Vorgänger **per Namen** über `IMigrationsAssembly` (Muster
  `AddChatLogBackfillMigrationTests`, DECISIONS 2026-10-09), nicht über eine hart kodierte Id. Mergt vor dem Fenster eine weitere
  Migration, bleibt alles richtig.
- Die Spec wird in Kind 1 **nicht** editiert (sie ist nach R5 fertig); die Abweichung steht hier, in der Klassendoku der Migration
  und im PR-Text. Kind 9 schreibt das Ziel in Operations.md und das Runbook.

## 2. Verträge

### 2.1 Entität `EmoteSlot` (Core, nur BCL — `CoreAssemblyReferenceTests` bleibt grün)

Spalten exakt wie Spec §4.1: `Id` (Guid-String, CLR-Default wie `Emote.Id`), `EmoteId`, `Name`, `AddedAt`, `AddedAtFromRest`,
`IsArchived`, `ArchivedAt`, `LastSyncedAt`, `SeedUnsettled`; Navigation `Emote`. Zeittypen wie bei `Emote` (`DateTime`, UTC,
`timestamptz`) — **[E2]** `AddedAt` ist `DateTime?`, nicht `DateTimeOffset?`, weil `Emote.FirstSeenAt` denselben 7TV-Wert bereits so
hält und die Leser (Kind 6) einen Typ erwarten. Kommentare englisch, Member-Reihenfolge nach Regel 19.

### 2.2 Geänderte Entitäten

| Entität | Neu | Bemerkung |
|---|---|---|
| `Emote` | `ICollection<EmoteSlot> Slots` | wie `UsageStats`, initialisiert |
| `UsageStat` | `string? SlotId` | **[E3]** keine `Slot`-Navigation und keine `EmoteSlot.UsageStats`-Collection in Kind 1 — kein Leser braucht sie; wer eine braucht, fügt sie in seinem Kind hinzu |
| `ChatLogBackfillRunEmote` | `string? SlotId`, `bool CreatedSlot` | Kommentar im Stil von `CreatedRow`; kein FK (Begründung steht schon am Typ) |
| `VoteSessionEmote` | `string[]? NamesAtCreation` | Npgsql → `text[]`; `NameAtCreation` bleibt |

### 2.3 `AppDbContext`-Mapping

| Objekt | Vertrag |
|---|---|
| `EmoteSlots` → `Emotes` | FK `EmoteId`, `WithMany(e => e.Slots)`, `DeleteBehavior.Cascade` |
| Partieller Unique-Index | `(EmoteId, AddedAt, Name)`, Filter `"AddedAtFromRest"`, Name **exakt** `IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative` (Kind 2 referenziert ihn als Konstante) |
| Nicht-unique Index | `(EmoteId, Name)`, Name `IX_EmoteSlots_EmoteId_Name` — explizit setzen, nicht dem EF-Default überlassen |
| DB-Defaults | `AddedAtFromRest`, `IsArchived`, `SeedUnsettled` → `DEFAULT false` (wie `IsPlaceholder`: Bilder, die die Spalte nicht nennen, landen auf `false`) |
| `UsageStats` → `EmoteSlots` | FK `SlotId` (nullable), `DeleteBehavior.Cascade`; Index `IX_UsageStats_SlotId_EmoteSetId_Date` auf `(SlotId, EmoteSetId, Date) INCLUDE (UseCount)`, **nicht** unique; der bestehende unique `(EmoteId, EmoteSetId, Date)`-Index bleibt unverändert (Tausch ist M2) |
| `ChatLogBackfillRunEmotes` | `SlotId` ohne FK; `CreatedSlot` mit `DEFAULT false`; PK bleibt `(RunId, EmoteId)` |
| `VoteSessionEmotes` | nur die Spalte |

**[E4]** Kein CHECK-Constraint (etwa `AddedAtFromRest ⇒ AddedAt IS NOT NULL`): die Spec nennt keinen, Kind 2 ist der einzige Schreiber
dieser Kombination und kann ihn bei Bedarf selbst einführen. Zwei Kaskadenpfade in `UsageStats` (`Emote →` und `EmoteSlot →`) sind für
Postgres ein gewöhnlicher Diamant; EF Core kennt auf Npgsql keine „multiple cascade paths"-Sperre.

### 2.4 Migration `AddEmoteSlots`

Scaffold per `dotnet ef migrations add AddEmoteSlots` (Startprojekt `EmotePurge.Api`, keine DB-Verbindung nötig), danach `Up` von
Hand in die Reihenfolge unten gebracht. Jeder handgeschriebene Block ist als solcher markiert (`HAND-WRITTEN (n of m)`, Muster
`AddChatLogBackfill`), damit er beim Re-Scaffold wörtlich übernommen werden kann (DECISIONS 2026-10-09). Alles läuft in **einer**
Transaktion (EF-Migrator-Default; `SET LOCAL` ist deshalb transaktionslokal).

| Schritt | Inhalt | Art |
|---|---|---|
| 1 | `SET LOCAL lock_timeout = '5s';` | Hand |
| 1b | **[E5]** `LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE` — das `ALTER TABLE` in Schritt 4 nimmt diese Sperre ohnehin; sie früher zu nehmen heißt, dass der Seed in Schritt 3 die endgültige Zeilenmenge sieht und kein Flush zwischen Seed und `ALTER` ein neues Paar einfügen kann. Mit `lock_timeout` 5 s scheitert eine vergessene psql-Sitzung schnell statt stumm zu warten. | Hand |
| 2 | `CREATE TABLE "EmoteSlots"` samt FK und beiden Indizes | EF |
| 3 | Seed: Hilfstabelle (`TEMP … ON COMMIT DROP`) mit `(SlotId, EmoteId, EmoteSetId)` je distinct Paar aus `UsageStats`, weil `EmoteSlots` das Set nicht trägt und alle drei Befüllungen dasselbe Paar→Slot-Mapping brauchen; daraus `INSERT INTO "EmoteSlots"` mit den Werten aus Abschnitt 3 (E6–E8) | Hand |
| 4 | `ALTER TABLE "UsageStats" ADD COLUMN "SlotId" text NULL` → `UPDATE` aus der Hilfstabelle über `(EmoteId, EmoteSetId)` → **Abbruchprüfung** (unten) → FK anlegen (nach dem `UPDATE`, damit die FK-Validierung ein Durchlauf ist) | EF + Hand |
| 5 | `CREATE INDEX "IX_UsageStats_SlotId_EmoteSetId_Date" … INCLUDE ("UseCount")` — nach der Befüllung, nicht davor | EF (umsortiert) |
| 6 | `ChatLogBackfillRunEmotes`: `SlotId text NULL`, `CreatedSlot boolean NOT NULL DEFAULT false`; `UPDATE` über `ChatLogBackfillRuns.EmoteSetId` des Laufs und den Seed desselben `(EmoteId, EmoteSetId)`; Zeilen ohne Seed bleiben `NULL` (E9) | EF + Hand |
| 7 | `VoteSessionEmotes`: `NamesAtCreation text[] NULL` | EF |

**Abbruchprüfung (Schritt 4, `DO $$ … RAISE EXCEPTION`, englische Meldung mit Zählwerten wie in `AddChatLogBackfill`):**
(a) keine `UsageStats`-Zeile mit `SlotId IS NULL`; (b) keine Zeile, deren Slot ein anderes `EmoteId` trägt; (c) kein Slot, an dem
Zeilen aus mehr als einem `EmoteSetId` hängen; (d) Zahl der Seeds = Zahl der distinct Paare. Auf einer konsistenten DB ist die Prüfung
nicht auslösbar (FK `UsageStats → Emotes`, Sperre aus 1b) — sie ist der Gurt, kein Pfad. Scheitert sie, rollt die Transaktion
alles zurück, die Migrationshistorie bleibt ohne Eintrag.

**`Down`:** EF-generiert, kein Guard (Spec: „alles fällt"): FK und Index auf `UsageStats`, die vier Spalten, Tabelle `EmoteSlots`.
Die Seed-Zuordnung geht damit verloren; ein erneutes `Up` seedet mit neuen Ids identisch (Test 8). Nach M2 ist M1-`Down` nur über
M2-`Down` erreichbar (Kind 3).

### 2.5 Kollaps-SQL (§12.2) — Ort und Inhalt

**[E10]** Eine eigene statische Klasse `EmoteSlotRollbackSql` in `src/EmotePurge.Infrastructure/Migrations/` (Präzedenz für
Nicht-Migrationsdateien dort: `SetSwitchAssignments.cs`, `UsageStatMigrationChecks.cs`; Sonar schließt `**/Migrations/**` aus)
mit zwei `public const string`: `CollapseUsageStatsSql` und `CollapseBackfillSnapshotSql`. Muster `AddEmotePlaceholderMarker.BackfillSql`:
**eine** Quelle für Tests (Kind 1, 3, 5) und den Textblock in Operations.md (Kind 9, mit dem Hinweis „identisch mit der Konstante").
Nicht in der M1-Klasse, weil das SQL dem Zustand **nach** M2/M3 gilt und M2/M3-`Down` vorausgeht.

Inhalt als Absicht (die Spec sagt „Summen"; **[E11]** gemeint sind die drei Zählspalten `UseCount`, `BotUseCount`, `SharedChatUseCount`):

- `UsageStats`: je `(EmoteId, EmoteSetId, Date)` mit > 1 Zeile ist der Gewinner die Zeile des Slots mit kleinstem `AddedAt`
  (`NULLS LAST`), dann ordinal `Name` (`COLLATE "C"`), dann `SlotId` als deterministischer letzter Schlüssel. Gewinner erhält die drei
  Summen der Gruppe; `Source` = 0, wenn eine Zeile der Gruppe 0 ist, sonst 1; übrige Zeilen löschen. Gruppen mit einer Zeile bleiben
  unberührt; ein zweiter Lauf ändert 0 Zeilen.
- `ChatLogBackfillRunEmotes`: je `(RunId, EmoteId)` mit > 1 Zeile die Zeile des ältesten Slots (gleiche Ordnung) behalten.

## 3. Planer-Entscheidungen zu den offenen Fragen

| # | Frage | Entscheidung | Begründung / verworfene Alternative |
|---|---|---|---|
| **[E6]** | `Name` des Seeds | `Emotes.Name` zum Migrationszeitpunkt | Der einzige Name, den wir je Emote haben; D11(a) macht ihn bis zum Settle zur Identität |
| **[E7]** | `LastSyncedAt` | `now()` der Migrationstransaktion — alle Seeds tragen denselben Stempel | Spec: „gestempelt bei Anlage". `Emote.LastSyncedAt` verworfen: es würde einen Abgleich behaupten, der den Slot nie gesehen hat. Der gemeinsame Stempel ist zugleich ein brauchbares Erkennungsmerkmal der Seeds im Betrieb |
| **[E8]** | `IsArchived` / `ArchivedAt` je Seed | `IsArchived = Emotes.IsArchived OR EmoteSetId ≠ Channels.ActiveEmoteSetId`; `ArchivedAt = Emotes.ArchivedAt`, wenn das Emote archiviert ist, sonst `NULL` | **Weicht vom Wortlaut „Archivzustand aus `Emotes`" ab** — siehe Rückfrage R1. Grund: mit Seeds je Set wäre der Seed eines alten Sets B nach dem Wortlaut „aktiv", obwohl der Kanal auf A steht; die aktive Ansicht (Kind 6) zeigte dann zwei gleichnamige Karten, und der erste Abgleich (6.2 Schritt 7) archivierte B mit `ArchivedAt = now`, einem falschen Datum. So ist B von Anfang an archiviert mit `ArchivedAt = NULL` („Zeitpunkt unbekannt"), was dem Endzustand entspricht, den Kind 5 für nicht aktive Sets ohnehin herstellt. Die Invariante 4.1 (aktiver Slot ⇒ aktives Emote) gilt in beiden Lesarten; Settle (6.3) ist vom Archivstatus unabhängig. Kanäle ohne `ActiveEmoteSetId` (leer) bekommen nur archivierte Seeds |
| **[E9]** | Snapshot-Zeilen ohne Seed ihres Sets | `SlotId` bleibt `NULL`, `CreatedSlot = false`, kein Abbruch | D12 erlaubt Seeds nur für Paare mit Nutzung; einen Seed für ein Paar ohne Zeilen zu erfinden, änderte D12. Die Spec nennt die Abbruchprüfung nur für `UsageStats`. Auf Prod ist die Menge leer (Vorbedingung 3a: 0 Läufe). Folge für M3 steht in Abschnitt 11 |
| **[E12]** | Id-Erzeugung im SQL | `gen_random_uuid()::text` | Postgres ≥ 13 (Dev und Prod: 16); liefert dieselbe 36-stellige, kleingeschriebene Bindestrich-Form wie `Guid.ToString()` — ein Leser kann Seed-Ids und Code-Ids nicht unterscheiden, und das soll so sein |
| **[E13]** | AK 1/3/4 an der Kind-1-Grenze | **AK 2 vollständig** (Test + Live). **AK 1:** M1-Teil auf einer Kopie der lokalen Dev-DB (Abschnitt 7), `migrations list` ohne Pending auf der Kopie; M2/M3 und die Prod-Kopie → Kind 9 (R2). **AK 3:** M1-`Down` entfernt alles (Test 7) und das Kollaps-SQL hält die Summen (Test 9 auf simuliertem M2-Stand); M2-/M3-`Down`-Guards → Kinder 3/5. **AK 4:** getrennte Seeds A/B, Zeilen je Set, Summen je Set, Snapshot-Zeilen mit Seed, `CreatedSlot = false`, `NamesAtCreation NULL` (Tests 2/4); Settle → Kind 2; „M2/M3 darauf grün" → Kinder 3/5 | Aufzeichnung: der PR-Text trägt eine AK-Tabelle mit Stand je Teilaussage („erfüllt" / „Rest in #37x"); je ein Kommentar auf #373 und #375 nennt die Konstante und die Fixture, die dort wiederzuverwenden sind |
| **[E14]** | Kollaps-Test ohne M2 | Der Test stellt den M2-Zustand her, soweit er fürs Kollaps-SQL zählt: nach `Up` wird im Scratch-Schema der unique `(EmoteId, EmoteSetId, Date)`-Index per Raw-SQL gelöscht, dann zwei Slot-Zeilen je Zelle eingefügt, das SQL gefahren, danach der Unique-Index wieder angelegt (das ist die Form des M2-`Down`-Guards) und M1-`Down` gefahren | Ohne den Index-Drop hat das SQL keinen Input. Der Snapshot-Kollaps ist in Kind 1 **nicht** fixturbar (PK `(RunId, EmoteId)` verbietet den Input bis M3) — Kind 5 testet ihn gegen den echten M3-Stand; Kind 3 fährt Test 9 zusätzlich gegen das echte M2-`Down` |
| **[E15]** | Slot-Zeilen in Purge-/Retention-Zählwerten | Nein | Spec 4.6: Purge, Retention, Account-Löschung „unverändert"; `CountChannelCascadeAsync` zählt `UsageRows` über `EmoteId`, das bleibt vollständig. Ein neuer Zählwert wäre ein neuer Vertrag im Trockenlauf-Log ohne Auftrag |

## 4. Randfälle

| Fall | Verhalten in M1 | Nachweis |
|---|---|---|
| Emote ohne `UsageStats` | kein Seed (D12); Slots entstehen erst durch den REST-Abgleich (Kind 2) | Test 2 (Emote Z) |
| Emote mit Nutzung in Sets A und B | zwei Seeds, Zeilen je Set getrennt, Summen je Set unverändert | Test 2, Live-Check 3–5 |
| Archiviertes Emote mit `ArchivedAt` | Seed archiviert mit demselben `ArchivedAt` | Test 3 |
| Platzhalter (`IsPlaceholder`, archiviert, `ArchivedAt NULL`) | Seed archiviert, `ArchivedAt NULL` („war nie drin"); nur möglich mit `Source = 1`-Zeilen | Test 3 |
| Aktives Emote, Zeilen nur in nicht aktivem Set | Seed archiviert, `ArchivedAt NULL` (E8) | Test 3 |
| Paar nur mit `Source = 1`-Zeilen / gemischt | Seed wie jedes andere Paar; `Source` bleibt je Zeile | Test 2 |
| `Emote.FirstSeenAt` gesetzt | wird **nicht** übernommen; `AddedAt NULL` auf allen Seeds | Test 2, Live-Check 6 |
| Snapshot-Zeile, deren Emote im Set des Laufs keine Nutzung hat | `SlotId NULL`, `CreatedSlot false` (E9) | Test 4 |
| Snapshot-Zeile mit Nutzung im Set des Laufs | `SlotId` = Seed dieses Paars, auch wenn der Kanal inzwischen auf einem anderen Set steht | Test 4 |
| `VoteSessionEmotes` | nur die Spalte; Bestandszeilen `NamesAtCreation NULL` | Test 4 |
| Leere DB | alle Hand-Schritte sind No-ops; genau das tut `PostgresFixture` bei jedem Suite-Start | Test 1 |
| Wiederholung | `Up` ist nicht idempotent und läuft durch den EF-Migrator genau einmal; `Up → Down → Up` liefert dieselbe Seed- und Zuordnungsstruktur mit neuen Ids | Test 8 |
| `lock_timeout` reißt (fremde Sitzung hält `UsageStats`) | Fehler 55P03 nach ≤ 5 s, Transaktion zurückgerollt, keine Tabelle, keine Spalte, kein Historieneintrag; Betreiber sucht den Blocker in `pg_stat_activity` und wiederholt | Test 6 |
| Zwei Kaskadenpfade in `UsageStats` | Löschen eines Emotes trifft eine Zeile über beide Pfade — Postgres löscht sie einmal, kein Fehler | Cascade-Test b |
| Kanal ohne `ActiveEmoteSetId` | alle Seeds archiviert (E8); Settle beim Rejoin ist Kind-2-Sache | Test 3 (ein Kanal mit leerem Set) |

## 5. Kaskaden-Nachweis

Kein Codepfad löscht `Emotes` direkt (geprüft: kein `Emotes.Remove`, kein `DELETE FROM "Emotes"` in `src`). Alle harten Löschungen von
Emote-Besitz laufen über `Channels.Remove` — `ChannelService.StagePurge` (Admin-Purge, Retention-Purge, Broadcaster-Selbstbereinigung
#245) und `ChannelIdentityService` (Merge mit emote-losem Verlierer; ein Verlierer mit Emotes wird vor jedem Schreiben verweigert).
`AccountDeletionService` löscht Votes und den User, nie Emotes. Die neuen FKs sind beide `Cascade`, also blockiert nichts und nichts
bleibt zurück.

Der Nachweis hat zwei Hälften, beide im PR benannt:

1. **„0 geändert" (Spec §15):** `ChannelRetentionPurgeTests`, `ChannelBroadcasterPurgeTests`, `DataRetentionServiceTests`,
   `AccountDeletionServiceTests`, `ChannelIdentityServiceTests` bleiben byteidentisch und sind an der Merge-Grenze grün — gegen die
   `PostgresFixture`-DB, die jetzt M1 enthält. `git diff --stat origin/main -- tests/` im PR-Text als Beleg.
2. **Neue Klasse `EmoteSlotCascadeTests`** (Integration, geteilte Fixture-DB, EF-Seeding, weil das Modell den Slot jetzt kennt):
   Kanal, Emote, Slot, `UsageStats`-Zeile mit `SlotId`, Backfill-Lauf mit Snapshot-Zeile mit `SlotId`.
   - (a) `ChannelService.PurgeAsync` (Konstruktion wie in `ChannelRetentionPurgeTests`) → keine `EmoteSlots`-, keine `UsageStats`-Zeile
     des Emotes mehr; Snapshot-Zeile fällt mit dem Lauf.
   - (b) Raw `DELETE` der Emote-Zeile → Slot und Nutzungszeile weg, kein Fehler trotz Diamant.
   - (c) Raw `DELETE` der Slot-Zeile → nur ihre Nutzungszeilen weg; Emote bleibt; Snapshot-Zeile behält ihre `SlotId` als Text
     (kein FK, Absicht) — das ist die Form von Operations-Schritt 3b (Kind 5).

## 6. Tests

### 6.1 `AddEmoteSlotsMigrationTests` (Integration, Muster `AddUsageStatEmoteSetIdMigrationTests`)

Fixture-Ansatz: `[Collection("Postgres")]`; je Fall eine eigene DB auf dem Container (`CREATE DATABASE` über den Admin-Kontext,
EF1002-Pragma wie dort), migriert auf den **per Namen** gefundenen Vorgänger (E1). Seeds auf dem Vorgängerstand: EF für unveränderte
Tabellen (`Channels`, `Emotes`, `ChatLogBackfillRuns`, `VoteSessions`), **Raw-SQL** für die drei Tabellen mit neuen Spalten
(`UsageStats`, `ChatLogBackfillRunEmotes`, `VoteSessionEmotes`), weil das Modell dort Spalten kennt, die noch nicht existieren.
Abfragen nach `Up` über EF oder `SqlQueryRaw`; Schema über `information_schema`/`pg_indexes`/`pg_constraint` (`confdeltype = 'c'`).
Helfer (`MigrateAsync`, `MigrateAndCaptureFailureAsync`, `ColumnExistsAsync`, `TableExistsAsync`) nach dem bestehenden Muster; ein
gemeinsamer Fixtures-Helfer für die Vorgängersuche ist erlaubt, kein Muss.

| # | Fall | Prüft |
|---|---|---|
| 1 | `Up_RunsThroughOnAnEmptyDatabase_AndCreatesTheSchema` | Tabelle, vier neue Spalten, beide Slot-Indizes mit exakten Namen (partieller `WHERE` in der `indexdef`), `UsageStats`-Index mit `INCLUDE`, beide FKs mit `CASCADE`; 0 Seeds |
| 2 | `Up_SeedsOneUnsettledSeedPerEmoteSetPair_AndAttachesOnlyThatSetsRows` | AK 2 + AK 4 erste Hälfte: X mit Zeilen in A und B (davon eine `Source = 1`), Y nur in A, Z ohne Nutzung; X hat `FirstSeenAt`; Erwartung: 2/1/0 Seeds, jede Zeile am Seed ihres Paars, `AddedAt NULL`, `AddedAtFromRest false`, `SeedUnsettled true`, Zeilenzahl und Summen je Paar vor/nach gleich |
| 3 | `Up_CopiesNameAndArchiveState_AndArchivesSeedsOfNonActiveSets` | E6–E8: aktives Emote in A (Seed aktiv), archiviertes Emote mit `ArchivedAt` (übernommen), Platzhalter (archiviert, `NULL`), aktives Emote nur in B (archiviert, `NULL`), Kanal mit leerem `ActiveEmoteSetId`; `LastSyncedAt` auf allen Seeds identisch und ≥ Teststart |
| 4 | `Up_FillsSnapshotSlotIdFromTheRunsSet_LeavesRowsWithoutSeedNull_AndAddsNamesAtCreation` | AK 4 zweite Hälfte + E9: Lauf auf B mit Snapshot-Zeilen für X (Seed B) und W (ohne Nutzung in B → `NULL`); `CreatedSlot false`; `NamesAtCreation NULL` auf einer Bestandszeile |
| 5 | `Up_DoesNotChangeAnyEmoteRow` | `Emotes` vor/nach byteidentisch (insbesondere `FirstSeenAt`, `Name`, `IsArchived`) — AK 2 letzter Satz, negativ geprüft |
| 6 | `Up_Aborts_WhenAnotherSessionHoldsUsageStats_AndLeavesNothingBehind` | zweite Verbindung hält `ACCESS EXCLUSIVE` auf `UsageStats` in offener Transaktion (`PostgresLockProbe` prüfen, sonst roher `NpgsqlConnection`); Fehlertext „lock timeout"; Tabelle, Spalten, Historieneintrag fehlen; Dauer ≈ 5 s ist akzeptiert |
| 7 | `Down_RemovesEverything_AndKeepsRowsAndSums` | AK 3 M1-Teil: nach `Up` auf befüllter DB `Down` zum Vorgänger: Tabelle, Spalten, Index weg; `UsageStats`-Zahl und Summen je Paar unverändert; Snapshot- und Ballot-Zeilen intakt |
| 8 | `UpDownUp_SeedsTheSameStructureAgain` | nach dem Rundlauf dieselbe Seed-Zahl und Paar-Zuordnung, Ids neu |
| 9 | `CollapseUsageStatsSql_SumsOntoTheOldestSlot_RestoresUniqueness_AndIsIdempotent` | E14: nach `Up` Unique-Index gelöscht; Zellen: datiert vs. `NULL` (datiert gewinnt), zwei `NULL` (ordinal kleinerer Name gewinnt, `COLLATE "C"`: Großbuchstabe vor Kleinbuchstabe), `Source` 0+1 → 0 und 1+1 → 1, alle drei Zählspalten summiert, Einzelzeilen unberührt; zweiter Lauf 0 Zeilen; `CREATE UNIQUE INDEX` gelingt; danach M1-`Down` grün |

Die Abbruchprüfung aus 2.4 bekommt keinen eigenen Fall: sie ist von außen auf einer konsistenten DB nicht auslösbar (Abschnitt 2.4);
Test 2 deckt ihre positive Seite („keine NULL") ab. Das steht als Kommentar an der Prüfung.

### 6.2 `EmoteSlotCascadeTests` — Abschnitt 5, Fälle (a)–(c).

### 6.3 Nicht nötig

Keine Vitest-/E2E-Änderung (kein Frontend berührt); keine Api.Tests (kein Endpoint, kein Filter); `CoreAssemblyReferenceTests`
läuft ohnehin mit.

## 7. Live-Verifikation (Regel 16) — gegen eine Kopie der lokalen Dev-DB

**Nie gegen die DB `emotepurge` selbst:** sie gehört dem geteilten Dev-Stack, den andere Sitzungen nutzen. **Nie `docker compose`
aus dem Worktree** (die `.env` liegt nur in `/home/dev/projects/EmotePurge`; Compose aus dem Worktree erzeugt die Container mit leerer
Konfiguration neu). `dotnet ef` aus dem Worktree ist davon nicht berührt.

1. **Kopie anlegen** im Container `emotepurge-dev-postgres` (Compose-Projekt `emote-purge-dev`, Host-Port `127.0.0.1:5432`): neue DB
   `emotepurge_slots_m1`, befüllt per `pg_dump | psql` **innerhalb** des Containers (`createdb -T` scheitert, weil Api und Worker
   Verbindungen auf der Vorlage halten). Benutzername aus `printenv POSTGRES_USER` im Container; Socket-Auth im Container braucht kein
   Passwort. Das Passwort braucht nur `dotnet ef --connection` vom Host — aus der `.env` des Haupt-Checkouts lesen, **nicht ausgeben**,
   und auf CRLF achten (Werte können ein `\r` tragen).
2. **Vorher messen** (auf der Kopie, Ergebnisse als CSV ins Scratchpad, nicht ins Repo): `migrations list` (ist die Kopie nicht auf
   `AddSystemLoadSampling`, erst dorthin `update`, dann messen); `count(*)` auf `UsageStats`; je `(EmoteId, EmoteSetId)` Zeilenzahl und
   die drei Summen; Zahl der distinct Paare; Zahl der Emotes mit > 1 Set; Snapshot-Zeilen je Lauf mit dessen `EmoteSetId`.
3. **M1 anwenden:** `dotnet ef database update AddEmoteSlots --project … --startup-project … --connection '…Database=emotepurge_slots_m1…'`
   aus dem Worktree; Laufzeit notieren.
4. **Nachher prüfen** (Absicht; der Umsetzer schreibt das SQL): (1) keine `UsageStats`-Zeile ohne `SlotId`; (2) jede Zeile zeigt auf einen
   Slot ihres `EmoteId`; (3) je Paar genau ein distinct `SlotId`, und jeder `SlotId` kommt in genau einem Paar vor; (4) Seed-Zahl =
   Paar-Zahl; (5) Zeilenzahl und Summen je Paar identisch zur CSV aus Schritt 2 (Diff leer); (6) alle Seeds `AddedAt NULL`,
   `AddedAtFromRest false`, `SeedUnsettled true`, ein gemeinsamer `LastSyncedAt`; (7) Archivzustand je Seed entspricht E8 (Stichprobe:
   ein aktives Emote mit altem Set, ein archiviertes); (8) Snapshot-Zeilen: `SlotId` passend zum Set des Laufs, Zahl der `NULL`-Zeilen
   notieren; (9) `migrations list` ohne Pending; (10) `pg_indexes` zeigt beide Slot-Indizes mit `WHERE` bzw. `INCLUDE`; (11) beide FKs
   mit `confdeltype = 'c'`.
5. **`Down`:** `database update AddSystemLoadSampling` (bzw. der per `list` angezeigte Vorgänger); Tabelle/Spalten weg, Diff gegen die
   CSV leer, `list` zeigt `AddEmoteSlots` als Pending. Danach erneut `Up` (Rundlauf), dann die Kopie **löschen**.
6. **Im PR festhalten:** Zeilenzahl, Paar-Zahl, Seed-Zahl, Mehr-Set-Emotes, Laufzeiten `Up`/`Down`, geprüfter SHA. Kein Hostname, kein
   Passwort, keine Kanalnamen außer dem Testkanal.

Die Spec-Aussage „Kopie der Prod-Daten" (AK 1) ist damit **nicht** erfüllt — siehe R2.

## 8. Gates („fertig" in diesem Repo)

- `dotnet test EmotePurge.slnx` (Docker, Testcontainers) — inkl. der fünf unveränderten Lösch-Testklassen.
- `npm --prefix web test -- --watch=false` — läuft mit, obwohl nichts im Frontend geändert ist (Fertigmeldungs-Definition);
  **kein** E2E nötig, solange `web/` unberührt bleibt.
- `dotnet format EmotePurge.slnx` sauber.
- **Scaffold-Gate** (DECISIONS 2026-10-09): nach Rebase auf `origin/main` zeigt `dotnet ef migrations list` `AddEmoteSlots` zuletzt,
  ein frisches `migrations add` erzeugt **keinen** Snapshot-Diff, der lokale Rundlauf `update <Vorgänger>` → `update` gelingt. Ist
  inzwischen eine fremde Migration gemergt: Re-Scaffold, Hand-Blöcke wörtlich übernehmen.
- **Erst committen, dann** `node scripts/coverage-local.mjs --backend-only` (misst nur Committetes; `Migrations/**` ist bei Sonar
  ausgeschlossen, die Entität ist Auto-Properties, das Mapping deckt jeder DB-Test).
- Live-Verifikation aus Abschnitt 7 **vor** dem Migrations-Commit abgeschlossen (Regel 16).
- Codex Sol vor dem Merge: `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem Worktree, Befund unverändert
  vorlegen; Widerspruch zum Opus-Review → Fable als Schiedsrichter (nur die strittigen Findings).

## 9. Commits und DECISIONS

Conventional Commits, englisch, in dieser Reihenfolge:

1. `docs: add implementation plan for emote slots M1` — diese Datei (committet der Orchestrator nach Freigabe).
2. `feat(core): add EmoteSlot entity and slot columns` — Entitäten, Navigationen, `AppDbContext`.
3. `feat(infrastructure): add AddEmoteSlots migration with per-set seeds` — Migration, Designer, Snapshot, `EmoteSlotRollbackSql`,
   **plus** der DECISIONS-Nachtrag (unten).
4. `test: cover AddEmoteSlots up, down, collapse and cascade paths` — beide Testklassen.
5. Fixes aus Review/Codex als eigene `fix:`-/`test:`-Commits.

**DECISIONS — [E16] Nachtrag statt neuer Eintrag.** Der Eintrag „2026-10-10 — The counting unit is the set entry (slot) … (#365, draft)"
beschreibt Tabelle, Seeds je Paar und das Fenster bereits und nennt `EmoteSlot.cs` und `*_AddEmoteSlots.cs` in seiner
`Betrifft`-Zeile; die Spec legt die finale Fassung in Kind 9. Regel 3 verlangt trotzdem, dass der Commit, der den Vertrag ins Schema
schreibt, seinen Text mitbringt. Lösung: Commit 3 hängt an den Entwurfseintrag einen kurzen, datierten Absatz „Child 1 (M1)" mit den
Punkten, die vom Entwurf abweichen oder ihn konkretisieren — E1 (Rollback-Ziel = Vorgänger laut `migrations list`), E7/E8 (Seed-Stempel
und Archivzustand je Set, falls R1 bestätigt), E9 (Snapshot-Zeilen ohne Seed bleiben `NULL`), E10 (Ort des Kollaps-SQL). Kein zweiter
Eintrag, keine Umformulierung des Bestands; Kind 9 macht den Eintrag final.

## 10. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; Modell explizit)

| # | Aufgabe | Modell | Input | Deliverable | Fertig, wenn | Parallel |
|---|---|---|---|---|---|---|
| T1 | Entitäten + Mapping | sonnet | Abschnitte 2.1–2.3, E2–E4; `Emote.cs`, `UsageStat.cs`, `ChatLogBackfillRunEmote.cs`, `VoteSessionEmote.cs`, `AppDbContext.cs` | Commit 2 (unstaged, Orchestrator committet) | `dotnet build EmotePurge.slnx` grün; `CoreAssemblyReferenceTests` grün; ein Probe-Scaffold (`migrations add`, danach `migrations remove`) zeigt genau die erwarteten Operationen — Tabelle, zwei Slot-Indizes mit den Namen aus 2.3, vier Spalten, zwei FKs mit Cascade — und nichts darüber hinaus | — |
| T2 | Migration `AddEmoteSlots` | **opus** | Abschnitte 1.3, 2.4, 3 (E5–E9, E12), Muster `AddUsageStatEmoteSetId` (Temp-Tabelle, `DO`-Blöcke, Klassendoku) und `AddChatLogBackfill` (Hand-Markierungen, Meldungen) | Migration + Designer + Snapshot | Scaffold, Hand-Blöcke markiert; `PostgresFixture`-Lauf auf leerer DB grün (irgendein Integrationstest); lokaler Rundlauf `update` → `update <Vorgänger>` → `update` auf einer Scratch-DB im Testcontainer oder der Kopie aus T5 | nach T1 |
| T3 | `EmoteSlotRollbackSql` | sonnet | Abschnitt 2.5, E10/E11, `AddEmotePlaceholderMarker.BackfillSql` als Muster | statische Klasse mit zwei Konstanten, Doku-Kommentar nennt Operations.md (Kind 9) und M2/M3-`Down` als Konsumenten | kompiliert; Review-Lesung gegen §12.2 Wort für Wort | parallel zu T2 |
| T4 | `AddEmoteSlotsMigrationTests` | sonnet | Abschnitt 6.1, E1, E14; die beiden Muster-Testklassen; `PostgresLockProbe` prüfen | Testklasse, Fälle 1–9 | alle neun grün, Laufzeit der Klasse notiert | nach T2 + T3; parallel zu T5/T6 |
| T5 | `EmoteSlotCascadeTests` + „0 geändert"-Beleg | sonnet | Abschnitt 5, `ChannelRetentionPurgeTests` als Konstruktionsmuster | Testklasse (a)–(c); `git diff --stat origin/main -- tests/` ohne die fünf Lösch-Testklassen | grün; die fünf Klassen grün und unverändert | nach T2; parallel zu T4/T6 |
| T6 | Live-Verifikation | sonnet | Abschnitt 7 vollständig; Memory-Fallen (Worktree/Compose, `.env`, CRLF) | Messprotokoll im Scratchpad + Kurzfassung für den PR-Text | alle elf Nachher-Checks grün, `Down`-Diff leer, Kopie gelöscht | nach T2; parallel zu T4/T5 |
| T7 | Review + Gates + PR | **opus** (Review); Codex Sol | gesamter Branch, Abschnitte 8–9, AK-Tabelle (E13) | Review-Befund; DECISIONS-Nachtrag (E16) im Migrations-Commit; PR mit AK-Tabelle, Messwerten, Abweichung 1.3; Kommentare auf #373/#375 | drei Suiten grün, Scaffold-Gate, Coverage ≥ 80 %, Codex-Befund vorgelegt; **Merge durch den Nutzer** | nach T4–T6 |

Reihenfolge: T1 → (T2 ∥ T3) → (T4 ∥ T5 ∥ T6) → T7. T6 darf nicht parallel zu einem Testcontainers-Lauf auf derselben Box laufen,
wenn Speicherdruck sichtbar wird (`/proc/pressure/memory`), sonst ist die Reihenfolge frei.

## 11. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **Kind 2:** Seeds haben `LastSyncedAt` = Migrationsstempel und (bei R1 = ja) sind Seeds nicht aktiver Sets archiviert mit
  `ArchivedAt NULL`; 6.2 Schritt 7 darf sie nicht als „verbleibende aktive Slots" sehen. Das Settle (6.3) findet den Seed über die
  angehängten Zeilen des aktiven Sets, nicht über `IsArchived`.
- **Kind 3 (M2):** Zwischen M1 und M2 schreiben die unveränderten Writer `UsageStats`-Zeilen mit `SlotId NULL` (lokal real, auf Prod
  durch das Fenster ausgeschlossen). M2-`Up` muss vor `SET NOT NULL` entweder diese Zeilen wie M1 an den Seed ihres Paars hängen
  (gleiche Hilfstabellen-Logik, neue Seeds für neue Paare) oder mit Zählwert abbrechen. Test 9 gegen das echte M2-`Down` wiederholen.
- **Kind 5 (M3):** Snapshot-Zeilen mit `SlotId NULL` aus M1 (E9) existieren lokal; M3 `SET NOT NULL` braucht dafür eine Regel. Der
  Snapshot-Kollaps (`CollapseBackfillSnapshotSql`) ist dort erstmals fixturbar.
- **Kind 9:** Rollback-Ziel = Vorgänger laut `migrations list` (E1); Operations-Block = Konstante aus `EmoteSlotRollbackSql` (E10);
  Fußnote in §12.2 der Spec; DECISIONS final.

## 12. Rückfragen an den Betreiber

- **R1 — Archivzustand der Seeds (E8).** Die Spec sagt „Name/Archivzustand aus `Emotes`". Wörtlich hieße das: ein Seed für ein nicht
  aktives Set B eines aktiven Emotes ist aktiv. Der Plan setzt stattdessen `IsArchived = true` für jeden Seed, dessen Set nicht das
  `ActiveEmoteSetId` des Kanals ist (Begründung in E8). **Bitte bestätigen oder auf den Wortlaut zurücksetzen.** Beides ist in M1 eine
  Zeile; der Unterschied zeigt sich erst in Kind 2 (Schritt 7) und Kind 6 (aktive Ansicht).
- **R2 — AK 1 „Kopie der Prod-Daten".** Kind 1 kann nur eine Kopie der lokalen Dev-DB fahren (Abschnitt 7); eine Prod-Kopie setzt
  einen Backup-Transfer voraus, den die Session nicht selbst anstößt (kein eigener Zugriff auf `vps`/`nas`). Vorschlag: der Lauf auf der
  Prod-Kopie gehört zu Kind 9 vor dem Fenster, dann gleich für M1–M3 zusammen. Soll er stattdessen schon jetzt stattfinden, bitte das
  Backup lokal bereitstellen; die Befehle aus Abschnitt 7 gelten dann unverändert.
