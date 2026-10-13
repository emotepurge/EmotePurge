# Plan #375 — Emote-Slots, Kind 5: Backfill je Slot und M3 `SwitchBackfillSnapshotToSlotKey`

Erstellt am 2026-10-13 im Worktree `EmotePurge-slots375` (Branch `feat/375-backfill-slots`, Basis `origin/main` @ `94a84782`).
**Status: Revision 2 (13.10.) — adversariales Review (Codex Sol: 3 × P2, 1 × P3) eingearbeitet; Betreiberentscheidungen vom
2026-10-13 eingetragen (Abschnitt 13); offen nur R6 (Freigabe des Live-Laufs, wird beim Start von T7 eingeholt).**
Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) (§4.3, §5.6, §6.2, §6.3, §6.5, §8,
§11.1 M3, §12.2, §12.3, §13 AK 22–27, 39, 44, 53, 56, §15, §16 Kind 5; D4, D7, D11 d, D12, D19, D28, D31, O2), die
[Backfill-Spec](../superpowers/specs/2026-10-09-chat-log-backfill-spec.md) (§3.1, §4.3, §4.4, D2, D3, D10, D25, D36, D41, OD-A, AC 26),
Issue #375 mit den vier Übergabekommentaren aus #371, #372, #373 und #374 (bindend), [Plan-372](Plan-372-Emote-Slots-Reconcile.md) §2.2a,
[Plan-373](Plan-373-Emote-Slots-Usage-Key.md) §2, §4, §11, [Plan-374](Plan-374-Emote-Slots-EventAPI-Deltas.md) §12, DECISIONS #365
(„Child 1" bis „Child 4", „Child 6"). Formatvorlage: Plan-373/Plan-374.

**Was dieser Plan trägt:** nur, was Spec und Übergaben offen lassen — den Flag-Zweig im Enqueue, die Bindungsregeln je Vorschau-Art,
die 409-Bedingungen, den Slot-Schlüssel in Worker und Block, die Regel für `NULL`-Snapshotzeilen in M3, den Rollback-Schritt 3b, Tests,
Live-Prüfung, Prod-Übergabe, Aufgabenfolge. Kein Code: Namen von Typen und Methoden stehen nur als Form. D0–D33 und die
Betreiberentscheidungen aus #371–#377 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **[A]** = Abweichung vom Spec-Wortlaut, Bestätigung durch den Betreiber
steht aus (Abschnitt 13). **[P]** = Präzisierung, die der Spec-Text offen lässt, ohne ihm zu widersprechen. **Sol-P*n*** = Befund des
adversarialen Reviews (Revision 1).

### Revision 2 (13.10.) — Betreiberentscheidungen

| Änderung | beantwortet |
|---|---|
| R1, R2, R3, R4, R5 und R7 (a) entschieden wie empfohlen; die Alternativen R7 (b) und „provisorischer Slot nach D11 (d)" entfallen; §9 und T8: M3 geht mit dem ersten Deploy nach #375 in einem eigenen kleinen Fenster nach Prod, **nicht** im gemeinsamen #379-Fenster; T4: Rebase-und-#403-Prüfung vor Beginn | Betreiber 2026-10-13 |
| #403 läuft parallel, eng geschnitten, Ziel: Merge vor T4 (12 A) | Betreiber 2026-10-13 |

### Revision 1 (13.10.) — was sich geändert hat

| Änderung | beantwortet |
|---|---|
| **E9 neu gefasst:** in `ReplaceBlockAsync` zuerst die Paar-Konsistenz `(SlotId, EmoteId)` gegen den Snapshot (Verstoß ⇒ `ArgumentException`, nichts geschrieben — auch für eine `EmoteId` eines anderen Kanals), erst danach die D25-Existenzprüfung. Die serverseitige Drift-Umschreibung („genau eine Snapshotzeile für das Emote") entfällt: sie bewies keine Seed→Ziel-Herkunft. Stattdessen meldet der Block bei Drift `SnapshotChanged` ohne Schreiben; der **Worker** liest den Snapshot neu, schreibt seine Aggregate über den stabilen Zeilenschlüssel `(EmoteId, Name)` um — den ändert die Umbindung nie —, prüft erneut und reicht denselben Block einmal neu ein, ohne neue Archivabfrage | Sol-P2-1 |
| **Settle nicht aktiver Sets nur über exaktes Datum:** `Settle` fällt heute auf `BindByName` zurück (`EmoteSlotReconcilePlanner.cs:235`), das einen **aktiven provisorischen** Slot gleichen Namens bindet und sein Datum autorisiert (`:317-335`) — für ein nicht aktives Set X würde das einen Push-/Restore-Slot des aktiven Sets Y mit Xs Datum und Xs Historie überschreiben. T4 trennt die Zielwahl von der Mutation; der REST-Pfad behält Datum-dann-Name unverändert, der Vorschau-Pfad nicht aktiver Sets wählt nur per exaktem autoritativem Datum, sonst In-place. Regressionstest mit Namensvetter samt Nutzung | Sol-P2-2 |
| **Undatierte Einträge nicht aktiver Vorschauen:** der Entwurf legte archivierte provisorische Slots mit `ArchivedAt = null` an, die REST bei einer späteren Aktivierung des Sets nie wiederfindet (`BindByDate` braucht ein autoritatives Datum, D7 `ArchivedAt`, `BindByName` einen aktiven Slot) ⇒ neue Slot-Id, Historie in der aktiven Ansicht verborgen (`UsageViewRows.cs:75-81`). Neu: 409, bis Daten da sind — Produktentscheidung, Rückfrage R7 | Sol-P2-3 |
| **Fixture-Umstellung in T1:** `SeedRunAsync` (`ChatLogBackfillServiceTests.cs:766-768`) legt Snapshotzeilen ohne `SlotId` an, `…Slots.cs:340-361` schreibt bewusst `NULL` und eine doppelte `SlotId` — beides ist unter M3 nicht mehr darstellbar. T1 stellt die Helfer auf echte Slots um und zieht die `NULL`-Fälle in die M3-Migrationstests auf ausdrücklichem M2-Schema; die pauschale Zusage „`…Slots.cs` unverändert" ist durch eine Liste der erhaltenen Paritätszusicherungen ersetzt (7.1a) | Sol-P3 |
| Abschnitt 12 A neu bewertet (Settle wird geteilt, Frontend-Spiegel der D2-Ordnung); Rückfragen R4 und R7 neu geschnitten; Aufwand 5,5 → 6 Tage | Folge |

## 0. Befund vor dem Plan

1. **Prod-Stand der Migrationen.** M1 `AddEmoteSlots` läuft seit 2026-10-11, M2 `20261012111509_SwitchUsageStatsToSlotKey` seit
   2026-10-12 ~15:13 UTC auf Prod (Epic-Tabelle #365, Plan-373 E3). Seit M2 hat `main` **keine** Migration bekommen
   (`git log origin/main -- src/EmotePurge.Infrastructure/Migrations/`: letzter Commit `a7050022` = M2; #374, #376, #377 ohne
   Migration). Nach diesem Kind ist **M3 die einzige offene Migration** des Epics. Das Issue (#375, „Migration and merge boundary") und
   Spec §11.2 Schritt 4 sprechen noch vom gemeinsamen Fenster aller drei Migrationen — überholt durch Plan-373 E3; M3 geht nach R1 (Betreiber 2026-10-13) mit dem ersten Deploy nach #375.
2. **Prod hat keine Backfill-Läufe und keine Snapshot-Zeilen** (`ChatLogBackfill:Enabled` dort nie an; Vorbedingung im M1- und
   M2-Fenster 0/0). Lokal gibt es abgeschlossene Läufe mit Snapshotzeilen `SlotId NULL` (M1 E9: Emote ohne Nutzung im Lauf-Set).
3. **Kind 3 hat geliefert** (Übergabe #373): Enqueue bindet **einen** Eintrag je Emote (D36 bleibt) an einen Basis-Slot
   (`BindSnapshotSlotsAsync`: exaktes Datum, sonst `BaseSlotRule.GetOrCreateAsync`), Snapshot mit `SlotId`/`CreatedSlot`;
   `ReplaceBlockAsync` sperrt Emote-Zeilen `FOR SHARE` (`RowLocks.LockEmotesForShareByIdsAsync`) und schlägt die `SlotId` je `EmoteId` im
   Snapshot nach; Worker, Blockzähler und DTOs sind noch je `EmoteId`. Der REST-Settle hängt Snapshotzeilen vom Seed aufs Ziel um (E11a,
   `SevenTvSyncService.MoveSeedSnapshotEntriesAsync`) und führt kollidierende Zellen zusammen (E10, `MoveSeedUsageAsync`).
4. **Die Bausteine der Bindung existieren** im reinen `EmoteSlotReconcilePlanner` (Schritte 3 `BindByDate`, 2 `Settle`, 4
   `ResumeArchivedSlots`, 5 `BindByName`, 6 neue Slots; alle privat) und in `ArchivedSlotUpsert` (autoritative archivierte Slots,
   race-sicher, bisher ohne Produktionsaufrufer).
5. **Widerspruch Spec ↔ Bestand:** Spec §8 („der Snapshot bleibt den Lauf lang gültig, kein Schreiber führt Slots zusammen") gilt seit
   Plan-373 E11a nicht mehr ganz — der REST-Settle hängt Snapshotzeilen eines **laufenden** Laufs um, wenn der Lauf bei Flag aus
   eingereiht wurde und das Flag danach angeht. Behandlung in E9 (Revision 1: Neulesen im Worker über den Zeilenschlüssel
   `(EmoteId, Name)`, nicht serverseitiges Raten).

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 5)

1. **M3** samt Modell, Designer, Snapshot, Migrationstests, `NULL`-Regel und `Down`-Guard (§5).
2. **Slot-Schlüssel in Worker und Block, flagunabhängig** (§3): Snapshot-DTO, Worker-Map `Name → SlotId`, Gate je Slot, Blockzähler je
   `(SlotId, Day)`, Aggregat mit `SlotId` und `EmoteId`, `ValidateBlock` je Slot mit Snapshot-Zugehörigkeit und Konsistenz.
3. **Enqueue bei Flag an** (§2): keine Dedup, Bindung je Vorschau-Art, Set-Seed-Settle nicht aktiver Sets, 409
   `backfill_slots_unsettled` (Code, Endpoint, `api-error.ts`, beide Locales).
4. **Settle-Schreibhelfer** geteilt von REST und Enqueue, Snapshot-Umbindung schlüsselsicher unter dem neuen PK (§4).
5. **Rollback-Schritt 3b** in Operations.md samt Konstante und Test (AK 39), Abschnitt „Backfill snapshot key (M3, #375)" (§6).
6. Docs: DECISIONS-Absatz „Child 5", Fußnoten der Backfill-Spec (Spec §8 letzte Zeile), i18n „Einträge" (R3, Betreiber 2026-10-13).

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 8 (#378) | Massenlöschung/Restore je Slot (web); berührt kein Backend dieses Kinds |
| #403 | eine Namensordnung für D13 und D2 (Abschnitt 12 A) |
| Kind 9 (#379) | Einschalten des Slot-Flags auf Prod, Abnahmelauf LAN (AK 22–26 live auf Prod-Kopie), Rückbau der Aus-Zweige — **inklusive des Aus-Zweigs dieses Kinds** (E1), Runbook |

### 1.3 Abweichungen und Planer-Entscheidungen auf oberster Ebene

- **[E1] Flag-Zweig im Enqueue.** `SevenTv:SlotReconcile:Enabled` aus ⇒ der Enqueue läuft **unverändert** den Kind-3-Pfad (D36-Auswahl
  „letzter Alias je ID", `BindSnapshotSlotsAsync`, eine Snapshotzeile je Emote). An ⇒ D28 (§2). Grund: bei Flag aus darf je
  `(Emote, Set)` höchstens ein Slot Zeilen tragen (Plan-373 E6a; davon hängen der verlustfreie M2-`Down` und die Leser im Emote-Modus ab).
  Mehrere datierte Slots je Emote im aktiven Set würden das bei Flag aus brechen, ohne dass Abgleich, Karte oder Map sie kennen. Damit
  wird `ChatLogBackfillService` ein neuer Leser von `SlotReconcileOptions` (Muster: optionaler Konstruktorparameter wie
  `UsageStatQueryService`) und kommt in `SlotReconcileRegistrationTests` (Plan-373 E15 hatte das ausdrücklich Kind 5 überlassen).
  Worker, Blockzähler und `ReplaceBlockAsync` lesen das Flag **nicht** — sie folgen der Form des Snapshots.
- **[E2] Der Enqueue schreibt Slot-Identität, nicht Mitgliedschaft.** Bei Flag an ändert er an bestehenden Slots nur `Name`, `AddedAt`,
  `AddedAtFromRest` (und beim Set-Seed-Settle die Seed-Marke und den Archivzustand des Seeds, E4); `IsArchived`/`ArchivedAt` bestehender
  Slots, die `Emote`-Zeile (außer dem unveränderten Platzhalter-Upsert) und die D13-Ableitung bleiben dem REST-Abgleich. Neue Slots
  entstehen **archiviert, nie drin** (`IsArchived = true`, `ArchivedAt = null`, Spec 4.1), auch für das aktive Set. Grund: der Enqueue
  hat keinen vollständigen Blick auf den Kanal (nur die Vorschau eines Sets, ggf. aus dem Cache), und jede Mitgliedschaftsschreibung
  außerhalb von REST erzeugt Zwischenzustände (aktiver Slot unter archiviertem Emote, Plan-374 S1), die Map, Delta und Leser gesondert
  behandeln müssten. Der nächste REST-Tick bindet die Slots per exaktem Datum (Schritt 3), un-archiviert sie und leitet `Emote.Name` ab.
- **Die Spec wird nur dort editiert, wo dieses Kind sie ablöst** (§8 Tabellenzeile Enqueue erhält einen Verweis auf DECISIONS „Child 5";
  AK 26 Satz zum undatierten Eintrag, R4/R7 vom Betreiber am 2026-10-13 bestätigt); alle [A] stehen im PR-Text und im DECISIONS-Absatz.

## 2. Enqueue bei Flag an (D28, Spec §8, AK 22, 26, 27, 53)

### 2.1 Ablauf und Sperren

Vor der Transaktion unverändert (Vorprüfungen, 7TV-Lesung, `Truncated`, `SetEmpty`). **[P]** Die Auswahl verliert die D36-Dedup; es
bleibt nur die Planer-Regel „ein `(Id, Name)`-Paar ist ein Eintrag, der erste zählt" (Plan-372 2.3 Schritt 1). In der Transaktion, in
dieser Reihenfolge (Konvention Kanal → Nutzer → Emote → Zeilen, Plan-373 E6, Z11 unverändert):

1. Kanal `FOR NO KEY UPDATE` per Id, Identitätsprüfung, Nutzer `FOR SHARE`, `IsBotActive`, Ausschluss, `AlreadyActive` — wie heute.
2. Platzhalter-Upsert (`ArchivedEmoteRowUpsert`, `CreatedRow`) — wie heute.
3. Emote-Zeilen aller Mitglieder `FOR NO KEY UPDATE` (`RowLocks.LockEmotesByIdsAsync`, ein Statement, sortiert) — wie heute.
4. **Hinter der Sperre neu lesen:** alle Slots der Mitglieder (vollständige Gruppen, auch archivierte und Seeds), die Seeds des
   Vorschau-Sets X und — wenn X nicht das aktive Set ist — nichts weiter; wenn X das aktive Set ist, die Seeds des aktiven Sets. „Seed
   von Set S" heißt genau `SetSeeds.SeedsOfSet(channelId, S)` (dieselbe Abfrage wie Settle, Map-Ausschluss und Leser; zeilenlose
   Enqueue-Seeds gehören zu keinem Set, E5). Das aktive Set wird **unter der Kanalsperre** gelesen (die Zeile aus Schritt 1), nicht aus
   der Vorab-Lesung.
5. Je Emote-Gruppe den reinen Bindungsplan rechnen (§2.2/2.3, Aufgabe T4). **Ein** Plan mit Ablehnung ⇒ ganzer Enqueue 409, Rollback,
   nichts geschrieben (2.4).
6. Schreiben: Slot-Änderungen (Umschlüsselung, Umbenennung, Datumsautorisierung, Seed-Settle), neue Slots (nur datiert, über
   `ArchivedSlotUpsert`; undatierte Einträge ohne Bindung lehnen ab, 2.4 c/b'), Settle-Zeilenumzug samt Snapshot-Umbindung älterer Läufe (§4),
   dann Lauf, Snapshot, Audit in einem `SaveChanges`, Commit.

**Wiederholung [E]:** die bestehende Drei-Versuche-Schleife um `EnqueueUnderLocksAsync` fängt zusätzlich den Slot-Schlüsselkonflikt
(`EmoteSlotKeys.IsSlotKeyConflict`) — gemeinsamer Zähler, Change-Tracker leeren, ab Schritt 1. Erreichbar nur über eine Umbenennung
gegen einen Schreiber, der die Emote-Sperre nicht nimmt (keiner bekannt); `ArchivedSlotUpsert` selbst ist per `ON CONFLICT DO NOTHING`
konfliktfrei. Der Plan-374-Hinweis „wer autoritative Slots umbenennt, prüft wie E12 gegen archivierte Gleichstands-Slots" ist hier per
Konstruktion erfüllt: der Plan sieht die **vollständige** Slotgruppe hinter der Sperre, und `BindByDate` benennt nur bei genau einem Slot
dieses Datums um — ein zweiter autoritativer Slot mit Datum und Zielnamen wäre Teil dieser Gruppe und bände per Namen.

**Neuer Sperrzyklus Z12 [E]:** Enqueue-Settle (Zeilen-`UPDATE`/`DELETE` an einem Seed von X) gegen Flush Phase 1 mit gepufferten
Zählwerten auf genau diesem Seed (nur nach einem Set-Wechsel innerhalb eines Flush-Fensters). Kein Warten (der Flush nimmt nur FK-`KEY
SHARE`), aber eine Zeile kann nach dem Settle am geleerten Seed landen — dieselbe Klasse wie Z5 (Plan-373 3.3), hingenommen und im
DECISIONS-Absatz genannt. Kein Zyklus mit einem laufenden Block: ein Enqueue setzt voraus, dass kein Lauf des Kanals aktiv ist.

### 2.2 Aktive Vorschau (X = aktives Set des Kanals unter der Sperre)

Je Gruppe *G* (Einträge einer 7TV-ID), Schritte und Reihenfolge wie REST §6.2, mit E2:

- **Vorbedingung:** hat das Emote einen unsettled Seed des aktiven Sets ⇒ Ablehnung (2.4 a). Kein Schritt 2 (Settle bleibt REST).
- **Schritt 3 Datums-Identität** (D4/D16): unverändert aus dem Planer; Bindung an aktive **und** archivierte autoritative Slots; bei genau
  einem Eintrag und genau einem Slot eines Datums wird umbenannt (AK 27). Der Archivzustand des gebundenen Slots bleibt (E2).
- **Schritt 4 D7** (erst nach allen Datumstreffern): Kandidaten und Wahl wie REST (gleicher Name, `ArchivedAt != null`, kein aktiver Slot
  gleichen Namens im Arbeitsstand, `ArchivedAt` absteigend, dann `Id`). Der Kandidat wird **nur umgeschlüsselt** (`AddedAt` = Datum des
  Eintrags, `AddedAtFromRest = true`), bleibt archiviert und behält `ArchivedAt`. Der nächste REST-Tick trifft ihn in Schritt 3 exakt und
  un-archiviert ihn; ein später eintreffender Restore-Bericht mit der alten Generation ist dann veraltet (D32/#376) — genau AK 53 (a) und
  (b).
- **Schritt 5 Namensbindung** (D11): an einen aktiven **provisorischen** Slot gleichen Namens, datierter Eintrag autorisiert dessen Datum
  — das deckt den Push-Slot aus #374 (Übergabe) und den Restore-Slot (AK 53 c). Ein undatierter Eintrag darf zusätzlich an einen aktiven
  autoritativen Slot gleichen Namens binden.
- **Schritt 6 neu:** datiert ⇒ autoritativer Slot über `ArchivedSlotUpsert` (archiviert, nie drin, `CreatedSlot = true` genau, wenn
  dieser Aufruf ihn angelegt hat). **Undatiert ohne Bindung ⇒ Ablehnung (2.4 c) [A]** — ein archivierter provisorischer Slot würde von
  REST nie gebunden (Schritt 5 bindet nur aktive), die importierte Historie läge verwaist neben dem Slot, den REST anlegt. REST legt für
  einen undatierten Live-Eintrag selbst einen aktiven provisorischen Slot an; danach bindet ein erneuter Enqueue per Namen. Abweichung
  vom Wortlaut D11 (d) für das aktive Set; für nicht aktive Sets gilt dasselbe aus demselben Grund (2.3, R7). **R4 entschieden: 409 (Betreiber 2026-10-13).**
- **Kein Schritt 7, keine Ableitung** (E2). Seeds anderer Sets unberührt (AK 22).

### 2.3 Nicht aktive Vorschau (X ≠ aktives Set)

- **Set-Seed-Settle (D12, §6.3, AK 26):** hat das Emote einen Seed von X: sind **alle** Einträge von *G* datiert, gilt die Settle-Regel
  des Planers mit **einer** Änderung der Zielsuche (Revision 1, Sol-P2-2): Kandidat = ältester Eintrag (Gleichstand nach dem
  Namensvergleich des Planers); „Ziel existiert" **nur** per exaktem autoritativem Datum (Schritt 3, aktiv oder archiviert) ⇒ Zeilen von X
  ziehen per Zusammenführung aufs Ziel, Snapshotzeilen älterer Läufe folgen, der Seed endet leer, settled, archiviert ohne Datum wie
  Plan-372 E12; sonst In-place-Umschlüsselung auf den ältesten Eintrag. **Kein Namens-Rückfall:** der REST-`Settle` sucht nach Schritt 3
  per `BindByName` (`EmoteSlotReconcilePlanner.cs:235`), und `BindByName` bindet einen **aktiven provisorischen** Slot gleichen Namens und
  autorisiert dessen Datum (`:317-335`). Aktiv heißt hier „im aktiven Set Y"; ein Push- oder Restore-Slot `A` aus Y bekäme Xs Datum, Xs
  Zeilen zögen darauf, und der nächste REST für Y fände seinen Eintrag `A(tY)` nicht mehr per Datum (der Slot trägt `tX`), legte einen
  neuen Slot an und archivierte den alten samt Ys Nutzung. T4 trennt deshalb die **Zielwahl** (REST: Datum, dann Name — unverändert;
  Vorschau nicht aktiv: nur Datum) von der gemeinsamen **Mutation** (In-place oder Umbindung samt Seed-Archivierung). Der REST-Pfad bleibt
  verhaltensgleich; seine Settle-Tests sind das Orakel. **Archivzustand eines in place gesettelten Seeds
  [E]:** `IsArchived = true` (der Seed steht nicht im aktiven Set — aktiv bliebe er ein gezählter, sichtbarer Slot mit fremdem Namen),
  `ArchivedAt` = bisheriger Wert, sonst jetzt (R2, Betreiber 2026-10-13). Ist ein Eintrag von *G* undatiert ⇒ Ablehnung (2.4 b), auch bei |G| = 1
  **[P]** (Spec 5.6 „seine Preview-Einträge sind nicht alle datiert" wörtlich; §6.3 erlaubt REST bei |G| = 1 ein provisorisches Settle,
  der Enqueue wartet stattdessen).
- **Datierte Einträge:** nur Schritt 3 (exaktes Datum, aktiv oder archiviert, gleicher Gleichstandsregel); sonst neuer Slot über
  `ArchivedSlotUpsert`. **Kein D7** und keine Namensbindung datierter Einträge (D28 „genaue Datumsbindung, bisheriges Archivverhalten").
- **Undatierte Einträge — R7 (a), Betreiber 2026-10-13 (Sol-P2-3):** Ablehnung (2.4 b'), solange nicht jeder Eintrag der Vorschau
  datiert ist. Der Entwurf legte nach D11 (d) einen archivierten provisorischen Slot (`ArchivedAt = null`) an; wird X später aktiv,
  findet REST ihn auf keinem Weg — Schritt 3 verlangt `AddedAtFromRest`, D7 verlangt `ArchivedAt != null` und einen datierten Eintrag,
  Schritt 5 nur aktive Slots — und legt eine neue Slot-Id an; die Importhistorie liegt auf einem archivierten eigenen Slot, den die
  aktive Ansicht nie zeigt (`UsageViewRows.cs:75-81`). Datierte Einträge haben dieses Problem nicht: ihre archivierten autoritativen Slots
  bindet REST per exaktem Datum. Der Backfill nutzt D11 (d) bei Flag an damit **gar nicht** ([A], bestätigt); der REST-Planer bleibt
  unverändert.
- Seeds des aktiven Sets und anderer Sets unberührt.

### 2.4 409 `backfill_slots_unsettled` (Spec 5.6, D12)

Ein neuer Wert `SlotsUnsettled` in `ChatLogBackfillEnqueueStatus`; der Endpoint antwortet `409` mit `ApiErrorCodes.BackfillSlotsUnsettled`
= `backfill_slots_unsettled`; Eintrag in `web/src/app/core/i18n/api-error.ts` und in beiden Locales unter `errors.api` (Text aus Spec 5.6,
englisch und deutsch; `api-error-locales.spec.ts` prüft automatisch, AK 44). Gründe, nur bei Flag an:

- (a) aktive Vorschau, ein Mitglied hat einen unsettled Seed des aktiven Sets (der nächste REST-Tick settelt; bei dauerhaft fehlenden
  v4-Daten länger — Spec §14 (c), hingenommen);
- (b) nicht aktive Vorschau, ein Mitglied hat einen Seed von X und mindestens einen undatierten Eintrag;
- (b') nicht aktive Vorschau, irgendein undatierter Eintrag (R7 (a), Betreiber 2026-10-13; umfasst (b));
- (c) aktive Vorschau, ein undatierter Eintrag ohne Namensbindung (2.2, [A]).

Eine Ablehnung schreibt nichts (auch kein Platzhalter, keine Slot-Änderung, kein Audit) und meldet nicht an den Worker. Eine
Information-Zeile (englisch, Kanal, Grund, Zahl der betroffenen Emotes, keine Namen) — das Settings-Tab zeigt nur den Code.

### 2.5 Snapshotzeilen

Eine Zeile je Eintrag mit `SlotId`, `EmoteId`, `SevenTvEmoteId`, `Name` (Alias im Set), `AddedToSetDay` (Tag **dieses** Eintrags),
`CreatedRow`, `CreatedSlot`. Zwei Einträge binden nie denselben Slot (der Planer schließt gebundene Slots aus; Prüfung als
Programmierfehler vor dem Speichern). **`CreatedRow` steht identisch auf allen Zeilen eines Emotes** (Übergabe #371, erste Variante: die
Herkunft gehört zur `Emote`-Zeile, nicht zum Slot; damit verliert das Kollaps-SQL beim Rollback nichts, egal welche Zeile es behält).
`CreatedSlot` ist wahr genau für einen in diesem Versuch angelegten Slot (`ArchivedSlotUpsert`-Ergebnis; provisorische Slots legt der Enqueue nach R4/R7 (a) nie an); ein
gesettelter Seed ist nie `CreatedSlot` (AK 39 iv). `emoteCount` im DTO = Snapshotzeilen (Spec 5.6, unverändert berechnet).

### 2.6 Flag aus — Parität

Der Kind-3-Pfad bleibt **Zeile für Zeile** derselbe; die einzige Änderung im Aus-Zustand ist die Schemaform (PK, M3) und das
slot-geschlüsselte Worker-DTO (§3), die bei einer Zeile je Emote dieselben Zellen schreiben. Gepinnt durch die Orakeltests aus T0 und die
ausdrücklich erhaltenen Kind-3-Zusicherungen aus 7.1a (Revision 1: nicht mehr „alle `…Slots.cs`-Tests unverändert", Sol-P3).

## 3. Worker, Blockzähler, `ReplaceBlockAsync` (flagunabhängig; AK 23, 24, 25)

- **[P] DTOs (Core):** `ChatLogBackfillSnapshotEmote(SlotId, EmoteId, Name, AddedToSetDay)` (Spec §8),
  `ChatLogBackfillAggregate(SlotId, EmoteId, Date, UseCount, BotUseCount, SharedChatUseCount)`. `GetSnapshotAsync` liefert je Zeile, im
  Speicher ordinal nach `EmoteId`, dann `SlotId` geordnet (D11 der Backfill-Spec, keine DB-Kollation).
- **Worker (K4):** `Coalesce(Name → SlotId)` aus dem Snapshot (Namen sind im Set eindeutig; die bestehende Info-Zeile über doppelte
  Aliase bleibt), `addedToSetDay` je `SlotId`, `emoteIdBySlotId` aus dem Snapshot. Der Snapshot wird weiter **einmal** je Lauf gelesen.
- **Blockzähler:** Gate je Slot, Zellen je `(SlotId, Day)`, `Aggregates()` geordnet nach `SlotId`, dann Tag, mit `EmoteId` aus der
  Zuordnung. `EmoteNameMatching` unverändert (O1: Dedup je Nachricht je Wert = je Slot ⇒ `ome44 hob44` zählt je Slot +1).
- **`ValidateBlock` (rein, vor der Transaktion):** Tag im Block, höchstens ein Aggregat je `(SlotId, Date)`; Verstoß ⇒
  `ArgumentException`, nichts geschrieben.
- **Unter der Sperre (Kanal `KEY SHARE` → Emote-Zeilen der Aggregate `FOR SHARE`, wie Kind 3) [E9, Revision 1 nach Sol-P2-1]:** die
  Snapshotzeilen des Laufs für die `SlotId`s **und** die `EmoteId`s der Aggregate und die Slots dieser `SlotId`s lesen. Die Prüfung läuft
  in zwei Stufen, **Paar-Konsistenz vor Existenz** — sonst würde ein Aggregat mit gültiger `SlotId`, aber der `EmoteId` eines anderen
  Kanals in der D25-Stufe still verworfen, und OD-A löschte trotzdem die Importzeilen des Blocks und schriebe Coverage und Fortschritt fort:
  1. **Paar-Stufe, für den ganzen Block vor jedem Schreiben:**
     - `(SlotId, EmoteId)` ist eine Snapshotzeile des Laufs ⇒ weiter zu Stufe 2.
     - `SlotId` steht im Snapshot unter einer **anderen** `EmoteId`, oder `EmoteId` hat keine Snapshotzeile im Lauf (u. a. ein Emote eines
       anderen Kanals) ⇒ `ArgumentException`, Rollback, nichts geschrieben (AK 24 „fremde `SlotId` / falscher `EmoteId`").
     - `SlotId` steht **nicht** im Snapshot, `EmoteId` aber schon (**Drift**) ⇒ neues Blockergebnis `SnapshotChanged` (vierter Fall neben
       `Committed`/`RunNotActive`/`LiveRowConflict`/`ChannelGone` im geschlossenen Ergebnistyp), Rollback, nichts geschrieben — weder
       Löschung noch Coverage noch Fortschritt. Der Server entscheidet hier **nicht**, wohin das Aggregat gehört: aus „das Emote hat genau
       eine Snapshotzeile" folgt keine Seed→Ziel-Herkunft (Revision 0 hätte jeden beliebigen Slot des Emotes umgeschrieben).
  2. **Existenz-Stufe (D25, unverändert):** Emote weg ⇒ verworfen mit Warnzeile; Slot weg ⇒ verworfen. Erst danach Löschung kanalweit
     (OD-A), Insert mit `SlotId` **und** `EmoteId` je Aggregat (nicht mehr über den Snapshot nachgeschlagen), Coverage und Fortschritt in
     derselben Transaktion. Die Nachschlagung „`SlotId` je `EmoteId`" aus Kind 3 entfällt; die `FOR SHARE`-Sperre bleibt (Serialisierung
     mit Settle und Enqueue-Settle).
- **Worker bei `SnapshotChanged` [E9]:** Herkunftsnachweis ist die **eigene Snapshotzeile** des Laufs: ihr Schlüssel `(RunId, EmoteId,
  Name)` ist stabil, die Umbindung des Settle ändert nur `SlotId`/`CreatedSlot` (`SevenTvSyncService.MoveSeedSnapshotEntriesAsync`, nach T3
  im Helfer). Der Worker liest den Snapshot neu, ordnet jede alte `SlotId` über ihre alte Zeile `(EmoteId, Name)` der neuen Zeile zu und
  schreibt die Aggregate um, aber **nur**, wenn sich die `SlotId` dieser Zeile tatsächlich geändert hat. Nicht zuordenbar (Zeile weg — die
  schlüsselsichere Umbindung aus §4 löscht sie bei vorhandener Zielzeile), unveränderte `SlotId` (dann war das Aggregat nie aus dem
  Snapshot — Programmierfehler) oder nach dem Umschreiben zwei Aggregate je `(SlotId, Date)` ⇒ Lauf scheitert mit `worker_error`. Sonst
  `ValidateBlock` erneut, derselbe Block **einmal** neu eingereicht (keine neue Archivabfrage, Bytes und Nachrichten wie gezählt), Map und
  Gate für die folgenden Blöcke aus dem neuen Snapshot. Ein zweites `SnapshotChanged` für denselben Block ⇒ `worker_error`. Erreichbar
  ist das nur, wenn das Slot-Flag während eines bei Flag aus eingereihten Laufs angeht (Befund 0.5); Operations nennt die Vorbedingung
  „Flag nur ohne aktiven Lauf umschalten" (§6), der Worker-Pfad ist der Gurt.
- **`LiveRowConflict` [P]:** die Zelle ist ab jetzt `(SlotId, EmoteSetId, Date)` — Spec 4.2 „eine Zelle ist live **oder** importiert"
  gilt je Slot; zwei Slots eines Emotes dürfen am selben Tag eine Live- und eine Importzeile haben. Die Kind-3-Grenze („erkennt nur die
  Zelle desselben Slots") ist damit der Vertrag, keine Lücke mehr. Kein zusätzlicher Emote-Check.
- **Flag-aus-Parität:** bei einer Snapshotzeile je Emote ist `SlotId ↔ EmoteId` bijektiv; Zähler, Validator und Insert erzeugen dieselben
  Zellen wie vor dem Umbau. Gepinnt durch den Orakeltest „Enqueue → Block → `UsageStats`" aus T0.

## 4. Settle-Schreibhelfer und Snapshot-Umbindung unter dem neuen PK

- **[E10] Extraktion:** `MoveSeedUsageAsync` (Zusammenführung kollidierender Zellen, E10 aus Plan-373) und
  `MoveSeedSnapshotEntriesAsync` (E11a) ziehen aus `SevenTvSyncService` in einen internen statischen Helfer in Infrastructure (Muster
  `ArchivedSlotUpsert`, offene Transaktion und Emote-Sperre als Vorbedingung des Aufrufers). REST und Enqueue-Settle rufen ihn; reine
  Verschiebung, Verhalten gleich.
- **Umbindung schlüsselsicher [E]:** mit PK `(RunId, SlotId)` kann das Umhängen Seed → Ziel kollidieren, wenn ein Lauf schon eine Zeile
  für das Ziel hat. Erreichbar ist das nach §2.4 a nicht (ein Lauf mit Zielzeile und Seedzeile desselben Emotes hätte zwei Zeilen je
  Emote, also Flag an, und bindet dann nie an einen Seed); der Helfer hängt trotzdem nur Zeilen um, deren Lauf noch keine Zeile fürs Ziel
  hat, und löscht die übrigen Seedzeilen (die Zielzeile behält ihr `CreatedSlot`). Ein Satz in DECISIONS, ein Test. Die Doku-Aussage „der
  Schlüssel `(RunId, EmoteId)` bleibt unberührt" im Sync-Code wird ersetzt.

## 5. M3 `SwitchBackfillSnapshotToSlotKey`

### 5.1 `Up`

1. `SET LOCAL lock_timeout = '5s'`, dann `LOCK TABLE "ChatLogBackfillRunEmotes" IN ACCESS EXCLUSIVE MODE` (vor jedem Lesen, wie M1/M2).
2. **Prüfung** (ein `DO`-Block, englische Meldung): kein Lauf im Status queued/running/paused mit einer Snapshotzeile `SlotId NULL`
   (M2-Prüfung (e) wiederholt — seit M2 kann keine entstehen, ein Fenster mit alten Images davor aber schon; Ausweg in der Meldung: Lauf
   abbrechen).
3. **[E11] `NULL`-Regel für abgeschlossene Läufe** (Übergabe #371 Punkt 1; R5, Betreiber 2026-10-13): eine Zeile `SlotId NULL` bekommt den Slot,
   der für `(EmoteId, Lauf-Set)` Zeilen trägt — der älteste in D2-Ordnung (`AddedAt` aufsteigend, `NULL` zuletzt, `Name COLLATE "C"`,
   `Id`), also dorthin, wo die Importzeilen des Laufs liegen; `CreatedSlot` bleibt `false`. Hat das Paar keinen Slot mit Zeilen, hat der
   Lauf für dieses Emote nie etwas geschrieben: die Zeile wird gelöscht. Kein Seed wird erfunden (D12, M1 E9). Preis: die
   `CreatedRow`-Herkunft solcher Platzhalter geht verloren — sie bleiben beim Cleanup stehen wie die Zeilen eines von Retention gelöschten
   Laufs (Operations.md Schritt 3 nennt diese Klasse schon). `RAISE NOTICE` mit beiden Zahlen. Prod: keine Läufe, kein Effekt.
4. Nachprüfung: keine `NULL`-Zeile, keine doppelte `(RunId, SlotId)` (kann nach 3 nicht entstehen — ein Slot gehört einem Emote, ein
   Lauf hat bisher eine Zeile je Emote; die Prüfung macht das zur Garantie).
5. `SlotId SET NOT NULL`; PK `(RunId, EmoteId)` fällt, PK `(RunId, SlotId)` entsteht; der Index `(RunId, SevenTvEmoteId)` bleibt.
   **[E]** kein zusätzlicher Index: alle Leser filtern über `RunId` (PK-Präfix), die `SlotId`-Suchen (Basis-Slot-Regel (b),
   Snapshot-Umbindung) laufen auf einer Tabelle, die nur Snapshots lebender Läufe hält.

### 5.2 `Down` (D31)

Gleiche Sperre; **Guard:** mehr als eine Zeile je `(RunId, EmoteId)` ⇒ `RAISE EXCEPTION` mit Verweis auf
`EmoteSlotRollbackSql.CollapseBackfillSnapshotSql`; sonst PK zurück auf `(RunId, EmoteId)`, `DROP NOT NULL`. Die per 5.1.3 gelöschten
Zeilen kommen nicht zurück (dokumentiert). Das Kollaps-SQL behält die Zeile des ältesten Slots; weil `CreatedRow` auf allen Zeilen eines
Emotes gleich steht (2.5), verliert es keine Platzhalter-Herkunft (Übergabe #371 Punkt 3).

### 5.3 Modell, Altimages

- `ChatLogBackfillRunEmote.SlotId` wird Pflicht, Schlüssel in `AppDbContext` getauscht; Designer und Snapshot per `migrations add`,
  `Up`/`Down` von Hand. Gate: Probe-`migrations add` danach leer.
- **Altimages gegen eine M3-Datenbank laufen** (Kind 3 bis vor #375): sie schreiben eine Zeile je Emote mit nicht-leerer `SlotId`, das
  passt zu NOT NULL und zum neuen PK; `PendingMigrationGuard` prüft nur ausstehende, nicht unbekannte Migrationen. Ein **neues** Image
  gegen eine Datenbank ohne M3 startet nicht (Guard). Damit ist M3 vorwärts wie rückwärts deploy-sicher; Rollback des Codes braucht
  kein `Down`, solange nur Läufe mit einer Zeile je Emote existieren (Flag aus).

## 6. Rollback-Schritt 3b und Operations (Spec §12.3, AK 39)

- **[E12]** Der Text aus Spec §12.3 wird eine Konstante neben `EmoteSlotRollbackSql` (gleiches Muster: eine Quelle für Test und
  Operations-Text, „keep both texts identical"). Prädikate: `IsArchived AND ArchivedAt IS NULL`, eine Snapshotzeile mit `CreatedSlot`
  zeigt darauf, **keine Live-Zeile** (`Source = 0`) — die Übergabe #373 („`CreatedSlot` heißt nicht nur Importzeilen", Flush Phase 2
  kann einen zeilenlosen Enqueue-Seed über Regel (b) wiederverwenden, Kaskade `UsageStats → EmoteSlots`) ist damit abgedeckt.
- Operations.md, Abschnitt „Chat-log backfill → Rolling back": Schritt **3b** nach dem Platzhalter-Cleanup und vor Schritt 4 (Löschen der
  Importzeilen; die Kaskade nähme sie sonst über den Slot mit, was harmlos, aber unübersichtlich ist), mit dem Satz aus §12.3 („a seed
  the enqueue settled is not `CreatedSlot` and stays"). Reihenfolge-Begründung: 3b braucht wie Schritt 3 die Snapshotzeilen.
- Operations.md, Slot-Abschnitt: neuer Unterabschnitt **„Backfill snapshot key (M3, #375)"** (englisch): was M3 tut, die `NULL`-Regel,
  Vorbedingung, Fenster (eigenes kleines Fenster mit dem ersten Deploy nach #375, R1), Rollback; die Flag-Tabelle ergänzt „Off: … the backfill enqueue binds one entry per emote; On: one
  snapshot row per set entry, refuses with `backfill_slots_unsettled` while …". **Neu dort [E]:** „Switch the slot flag only while no
  backfill run is queued, running or paused" (der Worker-Pfad `SnapshotChanged` aus E9 ist der Gurt, nicht der Normalweg).

## 7. Tests

Integrationstests `[Collection("Postgres")]`, Flag je Test über den Options-Typ. Zahlen sind Richtwerte (Spec §15: ~10 angepasst, +10).

### 7.1 Orakel (T0, vor jedem Umbau, auf dem Stand `94a84782`)

Charakterisierung des Aus-Pfads über **DB-Wirkungen**, nicht über DTOs (die ändern sich in T2): (1) Enqueue einer Vorschau mit
`[X ome44 (alt), X hob44 (neu)]` bei Flag aus → eine Snapshotzeile, Name des **letzten** Eintrags, Basis-Slot des Paars,
`CreatedSlot`/`CreatedRow` wie heute; (2) echter Durchlauf Enqueue → `ClaimNext` → `ReplaceBlockAsync` mit zwei Aggregaten → exakt die
`UsageStats`-Zeilen (Slot, Emote, Set, Tag, drei Zähler, `Source`) von heute; (3) nicht aktive Vorschau mit Seed des Sets bei Flag aus →
an den Seed gebunden, keine Slot-Änderung. Diese Tests bleiben nach jeder Aufgabe unverändert grün (nur Konstruktion der Aggregate passt
sich in T2 an).

### 7.1a Fixture-Umstellung in T1 (Revision 1, Sol-P3)

M3 macht zwei heutige Testformen undarstellbar: `SeedRunAsync` (`ChatLogBackfillServiceTests.cs:766-768`) legt Snapshotzeilen mit
erfundenen `EmoteId`s und **ohne** `SlotId` an, und `ReplaceBlock_DropsAggregatesWhoseSnapshotSlotIsMissing_NullOrAnotherEmotes`
(`…Slots.cs:340-370`) setzt eine Zeile auf `NULL` und eine zweite auf die `SlotId` einer anderen Zeile desselben Laufs (PK-Verstoß unter
`(RunId, SlotId)`). T1 stellt deshalb **im selben Commit wie M3** um: `SeedRunAsync` legt für Läufe mit Snapshot echte Emotes und Slots
an (über den bestehenden Test-Helfer aus Plan-373 E14) oder einen Lauf ohne Snapshot, wo der Test keinen braucht; der `NULL`-Fall zieht in
die M3-Migrationstests auf **ausdrücklichem M2-Schema** (7.2 Fall 2/3); der Fall „Slot eines anderen Emotes" wird in T2 zum
Paar-Stufen-Fall (`ArgumentException`, 7.4) statt eines stillen Verwerfens. **Erhaltene Paritätszusicherungen** (unverändert grün, als
Liste im PR): die Kind-3-Bindungstests im Aus-Zustand — dated exact match, Basis-Seed des Lauf-Sets mit `CreatedSlot = false`,
Wiederverwendung des zeilenlosen Seeds, verwaister Seed nach Regel (b), AK 56 —, die E11a-Tests „Settle hängt Snapshot um" in beiden
Startreihenfolgen, „gelöschter Slot ⇒ Aggregat verworfen" und `LiveRowConflict` über den Slot-Index. Alles andere in `…Slots.cs`, das
die Snapshot-Nachschlagung je `EmoteId` prüft, wird in T2 bewusst auf die Paar-Stufe umgeschrieben.

### 7.2 M3 (`SwitchBackfillSnapshotToSlotKeyMigrationTests`, Muster `SwitchUsageStatsToSlotKeyMigrationTests`, eigene DB je Fall)

1 leere DB → Schema (NOT NULL, PK `(RunId, SlotId)`, Index bleibt) · 2 M2-Stand mit abgeschlossenem Lauf: Zeile mit `NULL` und Slot mit
Zeilen des Lauf-Sets → angehängt; ohne solchen Slot → gelöscht; Zahlen in der Notice · 3 aktiver Lauf mit `NULL` → `Up` bricht ab, nichts
geändert · 4 `Up` bricht bei gehaltener Tabelle per `lock_timeout` ab · 5 `Down` ohne Dubletten → PK zurück · 6 `Down` mit zwei
Slotzeilen eines Emotes wirft; nach `CollapseBackfillSnapshotSql` grün, die Überlebende trägt `CreatedRow = true`, wenn das Emote vom Lauf
angelegt war (Übergabe #371: der bisher simulierte M3-Zustand in `AddEmoteSlotsMigrationTests` wird gegen das echte M3/M3-`Down`
wiederholt; die Simulation darf danach entfallen) · 7 Altimage-Form: nach M3 schreibt ein Kind-3-artiger Insert (eine Zeile je Emote)
ohne Fehler.

### 7.3 Enqueue (`ChatLogBackfillServiceTests.Slots.cs` und eine neue Teilklasse `…MultiSlot`, Flag an, sofern nicht anders)

- **AK 22** aktives Set ohne Treffer → eine `Emote`-Zeile, zwei autoritative archivierte Slots, zwei Snapshotzeilen mit eigenem
  `AddedToSetDay`, `CreatedSlot` beide wahr; mit vorhandenen autoritativen Slots → per Datum getroffen, `CreatedSlot` falsch; nicht
  aktives Set → archiviert; ein unsettled Seed von Set B samt Zeilen unberührt.
- **AK 26** Seed von nicht aktivem A, Vorschau `[ome44 (alt), hob44]` datiert → Seed heißt `ome44`, autoritativ, archiviert (E4), Marke
  weg, nur As Zeilen; `hob44` neuer archivierter Slot; „Ziel existiert"-Variante (exaktes Datum) → Zusammenführung, Seed leer, Snapshot
  eines älteren Laufs umgehängt; ein undatierter Eintrag → 409, nichts geschrieben (Platzhalter, Slots, Lauf, Audit), Marke bleibt; Seed
  des aktiven Sets → 409.
- **Sol-P2-2 (Regression):** Seed von nicht aktivem X, Vorschau `[A(tX)]`; im aktiven Set Y ein **aktiver provisorischer** Slot `A`
  (Push) mit Live-Zeilen → kein Namens-Ziel: der Seed wird in place auf `A(tX)` umgeschlüsselt und archiviert; Ys Slot behält `Name`,
  `AddedAt`, `AddedAtFromRest = false`, `IsArchived = false` und seine Zeilen; der nächste REST für Y (Flag an) bindet ihn per Namen und
  legt keinen neuen Slot an. Gegenprobe REST-Settle (Kind 2): dieselbe Lage im **aktiven** Set bindet weiter per Namen (Orakel
  unverändert).
- **AK 27** Konkurrenz Enqueue ↔ REST (Flag an) über `PostgresLockProbe`, beide Startreihenfolgen: neuer Eintrag A(t) einmal, nach
  Umbenennung C(t) → genau ein Slot, umbenannt, Snapshot und REST-Bindung zeigen darauf; echte Gruppe `[A(t), C(t)]` → zwei Slots.
- **AK 53** archiviertes S1(A,t1) mit Nutzung, kein aktives A: (a) Enqueue `[A(t3)]` vor REST → S1 umgeschlüsselt, archiviert,
  Snapshot auf S1, folgender REST un-archiviert S1, kein S2; (b) Enqueue, dann exakter Restore-Bericht (t1) → No-op, S1 bleibt; (c)
  Restore-Bericht, dann Enqueue → S1 per Namen gebunden, Datum t3 autorisiert. In allen: keine zweite Slot-Id, Historie auf S1.
- **Push-Slot (Übergabe #374):** aktiver provisorischer Push-Slot A, Vorschau `[A(t)]` → gebunden, Datum autorisiert, kein neuer Slot.
- **Undatiert:** aktive Vorschau, undatierter Eintrag mit aktivem gleichnamigem Slot → gebunden ohne Datumsänderung; ohne → 409 (c);
  nicht aktive Vorschau mit undatiertem Eintrag → 409 (b'), nichts geschrieben (Platzhalter, Slots, Lauf, Audit).
- **2.5** `CreatedRow` gleich auf allen Zeilen eines neu angelegten Emotes; zwei Einträge nie auf einem Slot.
- **AK 56** in beiden Flag-Zuständen grün (aus: Basis-Slot; an: archivierter autoritativer Slot).
- **Flag aus** (zusätzlich zu 7.1): Vorschau mit Seed des aktiven Sets → **kein** 409, Kind-3-Bindung.
- **Wiederholung:** injizierter Slot-Schlüsselkonflikt im ersten Versuch → zweiter Versuch gelingt.

### 7.4 Block und Worker

- `ChatLogBackfillServiceTests.Worker.cs`: **AK 24** zwei Aliase am selben Tag → zwei Zeilen, exakte Zähler je Kategorie; fremde `SlotId`
  bzw. falscher `EmoteId` → `ArgumentException` · **Sol-P2-1:** (i) gültige Snapshot-`SlotId` mit der `EmoteId` eines Emotes eines
  **anderen Kanals** → `ArgumentException`; (ii) Slot desselben Emotes, der nie im Snapshot stand → `SnapshotChanged`; (iii) E9-Drift: Lauf
  bei Flag aus eingereiht, REST-Settle (Flag an) „Ziel existiert" hängt den Snapshot um, Block mit Aggregat auf dem alten Seed →
  `SnapshotChanged`. In allen drei Fällen zugesichert: `UsageStats` (auch die Importzeilen des Blocks aus einem früheren Lauf), Coverage,
  `WeeksDone`, Bytes/Nachrichten **unverändert** · **AK 25** OD-A-Fixture
  mit Importzeilen beider Slots, Re-Run eines anderen Sets → alle `Source = 1` des Kanals ersetzt, Coverage neues Set, `WeeksDone` +1,
  eine Transaktion, Summe je `(EmoteId, Day)` ≥ alt · Live-Zeile auf Slot A und Import auf Slot B am selben Tag → kein Konflikt; Live auf
  derselben Zelle → `LiveRowConflict` · ~10 bestehende Tests: Aggregate und Snapshots mit `SlotId`.
- `ChatLogBackfillBlockCounterTests` (+3, AK 23): Gate je Slot (`ome44` vor dem 01.09. zählt, `hob44` nicht); `ome44 hob44` → je Slot +1;
  Aggregate je `(SlotId, Day)` mit `EmoteId`.
- `ChatLogBackfillWorkerTests` (+1, AK 23–24): Snapshot mit zwei Slots eines Emotes → Map je Slot, `ReplaceBlockAsync` erhält zwei
  Aggregate desselben Tages; `ChatLogBackfillFakes` auf die neuen DTOs. **E9 (+3):** `SnapshotChanged`, neuer Snapshot mit derselben Zeile
  `(EmoteId, Name)` unter neuer `SlotId` → Aggregate umgeschrieben, derselbe Block ohne zweite Archivabfrage neu eingereicht, folgende
  Blöcke zählen auf die neue `SlotId`; Zeile unverändert oder verschwunden → `worker_error`; zweites `SnapshotChanged` → `worker_error`.
- Integration Ende-zu-Ende (`…Worker.cs`): E9-Drift mit echtem Settle → nach dem Worker-Neulesen landen die Zeilen auf dem Ziel, keine
  Zeile am geleerten Seed.
- `ReplayDayCounterTests` (24), `HarnessRunnerTests`: unverändert grün (D20).

### 7.5 Übrige

- Settle-Helfer: bestehende Settle-Tests (`SevenTvSyncServiceSlotTests`, E10/E11a) unverändert grün; neu: Umbindung bei vorhandener
  Zielzeile im selben Lauf → Seedzeile gelöscht, Zielzeile unverändert.
- **AK 39** (`EmoteSlotRollbackSql`-Konstante 3b): (i) Backfill-Slot ohne Live-Nutzung → gelöscht, (ii) mit `Source = 0`-Zeile → bleibt,
  (iii) Slot eines in Schritt 3 gelöschten Platzhalters → schon per Kaskade weg, 3b fasst nichts weiter an, (iv) gesettelter Seed →
  bleibt; Votes bleiben. Ergebnis: genau (i) gelöscht.
- `SlotReconcileRegistrationTests` um `ChatLogBackfillService` ergänzt.
- **Api.Tests** (`ChatLogBackfillEndpointsTests`, +1–2): `SlotsUnsettled` → `409 backfill_slots_unsettled`; die Mapping-Tabelle bleibt
  vollständig (der `default`-Zweig wirft weiter).
- **Vitest:** `api-error-locales.spec.ts` automatisch; `backfill-run-status.spec.ts` nur, soweit es den alten Text „emotes" zusichert (R3: Text wird „entries"/„Einträge"). Keine E2E-Änderung
  (keine Verhaltensänderung der Seite; nur prüfen, dass keine Spec den alten Text „emotes)" zusichert).

## 8. Live-Verifikation (Regel 16) — Freigabe des Betreibers je Anlass

Randbedingungen: geteilte Dev-DB `emotepurge` nur lesend; **nie `docker compose`, nie etwas im Haupt-Checkout**; die #378-Sitzung belegt
`:5151`, `:4200` und Redis `:6394`. **Keine 7TV-Schreibzugriffe nötig:** `handofblood` trägt `ome44`/`hob44` (`hob44` seit 2026-09-01)
im echten Set; der Lauf liest 7TV (Vorschau) und das Chat-Archiv `logs.cyex.app` (extern, nur lesend, Betreiberzusage 08.10.; Cooldown und
`RequestDelaySeconds` gelten, kein paralleler Lauf). Ein Schritt mit 7TV-Schreibzugriff (Testkanal `olaf_olaf_son`) nur nach
ausdrücklicher Freigabe und nur, falls 8.6 gebraucht wird.

| Ressource | Wert |
|---|---|
| DB | `emotepurge_slots375` im Container `emotepurge-dev-postgres`; **nicht** per `createdb -T emotepurge` (scheitert, solange die Dev-Prozesse verbunden sind, und würde sie sonst aussperren), sondern `createdb` + `pg_dump emotepurge \| psql emotepurge_slots375` im Container (Benutzer und Passwort aus dessen Umgebung, nie ausgeben); die Dev-DB wird dabei nur gelesen |
| Redis | `docker run -d --name redis-slots375 -p 127.0.0.1:6395:6379 redis:7.2-alpine` (Image wie Compose) |
| Prozesse | Worker und Api per `dotnet run` aus dem Worktree (PIDs notieren, vor jedem Signal `ps -o args= -p <pid>`); Env-Overrides: `ConnectionStrings__DefaultConnection` → Kopie, `Redis__ConnectionString=127.0.0.1:6395`, `ChatLogBackfill__Enabled=true`, `SevenTv__SlotReconcile__Enabled` je Phase, `SevenTv__EventApi__Enabled=false`, `Retention__Enforce=false`; Api mit `--urls http://127.0.0.1:5175` |
| Anmeldung | R6 (offen, wird beim Start von T7 gefragt): einmalig ein kurzes `:5151`-Fenster, abgestimmt vom Orchestrator mit der #378-Sitzung — Worktree-Api auf `:5151` gegen die Kopie, Betreiber meldet sich an, Cookie in eine Scratchpad-Datei (Modus 600); danach dieselbe Api aus demselben Worktree auf `:5175` (gleicher Schlüsselring, Cookie gilt portunabhängig). Erster Schritt prüft `GET /api/auth/me` mit dem Cookie auf `:5175`; scheitert er, zurück zum Orchestrator |
| Dateien | Scratchpad: Cookie, Antwort-JSONs, SQL-Ausgaben, Protokoll |

0. `df -h /`, Größe der Dev-DB; Abbruch, wenn der Platz nicht das Doppelte trägt.
1. **Kopie und Vorbereitung:** M3 per `dotnet ef database update` mit `--connection` auf die Kopie (Laufzeit und Notice-Zahlen der
   `NULL`-Regel notieren, Gegenprobe `Down` → `Up`). Last begrenzen: `IsBotActive = false` außer `handofblood`. Bestehende aktive Läufe in
   der Kopie abbrechen. **Fenster kleinhalten (nur Kopie):** `Channels.CreatedAt` von `handofblood` so setzen, dass ein Lauf genau einen
   7-Tage-Block hat (Lauf 1: 3 Monate, Zählbeginn eine Woche nach `heute − 3 Monate`, also vor dem 01.09.; Lauf 2: 1 Monat, Zählbeginn eine
   Woche nach `heute − 1 Monat`). Aktives Set und Seeds von `handofblood` in der Kopie notieren.
2. **Phase A, Flag aus (Parität):** Worker und Api starten. Enqueue Lauf 1 über `POST /api/channels/handofblood/backfill` (Set: das
   Set mit `ome44`/`hob44`). Prüfen: eine Snapshotzeile je Emote, Name = letzter Alias, kein 409, Block geschrieben, Zeilen am
   Basis-Slot. Ergebnis gegen dieselbe Abfrage nach T0-Logik (eine Zeile je Emote) — der Aus-Pfad ist live unverändert.
3. **Phase B, Flag an:** Worker und Api per SIGTERM (PID geprüft), Neustart mit Flag an. **Vor dem ersten Tick** (Worker kurz
   verzögert starten oder Api zuerst): Enqueue für das aktive Set → **409 `backfill_slots_unsettled`** (Seeds des aktiven Sets, AK 26
   dritter Satz), nichts geschrieben (Zählung `EmoteSlots`, `ChatLogBackfillRuns` vorher/nachher). Nach dem ersten REST-Tick (Settle):
   Phase-A-Lauf und seine Importzeilen über das 3b-Cleanup und Schritt 4 aus Operations entfernen (übt AK 39 live), dann Enqueue Lauf 1
   erneut → zwei Snapshotzeilen für das Emote, je eigenes `AddedToSetDay`, `CreatedSlot` falsch (Slots aus REST per Datum getroffen,
   AK 22); nach dem Block: `ome44` hat Zeilen, `hob44` keine vor dem 01.09. (**AK 23** Gate je Slot). Lauf 2 (1 Monat): beide Slots mit
   Zeilen, an gemeinsamen Tagen zwei Zeilen (**AK 24**). Nach dem nächsten REST-Tick: **kein neuer Slot** für ein Vorschau-Emote (der
   Datumsvergleich Vorschau ↔ REST-Overlay trägt auf die Mikrosekunde; das ist der Live-Teil von AK 53 a und das wichtigste Risiko des
   Kinds), `/totals` zeigt die Importzahlen auf den Slot-Karten.
4. **Nicht aktives Set (AK 26):** hat die Kopie für `handofblood` ein nicht aktives Set mit Seed und datierter Vorschau, Enqueue dorthin
   → Seed gesettelt (Name, Datum, archiviert), Zeilen nur dieses Sets dort, neue Slots archiviert; sonst Integrationstest genügt
   (Protokoll nennt es).
5. **Messen:** Bytes/Nachrichten je Lauf, Dauer, Cooldown-Einträge; kein Lauf ohne Abstand zum vorigen.
6. **Abbau:** Worker und Api SIGTERM, `docker rm -f redis-slots375`, `DROP DATABASE emotepurge_slots375 WITH (FORCE)`, Cookie-Datei
   löschen. Protokoll mit SHA, Zahlen, Laufzeiten; keine Hostnamen, Passwörter, Tokens, keine Kanalnamen außer `handofblood`.

AK 25 (Re-Run über beide Slots), AK 27 (Konkurrenz) und AK 53 b/c sind live nicht gezielt herstellbar → Integrationstests.

## 9. Prod-Übergabe und Deploy-Hinweis (Betreiber führt aus; bash auf der Devbox, Passwort als Platzhalter)

**Offene Migrationen nach dem Merge (Stand Planung, in T8 neu prüfen):**

| Migration | Kind | auf Prod | Beleg |
|---|---|---|---|
| `20261011184206_AddEmoteSlots` (M1) | #371 | ja, seit 2026-10-11 | Memory/Epic, Nachkontrolle 11.667 Seeds |
| `20261012111509_SwitchUsageStatsToSlotKey` (M2) | #373 | ja, seit 2026-10-12 ~15:13 UTC | Epic-Tabelle #365 |
| `<ts>_SwitchBackfillSnapshotToSlotKey` (M3) | #375 | **nein** | dieses Kind |

**Unsicherheit:** (a) Ob Prod nach #374/#377 schon neu deployt ist, weiß der Plan nicht — für Migrationen unerheblich, beide tragen keine;
(b) parallele Branches (#378 web-only, #403 ohne Migration erwartet) könnten bis zum Merge eine Migration bringen — T8 prüft
`git log origin/main -- src/EmotePurge.Infrastructure/Migrations/` und schreibt das Ergebnis in den PR; (c) maßgeblich bleibt der
`list`-Schritt auf Prod: **genau** M3 `(Pending)`, mehr ⇒ erst durchsehen (Lehre aus #391).

**Zeitpunkt (R1, Betreiber 2026-10-13):** M3 wird **nicht** für das gemeinsame #379-Fenster zurückgehalten, sondern geht mit dem
**ersten `main`-Deploy nach dem #375-Merge** in einem eigenen kleinen Fenster nach Prod — wie M2 am 12.10. Der PR-Text sagt das
ausdrücklich („this merge makes the next main deploy need M3; do not deploy without the window below"), und der Kommentar auf #379
streicht M3 aus dessen Fenster (dort bleibt nur „Flag an").

**Fenster:** 1. Ankündigung; worker und api stoppen, `pg_stat_activity` ohne `EmotePurge%`. 2. Backup
`scripts/backup-postgres.sh`. 3. Vorbedingung `SELECT count(*) FROM "ChatLogBackfillRuns"` und `… FROM "ChatLogBackfillRunEmotes"` (beide
erwartet 0; sonst Abbruch — die `NULL`-Regel R5 ist nur für lokale Läufe gedacht). 4. Tunnel `ssh -N -L 15432:127.0.0.1:5433 vps`. 5. `dotnet ef migrations list …
--connection '…;Password=<PROD-PW>;Command Timeout=600'` → genau M3. 6. `dotnet ef database update <M3-Name> …` (Ziel benannt). 7. `list`
erneut leer; **beide** neuen Images gemeinsam hoch; Slot-Flag und Backfill bleiben aus. 8. Nachkontrolle: Health 200, Worker-Log
`Chat-log backfill disabled.`, Flush wie vorher. **Rollback:** Code zurück auf Prä-#375-Images ohne `Down` (5.3); `Down` nur bei
Bedarf, Guard passiert ohne Läufe.

## 10. Commits und DECISIONS

Conventional Commits, englisch, Tests im Commit ihres Features: (0) Plan; (1) `test: pin the flag-off backfill enqueue and block
behaviour`; (2) `feat: switch the backfill snapshot key to the slot (M3)` **plus DECISIONS-Teil und Operations-Unterabschnitt**; (3)
`feat(worker): count the chat-log backfill per slot` (DTOs, Zähler, Worker, Block-Validierung, E9) **plus DECISIONS-Teil**; (4)
`refactor(infrastructure): share the seed settle writes between the reconcile and the backfill` (+ schlüsselsichere Umbindung, Test);
(5) `feat(infrastructure): plan backfill slot bindings per preview kind` (reiner Planer); (6) `feat: bind every backfill preview entry
to its own slot when the slot flag is on` (Enqueue, 409, Api, `api-error.ts`, Locales, Registrierung) **plus DECISIONS-Teil, Spec-§8-
Verweis, Fußnoten Backfill-Spec**; (7) `docs: add the backfill slot cleanup to the rollback runbook` (3b, Konstante, AK-39-Test, ggf. i18n
„entries"); danach `fix:`/`test:` aus Review.

**DECISIONS:** datierter Absatz **„Child 5 (backfill per slot, M3), 2026-10-1x"** im Entwurfseintrag #365 **zwischen „Child 4" und
„Child 6"** (heute Zeilen ~511/513), englisch, wächst mit den Commits. Inhalt: E1 (Flag-Zweig, Grund E6a, neuer Options-Leser), E2
(Identität statt Mitgliedschaft, neue Slots archiviert), die Bindungsregeln 2.2/2.3 mit den [A]/[P], die 409-Gründe, Z12, `CreatedRow`
auf allen Zeilen, Slot-Schlüssel in Worker/Block, E9 (Paar vor Existenz, `SnapshotChanged`, Worker-Neulesen über `(EmoteId, Name)`) und Widerspruch zu Spec §8, `LiveRowConflict` je Slot als
Vertrag, E10 (Helfer, schlüsselsichere Umbindung), M3 mit `NULL`-Regel, Altimage-Sicherheit und Fensterentscheidung (R1: eigenes Fenster mit dem ersten Deploy nach #375, Betreiber 2026-10-13), 3b. Die
`Betrifft:`-Zeile des Eintrags ergänzt um die Migration, `ChatLogBackfillRunEmote.cs`, `ChatLogBackfillBlockCounter.cs`,
`ChatLogBackfillWorker.cs`, den Planer, den Settle-Helfer, `ChannelEndpoints.cs`, `ApiErrorCodes.cs`, `api-error.ts`, beide Locales.
„Child 8" wird nicht angefasst.

## 11. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander — Builds teilen sich `obj/`)

Grün-Strategie: erst das Orakel (T0), dann das Schema (T1) — der Mehrslot-Snapshot braucht den neuen PK —, dann der flagunabhängige
Slot-Schlüssel in Worker und Block (T2), der mit einer Zeile je Emote verhaltensgleich ist; danach Helfer (T3), reiner Planer (T4) und
erst zuletzt der Flag-an-Enqueue (T5), der als einziger neues Verhalten einschaltet.

| # | Aufgabe | Modell | Aufwand | Input | Flag-aus-Parität | Fertig, wenn |
|---|---|---|---|---|---|---|
| T0 | Orakeltests 7.1 auf dem unveränderten Stand | sonnet | 0,25 T | 7.1, 2.6; `ChatLogBackfillServiceTests*` | ist die Parität: pinnt Snapshot- und `UsageStats`-Wirkung des Aus-Pfads | Tests grün auf `94a84782`, Commit (1) |
| T1 | M3 + Modell + `NULL`-Regel + `Down`-Guard + Migrationstests 7.2 + **Fixture-Umstellung 7.1a** (`SeedRunAsync`, `NULL`-/Dubletten-Fälle auf M2-Schema) + Operations-Unterabschnitt + DECISIONS-Teil | **opus** | 1 T | §5, §6 (Unterabschnitt), 7.1a, 7.2; Muster M2-Migration und ihre Tests, `EmoteSlotRollbackSql`, Übergabe #371 | Schema-only; eine Zeile je Emote passt zum neuen PK; T0 und die Liste 7.1a unverändert grün | ganze Suite grün, Probe-`migrations add` leer, Commit (2) |
| T2 | Slot-Schlüssel: Core-DTOs, `GetSnapshotAsync`, Worker-Map/Gate, Blockzähler, `ValidateBlock`, `ReplaceBlockAsync` mit E9 (Paar-Stufe vor D25, neues Ergebnis `SnapshotChanged`), Worker-Neulesen über `(EmoteId, Name)`, `LiveRowConflict` je Slot, Fakes, Tests 7.4 | **opus** | 1,25 T | §3, 7.4; Kind-3-Code in `ReplaceBlockAsync` | Bijektion bei einer Zeile je Emote; T0 (2) unverändert grün | drei Backend-Suiten grün, `ReplayDayCounterTests` grün, Commit (3) |
| T3 | Settle-Helfer extrahieren, Umbindung schlüsselsicher, Test | sonnet | 0,25 T | §4; `SevenTvSyncService` Settle-Teil | reine Verschiebung; Settle läuft nur bei Flag an; bestehende Settle-Tests unverändert | Suite grün, Commit (4) |
| T4 | **Vorab: Rebase auf `origin/main` und prüfen, ob #403 gemergt ist — wenn ja, nutzt die neue Zielwahl (beide Modi) die #403-Ordnung und der #403-Test bleibt grün; wenn nein, Gleichstand unverändert übernehmen, #403 tauscht danach.** Reiner Bindungsplaner je Vorschau-Art (aktiv/nicht aktiv), Ablehnungsgründe, **`Settle` in Zielwahl und Mutation trennen** (REST: Datum, dann Name; Vorschau nicht aktiv: nur Datum, Sol-P2-2), Unit-Tests der Regeln 2.2/2.3 samt Namensvetter-Regression | **opus** | 0,75 T | §2.2–2.4; `EmoteSlotReconcilePlanner` (Schritte wiederverwenden, nicht kopieren — Abschnitt 12 A) | kein Aufrufer bis T5; `PlanLiveGroup`/`PlanAbsentEmote` verhaltensgleich, ihre Tests (auch alle Settle-Fälle) unverändert grün | Unit-Tests grün, Commit (5) |
| T5 | Enqueue Flag an: Flag-Leser, Ablauf 2.1, Schreiben, 409 (Status, Code, Endpoint, `api-error.ts`, Locales), Wiederholung, Registrierung, Integrations- und Api-Tests 7.3/7.5, DECISIONS, Spec-Verweis, Fußnoten | **opus** | 1,25 T | §2, 7.3, 7.5; Ergebnis T1–T4; Plan-372 2.2a, Plan-373 E6/E11 | Aus-Zweig = Kind-3-Pfad unverändert; T0 und die Liste 7.1a unverändert grün; Test „Flag aus kein 409" | drei Suiten grün (Web: Vitest, Lint, Prettier), Commit (6) |
| T6 | Rollback 3b: Konstante, Operations-Schritt, AK-39-Test; i18n-Text „entries"/„Einträge" (R3) | sonnet | 0,25 T | §6, 7.5 AK 39 | Doku/Betrieb; der Text gilt in beiden Zuständen | Suiten grün, Commit (7) |
| T7 | Live-Verifikation | sonnet | 0,5 T + Betreiberzeit | §8; vorher R6 beim Betreiber einholen | Phase A ist die Live-Parität | Protokoll 8.0–8.6, Abbau erledigt |
| T8 | Review, Gates, PR mit Deploy-Hinweis §9 (Migrationsliste neu prüfen; M3 mit dem ersten Deploy nach dem Merge im eigenen Fenster, nicht im #379-Fenster) | **opus** (Review); Codex Sol | 0,5 T | Branch, §1.3, §9, §10, §14 | — | `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`, `dotnet format`, `npm --prefix web run lint`/`format` grün; E2E nur, wenn T6 Seitenverhalten ändert (erwartet nicht); erst committen, dann `node scripts/coverage-local.mjs` ≥ 80 %; Sol-Befund unverändert vorgelegt; PR mit AK-Tabelle (22–27, 39, 44, 53, 56) und Migrationsliste; Kommentare auf #378/#379/#403 mit §14 |

Zusammen rund 6 Tage statt 2,5 (Spec §17). Mehraufwand: Orakel, Fixture-Umstellung, `NULL`-Regel, `SnapshotChanged` samt Worker-Neulesen, Helfer-Extraktion, geteilte Settle-Zielwahl, zwei
Vorschau-Arten. Ob T4/T5 zusätzlich einen Fable-Blick brauchen (Sperren, Identitätsregeln — gehebelt), entscheidet der Orchestrator.

## 12. Überschneidungen mit parallelen Arbeiten

### A. #403 (eine Namensordnung für D13 und D2)

#403 berührt: `EmoteSlotReconcilePlanner.Derive` (Gleichstand `StringComparer.Ordinal` → UTF-8, also `BaseSlotRule.Utf8Order`),
folgerichtig den Gleichstand der Settle-Kandidatenwahl im selben Planer (`Settle`: ältester Eintrag, Gleichstand „ordinal kleinster
Name", Spec §6.3) und ggf. die Gleichstandsordnung in `EmoteSlotDeltaPlanner` (nutzt `Derive`); `BaseSlotRule.D2Order`/`Utf8Order` sind
das Ziel, nicht der Gegenstand. Dazu Spec D13 und ein DECISIONS-Satz.

**Frontend-Spiegel (Stand 13.10., Info des Orchestrators):** die D2-Gleichstandsregel ist im Client zweimal nachgebaut, beide mit JS-`<`
(UTF-16): `web/src/app/core/usage-stats/merge-set-view.ts` `d2Names` (~Z. 323–337) und `web/src/app/features/usage-stats/usage-stats-page.ts`
`confirmationNamesFor` (~Z. 375–400). `confirmationNamesFor` baut die #378-Sitzung gerade um (wahrscheinlich entfällt sie); #403 lässt
sie in Ruhe. `d2Names` ist frei und gehört in #403. #375 berührt keine der beiden Dateien.

**#375 im Planer (Revision 1):** `Derive` bleibt unberührt (der Enqueue leitet nicht ab, E2). **Aber `Settle` wird geteilt** (Sol-P2-2):
T4 trennt die Zielwahl — in der genau die Kandidatenordnung „ältester Eintrag, Gleichstand ordinal kleinster Name" steht, die #403 auf
UTF-8 umstellen will — von der Mutation, und gibt der Zielwahl einen zweiten Modus (nur exaktes Datum). Die Schnittmenge ist damit nicht
mehr „eine Datei, disjunkte Mitglieder", sondern **dieselbe Methode**: ein paralleler Umbau ergibt einen echten, wenn auch kleinen
semantischen Konflikt (die Ordnungszeile wandert in die neue Zielwahl). Dazu der DECISIONS-Eintrag #365 (#375 schreibt zwischen
„Child 4" und „Child 6"; #403 setzt seinen Satz nicht dorthin).

**Entschieden (Betreiber 2026-10-13): #403 zuerst, aber eng — oder danach, nie gleichzeitig mit T4.** (1) #403 startet jetzt auf eigenem Branch ab
`origin/main` und beschränkt sich auf Vergleicher-Tausch ohne Umbau: `Derive`, die Gleichstandszeile in `Settle`, die Ordnung in
`EmoteSlotDeltaPlanner`, soweit sie `Derive` nutzt, `d2Names` im Client, Spec D13, ein DECISIONS-Satz und der Test mit
Ergänzungsebene gegen U+E000–U+FFFF. Ziel: Merge, bevor #375 mit T4 beginnt (T0–T3 geben dafür rund drei Tage). T4 setzt dann auf der
neuen Ordnung auf und verschiebt die Zeile nur. (2) Schafft #403 das nicht, wartet es bis nach dem #375-Merge und tauscht den Vergleicher
dann in der neuen Zielwahl — dieselbe kleine Änderung an anderer Stelle. Nicht empfohlen: beide gleichzeitig im Planer, weil die
Konfliktauflösung die Gleichstandsregel still auf eine der beiden Ordnungen zurücksetzen kann und keine Suite das ohne den neuen
Ergänzungsebenen-Test merkt. T4 nimmt in seine Settle-Tests mit Gleichstand auf `AddedAt` nur ASCII-Namen, damit sie in beiden
Ordnungen gleich ausgehen; den Unterschied prüft allein der #403-Test.

### B. #378 (Massenlöschung/Restore je Slot, Spec §5.7/§9.3, parallele Sitzung)

- `EmoteService` (insbesondere die Entries-Pfade für Delete/Restore): **nicht berührt.**
- `EmoteSlotReconcilePlanner`: berührt (T4, siehe A) — `PlanLiveGroup`/`PlanAbsentEmote` und `Derive` bleiben unverändert; #378 ist laut
  Issue reines Web und ändert den Planer nicht.
- `sync-deleted`/`sync-restored` (`SevenTvEndpoints.cs`): **nicht berührt.**
- Geteilte Dateien nur im Web: `web/public/i18n/{de,en}.json` (#375: `errors.api.backfill_slots_unsettled`, ggf. `backfill.runSet`; #378:
  `usageStats.delete.*`) — getrennte Schlüsselbereiche, höchstens ein textueller Merge. `api-error.ts` berührt nur #375.
- Fachliche Berührung ohne Dateikonflikt: AK 53 (b) setzt voraus, dass der Restore-Bericht mit der alten Generation ein No-op ist — das
  liefert #376 serverseitig; #378 sendet `slotId` und das ursprüngliche `AddedAt`. Nichts zu koordinieren.

## 13. Betreiberentscheidungen (2026-10-13) und offene Rückfrage

1. **R1 — entschieden:** M3 geht mit dem ersten `main`-Deploy nach #375 in einem eigenen kleinen Fenster nach Prod (§9), nicht im
   #379-Fenster; dieses schrumpft auf „Flag an".
2. **R2 — entschieden:** ein vom Enqueue in place gesettelter Seed eines nicht aktiven Sets bekommt `IsArchived = true`, `ArchivedAt` =
   bisheriger Wert, sonst jetzt (D7-fähig).
3. **R3 — entschieden:** `backfill.runSet` sagt in #375 „Einträge"/„entries" (T6).
4. **R4 — entschieden:** undatierter Eintrag einer aktiven Vorschau ohne Namensbindung ⇒ 409 `backfill_slots_unsettled` ([A] zu D11 d).
5. **R5 — entschieden:** `NULL`-Snapshotzeilen abgeschlossener Läufe hängt M3 an den Slot mit den Zeilen des Paars, sonst löscht es sie.
6. **R6 — offen, wird beim Start von T7 gefragt:** Freigabe für (i) ein kurzes `:5151`-Fenster zur Anmeldung (Abstimmung mit der
   #378-Sitzung), (ii) zwei Ein-Wochen-Läufe gegen `logs.cyex.app` für `handofblood`, (iii) das Verschieben von `Channels.CreatedAt` nur in
   der Kopie. Keine 7TV-Schreibzugriffe.
7. **R7 — entschieden: (a).** Undatierte Einträge nicht aktiver Vorschauen ⇒ 409; der Backfill nutzt D11 (d) nicht ([A]); der
   REST-Planer bleibt unverändert.
8. **#403 — entschieden:** läuft jetzt parallel auf eigenem Branch, eng geschnitten (Vergleicher-Tausch inkl. `d2Names`, nicht
   `confirmationNamesFor`), Ziel Merge vor T4; sonst tauscht der T4-Umsetzer nach dem Rebase den Vergleicher in der neuen Zielwahl
   (T4-Vorabschritt, 12 A).

## 14. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#379 (Betrieb):** Fenster = Flag an (M3 nach R1 dann schon auf Prod); Abnahme AK 22–26 auf der Prod-Kopie mit Backfill an; Vorbedingung
  „kein aktiver Backfill-Lauf beim Umschalten des Slot-Flags" ins Runbook (`SnapshotChanged` aus E9 ist nur der Gurt); 409 `backfill_slots_unsettled`
  ist bis zum ersten Settle-Tick je Kanal erwartbar. **Rückbau der Aus-Zweige** umfasst den Kind-3-Enqueue-Pfad (D36-Auswahl,
  `BindSnapshotSlotsAsync`, Basis-Slot-Bindung im Enqueue) und den Fall `SnapshotChanged` samt Worker-Neulesen (E9), sobald keine bei
  Flag aus eingereihten Läufe mehr existieren können.
- **#378:** keine Abhängigkeit; AK 53 (b) prüft den Bericht nur serverseitig.
- **#403:** Reihenfolge nach 12 A — vor T4 mergen oder nach #375; die Gleichstandszeile der Settle-Zielwahl liegt nach T4 in der
  getrennten Zielwahl (beide Modi), `d2Names` im Client gehört zu #403, `confirmationNamesFor` zu #378.
