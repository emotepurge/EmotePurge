# Plan #373 — Emote-Slots, Kind 3: Zählschlüssel auf den Slot (M2) und alle UsageStats-Schreiber

Erstellt am 2026-10-12 im Worktree `EmotePurge-slots` (Branch `feat/373-slot-usage-key`, Basis `origin/main` @ `5c9c5b2d`).
**Status: Revision 1 (12.10.) nach dem adversarialen Review (Codex Sol) und den Antworten des Betreibers; keine offenen Rückfragen.**
Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) (§6.1, §8, §11.1 M2, §11.2, §12,
§13 AK 14–17, 44, 56, §15, §16 Kind 3; D12, D15, D18, D19, D28, D31), Issue #373 mit den beiden Übergabekommentaren aus #371 und #372
(bindend), [Plan-372](Plan-372-Emote-Slots-Reconcile.md) §2.0, §2.4, §2.2a, §4, §11, DECISIONS #365 („Child 1", „Child 2").
Formatvorlage: Plan-372.

**Was dieser Plan trägt:** nur, was Spec und Übergaben offen lassen — Variante (a) des Betreibers, Entstehung der Basis-Seeds,
Flush- und Map-Verträge je Flag-Zustand, den Backfill-Mindestschnitt, M2 im Detail, Tests, Live-Prüfung, Prod-Übergabe, Aufgabenfolge.
Kein Code. D0–D33 und die Betreiberentscheidungen aus #371/#372 werden nicht wieder aufgemacht, außer den zwei unten bestätigten
Abweichungen.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **Betreiber 12.10.** = vom Betreiber entschieden.

### Revision 1 (12.10.) — was sich geändert hat

| Änderung | beantwortet |
|---|---|
| **E7 verschärft:** die An-Map lässt **jedes** Emote mit unsettled Seed des aktiven Sets ganz aus, auch seine schon gesettelten Slots (Definition = Settle-Population aus Plan-372, `LoadActiveSetSeedIdsAsync`); vertagtes Settle hält es dauerhaft draußen (Spec §14 (c), hingenommen). Tests „gesettelter Slot neben Seed" und „vertagt über zwei Ticks" | Sol P1 (1) |
| **E10 neu begründet:** das frühere Beispiel (An-Map zählt auf T neben S) ist mit dem neuen E7 unmöglich; die Zusammenführung bleibt als Gurt, weil eine Unique-Verletzung im Settle den Kanal dauerhaft blockiert und E11a dieselbe Umbindung auch für Importzeilen nutzt | Sol P1 (1), eigene Nachprüfung |
| **Neu E11a:** das Settle hängt bei „Ziel existiert" auch die Snapshot-Zeilen vom Seed auf das Ziel um (`CreatedSlot` → false); `ReplaceBlockAsync` nimmt vor dem Lesen der Snapshot-`SlotId` eine `FOR SHARE`-Sperre auf die Emote-Zeilen des Blocks und ist damit mit dem Settle serialisiert. Tests Enqueue → Block → Settle → nächster Block, beide Startreihenfolgen | Sol P1 (2) |
| **E9 neu: Flush in zwei Phasen.** Phase 1 schreibt alle echten Slot-Schlüssel und committet, Phase 2 löst die Platzhalter in eigener Transaktion auf und sieht dabei die Zeilen aus Phase 1. Das schließt die Verschränkung „letzte Importzeile gelöscht → Platzhalter neben gepuffertem altem Schlüssel → zweiter Seed" (nur lokal mit Backfill erreichbar) und lässt aufgelöste Schlüssel landen, auch wenn Phase 2 scheitert. Neuer Rückgabevertrag mit zurückgestellten Schlüsseln; E6a gilt jetzt für jeden Aus-Zustand ohne frühere An-Phase, nicht nur auf Prod | Sol P2 (3), (4) |
| **Neu E9a:** begrenzte Wiederholung des Final-Flush in `UsageFlushWorker.StopAsync` innerhalb des Shutdown-Budgets; Tests für Deadlock und `lock_timeout` im letzten Versuch | Sol P2 (4) |
| **E14 ersetzt:** kein automatischer Test-Interceptor; ein ausdrücklicher Test-Helfer und eine eigene Aufgabe **vor** M2 versorgen alle EF-Testschreiber (inkl. der `PostgresLockProbe`-Kontexte in `RowLocksTests`) mit echten `SlotId`; kein Schreibkontext wird automatisch repariert | Sol P2 (5) |
| Betreiberantworten eingearbeitet: E4 bestätigt; R1 → Testkanal in beiden Flag-Zuständen, Betreiber tippt von Hand (E21); R2 → lesende Abfragen auf der geteilten Dev-DB erlaubt (E22); R3 → kein Backfill-Live-Lauf (E23) | Betreiber 12.10. |
| Aufgabenliste neu geschnitten (Fixture-Aufgabe vor M2, Final-Flush eigene Aufgabe, Settle-Änderungen gebündelt), Aufwand neu geschätzt | Folge |

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 3, Variante (a))

1. **M2 `SwitchUsageStatsToSlotKey`** samt Modell, Designer, Snapshot, Migrationstests (§5).
2. **Zählpfad je Slot, unbedingt:** `EmoteMatchSnapshot.NameToSlotId`, `UsageCounterKey(SlotId, EmoteSetId)`, Zähler, Flush mit
   `ON CONFLICT ("SlotId","EmoteSetId","Date")` in zwei Phasen, Requeue und Final-Flush, Kaltstart (§3).
3. **Map-Bau am Flag** `SevenTv:SlotReconcile:Enabled` (§3.2): an = gesettelte Slots ohne Emotes mit offenem Seed (D15/AK 17), aus =
   heutige Namenssemantik mit dem Slot des Paars als Wert.
4. **Basis-Slot-Regel** und ihr Sperrvertrag (§2), geteilt von Flush und Backfill-Enqueue.
5. **Backfill-Basis** (§4): Enqueue bindet den einen Preview-Eintrag je Emote an einen Basisslot, Snapshot mit `SlotId`,
   `ReplaceBlockAsync` schreibt `SlotId` und `EmoteId`, serialisiert mit dem Settle.
6. **Settle-Änderungen** (Plan-372-Code): Zusammenführung kollidierender Zellen (E10), Umbindung der Snapshot-Zeilen (E11a).
7. **Test-Fixtures** mit ausdrücklicher `SlotId` (E14).
8. Docs: Operations (M2-Kurzfenster, Flag-Tabelle), DECISIONS-Absatz „Child 3".

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 4 (#374) | Delta-Push legt provisorische Slots an; bis dahin zählt ein per Dispatch hinzugefügtes Emote bei Flag an erst ab dem nächsten REST-Tick (E7) |
| Kind 5 (#375) | M3, Enqueue ohne Dedup, D7 für Vorschauen, Enqueue-Settle, 409 `backfill_slots_unsettled`, Snapshot-DTO mit `SlotId`, Blockzähler/`ValidateBlock` je Slot, Regeln für M1-Snapshotzeilen mit `SlotId NULL` und für zeilenlose Enqueue-Seeds |
| Kind 6 (#376) | Lese-API je Slot; Sichtbarkeit von Seeds in `/totals` |
| Kind 9 (#379) | Fenster M3 + Einschalten, Prod-Kopie-Lauf, Rückbau der Aus-Zweige (inkl. der Aus-Zweige dieses Kinds, E1) |

### 1.3 Abweichungen und Betreiberentscheidungen

- **[E1] Variante (a) — Betreiber 12.10.** Zähler, Flush, `ON CONFLICT`-Ziel und Backfill-Insert wechseln **unbedingt** auf den
  Slot-Schlüssel (Spec-Wortlaut). Nur der **Map-Bau** verzweigt am bestehenden Flag (§3.2). Grund: M1 läuft seit 11.10. auf Prod, das
  Flag bleibt dort bis #379 aus, Prod hat also nur unsettled Seeds; `main` wird weiter deployt, und nach dem Merge braucht jeder
  Main-Deploy M2 (`PendingMigrationGuard`). Prod zählt danach exakt wie heute, nur unter dem Seed-Slot; #379 settelt diese Seeds
  (Plan-372 „Ziel existiert", „M2 vor oder nach dem Einschalten").
- **[E2] Abweichung 1 — Betreiber 12.10.:** Der Kind-2-Vertrag „Flag aus = keine Schreibung auf `EmoteSlots`" wird gelockert: bei Flag
  aus dürfen **unsettled Basis-Seeds** entstehen — präzisiert durch E4: nur im Flush (zusammen mit der ersten Zeile eines Paars) und im
  Backfill-Enqueue. Sonst bleibt der Aus-Zustand aus Plan-372 §2.0 unverändert (Abgleich, Overlay, Leser, Bericht, Delta).
- **[E3] Abweichung 2 — Betreiber 12.10.:** D19 „alle drei Migrationen in einem Fenster" wird geteilt. M2 geht mit dem **nächsten
  Main-Deploy nach #373** in einem kleinen Fenster nach Prod (api und worker stoppen, Backup, M2 von Hand über den Tunnel, beide Images
  gemeinsam hoch — wie M1 am 11.10.); das §11.2-Fenster schrumpft auf M3 + Einschalten. Rollback bei Flag aus: höchstens ein Slot je
  Paar trägt Zeilen (E6a) ⇒ der `Down`-Guard von M2 passiert ⇒ verlustfrei, danach Prä-#373-Images; oder Flag aus lassen und vorwärts
  reparieren. **Nie ein Prä-#373-Image gegen eine M2-Datenbank.**
- Die Spec wird nicht editiert; Abweichungen stehen im PR-Text und im DECISIONS-Absatz (§9).

## 2. Basis-Slots: Entstehung, Regel, Sperre

### 2.1 Das Problem „Seed ohne Zeilen hat kein Set"

`EmoteSlots` hat keine `EmoteSetId`; das Set eines Seeds ergibt sich nur aus seinen `UsageStats`-Zeilen (Spec 4.1, D12). Ein Seed, der
beim Map-Refresh für jedes aktive Emote ohne Paar-Slot angelegt würde, wäre bis zur ersten Zählung set-los. Drei Folgen: (1)
**Wachstum** — ohne Wiederverwendung legte jeder Tick für jedes unbenutzte Emote einen neuen Seed an; (2) **Cross-Set-Leck** — mit
Wiederverwendung „irgendeines zeilenlosen Seeds" bekäme derselbe Seed nach einem Set-Wechsel innerhalb eines Flush-Fensters Zeilen
zweier Sets, genau der Fehler, den D12/R4 geschlossen hat; (3) **Waisen** — ein zeilenloser Seed wird nie gesettelt und nie gelesen.

### 2.2 Entscheidung

- **[E4] Seeds entstehen im Live-Pfad nur zusammen mit ihrer ersten Zeile, im Flush — Betreiber 12.10. bestätigt.** Die Aus-Map (§3.2)
  liefert für ein Emote ohne Paar-Slot einen **Platzhalter-Schlüssel** „noch kein Slot für Emote X" statt einer Slot-Id; der Zähler
  behandelt ihn opak; der Flush löst ihn je `(EmoteId, EmoteSetId)` des Zählschlüssels auf und legt den Seed bei Bedarf in **derselben
  Transaktion wie die Zeile** an (Phase 2, E9). Jeder Live-Seed trägt so vom ersten Moment an Zeilen genau eines Sets. Der Platzhalter
  ist eine reine Infrastructure-Konvention (Präfix im String, Erzeuger und Erkenner an einer Stelle); Worker und Core sehen nur
  Strings. Er entsteht nur bei Flag aus. Verworfen: Batch beim Map-Refresh (2.1), Anlegen beim Matching (kein DB-Zugriff im Chat-Pfad).
- **[E5] Basis-Slot-Regel** für das Paar `(EmoteId E, EmoteSetId X)`, ein internes statisches Hilfsmodul in Infrastructure (Muster
  `ArchivedSlotUpsert`), zwei Einstiege:
  - **Lesen (ohne Sperre, Map):** (a) der älteste Slot von E, der `UsageStats` von X trägt — D2-Ordnung `AddedAt` aufsteigend mit `NULL`
    zuletzt, dann `Name` ordinal, dann `Id`. **Ordnung im Speicher mit `StringComparer.Ordinal`**, kein `ORDER BY "Name"` in SQL
    (Kollationsfalle, §6.4). Existenzprüfung über den Slot-Index `(SlotId, EmoteSetId, Date)` je Slot-Kandidat, kein Scan aller Zeilen
    des Paars (läuft jeden Resync-Tick je Kanal).
  - **Holen-oder-Anlegen (Flush Phase 2, Enqueue):** (a); sonst (b) der älteste unsettled Seed von E, auf den eine Snapshot-Zeile eines
    Backfill-Laufs **desselben Sets X** zeigt (so ist ein zeilenloser Enqueue-Seed set-gebunden); sonst (c) ein **neuer unsettled
    Seed** mit M1-Semantik (`Name`, `IsArchived`, `ArchivedAt` 1:1 aus dem Emote, `AddedAt NULL`, `AddedAtFromRest false`,
    `SeedUnsettled true`, `LastSyncedAt` jetzt). Verlangt eine offene Transaktion und die Emote-Zeilen als vom Aufrufer gesperrt (E6);
    liest (a)/(b) **nach** der Sperre; mengenbasiert; Rückgabe Slot-Id je Paar und die Menge der angelegten.
- **[E6] Sperrvertrag:** Holen-oder-Anlegen läuft nur unter `RowLocks` in der Konventionsreihenfolge **Kanalzeilen (sortiert, ein
  Statement) → Emote-Zeilen (sortiert, ein Statement)**, Modus `FOR NO KEY UPDATE` (Plan-372 E1). Einen Unique-Index für Seeds je Paar
  gibt es nicht (Spec 4.1); Emote-Sperre plus Neu-Lesen ist die Konkurrenzkontrolle zwischen Flush und Enqueue. Ein Kanal, der in der
  Kanal-Sperre fehlt (Purge hat committet), entfällt; seine Schlüssel werden verworfen wie gelöschte.
- **[E6a] Invariante, Revision 1:** höchstens ein unsettled Seed je Paar, ein Seed trägt Zeilen nur eines Sets, und **bei Flag aus trägt
  je Paar höchstens ein Slot Zeilen** — in jedem Aus-Zustand ohne frühere An-Phase, auch lokal mit eingeschaltetem Backfill. Die
  Gefährdung aus dem Review (eine Importzeile ist die letzte Zeile des Paars, nachdem Retention den Lauf samt Snapshot gelöscht hat; ein
  Folge-Lauf eines anderen Sets löscht sie über das kanalweite `DELETE` in `ReplaceBlockAsync`; der nächste Map-Refresh liefert einen
  Platzhalter, während gepufferte Zählwerte noch den alten Slot tragen) schließt die Phasenfolge des Flush (E9): der alte Schlüssel ist
  in Phase 1 geschrieben, bevor Phase 2 den Platzhalter auflöst, und (a) findet ihn. Ein gepufferter alter Schlüssel stammt immer aus der
  Zeit vor dem Refresh und liegt deshalb im selben gezogenen Batch oder in dessen Requeue; war er schon früher geschrieben, trägt der Slot
  eine Live-Zeile, die kein Backfill-`DELETE` (nur `Source = 1`) entfernt. Nach einer An-Phase können zwei Slots eines Paars Zeilen tragen
  (zwei gesettelte Aliase); dann braucht ein Rollback das Kollaps-SQL (§8 R). M2 prüft die Seed-Teile der Invariante (§5.1).

### 2.3 Was mit den Seeds später passiert

- **Flag an (#379):** Flush-Seeds sind gewöhnliche unsettled Seeds mit Zeilen ihres Paars → Settle-Population des aktiven Sets
  (Plan-372 2.4) bzw. als Seeds fremder Sets unberührt (E7 dort).
- **Enqueue-Seeds ohne Treffer** bleiben zeilenlos (Backfill ist auf Prod aus, nur lokal); über (b) werden sie bei einem Folge-Lauf
  desselben Sets wiederverwendet; die endgültige Regel gehört Kind 5. Retention fasst Slots nicht an (Kaskade nur über Emote).
- **M2-Prüfung „höchstens ein unsettled Seed je Paar"** zählt Paare über Zeilen; zeilenlose Seeds gehören zu keinem Paar.

## 3. Live-Zählung

### 3.1 Benennung und Typen (Spec 6.1 Punkt 4, D15)

- **[E16]** `EmoteMatchSnapshot.NameToEmoteId` → `NameToSlotId`; `IEmoteMatchCache.ReplaceChannel(…, nameToSlotId)`;
  `UsageCounterKey(SlotId, EmoteSetId)`; `IEmoteUsageCounter.Increment(slotId, …)`. Reine Umbenennung über alle Konsumenten
  (`TwitchChatManager`, `Worker.cs` Kaltstart-Erkennung, `WarmMatchCacheIfEmptyAsync`, Map-Leser im Sync, Tests) in einer Aufgabe.
  `EmoteNameMatching` bleibt textgleich (O1: Dedup je Nachricht je Wert = je Slot).

### 3.2 Map-Bau (`RefreshMatchCacheAsync`)

- **[E7] Flag an (D15, AK 15, AK 17), Revision 1:** Eingabe = aktive, gesettelte Slots (nicht archiviert, nicht `SeedUnsettled`)
  aktiver Emotes des Kanals, `Name → SlotId`, `Coalesce` unverändert (O4). **Ausgeschlossen ist jedes Emote, das einen unsettled Seed
  des aktiven Sets hat — mit allen seinen Slots, auch schon gesettelten.** „Seed des aktiven Sets" heißt genau, was das Settle darunter
  versteht (unsettled Seed des Kanals mit `UsageStats` des aktiven Sets, Plan-372 2.3, `LoadActiveSetSeedIdsAsync`), damit Map und Settle
  nie auseinanderlaufen; beide teilen dieselbe Abfrage. Folge, gewollt (AK 17, Spec §14 (c)): bei vertagtem Settle (|G| ≥ 2, ein Eintrag
  ohne v4-Datum) zählt das Emote, bis ein datierter Abgleich settelt — auch über viele Ticks; die Zeile „settle deferred" (Plan-372)
  nennt den Kanal. **Keine virtuellen Einträge** (Plan-372 E14 gilt nicht für die Map): ein aktives Emote ohne aktiven eigenen Slot (vor
  dem ersten An-Abgleich, nach einem Delta-Push bis #374) zählt bis zum nächsten Abgleich nicht — es hat keine Slot-Id. **Tracker** liest
  genau die Map-Eingabe (distinct `(Name, EmoteId)`), damit „chat matching counts each name for one emote id only" stimmt;
  `DuplicateNames` im Status bleibt bei `ActiveNameEntries` (Inventarsicht). **Information-Zeile** (Spec 6.1 Punkt 1): Zahl der
  ausgeschlossenen Emotes, englisch, **nur bei Änderung je Kanal** (prozesslokal wie der Tracker). E21 aus Plan-372 endet.
- **[E8] Flag aus:** heutige Semantik — aktive Emotes, `Emote.Name`, `Coalesce`, emote-weiter Tracker, Logtexte wie heute; **Wert** je
  Emote = Basis-Slot-Lesen (a) für `(Emote, ActiveEmoteSetId)`, sonst der Platzhalter (E4). Keine Schreibung im Map-Bau.
- **Kaltstart:** `WarmMatchCacheIfEmptyAsync` und die Boot-Reihenfolge lesen `NameToSlotId.Count`. Bei Flag an mit lauter Seeds ist die
  Map leer → der Kanal gilt als kalt und wird in Phase 2 zuerst gesynct (gewollt, AK 17).
- **Set-Wechsel bei Flag aus:** neuer Paar-Schlüssel → Seed des neuen Paars oder Platzhalter; gepufferte Zählwerte des alten Sets
  tragen ihren alten Schlüssel und landen am alten Seed. **Umbenennung bei Flag aus:** der Aus-Abgleich ändert `Emote.Name` (K1), der
  Paar-Slot bleibt → die Historie läuft am selben Seed weiter; dessen `Name` veraltet bis zum Settle (bedeutungslos, solange unsettled).

### 3.3 Flush (`UsageStatFlushService.FlushAsync`)

- **[E9] Vertrag, Revision 1 (zwei Phasen):**
  1. **Gültigkeitsfilter über `EmoteSlots`** (Spec 6.1 Punkt 3): echte Slot-Ids → `(SlotId, EmoteId, ChannelName)` aus Slot, Emote,
     Kanal; fehlend → verworfen, **eine** englische Warnzeile mit der Zahl (AK 16). Platzhalter → Emote-Existenz; fehlend → verworfen.
  2. **Phase 1 — echte Schlüssel:** ein Upsert wie heute (`EmoteId` aus dem Slot, Konfliktziel der Slot-Index), eigene implizite
     Transaktion, keine Sperren außer den FK-`KEY SHARE`. Bei Flag an gibt es nur diese Phase. Scheitert sie, ist nichts geschrieben,
     der Aufruf wirft wie heute, der Worker stellt alles zurück.
  3. **Phase 2 — Platzhalter, nach dem Commit von Phase 1:** eigene Transaktion mit `SET LOCAL lock_timeout` (5 s), Sperren nach E6 für
     die Kanäle und Emotes der Platzhalter, Holen-oder-Anlegen (E5; (a) sieht die eben geschriebenen Zeilen), Zusammenfassen nach
     aufgelöstem `(SlotId, EmoteSetId)`, ein Upsert. Seed und erste Zeile committen zusammen oder gar nicht.
  4. **Rückgabevertrag:** statt nur der Kanalnamen ein Ergebnis aus den geschriebenen Kanalnamen (beide Phasen) und den
     **zurückgestellten** Platzhalter-Schlüsseln samt Zählwerten, wenn Phase 2 scheiterte (die Ausnahme dort wird gefangen, englische
     Warnzeile). Aufgelöste Schlüssel landen damit immer; ein umkämpfter Platzhalter hält keinen fremden Kanal auf.
  5. **Requeue (`UsageFlushWorker`):** ein Wurf (Phase 1) stellt wie heute alles zurück; ein Ergebnis mit zurückgestellten Schlüsseln
     verbucht den Erfolg für das Geschriebene und stellt nur diese zurück. Für sie gilt dieselbe Grenze von fünf Fehlversuchen in Folge
     (Tagesbuchung, Bestandsbegründung), eigener Zähler, danach verworfen mit Fehlerzeile. Platzhalter gehen als Platzhalter zurück und
     werden neu aufgelöst; verworfene (gelöschte) Schlüssel werden nie zurückgestellt.
- **[E9a] Final-Flush, Revision 1:** `StopAsync` wiederholt nach dem ersten Final-Flush, solange noch Zählwerte im Zähler stehen (aus
  Wurf oder Rückstellung), höchstens zweimal mit kurzer zufälliger Pause und nur, solange das Shutdown-Token nicht abgelaufen ist
  (Budget = Host-Shutdown-Timeout, im Repo nicht gesetzt; der Umsetzer prüft den .NET-10-Standard und hält drei Versuche samt
  `lock_timeout` darunter, sonst wird der `lock_timeout` für den Final-Flush gekürzt). Was danach übrig ist, wird mit einer Fehlerzeile
  (Zahl, keine Namen) verworfen — wie heute bei einem Fehlschlag.
- **Z5 (Settle vs. Flush), Flag an:** der Flush schreibt nur gesettelte Slots von Emotes ohne offenen Seed (E7), das Settle bewegt nur
  Zeilen von Seeds des aktiven Sets — die Zellen sind disjunkt. Ein 40P01 aus verschränkten Zeilen-Updates erholt sich wie bisher
  (Sync E17, Flush Requeue).

### 3.4 Settle-Änderungen nach M2 (Plan-372-Code)

- **[E10] Umbindung als Zusammenführung, Revision 1 (Gurt).** Die Umbindung „Ziel existiert" (Plan-372 2.2 Schritt 7, heute ein
  einfaches `UPDATE … SET SlotId`) wird ab M2 zur Zusammenführung: Zellen ohne Gegenstück am Ziel werden umgehängt, Zellen mit
  Gegenstück addiert (alle drei Zähler) und die Seed-Zeile gelöscht; `Source` = Live, wenn eine der beiden Live ist (Regel des
  Kollaps-SQL). Das Beispiel aus dem Entwurf (An-Map zählt auf T, während S unsettled ist) ist mit dem neuen E7 nicht mehr erreichbar;
  ein anderer Live-Pfad zu einer Kollision ist nicht bekannt. Die Änderung bleibt, weil (i) eine Unique-Verletzung im Settle jeden Tick
  dieses Kanals scheitern ließe (E17 wiederholt einmal und propagiert) — eine Dauerblockade statt eines Einzelfehlers —, (ii) E11a die
  Umbindung auch für Importzeilen nutzt, deren Tage der Plan nicht lückenlos gegen Live-Tage absichern kann, und (iii) sie billig ist
  (gleiche Sperrfolge, ein Statement mehr). Die Übergabe „kollisionsfrei nach M2" wird damit zum Vertrag statt zum Argument.
- **[E11a] Snapshot-Zeilen folgen dem Settle, Revision 1.** Bindet das Settle die Zeilen eines Seeds S an ein bestehendes Ziel T
  („Ziel existiert"), hängt es in derselben Transaktion auch **alle Snapshot-Zeilen** (`ChatLogBackfillRunEmotes`) mit `SlotId = S` auf T
  um und setzt dort `CreatedSlot = false`: der Lauf hat S angelegt, nicht T; `CreatedSlot` bleibt so eine wahre Aussage über den Slot,
  auf den die Zeile zeigt (12.3 darf T nie löschen). S bleibt leer und archiviert ohne Datum (Plan-372 E12) und wird von keinem Lauf mehr
  referenziert. Das In-place-Settle (gleiche Id) braucht nichts. Betroffen sind nur Läufe desselben Sets, weil S per E5/E6a Seed genau
  eines Sets ist und nur dessen Settle ihn anfasst.
  **Serialisierung:** `ReplaceBlockAsync` nimmt nach dem Kanal-`KEY SHARE` und **vor** dem Lesen der Snapshot-`SlotId` eine `FOR SHARE`-
  Sperre auf die Emote-Zeilen der Aggregate des Blocks (ein Statement, sortiert; neuer Modus-Einstieg in `RowLocks`, Reihenfolge Kanal →
  Emote). `FOR SHARE` schließt das `FOR NO KEY UPDATE` des Settle aus: läuft das Settle zuerst, liest der Block danach die umgehängte
  `SlotId`; läuft der Block zuerst, wartet das Settle und bewegt dessen frische Zeilen mit (E10). `FOR SHARE` verträgt sich mit den
  FK-`KEY SHARE` des Flush und dem Kanal-`KEY SHARE`. Kein neuer Zyklus mit Settle, Enqueue, Bericht oder Flush Phase 2 (alle Kanal →
  Emote) und keiner mit dem Aus-Sync (Emote → Kanal), weil das Kanal-`KEY SHARE` dessen Kanal-Update nicht blockiert. Z4 (Settle-`UPDATE`
  vs. Block-`DELETE`) wird für die Emotes der Aggregate ausgerichtet; für die übrigen Importzeilen des Kanals bleibt Plan-372s Erholung.

### 3.5 Sperrzyklen, die dieses Kind neu einführt

| Zyklus | Auflösung / Erholung |
|---|---|
| **Z9** Flush Phase 2 (Kanal → Emote) vs. Flag-aus-Sync (Emote → Kanal, Plan-372 Z3 Aus-Zustand) | nicht ausrichtbar ohne den Aus-Sync zu ändern; nur beim ersten Zählen eines Paars. Flush: nur Phase 2 wird zurückgestellt (E9); Sync: E17 (beide Zustände, E27) |
| **Z10** Flush Phase 2 vs. Flag-aus-Delta (Emote-Updates in EF-Reihenfolge) | Flush wie Z9; Delta: einmalige Wiederholung in beiden Zuständen (Plan-372 Z8) |
| **Z11** Enqueue-Emote-Sperre (Kanal → Nutzer → Emote) vs. Flag-aus-Sync | Enqueue: bestehende Deadlock-Wiederholung (3 Versuche); Sync E17 |
| ausgerichtet | Flush/Enqueue/Block vs. Purge, Legacy-Bericht (an), Flag-an-Sync und -Delta: alle Kanal → Emote |

## 4. Backfill-Mindestschnitt (Spec §16 Zeile 3 gegen Zeile 5)

- **[E11] Enqueue (`EnqueueUnderLocksAsync`):** Auswahl bleibt ein Preview-Eintrag je Emote (D36, last-wins) bis Kind 5. Nach Kanal-
  und Nutzersperre und dem Platzhalter-Upsert (Emote-Platzhalter wie heute) sperrt Enqueue die Emote-Zeilen aller Mitglieder über
  `RowLocks` (ein Statement, sortiert; D28 mit Modus E1) und bindet je Mitglied: (1) **datierter Eintrag:** autoritativer Slot von E mit
  exakt diesem `AddedAt` (bei mehreren der namensgleiche) — D28 Schritt 1, set-unabhängig, bei Flag aus auf Prod nie vorhanden; (2) sonst
  **Holen-oder-Anlegen** (E5) für `(E, Lauf-Set)`. Snapshot-Zeile mit nicht-null `SlotId`; `CreatedSlot = true` genau für in diesem
  Versuch angelegte Slots. Kein D7, kein Settle, kein 409 (Kind 5). Die bestehende Deadlock-Wiederholung umfasst die neuen Schritte.
  Erwogen und verworfen (das Review ließ die Wahl): ein engerer Schnitt, der bei Flag an nie an einen unsettled Seed bindet — er bräuchte
  das Flag im Enqueue (gegen E15) und eine eigene Slot-Anlage für das aktive Set, also genau Kind 5; E11a schließt das Loch kleiner.
- **`ReplaceBlockAsync`:** Aggregate bleiben je `EmoteId` (Worker, `ChatLogBackfillBlockCounter`, Snapshot-DTO **unverändert** bis Kind
  5 — eine Snapshot-Zeile je Emote, PK `(RunId, EmoteId)` bis M3). In der Transaktion: Kanal-`KEY SHARE`, Emote-`FOR SHARE` (E11a), dann
  `SlotId` je `EmoteId` aus dem Snapshot des Laufs; **Existenzprüfung gegen `EmoteSlots`** (Slot vorhanden, gehört zu diesem `EmoteId`,
  Emote im Kanal), sonst Aggregat verworfen mit der bestehenden Warnzeile (D25). Insert mit `SlotId` **und** `EmoteId`, weiter ohne
  `ON CONFLICT`; 23505 bleibt `LiveRowConflict`, jetzt über den Slot-Index — bei Flag aus (ein Slot je Paar) gleichwertig zu heute; bei
  Flag an erkennt er nur noch die Zelle desselben Slots (bekannte Grenze bis Kind 5; Import-Tage liegen vor dem Zählbeginn).
- **Läufe von vor #373:** nicht-terminale Läufe mit Snapshot-`SlotId NULL` (lokal möglich, M1 E9 und alte Enqueues) lässt M2 nicht zu
  (§5.1); `ReplaceBlockAsync` behandelt eine trotzdem gefundene `NULL` wie einen gelöschten Slot. Terminale Läufe bleiben unberührt
  (Kind 5/M3). Prod hat keine Läufe (Backfill aus; Vorbedingung im Fenster, §8).
- AK 56: leere migrierte DB, unbekanntes Einzel-Emote → Platzhalter-Emote, ein Basisslot (Seed, `CreatedSlot`), Snapshot mit `SlotId`;
  echter `EnqueueAsync` → `ReplaceBlockAsync` schreibt die Zeile mit genau dieser `SlotId`.

## 5. M2 `SwitchUsageStatsToSlotKey`

### 5.1 `Up` (Reihenfolge bindend, Übergabe #371)

1. `SET LOCAL lock_timeout = '5s'`, dann `LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE` (die Konstante läuft unter READ COMMITTED;
   das spätere `ALTER` sperrt zu spät).
2. `EmoteSlotSeedSql.AttachUnslottedUsageRowsSql` unverändert (hängt die `NULL`-Zeilen der alten Schreiber seit M1 an; auf Prod alle
   Zeilen seit 11.10.).
3. **Eigene Prüfung**, ein `DO`-Block mit `RAISE EXCEPTION` und sprechender englischer Meldung je Fall: (a) keine Zeile mit
   `SlotId NULL`; (b) keine Zeile, deren Slot zu einem anderen `EmoteId` gehört; (c) höchstens ein `SeedUnsettled`-Slot mit Zeilen je
   `(EmoteId, EmoteSetId)`; **[E12]** zusätzlich (d) kein unsettled Seed mit Zeilen zweier `EmoteSetId` (D12, E6a) und (e) kein Lauf im
   Status queued/running/paused mit einer Snapshot-Zeile `SlotId NULL` (Meldung nennt den Ausweg: Lauf abbrechen).
4. `SlotId SET NOT NULL`.
5. Index-Tausch: `IX_UsageStats_EmoteId_EmoteSetId_Date` neu als **nicht-unique** covering (`INCLUDE UseCount`, D18 — Seed-Konstante und
   Emote-Leser brauchen ihn), `IX_UsageStats_SlotId_EmoteSetId_Date` neu als **unique** covering. Da die Zeilen vorher je
   `(EmoteId, EmoteSetId, Date)` eindeutig waren und jeder Slot genau einem Emote gehört (b), kann der Unique-Aufbau nicht scheitern.

### 5.2 `Down` (D31)

`lock_timeout` + `LOCK TABLE` wie oben; **Guard:** mehr als eine Zeile je `(EmoteId, EmoteSetId, Date)` → `RAISE EXCEPTION` mit Verweis
auf `EmoteSlotRollbackSql.CollapseUsageStatsSql`; sonst Slot-Index nicht-unique, Emote-Index unique, `DROP NOT NULL`. `SlotId`-Werte
und angelegte Seeds bleiben (harmlos; ein erneutes `Up` findet nichts anzuhängen).

### 5.3 Modell und Testdaten

- `UsageStat.SlotId` wird Pflicht (Muster wie `EmoteId` derselben Entität), Index-Konfiguration in `AppDbContext` getauscht; Designer
  und `AppDbContextModelSnapshot` per `migrations add`, `Up`/`Down` von Hand ergänzt. Gate: Probe-`migrations add` danach leer.
- **[E14] Testdaten, Revision 1 (ersetzt den Interceptor):** ~300 Stellen legen `UsageStat` per EF ohne `SlotId` an
  (`UsageStatQueryServiceTests` 215, `VoteSessionQueryServiceTests` 43, `EmoteSetStatusServiceTests` 32, dazu Sync-, Retention-, Purge-,
  Kaskaden-, Backfill-Tests und die Schreiber in `RowLocksTests`, die über `PostgresLockProbe.CreateTaggedDbContext` eigene Kontexte
  bauen). Sie bekommen **ausdrücklich** echte `SlotId`: ein Test-Helfer in `Fixtures/` legt je `(EmoteId, EmoteSetId)` einen Slot an bzw.
  verwendet ihn wieder und gibt ihn zurück; Standard ist ein unsettled Seed (der Zustand, den M1/M2 auf Prod erzeugen), Tests mit Flag
  an oder mit Slot-Bezug wählen die Art (gesettelt, autoritativ, provisorisch) selbst. Kein automatisches Füllen in irgendeinem Kontext:
  der Vertrag „jeder Schreiber nennt seinen Slot" bleibt Prüfgegenstand, und kein künftiger EF-Schreiber kann von einer Reparatur
  verdeckt werden. Die Umstellung läuft **vor** M2 als eigene Aufgabe (T2): die Spalte ist seit M1 nullbar, ausdrückliche Werte sind
  schon gültig, die Suiten bleiben grün. Achtung: ein Seed des aktiven Sets ändert bei Flag an das Ergebnis eines Sync-Tests (Settle,
  Map-Ausschluss) — solche Tests nicht über den Standard bedienen. Tests, die **absichtlich** `NULL`-Zeilen schreiben (Plan-372
  Slot-Test 21, `SlotSyncKit` mit `slot = null`), ziehen in die M2-Migrationstests (§6.1 Fall 5).
- **Leser-Audit (Teil von T5):** alle `UsageStats`-Leser (`UsageStatQueryService`, `VoteSessionQueryService`, `EmoteSetStatusService`,
  Retention, Identität, Harness) aggregieren per `GroupBy`/`Sum`; keiner darf Eindeutigkeit von `(EmoteId, EmoteSetId, Date)` annehmen.
  Befund im PR.

## 6. Tests

Integrationstests `[Collection("Postgres")]`; Flag je Test über den Options-Typ. **[E15]** Kein neuer Leser von
`SlotReconcileOptions` (der Flush braucht das Flag nicht, der Platzhalter trägt seine Bedeutung; Enqueue und Block binden in beiden
Zuständen gleich) → `SlotReconcileRegistrationTests` bleibt unverändert; entsteht doch einer, kommt er dort dazu.

### 6.1 `SwitchUsageStatsToSlotKeyMigrationTests` (neu, Muster `AddEmoteSlotsMigrationTests`, eigene DB je Fall)

1 leere DB → Schema (NOT NULL, Slot-Index unique, Emote-Index nicht-unique, beide `INCLUDE UseCount`) · 2 M1 → alte Zeilen `NULL`
(bestehendes und neues Paar) → `Up`: Zeilen am Seed bzw. neuer Seed, Summen je Paar gleich · 3 `Up` bricht ab, wenn eine andere Sitzung
`UsageStats` hält, und hinterlässt nichts · 4 Prüfungen (a)–(e) je ein roter Fall mit Meldung; terminaler Lauf mit `NULL`-Snapshot
besteht · 5 **Übergabe-Fixture:** M1 → alter Schreiber (Raw-Insert ohne `SlotId`) → Kind-2-Settle über `SyncChannelAsync` mit gefaktem
7TV-Client und Flag an → M2: Summen je `(EmoteId, EmoteSetId)` erhalten, Zeilen am gesettelten Slot, neue Paare mit eigenem Seed, der
nächste Sync settelt per „Ziel existiert" (ersetzt Plan-372 Slot-Test 21) · 6 `Down` ohne Dubletten → Schema zurück, Summen gleich ·
7 `Down` mit zwei Slot-Zeilen derselben `(EmoteId, Set, Date)` wirft; nach `CollapseUsageStatsSql` grün, Summen unverändert (Fixtures
`CollapseUsageStatsSql_*` gegen das echte `Down`) · 8 **Flag-aus-Welt verlustfrei, inklusive der Review-Verschränkung:** M2 → (i) Flush
mit Platzhaltern und echten Schlüsseln; (ii) Paar, dessen einzige Zeile eine Importzeile eines per Retention gelöschten Laufs ist; ein
Block eines anderen Sets löscht sie; der Map-Refresh liefert den Platzhalter; ein Flush-Batch mit altem Slot-Schlüssel **und**
Platzhalter → genau ein Slot trägt das Paar → `Down` passiert den Guard, Summen gleich → `Up` erneut ohne Änderung · 9 Up-Down-Up mit
Tausenden Paaren innerhalb des Command-Timeouts (`SeedThousandsOfPairs`, `SeedSql_*`).

### 6.2 Flush (`UsageStatFlushServiceTests`, 17 angepasst +3 Spec, + eigene)

Spec: **AK 14** zwei Slots eines Emotes → zwei Zeilen `(SlotId, Set, heute)`, derselbe `EmoteId`, zweiter Flush addiert · **AK 16**
gelöschter Slot → verworfen, Warnzeile, Rest geschrieben, Kanal nicht gemeldet · **AK 17 (Flush-Hälfte)** nach In-place-Settle schreibt
der Flush auf denselben Slot, der die Seed-Historie trägt; Seed-Zeilen anderer Sets bleiben. Platzhalter: ohne Paar-Slot → genau ein Seed
mit genau der Zeile, M1-Felder · Paar-Slot inzwischen vorhanden → kein neuer Seed · **Phase-1-Zeile wird in Phase 2 gefunden** (alter
Schlüssel und Platzhalter desselben Paars im Batch, der Slot hatte vorher keine Zeile) → ein Slot, Summe · zwei Sets desselben Emotes →
zwei Seeds, je nur ihre Zeile · zeilenloser Enqueue-Seed desselben Sets (b), nicht eines anderen · gelöschtes Emote → verworfen ·
**Phase 2 scheitert** (andere Sitzung hält die Emote-Sperre über den `lock_timeout`; zweite Variante: injizierter 40P01) → Phase-1-Zeilen
committet, das Ergebnis nennt genau die zurückgestellten Platzhalter samt Zählwerten, kein Seed angelegt · Konkurrenz Flush-Platzhalter
vs. Enqueue für dasselbe Paar, beide Startreihenfolgen (`PostgresLockProbe`) → genau ein Seed · zwei Slots eines Emotes am selben Tag
verletzen nichts.

### 6.3 Sync, Map, Settle

- **`SevenTvSyncServiceSlotMapTests` (neu):** Flag an — AK 17 (Seeds des aktiven Sets → Map ohne X, Information-Zeile genau einmal;
  nach dem Settle `ome44` auf dem Slot mit der Seed-Historie, `hob44` auf dem neuen; ohne v4-Daten bleibt X draußen) · **gesettelter
  Slot T neben unsettled Seed S des aktiven Sets → X fehlt ganz** · **vertagtes Settle (zwei Aliase, einer undatiert) über zwei Ticks →
  X fehlt in beiden, nach einem datierten Tick vorhanden** · Seed nur eines fremden Sets → X zählt · AK 15 (Kollision zweier Emotes über
  Slots → ein Eintrag, Tracker warnt; zwei Aliase eines Emotes → keine Warnung) · keine virtuellen Einträge · Zeile nur bei Änderung.
  Flag aus — Map über `Emote.Name`, Wert = Seed des Paars bzw. Platzhalter; Set-Wechsel; Umbenennung behält den Slot; archivierte Emotes
  fehlen; nie ein Seed eines anderen Sets.
- **E10** (`SevenTvSyncServiceSlotTests`): „Ziel existiert" mit kollidierender `(T, Set, D)`-Zeile → summiert, `Source`-Regel, Seed
  archiviert ohne Datum, kein Fehler; ohne Kollision unverändert umgehängt.
- **E11a** (Integration, Backfill + Sync): Enqueue (Snapshot → S) → Block 1 schreibt auf S → REST-Settle „Ziel existiert" auf T → Snapshot
  zeigt auf T, `CreatedSlot false`, S leer → Block 2 schreibt auf T; nach dem nächsten Tick keine Zeile mehr an S. **Beide
  Startreihenfolgen** mit `PostgresLockProbe`: Settle hält die Emote-Sperre → Block wartet und schreibt danach auf T; Block hält
  `FOR SHARE` → Settle wartet und führt die frischen Blockzeilen mit. In-place-Settle lässt den Snapshot unverändert.
- **Bestand:** `SevenTvSyncServiceTests` (31 Map-Zusicherungen) und die übrigen Sync-Klassen: Umbenennung, Erwartungen auf Slot-Id bzw.
  Platzhalter.

### 6.4 Backfill, Hilfsmodul, Test-Helfer

- `ChatLogBackfillServiceTests*`: **AK 56** wie §4 · bestehendes Emote mit Seed des Lauf-Sets → gebunden, `CreatedSlot false` · datierter
  Eintrag mit autoritativem Slot gleichen Datums → dort gebunden · Folge-Lauf desselben Sets nutzt den zeilenlosen Seed wieder ·
  Snapshot-Slot fehlt → Aggregat verworfen · `LiveRowConflict` über den Slot-Index · handgebaute Snapshots mit `SlotId` (~10 angepasst).
- **Hilfsmodul E5:** D2-Ordnung inkl. Namens-Gleichstand ordinal; (b) nur gleiches Set; verlangt offene Transaktion. Falls die Umsetzung
  doch in SQL nach `Name` ordnet: Spalte per `UseLinguisticNameCollationAsync` auf ICU, Test muss ohne `COLLATE "C"` rot sein
  (Alpine-Postgres sortiert sonst bytewise und zeigt den Fehler nie).
- **`RowLocks`-`FOR SHARE`-Einstieg:** sortiert, ein Statement, blockiert einen `NO KEY UPDATE`-Sperrer, nicht einen FK-Insert.
- **Test-Helfer E14:** ein Slot je Paar, Wiederverwendung, wählbare Art.

### 6.5 Unit und Worker

`EmoteMatchCacheTests` (12), `EmoteNameMatchingTests` (24): nur Umbenennung · Platzhalter-Konvention: Erzeugen/Erkennen, echte Guid-Id
nie als Platzhalter · `EmoteUsageCounterTests` (13) umbenannt, +1: zwei Slots desselben Emotes in einer Nachricht → zwei Schlüssel ·
`EmoteMatchCacheSwitchCountingTests` +1 (AK 17: leere Map zählt nichts, danach Map mit zwei Slots → +1 je Slot, O1) ·
`WorkerBootSequenceTests` +1 (Kanal mit leerer Aufwärm-Map gilt als kalt und wird zuerst gesynct) · **`UsageFlushWorkerTests` (neu,
container-frei, Flush-Dienst als Substitut):** Teilergebnis stellt nur die zurückgestellten Schlüssel zurück, eigener Fehlerzähler mit
Grenze fünf · Final-Flush: erster Versuch wirft (Deadlock-Variante) → zweiter gelingt · Rückstellung im Final-Flush (`lock_timeout`-
Variante) → Wiederholung, nach drei Versuchen verworfen mit Fehlerzeile · abgelaufenes Shutdown-Token → keine weitere Wiederholung.
Api.Tests, Vitest, E2E: nichts.

## 7. Live-Verifikation (Regel 16) — Freigabe des Betreibers je Anlass

Randbedingungen wie Plan-372 §7: geteilte Dev-DB `emotepurge`, geteilter Api-/Worker-Container und Redis gehören einer anderen Sitzung;
**nie `docker compose`, nie etwas im Haupt-Checkout.** Betreiberantworten 12.10.: **[E21]** Testkanal `olaf_olaf_son` für **beide**
Flag-Zustände (nur dort lässt sich das Mehrnamen-Emote `ome44`/`hob44` per 7TV-Testtoken anlegen); der Betreiber **tippt die
vorbereiteten Nachrichten von Hand** mit dem eigenen Account. **[E22]** Lesende Abfragen auf der geteilten Dev-DB `emotepurge` sind
erlaubt; den Zeitpunkt stimmt der Orchestrator mit den anderen Sitzungen ab. **[E23]** Kein Backfill-Live-Lauf in #373 (AK 56 ist ein
Integrationstest; live im Abnahmelauf von #379).

| Ressource | Wert |
|---|---|
| DB | `emotepurge_slots373` im Container `emotepurge-dev-postgres` (Host `127.0.0.1:5432`), per `pg_dump \| psql` im Container |
| Redis | Wegwerf-Container `redis-slots373` auf `127.0.0.1:6391` |
| Prozesse | ein Worker per `dotnet run --project src/EmotePurge.Worker` aus dem Worktree (PID notieren); **keine** Api, kein Port `:5151` |
| Dateien | Scratchpad: CSVs, Protokoll, Nachrichtenliste, Header-Datei für das 7TV-Token (Modus 600) |

0. `df -h /` und `pg_database_size` der Dev-DB; Abbruch, wenn der Platz nicht das Doppelte trägt. **Vorbereitung durch T8:** prüfen, dass
   der Account des Betreibers nicht auf der Ausschlussliste für Chatter steht (`IExcludedChatterFilter` und seine Konfiguration), sonst
   zählt nichts; die **exakte Nachrichtenliste** je Phase mit erwarteter Wirkung je Nachricht schreiben (7.4/7.5) und dem Betreiber vorab
   vorlegen.
1. **Kopie und Vorher-Messung:** wie Plan-372 7.1/7.3 (Benutzer und Passwort aus dem Container, nie ausgeben, CR abschneiden). Messen:
   `count(*)` und `NULL`-Zeilen in `UsageStats`, Seeds, Paare; Erhaltungs-Population = Zeilen mit `Date < D0` je `(EmoteId, EmoteSetId)`
   (Zeilenzahl, drei Summen). In der Kopie nicht-terminale Backfill-Läufe abbrechen (Prüfung (e)).
2. **M2 auf der Kopie** per `dotnet ef database update` mit `--connection` (inkl. `Command Timeout=600`): Laufzeit notieren (→ Prod-
   Fenstergröße, hochgerechnet mit Prods `count(*)` und Prods `NULL`-Zeilen seit 11.10.); Prüfungen: keine `NULL`, Indizes laut
   `pg_indexes`, Erhaltung diff-leer. Dann `Down` (Laufzeit, Guard passiert, Erhaltung), dann erneut `Up`.
3. **Last begrenzen:** `IsBotActive = false` außer `olaf_olaf_son` und höchstens drei Kanälen, die laut Kopie am Messtag schon Zeilen
   haben (echter Chat); `handofblood` nur lesend, wenn es darunter ist. **Testkanal vorbereiten:** Ausgangszustand des aktiven Sets
   sichern; per 7TV-Schreib-API mit dem Token aus `~/.config/emotepurge-7tv-test-token` (nur als Header-Datei, nie ausgeben, nie
   `set -x`) das Emote `01KAMA120V9DC2P1YW9QWFHA16` zuerst unter `ome44`, danach unter `hob44` hinzufügen (`ome44` ist damit der ältere
   Eintrag).
4. **Phase A, Flag aus** (Env-Overrides: Kopie, Redis `:6391`, `ChatLogBackfill__Enabled=false`, `Retention__Enforce=false`,
   `SevenTv__EventApi__Enabled=false`, `SevenTv__SlotReconcile__Enabled=false`): ≥ 10 min zählen lassen. Prüfen: heutige Zeilen tragen
   die `SlotId` des Paar-Seeds; neue Seeds nur für Paare ohne Slot, jeder mit Zeilen, höchstens einer je Paar, keiner zeilenlos;
   `EmoteSlots` sonst unverändert; Erhaltung diff-leer. **Gleichlauf** (E22): je Emote die Zuwächse der Kopie gegen die der Dev-DB im
   selben Intervall, Toleranz ein Flush-Fenster an den Rändern. **Testchat:** der nach K1 gespeicherte Name (letzter Eintrag, `hob44`)
   zählt auf dem Seed, den der erste Flush anlegt; der andere Name zählt nicht (heutiges Verhalten); `ome44 hob44 ome44` → +1.
5. **Phase B, Flag an:** Worker per SIGTERM (PID vorher mit `ps -o args=` prüfen; Final-Flush im Log), Neustart mit
   `SevenTv__SlotReconcile__Enabled=true`. Prüfen: Aufwärm-Log lässt Emotes mit Seed des aktiven Sets aus, Information-Zeile einmal je
   Kanal (AK 17, live nur über Log und DB — das Boot-Fenster ist zum Tippen zu kurz); nach dem ersten Tick Settle laut Plan-372: der
   Phase-A-Seed des Testemotes heißt `ome44`, autoritativ, mit den Phase-A-Zeilen, `hob44` neu bei 0. **Testchat** `ome44 hob44 ome44`
   → nach dem Flush zwei Zeilen, je +1, derselbe `EmoteId` (AK 14); `hob44` einzeln → nur dieser Slot (O1). Danach Set wiederherstellen.
6. **Abbau:** Worker SIGTERM (PID geprüft), `docker rm -f redis-slots373`, `DROP DATABASE emotepurge_slots373 WITH (FORCE)`, temporäre
   Header-Datei löschen (die Token-Datei des Betreibers bleibt). Protokoll mit SHA, Zahlen, Laufzeiten; keine Hostnamen, Passwörter,
   Tokens, keine Kanalnamen außer `olaf_olaf_son`/`handofblood`.

AK 15 (Kollision zweier Emotes), AK 16 (gelöschter Slot), E10, E11a und der Final-Flush sind live nicht sinnvoll herstellbar → Test.

## 8. Prod-Übergabe (Betreiber führt aus; Befehle für bash auf der Devbox, Passwort als Platzhalter)

**Vorher:** prüfen, welche Migrationen `main` seit dem auf Prod laufenden Stand mitbringt (der Plan kennt den Prod-Stand nicht) — der
`list`-Schritt muss **genau** `…_SwitchUsageStatsToSlotKey` als `(Pending)` zeigen; mehr → erst diese Migrationen durchsehen (Lehre aus
#391). `count(*)` und `NULL`-Zeilen auf `UsageStats` für die Fenstergröße aus §7.2.

1. Ankündigung außerhalb großer Streams; **worker und api stoppen** (Portainer); `pg_stat_activity` ohne `EmotePurge%`.
2. **Backup** `scripts/backup-postgres.sh`.
3. Vorbedingung: `SELECT count(*) FROM "ChatLogBackfillRuns" WHERE "Status" IN ('queued','running','paused')` = 0 (Prüfung (e) bricht
   sonst ab).
4. Tunnel in einer eigenen Shell: `ssh -N -L 15432:127.0.0.1:5433 vps`.
5. `dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection
   'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>;Command Timeout=600'` → genau M2 pending.
6. `dotnet ef database update <M2-Name> …` mit derselben `--connection` (Ziel ausdrücklich benannt). Abbruch bei > 10 min (Zielwert aus
   §7.2): `Down` (Schritt R) oder vorwärts.
7. `list` erneut: nichts pending. **Beide neuen Images gemeinsam hoch**, Flag bleibt aus.
8. Nachkontrolle ≤ 5 min: Worker-Log ohne Flush-Fehler, `usage.flushed`; `SELECT count(*) FROM "UsageStats" WHERE "Date" = current_date`
   wächst; Zahl neuer Seeds seit Fensterbeginn (`SeedUnsettled` mit `LastSyncedAt` danach) plausibel klein; Aufwärm-Zeilen wie vor dem
   Fenster.

**R — Rollback**, solange das Flag aus ist: worker und api stoppen, Backup, `dotnet ef database update AddEmoteSlots …` (M2-`Down`; der
Guard passiert, E3/E6a), Prä-#373-Images hoch. Die angelegten Seeds bleiben; die `NULL`-Zeilen der alten Images hängt ein späteres
M2-`Up` wieder an. Wirft der Guard doch (nur nach einer An-Phase denkbar): `CollapseUsageStatsSql` aus Operations.md in einer
Transaktion, dann `Down`. **Nie ein Prä-#373-Image gegen die M2-DB.**

**Docs im selben Commit wie der Vertragswechsel (Regel 3):** `docs/Operations.md` — neuer Unterabschnitt „Usage key switch (M2, #373)"
im Slot-Abschnitt (Fenster, Vorbedingung, Rollback, englisch) und die Flag-Tabelle („Off: … no writes to `EmoteSlots` except unsettled
base seeds created with a pair's first counted row or by a backfill enqueue"); DECISIONS siehe §9.

## 9. Commits und DECISIONS

Conventional Commits, englisch, Tests im Commit ihres Features: (0) Plan-Commits; (1) `refactor: name the match map and usage counter key
after slots`; (2) `test: give every usage row in the test fixtures an explicit slot`; (3) `feat(infrastructure): add the base slot rule
and the pending slot key`; (4) `feat(infrastructure): bind backfill snapshot entries to base slots and write their slot id` **plus
DECISIONS-Teil**; (5) `feat: switch usage counting to the slot key (M2)` — Migration, Modell, zweiphasiger Flush mit Rückgabevertrag,
Requeue, Map **plus DECISIONS-Teil (Variante (a), beide Abweichungen) und Operations**; (6) `fix(worker): retry the final usage flush
within the shutdown budget`; (7) `feat(infrastructure): merge colliding cells and carry backfill snapshots along when the settle moves
seed rows` **plus DECISIONS-Teil**; danach `fix:`/`test:` aus Review.

**DECISIONS:** datierter Absatz „Child 3 (usage key), 2026-10-12" im Entwurfseintrag #365 hinter „Child 2 …", englisch, wächst mit den
Commits. Inhalt: E1 und die Abweichungen E2/E3 als **Betreiberentscheidung 12.10.** samt Rollback-Satz; E4 (bestätigt), E5, E6, E6a
mit der Phasenbegründung; E7/E8 (Map je Zustand, Ausschluss jedes Emotes mit offenem Seed, keine virtuellen Einträge, Tracker =
Map-Eingabe); E9/E9a; E10 als Gurt mit Begründung; E11/E11a (Mindestschnitt, Snapshot-Umbindung, `FOR SHARE`, Grenze
`LiveRowConflict`); E12/E14; Z9–Z11. `Betrifft:` ergänzt um `UsageFlushWorker.cs`, `EmoteUsageCounter.cs`, `IEmoteUsageCounter.cs`,
`Worker.cs`, `RowLocks.cs`, das Hilfsmodul, die Migration.

## 10. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander — Builds teilen sich `obj/`)

Grün-Strategie: M2 macht das Emote-Konfliktziel des Flush ungültig, und vor M2 ist der Slot-Index nicht unique — Flush, Map und M2
gehen deshalb **zusammen** (T5). Was ohne Schema geht, kommt vorher: Umbenennung (T1), ausdrückliche `SlotId` in allen Testschreibern
(T2, die Spalte ist seit M1 nullbar), Hilfsmodul (T3) und die Backfill-Bindung (T4, `LiveRowConflict` bleibt bis M2 am Emote-Index).
Danach Final-Flush (T6) und die Settle-Änderungen (T7), die erst mit M2 Wirkung haben.

| # | Aufgabe | Modell | Aufwand | Input | Fertig, wenn |
|---|---|---|---|---|---|
| T1 | Umbenennung `NameToSlotId`, `UsageCounterKey.SlotId`, `Increment(slotId…)`; Werte noch Emote-Ids | sonnet | 0,5 T | 3.1 (E16) | drei Suiten grün, keine Verhaltensänderung, Commit (1) |
| T2 | Test-Helfer E14 und Umstellung aller ~300 EF-Testschreiber inkl. `RowLocksTests`/`PostgresLockProbe`-Kontexte auf ausdrückliche `SlotId` | sonnet | 1 T | 5.3 (E14), 6.4 | Suite grün **vor** M2; keine `UsageStat`-Anlage ohne `SlotId` mehr außer den bewusst `NULL` schreibenden Migrationstests; Flag-an-Tests im Ergebnis unverändert; Commit (2) |
| T3 | Hilfsmodul E5 (Lesen, Holen-oder-Anlegen), Platzhalter-Konvention, `RowLocks`-`FOR SHARE`-Einstieg | **opus** | 0,5 T | §2, 6.4, 6.5; `ArchivedSlotUpsert`, `RowLocks`, `EmoteSlotSeedSql` | Tests grün, Commit (3) |
| T4 | Enqueue-Bindung + `ReplaceBlockAsync` mit `SlotId` aus dem Snapshot hinter Emote-`FOR SHARE` | **opus** | 0,75 T | §4, E6, E11a (Sperre), Z11; Ergebnis T3 | AK 56 und 6.4 grün (vor M2), bestehende Backfill-Tests grün, Commit (4) |
| T5 | **M2 + Modell + zweiphasiger Flush mit Rückgabevertrag + Requeue im Worker + Map (beide Zustände, E7-Ausschluss) + Leser-Audit + Docs** | **opus** | 2 T | §3.1–3.3, §5, 6.1–6.3 (ohne E10/E11a), 6.5 (Teilergebnis), Übergaben #371/#372, Plan-372 §2.0/2.4; Ergebnis T1–T4 | ganze Suite grün, Probe-`migrations add` leer, Commit (5) mit DECISIONS und Operations |
| T6 | Final-Flush-Wiederholung (E9a) | sonnet | 0,25 T | 3.3 E9a, 6.5 Final-Flush | `UsageFlushWorkerTests` grün, Commit (6) |
| T7 | Settle: Zusammenführung (E10) und Snapshot-Umbindung (E11a) | **opus** | 0,75 T | 3.4, Plan-372 2.2/2.4, `EmoteSlotRollbackSql`; Ergebnis T4/T5 | 6.3 E10/E11a inkl. beider Startreihenfolgen grün, Suite grün, Commit (7) |
| T8 | Live-Verifikation | sonnet | 0,5 T + Betreiberzeit | §7, E21–E23 | Protokoll 7.0–7.6, M2-Laufzeiten für §8, Set wiederhergestellt, Abbau erledigt |
| T9 | Review, Gates, PR | **opus** (Review); Codex Sol | 0,5 T | Branch, §1.3, §8, §9, §11 | drei Suiten grün (Web nur, falls berührt — erwartet nicht), `dotnet format`, Coverage ≥ 80 % (erst committen, dann `coverage-local.mjs --backend-only`), Codex-Befund unverändert vorgelegt, PR mit AK-Tabelle (14–17, 44, 56; AK 1 „Rest in #379"), Kommentare auf #374–#376, #379 mit §11 |

Reihenfolge strikt T1 → … → T9; zusammen rund 6,75 Tage statt der 3 aus Spec §17 (Mehraufwand: Fixture-Umstellung, zweiphasiger Flush,
Settle-Änderungen, Final-Flush). T5 bleibt die größte Aufgabe; Teilen ginge nur über eine in sich unstimmige Zwischenstufe (Map und Flush
müssen dieselben Schlüssel sprechen, M2 und der Flush dasselbe Konfliktziel). Ob ein zusätzlicher Fable-Blick auf T5/T7 nötig ist
(Sperren, Migration, Zählsemantik — hochgehebelt), entscheidet der Orchestrator.

## 11. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#374 (EventAPI):** Bei Flag an zählt ein Emote nur unter einer Slot-Id und nur ohne offenen Seed des aktiven Sets; ein Push ohne Slot
  zählt bis zum nächsten REST-Tick nicht (E7). Der provisorische Push-Slot schließt das, sobald der Delta-Pfad ihn anlegt; danach den
  Map-Refresh nach dem Delta beibehalten. Bei Flag aus bleibt der Delta-Pfad unverändert und erzeugt keine Seeds.
- **#375 (Backfill-Mehrslot, M3):** ersetzt die Bindung aus E11 durch D28 vollständig; Snapshot-DTO, Worker-Map, Blockzähler und
  `ValidateBlock` gehen auf `SlotId`, die Snapshot-Nachschlagung in `ReplaceBlockAsync` entfällt — die `FOR SHARE`-Serialisierung mit dem
  Settle und die Snapshot-Umbindung im Settle (E11a) bleiben erhalten bzw. werden auf Mehrslot-Snapshots erweitert. Zeilenlose
  Enqueue-Seeds (`CreatedSlot = true`, über (b) wiederverwendet) brauchen eine Regel; M3 braucht zusätzlich eine für `NULL`-Snapshotzeilen
  terminaler Läufe (M1 E9). Enqueue und Flush teilen den Sperrvertrag E6.
- **#376 (Lese-API):** Flush-Seeds bei Flag aus sind aktive unsettled Seeds (Archivzustand vom Emote); Spec 5.1 zeigt unsettled Seeds in
  der aktiven Ansicht — mit Flag aus also je Paar einen. Zeilenlose Enqueue-Seeds und die von E11a geleerten Seeds dürfen keine Karte
  werden.
- **#379 (Betrieb):** M2 ist dann schon auf Prod (E3); das Fenster ist **M3 + Einschalten**. Die bei Flag aus entstandenen Seeds sind
  gewöhnliche unsettled Seeds mit Zeilen und müssen beim ersten An-Tick settlen (Nachkontrolle §11.2 6(a)); bis dahin zählen ihre Emotes
  nicht (E7), bei dauerhaft fehlenden v4-Daten auch länger — die Zeile „settle deferred" nennt sie. Prod hat keine zeilenlosen Seeds
  (Backfill aus). Der Prod-Kopie-Lauf deckt M3 und das Einschalten ab. **Rückbau der Aus-Zweige** (E26) umfasst den Flag-aus-Map-Bau,
  den Platzhalter-Schlüssel und Phase 2 des Flush samt Rückstellung; Z9/Z10 entfallen mit ihnen. Die WAL-Kosten-Entscheidung aus Kind 2
  bleibt Vorbedingung.

## 12. Rückfragen an den Betreiber

Keine offen. E4, R1 (→ E21), R2 (→ E22) und R3 (→ E23) sind am 12.10. entschieden und oben eingearbeitet.
