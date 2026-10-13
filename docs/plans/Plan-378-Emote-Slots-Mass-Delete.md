# Plan #378 — Emote-Slots, Kind 8: Massenlöschung und Wiederherstellung je Slot

Erstellt am 2026-10-13 gegen den Worktree `EmotePurge-slots` (Branch `feat/377-slot-cards`, zuletzt auf `origin/main` rebased, Kopf
`beb738a2`); seit Revision 3 geprüft gegen den Worktree `EmotePurge-slots374` (Branch `feat/378-slot-mass-delete` = `origin/main` @ `94a84782`,
enthält #377). Umgesetzt wird dort (Abschnitt 10). Ziel-Datei im Repo:
`docs/plans/Plan-378-Emote-Slots-Mass-Delete.md`.

> **Migration: keine.** #378 ändert (R4 a) nur `web/` und Dokumente; keine Entität, Spalte oder Index wird
> berührt, der Berichtsvertrag mit `entries` stammt aus #376. #378 geht mit dem nächsten Main-Deploy bei Flag aus nach Prod und ist dort
> unsichtbar (Abschnitte 0 und 1).

**Status: Revision 3, freigegeben zur Umsetzung 2026-10-13 (zwei Sol-Runden).** R1–R4 und T-5 entschieden (Betreiber 2026-10-13,
Abschnitt 12); keine offenen Rückfragen.

### Revision 3 (13.10.) — zweite Sol-Runde und neue Betreiberentscheidung

Jeder Befund am Code im Worktree `EmotePurge-slots374` nachgeprüft.

| # | Befund (Sol, Runde 2) | Prüfung am Code | Entscheidung und Einarbeitung |
|---|---|---|---|
| T-1 [P1] | T0 heilt nicht, wenn der Abgleich vor dem Restore-Bericht läuft | stimmt: `EmoteSlotReconcilePlanner.PlanLiveGroup` ruft `BindByDate` → `ResumeArchivedSlots` → `BindByName` → Neuanlage → Archivierung unerklärter Slots (Schritt 7). Mit aktivem S1(A,t1) und live A(t9) legt der Abgleich Snew(A,t9) an und archiviert S1; der späte Bericht reaktiviert S1 provisorisch, der nächste Abgleich bindet A(t9) per Datum an Snew und archiviert S1 wieder | **Anlass für R4 (a)** (entschieden 2026-10-13, Abschnitt 12): mit erfolgreichem Lösch-Bericht ist S1 vor dem `ADD` archiviert; läuft dann der Abgleich vor dem Restore-Bericht, setzt D7 (`ResumeArchivedSlots`) S1 auf t9 fort, und der späte Bericht ist der vorgesehene No-op (AK 55 b). Unter (b) bräuchte T0 eine Zusammenführung zweier Slots samt Nutzung |
| T-2 [P2] | Hinkender REST plus identischer Retry lässt T0 erneut mutieren | stimmt für T0: nach dem Neu-Datieren autorisiert ein REST mit A(t1) über `BindByName` S1 wieder auf t1; derselbe Retry erfüllt die T0-Bedingung erneut | **entfällt mit R4 (a)** (kein T0): `RestoreSlots` reaktiviert nur archivierte Ziele, ein Retry auf einen aktiven Slot ist No-op (AK 55 a). Übrig bleibt die bekannte REST-Lag-Grenze für #379 (Abschnitt 11). Unter (b) bräuchte T0 eine dauerhafte Restore-Operations-Identität |
| T-3 [P2] | Zählregel verdeckt Kollateralverlust im Gleichstand | stimmt für 4.5 der Revision 1: A(t), B(t); A→C und B→A durch einen zweiten Editor; `REMOVE(A)` trifft B; Lesung C(t): erwartet 1, gelesen 1 | **angenommen.** Im Gleichstand gilt ein erwartetes Geschwister nur dann als vorhanden, wenn ein Eintrag bei t **seinen Basis-Alias** trägt; ein unbekannter Alias erfüllt keine Erwartung (4.5). Regressionsfall in 6.2 |
| T-4 [P2] | Übersprungene markierte Zeilen unterdrücken die Nachprüfung | stimmt: der Lese-Auslöser der Revision 1 verglich die Basis mit den **beabsichtigten** Entfernungen | **angenommen.** Auslöser rechnet mit den **tatsächlich gesendeten** `REMOVE`s (`done`/`unknown`): bleibt danach ein Basis-Eintrag von X übrig, wird gelesen (4.5). Test: A und B markiert, A `renamed` übersprungen, `REMOVE(B)` trifft C |
| T-5 [P2] | Klasse-3-Einträge ohne `slotId` umgehen T0 in jedem Kanal mit aktivem Set | stimmt: `EmoteService.ApplyEntries` überspringt Einträge ohne `slotId` (`entries.Where(e => e.SlotId is not null)`), für Delete wie Restore | **geändert angenommen, Betreiber 2026-10-13:** dieselbe Klasse wie die REST-Lag-Grenze — „der Slot ist auf unserer Seite noch aktiv, wenn der wieder hinzugefügte Eintrag zum ersten Mal abgeglichen wird"; Ergebnis ist das von AK 48 festgelegte Verhalten (neuer Slot bei 0, die alte Historie bleibt archiviert erhalten). Als gemeinsame bekannte Grenze an #379 (Abschnitt 11), kein eigener Mechanismus in #378 |
| T-6 [P2] | Parsebare v4-Daten sind nicht unbedingt gültige API-Daten | stimmt: `Date.parse` nimmt `"0"` und `2026-02-31T…` an; die Api bindet `AddedAt` als `DateTimeOffset?` (`SyncInSetEntryRequest`), `System.Text.Json` lehnt beides ab → `400 invalid_request_body`, Retries reparieren nichts | **angenommen.** Strenge Prüfung jedes Eintragsziels **vor** jedem `ADD`/`REMOVE`: ISO 8601 mit Datum, Uhrzeit und ausdrücklicher Zone (`Z` oder `±hh:mm`), gültige Kalenderfelder (inkl. Schaltjahr), Nachkommastellen beliebig, Zeichenkette wörtlich weitergegeben (4.3, 4.8). Ungültig in der Datei → Datei abgewiesen; ungültig an einer Karte (Server-Wert, sollte nie vorkommen) → die Karte fällt fail-closed aus dem Lauf wie eine provisorische (`pendingSync`, 4.9); Tests 6.1/6.3/6.4 |
| B-1 | **Betreiberentscheidung 2026-10-13:** nach #378 nennt der Lösch-Dialog einer Slot-Karte mit Geschwister nur ihren eigenen Alias („1 Emote löschen?"); die Regel „der Titel zählt die gelisteten Namen" bleibt, nur die Liste wird kürzer; Flag aus und Emote-Modus bleiben wie in #377 | — | 4.9, A9, Tests 6.4, DECISIONS |

### Revision 2 (13.10.) — was sich geändert hat

| Änderung | beantwortet |
|---|---|
| R1 entschieden (a): Klasse-3-Karten je Eintrag per Alias; alle „nur nach R1 a"-Vorbehalte aufgelöst | Betreiber 2026-10-13 |
| R2 entschieden (a): AK 36 per Node-Skript mit der Konstante des Branches gegen das Sonden-Set (falls schreibbar, sonst neues Wegwerf-Set) | Betreiber 2026-10-13 |
| R3 entschieden: Live-Lauf als Paket freigegeben, jeder einzelne 7TV-Schreibzugriff weiter vorher im Chat vorgelegt; Ausgangslage: `ome44` fehlt im Set von `olaf_olaf_son` und wird zuerst wieder hinzugefügt, `hob44` steht mit `addedAt` `2026-10-13T08:00:14.902Z` (7.0) | Betreiber 2026-10-13 |
| R4 entschieden (b) — *in Revision 3 wieder offen, siehe dort*: T0 fest; Variante (a) gestrichen; T0-Tests ausgeschrieben inkl. Nicht-Anwendungsfällen und Flag aus (6.0); Live-Schritt 7.5a für T0 | Betreiber 2026-10-13 |
| Vorbereitung in §10: Worktree `EmotePurge-slots374`, Branch von `origin/main` nach dem Merge von #404 (kein eigener Worktree, kein Branch von `feat/377`); Redis bleibt `:6394` | Orchestrator 2026-10-13 |
| Aufwand neu (§14) | Folge |

### Revision 1: Sol-Befunde

Jeder Befund am Code im Worktree `EmotePurge-slots` nachgeprüft (Pfade relativ dazu).

| # | Befund (Sol) | Prüfung am Code | Entscheidung und Einarbeitung |
|---|---|---|---|
| S1 [P1] | Der unveränderte Restore-Filter verwirft den gelöschten Alias, wenn ein Geschwister überlebt hat | stimmt: `already-present-filter.ts` `missingAliases` (Z. ~455–470) liest jeden Live-Alias der Id, den die Zeile nicht nennt, als fremd (`foreignEntry`) und gibt `[]` zurück; Zeile `[hob44]` gegen live `[ome44]` → `hob44` wird als „schon da" gezählt, kein `ADD` — an beiden Restore-Eingängen | **angenommen.** Neuer Eintrags-Modus des Filters für Zeilen mit Eintragsziel (4.7 a): Geschwister derselben Id sind erlaubt, geprüft werden nur „eigener Alias schon da" und „Name von einem anderen Emote belegt"; Id-Zeilen behalten die heutigen Regeln. Tests für Dock, Datei, Teil-Retry und unerwartete Entfernung (6.3) |
| S2 [P1] | Das Überschreiben der Vergleichsbasis vergisst einen Kollateralverlust aus einem früheren Versuch | stimmt: 4.4 Schritt 5 der Revision 0 setzte die Basis bei jedem „weiter" neu; A(t1), B(t2), C(t3), A und C markiert, `REMOVE(A)` trifft B → die Lesung vor C zeigt nur t1/t3, B ist vergessen, nach Abzug von t1/t3 bleibt nichts zu prüfen | **angenommen.** Die Basis je Emote **akkumuliert** über alle Lesungen des Laufs und wird nie überschrieben (4.4 Schritt 5); zusätzlich prüft jede neue Lesung sofort, ob ein Eintrag der bisherigen Basis fehlt, der nicht zu einem eigenen Versuch gehört (frühe Erkennung, gleiche Meldung). Regressionstest mit zwei markierten Slots und einem nicht markierten Geschwister (6.2) |
| S3 [P1] | Restore setzt voraus, dass der Lösch-Bericht den Slot archiviert hat | stimmt: `restoreOffered` (`delete-progress-section.ts` Z. ~309–312) hängt nicht am Berichtsstand; `EmoteService.RestoreSlots` (Z. ~821) reaktiviert nur archivierte Ziele; `EmoteSlotReconcilePlanner.PlanLiveGroup` ruft `ResumeArchivedSlots` (D7) **vor** dem Archivieren unerklärter Slots (Schritt 7), und D7 überspringt, solange ein aktiver gleichnamiger Slot existiert → S1(A,t1) aktiv blockiert D7, `BindByName` bindet nur provisorische Slots, A(t9) wird ein neuer Slot bei 0 | **angenommen, in geänderter Form:** Rückfrage **R4**. Empfohlen (b): kleine Backend-Regel — ein Restore-Eintrag, dessen Ziel noch **aktiv und autoritativ bei genau der gemeldeten Generation** steht, wird provisorisch neu datiert (statt No-op), damit REST den neuen Eintrag über den Namen an denselben Slot bindet (4.7 b, Task T0). Deckt fehlgeschlagenen Lösch-Bericht, Datei-Restore und eine Reaktivierung durch einen hinkenden REST vor dem Restore. Integrationsfall gescheiterter Lösch-Bericht → `ADD` → Restore-Bericht → REST (6.0). Migration bleibt „keine" |
| S4 [P2] | Die Seitenprüfung der Lesung ignoriert die Generation | stimmt: `memberKey` in `seven-tv-set-entries.ts` (Z. ~104) ist `(id, alias)`; die Bestätigungslesung vergleicht nur diese Menge und liefert die Daten des ersten Durchgangs | **angenommen.** Bei Lesungen mit Eintragsdatum gehört das normalisierte `addedAt` (ms) in den Mitgliedsschlüssel der Konsistenzprüfungen; Abweichung → `complete: false` (fail-closed). Bisherige Lesungen unverändert (4.1). Test: Alias-Tausch zwischen Haupt- und Bestätigungsdurchgang |
| S5 [P2] | Die Klärung nur über den Zeitpunkt bricht D4-Gleichstände | stimmt für 4.5 der Revision 0: bei A(t), B(t) bliebe eine verlorene Antwort auf `REMOVE(A)` `unknown`, weil B(t) noch da ist, und der Abzug von (X, t) striche B aus der Erwartung | **angenommen.** Klärung und Erwartung je **(Zeitpunkt, Anzahl)** mit Alias als Zusatz: eine `unknown`-Zeile ist `done`, wenn zum Zeitpunkt t kein Eintrag mit ihrem Alias mehr steht **und** die Zahl der Einträge bei t gegenüber der Basis gesunken ist; die Nachprüfung vergleicht je Zeitpunkt die erwartete Anzahl (Basis minus eigene `REMOVE`s) mit der gelesenen (4.5). Tests mit gleichen Zeitpunkten (6.2) |
| S6 [P2] | Version 3 lässt ältere Leser still die falsche Historie wiederherstellen | stimmt: der heutige Parser (`purge-run-export.ts` Z. ~309–315) übernimmt nur bekannte Felder; ein id-only-Restore un-archiviert nur das Emote, und D7 wählt den **zuletzt** archivierten gleichnamigen Slot (`ResumeArchivedSlots`, `OrderByDescending(ArchivedAt)`), also womöglich S2 statt S1 | **angenommen.** Datei mit mindestens einem Eintragsziel → `PURGE_RUN_FORMAT_VERSION` 4, ältere Leser weisen sie ab; Dateien ohne Ziel bleiben Version 3 (Flag aus bytegleich); der Parser liest 1–4; ein **fehlerhaftes** Ziel in einer v4-Datei weist die Datei ab, statt still herabzustufen (4.8) |
| S7 [P2] | Das Pauschal-Gate „bestehende Erwartungen unverändert" verbietet den gewollten Übergang | stimmt: `usage-stats-page.spec.ts` (Z. ~8510–8560) verlangt für eine markierte von zwei Slot-Karten einen Eintrag mit beiden Aliasen und `confirmationNames` sowie zwei frei werdende Plätze — genau das ersetzt #378 | **angenommen.** Gate auf Flag-aus-Verhalten und wirklich unveränderte Verträge begrenzt; die #377-Übergangsfälle mit Slot-Karten sind namentlich zum Ersetzen freigegeben, je ein Emote-Modus-Wächter bleibt (Abschnitt 1, 6.4, T4) |

Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) (§3 D3, D4, D7, D11, D21, D27, D32, D33,
O3; §5.7 in der Kind-6-Fassung; §9.1 und §9.3; §13 AK 36–38, 40, 42 Restore-Hälfte, 46 Restore-Fluss, 51, 55 Client-Hälfte; §15; §16 Kind 8),
Issue #378 mit den Übergaben aus #376 und #374 (bindend), PR #404 („Notes for the next children"), [Plan-376](Plan-376-Emote-Slots-Read-API.md)
§4 und §10, [Plan-377](Plan-377-Emote-Slots-Cards.md) §2.3, §2.6 und §8, [Plan-374](Plan-374-Emote-Slots-EventAPI-Deltas.md) §12, DECISIONS
#365 („Child 6", „Child 7"). Formatvorlage: Plan-374/Plan-377.

**Was dieser Plan trägt:** nur, was Spec und Übergaben offen lassen — Migrationsbefund, Flag-Matrix samt Nachweis „Flag aus = heute", die
Zeilenarten des Lauf-Eingangs, die Bindungsregel je Versuch als Entscheidungstabelle, Nachlese und D33-Nachprüfung, die Bericht- und
Restore-Körper, Seite und Dock, Grenzfälle, Tests, Live-Prüfung, Aufgabenfolge. **Kein Code:** Typ- und Funktionsnamen stehen nur als Form.
D0–D33 und die Betreiberentscheidungen aus #371–#377 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **[A]** = Abweichung vom Spec-Wortlaut, vom Betreiber bestätigt (R1 und R4, 2026-10-13).
**[P]** = Präzisierung, die die Spec offen lässt, ohne ihr zu widersprechen. **R*n*** = Rückfrage an den Betreiber (Abschnitt 12).

## 0. Migrationsbefund: **keine Migration**

#378 braucht weder Schema- noch Index- noch Daten-SQL. Begründung:

1. **Kein Backend-Code (R4 a).** Den Vertrag liefert #376: `SyncInSetRequest.Entries` mit `{ sevenTvEmoteId, alias?, slotId?, addedAt? }`
   (`SevenTvEndpoints.cs`, Formprüfung `TryMapSyncInSetEntries`), die Slot-Semantik in `EmoteService` (`ApplyEntries`, `ResolveSlot`,
   `DeleteSlots`/`RestoreSlots`), `/totals` mit `slotId`/`firstSeenAt`/`firstSeenAtFromRest`. #378 ändert `web/` (TypeScript, i18n, E2E) und
   Dokumente.
2. **Keine Entität, keine Spalte, kein Index berührt.** Die Slot-Spalten (`EmoteSlots`, `UsageStats.SlotId`) stehen seit M1/M2; #378 liest
   und meldet sie nur über bestehende Endpunkte. Die einzige Formatänderung betrifft eine **Client-Datei** (Lösch-Protokoll, 4.8, additiv) —
   keine Datenbank.
3. **Gate:** `git diff --stat origin/main -- src tests` ist am Ende leer; ein Probe-`dotnet ef migrations add` ist damit überflüssig (T7). Fällt
   dem Umsetzer doch eine Backend-Lücke auf, bricht er ab und meldet sie — dann wäre das ein eigener Plan-Nachtrag.

**Folge für den Betreiber:** #378 geht mit dem nächsten Main-Deploy nach dem Merge von #377 bei Flag aus nach Prod, ohne Fenster und ohne
Handgriff an der Datenbank. Vorsicht nur wie immer (Lehre aus #391): vor dem Deploy `dotnet ef migrations list` gegen Prod — bringt `main`
inzwischen M3 aus #375 mit, ist das dessen Fenster, nicht #378s.

## 1. Flag-Matrix und „Flag aus = heute"

Das Frontend kennt das Flag `SevenTv:SlotReconcile:Enabled` nicht; es liest den Modus aus der Antwort (Plan-377 F2/F4): **mit Flag aus trägt
keine `/totals`-Zeile eine `slotId`**, also gibt es keine Eintrags-Zeile (Abschnitt 3) und jeder Löschlauf ist ein Lauf je 7TV-Id wie heute.

| Gegenstand | Flag aus (Prod bis #379) | Flag an |
|---|---|---|
| Lauf-Eingang der Nutzungsseite | eine Id-Zeile je markierter Karte (eine Karte je Id) — bitgleich zu #377 | Eintrags-Zeile je markierter datierter Slot-Karte (und je datierter Klasse-3-Karte, R1 a); Id-Zeilen für Emote-Modus-Karten |
| `REMOVE` an 7TV | bisherige Mutation ohne `alias`, gleiche Bytes | Eintrags-Zeilen: Slot-Mutation mit `alias` (4.2); Id-Zeilen wie heute |
| Live-Lesungen des Sets | bisherige Abfrage (ohne `addedAt`), gleiche Bytes | Lauf mit Eintrags-Zeilen liest mit `addedAt` (4.1) |
| `beforeStep`-Hook | **nicht gesetzt** (kein Umweg durch den Hook) | gesetzt, sobald die Queue eine Eintrags-Zeile hat; Id-Zeilen gehen sofort durch |
| Nachlese nach dem Lauf | nur bei `unknown`-Zeilen, wie heute | zusätzlich D33-Nachprüfung (4.5) |
| `sync-deleted`/`sync-restored`-Körper | ohne Feld `entries` (weggelassen, nicht leer) | `entries` je Eintrags-Zeile; Ids ohne Eintrag wie heute (gemischter Körper erlaubt, Spec 5.7) |
| Queue-Schlüssel, `doneKeys`, `deleted`-Output | 7TV-Id | Id-Zeilen 7TV-Id, Eintrags-Zeilen `${sevenTvEmoteId}#${slotKey}` |
| Lösch-Protokoll (Datei) | Version 3, Zeilen ohne neue Felder, gleiche Bytes | mit Eintrags-Zeilen Version 4, Zeilen mit `slotId`/`addedAt` (4.8) |
| Restore-Filter | heutige Regeln | Zeilen mit Eintragsziel im Eintrags-Modus (4.7 a), Id-Zeilen heutige Regeln |
| Restore-Eingang am Dock | wie heute (angeboten, sobald der Lauf abgeschlossen ist) | Läufe mit `done`-Eintrags-Zeilen: Restore erst nach erfolgreichem Lösch-Bericht (4.7 b, R4 a); Läufe nur aus Id-Zeilen wie heute |
| Dock | keine neue Zeile | `pendingSync`-Zeile, Übersprung-Gründe in den Lauf-Zeilen, `unexpectedRemoval` mit Restore-Angebot |
| Vote-Seite, Tag-Entfernung, Import-Replace, Undo | unverändert | unverändert (D3: je Id, ohne `alias`) |

- **[E1] Zwei Mutationskonstanten statt einer mit `$alias`.** Die Spec (§9.3) lässt die eine `REMOVE`-Mutation `$alias: String` deklarieren
  und Vote-Seite/Import-Replace `alias: null` senden. Semantisch gleich (`alias` fehlt oder ist `null` → 7TV nimmt jeden Eintrag der Id,
  Sonde 5 Zweig A), aber jeder Aufrufer schickte andere Bytes als heute. Deshalb bleibt die bisherige Konstante unverändert (Vote-Seite,
  Import-Replace, Undo, Id-Zeilen), und eine **zweite** Konstante „Slot-Entfernung" trägt `alias` als **Pflicht**variable (`String!`), damit
  ein Eintrags-Request nie versehentlich ohne Alias — also „alle Einträge" — hinausgeht. Präzisierung [P], kein Verhaltensunterschied.
- **[E2] `addedAt` in der Live-Lesung nur auf Anforderung.** `loadSevenTvSetEntries` bekommt eine Option „mit Eintragsdatum"; ohne sie bleibt
  die Abfrage bytegleich. Grund: dieselbe Lesung sperrt bei Fehlschlag den Löschlauf, prüft Import und Undo — ein unbemerkter Fehler im neuen
  Feld träfe sonst Prod bei Flag aus.
- **Nachweis „Flag aus = heute":** (1) alle bestehenden Vitest-Fälle, die Flag-aus-Verhalten oder einen von #378 nicht berührten Vertrag
  beschreiben (Id-Zeilen, Emote-Modus, Vote-Seite, Tag-Entfernung, Import, Undo, Protokoll ohne Ziel), laufen ohne inhaltliche Änderung —
  erlaubt sind nur Ergänzungen von Fixture-Helfern um neue optionale Felder; **muss ein solcher Fall seine Erwartung ändern, bricht der Umsetzer
  ab und meldet es**. **Ausdrücklich zum Ersetzen freigegeben (S7)** sind die #377-Übergangsfälle mit **Slot-Karten** in
  `usage-stats-page.spec.ts` (ein Lauf-Eintrag je Id mit beiden Aliasen und `confirmationNames`, die Filter-Varianten dazu, „beide Slots frei" bei
  einer markierten Karte, „zwei statt vier" mit Slot-Karten) — sie werden durch die Eintrags-Fälle aus 6.4 ersetzt; je ein gleichwertiger Fall
  mit einer **Emote-Modus**-Duplikatkarte (`×2`) bleibt bzw. kommt als Wächter dazu. Jede weitere Erwartungsänderung: Abbruch und Meldung;
  (2) neue Wächter-Fälle: Lauf aus Id-Zeilen sendet exakt die bisherige Mutation und Variablen, keinen Hook, die bisherige Lesung, einen
  Bericht ohne `entries`, ein Protokoll ohne neue Felder; (3) die ganze E2E-Suite mit den Aus-Vorgaben der Mocks (Plan-377 F5) plus ein
  neuer Flag-aus-Löschlauf in `usage-atlas`; (4) Live-Phase A (Abschnitt 7).
- **Umschaltfenster:** Wird das Flag zwischen Seitenladen und Bericht abgeschaltet (Rollback), ignoriert der Server `entries` und archiviert je
  Id auf Emote-Ebene — ein Mehr-Slot-Emote verlöre im DB-Bild auch den nicht gelöschten Slot, bis REST (bei Flag aus emote-weit) es wieder
  aktiv setzt. Hingenommen, nur bei einem Flag-Rollback mitten im Lauf; im DECISIONS-Absatz benannt.

## 2. Umfang

### 2.1 Drin (Spec §16 Zeile 8, Issue #378)

1. **Lauf je Slot:** Eintrags-Zeilen mit `alias`, Slot-Mutation, Queue-Schlüssel je Eintrag, provisorische Zeilen aus dem Lauf (D11).
2. **Bindung je Versuch** (`beforeStep`, D21, AK 38, 51): frische Lesung vor jedem Versuch für Emotes mit ≥ 2 Live-Einträgen (Retries
   eingeschlossen), sonst Wiederverwendung bis 5 s; `renamed`/`departed` und Lesefehler überspringen die Zeile ohne Request.
3. **Nachlese und Nachprüfung** (D33, AK 40): `unknown`-Zeilen je Eintrag klären; nicht gemeldete Einträge desselben Emotes prüfen,
   `unexpectedRemoval` mit Alias und Restore-Angebot.
4. **Berichte** (D32, AK 37): `sync-deleted` mit `entries` samt `addedAt`-Generation, Retry mit identischem Körper; **Restore** (AK 42, 46, 55):
   Queue hält `slotId` und ursprüngliches `addedAt`, `sync-restored` sendet sie je `doneKey`, Retry identisch.
5. **Lösch-Protokoll:** Eintrags-Zeilen tragen `slotId`/`addedAt`, solche Dateien als Version 4; Restore aus der Datei sendet sie mit (4.8).
5a. **Restore-Filter im Eintrags-Modus** (S1) und die Sperre des exakten Restores bis zum erfolgreichen Lösch-Bericht (4.7 b, R4 a).
6. **Seite:** `selectedForDelete` je Karte, `pendingSync`-Zeile, `pendingRemovalSlots` (D27), `onDeleted` je Schlüssel samt Geschwister-
   Hinweisen, Rückbau des Übergangs aus #377 (Bestätigungsnamen je Id) für Eintrags-Zeilen.
7. **Live-Nachweis unserer Mutation** (AK 36) und Live-Prüfung von AK 37, 38 (renamed), 42, 46 (Abschnitt 7).
8. Vitest, E2E, Mocks; DECISIONS-Absatz „Child 8"; Spec-Nachträge §9.3; Statuszeilen.

### 2.2 Draußen (mit Besitzer)

| Gehört zu | Inhalt |
|---|---|
| Kind 9 (#379) | Flag auf Prod; Abnahmelauf über AK 34–42, 46–56; `docs/Architectur.md` Modul D (Mutationsbeispiel, Bericht je Slot) und `docs/Operations.md` final; Rückbau der Aus-Zweige — **nicht** der Id-Zeilen (Emote-Modus, Vote-Seite, Import, Undo brauchen sie dauerhaft) |
| Kind 5 (#375) | nichts aus #378; Backfill berührt den Löschlauf nicht |
| unverändert | Vote-Detail-Löschung je Id (D3), Tag-Entfernung (#201 T-C), Import-Replace und Undo (je Id, eigene Konstante), Server-Semantik der Berichte (#376), Fremdkanal-Raster |
| Backend | nichts (Abschnitt 0) |
| Kind 9 (#379) bzw. eigene Folge | **REST-Lag nach einem Restore:** autorisiert ein hinkender 7TV-REST (SevenTV#81) den provisorisch reaktivierten Slot mit dem alten `added_at`, blockiert er beim nächsten frischen REST die D7-Fortsetzung (aktiver gleichnamiger Slot) — dieselbe Klasse wie S3, aber im Abgleich, nicht im Löschlauf; als bekannte Grenze an #379 übergeben (Abschnitt 11) |

### 2.3 Spec ↔ Code ↔ Übergaben: gefundene Abweichungen und Präzisierungen

| # | Stelle | Befund | Behandlung |
|---|---|---|---|
| A1 | Spec §9.3 „nur Zeilen mit `addedAtFromRest` erreichen den Lauf" | Klasse-3-Karten (nicht gezählte Live-Einträge eines nicht aktiven Sets, je Eintrag nur in einer Slot-Antwort, Plan-377 F2) haben `firstSeenAtFromRest = false` und keine `slotId` — wörtlich gelesen fielen sie unter `pendingSync`, obwohl sie nie „abgeglichen" werden; heute sind sie löschbar | **R1 entschieden (a)**, Betreiber 2026-10-13 |
| A2 | Spec §9.3 eine Mutation mit `$alias`, Vote/Import senden `alias: null` | bytegleich nur mit eigener Konstante | [E1], [P] |
| A3 | Spec §9.3 Live-Liste „`readLiveAliasesFromActiveSet`/`…FromSet`" mit `addedAt` | `loadSevenTvSetEntries` liest kein `addedAt`; die Lesung vor dem Lauf läuft nur in der aktiven Ansicht | Option [E2]; die Bindung liest im Lauf selbst (4.4), unabhängig von der Vor-Lauf-Lesung |
| A4 | AK 38 „Transport-Retry nach Umbenennung → skip" | Einen Transport-Retry gibt es im Löschlauf nicht: ein verlorener Request endet `unknown` (`transportLossIsUnknown`), ein HTTP-429 und ein GQL-Ratelimit werden nach Pause wiederholt, und der Hook wird vor jedem dieser Versuche erneut gefragt (`runGatedOne`) | [P] AK 38 wird über den 429-Retry geprüft; `unknown` klärt die Nachlese |
| A5 | Spec §9.3 „`toDeleteQueue` schlüsselt `${sevenTvEmoteId}#${slotId}`" | Klasse 3 hat keine `slotId` (Plan-377 §8) | Schlüssel `${sevenTvEmoteId}#${slotKey}` — für Slot-Karten ist `slotKey = slotId`, also wörtlich die Spec-Form [P] |
| A6 | Spec §9.3 Nachprüfung „jede nicht gemeldete autoritative **Zeile** des Emotes" | Der Dienst kennt nur die markierten Zeilen, nicht die nicht markierten Geschwisterkarten | Vergleichsbasis = Einträge des Emotes in der letzten Lesung vor seinem `REMOVE` (4.5) [P]; umfasst jede Zeile und zusätzlich Einträge, die ein anderer Editor nach dem Laden hinzugefügt hat |
| A7 | #376-Übergabe P3-2 „Zeilen mit `slotId` auch provisorisch als Eintrag melden" | Nach Spec §9.3/D11 erreichen provisorische Zeilen den Lauf nie; damit gibt es keinen provisorischen Eintrag im Bericht | erledigt durch den Ausschluss (4.9); im PR-Text an #376 zurückgemeldet |
| A8 | Spec nennt nur `renamedSinceLoad`, `pendingSync`, `unexpectedRemoval` | `departed`, aliasloser Live-Eintrag, Lesefehler, fehlgeschlagene Nachprüfung brauchen Texte | neue Schlüssel `departedSinceLoad`, `noAlias`, `postCheckUnavailable`; Lesefehler nutzt die vorhandenen `massDelete.memberRead.unavailable`/`.truncated` [P] |
| A9 | #377-Übergabe: Bestätigungsnamen je Id („#378 entscheidet") | Mit Löschung je Eintrag nennt jede Eintrags-Zeile nur ihren Alias; Id-Zeilen setzen das Feld schon heute nie | Seite setzt `confirmationNames` nicht mehr; Panel-Fähigkeit bleibt (generisch, getestet) [E] |
| A10 | Spec §9.3 „`pendingSync` aus dem Lauf genommen" | Ort offen (vor der Bestätigung oder als übersprungene Lauf-Zeile) | vor der Bestätigung, als Dock-Zeile (4.9) [E] |
| A11 | Spec §9.3 Restore „sendet dieselben Felder je `doneKey`" | Der Restore-Filter (`missingAliases`) hält jeden nicht genannten Live-Alias der Id für fremd; ein Eintrags-Restore neben einem überlebenden Geschwister ginge nie hinaus (S1) | Eintrags-Modus des Filters (4.7 a) [P] |
| A12 | Spec 5.7/AK 46: Restore setzt die Historie auf derselben Karte fort | Steht der Slot beim Restore noch aktiv (Lösch-Bericht gescheitert, Klasse 3 im Fan-out, hinkender REST), legt der Abgleich für den neuen Eintrag einen neuen Slot an (AK 48), und der Restore-Bericht ist No-op (S3, T-1) | **R4 (a), Betreiber 2026-10-13:** exakter Restore erst nach erfolgreichem Lösch-Bericht; die übrigen Fälle als bekannte Grenze an #379 |

## 3. Begriffe: Zeilenarten des Lauf-Eingangs

| Art | Herkunft auf der Nutzungsseite | Lauf | Bericht |
|---|---|---|---|
| **Id-Zeile** | Emote-Modus-Karte (`slotId null`, kein `live:`-Schlüssel); gruppierte Klasse 3 (Emote-Modus-Antwort); Vote-Seite; Tag-Entfernung | eine Zeile je 7TV-Id, Schlüssel = Id, bisherige Mutation ohne Alias, Aliase für das Protokoll wie heute | Id ohne Eintrag (Altsemantik plus B1) |
| **Eintrags-Zeile** | datierte Slot-Karte (`slotId` gesetzt, `firstSeenAtFromRest`), aktive oder nicht aktive Ansicht, `'live'`; Klasse-3-Karte je Eintrag mit Datum (R1 a) | eine Zeile je Karte, Schlüssel `${id}#${slotKey}`, Slot-Mutation mit genau ihrem Alias, Bindung je Versuch | Eintrag `{ sevenTvEmoteId, alias, slotId (Klasse 3: fehlt), addedAt }` |
| **ausgeschlossen** | provisorische Slot-Karte (`firstSeenAtFromRest = false`); undatierte Klasse-3-Karte (`live:<id>##<name>`); `'left'`-Karte (wie heute) | erreicht den Lauf nicht | — |

Das **Eintragsziel** einer Eintrags-Zeile hat vier Teile: `slotId` (nullable, nur Klasse 3 ohne), `alias` (= Kartenname), `addedAt` (die
Wire-Zeichenkette, aus der gebunden und gemeldet wird: bei Slot-Karten `firstSeenAt` der `/totals`-Zeile **wörtlich**, bei Klasse 3 das
`addedAt` des Live-Eintrags) und `cardKey` (= `slotKey` der Karte; bildet den Queue-Schlüssel und führt `onDeleted` zur Karte zurück).

## 4. Verträge

### 4.1 Live-Lesung mit Datum (`core/seven-tv/seven-tv-set-entries.ts`) — [E2]

- Neue Option „mit Eintragsdatum". Gesetzt, wählt die Abfrage zusätzlich `addedAt` je Eintrag (v4 GQL, belegt durch Sonde S1); das Ergebnis
  bekommt zusätzlich eine Liste der Einträge je 7TV-Id mit `alias` (wie gelesen, auch `null`/leer) und `addedAt` (Wire-Zeichenkette oder
  `null`). Bestehende Felder (`aliasesById`, `aliaslessIds`, `defaultNameById`, `animatedById`, `occupiedSlots`, `complete`) bleiben unverändert
  in Bedeutung und Befüllung.
- Ohne Option: Abfragetext und Ergebnis bytegleich wie heute; die neue Liste bleibt leer bzw. fehlt.
- Die Konsistenzprüfungen (`complete`) bleiben. **Mit Option (S4)** gehört das normalisierte `addedAt` (ms; `null` als eigener Wert) in den
  Mitgliedsschlüssel aller Konsistenzprüfungen (Wiederholung über Seiten, Bestätigungsdurchgang): ein Alias-Tausch zwischen zwei Einträgen
  derselben Id zwischen Haupt- und Bestätigungsdurchgang lässt die `(id, alias)`-Menge gleich, verschiebt aber die Generationen — das ergibt
  `complete: false`, und der Hook überspringt (fail-closed). Ohne Option bleibt der Schlüssel `(id, alias)`, bytegleich.
- Zeitvergleich überall in **Millisekunden über `Date.parse`**, nie als Zeichenkette (die Wire-Wege drucken verschieden viele Nachkommastellen,
  Plan-377 2.4). Ein nicht parsebares Datum gilt als `null`.

### 4.2 Mutationen (`core/seven-tv/seven-tv-delete.service.ts`) — [E1]

- Die bestehende `REMOVE`-Konstante bleibt **unverändert** (Text und `resultPath`); ihre Doku nennt, dass sie jeden Eintrag der Id nimmt und
  von Id-Zeilen, Vote-Seite, Import-Replace und Undo benutzt wird.
- Neue Konstante „Slot-Entfernung": `removeEmote(id: { emoteId, alias })` mit `$alias: String!`, gleicher `resultPath`. Nur Eintrags-Zeilen
  benutzen sie; `buildRequest` wählt die Konstante allein nach der Zeilenart.
- Gesendet wird **immer der Zeilen-Alias** — der Hook (4.4) lässt einen Versuch nur durch, wenn die frische Lesung genau diesen Alias am
  Eintrag zeigt („gesendet wird nur der soeben verifizierte Alias", D21).

### 4.3 Lauf-Eingang und Queue

- `DeletableEmote` (Panel) und `DeleteQueueEmote` (Dienst) bekommen das optionale Eintragsziel (Abschnitt 3). Ohne Ziel: Id-Zeile wie heute.
- `RunQueueEmote` (Engine) bekommt dasselbe optionale Feld, damit Ergebniszeilen, Protokoll, Restore-Eingang und Retry es tragen; die Engine
  liest es nicht (Doku an `key` aktualisieren: „Delete: Id-Zeile `sevenTvEmoteId`, Eintrags-Zeile `${sevenTvEmoteId}#${cardKey}`").
- **`toDeleteQueue`:** Id-Zeilen wie heute (je Id eine Zeile, Aliase vereinigt). Eintrags-Zeilen eine je `cardKey`; `aliases = [alias]` — sie
  werden **nie** mit Live-Aliasen der Id angereichert (das Protokoll und ein Restore brächten sonst Geschwister zurück, die gar nicht gelöscht
  wurden). Doppelter `cardKey` → zweite Zeile verworfen. Id-Zeile und Eintrags-Zeilen derselben Id im selben Aufruf (von der Seite nicht
  erzeugbar) → die Eintrags-Zeilen gehen in der Id-Zeile auf, ihre Aliase werden vereinigt [E] (das `REMOVE` ohne Alias nimmt sie ohnehin).
- **`toDeleteQueueEmotes` (`delete-flow.ts`):** reicht das Ziel durch und erweitert die Aliase nur für Id-Zeilen aus der Vor-Lauf-Lesung (heute).
- **Strenge Datumsprüfung jedes Eintragsziels (T-6)** — eine reine Hilfsfunktion in `core/seven-tv/`: gültig nur ISO 8601 mit Datum, Uhrzeit und
  ausdrücklicher Zone (`Z` oder `±hh:mm`), Kalenderfelder im gültigen Bereich (Monatslängen, Schaltjahre), Uhrzeitfelder im gültigen Bereich,
  Nachkommastellen in beliebiger Zahl. Die Zeichenkette wird nie umgeformt, sondern wörtlich weitergegeben. Angewendet beim Bilden der
  Eintrags-Zeilen auf der Seite (4.9), beim Lesen einer Protokolldatei (4.8) und noch einmal beim Bau des Restore-Queues, **bevor** ein `ADD`
  hinausgeht — damit kein Lauf 7TV ändert und dann an einem Bericht scheitert, den die Api als `400 invalid_request_body` abweist.
- **Vor-Lauf-Lesung (`readLiveAliasesThenDelete`) unverändert:** bleibt bei der bisherigen Abfrage und der Sperre „Id ganz aus dem Set → ganzer
  Lauf blockiert" (#229, „gezeigt = gelöscht"). Ein einzelner verschwundener oder umbenannter Eintrag einer noch vorhandenen Id ist kein
  Blockgrund, sondern ein Übersprung je Zeile (4.4) [E].

### 4.4 Bindung je Versuch (`beforeStep`, D21, AK 38, 51)

**Zustand je Lauf** (in der Operation, nicht auf dem Engine-Objekt; Muster `SevenTvUndoService.createOperation`): die gehaltene Lesung (mit
Eintragsdatum), ihr Startzeitpunkt, ein Zähler aufeinanderfolgender Lesefehler, je Emote die **Vergleichsbasis** für 4.5.

**Hook nur bei Eintrags-Zeilen.** Die Operation setzt `beforeStep` nur, wenn die Queue mindestens eine Eintrags-Zeile enthält; Id-Zeilen
antworten darin sofort „weiter" ohne Lesung. Ein Lauf nur aus Id-Zeilen hat keinen Hook (Flag aus bitgleich).

**Vor jedem Versuch einer Eintrags-Zeile (X, Alias a, Zeitpunkt t), in dieser Reihenfolge:**

1. Drei Lesefehler in Folge → überspringen ohne Lesung (Grund wie der letzte Lesefehler); Id-Zeilen laufen weiter. Spiegel von Undo E19
   (`MAX_CONSECUTIVE_RECHECK_FAILURES`).
2. **Frisch lesen**, wenn (a) noch keine Lesung gehalten wird, oder (b) die gehaltene Lesung beim Versuch **älter als 5 s** ist (gemessen ab
   ihrem Request-Start — konservativ), oder (c) die gehaltene Lesung für X **≥ 2 Einträge** zeigt (D21/D33, AK 51 — auch wenn sie jünger als
   5 s ist). Sonst die gehaltene Lesung verwenden.
3. Lesung mit eigener Frist (20 s, eigene Konstante wie `RECHECK_READ_TIMEOUT_MS` im Undo); Fehlschlag oder Zeitablauf → Zähler +1, überspringen
   mit `massDelete.memberRead.unavailable`; unvollständig → Zähler +1, überspringen mit `massDelete.memberRead.truncated`; Erfolg → Zähler 0,
   Lesung wird die gehaltene.
4. **Klassifizieren** gegen die gehaltene Lesung — Einträge von X mit Zeitpunkt t (ms):

| Live-Einträge von X mit Zeitpunkt t | Ergebnis | Dock-Text der Zeile |
|---|---|---|
| keiner | überspringen, `departed` | `usageStats.delete.departedSinceLoad` ({{name}}) |
| genau einer, Alias = a (ordinal) | **weiter**, `REMOVE(X, a)` | — |
| genau einer, anderer nicht leerer Alias b | überspringen, `renamed` | `usageStats.delete.renamedSinceLoad` ({{name}} → {{newName}}) |
| genau einer, Alias `null`/leer | überspringen, fail-closed [E] — ob `removeEmote` mit dem Standardnamen einen aliaslosen Eintrag trifft, ist nicht belegt, und ohne Alias nähme 7TV alle | `usageStats.delete.noAlias` ({{name}}) |
| mehrere (D4-Gleichstand), einer mit Alias a | weiter, `REMOVE(X, a)` | — |
| mehrere, keiner mit Alias a | überspringen, `departed` (D4: Umbenennung im Gleichstand = Entfernen + Hinzufügen) | `departedSinceLoad` |

5. **Vergleichsbasis (S2):** je Emote X eine Menge von Einträgen `(Zeitpunkt, Alias)`, die über **alle** Lesungen des Laufs **akkumuliert**
   und nie überschrieben wird — jede Lesung, die X enthält, fügt ihre Einträge von X hinzu (ein schon bekannter Zeitpunkt mit gleichem Alias wird
   nicht doppelt gezählt; ein bekannter Zeitpunkt mit neuem Alias ist ein Umbenennungs-Nachtrag und ersetzt den Alias dieses Eintrags). Dazu je
   X die Liste der eigenen gesendeten `REMOVE`s `(t, Alias)`.
6. **Frühe Kollateral-Erkennung (S2):** bevor eine neue Lesung die Basis ergänzt, wird je X geprüft, ob ein Basis-Eintrag fehlt, der zu keinem
   eigenen gesendeten `REMOVE` gehört (Zählregel aus 4.5). Ein solcher Eintrag wird sofort als unerwartete Entfernung am Lauf vermerkt
   (gleiche Liste wie 4.5, kein Doppeleintrag) — er kann damit nicht mehr „vergessen" werden, auch wenn die Nachlese später nichts mehr zu
   prüfen hätte. Der Lauf geht weiter (die Zeile selbst wird normal klassifiziert).

**Übersprung-Mechanik:** Der Hook antwortet `skip` mit dem übersetzten Text; die Engine setzt die Zeile `cancelled` mit diesem Text, sendet
nichts, fragt `abortOn` nicht (bestehender Vertrag). Der Hook wirft nie und antwortet genau einmal (Engine-Vertrag „exactly one value"); jede
Lesung läuft mit eigener Frist, weil die Engine dem Hook keine gibt.

**Mehrere Karten desselben Emotes in einem Lauf** (`ome44` und `hob44` markiert): nach dem ersten `REMOVE` zeigt die gehaltene Lesung X noch
mit zwei Einträgen → Regel 2 (c) liest frisch → der verbliebene Eintrag bindet. Kein Sonderfall.

**Kosten:** je Versuch einer Mehr-Eintrags-Zeile eine volle Set-Lesung (bei ~900 Einträgen zwei Seiten plus eine Bestätigungsseite) gegen 7TVs
globalen Bucket — von D21 so gewollt; eine Ratenbegrenzung der Lesung endet als Lesefehler (fail-closed, Regel 1/3).

### 4.5 Nachlese: `unknown` klären und Nachprüfung (D33, AK 40)

- **Zählregel (S5, für Klärung, Nachprüfung und 4.4 Schritt 6).** Je Emote X und Zeitpunkt t: **Basiszahl** = Einträge der Vergleichsbasis
  bei t; **eigene Abgänge** = gesendete `REMOVE`s an X bei t, die `done` sind; **Lesezahl** = Einträge der Lesung bei t. Ohne Gleichstand ist
  die Basiszahl 1, und die Regel fällt auf „(X, t) fehlt" zurück; mit D4-Gleichstand (A(t), B(t)) zählt sie, statt den Zeitpunkt als Ganzes zu
  behandeln.
- **Wann gelesen wird:** wie heute bei mindestens einer `unknown`-Zeile, **zusätzlich**, wenn für mindestens ein Emote X ein Slot-`REMOVE`
  hinausging und nach Abzug der **tatsächlich gesendeten** `REMOVE`s an X (Zeilen `done` oder `unknown`; übersprungene und fehlgeschlagene
  zählen nicht) noch mindestens ein Eintrag der Vergleichsbasis von X übrig ist (T-4: eine markierte, aber übersprungene Zeile darf die
  Nachprüfung nicht unterdrücken — sie ist selbst ein Eintrag, der noch da sein soll). Eine Lesung
  bedient beides; mit Eintragsdatum, sobald der Lauf eine Eintrags-Zeile hat, sonst die bisherige Abfrage. `CANCEL_SETTLE_GRACE_MS` nach
  eigenem `cancel()` und `SET_ENTRIES_READ_TIMEOUT_MS` wie heute. Der Lauf ist während der Lesung `settling` (Unload-Wächter bleibt scharf).
  Schon in 4.4 Schritt 6 erkannte unerwartete Entfernungen bleiben vermerkt, auch wenn die Nachlese scheitert.
- **`unknown`-Eintrags-Zeile (X, Alias a, t):** `done` nur, wenn die Lesung bei t **keinen** Eintrag mit Alias a zeigt **und** die Lesezahl
  bei t kleiner ist als Basiszahl minus eigene Abgänge bei t (positiv nur, Plan-275 N1). Ein umbenannter, noch vorhandener Eintrag (gleiche
  Zahl, anderer Alias) bleibt `unknown`; bei A(t), B(t) und verlorener Antwort auf `REMOVE(A)` wird A `done`, wenn nur noch B(t) steht. Mehrere
  `unknown`-Zeilen bei demselben t werden in Queue-Reihenfolge gegen die Differenz verrechnet (nie mehr `done` als Einträge fehlen).
  Id-Zeilen wie heute (`settleDeleteResult` je Id).
- **Nachprüfung je X und t:** erwartete Zahl = Basiszahl minus eigene Abgänge (`done` und in diesem Schritt geklärte) minus noch `unknown`
  gebliebene eigene Zeilen bei t (die dürfen fehlen). Ist die Lesezahl kleiner als die erwartete Zahl → **unerwartete Entfernung** in Höhe der
  Differenz, benannt mit den Aliasen der Basis-Einträge bei t, die in der Lesung fehlen und nicht zu eigenen Zeilen gehören
  `{ sevenTvEmoteId, alias, addedAt t }`. Übersprungene oder fehlgeschlagene Zeilen von X zählen als erwartet (sie sollten noch da sein). Ein
  Abzug „(X, t) als Ganzes" findet nicht statt — B(t) neben gelöschtem A(t) bleibt erwartet (S5).
- **Gleichstand: Alias statt nur Anzahl (T-3).** Hat die Basis bei t **mehr als einen** Eintrag, gilt jedes erwartete Geschwister nur dann als
  vorhanden, wenn ein Eintrag bei t **seinen Basis-Alias** trägt — ein Eintrag mit unbekanntem Alias erfüllt keine Erwartung (D4: eine
  Umbenennung im Gleichstand ist Entfernen plus Hinzufügen). Maßgeblich ist der Alias aus der letzten Lesung **vor dem ersten eigenen
  `REMOVE` bei t**; spätere Lesungen ändern die Aliase einer Gleichstandsgruppe nicht mehr. Beispiel: Basis A(t), B(t); ein zweiter Editor
  benennt A→C und B→A, `REMOVE(A)` trifft B; Lesung C(t) → B gemeldet. Ohne Gleichstand (Basiszahl 1) bleibt die Anzahl-Regel: ein umbenanntes
  Geschwister ist über sein eigenes Datum eindeutig und keine Entfernung. Preis: eine legitime Fremd-Umbenennung innerhalb einer
  Gleichstandsgruppe meldet ebenfalls (Fehlalarm, „möglicherweise").
- **Ergebnis am Lauf-Datensatz:** `unexpectedRemovals` (Liste, sonst leer), gesetzt **im selben Update** wie `result` und `phase: 'reporting'`
  (Plan-275 Festlegung 10 — kein Leser sieht einen Zwischenstand). Die unerwarteten Entfernungen werden **nicht** gemeldet (der Lauf hat sie
  nicht bestätigt); REST bzw. ein EventAPI-Pull gleicht sie ab.
- **Nachlese gescheitert, Nachprüfung geschuldet:** Kennzeichen am Datensatz, Dock-Zeile `usageStats.delete.postCheckUnavailable` [E] —
  „konnte nicht prüfen, ob weitere Einträge betroffen sind; Set in 7TV ansehen". Kein automatischer zweiter Versuch.
- **Fehlalarm, hingenommen:** entfernt ein anderer Editor während des Laufs ein Geschwister selbst, meldet die Nachprüfung es ebenfalls. Der
  Text sagt deshalb „möglicherweise", und der Restore ist ein Angebot, keine Automatik.

### 4.6 Bericht `sync-deleted` (D32, AK 37, 41)

- `sevenTvEmoteIds` = eindeutige Ids der `done`-Zeilen in Queue-Reihenfolge (Id-Zeilen und Eintrags-Zeilen gemischt). `classifySyncInSetResponse`
  zählt weiter gegen diese Zahl — `archivedCount` bleibt serverseitig „gemeldete Ids mit Emote-Zeile" (Plan-376 4.1), also keine falsche
  Teil-Meldung bei Teil-Löschung.
- `entries` = je `done`-Eintrags-Zeile `{ sevenTvEmoteId, alias, slotId, addedAt }` mit `addedAt` **wörtlich** aus dem Eintragsziel (nie durch
  ein `Date` geschleust); Klasse-3-Zeilen (R1 a) ohne `slotId` — der Server überspringt sie (`ApplyEntries`), ohne Rückfall auf die Emote-Ebene,
  REST gleicht ab. Feld weggelassen, wenn es keine `done`-Eintrags-Zeile gibt.
- **Retry** (`retrySyncReport`, automatische Wiederholungen): derselbe Körper, abgeleitet aus dem Ergebnis am Datensatz — deterministisch, nie
  aus dem aktuellen Seitenstand.
- Tag-Entfernung: unverändert (nur Id-Zeilen, `removedIds` = `doneKeys` = Ids).

### 4.7 Restore (AK 42, 46, 55; `core/seven-tv/seven-tv-restore.service.ts`)

- `RestoreQueueEmote` bekommt das optionale Eintragsziel (`slotId?`, `addedAt` — das **ursprüngliche** der gelöschten Generation).
  `toRestoreQueue` schlüsselt weiter `${sevenTvEmoteId}#${alias}` und führt neben `aliasByKey` eine Zuordnung Schlüssel → Eintragsziel (gleiches
  Muster, gleiche Lebensdauer: in der Closure des Laufs, Plan-275 Festlegung 8).
- `sync-restored`: `sevenTvEmoteIds` wie heute (eindeutige Ids der `doneKeys`); `entries` je `done`-Schlüssel mit Ziel
  `{ sevenTvEmoteId, alias, slotId, addedAt (ursprünglich) }`; Schlüssel ohne Ziel tragen nichts bei. Ein Retry sendet denselben Körper (Server:
  zweiter Restore ist No-op ohne Datums-Reset, AK 55 a; REST vor dem Bericht → No-op, AK 55 b).
- **Ziel des Restore** bleibt die heutige Eigentümer-Auflösung (`restoreStartTarget`); die `slotId` aus dem Löschlauf geht unverändert mit — der
  Server findet den Ursprung über die `slotId` (Plan-376 Schiedsspruch B3). Der von #376 geschuldete Vitest-Fall (geteiltes Set, angesehener
  Kanal ≠ getrackter Eigentümer) kommt in T3.
- **Restore-Eingang am Dock** (`delete-progress-section.ts`, `restorableItems` → `RestoreCandidate`): reicht das Eintragsziel der Lauf-Zeile durch.
  Eine `unknown`-Eintrags-Zeile geht wie heute als `uncertain`.
- **(a) Restore-Filter im Eintrags-Modus (S1, `shared/seven-tv/already-present-filter.ts`).** Heute behandelt `missingAliases` jeden Live-Alias
  der Id, den die Zeile nicht nennt, als fremden Eintrag und lässt die ganze Zeile fallen (Regel 2) — nach einer Teil-Löschung steht aber
  gerade das Geschwister noch da. Neu: eine Zeile **mit Eintragsziel** wird nur geprüft auf (1) „ihr eigener Alias steht unter dieser Id schon
  im Set" → übersprungen als schon vorhanden; (2) „ihr Alias gehört einem anderen Emote" → übersprungen als Name belegt (wie heute); (3) sonst
  geht sie hinaus, **gleich welche anderen Einträge die Id hat** (Geschwister und aliaslose Einträge derselben Id sind kein Hindernis: 7TV
  erlaubt mehrere Einträge einer Id, und das `ADD` mit Alias kann keinen davon berühren). `uncertain`-Regeln (unvollständige Lesung) gelten
  unverändert. Zeilen **ohne** Ziel behalten Regel 1–4 bytegleich. Gilt an beiden Eingängen (`delete-progress-section` und `restore-flow`, die
  denselben Filter und `loadRestoreConfirmPreview` nutzen) und für die Bestätigungszahl („n werden hinzugefügt"). Teil-Retry: sind von zwei
  Eintrags-Zeilen einer Id eine schon wiederhergestellt, geht nur die andere hinaus.
- **(b) Exakter Restore erst nach erfolgreichem Lösch-Bericht (S3, R4 a, Betreiber 2026-10-13).** Der Server
  reaktiviert mit einem Restore-Eintrag nur einen **archivierten** Slot (`RestoreSlots`); steht der Slot noch aktiv, ist der Bericht No-op, und
  der Abgleich legt für den neu hinzugefügten Eintrag einen neuen Slot bei 0 an (AK 48: D7 nur gegen archivierte Slots, `ResumeArchivedSlots` läuft
  vor dem Archivieren unerklärter Slots). Der normale Weg, auf dem der Slot vor dem Restore archiviert wird, ist der Lösch-Bericht des Laufs.
  Deshalb:
  - **Sperre:** Hat der Lauf mindestens eine `done`-Eintrags-Zeile, bietet `DeleteProgressSection` den Restore erst an, wenn `syncReport`
    `succeeded` oder `partial` ist (der Bericht hat den Server erreicht und dort angewendet, `partial` heißt nur Fehlmengen oder Kanal-Abweichung).
    Bei `failed` steht statt des Knopfs eine Zeile `usageStats.delete.restoreAfterReport` („Wiederherstellen erst, wenn der Lösch-Bericht
    angekommen ist — Bericht erneut senden") neben dem bestehenden Retry-Knopf; ein erfolgreicher Retry hebt die Sperre auf. Läufe **nur aus
    Id-Zeilen** (Flag aus, Vote-Seite, Tag-Entfernung) bleiben wie heute — keine Sperre, Flag aus bitgleich. `unknown`-Eintrags-Zeilen gehören
    nicht zum Bericht; sie werden zusammen mit den `done`-Zeilen angeboten, sobald die Sperre fällt (Filter mit `uncertain` wie heute).
  - **Wirkung:** Der Slot ist beim `ADD` archiviert. Läuft der Abgleich vor dem Restore-Bericht, setzt D7 denselben Slot auf das neue Datum fort,
    und der späte Bericht ist der vorgesehene No-op (AK 55 b); läuft der Bericht zuerst, reaktiviert er den Slot provisorisch, und REST autorisiert
    das neue Datum (AK 42, 46). Retries sind No-ops (AK 55 a). Kein Backend-Code.
  - **Was bleibt (bekannte Grenzen, an #379, Abschnitt 11):** (1) Lösch-Bericht endgültig gescheitert und nicht wiederholbar (401/403/404) — der
    exakte Restore bleibt gesperrt; 404 heißt „Set weg", 401/403 „kein Recht/keine Sitzung" — dann ist auch kein Restore-Bericht möglich;
    nach erneutem Anmelden genügt der Retry. (2) Datei-Restore (4.8) kennt den Berichtsstand des Ursprungslaufs nicht; nach einem gescheiterten
    Bericht und vor dem nächsten Abgleich kann er auf einen noch aktiven Slot treffen — selten, weil Dateien meist später wiederhergestellt werden.
    (3) Hinkender 7TV-REST (SevenTV#81), der einen archivierten Slot per exaktem Datum zurückholt oder einen provisorisch reaktivierten mit dem alten
    Datum autorisiert. (4) Klasse-3-Einträge ohne `slotId` (R1 a) in Kanälen, in denen das Set aktiv ist: der Bericht überspringt sie
    (`ApplyEntries`), archiviert wird dort nur durch REST oder einen EventAPI-Pull; ein schneller Restore vor dieser Beobachtung trifft einen noch
    aktiven Slot (T-5). Alle vier sind **eine Klasse** — „der Slot ist auf unserer Seite noch aktiv, wenn der wieder hinzugefügte Eintrag zum ersten
    Mal abgeglichen wird" — mit dem von AK 48 festgelegten Ergebnis: neuer Slot bei 0, die bisherige Historie bleibt archiviert erhalten (nicht
    verloren, nur nicht fortgesetzt). Eine Heilung gehört in den Abgleich (D7/AK 48), nicht in den Löschlauf.
- **Restore-Angebot einer unerwarteten Entfernung** [E]: eigener Knopf an der `unexpectedRemoval`-Zeile, startet **dieselbe** Restore-Kette des
  Docks mit genau diesen Einträgen (Alias aus der Vergleichsbasis, **ohne** Eintragsziel → Bericht nur mit der Id). Grund: die `slotId` des
  Geschwisters kennt der Dienst nicht, und der Generation des Geschwisters ist nicht anzusehen, ob sie noch die gemeldete ist. Der Filter
  prüft diese Zeilen trotzdem im **Eintrags-Modus** (a) (sie stehen neben überlebenden Geschwistern derselben Id). Die
  Wiederaufnahme ist ein neuer 7TV-Eintrag (D33, D4); die Historie führt D7 im nächsten Abgleich fort, wenn kein aktiver gleichnamiger Slot
  existiert. Nicht der allgemeine „Wiederherstellen"-Knopf des Laufs: der brächte auch die gewollt gelöschten Einträge zurück.

### 4.8 Lösch-Protokoll (`shared/export/purge-run-export.ts`) — [E]

- Eintrags-Zeilen schreiben zusätzlich `slotId` (bei Klasse 3 nicht) und `addedAt`; `name` = Alias, `aliases = [alias]`. Id-Zeilen ohne die
  Felder (Datei bytegleich).
- **Versionssprung 3 → 4 nur für Dateien mit Eintragsziel (S6).** Ein älterer Leser würde die Ziele still verwerfen und nur je Id melden; der
  Server un-archiviert dann nur das Emote, und D7 wählt beim nächsten REST den **zuletzt** archivierten gleichnamigen Slot
  (`ResumeArchivedSlots`, absteigend nach `ArchivedAt`) — womöglich nicht den gelöschten. Das ist genau das „still Falsche", gegen das die
  Version da ist. Deshalb: enthält ein Lauf mindestens eine Eintrags-Zeile, schreibt er Version 4 (ältere Leser weisen die Datei ab); ohne
  Eintrags-Zeile bleibt es Version 3 (Flag aus bytegleich). Doku am Konstanten-Kommentar um den Schritt `3 → 4` ergänzen.
- `parsePurgeRunProtocol` akzeptiert 1–4. In Version 4 sind Ziele **streng**: `slotId` (falls vorhanden) nicht leer, `addedAt` vorhanden und
  nach der strengen Datumsprüfung aus 4.3 gültig (T-6; `Date.parse` allein genügt nicht); ein fehlerhaftes Ziel weist **die Datei** ab (eigener Fehlerschlüssel unter `restore.import.errors`), statt still auf id-only
  herabzustufen. `RestoreRow` bekommt das optionale Ziel; `restore-flow.ts` reicht es bis `startRestore` durch. CSV unverändert (kein
  Restore-Eingang).

### 4.9 Seite (`features/usage-stats/usage-stats-page.ts`/`.html`, `core/usage-stats/merge-set-view.ts`)

- **`selectedForDelete`** (ersetzt den Übergang aus Plan-377 2.6 für Eintrags-Zeilen): je markierter `'live'`-Karte nach Abschnitt 3 —
  datierte Slot-Karte → Eintrags-Zeile; Klasse-3-Karte je Eintrag mit Datum → Eintrags-Zeile (R1 a); Emote-Modus-Karte und gruppierte Klasse 3 → Id-Zeile mit der
  heutigen Regel (Aliase über alle `'live'`-Karten der Id); provisorische Slot-Karte und undatierte Klasse 3 → nicht im Lauf. `hidden` je Karte.
  Eine Karte, deren Eintragsziel die strenge Datumsprüfung (4.3) nicht besteht, fällt fail-closed wie eine provisorische aus dem Lauf.
  **Lösch-Dialog (Betreiberentscheidung 2026-10-13, B-1):** eine Eintrags-Zeile nennt in der Bestätigung **nur ihren eigenen Alias** — eine
  Slot-Karte mit Geschwister ergibt „1 Emote löschen?"; die Regel „der Titel zählt die gelisteten Namen" bleibt, nur die Liste wird kürzer. Flag aus
  und Emote-Modus (Id-Zeilen, `×n`) bleiben wie in #377.
  `confirmationNames` wird nicht mehr gesetzt (A9); die dann toten Helfer (`confirmationNamesFor`, ggf. `isPerEntryCard`) entfernt der Umsetzer,
  wenn sie keinen anderen Leser haben.
- **Klasse-3-Datum (R1 a):** das Zeilenmodell bekommt für Klasse-3-Karten je Eintrag das rohe `addedAt` des Live-Eintrags als eigenes Feld
  (heute steckt es nur im `slotKey`); `firstSeenAt` bleibt `null` (Plan-377 Kenntnis (a)). `mergeSetView` füllt es; Schlüssel unverändert.
- **`pendingSync`-Zeile** (A10, [E]): markierte, aber ausgeschlossene Karten (provisorisch, undatierte Klasse 3) nennt eine Dock-Zeile unter der
  Markierungszeile: `usageStats.delete.pendingSync` (Plural `one`/`other`, `{{names}}` kommagetrennt, „… wird gerade mit 7TV abgeglichen, in
  einer Minute erneut"). Die Zeile entsteht mit ihrem eigenen `@if` im schon gemounteten Dock → `DockOutcomeAnnouncer` spricht sie, Reihenfolge
  direkt nach der Hidden-by-Filter-Zeile (UI-Designsprache §4.5, dort nachtragen). „Löschen (M)" zählt nur Lauf-Zeilen; sind nur ausgeschlossene
  Karten markiert, ist der Knopf aus (bestehende Regel `selectedEmotes().length === 0`).
- **`pendingRemovalSlots`** (D27): Id-Zeilen wie heute (Summe `slotCount` aller `'live'`-Karten der Id); je Eintrags-Zeile 1; ausgeschlossene
  Karten 0.
- **`onDeleted(doneKeys)`:** Schlüssel ohne `#` → Id-Pfad wie heute (alle Karten der Id weg, frei = max(Summe `slotCount`, Aliaszahl der
  Lauf-Zeile)); Schlüssel `${id}#${cardKey}` → genau die Karte mit diesem `slotKey` weg, frei +1, und aus den `siblingNames` der übrigen Karten
  derselben Id fällt ihr Name (Marker `+n` stimmt sofort, Spec 9.3 „Geschwister-Hinweise neu berechnet"). Lauf eines anderen Sets oder nicht
  aktive Ansicht → `refresh()` wie heute. 7TV-Ids enthalten nie `#` (Crockford-Base32), die Trennung ist eindeutig.

### 4.10 Dock und Texte (beide Locales, `de.json`/`en.json`)

Neu unter `usageStats.delete`: `renamedSinceLoad`, `departedSinceLoad`, `noAlias`, `pendingSync` (Plural), `unexpectedRemoval` (Plural,
`{{names}}`, mit „möglicherweise"), `restoreUnexpected` (Knopf), `postCheckUnavailable`. Kein neuer API-Fehlercode (Regel 7 unberührt). Die
Übersprung-Texte erscheinen als Fehlertext der Lauf-Zeile (`run-progress-panel` zeigt ihn); `unexpectedRemoval`/`postCheckUnavailable` stehen in
`DeleteProgressSection` bei den Lauf-Aktionen (Muster der `unknownRows`-Zeilen) — ob sie zusätzlich der `DockOutcomeAnnouncer` sprechen muss,
entscheidet der Umsetzer nach §4.5 und gleicht es mit der Behandlung der `unknownRows`-Zeilen ab (beide entstehen beim Abschluss des Laufs im
schon gemounteten Dock); weicht er ab, Nachtrag in §4.5. Keine neue Farbe, kein neuer Baustein.

## 5. Grenzfälle

| Fall | Verhalten | Beleg |
|---|---|---|
| Dieselbe 7TV-Id in mehreren Slots, nur einer markiert | eine Eintrags-Zeile, `REMOVE(X, a)`, Geschwister bleibt; Bericht mit einem Eintrag; Karte weg, `occupiedSlots` −1 | AK 37 |
| Beide Slots markiert | zwei Eintrags-Zeilen; zweite bindet nach frischer Lesung; Server archiviert beim letzten Slot das Emote mit einer Leave | 4.4, AK 41 |
| Alias zwischen Laden und Versuch umbenannt | `renamed`, kein `REMOVE`, Dock nennt A → C | AK 38 |
| Tausch im kontinuierlichen Lauf (< 5 s): S1(A,t1)→C, S2(B,t2)→A nach dem ersten Schritt | X hat ≥ 2 Einträge → frische Lesung vor dem Versuch → S1 `renamed`, kein `REMOVE(A)` | AK 51 |
| Eintrag verschwunden, Id noch im Set | `departed`, Lauf geht weiter | 4.4 |
| Id ganz verschwunden (vor dem Lauf) | ganzer Lauf blockiert wie heute (#229) | 4.3 |
| Id ganz verschwunden (während des Laufs) | `departed` je Zeile | 4.4 |
| D4-Gleichstand (gleiches `added_at`) | Bindung über den Namen innerhalb der Gruppe; ohne Treffer `departed` | 4.4 |
| Aliasloser Live-Eintrag | fail-closed `noAlias` | 4.4 |
| Provisorische Zeile markiert | nicht im Lauf, `pendingSync`-Zeile, kein `REMOVE` | AK 38, D11 |
| Rate-Limit-Pause (GQL oder HTTP 429) | Engine wiederholt; Hook fragt erneut; gehaltene Lesung > 5 s oder X mit ≥ 2 Einträgen → frisch | AK 38 (A4) |
| Lesung im Hook scheitert / unvollständig | Zeile übersprungen mit `memberRead.*`; nach drei in Folge alle weiteren Eintrags-Zeilen ohne Lesung | 4.4 |
| Verlorene Antwort / Abbruch im Flug | Zeile `unknown`; Nachlese klärt je (X, t), positiv nur | 4.5 |
| Zweiter Editor benennt zwischen Lesung und Request um (D33) | 7TV entfernt einen anderen Eintrag; Nachprüfung meldet `unexpectedRemoval` mit Restore-Angebot | AK 40 |
| Nachlese scheitert, Nachprüfung geschuldet | `postCheckUnavailable` | 4.5 |
| Teil-Löschung, Bericht-Retry (Antwort verloren) | identischer Körper; Server No-op, keine zweite Leave | AK 41 |
| Geteiltes Set (Kanäle A, B) | Bericht mit A-`slotId`s; Server: Ursprung A, B per Datum (Fan-out); Restore mit Eigentümer-Kanal B und A-`slotId` korrekt | AK 54, B3 |
| Nicht aktive Ansicht, datierte Slot-Karte | Eintrags-Zeile; Bindung über `firstSeenAt`; Bericht mit `slotId` des angesehenen Kanals (kein Trefferkanal → nicht angefasst), Fan-out in Kanäle mit aktivem Set | Plan-376 4.2 |
| Nicht aktive Ansicht, Slot-Karte über den Namen an einen **undatierten** Vorschau-Eintrag gebunden | die Lauf-Lesung (v4) liefert das echte Datum; weicht es vom Slot-Datum ab → `departed` (fail-closed) | 4.4 |
| Set-Wechsel während Bestätigung/Lesung | bestehende Abbrüche (`abortReasonBeforeStart`) | unverändert |
| Emote-Modus-Karte mit Flag an (unsettled Seed, E14) | Id-Zeile, `REMOVE` ohne Alias nimmt alle Einträge (heutiges Verhalten bis zum Settle) | Plan-376 R2 |
| Flag-Rollback mitten im Lauf | Server ignoriert `entries`, archiviert je Id; REST heilt | Abschnitt 1 |
| Restore nach Teil-Löschung, bevor REST lief | `sync-restored` mit `slotId`+ursprünglichem `addedAt` → derselbe Slot aktiv, provisorisch; REST autorisiert das neue Datum auf derselben `slotId` | AK 42, 46 |
| Restore einer unerwarteten Entfernung | neuer 7TV-Eintrag, Bericht nur mit Id; Filter im Eintrags-Modus; Historie per D7, wenn möglich | 4.7 |
| Restore nach Teil-Löschung, Geschwister lebt (`hob44` gelöscht, `ome44` steht) | Filter im Eintrags-Modus lässt `hob44` durch; `ome44` ist kein Hindernis | 4.7 a (S1) |
| Teil-Retry eines Restores (ein Alias schon zurück) | nur der fehlende Eintrag geht hinaus | 4.7 a (S1) |
| Lösch-Bericht gescheitert, dann Restore | Restore gesperrt mit Hinweis, bis der Bericht-Retry gelingt; dann Restore mit Zielen auf den archivierten Slot | 4.7 b (S3, R4 a) |
| Abgleich läuft zwischen `ADD` und Restore-Bericht | D7 setzt den archivierten Slot auf das neue Datum fort; der späte Bericht ist No-op | 4.7 b (T-1) |
| Gleichstand A(t), B(t), Fremd-Umbenennung A→C, B→A, `REMOVE(A)` trifft B | B als unerwartete Entfernung (Alias-Regel im Gleichstand) | 4.5 (T-3) |
| A und B markiert, A übersprungen (`renamed`), `REMOVE(B)` trifft C | Nachlese läuft trotzdem (A ist noch erwartet), C gemeldet | 4.5 (T-4) |
| Ungültiges `addedAt` (`"0"`, `2026-02-31…`) in Datei oder Karte | Datei abgewiesen bzw. Karte nicht im Lauf; kein `ADD`/`REMOVE` vor einem sicher scheiternden Bericht | 4.3 (T-6) |
| Kollateralverlust beim ersten von zwei markierten Slots (A, C markiert, B fällt) | Basis akkumuliert; die Lesung vor C erkennt B sofort, `unexpectedRemoval` | 4.4 Schritt 5/6 (S2) |
| Alias-Tausch zwischen Haupt- und Bestätigungsdurchgang einer mehrseitigen Lesung | `complete: false` → Übersprung (fail-closed) | 4.1 (S4) |
| D4-Gleichstand, verlorene Antwort auf `REMOVE(A)`, nur B(t) übrig | A `done` nach Zählregel; B bleibt erwartet | 4.5 (S5) |
| Protokoll mit Eintragszielen in einem alten Image (Rollback) | Version 4 → Datei abgewiesen, kein stilles id-only | 4.8 (S6) |

## 6. Tests

Regel 12: Verhalten, nicht Vorlage — Zustände, Ausgaben, Requests (Körper und Variablen), Sperr- und Übersprung-Entscheidungen samt Grund,
Accessibility-Semantik; keine CSS-Klassen, keine Snapshots, Übersetzungswortlaut nur zur Identifikation. `HttpTestingController` für alle
Lesungen, Mutationen und Berichte.

### 6.0 Backend — entfällt mit R4 (a)

Kein Backend-Test (kein Backend-Code; R4 b verworfen, Abschnitt 12). Die bestehenden `EmoteServiceTests` decken die Server-Seite, auf die sich die Sperre stützt, schon ab (AK 42, 46, 52,
55 a/b: Reaktivierung nur archivierter Slots, No-op bei aktivem oder anders datiertem Slot).

### 6.0a Datumsprüfung (neue Hilfsfunktion, eigener co-located Spec, +6; T1)

Gültig: `…T08:00:14.902Z`, `…T08:00:14Z`, `…T08:00:14.902123+02:00`, `2024-02-29T…Z` · ungültig: `"0"`, `2026-02-31T00:00:00.000Z`,
`2025-02-29T…Z`, ohne Zone (`2026-10-13T08:00:14`), nur Datum, `25:00` als Stunde · die Zeichenkette kommt unverändert zurück (keine Umformung).

### 6.1 `seven-tv-set-entries.spec.ts` (+5)

Ohne Option: Abfragetext unverändert (Wächter), keine Eintragsliste · mit Option: Abfrage wählt `addedAt`, Einträge je Id mit Alias und Datum
in 7TV-Reihenfolge, auch für zwei Einträge einer Id und einen aliaslosen · Datum fehlt/`null` → `null` · `complete`-Regeln gelten unverändert
mit Option · **S4:** mehrseitige Lesung mit Option, zwischen Haupt- und Bestätigungsdurchgang tauschen A(t1)/B(t2) ihre Aliase → `complete:
false`; ohne Option bleibt dieselbe Lesung `complete: true` (Wächter des alten Schlüssels).

### 6.2 `seven-tv-delete.service.spec.ts` (+18) und `seven-tv-run-settlement.spec.ts` (+5)

**Neu aus Revision 3:** **T-3** — Basis A(t), B(t), gemockte Lesungen mit Fremd-Umbenennung A→C und B→A vor dem Request, Nachlese nur C(t)
→ B als unerwartete Entfernung, obwohl die Anzahl stimmt; dieselbe Gruppe ohne Umbenennung, Nachlese B(t) → nichts · **T-4** — A und B markiert, A
`renamed` übersprungen, nach `REMOVE(B)` fehlt C(t3) → die Nachlese läuft trotzdem und meldet C; ein Lauf, dessen einzige Eintrags-Zeile
übersprungen wurde und der kein `REMOVE` sandte → keine Nachlese.
**Neu aus Revision 1:** **S2** — A(t1), B(t2), C(t3), A und C markiert, gemockte Lesung nach `REMOVE(A)` ohne B → die Lesung vor C vermerkt B als
unerwartete Entfernung, auch wenn die Nachlese danach scheitert oder nichts zu prüfen hätte; ein Basis-Eintrag, der nur umbenannt wurde, löst
nichts aus · **S5** — A(t), B(t) gleichzeitig, verlorene Antwort auf `REMOVE(A)`, Nachlese zeigt nur B(t) → A `done`, keine unerwartete
Entfernung; Nachlese zeigt B(t) und C(t) (A umbenannt) → A bleibt `unknown`; erfolgreiches `REMOVE(A)`, Nachlese ohne B(t) → B als unerwartete
Entfernung gemeldet (kein Abzug des ganzen Zeitpunkts); zwei `unknown`-Zeilen bei einem t, nur einer fehlt → genau eine `done`.

Queue: Id-Zeile Schlüssel = Id, bisherige Mutation, Variablen ohne `alias` (Wächter); Eintrags-Zeile Schlüssel `${id}#${cardKey}`, Slot-Mutation,
`alias` = Zeilen-Alias (AK 37); zwei Karten einer Id → zwei Zeilen; Eintrags-Zeile nie um Live-Aliase erweitert · **Hook:** Lauf nur aus
Id-Zeilen setzt keinen Hook; Eintrags-Zeile bindet → genau ein `REMOVE` mit Alias; `renamed` (AK 38: kein `REMOVE`, Text nennt beide Namen);
`departed`; aliasloser Eintrag; Gleichstand mit/ohne passenden Namen; Lesefehler und unvollständige Lesung → Übersprung, Zähler, nach drei in
Folge keine weitere Lesung, Id-Zeilen laufen weiter · **Frische:** zwei Ein-Eintrags-Emotes binnen 5 s → eine Lesung; Pause > 5 s (Fake-Timer,
GQL-Ratelimit) → zweite Lesung vor dem Retry; **AK 51:** Emote mit zwei Einträgen, zweiter Versuch < 5 s nach dem ersten, dazwischen Tausch
in der gemockten Lesung → frische Lesung, `renamed`, kein `REMOVE(A)`; HTTP 429 → Retry fragt den Hook erneut (A4) · **Nachlese:** `unknown`-
Eintrags-Zeile `done` nur bei fehlendem (X, t), bleibt `unknown` bei umbenanntem Eintrag; **AK 40:** Nachlese ohne den nicht gemeldeten
(X, t2) → `unexpectedRemovals` mit Alias; vollständige Liste → leer; übersprungene Zeile von X gilt als erwartet; Nachlese scheitert →
Kennzeichen `postCheckUnavailable`; Lauf ohne Eintrags-Zeilen und ohne `unknown` → keine Nachlese (Wächter); `result` und `unexpectedRemovals`
erscheinen im selben Update · **Bericht:** gemischter Körper (Ids, `entries` nur für Eintrags-Zeilen, `addedAt` wörtlich inkl. Mikrosekunden-
Zeichenkette), Klasse-3-Eintrag ohne `slotId`; ohne Eintrags-Zeilen kein Feld `entries` (Wächter); `retrySyncReport` und automatische Retries
senden denselben Körper.
`seven-tv-run-settlement.spec.ts`: Klärung je Eintrag positiv nur; Id-Zeilen unverändert; Mischung.

### 6.3 Restore und Protokoll (T3)

- `seven-tv-restore.service.spec.ts` +5: Queue hält Ziel je Schlüssel; Bericht `entries` mit `slotId` und **ursprünglichem** `addedAt` je
  `done`-Schlüssel (AK 42, 46); zwei Aliase einer Id → eine Id, zwei Einträge; Retry identisch (AK 55); Zeile ohne Ziel → Körper wie heute
  (Wächter).
- `purge-run-export.spec.ts` +5: Lauf mit Eintrags-Zeile schreibt Version 4 mit `slotId`/`addedAt`; Lauf nur aus Id-Zeilen schreibt Version 3
  bytegleich (Wächter); Parser liest 1–4 und übernimmt gültige Ziele in `RestoreRow`; v4-Datei mit fehlerhaftem Ziel wird abgewiesen (S6);
  v3-Datei ohne Ziele wie heute.
- `already-present-filter.spec.ts` +5 (S1): Eintrags-Zeile `[hob44]` neben live `ome44` derselben Id → geht hinaus; eigener Alias schon da →
  übersprungen als vorhanden; Alias gehört einem anderen Emote → `skippedNameTaken`; Teil-Retry (zwei Eintrags-Zeilen, eine schon zurück) → nur
  die andere; Id-Zeile `[hob44]` neben `ome44` → wie heute verworfen (Wächter Regel 2). Dazu `loadRestoreConfirmPreview` zählt Eintrags-Zeilen
  richtig.
- `delete-progress-section.spec.ts` +3: Restore-Eingang reicht das Ziel durch und sendet nach einer Teil-Löschung mit lebendem Geschwister
  tatsächlich das `ADD` (S1, Ende-zu-Ende über den echten Filter mit gemockter Lesung); **geteiltes Set** — Löschung im angesehenen Kanal A,
  Restore mit Eigentümer-Kanal B als `expectedChannelName` sendet die `slotId` aus A unverändert (Übergabe #376). **Sperre (R4 a), +4:** Lauf
  mit `done`-Eintrags-Zeile und `syncReport` `failed` → kein Restore-Knopf, Zeile `restoreAfterReport` sichtbar, Retry-Knopf erreichbar;
  erfolgreicher Retry → Restore-Knopf erscheint; `partial` → Restore angeboten; Lauf nur aus Id-Zeilen mit `failed` → Restore angeboten wie heute
  (Flag-aus-Wächter).
- `seven-tv-restore.service.spec.ts` +1 (T-6): ein Ziel mit ungültigem `addedAt` erreicht nie die Queue — kein `ADD`.
- `restore-flow.spec.ts` +2: Restore aus v4-Datei mit Ziel sendet `entries` und passiert den Filter neben einem lebenden Geschwister (S1).

### 6.4 Seite, Panel, Flow (T4) und Dock (T5)

- `usage-stats-page.spec.ts` +10, **ersetzt (S7)** die #377-Übergangsfälle mit Slot-Karten („one entry per 7TV id, naming both slots", die beiden
  Filter-Varianten mit `confirmationNames`, „projects both slots … when one of its two cards is marked", „frees two slots, not four" mit
  Slot-Karten) durch: datierte Slot-Karte → Eintrags-Zeile mit `slotId`, Alias, `firstSeenAt` wörtlich, `cardKey`; Emote-Modus-Karte →
  Id-Zeile wie #377 (Wächter, auch ohne `confirmationNames`); provisorische Karte nicht im Lauf, `pendingSync` nennt sie, Knopf-Zahl ohne sie, nur
  provisorische markiert → Knopf aus; datierte Klasse-3-Karte → Eintrags-Zeile ohne `slotId` mit dem Live-`addedAt`, undatierte → `pendingSync` (R1 a); `pendingRemovalSlots` je Eintrag 1, provisorisch 0; `onDeleted` mit `${id}#${slotId}` →
  nur diese Karte weg, `occupiedSlots` −1, Geschwister verliert `+1` (AK 37); `onDeleted` mit Id-Schlüssel wie heute (Wächter); nicht aktive
  Ansicht → `refresh`. **Legacy-Wächter (S7):** eine Emote-Modus-Duplikatkarte (`×2`, nicht aktive Ansicht ohne Slot-Zeilen) liefert weiter einen
  Id-Eintrag mit beiden Aliasen, ohne `confirmationNames`, `pendingRemovalSlots` 2, und `onDeleted` mit Id-Schlüssel und zwei Aliasen gibt zwei
  Plätze frei (A8 bleibt belegt).
- `merge-set-view.spec.ts` +1 (R1 a): Klasse-3-Karte trägt das rohe `addedAt`.
- `delete-flow.spec.ts` +2: `toDeleteQueueEmotes` erweitert Eintrags-Zeilen nicht; Vor-Lauf-Sperre bleibt je Id.
- `mass-delete-panel.spec.ts` +2: Ziel erreicht den Dienst unverändert; **B-1:** eine markierte Slot-Karte mit Geschwister → die Bestätigung
  bekommt genau ihren Alias, der Titel zählt 1. `delete-confirm-dialog.spec.ts`: keine Änderung erwartet (die Titelregel bleibt).
- `delete-progress-section.spec.ts` +3 (T5): `unexpectedRemoval`-Zeile mit Alias und Knopf; Knopf startet die Restore-Kette nur mit diesen
  Einträgen ohne Ziel; `postCheckUnavailable`.
- `dock-outcome-announcer.spec.ts` +1 (T4): spricht die `pendingSync`-Zeile an ihrer Stelle; ggf. +1 (T5) nach 4.10.

### 6.5 E2E (`web/e2e`, Import aus `support/test.ts`; T6)

- `support/mocks.ts`: die Set-Lesung des 7TV-Mocks liefert optional `addedAt` je Eintrag; ein Helfer liest Mutationsvariablen und Berichts-Körper
  zur Prüfung. Kein bestehender Aufruf ändert sich.
- `usage-atlas.e2e.spec.ts` +4: (1) **Flag-aus-Löschlauf** (Mocks im Aus-Zustand): `REMOVE` ohne `alias`, Bericht ohne `entries`, Karte weg;
  (2) **Teil-Löschung** (AK 37): zwei Slot-Karten `ome44`/`hob44`, `hob44` löschen → Slot-Mutation mit `alias: "hob44"`, `sync-deleted` mit
  einem Eintrag samt `slotId` und `addedAt`, `ome44` bleibt ohne `+1`, Slot-Leiste −1; (3) **Restore** derselben Karte über das Dock →
  `sync-restored` mit `slotId` und ursprünglichem `addedAt` (AK 42); (4) **provisorische Karte** → `pendingSync`-Zeile, kein `REMOVE`.
- Die Suite läuft nur, wenn auf `:5151` nichts lauscht (`ss -ltnp | grep 5151` leer).

### 6.6 Nicht nötig

Backend-Tests (kein Backend), Api.Tests (Endpoint und Filter unverändert), `api-error-locales.spec.ts` (kein neuer
API-Fehlercode). Engine-Spec nur, wenn die Hook-Wiederholung nach HTTP 429
dort noch nicht belegt ist (Umsetzer prüft, dann +1 in `seven-tv-run-engine.spec.ts`).

## 7. Live-Verifikation (Regel 16 sinngemäß) — Gliederung; der Orchestrator fährt sie mit dem Betreiber, Freigabe je Anlass

Randbedingungen: geteilte Dev-DB, Dev-Container und Redis gehören anderen Sitzungen — **nie `docker compose`, nie etwas im Haupt-Checkout,
nie im Worktree `EmotePurge-slots`.** Kein Zugriff auf `vps`/`nas`. `handofblood` nur lesend. **Jeder 7TV-Schreibzugriff und jeder Löschlauf
auf `olaf_olaf_son` wird vorher einzeln im Chat vorgelegt und erst nach dem OK des Betreibers gesendet** (R3 entschieden: Paket freigegeben,
Einzel-OK bleibt); das Token aus `~/.config/emotepurge-7tv-test-token` nur als
Header-Datei im Scratchpad (Modus 600, `curl -H @datei`), nie ausgeben, nie `set -x`. **Chat-Schritte tippt der Betreiber selbst**; der Orchestrator
gibt dafür den genauen Text und wartet auf „getippt".

| Ressource | Wert |
|---|---|
| DB | `emotepurge_slots378` im Container `emotepurge-dev-postgres`, Kopie der Dev-DB `emotepurge` per `pg_dump \| psql` im Container (Zugangsdaten aus dem Container-Env, nie ausgeben) |
| Migrationen | **nur auf der Kopie:** die Dev-DB hinkt M2 hinterher (`SwitchUsageStatsToSlotKey` fehlt). Erst `dotnet ef migrations list --connection <Kopie>` aus dem #378-Worktree, Pending-Liste ins Protokoll, dann `database update` auf der Kopie. Nie gegen `emotepurge` |
| Redis | Wegwerf-Container `redis-slots378` auf `127.0.0.1:6394` (**nicht** `:6393`, das gehört #377); vorher `ss -ltn` |
| Prozesse | Worker und Api per `dotnet run` aus dem #378-Worktree, PIDs notieren; Api auf `:5151` (nur dort stimmt die Twitch-Redirect-URI) — `:5151` ist zur Planzeit belegt (#377 T9): vorher abstimmen; `ng serve` auf `:4200` (ebenfalls belegt — abstimmen) |
| Env | Kopie, Redis `:6394`, `ChatLogBackfill__Enabled=false`, `Retention__Enforce=false`, `SevenTv__EventApi__Enabled=false` (Berichtspfad isolieren, wie Plan-376), `SevenTv__ResyncIntervalSeconds=1800` (kein Tick dazwischen; Resync gezielt über den Admin-Endpunkt `POST /api/admin/channels/olaf_olaf_son/resync`), `SevenTv__SlotReconcile__Enabled` je Phase |
| Last | in der Kopie `IsBotActive = false` außer `olaf_olaf_son` |
| Browser | Node-Playwright-Skript mit `web/node_modules` (kein Chrome-MCP), Cookie aus Scratchpad-Datei (600); Netzwerkprotokoll mitschneiden (7TV-GQL-Körper, `sync-*`-Körper) |
| Testobjekte | Emote X = `01KAMA120V9DC2P1YW9QWFHA16` (`ome44`, `hob44`); Emote Y = ein Ein-Eintrags-Emote des Sets, mit dem Betreiber gewählt (Phase A) |

**Tor G** (aus Plan-374) vor jedem Schritt, der auf einem bestimmten 7TV-Stand aufbaut: (a) v3-REST des Sets lesend pollen, bis es den
erwarteten Stand zeigt (7TVs REST-Cache kann 10–30 min hinken, SevenTV#81); (b) wenn nötig Resync über den Admin-Endpunkt; (c) per SQL die
erwarteten Slots prüfen. Scheitert (c), nicht weitermachen, Befund protokollieren. **Die Antwort von `updateEmoteAlias` nennt den ersten Alias
des Emotes im Set, nicht den neuen** — nie als Beleg einer Umbenennung nehmen, immer per v4-Lesung prüfen.

0. **Vorbereitung:** `df -h /`; Ports; Kopie, Migrationen, Last; Ausgangszustand des aktiven Sets von `olaf_olaf_son` per v4-Lesung (Id, Alias,
   `addedAt`) ins Scratchpad. **Bekannte Ausgangslage (Betreiber 2026-10-13):** `ome44` wurde nach #377 T9 wieder entfernt, `hob44` steht mit
   `addedAt` `2026-10-13T08:00:14.902Z`. Erster Schreibzugriff (Einzel-OK): `addEmote(X, alias ome44)`; v4-Lesung bestätigt beide Einträge mit
   eigenem `addedAt` — `ome44` ist jetzt der **jüngere** Eintrag, also nennt D13 `hob44` als Emote-Namen und D2 ordnet `hob44` zuerst (die
   Erwartungen in 7.3–7.6 gelten mit vertauschter Reihenfolge; maßgeblich ist die Slot-Identität, nicht der Name). Für Phase A Emote Y wählen.
   Dies geschieht **vor** dem Umschalten in 7.2, damit X beim Boot mit Flag an vollständig im Set steht (Falle T5).
1. **Phase A, Flag aus (Prod-Nachweis):** Worker und Api mit Flag aus. Seite des Testkanals; X ist **eine** Karte (Emote-Modus) — nicht
   anfassen. Y markieren, löschen (Freigabe), Netzwerk: Mutationstext gleich der unveränderten Konstante (Diff gegen den Text auf der Basis),
   Variablen nur `setId`/`emoteId`, Set-Lesung ohne `addedAt`, `sync-deleted` ohne `entries`; Restore über das Dock (Freigabe) → `sync-restored`
   ohne `entries`; Y wieder im Set (v4-Lesung).
2. **Umschalten:** Tor G — v3-REST listet X mit `ome44` und `hob44`. **Erst beide Prozesse stoppen**, dann beide mit Flag an starten (Übergabe
   #373). Boot-Sync settelt X: per SQL zwei autoritative Slots, kein `SeedUnsettled` (**Falle T5:** X muss beim Boot im Set stehen und gesettelt
   sein, bevor irgendein „X nicht im Set"-Zustand erzeugt wird — sonst bleibt der Seed unsettled und das Emote archiviert).
3. **Zählung vorbereiten (Chat):** Betreiber tippt in `#olaf_olaf_son` genau: `ome44 hob44` (eine Nachricht). Nach dem Flush (≤ 30 s,
   `usage.flushed`) per SQL je Slot eine Zeile für heute. Seite zeigt zwei Karten mit `+1`.
4. **AK 37/41 — Teil-Löschung:** nur `hob44` markieren, löschen (Freigabe). Netzwerk: vor dem `REMOVE` eine Set-Lesung **mit** `addedAt`
   (X hat zwei Einträge → frisch), Slot-Mutation mit `alias: "hob44"`, `sync-deleted` mit `entries: [{ X, slotId(hob44), "hob44", addedAt
   wörtlich wie in /totals }]`. Seite: `ome44` bleibt ohne `+1`, Slot-Leiste −1. SQL: Slot `hob44` archiviert (`ArchivedAt` gesetzt), Emote
   aktiv, keine neue Leave-Zeile. v4-Lesung: `ome44` mit unverändertem `addedAt`, `hob44` fehlt.
5. **AK 42/46/55 und `pendingSync` — Restore:** Restore über das Dock (Freigabe) → `ADD hob44`, `sync-restored` mit `slotId` und dem
   **ursprünglichen** `addedAt`. SQL: derselbe Slot aktiv, `AddedAtFromRest = false`, `UsageStats` am Slot. Seite (Neuladen): `hob44` mit
   derselben `slotId` im `/totals`-Mitschnitt und der Zahl aus Schritt 3. **Vor dem Tick:** `hob44` markieren → `pendingSync`-Zeile, Knopf-Zahl
   ohne sie; wieder abwählen, nichts löschen. Idempotenz: denselben `sync-restored`-Körper per `curl` mit Cookie an **unsere** Api erneut
   senden (kein 7TV-Schreibzugriff) → 200, `AddedAt` unverändert. Tor G (v3 zeigt `hob44`), Resync → derselbe Slot autoritativ mit dem neuen
   7TV-Datum, Zählung erhalten (AK 46). Optional Chat: Betreiber tippt `hob44` → Zeile am selben Slot.
5a. **Sperre live — Restore nach gescheitertem Lösch-Bericht (S3, R4 a).** Reproduzierbar ohne Code-Eingriff: das Playwright-Skript blockiert
   für diesen einen Lauf die Anfragen an `…/emote-sets/*/sync-deleted` und an den Fallback-Resync (`…/channels/*/resync`) per
   `page.route(…abort)`. Vorbedingung: `hob44` steht nach 7.5 autoritativ (SQL), Worker-Log zeigt keinen anstehenden Tick (Intervall 1800 s,
   EventAPI aus). `hob44` markieren, löschen (Einzel-OK für das `REMOVE`) → Dock zeigt den Bericht `failed`; SQL: der Slot von `hob44` ist noch
   aktiv bei seinem alten Datum (der Befund S3). **Erwartet:** kein Restore-Knopf, stattdessen die Zeile `restoreAfterReport`. Routen entfernen,
   „Bericht erneut senden" → `succeeded`; SQL: Slot archiviert. Jetzt Restore über das Dock (Einzel-OK für das `ADD`) → `sync-restored` mit
   `slotId` und altem Datum → derselbe Slot aktiv, provisorisch; Tor G, Resync → derselbe Slot autoritativ mit dem neuen 7TV-Datum, **kein zweiter
   Slot**, Zählung erhalten. Zwei Schreibzugriffe. Die bekannten Grenzen aus 4.7 b (hinkender REST, Klasse 3 im Fan-out, Datei nach gescheitertem
   Bericht) sind live nicht verlässlich herstellbar und werden nur dokumentiert.
6. **AK 38 — Umbenennung zwischen Laden und Lauf:** Seite geladen, **nicht** neu laden. Freigabe: `updateEmoteAlias hob44 → hob45`; v4-Lesung
   bestätigt. `hob44`-Karte markieren, löschen → Netzwerk: kein `REMOVE`; Dock-Zeile nennt `hob44 → hob45`; SQL unverändert. Freigabe:
   zurückbenennen `hob45 → hob44`; v4-Lesung bestätigt.
7. **AK 36 — unsere Mutation gegen ein Wegwerf-Set (R2 entschieden: (a), Betreiber 2026-10-13).** Ein Node-Skript im Scratchpad liest den
   **Text der Slot-Konstante aus der Datei des Branches** (`seven-tv-delete.service.ts` am geprüften SHA, wörtlich übernommen, nicht
   abgeschrieben) und sendet ihn mit Variablen in derselben Form wie `buildRequest`. Set: das Sonden-Set `01M4DJE099DGKA5CB8AQB0G51M`, wenn das
   Test-Token es bearbeiten darf (zuerst eine lesende Prüfung der Editor-Rechte; sonst ein neues Wegwerf-Set des Test-Kontos, ein
   Schreibzugriff mehr). Ablauf: v4-Lesung davor; fehlen zwei Einträge von X, mit Einzel-OK hinzufügen (z. B. `probe1`, `probe2`); Slot-Mutation
   mit `probe2` (Einzel-OK); v4-Lesung danach: `probe2` fehlt, `probe1` mit unverändertem `addedAt`. Set-ID, Datum, SHA und beide Lesungen (ohne
   Token) ins PR. Aufräumen: `probe1` entfernen bzw. neues Set löschen (Einzel-OK).
8. **Nicht live verlässlich herstellbar → nur Test:** AK 40 (D33 braucht einen zweiten Editor im Request-Umlauf), AK 51 (Tausch binnen 5 s),
   `departed` während des Laufs, aliasloser Eintrag, Lesefehler, AK 55 b, geteiltes Set, Kollateralverlust über zwei Versuche (S2), Gleichstand
   (S5), Alias-Tausch zwischen zwei Lesedurchgängen (S4), T-3/T-4 (6.2), ungültige Daten (6.0a), Klasse 3 (R1 a; optional live, wenn ein nicht
   aktives Set mit Duplikat ohne Aufwand verfügbar ist). **Live mitgeprüft wird S1:** Schritt 5 ist genau der
   Restore neben dem lebenden Geschwister `ome44`.
9. **E2E** erst, wenn `:5151` frei ist (eigene Api gestoppt).
10. **Set wiederherstellen:** aktuelles Set gegen den Ausgangszustand aus Schritt 0 **nach (Id, Alias)** vergleichen (`addedAt` darf neu sein);
    Unterschiede nur mit Freigabe beheben.
11. **Abbau:** Worker und Api per SIGTERM (PIDs vorher mit `ps -o args=` prüfen), `docker rm -f redis-slots378`, `DROP DATABASE
    emotepurge_slots378 WITH (FORCE)`, Header- und Cookie-Datei löschen (die Token-Datei bleibt). Protokoll mit SHA, Zeitpunkten und Ergebnis je
    Schritt; keine Hostnamen, Passwörter, Tokens, Cookies, keine Kanalnamen außer `olaf_olaf_son`.

## 8. Prod-Übergabe

- **Keine Migration, kein Fenster, kein Flag-Wechsel.** #378 geht nach #377 mit dem nächsten Main-Deploy (Api-Image trägt das Frontend).
- **Vorher:** `dotnet ef migrations list` gegen Prod (Tunnel wie in CLAUDE.md, Passwort als Platzhalter) ohne `(Pending)` — sonst bringt `main`
  die Migration eines anderen Kinds mit.
- **Was Nutzer auf Prod merken:** nichts (Flag aus: jede Zeile Id-Zeile, alle Requests bytegleich).
- **Nachkontrolle ≤ 5 min** (nur wenn ohnehin ein Löschlauf ansteht, sonst keiner eigens): ein Löschlauf schickt `REMOVE` ohne `alias` und
  `sync-deleted` ohne `entries`, Antwort 200.
- **Rollback:** Images des vorigen Commits; keine Daten- oder Schemafolge (kein Backend-Code). Eine Protokolldatei mit Eintragszielen (Version 4, nur mit Flag an entstehbar) weist ein altes Image ab (S6) — gewollt; Dateien ohne
  Ziele bleiben Version 3 und lesbar.

## 9. Commits, DECISIONS, Spec, Statuszeilen

Conventional Commits, englisch, ohne Ticketnummern, Tests im Commit ihres Features, **Dateien exakt stagen** (geteilter Index, Memory „Parallel
sessions share git index"). Vorschlag je Task in Abschnitt 10.

- **DECISIONS:** ein englischer Absatz „**Child 8 (mass delete and restore per slot), 2026-10-1x**" im Entwurfseintrag #365, **hinter** „Child 7"
  und vor dem nächsten `###`-Eintrag; er wächst mit den Commits von T1–T5 (Regel 3: jeder vertragsändernde Commit trägt seinen Teil). Inhalt:
  Zeilenarten (Abschnitt 3) und Flag-Matrix; [E1] zwei Mutationskonstanten und [E2] Lesung mit Datum nur auf Anforderung, je mit Grund; die
  Bindungstabelle 4.4 (5-s-Regel ab Request-Start, frisch bei ≥ 2 Einträgen, drei Lesefehler, fail-closed bei aliaslosem Eintrag); die
  Generation in der Seitenprüfung der Lesung (S4); akkumulierte Vergleichsbasis mit früher Kollateral-Erkennung (S2) und die Zählregel für
  Gleichstände (S5); Nachlese und Nachprüfung 4.5 samt Vergleichsbasis (A6) und `postCheckUnavailable`; Berichtskörper 4.6 (wörtliches
  `addedAt`, gemischter Körper, Klasse 3 nach R1 a); die Alias-Regel im Gleichstand und der Lese-Auslöser nach tatsächlich gesendeten
  `REMOVE`s (T-3, T-4); die strenge Datumsprüfung (T-6); Restore 4.7 inkl. Eintrags-Modus des Filters (S1), Restore-Angebot ohne Ziel und die
  Sperre bis zum erfolgreichen Lösch-Bericht (R4 a) samt der einen Klasse bekannter Grenzen (4.7 b) statt der Regel für einen noch aktiven Slot der gemeldeten Generation (S3, als Ergänzung des Absatzes „Child 6" zu den Berichten, mit
  bekannter Grenze „nur umbenannter t1-Eintrag"); Protokoll Version 4 nur mit Eintragszielen, strenge Ziele (S6); Seite 4.9 (Ausschluss vor der
  Bestätigung, `onDeleted` je Schlüssel, Rückbau der Bestätigungsnamen, A9); Flag-Rollback als bekannte Grenze; R1 (a), R4 und B-1 (Dialog
  nennt nur den eigenen Alias) als Betreiberentscheidungen.
  `Betrifft:` als **eigene Zeile am Ende des `Betrifft`-Blocks** (Rebase gegen #377 löst dann additiv auf): `seven-tv-delete.service.ts`,
  `seven-tv-set-entries.ts`, `seven-tv-run-engine.ts`, `seven-tv-run-settlement.ts`, `seven-tv-restore.service.ts`, `purge-run-export.ts`,
  `restore-flow.ts`, `delete-flow.ts`, `mass-delete-panel.ts`, `delete-progress-section.ts`, `dock-outcome-announcer.ts`, `usage-stats-page.ts`,
  `already-present-filter.ts`, `merge-set-view.ts`, `usage-stat.model.ts`, `e2e/support/mocks.ts` und die neue Datumsprüfung in `core/seven-tv/`.
- **Spec (Regel 3, deutsch, datierter Hinweis „*Kind 8 (#378), Betreiber <Datum>*" am Abschnitt, Text bleibt sonst stehen):** §9.3 — zwei
  Konstanten statt `alias: null` (A2), Klasse 3 je Eintrag (R1 a, A1), Ausschluss vor der Bestätigung (A10), Vergleichsbasis der Nachprüfung (A6),
  zusätzliche Übersprung-Gründe (A8), AK-38-Lesart des Retry (A4), Eintrags-Modus des Restore-Filters (A11); **keine Änderung an §5.7** (R4 a); in §9.3 ein Satz zur Restore-Sperre und zur Klasse bekannter Grenzen (A12); §18 Dateireferenz um die neuen i18n-Schlüssel; Statuszeile der Spec
  („Kind 8 umgesetzt …") nach dem Merge.
- **UI-Designsprache §4.5:** Reihenfolge des `DockOutcomeAnnouncer` um die `pendingSync`-Zeile (und ggf. 4.10) ergänzen — im Commit, der sie
  einführt.
- **Architectur.md:** nicht in #378 (Modul D gehört #379, Spec §16 Kind 9); Hinweis in Abschnitt 11.
- **Statuszeilen nach dem Merge** (Orchestrator, per REST-PATCH, weil `gh issue edit` an Projects-classic scheitert): Zeile 8 der Kindertabelle
  in Epic #365 („merged (PR #…, sha, Datum); no migration, flag off on prod"); in Epic #346 die #365-Zeile, falls sie Kinder-Stände führt.
  Feature-Ideen und `CLAUDE.md` bleiben bei #379.

## 10. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; strikt nacheinander)

**Vorbereitung (Orchestrator, kein Task):** Umsetzung im Worktree **`/home/dev/projects/EmotePurge-slots374`**, Branch
**`feat/378-slot-mass-delete` von `origin/main`, erst nachdem #404 (#377) gemergt ist** — kein eigener Worktree `EmotePurge-slots378`, kein Branch
von `feat/377-slot-cards`, nie im Worktree `EmotePurge-slots` arbeiten. Vor dem Anlegen: `git -C …/EmotePurge-slots374 status` sauber (der
Worktree trug #374; nichts Ungesichertes überschreiben), `git fetch`, prüfen, dass `origin/main` den Merge von #404 enthält. Damit entfällt jeder
Rebase gegen #377; der DECISIONS-Absatz „Child 8" kommt direkt hinter „Child 7". Plan als `docs/plans/Plan-378-Emote-Slots-Mass-Delete.md`
committen (`docs(plans): add plan for emote slot mass delete and restore`). R1–R4 sind entschieden (Abschnitt 12). Redis für
den Live-Lauf bleibt `:6394`.

**Gate je Aufgabe:** `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run format:check` grün. E2E nur in
T6 und T7 (`npm --prefix web run e2e`, **nur ohne Api auf `:5151`** — vorher `ss -ltnp | grep 5151` leer, sonst Orchestrator fragen). **Abbruch und
Meldung**, wenn ein Flag-aus- oder unberührter Fall seine Erwartung ändern müsste (Abschnitt 1; die freigegebenen #377-Übergangsfälle
ausgenommen) oder eine Backend-Änderung nötig scheint (Abschnitt 0). Vitest
mit Typprüfung laufen lassen (Memory „npx vitest überspringt Typprüfung" — die Projekt-Skripte nutzen).

| # | Aufgabe | Modell | Aufwand | Hängt ab von | Input | Fertig, wenn / Commit |
|---|---|---|---|---|---|---|
| T1 | **Bausteine:** Lesung mit Datum auf Anforderung samt Generation in der Seitenprüfung (4.1, S4), strenge Datumsprüfung (4.3, T-6, 6.0a), zweite Mutationskonstante (4.2), Eintragsziel an `DeletableEmote`/`DeleteQueueEmote`/`RunQueueEmote`, `toDeleteQueue`-Schlüssel und Nicht-Anreicherung (4.3), `buildRequest` nach Zeilenart; 6.1 und die Queue-/Request-Fälle aus 6.2 inkl. Wächter | sonnet | 0,6 T | — | 1, 3, 4.1–4.3, 6.1, 6.2 (Queue); `seven-tv-set-entries.ts`, `seven-tv-delete.service.ts`, `seven-tv-run-engine.ts` (nur Typ/Doku), `mass-delete-panel.ts` (nur Typ), `delete-flow.ts` (`toDeleteQueueEmotes`) | Specs grün, bestehende unverändert; zwei Commits: `feat(web): read each set entry's add date on request`, `feat(web): address a delete row to one set entry by its alias` **plus DECISIONS-Teil** ([E1], [E2], Zeilenarten) |
| T2 | **Bindung, Nachlese, Bericht:** Operation mit Hook (4.4) samt akkumulierter Basis und früher Kollateral-Erkennung (S2), Klärung je Eintrag und Nachprüfung nach der Zählregel mit Alias-Regel im Gleichstand und Lese-Auslöser nach gesendeten `REMOVE`s (4.5, S5, T-3, T-4, Ergebnis am Datensatz im selben Update), Berichtskörper und Retry (4.6), Übersprung-Texte in beiden Locales; Rest von 6.2 | **opus** | 1,4 T | T1 | 4.4–4.6, 5, 6.2; `SevenTvUndoService.createOperation`/`recheckBeforeRemove` als Muster (nur lesen); `seven-tv-run-settlement.ts`; Engine-Vertrag `beforeStep` | 6.2 grün inkl. AK 38, 40, 51 und der S2-/S5-Fälle; drei Commits: `feat(web): bind each slot removal to a fresh read of its set entry`, `feat(web): flag an unreported entry a slot removal took`, `feat(web): report removed slots with their entry generation` **plus DECISIONS-Teil** (4.4–4.6) und Spec §9.3 (A4, A6, A8) |
| T3 | **Restore, Filter und Protokoll:** Restore-Queue mit Ziel, Bericht und Retry (4.7), Eintrags-Modus des Restore-Filters (4.7 a, S1) an beiden Eingängen, Restore-Eingang am Dock mit **Sperre bis zum erfolgreichen Lösch-Bericht** (4.7 b, R4 a, `restoreAfterReport` in beiden Locales), Protokoll Version 4 mit strengen Zielen, Parser 1–4, `RestoreRow`, `restore-flow` (4.8, S6); 6.3 inkl. Shared-Set-Fall | **opus** | 0,9 T | T2 | 4.7, 4.8, 6.3; Plan-376 §10 (#378-Punkt); `seven-tv-restore.service.ts`, `already-present-filter.ts`, `delete-progress-section.ts`, `purge-run-export.ts`, `restore-flow.ts` | 6.3 grün, Id-Zeilen-Filter und v3-Dateien unverändert (Wächter); drei Commits: `feat(web): restore removed slots with their original entry`, `fix(web): let a slot restore pass next to its surviving siblings`, `feat(web): keep the slot and add date of each removed entry in a version 4 purge protocol` **plus DECISIONS-Teil** (4.7, 4.8) |
| T4 | **Seite, Panel, Flow:** `selectedForDelete` je Karte (R1 a), Klasse-3-Datum am Modell, Ausschluss und `pendingSync`-Zeile samt Ansage, `pendingRemovalSlots`, `onDeleted` je Schlüssel samt Geschwistern, Rückbau der Bestätigungsnamen auf der Seite; 6.4 erster bis fünfter Punkt und Announcer | **opus** | 1,0 T | T3 | 3, 4.9, 4.10, 5, 6.4; Plan-377 2.3, 2.6, §8; UI-Designsprache §4.5; `usage-stats-page.ts`/`.html`, `merge-set-view.ts`, `usage-stat.model.ts`, `dock-outcome-announcer.ts` | Seiten-, Flow-, Panel-, Announcer-Specs grün, Flag-aus- und Emote-Modus-Wächter grün, die #377-Übergangsfälle mit Slot-Karten ersetzt (S7, Abschnitt 1); drei Commits: `feat(web): delete marked slot cards one set entry each`, `feat(web): hold provisional slot cards out of a delete run`, `refactor(web): stop naming every alias of an id in the delete confirmation` **plus DECISIONS-Teil** (4.9, R1), Spec §9.3 (A1, A10), UI-Designsprache §4.5 |
| T5 | **Dock: unerwartete Entfernung** — Zeile, Restore-Angebot über die bestehende Kette (4.7 letzter Punkt), `postCheckUnavailable`, Texte in beiden Locales, Ansage nach 4.10; 6.4 sechster Punkt | sonnet | 0,4 T | T4 | 4.5, 4.7, 4.10, 6.4; `delete-progress-section.ts`, `dock-outcome-announcer.ts` | Specs grün; `feat(web): offer to restore an entry a slot removal took unexpectedly` **plus DECISIONS-Teil** |
| T6 | **E2E und Mocks** (6.5) | sonnet | 0,5 T | T1–T5 | 6.5; `e2e/support/mocks.ts`, `usage-atlas.e2e.spec.ts`; Plan-377 F5 | ganze E2E-Suite grün, bestehende Fälle unverändert; `test(e2e): cover a slot delete run, its restore and the flag-off request shape` |
| T7 | **Gates, Coverage, Review, PR** | **opus** (Review); Codex Sol | 0,4 T | T6 | Branch, Abschnitte 0, 1, 2.3, 9, 11 | alle drei Suiten grün (`dotnet test EmotePurge.slnx` trotz reinem Frontend, `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e`), Lint/Prettier und `dotnet format --verify-no-changes` sauber, `git diff --stat origin/main -- src tests` leer; **erst committen**, dann `node scripts/coverage-local.mjs --frontend-only` ≥ 80 % (Schwachstelle erwartbar `usage-stats-page.ts`, Näherung, Memory „coverage-local.mjs: Grenzen"); Opus-Review; `/codex:review --model gpt-6.1-sol --scope branch --base origin/main` aus dem #378-Worktree, Befund unverändert vorgelegt; Widerspruch Opus ↔ Sol → Fable nur über die strittigen Findings; PR (Entwurf bis T8) mit AK-Tabelle (36–38, 40, 42 Restore-Hälfte, 46 Restore-Fluss, 51, 55 Client-Hälfte), Abweichungen 2.3, Prod-Hinweis „unsichtbar mit Flag aus", Rückmeldung an #376 zu P3-2 (A7); Kommentar auf #379 mit Abschnitt 11 |
| T8 | **Live-Verifikation** (Abschnitt 7) | sonnet (Skripte); Orchestrator mit Betreiber | 0,7 T + Betreiberzeit | T7, vor dem Merge | 7; R2 (a), R3 (Einzel-OK je Schreibzugriff); Port-Abstimmung durch den Orchestrator | Protokoll 7.0–7.11 inkl. 7.5a, AK-36-Lesungen im PR, Set nach (Id, Alias) wiederhergestellt, Abbau erledigt |

Reihenfolge strikt **T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8**. T3 und T4 sind weitgehend dateidisjunkt, teilen aber Worktree, Index und Testläufe,
und T4 braucht T3s Restore-Eingang für die Seite nicht, wohl aber T2s Ergebnisfelder — deshalb nacheinander. T2 und T4 berühren die
destruktive Seite und laufen auf Opus; ob T2 zusätzlich an Fable zum Review geht (Freigabe-Checkpoint einer riskanten Änderung), entscheidet der
Orchestrator vor dem Merge.

## 11. Hinweise an die folgenden Kinder (im PR-Text wiederholen)

- **#379 (Betrieb):** Das Frontend braucht für das Einschalten nichts. Abnahmelauf: AK 37 (Teil-Löschung), 38 (`renamed`), 42/46 (Restore auf
  derselben Karte) auf dem LAN-Stack wiederholen; AK 40/51 bleiben Testnachweise. `docs/Architectur.md` Modul D: das Mutationsbeispiel zeigt
  heute nur die Id-Form — die Slot-Form mit `alias` und die Bericht-`entries` nachtragen. Der Rückbau der Aus-Zweige entfernt **keine** Id-Zeilen
  (Emote-Modus mit unsettled Seed, Vote-Seite, Import-Replace, Undo brauchen sie dauerhaft) und nicht die unveränderte `REMOVE`-Konstante.
  Nachkontrolle nach dem Einschalten: ein Löschlauf einer Slot-Karte schickt `alias` und `entries`; `sync-deleted` antwortet 200.
  **Bekannte Grenze zur Entscheidung vor dem Einschalten — eine Klasse, vier Fälle (4.7 b; Betreiber 2026-10-13: R4 a, T-5 an #379).** Gemeinsame
  Ursache: der Slot ist auf unserer Seite noch aktiv, wenn ein wieder hinzugefügter Eintrag zum ersten Mal abgeglichen wird. **Ergebnis in allen
  vier Fällen nach AK 48:** D7 greift nicht (aktiver gleichnamiger Slot), der neue Eintrag bekommt einen **neuen Slot bei 0**, der alte Slot
  wird archiviert und **behält seine Historie** (nicht verloren, nur nicht auf derselben Karte fortgesetzt).
  1. **Lösch-Bericht endgültig unmöglich** (401/403/404 nach allen Retries): die Restore-Sperre von #378 bleibt stehen; wird außerhalb des
     Docks trotzdem wiederhergestellt (7TV direkt), gilt das Ergebnis oben.
  2. **Datei-Restore nach gescheitertem Lösch-Bericht**, bevor ein Abgleich den Slot archiviert hat: die Datei kennt den Berichtsstand nicht.
  3. **REST-Lag (SevenTV#81):** ein hinkender 7TV-REST holt einen archivierten Slot per exaktem Datum zurück oder autorisiert einen provisorisch
     reaktivierten mit dem alten `added_at`; der nächste frische REST trifft dann einen aktiven gleichnamigen Slot.
  4. **Klasse-3-Eintrag ohne `slotId` (R1 a, T-5)** in einem Kanal, in dem das Set aktiv ist: der Bericht überspringt ihn (`ApplyEntries`),
     archiviert wird dort nur durch REST oder einen EventAPI-Pull; ein schneller Restore vor dieser Beobachtung trifft einen noch aktiven Slot.
  #378 schließt nur den Dock-Weg über die Restore-Sperre. Eine Heilung gehört in den Abgleich (D7/AK 48, etwa D7 auch gegen einen in demselben
  Abgleich als unerklärt archivierten gleichnamigen Slot — widerspricht dem heutigen AK 48 und braucht eine Spec-Entscheidung); #379 entscheidet
  vor dem Einschalten, ob das eine Folge wird oder dokumentiert bleibt.
- **#376 (zurückgemeldet):** P3-2 ist durch den Ausschluss provisorischer Zeilen gegenstandslos (A7); die Restore-Undo-Kette behält
  `restoreStartTarget` und sendet die `slotId` unverändert, der Shared-Set-Fall ist als Vitest belegt.
- **#375:** keine Berührung.

## 12. Entscheidungen des Betreibers (2026-10-13) — keine offenen Rückfragen

- **R1 entschieden: (a), Betreiber 2026-10-13.** Klasse-3-Karten (nicht gezählte Live-Einträge eines nicht aktiven Sets, je Eintrag in einer
  Slot-Antwort) werden je Eintrag per Alias gelöscht, gebunden an das `addedAt` des Live-Eintrags; der Bericht trägt ihren Eintrag ohne
  `slotId` (der Server überspringt ihn, REST gleicht Kanäle mit aktivem Set ab); undatierte Klasse-3-Karten fallen unter `pendingSync`.
  Bestätigte Abweichung vom Wortlaut von Spec §9.3 (A1). Wirkung: 3, 4.6, 4.9, 6.4, T4.
- **R2 entschieden: (a), Betreiber 2026-10-13.** AK 36 per Node-Skript mit dem Text der Slot-Konstante aus dem Branch gegen das Sonden-Set
  `01M4DJE099DGKA5CB8AQB0G51M`, falls das Test-Token schreiben darf, sonst gegen ein neues Wegwerf-Set; die Seite belegt AK 37 am aktiven Set
  von `olaf_olaf_son`. Wirkung: 7.7.
- **R3 entschieden, Betreiber 2026-10-13.** Live-Lauf als Paket freigegeben; **jeder einzelne 7TV-Schreibzugriff wird trotzdem vorher im Chat
  vorgelegt und wartet auf ein OK.** Ausgangslage: `ome44` ist nicht im Set und wird zuerst wieder hinzugefügt; `hob44` steht mit `addedAt`
  `2026-10-13T08:00:14.902Z`. Rund 12–16 Schreibzugriffe (Vorbereitung 1; Phase A 2; Teil-Löschung 1; Restore 1; Sperr-Schritt 7.5a 2; Umbenennung hin
  und zurück 2; AK 36 2–5; Wiederherstellung 0–2), ein bis zwei Chat-Nachrichten des Betreibers (7.3, optional 7.5), Abstimmung von
  `:5151`/`:4200` mit der #377-Sitzung. Wirkung: 7.
- **R4 entschieden: (a), Betreiber 2026-10-13** (nach der zweiten Sol-Runde neu entschieden; zunächst war (b) gewählt). Restore-Sperre im
  Frontend, kein Backend: der exakte Restore eines Laufs mit `done`-Eintrags-Zeilen wird erst angeboten, wenn der Lösch-Bericht
  `succeeded`/`partial` ist (4.7 b); der Slot ist dann vor dem `ADD` archiviert, Bericht und Abgleich landen in jeder Reihenfolge auf demselben
  Slot (T-1), Retries bleiben No-ops (T-2). Wirkung: 0, 4.7 b, 6.3, 7.5a, T3; kein T0.
  *Verworfen: (b) Backend-Heilung in `EmoteService`, weil sie nach zwei Review-Runden zu einem eigenen Backend-Entwurf gewachsen wäre
  (Neudatierung, dauerhafte Restore-Identität, Zusammenführen von Slots samt Nutzung, Datums-Auflösung ohne `slotId`, Änderungen an Spec 5.7 und
  D7, rund +2 T) — für einen seltenen Fehlerpfad, dessen Ursache im Abgleich liegt.*
- **T-5 entschieden, Betreiber 2026-10-13:** Klasse-3-Einträge ohne `slotId` in Kanälen mit aktivem Set werden in #378 **nicht** behandelt; sie
  gehen zusammen mit der REST-Lag-Grenze als bekannte Grenze an #379 (Abschnitt 11).

## 13. Risiken für das adversariale Review

1. **D33-Restrisiko bleibt:** zwischen letzter Lesung und Request kann ein zweiter Editor umbenennen; nur Erkennung und Restore-Angebot, keine
   Verhinderung (kein 7TV-Selektor über `added_at`). Die Nachprüfung hängt an einer weiteren Lesung, die selbst scheitern kann
   (`postCheckUnavailable`).
2. **Vergleichsbasis aus der Lesung statt aus Seitenzeilen (A6):** erkennt mehr (auch nach dem Laden hinzugekommene Einträge), meldet aber auch
   legitime Fremd-Entfernungen während des Laufs.
3. **Lesekosten:** je Mehr-Eintrags-Versuch eine volle Set-Lesung gegen 7TVs globalen Bucket; bei großen Sets und vielen markierten
   Mehr-Eintrags-Karten können Lesungen ratenbegrenzt scheitern → Übersprünge (fail-closed), nach drei Fehlern in Folge der Rest. Zu prüfen, ob
   ein Warten statt Überspringen besser wäre.
4. **Aliasloser Eintrag (`noAlias`):** fail-closed ohne Nachweis, ob v4 `alias: null` je liefert; im schlimmsten Fall ist ein Eintrag nur je Id
   löschbar.
5. **Restore einer unerwarteten Entfernung ohne Ziel:** die Historie hängt an D7 und an der Reihenfolge im REST-Abgleich (archiviert REST den
   alten Slot, bevor D7 den neuen Eintrag bindet?); im ungünstigen Fall beginnt der wiederhergestellte Eintrag bei 0.
6. **Protokoll Version 4 (S6):** ein Lauf mit Eintrags-Zeilen schreibt eine Datei, die ältere Images abweisen; zu prüfen, ob der
   Wechsel 3 ↔ 4 je Lauf (nach Inhalt) Leser oder Tests verwirrt, die eine feste Version erwarten.
7. **Engine-Vertrag:** der Hook wird je Versuch gefragt; gibt es einen Wiederholungspfad, der den Hook umgeht (z. B. eine künftige
   Transport-Wiederholung), entfiele die Bindung — Kommentar an `runGatedOne` und am Hook der Löschung.
8. **(verworfen mit R4 b, nur zur Nachvollziehbarkeit) Restore-Regel:** erweitert einen Vertrag aus #376 nachträglich; zu prüfen, ob ein anderer Restore-Pfad (Undo des Imports, Datei
   eines anderen Kanals) einen aktiven Slot der gleichen Generation erreichen kann, ohne dass 7TV den Eintrag wirklich verloren hat — das
   Argument „erfolgreiches `ADD(A)` belegt, dass kein Eintrag mehr `A` heißt" hält nur für ein `ADD` mit genau diesem Alias in dieses Set.
9. **Akkumulierte Vergleichsbasis (S2):** meldet auch Einträge, die ein anderer Editor nach dem Laden hinzugefügt und während des Laufs wieder
   entfernt hat (Fehlalarm, „möglicherweise").

## 14. Aufwand

Rund **5,9 Tage** (T1 0,6 · T2 1,4 · T3 0,9 · T4 1,0 · T5 0,4 · T6 0,5 · T7 0,4 · T8 0,7) plus Betreiberzeit; gegenüber 3 aus Spec §17,
4,9 in Revision 0, 6,0 in Revision 1 und 6,1 in Revision 2. Verschiebungen in Revision 3: T0 entfällt (−0,6); T1 +0,1 (Datumsprüfung, T-6);
T2 +0,1 (Alias-Regel im Gleichstand, Lese-Auslöser, T-3/T-4); T3 +0,1 (Restore-Sperre statt T0); T8 +0,1 (Sperr-Schritt 7.5a). Mehraufwand gegenüber der Spec insgesamt: Flag-aus-Bytegleichheit, die Sol-Befunde (Eintrags-Modus des Filters,
Restore-Sperre, Basis-Akkumulation, Zählregel, Generation in der Seitenprüfung, Protokoll-Version), Rückbau des #377-Übergangs, `pendingSync`-Zeile samt Ansage und die Klasse-3-Behandlung (R1 a).
