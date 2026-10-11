# Plan #372 — Emote-Slots, Kind 2: REST-Abgleich je Slot mit Datums-Identität und Wiederaufnahme

Erstellt am 2026-10-11 im Worktree `EmotePurge-slots` (Branch `feat/372-slot-reconcile`, Basis `origin/main` @ `38e640c8`).
**Status: Entwurf, wartet auf Freigabe durch den Betreiber (Abschnitt 12).** Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md)
(§6.2–6.5, §4.1, §5.4, §10, §13 AK 5–13, 27, 29, 32, 47–50, §15, §16 Kind 2), Issue #372 samt Übergabekommentar aus #371, Epic #365.
Formatvorlage: [Plan-371](Plan-371-Emote-Slots-M1.md).

**Was dieser Plan trägt:** nur, was die Spec offen lässt — Schrittfolge im Abgleich, Settle-Mechanik, Sperre und Konfliktpfad,
Leser-Regeln für den Übergang, Tests, Live-Prüfung, Gates, Aufgabenfolge. Kein Code. Bei Abweichung gilt die Spec; der Fund wird
gemeldet. D0–D33 werden nicht wieder aufgemacht.

**Lesehinweis:** Zeilen mit **[E]** sind Planer-Entscheidungen, kein Spec-Text — der Betreiber kann jede einzeln kippen. Was nur er
entscheiden kann, steht in Abschnitt 12.

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 2)

1. **Overlay** `GetSetEntriesAsync`: Query selektiert `alias`, Overlay nach `(emoteId, alias)` (D14, §6.4); AK 11, AK 29 (Live-Probe des Betreibers, Fixture).
2. **`ReconcileAsync` je Slot** ohne Dedup (ersetzt K1): Datums-Identität, D7, Namens-Bindung, neue Slots, Archivierung, D13 (§6.2).
3. **Settle** nur des Seeds des aktiven Sets (§6.3, D12), einschließlich des #371-Randfalls „Seed neben REST-Slot".
4. **Sperre**: owning `Emote`-Zeilen innerhalb der Abgleichstransaktion sperren, danach Slotgruppen neu lesen (§6.5); `IsSlotKeyConflict`;
   Wiederholungspfad.
5. **`ArchivedSlotUpsert`** (neu, §18) als Baustein für Kind 3/5.
6. **Leser über Slots:** `OccupiedSlots`, `DuplicateNames` (D27, D15; AK 32 vorgezogen) und der Namenskollisions-Tracker.
7. **Keine Migration.** Zähl-Map, Zähler und Flush bleiben auf dem Emote-Schlüssel (D19); die Map wird weiter aus `Emote.Name` gebaut.

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 3 (#373) | M2, Map `Name→SlotId` aus gesettelten Slots, Zähler/Flush je Slot, Kaltstart, Information-Zeile „ausgelassene Seeds" (§6.1 Punkt 1), Enqueue-Basisbindung |
| Kind 4 (#374) | EventAPI je `(Id, Name)`: der Delta-Pfad bleibt in #372 **unverändert emote-weit** und schreibt keine Slots |
| Kind 5 (#375) | Enqueue-Hälfte von AK 27, D7 für Vorschauen, Backfill-Settle nicht aktiver Sets, 409 |
| Kind 6 (#376) | Lese-API je Slot, Berichte mit `Entries` (Sperre wie hier), Sichtbarkeit von Seeds in `/totals` |
| Kind 9 (#379) | DECISIONS final, Operations (Nachkontrolle „settle deferred"), Prod-Kopie-Lauf |

### 1.3 Abweichung von der Spec

- **[E1] Sperrmodus `FOR NO KEY UPDATE` statt `FOR UPDATE`** (Spec §6.5, D28, D32 nennen `FOR UPDATE`). Beide Modi schließen sich
  gegenseitig und sich selbst aus, die Serialisierung jeder autoritativen Auflösung desselben Emotes (der Zweck in §6.5) bleibt also
  vollständig erhalten — auch wenn Kind 5/6 `FOR UPDATE` nehmen sollten. Anders als `FOR UPDATE` blockiert `NO KEY UPDATE` aber nicht
  das `KEY SHARE`, das jede FK-Prüfung eines Inserts auf `Emotes` nimmt (Flush, Backfill-`ReplaceBlockAsync`, Ballot-Zeilen). Mit
  `FOR UPDATE` entstünden echte Zyklen: Flush bzw. `ReplaceBlockAsync` hält `UsageStats`-Zeilen und wartet auf `KEY SHARE` eines
  gesperrten Emotes, während das Settle dieselben `UsageStats`-Zeilen umbinden will. Präzedenz im Repo: `ChannelQueries`
  („serializes … without blocking foreign-key inserts"). Weil das den Spec-Wortlaut ändert, steht es zusätzlich als Rückfrage R1.
- Die Spec wird nicht editiert; Abweichung im PR-Text und im DECISIONS-Nachtrag (Abschnitt 9).

## 2. Verträge

### 2.1 Overlay (D14, §6.4)

- Query: Items selektieren zusätzlich `alias` und `emote { id default_name: defaultName }` (der zweite Teil ist **[E2]**, s. u.).
  DTO bekommt `Alias` und `DefaultName`; das private `SetEntriesOverlay` hält statt `AddedAtByEmoteId` ein Datum je
  `(EmoteId, Alias)`, ordinal, case-sensitiv. Das Overlay gehört zum angefragten Set; `EntryCount`/`RemoteEntryCount` unverändert.
- Anwendung in `BuildChannelStateResultAsync`: jeder v3-Eintrag bekommt `AddedToSetAt` aus dem Overlay über `(Id, Name)`; kein Treffer
  → undatiert (D11 e). Datum wie geliefert (`UtcDateTime`, ms-genau).
- **[E2]** Schlüssel ist `(emote.id, alias ?? defaultName)`: ein aliasloser Alt-Eintrag (in alten Sets live gefunden, Memory „keine neuen
  aliaslosen Einträge") heißt in v3 unter dem Standardnamen. Ohne Rückfall bliebe ein solcher Eintrag dauerhaft undatiert, und eine
  Mehr-Slot-Gruppe mit ihm würde nie gesettelt (§6.3 letzter Fall) — aus „vorübergehend" (§14 c) würde „dauerhaft". Kosten: ein Feld je
  Item. Ein doppelter Schlüssel im Overlay (nach 7TVs Eindeutigkeit unmöglich) → erster gewinnt, kein Log.
- Unverändert: all-or-nothing bei Seitenfehlern (dann ist der ganze Abgleich undatiert), Eintrag mit `emote: null` weder datiert noch
  gezählt, Warnzeilen des Abrufs.

### 2.2 Transaktion, Sperre, erneutes Lesen (§6.5)

Ablauf in `ApplyAndSaveAsync`, alles in der bestehenden einen Transaktion je Speicherversuch:

1. `RecordObservedSetAsync` wie heute vor der Transaktion.
2. **Erste Anweisung in der Transaktion: Sperre** der owning `Emote`-Zeilen, die dieser Abgleich schreiben kann — **[E3]** alle Zeilen des
   Kanals, deren `SevenTvEmoteId` in der Live-Liste steht, **oder** die aktiv sind, **oder** einen aktiven eigenen Slot haben; ein
   Statement, sortiert nach `Emote.Id`, Modus aus E1. Archivierte Emotes ohne Live-Eintrag und ohne aktiven Slot werden nur gelesen
   (Leave-Nachkontrolle b2) und nicht gesperrt.
3. **Danach** das Lesen der Emotes des Kanals mit `Include(Slots)` (der heutige eine `existing`-Query wandert hinter die Sperre). Das
   Lesen muss Datenbankstand liefern: im geteilten Kontext eines Resync-Ticks dürfen keine veralteten getrackten `Emote`/`EmoteSlot`
   dieses Kanals die Identitätsauflösung von EF gewinnen (vor dem Lesen lösen oder zusichern, dass keine getrackt sind).
4. Settle-Zuordnung (welcher unsettled Seed gehört zum aktiven Set) mit **einer** Abfrage über `UsageStats` der Seeds dieses Kanals.
5. Abgleich je Gruppe (2.3), Settle-Umbindung von `UsageStats.SlotId` sofort in derselben Transaktion (Bulk-Update), Leave-Upsert wie
   heute, `SaveChanges`, Commit.

**Sperrreihenfolge [E4]:** `Emotes` (sortiert) → `UsageStats` (Settle) → `EmoteSetLeaveObservations` → `SaveChanges`
(`EmoteSlots`, `Emotes`, `Channels`). Der Kommentar zur Lock-Reihenfolge in `ApplyAndSaveAsync` wird entsprechend neu geschrieben.
Bekannter Rest-Zyklus: der heutige set-zentrische Bericht (`EmoteService`, Schritt 6) nimmt Beobachtungszeilen **vor** den
Emote-Zeilen; trifft er im selben Millisekundenfenster denselben Emote mit einer Leave wie der Abgleich, erkennt Postgres einen
Deadlock (40P01). Ebenso kann ein Purge (Kanalzeile → Emote-Löschung) mit der Kanal-Aktualisierung am Ende kreuzen. **[E5]** Beides
fängt der Wiederholungspfad (2.6); den Bericht in #372 umzustellen ist verworfen (Kind 6 baut genau diesen Pfad mit Emote-Sperre neu,
Hinweis in Abschnitt 11). Neue Emotes haben noch keine Zeile zum Sperren: ein paralleler Anleger trifft den Emote-Unique-Index, der
E10-Pfad wiederholt, und der zweite Versuch sperrt die jetzt vorhandene Zeile.

**Sperr-Helfer [E6]:** neue statische Erweiterung in `Infrastructure/Persistence` (Muster `ChannelQueries`) mit zwei Einstiegen — nach
Emote-Ids (für Kind 5/6) und für den Abgleich nach Kanal + Live-IDs (Prädikat aus Schritt 2) — gleicher Modus, gleiche Sortierung,
Rückgabe der gesperrten Ids. Das ist die Sperrkonvention, die D28/D32 voraussetzen.

### 2.3 Abgleich je `SevenTvEmoteId`-Gruppe *G* (§6.2)

Begriffe: **eigene Slots** = alle Slots des Emotes außer `SeedUnsettled`. **Seed des aktiven Sets** = der unsettled Seed, an dem
`UsageStats` mit `EmoteSetId = aktives Set` hängen. **Fremde Seeds** = alle übrigen unsettled Seeds.

**[E7] Unsettled Seeds werden von den Schritten 3–7 und vom Archivzweig nie angefasst; nur Schritt 2 ändert einen Seed.** Fremde Seeds
bleiben damit vollständig unberührt (§6.2 Schritt 2, §6.3 letzter Absatz) — kein `ArchivedAt = now`, kein D7-Kandidat, keine
Umbenennung. Ihr `IsArchived` (1:1 aus M1) hat bis zu ihrem eigenen Settle keine Bedeutung; alle Leser in #372 lassen unsettled Seeds
aus (2.5). Die Invariante §4.1 („aktiver Slot ⇒ aktives Emote") gilt für eigene Slots.

1. **Emote-Zeile** finden/anlegen/un-archivieren wie heute (`LastEnteredSetAtUtc`, `IsPlaceholder`); `ImageUrl` aus dem ersten
   Eintrag der Gruppe. Doppelte `(Id, Name)`-Paare (7TV-Fehler) → erstes gilt.
2. **Settle** (2.4), wenn ein Seed des aktiven Sets existiert. Ergebnis „vertagt" → die ganze Gruppe wird übersprungen (nichts anlegen,
   nichts archivieren, kein D13), nur Schritt 1 gilt.
3. **Datumstreffer** über eigene autoritative Slots (aktiv oder archiviert) je Datum *t*: sind es genau ein Live-Eintrag und genau ein
   Slot mit *t*, binden sie (§6.5-Sonderfall; Name übernehmen, ggf. un-archivieren, `ArchivedAt := null`); sonst binden nur
   namensgleiche Paare, der Rest bleibt offen (D4, AK 8).
4. **D7** für jeden noch offenen datierten Eintrag *n*, wenn **nach Schritt 3** kein aktiver eigener Slot den Namen trägt (aktiv heißt:
   im aktuellen Arbeitsstand, also einschließlich der in Schritt 3 gebundenen und der noch nicht archivierten unerklärten Slots — so
   ergeben sich AK 48 und 50). Kandidaten: eigene Slots mit `Name == n.Name` (ordinal) und `ArchivedAt != null`; **[E8]** unsettled
   Seeds sind ausgeschlossen (E7). Auswahl `ArchivedAt` absteigend, dann `Id` ordinal aufsteigend; gleiche `Id` auf `n.AddedToSetAt`
   umschlüsseln, `AddedAtFromRest = true`, un-archivieren, `ArchivedAt := null`.
5. **Namens-Bindung** (D11): offener Eintrag → aktiver provisorischer eigener Slot gleichen Namens; datiert autorisiert dessen Datum.
   Undatierter Eintrag darf zusätzlich an einen aktiven autoritativen eigenen Slot gleichen Namens binden; ein undatierter Eintrag
   überschreibt nie ein Datum.
6. **Neue Slots** für alle übrigen Einträge (datiert → autoritativ; undatiert → `AddedAt = null`, provisorisch).
7. **Archivierung** jedes aktiven eigenen Slots, der in diesem Durchlauf nicht gebunden wurde (`ArchivedAt = now`).
8. **D13:** `Emote.Name` = Name des aktiven eigenen Slots mit kleinstem `AddedAt` (`null` zuletzt, dann `Name` ordinal, dann `Id`).
   **[E9]** `Emote.FirstSeenAt` folgt demselben Slot, aber ein abgeleitetes `null` überschreibt kein bekanntes Datum (Regel „null never
   overwrites" aus DECISIONS 2026-08-03 bleibt; ein Overlay-Ausfall darf kein Datum löschen, das der nächste datierte Lauf ohnehin
   wieder setzt).

**Nach allen Gruppen:** Emotes ohne Live-Eintrag → aktive eigene Slots archivieren (auch unter einem schon archivierten Emote, z. B.
nach einem Delta-Pull), Emote archivieren, b1/b2 und Leave unverändert auf Emote-Ebene. Der 15-Minuten-Messlog bleibt.

Alle Vergleiche und Ordnungen laufen **im Speicher** mit `StringComparer.Ordinal`; kein `ORDER BY "Name"` in SQL (sonst bräuchte der
Test eine ICU-Spaltencollation, Muster `AddEmoteSlotsMigrationTests.UseLinguisticNameCollationAsync`).

**[E10] Reine Entscheidungslogik als eigene Klasse:** Schritte 2–8 rechnen in einer internen, statischen, BCL-reinen Klasse in
`Infrastructure/Services` (Präzedenz `ArchivedEmoteRowUpsert`, `EmoteNameMatching`) aus einer Momentaufnahme (Live-Einträge der Gruppe,
eigene Slots, Seed des aktiven Sets) einen Plan: Bindungen, Umbenennungen, Autorisierungen, D7-Umschlüsselungen, neue Slots,
Archivierungen, Seed-Aktion, abgeleiteter Name/Datum, Inventaränderung ja/nein. Der Dienst wendet den Plan auf getrackte Entitäten an.
Damit sind Reihenfolge, Gleichstände und D7 als Unit-Tests billig prüfbar; die Integrationstests beweisen die Verdrahtung.

### 2.4 Settle (§6.3, D12)

**[E11] Erst Zielsuche nach den normalen Identitätsregeln, dann Umschlüsseln.** Zielkandidat ist der Eintrag, dem der Seed gehört: bei
|G| = 1 der eine Eintrag, bei |G| ≥ 2 und allen datiert `oldest` (kleinstes Datum, Gleichstand ordinal kleinster Name).

- **Ziel existiert schon** — der Kandidat würde nach Schritt 3 (datiert) bzw. Schritt 5 (undatiert, aktiver eigener Slot gleichen
  Namens) an einen eigenen Slot binden: die Zeilen des Seeds (nur das aktive Set, M1-Invariante) werden auf diesen Slot umgebunden, der
  Seed wird `SeedUnsettled = false`, `IsArchived = true`, **`ArchivedAt = null`** (**[E12]**: „war nie ein eigener Eintrag" — schließt
  ihn als D7-Kandidaten aus und stempelt kein irreführendes Archivdatum). Das deckt den #371-Randfall „Seed neben REST-Slot" und ist
  §6.3 erster Absatz; die Erweiterung vom wörtlichen 3-Tupel auf die Datumsregel aus §6.5 verhindert, dass ein Seed neben einem nach
  Umbenennung namensverschiedenen Slot desselben Datums einen zweiten Slot für denselben Eintrag erzeugt.
- **Sonst** wie §6.3: |G| = 1 datiert → Seed bekommt Datum und Namen, autoritativ; |G| = 1 undatiert → Name, provisorisch; |G| ≥ 2 alle
  datiert → Seed auf `oldest` umgeschlüsselt. In allen Fällen Marke weg und Seed aktiv (`IsArchived = false`, `ArchivedAt = null`),
  denn er steht jetzt für einen Live-Eintrag.
- **|G| ≥ 2 mit mindestens einem undatierten Eintrag → vertagt** (Marke bleibt, Gruppe übersprungen). Gibt es keinen Seed des aktiven
  Sets, gilt das nicht — dann binden undatierte Einträge nach Namen (D11 e).
- Danach durchlaufen **alle** übrigen Einträge die Schritte 3–6 (**[E13]**, U1: §6.3 „Schritt 5" meint den gewöhnlichen Pfad; im
  Grundfall ergibt das neue Slots, AK 5, und bei schon vorhandenen autoritativen Slots keinen Unique-Konflikt).
- Mehr als ein Seed des aktiven Sets für dasselbe Emote (Invariante verletzt) → nacheinander; ab dem zweiten greift „Ziel existiert".
- Ein Seed des aktiven Sets, dessen Emote keinen Live-Eintrag hat, bleibt unsettled und unberührt (§6.3 letzter Absatz); kehrt das
  Emote zurück, setzt Schritt 2 seine Historie fort.
- `UsageStats`-Zeilen mit `SlotId NULL` (alte Schreiber, lokal) fasst das Settle nicht an; sie hängt `EmoteSlotSeedSql` (M2-`Up`) an
  den Slot, der die Zeilen des Paars trägt (E17 aus #371).
- Log je Kanal und Durchlauf, wenn > 0 vertagt: Spec-Wortlaut mit Kanal davor, etwa „{Channel}: settle deferred: v4 dates missing for
  {Count} emotes" (Information). Der Kanal muss drinstehen, weil §11.2 Schritt 6 (a) die Kanäle aus dieser Zeile abliest.

### 2.5 Leser über Slots (D15, D27; AK 32) und die Virtuell-Regel

**[E14] Ein aktives Emote ohne aktiven eigenen Slot zählt in allen #372-Lesern als genau ein virtueller Slot unter `Emote.Name`.** Den
Zustand gibt es nach dem Deploy bis zum ersten Abgleich jedes Kanals, dauerhaft bei Kanälen mit Sync-Fehlergrund, nach einem
Delta-Push bis zum nächsten Tick (Delta-Pfad schreibt keine Slots, Kind 4) und bei vertagtem Settle; §4.1 nennt ihn ausdrücklich
vorübergehend. Ohne die Regel fiele `occupiedSlots` dort auf 0 und Kollisionen verschwänden. Mit ihr verhalten sich alle Leser vor dem
ersten Abgleich exakt wie heute.

- **`OccupiedSlots`** = Summe über aktive Emotes von max(1, Zahl aktiver eigener Slots).
- **`DuplicateNames`** = Namen (ordinal) über aktive eigene Slots aktiver Emotes plus virtuelle Slots, die bei > 1 **verschiedenen**
  `EmoteId` vorkommen; DTO-Form unverändert (je Name die beteiligten Emotes, je Emote einmal). Zwei Slots desselben Emotes sind nie ein
  Duplikat. Zweistufige Form (Kollisionsnamen in SQL, Zeilen danach) darf bleiben.
- **Tracker** in `RefreshMatchCacheAsync`: Eingabe = dieselbe Menge (Name, EmoteId) distinct; **[E15]** die Map selbst bleibt
  `Coalesce` über `Emote.Name` (D19). Folge: der Tracker meldet auch Kollisionen eines Zweitnamens, den die Map in #372 noch nicht zählt;
  der Logtext wird neutral formuliert (englisch, s. 2.7).

### 2.6 Konflikt- und Wiederholungspfad

- **`IsSlotKeyConflict`**: Unique-Verletzung mit `ConstraintName` = `IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative`. **[E16]** Der
  Indexname wird eine Konstante, die `AppDbContext` (Mapping, String bleibt gleich → kein Snapshot-Diff) und das Prädikat teilen; Ort
  neben dem Sperr-Helfer, damit Kind 5/6 dasselbe Prädikat nutzen.
- **[E17]** `SaveSyncAsync` behandelt `IsEmoteKeyConflict`, `IsSlotKeyConflict` und Deadlock (SqlState 40P01, als `DbUpdateException`
  aus `SaveChanges` **oder** als nackte `PostgresException` aus Sperr-/Bulk-/Leave-Statement) gleich: **eine** Wiederholung mit
  geleertem ChangeTracker und neu gelesener Kanalzeile, je eine englische Warnzeile; der zweite Fehlschlag propagiert wie heute.
  Unter der Sperre kann ein Slot-Konflikt in #372 nur ein Schreiber ohne Sperre auslösen — es gibt keinen; das Prädikat ist der Gurt
  für Kind 3–6.

### 2.7 Inventaränderung, Logs

- **[E18] `changed` misst das sichtbare Inventar** (das, was die Leser aus 2.5 zeigen), sonst wie Spec §6.2: Anlage, Un-Archive,
  Umbenennung, Archivierung eines eigenen Slots; Emote angelegt/un-archiviert/archiviert, Bild, abgeleiteter Name. **Nicht:** Autorisieren
  eines Datums, Entfernen der Marke, Umbinden von Zeilen, Archivieren eines leeren Seeds, Archivieren von Slots unter einem schon
  archivierten Emote, und **das Anlegen des ersten eigenen Slots eines Emotes, wenn er genau dessen virtuellen Slot (gleicher Name)
  ersetzt**. Sonst feuerte der erste Abgleich nach dem Deploy `channel.synced` für jeden Kanal, und Bestandstests wie
  `SyncChannel_NoOpResync_ReportsNoChanges` (Emote-Zeile ohne Slot) kippten ohne Nutzerwirkung. AK 5/12 bleiben erfüllt (dort entsteht
  ein zweiter sichtbarer Slot bzw. ein neuer Name).
- **[E19] Logs:** neu nur die Settle-Zeile (2.4) und eine Information-Zusammenfassung je Kanal und Durchlauf, **nur wenn** sich Slots
  geändert haben (Zählwerte angelegt/umbenannt/D7/archiviert/gesettelt/vertagt, keine Namen). Keine Metriken (U9). Log-Zeilen in den
  Methoden, die #372 umschreibt (`ReconcileAsync`, Tracker-Zeilen in `RefreshMatchCacheAsync`), werden englisch (CLAUDE.md „Sprache");
  der K1-Kommentar zur Dedup fällt mit der Dedup.

### 2.8 `ArchivedSlotUpsert` (U7)

**[E20]** Interne statische Klasse in `Infrastructure/Services`, Schwester von `ArchivedEmoteRowUpsert`: legt für **datierte** Einträge
`(EmoteId, Name, AddedAt)` race-sicher einen autoritativen Slot an, der „nie im aktiven Set war" (`IsArchived = true`,
`ArchivedAt = null`, `AddedAtFromRest = true`, `SeedUnsettled = false`, `LastSyncedAt = now`), mit einem Statement und Konfliktziel =
der partielle Unique-Index (Indexinferenz mit Prädikat), Einfügen in Schlüsselreihenfolge. Ein vorhandener Slot desselben Schlüssels,
aktiv oder archiviert, bleibt unverändert. Rückgabe: Slot-Id je Eingabeschlüssel und die Menge der tatsächlich angelegten (für
`CreatedSlot`, Kind 5). Undatierte Einträge (D11 d) gehören **nicht** hinein: sie brauchen die Emote-Sperre und Namensbindung, die Kind
3/5 selbst festlegen. In #372 ohne Produktivaufrufer; nur Tests.

## 3. Auflösung der offenen Punkte aus der Zuarbeit (U1–U17)

U1 → E13 · U2 → Inhalt steht in AK 13 selbst, der Test bildet ihn ab, der PR nennt den toten Verweis „Beispiel iv" · U3 → Settle ist Vorschritt je Gruppe mit Zielsuche nach Schritt-3/5-Regeln (E11), der gesettelte Seed ist danach ein gewöhnlicher Slot · U5 → E15; die Zeile „Seeds ausgelassen" (§6.1) gehört zur Map, also Kind 3 · U6 → Abschnitt 5 · U7 → E20 · U8 → E1, E3–E6, E17 · U9 → keine Metriken (E19) · U10 → E12 · U11 → E7 · U12 → E8 · U13 → E18 (Umschlüsseln mit gleichem Namen zählt nicht, mit neuem Namen als Umbenennung) · U14 → überall ordinal und case-sensitiv, keine Normalisierung (die Matching-Normalisierung bleibt in `EmoteNameMatching`) · U15 → E2; v4-Alias ≠ v3-Name → undatiert (D11 e) · U16 → K1-Kommentar fällt (E19), Nachtrag am Entwurfseintrag (Abschnitt 9) · U17 → keine Planfolge.

**[E21] U4 — Map auf Emote-Schlüssel nach D13:** hingenommen. Zwischen #372 und Kind 3 zählt die unveränderte Map den **ältesten** Namen (bei `handofblood` `ome44` statt seit #341 `hob44`). Betrifft nur Dev (Prod sieht alle Kinder in einem Fenster); der DECISIONS-Nachtrag nennt es.

**Wenn Daten fehlen (geprüft gegen die Spec):** „Warten statt Raten" gilt nur fürs Settle bei |G| ≥ 2 (§6.3). Im gewöhnlichen Abgleich
sind fehlende Daten D11 (e): Namensbindung, auch an autoritative Slots. Ohne Umbenennung ändert ein Overlay-Ausfall daher nichts; eine
Umbenennung **während** eines Ausfalls wird als Entfernen + Hinzufügen gelesen (alter Slot archiviert, neuer provisorisch), und der
nächste datierte Lauf bindet den alten Slot per Datum zurück, während der provisorische als unerklärt archiviert wird und bleibt
(D11: nie gelöscht). Eine Rückkehr während eines Ausfalls kann D7 nicht anwenden (D7 nur datiert) — die Historie bleibt dann auf dem
archivierten Slot. Beides ist Spec-Folge und steht als Randfall in Abschnitt 4.

## 4. Randfälle

| Fall | Verhalten | Nachweis |
|---|---|---|
| Emote-Zeile ohne Slot (vor erstem Abgleich, Delta-Push) | Leser zählen virtuell (E14); Abgleich legt den Slot an, ohne `changed` (E18) | Slot-Test 14, Status-Test 1 |
| Fremder Seed eines aktiven Emotes (1:1 aktiv aus M1) | unberührt, nicht gezählt, kein D7-Kandidat | Slot-Test 6, Unit |
| Kanal wechselt auf Set B | Seed von B wird in Schritt 2 gesettelt, A-Slots ohne Datumstreffer werden archiviert | Slot-Test 6 |
| Seed neben REST-Slot (#371-Randfall) | Zeilen auf den REST-Slot, Seed archiviert ohne Datum | Slot-Test 5, Live 8 |
| Mehr-Slot-Gruppe, Overlay fehlt ganz | Settle vertagt, Gruppe unverändert, Zeile mit Kanal | Slot-Test 2 |
| Aliasloser Alt-Eintrag | datiert über `defaultName` (E2) | Client-Test 2 |
| Umbenennung während Overlay-Ausfall | Entfernen + Hinzufügen, nächster datierter Lauf bindet zurück, provisorischer Slot bleibt archiviert | Unit |
| Rückkehr während Overlay-Ausfall | neuer provisorischer Slot, kein D7; nächster datierter Lauf autorisiert ihn | Unit |
| Delta-Pull vor dem nächsten Tick | Emote archiviert, Slots noch aktiv → Leser ignorieren sie (Emote-Filter); nächster Tick archiviert sie ohne `changed` | Unit + Status-Test |
| Gleiches `added_at` zweier Einträge (§2.4) | nur namensgleiche Paare binden, Rest neu bzw. archiviert | Slot-Test 9 |
| Zweite Beobachtung desselben Eintrags unter neuem Namen (AK 27) | wartet auf die Sperre, liest neu, benennt um, eine SlotId | Konkurrenz-Test 1 |
| Paralleler Anleger desselben Emotes | E10-Retry, zweiter Versuch sperrt die vorhandene Zeile | bestehende E10-Tests |
| Deadlock mit Legacy-Bericht oder Purge | eine Wiederholung (E17); bei Purge endet sie im bestehenden „row vanished" | Konkurrenz-Test 3 |
| Veraltete getrackte Entitäten im Tick-Kontext | Lesen nach der Sperre liefert DB-Stand (2.2 Schritt 3) | Review + Konkurrenz-Test 1 |
| Flush schreibt während des Settle (#372 zählt weiter je Emote) | `NO KEY UPDATE` blockiert die FK-Prüfung nicht; neue Zeilen bleiben `SlotId NULL` bis M2 | Sperr-Test 2 |
| Implausibler Wipe | Wächter unverändert vor dem Schreiben | bestehende Tests |

## 5. AK-Grenze in #372 (PR-Text führt die Tabelle mit „erfüllt / Rest in #37x")

AK 5–13 und 47–50: Test (Abschnitt 6); live zusätzlich 5, 7 (Umbenennung, Tausch, Entfernen + Hinzufügen, Rückkehr), 9, 12, 47. AK 8
zweite Hälfte und 48–50 sind live nicht herstellbar (identische Zeitstempel bzw. D7-Vorbedingungen lassen sich über 7TV nicht gezielt
erzeugen) → nur Test, im PR so benannt. **AK 4** (Settle-Hälfte, formal Kind 1): Set A gesettelt, B-Zeilen unberührt; nach Wechsel auf
B liegen sie auf Bs ältestem datiertem Slot — Test hier. **AK 27:** REST-Seite unter der Sperre (zweiter Schreiber per Test-Transaktion
mit dem Sperr-Helfer) → erfüllt für REST, Enqueue-Hälfte „Rest in #375". **AK 29:** Live-Probe des Betreibers vor dem Merge. **AK 32:**
vorgezogen aus #376 (Tests hier), #376 übernimmt den Haken.

## 6. Tests

Alle Integrationstests `[Collection("Postgres")]`, 7TV über NSubstitute-`ISevenTvApiClient` mit 4-Argument-`SevenTvEmote` für datierte
Einträge.

**6.1 Client (Unit, `SevenTvApiClientChannelStateTests` — die Spec nennt ihn „SevenTvApiClientTests"):** bestehende Overlay-Fälle
bekommen `alias` in ihre JSON-Antworten. Neu: (1) Fixture `Unit/TestData/set-entries-alias.json` aus der Live-Probe — zwei Einträge
einer ID mit verschiedenen Aliasen erhalten je ihr eigenes Datum, ein Eintrag ohne Treffer bleibt undatiert (AK 11); (2) Alias `null` →
Datum über `defaultName` (E2).

**6.2 Entscheidungsklasse (Unit, neu, E10):** Tausch A↔C/B↔A; Gleichstand mit 1:1-Sonderfall vs. namensentschieden; D7 mit Arbeitsstand
nach Schritt 3 (S1 umbenannt, dann Rückkehr unter dem frei gewordenen Namen → D7 greift); D7-Auswahl `ArchivedAt` desc, `Id` asc;
D7 schließt `ArchivedAt NULL` und unsettled Seeds aus; Namensbindung undatiert an autoritativ, datiert autorisiert provisorisch;
D13-Ordnung inkl. aller `null` und E9; Settle-Fälle (Ziel existiert per Datum, per Namen; Umschlüsseln |G| = 1/≥ 2; vertagt; zwei
Seeds); `changed` je Fall aus E18; die beiden Ausfall-Randfälle aus Abschnitt 4. Umfang nach Ermessen, ungefähr 15 Fälle.

**6.3 `SevenTvSyncServiceSlotTests` (Integration, neu; ersetzt `SevenTvSyncServiceDuplicateEmoteIdTests`):**
1 AK 5 (inkl. `HasChanges` nur im ersten Lauf, nur A-Zeilen am Seed) · 2 AK 6 (beide Varianten, Log-Zeile mit Kanal über
`RecordingLogger`, nächster Lauf mit Daten = AK 5) · 3 AK 7 Tausch · 4 AK 7 Entfernen + Hinzufügen und Rückkehr · 5 Seed neben
REST-Slot (Zeilen umgebunden, Seed `IsArchived`, `ArchivedAt NULL`, Marke weg) · 6 AK 4-Hälfte und fremde Seeds (Seed B unberührt
inkl. `IsArchived`/`ArchivedAt`/`LastSyncedAt`; Wechsel auf B settelt B) · 7 AK 8 Wiederkehr nach REST-Lag · 8 AK 8 Gleichstand ·
9 AK 9 (zwei Slots, einer geht, keine Leave; beide gehen, Leave nach b1) · 10 AK 10 · 11 AK 12 · 12 AK 13 (provisorischer Slot
per Test-Seed, weil der Delta-Pfad in #372 keinen anlegt) · 13 AK 47 · 14 AK 48 · 15 AK 49 · 16 AK 50 · 17 erster eigener Slot
ersetzt virtuellen ohne `HasChanges` · 18 `FirstSeenAt` bleibt bei undatiertem Lauf (E9). Summen je `(EmoteId, EmoteSetId)` vor/nach
gleich, wo ein Settle läuft.

**6.4 Konkurrenz (Integration, neu, eigene Klasse):** (1) AK 27 REST-Seite: Emote-Zeile vorhanden, eine Test-Transaktion nimmt die Sperre
über den Helfer und legt A(t) an; der Abgleich mit Live `[C(t)]` blockiert (`PostgresLockProbe.WaitUntilBlockedOnLockAsync`), nach
Commit liest er neu und benennt **denselben** Slot in C um; Gegenprobe Live `[A(t), C(t)]` → zwei Slots. (2) `IsSlotKeyConflict`-Pfad:
ein `SaveChanges`-Interceptor (`PostgresFixture.CreateDbContext(interceptors)`) legt beim ersten Speichern einen kollidierenden
autoritativen Slot über eine zweite Verbindung an → eine Wiederholung, Ergebnis ein Slot. (3) Deadlock-Pfad: Interceptor wirft beim ersten
Speichern eine `PostgresException` 40P01 → eine Wiederholung, Erfolg; beim zweiten Mal propagiert sie.

**6.5 Sperr-Helfer und `ArchivedSlotUpsert` (Integration):** (1) zwei Sperrer desselben Emotes serialisieren, Reihenfolge nach `Id`;
(2) ein `UsageStats`-Insert für ein gesperrtes Emote wartet **nicht** (E1, Gegenprobe in einem Kommentar: mit `FOR UPDATE` würde er
warten); (3) Upsert legt an, ist beim zweiten Aufruf No-op, lässt vorhandenen aktiven Slot unverändert, meldet nur Angelegtes;
(4) Prädikat `IsSlotKeyConflict` (Unit, `PostgresException` mit Constraint-Namen, positiv/negativ).

**6.6 `EmoteSetStatusServiceTests`:** +2 (AK 32: zwei Slots eines Emotes = 2 Plätze und kein Duplikat; Namenskollision zweier Emotes über
Slot-Namen inkl. Zweitname) und ~3 angepasst; +1 Virtuell-Regel (Emote ohne Slot zählt 1, unsettled Seed zählt nicht separat, aktive
Slots unter archiviertem Emote zählen nicht). Tracker: ein Fall in `SevenTvSyncServiceTests` (Kollision über Zweitnamen → Warnzeile).

**6.7 Angepasst:** `SevenTvSyncServiceTests` (~10: Name/`FirstSeenAt` jetzt abgeleitet, Dedup-Erwartungen), `…LeaveObservationTests`
(AK 9), `…PlaceholderTests` (Slot-Anlage beim Un-Archive). Erwartung: die übrigen Sync-Testklassen bleiben unverändert grün.

**6.8 Nicht nötig:** Worker.Tests (kein Worker-Code), Api.Tests (kein Endpoint/Filter), Vitest/E2E (kein `web/`).

## 7. Live-Verifikation (Regel 16) — Vorschlag, braucht Freigabe (R2)

Randbedingungen: geteilte Dev-DB `emotepurge` (M1 seit 11.10.), geteilter Api-Container und geteilter Worker (Stand vor #372) gehören
gerade einer anderen Sitzung und werden **nicht** angefasst; Redis ist geteilt. **Nie `docker compose` aus dem Worktree, nie etwas im
Haupt-Checkout lesen.**

1. **Kopie:** im Container `emotepurge-dev-postgres` neue DB `emotepurge_slots372`, befüllt per `pg_dump | psql` **im** Container (Benutzer
   aus `printenv POSTGRES_USER`, Socket-Auth). Keine serverweiten Eingriffe. Das Passwort für den Host-Zugriff aus `printenv
   POSTGRES_PASSWORD` des Containers in eine Shell-Variable, nie ausgeben, CR abschneiden.
2. **Last begrenzen:** in der Kopie alle Kanäle `IsBotActive = false` außer dem Testkanal des Betreibers (R2), `handofblood` (nur lesend:
   echter Mehr-Slot-Fall `01KAMA120V9DC2P1YW9QWFHA16`) und höchstens zwei weiteren Kanälen mit Mehr-Slot-Emotes und Nutzung in mehreren
   Sets (per SQL aus der Kopie gewählt).
3. **Vorher messen** (CSV ins Scratchpad): je `(EmoteId, EmoteSetId)` Zeilenzahl und drei Summen; alle Seeds mit allen Spalten; Emotes der
   aktiven Kanäle mit Name/`FirstSeenAt`.
4. **Wegwerf-Redis:** `docker run` eines Redis-Containers auf eigenem Host-Port (z. B. `127.0.0.1:6390`), Name `redis-slots372`.
5. **Worker aus dem Worktree:** `dotnet run --project src/EmotePurge.Worker` mit Env-Overrides: Connection-String auf die Kopie, Redis auf
   den Wegwerf-Port, `ChatLogBackfill__Enabled=false`, `Retention__Enforce=false`, `SevenTv__EventApi__Enabled=false` (reiner REST-Pfad),
   Helix-Intervalle hoch (`Twitch__LivePollIntervalSeconds`, `Twitch__IdentityReconcileIntervalMinutes`). Fehlen Helix-Credentials, loggen
   diese Dienste Fehler — für #372 belanglos. PID notieren.
6. **Erster Tick (= erster Abgleich nach Deploy), SQL-Prüfungen:** (a) unsettled Seeds des aktiven Sets = 0 außer den Kanälen der Zeile
   „settle deferred"; (b) `handofblood`-X: zwei eigene Slots, `ome44` autoritativ mit dem alten Datum und allen Zeilen des aktiven Sets,
   `hob44` ohne Zeilen, `Emote.Name = ome44` (AK 5); (c) fremde Seeds byte-gleich zur CSV (AK 4-Hälfte); (d) Summen je Paar = CSV;
   (e) kein autoritativer Slot mit `AddedAt NULL`; kein aktiver eigener Slot unter archiviertem Emote; aktive Emotes ohne aktiven eigenen
   Slot nur in vertagten Gruppen; (f) Dauer des Abgleichs für `handofblood` aus den Log-Zeitstempeln. **Zweiter Tick:** keine Slot-Schreibung
   (`LastSyncedAt` unverändert), keine Zusammenfassungszeile.
7. **Testkanal, Handgriffe des Betreibers in 7TV** (je danach einen Tick abwarten; die Zuarbeit für T7 schreibt die SQL-Abfragen):
   (i) Emote E mit Alias a1 hinzufügen → neuer autoritativer Slot; (ii) zweiten Alias a2 hinzufügen → zweiter Slot, zweiter Tick ohne
   Änderung (AK 12); (iii) a2 → a3 umbenennen → gleiche SlotId, `AddedAt` unverändert; (iv) a1 und a3 über einen Zwischennamen tauschen →
   beide SlotIds behalten ihr Datum, Namen getauscht (AK 7); (v) a3 entfernen → nur dieser Slot archiviert, Emote aktiv, keine neue
   Leave-Zeile (AK 9); (vi) E unter a3 wieder hinzufügen → **dieselbe** SlotId, neues `AddedAt`, `AddedAtFromRest = true` (AK 47, AK 7
   Rückkehr); (vii) beide Aliase entfernen → Slots und Emote archiviert; Leave nach b1 bzw. b2 (Glaubwürdigkeitsfenster 30 min beachten).
8. **Optional, #371-Randfall:** im Testkanal-Chat E benutzen (der Worker zählt in #372 noch je Emote und schreibt `SlotId NULL`), dann
   `EmoteSlotSeedSql.AttachUnslottedUsageRowsSql` auf der Kopie in einer Transaktion mit `lock_timeout` und `LOCK TABLE "UsageStats"` fahren:
   ist E erst nach M1 entstanden und hatte keine Zeilen, entsteht ein Seed neben dem REST-Slot; nächster Tick → Zeilen am REST-Slot, Seed
   archiviert ohne Datum, Marke weg.
9. **Abbau:** Worker per SIGTERM (PID vorher mit `ps -o args=` prüfen), `docker rm -f redis-slots372`, `DROP DATABASE emotepurge_slots372
   WITH (FORCE)`. Protokoll ins Scratchpad; PR nennt SHA, Zahlen, Dauer, keine Hostnamen/Passwörter, keine Kanalnamen außer Testkanal und
   `handofblood`.

Variante (a) — den geteilten Docker-Worker durch ein #372-Image ersetzen — wird nicht empfohlen: er gehört gerade einer anderen Sitzung und
schriebe Slot-Änderungen in die geteilte DB.

**AK 29 (vor dem Merge, Betreiber von Hand):** die neue Query gegen die öffentliche v4-GQL für ein Set mit einem Mehr-Slot-Emote (und, wenn
vorhanden, einem aliaslosen Alt-Eintrag) fahren; Antwort wird `Unit/TestData/set-entries-alias.json` (darf auf wenige Einträge gekürzt
werden, Struktur bleibt). Den fertigen Befehl liefert der Orchestrator nach T1; der PR nennt Datum und Set-ID, kein Token.

## 8. Gates

- `dotnet test EmotePurge.slnx` (Docker), `npm --prefix web test -- --watch=false` (läuft mit, `web/` unberührt), **kein** E2E.
- `dotnet format EmotePurge.slnx` sauber; kein Snapshot-Diff (Probe-`migrations add` erzeugt nichts, E16).
- Live-Verifikation (Abschnitt 7) und AK 29 vor dem Feature-Commit bzw. vor dem Merge.
- **Erst committen, dann** `node scripts/coverage-local.mjs --backend-only`; Schwachstelle ist erfahrungsgemäß `SevenTvSyncService`
  (große Bestandsdatei, Näherung dateigenau).
- Opus-Review, dann `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem Worktree; Befund unverändert vorlegen;
  Widerspruch → Fable nur über die strittigen Findings. Merge durch den Nutzer.

## 9. Commits und DECISIONS

Conventional Commits, englisch: (1) `docs: add implementation plan for slot-aware reconcile` (diese Datei); (2) `feat(infrastructure):
key the v4 set-entry overlay by emote id and alias` (+ Fixture, Tests); (3) `feat(infrastructure): add emote row lock, slot key conflict
detection and ArchivedSlotUpsert`; (4) `feat(infrastructure): reconcile REST entries per slot with date identity, resumption and per-set
settle` **plus DECISIONS-Nachtrag**; (5) `feat(infrastructure): read occupied slots, duplicate names and the collision tracker from
slots`; danach `fix:`/`test:` aus Review. Tests reisen im Commit ihres Features.

**DECISIONS [E22]:** wie #371 ein datierter Absatz „Child 2 (slot reconcile), <Datum>" direkt hinter „Child 1 (M1)" im Entwurfseintrag
von 2026-10-10, englisch, mit: E1 (Sperrmodus und Begründung), E3–E6 (Umfang, Reihenfolge, Helfer als Konvention für Kind 4–6), E7/E8
(Seeds nur durch das Settle), E11/E12 (Zielsuche, leerer Seed ohne Archivdatum), E14 (Virtuell-Regel der Leser), E17 (Wiederholungspfad),
E18 (`changed` misst sichtbares Inventar), E21 (Map zählt bis Kind 3 den ältesten Namen), E2 (Overlay-Rückfall `defaultName`), E9.
Die `Betrifft`-Zeile bekommt `EmoteSetStatusService.cs`, `ArchivedSlotUpsert.cs` und die Datei des Sperr-Helfers. Kind 9 macht final.

## 10. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; Modell explizit)

Builds und Testläufe teilen sich `obj/` im Worktree: **nie zwei Subagents gleichzeitig bauen oder testen lassen.** T1–T3 sind
dateidisjunkt und dürfen parallel implementiert werden, ihre Testläufe aber nur nacheinander (oder je ein eigener Worktree).

| # | Aufgabe | Modell | Input | Fertig, wenn |
|---|---|---|---|---|
| T1 | Overlay | sonnet | 2.1, E2, 6.1; `SevenTvApiClient.cs`, `SevenTvApiDtos.cs`, `SevenTvApiClientChannelStateTests` | Tests grün mit vorläufiger, strukturgleicher Fixture; fertiger AK-29-Befehl für den Betreiber im Abschlussbericht; echte Fixture ersetzt die vorläufige vor dem Merge |
| T2 | Sperr-Helfer, Indexkonstante, `IsSlotKeyConflict`, `ArchivedSlotUpsert` | sonnet | 2.2 (E3, E4, E6), 2.6 (E16), 2.8 (E20), 6.5; `ChannelQueries`, `ArchivedEmoteRowUpsert`, `PostgresLockProbe` | 6.5 grün; kein Snapshot-Diff |
| T3 | **Entscheidungsklasse** (heikel) | **opus** | 2.3, 2.4, 2.7 (E18), Abschnitt 3 „Wenn Daten fehlen", 6.2 | Unit-Tests 6.2 grün; jede Regel trägt im Code den Spec-Verweis (§6.2 Schritt n, D-Nummer) |
| T4 | **Integration in `SevenTvSyncService`** (heikel: Sperre, Settle-Umbindung, Wiederholung) | **opus** | 2.2–2.7, E17, E19, Abschnitt 4; Ergebnis T2/T3 | 6.3 und 6.4 grün; `DuplicateEmoteIdTests` gelöscht; 6.7 angepasst; ganze Infrastructure-Suite grün; DECISIONS-Absatz (E22) im Commit |
| T5 | Leser über Slots | sonnet | 2.5 (E14, E15), 6.6; `EmoteSetStatusService`, `RefreshMatchCacheAsync` | 6.6 grün; Logtexte englisch |
| T6 | Live-Verifikation | sonnet | Abschnitt 7 vollständig, Freigabe R2, Handgriffe des Betreibers abgestimmt | alle Prüfungen aus 7.6–7.7 protokolliert, Abbau erledigt |
| T7 | Review, Gates, PR | **opus** (Review); Codex Sol | Branch, Abschnitte 5, 8, 9, 11 | drei Suiten grün, Coverage ≥ 80 %, Codex-Befund vorgelegt, PR mit AK-Tabelle und Abweichung 1.3; Kommentare auf #373–#376 mit Abschnitt 11 |

Reihenfolge: (T1 ∥ T2 ∥ T3) → T4 → T5 → T6 → T7. AK 29 (Betreiber) kann jederzeit nach T1 laufen, muss vor T7 da sein. Ob T3 oder ein
Review von T3/T4 zusätzlich an Fable geht, entscheidet der Orchestrator (Identitätslogik ist hochgehebelt: ein Fehler verteilt Historie
falsch und ist später kaum zu entwirren).

## 11. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **Kind 3 (#373):** Die Map aus gesettelten eigenen Slots darf die Virtuell-Regel (E14) **nicht** auf Emotes anwenden, die einen unsettled
  Seed des aktiven Sets haben — sonst bricht AK 17. Die Information-Zeile „ausgelassene Seeds" gehört zur Map. Das Settle-Umbinden
  (`UsageStats.SlotId` vom Seed auf einen vorhandenen Slot) bleibt nach M2 kollisionsfrei, weil der Seed vor M2 entstanden ist und Seed-
  und Zielzeilen desselben Paars nach dem alten Unique-Index verschiedene Tage haben; Fixture „M1 → alter Writer → Kind-2-Settle → M2"
  ruft das Settle über `SyncChannelAsync` mit gefaktem 7TV-Client (es gibt bewusst keine eigene Settle-API). E21 endet mit Kind 3.
- **Kind 4 (#374):** Der Delta-Pfad schreibt in #372 keine Slots; sobald er es tut, nimmt er für betroffene Emotes denselben Sperr-Helfer
  (Api-Schreiber laufen parallel zum Worker). Leser-Regel E14 bleibt als Netz für Emotes ohne Slot.
- **Kind 5 (#375):** Sperr-Helfer (Modus E1), `IsSlotKeyConflict`, `ArchivedSlotUpsert` (nur datiert) und die Entscheidungsklasse (D7,
  Settle-Zielsuche) wiederverwenden statt nachbauen.
- **Kind 6 (#376):** Entries-Berichte nehmen die Emote-Sperre **vor** dem Leave-Upsert; den Legacy-Pfad dabei mit umstellen, dann entfällt
  der Rest-Zyklus aus E5. `/totals` aktiv muss für Emotes ohne eigenen Slot dieselbe Virtuell-Regel oder eine bewusste Alternative
  wählen; die nicht aktive Ansicht ordnet Seeds über das `EmoteSetId` ihrer Zeilen zu (E7). AK 32 ist hier schon getestet.
- **Kind 9 (#379):** Nachkontrolle §11.2 Schritt 6 (a) liest die Kanäle aus „{Channel}: settle deferred …"; Live-Schritte aus Abschnitt 7
  sind für den Abnahmelauf wiederverwendbar; DECISIONS final inkl. E1.

## 12. Rückfragen an den Betreiber

- **R1 — Sperrmodus (E1):** Die Spec schreibt `SELECT … FOR UPDATE`. Darf es `FOR NO KEY UPDATE` sein? Gleiche Serialisierung unter allen
  Schreibern desselben Emotes, aber keine Blockade der FK-Prüfungen von Flush, Backfill-Block und Ballot (sonst echte Deadlock-Zyklen mit
  dem Settle). **Empfehlung: ja**, als dokumentierte Abweichung im DECISIONS-Nachtrag.
- **R2 — Live-Verifikation (Abschnitt 7):** Freigabe für Kopie `emotepurge_slots372` auf dem geteilten Dev-Postgres, Wegwerf-Redis auf
  eigenem Port und Worker per `dotnet run` aus dem Worktree — und: **welcher Testkanal** (Twitch-Kanal, dessen aktives 7TV-Set du für
  die Handgriffe 7.7 von Hand bearbeitest)? **Empfehlung:** diese Variante, mit deinem eigenen Testkanal; `handofblood` nur lesend.
