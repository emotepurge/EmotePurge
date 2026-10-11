# Plan #371 — Emote-Slots, Kind 1: M1 `AddEmoteSlots` (additives Schema + Seeds)

Erstellt am 2026-10-11 im Worktree `EmotePurge-slots` (Branch `feat/371-emote-slot-schema`, Basis `origin/main` @ `32a46f73`).
**Status: vom Betreiber freigegeben (2026-10-11, R1/R2 entschieden) und umgesetzt.** Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md)
(§4 Datenmodell, §11.1 M1, §11.2 Fenster, §12.2 Rollback, §13 AK 1–4 und AK 44, §15 Tests, §16 Kind 1), Issue #371, Epic #365.
Formatvorlage: [Plan-346](Plan-346-Chat-Log-Backfill.md) (Aufbau, nicht Umfang).

**Was dieser Plan trägt:** nur, was die Spec offen lässt — Seed-Werte, Schrittfolge, Grenze der AK im Kind 1, Ort des Kollaps-SQL,
Tests, Live-Prüfung, Gates, Commits, Aufgabenfolge. Kein Code. Bei Abweichung gilt die Spec; der Fund wird gemeldet. Die
Betreiber-Entscheidungen D0–D33 werden nicht wieder aufgemacht.

**Lesehinweis:** Zeilen mit **[E]** sind Planer-Entscheidungen, kein Spec-Text — der Betreiber kann jede einzeln kippen. Was nur er
entscheiden kann, steht in Abschnitt 12.

**Revision 2026-10-11** (nach Codex-Sol-Adversarial-Review und Betreiber-Entscheidungen; Erstfassung `0d31ce0c`): (1) E8 gestrichen —
Seeds kopieren `IsArchived`/`ArchivedAt` 1:1 aus `Emotes` (Spec-Wortlaut; Codex-Befund 1, Betreiber); Test 3, Live-Check 7, Randfälle,
DECISIONS-Nachtrag und Kind-2-Hinweis angepasst. (2) R2 entschieden: Prod-Kopie (AK 1) in Kind 9 für M1–M3 gemeinsam. (3) Codex-
Befund 2: die Seed-Logik wird zur wiederverwendbaren, idempotenten Konstante `EmoteSlotSeedSql` (neu E17), die M1 **und** M2-`Up`
fahren; die Behauptung „Prod ist von NULL-Zeilen ausgeschlossen" war falsch (§11.2 Schritt 4 erlaubt alte Images auf M1) und ist
korrigiert; Tests 10–11 neu. (4) Codex-Befund 3: der Snapshot-Kollaps wird in Kind 1 auf simuliertem M3-Stand fixturiert (E14
geändert, Test 12 neu). Aufgabenliste T2–T4 und T6 entsprechend.

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
   Ballot-Erzeugung bleiben byteidentisch. Sie schreiben nach M1 Zeilen mit `SlotId = NULL`; das ist im additiven Stand gewollt —
   lokal immer, auf Prod nach einem abgebrochenen Fenster (§11.2 Schritt 4: alte Images laufen auf M1 weiter). Deshalb ist die
   Seed-Logik eine wiederverwendbare Konstante (E17), die solche Zeilen später nachträglich anhängt.

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
| 1b | **[E5]** `LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE` — das `ALTER TABLE` in Schritt 3 nimmt diese Sperre ohnehin; sie schon hier zu nehmen heißt, dass zwischen `CREATE TABLE`, `ALTER` und dem Seed (Schritt 4) kein Flush mehr schreibt und die Konstante die endgültige Zeilenmenge sieht. Mit `lock_timeout` 5 s scheitert eine vergessene psql-Sitzung schnell statt stumm zu warten. | Hand |
| 2 | `CREATE TABLE "EmoteSlots"` samt FK und beiden Indizes | EF |
| 3 | `ALTER TABLE "UsageStats" ADD COLUMN "SlotId" text NULL` | EF (umsortiert) |
| 4 | **Seed + Anhängen = `EmoteSlotSeedSql.AttachUnslottedUsageRowsSql`** (E17, Abschnitt 2.6): auf der frischen Spalte ist jede Zeile `NULL` und kein Slot existiert, der Lauf ist also der vollständige Seed je distinct Paar mit den Werten aus Abschnitt 3 (E6/E7, Archivzustand 1:1) | Hand (Konstante) |
| 5 | **Abbruchprüfung** (unten) → FK anlegen (nach dem Anhängen, damit die FK-Validierung ein Durchlauf ist) | Hand + EF |
| 6 | `CREATE INDEX "IX_UsageStats_SlotId_EmoteSetId_Date" … INCLUDE ("UseCount")` — nach der Befüllung, nicht davor | EF (umsortiert) |
| 7 | `ChatLogBackfillRunEmotes`: `SlotId text NULL`, `CreatedSlot boolean NOT NULL DEFAULT false`; `UPDATE` über `ChatLogBackfillRuns.EmoteSetId` des Laufs und den Slot, der die Zeilen desselben `(EmoteId, EmoteSetId)` trägt (nach Schritt 4 genau der Seed); Zeilen ohne Seed bleiben `NULL` (E9) | EF + Hand |
| 8 | `VoteSessionEmotes`: `NamesAtCreation text[] NULL` | EF |

**Abbruchprüfung (Schritt 5, `DO $$ … RAISE EXCEPTION`, englische Meldung mit Zählwerten wie in `AddChatLogBackfill`):**
(a) keine `UsageStats`-Zeile mit `SlotId IS NULL`; (b) keine Zeile, deren Slot ein anderes `EmoteId` trägt; (c) kein Slot, an dem
Zeilen aus mehr als einem `EmoteSetId` hängen; (d) Zahl der Seeds = Zahl der distinct Paare. Auf einer konsistenten DB ist die Prüfung
nicht auslösbar (FK `UsageStats → Emotes`, Sperre aus 1b) — sie ist der Gurt, kein Pfad. Scheitert sie, rollt die Transaktion
alles zurück, die Migrationshistorie bleibt ohne Eintrag. Die Prüfung gehört zur **Migration**, nicht zur Konstante: (c) und (d)
gelten nur auf der frischen Spalte; M2-`Up` (Kind 3) formuliert nach seinem Lauf der Konstante eine eigene Prüfung (mindestens (a)
und (b)).

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

### 2.6 Seed-SQL, wiederverwendbar — `EmoteSlotSeedSql.AttachUnslottedUsageRowsSql`

**[E17]** Eigene statische Klasse `EmoteSlotSeedSql` in `src/EmotePurge.Infrastructure/Migrations/`, Schwester von
`EmoteSlotRollbackSql`, bewusst nicht dieselbe Klasse: Vorwärtspfad (M1-`Up`, M2-`Up`, Reparatur nach abgebrochenem Fenster) und
Rückwärtspfad (Kollaps vor `Down`) dürfen in Operations.md und im Code nicht verwechselbar sein. Eine `public const string`, als
**eine** Einheit ausführbar (ein `DO`-Block, oder Statements, die der Aufrufer in einer Transaktion fährt — der Test ruft sie über ein
einzelnes `ExecuteSqlRawAsync` in einer Transaktion auf). Sonar schließt den Ordner aus; die Konstante ist über die Migrationstests
gedeckt. Die Migration ruft die Konstante und enthält **keine** zweite Kopie der Seed-Logik.

Vertrag (Absicht):

1. **Eingabe** sind alle `UsageStats`-Zeilen mit `SlotId IS NULL`. Zeilen mit `SlotId` werden nie angefasst.
2. **Ziel-Slot je Paar `(EmoteId, EmoteSetId)`:** trägt bereits mindestens ein Slot Zeilen dieses Paars (gleiches `EmoteId`,
   gleiches `EmoteSetId`, `SlotId IS NOT NULL`), hängen die `NULL`-Zeilen an **diesen** Slot — nach Kind 2 ist das der gesettelte Seed
   bzw. der autoritative Slot, an den das Settle die Set-Zeilen gebunden hat. Tragen mehrere Slots Zeilen des Paars, gewinnt der
   **älteste nach D2-Ordnung**: kleinstes `AddedAt` (`NULLS LAST`), dann ordinal `Name` (`COLLATE "C"`), dann `Id`. Begründung: eine
   `NULL`-Zeile stammt von einem Schreiber, der je Emote zählt — genau der Bestand, den D2 dem ältesten Slot des Sets zuweist; die Summe
   je Paar bleibt erhalten, nur die Zuschreibung folgt D2. Abbruch verworfen: er bräuchte eine weitere Reparaturprozedur (Codex-Befund
   2), während die Ordnung dieselbe wie im Kollaps-SQL ist, also keine neue Regel. Vor M2 ist der Fall ohnehin beweisbar
   einelementig: Kind 2 schreibt keine Nutzungszeilen, das Settle bindet alle Zeilen eines Sets an genau einen Slot, neue Slots
   beginnen bei 0.
3. **Kein Slot mit Zeilen des Paars** → neuer unsettled Seed (`AddedAt NULL`, `AddedAtFromRest false`, `SeedUnsettled true`,
   `Name`/`IsArchived`/`ArchivedAt` 1:1 aus `Emotes`, `LastSyncedAt = now()`), daran alle `NULL`-Zeilen des Paars (D12). Das gilt
   auch für ein neues Set eines Emotes, dessen andere Sets längst gesettelt sind; der nächste Abgleich settelt den neuen Seed.
4. **Idempotent:** ein zweiter Lauf ändert 0 Zeilen und legt 0 Seeds an.
5. **Keine Indexverletzung:** vor M2 ist `(EmoteId, EmoteSetId, Date)` unique, eine `NULL`-Zeile an `(E, S, D)` existiert also nur,
   wenn dort sonst keine Zeile liegt — das Anhängen kann den Slot-Index nicht doppelt belegen; nach M2 gibt es keine `NULL`-Zeilen
   mehr (`NOT NULL`).
6. Auf einer frisch angelegten Spalte (M1 Schritt 4) greift für jedes Paar Regel 3 — das ist der vollständige Seed aus Spec §11.1
   Schritt 3/4.

## 3. Planer-Entscheidungen zu den offenen Fragen

| # | Frage | Entscheidung | Begründung / verworfene Alternative |
|---|---|---|---|
| **[E6]** | `Name` des Seeds | `Emotes.Name` zum Migrationszeitpunkt | Der einzige Name, den wir je Emote haben; D11(a) macht ihn bis zum Settle zur Identität |
| **[E7]** | `LastSyncedAt` | `now()` der Migrationstransaktion — alle Seeds tragen denselben Stempel | Spec: „gestempelt bei Anlage". `Emote.LastSyncedAt` verworfen: es würde einen Abgleich behaupten, der den Slot nie gesehen hat. Der gemeinsame Stempel ist zugleich ein brauchbares Erkennungsmerkmal der Seeds im Betrieb |
| ~~E8~~ → **Betreiber 2026-10-11** | `IsArchived` / `ArchivedAt` je Seed | **Spec-Wortlaut:** beide Werte **1:1 aus `Emotes`**, für jeden Seed des Emotes, unabhängig davon, ob sein Set das aktive ist. Keine Ableitung aus `ActiveEmoteSetId`, keine Sonderregel für Kanäle mit leerem Set | Ersetzt die frühere Planer-Entscheidung E8 (abgeleiteter Archivzustand je Set) nach Codex-Befund 1: die Spec ist nach R5 fertig, ein Kind darf einen Eingabewert des Abgleichs nicht umdeuten. Die Folge — ein aktiver Seed für ein nicht aktives Set eines aktiven Emotes — ist ein Risiko, das Kind 2 innerhalb der Spec auflöst (Abschnitt 11) |
| **[E9]** | Snapshot-Zeilen ohne Seed ihres Sets | `SlotId` bleibt `NULL`, `CreatedSlot = false`, kein Abbruch | D12 erlaubt Seeds nur für Paare mit Nutzung; einen Seed für ein Paar ohne Zeilen zu erfinden, änderte D12. Die Spec nennt die Abbruchprüfung nur für `UsageStats`. Auf Prod ist die Menge zum Fensterbeginn leer (Vorbedingung 3a: 0 Läufe), und das Backfill-Flag bleibt bis nach dem Fenster aus — auch nach einem Abbruch mit alten Images entstehen dort keine Läufe. Folge für M3 steht in Abschnitt 11 |
| **[E12]** | Id-Erzeugung im SQL | `gen_random_uuid()::text` | Postgres ≥ 13 (Dev und Prod: 16); liefert dieselbe 36-stellige, kleingeschriebene Bindestrich-Form wie `Guid.ToString()` — ein Leser kann Seed-Ids und Code-Ids nicht unterscheiden, und das soll so sein |
| **[E13]** | AK 1/3/4 an der Kind-1-Grenze | **AK 2 vollständig** (Test + Live). **AK 1:** M1-Teil auf einer Kopie der lokalen Dev-DB (Abschnitt 7), `migrations list` ohne Pending auf der Kopie; M2/M3 und der Lauf auf der **Prod-Kopie → „Rest in #379"** (Betreiber 2026-10-11: Kind 9 fährt ihn für M1–M3 gemeinsam vor dem Fenster). **AK 3:** M1-`Down` entfernt alles (Test 7), das Kollaps-SQL hält die Summen (Test 9 auf simuliertem M2-Stand) und behält je `(RunId, EmoteId)` den ältesten Slot (Test 12 auf simuliertem M3-Stand); M2-/M3-`Down`-Guards → Kinder 3/5. **AK 4:** getrennte Seeds A/B, Zeilen je Set, Summen je Set, Snapshot-Zeilen mit Seed, `CreatedSlot = false`, `NamesAtCreation NULL` (Tests 2/4); Settle → Kind 2; „M2/M3 darauf grün" → Kinder 3/5 | Aufzeichnung: der PR-Text trägt eine AK-Tabelle mit Stand je Teilaussage („erfüllt" / „Rest in #37x"); je ein Kommentar auf #373 und #375 nennt die Konstanten und die Fixtures, die dort wiederzuverwenden sind |
| **[E14]** | Kollaps-Tests ohne M2/M3 | Beide Konstanten werden in Kind 1 auf **simuliertem** Zielstand gefahren, hergestellt per Raw-SQL im Scratch-Schema nach `Up`. **UsageStats (Test 9):** unique `(EmoteId, EmoteSetId, Date)`-Index löschen → zwei Slot-Zeilen je Zelle → Konstante → Unique-Index wieder anlegen (Form des M2-`Down`-Guards) → M1-`Down`. **Snapshot (Test 12):** PK `(RunId, EmoteId)` durch `(RunId, SlotId)` ersetzen → mehrere Slot-Zeilen je `(RunId, EmoteId)` → Konstante → PK `(RunId, EmoteId)` wieder anlegen (Form des M3-`Down`-Guards) → M1-`Down` | Ohne die Simulation hat keines der beiden SQL Input. Die Simulation deckt genau die Schemaeigenschaft ab, die das SQL berührt (Eindeutigkeit bzw. PK), nicht `NOT NULL`. Kind 3 wiederholt Test 9 gegen das echte M2-`Down`, Kind 5 Test 12 gegen das echte M3-`Down` (Codex-Befund 3) |
| **[E15]** | Slot-Zeilen in Purge-/Retention-Zählwerten | Nein | Spec 4.6: Purge, Retention, Account-Löschung „unverändert"; `CountChannelCascadeAsync` zählt `UsageRows` über `EmoteId`, das bleibt vollständig. Ein neuer Zählwert wäre ein neuer Vertrag im Trockenlauf-Log ohne Auftrag |

## 4. Randfälle

| Fall | Verhalten in M1 | Nachweis |
|---|---|---|
| Emote ohne `UsageStats` | kein Seed (D12); Slots entstehen erst durch den REST-Abgleich (Kind 2) | Test 2 (Emote Z) |
| Emote mit Nutzung in Sets A und B | zwei Seeds, Zeilen je Set getrennt, Summen je Set unverändert | Test 2, Live-Check 3–5 |
| Archiviertes Emote mit `ArchivedAt` | Seed archiviert mit demselben `ArchivedAt` | Test 3 |
| Platzhalter (`IsPlaceholder`, archiviert, `ArchivedAt NULL`) | Seed archiviert, `ArchivedAt NULL` („war nie drin"); nur möglich mit `Source = 1`-Zeilen | Test 3 |
| Aktives Emote, Zeilen nur in nicht aktivem Set | Seed **aktiv** (`IsArchived`/`ArchivedAt` wie das Emote, Spec-Wortlaut); Umgang damit ist Risiko von Kind 2 (Abschnitt 11) | Test 3 |
| Paar nur mit `Source = 1`-Zeilen / gemischt | Seed wie jedes andere Paar; `Source` bleibt je Zeile | Test 2 |
| `Emote.FirstSeenAt` gesetzt | wird **nicht** übernommen; `AddedAt NULL` auf allen Seeds | Test 2, Live-Check 6 |
| Snapshot-Zeile, deren Emote im Set des Laufs keine Nutzung hat | `SlotId NULL`, `CreatedSlot false` (E9) | Test 4 |
| Snapshot-Zeile mit Nutzung im Set des Laufs | `SlotId` = Seed dieses Paars, auch wenn der Kanal inzwischen auf einem anderen Set steht | Test 4 |
| `VoteSessionEmotes` | nur die Spalte; Bestandszeilen `NamesAtCreation NULL` | Test 4 |
| Leere DB | alle Hand-Schritte sind No-ops; genau das tut `PostgresFixture` bei jedem Suite-Start | Test 1 |
| Wiederholung | `Up` läuft durch den EF-Migrator genau einmal; `Up → Down → Up` liefert dieselbe Seed- und Zuordnungsstruktur mit neuen Ids; die Seed-Konstante selbst ist idempotent (E17) | Tests 8, 10 |
| `NULL`-Zeilen alter Schreiber nach M1 (lokal immer; Prod nach abgebrochenem Fenster mit alten Images auf M1) | bleiben `NULL`, bis die Seed-Konstante läuft — in M2-`Up` (Kind 3, bindend) oder von Hand als Reparaturschritt; bestehendes Paar → der Slot, der dessen Zeilen trägt, neues Paar → neuer Seed | Tests 10, 11; Live-Check 12 |
| `lock_timeout` reißt (fremde Sitzung hält `UsageStats`) | Fehler 55P03 nach ≤ 5 s, Transaktion zurückgerollt, keine Tabelle, keine Spalte, kein Historieneintrag; Betreiber sucht den Blocker in `pg_stat_activity` und wiederholt | Test 6 |
| Zwei Kaskadenpfade in `UsageStats` | Löschen eines Emotes trifft eine Zeile über beide Pfade — Postgres löscht sie einmal, kein Fehler | Cascade-Test b |
| Kanal ohne `ActiveEmoteSetId` | keine Sonderregel; Seeds folgen dem Archivzustand des Emotes; Settle beim Rejoin ist Kind-2-Sache | Test 3 (ein Kanal mit leerem Set) |

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
| 3 | `Up_CopiesNameAndArchiveStateFromTheEmote_ForEverySeed` | E6/E7 + Spec-Wortlaut: aktives Emote in A (Seed aktiv), archiviertes Emote mit `ArchivedAt` (beides übernommen), Platzhalter (archiviert, `ArchivedAt NULL`), aktives Emote mit Zeilen nur in B (Seed **aktiv**, nicht archiviert), Kanal mit leerem `ActiveEmoteSetId` (Seeds folgen dem Emote); `LastSyncedAt` auf allen Seeds identisch und ≥ Teststart |
| 4 | `Up_FillsSnapshotSlotIdFromTheRunsSet_LeavesRowsWithoutSeedNull_AndAddsNamesAtCreation` | AK 4 zweite Hälfte + E9: Lauf auf B mit Snapshot-Zeilen für X (Seed B) und W (ohne Nutzung in B → `NULL`); `CreatedSlot false`; `NamesAtCreation NULL` auf einer Bestandszeile |
| 5 | `Up_DoesNotChangeAnyEmoteRow` | `Emotes` vor/nach byteidentisch (insbesondere `FirstSeenAt`, `Name`, `IsArchived`) — AK 2 letzter Satz, negativ geprüft |
| 6 | `Up_Aborts_WhenAnotherSessionHoldsUsageStats_AndLeavesNothingBehind` | zweite Verbindung hält `ACCESS EXCLUSIVE` auf `UsageStats` in offener Transaktion (`PostgresLockProbe` prüfen, sonst roher `NpgsqlConnection`); Fehlertext „lock timeout"; Tabelle, Spalten, Historieneintrag fehlen; Dauer ≈ 5 s ist akzeptiert |
| 7 | `Down_RemovesEverything_AndKeepsRowsAndSums` | AK 3 M1-Teil: nach `Up` auf befüllter DB `Down` zum Vorgänger: Tabelle, Spalten, Index weg; `UsageStats`-Zahl und Summen je Paar unverändert; Snapshot- und Ballot-Zeilen intakt |
| 8 | `UpDownUp_SeedsTheSameStructureAgain` | nach dem Rundlauf dieselbe Seed-Zahl und Paar-Zuordnung, Ids neu |
| 9 | `CollapseUsageStatsSql_SumsOntoTheOldestSlot_RestoresUniqueness_AndIsIdempotent` | E14: nach `Up` Unique-Index gelöscht; Zellen: datiert vs. `NULL` (datiert gewinnt), zwei `NULL` (ordinal kleinerer Name gewinnt, `COLLATE "C"`: Großbuchstabe vor Kleinbuchstabe), `Source` 0+1 → 0 und 1+1 → 1, alle drei Zählspalten summiert, Einzelzeilen unberührt; zweiter Lauf 0 Zeilen; `CREATE UNIQUE INDEX` gelingt; danach M1-`Down` grün |
| 10 | `SeedSql_AttachesLateRowsOfExistingAndNewPairs_AndIsIdempotent` | E17 Regeln 1, 3, 4: nach `Up` per Raw-SQL (so, wie der alte Flush schreibt: ohne `SlotId`) Zeilen für (i) ein bestehendes Paar an neuem Datum, (ii) ein neues Set eines bestehenden Emotes, (iii) ein neues Emote ohne jeden Slot; Konstante einmal: (i) hängt am vorhandenen Seed, (ii)/(iii) bekommen je einen neuen unsettled Seed mit Werten aus `Emotes`; keine Zeile mit `SlotId` wurde verändert; zweiter Lauf: 0 Zeilen, 0 Seeds |
| 11 | `SeedSql_AttachesToTheSlotCarryingThePairsRows_AfterASimulatedSettle_AndPicksTheOldestWhenSeveralDo` | E17 Regel 2: (a) den Seed eines Paars per Raw-SQL so umschreiben, wie Kind 2 ihn settelt (`AddedAt` gesetzt, `AddedAtFromRest true`, `SeedUnsettled false`, neuer Name) → späte `NULL`-Zeile hängt an genau diesem Slot, kein neuer Seed; (b) ein zweiter Slot desselben Emotes trägt Zeilen desselben Sets an **anderen** Daten (kein Index-Drop nötig) → die `NULL`-Zeile geht an den D2-ältesten (`AddedAt` asc, `NULLS LAST`, dann `Name COLLATE "C"`), nicht an den jüngeren; Summe je Paar unverändert |
| 12 | `CollapseBackfillSnapshotSql_KeepsTheOldestSlotsRow_PerRunAndEmote_AndRestoresThePrimaryKey` | E14 Snapshot-Teil: nach `Up` PK per Raw-SQL auf `(RunId, SlotId)` gesetzt; je `(RunId, EmoteId)` mehrere Zeilen mit verschiedenen Slots: datiert vs. `NULL` (datiert bleibt), zwei `NULL` (ordinal kleinerer Name bleibt), derselbe Emote in zwei Läufen (je Lauf eigene Entscheidung), Einzelzeilen unberührt; zweiter Lauf 0 Zeilen; PK `(RunId, EmoteId)` wieder anlegbar; danach M1-`Down` grün |

Die Abbruchprüfung aus 2.4 bekommt keinen eigenen Fall: sie ist von außen auf einer konsistenten DB nicht auslösbar (Abschnitt 2.4);
Test 2 deckt ihre positive Seite („keine NULL") ab. Das steht als Kommentar an der Prüfung. Zwölf Fälle in dieser Klasse plus die
drei aus 6.2 — etwas über den „~10" der Spec, weil die Konstanten aus E10/E17 ihre eigenen Fixtures tragen (Codex-Befunde 2 und 3).

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
   `AddedAtFromRest false`, `SeedUnsettled true`, ein gemeinsamer `LastSyncedAt`; (7) `Name`, `IsArchived`, `ArchivedAt` jedes Seeds
   sind **gleich** den Werten seines Emotes (Join-Vergleich über alle Seeds, 0 Abweichungen — kein Stichprobenurteil); (8)
   Snapshot-Zeilen: `SlotId` passend zum Set des Laufs, Zahl der `NULL`-Zeilen notieren; (9) `migrations list` ohne Pending; (10)
   `pg_indexes` zeigt beide Slot-Indizes mit `WHERE` bzw. `INCLUDE`; (11) beide FKs mit `confdeltype = 'c'`; (12) die Seed-Konstante
   (E17) noch einmal auf der migrierten Kopie gefahren: 0 Zeilen, 0 Seeds (Idempotenz auf echten Daten).
5. **`Down`:** `database update AddSystemLoadSampling` (bzw. der per `list` angezeigte Vorgänger); Tabelle/Spalten weg, Diff gegen die
   CSV leer, `list` zeigt `AddEmoteSlots` als Pending. Danach erneut `Up` (Rundlauf), dann die Kopie **löschen**.
6. **Im PR festhalten:** Zeilenzahl, Paar-Zahl, Seed-Zahl, Mehr-Set-Emotes, Laufzeiten `Up`/`Down`, geprüfter SHA. Kein Hostname, kein
   Passwort, keine Kanalnamen außer dem Testkanal.

Die Spec-Aussage „Kopie der Prod-Daten" (AK 1) ist damit noch **nicht** erfüllt — Betreiber-Entscheidung 2026-10-11: dieser Lauf
gehört zu Kind 9 (#379), für M1–M3 gemeinsam, vor dem Fenster. Die Schritte 2–5 oben gelten dort unverändert.

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
3. `feat(infrastructure): add AddEmoteSlots migration with per-set seeds` — Migration, Designer, Snapshot, `EmoteSlotSeedSql`,
   `EmoteSlotRollbackSql`, **plus** der DECISIONS-Nachtrag (unten).
4. `test: cover AddEmoteSlots up, down, collapse and cascade paths` — beide Testklassen.
5. Fixes aus Review/Codex als eigene `fix:`-/`test:`-Commits.

**DECISIONS — [E16] Nachtrag statt neuer Eintrag.** Der Eintrag „2026-10-10 — The counting unit is the set entry (slot) … (#365, draft)"
beschreibt Tabelle, Seeds je Paar und das Fenster bereits und nennt `EmoteSlot.cs` und `*_AddEmoteSlots.cs` in seiner
`Betrifft`-Zeile; die Spec legt die finale Fassung in Kind 9. Regel 3 verlangt trotzdem, dass der Commit, der den Vertrag ins Schema
schreibt, seinen Text mitbringt. Lösung: Commit 3 hängt an den Entwurfseintrag einen kurzen, datierten Absatz „Child 1 (M1)" mit den
Punkten, die vom Entwurf abweichen oder ihn konkretisieren — E1 (Rollback-Ziel = Vorgänger laut `migrations list`), E7 (Seed-Stempel
`LastSyncedAt = now()`; Archivzustand 1:1 aus `Emotes` nach Spec), E9 (Snapshot-Zeilen ohne Seed bleiben `NULL`), E10 (Ort des
Kollaps-SQL), E17 (Seed-Logik als idempotente Konstante, die M2-`Up` vor `SET NOT NULL` wiederholt; Regel für Paare mit mehreren
Slots = D2-Ordnung). Kein zweiter Eintrag, keine Umformulierung des Bestands; Kind 9 macht den Eintrag final.

## 10. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; Modell explizit)

| # | Aufgabe | Modell | Input | Deliverable | Fertig, wenn | Parallel |
|---|---|---|---|---|---|---|
| T1 | Entitäten + Mapping | sonnet | Abschnitte 2.1–2.3, E2–E4; `Emote.cs`, `UsageStat.cs`, `ChatLogBackfillRunEmote.cs`, `VoteSessionEmote.cs`, `AppDbContext.cs` | Commit 2 (unstaged, Orchestrator committet) | `dotnet build EmotePurge.slnx` grün; `CoreAssemblyReferenceTests` grün; ein Probe-Scaffold (`migrations add`, danach `migrations remove`) zeigt genau die erwarteten Operationen — Tabelle, zwei Slot-Indizes mit den Namen aus 2.3, vier Spalten, zwei FKs mit Cascade — und nichts darüber hinaus | — |
| T2 | Migration `AddEmoteSlots` **und** `EmoteSlotSeedSql` | **opus** | Abschnitte 1.3, 2.4, 2.6, 3 (E5–E7, E9, E12, E17), Muster `AddUsageStatEmoteSetId` (`DO`-Blöcke, Klassendoku) und `AddChatLogBackfill` (Hand-Markierungen, Meldungen), `AddEmotePlaceholderMarker.BackfillSql` (Konstante) | Konstante + Migration + Designer + Snapshot; die Migration ruft die Konstante, keine zweite Kopie der Seed-Logik | Scaffold, Hand-Blöcke markiert; `PostgresFixture`-Lauf auf leerer DB grün (irgendein Integrationstest); lokaler Rundlauf `update` → `update <Vorgänger>` → `update` auf einer Scratch-DB im Testcontainer oder der Kopie aus T6; Konstante zweimal hintereinander auf derselben DB → 0 Zeilen beim zweiten Lauf | nach T1 |
| T3 | `EmoteSlotRollbackSql` | sonnet | Abschnitt 2.5, E10/E11, `AddEmotePlaceholderMarker.BackfillSql` als Muster | statische Klasse mit zwei Konstanten, Doku-Kommentar nennt Operations.md (Kind 9) und M2/M3-`Down` als Konsumenten | kompiliert; Review-Lesung gegen §12.2 Wort für Wort; die Ordnung (`AddedAt NULLS LAST`, `Name COLLATE "C"`, Id) ist dieselbe wie in E17 Regel 2 | parallel zu T2 |
| T4 | `AddEmoteSlotsMigrationTests` | sonnet | Abschnitt 6.1, E1, E14, E17; die beiden Muster-Testklassen; `PostgresLockProbe` prüfen | Testklasse, Fälle 1–12 | alle zwölf grün, Laufzeit der Klasse notiert | nach T2 + T3; parallel zu T5/T6 |
| T5 | `EmoteSlotCascadeTests` + „0 geändert"-Beleg | sonnet | Abschnitt 5, `ChannelRetentionPurgeTests` als Konstruktionsmuster | Testklasse (a)–(c); `git diff --stat origin/main -- tests/` ohne die fünf Lösch-Testklassen | grün; die fünf Klassen grün und unverändert | nach T2; parallel zu T4/T6 |
| T6 | Live-Verifikation | sonnet | Abschnitt 7 vollständig; Memory-Fallen (Worktree/Compose, `.env`, CRLF) | Messprotokoll im Scratchpad + Kurzfassung für den PR-Text | alle zwölf Nachher-Checks grün, `Down`-Diff leer, Kopie gelöscht | nach T2; parallel zu T4/T5 |
| T7 | Review + Gates + PR | **opus** (Review); Codex Sol | gesamter Branch, Abschnitte 8–9, AK-Tabelle (E13) | Review-Befund; DECISIONS-Nachtrag (E16) im Migrations-Commit; PR mit AK-Tabelle, Messwerten, Abweichung 1.3; Kommentare auf #373/#375 | drei Suiten grün, Scaffold-Gate, Coverage ≥ 80 %, Codex-Befund vorgelegt; **Merge durch den Nutzer** | nach T4–T6 |

Reihenfolge: T1 → (T2 ∥ T3) → (T4 ∥ T5 ∥ T6) → T7. T6 darf nicht parallel zu einem Testcontainers-Lauf auf derselben Box laufen,
wenn Speicherdruck sichtbar wird (`/proc/pressure/memory`), sonst ist die Reihenfolge frei.

## 11. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **Kind 2 (Risiko, innerhalb der Spec aufzulösen — keine Entscheidung dieses Plans):** Seeds kopieren den Archivzustand 1:1 vom
  Emote. Ein aktives Emote mit Nutzung in einem **nicht** aktiven Set B hat deshalb nach M1 einen **aktiven** Seed für B, bis Kind 2 ihn
  behandelt. Zwei Fallen: 6.2 Schritt 7 („verbleibende aktive Slots dieses Abgleichs archivieren") darf diesem Seed kein irreführendes
  `ArchivedAt = now` stempeln, und er darf nicht als aktiver Slot des aktiven Sets erscheinen (aktive Ansicht Kind 6, `OccupiedSlots`
  D27; die Match-Map D15 lässt unsettled Seeds ohnehin aus). Das Settle (6.3) findet den Seed über die angehängten Zeilen des
  aktiven Sets, nicht über `IsArchived`. Seeds tragen `LastSyncedAt` = Migrationsstempel.
- **Kind 3 (M2), bindend:** Zwischen M1 und M2 schreiben die unveränderten Writer `UsageStats`-Zeilen mit `SlotId NULL` — lokal immer,
  auf Prod nach einem abgebrochenen Fenster, in dem die alten Images auf M1 weiterlaufen (§11.2 Schritt 4). **M2-`Up` fährt vor
  `SET NOT NULL` die Konstante `EmoteSlotSeedSql.AttachUnslottedUsageRowsSql`** (E17) statt abzubrechen — **Reihenfolge: `lock_timeout` + `LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE` zuerst, dann Konstante, dann Prüfung, dann `ALTER`** (Schiedsspruch Fable 2026-10-11: die Statements laufen unter READ COMMITTED, ein Flush zwischen ihnen erzeugte sonst einen zweiten Seed; Gurt für M2s Prüfung: höchstens ein `SeedUnsettled`-Slot je Paar): bestehende Paare hängen an den
  Slot, der ihre Zeilen trägt (nach Kind 2 der gesettelte Seed), neue Paare bekommen einen neuen unsettled Seed, den der nächste
  Abgleich settelt; danach die eigene Abbruchprüfung (keine `NULL`, kein fremdes `EmoteId`). Fixture in Kind 3: M1 → Zeilen des alten
  Writers (Raw-Insert ohne `SlotId`) → Settle durch Kind-2-Code → M2-`Up` grün, Summen je Paar unverändert, bestehende Paare und neue
  Paare je ein Fall. Test 9 gegen das echte M2-`Down` wiederholen.
- **Kind 5 (M3):** Snapshot-Zeilen mit `SlotId NULL` aus M1 (E9) existieren lokal; M3 `SET NOT NULL` braucht dafür eine Regel. Test 12
  (`CollapseBackfillSnapshotSql`) gegen das echte M3-`Down` wiederholen.
- **Kind 9:** Rollback-Ziel = Vorgänger laut `migrations list` (E1); Operations-Blöcke = Konstanten aus `EmoteSlotRollbackSql` (E10)
  und, als Reparaturschritt nach einem abgebrochenen Fenster vor dem zweiten Versuch, `EmoteSlotSeedSql` (E17); Prod-Kopie-Lauf für
  M1–M3 (AK 1); Fußnote in §12.2 der Spec; DECISIONS final.

## 12. Rückfragen an den Betreiber — alle entschieden (2026-10-11)

Keine offenen Fragen mehr.

- **R1 — Archivzustand der Seeds:** Spec-Wortlaut. `IsArchived`/`ArchivedAt` 1:1 aus `Emotes`; die frühere Planer-Entscheidung E8 ist
  gestrichen (Abschnitt 3). Die Folge für Seeds nicht aktiver Sets ist ein Risiko von Kind 2 (Abschnitt 11).
- **R2 — AK 1 „Kopie der Prod-Daten":** gehört zu Kind 9 (#379), für M1–M3 gemeinsam vor dem Fenster; in der AK-Tabelle des PR als
  „Rest in #379" (E13).
