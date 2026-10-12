# Plan #377 — Emote-Slots, Kind 7: Frontend-Modell und Nutzungsseite, zwei Karten je Emote

Erstellt am 2026-10-12 im Worktree `EmotePurge-slots` (Branch `feat/377-slot-cards`, Basis `origin/main` @ `4b754279`, #376 gemergt).
**Status: Entwurf — offene Fragen an den Betreiber in Abschnitt 0, adversariale Sol-Prüfung steht aus.** Quelle und einzige
Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) (§3 D1, D2, D3, D7, D21–D23, D25; §5.1–5.3, 5.5 in der
Kind-6-Fassung; §9.1, 9.2, 9.4; §13 AK 34, 35, 46 UI-Hälfte; §15; §16 Kind 7), Issue #377 mit dem Übergabekommentar aus #376 (bindend),
[Plan-376](Plan-376-Emote-Slots-Read-API.md) §0 (R1, R2), §2, §10. Formatvorlage: Plan-376.

**Was dieser Plan trägt:** nur, was Spec und Übergabe offen lassen — die Schlüssel der Zeilen im Emote-Modus (den die Spec nicht kannte),
die Join-Regeln von `mergeSetView` im Detail, die Absicherung „Flag aus = heute", die Übergangsregeln für den Löschlauf je 7TV-Id bis #378,
Tests, Live-Prüfung, Aufgabenfolge. Kein Code. D0–D33 und die Betreiberentscheidungen aus #371–#376 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **F*n*** = offene Frage an den Betreiber (Abschnitt 0). **A*n*** = gefundene
Abweichung Spec ↔ Code ↔ Übergabe (Abschnitt 1.3).

## 0. Offene Fragen an den Betreiber

Jede Frage mit Empfehlung. Der Plan ist durchgängig auf die Empfehlung geschrieben; eine andere Antwort ändert nur die genannten Stellen.

**F1 — Emote-Modus-Zeilen behalten `×n` und „n Plätze im Set: a, b". Empfehlung: ja (Abweichung von D23/§9.1 „`slotCount`/`aliases`
entfallen, `setView.slots` entfällt").** Heute zeigt eine nicht aktive Set-Ansicht ein Emote, das unter zwei Namen im Set steht, als **eine**
Zelle mit `×2` und dem Satz „2 Plätze im Set: AliasOne, AliasTwo" (E2E `usage-atlas.e2e.spec.ts`, AK-58-Fälle im Seiten-Spec). Auf Prod
bleibt das Flag bis #379 aus, jede Zeile ist dann im Emote-Modus, und laut Spec §1 trägt **jedes** untersuchte Set solche Paare — die
Umstellung auf `+1`/„Auch im Set als …" wäre auf Prod sofort sichtbar und widerspräche der Vorgabe „Flag aus = Seite wie heute". Auch mit
Flag an bleibt der Fall bestehen (Emote mit unsettled Seed des angesehenen Sets, Plan-376 R2): die Karte summiert dann wirklich über
mehrere Einträge. Daraus die Regel: **`×n` heißt „diese eine Karte zählt n Einträge zusammen" (Emote-Modus), `+n` heißt „dieses Emote steht
zusätzlich unter n anderen Namen im Set, jeweils als eigene Karte" (Slot-Modus).** Beide Marker schließen sich je Zeile aus. Folge: die
Felder `slotCount`/`aliases` bleiben im Zeilenmodell mit enger gefasster Bedeutung (2.2), der Schlüssel `usageStats.setView.slots` bleibt,
`usageStats.setView.alsoAs` kommt dazu, der bestehende E2E-Fall „2 Plätze im Set" bleibt **unverändert** als Flag-aus-Wächter, und AK 35
„zeigt zwei Zellen statt ‚2 Plätze im Set'" wird durch einen **zusätzlichen** Slot-Modus-Fall erfüllt. *Alternative:* `+n` überall —
dann ändern sich der E2E-Fall, das AK-58-Trio und der Prod-Anblick nicht aktiver Sets mit dem nächsten Deploy.

**F2 — Nicht gezählte Live-Einträge (Klasse 3) bleiben je 7TV-Id gruppiert, solange die Antwort keine Slot-Zeile enthält. Empfehlung: ja
(Abweichung von D22 „Klasse 3 je Eintrag" für den Emote-Modus).** Heute wird ein nie gezählter Emote mit zwei Live-Einträgen eine Zelle
`×2`. Ein nie aktives Set (Halloween-Set vor der Umstellung) besteht fast nur aus solchen Zellen — „je Eintrag" würde die Zellenzahl, „x von
y Emotes", „alle markieren" und den Prod-Anblick verändern. Das Frontend kennt das Flag nicht; die Antwort verrät es aber: **mit Flag aus
hat keine `/totals`-Zeile eine `slotId`**. Regel [E]: `mergeSetView` arbeitet „slotweise" genau dann, wenn mindestens eine `/totals`-Zeile
der Antwort eine `slotId` trägt; sonst gruppiert es Klasse 3 je Id wie heute. Mit Flag an und lauter unsettled Seeds (erster Tick nach dem
Einschalten) ist die Ansicht dann folgerichtig ganz im Emote-Modus. *Alternative A:* Klasse 3 immer je Eintrag (Prod-Anblick ändert sich).
*Alternative B:* das Flag über `set-status` an das Frontend geben (Backend-Änderung, nicht in diesem Kind).

**F3 — Bis #378 löscht das Markieren einer Slot-Karte alle Einträge des Emotes. Empfehlung: hinnehmen, keine eigene Sperre.** Der Löschlauf
bleibt je 7TV-Id (Spec §16, Issue): `removeEmote` ohne `alias` nimmt jeden Eintrag der Id (Sonde 5, Zweig A), `toDeleteQueue` führt
gleiche Ids schon heute zu einer Zeile zusammen. Mit Flag an (nur lokal, bis #379 nie auf Prod, #378 mergt vor #379) markiert man also
`hob44` und verliert auch `ome44`. Der Plan sorgt dafür, dass alles, was der Nutzer vorher sieht, das richtig sagt: der Lauf bekommt je Id
**eine** Zeile mit **allen** Namen (Bestätigungsdialog, Protokoll, Restore), die Dock-Projektion zählt alle Einträge der Id als frei werdend
(2.6). *Alternative:* in #377 eine Sperre „Slot-Karten erst mit #378 löschbar" — eigener Sperrgrund, i18n, Tests für einen Zustand, den
nur der Entwickler je sieht.

**F4 — Schlüssel einer Emote-Modus-Zeile ist die 7TV-Id wie heute, Serien-Join über `slotId ?? sevenTvEmoteId`. Empfehlung: ja
(Abweichung von der Übergabe „`slotId ?? emoteId`", Plan-376 §10 „`emote:${emoteId}`" und Spec §5.3 „bei `null` über `EmoteId`").** Gleichwertig,
weil eine 7TV-Id in einer Antwort entweder genau eine Emote-Modus-Zeile **oder** nur Slot-Zeilen hat (Invariante §5.1); aber nur so bleiben
mit Flag aus alle Schlüssel bitgleich zu heute (Auswahl, `track`, Inspector, Rang, Füllbalken, Serien-Map), und die E2E-Serien-Mocks, die
als `emoteId` absichtlich `emote-of-<7tv-id>` senden (`mocks.ts` `mockUsageChannelSeries`), passen weiter. Ein Join über `emoteId` hätte in
der E2E-Suite still alle Sidecar-Kurven geleert (A2).

**F5 — `mocks.ts`-Vorgaben bilden den Aus-Zustand ab (`slotId: null`, `firstSeenAtFromRest: false`, `siblingNames: []`). Empfehlung: ja
(Abweichung von Spec §9.4 „`firstSeenAtFromRest` Default `true`").** Das Backend liefert im Emote-Modus immer `false` (§5.1). Mit diesen
Vorgaben ist die **ganze bestehende E2E-Suite** der Nachweis „Flag aus = heute"; Slot-Fälle setzen ihre Felder ausdrücklich.

Zur Kenntnis, keine Frage: **(a)** Klasse-3-Einträge behalten `firstSeenAt: null` wie heute (kein „Neu" aus dem Live-Datum); **(b)** die
Bandüberschriften und „x von y Emotes" zählen Karten, nicht Emotes (D1, Out of Scope §19); **(c)** im Slot-Modus können „N markiert" (Karten)
und „Löschen (M)"/„Zur Abstimmung stellen (M)" (eindeutige 7TV-Ids) auseinanderlaufen — gewollt, weil Löschlauf und Ballot je Id arbeiten
(D3, F3).

## 1. Umfang

### 1.1 Drin (Spec §16 Zeile 7, Issue #377)

1. **Modell** (§9.1): neue Wire-Felder in den DTOs, `slotId`/`slotKey`/`siblingNames`/`firstSeenAtFromRest` im Zeilenmodell, `addedAt` am
   Live-Eintrag (`ForeignEmoteRow`), `slotId`/`name` an Serien-Einträgen, `slotId` an der Tagesserie.
2. **`mergeSetView`** (D22, F2): Join der Slot-Zeilen über Datum bzw. Namen, Emote-Modus-Zeilen über die Id wie heute, Klasse 3 je Eintrag
   nur im Slot-Modus, Geschwisternamen der nicht aktiven Ansicht aus der Live-Liste.
3. **Seite** (§9.2): Schlüssel `slotKey` für Auswahl, `track`, Inspector, Rang, Füllbalken; Serien je Slot; Bänder, Rang, „nie benutzt",
   Pareto je Karte (fällt aus dem Zeilenmodell heraus).
4. **Dedupes je 7TV-Id:** Tag zuweisen/entfernen, Ballot (Null- und Set-Session) samt `voteBallotSize`, Kopier-Fluss (Übertragen),
   Löschlauf-Eingabe samt Dock-Projektion und `onDeleted` (Übergang bis #378, F3).
5. **Karten** (D23, F1): Marker `+n` mit `title`, `aria-label`, Inspector-Zeile und Sidecar über `usageStats.setView.alsoAs`.
6. **Drilldown:** `/daily?slotId=` für Slot-Karten, `?emoteId=` sonst; Cache trennt beide.
7. **Export** (D25): eine Zeile je Karte; Re-Import und Emote-Liste deduplizieren nach Id (besteht, wird durch Tests festgehalten).
8. **Vote-Detail** (D26 Abnehmerseite): `emoteNames.join(' / ')` an den drei Stellen, Suche trifft jeden Namen.
9. **D7 (AK 46, UI-Hälfte):** dieselbe `slotId` kommt mit neuem Datum und weiterlaufender Zählung zurück → dieselbe Karte, „Neu" nach
   neuem Datum. Keine eigene Logik — der stabile Schlüssel trägt es; Tests halten es fest.
10. i18n (Regel 7 betrifft nur Fehlercodes; hier ein neuer UI-Schlüssel in beiden Locales), Vitest, E2E, Mocks, DECISIONS-Absatz.

### 1.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 8 (#378) | `removeEmote` mit `alias`, Queue je `slotId`, `pendingSync`, `beforeStep` mit frischem Read, Nachprüfung D33, Berichte mit `entries`, Restore-Queue mit `slotId`, `pendingRemovalSlots` = markierte Live-Zeilen (Spec §9.3) |
| Kind 9 (#379) | Flag auf Prod, Abnahme, Rückbau der Aus-Zweige — **nicht** des Emote-Modus im Frontend (der bleibt auch mit Flag an nötig, Plan-376 R2) |
| unverändert | Fremdkanal-Raster (`foreign-emote-grid`, schlüsselt je Id; Duplikate dort sind ein eigenes Thema), Import-Queue je Id (K2), Leaderboard, Tag-Routen und Tag-Filter (je Id, D3), Wortlaut „Emotes" (§19) |
| Backend | **nicht berührt.** #376 liefert alle Felder; geprüft am Code @ `4b754279` (A3, 1.3) |

### 1.3 Spec ↔ Code ↔ Übergabe: gefundene Abweichungen

| # | Spec / Übergabe sagt | Code sagt (@ `4b754279`) | Umgang |
|---|---|---|---|
| A1 | §9.1: `slotKey` = `slotId` oder `live:…`; Übergabe: Karten über `slotId ?? emoteId`; Plan-376 §10: `emote:${emoteId}` | Die Spec kannte den Emote-Modus (Kind 6, R2) nicht; heute ist der Seitenschlüssel `sevenTvEmoteId` | F4: Emote-Modus-Zeilen behalten `sevenTvEmoteId` |
| A2 | §5.3/Übergabe: Serien-Join bei `slotId null` über `emoteId` | E2E-Mock sendet `emoteId: emote-of-<7tv-id>` ≠ `emoteId` der Totals-Zeile; die Seite liest `emoteId` der Serie heute nicht | F4: Join über `slotId ?? sevenTvEmoteId` |
| A3 | §2.4: „Die Live-Liste (`ForeignEmoteRow`) trägt seit #346 `addedAt`" | Stimmt für das Backend-DTO und den Draht (`ForeignEmoteSetService` mappt `AddedAt`, v4-GQL je Eintrag); das **Frontend**-Modell `ForeignEmoteRow` hat kein Feld | optionales `addedAt` im Frontend-Modell (2.1) |
| A4 | D22: Klasse 3 je Eintrag | heute je Id gruppiert; nie aktive Sets bestehen fast nur daraus | F2 |
| A5 | D23/§9.1: `slotCount`/`aliases`/`setView.slots` entfallen | Prod-Anblick nicht aktiver Sets mit Duplikaten; E2E und AK-58-Trio nageln ihn fest | F1 |
| A6 | §9.2/D25: Parser und `toImportRow` deduplizieren nach Id | `dedupeImportRows` tut das schon in Parser, Emote-Liste und Kopier-Fluss; der Kopier-Dialog zählt aber **vor** dem Dedupe | Tests (T6); Kopier-Erfassung dedupliziert vorher (2.6) |
| A7 | §9.2: Vote-Erstellung distinct | Server dedupliziert schon (Plan-376 A5) | Client dedupliziert trotzdem — für die Zählung im Button und im Dialog |
| A8 | §9.2: `onDeleted` „entfernt genau diese Zeilen" (Kind-8-Sicht) | `onDeleted` summiert frei werdende Plätze **je Zeile** mit `max(slotCount, Alias-Zahl des Laufs)` — mit zwei Slot-Zeilen einer Id würde doppelt gezählt (4 statt 2) | Summe je Id (2.6); Fehler läge sonst nur mit Flag an |
| A9 | §9.4: Mock-Default `firstSeenAtFromRest: true` | Backend liefert im Emote-Modus `false` | F5 |
| A10 | Übergabe: „Search should match every name" | bezieht sich auf Ballot-Zeilen; auf der Nutzungsseite verlangt AK 34 „Suche `hob44` findet die **zweite** Karte" | Vote-Detail: jeder Name; Nutzungsseite: nur der eigene Slot-Name (2.4, 2.8) |
| A11 | AK 35: E2E zeigt zwei Zellen **statt** „2 Plätze im Set" | der bestehende Fall ist der einzige E2E-Nachweis des Flag-aus-Anblicks nicht aktiver Duplikate | F1: alter Fall bleibt, neuer Slot-Fall kommt dazu |
| A12 | §2.5/§2.6 Zeilenangaben (`en.json:844`, `usage-stats-page.ts:1396` …) | verschoben (`setView.slots` steht bei `en.json:1062`) | nur Hinweis; Umsetzer sucht nach Namen, nicht nach Zeilen |
| A13 | §15: `usage-atlas` „1 ersetzt, +4 (Suche, Drilldown, Löschlauf, D7-Karte)" | der Löschlauf je Slot ist Kind 8 | hier: 0 ersetzt, +4 ohne Löschlauf je Slot; die Dedupe des Lauf-Eingangs prüft Vitest (4.2) |

### 1.4 Migration

Keine. Kein Backend, kein Wartungsfenster. Das Frontend wird vom Api-Image ausgeliefert; #377 geht mit dem nächsten Main-Deploy live und
muss dort mit Flag aus unsichtbar sein (Abschnitt 3).

## 2. Verträge

### 2.1 Wire-Modelle (`core/usage-stats/usage-stat.model.ts`, `core/seven-tv/foreign-emote-set.model.ts`, `core/voting/vote-session.model.ts`)

- **`EmoteUsageTotalDto`** + `slotId: string | null`, `firstSeenAtFromRest: boolean`, `siblingNames: string[]`. Doku je Feld aus §5.1
  (Kind-6-Fassung): `slotId null` = Emote-Modus (Summe über alle Slots des Emotes im Set; mit Flag aus jede Zeile); `emoteName`/`firstSeenAt`
  = Slot-Name/-Datum bzw. `Emote.Name`/`FirstSeenAt`; `siblingNames` = Namen der anderen Zeilen desselben Emotes in dieser Antwort, D2-Ordnung,
  im Emote-Modus leer.
- **`EmoteSeriesEntry`** + `slotId: string | null`, `name: string`. Die Doku zu `emoteId` („transitional, kein Leser") bleibt wahr (F4).
- **`EmoteUsageSeries`** + `slotId: string | null` (`null` auf dem Emote-Pfad).
- **`ForeignEmoteRow`** + `addedAt?: string | null` — optional, weil dieselbe Form von Leaderboard und Fremdkanal-Vorschau ohne Datum kommt;
  ISO-UTC mit Millisekunden, `null`/fehlend = 7TV nannte keins.
- **`VoteSessionResult`** + `emoteNames: string[]` (nie leer, `[0] == emoteName`, Plan-376 2.7).
- **Toleranz mitten im Deploy [E]:** Typen wie der Draht (nicht optional, außer `addedAt`); die Leser (`mergeSetView`, Vote-Detail) lesen
  fehlende Felder als Aus-Werte (`slotId` → `null`, `siblingNames` → `[]`, `firstSeenAtFromRest` → `false`, `emoteNames` → `[emoteName]`) —
  Muster wie `firstCountedDay` („absent from an older Api response … reads as null").

### 2.2 Zeilenmodell `EmoteUsageTotal`

| Feld | Bedeutung |
|---|---|
| `slotId: string \| null` | aus dem DTO; `null` für Emote-Modus-Zeilen und Klasse 3 |
| `slotKey: string` **neu** | der seitenweite Schlüssel, Regel 2.3 |
| `siblingNames: string[]` **neu** | andere Namen desselben Emotes **als eigene Karten**; nur Slot-Modus-Zeilen und Klasse 3 je Eintrag; sonst `[]` |
| `firstSeenAtFromRest: boolean` **neu** | aus dem DTO; Klasse 3 `false` |
| `slotCount: number` (bleibt, F1) | **Zahl der Set-Einträge, die diese eine Karte zusammenfasst:** Emote-Modus-Zeile der nicht aktiven Ansicht = Zahl der Live-Einträge ihrer Id (heute), Klasse 3 gruppiert = Gruppengröße (heute), sonst `1` |
| `aliases: string[]` (bleibt, F1) | die Namen eben dieser Einträge in Live-Reihenfolge; `[emoteName]`, wenn die Karte nur einen vertritt |
| übrige Felder | unverändert; `emoteName`/`firstSeenAt` sind bei Slot-Zeilen die des Slots (D1) |

Invariante (Tests): `siblingNames.length > 0` ⇒ `slotCount === 1`; `slotCount > 1` ⇒ `siblingNames` leer. Daraus folgen die Marker in 2.7.

### 2.3 Schlüssel (`slotKey`, F4)

| Zeile | `slotKey` |
|---|---|
| Slot-Modus (`slotId` gesetzt), aktiv oder nicht aktiv, auch `'left'` | `slotId` (Guid-String) |
| Emote-Modus (`slotId null`, DB-Zeile) | `sevenTvEmoteId` — **bitgleich zu heute** |
| Klasse 3, gruppiert (Emote-Modus-Antwort, F2) | `sevenTvEmoteId` — **bitgleich zu heute** |
| Klasse 3 je Eintrag (Slot-Modus-Antwort) | `live:${sevenTvEmoteId}#${addedAt ?? name}` (D21), `addedAt` als roher Wire-String |

**Kollisionsfreiheit** (Test): Guid-Strings (mit Bindestrichen), 7TV-ObjectIds (26 Zeichen Crockford-Base32) und das Präfix `live:` sind
disjunkt; eine 7TV-Id steht in einer Antwort entweder in genau einer Emote-Modus-Zeile oder nur in Slot-Zeilen (§5.1) — eine Emote-Modus-
Zeile verbraucht alle Live-Einträge ihrer Id (2.4), also gibt es zu ihr keine Klasse-3-Zeile. Namen sind je Set eindeutig, `live:<id>#<name>`
also auch.

**Schlüsselwechsel zur Laufzeit (nur Flag an):** settelt ein Emote zwischen zwei Reloads, wechselt seine Karte von `sevenTvEmoteId` auf
`slotId`; eine Markierung darauf fällt durch `retainAmong` heraus und die bestehende #94-Meldung „Auswahl bereinigt" erscheint. Hingenommen
(einmal je Emote, nur beim Einschwingen). Persistiert wird kein Schlüssel: die URL trägt nur `emoteSetId` (`listQueryState`), weder
`localStorage` noch `sessionStorage` halten Auswahl oder Inspector.

### 2.4 `mergeSetView` (D22, F2) — reine Funktion, Signatur unverändert `(totals, live, isActiveSet)`

**Modus der Antwort [E]:** `slotView` = mindestens eine Zeile in `totals` hat `slotId ≠ null`.

**Aktive Ansicht** (`isActiveSet` oder `live === null`): 1:1 wie heute, je DTO eine Zeile `'live'`, `slotCount 1`, `aliases [emoteName]`,
`siblingNames` aus dem DTO, `slotKey` nach 2.3. Kein Live-Abgleich (E16).

**Nicht aktive Ansicht**, in dieser Reihenfolge; jeder Live-Eintrag wird höchstens einmal verbraucht:

1. **Emote-Modus-Zeilen** (`slotId null`): binden **alle** Live-Einträge ihrer 7TV-Id wie heute — `emoteName`/`imageUrl` vom ersten
   Eintrag der Gruppe in Live-Reihenfolge, `slotCount` = Gruppengröße, `aliases` = deren Namen; keine Gruppe → `'left'`.
2. **Datierte Slot-Zeilen** (`firstSeenAtFromRest` und `firstSeenAt` parsebar): binden den Live-Eintrag derselben Id mit **gleichem
   Zeitpunkt**, verglichen über `Date.parse` (Millisekunden), nie über Zeichenketten (die beiden Wege serialisieren unterschiedlich viele
   Nachkommastellen). Mehrere Einträge mit gleichem Zeitpunkt (D4-Gleichstand) → nur der mit gleichem Namen.
3. **Übrige Slot-Zeilen** (provisorisch oder in Schritt 2 ohne Treffer): binden den noch freien Live-Eintrag derselben Id mit gleichem
   Namen — provisorische Zeilen jeden, datierte nur einen **undatierten** Eintrag (Spiegel von §6.2 Schritt 5 [E]: fehlt der Vorschau ein
   Datum, darf eine gezählte Karte nicht als „nicht mehr im Set" verschwinden).
4. Gebundene Slot-Zeile: `'live'`, Zahlen aus dem DTO, `emoteName`/`imageUrl` vom Live-Eintrag (frischer, wie heute), `slotCount 1`,
   `aliases [Name]`. Ungebunden → `'left'` mit DTO-Werten (die Seite blendet `'left'` aus, außer bei abgeschnittener Liste — heute).
5. **Rest der Live-Einträge (Klasse 3):** `slotView` → **je Eintrag** eine Zeile (`emoteId null`, Zahlen `null`, `firstSeenAt null`,
   `slotCount 1`, `aliases [Name]`, `slotKey` `live:…`); sonst je Id gruppiert wie heute.
6. **`siblingNames` (nicht aktive Ansicht):** für Slot-Zeilen und Klasse 3 je Eintrag = Namen der **anderen Live-Einträge** derselben Id,
   ordinal distinct, ohne den eigenen, sortiert nach `addedAt` aufsteigend (`null` zuletzt), dann Name ordinal (D2-Ordnung, Spec §9.1);
   `'left'`-Zeilen behalten die DTO-Liste; Emote-Modus-Zeilen `[]`.

**Reihenfolge der Ausgabe** wie heute: `totals`-Reihenfolge (gebunden oder `'left'`), danach Klasse 3 in der Reihenfolge des ersten
Auftretens in `live`. Die Doku-Kommentare am Kopf der Datei werden auf diese Regeln umgeschrieben (sie nennen heute „#74 duplicate groups
to one cell" als einzige Form).

### 2.5 Seite (`features/usage-stats/usage-stats-page.ts`/`.html`)

| Stelle | Neu |
|---|---|
| `selection` (Schlüsselfunktion), `@for … track`, `inspectedId`/`inspect`/`onCellFocus`/`onCellClick`, Inspector-Fallback, `fillPercents`, `usageRank`/`inspectedRank`, `fillPercent` | `slotKey` statt `sevenTvEmoteId`; Kommentare, die „Keyed by 7TV id (7.2)" sagen, nennen die Regel 2.3 |
| `seriesByEmote` | Map mit Schlüssel `slotId ?? sevenTvEmoteId` je Serien-Eintrag; Nachschlagen mit demselben Ausdruck an der Zeile; Klasse 3 hat keine Zahlen und fragt nie (Bestand) |
| Bänder, `deadCount`, `totalUses`, Pareto, Verteilung, `emoteCountKey`, „Neu"/„Im Set seit"/Trend, `hideObserved` | keine Codeänderung — je Zeile = je Karte (D1) |
| Tag-Filter | unverändert (Schlüssel je 7TV-Id, D3): beide Slot-Karten eines getaggten Emotes erscheinen |
| `assignTags`, `tagUnassignIds`/`tagUnassignCount` | eindeutige 7TV-Ids (erste Vorkommen) |
| `voteBallotEmoteIds`, `voteBallotSevenTvEmoteIds`, `voteBallotSize` | eindeutige Ids |
| `importableSelection`, `importableVisible` | nach Id dedupliziert **vor** der Erfassung (A6), damit Dialog-Zahl und kopierte Zeilen übereinstimmen; der Name ist der der ersten Zeile in der jeweiligen Reihenfolge |
| `selectedForDelete`, `pendingRemovalSlots`, `onDeleted` | 2.6 |
| `canDrilldown`, `openDrilldown` | Gate unverändert (`emoteId` und Zahlen vorhanden); `openDrilldown` gibt zusätzlich `slotId` mit (2.9) |
| Suche | unverändert: trifft den Namen **dieser** Karte (AK 34) |

### 2.6 Übergang Löschlauf je 7TV-Id bis #378 (F3)

- **`selectedForDelete`:** je eindeutiger 7TV-Id unter den markierten `'live'`-Zeilen **ein** `DeletableEmote`: `emoteId` = erste nicht-`null`
  `emoteId` der Id, `name` = Name der ersten markierten Zeile, `aliases` = ordinal distinct Vereinigung über **alle** `'live'`-Zeilen dieser
  Id in `emotes()` (markiert oder nicht) aus `aliases` und `siblingNames` — ein `REMOVE` ohne Alias nimmt sie alle, das Protokoll muss sie
  für den Restore kennen; `hidden` = jede markierte Zeile der Id ist ausgeblendet. Die aktive Ansicht liest die Live-Aliase weiterhin selbst
  (`readLiveAliasesFromActiveSet`, Bestand); `toDeleteQueue` bleibt als zweite Sicherung unverändert.
- **`pendingRemovalSlots`:** Summe über die eindeutigen markierten `'live'`-Ids der Summe von `slotCount` **aller** `'live'`-Zeilen dieser Id.
  Mit Flag aus (eine Zeile je Id) gleich der heutigen Zahl.
- **`onDeleted`:** frei werdende Plätze je **Id** = `max(Summe slotCount der Zeilen dieser Id, Alias-Zahl der Laufzeile)` (A8); das
  Entfernen aller Totals-Zeilen mit gelöschter Id bleibt (nimmt jetzt alle Slot-Zeilen der Id mit — richtig, weil das `REMOVE` alle nahm).

### 2.7 Karten-Marker (D23, F1)

- **Slot-Modus mit Geschwistern** (`siblingNames.length > 0`): Scrim-Glyph `+{{n}}` (n = Zahl der Geschwister) an der Stelle des heutigen
  `×{{slotCount}}`, dekorativ (`aria-hidden`), `title` = `usageStats.setView.alsoAs`; `aria-label` der Zelle hängt `' · ' + alsoAs` an;
  Inspector-Zeile und Sidecar zeigen denselben Satz.
- **Emote-Modus mit `slotCount > 1`:** heutiges `×n`/`setView.slots` an allen vier Stellen, unverändert.
- **Neuer Schlüssel** `usageStats.setView.alsoAs` in `de.json` und `en.json`, Parameter `names` (Komma-Liste), Wortlaut sinngemäß
  „Auch im Set als {{names}}" / „Also in the set as {{names}}". Der Namensvetter-Marker `≈` bleibt daneben (beide in einem Scrim, wie heute).
- Designsprache §2.5 gilt wie für `≈` und `×n`; keine neue Farbe, kein neuer Baustein.

### 2.8 Vote-Detail (`features/voting/vote-session-detail-page.{ts,html}`, `shared/emotes/emote-usage-filter.ts`)

- Anzeige `emoteNames.join(' / ')` an den drei Stellen, die heute `emoteName` zeigen (Inspector-Zeile, `aria-label` der Zelle, Sidecar-
  Überschrift); Drilldown-Titel, Lösch-`name`, Ballot-Logik unverändert (`emoteName`). Löschen bleibt je Id ohne `alias` (D3), die Live-Alias-
  Lesung des Panels (`readLiveAliasesFromSet`) bleibt.
- **`EmoteUsageFilter`:** `FilterableEmote` bekommt optional `emoteNames?: readonly string[]`; die Namenssuche trifft, wenn `emoteName`
  **oder** einer der `emoteNames` passt. Die Nutzungsseite liefert das Feld nicht (A10).

### 2.9 Tagesserie und Drilldown (`core/usage-stats/usage-stat.service.ts`, `shared/emotes/emote-drilldown-dialog.ts`)

- `getDailySeries` bekommt statt der nackten `emoteId` ein Ziel „Slot **oder** Emote" (diskriminierte Form, Signatur in Worten: Kanal,
  Ziel `{ slotId } | { emoteId }`, Bereich, Scope → Serie). Request: genau einer der Parameter `slotId=`/`emoteId=` (der Server ließe bei beiden
  `slotId` gewinnen; wir senden nie beide). Cache-Schlüssel `…|slot:<id>|…` bzw. `…|emote:<id>|…`, so dass Slot- und Emote-Serien mit
  zufällig gleicher Id nie kollidieren.
- `EmoteDrilldownData` + `slotId?: string | null`; der Dialog fragt mit `slotId`, wenn gesetzt, sonst mit `emoteId`. Nutzungsseite: Slot-Karte
  → `slotId`, Emote-Modus-Zeile → `emoteId`; Vote-Detail: immer `emoteId` (D3, Summe). Der 7TV-Link bleibt je Emote.

### 2.10 Export (D25)

Eine Zeile je Karte, ohne neue Spalten (CSV/JSON wie heute). `seven_tv_emote_id` ist nicht mehr eindeutig. Re-Import einer Nutzungs-Datei
(`parseImportSource`) und der Zweck „Emote-Liste" deduplizieren nach Id, erste Zeile gewinnt (Bestand, A6) — die Reihenfolge der Datei ist die
Atlas-Reihenfolge, also gewinnt bei zwei Karten die meistgenutzte. Hingenommen [E].

## 3. Absicherung „Flag aus = heute"

Mit Flag aus hat jede `/totals`-Zeile `slotId null`, `siblingNames []`, `firstSeenAtFromRest false` (Plan-376 R1). Dann gilt nach diesem Plan:

| Gegenstand | warum unverändert |
|---|---|
| Schlüssel (Auswahl, `track`, Inspector, Rang, Füllbalken, Serien) | Emote-Modus und gruppierte Klasse 3 behalten `sevenTvEmoteId` (2.3) |
| Zellen der nicht aktiven Ansicht, Klasse 3, `'left'` | `slotView` ist falsch → Join je Id und Gruppierung wie heute (2.4) |
| Marker, `aria-label`, Inspector, Sidecar | nur `×n`/`setView.slots` kann erscheinen; `alsoAs` braucht `siblingNames` (2.7) |
| Dock-Projektion, `onDeleted`, Löschlauf, Ballot, Tags, Kopieren | Dedupe je Id ist bei einer Zeile je Id die Identität (2.5, 2.6) |
| Drilldown | Emote-Modus fragt `?emoteId=` wie heute; nur der Cache-Schlüssel ist intern anders |
| URL, gespeicherte Zustände | keine betroffen (2.3) |

**Nachweis:** (1) alle bestehenden Vitest-Fälle laufen ohne inhaltliche Änderung — erlaubt sind nur Ergänzungen der Fixture-Helfer um die
neuen Felder mit Aus-Werten (`emote()` im Seiten-Spec, Ergebnis-Fixture im Vote-Detail-Spec, die wenigen Objektliterale vom Typ
`EmoteUsageTotal`); **ändert ein bestehender Fall seine Erwartung, bricht der Umsetzer ab und meldet es** (das hieße: Prod-Verhalten ändert
sich). Ausdrücklich unverändert grün: `merge-set-view.spec.ts` (8), das AK-58-Trio und die AK-72-Fälle im Seiten-Spec, `usage-export*.spec.ts`,
`import-source-parser.spec.ts`. (2) die ganze E2E-Suite mit den Aus-Vorgaben der Mocks (F5), insbesondere `usage-atlas.e2e.spec.ts` mit
„2 Plätze im Set: AliasOne, AliasTwo" und den beiden Klasse-3-Zellen. (3) Live-Phase A (Abschnitt 5): Zellen-Labels der Seite gegen eine
Baseline von `4b754279`, Diff leer.

## 4. Tests

Regel 12: Verhalten, nicht Vorlage — Zustände, `computed()`-Ergebnisse, Ausgaben, Sperrentscheidungen, Accessibility-Semantik (Rolle,
zugänglicher Name, `aria-*`); keine CSS-Klassen, keine Snapshots, Übersetzungswortlaut nur zur Identifikation.

### 4.1 `merge-set-view.spec.ts` (8 bestehende unverändert, +9)

Slot-Modus aktiv: `slotKey = slotId`, `siblingNames` aus dem DTO · nicht aktiv, datierter Join über den Zeitpunkt bei abweichender
Zeichenkettenform (`…56.780Z` gegen `…56.78Z`) · Gleichstand zweier Einträge mit gleichem Zeitpunkt bindet über den Namen · provisorische
Zeile bindet über den Namen · datierte Zeile ohne Datumstreffer, Eintrag undatiert mit gleichem Namen → gebunden; Eintrag datiert mit anderem
Zeitpunkt → Zeile `'left'`, Eintrag Klasse 3 · Klasse 3 je Eintrag mit `live:<id>#<addedAt>` bzw. `#<name>` bei fehlendem Datum,
`siblingNames` aus der Live-Liste in D2-Ordnung · Emote-Modus-Zeile in einer Slot-Antwort verbraucht alle Einträge ihrer Id, `slotCount` =
Gruppengröße, keine Klasse 3 dazu · Schlüssel einer gemischten Ansicht paarweise verschieden (AK 35) · fehlende Neufelder (älteres DTO) lesen
sich wie Aus-Werte.

### 4.2 `usage-stats-page.spec.ts` (bestehende unverändert bis auf Fixture-Helfer, +12)

AK 34: zwei Slot-Zeilen einer Id → zwei Karten, Namenssuche `hob44` lässt genau eine übrig, Rang je Karte, Band je Karte · Auswahl je
`slotKey`: Markieren der einen Karte markiert die andere nicht · Inspector bleibt nach einem stillen Reload auf der Slot-Karte ·
`seriesByEmote`/Kurve der Slot-Karte aus dem Serien-Eintrag mit ihrer `slotId`, Emote-Modus-Zeile aus dem Eintrag mit `slotId null` ·
Tag zuweisen und Ballot (Null- und Set-Session) senden eindeutige Ids, `voteBallotSize` zählt Ids · Kopier-Erfassung zählt Ids ·
`selectedForDelete` mit einer markierten von zwei Slot-Karten → ein Eintrag mit beiden Namen · `pendingRemovalSlots` = 2 dafür ·
`onDeleted` mit zwei Slot-Zeilen einer Id und Laufzeile mit zwei Aliasen → `occupiedSlots` −2 (nicht −4), beide Karten weg (A8) ·
Drilldown einer Slot-Karte übergibt `slotId`, einer Emote-Modus-Zeile nicht · **D7 (AK 46):** Reload mit derselben `slotId`, neuem
`firstSeenAt` (heute) und höherer Zahl → dieselbe Karte bleibt markiert und inspiziert, Zahl gestiegen, „unter Beobachtung" an ·
`aria-label` der Slot-Karte enthält den `alsoAs`-Satz, der einer Emote-Modus-Zeile mit `slotCount 2` den `slots`-Satz.

### 4.3 Übrige Vitest-Dateien

- `usage-stat.service.spec.ts` +2: Slot-Ziel sendet nur `slotId`; Slot- und Emote-Serie gleicher Id werden getrennt gecacht.
- `emote-drilldown-dialog.spec.ts` +2: mit `slotId` fragt der Dialog die Slot-Serie, ohne die Emote-Serie.
- `emote-usage-filter.spec.ts` +1: Namenssuche trifft über `emoteNames`; ohne das Feld nur `emoteName`.
- `vote-session-detail-page.spec.ts` +1: Ergebniszeile mit `emoteNames [ome44, hob44]` → zugänglicher Name/Inspector nennt „ome44 / hob44",
  Suche `hob44` findet sie; fehlendes Feld → `[emoteName]`.
- Export (T6) +3: `usage-export.spec.ts` zwei Karten einer Id → zwei Zeilen; `import-source-parser.spec.ts` Nutzungs-Datei mit zwei Zeilen
  einer Id → eine Zeile, `duplicatesCollapsed 1`, erste gewinnt; `usage-export-purposes.spec.ts` Emote-Liste aus zwei Karten → eine Zeile.

### 4.4 E2E (`web/e2e`, Import aus `support/test.ts`)

- **`support/mocks.ts`:** `MockEmoteUsage` + optionale `slotId`, `firstSeenAtFromRest`, `siblingNames` mit Aus-Vorgaben (F5);
  `mockUsageChannelSeries` sendet zusätzlich `slotId: null`, `name` und nimmt optional Slot-Einträge (Schlüssel `slotId`) an; die
  Set-Vorschau-Mocks nehmen optional `addedAt` je Eintrag; `MockVoteSessionEmote` + optionales `emoteNames` (Vorgabe `[emoteName]`). Kein
  bestehender Aufruf ändert sich.
- **`usage-atlas.e2e.spec.ts` +4, 0 ersetzt (F1, A11, A13):** (1) aktive Ansicht, Slot-Modus: zwei Zellen `ome44`/`hob44`, zugänglicher Name
  mit „Auch im Set als …", Suche `hob44` → eine Zelle, keine Konsolenmeldung (NG0955-Wächter wie im bestehenden Fall); (2) Drilldown einer
  Slot-Zelle fragt `/daily` mit `slotId` (Request geprüft), Emote-Modus-Zelle mit `emoteId`; (3) nicht aktive Ansicht, Slot-Modus: Live-Liste
  mit `addedAt`, eine gezählte Slot-Zelle plus Klasse-3-Zelle des Geschwistereintrags statt „2 Plätze im Set"; (4) D7: erster Abruf
  `ome44` mit Zahl n und altem Datum, nach „Aktualisieren" dieselbe `slotId` mit neuem Datum und n+k → dieselbe Zelle bleibt markiert, Zahl
  n+k, Sidecar zeigt „unter Beobachtung".
- **`vote-ballot.e2e.spec.ts` +1:** Set-Session-Ergebnis mit `emoteNames [ome44, hob44]` → Zelle heißt „ome44 / hob44", Suche `hob44` findet sie.

### 4.5 Nicht nötig

Backend-Tests (kein Backend), Lauf-Engine-/Lösch-Service-Specs (Kind 8), `api-error-locales.spec.ts` (kein neuer Fehlercode, Regel 7
unberührt).

## 5. Live-Verifikation (Regel 16 sinngemäß; vom Orchestrator koordiniert, Freigabe je Anlass)

Randbedingungen wie Plan-376 §6: geteilte Dev-DB nur als Kopie, nie `docker compose`, nie etwas im Haupt-Checkout; Testkanal
`olaf_olaf_son`, `handofblood` nur lesend; 7TV-Schreibzugriffe nur mit dem Token aus `~/.config/emotepurge-7tv-test-token` als Header-Datei.
**Port-Abstimmung:** Api auf `:5151` (nur dort stimmt die Twitch-Redirect-URI) kollidiert mit der #374-Sitzung in `EmotePurge-slots374`;
während dieses Schritts keine E2E-Läufe und keine andere Api auf `:5151`; Dev-Server `:4200` (Branch) und `:4201` (Baseline) ebenfalls
vorher prüfen. Browser über ein Node-Playwright-Skript mit `web/node_modules` (kein Chrome-MCP), Cookie aus einer Scratchpad-Datei (0600).

0. `df -h /`; DB-Kopie `emotepurge_slots377`, Wegwerf-Redis, Worker und Api per `dotnet run` aus dem Worktree mit Env-Overrides wie Plan-376 6.1
   (Backfill, Retention, EventAPI aus). Testkanal vorbereiten: `01KAMA120V9DC2P1YW9QWFHA16` als `ome44` und `hob44` im Set (Ausgangszustand sichern).
1. **Baseline:** `git archive 4b754279 web` in den Scratchpad, `node_modules` per Symlink, `ng serve --port 4201` mit derselben Proxy-Datei.
2. **Phase A, Flag aus:** Skript liest auf beiden Dev-Servern für `handofblood` (aktives Set, ein nicht aktives Set mit Duplikat, ein nie
   aktives Set) und den Testkanal die Liste der Zellen (zugänglicher Name, Reihenfolge), die Bandüberschriften, „x von y", Slot-Leiste, nach
   Markieren einer Duplikat-Zelle die Dock-Zahlen. **Diff leer.**
3. **Phase B, Flag an** (Worker und Api neu mit `SevenTv__SlotReconcile__Enabled=true`, ein Tick): Testkanal zeigt zwei Zellen mit `+1`
   (AK 34), Suche `hob44`, Drilldown fragt `slotId` (Netzwerkprotokoll), nicht aktives Set mit Klasse 3 je Eintrag, keine NG0955 in der
   Konsole; Set-Session über die Seite aus beiden Karten anlegen → eine Ballot-Zeile „ome44 / hob44" (AK 33/35); Export beider Karten → zwei
   Zeilen, Re-Import-Vorschau eine (AK 35).
4. **D7 live (AK 46):** `hob44` per 7TV mit `alias` entfernen, Resync-Tick abwarten, `hob44` wieder hinzufügen, Tick → dieselbe Karte (gleiche
   `slotId` im Netzwerkprotokoll), alte Zählung, „Neu". Danach Set wiederherstellen. *Kein* Löschlauf aus unserer Seite (je Id, F3).
5. **Abbau:** Prozesse per SIGTERM (PIDs vorher mit `ps -o args=` prüfen), Redis-Container, `DROP DATABASE … WITH (FORCE)`, Cookie- und
   Header-Datei, Baseline-Ordner. Protokoll mit SHA und Ergebnissen, keine Hostnamen/Tokens/Cookies.

## 6. DECISIONS und Spec

**DECISIONS: genau ein englischer Absatz** „Child 7 (usage page per slot), <Datum>" im Entwurfseintrag #365 (`docs/DECISIONS.md`, Abschnitt ab
Zeile ~443), **hinter dem Absatz „Child 6 (read API and reports)"** und vor dem nächsten `###`-Eintrag. Er wächst mit den Commits von T1, T3 und
T4. **Rebase-Konflikt erwartet:** #374 (Kind 4) schreibt parallel einen eigenen Kind-Absatz in denselben Eintrag; beim Rebase beide Absätze
behalten, Reihenfolge nach Kindnummer (Child 4 vor Child 6 vor Child 7), Inhalt nicht mischen. Inhalt: F1–F5 als Betreiberentscheidungen; die
Schlüsselregel 2.3 samt Kollisionsargument; die Join-Regel 2.4 (Zeitpunkt in ms, Gleichstand, undatierte Fallback-Bindung, Modus der Antwort);
der Übergang 2.6 bis #378; die Abweichungen A1, A2, A4, A5, A9, A11. `Betrifft:` ergänzt um `merge-set-view.ts`, `usage-stat.model.ts`,
`usage-stat.service.ts`, `usage-stats-page.ts`/`.html`, `emote-drilldown-dialog.ts`, `emote-usage-filter.ts`, `vote-session-detail-page.*`,
`foreign-emote-set.model.ts`, `vote-session.model.ts`, `e2e/support/mocks.ts`.

**Spec-Änderungen (Regel 3; deutsch, datierter Hinweis „Kind 7, Betreiber <Datum>" am Abschnitt, Text ersetzt):** §9.1 (Schlüssel der
Emote-Modus-Zeilen, `slotCount`/`aliases` mit enger Bedeutung, `slotView`), §9.2 Zeilen „Karte (D23)" und „`seriesByEmote`" (Join-Schlüssel),
D22/D23 je ein Halbsatz mit Verweis auf §9.1, §9.4 (Mock-Vorgaben). AK 34/35/46 bleiben wörtlich gültig; AK 35s E2E-Satz gilt für den
Slot-Modus. Nur wenn der Betreiber F1/F2/F4/F5 wie empfohlen entscheidet; sonst entfallen die betreffenden Änderungen.

## 7. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext)

**Gate je Aufgabe:** `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run format:check` grün. E2E nur in
T7 und T8 (`npm --prefix web run e2e`, **nur ohne Api auf `:5151`** — vorher `ss -ltnp | grep 5151` leer, sonst Orchestrator fragen). Abbruch
und Meldung, wenn ein bestehender Fall seine Erwartung ändern müsste (Abschnitt 3). Commits Conventional, englisch, Tests im Commit ihres
Features, **Dateien exakt stagen** (geteilter Index, Memory „Parallel sessions share git index").

**Reihenfolge strikt T1 → T8.** T2, T5 und T6 sind dateidisjunkt zu T3/T4 und könnten parallel implementiert werden, laufen aber im selben
Worktree mit geteiltem Index und geteilten Testläufen — deshalb nacheinander. T3 und T4 berühren dieselben Dateien (Seite `.ts`/`.html`/`.spec.ts`)
und sind zwingend sequenziell.

| # | Aufgabe | Modell | Aufwand | Hängt ab von | Input | Fertig, wenn / Commit |
|---|---|---|---|---|---|---|
| T1 | **Modell und `mergeSetView`:** Wire-Felder (2.1), Zeilenmodell (2.2), Schlüssel (2.3), Join-Regeln (2.4), Datei-Doku; Fixture-Helfer der bestehenden Specs um Aus-Werte ergänzen; 4.1 | **opus** | 0,75 T | — | 0 (F1, F2, F4), 1.3, 2.1–2.4, 3, 4.1; `merge-set-view.ts`, `usage-stat.model.ts`, `foreign-emote-set.model.ts` | 8 bestehende + 9 neue Fälle grün, Seite kompiliert unverändert (schlüsselt noch je Id); `feat(web): key usage rows by slot and join set views per entry` **plus DECISIONS-Teil** (2.3, 2.4, F1, F2, F4) und Spec §9.1/D22 |
| T2 | **Tagesserie und Drilldown** (2.9), 4.3 erste zwei Punkte | sonnet | 0,25 T | T1 | 2.9; `usage-stat.service.ts`, `emote-drilldown-dialog.ts`, Aufrufer auf beiden Seiten | Specs grün; `feat(web): ask the daily series per slot for slot cards` |
| T3 | **Seite je Slot:** Schlüssel und Ableitungen (2.5), Dedupes, Übergang Löschlauf (2.6), Drilldown-Übergabe; 4.2 ohne den `aria-label`-Fall | **opus** | 0,75 T | T1, T2 | 2.5, 2.6, 3, 4.2, F3 | Seiten-Spec grün, bestehende Fälle inhaltlich unverändert; zwei Commits: `feat(web): key the usage atlas by slot` und `feat(web): dedupe 7TV ids for tags, ballots, copy and the per-id delete run` **plus DECISIONS-Teil** (2.6, A8) |
| T4 | **Karten-Marker und i18n** (2.7): Zelle, `aria-label`, Inspector-Zeile, Sidecar; `alsoAs` in beiden Locales; `aria-label`-Fall aus 4.2 | sonnet | 0,25 T | T3 | 2.7, F1; Designsprache §2.5; `usage-stats-page.html` | Spec grün; `alsoAs` in beiden Locales vorhanden (es gibt keinen allgemeinen Paritäts-Spec, nur den für Fehlercodes — per Hand prüfen); `feat(web): name an emote's other slots on its cards` **plus DECISIONS-Teil** (F1-Marker) und Spec §9.2 |
| T5 | **Vote-Detail und Filter** (2.8), 4.3 Punkte 3–4 | sonnet | 0,25 T | T1 (nur Modell) | 2.8; `vote-session-detail-page.*`, `emote-usage-filter.ts`, `vote-session.model.ts` | Specs grün; `feat(web): show every alias on a ballot row and find it by any of them` |
| T6 | **Export-Nachweise** (2.10), 4.3 letzter Punkt — keine Produktivänderung erwartet; braucht es doch eine, Abbruch und Meldung | haiku | 0,1 T | T3 | 2.10, A6 | `test(web): pin one export row per slot card and the id dedupe on re-import` |
| T7 | **E2E und Mocks** (4.4) | sonnet | 0,5 T | T1–T6 | 4.4, F5; `e2e/support/mocks.ts`, `usage-atlas.e2e.spec.ts`, `vote-ballot.e2e.spec.ts` | ganze E2E-Suite grün (bestehende Fälle unverändert); `test(e2e): cover slot cards, slot drilldown, per-entry set view and resumed slots` **plus Spec §9.4** |
| T8 | **Gates, Coverage, Review, PR** | **opus** (Review); Codex Sol | 0,25 T | T7 | Branch, 1.3, 3, 6, 9 | alle drei Web-Gates plus `npm --prefix web run e2e` grün; erst committen, dann `node scripts/coverage-local.mjs --frontend-only` ≥ 80 % (Schwachstelle erwartbar `usage-stats-page.ts`, dateigenaue Näherung, s. Memory „coverage-local.mjs: Grenzen"); `dotnet test` entfällt (kein Backend), Nachweis „Backend unberührt" per `git diff --stat origin/main -- src tests` leer; `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem Worktree, Befund unverändert vorgelegt; Widerspruch Opus ↔ Sol → Fable nur über die strittigen Findings; PR mit AK-Tabelle (34, 35, 46 UI-Hälfte), Abweichungen 1.3, Prod-Hinweis „unsichtbar mit Flag aus"; Kommentar auf #378 mit Abschnitt 8 |
| T9 | **Live-Verifikation** (Abschnitt 5) | sonnet | 0,5 T + Betreiberzeit | T8 vor dem Merge | 5; Port-Abstimmung durch den Orchestrator | Protokoll 5.0–5.5, Phase-A-Diff leer, Set wiederhergestellt, Abbau erledigt |

Die adversariale Sol-Prüfung dieses Plans (`/codex:adversarial-review --model gpt-6.1-sol`, Fokus: Flag-aus-Identität, Join-Regeln,
Schlüssel-Kollisionen, Übergang Löschlauf) steht vor T1 aus und ist Pflicht (globale Regel).

## 8. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#378 (Löschung je Slot):** `selectedForDelete` liefert heute je 7TV-Id **eine** Zeile mit allen Namen (2.6) — #378 ersetzt das durch eine
  Zeile je markierter Slot-Karte mit `slotId`, `alias` = `emoteName`, `addedAt` = `firstSeenAt` wörtlich und `addedAtFromRest`; Zeilen mit
  `slotId null` (Emote-Modus, mit Flag aus alle) bleiben je Id **ohne** Eintrag (Plan-376 §10). `pendingRemovalSlots` und `onDeleted` rechnen
  dann je Zeile statt je Id. Der `slotKey` einer Emote-Modus-Zeile ist die 7TV-Id, einer Klasse-3-Zeile je Eintrag `live:<id>#<addedAt|name>` —
  die Queue schlüsselt laut Spec `${sevenTvEmoteId}#${slotId}`, Klasse 3 hat keine `slotId` (Spec §9.3 klären). `onDeleted` bekommt dann
  `doneKeys` je Slot; `slotCount > 1` gibt es nur im Emote-Modus (F1).
- **#379 (Betrieb):** Das Frontend braucht für das Einschalten nichts. Nachkontrolle: `handofblood` zeigt `ome44`/`hob44` als zwei Karten mit
  `+1`; nicht aktive Sets mit noch unsettled Seeds zeigen weiter `×n` (F1) — kein Fehler. Der Emote-Modus im Frontend wird mit dem Rückbau der
  Aus-Zweige **nicht** entfernt.

## 9. Aufwand

Rund **3,4 Tage** (T1 0,75 · T2 0,25 · T3 0,75 · T4 0,25 · T5 0,25 · T6 0,1 · T7 0,5 · T8 0,25 · T9 0,5) gegenüber 3 aus Spec §17. Mehraufwand:
der Emote-Modus, den die Spec nicht kannte (Schlüssel, Join, Marker-Doppelregel), die Übergangsrechnung des Löschlaufs je Id (A8) und die
Baseline-Diff-Prüfung der Live-Phase A.
