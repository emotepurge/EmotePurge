# Plan #374 — Emote-Slots, Kind 4: EventAPI-Deltas je `(Id, Name)`

Erstellt am 2026-10-12 im Worktree `EmotePurge-slots374` (Branch `feat/374-eventapi-slot-deltas`, Basis `origin/main` @ `4b754279`).
**Status: Entwurf (12.10.) — vor dem adversarialen Review; offene Rückfragen R1–R3 (Abschnitt 13).**
Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) (§2.3, §7.1, §7.2, §10, §13 AK 18–21,
§15, §16 Kind 4; D4, D7, D11 b, D12, D13, D14, D17, O5, O6), Issue #374 mit den Übergabekommentaren aus #372 und #373 (bindend),
[Plan-372](Plan-372-Emote-Slots-Reconcile.md) §2.0, §2.2, §2.2a (Z8), §2.5 (E14), §11; [Plan-373](Plan-373-Emote-Slots-Usage-Key.md)
§3.2 (E7), §11; [Plan-376](Plan-376-Emote-Slots-Read-API.md) §4.2a (B1); DECISIONS #365 („Child 2", „Child 3", „Child 6").
Formatvorlage: Plan-373/Plan-376.

**Was dieser Plan trägt:** nur, was Spec und Übergaben offen lassen — Migrationsbefund, Flag-Matrix, die Entscheidungsregeln je
Änderungsart und Emote-Zustand, Sperre und Wiederholung, Leave-Regel, Worker-Rückfall, Tests, Live-Prüfung, Aufgabenfolge. Kein Code:
Namen von Typen und Methoden stehen nur als Form. D0–D33 und die Betreiberentscheidungen aus #371–#376 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **[A]** = Abweichung vom Spec-Wortlaut, braucht Betreiberbestätigung
(Abschnitt 13). **[P]** = Präzisierung, die der Spec-Text offen lässt, ohne ihm zu widersprechen.

## 0. Migrationsbefund (Auftrag A): **keine Migration**

#374 braucht **weder Schema- noch Index- noch Daten-SQL**. Begründung aus Spec und Code:

1. **Alle Spalten existieren seit M1** (`20261011184206_AddEmoteSlots`): ein Push-Slot nach D11 b ist `Name` = Alias,
   `AddedAt = UtcNow`, `AddedAtFromRest = false`, `IsArchived = false`, `SeedUnsettled = false`, `LastSyncedAt` jetzt; eine Umbenennung
   schreibt `Name`/`LastSyncedAt`; ein Pull schreibt `IsArchived`/`ArchivedAt`/`LastSyncedAt`. Das ist genau die Spaltenmenge aus Spec 4.1.
2. **Die Zugriffe sind indiziert:** Slots eines Emotes nach Namen über `IX_EmoteSlots_EmoteId_Name` (M1); „unsettled Seed des aktiven
   Sets" über `SetSeeds.SeedsOfSet` und den seit M2 eindeutigen Slot-Index `IX_UsageStats_SlotId_EmoteSetId_Date`. Provisorische Slots
   berühren den partiellen Unique-Index `IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative` nicht (Filter `AddedAtFromRest`), ein
   Slot-Schlüsselkonflikt ist im Delta-Pfad also unmöglich.
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
| F4 | Spec §7.2 „`AmbiguousDelta` schreibt nichts" | gilt für den ganzen Dispatch; enthält er Pulls anderer Emotes, entfallen auch deren (sonst immer glaubwürdige) Leave-Beobachtungen | wörtlich übernommen, Folge als Risiko (14.3) |
| F5 | Spec §7.2 „Leave nur für IDs, deren letzter aktiver Slot ging oder die schon archiviert waren" | ein Pull einer Id **ohne** Emote-Zeile ist nicht genannt; heute wird er beobachtet | **[P]** weiter beobachtet (E8) |
| F6 | Spec §15 „`SevenTvEventClient`-Dispatch-Test" | der Client ist eine Transportklasse; Regel 11 verifiziert Transporte live, nicht gegen Fakes | **[E]** Entscheidung als reine Abbildung herausgezogen und container-frei getestet (E11) |
| F7 | 7TV-Quelle (`7tv-write-semantics`, SevenTV @ `e1733255`) | `value.timestamp` im Dispatch ist das Hochladedatum des Emotes, **kein** `added_at` (vgl. DECISIONS 2026-08-03) | wird nicht gelesen; Push-Datum bleibt `UtcNow` (O6) |

Die Spec wird nur für bestätigte [A]-Punkte nachgetragen (Abschnitt 10); [P]/[E] stehen im DECISIONS-Absatz.

## 2. Flag-Matrix (Auftrag B)

| `SevenTv:EventApi:Enabled` | `SevenTv:SlotReconcile:Enabled` | Verhalten des Delta-Pfads |
|---|---|---|
| aus | aus oder an | Kein Socket, keine Dispatches; #374 ist inert. Nur der periodische REST-Abgleich (#372) arbeitet. |
| an | **aus** (Prod bis #379) | **Exakt `main`:** emote-weit (Push legt an/un-archiviert, `updated` überschreibt `Emote.Name`, Pull archiviert, Leave je gezogener Id), heutige Sperr- und Transaktionsform, keine Schreibung auf `EmoteSlots`, nie `AmbiguousDelta`, dieselben Ergebnisse und Publikationen. Gezählt wird wie seit #373: die Aus-Map gibt für ein per Delta (re-)aktiviertes Emote den Paar-Slot oder den Platzhalter aus, den erst der Flush auflöst — **der Delta-Pfad selbst legt keinen Seed an** (Plan-373 §11). |
| an | **an** | Slot-Pfad nach Abschnitt 3–4: Regeln je `(Id, Name)`, provisorische Push-Slots, Umbenennung in place, Archivierung je Slot, Leave nur beim letzten Slot; Mehrdeutiges → `AmbiguousDelta` → REST-Resync. Ein angewendetes Delta frischt die Map auf, ein Push-Slot zählt damit sofort (Plan-373 E7, §11). |

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
4. **Entscheidung:** eine reine Klasse plant den Dispatch aus der Momentaufnahme (Abschnitt 4). Ergebnis ist **entweder** „mehrdeutig"
   samt Grund und Id **oder** ein Plan: Emote-Aktionen (anlegen, un-archivieren, archivieren, Bild), Slot-Anlagen, Slot-Änderungen,
   D13-Ableitung je Emote, die Menge der zu beobachtenden Leaves.
5. **Mehrdeutig:** Transaktion zurückrollen, nichts geschrieben (auch keine Leave), englische Information-Zeile mit Kanal, 7TV-Id und
   Grundcode, Ergebnis `AmbiguousDelta`.
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
- Ein Slot-Schlüsselkonflikt ist im Delta-Pfad unmöglich (Abschnitt 0 Punkt 2); er bekommt keinen Zweig.

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
| | S3 | **[E5]** `n == Emote.Name` → **No-op** (das ist der virtuelle Slot); sonst D7-Kandidat `n` → **mehrdeutig**; sonst **mehrdeutig** (ein neuer Slot ließe den virtuellen Namen aus Map und Belegung verschwinden) |
| `updated (Id, o → n)` | `o` oder `n` fehlt | **mehrdeutig** |
| | S2 | aktiver eigener Slot `o` fehlt → **mehrdeutig**; `n ≠ o` und `n` ist Name eines **anderen** aktiven eigenen Slots → **mehrdeutig** (7TV-Quirk: beim Umbenennen des zweiten Slots trägt `value.name` den Namen des ersten); sonst `Name := n` in place, `AddedAt` und Datums-Herkunft unverändert (S1-Sonde: umbenennungsfest), Bild; `o == n` → nur Bild |
| | S0, S1, S3 | **mehrdeutig** (kein aktiver Slot, an dem die Umbenennung hängen kann) |
| `pulled (Id, n?)` | S0, S1 | nichts schreiben; Leave beobachten (heutiges „schon archiviert", E8) |
| | S2, `n` fehlt | genau ein aktiver eigener Slot → diesen archivieren; ≥ 2 → **mehrdeutig** (AK 20) |
| | S2, `n` gesetzt | aktiver eigener Slot `n` → archivieren; **[A] R1:** fehlt er → **mehrdeutig** (Spec: nicht geregelt, F1) |
| | S3 | **[E5]** `n` fehlt oder `n == Emote.Name` → Emote archivieren, Leave; sonst **mehrdeutig** |

Nach einem Archivieren: war es der letzte aktive eigene Slot, wird das Emote archiviert (3.3) und die Leave beobachtet; sonst bleibt das
Emote aktiv, keine Leave, D13 leitet neu ab (AK 19).

- **[E3] D7-Kandidat** = eigener Slot mit `Name == n`, `IsArchived` und `ArchivedAt ≠ null` — genau die Kandidatenmenge, die REST in §6.2
  Schritt 4 wiederaufnehmen kann. Ein archivierter Slot mit `ArchivedAt = null` (nie im aktiven Set: Backfill-Slot, geleerter Seed nach
  Plan-372 E12) ist kein Kandidat; ein Push unter seinem Namen legt einen provisorischen Slot an, den REST per Namensbindung autorisiert.
- **[E6] Mehrere Änderungen eines Dispatch:** gleiche Einträge innerhalb einer Art werden vorab dedupliziert; dann werden die Arten in der
  heutigen Reihenfolge Push → Umbenennung → Pull auf einem Arbeitsstand je Emote angewendet (zwei `pulled`-Items derselben Id — 7TVs
  Entfernen ohne Alias — archivieren beide Slots, das Emote einmal, eine Leave). **Kommt ein Name einer Id in mehr als einer Änderung
  vor** — als Push-Name, Umbenennungs-Quelle, Umbenennungs-Ziel oder Pull-Name, nach der Dedup —, oder trifft ein namenloser Pull eine Id
  mit weiteren Änderungen, ist der Dispatch **mehrdeutig**: die Reihenfolge auf dem Draht ist nach dem Parsen verloren, und nur bei
  namensdisjunkten Änderungen ist das Ergebnis reihenfolgeunabhängig.
- **[E7] Mehrdeutig ist der ganze Dispatch** (§7.2): eine mehrdeutige Änderung irgendeines Emotes → nichts wird geschrieben, auch nicht für
  die übrigen Emotes und Leaves desselben Dispatch. Im Shared-Set entscheidet jeder Kanal für sich (Fan-out wie heute je Kanal).
- **[E8] Leave-Regel:** beobachtet wird jede gezogene Id, deren Emote nach Anwendung keinen aktiven Slot (eigen oder virtuell) mehr hat —
  also: letzter Slot ging, Emote war schon archiviert, oder es gibt keine Emote-Zeile (heute ebenso). Nicht beobachtet: Id, deren Emote
  aktiv bleibt.

### 4.3 Grundcodes der Logzeile (englisch, ohne Aliasnamen)

`seed-of-active-set`, `name-missing`, `archived-namesake`, `stale-slots-under-archived-emote`, `virtual-slot`, `rename-source-missing`,
`rename-onto-active-sibling`, `nameless-pull-with-siblings`, `pull-target-missing` (nur mit R1), `conflicting-changes`. Information-Level:
im Einschaltfenster von #379 (Seeds vor dem ersten Tick) sind solche Zeilen erwartbar.

## 5. Grenzfälle

| Fall | Verhalten | Nachweis |
|---|---|---|
| Delta mit Id und Name bei Umbenennung des ersten Slots (`o` korrekt, `n` korrekt) | in place, gleiche SlotId, Datum bleibt | Integration, Live (v) |
| Umbenennung des zweiten Slots (Quirk: `n` = Name des ersten) | mehrdeutig, REST benennt per Datums-Identität um | Integration, Live (vi) |
| Push eines zweiten Namens (Alias) eines aktiven Emotes | zweiter provisorischer Slot, `Emote.Name` bleibt beim älteren; zählt nach dem Map-Refresh sofort | Integration, Live (iii) |
| Push eines schon aktiven Namens (Duplikat, REST schneller) | No-op, `NoChange` | Integration |
| Rückkehr nach Archivierung unter altem Namen (D7) | mehrdeutig, nichts geschrieben; REST nimmt denselben Slot mit neuem Datum wieder auf (AK 47) | Integration (REST danach), Live (viii) |
| Rückkehr unter **neuem** Namen nach Archivierung | Emote un-archiviert, neuer provisorischer Slot bei 0 (D4: neuer Eintrag) | Integration |
| Delta auf Emote mit unsettled Seed des aktiven Sets | mehrdeutig (AK 20) | Integration |
| Delta auf Emote mit nur fremden Seeds | Seeds unberührt; Regeln nach S0–S3 | Integration |
| Provisorischer Push-Slot, dann REST mit Datum | REST bindet per Name und autorisiert dieselbe SlotId (§6.2 Schritt 5) | Integration |
| Provisorischer Push-Slot, REST veraltet (Eintrag fehlt noch) | REST archiviert den Slot (und ggf. das Emote, ohne Leave im Fenster); der nächste aktuelle REST nimmt ihn per D7 wieder auf — die gezählten Minuten bleiben am selben Slot | Integration (zweistufig) |
| Umbenennung, dann veralteter REST | REST benennt per Datum kurz zurück, der aktuelle REST wieder vor; gleiche SlotId | Unit/Integration, Bestand #372 |
| Delta während laufendem REST-Resync desselben Kanals | im Worker unmöglich gleichzeitig: beide nehmen das prozesslokale Namens-/Zeilen-Tor; das Delta läuft danach auf dem frischen Stand | Bestand |
| Delta gleichzeitig mit Api-Schreiber (Bericht, Enqueue, Ballot, Backfill-Block, Flush) | Abschnitt 6 | Konkurrenz-Tests |
| Doppelter Dispatch (Wiederholung nach Reconnect) | Push: No-op; Umbenennung: Quelle fehlt → mehrdeutig → Resync (harmlos); Pull: Emote archiviert → nur Leave-Upsert (Zeitstempel wie heute) bzw. mit R1 mehrdeutig | Integration |
| Out-of-order (Pull vor Push desselben Namens) | Pull: S0/S1 → nur Leave, sonst R1 mehrdeutig; danach Push → provisorischer Slot. Falsch aktiv bis zum nächsten REST-Tick (≤ Resync-Intervall) — der periodische Abgleich ist Pflicht, nicht Optimierung | Unit |
| Pull ohne Namen bei genau einem aktiven Slot | dieser Slot, Emote archiviert, Leave | Unit |
| 7TV-Entfernen ohne Alias (zwei `pulled`-Items einer Id) | beide Slots archiviert, Emote archiviert, eine Leave (AK 18, 19) | Parser, Integration, Live (ix) |
| Aktive Slots unter archiviertem Emote (aus einer Flag-aus-Phase) | Push mehrdeutig (E4); Pull nur Leave | Unit |
| Virtueller Zustand nach Restore-Bericht ohne Eintrag (Plan-376 4.2a) | Push des alten Namens: D7-Kandidat → mehrdeutig; Push von `Emote.Name` ohne Kandidat → No-op; Pull von `Emote.Name` → Emote archiviert | Unit |
| Shared Set, ein Kanal mehrdeutig, der andere nicht | je Kanal eigenes Ergebnis; nur der mehrdeutige resynct | Integration |
| `user.update` ohne neues Set / REST mit `emote_set: null` | unverändert (`SetNotActive` bzw. heutiger Resync-Pfad); kein Slot-Code berührt | Bestand |
| Implausibler Wipe (Pull aller aktiven Slots) | `ImplausibleSkipped`, Resync | Integration |
| Persistenter REST-Ausfall bei laufender EventAPI | mehrdeutige Dispatches bleiben bis zur Erholung unangewendet (bei Flag aus wären sie angewendet) | — (Risiko 14.4) |

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

Je Zeile aus 4.2 ein Fall, dazu E3 (archiviert ohne `ArchivedAt` ist kein Kandidat), E4, E5, E6 (Dedup; zwei Pulls einer Id; gleicher Name in
zwei Arten), E7 (ein mehrdeutiges Emote macht den Dispatch mehrdeutig), E8 (Leave-Menge), D13 nach Push/Umbenennung/Pull (inkl.
`null`-Datum zuletzt), Plausibilitätseingabe, „Applied vs. NoChange"-Signal. Rund 25 Fälle.

### 7.3 `SevenTvSyncServiceDeltaSlotTests` (Integration, neu, Flag an) — AK 19, 20

Die neun Spec-Fälle: (1) Push `(X, probeB)` auf `probeA` → zweiter provisorischer Slot, `Emote.Name = probeA` · (2) Push eines aktiven Namens →
`NoChange`, Slots byte-gleich · (3) Push unter archiviertem Namen → `AmbiguousDelta`, DB byte-gleich, danach `SyncChannelAsync` mit
gefaktem REST `[probeB(t3)]` → dieselbe SlotId, `AddedAt = t3`, Historie bleibt (D7) · (4) Pull `(X, probeA)` → nur `probeA` archiviert,
keine Leave · (5) Pull `(X, probeB)` danach → Emote archiviert, Leave genau einmal · (6) Umbenennung `probeB → probeC` in place, gleiche
SlotId, Datum gleich · (7) Umbenennung auf aktiven Geschwisternamen → `AmbiguousDelta` · (8) Pull ohne Namen bei zwei aktiven →
`AmbiguousDelta` · (9) Delta auf Emote mit Seed des aktiven Sets → `AmbiguousDelta`. Zusätzlich: provisorischer Slot → REST datiert →
gleiche SlotId autoritativ; zwei Pulls einer Id → eine Leave; Shared Set mit einem mehrdeutigen Kanal; Map nach `Applied` enthält den
Push-Slot (Plan-373 E7); mehrdeutiger Dispatch mit Pull eines zweiten Emotes schreibt auch dessen Leave nicht (E7).

### 7.4 Aus-Nachweis und Konkurrenz

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
   `SevenTv__ResyncIntervalSeconds=1800` (damit nur Deltas wirken; REST-Prüfungen erzwingt ein Worker-Neustart, dessen Boot-Sync den Kanal
   abgleicht). `redis-cli SUBSCRIBE` auf den Live-Event-Kanal mitschneiden (für AK 21).
2. **Phase A, Flag aus:** Push `probeA`, Umbenennung `probeA → probeB`, Entfernen (je nach Freigabe). Prüfen: `EmoteSlots`-Prüfsumme
   unverändert, Emote-Zeile wie `main` (Name folgt `value.name`), Outcomes `Applied`, keine Zeile `ambiguous`. Danach Set wiederherstellen.
3. **Phase B, Flag an** (Neustart mit `SevenTv__SlotReconcile__Enabled=true`; erster Boot-Sync settelt den Kanal):
   (i) `addEmote(X, probeA)` → provisorischer Slot `probeA`, `Applied`, ein Live-Event · (ii) v3-REST pollen (lesend, ohne Token), bis
   `probeA` gelistet ist; Worker neu starten → dieselbe SlotId, `AddedAtFromRest = true`, `AddedAt` = 7TV-`addedAt` · (iii) `addEmote(X, probeB)` →
   zweiter provisorischer Slot, `Emote.Name = probeA` (AK 19) · (iv) optional: Betreiber tippt `probeA probeB` im Testchat → nach dem Flush
   zwei Zeilen an den beiden SlotIds (Push-Slot zählt sofort) · (v) `updateEmoteAlias(probeA → probeD)` (erster Slot) → in place, gleiche
   SlotId und gleiches Datum (AK 20) · (vi) `updateEmoteAlias(probeB → probeC)` (zweiter Slot, Quirk) → `AmbiguousDelta`-Zeile,
   unmittelbarer Resync im Log, **kein** Live-Event aus dem Delta (AK 21) · (vii) `removeEmote(X, alias probeC)` → nur dieser Slot
   archiviert, keine Leave (AK 19) · (viii) `addEmote(X, probeC)` → `AmbiguousDelta` (archivierter Namensvetter), DB unverändert; nach
   REST-Nachlauf und Neustart dieselbe SlotId mit neuem Datum (D7, AK 19/47) · (ix) `removeEmote(X)` **ohne** Alias → zwei `pulled`-Items im
   Log, beide Slots archiviert, Emote archiviert, genau eine Leave (AK 18/19). Rohe Dispatch-Frames von (vi) und (ix) als Fixture-Kandidaten
   ins Scratchpad (ohne Actor-Daten).
4. **Nicht live herstellbar → nur Test:** Delta auf unsettled Seed (der Boot-Sync settelt vor dem ersten Dispatch), Pull ohne Namen bei
   zwei Slots (7TV sendet immer `old_value.name`), Deckungslücke, Schlüsselkonflikt.
5. **Abbau:** Set auf den Ausgangszustand (Freigabe), Worker per SIGTERM (PID vorher mit `ps -o args=` prüfen), `docker rm -f
   redis-slots374`, `DROP DATABASE emotepurge_slots374 WITH (FORCE)`, Header-Datei löschen (die Token-Datei des Betreibers bleibt).
   Protokoll mit SHA, Zeitpunkten, Ergebnissen; keine Hostnamen, Passwörter, Tokens, keine Kanalnamen außer `olaf_olaf_son`.

## 9. Prod-Übergabe

Kein Fenster, keine Migration, kein Handgriff. #374 geht mit dem nächsten Main-Deploy bei Flag aus nach Prod. Vorher (Betreiber, Lehre
aus #391): prüfen, ob `main` seit dem Prod-Stand andere Migrationen mitbringt — wenn ja, gehört deren Fenster zu ihrem Kind.
Nachkontrolle ≤ 5 min nach dem Deploy (nur wenn `SEVENTV_EVENTAPI_ENABLED=true` auf Prod): Worker-Log zeigt Dispatches wie vorher
(„7TV-Dispatch auf … angewendet"), **keine** Zeile mit „ambiguous" (bei Flag aus unmöglich), keine Zunahme von „7TV-Dispatch-Verarbeitung
fehlgeschlagen".

## 10. Commits, DECISIONS, Spec, Statuszeilen

Conventional Commits, englisch, ohne Ticketnummern; Tests im Commit ihres Features:
(0) `docs(plans): add plan for emote slot EventAPI deltas` (diese Datei; Revisionen als `docs(plans): revise …`);
(1) `feat(infrastructure): read old and new names from EventAPI emote set dispatches`;
(2) `feat(infrastructure): plan EventAPI deltas per emote id and name`;
(3) `feat(infrastructure): apply EventAPI deltas per slot behind the slot reconcile flag` **plus DECISIONS und Operations**;
(4) `feat(worker): answer an ambiguous EventAPI delta with a REST resync`;
danach `fix:`/`test:`/`docs:` aus Review und Live-Lauf.

- **DECISIONS:** ein datierter Absatz „**Child 4 (EventAPI deltas), 2026-10-1x**" im Entwurfseintrag #365, eingefügt **zwischen** „Child 3"
  und „Child 6" (eigener Hunk; #377 hängt erwartungsgemäß hinter „Child 6" an). Knapp, englisch: Flag-Vertrag (Aus = `main`, E1), die
  Regeltabelle in Kurzform (Zustände H/S0–S3, E3–E8), Sperre/Deckung/Wiederholung (Z8, E2, E9), Worker-Abbildung (E11), bestätigte [A].
  Die `Betrifft`-Zeile bekommt `SevenTvDispatchParser.cs`, `SevenTvModels.cs`, `SevenTvEventClient.cs` und die Datei der
  Entscheidungsklasse — **als eigene Zeile am Ende des `Betrifft`-Blocks**, damit ein Rebase gegen #377 nur additiv auflöst.
- **Operations:** die Flag-Zeile (`docs/Operations.md`, Tabelle bei `SevenTv:SlotReconcile:Enabled`) um je einen Halbsatz ergänzen — Off:
  „EventAPI deltas stay emote-wide and write no slot"; On: „EventAPI deltas add, rename and archive single slots; an ambiguous dispatch
  writes nothing and triggers a REST resync".
- **Spec:** nur bei bestätigtem R1 ein datierter Nachtrag in §7.2 im Stil von Kind 6 („Geändert in Kind 4 (#374), Betreiber …"). [P]-Punkte
  nicht in die Spec (DECISIONS genügt).
- **Statuszeilen:** nach dem Merge (Orchestrator, per REST-PATCH, weil `gh issue edit` an Projects-classic scheitert) die Zeile 4 der
  Kindertabelle in Epic #365 („merged (PR #…, sha, Datum); no migration, flag off on prod") und, falls Epic #346 eine #365-Zeile führt,
  deren Stand. Feature-Ideen und `CLAUDE.md` bleiben bei #379.

## 11. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander — Builds teilen sich `obj/`)

| # | Aufgabe | Modell | Aufwand | Input | Fertig, wenn |
|---|---|---|---|---|---|
| T1 | Parser, Delta-Modell, `AmbiguousDelta`-Wert, Aus-Zweig auf das neue Modell, 11 Test-Konstruktionen, Parser-Tests | sonnet | 0,5 T | 3.1, E1, 7.1; `SevenTvDispatchParser.cs`, `SevenTvModels.cs`, `SevenTvSyncService.ApplyDeltaAttemptAsync` (nur Aus-Lesestellen), Fixtures | Parser-Tests grün (AK 18), alle Delta-Tests unverändert grün, drei Suiten bauen (der neue Wert wird in T1 noch nicht erzeugt), Commit (1) |
| T2 | Entscheidungsklasse (rein, BCL), Wiederverwendung der D13-Ableitung aus `EmoteSlotReconcilePlanner` | **opus** | 0,75 T | Abschnitt 4 vollständig, 3.3, E3–E8, 7.2; `EmoteSlotReconcilePlanner` (`SlotState`, `Derive`) | 7.2 grün, jede Regel trägt im Code ihren Verweis (§7.2/D17 bzw. Plan-Kürzel), Commit (2) |
| T3 | Integration bei Flag an: Lesen der Slots hinter der Sperre, Seed-Abfrage, Deckung, Plan anwenden, D13, Leave-Regel, Plausibilität auf dem Plan, Wiederholung (E9), Logzeile; DECISIONS, Operations, ggf. Spec | **opus** | 1,25 T | 3.2–3.4, Abschnitte 5, 6, 7.3, 7.4, 10; Plan-372 2.2 (E24) und Z8; Ergebnis T1/T2 | 7.3 und 7.4 grün, ganze Infrastructure-Suite grün, Commit (3) |
| T4 | Worker: Abbildung E11, `AmbiguousDelta`-Zweig, Test | sonnet | 0,25 T | 3.5, 7.5; `SevenTvEventClient.cs` | Worker.Tests grün, Commit (4) |
| T5 | Live-Verifikation | sonnet | 0,5 T + Betreiberzeit | Abschnitt 8; jeder 7TV-POST erst nach Freigabe im Chat | Protokoll 8.1–8.5, Set wiederhergestellt, Abbau erledigt, Fixture-Kandidaten abgelegt |
| T6 | Review, Gates, PR | **opus** (Review); Codex Sol | 0,5 T | Branch, Abschnitte 0, 1.3, 9, 10, 12 | `dotnet test EmotePurge.slnx` grün, `npm --prefix web test -- --watch=false` grün, kein E2E (kein `web/`), `dotnet format` sauber, Probe-`migrations add` leer, erst committen dann `node scripts/coverage-local.mjs --backend-only` ≥ 80 %, `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem Worktree, Befund unverändert vorgelegt (Widerspruch zum Opus-Review → Fable nur über die strittigen Findings), PR mit AK-Tabelle (18–21; 44 anteilig), Kommentare an #375, #378, #379 mit Abschnitt 12 |

Reihenfolge T1 → T2 → T3 → T4 → T5 → T6 (T2 ist dateidisjunkt zu T1 und darf parallel implementiert werden, Testläufe nacheinander). Rund
3,75 Tage statt 2,5 aus Spec §17 (Mehraufwand: virtueller Zustand, Deckung/Wiederholung, Aus-Nachweis, Live-Lauf mit drei REST-Nachläufen).
Ob T2 oder ein Review von T2/T3 zusätzlich an Fable geht (Identitätsregeln, hochgehebelt), entscheidet der Orchestrator.

## 12. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#375 (Backfill-Mehrslot):** Ein Push legt bei Flag an einen **provisorischen** aktiven Slot an (`AddedAtFromRest = false`, `AddedAt` =
  Push-Zeit). Eine aktive Vorschau muss einen datierten Eintrag ohne Datumstreffer und ohne D7 an diesen Slot **per Namen binden und sein
  Datum autorisieren** (REST §6.2 Schritt 5) — sonst teilen Push-Slot und Vorschau-Slot denselben 7TV-Eintrag, und der Push-Slot wird beim
  nächsten REST verwaist archiviert.
- **#378 (Löschung je Slot):** Die Lösch-Dispatches (ein `pulled`-Item mit `old_value.name` je entferntem Slot) archivieren bei Flag an genau
  diesen Slot; Leave erst beim letzten. Ein Bericht, der danach eintrifft, findet den Slot archiviert (No-op bzw. B2 aus Plan-376).
  Provisorische Push-Slots erreichen den Löschlauf nie (Spec 9.3).
- **#379 (Betrieb):** Im Einschaltfenster sind `AmbiguousDelta`-Zeilen mit Grund `seed-of-active-set` erwartbar, bis der erste Tick je Kanal
  settelt; danach sollten sie selten sein. Nachkontrolle: Zahl der Zeilen je Grund über den ersten Tag. Der Rückbau der Aus-Zweige umfasst
  den Delta-Aus-Zweig (E1) und die Einschränkung der Wiederholung auf Deadlocks bei Flag aus.

## 13. Rückfragen an den Betreiber

- **R1 [A] — Pull eines unbekannten Namens.** Ein `pulled (X, n)` trifft ein aktives Emote mit aktiven Slots, aber keinem namens `n` (verpasste
  Umbenennung, veralteter Stand nach einer Aus-Phase, doppelte Zustellung). Spec: nicht geregelt, wörtlich gelesen ein No-op — der veraltete
  Slot bliebe aktiv und zählbar bis zum nächsten REST-Tick. **Empfehlung: `AmbiguousDelta`** (sofortiger Resync, symmetrisch zu
  „Umbenennung: Slot nicht gefunden"); Preis: ein zusätzlicher REST-Aufruf bei doppelten Pull-Zustellungen.
- **R2 — Umfang der Live-Prüfung.** Abschnitt 8 braucht rund 12 freigegebene 7TV-Schreibzugriffe auf das Set von `olaf_olaf_son` (Phase A:
  3, Phase B: 7, Wiederherstellung: 1–2) und optional zwei getippte Testnachrichten (8.3 iv). **Empfehlung:** so freigeben; Schritt (iv)
  mitnehmen, weil er die Übergabe aus #373 („Push-Slot zählt sofort") als einziger live belegt.
- **R3 — Rückkehr eines entfernten Alias zählt bis zum REST-Nachlauf nicht.** Ein Push unter dem Namen eines archivierten Slots ist nach D17
  mehrdeutig und schreibt nichts; ist 7TVs REST-Cache veraltet (10–30 min, SevenTV#81), bleibt das Emote so lange archiviert und ungezählt —
  heute (Flag aus) zählt es sofort. Ein provisorischer Slot würde die Historie spalten (D7), eine Zählung ohne Slot gibt es bei Flag an nicht
  (Plan-373 E7). **Empfehlung: hinnehmen** (Spec-Folge von D17, selten, nach dem nächsten aktuellen REST-Tick geheilt); im DECISIONS-Absatz
  als bekannte Grenze.

## 14. Risiken für das adversariale Review

1. **E5 (virtueller Zustand):** Push eines neuen Namens ist mehrdeutig statt „neuer Slot". Angreifbar, wenn der Zustand häufiger ist als
   angenommen (nach #376 4.2a: Restore ohne Eintrag). Folge nur ein Resync, nie ein Fehlzustand.
2. **E3 (nur D7-Kandidaten blockieren einen Push):** korrekt, solange REST §6.2 Schritt 4 nur `ArchivedAt ≠ null` wiederaufnimmt; ändert ein
   späteres Kind die D7-Kandidaten, muss E3 mitziehen (Kommentar an beiden Stellen).
3. **E7/F4 (ganzer Dispatch mehrdeutig):** die immer glaubwürdigen Leaves anderer gezogener Emotes desselben Dispatch entfallen; der
   REST-Abgleich beobachtet sie nur außerhalb des 30-min-Fensters (b1/b2). Selten (7TV schickt je Mutation einen Dispatch), aber eine
   bewusste Lücke gegenüber `main`.
4. **REST-Abhängigkeit:** jede Mehrdeutigkeit hängt an einem REST-Aufruf, dessen Antwort bis zu 30 min veraltet sein kann; bei
   anhaltendem REST-Ausfall bleiben mehrdeutige Änderungen liegen. Der periodische Abgleich bleibt das Netz.
5. **E6 (Reihenfolge Push → Umbenennung → Pull):** nach dem Parsen ist die Draht-Reihenfolge verloren. E6 behauptet, dass namensdisjunkte
   Änderungen einer Id reihenfolgeunabhängig sind, und erklärt alles andere für mehrdeutig (z. B. Umbenennung `a → b` plus Push `a`). Das
   Review sollte die Behauptung gegen die Regeltabelle 4.2 prüfen (der namenlose Pull neben weiteren Änderungen derselben Id ist
   deshalb ausdrücklich mehrdeutig).
6. **Aus-Identität (E1):** der Parser ändert sich für beide Zustände; ein Fehler dort wirkt sofort auf Prod. Deshalb laufen alle
   bestehenden Delta-Tests unverändert mit Flag aus, plus der Byte-Vergleich in 7.4.
7. **Live-Lauf ohne Seed-Fall:** AK 20 „Delta auf unsettled Seed" ist nur integriert getestet; im Einschaltfenster von #379 ist es der
   häufigste Grund.
