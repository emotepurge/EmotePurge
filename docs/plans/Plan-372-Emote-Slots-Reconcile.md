# Plan #372 — Emote-Slots, Kind 2: REST-Abgleich je Slot mit Datums-Identität und Wiederaufnahme

Erstellt am 2026-10-11 im Worktree `EmotePurge-slots` (Branch `feat/372-slot-reconcile`, Basis `origin/main` @ `38e640c8`).
**Status: Revision 1 (12.10.) nach dem adversarialen Review (Codex Sol) und den Betreiberentscheidungen R1–R3; keine offenen
Rückfragen.** Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md)
(§6.2–6.5, §4.1, §5.4, §10, §13 AK 5–13, 27, 29, 32, 47–50, §15, §16 Kind 2), Issue #372 samt Übergabekommentar aus #371, Epic #365.
Formatvorlage: [Plan-371](Plan-371-Emote-Slots-M1.md).

**Was dieser Plan trägt:** nur, was die Spec offen lässt — Schrittfolge im Abgleich, Settle-Mechanik, Sperre und Konfliktpfad,
Leser-Regeln für den Übergang, Feature-Flag, Tests, Live-Prüfung, Gates, Aufgabenfolge. Kein Code. Bei Abweichung gilt die Spec; der
Fund wird gemeldet. D0–D33 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **Betreiber 12.10.** = vom Betreiber entschieden, nicht mehr offen.

### Revision 1 (12.10.) — was sich geändert hat

| Änderung | beantwortet |
|---|---|
| Deckungsprüfung nach dem Reload: jede schreibbare Emote-Zeile muss gesperrt sein, sonst Neustart der Transaktion (höchstens zweimal) — E24, 2.2; deterministischer Test „Eltern-Insert zwischen Sperre und Lesen" mit konkurrierender Alias-Beobachtung, 6.5 (2) | Sol P1 |
| Sperrvertrag: `FOR NO KEY UPDATE` als dokumentierte Abweichung (R1, Betreiber 12.10.); Sync nimmt die Kanalzeile zuerst (E23), der Legacy-Bericht sperrt bei Flag an seine Emote-Zeilen vorab (E25), Leave-Zeilen ordinal sortiert; verbleibende Zyklen mit Erholung je Teilnehmer, auch `MarkInSetAsync` (2.2a, 2.9); echte Deadlock-Tests mit wechselndem Opfer, 6.5 (4)–(7) | Sol P2 (Sperrvertrag) |
| E9 ersetzt: `Emote.FirstSeenAt` folgt strikt D13 einschließlich `null`; unveränderte Slots behalten ihr Datum ohnehin (2.3 Schritt 8); Test undatierter Ersatz eines datierten Slots | Sol P2 (FirstSeenAt) |
| Null-Gate der Live-Prüfung über Live-Gruppen-Mitgliedschaft definiert, getrennte Unverändert-Prüfungen für abwesende Emotes und fremde Sets, Fixture „archiviertes Emote mit Nutzung im aktiven Set" (7.6, 6.3 Fall 20) | Sol P2 (Null-Gate) |
| Erhaltungsprüfung nur über `UsageStats` mit `Date < D0` (Flush schreibt nur den laufenden Tag), Population definiert (7.3) | Sol P2 (Isolation), R2 |
| Feature-Flag `SevenTv:SlotReconcile:Enabled`, Default aus; Aus = heutiges Verhalten in Abgleich, Overlay, Lesern und Bericht (2.0); Tests für beide Zustände; Prod-Übergang als Randfall | R3 (Betreiber 12.10.) |
| Live-Prüfung freigegeben (R2), Testkanal `olaf_olaf_son`, Set-Änderungen per 7TV-Schreib-API mit Token des Betreibers, Handweg als Rückfall (7) | R2 (Betreiber 12.10.) |
| Aufgabenliste: Flag in T1, eigene Aufgabe T5 für den Berichtspfad; `SevenTvSyncServiceDuplicateEmoteIdTests` bleibt als Aus-Nachweis | Folge |

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 2)

1. **Feature-Flag** `SevenTv:SlotReconcile:Enabled` (Default `false`, Betreiber 12.10.) mit vollständiger Verdrahtung (2.0).
2. **Overlay** `GetSetEntriesAsync`: Query selektiert `alias`, Overlay nach `(emoteId, alias)` (D14, §6.4); AK 11, AK 29.
3. **`ReconcileAsync` je Slot** ohne Dedup (ersetzt K1 bei Flag an): Datums-Identität, D7, Namens-Bindung, neue Slots, Archivierung, D13.
4. **Settle** nur des Seeds des aktiven Sets (§6.3, D12), einschließlich des #371-Randfalls „Seed neben REST-Slot".
5. **Sperre**: Kanalzeile, dann owning `Emote`-Zeilen in der Abgleichstransaktion, danach Slotgruppen neu lesen und Deckung prüfen
   (§6.5); `IsSlotKeyConflict`; Wiederholungspfad.
6. **Legacy-Bericht** `EmoteService.MarkInSetAsync`: Sperrreihenfolge an den Abgleich angleichen (Flag an) und Erholung der ganzen
   Transaktion bei Deadlock (beide Zustände).
7. **`ArchivedSlotUpsert`** (neu, §18) als Baustein für Kind 3/5.
8. **Leser über Slots** (Flag an): `OccupiedSlots`, `DuplicateNames` (D27, D15; AK 32 vorgezogen) und der Namenskollisions-Tracker.
9. **Keine Migration.** Zähl-Map, Zähler, Flush und Backfill-Block bleiben auf dem Emote-Schlüssel (D19); die Map wird weiter aus
   `Emote.Name` gebaut.

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 3 (#373) | M2, Map `Name→SlotId` aus gesettelten Slots, Zähler/Flush je Slot, Kaltstart, Information-Zeile „ausgelassene Seeds" (§6.1 Punkt 1), Enqueue-Basisbindung |
| Kind 4 (#374) | EventAPI je `(Id, Name)`: der Delta-Pfad bleibt in #372 in beiden Flag-Zuständen **unverändert emote-weit** und schreibt keine Slots |
| Kind 5 (#375) | Enqueue-Hälfte von AK 27, D7 für Vorschauen, Backfill-Settle nicht aktiver Sets, 409 |
| Kind 6 (#376) | Lese-API je Slot, Berichte mit `Entries` (Sperre-dann-Lesen), Sichtbarkeit von Seeds in `/totals` |
| Kind 9 (#379) | **Flag auf Prod einschalten**, DECISIONS final, Operations (Nachkontrolle „settle deferred"), Prod-Kopie-Lauf, Rückbau der Aus-Zweige |

### 1.3 Abweichungen von der Spec

- **[E1] Sperrmodus `FOR NO KEY UPDATE` statt `FOR UPDATE` — Betreiber 12.10. (R1).** Spec §6.5, D28, D32 nennen `FOR UPDATE`. Beide
  Modi schließen sich gegenseitig und sich selbst aus; die Serialisierung jeder autoritativen Auflösung desselben Emotes (Zweck von
  §6.5) bleibt vollständig, auch wenn Kind 5/6 `FOR UPDATE` nehmen. `NO KEY UPDATE` blockiert aber nicht das `KEY SHARE` der
  FK-Prüfungen auf `Emotes` (Flush, Backfill-`ReplaceBlockAsync`, Ballot-Zeilen) — genau deshalb müssen Flush und Backfill in #372 nicht
  angefasst werden (D19). Präzedenz: `ChannelQueries.LockChannelByIdAsync`. Was an Zyklen bleibt und wer sich erholt: 2.2a.
- **[E26] Feature-Flag — Betreiber 12.10. (R3).** Die Spec kennt kein Flag. Grund: M1 läuft seit 11.10. ~23:30 auf Prod (Deploy
  `6806c12f`, 11.667 Seeds, alle unsettled; die Main-Images schreiben bis M2 weiter `SlotId NULL`). Jeder Merge von #372 erreicht Prod
  mit dem nächsten Main-Deploy; das Flag hält den Slot-Abgleich dort aus, bis Kind 9 es einschaltet.
- Die Spec wird nicht editiert; Abweichungen im PR-Text und im DECISIONS-Nachtrag (Abschnitt 9).

## 2. Verträge

### 2.0 Feature-Flag `SevenTv:SlotReconcile:Enabled` (E26)

- **Verdrahtung wie `SevenTv:EventApi:Enabled`/`ChatLogBackfill:Enabled`:** Schlüssel mit `false` in `appsettings.json` von Api **und**
  Worker; in `docker-compose.yml` und `docker-compose.prod.yml` bei api **und** worker
  `SevenTv__SlotReconcile__Enabled=${SEVENTV_SLOT_RECONCILE_ENABLED:-false}`; `.env.example` mit `false` und Kommentar; eine Zeile in
  `docs/Operations.md` (Tabellenform wie „Chat-log backfill", Aussage „read by api and worker, set both together, restart required").
- **Ein Lesepunkt:** ein kleiner Options-Typ in Infrastructure, registriert in `AddEmotePurgeInfrastructure`, beim Start einmal gelesen,
  kein Hot-Reload. Die Schlüsselkonstante steht dort und nirgends sonst.
- **Aus (Default) heißt exakt heutiges Verhalten:**
  - *Overlay:* Daten wie heute je Emote-Id (letzter Treffer in Seitenreihenfolge gewinnt); das zusätzlich selektierte `alias` bleibt
    ungenutzt.
  - *Abgleich:* der heutige emote-weite Pfad einschließlich K1-Dedup und heutiger Sperrreihenfolge (keine Kanal-/Emote-Vorabsperre);
    **keine** Schreibung auf `EmoteSlots`, keine auf `UsageStats.SlotId`; Seeds bleiben unberührt.
  - *Leser* `OccupiedSlots`, `DuplicateNames`, Tracker: die heutigen emote-weiten Abfragen (bleiben als Aus-Zweig erhalten). Grund:
    eigene Slots aus einer früheren An-Phase würden im Aus-Zustand nicht gepflegt und wären veraltet.
  - *Legacy-Bericht:* heutige Reihenfolge (Leave → Emote).
  - *EventAPI-Delta:* unverändert (in beiden Zuständen).
- **An:** alles aus 2.1–2.9.
- **In beiden Zuständen [E27]:** die Deadlock-Erholung (2.6 für den Sync, 2.9 für den Bericht) und die ordinale Sortierung der
  Leave-Upserts (2.2). Beide ändern kein Ergebnis, sondern machen nur einen sonst fehlschlagenden Lauf erfolgreich; sie sind nötig, weil
  beim Umschalten Api und Worker kurz verschiedene Zustände haben können (Zyklus Z2).
- **An → Aus → An** ist erlaubt: im Aus-Zustand bleiben eigene Slots stehen und ungelesen; beim Wiedereinschalten konvergiert der
  Abgleich aus DB-Stand und REST (Datums-Identität bindet zurück, neue Emotes bekommen ihren ersten Slot ohne `changed`, E18).

### 2.1 Overlay (D14, §6.4)

- Query: Items selektieren zusätzlich `alias` und `emote { id default_name: defaultName }` (zweiter Teil **[E2]**). DTO bekommt `Alias`
  und `DefaultName`; das private Overlay hält bei Flag an ein Datum je `(EmoteId, Alias)`, ordinal, case-sensitiv; bei Flag aus wie
  heute (2.0). `EntryCount`/`RemoteEntryCount` unverändert.
- Anwendung in `BuildChannelStateResultAsync` (Flag an): jeder v3-Eintrag bekommt `AddedToSetAt` über `(Id, Name)`; kein Treffer →
  undatiert (D11 e). Datum wie geliefert (`UtcDateTime`, ms-genau).
- **[E2]** Schlüssel ist `(emote.id, alias ?? defaultName)`: ein aliasloser Alt-Eintrag heißt in v3 unter dem Standardnamen; ohne
  Rückfall bliebe er dauerhaft undatiert und seine Mehr-Slot-Gruppe nie gesettelt. Doppelter Schlüssel → erster gewinnt, kein Log.
- Unverändert: all-or-nothing bei Seitenfehlern, `emote: null` weder datiert noch gezählt, Warnzeilen des Abrufs.

### 2.2 Transaktion, Sperre, erneutes Lesen, Deckung (§6.5) — Flag an

Ablauf in `ApplyAndSaveAsync`, eine Transaktion je Speicherversuch:

1. `RecordObservedSetAsync` wie heute vor der Transaktion.
2. **[E23] Erste Anweisung: Kanalzeile `FOR NO KEY UPDATE`** (bestehender `ChannelQueries.LockChannelByIdAsync` oder ein reines
   Sperr-Statement; die Kanalzeile wird ohnehin am Ende aktualisiert). Keine Zeile → bestehender „row vanished"-Ausgang. Damit nimmt der
   Sync die Kanalzeile in derselben Reihenfolge wie Purge, Merge, Join, Identitätsabgleich, Tag-Bericht und Backfill-Enqueue (alle
   Kanal → Emote); verträglich mit dem `KEY SHARE` von Flush und `ReplaceBlockAsync`.
3. **Emote-Sperre** der Zeilen, die dieser Abgleich schreiben kann — **[E3]** alle Zeilen des Kanals, deren `SevenTvEmoteId` in der
   Live-Liste steht, **oder** die aktiv sind, **oder** einen aktiven eigenen Slot haben; ein Statement, sortiert nach `Emote.Id`, Modus E1;
   Rückgabe der gesperrten Ids.
4. **Lesen** der Emotes des Kanals mit `Include(Slots)` hinter der Sperre, Datenbankstand (keine veralteten getrackten `Emote`/`EmoteSlot`
   des Kanals im Tick-Kontext: vorher lösen oder zusichern, dass keine getrackt sind).
5. **[E24] Deckungsprüfung (Sol P1):** das Prädikat aus Schritt 3 wird auf dem gelesenen Stand neu ausgewertet; jede persistierte Zeile,
   die es erfüllt, muss in der Menge der gesperrten Ids liegen. Fehlt eine (ein paralleler Schreiber hat zwischen 3 und 4 eine Eltern-Zeile
   angelegt oder un-archiviert), wird die Transaktion zurückgerollt, der ChangeTracker geleert, die Kanalzeile neu gelesen und ab Schritt 2
   neu begonnen — **höchstens zwei Neustarts**, je eine englische Warnzeile; danach propagiert der Fehler wie ein zweiter Fehlschlag (der
   nächste Tick versucht es erneut). Zusätzlich vor `SaveChanges` eine Zusicherung: geänderte `Emote`-Einträge ⊆ gesperrt ∪ in diesem
   Versuch neu angelegt; ein Verstoß ist ein Programmierfehler (Ausnahme, kein Retry). Diese Zusicherung trägt die Zyklusfreiheit aus Z1.
6. Settle-Zuordnung (welcher unsettled Seed gehört zum aktiven Set) mit **einer** Abfrage über `UsageStats` der Seeds dieses Kanals.
7. Abgleich je Gruppe (2.3), Settle-Umbindung von `UsageStats.SlotId` (Bulk-Update), Leave-Upsert, `SaveChanges`, Commit.

**Sperrreihenfolge [E4], Flag an:** `Channels` → `Emotes` (sortiert) → `UsageStats` (Settle) → `EmoteSetLeaveObservations` → `SaveChanges`
(`EmoteSlots`, `Emotes` ⊆ gesperrt ∪ neu, `Channels` schon gehalten). **Flag aus:** heutige Reihenfolge. **Beide Zustände:**
`EmoteSetLeaveObservations.RecordAsync` sortiert seine Eingabe ordinal nach `SevenTvEmoteId` selbst (heute Aufruferreihenfolge), damit zwei
Leave-Schreiber desselben Kanals nie gegenläufig sperren. Der Kommentar zur Lock-Reihenfolge in `ApplyAndSaveAsync` wird neu geschrieben.
Neue Emotes haben noch keine Zeile zum Sperren: ein paralleler Anleger trifft den Emote-Unique-Index (E10-Pfad) oder die Deckungsprüfung.

**Sperr-Helfer [E6]:** statische Erweiterung in `Infrastructure/Persistence` (Muster `ChannelQueries`) mit zwei Einstiegen — nach
Emote-Ids (Bericht, Kind 5/6) und für den Abgleich nach Kanal + Live-IDs (Prädikat aus Schritt 3) — gleicher Modus, gleiche Sortierung,
Rückgabe der gesperrten Ids. Das ist die Sperrkonvention, die D28/D32 voraussetzen.

### 2.2a Sperrzyklen und Erholung (Betreiber 12.10., R1)

| Zyklus | Auflösung | Erholung, falls er doch eintritt | Nachweis |
|---|---|---|---|
| **Z1** Sync (an: Emote → Leave) vs. Legacy-Bericht (heute Leave → Emote), beide an | **ausgerichtet:** Bericht sperrt bei Flag an seine Emote-Zeilen vorab (E25); Leave-Upserts beider Seiten sortiert; Sync schreibt nur gesperrte Emotes (E24) | Sync E17, Bericht 2.9 | 6.5 (5): beide Reihenfolgen warten statt 40P01 |
| **Z2** Umschaltfenster: Worker an / Api aus und umgekehrt | **unvermeidbar** (verschiedene Reihenfolgen je Zustand) | beide Seiten wiederholen die ganze Transaktion einmal (Sync E17, Bericht 2.9, je in beiden Zuständen, E27) | 6.5 (6): vier echte Deadlocks, jede Seite einmal Opfer |
| **Z3** Sync (Emote → Kanal-Update) vs. Purge/Merge/Join/Identität/Tag-Bericht (Kanal → Emote) | **ausgerichtet:** Sync nimmt die Kanalzeile zuerst (E23) | Sync E17; Purge endet für den Sync im bestehenden „row vanished" | 6.5 (4): beide Reihenfolgen ohne 40P01 |
| **Z4** Settle-`UPDATE` vs. Backfill-`ReplaceBlockAsync`-`DELETE` auf `UsageStats` (M1-geseedete Importzeilen; statement-interne Zeilenreihenfolge) | **nicht ausrichtbar** ohne den Backfill zu ändern (D19) | Sync E17; Backfill: 40P01 ist `IsTransient` → Lauf pausiert und setzt am Block fort (bestehender Pfad im `ChatLogBackfillWorker`) | 6.5 (7): Sync als Opfer eines echten 40P01 im Settle-Schritt; Zusicherung, dass Npgsql 40P01 als transient meldet. Ein Nachstellen *innerhalb* zweier Statements ist nicht deterministisch möglich — im PR so benannt |
| **Z5** Settle vs. Flush auf `UsageStats` | praktisch nur am M1-Tag: Settle bewegt nur Zeilen mit `SlotId = Seed` (Datum ≤ M1-Tag), Flush schreibt nur den laufenden Tag und neue Zeilen mit `SlotId NULL` | Sync E17; Flush: bestehendes Requeue der Zählwerte | 6.6 (2) (FK wartet nicht); kein eigener Deadlock-Test |

### 2.3 Abgleich je `SevenTvEmoteId`-Gruppe *G* (§6.2) — Flag an

Begriffe: **eigene Slots** = alle Slots des Emotes außer `SeedUnsettled`. **Seed des aktiven Sets** = der unsettled Seed, an dem
`UsageStats` mit `EmoteSetId = aktives Set` hängen. **Fremde Seeds** = alle übrigen unsettled Seeds.

**[E7] Unsettled Seeds werden von den Schritten 3–7 und vom Archivzweig nie angefasst; nur Schritt 2 ändert einen Seed.** Fremde Seeds
bleiben vollständig unberührt (§6.2 Schritt 2, §6.3 letzter Absatz). Ihr `IsArchived` (1:1 aus M1) hat bis zu ihrem eigenen Settle keine
Bedeutung; alle Leser lassen unsettled Seeds aus (2.5). Die Invariante §4.1 gilt für eigene Slots.

1. **Emote-Zeile** finden/anlegen/un-archivieren wie heute (`LastEnteredSetAtUtc`, `IsPlaceholder`); `ImageUrl` aus dem ersten Eintrag
   der Gruppe. Doppelte `(Id, Name)`-Paare → erstes gilt.
2. **Settle** (2.4), wenn ein Seed des aktiven Sets existiert. „Vertagt" → Gruppe übersprungen (nichts anlegen/archivieren, kein D13), nur
   Schritt 1 gilt.
3. **Datumstreffer** über eigene autoritative Slots (aktiv oder archiviert) je Datum *t*: genau ein Live-Eintrag und genau ein Slot mit *t*
   → binden (Name übernehmen, ggf. un-archivieren, `ArchivedAt := null`); sonst binden nur namensgleiche Paare (D4, AK 8).
4. **D7** für jeden noch offenen datierten Eintrag *n*, wenn nach Schritt 3 kein aktiver eigener Slot (Arbeitsstand inkl. der in 3
   gebundenen und der noch nicht archivierten unerklärten) den Namen trägt. Kandidaten: eigene Slots mit `Name == n.Name` (ordinal) und
   `ArchivedAt != null`; **[E8]** unsettled Seeds ausgeschlossen. Auswahl `ArchivedAt` absteigend, dann `Id` ordinal aufsteigend;
   umschlüsseln auf `n.AddedToSetAt`, `AddedAtFromRest = true`, un-archivieren, `ArchivedAt := null`.
5. **Namens-Bindung** (D11): offener Eintrag → aktiver provisorischer eigener Slot gleichen Namens; datiert autorisiert dessen Datum.
   Undatiert darf zusätzlich an einen aktiven autoritativen eigenen Slot gleichen Namens binden; undatiert überschreibt nie ein Slot-Datum.
6. **Neue Slots** für alle übrigen Einträge (datiert → autoritativ; undatiert → `AddedAt = null`, provisorisch).
7. **Archivierung** jedes aktiven eigenen Slots, der nicht gebunden wurde (`ArchivedAt = now`).
8. **D13 (strikt, Sol P2):** gibt es danach mindestens einen aktiven eigenen Slot, dann `Emote.Name` = Name des Slots mit kleinstem
   `AddedAt` (`null` zuletzt, dann `Name` ordinal, dann `Id`) und **`Emote.FirstSeenAt` = dessen `AddedAt`, auch wenn das `null` ist.** Ein
   Overlay-Ausfall löscht trotzdem kein Datum: ohne Umbenennung binden die undatierten Einträge per Schritt 5 an ihre Slots, deren Daten
   unverändert bleiben, also bleibt auch der abgeleitete Wert. Nur wenn ein anderer Slot ausgewählt wird (Ersatz), folgt `FirstSeenAt`
   ihm — ein Ersatz erbt nie das Datum des gegangenen Slots. Ohne aktiven eigenen Slot (Emote archiviert, Gruppe vertagt) bleiben
   `Name`/`FirstSeenAt` unverändert. Die DECISIONS-Regel „null never overwrites" (2026-08-03) gilt bei Flag an auf Slot-Ebene (Schritt 5),
   bei Flag aus wie heute.

**Nach allen Gruppen:** Emotes ohne Live-Eintrag → aktive eigene Slots archivieren (auch unter schon archiviertem Emote, z. B. nach einem
Delta-Pull), Emote archivieren, b1/b2 und Leave unverändert auf Emote-Ebene. Der 15-Minuten-Messlog bleibt.

Alle Vergleiche und Ordnungen **im Speicher** mit `StringComparer.Ordinal`; kein `ORDER BY "Name"` in SQL.

**[E10] Reine Entscheidungslogik als eigene Klasse:** Schritte 2–8 rechnen in einer internen, statischen, BCL-reinen Klasse in
`Infrastructure/Services` (Präzedenz `ArchivedEmoteRowUpsert`, `EmoteNameMatching`) aus einer Momentaufnahme (Live-Einträge, eigene Slots,
Seed des aktiven Sets) einen Plan: Bindungen, Umbenennungen, Autorisierungen, D7-Umschlüsselungen, neue Slots, Archivierungen,
Seed-Aktion, abgeleiteter Name/Datum, Inventaränderung ja/nein. Der Dienst wendet den Plan auf getrackte Entitäten an.

### 2.4 Settle (§6.3, D12)

**[E11] Erst Zielsuche nach den normalen Identitätsregeln, dann Umschlüsseln.** Zielkandidat: bei |G| = 1 der eine Eintrag, bei |G| ≥ 2 und
allen datiert `oldest` (kleinstes Datum, Gleichstand ordinal kleinster Name).

- **Ziel existiert schon** (Kandidat bände nach Schritt 3 bzw. 5 an einen eigenen Slot): Zeilen des Seeds (nur aktives Set) auf diesen Slot
  umbinden; Seed `SeedUnsettled = false`, `IsArchived = true`, **`ArchivedAt = null`** (**[E12]**: nie ein eigener Eintrag, kein
  D7-Kandidat). Deckt den #371-Randfall „Seed neben REST-Slot".
- **Sonst** wie §6.3: |G| = 1 datiert → Datum und Name, autoritativ; |G| = 1 undatiert → Name, provisorisch; |G| ≥ 2 alle datiert → auf
  `oldest`. Marke weg, Seed aktiv (`IsArchived = false`, `ArchivedAt = null`).
- **|G| ≥ 2 mit mindestens einem undatierten Eintrag → vertagt.** Ohne Seed des aktiven Sets gilt D11 e.
- Danach durchlaufen alle übrigen Einträge die Schritte 3–6 (**[E13]**).
- Mehr als ein Seed des aktiven Sets → nacheinander; ab dem zweiten greift „Ziel existiert".
- Seed des aktiven Sets ohne Live-Eintrag seines Emotes → unsettled und unberührt; kehrt das Emote zurück, setzt Schritt 2 fort.
- `UsageStats` mit `SlotId NULL` (Aus-Images, alte Schreiber) fasst das Settle nicht an; sie hängt `AttachUnslottedUsageRowsSql` (M2-`Up`)
  an den Slot, der die Zeilen des Paars trägt, oder legt einen Seed neben einen vorhandenen REST-Slot, den der nächste Lauf per „Ziel
  existiert" settelt (Prod-Übergang, Abschnitt 4).
- Log je Kanal und Durchlauf bei > 0 vertagt: „{Channel}: settle deferred: v4 dates missing for {Count} emotes" (Information) — §11.2
  Schritt 6 (a) liest die Kanäle daraus.

### 2.5 Leser über Slots (D15, D27; AK 32) — Flag an

**[E14] Ein aktives Emote ohne aktiven eigenen Slot zählt als genau ein virtueller Slot unter `Emote.Name`** (nach dem Einschalten bis zum
ersten Abgleich, bei Sync-Fehlergrund, nach einem Delta-Push, bei vertagtem Settle). Damit verhalten sich die Leser direkt nach dem
Einschalten exakt wie im Aus-Zustand.

- **`OccupiedSlots`** = Summe über aktive Emotes von max(1, Zahl aktiver eigener Slots).
- **`DuplicateNames`** = Namen (ordinal) über aktive eigene Slots aktiver Emotes plus virtuelle Slots, die bei > 1 verschiedenen `EmoteId`
  vorkommen; DTO-Form unverändert; zwei Slots desselben Emotes sind nie ein Duplikat.
- **Tracker** in `RefreshMatchCacheAsync`: Eingabe = dieselbe Menge (Name, EmoteId) distinct; **[E15]** die Map bleibt `Coalesce` über
  `Emote.Name` (D19); Logtext neutral, englisch.
- Flag aus: die heutigen Abfragen (2.0).

### 2.6 Konflikt- und Wiederholungspfad (Sync)

- **`IsSlotKeyConflict`**: Unique-Verletzung mit `ConstraintName` = `IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative`. **[E16]**
  Indexname als Konstante, geteilt von `AppDbContext` (String gleich → kein Snapshot-Diff) und Prädikat; Ort neben dem Sperr-Helfer.
- **[E17]** `SaveSyncAsync` behandelt `IsEmoteKeyConflict`, `IsSlotKeyConflict` (nur Flag an relevant) und Deadlock (SqlState 40P01, als
  `DbUpdateException` aus `SaveChanges` **oder** als nackte `PostgresException` aus Sperr-/Bulk-/Leave-Statement) gleich: **eine**
  Wiederholung der ganzen Transaktion mit geleertem ChangeTracker und neu gelesener Kanalzeile, je eine englische Warnzeile; der zweite
  Fehlschlag propagiert wie heute. Die Deadlock-Erholung gilt in beiden Flag-Zuständen (E27). Die Neustarts der Deckungsprüfung (E24)
  zählen getrennt.

### 2.7 Inventaränderung, Logs

- **[E18] `changed` misst das sichtbare Inventar** (das, was die Leser aus 2.5 zeigen): Anlage, Un-Archive, Umbenennung, Archivierung eines
  eigenen Slots; Emote angelegt/un-archiviert/archiviert, Bild, abgeleiteter Name. **Nicht:** Autorisieren eines Datums, Entfernen der
  Marke, Umbinden von Zeilen, Archivieren eines leeren Seeds, Archivieren von Slots unter schon archiviertem Emote, und **das Anlegen des
  ersten eigenen Slots, wenn er genau den virtuellen Slot (gleicher Name) ersetzt** — sonst feuerte der erste Lauf nach dem Einschalten
  `channel.synced` für jeden Kanal.
- **[E19] Logs:** neu nur die Settle-Zeile, die Neustart-/Wiederholungszeilen und eine Information-Zusammenfassung je Kanal und Lauf,
  **nur wenn** sich Slots geändert haben (Zählwerte, keine Namen). Keine Metriken. Log-Zeilen in umgeschriebenen Methoden werden englisch;
  der K1-Kommentar bleibt im Aus-Zweig.

### 2.8 `ArchivedSlotUpsert` (U7)

**[E20]** Interne statische Klasse, Schwester von `ArchivedEmoteRowUpsert`: legt für **datierte** Einträge `(EmoteId, Name, AddedAt)`
race-sicher einen autoritativen Slot an, der nie im aktiven Set war (`IsArchived = true`, `ArchivedAt = null`, `AddedAtFromRest = true`,
`SeedUnsettled = false`, `LastSyncedAt = now`), ein Statement, Konfliktziel = der partielle Unique-Index, Einfügen in Schlüsselreihenfolge.
Vorhandener Slot desselben Schlüssels bleibt unverändert. Rückgabe: Slot-Id je Schlüssel und die Menge der angelegten. Undatierte gehören
nicht hinein. In #372 ohne Produktivaufrufer.

### 2.9 Legacy-Bericht `EmoteService.MarkInSetAsync` (Betreiber 12.10., R1)

- **[E25] Flag an:** erste Anweisung der Transaktion ist die Sperre der Emote-Zeilen, die `SaveChanges` ändern wird (Ids der geänderten
  getrackten `Emote`-Einträge, Helfer nach Ids, gleicher Modus, sortiert); danach Leave-Upsert (sortiert, 2.2) und `SaveChanges`. Damit
  gleiche Reihenfolge wie der Sync (Z1). Das Lesen bleibt wie heute vor der Sperre — bekannte, unveränderte Grenze; Kind 6 stellt auf
  Sperre-dann-Lesen um.
- **Flag aus:** heutige Reihenfolge.
- **Beide Zustände (E27): Erholung der ganzen Transaktion** bei 40P01 (`DbUpdateException` mit innerer `PostgresException` oder nackte
  `PostgresException`): ChangeTracker leeren, die Methode von ihren ersten Lesezugriffen bis zum Commit einmal neu durchlaufen (Audit-
  Einträge entstehen dadurch genau einmal, weil der erste Versuch zurückgerollt ist), englische Warnzeile; der zweite Fehlschlag propagiert
  wie heute (500). Nichts, was nach dem Commit kommt, läuft doppelt.

## 3. Auflösung der offenen Punkte aus der Zuarbeit (U1–U17)

U1 → E13 · U2 → Inhalt steht in AK 13 selbst, der Test bildet ihn ab, der PR nennt den toten Verweis „Beispiel iv" · U3 → E11 · U5 →
E15; „Seeds ausgelassen" gehört zur Map, also Kind 3 · U6 → Abschnitt 5 · U7 → E20 · U8 → E1 (Betreiber 12.10.), E3–E6, E17, E23–E25,
E27 · U9 → keine Metriken (E19) · U10 → E12 · U11 → E7 · U12 → E8 · U13 → E18 · U14 → überall ordinal, case-sensitiv · U15 → E2 · U16 →
K1-Kommentar bleibt im Aus-Zweig, Nachtrag am Entwurfseintrag (Abschnitt 9) · U17 → keine Planfolge.

**[E21] U4 — Map auf Emote-Schlüssel nach D13:** hingenommen. Bei Flag an zählt die unveränderte Map bis Kind 3 den **ältesten** Namen (bei
`handofblood` `ome44` statt `hob44`). Bei Flag aus bleibt `Emote.Name` wie heute, also auf Prod keine Wirkung, bis Kind 9 das Flag mit den
übrigen Kindern einschaltet.

**Wenn Daten fehlen:** „Warten statt Raten" gilt nur fürs Settle bei |G| ≥ 2 (§6.3). Im gewöhnlichen Abgleich ist fehlendes Datum D11 (e):
Namensbindung, auch an autoritative Slots — ohne Umbenennung ändert ein Ausfall nichts (auch nicht `FirstSeenAt`, 2.3 Schritt 8). Eine
Umbenennung während eines Ausfalls wird als Entfernen + Hinzufügen gelesen; der nächste datierte Lauf bindet den alten Slot per Datum zurück,
der provisorische bleibt archiviert. Eine Rückkehr während eines Ausfalls kann D7 nicht anwenden.

## 4. Randfälle

| Fall | Verhalten | Nachweis |
|---|---|---|
| Flag aus | heutiges Ergebnis, `EmoteSlots` byte-gleich, `UsageStats.SlotId` unverändert | 6.4 |
| Prod-Übergang: Aus-Images schreiben `SlotId NULL` und Emotes ohne Slot, später Flag an | erster An-Lauf: erster eigener Slot ohne `changed`, Seeds gesettelt, NULL-Zeilen unberührt; M2-`AttachUnslottedUsageRowsSql` hängt sie an den Slot des Paars oder erzeugt einen Seed neben dem REST-Slot, nächster Lauf settelt ihn per „Ziel existiert"; Summen erhalten. Reihenfolge M2 ↔ Einschalten beliebig | Slot-Test 21 |
| An → Aus → An, Umbenennung im Aus-Zustand | Aus: emote-weit, Slots ungelesen; An: Datumstreffer benennt denselben Slot um | Slot-Test 22 |
| Api und Worker kurz in verschiedenen Zuständen | Zyklus Z2, beide Seiten erholen sich | 6.5 (6) |
| Emote-Zeile ohne Slot (vor erstem An-Lauf, Delta-Push) | Leser zählen virtuell (E14); Abgleich legt den Slot ohne `changed` an | Slot-Test 17, Status-Test |
| Fremder Seed eines aktiven Emotes | unberührt, nicht gezählt, kein D7-Kandidat | Slot-Test 6, Unit |
| Archiviertes Emote mit Nutzung im aktiven Set (M1-Seed, kein Live-Eintrag) | Seed unberührt, keine Vertagung, kein Teil der Settle-Population | Slot-Test 20, Live 6 |
| Kanal wechselt auf Set B | Seed von B wird gesettelt, A-Slots ohne Datumstreffer archiviert | Slot-Test 6 |
| Seed neben REST-Slot (#371-Randfall) | Zeilen auf den REST-Slot, Seed archiviert ohne Datum | Slot-Test 5, Live 8 |
| Mehr-Slot-Gruppe, Overlay fehlt ganz | Settle vertagt, Gruppe unverändert, Zeile mit Kanal | Slot-Test 2 |
| Aliasloser Alt-Eintrag | datiert über `defaultName` (E2) | Client-Test 2 |
| Overlay-Ausfall ohne Umbenennung | Slots, Daten und `FirstSeenAt` unverändert | Slot-Test 18 |
| Undatierter Ersatz eines datierten Slots | alter Slot archiviert, neuer provisorisch, `Emote.Name` = neuer Name, `FirstSeenAt = null` | Slot-Test 19, Unit |
| Rückkehr während Overlay-Ausfall | neuer provisorischer Slot, kein D7; nächster datierter Lauf autorisiert | Unit |
| Delta-Pull vor dem nächsten Tick | Emote archiviert, Slots noch aktiv → Leser ignorieren sie; nächster Tick archiviert sie ohne `changed` | Unit + Status-Test |
| Gleiches `added_at` zweier Einträge | nur namensgleiche Paare binden | Slot-Test 8 |
| Zweite Beobachtung desselben Eintrags unter neuem Namen (AK 27) | wartet auf die Sperre, liest neu, benennt um, eine SlotId | 6.5 (1) |
| Eltern-Zeile entsteht zwischen Sperre und Lesen | Deckungsprüfung → Neustart, zweiter Versuch sperrt sie mit | 6.5 (2) |
| Paralleler Anleger desselben Emotes | E10-Retry oder Deckungs-Neustart | bestehende E10-Tests, 6.5 (2) |
| Purge während des Syncs | Sync wartet auf die Kanalzeile bzw. Purge auf den Sync; „row vanished" | 6.5 (4) |
| Deadlock mit Backfill-Block | Sync wiederholt, Backfill setzt am Block fort | 6.5 (7) |
| Flush schreibt während des Settle | `NO KEY UPDATE` blockiert die FK-Prüfung nicht; neue Zeilen `SlotId NULL` bis M2 | 6.6 (2) |
| Implausibler Wipe | Wächter unverändert vor dem Schreiben | bestehende Tests |

## 5. AK-Grenze in #372 (PR-Text führt die Tabelle mit „erfüllt / Rest in #37x")

AK 5–13 und 47–50: Test (Abschnitt 6, Flag an); live zusätzlich 5, 7 (Umbenennung, Tausch, Entfernen + Hinzufügen, Rückkehr), 9, 12, 47
(7.7). AK 8 zweite Hälfte und 48–50 sind live nicht herstellbar → nur Test. **AK 4** (Settle-Hälfte): Test hier. **AK 27:** REST-Seite unter
der Sperre → erfüllt für REST, Enqueue-Hälfte „Rest in #375". **AK 29:** Live-Probe des Betreibers vor dem Merge. **AK 32:** vorgezogen aus
#376, #376 übernimmt den Haken. Alle AK gelten für den An-Zustand; auf Prod werden sie mit dem Einschalten in Kind 9 wirksam.

## 6. Tests

Alle Integrationstests `[Collection("Postgres")]`, 7TV über NSubstitute-`ISevenTvApiClient` mit 4-Argument-`SevenTvEmote` für datierte
Einträge. Das Flag wird je Test über den Options-Typ gesetzt. **Bestehende Testklassen laufen unverändert mit Flag aus und sind damit der
Nachweis „Aus = heute"**; Slot-Erwartungen stehen in neuen Klassen mit Flag an.

**6.1 Client (Unit, `SevenTvApiClientChannelStateTests`):** bestehende Fälle bleiben (Aus). Neu, Flag an: (1) Fixture
`Unit/TestData/set-entries-alias.json` aus der Live-Probe — zwei Einträge einer ID mit verschiedenen Aliasen erhalten je ihr Datum, ein
Eintrag ohne Treffer bleibt undatiert (AK 11); (2) Alias `null` → Datum über `defaultName`. Flag aus: (3) dieselbe Fixture → beide
Einträge tragen das Datum des letzten Treffers je Emote-Id (heutiges Verhalten).

**6.2 Entscheidungsklasse (Unit, neu, E10):** Tausch A↔C/B↔A; Gleichstand mit 1:1-Sonderfall vs. namensentschieden; D7 mit Arbeitsstand nach
Schritt 3; D7-Auswahl; D7 schließt `ArchivedAt NULL` und unsettled Seeds aus; Namensbindung undatiert an autoritativ, datiert autorisiert
provisorisch; D13-Ordnung inkl. aller `null`; **`FirstSeenAt` abgeleitet inkl. `null`, unveränderter Slot behält sein Datum bei undatiertem
Lauf, undatierter Ersatz eines datierten Slots ergibt `null`**; Settle-Fälle; `changed` je Fall aus E18; die Ausfall-Randfälle. Ungefähr
15–18 Fälle.

**6.3 `SevenTvSyncServiceSlotTests` (Integration, neu, Flag an):**
1 AK 5 (inkl. `HasChanges` nur im ersten Lauf, nur A-Zeilen am Seed) · 2 AK 6 (beide Varianten, Log-Zeile mit Kanal, nächster Lauf mit
Daten = AK 5) · 3 AK 7 Tausch · 4 AK 7 Entfernen + Hinzufügen und Rückkehr · 5 Seed neben REST-Slot · 6 AK 4-Hälfte und fremde Seeds
(Seed B unberührt inkl. `IsArchived`/`ArchivedAt`/`LastSyncedAt`; Wechsel auf B settelt B) · 7 AK 8 Wiederkehr nach REST-Lag · 8 AK 8
Gleichstand · 9 AK 9 · 10 AK 10 · 11 AK 12 · 12 AK 13 (provisorischer Slot per Test-Seed) · 13 AK 47 · 14 AK 48 · 15 AK 49 · 16 AK 50 ·
17 erster eigener Slot ersetzt virtuellen ohne `HasChanges` · 18 Overlay-Ausfall ohne Umbenennung: Slot-Daten und `FirstSeenAt` bleiben ·
19 undatierter Ersatz eines datierten Slots: `FirstSeenAt = null`, Name = neuer Name (Sol P2) · 20 archiviertes Emote mit Nutzung im aktiven
Set, ohne Live-Eintrag: Seed byte-gleich, keine Vertagungszeile (Sol P2) · 21 Prod-Übergang (Abschnitt 4): Lauf mit Flag aus (neues Emote,
`SlotId NULL`-Zeilen über den heutigen Schreibweg) → Lauf mit Flag an → `AttachUnslottedUsageRowsSql` → nächster Lauf; Summen je
`(EmoteId, EmoteSetId)` erhalten, am Ende alle Zeilen am REST-Slot · 22 An → Aus (Umbenennung in 7TV) → An: dieselbe SlotId, umbenannt.
Summen je `(EmoteId, EmoteSetId)` vor/nach gleich, wo ein Settle läuft.

**6.4 Aus-Zustand (Integration):** (1) Flag aus über einen Kanal mit Seeds und Mehr-Alias-Emote: `EmoteSlots` vor/nach byte-gleich (alle
Spalten), `UsageStats.SlotId` unverändert, Emote-Ergebnis = heutige Erwartung (K1). (2) `SevenTvSyncServiceDuplicateEmoteIdTests` bleibt
(statt gelöscht) und läuft mit Flag aus. (3) Leser mit eigenen Slots in der DB, Flag aus → Ergebnisse wie emote-weit (6.7).

**6.5 Konkurrenz (Integration, neu, eigene Klasse, echte PostgreSQL-Verschränkungen):**
1. **AK 27 REST-Seite:** Emote-Zeile vorhanden, Test-Transaktion nimmt die Sperre über den Helfer und legt A(t) an; der Abgleich mit Live
   `[C(t)]` blockiert (`PostgresLockProbe.WaitUntilBlockedOnLockAsync`), nach Commit liest er neu und benennt **denselben** Slot in C um;
   Gegenprobe Live `[A(t), C(t)]` → zwei Slots.
2. **Eltern-Insert zwischen Sperre und Lesen (Sol P1):** ein Command-Interceptor hält den Abgleich nach dem Sperr-Statement und vor dem
   Reload an; eine zweite Verbindung legt die fehlende Emote-Zeile an (`ArchivedEmoteRowUpsert`, ohne Kanalsperre) und committet; eine
   dritte nimmt deren Sperre über den Helfer und legt A(t) an (konkurrierende Alias-Beobachtung), noch offen. Abgleich mit Live `[C(t)]`
   weiter → Deckungsprüfung schlägt an, Neustart, wartet auf die dritte, liest A(t) und benennt um. Erwartet: **genau ein** autoritativer
   Slot `(E, t)`, eine Neustart-Warnzeile. Der Testkommentar begründet, warum ohne Prüfung zwei Slots entstünden (kein Testschalter im
   Produktivcode).
3. **`IsSlotKeyConflict`-Pfad:** `SaveChanges`-Interceptor legt beim ersten Speichern einen kollidierenden autoritativen Slot über eine zweite
   Verbindung an → eine Wiederholung, ein Slot.
4. **Z3 Purge:** Purge-Seite über dieselben Statements wie `PurgeAsync` (Kanal `FOR UPDATE`, dann Emote-Löschung). (a) Purge hält den
   Kanal → Sync blockiert an seiner Kanalsperre, endet nach dem Commit im „row vanished"-Ausgang; (b) Sync hält → Purge wartet, läuft danach
   durch. Beide ohne 40P01.
5. **Z1 ausgerichtet (beide an):** echter `MarkInSetAsync` und echter Sync auf demselben Emote mit Leave; je ein Interceptor hält die erste
   Seite nach ihrer Emote-Sperre an → die zweite blockiert (Probe), nach Freigabe beide erfolgreich, kein 40P01, Endzustand wie seriell.
   Beide Reihenfolgen.
6. **Z2 Umschaltfenster — vier echte Deadlocks:** Fenster A (Sync an, Bericht aus) und Fenster B (Sync aus, Bericht an). Die Seite mit
   Leave-zuerst wird nach ihrem Leave-Upsert angehalten, die andere läuft bis zum Warten auf die Leave-Zeile, dann Freigabe → echter 40P01.
   **Opfer deterministisch** über ein sitzungsweites `deadlock_timeout` (kurz beim gewünschten Opfer, lang bei der anderen Seite; der
   Testcontainers-Benutzer ist Superuser; z. B. über die Npgsql-Connection-String-Option `Options=-c deadlock_timeout=…` je Kontext). Je
   Fenster einmal der Sync, einmal der Bericht als Opfer. Erwartet: das Opfer wiederholt einmal und gelingt, die andere Seite gelingt
   direkt, Endzustand wie seriell (Slots/Emotes, Leave-Zeilen, Audit-Einträge je genau einmal), eine Warnzeile beim Opfer.
7. **Z4 Sync als Opfer im Settle-Schritt:** skriptierte Gegen-Transaktion hält eine Seed-Zeile in `UsageStats` (`FOR UPDATE`), der Sync
   blockiert im Settle-Update, die Gegen-Transaktion fordert die Emote-Sperre an → echter 40P01. (a) Sync Opfer → wiederholt, gelingt;
   (b) Gegen-Transaktion Opfer → Sync gelingt ohne Wiederholung. Zusätzlich: die gefangene Ausnahme meldet `IsTransient == true` (Basis der
   Backfill-Fortsetzung).
8. **Grenzen:** zweiter Fehlschlag propagiert (hier darf ein injizierter 40P01 per Interceptor den Zweig treffen; alle Erholungsnachweise
   oben sind echt); Deckungsprüfung gibt nach zwei Neustarts auf.

**6.6 Sperr-Helfer und `ArchivedSlotUpsert` (Integration):** (1) zwei Sperrer desselben Emotes serialisieren, Reihenfolge nach `Id`;
(2) ein `UsageStats`-Insert für ein gesperrtes Emote wartet **nicht** (E1; Kommentar: mit `FOR UPDATE` würde er warten); (3) Upsert legt an,
ist beim zweiten Aufruf No-op, lässt vorhandenen aktiven Slot unverändert, meldet nur Angelegtes; (4) `IsSlotKeyConflict` (Unit,
positiv/negativ).

**6.7 `EmoteSetStatusServiceTests`:** bestehende Fälle unverändert (Aus). Flag an: +2 AK 32 (zwei Slots eines Emotes = 2 Plätze, kein
Duplikat; Kollision zweier Emotes über Slot-Namen inkl. Zweitname); +1 Virtuell-Regel (Emote ohne Slot zählt 1, unsettled Seed nicht
separat, aktive Slots unter archiviertem Emote nicht). Flag aus mit vorhandenen eigenen Slots: Ergebnisse wie emote-weit. Tracker: je ein
Fall in `SevenTvSyncServiceSlotTests` (Kollision über Zweitnamen → Warnzeile) und Aus (wie heute).

**6.8 Nicht nötig:** Worker.Tests (kein Worker-Code; das Backfill-Verhalten bei transientem Fehler ist dort schon getestet), Api.Tests (kein
Endpoint/Filter; Default aus), Vitest/E2E (kein `web/`).

## 7. Live-Verifikation (Regel 16) — Betreiber 12.10. (R2)

Randbedingungen: geteilte Dev-DB `emotepurge`, geteilter Api-Container, geteilter Worker und geteiltes Redis gehören einer anderen Sitzung
und werden **nicht** angefasst. **Nie `docker compose` aus dem Worktree, nie etwas im Haupt-Checkout lesen.**

1. **Kopie:** im Container `emotepurge-dev-postgres` neue DB `emotepurge_slots372` per `pg_dump | psql` **im** Container (Benutzer aus
   `printenv POSTGRES_USER`, Socket-Auth). Keine serverweiten Eingriffe. Passwort für den Host-Zugriff aus `printenv POSTGRES_PASSWORD` in
   eine Shell-Variable, nie ausgeben, CR abschneiden.
2. **Last begrenzen:** in der Kopie alle Kanäle `IsBotActive = false` außer **`olaf_olaf_son`** (Testkanal), `handofblood` (nur lesend:
   Mehr-Slot-Fall `01KAMA120V9DC2P1YW9QWFHA16`) und höchstens zwei weiteren Kanälen mit Mehr-Slot-Emotes und Nutzung in mehreren Sets (per
   SQL gewählt; mindestens einer davon mit einem archivierten Emote mit Nutzung im aktiven Set). Fehlt `olaf_olaf_son` in der Kopie: minimale
   Kanalzeile nur in der Kopie anlegen (normalisierter Name, `IsBotActive = true`; `TwitchChannelId` füllt der Sync).
3. **Vorher messen** (CSV ins Scratchpad). **D0** = aktuelles UTC-Datum bei der Messung (nicht zwischen 23:55 und 00:05 UTC messen).
   **Erhaltungs-Population:** `UsageStats`-Zeilen der aktiven Kopie-Kanäle mit `Date < D0` — der Flush schreibt nur den laufenden Tag,
   Backfill ist aus, Retention schreibt nicht; der zählende Worker kann diese Population also nicht verändern. Je `(EmoteId, EmoteSetId)`:
   Zeilenzahl und die Summen von `UseCount`, `BotUseCount`, `SharedChatUseCount`; dazu je `SlotId` dieselben Werte. Außerdem: alle Seeds
   mit allen Spalten; Emotes der aktiven Kanäle mit Name/`FirstSeenAt`; Prüfsumme über `EmoteSlots` (alle Spalten, sortiert).
4. **Wegwerf-Redis:** `docker run` auf eigenem Host-Port (z. B. `127.0.0.1:6390`), Name `redis-slots372`.
5. **Worker aus dem Worktree:** `dotnet run --project src/EmotePurge.Worker` mit Env-Overrides: Connection-String auf die Kopie, Redis auf
   den Wegwerf-Port, `ChatLogBackfill__Enabled=false`, `Retention__Enforce=false`, `SevenTv__EventApi__Enabled=false`,
   `SevenTv__ResyncIntervalSeconds` passend (Default 60; für die Handgriffe ggf. 120), Helix-Intervalle hoch. PID notieren.
   **Zuerst ein Lauf mit `SevenTv__SlotReconcile__Enabled=false`:** ein Tick, dann `EmoteSlots`-Prüfsumme = Schritt 3 (Aus-Zustand auf
   echten Daten). Worker stoppen (SIGTERM, PID vorher mit `ps -o args=` prüfen) und mit **`SevenTv__SlotReconcile__Enabled=true`** neu
   starten.
6. **Erster An-Tick, SQL-Prüfungen:**
   (a) **Settle-Population (Sol P2):** Seeds des aktiven Sets, deren Emote mindestens einen Eintrag in der aktuellen Live-Liste des Kanals hat
   (Live-Gruppen-Mitgliedschaft, aus dem Log bzw. einer REST-Abfrage des aktiven Sets ermittelt). Davon unsettled = 0 außer den Gruppen der
   Kanäle aus „settle deferred"; die Zahl der vertagten Gruppen je Kanal = Zahl in der Logzeile.
   (b) **Unverändert, getrennt geprüft:** Seeds des aktiven Sets, deren Emote **keinen** Live-Eintrag hat (z. B. archivierte Emotes mit
   Nutzung), und alle Seeds anderer Sets byte-gleich zur CSV (AK 4-Hälfte).
   (c) `handofblood`-X: zwei eigene Slots, `ome44` autoritativ mit dem alten Datum und allen Zeilen des aktiven Sets, `hob44` ohne Zeilen,
   `Emote.Name = ome44`, `FirstSeenAt` = Datum von `ome44` (AK 5).
   (d) **Erhaltung:** über die Population aus 3 je `(EmoteId, EmoteSetId)` Zeilenzahl und drei Summen = CSV; je `SlotId` nur Verschiebungen
   Seed → Ziel.
   (e) kein autoritativer Slot mit `AddedAt NULL`; kein aktiver eigener Slot unter archiviertem Emote; aktive Emotes ohne aktiven eigenen Slot
   nur in vertagten Gruppen.
   (f) Dauer des Abgleichs für `handofblood` aus den Log-Zeitstempeln.
   **Zweiter Tick:** keine Slot-Schreibung (`LastSyncedAt` der Slots unverändert), keine Zusammenfassungszeile.
7. **Testkanal `olaf_olaf_son` — Set-Änderungen je AK** (Betreiber 12.10.). Ausführung durch den Live-Test-Agenten über dieselben
   v4-Schreib-Mutationen, die die App verwendet (der T7-Agent sucht sie im Bestand); **Rückfall:** der Betreiber führt denselben Schritt von
   Hand in der 7TV-Oberfläche aus. Vorher den Ausgangszustand des aktiven Sets (Einträge und Aliase) ins Scratchpad sichern. Nach jedem
   Schritt mindestens einen Tick abwarten; die SQL-Abfragen schreibt der T7-Agent vorab.

   | Schritt | 7TV-Änderung | AK | SQL-Prüfung danach |
   |---|---|---|---|
   | (i) | Emote E unter Alias a1 hinzufügen | 5 (Grundfall neu) | ein neuer autoritativer Slot `(E, a1)`, `AddedAt` = v4-`added_at`, `AddedAtFromRest`; Emote aktiv |
   | (ii) | E zusätzlich unter a2 | 12 | zweiter Slot; zweiter Tick ohne Slot-Schreibung; `OccupiedSlots` +1 über den Leser (Api nicht nötig: dieselbe Abfrage per SQL nachgebildet) |
   | (iii) | a2 → a3 umbenennen | 7 (Umbenennung) | gleiche SlotId, `AddedAt` unverändert, Name a3 |
   | (iv) | a1 ↔ a3 über einen Zwischennamen tauschen, alle drei Änderungen innerhalb eines Ticks | 7 (Tausch) | beide SlotIds behalten ihr Datum, Namen getauscht |
   | (v) | a3 entfernen | 9 | nur dieser Slot archiviert (`ArchivedAt` gesetzt), Emote aktiv, keine neue Leave-Zeile |
   | (vi) | E unter a3 wieder hinzufügen | 47, 7 (Rückkehr) | **dieselbe** SlotId, neues `AddedAt`, `AddedAtFromRest = true`, `ArchivedAt NULL` |
   | (vii) | beide Aliase entfernen | 9 (zweite Hälfte) | Slots und Emote archiviert; Leave nach b1 bzw. b2 (Glaubwürdigkeitsfenster 30 min) |
   | Ende | Ausgangszustand wiederherstellen | — | Set entspricht der Sicherung |

   **Token:** der Betreiber legt das 7TV-Token in eine Datei im Scratchpad (außerhalb des Repos, Modus 600) oder übergibt es als
   Umgebungsvariable. Der Agent liest es nur in die Umgebung bzw. eine Header-Datei (`curl -H @datei`, damit es nicht in der Prozessliste
   steht), nie `set -x`, nie ausgeben, nie loggen, nie committen, nie ins Protokoll oder in den PR. Abbau löscht die Datei. Die Änderungen sind
   für jeden sichtbar, der den Kanal verfolgt (geteilter Dev-Worker, ggf. Prod) — beabsichtigt, es ist der Testkanal.
8. **Optional, #371-Randfall:** im Testkanal-Chat E benutzen (Zeilen des laufenden Tages, `SlotId NULL`), dann
   `EmoteSlotSeedSql.AttachUnslottedUsageRowsSql` auf der Kopie in einer Transaktion mit `lock_timeout` und `LOCK TABLE "UsageStats"`: ist E
   nach M1 entstanden, entsteht ein Seed neben dem REST-Slot; nächster Tick → Zeilen am REST-Slot, Seed archiviert ohne Datum, Marke weg.
   Läuft außerhalb der Erhaltungs-Population (Datum D0).
9. **Abbau:** Worker per SIGTERM (PID geprüft), `docker rm -f redis-slots372`, `DROP DATABASE emotepurge_slots372 WITH (FORCE)`, Token-Datei
   löschen. Protokoll ins Scratchpad; PR nennt SHA, Zahlen, Dauer, keine Hostnamen/Passwörter/Tokens, keine Kanalnamen außer
   `olaf_olaf_son` und `handofblood`.

**AK 29 (vor dem Merge, Betreiber von Hand):** die neue Query gegen die öffentliche v4-GQL für ein Set mit einem Mehr-Slot-Emote (und, wenn
vorhanden, einem aliaslosen Alt-Eintrag); Antwort wird `Unit/TestData/set-entries-alias.json` (darf gekürzt werden, Struktur bleibt). Den
fertigen Befehl liefert der Orchestrator nach T1; der PR nennt Datum und Set-ID, kein Token.

## 8. Gates

- `dotnet test EmotePurge.slnx` (Docker), `npm --prefix web test -- --watch=false`, **kein** E2E.
- Bestehende Sync-/Status-/Client-Testklassen unverändert grün (= Aus-Nachweis); neue Klassen mit Flag an grün.
- `dotnet format EmotePurge.slnx` sauber; kein Snapshot-Diff (Probe-`migrations add` erzeugt nichts, E16).
- Flag vollständig verdrahtet: beide `appsettings.json`, beide Compose-Dateien je api und worker, `.env.example`, `docs/Operations.md`.
- Live-Verifikation (Abschnitt 7) und AK 29 vor dem Merge.
- **Erst committen, dann** `node scripts/coverage-local.mjs --backend-only`; Schwachstelle erfahrungsgemäß `SevenTvSyncService`.
- Opus-Review, dann `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem Worktree; Befund unverändert vorlegen;
  Widerspruch → Fable nur über die strittigen Findings. Merge durch den Nutzer; der nächste Main-Deploy bringt #372 mit Flag aus nach Prod.

## 9. Commits und DECISIONS

Conventional Commits, englisch: (1) `docs: add implementation plan for slot-aware reconcile`, (1b) `docs: revise slot reconcile plan after
adversarial review` (diese Datei); (2) `feat: add the SevenTv:SlotReconcile:Enabled feature flag, default off` (Options, appsettings,
Compose, `.env.example`, Operations) **plus DECISIONS-Absatz (Flag-Vertrag)**; (3) `feat(infrastructure): key the v4 set-entry overlay by
emote id and alias` (+ Fixture, Tests); (4) `feat(infrastructure): add emote row lock, slot key conflict detection and ArchivedSlotUpsert`
(inkl. sortierter Leave-Upserts); (5) `feat(infrastructure): reconcile REST entries per slot with date identity, resumption and per-set
settle` **plus DECISIONS (Sperrvertrag, Deckung, Zyklen)**; (6) `feat(infrastructure): align the legacy in-set report with the emote lock
order and retry it on deadlock` **plus DECISIONS (Bericht)**; (7) `feat(infrastructure): read occupied slots, duplicate names and the
collision tracker from slots`; danach `fix:`/`test:` aus Review. Tests reisen im Commit ihres Features.

**DECISIONS [E22]:** wie #371 ein datierter Absatz „Child 2 (slot reconcile), <Datum>" direkt hinter „Child 1 (M1)" im Entwurfseintrag
von 2026-10-10, englisch; er wächst mit den Commits (Regel 3: jeder Commit, der einen Vertrag ändert, trägt seinen Teil). Inhalt: Flag
(E26, Aus-Semantik 2.0, E27); E1 als **dokumentierte Abweichung vom Spec-Wortlaut, Betreiberentscheidung 12.10.**, mit Begründung (FK-
`KEY SHARE`, D19); E23–E25 und die Zyklentabelle 2.2a (verbleibend Z2, Z4, Z5 und wer sich erholt); E3–E6 (Helfer als Konvention für Kind
4–6); E7/E8; E11/E12; E14; E17; E18; E21; E2; D13 strikt für `FirstSeenAt` (die Regel „null never overwrites" wandert bei Flag an auf die
Slot-Ebene). Die `Betrifft`-Zeile bekommt `EmoteSetStatusService.cs`, `EmoteService.cs`, `EmoteSetLeaveObservations.cs`,
`ArchivedSlotUpsert.cs`, die Datei des Sperr-Helfers, den Options-Typ und die Compose-Dateien. Kind 9 macht final.

## 10. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; Modell explizit)

Builds und Testläufe teilen sich `obj/` im Worktree: **nie zwei Subagents gleichzeitig bauen oder testen lassen.** T1–T3 sind
dateidisjunkt und dürfen parallel implementiert werden, ihre Testläufe nur nacheinander.

| # | Aufgabe | Modell | Input | Fertig, wenn |
|---|---|---|---|---|
| T1 | Flag-Verdrahtung + Overlay | sonnet | 2.0, 2.1, E2, E26, 6.1; `SevenTvApiClient.cs`, `SevenTvApiDtos.cs`, beide `appsettings.json`, Compose-Dateien, `.env.example`, Operations | 6.1 grün (beide Zustände) mit vorläufiger, strukturgleicher Fixture; Verdrahtung vollständig (Gate); AK-29-Befehl im Abschlussbericht; Commits (2) und (3) mit DECISIONS-Teil |
| T2 | Sperr-Helfer, Indexkonstante, `IsSlotKeyConflict`, `ArchivedSlotUpsert`, sortierte Leave-Upserts | sonnet | 2.2 (E3, E4, E6), 2.6 (E16), 2.8, 6.6; `ChannelQueries`, `ArchivedEmoteRowUpsert`, `EmoteSetLeaveObservations`, `PostgresLockProbe` | 6.6 grün; kein Snapshot-Diff; bestehende Leave-Tests grün |
| T3 | **Entscheidungsklasse** | **opus** | 2.3 (inkl. Schritt 8 strikt), 2.4, 2.7 (E18), Abschnitt 3 „Wenn Daten fehlen", 6.2 | 6.2 grün; jede Regel trägt im Code den Spec-Verweis |
| T4 | **Integration in `SevenTvSyncService`** (Flag-Zweig, Kanal- und Emote-Sperre, Deckungsprüfung, Settle-Umbindung, Wiederholung) | **opus** | 2.0, 2.2–2.7, 2.2a, E17, E19, E23, E24, E27, Abschnitt 4; Ergebnis T1–T3 | 6.3, 6.4, 6.5 (1)–(4), (7), (8) grün; bestehende Sync-Klassen unverändert grün; ganze Infrastructure-Suite grün; Commit (5) mit DECISIONS-Teil |
| T5 | **Legacy-Bericht `MarkInSetAsync`** (Vorabsperre bei an, Erholung in beiden Zuständen) | **opus** | 2.9, 2.2a (Z1, Z2), E25, E27, 6.5 (5)–(6); Ergebnis T2/T4 | 6.5 (5)–(6) grün, je Seite einmal echtes Opfer; bestehende EmoteService-Tests grün; Commit (6) mit DECISIONS-Teil |
| T6 | Leser über Slots | sonnet | 2.5 (E14, E15), 2.0 (Aus-Zweig), 6.7; `EmoteSetStatusService`, `RefreshMatchCacheAsync` | 6.7 grün (beide Zustände); Logtexte englisch; Commit (7) |
| T7 | Live-Verifikation | sonnet | Abschnitt 7 vollständig; Token des Betreibers im Scratchpad; Rückfall Handweg abgestimmt | alle Prüfungen aus 7.5–7.7 protokolliert, Set wiederhergestellt, Abbau erledigt, kein Token in Protokoll/Repo |
| T8 | Review, Gates, PR | **opus** (Review); Codex Sol | Branch, Abschnitte 5, 8, 9, 11 | drei Suiten grün, Coverage ≥ 80 %, Codex-Befund vorgelegt, PR mit AK-Tabelle, Abweichungen 1.3 und Flag-Hinweis; Kommentare auf #373–#376 und #379 mit Abschnitt 11 |

Reihenfolge: (T1 ∥ T2 ∥ T3) → T4 → T5 → T6 → T7 → T8 (T5 und T6 sind dateidisjunkt, Testläufe aber nacheinander). AK 29 (Betreiber) jederzeit
nach T1, vor T8. Ob T3 oder ein Review von T3/T4/T5 zusätzlich an Fable geht, entscheidet der Orchestrator (Identitäts- und Sperrlogik sind
hochgehebelt).

## 11. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **Kind 3 (#373):** Auf Prod ist das Flag aus, bis Kind 9 es einschaltet — nichts in Kind 3 darf gesettelte Slots voraussetzen, solange das
  Flag aus ist (Map/Zähler entweder am selben Flag oder mit emote-weitem Rückfall; Entscheidung in Kind 3). Die Map aus gesettelten Slots darf
  die Virtuell-Regel (E14) **nicht** auf Emotes mit unsettled Seed des aktiven Sets anwenden (AK 17). M2 kann vor oder nach dem Einschalten
  laufen (Abschnitt 4, Slot-Test 21). Settle-Umbinden bleibt nach M2 kollisionsfrei; Fixture „M1 → alter Writer → Kind-2-Settle → M2" ruft
  das Settle über `SyncChannelAsync` mit gefaktem 7TV-Client und Flag an. E21 endet mit Kind 3.
- **Kind 4 (#374):** Der Delta-Pfad schreibt in #372 keine Slots; sobald er es tut, nur bei Flag an, mit Kanal-zuerst und demselben
  Sperr-Helfer (Api-Schreiber laufen parallel). E14 bleibt das Netz.
- **Kind 5 (#375):** Sperr-Helfer (Modus E1), Kanal → Emote-Reihenfolge, Deckungsprüfung nach dem Reload (E24), `IsSlotKeyConflict`,
  `ArchivedSlotUpsert` (nur datiert) und die Entscheidungsklasse wiederverwenden. Schreibt das Enqueue `UsageStats`-Zeilen um, gehört es zu
  Zyklus Z4 und braucht dieselbe Erholung.
- **Kind 6 (#376):** Entries-Berichte nehmen die Emote-Sperre **vor** dem Leave-Upsert und lesen danach (Sperre-dann-Lesen); der Legacy-Pfad
  hat seit #372 die Vorabsperre bei Flag an und die Deadlock-Erholung — beides beim Umbau erhalten. `/totals` aktiv braucht für Emotes ohne
  eigenen Slot die Virtuell-Regel oder eine bewusste Alternative. AK 32 ist hier schon getestet.
- **Kind 9 (#379):** **Schaltet `SEVENTV_SLOT_RECONCILE_ENABLED=true` auf Prod ein** (Api und Worker zusammen neu starten; das kurze
  Mischfenster deckt Z2). Davor der Prod-Kopie-Lauf mit Flag an (Abschnitt 7 wiederverwendbar, inkl. der Aus-Prüfsumme), danach Nachkontrolle
  §11.2 Schritt 6 (a) über „{Channel}: settle deferred …". DECISIONS final inkl. E1 und Flag; Rückbau der Aus-Zweige erst nach bewährtem
  Prod-Betrieb (eigener Folgeschritt).

## 12. Rückfragen an den Betreiber

Keine offen. R1 (Sperrmodus), R2 (Live-Verifikation, Testkanal `olaf_olaf_son`, 7TV-Token) und R3 (Feature-Flag) sind am 12.10. entschieden
und oben eingearbeitet.
