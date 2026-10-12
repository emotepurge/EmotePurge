# Plan #373 — Emote-Slots, Kind 3: Zählschlüssel auf den Slot (M2) und alle UsageStats-Schreiber

Erstellt am 2026-10-12 im Worktree `EmotePurge-slots` (Branch `feat/373-slot-usage-key`, Basis `origin/main` @ `5c9c5b2d`).
**Status: Entwurf, vor dem adversarialen Review.** Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md)
(§6.1, §8, §11.1 M2, §11.2, §12, §13 AK 14–17, 44, 56, §15, §16 Kind 3; D12, D15, D18, D19, D28, D31), Issue #373 mit den beiden
Übergabekommentaren aus #371 und #372 (bindend), [Plan-372](Plan-372-Emote-Slots-Reconcile.md) §2.0, §2.4, §2.2a, §4, §11, DECISIONS
#365 („Child 1", „Child 2"). Formatvorlage: Plan-372.

**Was dieser Plan trägt:** nur, was Spec und Übergaben offen lassen — Variante (a) des Betreibers, Entstehung der Basis-Seeds,
Flush- und Map-Verträge je Flag-Zustand, den Backfill-Mindestschnitt, M2 im Detail, Tests, Live-Prüfung, Prod-Übergabe, Aufgabenfolge.
Kein Code. D0–D33 und die Betreiberentscheidungen aus #371/#372 werden nicht wieder aufgemacht, außer den zwei unten bestätigten
Abweichungen.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **Betreiber 12.10.** = vom Betreiber entschieden.

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 3, Variante (a))

1. **M2 `SwitchUsageStatsToSlotKey`** samt Modell, Designer, Snapshot, Migrationstests (§5).
2. **Zählpfad je Slot, unbedingt:** `EmoteMatchSnapshot.NameToSlotId`, `UsageCounterKey(SlotId, EmoteSetId)`, Zähler, Flush mit
   `ON CONFLICT ("SlotId","EmoteSetId","Date")`, Kaltstart (§3).
3. **Map-Bau am Flag** `SevenTv:SlotReconcile:Enabled` (§3.2): an = gesettelte Slots (D15/AK 17), aus = heutige Namenssemantik mit
   dem Slot des Paars als Wert.
4. **Basis-Slot-Regel** und ihr Sperrvertrag (§2), geteilt von Flush und Backfill-Enqueue.
5. **Backfill-Basis** (§4): Enqueue bindet den einen Preview-Eintrag je Emote an einen Basisslot, Snapshot mit `SlotId`,
   `ReplaceBlockAsync` schreibt `SlotId` und `EmoteId`.
6. **Settle-Umbindung merge-sicher** nach M2 (E10).
7. Docs: Operations (M2-Kurzfenster, Flag-Tabelle), DECISIONS-Absatz „Child 3".

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 4 (#374) | Delta-Push legt provisorische Slots an; bis dahin zählt ein per Dispatch hinzugefügtes Emote bei Flag an erst ab dem nächsten REST-Tick (E7) |
| Kind 5 (#375) | M3, Enqueue ohne Dedup, D7 für Vorschauen, Enqueue-Settle, 409 `backfill_slots_unsettled`, Snapshot-DTO mit `SlotId`, Blockzähler/`ValidateBlock` je Slot, Regel für M1-Snapshotzeilen mit `SlotId NULL` |
| Kind 6 (#376) | Lese-API je Slot; Sichtbarkeit von Seeds in `/totals` |
| Kind 9 (#379) | Fenster M3 + Einschalten, Prod-Kopie-Lauf, Rückbau der Aus-Zweige (inkl. der Aus-Zweige dieses Kinds, E1) |

### 1.3 Abweichungen und Betreiberentscheidungen

- **[E1] Variante (a) — Betreiber 12.10.** Zähler, Flush, `ON CONFLICT`-Ziel und Backfill-Insert wechseln **unbedingt** auf den
  Slot-Schlüssel (Spec-Wortlaut). Nur der **Map-Bau** verzweigt am bestehenden Flag (§3.2). Grund: M1 läuft seit 11.10. auf Prod, das
  Flag bleibt dort bis #379 aus, Prod hat also nur unsettled Seeds; `main` wird weiter deployt, und nach dem Merge braucht jeder
  Main-Deploy M2 (`PendingMigrationGuard`). Prod zählt danach exakt wie heute, nur unter dem Seed-Slot; #379 settelt diese Seeds
  (Plan-372 „Ziel existiert", Slot-Test 21 „M2 vor oder nach dem Einschalten").
- **[E2] Abweichung 1 — Betreiber 12.10.:** Der Kind-2-Vertrag „Flag aus = keine Schreibung auf `EmoteSlots`" wird gelockert: bei Flag
  aus dürfen **unsettled Basis-Seeds** entstehen — präzisiert durch E4: nur im Flush (beim ersten Zählen eines Emotes in einem Set)
  und im Backfill-Enqueue. Sonst bleibt der Aus-Zustand aus Plan-372 §2.0 unverändert (Abgleich, Overlay, Leser, Bericht, Delta).
- **[E3] Abweichung 2 — Betreiber 12.10.:** D19 „alle drei Migrationen in einem Fenster" wird geteilt. M2 geht mit dem **nächsten
  Main-Deploy nach #373** in einem kleinen Fenster nach Prod (api und worker stoppen, Backup, M2 von Hand über den Tunnel, beide Images
  gemeinsam hoch — wie M1 am 11.10.); das §11.2-Fenster schrumpft auf M3 + Einschalten. Rollback bei Flag aus: höchstens ein Slot je
  Paar trägt Zeilen ⇒ der `Down`-Guard von M2 passiert ⇒ verlustfrei, danach Prä-#373-Images; oder Flag aus lassen und vorwärts
  reparieren. **Nie ein Prä-#373-Image gegen eine M2-Datenbank.**
- Die Spec wird nicht editiert; Abweichungen stehen im PR-Text und im DECISIONS-Absatz (§9).

## 2. Basis-Slots: Entstehung, Regel, Sperre

### 2.1 Das Problem „Seed ohne Zeilen hat kein Set"

`EmoteSlots` hat keine `EmoteSetId`; das Set eines Seeds ergibt sich nur aus seinen `UsageStats`-Zeilen (Spec 4.1, D12). Ein Seed, der
beim Map-Refresh für jedes aktive Emote ohne Paar-Slot angelegt würde, wäre bis zur ersten Zählung set-los. Drei Folgen, alle geprüft:
(1) **Wachstum:** ohne Wiederverwendung legte jeder Tick für jedes unbenutzte Emote einen neuen Seed an; (2) **Cross-Set-Leck:** mit
Wiederverwendung „irgendeines zeilenlosen Seeds" bekäme derselbe Seed nach einem Set-Wechsel innerhalb eines Flush-Fensters Zeilen
zweier Sets (gepufferte Zählwerte des alten Sets plus neue) — genau der Fehler, den D12/R4 geschlossen hat; das Settle (Plan-372 2.4)
schlüsselte ihn dann mit fremden Zeilen um; (3) **Waisen:** ein zeilenloser Seed wird nie gesettelt (Settle-Population = Seeds mit
Zeilen des aktiven Sets), nie gelesen (`ActiveNameEntries` und die An-Map lassen unsettled Seeds aus) und überlebt jeden Neustart.

### 2.2 Entscheidung

- **[E4] Seeds entstehen im Live-Pfad nur zusammen mit ihrer ersten Zeile, in der Flush-Transaktion.** Die Aus-Map (§3.2) liefert für
  ein Emote ohne Paar-Slot einen **Platzhalter-Schlüssel** „noch kein Slot für Emote X" statt einer Slot-Id; der Zähler behandelt ihn
  opak; der Flush löst ihn je `(EmoteId, EmoteSetId)` des Zählschlüssels auf und legt den Seed bei Bedarf **in derselben Transaktion
  wie die Zeile** an. Damit trägt jeder Live-Seed vom ersten Moment an Zeilen genau eines Sets (das Set steckt im Zählschlüssel), es
  gibt keine Waisen, und ein Set-Wechsel kann nichts vermischen. Verworfen: Batch beim Map-Refresh (2.1), lazy beim Matching (kein
  DB-Zugriff im Chat-Pfad). Der Platzhalter ist eine reine Infrastructure-Konvention (Präfix im String, intern, mit Erzeuger und
  Erkenner an einer Stelle); Worker und Core sehen nur Strings. Er entsteht nur bei Flag aus; die An-Map liefert ausschließlich
  Slot-Ids. **Bitte prüfen:** Der Betreiber hat „wird ein unsettled Seed angelegt" für den Map-Bau formuliert; E4 verschiebt den
  Zeitpunkt auf die erste Zählung, das sichtbare Ergebnis (Zählung unter dem Seed des Paars) ist gleich.
- **[E5] Basis-Slot-Regel** für das Paar `(EmoteId E, EmoteSetId X)`, ein internes statisches Hilfsmodul in Infrastructure (Muster
  `ArchivedSlotUpsert`), zwei Einstiege:
  - **Lesen (ohne Sperre, Map):** (a) der älteste Slot von E, der `UsageStats` von X trägt — D2-Ordnung `AddedAt` aufsteigend mit `NULL`
    zuletzt, dann `Name` ordinal, dann `Id`. **Ordnung im Speicher mit `StringComparer.Ordinal`**, kein `ORDER BY "Name"` in SQL
    (Kollationsfalle, siehe §6.4). Existenzprüfung über den Slot-Index `(SlotId, EmoteSetId, Date)` je Slot-Kandidat, nicht über einen
    Scan aller Zeilen des Paars (läuft jeden Resync-Tick je Kanal).
  - **Holen-oder-Anlegen (Flush, Enqueue):** (a); sonst (b) der älteste unsettled Seed von E, auf den eine Snapshot-Zeile eines
    Backfill-Laufs **desselben Sets X** zeigt (so ist auch ein zeilenloser Enqueue-Seed set-gebunden); sonst (c) ein **neuer unsettled
    Seed** mit M1-Semantik: `Name`, `IsArchived`, `ArchivedAt` 1:1 aus dem Emote, `AddedAt NULL`, `AddedAtFromRest false`,
    `SeedUnsettled true`, `LastSyncedAt` jetzt. Verlangt eine offene Transaktion und die Emote-Zeilen der angefragten Emotes als vom
    Aufrufer gesperrt (E6); liest (a)/(b) **nach** der Sperre; ein Statement je Schritt, mengenbasiert; Rückgabe Slot-Id je Paar und
    die Menge der angelegten.
- **[E6] Sperrvertrag:** Holen-oder-Anlegen läuft nur unter `RowLocks` in der Konventionsreihenfolge **Kanalzeilen (sortiert, ein
  Statement) → Emote-Zeilen (sortiert, ein Statement)**, Modus `FOR NO KEY UPDATE` (Plan-372 E1, Betreiber 12.10.). Einen Unique-Index
  für Seeds je Paar gibt es nicht (Spec 4.1); die Emote-Sperre plus Neu-Lesen ist die einzige Konkurrenzkontrolle zwischen Flush und
  Enqueue. Ein Kanal, der in der Kanal-Sperre fehlt (Purge hat committet), entfällt; seine Schlüssel werden verworfen wie gelöschte.
- **Invariante [E6a]:** höchstens ein unsettled Seed je Paar, und ein Seed trägt Zeilen nur eines Sets. M2 prüft beides (§5.1); die
  Regel E5 erhält beides. Folge für den Rollback: bei Flag aus trägt je Paar genau ein Slot Zeilen, der M2-`Down`-Guard passiert (E3).

### 2.3 Was mit den Seeds später passiert

- **Flag an (#379):** Flush-Seeds sind gewöhnliche unsettled Seeds mit Zeilen ihres Paars → Settle-Population des aktiven Sets
  (Plan-372 2.4) bzw. bleiben als Seeds fremder Sets unberührt (E7 dort). Keine Sonderbehandlung.
- **Enqueue-Seeds ohne Treffer** bleiben zeilenlos (Backfill ist auf Prod aus, nur lokal); über (b) werden sie bei einem Folge-Lauf
  desselben Sets wiederverwendet; endgültig behandelt sie Kind 5 (Hinweis §11). Retention fasst Slots nicht an (Kaskade nur über Emote).
- **M2-Prüfung „höchstens ein unsettled Seed je Paar"** zählt Paare über Zeilen; zeilenlose Seeds gehören zu keinem Paar und
  bestehen sie.

## 3. Live-Zählung

### 3.1 Benennung und Typen (Spec 6.1 Punkt 4, D15)

- **[E16]** `EmoteMatchSnapshot.NameToEmoteId` → `NameToSlotId`; `IEmoteMatchCache.ReplaceChannel(…, nameToSlotId)`;
  `UsageCounterKey(SlotId, EmoteSetId)`; `IEmoteUsageCounter.Increment(slotId, …)`. Reine Umbenennung, alle Konsumenten
  (`TwitchChatManager`, `Worker.cs` Kaltstart-Erkennung, `WarmMatchCacheIfEmptyAsync`, Map-Leser im Sync, Tests) in einer Aufgabe.
  `EmoteNameMatching` bleibt textgleich (O1: Dedup je Nachricht je Wert = je Slot).

### 3.2 Map-Bau (`RefreshMatchCacheAsync`)

- **[E7] Flag an (D15, AK 15, AK 17):** Eingabe = aktive, gesettelte Slots (nicht archiviert, nicht `SeedUnsettled`) **aktiver** Emotes
  des Kanals, `Name → SlotId`, `Coalesce` unverändert (O4, zuerst geladener gewinnt). **Keine virtuellen Einträge** (E14 aus Plan-372
  gilt nicht für die Map): ein Emote mit unsettled Seed des aktiven Sets zählt nicht (AK 17), ebenso ein aktives Emote ohne aktiven
  eigenen Slot (vor dem ersten An-Abgleich, nach einem Delta-Push bis #374, ohne v4-Daten) — es hat keine Slot-Id, unter der es zählen
  könnte, und ein Seed für das aktive Set würde es per D12 ohnehin sperren. **Tracker** liest genau die Map-Eingabe (distinct
  `(Name, EmoteId)`), damit seine Aussage „chat matching counts each name for one emote id only" stimmt; `DuplicateNames` im Status
  bleibt bei `ActiveNameEntries` (Inventarsicht mit E14, Plan-372). **Information-Zeile** (Spec 6.1 Punkt 1): Zahl der aktiven Emotes
  mit unsettled Seed des aktiven Sets, englisch, **nur bei Änderung je Kanal** (prozesslokal wie der Tracker), sonst spammt jeder Tick
  bei vertagtem Settle. E21 aus Plan-372 endet.
- **[E8] Flag aus:** heutige Semantik — aktive Emotes, `Emote.Name`, `Coalesce`, emote-weiter Tracker, Logtexte wie heute; **Wert** je
  Emote = Basis-Slot-Lesen (a) für `(Emote, ActiveEmoteSetId)`, sonst der Platzhalter (E4). Keine Schreibung im Map-Bau.
- **Kaltstart:** `WarmMatchCacheIfEmptyAsync` und die Boot-Reihenfolge lesen `NameToSlotId.Count`. Bei Flag an mit lauter Seeds ist die
  Map leer → der Kanal gilt als kalt und wird in Phase 2 zuerst gesynct (gewollt, AK 17); das Aufwärmen wiederholt sich dann je Aufruf
  (billig, ohne Logzeile bei 0).
- **Set-Wechsel bei Flag aus:** neuer Paar-Schlüssel → Seed des neuen Paars (aus M1/früherer Nutzung) oder Platzhalter; gepufferte
  Zählwerte des alten Sets tragen ihren alten Schlüssel und landen am alten Seed. **Umbenennung bei Flag aus:** der Flag-aus-Abgleich
  ändert `Emote.Name` (K1), der Paar-Slot bleibt derselbe → die Historie läuft am selben Seed weiter; dessen `Name` veraltet bis zum
  Settle (bedeutungslos, solange er unsettled ist).

### 3.3 Flush (`UsageStatFlushService.FlushAsync`)

- **[E9] Vertrag:**
  1. **Gültigkeitsfilter über `EmoteSlots`** (Spec 6.1 Punkt 3): echte Slot-Ids → `(SlotId, EmoteId, ChannelName)` aus Slot, Emote,
     Kanal. Fehlender Slot → Schlüssel verworfen, **eine** englische Warnzeile mit der Zahl, Rest geschrieben (AK 16). Platzhalter →
     Emote-Existenz; fehlend → verworfen, gleiche Zeile.
  2. **Ohne Platzhalter** (Normalfall, und bei Flag an immer): ein Upsert wie heute, `EmoteId` aus dem Slot, `SlotId` aus dem Schlüssel,
     Konfliktziel der Slot-Index; keine Sperren außer den FK-`KEY SHARE`.
  3. **Mit Platzhaltern:** eine Transaktion mit `SET LOCAL lock_timeout` (5 s), Sperren nach E6 für die Kanäle und Emotes der
     Platzhalter, Holen-oder-Anlegen (E5), dann **ein** Upsert für alle Schlüssel. Seed und erste Zeile committen zusammen oder gar
     nicht — ein fehlgeschlagener Flush hinterlässt keinen zeilenlosen Seed.
  4. **Zusammenfassen nach aufgelöstem Schlüssel** vor dem Upsert: ein Platzhalter und eine echte Slot-Id können im selben Batch auf
     denselben Slot zeigen (Map-Refresh dazwischen, oder ein Enqueue-Seed über (b)); ohne Summierung bräche das Upsert („cannot affect
     row a second time").
  5. **Requeue unverändert** (`UsageFlushWorker`): bei Fehler gehen die Schlüssel wie gezogen zurück, Platzhalter als Platzhalter,
     und werden beim nächsten Versuch neu aufgelöst; verworfene (gelöschte) Schlüssel werden nicht zurückgestellt.
     **Final-Flush in `StopAsync`** nimmt denselben Weg; `lock_timeout` begrenzt sein Warten.
  6. Rückgabe weiterhin die distinct Kanalnamen der geschriebenen Zeilen.
- **Z5 (Settle vs. Flush), Flag an:** der Flush schreibt nur gesettelte Slots, das Settle bewegt nur Zeilen von Seeds des aktiven Sets —
  disjunkt; trifft das Settle auf eine schon vorhandene Zielzeile, gilt E10. Ein 40P01 zwischen beiden (Zeilen-Updates in
  verschiedener Reihenfolge) erholt sich wie bisher (Sync E17, Flush Requeue).
- **[E10] Settle-Umbindung merge-sicher.** Am Code geprüft: die Übergabe „Settle-Umbinden bleibt nach M2 kollisionsfrei" stimmt nicht in
  jedem Zustand. Erreichbar: An-Phase legt einen eigenen Slot T an, der keine Zeilen des Paars bekommt; Aus-Phase zählt das Paar
  daraufhin über E5 (c) auf einen neuen Seed S (T trägt keine Zeilen); Wiedereinschalten am selben Tag → die An-Map zählt sofort auf T,
  der erste Abgleich settelt S per „Ziel existiert" auf T und verschiebt Ss heutige Zeile auf eine schon vorhandene `(T, Set, heute)` →
  Unique-Verletzung, jeder Tick scheitert (E17-Wiederholung, dann propagiert). Deshalb wird die Umbindung (Plan-372 2.2 Schritt 7) ab
  M2 zur **Zusammenführung**: Zellen ohne Gegenstück werden umgehängt, Zellen mit Gegenstück auf die Zielzeile addiert (alle drei
  Zähler) und die Seed-Zeile gelöscht; `Source` = Live, wenn eine der beiden Live ist (Regel des Kollaps-SQL, `EmoteSlotRollbackSql`).
  Gleiche Sperrfolge wie heute (`UsageStats` nach den Emote-Sperren). Deckt auch „mehr als ein Seed des aktiven Sets" ab.

### 3.4 Sperrzyklen, die dieses Kind neu einführt

| Zyklus | Auflösung / Erholung |
|---|---|
| **Z9** Flush-Platzhalterpfad (Kanal → Emote) vs. Flag-aus-Sync (Emote → Kanal, Plan-372 Z3 Aus-Zustand) | nicht ausrichtbar ohne den Aus-Sync zu ändern; tritt nur beim ersten Zählen eines Emotes in einem Set auf. Flush: Requeue; Sync: E17 (Deadlock-Erholung in beiden Zuständen, E27) |
| **Z10** Flush-Platzhalterpfad vs. Flag-aus-Delta (Emote-Updates in EF-Reihenfolge) | Flush Requeue; Delta: einmalige Wiederholung in beiden Zuständen (Plan-372 Z8) |
| **Z11** Enqueue-Emote-Sperre (Kanal → Nutzer → Emote) vs. Flag-aus-Sync | Enqueue: bestehende Deadlock-Wiederholung (3 Versuche); Sync E17 |
| ausgerichtet | Flush/Enqueue vs. Purge, Legacy-Bericht (an), Flag-an-Sync, Flag-an-Delta: alle Kanal → Emote |

## 4. Backfill-Mindestschnitt (Spec §16 Zeile 3 gegen Zeile 5)

- **[E11] Enqueue (`EnqueueUnderLocksAsync`):** Auswahl bleibt ein Preview-Eintrag je Emote (D36, last-wins) bis Kind 5. Nach Kanal-
  und Nutzersperre und dem Platzhalter-Upsert (Emote-Platzhalter wie heute) sperrt Enqueue die Emote-Zeilen aller Mitglieder über
  `RowLocks` (ein Statement, sortiert; D28 mit Modus E1) und bindet je Mitglied:
  1. **datierter Eintrag:** autoritativer Slot von E mit exakt diesem `AddedAt` (bei mehreren der namensgleiche) — D28 Schritt 1,
     set-unabhängig; bei Flag aus auf Prod nie vorhanden;
  2. sonst **Holen-oder-Anlegen** (E5) für `(E, Lauf-Set)`.
  Snapshot-Zeile mit nicht-null `SlotId`; `CreatedSlot = true` genau für in diesem Versuch angelegte Slots. Kein D7, kein Settle, kein
  409 (Kind 5). Die bestehende Deadlock-Wiederholung umfasst die neuen Schritte (ChangeTracker leer, alles neu).
- **`ReplaceBlockAsync`:** Aggregate bleiben je `EmoteId` (Worker, `ChatLogBackfillBlockCounter`, Snapshot-DTO **unverändert** bis Kind
  5 — eine Snapshot-Zeile je Emote, PK `(RunId, EmoteId)` bis M3). In der Transaktion nach dem Kanal-`KEY SHARE`: `SlotId` je `EmoteId`
  aus dem Snapshot des Laufs; **Existenzprüfung gegen `EmoteSlots`** (Slot vorhanden, gehört zu diesem `EmoteId`, Emote im Kanal);
  sonst Aggregat verworfen mit der bestehenden Warnzeile (Flush-Regel D25). Insert mit `SlotId` **und** `EmoteId`, weiter ohne
  `ON CONFLICT`; 23505 bleibt `LiveRowConflict`, jetzt über den Slot-Index — bei Flag aus (ein Slot je Paar) gleichwertig zu heute; bei
  Flag an erkennt er nur noch die Zelle desselben Slots (bekannte Grenze bis Kind 5; Import-Tage liegen vor dem Zählbeginn).
- **Läufe von vor #373:** nicht-terminale Läufe mit Snapshot-`SlotId NULL` (lokal möglich, M1 E9 und alte Enqueues) lässt M2 nicht zu
  (§5.1); `ReplaceBlockAsync` behandelt eine trotzdem gefundene `NULL` wie einen gelöschten Slot. Terminale Läufe bleiben unberührt
  (Kind 5/M3). Prod hat keine Läufe (Backfill aus; Vorbedingung im Fenster, §8).
- AK 56 ist damit erfüllbar: leere migrierte DB, unbekanntes Einzel-Emote → Platzhalter-Emote, ein Basisslot (Seed, `CreatedSlot`),
  Snapshot mit `SlotId`; echter `EnqueueAsync` → `ReplaceBlockAsync` schreibt die Zeile mit genau dieser `SlotId`.

## 5. M2 `SwitchUsageStatsToSlotKey`

### 5.1 `Up` (Reihenfolge bindend, Übergabe #371)

1. `SET LOCAL lock_timeout = '5s'`, dann `LOCK TABLE "UsageStats" IN ACCESS EXCLUSIVE MODE` (die Konstante läuft unter READ COMMITTED;
   das spätere `ALTER` sperrt zu spät).
2. `EmoteSlotSeedSql.AttachUnslottedUsageRowsSql` unverändert (hängt die `NULL`-Zeilen der alten Schreiber seit M1 an; auf Prod alle
   Zeilen seit 11.10.).
3. **Eigene Prüfung**, ein `DO`-Block mit `RAISE EXCEPTION` und sprechender englischer Meldung je Fall: (a) keine Zeile mit
   `SlotId NULL`; (b) keine Zeile, deren Slot zu einem anderen `EmoteId` gehört; (c) höchstens ein `SeedUnsettled`-Slot mit Zeilen je
   `(EmoteId, EmoteSetId)`; **[E12]** zusätzlich (d) kein unsettled Seed mit Zeilen zweier `EmoteSetId` (Invariante D12, E6a) und (e)
   kein Lauf im Status queued/running/paused mit einer Snapshot-Zeile `SlotId NULL` (Meldung nennt den Ausweg: Lauf abbrechen).
4. `SlotId SET NOT NULL`.
5. Index-Tausch: `IX_UsageStats_EmoteId_EmoteSetId_Date` neu als **nicht-unique** covering (`INCLUDE UseCount`, D18 — die Seed-Konstante
   und die Emote-Leser brauchen ihn), `IX_UsageStats_SlotId_EmoteSetId_Date` neu als **unique** covering. Da die Zeilen vorher je
   `(EmoteId, EmoteSetId, Date)` eindeutig waren und jeder Slot genau einem Emote gehört (Prüfung b), kann der Unique-Aufbau nicht an
   Dubletten scheitern.

### 5.2 `Down` (D31)

`lock_timeout` + `LOCK TABLE` wie oben; **Guard:** mehr als eine Zeile je `(EmoteId, EmoteSetId, Date)` → `RAISE EXCEPTION` mit Verweis
auf `EmoteSlotRollbackSql.CollapseUsageStatsSql`; sonst Slot-Index nicht-unique, Emote-Index unique, `DROP NOT NULL`. `SlotId`-Werte
bleiben; angelegte Seeds bleiben (harmlos; ein erneutes `Up` findet nichts anzuhängen). M1-`Down` ist danach wie bisher erreichbar.

### 5.3 Modell

- **[E14]** `UsageStat.SlotId` wird Pflicht (Muster wie `EmoteId` derselben Entität), Index-Konfiguration in `AppDbContext` getauscht,
  Kommentare dort neu; Designer und `AppDbContextModelSnapshot` per `migrations add` erzeugt, `Up`/`Down` von Hand ergänzt. Gate:
  Probe-`migrations add` danach erzeugt nichts.
- **Testdaten:** ~300 Stellen in Tests legen `UsageStat` per EF ohne `SlotId` an (`UsageStatQueryServiceTests` allein 215). Statt jede
  anzufassen: ein **Test-Interceptor** in `Fixtures/`, im Standard-`CreateDbContext` des `PostgresFixture` registriert, der vor dem
  Speichern jeder hinzugefügten `UsageStat` ohne `SlotId` den Slot gibt, den `AttachUnslottedUsageRowsSql` ihr gäbe (ältester Slot mit
  Zeilen des Paars, sonst ein neuer unsettled Seed je Paar, auch über mehrere Zeilen desselben Speicherns). Explizit gesetzte `SlotId`
  bleiben unberührt. Risiko benannt: er könnte einen künftigen EF-Schreiber ohne `SlotId` verdecken — Produktivcode legt heute keine
  `UsageStat` per EF an (nur Raw-SQL in Flush, Backfill, Settle); der Interceptor hat einen eigenen Test. Tests, die **absichtlich**
  `NULL`-Zeilen schreiben (Plan-372 Slot-Test 21, `SlotSyncKit` mit `slot = null`), sind auf der neuesten Schema-Stufe nicht mehr
  darstellbar und ziehen in die M2-Migrationstests (§6.1 Fall 5).
- **Leser-Audit (Teil von T4):** alle `UsageStats`-Leser (`UsageStatQueryService`, `VoteSessionQueryService`, `EmoteSetStatusService`,
  Retention, Identität, Harness) aggregieren per `GroupBy`/`Sum`; keiner darf Eindeutigkeit von `(EmoteId, EmoteSetId, Date)` annehmen
  (bei Flag an tragen zwei Slots eines Emotes je eine Zeile). Befund im PR.

## 6. Tests

Integrationstests `[Collection("Postgres")]`; Flag je Test über den Options-Typ. **[E15]** Kein neuer Leser von
`SlotReconcileOptions` (der Flush braucht das Flag nicht, der Platzhalter trägt seine Bedeutung; Enqueue bindet in beiden Zuständen
gleich) → `SlotReconcileRegistrationTests` bleibt unverändert. Entsteht doch ein Leser, kommt er dort dazu.

### 6.1 `SwitchUsageStatsToSlotKeyMigrationTests` (neu, Muster `AddEmoteSlotsMigrationTests`, eigene DB je Fall)

1 leere DB → Schema (NOT NULL, Slot-Index unique, Emote-Index nicht-unique, beide `INCLUDE UseCount`) · 2 M1 → alte Zeilen `NULL`
(bestehendes Paar und neues Paar) → `Up`: Zeilen am Seed bzw. neuer Seed, Summen je Paar gleich · 3 `Up` bricht ab, wenn eine andere
Sitzung `UsageStats` hält, und hinterlässt nichts · 4 Prüfungen (a)–(e) je ein roter Fall mit Meldung; terminaler Lauf mit `NULL`-Snapshot
besteht · 5 **Übergabe-Fixture:** M1 → alter Schreiber (Raw-Insert ohne `SlotId`) → Kind-2-Settle über `SyncChannelAsync` mit gefaktem
7TV-Client und Flag an → M2: Summen je `(EmoteId, EmoteSetId)` erhalten, Zeilen am gesettelten Slot, neue Paare mit eigenem Seed, der
nächste Sync settelt per „Ziel existiert" (ersetzt Plan-372 Slot-Test 21) · 6 `Down` ohne Dubletten → Schema zurück, Summen gleich ·
7 `Down` mit zwei Slot-Zeilen derselben `(EmoteId, Set, Date)` wirft; nach `CollapseUsageStatsSql` grün, Summen unverändert (Fixtures
`CollapseUsageStatsSql_*` gegen das echte `Down`) · 8 **Flag-aus-Welt verlustfrei:** M2 → Flush mit Platzhaltern (legt Seeds an) und
echten Schlüsseln → `Down` passiert den Guard, Summen gleich → `Up` erneut ohne Änderung · 9 Up-Down-Up mit Tausenden Paaren innerhalb
des Command-Timeouts (Fixture `SeedThousandsOfPairs`, `SeedSql_*`).

### 6.2 Flush (`UsageStatFlushServiceTests`, 17 angepasst +3 Spec, + Flag-aus-Fälle)

Spec: **AK 14** zwei Slots eines Emotes → zwei Zeilen `(SlotId, Set, heute)`, derselbe `EmoteId`, zweiter Flush addiert · **AK 16**
gelöschter Slot → verworfen, Warnzeile, Rest geschrieben, Kanal nicht gemeldet · **AK 17 (Flush-Hälfte)** nach In-place-Settle schreibt
der Flush auf denselben Slot, der die Seed-Historie trägt, die Seed-Zeilen anderer Sets bleiben. Neu: Platzhalter ohne Paar-Slot → genau
ein Seed mit genau der Zeile, M1-Felder · Platzhalter bei inzwischen vorhandenem Paar-Slot → kein neuer Seed · Platzhalter und echte Id
desselben Slots im Batch → eine Zeile, Summe · Platzhalter für zwei Sets desselben Emotes → zwei Seeds, je nur die Zeile ihres Sets ·
Platzhalter findet den zeilenlosen Enqueue-Seed desselben Sets (b), nicht den eines anderen Sets · Platzhalter für gelöschtes Emote →
verworfen · Fehler nach dem Anlegen (injiziert per Interceptor) → kein Seed, kein Teil-Commit · **Konkurrenz:** Flush-Platzhalter und
Enqueue für dasselbe Paar, beide Startreihenfolgen (`PostgresLockProbe`) → genau ein Seed, beide zeigen darauf · zwei Slots eines Emotes
am selben Tag verletzen nichts.

### 6.3 Sync und Map

- **`SevenTvSyncServiceSlotMapTests` (neu):** Flag an — AK 17 (Seeds des aktiven Sets → Map ohne X, Information-Zeile mit Zahl genau
  einmal; nach dem Settle `ome44` auf dem Slot mit der Seed-Historie, `hob44` auf dem neuen; ohne v4-Daten bleibt X draußen) · AK 15
  (Namenskollision zweier Emotes über Slots → ein Eintrag, Tracker warnt; zwei Aliase eines Emotes → keine Warnung) · keine virtuellen
  Einträge · Zeile nur bei Änderung. Flag aus — Map über `Emote.Name`, Wert = Seed des Paars bzw. Platzhalter; Set-Wechsel; Umbenennung
  behält den Slot; archivierte Emotes fehlen; nie ein Seed eines anderen Sets.
- **E10:** in `SevenTvSyncServiceSlotTests` — „Ziel existiert" mit kollidierender `(T, Set, D)`-Zeile → summiert, `Source`-Regel, Seed
  archiviert ohne Datum, kein Fehler; ohne Kollision unverändert umgehängt.
- **Bestand:** `SevenTvSyncServiceTests` (31 Map-Zusicherungen) und die übrigen Sync-Klassen: Umbenennung, Erwartungen auf Slot-Id bzw.
  Platzhalter.

### 6.4 Backfill und Hilfsmodul

- `ChatLogBackfillServiceTests*`: **AK 56** wie §4 · bestehendes Emote mit Seed des Lauf-Sets → gebunden, `CreatedSlot false` · datierter
  Eintrag mit autoritativem Slot gleichen Datums → dort gebunden · Folge-Lauf desselben Sets nutzt den zeilenlosen Seed wieder ·
  Snapshot-Slot fehlt → Aggregat verworfen · `LiveRowConflict` über den Slot-Index · handgebaute Snapshots mit `SlotId` (~10 angepasst).
- **Hilfsmodul E5** (Integration): D2-Ordnung inkl. Namens-Gleichstand ordinal **unter linguistischer Spaltenkollation** — nur nötig,
  falls die Umsetzung doch in SQL nach `Name` ordnet: dann Spalte per `UseLinguisticNameCollationAsync` auf ICU, Test muss ohne
  `COLLATE "C"` rot sein (Alpine-Postgres sortiert sonst bytewise und zeigt den Fehler nie) · (b) nur gleiches Set · verlangt offene
  Transaktion.
- **Test-Interceptor:** füllt fehlende `SlotId` mit Attach-Semantik, lässt gesetzte stehen.

### 6.5 Unit und Worker

`EmoteMatchCacheTests` (12), `EmoteNameMatchingTests` (24): nur Umbenennung · Platzhalter-Konvention: Erzeugen/Erkennen, echte Guid-Id
nie als Platzhalter erkannt · `EmoteUsageCounterTests` (13) umbenannt, +1: zwei Slots desselben Emotes in einer Nachricht → zwei
Schlüssel · `EmoteMatchCacheSwitchCountingTests` +1 (AK 17: leere Map zählt nichts, danach Map mit zwei Slots → +1 je Slot, O1) ·
`WorkerBootSequenceTests` +1 (Kanal mit leerer Aufwärm-Map gilt als kalt und wird zuerst gesynct). Api.Tests, Vitest, E2E: nichts.

## 7. Live-Verifikation (Regel 16) — Freigabe des Betreibers je Anlass

Randbedingungen wie Plan-372 §7: geteilte Dev-DB `emotepurge`, geteilter Api-/Worker-Container und Redis gehören einer anderen Sitzung;
**nie `docker compose`, nie etwas im Haupt-Checkout.** Ressourcen, die der Orchestrator mit anderen Sitzungen abstimmt:

| Ressource | Wert |
|---|---|
| DB | `emotepurge_slots373` im Container `emotepurge-dev-postgres` (Host `127.0.0.1:5432`), per `pg_dump \| psql` im Container |
| Redis | Wegwerf-Container `redis-slots373` auf `127.0.0.1:6391` |
| Prozesse | ein Worker per `dotnet run --project src/EmotePurge.Worker` aus dem Worktree (PID notieren); **keine** Api, kein Port `:5151` |
| Dateien | Scratchpad: CSVs, Protokoll, ggf. Header-Datei für das 7TV-Token (Modus 600) |

0. `df -h /` und `pg_database_size` der Dev-DB; Abbruch, wenn der Platz nicht das Doppelte trägt.
1. **Kopie und Vorher-Messung:** wie Plan-372 7.1/7.3 (Benutzer und Passwort aus dem Container, nie ausgeben, CR abschneiden). Messen:
   `count(*)` und `NULL`-Zeilen in `UsageStats`, Seeds, Paare; Erhaltungs-Population = Zeilen mit `Date < D0` je `(EmoteId, EmoteSetId)`
   (Zeilenzahl, drei Summen). In der Kopie nicht-terminale Backfill-Läufe abbrechen (Prüfung (e)).
2. **M2 auf der Kopie** per `dotnet ef database update` mit `--connection` (inkl. `Command Timeout=600`): Laufzeit notieren (→ Prod-
   Fenstergröße, hochgerechnet mit Prods `count(*)` und Prods `NULL`-Zeilen seit 11.10.); Prüfungen: keine `NULL`, Indizes laut
   `pg_indexes`, Erhaltung diff-leer. Dann `Down` (Laufzeit, Guard passiert, Erhaltung), dann erneut `Up`.
3. **Last begrenzen:** `IsBotActive = false` außer `olaf_olaf_son` und höchstens drei Kanälen, die laut Kopie am Messtag schon Zeilen
   haben (gerade oder kürzlich live — echter Chat); `handofblood` nur lesend, wenn es darunter ist.
4. **Phase A, Flag aus** (Env-Overrides: Kopie, Redis `:6391`, `ChatLogBackfill__Enabled=false`, `Retention__Enforce=false`,
   `SevenTv__EventApi__Enabled=false`, `SevenTv__SlotReconcile__Enabled=false`): ≥ 10 min zählen lassen. Prüfen: heutige Zeilen tragen
   die `SlotId` des Paar-Seeds; neue Seeds nur für Paare ohne Slot, jeder mit Zeilen, höchstens einer je Paar, keiner zeilenlos;
   `EmoteSlots` sonst unverändert (Prüfsumme ohne die neuen Seeds); Erhaltung diff-leer. **Gleichlauf (AK-14-Analogon unter Seeds):**
   je Emote die Zuwächse der Kopie gegen die der Dev-DB im selben Intervall (nur lesend, Rückfrage R2), Toleranz ein Flush-Fenster an
   den Rändern. Testchat in `olaf_olaf_son` (R1): jede gesendete Nennung erscheint am Seed des Paars.
5. **Phase B, Flag an:** Worker per SIGTERM (PID vorher mit `ps -o args=` prüfen; Final-Flush im Log), Neustart mit
   `SevenTv__SlotReconcile__Enabled=true`. Prüfen: Aufwärm-Log zeigt für Kanäle mit Seeds weniger/keine Namen (AK 17), Information-Zeile
   „not counted yet" einmal je Kanal; nach dem ersten Tick Settle laut Plan-372 und Map aus Slots. **Testkanal:** Ausgangszustand des
   aktiven Sets sichern; per 7TV-Schreib-API mit dem Token aus `~/.config/emotepurge-7tv-test-token` (nur als Header-Datei, nie
   ausgeben, nie `set -x`) Emote E unter zwei Aliasen a1/a2 hinzufügen; einen Tick warten (zwei Slots); Testchat „a1 a2 a1" → nach dem
   Flush zwei Zeilen, je +1, derselbe `EmoteId` (AK 14); a1 einzeln → nur Slot a1 (O1). Danach Set wiederherstellen.
6. **Abbau:** Worker SIGTERM (PID geprüft), `docker rm -f redis-slots373`, `DROP DATABASE emotepurge_slots373 WITH (FORCE)`, temporäre
   Header-Datei löschen (die Token-Datei des Betreibers bleibt). Protokoll mit SHA, Zahlen, Laufzeiten; keine Hostnamen, Passwörter,
   Tokens, keine Kanalnamen außer `olaf_olaf_son`/`handofblood`.

AK 15 (Kollision zweier Emotes) und AK 16 (gelöschter Slot) sind live nicht herstellbar → nur Test. AK 56 → Test (R3).

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
6. `dotnet ef database update <M2-Name> …` mit derselben `--connection` (Ziel ausdrücklich benannt, damit keine unerwartete Migration
   mitläuft). Abbruch bei > 10 min (Zielwert aus §7.2): `Down` (Schritt R) oder vorwärts.
7. `list` erneut: nichts pending. **Beide neuen Images gemeinsam hoch**, Flag bleibt aus.
8. Nachkontrolle ≤ 5 min: Worker-Log ohne Flush-Fehler, `usage.flushed`; `SELECT count(*) FROM "UsageStats" WHERE "Date" = current_date`
   wächst; Zahl neuer Seeds seit Fensterbeginn (`SeedUnsettled` mit `LastSyncedAt` danach) plausibel klein; Aufwärm-Zeilen wie vor dem
   Fenster.

**R — Rollback**, solange das Flag aus ist: worker und api stoppen, Backup, `dotnet ef database update AddEmoteSlots …` (M2-`Down`; der
Guard passiert, E3), Prä-#373-Images hoch. Die angelegten Seeds bleiben; die `NULL`-Zeilen der alten Images hängt ein späteres M2-`Up`
wieder an. Wirft der Guard doch (nur nach einer An-Phase denkbar): `CollapseUsageStatsSql` aus Operations.md in einer Transaktion, dann
`Down`. **Nie ein Prä-#373-Image gegen die M2-DB.**

**Docs im selben Commit wie der Vertragswechsel (Regel 3):** `docs/Operations.md` — neuer Unterabschnitt „Usage key switch (M2, #373)"
im Slot-Abschnitt (Fenster, Vorbedingung, Rollback, englisch) und die Flag-Tabelle („Off: … no writes to `EmoteSlots` except unsettled
base seeds created with a pair's first counted row or by a backfill enqueue"); DECISIONS siehe §9.

## 9. Commits und DECISIONS

Conventional Commits, englisch, Tests im Commit ihres Features: (0) `docs: add the plan for switching usage counting to slots` (diese
Datei); (1) `refactor: name the match map and usage counter key after slots` (reine Umbenennung); (2) `feat(infrastructure): add the
base slot rule and the pending slot key` (+ Test-Interceptor); (3) `feat(infrastructure): bind backfill snapshot entries to base slots
and write their slot id` **plus DECISIONS-Teil**; (4) `feat: switch usage counting to the slot key (M2)` — Migration, Modell, Flush, Map,
Fixtures **plus DECISIONS-Teil (Variante (a), beide Abweichungen) und Operations**; (5) `fix(infrastructure): merge colliding cells when
the settle moves seed rows` **plus DECISIONS-Teil**; danach `fix:`/`test:` aus Review.

**DECISIONS:** datierter Absatz „Child 3 (usage key), 2026-10-12" im Entwurfseintrag #365 hinter „Child 2 …", englisch, wächst mit den
Commits. Inhalt: E1 und die Abweichungen E2/E3 als **Betreiberentscheidung 12.10.** samt Rollback-Satz; E4–E6a (Platzhalter, Regel,
Sperre, Invariante); E7/E8 (Map je Zustand, keine virtuellen Einträge, Tracker = Map-Eingabe, Zeile nur bei Änderung); E9; E10 als
Korrektur der Kind-2-Übergabe; E11 (Mindestschnitt, Grenze `LiveRowConflict`); E12/E14; Z9–Z11. `Betrifft:` ergänzt um
`UsageFlushWorker.cs` (falls berührt), `EmoteUsageCounter.cs`, `IEmoteUsageCounter.cs`, `Worker.cs`, das Hilfsmodul, die Migration.

## 10. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander — Builds teilen sich `obj/`)

Grün-Strategie: M2 macht das Emote-Konfliktziel des Flush ungültig, und vor M2 ist der Slot-Index nicht unique — Flush, Map und M2
gehen deshalb **zusammen** (T4). Was ohne Schema geht, kommt vorher: Umbenennung (T1), Hilfsmodul (T2) und die Backfill-Bindung (T3,
additiv: `SlotId` ist seit M1 nullbar, `LiveRowConflict` bleibt bis M2 am Emote-Index).

| # | Aufgabe | Modell | Input | Fertig, wenn |
|---|---|---|---|---|
| T1 | Umbenennung `NameToSlotId`, `UsageCounterKey.SlotId`, `Increment(slotId…)`; Werte noch Emote-Ids | sonnet | 3.1 (E16) | drei Suiten grün, keine Verhaltensänderung, Commit (1) |
| T2 | Hilfsmodul E5 (Lesen, Holen-oder-Anlegen), Platzhalter-Konvention, Test-Interceptor (noch nicht verdrahtet) | **opus** | §2, 5.3, 6.4, 6.5; `ArchivedSlotUpsert`, `RowLocks`, `EmoteSlotSeedSql` (D2-Ordnung) | Tests 6.4/6.5 grün, Commit (2) |
| T3 | Enqueue-Bindung + `ReplaceBlockAsync` mit `SlotId` aus dem Snapshot | **opus** | §4, E6, Z11; Ergebnis T2 | AK 56 und 6.4 grün (vor M2), bestehende Backfill-Tests grün, Commit (3) |
| T4 | **M2 + Modell + Flush + Map (beide Zustände) + Interceptor verdrahten + Bestandsfixtures + Leser-Audit** | **opus** | §3, §5, 6.1–6.3, 6.5, Übergaben #371/#372, Plan-372 §2.0/2.4/§4; Ergebnis T1–T3 | 6.1, 6.2, 6.3 (ohne E10) grün, ganze Suite grün, Probe-`migrations add` leer, Commit (4) mit DECISIONS und Operations |
| T5 | E10 Settle-Zusammenführung | **opus** | 3.3 E10, Plan-372 2.2/2.4; `EmoteSlotRollbackSql` | E10-Test grün, Suite grün, Commit (5) |
| T6 | Live-Verifikation | sonnet | §7, Antworten auf R1–R3 | Protokoll 7.0–7.6, M2-Laufzeiten für §8, Set wiederhergestellt, Abbau erledigt |
| T7 | Review, Gates, PR | **opus** (Review); Codex Sol | Branch, §1.3, §8, §9, §11 | drei Suiten grün (Web nur, falls berührt — erwartet nicht), `dotnet format`, Coverage ≥ 80 % (erst committen, dann `coverage-local.mjs --backend-only`), Codex-Befund unverändert vorgelegt, PR mit AK-Tabelle (14–17, 44, 56; AK 1 „Rest in #379"), Kommentare auf #374–#376, #379 mit §11 |

Reihenfolge strikt T1 → T2 → T3 → T4 → T5 → T6 → T7. T4 ist die größte Aufgabe; ob ein zusätzlicher Fable-Blick auf T4/T5 nötig ist
(Sperren, Migration, Zählsemantik — hochgehebelt), entscheidet der Orchestrator.

## 11. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#374 (EventAPI):** Bei Flag an zählt ein Emote nur unter einer Slot-Id; ein Push ohne Slot zählt bis zum nächsten REST-Tick nicht
  (E7). Der provisorische Push-Slot schließt das, sobald der Delta-Pfad ihn anlegt; danach den Map-Refresh nach dem Delta beibehalten.
  Bei Flag aus bleibt der Delta-Pfad unverändert und erzeugt keine Seeds (Seeds entstehen nur im Flush).
- **#375 (Backfill-Mehrslot, M3):** ersetzt die Bindung aus E11 durch D28 vollständig; Snapshot-DTO, Worker-Map, Blockzähler und
  `ValidateBlock` gehen auf `SlotId`, die Snapshot-Nachschlagung in `ReplaceBlockAsync` entfällt. Zeilenlose Enqueue-Seeds (`CreatedSlot
  = true`, über (b) wiederverwendet) brauchen eine Regel (Settle bei datierter Vorschau oder Aufräumen nach 12.3); M3 braucht zusätzlich
  eine Regel für `NULL`-Snapshotzeilen terminaler Läufe (M1 E9). Enqueue und Flush teilen den Sperrvertrag E6.
- **#376 (Lese-API):** Flush-Seeds bei Flag aus sind aktive unsettled Seeds (Archivzustand vom Emote); Spec 5.1 zeigt unsettled Seeds
  in der aktiven Ansicht — mit Flag aus also je Paar einen. Zeilenlose Enqueue-Seeds dürfen keine Karte werden.
- **#379 (Betrieb):** M2 ist dann schon auf Prod (E3); das Fenster ist **M3 + Einschalten**. Die bei Flag aus entstandenen Seeds sind
  gewöhnliche unsettled Seeds mit Zeilen und müssen beim ersten An-Tick settlen (Nachkontrolle §11.2 6(a)); Prod hat keine zeilenlosen
  Seeds (Backfill aus). Der Prod-Kopie-Lauf deckt M3 und das Einschalten ab, für M2 genügt ab dann die Kind-3-Messung. **Rückbau der
  Aus-Zweige** (E26) umfasst den Flag-aus-Map-Bau, den Platzhalter-Schlüssel und das Holen-oder-Anlegen im Flush; Z9/Z10 entfallen mit
  ihnen. Die WAL-Kosten-Entscheidung aus Kind 2 bleibt Vorbedingung.

## 12. Rückfragen an den Betreiber

- **R1 — Wer schreibt den Testchat in `olaf_olaf_son`?** (a) **Empfohlen:** der Betreiber tippt die vorgegebenen Nachrichten von Hand mit
  dem eigenen Account (zählt als `UseCount`); (b) ein Bot-Account (`EmotePurgeBot`) mit User-Token per Device Code Flow (Scope zum
  Senden, Betreiber gibt einmal den Code auf `twitch.tv/activate` ein, Token-Inhaber per Helix geprüft, Token im Scratchpad mit 600) —
  zählt womöglich als `BotUseCount`, falls die Bot-Erkennung greift. Ohne Antwort: (a). In beiden Fällen prüft T6 vorher, dass der
  sendende Account nicht auf der Ausschlussliste für Chatter steht (`IExcludedChatterFilter`) — sonst zählt nichts.
- **R2 — Darf die Live-Prüfung die geteilte Dev-DB `emotepurge` nur lesend abfragen** (Zuwächse je Emote im selben Intervall als
  Gleichlauf-Beweis „zählt exakt wie heute")? Der Orchestrator stimmt den Zeitpunkt mit der anderen Sitzung ab. Ohne Erlaubnis entfällt
  der Gleichlauf-Vergleich; es bleibt der Testchat.
- **R3 — Backfill live in #373?** Empfehlung: nein — AK 56 ist ein Integrationstest auf leerer DB, das Backfill ist auf Prod bis #379
  aus, Kind 5 ersetzt die Bindung, und der Abnahmelauf von #379 fährt AK 22–27 und 56 live. Ein Live-Lauf hier bräuchte eine Api aus dem
  Worktree (Port ≠ `:5151`) samt Sitzungs-Cookie gegen die Kopie und ein Archiv mit Logs vor dem Zählbeginn des Testkanals.
