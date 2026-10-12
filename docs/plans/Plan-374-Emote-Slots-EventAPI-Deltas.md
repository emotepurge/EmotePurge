# Plan #374 — Emote-Slots, Kind 4: EventAPI-Deltas je `(Id, Name)`

Erstellt am 2026-10-12 im Worktree `EmotePurge-slots374` (Branch `feat/374-eventapi-slot-deltas`, Basis `origin/main` @ `4b754279`).
**Status: Revision 2 (12.10.) — adversariales Review (Codex Sol: 2 × P1, 4 × P2) eingearbeitet, Betreiberentscheidungen R1, R3, R4, R5
eingetragen; offen nur R2 (Freigabe des Live-Laufs, wird vor T5 eingeholt).**
Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) (§2.3, §7.1, §7.2, §10, §13 AK 18–21,
§15, §16 Kind 4; D4, D7, D11 b, D12, D13, D14, D17, O5, O6), Issue #374 mit den Übergabekommentaren aus #372 und #373 (bindend),
[Plan-372](Plan-372-Emote-Slots-Reconcile.md) §2.0, §2.2, §2.2a (Z8), §2.5 (E14), §11; [Plan-373](Plan-373-Emote-Slots-Usage-Key.md)
§3.2 (E7), §11; [Plan-376](Plan-376-Emote-Slots-Read-API.md) §4.2a (B1); DECISIONS #365 („Child 2", „Child 3", „Child 6").
Formatvorlage: Plan-373/Plan-376.

**Was dieser Plan trägt:** nur, was Spec und Übergaben offen lassen — Migrationsbefund, Flag-Matrix, die Entscheidungsregeln je
Änderungsart und Emote-Zustand, Sperre und Wiederholung, Leave-Regel, Worker-Rückfall, Tests, Live-Prüfung, Aufgabenfolge. Kein Code:
Namen von Typen und Methoden stehen nur als Form. D0–D33 und die Betreiberentscheidungen aus #371–#376 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **[A]** = Abweichung vom Spec-Wortlaut; alle [A] sind vom Betreiber
bestätigt (Abschnitt „Betreiber-Entscheidungen (12.10.)"). **[P]** = Präzisierung, die der Spec-Text offen lässt, ohne ihm zu widersprechen. **Sol-P*n*** = Befund des
adversarialen Reviews (Revision 1).

### Revision 1 (12.10.) — was sich geändert hat

| Änderung | beantwortet |
|---|---|
| **E10 neu:** Umbenennung in place nur, wenn das Emote **genau einen** aktiven eigenen Slot hat; bei ≥ 2 löst REST über die Datums-Identität auf. Grund: der 7TV-Quirk (`value.name` = Name des ersten Slots) ist nur erkennbar, solange unsere Geschwisternamen frisch sind; nach einem veralteten REST (Umbenennung kurz zurückgedreht) oder einer Aus-Phase würde eine Quirk-Umbenennung angenommen und Zählung dauerhaft dem falschen Slot gutgeschrieben. Restlücke benannt (14.1) | Sol-P1-1 |
| **E7 geteilt:** „mehrdeutig" verwirft weiter jede Inventarschreibung des Dispatch, **erhält aber die Leave-Beobachtung** jeder gezogenen Id, deren Ausgang für sich allein eindeutig „kein aktiver Slot mehr" ist. Sonst kann die Abgangsmeldung im 30-min-Fenster ganz verloren gehen (`ArchiveAbsentEmotesAsync` b1/b2, `EmoteTagService` `Holds`) | Sol-P1-2 → R5 |
| **E12 neu:** Vorprüfung jeder Umbenennung gegen den partiellen Unique-Index (anderer autoritativer eigener Slot mit gleichem `AddedAt` und Zielnamen, aktiv **oder** archiviert) → mehrdeutig; ein trotzdem auftretender Slot-Schlüsselkonflikt wird zu `AmbiguousDelta` statt zum verworfenen Dispatch. Abschnitt 0 Punkt 2 korrigiert; keine Index-Änderung, die Migrationsaussage bleibt | Sol-P2-1 |
| **E5 vereinfacht:** jeder Push im virtuellen Zustand S3 ist mehrdeutig (die Flag-an-Map zählt keine virtuellen Einträge, ein No-op ließe das Emote ungezählt; die Kandidatenprüfung lag hinter dem No-op) | Sol-P2-2 |
| **E6 neu gefasst:** alle Änderungen werden gegen die **Ausgangs-Momentaufnahme** aufgelöst; je Id sind nur reine Push-, reine Pull- oder genau eine Umbenennung erlaubt, jede Mischung ist mehrdeutig. Die frühere Behauptung „namensdisjunkt = reihenfolgeunabhängig" ist gestrichen (Gegenbeispiel archiviertes A, Push B + Pull A) | Sol-P2-3 |
| **Live-Lauf (8) neu geschnitten:** jede abhängige 7TV-Änderung erst nach belegter Vorbedingung (v3-REST zeigt den erwarteten Stand, Worker-Neustart, DB-Prüfung); Delta-Ergebnis und Folge-Resync getrennt protokolliert; Zählung des Push-Slots vor der REST-Autorisierung Pflichtschritt | Sol-P2-4 |
| **T1 mit Orakel:** vor dem Umbau ein Charakterisierungstest des Aus-Pfads auf dem `main`-Stand (Eingaben → Emote-Zeilen, Leaves, Outcomes), der nach dem Umbau unverändert grün bleiben muss | Sol, „next steps" |
| Neue Rückfragen R4 (Abweichung von AK 20s Beispiel) und R5 (Leave trotz Mehrdeutigkeit); R1 auf die Momentaufnahme umgestellt; Aufwand 3,75 → 4,75 Tage | Folge |

### Revision 2 (12.10.) — was sich geändert hat

| Änderung | beantwortet |
|---|---|
| Abschnitt „Betreiber-Entscheidungen (12.10.)" neu; R1, R3, R4, R5 entschieden wie empfohlen, R2 offen bis vor T5; Abschnitt 13 umgestellt | Betreiber 12.10. |
| T3 und Abschnitt 10 nennen die Spec-Stellen (§7.2, AK 20, Grenze zu D17 in §14) und den DECISIONS-Absatz „Child 4", die im Implementierungs-Commit (3) mitgepflegt werden | Betreiber 12.10., Regel 3 |

## Betreiber-Entscheidungen (12.10.)

| # | Frage | Entscheidung | Wirkung im Plan |
|---|---|---|---|
| **R1** [A] | Pull eines Namens, zu dem im Ausgangsstand kein aktiver eigener Slot passt, bei aktivem Emote | **`AmbiguousDelta`** (Grund `pull-target-missing`) | 4.2 Zeile `pulled`, S2 mit Namen; Ergänzung in Spec §7.2 |
| **R2** | Freigabe des Live-Laufs (rund 12 7TV-Schreibzugriffe, vier Tore mit Neustart, zwei Testnachrichten) | **offen** — wird vor T5 eingeholt | T5 startet erst nach der Freigabe; jeder POST zusätzlich einzeln im Chat |
| **R3** | Rückkehr eines entfernten Alias (und jeder Push im virtuellen Zustand) zählt bis zum REST-Nachlauf nicht | **hinnehmen**, als bekannte Grenze dokumentieren | DECISIONS „Child 4"; Spec §14 „Zur Kenntnis" als Grenze zu D17 |
| **R4** [A] | Umbenennung in place bei Mehr-Slot-Emotes | **nur bei genau einem aktiven eigenen Slot**; bei ≥ 2 `AmbiguousDelta` (`rename-with-siblings`), REST benennt per Datums-Identität um; das AK-20-Beispiel wird auf einen Slot umgestellt | E10, 4.2 Zeile `updated`, 7.3 Fälle 6/7; Änderung in Spec §7.2 und AK 20 |
| **R5** [A] | Leave-Beobachtung trotz mehrdeutigem Dispatch | **beobachten**: „`AmbiguousDelta` schreibt nichts" heißt „kein Inventar"; Leaves gezogener Ids mit für sich eindeutigem Ausgang „kein aktiver Slot mehr" werden geschrieben | E7, 3.2 Schritt 5, 3.4; Ergänzung in Spec §7.2 |

## 0. Migrationsbefund (Auftrag A): **keine Migration**

#374 braucht **weder Schema- noch Index- noch Daten-SQL**. Begründung aus Spec und Code:

1. **Alle Spalten existieren seit M1** (`20261011184206_AddEmoteSlots`): ein Push-Slot nach D11 b ist `Name` = Alias,
   `AddedAt = UtcNow`, `AddedAtFromRest = false`, `IsArchived = false`, `SeedUnsettled = false`, `LastSyncedAt` jetzt; eine Umbenennung
   schreibt `Name`/`LastSyncedAt`; ein Pull schreibt `IsArchived`/`ArchivedAt`/`LastSyncedAt`. Das ist genau die Spaltenmenge aus Spec 4.1.
2. **Die Zugriffe sind indiziert:** Slots eines Emotes nach Namen über `IX_EmoteSlots_EmoteId_Name` (M1); „unsettled Seed des aktiven
   Sets" über `SetSeeds.SeedsOfSet` und den seit M2 eindeutigen Slot-Index `IX_UsageStats_SlotId_EmoteSetId_Date`. Provisorische Slots
   berühren den partiellen Unique-Index `IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative` nicht (Filter `AddedAtFromRest`,
   `AppDbContext.cs:63-65`). **Revision 1 (Sol-P2-1):** eine Umbenennung eines **autoritativen** Slots kann ihn sehr wohl verletzen — der
   Filter schließt archivierte Slots nicht aus, ein D4-Gleichstand hinterlässt z. B. aktives `A(t)` neben archiviertem `B(t)`. Das löst
   eine Vorprüfung im Plan (E12), nicht ein anderer Index; alle Slot-Schreiber sperren die Emote-Zeile, also kann zwischen Vorprüfung und
   Speichern niemand einen kollidierenden Slot anlegen.
3. **Was sich am Vertrag ändert, ist nicht persistiert:** das Delta-Modell (`SevenTvEmoteSetDelta`), der Parser und der neue
   Ergebniswert `SevenTvDeltaOutcome.AmbiguousDelta` leben nur im Speicher (Worker ↔ Infrastructure); nichts davon steht in einer
   Tabelle oder im Modell-Snapshot.
4. **Kein Datenbestand ist nachzuziehen:** bei Flag aus schreibt der Delta-Pfad weiterhin nichts auf `EmoteSlots` (Abschnitt 2), also gibt
   es auf Prod keinen Zustand, den eine Daten-Migration vorbereiten oder reparieren müsste. Bei Flag an (erst #379) sind Push-Slots
   gewöhnliche provisorische Slots, die der REST-Abgleich aus #372 ohnehin kennt (§6.2 Schritt 5).
5. **Gate:** ein Probe-`dotnet ef migrations add` nach der Umsetzung erzeugt nichts (Abschnitt 11, T6).

**Folge für den Betreiber:** #374 geht mit dem nächsten Main-Deploy bei Flag aus auf Prod, ohne Fenster und ohne Handgriff an der
Datenbank. Einzige Vorsicht ist die allgemeine aus #391: vor dem Deploy prüfen, welche Migrationen `main` **insgesamt** mitbringt (mergt
#375 vorher, kommt M3 mit — das ist dann dessen Fenster, nicht #374s). Eine unvermeidliche Migration ist nicht in Sicht; Alternativen
ohne Migration sind daher nicht nötig.

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 4)

1. **Parser** (§7.1): `pushed` liefert `(Id, Name)`, `updated` liefert `(Id, OldName?, NewName?, ImageUrl)` aus `old_value.name` und
   `value.name`, `pulled` liefert `(Id, Name?)` aus `old_value` — ein Eintrag je Item, fehlende Namen `null`, kein Wurf (AK 18).
2. **Delta-Anwendung je `(Id, Name)` bei Flag an** (§7.2, D17): Push, Umbenennung, Pull nach Abschnitt 4; mehrdeutige Dispatches
   schreiben **nichts** und liefern `AmbiguousDelta` (AK 19, 20).
3. **Leave nur beim letzten aktiven Slot** (§7.2, D17).
4. **Worker-Rückfall:** `AmbiguousDelta` → `ResyncChannelAsync` wie `SetNotActive`, kein eigenes `channel.synced` (AK 21).
5. **Flag aus:** Ergebnis byte-gleich zu `main` (Abschnitt 2), nachgewiesen durch die bestehenden Delta-Tests und einen eigenen
   Aus-Test.
6. Docs: DECISIONS-Absatz „Child 4", Flag-Zeile in `docs/Operations.md`, Spec-Nachtrag für bestätigte Abweichungen (Abschnitt 10).

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 5 (#375) | Vorschau-Bindung; Hinweis aus diesem Kind in Abschnitt 12 (Push-Slot vs. aktive Vorschau) |
| Kind 8 (#378) | Massenlöschung mit `alias`; ihre Dispatches (ein `pulled`-Item je Slot) verarbeitet dieses Kind bereits richtig |
| Kind 9 (#379) | Einschalten des Flags auf Prod, Rückbau der Aus-Zweige (auch des Delta-Aus-Zweigs) |
| unverändert | `user.update` (Set-Wechsel), Abonnement-Konvergenz, Lückenfüllen nach Reconnect, REST-Abgleich, Zähler/Flush |

### 1.3 Spec ↔ Ist-Stand: gefundene Abweichungen und Präzisierungen

| # | Stelle | Befund | Behandlung |
|---|---|---|---|
| F1 | Spec §7.2 Tabelle `pulled` | nennt als mehrdeutig nur „Name fehlt bei ≥ 2 aktiven Slots"; der Fall „Name gesetzt, aber kein aktiver eigener Slot dieses Namens, Emote noch aktiv" ist nicht geregelt | **[A]** mehrdeutig, R1 |
| F2 | Spec §7.2 `pushed` „archivierter Slot gleichen Namens" | lässt offen, ob jeder archivierte Slot zählt oder nur ein D7-Kandidat | **[P]** nur D7-Kandidaten (E3) |
| F3 | Spec §7.2 | regelt nicht den Zustand „aktives Emote ohne aktiven eigenen Slot" (virtueller Slot, Plan-372 E14), den Flag-aus-Phasen, ein Restore-Bericht ohne Eintrag (Plan-376 4.2a) oder ein Sync-Fehler hinterlassen | **[P]** Regeln in 4.2 (E5) |
| F4 | Spec §7.2 „`AmbiguousDelta` schreibt nichts" | gilt für den ganzen Dispatch; enthält er Pulls anderer Emotes, entfallen auch deren (sonst immer glaubwürdige) Leave-Beobachtungen — und die kann REST im 30-min-Fenster nicht nachholen, wenn das Emote vor Fensterende zurückkehrt (b2 greift nur bei weiterhin fehlendem Emote, `SevenTvSyncService.cs:1252-1261`, `:1730`); `EmoteTagService` (`Holds`, `:555-562`) wertet die fehlende Beobachtung dann dauerhaft als gültige Platzierung (Sol-P1-2) | **[A]** Inventar nichts, eindeutige Last-Slot-Leaves doch (E7), R5 |
| F8 | Spec AK 20 „Rename `(X, probeB → probeC)` → in place" bei zwei Slots `probeA`/`probeB` | `probeB` ist der zweite Eintrag; 7TV liefert dafür `value.name = probeA` (Quirk, §2.3) — der Fall ist auf dem Draht so nicht herstellbar. Eine Annahme in place bei ≥ 2 Slots stützt sich auf die Frische unserer Geschwisternamen, die ein veralteter REST oder eine Aus-Phase nicht garantiert (Sol-P1-1) | **[A]** in place nur bei genau einem aktiven eigenen Slot (E10), R4 |
| F9 | Spec §4.1 Index „bei einer Umbenennung ändert sich die Zeile, nicht der Treffer" | eine Umbenennung eines autoritativen Slots kann den Unique-Index gegen einen **archivierten** Gleichstands-Slot verletzen (`AppDbContext.cs:63-65`, Filter nur `AddedAtFromRest`) (Sol-P2-1) | **[P]** Vorprüfung E12 |
| F5 | Spec §7.2 „Leave nur für IDs, deren letzter aktiver Slot ging oder die schon archiviert waren" | ein Pull einer Id **ohne** Emote-Zeile ist nicht genannt; heute wird er beobachtet | **[P]** weiter beobachtet (E8) |
| F6 | Spec §15 „`SevenTvEventClient`-Dispatch-Test" | der Client ist eine Transportklasse; Regel 11 verifiziert Transporte live, nicht gegen Fakes | **[E]** Entscheidung als reine Abbildung herausgezogen und container-frei getestet (E11) |
| F7 | 7TV-Quelle (`7tv-write-semantics`, SevenTV @ `e1733255`) | `value.timestamp` im Dispatch ist das Hochladedatum des Emotes, **kein** `added_at` (vgl. DECISIONS 2026-08-03) | wird nicht gelesen; Push-Datum bleibt `UtcNow` (O6) |

Die Spec wird nur für bestätigte [A]-Punkte nachgetragen (Abschnitt 10); [P]/[E] stehen im DECISIONS-Absatz.

## 2. Flag-Matrix (Auftrag B)

| `SevenTv:EventApi:Enabled` | `SevenTv:SlotReconcile:Enabled` | Verhalten des Delta-Pfads |
|---|---|---|
| aus | aus oder an | Kein Socket, keine Dispatches; #374 ist inert. Nur der periodische REST-Abgleich (#372) arbeitet. |
| an | **aus** (Prod bis #379) | **Exakt `main`:** emote-weit (Push legt an/un-archiviert, `updated` überschreibt `Emote.Name`, Pull archiviert, Leave je gezogener Id), heutige Sperr- und Transaktionsform, keine Schreibung auf `EmoteSlots`, nie `AmbiguousDelta`, dieselben Ergebnisse und Publikationen. Gezählt wird wie seit #373: die Aus-Map gibt für ein per Delta (re-)aktiviertes Emote den Paar-Slot oder den Platzhalter aus, den erst der Flush auflöst — **der Delta-Pfad selbst legt keinen Seed an** (Plan-373 §11). |
| an | **an** | Slot-Pfad nach Abschnitt 3–4: Regeln je `(Id, Name)`, provisorische Push-Slots, Umbenennung in place (nur bei genau einem aktiven Slot, E10), Archivierung je Slot, Leave nur beim letzten Slot; Mehrdeutiges → `AmbiguousDelta` (kein Inventar, eindeutige Last-Slot-Leaves doch, E7) → REST-Resync. Ein angewendetes Delta frischt die Map auf, ein Push-Slot zählt damit sofort (Plan-373 E7, §11). |

- **[E1] Der Parser ist flag-unabhängig, die Anwendung verzweigt einmal am Anfang des Schreibteils.** Der Aus-Zweig bekommt je Änderung
  genau das Tripel `(Id, Name, ImageUrl)`, das er heute bekommt — ein fehlender Name ist dort wie heute die leere Zeichenkette des DTO,
  eine doppelte gezogene Id bleibt für Plausibilität, Archivschleife und Leave-Upsert ergebnisgleich (der Upsert dedupliziert schon heute).
  Damit ist „Aus = `main`" eine Eigenschaft des Codes, nicht nur der Tests.
- **Umschaltfenster:** Deltas laufen nur im Worker; ein Api/Worker-Mischzustand berührt den Delta-Pfad nicht. Für Z8 (Delta ↔ Bericht/Sync
  mit verschiedener Sperrfolge) bleibt die bestehende einmalige Deadlock-Wiederholung in beiden Zuständen.
- **An → Aus → An:** im Aus-Zustand pflegt der Delta-Pfad keine Slots; danach können Slots veraltet sein (Name, Archivzustand). Die Regeln
  in 4.2 behandeln jeden solchen Widerspruch als mehrdeutig (Slot nicht gefunden, aktive Slots unter archiviertem Emote, virtueller
  Zustand), und der REST-Abgleich konvergiert über die Datums-Identität. Kein Delta rät über einen veralteten Stand hinweg.

## 3. Verträge

### 3.1 Parser und Modell (§7.1)

- `SevenTvEmoteSetDelta` trägt drei Listen: `Pushed` (bestehender Emote-Typ, `Name` = `value.name`), `Updated` (neuer Typ
  `SevenTvEmoteRename` mit `Id`, `OldName?`, `NewName?`, `ImageUrl`), `Pulled` (neuer Typ `SevenTvPulledEntry` mit `Id`, `Name?`). Abgeleitet:
  `PulledIds` (ordinal distinct, für den Aus-Zweig und das Sperren) und `IsEmpty` über alle drei Listen.
- **Robustheit wie heute:** ein Item ohne verwertbare Id wird übersprungen und mit Warnung geloggt; ein fehlender oder leerer Name ist
  `null` (in `Updated`/`Pulled`), nie ein Wurf. Ein `updated`-Item ohne `old_value` liefert `OldName = null` (der Aus-Zweig braucht es nicht).
  `index` wird nicht gelesen (7TV zeigt damit auf den ersten Slot der Id, §2.3).
- `SevenTvDeltaOutcome` bekommt `AmbiguousDelta`; es entsteht nur nach dem Lesen unter dem Zeilen-Tor und trägt deshalb den Login
  (`ForChannel`, wie `SetNotActive`). Die Typ- und Interface-Kommentare (`SevenTvDeltaResult`, `ISevenTvSyncService`) werden ergänzt.
- Logzeilen in angefassten Methoden werden englisch (Projektsprache); bestehende deutsche Zeilen im Aus-Zweig bleiben wörtlich, weil
  „Aus = `main`" auch für Logs gilt.

### 3.2 Ablauf eines Dispatch bei Flag an (Erweiterung von `ApplyDeltaAttemptAsync`)

Unverändert davor: Namens- und Zeilen-Tor, Sperr- und Ausschlussprüfungen, `SetNotActive`. Dann in **einer** Transaktion:

1. **Sperren (Z8, unverändert):** Kanalzeile über den Kanal-Einstieg von `RowLocks`, dann die vorhandenen Emote-Zeilen des Kanals aller
   Dispatch-Ids (gepusht, umbenannt, gezogen) in einem sortierten Statement.
2. **Lesen hinter der Sperre:** alle Emote-Zeilen des Kanals (für die Plausibilität, wie heute) und **nur für die betroffenen Ids** ihre
   Slots; dazu mit einer Abfrage, welche betroffenen Emotes einen unsettled Seed des aktiven Sets tragen (`SetSeeds.SeedsOfSet`, dieselbe
   Definition wie Settle und Map). Keine veralteten getrackten Instanzen (wie Plan-372 2.2 Schritt 4).
3. **[E2] Deckungsprüfung:** jede gelesene Emote-Zeile einer Dispatch-Id muss in der Menge der gesperrten Ids liegen; fehlt eine (ein
   Schreiber ohne Kanalsperre — Ballot-Platzhalter — hat sie zwischen Id-Lesen und Sperre angelegt), wird der Versuch verworfen und läuft
   über die Wiederholung (3.4). Vor dem Speichern die bestehende Zusicherung „geänderte Emotes und Eigentümer geschriebener Slots ⊆
   gesperrt ∪ neu" (Programmierfehler, keine Wiederholung).
4. **Entscheidung:** eine reine Klasse plant den Dispatch aus der Momentaufnahme (Abschnitt 4), **je Emote unabhängig** und gegen den
   Ausgangsstand (E6). Ergebnis: je Emote **entweder** „mehrdeutig" samt Grund **oder** ein Teilplan (Emote-Aktionen anlegen,
   un-archivieren, archivieren, Bild; Slot-Anlagen; Slot-Änderungen; D13-Ableitung; Leave ja/nein). Der Dispatch ist mehrdeutig, sobald
   ein Emote es ist (E7).
5. **Mehrdeutig:** keine Inventarschreibung (keine Emote-, keine Slot-Änderung); **[A] R5 / E7:** die Leaves der Emotes, deren Teilplan
   für sich eindeutig ist und mit „kein aktiver Slot mehr" endet, werden trotzdem beobachtet — in derselben Transaktion, die dann nur
   diese Upserts committet. Englische Information-Zeile mit Kanal, 7TV-Id und Grundcode, Ergebnis `AmbiguousDelta`.
6. **Plausibilität** (bestehender Wächter) auf dem Plan: aktive Emotes vorher > 0 und danach 0 → `ImplausibleSkipped`. Ein Emote ist danach
   aktiv, wenn es einen aktiven eigenen Slot behält oder im virtuellen Zustand nicht gezogen wurde — gleichbedeutend mit „aktive
   `(Id, Name)` nach Anwendung" aus §7.2.
7. **Leere-Set-Evidenz:** `emptySetConfirmations.Reset` wie heute, wenn der Dispatch Pushes oder Umbenennungen enthält — **[E]** auch bei
   `AmbiguousDelta` (die Evidenz „Set ist nicht leer" bleibt wahr), nicht bei `ImplausibleSkipped` (wie heute nach dem Wächter).
8. **Anwenden** auf getrackte Entitäten, Leave-Upsert (Position nach den Emote-Sperren, sortiert), `SaveChanges`, Commit. `Applied`, wenn
   eine Emote- oder Slot-Entität geändert wurde, sonst `NoChange` (eine reine Leave-Beobachtung ist wie heute `NoChange`). Bei `Applied`
   Map-Auffrischung und `channel.synced` durch den Worker wie heute.

### 3.3 Emote-Zeile und D13 bei Flag an

- **Anlegen** (Push einer unbekannten Id): wie heute (`LastEnteredSetAtUtc` jetzt), `Name`/`FirstSeenAt` aus der Ableitung.
- **Un-archivieren** (Push auf archiviertes Emote): wie heute (`LastEnteredSetAtUtc` jetzt, `IsPlaceholder = false`, `ArchivedAt = null`).
- **Archivieren** (letzter aktiver Slot gezogen): `IsArchived`, `ArchivedAt` jetzt, `IsPlaceholder = false` (D37), wie heute.
- **Bild:** aus der Änderung, nie mit einer leeren URL über eine bekannte (bestehende Delta-Regel).
- **Nie ein Umbenennen der Emote-Zeile aus dem Dispatch.** `Emote.Name`/`FirstSeenAt` folgen strikt D13 aus den aktiven eigenen Slots nach
  Anwendung (dieselbe Ableitung wie der REST-Abgleich, Plan-372 2.3 Schritt 8); ohne aktiven eigenen Slot bleiben beide unverändert.
  Folge, gewollt: ein Push-Slot (`UtcNow`) ordnet vor einem undatierten Slot (`null` zuletzt) und kann so `Emote.Name` übernehmen.

### 3.4 Wiederholung

- **[E9]** Bei Flag an teilen sich **eine** Ganz-Wiederholung (geleerter ChangeTracker, neu gelesene Kanalzeile, wie heute): Deadlock
  (bestehend, beide Zustände), Deckungslücke (E2) und ein Emote-Schlüsselkonflikt beim Speichern (`IX_Emotes_ChannelId_SevenTvEmoteId`,
  paralleler Anleger derselben Id). Der zweite Fehlschlag propagiert; der Client loggt und verwirft den Dispatch, der nächste REST-Tick
  gleicht ab (Bestand). Bei Flag aus bleibt die Wiederholung wie heute auf Deadlocks beschränkt.
- **Slot-Schlüsselkonflikt (Revision 1, Sol-P2-1):** die Vorprüfung E12 schließt ihn aus; tritt er beim Speichern trotzdem auf
  (`EmoteSlotKeys.IsSlotKeyConflict`), wird er **nicht** wiederholt (dieselbe Umbenennung kollidierte erneut), sondern als
  `AmbiguousDelta` mit Grund `authoritative-key-collision` beantwortet: Transaktion zurück, die eindeutigen Last-Slot-Leaves (E7) in
  einer neuen Transaktion unter denselben Sperren, Resync. So endet er nie im verwerfenden `catch` des Clients.

### 3.5 Worker

- **[E11]** Die Folgeentscheidung je Ergebnis wird eine reine, interne Abbildung im Worker (Form: „Ergebnis → {Live-Event
  veröffentlichen, Resync mit Übernahme, Abonnement abbauen, nichts}"), die `ApplyDeltaToChannelAsync` nur noch ausführt.
  `AmbiguousDelta` → Resync mit Übernahme, wie `SetNotActive`/`ImplausibleSkipped`; der Dispatch selbst veröffentlicht nichts. Der Resync
  veröffentlicht wie für jeden unbeaufsichtigten Aufrufer nur bei `HasChanges` (AK 21 „kein `channel.synced`" heißt: keines aus dem
  Delta; ein Resync, der wirklich etwas ändert, meldet das wie heute).

## 4. Entscheidungsregeln je `(Id, Name)` (D17) — Flag an

### 4.1 Zustände eines betroffenen Emotes (aus der Momentaufnahme 3.2 Schritt 2)

| Kürzel | Zustand |
|---|---|
| **H** | Emote trägt einen unsettled Seed des aktiven Sets (D12) — vorrangig vor allem anderen |
| **S0** | keine Emote-Zeile |
| **S1** | Emote-Zeile archiviert |
| **S2** | Emote aktiv, mindestens ein aktiver eigener Slot |
| **S3** | Emote aktiv, kein aktiver eigener Slot („virtueller Slot" unter `Emote.Name`, Plan-372 E14) |

„Eigene Slots" = alle Slots des Emotes außer unsettled Seeds (Plan-372 E7). Fremde Seeds werden nie gelesen, geändert oder gezählt.
Alle Namensvergleiche ordinal und case-sensitiv.

### 4.2 Regeln

| Änderung | Zustand | Regel |
|---|---|---|
| jede | H | **mehrdeutig** (AK 20, D12) |
| `pushed (Id, n)` | `n` fehlt | **mehrdeutig** |
| | S0 | Emote anlegen, neuer provisorischer Slot `n` |
| | S2 | aktiver eigener Slot `n` vorhanden → **No-op** (REST war schneller, AK 19); sonst archivierter D7-Kandidat `n` (E3) → **mehrdeutig** (AK 19); sonst neuer provisorischer Slot `n` |
| | S1 | **[E4]** ein aktiver eigener Slot unter dem archivierten Emote (veralteter Stand) → **mehrdeutig**; sonst D7-Kandidat `n` → **mehrdeutig**; sonst Emote un-archivieren, neuer provisorischer Slot `n` |
| | S3 | **[E5, Revision 1]** immer **mehrdeutig**: die Flag-an-Map zählt keine virtuellen Einträge (`SevenTvSyncService.cs:1193-1211`), ein No-op ließe das Emote bis zum nächsten Tick ungezählt; ein neuer Slot ließe den virtuellen Namen aus Map und Belegung verschwinden; ein D7-Kandidat (nach Restore ohne Eintrag) braucht ohnehin REST |
| `updated (Id, o → n)` | `o` oder `n` fehlt | **mehrdeutig** |
| | S2 mit **genau einem** aktiven eigenen Slot | **[E10]** dieser Slot heißt nicht `o` → **mehrdeutig**; **[E12]** `n ≠ o`, der Slot ist autoritativ und ein **anderer** autoritativer eigener Slot (aktiv oder archiviert) trägt dasselbe `AddedAt` und den Namen `n` → **mehrdeutig**; sonst `Name := n` in place, `AddedAt` und Datums-Herkunft unverändert (S1-Sonde: umbenennungsfest), Bild; `o == n` → nur Bild |
| | S2 mit **≥ 2** aktiven eigenen Slots | **[E10, A] R4:** **mehrdeutig** — REST löst über die Datums-Identität auf (§6.5) |
| | S0, S1, S3 | **mehrdeutig** (kein aktiver Slot, an dem die Umbenennung hängen kann) |
| `pulled (Id, n?)` | S0, S1 | nichts schreiben; Leave beobachten (heutiges „schon archiviert", E8) |
| | S2, `n` fehlt | genau ein aktiver eigener Slot → diesen archivieren; ≥ 2 → **mehrdeutig** (AK 20) |
| | S2, `n` gesetzt | aktiver eigener Slot `n` → archivieren; **[A] R1:** fehlt er → **mehrdeutig** (Spec: nicht geregelt, F1) |
| | S3 | **[E5]** `n` fehlt oder `n == Emote.Name` → Emote archivieren, Leave (kein Slot existiert, nichts wird falsch gezählt); sonst **mehrdeutig** |

Nach einem Archivieren: war es der letzte aktive eigene Slot, wird das Emote archiviert (3.3) und die Leave beobachtet; sonst bleibt das
Emote aktiv, keine Leave, D13 leitet neu ab (AK 19). Mehrere Pulls einer Id (E6) werden gemeinsam gegen den Ausgangsstand aufgelöst:
„letzter Slot" heißt, dass nach allen Pulls dieser Id kein aktiver eigener Slot bleibt.

- **[E10] Umbenennung in place nur bei genau einem aktiven eigenen Slot (Sol-P1-1).** Gegenbeispiel zur Fassung vor Revision 1: lokal
  `A(t1)`, `B(t2)`; 7TV benennt `A → C` (erster Slot, Dispatch korrekt, lokal in place); ein veralteter REST dreht lokal per Datum auf
  `A`/`B` zurück (§5); 7TV benennt den zweiten Slot `B → D`, der Dispatch trägt `o = B`, `n = C` (Quirk). `C` ist lokal kein
  Geschwistername, die alte Regel nähme `B → C` an — Chat-„C" zählte am Slot `t2`, und der spätere REST benennt die Slots richtig, verschiebt
  aber die gezählten Zeilen nicht. Mit genau einem lokalen aktiven Slot kann der Quirk nur auftreten, wenn 7TV einen zweiten Eintrag hat,
  den wir nicht kennen; dann trägt eine Umbenennung des zweiten Eintrags dessen Namen als `o`, und den haben wir nicht → mehrdeutig. Die
  verbleibende Lücke (lokal nur der **zweite** Eintrag aktiv, der erste fehlt) steht in 14.1.
- **[E12] Vorprüfung gegen den Unique-Index (Sol-P2-1).** Gegenbeispiel: D4-Gleichstand, AK 8 hinterlässt aktives `A(t)` und archiviertes
  `B(t)`, beide autoritativ; eine Umbenennung `A → B` verletzte `IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative`. Mehrdeutig; REST löst
  den Gleichstand nach D4 auf (Umbenennung im Gleichstand = Entfernen + Hinzufügen).

- **[E3] D7-Kandidat** = eigener Slot mit `Name == n`, `IsArchived` und `ArchivedAt ≠ null` — genau die Kandidatenmenge, die REST in §6.2
  Schritt 4 wiederaufnehmen kann. Ein archivierter Slot mit `ArchivedAt = null` (nie im aktiven Set: Backfill-Slot, geleerter Seed nach
  Plan-372 E12) ist kein Kandidat; ein Push unter seinem Namen legt einen provisorischen Slot an, den REST per Namensbindung autorisiert.
- **[E6] Mehrere Änderungen eines Dispatch (Revision 1, Sol-P2-3).** Gleiche Einträge innerhalb einer Art werden vorab dedupliziert. Je
  Id ist dann nur **eine Art** erlaubt: beliebig viele Pushes (verschiedene Namen), beliebig viele Pulls (z. B. 7TVs Entfernen ohne Alias:
  ein Item je Slot) oder **genau eine** Umbenennung. Jede Mischung (Push + Pull, Umbenennung + Pull, Push + Umbenennung, zwei
  Umbenennungen) derselben Id ist **mehrdeutig** (`mixed-change-kinds`). Innerhalb der erlaubten Fälle wird jede Änderung gegen die
  **Ausgangs-Momentaufnahme** aufgelöst, nicht gegen einen Zwischenstand: Pushes prüfen Namensvettern und D7-Kandidaten im Ausgangsstand,
  Pulls finden ihre Ziele im Ausgangsstand, ein Pull-Name kommt höchstens einmal vor. Damit ist das Ergebnis je Id von der Draht-Reihenfolge
  unabhängig. Die Fassung vor Revision 1 behauptete das für „namensdisjunkte" Änderungen; Gegenbeispiel: archiviertes Emote mit
  archiviertem `A`, Dispatch Push `B` + Pull `A` — Push zuerst macht den Pull zum fehlenden Ziel (R1 mehrdeutig), Pull zuerst ergibt Leave
  plus neuen Slot `B`; ob das Emote dazwischen das Set verlassen hat (Leave ja/nein), ist aus dem Dispatch nicht entscheidbar. Ids
  untereinander bleiben unabhängig (verschiedene Emote-Zeilen).
- **[E7] Mehrdeutig ist der ganze Dispatch für das Inventar; Leaves mit eindeutigem Ausgang bleiben (Revision 1, Sol-P1-2, [A] R5).** Eine
  mehrdeutige Änderung irgendeines Emotes → keine Emote- und keine Slot-Schreibung für den ganzen Dispatch (§7.2). **Beobachtet wird
  trotzdem** die Leave jeder gezogenen Id, deren eigener Teilplan eindeutig ist und mit „kein aktiver Slot mehr" endet (letzter Slot
  gezogen, schon archiviert, keine Emote-Zeile, S3-Pull von `Emote.Name`). Grund: `main` beobachtet jede gezogene Id unbedingt; ohne diese
  Ausnahme kann ein Abgang, der im 30-min-Glaubwürdigkeitsfenster liegt und vor dessen Ende zurückkehrt, dauerhaft unbeobachtet bleiben
  (F4), und `Holds` hält eine Tag-Platzierung über einen echten Abgang hinweg. Nicht beobachtet: gezogene Ids, deren eigener Teilplan
  mehrdeutig ist oder deren Emote einen weiteren aktiven Slot behält. Im Shared-Set entscheidet jeder Kanal für sich (Fan-out wie heute je
  Kanal).
- **[E8] Leave-Regel:** beobachtet wird jede gezogene Id, deren Emote nach Anwendung keinen aktiven Slot (eigen oder virtuell) mehr hat —
  also: letzter Slot ging, Emote war schon archiviert, oder es gibt keine Emote-Zeile (heute ebenso). Nicht beobachtet: Id, deren Emote
  aktiv bleibt. Bei mehrdeutigem Dispatch gilt E7.

### 4.3 Grundcodes der Logzeile (englisch, ohne Aliasnamen)

`seed-of-active-set`, `name-missing`, `archived-namesake`, `stale-slots-under-archived-emote`, `virtual-slot` (E5), `rename-source-missing`,
`rename-with-siblings` (E10), `authoritative-key-collision` (E12), `nameless-pull-with-siblings`, `pull-target-missing` (nur mit R1),
`mixed-change-kinds` (E6). Information-Level:
im Einschaltfenster von #379 (Seeds vor dem ersten Tick) sind solche Zeilen erwartbar.

## 5. Grenzfälle

| Fall | Verhalten | Nachweis |
|---|---|---|
| Umbenennung eines Emotes mit genau einem aktiven Slot | in place, gleiche SlotId, Datum bleibt | Integration, Live (iii) |
| Umbenennung eines Emotes mit ≥ 2 aktiven Slots (erster oder zweiter Eintrag, Quirk oder nicht) | mehrdeutig (E10), REST benennt per Datums-Identität um | Integration, Live (v) |
| Sol-P1-1: Umbenennung `A → C`, veralteter REST dreht zurück, Quirk-Umbenennung `B → D` mit `n = C` | mehrdeutig (≥ 2 Slots); kein Zählwert landet an `t2` unter `C` | Integration (mit Flush) |
| Umbenennung eines autoritativen Slots auf den Namen eines archivierten Gleichstands-Slots (`A(t)` aktiv, `B(t)` archiviert, `A → B`) | mehrdeutig (E12), kein 23505 | Integration (Postgres) |
| Push eines zweiten Namens (Alias) eines aktiven Emotes | zweiter provisorischer Slot, `Emote.Name` bleibt beim älteren; zählt nach dem Map-Refresh sofort | Integration, Live (iv) |
| Archiviertes Emote mit archiviertem `A`, ein Dispatch mit Push `B` und Pull `A` (Sol-P2-3) | mehrdeutig (`mixed-change-kinds`); in jeder Reihenfolge der Listen dasselbe Ergebnis | Unit (Permutationen) |
| Mehrdeutige Änderung an `X` und Last-Slot-Pull von `Y` in einem Dispatch, `Y` jung im Set (Sol-P1-2) | kein Inventar geschrieben; Leave für `Y` beobachtet (E7); REST danach archiviert `Y` ohne zweite Leave; Rückkehr von `Y` im Fenster → `Holds` sieht den Abgang | Integration |
| Push eines schon aktiven Namens (Duplikat, REST schneller) | No-op, `NoChange` | Integration |
| Rückkehr nach Archivierung unter altem Namen (D7) | mehrdeutig, nichts geschrieben; REST nimmt denselben Slot mit neuem Datum wieder auf (AK 47) | Integration (REST danach), Live (vii) |
| Rückkehr unter **neuem** Namen nach Archivierung | Emote un-archiviert, neuer provisorischer Slot bei 0 (D4: neuer Eintrag) | Integration |
| Delta auf Emote mit unsettled Seed des aktiven Sets | mehrdeutig (AK 20) | Integration |
| Delta auf Emote mit nur fremden Seeds | Seeds unberührt; Regeln nach S0–S3 | Integration |
| Provisorischer Push-Slot, dann REST mit Datum | REST bindet per Name und autorisiert dieselbe SlotId (§6.2 Schritt 5) | Integration |
| Provisorischer Push-Slot, REST veraltet (Eintrag fehlt noch) | REST archiviert den Slot (und ggf. das Emote, ohne Leave im Fenster); der nächste aktuelle REST nimmt ihn per D7 wieder auf — die gezählten Minuten bleiben am selben Slot | Integration (zweistufig) |
| Umbenennung, dann veralteter REST | REST benennt per Datum kurz zurück, der aktuelle REST wieder vor; gleiche SlotId | Unit/Integration, Bestand #372 |
| Delta während laufendem REST-Resync desselben Kanals | im Worker unmöglich gleichzeitig: beide nehmen das prozesslokale Namens-/Zeilen-Tor; das Delta läuft danach auf dem frischen Stand | Bestand |
| Delta gleichzeitig mit Api-Schreiber (Bericht, Enqueue, Ballot, Backfill-Block, Flush) | Abschnitt 6 | Konkurrenz-Tests |
| Doppelter Dispatch (Wiederholung nach Reconnect) | Push: No-op; Umbenennung: Quelle fehlt → mehrdeutig → Resync (harmlos); Pull: Emote archiviert → nur Leave-Upsert (Zeitstempel wie heute) bzw. mit R1 mehrdeutig | Integration |
| Out-of-order über zwei Dispatches (Pull vor Push desselben Namens) | Pull: S0/S1 → nur Leave, sonst R1 mehrdeutig; danach Push → provisorischer Slot. Falsch aktiv bis zum nächsten REST-Tick (≤ Resync-Intervall) — der periodische Abgleich ist Pflicht, nicht Optimierung | Unit |
| Pull unter veralteten lokalen Namen (lokal `A(t1)`, `B(t2)`, auf 7TV inzwischen `B(t1)`, `D(t2)`) | Pull `B` archiviert lokal `t2` statt `t1`; keine Nutzungszeile wird bewegt, der nächste aktuelle REST bindet `t1` per Datum zurück und archiviert `t2` (§6.2 Schritt 3/7). Pulls schreiben keine Zählung, daher hier kein E10-Gegenstück | Unit, Risiko 14.1 |
| Pull ohne Namen bei genau einem aktiven Slot | dieser Slot, Emote archiviert, Leave | Unit |
| 7TV-Entfernen ohne Alias (zwei `pulled`-Items einer Id) | beide Slots archiviert, Emote archiviert, eine Leave (AK 18, 19) | Parser, Integration, Live (viii) |
| Aktive Slots unter archiviertem Emote (aus einer Flag-aus-Phase) | Push mehrdeutig (E4); Pull nur Leave | Unit |
| Virtueller Zustand nach Restore-Bericht ohne Eintrag (Plan-376 4.2a) | jeder Push mehrdeutig (E5) → REST wendet D7 an; Pull von `Emote.Name` → Emote archiviert, Leave | Unit, Integration (Restore ohne Eintrag → Push → Resync → D7) |
| Shared Set, ein Kanal mehrdeutig, der andere nicht | je Kanal eigenes Ergebnis; nur der mehrdeutige resynct | Integration |
| `user.update` ohne neues Set / REST mit `emote_set: null` | unverändert (`SetNotActive` bzw. heutiger Resync-Pfad); kein Slot-Code berührt | Bestand |
| Implausibler Wipe (Pull aller aktiven Slots) | `ImplausibleSkipped`, Resync | Integration |
| Persistenter REST-Ausfall bei laufender EventAPI | mehrdeutige Dispatches bleiben bis zur Erholung unangewendet (bei Flag aus wären sie angewendet) | — (Risiko 14.5) |

## 6. Nebenläufigkeit

Der Delta-Pfad behält Z8 aus Plan-372 (Kanal → Emotes sortiert → Leave → Save) und schreibt Slots nur unter der Sperre ihres Emotes.

| Gegenüber | Sperren | Ergebnis |
|---|---|---|
| REST-Abgleich (gleicher Worker) | prozesslokales Tor | seriell |
| Berichte mit/ohne Einträge (#376, Flag an) | Kanal `NO KEY UPDATE` → Emote → Leave | warten an der Kanalzeile, kein Zyklus |
| Backfill-Enqueue | Kanal → Nutzer → Emote | wartet an der Kanalzeile |
| Backfill-Block | Kanal `KEY SHARE`, Emote `FOR SHARE` | Kanal verträglich, wartet am Emote; das Delta braucht danach nichts vom Block → kein Zyklus |
| Flush Phase 1 | FK-`KEY SHARE` auf Slot- und Emote-Zeilen | verträglich mit `NO KEY UPDATE` und mit Nicht-Schlüssel-Updates an Slots |
| Flush Phase 2 | nur Flag aus | nicht gleichzeitig mit dem Slot-Pfad |
| Ballot-Platzhalter (`ArchivedEmoteRowUpsert`, ohne Kanalsperre) | Insert einer archivierten Emote-Zeile | Deckungsprüfung E2 → Wiederholung |
| Admin-Purge | kaskadierendes `DELETE` | Kanal-zuerst wie Plan-372 Z6; Kanalzeile weg → `ChannelUnknown` |

Neue Zyklen führt #374 nicht ein. Z2/Z8 im Umschaltfenster deckt die bestehende Deadlock-Wiederholung.

## 7. Tests

Integrationstests `[Collection("Postgres")]`, Flag je Test über den Options-Typ; die bestehenden Delta-Tests laufen **unverändert mit Flag
aus** und sind damit der Aus-Nachweis.

### 7.1 Parser (`SevenTvDispatchParserTests`, Unit, +4 Spec, + eigene) — AK 18

`pushed` → `(Id, Name)` · `updated` mit beiden Namen (vorhandene Echt-Fixture `…-renamed.json`: `FeelsRainMan → rain`) · `pulled` mit zwei
Items einer Id (neue Fixture, aus der Echt-Fixture abgeleitet; T5 ersetzt sie, wenn der Live-Lauf einen echten Frame liefert) · fehlende
Namen `null` (`old_value` ohne `name`, `old_value` fehlt, leerer Name) · bestehende Fälle angepasst (`PulledIds` abgeleitet) · `PulledIds`
ordinal distinct.

### 7.2 Entscheidungsklasse (Unit, neu, BCL-rein) — Kern von AK 19/20

Je Zeile aus 4.2 ein Fall, dazu E3 (archiviert ohne `ArchivedAt` ist kein Kandidat), E4, E5 (jeder S3-Push mehrdeutig, auch mit
D7-Kandidat), E6 (Dedup; zwei Pulls einer Id gegen den Ausgangsstand; jede Mischung zweier Arten derselben Id mehrdeutig; **Permutationen**:
dieselben Änderungen in vertauschter Listenreihenfolge ergeben denselben Plan, u. a. archiviertes `A` mit Push `B` + Pull `A`), E7
(mehrdeutiges `X` plus eindeutiger Last-Slot-Pull `Y` → kein Inventar, Leave `Y`; mehrdeutiger Pull `Y` → keine Leave `Y`; Pull `Y` mit
verbleibendem Slot → keine Leave), E8, E10 (Umbenennung bei einem bzw. zwei aktiven Slots; Quirk-Fall nach zurückgedrehten Namen), E12
(aktiv `A(t)` + archiviert `B(t)`, `A → B`; provisorischer Slot unberührt von E12), D13 nach Push/Umbenennung/Pull (inkl. `null`-Datum
zuletzt), Plausibilitätseingabe, „Applied vs. NoChange"-Signal. Rund 35 Fälle.

### 7.3 `SevenTvSyncServiceDeltaSlotTests` (Integration, neu, Flag an) — AK 19, 20

Die neun Spec-Fälle: (1) Push `(X, probeB)` auf `probeA` → zweiter provisorischer Slot, `Emote.Name = probeA` · (2) Push eines aktiven Namens →
`NoChange`, Slots byte-gleich · (3) Push unter archiviertem Namen → `AmbiguousDelta`, DB byte-gleich, danach `SyncChannelAsync` mit
gefaktem REST `[probeB(t3)]` → dieselbe SlotId, `AddedAt = t3`, Historie bleibt (D7) · (4) Pull `(X, probeA)` → nur `probeA` archiviert,
keine Leave · (5) Pull `(X, probeB)` danach → Emote archiviert, Leave genau einmal · (6) Umbenennung `probeB → probeC` **bei genau einem
aktiven Slot** in place, gleiche SlotId, Datum gleich (mit R4; das Spec-Beispiel mit zwei Slots wird zu Fall 7) · (7) Umbenennung bei zwei
aktiven Slots, sowohl auf den Geschwisternamen (Quirk) als auch auf einen freien Namen → `AmbiguousDelta`, DB byte-gleich · (8) Pull ohne
Namen bei zwei aktiven → `AmbiguousDelta` · (9) Delta auf Emote mit Seed des aktiven Sets → `AmbiguousDelta`.
Zusätzlich (Revision 1): **Sol-P1-1 mit Flush** — `A(t1)`, `B(t2)` autoritativ; Umbenennung `A → C`; `SyncChannelAsync` mit gefaktem,
veraltetem REST `[A(t1), B(t2)]`; Quirk-Dispatch `o = B`, `n = C` → `AmbiguousDelta`; Zähler mit `C` und Flush → keine Zeile am Slot `t2`.
**Sol-P2-1** — aktiv `A(t)`, archiviert `B(t)`, Umbenennung `A → B` → `AmbiguousDelta`, kein 23505, DB byte-gleich. **Sol-P1-2** —
Dispatch mit mehrdeutigem `X` und Last-Slot-Pull von `Y` (Y vor 5 min eingetreten) → `AmbiguousDelta`, Leave `Y` vorhanden, Slots/Emotes
byte-gleich; danach veralteter REST ohne `Y` (archiviert ohne zweite Leave, Fenster), dann REST mit `Y` zurück → die Leave bleibt, eine
Tag-Platzierung von `Y` aus der Zeit davor hält laut `EmoteTagService` nicht mehr. **Sol-P2-2** — Restore-Bericht ohne Eintrag (Plan-376
4.2a) → Push des alten Namens → `AmbiguousDelta` → `SyncChannelAsync` mit REST-Datum → D7, dieselbe SlotId, Map enthält sie. Provisorischer
Slot → REST datiert → gleiche SlotId autoritativ; zwei Pulls einer Id → eine Leave; Shared Set mit einem mehrdeutigen Kanal; Map nach
`Applied` enthält den Push-Slot und ein Flush schreibt auf ihn (Plan-373 E7).

### 7.4 Aus-Nachweis und Konkurrenz

- **Orakel `main` (Revision 1):** T1 schreibt **vor** jeder Änderung am Parser einen Charakterisierungstest des Aus-Pfads auf dem Stand
  `origin/main` — eine Tabelle von Dispatch-Frames (Echt-Fixtures plus synthetische: Quirk-Umbenennung, zwei `pulled`-Items einer Id,
  fehlende Namen, leere Bild-URL, archivierte/Platzhalter-Zeilen) mit den beobachteten Emote-Zeilen, Leaves und Outcomes — und committet
  ihn grün. Nach dem Umbau muss er unverändert grün sein; jede Abweichung ist ein Fehler, keine Testanpassung.
- **Aus, eigen:** Kanal mit Seeds und Mehr-Alias-Emote; Push, Umbenennung (inkl. Quirk-Frame), Pull, zwei Pulls einer Id → `EmoteSlots` vor/nach
  byte-gleich (alle Spalten), Emote-Ergebnis, Leaves und Outcomes wie `main`; nie `AmbiguousDelta`.
- **Bestand:** `SevenTvSyncServiceTests` (Delta-Fälle), `…PlaceholderTests`, `…EmptySetTests`, `…LeaveObservationTests`,
  `…RenameHandoverTests` nur mit dem neuen Delta-Typ gebaut (11 Konstruktionen), Erwartungen unverändert.
- **Konkurrenz** (`SevenTvSyncServiceSlotConcurrencyTests`, `EmoteServiceInSetConcurrencyTests`): `DeltaLockStatements_FollowTheFlag`
  um das Slot-Lesen erweitert (Reihenfolge Kanal → Emote → Lesen); `DeltaPull_AgainstReportOrSync_BothOn_…` mit Slot-Schreibung, beide
  Startreihenfolgen, kein 40P01, Endzustand wie seriell; **neu** Deckungslücke (Interceptor hält das Delta nach der Sperre an, zweite
  Verbindung legt die Emote-Zeile der Push-Id an) → eine Wiederholung, ein Slot; **neu** Emote-Schlüsselkonflikt beim Speichern → eine
  Wiederholung; zweiter Fehlschlag propagiert.

### 7.5 Worker (container-frei) — AK 21

`Worker.Tests`: die Abbildung aus E11 für alle sechs Ergebnisse (`AmbiguousDelta` → Resync mit Übernahme, nie Veröffentlichung). Keine
Fakes des Sockets (Regel 11); der Transport wird live geprüft (8.5).

### 7.6 Nicht nötig

Api.Tests (kein Endpoint, kein Filter), Vitest/E2E (kein `web/`), Migrationstests (keine Migration). `CoreAssemblyReferenceTests` bleibt
grün (neue Core-Typen sind BCL-Records).

## 8. Live-Verifikation (Regel 16) — Freigabe des Betreibers je Anlass

Randbedingungen wie Plan-372/373 §7: geteilte Dev-DB `emotepurge`, geteilter Api-/Worker-Container und Redis gehören einer anderen
Sitzung — **nie `docker compose`, nie etwas im Haupt-Checkout.** Kein Zugriff auf `vps`/`nas`. Der geteilte Dev-Worker kann dieselben
Dispatches des Testkanals mitverarbeiten (eigene DB, beabsichtigt).

| Ressource | Wert |
|---|---|
| DB | `emotepurge_slots374` im Container `emotepurge-dev-postgres`, per `pg_dump \| psql` im Container (Benutzer/Passwort aus dem Container, nie ausgeben, CR abschneiden) |
| Redis | Wegwerf-Container `redis-slots374` auf `127.0.0.1:6392` |
| Prozess | ein Worker per `dotnet run --project src/EmotePurge.Worker` aus dem Worktree, PID notieren; keine Api, kein `:5151` |
| 7TV-Schreibzugriffe | v4-GQL-Mutationen (`addEmote` mit `alias`, `updateEmoteAlias`, `removeEmote` mit und ohne `alias`) mit dem Token aus `~/.config/emotepurge-7tv-test-token`, nur über eine Header-Datei im Scratchpad (Modus 600, `curl -H @datei`), nie `set -x`, nie ausgeben; **jeder POST wird dem Betreiber vorher im Chat vorgelegt und erst nach seiner Freigabe gesendet** |
| Testobjekt | Kanal `olaf_olaf_son`, Emote `01KAMA120V9DC2P1YW9QWFHA16`, Aliase `probeA`…`probeD` |

1. **Vorbereitung:** `df -h /`, DB-Größe; Kopie anlegen; in der Kopie `IsBotActive = false` außer `olaf_olaf_son`; Ausgangszustand des
   aktiven Sets des Testkanals (Einträge, Aliase, `addedAt`) ins Scratchpad; Prüfsumme über `EmoteSlots` (alle Spalten, sortiert).
   Env-Overrides: Kopie, Redis `:6392`, `SevenTv__EventApi__Enabled=true`, `ChatLogBackfill__Enabled=false`, `Retention__Enforce=false`,
   `SevenTv__ResyncIntervalSeconds=1800` (damit der periodische Tick nicht dazwischenfunkt; der **automatische Resync nach
   `AmbiguousDelta` läuft trotzdem sofort** und kann veraltetes REST lesen — deshalb die Tore unten). `redis-cli SUBSCRIBE` auf den
   Live-Event-Kanal mit Zeitstempeln mitschneiden (für AK 21).
   **Tor G (Revision 1, Sol-P2-4)** vor jeder Änderung, die auf einem bestimmten lokalen Stand aufbaut: (a) v3-REST des Sets (lesend, ohne
   Token) pollen, bis es für X genau die erwarteten Aliase listet; (b) Worker per SIGTERM (PID geprüft) stoppen und neu starten — der
   Boot-Sync gleicht den Kanal ab; (c) per SQL prüfen, dass die erwarteten Slots von X mit erwartetem Namen, Archivzustand und
   `AddedAtFromRest` bestehen. Erst dann die nächste 7TV-Änderung zur Freigabe vorlegen. Scheitert (c), wird nicht weitergemacht, sondern
   der Befund protokolliert.
   **Nachweis „Delta schrieb nichts"** bei mehrdeutigen Schritten, getrennt vom Folge-Resync: die Logzeile des Delta-Ergebnisses steht vor
   der Startzeile des Resync; kein Slot und keine Emote-Zeile von X trägt ein `LastSyncedAt` zwischen Dispatch-Empfang und Resync-Beginn;
   im Mitschnitt kein Live-Event in diesem Intervall. Was der Resync danach schreibt oder veröffentlicht, wird gesondert notiert.
2. **Phase A, Flag aus:** Push `probeA`, Umbenennung `probeA → probeB`, Entfernen (je nach Freigabe). Prüfen: `EmoteSlots`-Prüfsumme
   unverändert, Emote-Zeile wie `main` (Name folgt `value.name`), Outcomes `Applied`, keine Zeile `ambiguous`. Danach Set wiederherstellen.
3. **Phase B, Flag an** (Neustart mit `SevenTv__SlotReconcile__Enabled=true`; erster Boot-Sync settelt den Kanal):
   Vorbedingung: Tor G mit „X nicht im Set".
   (i) `addEmote(X, probeA)` → provisorischer Slot `probeA` (`AddedAtFromRest = false`), `Applied`, ein Live-Event ·
   (ii) **Pflicht, vor jeder REST-Autorisierung:** Betreiber tippt `probeA` im Testchat → nach dem Flush eine Zeile an genau dieser SlotId
   (Push-Slot zählt sofort, Übergabe #373) ·
   (iii) `updateEmoteAlias(probeA → probeD)` (einziger Slot) → in place, gleiche SlotId, Datum unverändert (AK 20) ·
   Tor G1: REST listet `probeD` → nach dem Neustart dieselbe SlotId autoritativ, die Zeile aus (ii) hängt weiter an ihr ·
   (iv) `addEmote(X, probeB)` → zweiter provisorischer Slot, `Emote.Name = probeD` (AK 19); Betreiber tippt `probeD probeB` → nach dem Flush je
   eine Zeile an beiden SlotIds (O1) ·
   Tor G2: REST listet `probeD`, `probeB` → beide autoritativ, SlotIds unverändert ·
   (v) `updateEmoteAlias(probeB → probeC)` (zweiter Eintrag, Quirk, zwei Slots) → `AmbiguousDelta` mit `rename-with-siblings`, Nachweis
   „Delta schrieb nichts", Folge-Resync im Log; aus dem Delta kein Live-Event (AK 21) · Tor G3: REST listet `probeD`, `probeC` → der Slot
   `t2` heißt `probeC`, gleiche SlotId, Zeilen unverändert ·
   (vi) `removeEmote(X, alias probeC)` → nur dieser Slot archiviert, Emote aktiv, keine Leave (AK 19) ·
   (vii) `addEmote(X, probeC)` → `AmbiguousDelta` mit `archived-namesake`, Nachweis „Delta schrieb nichts" · Tor G4: REST listet `probeC`
   mit **neuem** `addedAt` → dieselbe SlotId wie der alte `probeC`-Slot, `AddedAt` neu, `ArchivedAt = null`, Zeilen aus (iv) daran (D7,
   AK 19/47; hat schon der Folge-Resync mit frischem REST D7 angewendet, ist das Ergebnis dasselbe) ·
   (viii) `removeEmote(X)` **ohne** Alias → zwei `pulled`-Items im Log, beide Slots archiviert, Emote archiviert, genau eine Leave (AK 18/19).
   Rohe Dispatch-Frames von (v) und (viii) als Fixture-Kandidaten ins Scratchpad (ohne Actor-Daten).
4. **Nicht live herstellbar → nur Test:** Delta auf unsettled Seed (der Boot-Sync settelt vor dem ersten Dispatch), Pull ohne Namen bei
   zwei Slots (7TV sendet immer `old_value.name`), Deckungslücke, Schlüsselkonflikte (Emote-Index, E12), gemischte Arten in einem Dispatch
   (E6), Leave trotz Mehrdeutigkeit (E7, braucht zwei Emotes in einem Dispatch), der Sol-P1-1-Ablauf (braucht einen veralteten REST).
5. **Abbau:** Set auf den Ausgangszustand (Freigabe), Worker per SIGTERM (PID vorher mit `ps -o args=` prüfen), `docker rm -f
   redis-slots374`, `DROP DATABASE emotepurge_slots374 WITH (FORCE)`, Header-Datei löschen (die Token-Datei des Betreibers bleibt).
   Protokoll mit SHA, Zeitpunkten, Ergebnissen je Tor; keine Hostnamen, Passwörter, Tokens, keine Kanalnamen außer `olaf_olaf_son`.

## 9. Prod-Übergabe

Kein Fenster, keine Migration, kein Handgriff. #374 geht mit dem nächsten Main-Deploy bei Flag aus nach Prod. Vorher (Betreiber, Lehre
aus #391): prüfen, ob `main` seit dem Prod-Stand andere Migrationen mitbringt — wenn ja, gehört deren Fenster zu ihrem Kind.
Nachkontrolle ≤ 5 min nach dem Deploy (nur wenn `SEVENTV_EVENTAPI_ENABLED=true` auf Prod): Worker-Log zeigt Dispatches wie vorher
(„7TV-Dispatch auf … angewendet"), **keine** Zeile mit „ambiguous" (bei Flag aus unmöglich), keine Zunahme von „7TV-Dispatch-Verarbeitung
fehlgeschlagen".

## 10. Commits, DECISIONS, Spec, Statuszeilen

Conventional Commits, englisch, ohne Ticketnummern; Tests im Commit ihres Features:
(0) `docs(plans): add plan for emote slot EventAPI deltas` (diese Datei; Revisionen als `docs(plans): revise …`);
(0b) `test(infrastructure): characterize the emote-wide EventAPI delta path` (Orakel, Stand `main`, vor jeder Änderung);
(1) `feat(infrastructure): read old and new names from EventAPI emote set dispatches`;
(2) `feat(infrastructure): plan EventAPI deltas per emote id and name`;
(3) `feat(infrastructure): apply EventAPI deltas per slot behind the slot reconcile flag` **plus DECISIONS, Operations und Spec-Nachtrag**;
(4) `feat(worker): answer an ambiguous EventAPI delta with a REST resync`;
danach `fix:`/`test:`/`docs:` aus Review und Live-Lauf.

- **DECISIONS:** ein datierter Absatz „**Child 4 (EventAPI deltas), 2026-10-1x**" im Entwurfseintrag #365, eingefügt **zwischen** „Child 3"
  und „Child 6" (eigener Hunk; #377 hängt erwartungsgemäß hinter „Child 6" an). Knapp, englisch: Flag-Vertrag (Aus = `main`, E1), die
  Regeltabelle in Kurzform (Zustände H/S0–S3, E3–E8, E10, E12), Sperre/Deckung/Wiederholung (Z8, E2, E9), Worker-Abbildung (E11), die
  bestätigten [A] (R1, R4, R5) als Betreiberentscheidung, R3 als bekannte Grenze, die Restlücke aus 14.1. Die `Betrifft`-Zeile bekommt
  `SevenTvDispatchParser.cs`, `SevenTvModels.cs`, `SevenTvEventClient.cs` und die Datei der Entscheidungsklasse — **als eigene Zeile am Ende
  des `Betrifft`-Blocks**, damit ein Rebase gegen #377 nur additiv auflöst.
- **Operations:** die Flag-Zeile (`docs/Operations.md`, Tabelle bei `SevenTv:SlotReconcile:Enabled`) um je einen Halbsatz ergänzen — Off:
  „EventAPI deltas stay emote-wide and write no slot"; On: „EventAPI deltas add, rename and archive single slots; an ambiguous dispatch
  writes no inventory (only certain last-slot leaves) and triggers a REST resync".
- **Spec (im Commit (3), Betreiber 12.10.):** datierte Nachträge im Stil von Kind 6 („*Geändert in Kind 4 (#374), Betreiber 2026-10-12
  (Plan-374 R1, R4, R5): … Begründung in DECISIONS (#365, Absatz „Child 4").*"), Text bleibt sonst stehen:
  - **§7.2 Tabelle, Zeile `updated`:** in place nur bei genau einem aktiven eigenen Slot; bei ≥ 2 mehrdeutig (R4).
  - **§7.2 Tabelle, Zeile `pulled`:** zusätzlich mehrdeutig, wenn kein aktiver eigener Slot den gezogenen Namen trägt und das Emote aktiv ist (R1).
  - **§7.2 Satz „`AmbiguousDelta` schreibt nichts":** gilt für Emote- und Slot-Zeilen; Leave-Beobachtungen gezogener Ids mit eindeutigem
    Ausgang „kein aktiver Slot mehr" werden trotzdem geschrieben (R5).
  - **§13 AK 20:** das Beispiel „Rename `(X, probeB → probeC)` → in place" gilt für ein Emote mit genau einem Slot; dazu „Rename bei zwei
    aktiven Slots → `AmbiguousDelta`" (R4).
  - **§14 „Zur Kenntnis"** neuer Punkt als Grenze zu D17: ein Push unter dem Namen eines archivierten Slots, jeder Push im virtuellen
    Zustand und jede Umbenennung eines Mehr-Slot-Emotes zählen erst ab dem nächsten aktuellen REST (bis zu 30 min, SevenTV#81) (R3, R4).
  [P]-Punkte nicht in die Spec (DECISIONS genügt).
- **Statuszeilen:** nach dem Merge (Orchestrator, per REST-PATCH, weil `gh issue edit` an Projects-classic scheitert) die Zeile 4 der
  Kindertabelle in Epic #365 („merged (PR #…, sha, Datum); no migration, flag off on prod") und, falls Epic #346 eine #365-Zeile führt,
  deren Stand. Feature-Ideen und `CLAUDE.md` bleiben bei #379.

## 11. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander — Builds teilen sich `obj/`)

| # | Aufgabe | Modell | Aufwand | Input | Fertig, wenn |
|---|---|---|---|---|---|
| T1 | **Zuerst** Orakel des Aus-Pfads auf dem `main`-Stand (7.4), dann Parser, Delta-Modell, `AmbiguousDelta`-Wert, Aus-Zweig auf das neue Modell, 11 Test-Konstruktionen, Parser-Tests | sonnet | 0,75 T | 3.1, E1, 7.1, 7.4 „Orakel"; `SevenTvDispatchParser.cs`, `SevenTvModels.cs`, `SevenTvSyncService.ApplyDeltaAttemptAsync` (nur Aus-Lesestellen), Fixtures | Orakel grün vor dem Umbau (Commit 0b) und unverändert grün danach; Parser-Tests grün (AK 18); alle Delta-Tests unverändert grün; drei Suiten bauen; Commit (1) |
| T2 | Entscheidungsklasse (rein, BCL), je Emote gegen den Ausgangsstand, Wiederverwendung der D13-Ableitung aus `EmoteSlotReconcilePlanner` | **opus** | 1 T | Abschnitt 4 vollständig (E3–E8, E10, E12), 3.3, 7.2; `EmoteSlotReconcilePlanner` (`SlotState`, `Derive`) | 7.2 grün inkl. Permutationen, jede Regel trägt im Code ihren Verweis (§7.2/D17 bzw. Plan-Kürzel), Commit (2) |
| T3 | Integration bei Flag an: Lesen der Slots hinter der Sperre, Seed-Abfrage, Deckung, Plan anwenden, D13, Leave-Regel inkl. E7-Ausnahme (Leave-only-Commit), Plausibilität auf dem Plan, Wiederholung (E9), Slot-Schlüsselkonflikt → `AmbiguousDelta`, Logzeile; **im selben Commit (Regel 3):** DECISIONS-Absatz „Child 4" (zwischen „Child 3" und „Child 6", mit R1/R4/R5 als Betreiberentscheidung und R3 als bekannter Grenze), Operations-Flag-Zeile, Spec-Nachträge §7.2 (`updated`, `pulled`, Satz „schreibt nichts"), AK 20 und §14 (Grenze zu D17) nach Abschnitt 10 | **opus** | 1,5 T | 3.2–3.4, Abschnitte 5, 6, 7.3, 7.4, 10, „Betreiber-Entscheidungen"; Plan-372 2.2 (E24) und Z8; `EmoteTagService` (`Holds`); Ergebnis T1/T2 | 7.3 und 7.4 grün, ganze Infrastructure-Suite grün, Spec- und DECISIONS-Stellen gepflegt, Commit (3) |
| T4 | Worker: Abbildung E11, `AmbiguousDelta`-Zweig, Test | sonnet | 0,25 T | 3.5, 7.5; `SevenTvEventClient.cs` | Worker.Tests grün, Commit (4) |
| T5 | Live-Verifikation mit Toren G–G4 | sonnet | 0,75 T + Betreiberzeit | Abschnitt 8; jeder 7TV-POST erst nach Freigabe im Chat | Protokoll 8.1–8.5 mit Ergebnis je Tor und getrenntem Nachweis „Delta schrieb nichts", Set wiederhergestellt, Abbau erledigt, Fixture-Kandidaten abgelegt |
| T6 | Review, Gates, PR | **opus** (Review); Codex Sol | 0,5 T | Branch, Abschnitte 0, 1.3, 9, 10, 12 | `dotnet test EmotePurge.slnx` grün, `npm --prefix web test -- --watch=false` grün, kein E2E (kein `web/`), `dotnet format` sauber, Probe-`migrations add` leer, erst committen dann `node scripts/coverage-local.mjs --backend-only` ≥ 80 %, `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem Worktree, Befund unverändert vorgelegt (Widerspruch zum Opus-Review → Fable nur über die strittigen Findings), PR mit AK-Tabelle (18–21; 44 anteilig), Kommentare an #375, #378, #379 mit Abschnitt 12 |

Reihenfolge T1 → T2 → T3 → T4 → T5 → T6 (T2 ist dateidisjunkt zu T1 und darf parallel implementiert werden, Testläufe nacheinander). Rund
**4,75 Tage** statt 2,5 aus Spec §17 (Mehraufwand: Orakel, virtueller Zustand, Momentaufnahme-Auflösung und Permutationen, Leave-Ausnahme,
Index-Vorprüfung, Deckung/Wiederholung, Live-Lauf mit vier Toren). Ob T2 oder ein Review von T2/T3 zusätzlich an Fable geht
(Identitätsregeln, hochgehebelt), entscheidet der Orchestrator.

## 12. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#375 (Backfill-Mehrslot):** Ein Push legt bei Flag an einen **provisorischen** aktiven Slot an (`AddedAtFromRest = false`, `AddedAt` =
  Push-Zeit). Eine aktive Vorschau muss einen datierten Eintrag ohne Datumstreffer und ohne D7 an diesen Slot **per Namen binden und sein
  Datum autorisieren** (REST §6.2 Schritt 5) — sonst teilen Push-Slot und Vorschau-Slot denselben 7TV-Eintrag, und der Push-Slot wird beim
  nächsten REST verwaist archiviert. Wer autoritative Slots umbenennt, prüft wie E12 gegen archivierte Gleichstands-Slots.
- **#378 (Löschung je Slot):** Die Lösch-Dispatches (ein `pulled`-Item mit `old_value.name` je entferntem Slot) archivieren bei Flag an genau
  diesen Slot; Leave erst beim letzten. Ein Bericht, der danach eintrifft, findet den Slot archiviert (No-op bzw. B2 aus Plan-376).
  Provisorische Push-Slots erreichen den Löschlauf nie (Spec 9.3). Umbenennungen von Mehr-Slot-Emotes aus dem Import-Pfad
  (`updateEmoteAlias`) kommen als `AmbiguousDelta` zurück und werden per REST nachgezogen (E10) — der Dock darf bis zum nächsten Abgleich
  den alten Namen zeigen.
- **#379 (Betrieb):** Im Einschaltfenster sind `AmbiguousDelta`-Zeilen mit Grund `seed-of-active-set` erwartbar, bis der erste Tick je Kanal
  settelt; danach sollten sie selten sein (häufigster Dauergrund: `rename-with-siblings`). Nachkontrolle: Zahl der Zeilen je Grund über den
  ersten Tag. Der Rückbau der Aus-Zweige umfasst den Delta-Aus-Zweig (E1) und die Einschränkung der Wiederholung auf Deadlocks bei Flag
  aus.

## 13. Rückfragen an den Betreiber

Entschieden am 12.10. (Abschnitt „Betreiber-Entscheidungen (12.10.)"), jeweils wie empfohlen:

- **R1 [A] — Pull eines unbekannten Namens: `AmbiguousDelta`.** Gilt für reine Pull-Dispatches, deren Ausgangsstand (E6) aktive Slots,
  aber keinen mit dem gezogenen Namen hat; Preis: ein zusätzlicher REST-Aufruf bei doppelten Pull-Zustellungen.
- **R3 — Rückkehr eines entfernten Alias zählt bis zum REST-Nachlauf nicht: hingenommen**, als bekannte Grenze in DECISIONS „Child 4" und
  Spec §14 dokumentiert (gilt ebenso für jeden Push im virtuellen Zustand, E5).
- **R4 [A] — Umbenennung in place nur bei genau einem aktiven Slot:** Mehr-Slot-Umbenennungen sind `AmbiguousDelta`, REST benennt per
  Datums-Identität um; das AK-20-Beispiel wird auf einen Slot umgestellt. Preis: der neue Name eines Mehr-Slot-Emotes zählt erst ab dem
  nächsten aktuellen REST.
- **R5 [A] — Leaves trotz mehrdeutigem Dispatch beobachten:** „schreibt nichts" heißt „kein Inventar"; Leaves mit eindeutigem Ausgang „kein
  aktiver Slot mehr" werden geschrieben (E7), als Ergänzung zu Spec §7.2.

Offen:

- **R2 — Freigabe des Live-Laufs.** Abschnitt 8 braucht rund 12 freigegebene 7TV-Schreibzugriffe auf das Set von `olaf_olaf_son` (Phase A:
  3, Phase B: 7, Wiederherstellung: 1–2), vier Worker-Neustarts an den Toren G1–G4 (je bis zu 30 min Warten auf 7TVs REST) und zwei
  Pflicht-Testnachrichten des Betreibers (8.3 ii und iv). Wird vor T5 eingeholt. **Empfehlung:** so freigeben; die Testnachrichten belegen
  als Einzige live, dass ein Push-Slot vor der REST-Autorisierung zählt (Übergabe #373).

## 14. Risiken für das adversariale Review

1. **E10-Restlücke:** Ist lokal nur der **zweite** 7TV-Eintrag eines Emotes aktiv und der erste fehlt (nur nach einer Aus-Phase oder einem
   Sync-Fehler denkbar), nimmt E10 eine Quirk-Umbenennung dieses Slots auf den Namen des ersten Eintrags an. Ohne den Ausgangsstand von 7TV
   ist das aus dem Dispatch nicht erkennbar. Pulls unter veralteten Namen (Grenzfall in 5) können den falschen Slot archivieren, bewegen
   aber keine Zählung; der nächste aktuelle REST heilt das per Datum.
2. **E5 (virtueller Zustand):** jeder Push ist mehrdeutig. Folge nur ein Resync und bis zu 30 min fehlende Zählung (R3), nie ein
   Fehlzustand.
3. **E3 (nur D7-Kandidaten blockieren einen Push):** korrekt, solange REST §6.2 Schritt 4 nur `ArchivedAt ≠ null` wiederaufnimmt; ändert ein
   späteres Kind die D7-Kandidaten, muss E3 mitziehen (Kommentar an beiden Stellen).
4. **E7 (Leave trotz Mehrdeutigkeit):** die Ausnahme beobachtet nur Ids, deren eigener Teilplan eindeutig ist. Ein mehrdeutiger Pull (z. B.
   namenlos bei zwei Slots) bleibt ohne Leave; REST holt sie nur außerhalb des Fensters nach (b1/b2). Restlücke gegenüber `main`, das jede
   gezogene Id beobachtet — dort allerdings auch Ids, deren Emote einen weiteren Slot behält (falsche Leave).
5. **REST-Abhängigkeit:** jede Mehrdeutigkeit hängt an einem REST-Aufruf, dessen Antwort bis zu 30 min veraltet sein kann; bei
   anhaltendem REST-Ausfall bleiben mehrdeutige Änderungen liegen. Mit E10 trifft das jede Umbenennung eines Mehr-Slot-Emotes. Der
   periodische Abgleich bleibt das Netz.
6. **E6:** Reihenfolgeunabhängigkeit ist jetzt konstruktiv (eine Art je Id, Auflösung gegen den Ausgangsstand) und durch Permutationstests
   belegt. Zu prüfen bleibt, ob 7TV je Pushes und Pulls derselben Id in **einem** Dispatch sendet; falls ja, kostet E6 dort einen Resync.
7. **Aus-Identität (E1):** der Parser ändert sich für beide Zustände; ein Fehler dort wirkt sofort auf Prod. Abgesichert durch das Orakel
   auf dem `main`-Stand (7.4), die unveränderten Delta-Tests und den Byte-Vergleich.
8. **Live-Lauf ohne Seed-Fall:** AK 20 „Delta auf unsettled Seed" ist nur integriert getestet; im Einschaltfenster von #379 ist es der
   häufigste Grund.
