# Plan #376 — Emote-Slots, Kind 6: Lese-API je Slot, Ballot-Namen und lebenszyklus-bewusste Berichte

Erstellt am 2026-10-12 im Worktree `EmotePurge-slots` (Branch `feat/376-slot-read-api`, Basis `origin/main` @ `aaa71fce`).
**Status: Entwurf, Rückfragen R1–R7 offen.** Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md)
(§5.1–5.5, §5.7, §6.5, §9.1–9.3 nur als Abnehmer-Sicht, §13 AK 28–33, 41–42, 46, 52, 54–55, §15, §16 Kind 6; D3, D7, D13, D24–D27,
D29, D32), Issue #376 mit den Übergabekommentaren aus #372 und #373 (bindend), [Plan-372](Plan-372-Emote-Slots-Reconcile.md) §2.0,
§2.2a, §2.5 (E14), §2.9, §11; [Plan-373](Plan-373-Emote-Slots-Usage-Key.md) §1.3, §3.2 (E7), §11. Formatvorlage: Plan-373.

**Was dieser Plan trägt:** nur, was Spec und Übergaben offen lassen — die Flag-Lage auf Prod, die Darstellung nicht gesettelter
Emotes, den genauen Wire-Vertrag für #377/#378, die Bericht-Semantik im Detail, Sperrfolge, Tests, Live-Prüfung, Aufgabenfolge. Kein
Code. D0–D33 und die Betreiberentscheidungen aus #371–#373 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **R*n*** = Rückfrage an den Betreiber (Abschnitt 0); bis zur Antwort gilt
die Empfehlung als Arbeitsannahme, umgesetzt wird erst nach der Antwort.

## 0. Rückfragen an den Betreiber

**R1 — Verzweigt die Lese-API am Flag `SevenTv:SlotReconcile:Enabled`?**
Empfehlung: **ja.** Bei Flag aus liefern `/totals`, `/series` und die Ballot-Namen das heutige Bild — eine Zeile je Emote, `slotId`
`null`, Name und Datum aus der `Emote`-Zeile, `siblingNames` leer, `firstSeenAtFromRest` `false`; nur die neuen Felder kommen additiv
hinzu. Bei Flag an gilt die Slot-Sicht aus Abschnitt 2. Begründung: (1) Prod hat das Flag bis #379 aus und nur unsettled Seeds; jeder
Merge geht mit dem nächsten Main-Deploy nach Prod — die Lese-API muss dort byte-gleich zu heute bleiben bis auf additive Felder.
(2) Dieselbe Regel gilt schon für `OccupiedSlots`/`DuplicateNames` (Plan-372 2.0): eigene Slots aus einer früheren An-Phase werden im
Aus-Zustand nicht gepflegt und wären veraltet; Karte und Belegungszahl dürfen nicht auseinanderlaufen. **Ehrlich angemerkt:** auf Prod
ergäbe auch die flag-lose Slot-Sicht mit der Regel aus R2 dasselbe Bild (jedes Emote fällt dort in den Emote-Modus); der Unterschied
zeigt sich nur lokal nach An → Aus. Folge für den Zwischenstand bis #377: **mit Flag an** sieht das heutige Frontend zwei Zeilen mit
gleicher `sevenTvEmoteId` (doppelte `track`-Schlüssel, NG0955-Warnung, Auswahl kollidiert) — nur lokal, auf Prod nie, solange das Flag
aus ist.

**R2 — Wie erscheinen bei Flag an Emotes, die noch keinen gesettelten Zustand haben?** (Übergabe #372: „`/totals` braucht die
Virtuell-Regel oder eine bewusste Alternative"; Übergabe #373: zeilenlose Enqueue-Seeds und geleerte Seeds dürfen keine Karte werden.)
Empfehlung: **Emote-Modus statt Seed-Karte.** Ein Emote fällt im angefragten Set X in den Emote-Modus, wenn es (a) einen unsettled Seed
mit Zeilen von X hat (dieselbe Definition wie der Map-Ausschluss E7 aus Plan-373 und die Settle-Population aus Plan-372, nur auf X
statt auf das aktive Set verallgemeinert) oder (b) — nur in der aktiven Ansicht — keinen aktiven eigenen Slot hat (E14, virtueller
Slot). Im Emote-Modus hat es **genau eine Zeile** wie bei Flag aus: `slotId` `null`, Name `Emote.Name`, Datum `Emote.FirstSeenAt`,
`firstSeenAtFromRest` `false`, Zahlen = Summe über alle Zeilen des Emotes in X, `siblingNames` leer. **Abweichung von Spec 5.1**, die
Seeds als eigene Zeilen mit `FirstSeenAt = Slot.AddedAt` (bei Seeds `null`) zeigt. Gründe: Ein Seed zählt nicht (E7), seine Karte sähe aus
wie eine zählende; sein `AddedAt` ist `null`, die Karte verlöre „Neu"/„Im Set seit" ohne Not; sein `Name` kann veraltet sein (Plan-373
3.2); E14 verlangte ausdrücklich, dass die Leser vor dem Settle „exakt wie im Aus-Zustand" aussehen. `slotId` bleibt `null`, weil die
Zahlen der Zeile nicht genau einem Slot gehören — so gilt ohne Ausnahme: **`slotId` gesetzt ⇔ die Zeile zeigt genau die Zahlen dieses
einen Slots**, und `/daily?slotId=` liefert dieselben Zahlen wie die Karte. Bekannte, flüchtige Unschärfe: hat ein Emote einen offenen
Seed **und** schon gesettelte aktive Slots (lokal nach einer Backfill-Enqueue, bis zum nächsten Tick), zeigt `/totals` eine Karte,
`occupiedSlots` zählt die Slots — hingenommen, weil das Emote in dieser Lage ohnehin nicht zählt.

**R3 — Berichte mit `entries`: Flag-Lage und Mischform.** Empfehlung: (a) **Flag aus → `entries` werden nach der Formprüfung
ignoriert, alle Ids laufen heutige Semantik** (Emote-Ebene, Leave für jede gefundene Zeile) — der Aus-Zustand schreibt keine Slots
(Plan-372 2.0). (b) **Flag an, je 7TV-Id entschieden:** hat eine Id mindestens einen Eintrag in `entries`, gilt für sie nur die
Slot-Semantik (Abschnitt 4); Einträge ohne `slotId` werden übersprungen (keine Mutation, keine Leave — „SlotId fehlt → Eintrag
ignorieren", Spec 5.7), **ohne** Rückfall auf die Emote-Ebene. Ids ohne Eintrag laufen die heutige Semantik. Eine Anfrage darf also
beides tragen (z. B. Nutzungsseite: datierte Zeilen je Slot, Zeilen mit `slotId null` je Id). Grund für „kein Rückfall": die Spec-Zeile ist
mehrdeutig; ein Rückfall würde bei einem Mehr-Slot-Emote das ganze Emote archivieren und eine falsche Leave schreiben — genau Codex R1
Befund 5 — und Tag-Platzierungen (`Holds`) verfallen lassen. Der Resync aus Stufe 7 heilt das Übersprungene binnen Sekunden.

**R4 — Fehlercodes der Berichte.** Die Spec nennt `400 invalid_request`; diesen Code gibt es nicht. Empfehlung: **fremde `slotId` im
Ursprungskanal → `400 emote_ids_invalid`** (bestehender Code; der Text „…gehört nicht zu diesem Channel oder ist nicht mehr im Set"
passt), **Formfehler** (Eintrag ohne `sevenTvEmoteId`, Eintrags-Id nicht in `sevenTvEmoteIds`, `slotId` ohne `addedAt`) **→ `400
invalid_request_body`**. Beide Codes stehen schon in `api-error.ts` und beiden Locales → kein Web-Commit in #376 (Regel 7 unberührt).
Alternative: ein neuer Code `slot_entries_invalid` mit Web-Teil.

**R5 — Flag-unabhängige Namensregeln sofort auf Prod?** D26 (`NamesAtCreation` aller Aliase nach `addedAt`, `NameAtCreation` = erstes
Element) und D29 (Tag-Alias-Snapshot eines nicht aktiven Sets = ältester Eintrag) lesen nur die 7TV-Live-Liste, keine Slots.
Empfehlung: **nicht am Flag.** Folge auf Prod mit dem nächsten Deploy: Eine neue Set-Session friert bei einem Emote mit zwei Aliasen den
**ältesten** Alias als `NameAtCreation` ein statt des zuerst gelisteten; ein Tag aus einem nicht aktiven Set speichert den ältesten Alias.
Bestehende Sessions/Tags bleiben unverändert. Gewollt nach D26/D29, aber für Nutzer sichtbar — deshalb hier zur Bestätigung.

**R6 — Sperre-dann-Lesen auch für den Bericht ohne `entries` (Flag an)?** Plan-372 2.9 kündigt an: „Kind 6 stellt auf
Sperre-dann-Lesen um." Empfehlung: **ja, beide Pfade bei Flag an** (eine Transaktionsform, Abschnitt 4.3); Flag aus bleibt Zeile für Zeile
wie heute. Damit schließt #376 auch die in Plan-372 2.9 benannte Grenze (ein zwischen Lesen und Sperre deaktivierter oder auf ein anderes
Set gewechselter Kanal wird noch geschrieben). Risiko: Umbau eines produktiven Pfads; abgefangen durch die bestehenden Z1/Z2/Z6-Tests,
die unverändert grün bleiben müssen.

**R7 — Live-Prüfung braucht eine Api mit Anmeldung.** Anders als #372/#373 prüft #376 HTTP-Endpunkte; die Berichte verlangen eine
echte Sitzung und die 7TV-Eigentümerprüfung. Empfehlung: Api aus dem Worktree per `dotnet run` auf **`:5151`** gegen die DB-Kopie (nur
dort stimmt die registrierte Twitch-Redirect-URI), für die Dauer der Prüfung abgestimmt mit den anderen Sitzungen (keine E2E-Läufe
parallel, Haupt-Checkout-Api nicht auf `:5151`); der Betreiber meldet sich einmal im Browser an und legt den Cookie in eine
Scratchpad-Datei (Modus 600), die der Agent nur als Header-Datei liest. Alternative: nur Infrastructure-Tests, keine HTTP-Live-Prüfung —
widerspräche Regel 16.

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 6)

1. **`/totals` je Slot** (D24, §5.1) samt Emote-Modus (R2), `siblingNames`, `firstSeenAtFromRest`, Namensvettern je Slot-Name; AK 28.
2. **`/series` je Slot** (D24, §5.3), Kodierung bytegleich; AK 31.
3. **`/daily` mit `slotId` oder `emoteId`** (D24, §5.2); AK 30.
4. **Ballot:** `NamesAtCreation` schreiben, `EmoteNames` lesen (D26); AK 33. **Tag-Alias-Snapshot** = ältester Eintrag (D29).
5. **Berichte mit `entries`** (D32, §5.7): Ursprungskanal-Validierung, Fan-out über `(SevenTvEmoteId, AddedAt)`, veraltete und wiederholte
   Berichte als erfolgreiche No-ops, Leave nur beim letzten aktiven Slot, Sperre-dann-Lesen (R6); AK 41–42, 46, 52, 54–55.
6. **Gemeinsame Bausteine:** die Seed-von-Set-Abfrage aus `SevenTvSyncService` herausgelöst (eine Quelle für Map, Settle und Leser),
   eine reine Ansichtsklasse für die Zeilenbildung (Abschnitt 2.3), die D13-Ableitung des Planers für den Bericht zugänglich.
7. Tests (Infrastructure, Api), Live-Prüfung, DECISIONS-Absatz „Child 6".

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 7 (#377) | Frontend-Modell und Seite, `slotKey`, `mergeSetView`, Karten, Drilldown je Slot, Vote-Detail mit `emoteNames`, Export |
| Kind 8 (#378) | Mutation mit `alias`, Löschlauf je Slot, Senden von `entries` samt `addedAt`, Restore-Queue mit `slotId` |
| Kind 5 (#375) | M3, Backfill-Mehrslot, 409 `backfill_slots_unsettled` |
| Kind 9 (#379) | Einschalten des Flags auf Prod, Abnahme, Rückbau der Aus-Zweige (auch der Lese- und Bericht-Aus-Zweige dieses Kinds) |
| unverändert | `GET /emotes` (D30), Tag-Routen, `/import-coverage`, Legacy-Guid-Berichte, `sync-imported`, `GetTotalsByEmoteIdsAsync` (Ballot-Summe je Emote, D3), Harness-Leser (D20), `GetUsageStatsAsync` (Rohliste) |

### 1.3 Spec ↔ Ist-Stand: gefundene Abweichungen

| # | Spec sagt | Code sagt (aaa71fce) | Umgang |
|---|---|---|---|
| A1 | §16: Kinder 1–8 mergen, „ohne dass Prod sie sieht" | M1 und M2 laufen auf Prod, Flag aus; jeder Merge geht mit dem nächsten Main-Deploy live | R1, R3, R5; Abschnitt 7 |
| A2 | §5.1: Seeds als eigene Zeilen, `FirstSeenAt = Slot.AddedAt` | Seeds haben `AddedAt null`, veraltbaren `Name`, zählen nicht (E7); auf Prod gibt es nur Seeds | R2 (Emote-Modus) |
| A3 | §5.7: `400 invalid_request` | Code existiert nicht | R4 |
| A4 | §2.2 Zeile „Berichte" beschreibt `Entries`, `SlotId`-Validierung und Fan-out als Ist-Stand | `SyncInSetRequest` hat kein `Entries`; `MarkInSetAsync` arbeitet nur je 7TV-Id auf Emote-Ebene | Zeile ist Zielbild, nicht Ist; Abschnitt 4 baut es |
| A5 | §5.5/D26: „Vote-Erstellung dedupliziert IDs" (neu) | `VoteSessionService` dedupliziert `emoteIds` und `sevenTvEmoteIds` schon; Test `CreateAsync_WithEmoteIds_PersistsDedupedBallotRows` | nur `NamesAtCreation` neu; AK-33-Dedup-Hälfte ist erfüllt, ein Set-Session-Fall kommt dazu |
| A6 | AK 32 gehört #376 | in #372 umgesetzt und getestet (Flag an, `EmoteSetStatusServiceSlotTests`) | nur der Haken in der PR-Tabelle; keine Codeänderung |
| A7 | §6.5/D32: `SELECT FOR UPDATE` | `RowLocks` sperrt `FOR NO KEY UPDATE` (Plan-372 E1, Betreiber 12.10.) | bleibt `NO KEY UPDATE` |
| A8 | §9.3: Bericht-Eintrag `{ sevenTvEmoteId, slotId, alias }` | — | §5.7/D32 (R5-Fassung) gilt: zusätzlich `addedAt`, Pflicht bei gesetzter `slotId` |
| A9 | §5.2: „keiner → 400 `emote_id_empty`" | `emoteId` ist heute ein Pflicht-Query-Parameter; fehlt er, antwortet das Framework (vermutlich `invalid_request_body` über den globalen Handler) | wird mit `slotId` optional; „keiner" → `emote_id_empty` wie in der Spec; Umsetzer prüft den heutigen Code des Fehlfalls und nennt die Änderung im PR |
| A10 | §5.1 nennt `SlotId` als `string` | virtuelle und Emote-Modus-Zeilen haben keinen Slot | `slotId` ist `string \| null` (R2) |
| A11 | D13-Ableitung als gemeinsame Regel | im Planer `EmoteSlotReconcilePlanner` privat | T5 macht sie intern nutzbar, Verhalten unverändert |
| A12 | §15: `UsageStatsDailyScopeEndpointTests` +4 | die Klasse prüft heute nur die Scope-Verdrahtung über ein NSubstitute-`IUsageStatQueryService` | neue Fälle in derselben Klasse, Muster unverändert |

### 1.4 Migration

**Keine.** `VoteSessionEmotes.NamesAtCreation` liegt seit M1 vor; `EmoteSlots` und `UsageStats.SlotId` (unique Slot-Index seit M2) tragen
alle neuen Leser; der Bericht schreibt nur Spalten, die es gibt. Kein Wartungsfenster für #376 (Abschnitt 7).

## 2. Lese-API — Vertrag für #377

### 2.1 Begriffe

- **Eigene Slots** eines Emotes: alle `EmoteSlots` des Emotes mit `SeedUnsettled = false`.
- **Seed von X:** ein unsettled Seed des Kanals mit mindestens einer `UsageStats`-Zeile mit `EmoteSetId = X` (ohne Datumsgrenze).
  **[E] eine Abfrage für alle:** die heute private `ActiveSetSeeds` in `SevenTvSyncService` wird als Abfrage „Seeds eines Sets je Kanal" in
  `Infrastructure/Persistence` herausgelöst (Muster `ActiveNameEntries`) und von Settle, Map-Ausschluss und Lesern geteilt — reine
  Verschiebung, Verhalten des Syncs unverändert.
- **D2-Ordnung:** `AddedAt` aufsteigend, `null` zuletzt, dann `Name`, dann `Id` in UTF-8-Byteordnung — `BaseSlotRule.D2Order` /
  `Utf8Order`, im Speicher, nie `ORDER BY "Name"` in SQL.
- **Modus je Emote im angefragten Set X** (Flag an; R2): **Emote-Modus**, wenn das Emote einen Seed von X hat, oder — nur aktive Ansicht —
  kein aktiver eigener Slot existiert; sonst **Slot-Modus**. **Flag aus: jedes Emote im Emote-Modus** (R1).

### 2.2 `GET /api/channels/{c}/usage-stats/totals?from&to[&emoteSetId]`

**Basismenge** wie heute: aktive Ansicht = nicht archivierte Emotes, mit 0 aufgefüllt; nicht aktive Ansicht = Emotes mit ≥ 1 Zeile unter X
(archivierte eingeschlossen).

**Zeilen je Emote:**

| Modus | Ansicht | Zeilen |
|---|---|---|
| Emote | beide | genau eine, Zahlen = Summe über alle Zeilen des Emotes in X (heutiges Ergebnis) |
| Slot | aktiv | eine je **aktivem** eigenem Slot (nicht archiviert), mit 0 aufgefüllt; archivierte eigene Slots erscheinen nicht, auch wenn sie Zeilen in X tragen (D1/D7: deren Historie kehrt mit der Wiederaufnahme auf dieselbe Karte zurück) |
| Slot | nicht aktiv | eine je eigenem Slot (archiviert oder nicht) mit ≥ 1 Zeile in X; ein Slot ohne Zeilen in X erscheint nicht |

Jedes Emote der Basismenge hat damit mindestens eine Zeile: in der aktiven Ansicht hat der Slot-Modus per Definition einen aktiven
eigenen Slot; in der nicht aktiven liegen die Zeilen von X entweder auf einem Seed von X (→ Emote-Modus) oder auf eigenen Slots. Zeilenlose
Enqueue-Seeds und die vom Settle geleerten Seeds (Plan-373 E11a) erzeugen nie eine Zeile (Übergabe #373).

**Wire-Felder** (camelCase, Reihenfolge frei; Bestand unverändert in Bedeutung, außer wo genannt):

| Feld | Emote-Modus | Slot-Modus |
|---|---|---|
| `emoteId` | `Emote.Id` | `Emote.Id` des Slots |
| `slotId` **neu** | `null` | `EmoteSlot.Id` |
| `emoteName` | `Emote.Name` | `Slot.Name` |
| `sevenTvEmoteId`, `imageUrl` | vom Emote | vom Emote |
| `totalUseCount`, `previousWindowUseCount`, `lastUsedDate` | Summe/Max über alle Zeilen des Emotes in X | über die Zeilen dieses Slots in X; Regeln wie heute (nur `UseCount`, `lastUsedDate` unbegrenzt in der Zeit, `UseCount = 0`-Zeilen zählen nicht als Nutzung) |
| `firstSeenAt` | `Emote.FirstSeenAt` | `Slot.AddedAt` (`null` = unbekannt, nie „neu") |
| `firstSeenAtFromRest` **neu** | `false` | `Slot.AddedAtFromRest` |
| `isArchived` | `Emote.IsArchived` | `Slot.IsArchived \|\| Emote.IsArchived`; in der aktiven Ansicht immer `false` |
| `nameTwinEmoteSetIds` | heutige Regel über `Emote.Name` | Regel 2.6 über den Slot-Namen |
| `siblingNames` **neu** | `[]` | die Namen der **anderen Zeilen desselben Emotes in dieser Antwort**, in D2-Ordnung ihrer Slots, ordinal distinct, ohne den eigenen Namen |

Sortierung der Antwort wie heute (absteigend nach `totalUseCount`); keine Zusage zur Reihenfolge bei Gleichstand.

**Invarianten** (Tests): `slotId ≠ null` ⇔ Slot-Modus; je `slotId` höchstens eine Zeile; je `emoteId` im Emote-Modus genau eine Zeile; die
Summe der `totalUseCount` aller Zeilen eines Emotes ist im Emote-Modus gleich der heutigen Zahl und im Slot-Modus gleich der Summe seiner
angezeigten Slots.

**Aggregation (Regel 10):** eine Abfrage über `UsageStats` gefiltert auf die Slot-Id-Liste des Kanals und `EmoteSetId = X`, gruppiert nach
`SlotId` (Index-only-Scan über den unique Slot-Index `(SlotId, EmoteSetId, Date) INCLUDE (UseCount)`); Emote-Modus-Zeilen falten die
Slot-Aggregate im Speicher. Ein Codepfad für beide Flag-Zustände; das Flag entscheidet nur den Modus.

### 2.3 Zeilenbildung als reine Klasse [E]

Modusentscheidung, Zeilenmenge, Anzeigename/-datum, `isArchived` und `siblingNames` rechnet eine interne, statische, BCL-reine Klasse in
`Infrastructure/Services` (Präzedenz `EmoteSlotReconcilePlanner`, `ActiveNameEntries`) aus einer Momentaufnahme: Emotes, ihre Slots, die
Seed-von-X-Menge, Flag, aktive/nicht aktive Ansicht, die Menge der Slots mit Zeilen in X. Ausgabe: Ansichtszeilen mit der Liste der
Slot-Ids, deren Aggregate sie trägt. **`/totals` und `/series` benutzen dieselbe Klasse**, damit Karte und Kurve nie auseinanderlaufen.
Unit-Tests in `Infrastructure.Tests/Unit` (Regel 11).

### 2.4 `GET …/usage-stats/series?from&to[&emoteSetId]`

- Dieselben Ansichtszeilen wie `/totals` (2.3); **eine Entry je Ansichtszeile mit mindestens einem Tag eigener Nutzung im Bereich** (wie
  heute: sonst entfällt sie).
- Entry: `sevenTvEmoteId`, `emoteId`, **`slotId`** (neu, `null` im Emote-Modus), **`name`** (neu, = `emoteName` der Ansichtszeile),
  `days`. **JSON-Reihenfolge** `sevenTvEmoteId, emoteId, slotId, name, days` (Positionsreihenfolge des Records, Spec 5.3).
- `days`-Kodierung **bytegleich**: `[offset, useCount]`-Paare, aufsteigend; im Slot-Modus die Zeilen genau dieses Slots (je Tag eindeutig
  durch den Slot-Index), im Emote-Modus die Summe je Tag über die Slots des Emotes (heutiges Zwischenverhalten aus #373).
- Reihenfolge der Entries [E]: nach `emoteId`, innerhalb eines Emotes in D2-Ordnung der Slots.
- `liveDays`, `firstCountedDay`, `lastCountedDay`, `channelFirstCountedDay` unverändert (emote-/kanalweit, nicht je Slot).
- `ChannelUsageSeriesWireFormatTests` pinnt das JSON exakt — wird um `slotId`/`name` erweitert, die Paar-Kodierung bleibt Prüfgegenstand.

### 2.5 `GET …/usage-stats/daily?(slotId|emoteId)&from&to[&emoteSetId|setScope=all]`

- **Parameter:** `slotId` und `emoteId` beide optional. `slotId` gesetzt → Slot-Serie (gilt auch, wenn `emoteId` zusätzlich gesetzt ist);
  nur `emoteId` → Emote-Serie (heutiges Verhalten, Summe über die Slots); keiner (oder nur Leerraum) → **400 `emote_id_empty`**. Danach die
  bestehende Leiter (`invalid_date_format` → `from_after_to` → `range_too_large`, Scope-Parser → `invalid_emote_set_id`).
- **Slot-Serie:** Slot über `EmoteSlots` → `Emote` → `Channel` gegen den Kanal der Route aufgelöst; unbekannt oder fremder Kanal → **404**
  (ohne Code, wie heute). Archivierte Slots und Seeds sind erlaubt (Ballot-/Historienfall). Scope wie heute (aktives Set, ein Set, alle
  Sets); `days`, `firstUsedDate`/`lastUsedDate` über die Zeilen dieses Slots im Scope; `liveDays` mit der heutigen Set-Regel. Slot ohne
  Nutzung im Scope → leere Serie, `200`.
- **DTO** `EmoteUsageSeriesDto` + **`slotId`** (`null` auf dem Emote-Pfad). `emoteName`: Slot-Pfad = `Slot.Name`, bei einem unsettled Seed
  `Emote.Name` (R2-Regel); Emote-Pfad = `Emote.Name`.
- **Flag-unabhängig [E]:** die Slot-Serie ist eine reine Nachschlagung; bei Flag aus fragt das Frontend sie nie an (`slotId` immer `null`).
- Core: eine zweite Methode für die Slot-Serie neben `GetDailySeriesAsync` (Signatur in Worten: Kanalname, Slot-Id, Bereich, Scope →
  Serie oder `null`); die Emote-Methode bleibt unverändert.

### 2.6 Namensvettern je Slot (`nameTwinEmoteSetIds`) [E]

Slot-Modus: Name der Zeile = Slot-Name. Ein Namensvetter ist eine **andere** `SevenTvEmoteId` desselben Kanals, deren `Emote.Name` oder
einer ihrer eigenen Slot-Namen (ordinal) gleich ist; gemeldet werden die Sets, unter denen der Vetter Zeilen trägt, ohne X, aufsteigend
ordinal, wie heute. Emote-Modus und Flag aus: heutige Regel über `Emote.Name`. Zwei Slots desselben Emotes sind nie Vettern.

### 2.7 Votes (D26) und Tags (D29)

- **Schreiben (`VoteSessionService`, Set-Session):** je 7TV-Id alle Einträge der Live-Liste in D2-Ordnung (`ForeignEmoteRow.AddedAt`,
  `null` zuletzt, dann Name in UTF-8-Byteordnung); `NamesAtCreation` = diese Namen (ordinal distinct), `NameAtCreation` =
  `NamesAtCreation[0]`, `ImageUrlAtCreation` vom ersten Eintrag. Ersetzt das „first wins" (K5). Null-Session-Ballots: `NamesAtCreation`
  `null` wie bisher. Dedup der Request-Ids besteht (A5). Flag-unabhängig (R5).
- **Lesen (`VoteSessionQueryService`):** `VoteSessionResultDto` + **`emoteNames`** (Liste, nie leer, `emoteNames[0] == emoteName`).
  Set-Session-Zeile: `NamesAtCreation`, sonst `[NameAtCreation ?? Emote.Name]`. Null-Session-Zeile (dynamisch oder Guid-Ballot), Flag an:
  `Emote.Name` zuerst, danach die Namen der aktiven eigenen Slots des Emotes in D2-Ordnung, ordinal distinct; ohne aktive eigene Slots
  `[Emote.Name]`. Flag aus: `[Emote.Name]` (R1). `emoteName`, Sortierung, Zahlen, `eligible` unverändert. Eine Abfrage für alle Slots der
  Kandidaten, keine je Zeile.
- **Tags (D29):** `EmoteTagService.ReadForeignSnapshotAsync` nimmt je 7TV-Id den ältesten Eintrag (D2-Ordnung über `AddedAt`) statt des
  ersten; die aktive Ansicht liest weiter `Emote.Name` (D13 liefert bei Flag an den ältesten Slot, bei Flag aus heutiges Verhalten).
  `CurrentName` = `Emote.Name` (besteht). Flag-unabhängig (R5).

## 3. Kompatibilität

| Konsument | Flag aus (Prod bis #379) | Flag an (lokal) |
|---|---|---|
| heutiges Frontend, `/totals`, `/series`, `/daily`, Ballot | unverändert bis auf additive Felder (`slotId: null`, `firstSeenAtFromRest: false`, `siblingNames: []`, `name`, `emoteNames`) | zwei Zeilen je Mehr-Slot-Emote mit gleicher `sevenTvEmoteId` (R1, Zwischenstand bis #377) |
| `/daily?emoteId=` | unverändert | unverändert (Summe) |
| `/daily` ohne beide Ids | `emote_id_empty` statt Framework-400 (A9) | dito |
| `sync-deleted`/`sync-restored` ohne `entries` | unverändert | Sperre-dann-Lesen (R6), Ergebnis wie heute |
| mit `entries` | Formprüfung, dann wie ohne (R3 a) | Abschnitt 4 |
| neue Set-Sessions, Tags aus nicht aktiven Sets | ältester Alias (R5) | dito |

## 4. Berichte mit `entries` (D32, §5.7)

### 4.1 Wire-Vertrag

- Request `SyncInSetRequest` + **`entries`** (optional, Liste von `{ sevenTvEmoteId, alias, slotId?, addedAt? }`). `addedAt` ist ISO-8601
  mit Zeitzone; der Client sendet **wörtlich** das `firstSeenAt` der `/totals`-Zeile, aus der die `slotId` stammt (das autoritative Datum
  der gemeldeten Generation). Server vergleicht in UTC mit Mikrosekunden-Genauigkeit (Postgres-Auflösung; 7TV liefert ms).
- `entries` fehlt oder ist leer → heutiger Pfad.
- **Formprüfung im Endpoint (Stufe 3, vor der Eigentümerprüfung, wie `emote_ids_empty`):** jeder Eintrag hat eine nicht leere
  `sevenTvEmoteId`, die in `sevenTvEmoteIds` steht; gesetzte `slotId` verlangt `addedAt`. Verstoß → 400 (Code nach R4). `alias` dient nur
  dem Gleichstand im Fan-out (4.2) und darf fehlen.
- Core: Eintrags-DTO in `Core/Services` (BCL-rein, `AddedAt` als UTC-`DateTime?`); `IEmoteService.MarkDeletedInSetAsync`/
  `MarkRestoredInSetAsync` bekommen die Einträge als zusätzlichen optionalen Parameter. Ergebnis-DTOs bekommen ein Statusfeld „abgelehnt:
  fremde Slot-Id" (der Endpoint antwortet dann 400 nach R4, ohne Live-Event und ohne Resync; die Transaktion hat nichts geschrieben,
  kein Audit). Handler bleibt ohne `AppDbContext`/`IConnectionMultiplexer` (Regel 4).
- **Antwort unverändert in der Form:** je Trefferkanal `archivedCount`/`restoredCount` = Zahl der gemeldeten Ids mit `Emote`-Zeile in
  diesem Kanal (wie heute, unabhängig von `entries`), `notFoundIds` wie heute. Intern `NewlyChangedCount` = Ids, deren Emote **oder** ein
  Slot in diesem Kanal tatsächlich geändert wurde (steuert `channel.synced`). Resync (Stufe 7) wie heute.
- **Audit [E]:** Kanal-Einträge bekommen bei `entries` zusätzlich `entryCount` (in diesem Kanal aufgelöste Einträge mit `slotId`);
  Papier-Einträge `entryCount` = Zahl der Einträge. Ohne `entries` byte-gleich zu heute.

### 4.2 Semantik je Kanal und Id (Flag an)

**Ursprung [E]:** der erwartete Kanal (`expectedChannelName`, normalisiert), **wenn** er nach der Sperre ein Treffer ist; sonst gibt es
keinen Ursprung, und alle Treffer lösen per Fan-out auf (die `slotId`s bleiben dann ungeprüft und ungenutzt).

Je Trefferkanal H und Id X mit Einträgen (R3 b), mit `e` = Emote-Zeile von X in H (keine → X in `notFoundIds` von H, kein 400):

1. **Auflösung je Eintrag mit `slotId`:**
   - *H = Ursprung:* Slot `s` = eigener Slot von `e` mit `Id = slotId`. Fehlt er (Slot eines anderen Emotes, eines anderen Kanals oder
     unbekannt) → **ganzer Bericht abgelehnt** (400, R4), geprüft für alle Ursprungseinträge **vor** jeder Mutation. Ist `s.AddedAt ≠
     addedAt` (auch: Seed mit `null`, provisorisch neu datiert) → **veraltet, No-op** (2xx).
   - *H ≠ Ursprung oder kein Ursprung:* Kandidaten = eigene **autoritative** Slots von `e` mit `AddedAt = addedAt`; mehrere →
     zusätzlich `Name = alias` (D4-Gleichstand); genau einer → `s`; sonst nichts in H (REST gleicht ab). Eine fremde `slotId` ist hier nie
     ein Fehler.
2. **Delete:** `s` aktiv → archivieren (`IsArchived`, `ArchivedAt = now`, `LastSyncedAt = now`); `s` schon archiviert → No-op.
3. **Restore:** `s` archiviert → reaktivieren (`IsArchived = false`, `ArchivedAt = null`, `AddedAt = UtcNow`, `AddedAtFromRest = false`,
   `LastSyncedAt = now`); `UsageStats` bleiben am Slot. `s` schon aktiv → No-op (kein Datums-Reset). Der Resync aus Stufe 7 autorisiert das
   neue Datum auf derselben `SlotId` (Plan-372 2.3 Schritt 5).
4. **Emote-Folge:** Delete — hatte `e` vor dem Bericht ≥ 1 aktiven eigenen Slot und danach keinen → `e` archivieren (`ArchivedAt`,
   `LastSyncedAt`, `IsPlaceholder = false`) **und** Leave für X in H; sonst keine Leave, `e` bleibt aktiv. Restore — wurde ein Slot
   reaktiviert und ist `e` archiviert → un-archivieren (`LastEnteredSetAtUtc = now`, `IsPlaceholder = false`, `ArchivedAt = null`). Danach
   **D13** (`Emote.Name`/`FirstSeenAt` aus dem ältesten aktiven eigenen Slot) über dieselbe Ableitung wie der Abgleich (A11); ohne aktiven
   eigenen Slot bleiben beide unverändert.
5. Einträge ohne `slotId`: übersprungen (R3 b). Ids ohne Eintrag: heutige Semantik (Emote-Ebene, Leave für jede gefundene Zeile).

**Grenzfälle, die die Tests festhalten:**

- Wiederholter Delete (Retry, verlorene Antwort) → No-op, **keine** zweite Leave (AK 41).
- Verspäteter Delete nach D7-Wiederaufnahme (`s` aktiv bei t3, Bericht t1) → No-op ohne Leave (AK 52).
- Restore-Retry nach gespeichertem Restore → No-op; REST t9 danach → `s` autoritativ bei t9 (AK 55 a). REST mit D7 vor dem Bericht → `s`
  aktiv bei t9, Bericht t1 → No-op, `AddedAt` bleibt t9 (AK 55 b).
- **REST vor dem Delete-Bericht** (7TV-Löschung, dann Tick, dann Bericht): `s` schon archiviert → No-op, keine Leave aus dem Bericht; die
  Leave stammt dann aus den b1/b2-Regeln des Syncs. Folge der Spec („nur wenn diese Mutation den letzten Slot entfernt"), heute schreibt der
  Bericht in diesem Fall eine Leave — zur Kenntnis im PR und in DECISIONS.
- Aktive eigene Slots unter archiviertem Emote (nach einem Delta-Pull vor dem Tick): zählen für Schritt 4 nicht als „aktiv" (Invariante 4.1:
  Leser ignorieren sie) [E].
- Seeds und virtuelle Slots sind nie Ziel eines Eintrags (Seed: `AddedAt null` ≠ `addedAt`; virtuell: keine `slotId`).

### 4.3 Transaktion und Sperren (Flag an, beide Pfade nach R6)

Ein Versuch, in dieser Reihenfolge; die bestehende Wiederholung (`MarkInSetAsync`: eine Wiederholung bei 40P01 oder verschwundenem
Trefferkanal) bleibt unverändert darum herum:

1. Treffer-Kandidaten lesen wie heute (ungesperrt; Sperrliste, Broadcaster-Sperre, Sortierung ordinal nach Name).
2. Transaktion; **Kanalzeilen aller Kandidaten** sperren (`RowLocks`, ein Statement, sortiert nach `Id`). Fehlt einer → bestehende
   Ganz-Wiederholung (`HitChannelVanished`, Z6-Verhalten aus Plan-372 bleibt bitgleich, auch die Warnzeile).
3. **Unter der Sperre neu lesen** [E]: `IsBotActive` und `ActiveEmoteSetId` der gesperrten Kanäle; wer nicht mehr passt, ist kein Treffer
   (keine Wiederholung). Danach erst die Unresolved-Bestimmung des erwarteten Kanals.
4. Emote-Ids der Treffer für die gemeldeten 7TV-Ids lesen, **Emote-Zeilen sperren** (sortiert, ein Statement), dann die Emotes mit
   `Include(Slots)` lesen — Datenbankstand, der ChangeTracker ist zu Versuchsbeginn leer.
5. Anwenden (Legacy-Ids und Slot-Einträge), Leave-Upserts (ordinal sortiert, wie heute), Audit, `SaveChanges`, Commit.

Begründung und Zyklen: Kanal → Emote → Leave ist die Reihenfolge von Sync (E23), Delta (Z8), Purge (Z6), Enqueue und Flush Phase 2.
`NO KEY UPDATE` auf `EmoteSlots` (implizit beim Update) verträgt sich mit dem FK-`KEY SHARE` des Flush (Phase 1) und des Backfill-Blocks.
Eine Deckungsprüfung wie Plan-372 E24 ist **nicht** nötig [E]: unter der Kanalsperre legt nur ein Schreiber ohne Kanalsperre noch
Emote-Zeilen an (Set-Session-Ballot über `ArchivedEmoteRowUpsert`); solche Zeilen sind archiviert und slotlos, also für Delete und
Restore ohne Wirkung — der Umsetzer prüft das am Code und bricht ab, wenn es einen weiteren Anleger ohne Kanalsperre gibt.
**Flag aus:** Ablauf Zeile für Zeile wie heute (Lesen vor der Transaktion, Leave → Emote), `entries` ignoriert.

## 5. Tests

Integrationstests `[Collection("Postgres")]`, Flag je Test über den Options-Typ; Slots und Zeilen über den Test-Helfer aus Plan-373 E14
(ausdrückliche `SlotId`). Bestehende Testklassen laufen mit Flag aus und sind damit der Nachweis „Aus = heute". **[E]** Neue Leser von
`SlotReconcileOptions` (`UsageStatQueryService`, `VoteSessionQueryService`) kommen in `SlotReconcileRegistrationTests`.

### 5.1 Unit

- **Ansichtsklasse (2.3), neu, ~14 Fälle:** Modus je Lage (Seed von X → Emote; ohne aktiven eigenen Slot aktiv → Emote; nicht aktiv ohne
  eigenen Slot mit Zeilen → keine Zeile; Flag aus → Emote), Seed eines **anderen** Sets löst keinen Emote-Modus aus, zeilenloser Seed
  ebenso, geleerter archivierter Slot ohne Zeilen erscheint nicht, archivierter eigener Slot nur in der nicht aktiven Ansicht,
  `siblingNames` (D2-Ordnung, distinct, ohne eigenen Namen, leer im Emote-Modus), `isArchived`-Regel, Namens-Gleichstand in UTF-8-Ordnung.

### 5.2 `UsageStatQueryServiceTests` (~20 angepasst, +8)

AK 28: aktive Ansicht, `X` mit zwei gesettelten Slots → zwei Zeilen, verschiedene `slotId`, `siblingNames` je `[anderer]`,
`firstSeenAt`/`firstSeenAtFromRest` je Slot · AK 28 nicht aktiv: nur Slots mit Zeilen unter X, archivierte eingeschlossen · AK 28 D7: ein
reaktivierter Slot erscheint aktiv mit seiner vorhandenen Nutzung · R2: Seed des aktiven Sets → eine Zeile, Emote-Zahlen, `slotId null`,
Name/Datum vom Emote · R2: aktives Emote ohne eigenen Slot → eine Zeile · R1: dieselbe DB mit Flag aus → heutiges Ergebnis byte-gleich bis
auf die neuen Felder · AK 31: `/series` je Slot mit `slotId`/`name`, Emote-Modus summiert je Tag, Entry-Reihenfolge · 2.6 Namensvettern über
einen Zweitnamen. Bestehende Fälle bleiben mit Flag aus inhaltlich unverändert (nur DTO-Konstruktion angepasst).

### 5.3 `/daily` (AK 30)

Infrastructure: Slot-Serie nur der Slot; Emote-Serie = Summe; fremder Slot (anderer Kanal) → `null`; Seed-Slot → Name vom Emote; leere
Serie bei Slot ohne Nutzung. **Api.Tests `UsageStatsDailyScopeEndpointTests` +4:** nur `slotId` → Slot-Methode mit Scope; nur `emoteId` →
Emote-Methode; beide → Slot-Methode; keiner → 400 `emote_id_empty`, kein Service-Aufruf. Filter-Matrix (`AuthFilterMatrixTests`) unverändert
grün.

### 5.4 Votes und Tags (AK 33, D29)

`VoteSessionServiceTests` +1: Set-Session, `X` zweimal in der Live-Liste (`ome44` 2026-07-01, `hob44` 2026-09-01, Liste in umgekehrter
Reihenfolge) und doppelte `sevenTvEmoteIds` im Request → eine Zeile, `NamesAtCreation = [ome44, hob44]`, `NameAtCreation = ome44`;
bestehender „first wins"-Fall angepasst (Erwartung jetzt D2). `VoteSessionQueryServiceTests` +2: Set-Session `emoteNames` aus
`NamesAtCreation` (Altzeile mit `NULL` → `[NameAtCreation]`); Null-Session mit Flag an → Live-Namen der Slots, Flag aus → `[Emote.Name]`.
`EmoteTagServiceTests` +1 (D29): Fremd-Set mit zwei Einträgen einer Id → Alias des ältesten.

### 5.5 Berichte (`EmoteServiceTests` +12, `EmoteServiceInSetConcurrencyTests` +3, `EmoteTagServiceTests` +1, Api.Tests +5)

`EmoteServiceTests` (Flag an, außer wo genannt): AK 41 Teil-Löschung (nur `hob44` archiviert, Emote aktiv, keine Leave) · derselbe Bericht
erneut → No-op · danach `ome44` → Emote archiviert, Leave genau einmal · fremde `slotId` im Ursprung → abgelehnt, nichts geschrieben, kein
Audit · AK 52 verspäteter Delete nach D7 · AK 42 Restore der passenden archivierten Generation, `AddedAt` provisorisch, Nutzung bleibt ·
AK 46 Undo: Delete → Restore → `SyncChannelAsync` mit REST `[A(t9)]` → dieselbe `SlotId` autoritativ bei t9, `/totals` zeigt eine Zeile mit
alter und neuer Nutzung · AK 54 Shared Set mit lokalen `SA`/`SB`: Delete und Restore wirken je lokal, kein 400 wegen `SB`; Geschwister ohne
eindeutigen Treffer → nichts · AK 55 a/b · Gleichstand im Fan-out über `alias` · R3 b Mischform (Id mit Eintrag ohne `slotId` übersprungen,
Id ohne Eintrag heutige Semantik) · Flag aus mit `entries` → Ergebnis wie ohne. Bestehende Fälle grün (Aus-Nachweis).
`EmoteServiceInSetConcurrencyTests`: Entries-Delete gegen Flag-an-Sync auf dasselbe Emote, **beide Startreihenfolgen**
(`PostgresLockProbe`), kein 40P01, Endzustand wie seriell · Entries-Bericht gegen `PurgeAsync` (Z6-Muster, beide Reihenfolgen) ·
Kanal wechselt das aktive Set zwischen Kandidaten-Lesen und Sperre → kein Treffer, nichts geschrieben (R6). Bestehende Z1/Z2/Z6-Fälle
unverändert grün.
`EmoteTagServiceTests` +1: Teil-Löschung lässt `Holds` der Platzierung stehen.
**Api.Tests `SevenTvEmoteSetSyncBookkeepingEndpointTests` +5:** `slotId` ohne `addedAt` → 400 vor der Eigentümerprüfung; Eintrags-Id nicht in
`sevenTvEmoteIds` → 400; Einträge werden an den Service weitergereicht (Form, UTC); Ablehnungsstatus des Service → 400 (R4) ohne
Live-Event und ohne Resync; stale No-op → 200 mit unveränderter Antwortform.

### 5.6 Nicht nötig

Worker.Tests (kein Worker-Code außer der verschobenen Seed-Abfrage, deren Sync-Tests das abdecken), Vitest/E2E (kein `web/` in #376,
R4), Migrationstests (keine Migration).

## 6. Live-Verifikation (Regel 16) — Freigabe des Betreibers je Anlass

Randbedingungen wie Plan-373 §7: geteilte Dev-DB, Api-/Worker-Container und Redis gehören anderen Sitzungen; **nie `docker compose`, nie
etwas im Haupt-Checkout.** Testkanal `olaf_olaf_son`, 7TV-Schreibzugriffe mit dem Token aus `~/.config/emotepurge-7tv-test-token` nur als
Header-Datei (nie ausgeben, nie `set -x`). `handofblood` nur lesend.

| Ressource | Wert |
|---|---|
| DB | `emotepurge_slots376` im Container `emotepurge-dev-postgres`, per `pg_dump \| psql` im Container |
| Redis | Wegwerf-Container `redis-slots376` auf `127.0.0.1:6392` |
| Prozesse | ein Worker und eine Api per `dotnet run` aus dem Worktree (PIDs notieren); Api auf `:5151` nach R7 |
| Baseline | Build von `aaa71fce` aus `git archive` in einem Scratchpad-Ordner (kein `git worktree add`, keine Änderung an Repo-Metadaten) |
| Dateien | Scratchpad: Cookie-Datei und Token-Header-Datei (Modus 600), Antwort-JSONs, Protokoll |

0. `df -h /`, Größe der Dev-DB; Abbruch, wenn der Platz nicht das Doppelte trägt. Ausschlussliste für Chatter prüfen (wie Plan-373 7.0).
1. **Kopie**, Last begrenzen (`IsBotActive = false` außer `olaf_olaf_son` und `handofblood`), Env-Overrides wie Plan-373 (Kopie, Redis
   `:6392`, `ChatLogBackfill__Enabled=false`, `Retention__Enforce=false`, `SevenTv__EventApi__Enabled=false`). Testkanal vorbereiten:
   Ausgangszustand des Sets sichern; `01KAMA120V9DC2P1YW9QWFHA16` erst als `ome44`, dann als `hob44` hinzufügen.
2. **Phase A, Flag aus — Prod-Nachweis.** Baseline-Api und neue Api nacheinander auf denselben Stand der Kopie (Worker gestoppt,
   damit sich nichts bewegt): `/totals`, `/series`, `/daily?emoteId=` für `handofblood` (aktives Set, ein nicht aktives Set, 30 und 365
   Tage) und den Testkanal, Vote-Ergebnis einer bestehenden Session. **Diff nach Entfernen der neuen Felder leer**; die neuen Felder haben
   die Aus-Werte. Latenz `/totals` und `/series` für `handofblood` je dreimal, Baseline gegen neu (Abbruch und Meldung bei > 1,5-fach).
3. **Phase B, Flag an** (Worker und Api mit `SevenTv__SlotReconcile__Enabled=true`, Worker mindestens ein Tick, Settle laut Plan-372):
   AK 28 (Testkanal: zwei Zeilen `ome44`/`hob44`, `siblingNames`, Daten je Slot), AK 31 (`/series` je Slot), AK 30 (`/daily?slotId=` je
   Slot, `?emoteId=` Summe, Slot-Id aus `handofblood` gegen den Testkanal → 404), AK 32 (Status-Endpunkt, Abgleich mit #372), AK 33
   (Set-Session über die Api anlegen → eine Zeile, `emoteNames = [ome44, hob44]`; Null-Session-Ballot zeigt Live-Namen). Latenz wie in A.
4. **Berichte live** (Flag an): `hob44` per 7TV-Schreib-API mit `alias` entfernen, dann `sync-deleted` mit `entries` (Slot-Id und
   `firstSeenAt` aus `/totals`) → nur dieser Slot archiviert, Emote aktiv, keine neue Leave-Zeile (AK 41); derselbe Bericht erneut → 200,
   nichts geändert; mit einer Slot-Id von `handofblood` → 400. `hob44` per 7TV wieder hinzufügen, `sync-restored` mit `entries` (dieselbe
   Slot-Id, ursprüngliches Datum) → derselbe Slot aktiv, provisorisch; nach dem Resync-Tick autoritativ mit neuem 7TV-Datum, Historie am
   selben Slot (AK 42, 46); Restore erneut → 200, `AddedAt` unverändert (AK 55 a). Danach Set wiederherstellen.
5. **Abbau:** Worker und Api per SIGTERM (PIDs vorher mit `ps -o args=` prüfen), `docker rm -f redis-slots376`, `DROP DATABASE
   emotepurge_slots376 WITH (FORCE)`, Cookie- und Header-Datei löschen, Baseline-Ordner löschen. Protokoll mit SHA, Zahlen, Latenzen;
   keine Hostnamen, Passwörter, Tokens, Cookies, keine Kanalnamen außer `olaf_olaf_son`/`handofblood`.

AK 52, 54, 55 b und die Konkurrenzfälle sind live nicht sinnvoll herstellbar → Test.

## 7. Prod-Übergabe

- **Keine Migration, kein Fenster, kein Flag-Wechsel.** #376 geht mit dem nächsten regulären Main-Deploy (api und worker gemeinsam,
  Images desselben Commits).
- **Vorher:** `dotnet ef migrations list` gegen Prod (Tunnel wie in CLAUDE.md, Passwort als Platzhalter) muss **ohne** `(Pending)` sein —
  sonst bringt `main` die Migration eines anderen Kinds mit (Lehre aus #391), und das ist dann dessen Fenster.
- **Was Nutzer auf Prod merken** (im PR-Text): nichts auf der Nutzungsseite; neue Set-Sessions und Tags aus nicht aktiven Sets nehmen den
  ältesten Alias (R5); `/daily` ohne Id antwortet `emote_id_empty`.
- **Nachkontrolle ≤ 5 min:** Nutzungsseite eines großen Kanals lädt wie vorher (Zahlen stichprobenartig gegen den Stand vor dem Deploy),
  Api-Log ohne neue Fehler, `sync-deleted` aus einem Löschlauf antwortet 200.
- **Rollback:** Images des vorigen Commits; keine Daten- oder Schemafolge (keine Slot-Schreibung bei Flag aus, `NamesAtCreation` wird von
  den alten Images ignoriert).

## 8. Commits und DECISIONS

Conventional Commits, englisch, Tests im Commit ihres Features: (0) dieser Plan; (1) `refactor(infrastructure): share the seeds-of-set
query between sync and readers`; (2) `feat(infrastructure): build usage view rows per slot` (Ansichtsklasse); (3) `feat: report usage
totals and channel series per slot` **plus DECISIONS-Teil** (R1, R2, Wire-Vertrag); (4) `feat: serve the daily series per slot or per emote`
**plus DECISIONS-Teil** (A9); (5) `feat(infrastructure): freeze all aliases on set-session ballots and take the oldest alias for tag
snapshots` **plus DECISIONS-Teil** (R5); (6) `feat: archive and restore single slots from set-centric reports` (Service, Sperrfolge,
Tests) **plus DECISIONS-Teil** (R3, R6, Leave-Regel, REST-vor-Bericht); (7) `feat(api): accept slot entries on sync-deleted and
sync-restored` **plus DECISIONS-Teil** (R4); danach `fix:`/`test:` aus Review.

**DECISIONS:** datierter Absatz „Child 6 (read API and reports), <Datum>" im Entwurfseintrag #365 hinter „Child 3 …" (bzw. hinter dem
zuletzt gemergten Kind-Absatz), englisch, wächst mit den Commits (Regel 3). Inhalt: R1–R7 mit der jeweiligen Betreiberantwort; die
Abweichungen A2, A3, A7, A8, A9, A10; die Zeilenregel 2.2 samt Invariante `slotId ⇔ Slot-Modus`; Ursprung/Fan-out-Regel; die
Leave-Folge „REST vor Bericht". `Betrifft:` ergänzt um `UsageStatQueryService.cs`, `IUsageStatQueryService.cs`, `UsageStatsEndpoints.cs`,
`VoteSessionService.cs`, `VoteSessionQueryService.cs`, `IVoteSessionQueryService.cs`, `EmoteTagService.cs`, `EmoteService.cs`,
`IEmoteService.cs`, `SevenTvEndpoints.cs`, die neue Ansichtsklasse und die herausgelöste Seed-Abfrage.

## 9. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander — Builds teilen sich `obj/`)

Vor T1: Antworten auf R1–R7 eingearbeitet (Plan-Revision). Jede Aufgabe endet mit grüner ganzer Backend-Suite (`dotnet test
EmotePurge.slnx`) und `dotnet format`; Abbruch und Meldung an den Orchestrator, wenn eine bestehende Testklasse mit Flag aus ihr Ergebnis
ändern müsste (das hieße: Prod-Verhalten ändert sich).

| # | Aufgabe | Modell | Aufwand | Input | Fertig, wenn |
|---|---|---|---|---|---|
| T1 | Seed-von-Set-Abfrage herauslösen (Sync nutzt sie, Verhalten gleich); D13-Ableitung des Planers intern zugänglich machen (A11, ohne Verhaltensänderung) | sonnet | 0,25 T | 2.1, A11; `SevenTvSyncService.ActiveSetSeeds`, `ActiveNameEntries`, `EmoteSlotReconcilePlanner` | Sync-, Map- und Planer-Tests unverändert grün; Commit (1) |
| T2 | Ansichtsklasse (2.3) mit Unit-Tests | **opus** | 0,5 T | 2.1–2.3, R1, R2, 5.1; `BaseSlotRule.D2Order`/`Utf8Order` | 5.1 grün; jede Regel trägt im Code den Verweis (Spec/R/E); Commit (2) |
| T3 | `/totals` und `/series` je Slot: Core-DTOs, Service (Flag-Injektion, Aggregation 2.2, Namensvettern 2.6), Registrierungstest, Wire-Format-Test | **opus** | 1 T | 2.2, 2.4, 2.6, 3, 5.2; Ergebnis T1/T2; `ChannelUsageSeriesWireFormatTests`, `SlotReconcileRegistrationTests` | 5.2 grün, bestehende Fälle mit Flag aus inhaltlich unverändert; Commit (3) mit DECISIONS-Teil |
| T4 | `/daily` je Slot oder Emote: Core-Methode, Service, Endpoint-Parameter, Api.Tests | sonnet | 0,5 T | 2.5, A9, A12, 5.3 | 5.3 grün, `AuthFilterMatrixTests` grün; Commit (4) mit DECISIONS-Teil |
| T5 | Ballot (D26) und Tag-Snapshot (D29) | sonnet | 0,5 T | 2.7, R5, 5.4; `VoteSessionService`, `VoteSessionQueryService`, `EmoteTagService.ReadForeignSnapshotAsync` | 5.4 grün; Commit (5) mit DECISIONS-Teil |
| T6 | **Berichte mit `entries` im Service**: Core-DTOs und Signaturen, Umbau `MarkInSetOnceAsync` auf 4.3 (Flag an, beide Pfade), Semantik 4.2, Ablehnungsstatus | **opus** | 1,5 T | Abschnitt 4, R3, R6, 5.5 (ohne Api.Tests); Plan-372 2.2a/2.9; Übergabe #372; `EmoteServiceInSetConcurrencyTests`, `PostgresLockProbe`; Ergebnis T1 | `EmoteServiceTests`, Konkurrenz- und Tag-Fälle aus 5.5 grün, bestehende Z1/Z2/Z6-Fälle unverändert grün; Commit (6) mit DECISIONS-Teil |
| T7 | Endpoint: `entries` binden, Formprüfung Stufe 3, Ablehnung → 400, Api.Tests | sonnet | 0,5 T | 4.1, R4, 5.5 (Api.Tests); Ergebnis T6 | Api.Tests aus 5.5 grün, bestehende Bookkeeping-Tests unverändert grün; Commit (7) mit DECISIONS-Teil |
| T8 | Live-Verifikation | sonnet | 0,75 T + Betreiberzeit | Abschnitt 6, R7 | Protokoll 6.0–6.5, Phase-A-Diff leer, Latenzen notiert, Set wiederhergestellt, Abbau erledigt |
| T9 | Review, Gates, PR | **opus** (Review); Codex Sol | 0,5 T | Branch, Abschnitte 1.3, 3, 7, 8, 10 | `dotnet test EmotePurge.slnx` und `npm --prefix web test -- --watch=false` grün (Web unverändert, Lauf als Nachweis), kein E2E nötig (kein `web/`); `dotnet format`; erst committen, dann `node scripts/coverage-local.mjs --backend-only` ≥ 80 % (Schwachstellen erwartbar `EmoteService`, `UsageStatQueryService`); `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem Worktree, Befund unverändert vorgelegt; Widerspruch Opus ↔ Sol → Fable nur über die strittigen Findings; PR mit AK-Tabelle (28, 30, 31, 33, 41, 42, 46, 52, 54, 55 erfüllt; 32 „erfüllt in #372"), Abweichungen 1.3 und Prod-Hinweis 7; Kommentare auf #377, #378, #379 mit Abschnitt 10 |

Reihenfolge strikt T1 → … → T9 (T5 ist dateidisjunkt und dürfte parallel implementiert werden, Testläufe aber nacheinander). Zusammen
rund 6 Tage statt der 4 aus Spec §17 (Mehraufwand: Flag-Doppelpfad, Emote-Modus, Sperre-dann-Lesen für beide Pfade, Live-Prüfung mit
Api). Ob T6 zusätzlich einen Fable-Blick bekommt (Sperrfolge, Leave-Semantik, produktiver Pfad — hochgehebelt), entscheidet der
Orchestrator; die adversariale Sol-Prüfung dieses Plans ist Pflicht.

## 10. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#377 (Frontend):** `slotId` ist `string | null`. `null` heißt Emote-Modus: die Zeile trägt die Zahlen des ganzen Emotes, Drilldown über
  `/daily?emoteId=`, Schlüssel z. B. `emote:${emoteId}`; nur bei gesetzter `slotId` `/daily?slotId=`. Bei Flag aus (Prod bis #379) sind
  **alle** Zeilen im Emote-Modus — die Seite muss in diesem Zustand aussehen wie heute. `siblingNames` füllt nur die Slot-Sicht; für die
  nicht aktive Ansicht rechnet `mergeSetView` die Geschwister aus der Live-Liste (Spec 9.1). `/series`-Entries joinen über `slotId`, im
  Emote-Modus über `emoteId`. Ballot: `emoteNames`, `[0] == emoteName`. `firstSeenAtFromRest` ist im Emote-Modus immer `false` — die
  `mergeSetView`-Bindung über `(sevenTvEmoteId, addedAt)` greift nur im Slot-Modus.
- **#378 (Löschung je Slot):** `entries` nach 4.1; `addedAt` wörtlich aus `firstSeenAt` der Zeile; nur Zeilen mit `slotId` und
  `firstSeenAtFromRest` gehen als Eintrag; Zeilen mit `slotId null` (Emote-Modus, bei Flag aus alle) werden **ohne Eintrag** je Id gemeldet
  (Altsemantik, R3 b) — eine Anfrage darf beides tragen. Retries senden denselben Body. Ablehnung `emote_ids_invalid` (R4) heißt: die
  Slot-Ids passen nicht zum erwarteten Kanal (veralteter Tab) — nichts geschrieben, kein Resync; der periodische Tick heilt.
- **#379 (Betrieb):** #376 braucht kein Fenster. Mit dem Einschalten des Flags wird die Slot-Sicht und die Entries-Semantik auf Prod
  wirksam; Nachkontrolle: `handofblood` zeigt zwei Zeilen (AK 28), Latenz `/totals` gegen den Wert aus 6.2. Der Rückbau der Aus-Zweige
  umfasst den Emote-Modus-Zwang bei Flag aus, die Aus-Zweige der Ballot-Namen und den Bericht-Aus-Pfad (dann gilt nur noch 4.3).
