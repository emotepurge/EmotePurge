# Emote-Sets: nicht-aktive Sets ansehen, bearbeiten, zwischen Sets übertragen, pro Set zählen — Spec

**Datum:** 2026-09-20 · **Status:** Entwurf; adversariale Zweitmeinung am 2026-09-20 eingeholt (Codex Sol) — fünf Befunde, alle fünf eingearbeitet (s. **Nachtrag** am Ende) · **Konzept:** [Konzept-Emote-Sets-2026-09-19.md](../../Konzept-Emote-Sets-2026-09-19.md) (achte Fassung, zwei Codex-Reviews) · **Epic:** #200 (wird mit den Kind-Issues aus Abschnitt 12 zum Epic) · **Formatvorlage:** [2026-09-13-7tv-bestenliste-import-quelle-spec.md](2026-09-13-7tv-bestenliste-import-quelle-spec.md) · **Berührt:** #69, #74, #76, #201

Diese Spec **zerlegt** das Konzept in seiner achten Fassung. Sie entwirft es nicht neu. Die Entscheidungen
A–M des Betreibers, die verworfenen Alternativen (Emote-Split, `EmoteSet`-Entität, Sammelview,
gestaffelter Deploy, `NULL`-Platzhalter für Altdaten), die drei Review-Runden und die Live-Messungen
vom 2026-09-19 stehen mit Begründung im Konzept und werden hier **nicht** wieder aufgerollt. Wo das
Konzept eine frühere Fassung revidiert, gilt die spätere; wo diese Spec vom Konzept abweicht, steht
es als nummerierte Entscheidung in Abschnitt 2 mit Beleg.

Neu gegenüber dem Konzept ist nur dies: die **elf Punkte, die das Konzept an den Plan delegiert**, sind
hier entschieden (E1–E11, je mit dem Buchstaben (a)–(k) aus dem Auftrag), dazu vierzehn weitere
Festlegungen, die beim Nachprüfen am Code nötig wurden (E12–E25); **siebzehn im Code nachgeprüfte
Fallen** (Abschnitt 3); die Verträge in prüfbarer Form; die vier noch nicht gemessenen Sonden als
T0-Aufgaben mit beiden Zweigen (Abschnitt 11); der Schnitt in Kind-Issues (Abschnitt 12); und die
Aufgabenreihenfolge.

**Zeitbezug, verbindlich.** HandOfBlood wechselt am **2026-10-01** auf sein Halloween-Set. Der
bindende Harness-Lauf (#69, Epic #118) ist am **2026-10-08**. **Der Deploy dieser Spec liegt
dahinter, nicht darauf** (Konzept 12.2): das Runbook des Laufs liegt in `infra-docs`, und vor dem
Wartungsfenster ist dort nachzusehen, ob der Lauf Nachläufer hat, die den Zählpfad unverändert
brauchen. Der **Zwischenweg** (Konzept 12.4 — Halloween-Set in einem nie getrackten Wegwerfkanal
aktiv setzen, von HandOfBloods Nutzungsseite dorthin übertragen, am 01.10. wechseln, danach den
Wegwerfkanal purgen) ist eine **Betreiber-Handlung vor dem 2026-10-01 und kein Bauauftrag dieser
Spec**; er ist Vorbedingung V1 in Abschnitt 13 und liefert die Zuordnungsliste für die Migration.

Gemessene Fakten, auf denen alles Weitere steht (alle aus dem Konzept, Abschnitt 11, live am
2026-09-19; Zeilenangaben in dieser Spec sind am 2026-09-20 gegen `docs/konzept-emote-sets`
nachgeprüft):

- HandOfBlood (Twitch `49140130`) führt drei Sets: `HandOfBlood's Emotes` (aktiv, 762 Einträge /
  760 IDs, ID `01GV88A38G0006FW5TVZVMG507`), `Halloween Set` (687 / 686, `01J94NYQR0000D15QN0BDGN85E`),
  `Christmas Set` (841 / 840); alle Kapazität 1000, `flags: 0`.
- **Haupt ∩ Halloween nach ID: 338** (49 % des Halloween-Sets), nur in Halloween: 348 (51 %).
  Nach **Namen**: 520 in beiden, davon **328** gleiche ID, **192** Namensvettern mit anderer ID;
  **242** Namen nur im Hauptset. Der ausgelieferte Lauf überspringt per ID (338), es bleiben ~424 in
  `toAdd`, davon 192 Kollisionen; landen würden **~232** (687 + 232 = 919 ≤ 1000).
- **Jedes** untersuchte Set trägt #74-Duplikate (gleiche ID, zwei Aliase): 762/760, 687/686,
  841/840, papaplatte `Christmas 2025` 940/939.
- v4 `userByConnection(platform: TWITCH, platformId:)` liefert `emoteSets { id name capacity owner }`
  **ohne Anmeldung**; `editableEmoteSetIds` nur **mit** (nullt das Elternobjekt). v3
  `users/twitch/{id}` liefert `.user.emote_sets[] {id, name, capacity, flags}` plus `emote_set_id`;
  persönliche Sets tragen `flags: 4`, Kapazität 5.
- Bedarfsabruf: `SetEntriesPerPage = 500`, `MaxSetEntryPages = 10`
  (`SevenTvApiClient.cs:46-47`), je Seite ein Permit des providerweiten Budgets
  (`ForeignEmoteSetService.cs:38-43`), Budget `MaxRequestsPerWindow = 60`/min bei
  `MaxConcurrent = 2` (`ForeignEmoteSetProviderBudget.cs:77-83`). Ein ~900er-Set = **zwei Permits**.
- Der Lauf paced mit `RUN_DELAY_MS = 275` (`seven-tv-run-engine.ts:29`); jede abgelehnte Mutation
  zieht ein Ticket aus 7TVs `emote_set_change`-Eimer.
- `stop_grace_period: 60s` steht für den Worker in `docker-compose.prod.yml:95`; Prod-Postgres ist
  `postgres:16-alpine` (`:28`) — `INCLUDE`-Indizes und partielle Unique-Indizes sind verfügbar.

**Nachtrag vom 2026-09-20 zu Punkt 1 und Punkt 4 dieser Liste.** Das Zitat oben stammt aus der
Konzept-Messung vom 2026-09-19 und bleibt unverändert stehen; es ist seither überholt. Am
2026-09-20 nachgemessen (Sonden 1 und 7, Abschnitt 11): v3 `users/twitch/49140130` liefert **drei**
Sets, dieselbe Frage an v4 **vier** — das persönliche Set (`01HMHSTX2G000CNKGAKWBJQA56`,
`Personal Emotes`, Kapazität 5, `kind: PERSONAL`) fehlt in v3 ganz. „HandOfBlood führt drei Sets"
ist damit die **v3-Sicht**, nicht der Bestand, und v3 und v4 stehen nicht gleichwertig
nebeneinander: **v4 ist die Quelle der Set-Liste** (E7) — einschließlich der aktiven Set-ID, die v4
an `style.activeEmoteSetId` mitliefert. Die Zahlen zu Schnittmengen, Duplikaten, Bedarfsabruf und
Lauf-Pacing sind davon unberührt.

---

## 1. Auftrag in einem Satz

Ein Kanal-Manager oder 7TV-Editor kann auf der Nutzungsseite **jedes Set des Kanals** wählen, dessen
Chat-Nutzung **je Set getrennt** sehen, aus dem gewählten Set löschen und wiederherstellen, beim
Übertragen und Importieren **Quell- und Ziel-Set** wählen — auch ein Ziel-Set, das gerade nicht aktiv
ist oder zu einem Kanal gehört, den EmotePurge nicht trackt —, und eine Abstimmung über ein
nicht-aktives Set anlegen; **`Emote` bleibt eine Zeile pro Kanal**, gezählt wird pro Set über
`UsageStat.EmoteSetId`, und nicht-aktive Sets werden bei Bedarf live von 7TV geholt.

Der Weg, den das eröffnet: HandOfBloods Mod-Team („you can only edit the active set and can't
transfer emotes between sets", 2026-09-18) kann ein Saison-Set vor dem Wechsel befüllen und nach dem
Wechsel mit Zahlen bereinigen, statt für jede Saison ein Set zu wechseln, um es zu bearbeiten.

---

## 2. Entscheidungen dieser Spec

E1–E11 sind die elf Punkte, die das Konzept an den Plan delegiert (Buchstabe und Konzeptzeile in der
ersten Spalte). E12–E25 sind Festlegungen, die beim Nachprüfen am Code nötig wurden. Keine davon
rollt eine Entscheidung A–M des Betreibers neu auf.

| # | Frage | Entscheidung | Begründung |
|---|---|---|---|
| E1 (a, Konzept 528–530) | Wo lebt die Zuordnungsliste der Migration? | **Als kompilierte Konstanten in einer gitignorierten Quelldatei neben der Migration**, nach dem Bestandsmuster von `appsettings.Lan.json`: ein `internal static partial class SetSwitchAssignments` in `Infrastructure/Migrations/` — die **committete** Hälfte trägt die beiden Eintragsarten, den Lader und den Abbruch, die **gitignorierte** Hälfte `SetSwitchAssignments.Local.cs` trägt die Werte, und daneben liegt ein committetes `SetSwitchAssignments.Local.cs.example` mit Platzhaltern. Zwei Eintragsarten: Wechseleinträge `(ChannelId, TwitchChannelId, OldEmoteSetId, NewEmoteSetId, BoundaryUtc, ExpectedArchivedCount)` und Kein-Wechsel-Einträge `(ChannelId, TwitchChannelId, ConfirmedEmoteSetId)`. Seit dem 2026-09-20 ist die Liste eine **lückenlose Klassifikation jedes Kanals mit Nutzungszeilen**, keine Liste der Wechsler (4.2, Befund E in Abschnitt 24); die Migration rendert sie als zwei `VALUES`-Listen in zwei temporäre Tabellen und liest nur daraus. **Keine** persistierte Tabelle, **keine** Konfigurationsquelle zur Laufzeit | Kein Laufzeitpfad, kein Schema, das nach dem Umstieg leer herumsteht, und das tragende Argument bleibt unverändert: eine Tabelle oder eine `appsettings`-Quelle hätte nur einen Vorteil — Ändern ohne Rebuild —, und genau der ist unerwünscht; eine falsche Liste ist ein Abbruch, der Betreiber korrigiert sie und **baut neu**, die Migration rät nie. Neu seit dem 2026-09-20 ist allein der **Ort**, und zwar aus einem Grund, der selbst eine Entscheidung ist: seit der lückenlosen Klassifikation nennt die Liste **jeden Kanal mit Nutzungszeilen** und ist damit die Nutzerliste des Dienstes; dieses Repo ist öffentlich (AGPL-3.0), und die Kennung lässt sich nicht weglassen, weil die Set-IDs der **Inhalt** der Einträge sind und 7TV zu jeder Set-ID den Besitzer nennt. Was der Ortswechsel kostet, steht ungeschönt in 4.2 |
| E2 (b, 1033–1034) | Bleibt `RunResult.doneIds` als Guid-Teilmenge? | **Fällt.** `RunResult` behält `doneKeys` als einzige Rückmelde-Identität; `doneIds` wird entfernt, nicht „deprecated" | Eine Guid-Teilmenge neben `doneKeys` ist genau die Einladung, sie als Rückmelde-Identität zu benutzen — der Fehler, den 6.5 gerade beseitigt (`seven-tv-delete.service.ts:187-189`, `mass-delete-panel.ts:297-303`, `seven-tv-restore.service.ts:200-205`). Alle fünf Leser (Panel `deleted`, Delete-Report, Delete-Retry, Restore-Report, Restore-Retry) sprechen nach dieser Spec Keys; ein sechster Leser existiert nicht (`grep doneIds web/src` — nur die genannten plus Specs). Entfernen macht jede vergessene Stelle zum Compile-Fehler statt zum stillen Fehlverhalten |
| E3 (c, 1048–1049) | Wann fällt die alte Body-Form `{ emoteIds }` von `sync-deleted`/`sync-restored`? | **In einem eigenen Folge-Commit nach dem Deploy, frühestens 14 Tage danach, und nur wenn das Api-Log in diesen 14 Tagen keine Altform-Anfrage mehr zeigt.** Der Service loggt jede Altform-Anfrage einmal je Aufruf auf `Information` (`"sync-deleted: legacy body form {emoteIds} used"`), damit die Entscheidung gemessen ist | Ein Deploy kann keinen offenen Tab schließen; 14 Tage sind länger als jede plausible Tab-Lebensdauer, und die Log-Zeile ersetzt die Vermutung durch einen Befund. Der Folge-Commit ist Folge-Issue 1 (Abschnitt 21) |
| E4 (d, 1119–1123) | Behält `CreateVoteSessionRequest` beide Felder? | **Ja, beide** — `EmoteIds` (Null-Session, Guid) **und** `SevenTvEmoteIds` + `EmoteSetId` (Set-Session), mit einer Ausschlussregel (Abschnitt 9). Null-Sessions gehen **nicht** auf die 7TV-Id um | Eine Null-Session prüft heute `e.Id … && !e.IsArchived` (`VoteSessionService.cs:302-307`) und schreibt `VoteSessionEmote.EmoteId` direkt; ein Umbau auf 7TV-Ids hätte für einen Pfad, der sich fachlich nicht ändert, einen zweiten Lookup und eine zweite Fehlerklasse eingeführt. Der Dialog friert nichts ein: die Seite löst die Schlüssel des Rasters (7TV-Ids) für eine Null-Session **im Absende-Moment** über `selectedItems()` in Guids auf (jede Zeile des aktiven Sets ist Klasse 1 und hat eine, 6.5) — die beiden Sätze, an die das Konzept den Plan bindet, gelten |
| E5 (e, 1313–1315) | Wird `SyncImportedRequest.TargetEmoteSetId` später Pflicht? | **Nein, bleibt dauerhaft nullbar.** Fehlt es, schreibt der Service `targetEmoteSetId: null` und `targetIsActiveSetOfChannel: null`; die Audit-Ansicht zeigt dann kein Set | Pflicht würde bei jedem späteren Deploy die F6-Klasse aus #147 neu öffnen (400 **nach** der Mutation für jeden offenen Tab) und kaufte nichts: der eigene Client sendet das Feld immer (Vitest-Spec pinnt es, AK 44), und ein `null` im Audit ist eine ehrliche Aussage über einen alten Client, kein Datenverlust. Nicht an E3 gekoppelt |
| E6 (f, 1247–1249) | Aus wie vielen Endpunkten besteht die Angebotsliste des Ziel-Pickers? | **Ein** Endpunkt für den Picker: `GET /api/seventv/me/emote-set-targets` (eigener Account + `editor_of`-Accounts, je Account die öffentliche Set-Liste, je Account `trackedChannelName`). Daneben existieren `GET /api/channels/{name}/emote-sets` (Dropdown der Nutzungsseite, K4) und `GET /api/seventv/channels/{name}/emote-sets` (Quell-Picker, K3) — **drei Routen, ein Dienst** `ISevenTvEmoteSetListService.ListByTwitchIdAsync`, **ein** Cache. Der Picker ruft `listMine()` **nicht mehr** | Der Picker braucht je Account beides — Set-Liste **und** „ist getrackt" —, und nur ein account-weiter Endpunkt kennt beide zusammen; das Dropdown dagegen braucht Sets eines Kanals, in dem der Nutzer nur Moderator sein kann (kein Account im Sinne von `editor_of`). **Budget:** je Account ein v4-Request (E7), 60 s Redis-Cache je Twitch-ID (E12). Ein HandOfBlood-Editor mit *k* `editor_of`-Accounts kostet beim Öffnen des Pickers höchstens 1 + *k* Permits, bei *k* = 3 also 4 von 60/min; zehn gleichzeitige Öffnungen desselben Teams kosten **ebenfalls nur 1 + *k*** — aber **nur, weil der Listen-Dienst koalesziert** (6.1, Wächter 2). Der frühere Satz „ab dem zweiten trifft jeder den Cache" galt so nicht: ein Cache ohne Singleflight füllt sich erst, wenn der erste Aufruf **fertig** ist; bis dahin sind alle gleichzeitigen Aufrufe kalte Misses und gehen einzeln an v4 (zehn Öffnungen × (1 + *k*) = 40 von 60/min bei *k* = 3). Die Rechnung hängt damit an der Koaleszierung, nicht am Cache allein — deshalb steht sie in 6.1 als Vertrag und nicht nur hier als Annahme. Der teuerste Fall je Dialog bleibt Baustein 3 (Zielvorschau eines 900er-Sets: 2 Permits) — der Picker verdoppelt die Dialogkosten, er vervielfacht sie nicht. Die Such-Quote (100/min, v3/v4 geteilt) berührt keiner der Requests: alle laufen über Twitch-ID, nicht über `users(query:)` |
| E7 (g, 1396–1401) | Woher kommen Set-Liste und `isPersonal`? | **Aus v4**, mit der am 2026-09-20 gemessenen Abfrage `query($pid: String!) { users { userByConnection(platform: TWITCH, platformId: $pid) { id style { activeEmoteSetId } emoteSets { id name capacity kind owner { id mainConnection { platformDisplayName } } } } } }`; `isPersonal := kind == PERSONAL`, und **wählbar ist allein `kind == NORMAL`** (8.6). Der Besitzer kommt **je Set** aus `owner.mainConnection.platformDisplayName` — das ist ein **Anzeigename**, kein Login; er heißt im Vertrag deshalb `ownerDisplayName`, dient **nur der Anzeige** und wird nie verglichen: jeder Abgleich läuft über IDs. **Kein** v3-Aufruf, auch nicht für ungetrackte Accounts — `style.activeEmoteSetId` kommt im selben Request mit (gemessen am 2026-09-20). Für **getrackte** Kanäle bleibt `Channel.ActiveEmoteSetId` die Quelle (E21); das ist eine bewusste Aufteilung, kein Widerspruch: wo wir einen beobachteten Zustand haben, gilt er, wo wir keinen haben, gilt 7TVs Sicht | In dieser Gewichtung: (a) **v3 ist unvollständig** — am 2026-09-20 liefert `GET /v3/users/twitch/49140130` drei Sets, dieselbe Frage an v4 **vier**; das persönliche Set (`01HMHSTX2G000CNKGAKWBJQA56`, `Personal Emotes`, Kapazität 5, `kind: PERSONAL`) fehlt in v3 ganz. Ein Picker auf v3 hätte es nie gesehen und auch nicht ausschließen können (Sonden 1 und 7, Abschnitt 11). (b) `kind` ist ein dokumentierter Enum (`EmoteSetKind`: `NORMAL`, `PERSONAL`, `GLOBAL`, `SPECIAL`) statt eines gemessenen Bits. (c) Besitzer **und** aktive Set-ID kommen im selben Request mit — die frühere Aufteilung (Liste aus v4, aktive Set-ID ungetrackter Accounts aus v3) hätte einen zweiten Request gekostet und ist seit der Messung vom 2026-09-20 gegenstandslos. (d) Der Pfad hängt nicht mehr an `user.emote_sets` und damit weniger an der #43-Fläche (F13). **Budget:** **ein** Request je Account — getrackt wie ungetrackt —, Analyzer `complexity 14, depth 6` (ohne `style` waren es 12/6); die Rechnung „1 + *k* Permits" in E6 bleibt damit gültig |
| E8 (h, 1412–1413) | Woher kommt `sevenTvUserId` im Set-ID-Lesemodus? | **Fehlt:** `ForeignEmoteSet.SevenTvUserId` wird `string?` (Frontend `string \| null`), im Set-ID-Modus `null`, `channelName` = Routenkanal, keine Identitätsauflösung | Die Kanalzeile hält keine 7TV-User-ID (`Channel.cs:5-47` — nur `TwitchChannelId`; die 7TV-ID lebt allein in der Worker-Registry, `SevenTvSubscriptionRegistry.cs:8`), die Alternative des Konzepts existiert also nicht. Der einzige Frontend-Leser reicht das Feld nur durch (`foreign-channel-step.ts:28,206`), niemand rechnet damit (F15) |
| E9 (i, 1488–1493) | Wie antwortet `getSetWarning` für ein nicht-aktives Ziel? | **Der Endpunkt nimmt `?emoteSetId=`, und `EmoteSetOwnershipService.CheckAsync` wird auf die Set-ID parametrisiert** (`emoteSetId ?? channel.ActiveEmoteSetId`): Tier 1 fragt den Besitzer **dieses** Sets, Tier 2 sucht Kanäle mit **dieser** `ActiveEmoteSetId`, Tier 3 vergleicht die moderierten Kanäle mit **dieser** ID. Für ein **ungetracktes** Ziel (7.3, Klasse 2) gibt es keinen Kanal und damit `UNAVAILABLE_WARNING` („nicht geprüft") | Die drei Tiers sind am Code schon set-agnostisch — sie lesen die aktive ID nur an vier Stellen (`EmoteSetOwnershipService.cs:30,37,50,57`; Tier 3 vergleicht `identity.ActiveEmoteSetId == activeEmoteSetId`, `:105`). Die Kosten sind dieselben wie heute (2 Requests Tier 1, bis zu N Tier 3 — unbudgetiert wie heute, keine neue Kostenklasse). Die Konzept-Schranke gilt: die Prüfung **läuft**, ein Grün ist ein Befund, kein Ausbleiben. `available: false` bleibt die Antwort auf „nicht prüfbar" |
| E10 (j, 1699–1702) | Advisory-Lock je Kanal oder Wiederholen? | **Wiederholen, einmal.** `SyncChannelAsync` fängt beim `SaveChangesAsync` die Unique-Verletzung auf `IX_Emotes_ChannelId_SevenTvEmoteId` (`DbUpdateException` mit `PostgresException.SqlState == "23505"`) genau einmal ab, leert den Change-Tracker, lädt die Kanalzeile neu und wiederholt die Zuweisung + `ReconcileAsync` aus demselben Aufruf heraus unter denselben Gates; der zweite Konflikt propagiert wie heute (`Worker.cs:105`, `SevenTvPeriodicResyncWorker.cs:98`). Kein `pg_advisory_xact_lock` | Ein Advisory-Lock müsste im Worker die Spanne Lesen (`SevenTvSyncService.cs:442-444`) → Speichern (`:108`) decken und in der Api das Upsert — also jeden 60-s-Tick jedes Kanals um eine Sperre verlängern, für einen Fall, dessen Schaden **eine** verlorene Runde ist. Wiederholen ist lokal, worker-seitig, ohne Api-Änderung, und mit zwei `AppDbContext`-Instanzen erzwungen testbar (AK 78). Die Api-Seite ist als Einzelstatement (`INSERT … ON CONFLICT DO NOTHING`) ohnehin atomar |
| E11 (k, 2199–2201) | Wert des `lock_timeout`? | **5 s**, gesetzt als **erstes Statement der Migration**: `SET LOCAL lock_timeout = '5s'` (transaktionsgebunden, kann nicht in die Sitzung lecken). Kein `statement_timeout`. Zusätzlich empfohlen, nicht vorausgesetzt: `Options=-c lock_timeout=5s` im `--connection`-String des Betreibers | Im Wartungsfenster stehen Worker und Api; nichts Legitimes hält eine Sperre auf `UsageStats`. Fünf Sekunden trennen „vergessene `psql`-Sitzung" sauber von Lock-Queue-Latenz. Ein `statement_timeout` wäre falsch: die Dauer des Backfills hängt an der Zeilenzahl, die niemand gemessen hat (Konzept 12.3), und ein Abbruch mitten im Backfill kostete das Fenster, nicht die Daten. EF Core führt die Migration transaktional aus (Npgsql, transaktionales DDL), `SET LOCAL` gilt damit genau für sie |
| E12 | Cache und Schlüsselräume der neuen 7TV-Lesepfade | **Ein Redis-Cache je Pfad, Präfix je Schlüsselraum, 60 s, fail-open** wie `ForeignEmoteSetCache` (`:17-20, :24-25`): `7tvsets:{twitchId}` (Set-Liste je Account), `7tvforeign:set:{setId}` (Set-Vorschau nach Set-ID) neben dem bestehenden `7tvforeign:{login}` | Konzept 6.4/7.5: Set-ID als zweiter Schlüsselraum **im selben Cache**, sonst überschriebe „Set X von Kanal A" den Eintrag „aktives Set von A". TTL-Frage bleibt Sonde 4 (Abschnitt 11) |
| E13 | Neue Fehlercodes | **Vier:** `invalid_emote_set_id` (400, Format), `emote_set_id_empty` (400, neue Body-Form ohne Set-ID), `emote_set_not_found` (404, 7TV kennt das Set nicht — nur am set-zentrierten Endpunkt), `vote_session_set_ballot_invalid` (400, Ausschlussregel aus E4). 503-Fälle (7TV nicht erreichbar, 429, Budget) nutzen den **vorhandenen** `foreign_channel_seventv_unavailable` | Regel 7: jeder Code in `ApiErrorCodes.cs`, `web/src/app/core/i18n/api-error.ts` (`KNOWN_API_ERROR_CODES`, `:10-46`) und **beiden** Locales (`errors.api.*`); `api-error-locales.spec.ts` erzwingt die hinteren zwei Schritte. Der 503-Text ist quellneutral („7TV ist gerade nicht erreichbar", `de.json:1097`-Nachbarschaft, Präzedenz E13 der Bestenlisten-Spec) |
| E14 | Format der Set-ID an der Api-Grenze | **Ein `IEndpointFilter` `EmoteSetIdValidationFilter`** nach dem Muster von `ChannelNameValidationFilter`: nicht leer, 1–32 Zeichen, nur `[0-9A-Za-z]`, ordinal. Gilt für Query-Parameter und Body-Feld | 7TV-Set-IDs sind gemessen 26-stellige ULIDs (`01GV88A38G0006FW5TVZVMG507`), ältere Objekt-IDs 24-stellig hex (`Emote.cs:7`); die Schranke ist bewusst weiter als beide, weil sie nur Unfug abweist (leer, Pfadzeichen, Anführungszeichen), nicht 7TVs Format nachbildet |
| E15 | `GetRowsAsync` (Harness #69) unter dem dreispaltigen Schlüssel | **Summiert über `EmoteSetId`** je `(EmoteId, Date)`; `UsageStatRowDto` bleibt unverändert | `HarnessRunner.cs:279,624` liest die Live-Zeilen als eine je `(EmoteId, Date)`; nach einem Set-Wechsel gäbe es zwei. Der Harness vergleicht Chat-Zählung gegen Zeilen, nicht gegen Sets — die Summe ist die richtige Zahl, und der Vertrag von `HarnessRunnerTests` (50) und `ReplayDayCounterTests` (24) bleibt unangetastet (F11). Die Set-ID als Harness-Eingabe (Konzept 5.3) bleibt Auflage an den #69-Plan |
| E16 | Wo die Vereinigung der Set-Ansicht gebildet wird | **Im Frontend.** `/usage-stats/totals?emoteSetId=X` liefert für ein nicht-aktives Set nur die Zeilen des Kanals mit ≥ 1 `UsageStat` unter X (archivierte eingeschlossen); die Live-Mitgliederliste holt die Seite getrennt über `GET /api/seventv/channels/{name}/emotes?emoteSetId=X` und vereinigt über `sevenTvEmoteId` | Konzept 6.4 verlangt, dass stille Reloads (`usage.flushed`, alle 30 s) **keinen** 7TV-Abruf auslösen. Läge die Vereinigung im Backend, hinge der 7TV-Abruf an `/totals` und damit an jedem Reload — der Cache dämpfte das auf 2 Permits/min je Betrachter, die Regel wäre trotzdem verletzt. Im Frontend lädt die Liste beim Wählen des Sets und bei lauten Reloads (`channel.synced`, Refresh-Knopf) |
| E17 | Kennzeichnung einer `null`-Zelle | **Eine** Beschriftung für jede `null`-Zelle: „keine Zählungen unter diesem Set" — nicht zwei („nie im aktiven Set gezählt" vs. „unter anderem Set gezählt") | Die Unterscheidung bräuchte je Zeile eine zweite Historienabfrage über alle Set-IDs und trüge nichts zur Löschentscheidung bei; die Historie unter dem anderen Set ist einen Dropdown-Wechsel entfernt. Konzept 6.2 bindet nur daran, dass die Kennzeichnung **aus der Historie** kommt (keine `UsageStat` unter X) und **nicht** aus dem Fehlen der Zeile — das gilt: ein Klasse-2b-Mitglied mit Guid und ohne Zeile unter X ist genauso `null` wie Klasse 3 |
| E18 | `deleted`-Output des Mass-Delete-Panels | **Emittiert `sevenTvEmoteId`s** (die `doneKeys` des Laufs); beide Host-Seiten filtern nach `sevenTvEmoteId` — auch die Vote-Session-Detailseite | Das Panel ist von zwei Seiten eingebunden (`usage-stats-page.ts:1349`, `vote-session-detail-page.ts:690-695`); die Detailseite behält ihren Guid-Schlüssel (6.5), filtert `results.emotes` aber über die Identität, die das Panel liefert — `VoteSessionResultDto` trägt `SevenTvEmoteId` (`IVoteSessionQueryService.cs:21`). Eine Zeile Änderung dort, kein Schlüsselwechsel (F12) |
| E19 | Set-Liste im Dropdown und stille Reloads | Die Set-Liste (`/emote-sets`) wird **einmal je Kanal-Aufruf** und bei lautem Reload geladen, nie bei `usage.flushed` | Sonst kostete jeder Betrachter einer Nutzungsseite 2 Permits/min für eine Liste, die sich pro Tag einmal ändert. Der 60-s-Cache dämpft, die Regel verhindert |
| E20 | Duplikat-Zelle (#74) in der **aktiven** Ansicht | **Nur in nicht-aktiven Ansichten.** Die aktive Ansicht holt keine Live-Liste (E16) und kennt je ID einen Alias (`SevenTvSyncService.cs:515-517`); dort bleibt es beim heutigen Bild | Slot-Zahl und beide Aliase kommen allein aus der Live-Liste. Das Duplikat-Banner (Namen, nicht IDs — `EmoteSetStatusService.cs:66-71`) ist ein anderer Fall und bleibt |
| E21 | Wo `isActive` der Set-Liste herkommt | Für getrackte Kanäle **aus `Channel.ActiveEmoteSetId`** (unser beobachteter Zustand), für ungetrackte Accounts aus **`style.activeEmoteSetId` derselben v4-Antwort** (E7) — kein zweiter Request, kein v3-Aufruf | Die Zahlen der Nutzungsseite hängen am beobachteten Set (Konzept 5.2); ein Dropdown, das 7TVs Sicht als „aktiv" markiert, während der Cache noch die alte Generation zählt, widerspräche der Seite unter ihm. **Seit dem 2026-09-20 ist die Aufteilung eine Wahl, keine Notlage:** v4 trägt die aktive Set-ID an `style.activeEmoteSetId` und liefert für `platformId: 49140130` denselben Wert wie v3 `.emote_set_id` (`01GV88A38G0006FW5TVZVMG507`, am selben Tag gegengeprüft). Für getrackte Kanäle ignorieren wir sie trotzdem, weil dort der beobachtete Zustand zählt; genommen wird sie genau dort, wo es keinen gibt |
| E22 | `sync-imported`-Papier für ungetrackte Ziele: Besitzer-Prüfung | `GqlEditorOfQuery` liest zusätzlich `user { id }`; `SevenTvEditorGrant`/`SevenTvEditorGrantEntry` bekommen `SevenTvUserId` (nullbar, additiv). Der set-zentrierte Endpunkt vergleicht `GetEmoteSetOwnerIdAsync(setId)` (`SevenTvApiClient.cs:32-33`) mit der 7TV-ID des Akteurs (`ResolveSevenTvIdentityAsync`) und den 7TV-IDs seiner Grants; ein Grant-Eintrag **ohne** 7TV-ID (Cache-Payload von vor dem Deploy, F10) wird live nachgelöst | Die Besitzer-Prüfung ist eine Prüfung auf 7TV-IDs, die Grants tragen heute nur Twitch-IDs (`:355-360`). Additiv im selben Request, kein Zusatzaufruf im Normalfall |
| E23 | Wo die Set-Ansicht das „nicht mehr im Set"-Badge herleitet | Zeile in `/totals?emoteSetId=X`, aber **nicht** in der Live-Liste ⇒ Badge, nicht wählbar zum Löschen, zählt in Summe und Pareto-Nenner | Konzept 6.2, Zeile 3 der Tabelle; das Idiom ist `archivedBadge` (`de.json:781`) |
| E24 | Namensvetter-Merkmal: Datenquelle | `/totals?emoteSetId=X` liefert je Zeile `nameTwinEmoteSetIds: string[]` — Set-IDs, unter denen eine **andere** `SevenTvEmoteId` **desselben Kanals mit demselben `Emote.Name`** mindestens eine `UsageStat` trägt. Setnamen dazu mappt die Seite aus der Dropdown-Liste | Eine Datenbankfrage (Regel 10: ID-Liste zuerst, dann `UsageStats`), kein 7TV-Request, ordinal wie das Chat-Matching (`EmoteNameMatching.cs:89`, nicht verifiziert — Zeile aus dem Konzept) |
| E25 | Kind-Issue-Schnitt | **Sechs Code-Issues K1–K6 entlang der Bauschritte 3–8, plus K0 (Sonden, Zuordnungsliste, Purges — Betreiber, kein Code) und K7 (Wartungsfenster — Runbook, kein Code)** | Kein belegbar besserer Schnitt gefunden: Schritt 6 ist der größte, aber das Konzept begründet, warum Schlüsselwechsel und Vereinigungsliste nicht trennbar sind (NG0955 ab der ersten Guid-losen Zeile). Ein Backend/Frontend-Split innerhalb von Schritt 6 hätte zusätzlich eine Issue-Grenze quer durch die Identität gelegt. **Nachtrag 2026-09-20:** das frühere Zusatzargument „der `/series`-Wire-Bruch läge dann in einem Zwischenzustand, in dem das ausgelieferte Frontend gegen ein umgebautes Backend rennt" ist gegenstandslos — `/series` liefert seit 6.5 (Schritt 1) `sevenTvEmoteId` **additiv** neben `emoteId`, es gibt keinen Bruch mehr, der in einem Zwischenzustand liegen könnte. Der Schnitt steht damit allein auf NG0955, und das genügt. Details in Abschnitt 12 |

---
## 3. Siebzehn Fallen, im Code nachgeprüft am 2026-09-20

Jede davon ist eine **prüfbare Vorgabe**, keine Fußnote. F1–F9 stehen so oder ähnlich im Konzept
und sind hier mit nachgeprüften Zeilennummern festgehalten; F10–F16 sind beim Nachprüfen
dazugekommen, F17 bei der Einarbeitung des Codex-Reviews (Abschnitt 24, N2).

### F1 — Das Conflict-Target ist ein Index, kein Spaltensatz; der alte Worker verwirft nach fünf Versuchen

`UsageStatFlushService.cs:67-76` schreibt handgeschriebenes SQL: `INSERT … FROM UNNEST(@emoteIds,
@useCounts, @botUseCounts, @sharedChatUseCounts)` (`:70`) mit `ON CONFLICT ("EmoteId", "Date")`
(`:71`). PostgreSQL verlangt für ein Conflict-Target einen Unique-Index **exakt** dieser Spalten.
Sobald der Index `(EmoteId, Date)` (`AppDbContext.cs:42-44`) durch `(EmoteId, EmoteSetId, Date)`
ersetzt ist, lehnt Postgres das alte Statement ab, bevor es eine Zeile schreibt. `UsageFlushWorker`
stellt den Batch fünfmal zurück (`MaxConsecutiveFailuresToRequeue = 5`, `:19`, `:89-103`) und
verwirft ihn dann; der Shutdown-Flush (`:30-38`) läuft gegen dasselbe Schema. Die beiden früheren
Zähler-Migrationen (`20260901193259_AddUsageStatBotUseCount`,
`20260907080507_AddUsageStatSharedChatUseCount`) waren additiv und begründen das ausdrücklich mit
dem „still-running old image" (`:13-17` der letzteren) — **diese Migration ist die erste an dieser
Tabelle, die ein laufendes altes Image nicht verträgt.** Vorgabe: Wartungsfenster (Abschnitt 10),
kein „Schema zuerst, dann Images".

### F2 — Die Set-ID kommt im Zählpfad nirgends vor, und der Snapshot muss ein Objekt sein

`IEmoteMatchCache` (`IEmoteMatchCache.cs:5-9`) hält je Kanal `IReadOnlyDictionary<string, string>`
Name → `Emote.Id` (`EmoteMatchCache.cs:11`, `ReplaceChannel :13-14`, `GetChannelEmotes :19-20`);
gefüllt aus `RefreshMatchCacheAsync` (`SevenTvSyncService.cs:403-437`), das nur `Name` und `Id`
projiziert (`:405-408`) und über `EmoteNameMatching.Coalesce` (`:416-417`) an `ReplaceChannel`
(`:436`) übergibt. `TwitchChatManager` liest das Wörterbuch (`:1024`), klassifiziert (`:1045`), matcht
(`:1051-1052`) und ruft `usageCounter.Increment(emoteId, category)` (`:1054`);
`IEmoteUsageCounter.Increment(string, UsageCategory)` (`:7`) puffert in
`ConcurrentDictionary<string, EmoteUsageCounts>` (`EmoteUsageCounter.cs:8`); `UsageFlushWorker:79`
reicht das Wörterbuch an `IUsageStatFlushService.FlushAsync(IReadOnlyDictionary<string,
EmoteUsageCounts>)` (`:39`), das nach `Emote.Id` validiert (`:29-32`). **Kein Feld, kein Parameter,
kein Schlüsselbestandteil trägt ein Set.** Vorgabe (Abschnitt 5): der Lesezugriff liefert
Wörterbuch, Set-ID und Generationszeitpunkt als **ein** Objekt — zwei getrennte Aufrufe könnten
einen Cache-Tausch dazwischen sehen. Der Warmstart (`WarmMatchCacheIfEmptyAsync`, `:280-296`) und
der Sync (`:108-109`) füllen den Cache aus derselben Methode, die `ActiveEmoteSetId` schreibt
(`:86`); die Set-ID reist daher ohne zweite Quelle mit.

### F3 — Eine Zeile ohne `Emote.Id` bricht neun Stellen, davon eine unumkehrbar

Konzept 6.5 hat es gemessen; hier die nachgeprüften Anker, weil jede Stelle eine Aufgabe in K4/K5
ist:

| Stelle | Heute | Beleg |
|---|---|---|
| Auswahlschlüssel | `new ListSelection(this.atlasOrder, (emote) => emote.emoteId, this.emotes)` | `usage-stats-page.ts:549-553` |
| inneres `track` | `@for (emote of row.items; track emote.emoteId; …)` — außen `*cdkVirtualFor … trackBy: trackRow` (Index) bleibt | `usage-stats-page.html:585` bzw. `:524` |
| Inspector / Rang / Füllgrad / Serien-Map | `inspectedId`, `usageRank`, `fillPercents`, `seriesByEmote` schlüsseln über `emoteId` | `:561-565`, `:533-537`, `:488-494`, `:678-680` |
| Queue-Key | `key: emote.emoteId` (Delete), `key: emote.emoteId` (Restore); `DeleteQueueEmote.emoteId: string` Pflicht; `DeletableEmote.emoteId: string` Pflicht | `seven-tv-delete.service.ts:118`, `seven-tv-restore.service.ts:152`, `:55-59`, `mass-delete-panel.ts:34-37` |
| Rückmeldung | `reportDeleted` nur bei `result.doneIds.length > 0`; Body `{ emoteIds }`; Server matcht `emoteIds.Contains(e.Id)` | `seven-tv-delete.service.ts:187-189`, `emote-admin.service.ts:55-59`, `EmoteEndpoints.cs:274`, `EmoteService.cs:24-26` |
| optimistischer Listen-Update | Panel emittiert `doneIds` (`:300-303`), Seite filtert `!deletedIds.includes(item.emoteId)` | `mass-delete-panel.ts:297-303`, `usage-stats-page.ts:1349-1350` |
| **Purge-Protokoll** | Panel filtert Zeilen ohne `emoteId` **vorher heraus** („silently short protocol"), `buildPurgeRunProtocol` verlangt `emoteId: string`, Parser akzeptiert nur `typeof row.emoteId === 'string'` | `mass-delete-panel.ts:344-352`, `purge-run-export.ts:37-42`, `:154-162` |
| Restore aus Protokoll | `emoteId: row.emoteId` in die Queue | `restore-flow.ts:82-86` |
| Voting-Draht | `emoteIds: this.selection.selectedKeys` | `usage-stats-page.ts:1375` |

Nicht betroffen (Beleg): Übertragen (`toImportRow`, `usage-export-purposes.ts:46-49` — nur
`sevenTvEmoteId` und Name), Import-Queue (`key: row.sevenTvEmoteId`,
`seven-tv-import.service.ts:204-208`), die 7TV-Mutationen selbst (`REMOVE_OPERATION`,
`seven-tv-delete.service.ts:42-48`; `ADD_OPERATION`, `seven-tv-restore.service.ts:35-41`). Das
Purge-Protokoll ist die einzige Rückwegdatei einer Löschung; ein Halloween-Lauf ohne den Vertrag aus
Abschnitt 7 wäre zur Hälfte (348 von 686) **unumkehrbar, ohne dass es jemand sähe**.

### F4 — Das Wire-Format von `/series` ist per Test fixiert, und zwei Frontend-Caches kennen kein Set

`ChannelUsageSeriesWireFormatTests.cs:23-37` pinnt
`{"emotes":[{"emoteId":"emote-1","days":[[0,3],[4,8]]}]}`; `EmoteSeriesEntryDto(string EmoteId, …)`
(`IUsageStatQueryService.cs:95`) wird aus `g.Key` = `u.EmoteId` gebaut (`UsageStatQueryService.cs:228-233`).
Der Frontend-Vertrag `EmoteSeriesEntry.emoteId` (`usage-stat.model.ts:51-55`) und die E2E-Mocks
(`e2e/support/mocks.ts:688-703`, `days: Record<string, …>` nach Emote-ID) hängen daran. Die Caches:
`/daily` nach `${channelName}|${emoteId}|${from}|${to}` (`usage-stat.service.ts:33`), `/series`
nach `${channelName}|${from}|${to}` (`:59`); `clearSeriesCache()` (`:73-76`) läuft heute beim
Kanal-/Zeitraumwechsel (`usage-stats-page.ts:1665-1667`), nicht bei einem Set-Wechsel, den es noch
nicht gibt. Vorgabe: der Wire-Format-Test **wird erweitert, nicht ersetzt** — `/series` liefert
`sevenTvEmoteId` **zusätzlich** zu `emoteId`, und der Test pinnt ab K1 beide Felder (6.5, Schritt 1);
`emoteId` fällt erst nach K4 in einem eigenen Schritt (Folge-Issue 5, Muster wie E3). Umgangen wird
der Test in keiner Fassung. Beide Cache-Schlüssel bekommen die Set-ID; der Drilldown-Dialog liest die
Set-ID aus seinen eingefrorenen `data` (`emote-drilldown-dialog.ts:319-321`), nicht aus dem Dropdown.

### F5 — Der Ziel-Loader liest das aktive Set, und der Dialog schließt mit dessen ID

`loadImportTarget(emoteAdminService, channelName)` (`import-target-loader.ts:66-98`) ruft
`getSetStatus` und `listEmotes` (`:70-71`) — beides das **aktive** Set des Zielkanals
(`emote-admin.service.ts:80-82`, `:87-91`) — und liefert `setId: status.value.activeEmoteSetId`
(`:89`), `occupiedSlots`, `capacity`, `emotes` (`:90-93`). `buildImportPreview` rechnet
`alreadyPresent` und `nameCollisions` auf genau dieser Liste (`import-preview.ts:34-35, :43-50`);
`projectSlots(target.occupiedSlots, target.capacity, preview.toAdd.length)`
(`import-confirm-dialog.ts:397`); `execute()` schließt mit `targetSetId: target.setId` (`:478`), das
`import-flow.ts:93-94` als `setId` an `startImport` gibt. Der Picker kennt nur Kanäle
(`ImportTargetChoice { scope, channelName }`, `import-target-dialog.ts:29-32`), schließt den eigenen
aus (`import-target-options.ts:30`) und deaktiviert ungetrackte (`:31`). Die Kopfzeile nennt die
Set-**ID** (`import-confirm-dialog.ts:100-105`). **Ein Picker ohne Loader-Umbau schriebe ins
falsche Set** — oder meldete für „HandOfBlood → HandOfBlood, Halloween" „762 bereits vorhanden,
nichts hinzuzufügen" (`nothingToAdd`, `:419`). Vorgabe: Abschnitt 8, Ziel-Picker.

### F6 — Die Set-Vorschau holt keine Kapazität, und ihr Cache ist nach Login geschlüsselt

`GqlEmoteSetPreviewQuery` fragt `emotes { total_count page_count items { … } }`
(`SevenTvApiClient.cs:67-68`), `SevenTvEmoteSetPreview(TotalCount, Truncated, Items)`
(`SevenTvModels.cs:503`) und `ForeignEmoteSet(ChannelName, SevenTvUserId, EmoteSetId, TotalCount,
Truncated, Emotes)` (`IForeignEmoteSetService.cs:123-129`) haben kein Kapazitätsfeld.
`ForeignEmoteSetCache` schlüsselt `7tvforeign:{login}` (`ForeignEmoteSetCache.cs:24`, `:63`), TTL
60 s (`:25`); `HardenedForeignEmoteSetService` liest ihn nur nach `normalized` Login (`:78`), und
der Coalescer ebenso (`:95-96`). Vorgabe: Abfrage um `capacity` (und `name`) am Set-Objekt erweitern,
`Capacity` nullbar (0 → `null` wie `:704`), zweiter Schlüsselraum (E12).

### F7 — `sync-imported` an ein ungetracktes Ziel wird mit 404 verworfen

`reportImported` postet an `run.targetChannelName` (`seven-tv-import.service.ts:296-302`); die Gruppe
`/api/channels/{channelName}/emotes` steht hinter `UsageStatsAccessAuthorizationFilter`
(`EmoteEndpoints.cs:24-29`), und `MarkImportedAsync` lädt die Kanalzeile und liefert ohne sie
`false` (`EmoteService.cs:111-115`), was der Endpunkt als 404 durchreicht (`EmoteEndpoints.cs:206`).
Für ein Set, dessen Account kein getrackter Kanal ist, gibt es keine Zeile — die Papierspur wäre
weg. Vorgabe: set-zentrierter Endpunkt ohne Kanalzeile (Abschnitt 6.6).

### F8 — Das Voting sperrt archivierte Zeilen beim Anlegen **und** beim Abstimmen, und die Detailseite leitet die Berechtigung aus Daten ab

`AllEmoteIdsEligibleAsync` zählt `!e.IsArchived` (`VoteSessionService.cs:302-307`) und weist einen
Wahlzettel mit archivierter Zeile ganz ab (`:41-53`); `IsEmoteVotableAsync` (`:314-320`) sperrt
Votes darauf. `BuildResultRow` nullt `useCount` für archivierte Zeilen
(`VoteSessionQueryService.cs:191`), `VoteSessionResultDto` trägt `IsArchived`
(`IVoteSessionQueryService.cs:21-22`). Die Detailseite: `canSelectForDelete = this.hasUsageData`
(`vote-session-detail-page.ts:211-220`), obwohl `canManage` aus `/permissions` bereits vorliegt
(`:198-205`); das Panel bekommt `activeEmoteSetId()` (`.html:155-160`, `.ts:842-846`).
`VoteSessionEmote` hält nur `EmoteId` (`VoteSessionEmote.cs:9-10`); `Vote.EmoteId` und
`VoteSessionEmote.EmoteId` kaskadieren auf `Emote` (`AppDbContext.cs:83-99`, `:101-114`) — deshalb
**muss** eine Wahlzettel-Zeile existieren, bevor die erste Stimme fällt (Konzept 8). Und der
Wettlauf: `ReconcileAsync` liest alle Zeilen (`SevenTvSyncService.cs:442-444`), staged im
Anlegezweig von `UpsertEmote` (`:530 ff.`) ein `Add` und schreibt erst am Ende (`:108`);
`ChannelSyncGate` ist ein prozessweiter Semaphor (`ChannelSyncGate.cs:20-22`, Singleton
`ServiceCollectionExtensions.cs:88`), und `SyncChannelAsync` läuft nur im Worker (`Worker.cs:140`,
`SevenTvPeriodicResyncWorker.cs:75`, `SevenTvEventClient.cs:452`). Vorgabe: Abschnitt 9, E10.

### F9 — Das Beobachtungs-Log hat fünf Schließstellen, und keine davon existiert

`LeaveAsync` setzt nur `IsBotActive = false` (`ChannelService.cs:64`); der Rename beim Join setzt
`TrackingResumedAt` (`:244-247`), `ChannelIdentityService.RenameAsync` ebenso (`:338-342`), der Merge
(`:448-450`); der Purge kaskadiert (`:105`). Der Set-Wechsel wird an genau einer Stelle festgestellt
(`emoteSetSwitched`, `SevenTvSyncService.cs:83`) und **nach** der Plausibilitätssperre (`:74-78`
ruft `TryGuardAgainstImplausibleWipeAsync :351-370`) — während einer #76-Blockade wird `:83` nie
erreicht. Vorgabe: Abschnitt 4.3 nennt jede Stelle; `SevenTvSyncServiceTests` (45 Fälle) und
`ChannelServiceTests`/`ChannelIdentityServiceTests` bekommen je Stelle einen Fall.

### F10 — Der Grants-Cache hält ein JSON-Payload mit zehn Minuten TTL; ein neues Feld ist nach dem Deploy erst einmal leer

`ModRoleCache` speichert `SevenTvEditorGrants` unter `7tveditor:{twitchUserId}` (`ModRoleCache.cs:52`)
mit `Auth:ModCheckCacheTtlMinutes` (Default 10, `:137`) und liest `stored.Entries ?? []` (`:41`);
`MyChannelsService` kennt schon ein `isLegacyGrantPayload` (`:59`). Ein additives `SevenTvUserId`
(E22) fehlt in jedem Eintrag, der vor dem Deploy geschrieben wurde. Vorgabe: fehlende 7TV-ID im
Grant ⇒ live nachlösen (`ResolveSevenTvIdentityAsync(TwitchChannelId)`), **nie** als „kein
Editor" lesen.

### F11 — Der Harness liest eine Zeile je `(EmoteId, Date)`

`HarnessRunner.cs:279` und `:624` rufen `GetRowsAsync` und vergleichen je Emote und Tag;
`UsageStatRowDto(EmoteId, Date, UseCount, BotUseCount, SharedChatUseCount)` (`IUsageStatQueryService.cs:149`).
Nach dem ersten Set-Wechsel gäbe es zwei Zeilen je Tag. Vorgabe E15: `GetRowsAsync` summiert; die
`UsageStatQueryServiceTests` für `GetRowsAsync` bekommen einen Fall mit zwei Set-IDs an einem Tag.

### F12 — Das Panel ist von zwei Seiten eingebunden; seine Ausgabe wechselt die Identität

`app-mass-delete-panel` sitzt in `usage-stats-page.html:890` und `vote-session-detail-page.html:155-160`;
`onDeleted(deletedIds)` filtert dort `!deletedIds.includes(emote.emoteId)` (`vote-session-detail-page.ts:690-695`).
Mit E18 spricht `deleted` 7TV-Ids. Vorgabe: die Detailseite filtert über `emote.sevenTvEmoteId`; ihr
Auswahlschlüssel bleibt die Guid (Konzept 6.5, letzte Tabellenzeile).

### F13 — Das v3-DTO kennt `user.emote_sets` nicht, und #43 kann es nullen

`SevenTvUserRestUserDto` mappt nur `Id` und `Connections` (`SevenTvApiDtos.cs:114-118`); der
Kommentar `:101-106` dokumentiert, dass 7TV bereits `connections[].emote_set` genullt hat.
**Seit der Drehung von E7 auf v4 (2026-09-20) trifft die Falle den Listen-Dienst nicht mehr:** er
liest `user.emote_sets` nicht, die additive DTO-Klasse entfällt, und die v4-Abfrage ist nicht mehr
Ersatz, sondern der Weg. Die Falle bleibt beschrieben, weil der Sync denselben v3-Endpunkt
weiterliest (`SevenTvApiClient.cs:167`).

### F14 — Ein 7TV-Request, der das Budget nicht belastet, ist einer zu viel

`ForeignEmoteSetService` belastet `IForeignUpstreamRequestBudget` je Upstream-Request (`:38-43`), der
Client je Vorschau-Seite; die neuen Set-Listen-Requests (E6, drei Routen) und der Set-ID-Lesepfad
müssen dasselbe tun, sonst ist die 60/min-Decke nur noch für die Fremdkanal-Vorschau eine. Vorgabe:
jeder Upstream-Request der neuen Pfade zieht ein Permit (AK 24). **Das Permit allein genügt aber
nicht:** ohne Koaleszierung und Breaker kann ein v4-Ausfall dasselbe Budget leeren und die Vorschau
mit in 503 ziehen — die vollständige Wächterkette des Listen-Dienstes steht in 6.1.

### F15 — `sevenTvUserId` ist im Vertrag Pflicht und wird nur durchgereicht

`ForeignEmoteSet.SevenTvUserId: string` (`IForeignEmoteSetService.cs:125`), Frontend
`foreign-emote-set.model.ts:34` und `foreign-channel-step.ts:28, :206`; kein Leser rechnet damit
(`grep sevenTvUserId web/src/app` — der Admin-Kanaldetail-Treffer ist ein anderes Modell). Vorgabe
E8: nullbar.

### F16 — Bänder, Sortierung und Füllgrad rechnen mit `number`; der Filter kann `null` schon

`usage-bands.ts` nimmt `readonly number[]` (`:56`, `:113-118`, `usageFillPercent(count: number, …)`
`:198`); die Sortierung `b.totalUseCount - a.totalUseCount` (`usage-stats-page.ts:535`) liefert `NaN`
für `null`. `emote-usage-filter.ts` dagegen deklariert `totalUseCount: number | null` (`:9`) und
behandelt `null` als „nicht im Bereich" (`:58-59`) — der einzige Bestandsleser, der schon vorbereitet
ist. Vorgabe: `null`-Zeilen werden **vor** Bändern, Sortierung und Füllgrad abgetrennt und als eigene
Gruppe am Ende geführt (Abschnitt 8.3); `groupIntoUsageBands` und `usageBandThresholds` bleiben auf
`number`.

### F17 — Eine falsch geformte GraphQL-Abfrage sieht aus wie ein Dauerausfall von 7TV

`IsRateLimited` erkennt allein `errors[].extensions.status == 429`
(`SevenTvApiClient.cs:842-843`); jeder **andere** GraphQL-Fehler kommt bei 7TV ebenfalls als
**HTTP 200 mit `errors[]`** und fällt damit in denselben Zweig wie ein Transportfehler —
`Unavailable` (`:446-451` für den Suchpfad, `:513` für den Vorschaupfad). Der Kommentarblock
`:75-82` hält den Präzedenzfall fest, der das gekostet hat: eine Revision dieses Clients schickte
`$sort` als bare Zeichenkette statt als `Sort`-Input-Objekt, 7TV antwortete auf **jede** Anfrage
mit einem Coercion-Fehler, und das Symptom war eine **permanent nicht verfügbare** Bestenliste —
nicht eine offensichtlich falsche Abfrage. Der Zustandsraum ist hier bewusst ärmer als die
Wirklichkeit: „unsere Abfrage ist falsch" und „7TV ist weg" landen auf demselben Wert, und keine
Menge an Unit-Tests trennt sie, weil ein Test gegen eine selbstgeschriebene Fixture beide Seiten
aus derselben Annahme speist (Regel: Fremd-APIs live prüfen).

**Folge für den neuen v4-Listenpfad (6.1, T2.1):** die **Live-Sonde ist das Einzige**, was die
beiden Fälle trennt. Sonde 7 ist am 2026-09-20 live gegen `https://7tv.io/v4/gql` gelaufen
(Abschnitt 11) — die Fixture für `SevenTvApiClientEmoteSetListTests` wird deshalb aus einer
**echten Antwort** gebaut und ist kein Papierentwurf. Wer die Abfrage später ändert (ein Feld
ergänzt, einen Typ anpasst), ändert sie erst live und dann in der Fixture, nie umgekehrt; sonst ist
der erste Befund im Betrieb „7TV ist ausgefallen".

---
## 4. Vertrag: Datenmodell und Migration

### 4.1 Schema — was sich ändert, was nicht

| Objekt | Änderung | Beleg heute |
|---|---|---|
| `UsageStat.EmoteSetId` | **neu**, `text NOT NULL` (7TV-Set-ID, nie Default) | `UsageStat.cs:3-26` hat kein Set |
| Unique-Index `UsageStats` | `(EmoteId, Date) INCLUDE (UseCount)` → **`(EmoteId, EmoteSetId, Date) INCLUDE (UseCount)`**; der alte Index fällt | `AppDbContext.cs:42-44` |
| Tabelle `ChannelEmoteSetObservations` | **neu**: `Id bigint`, `ChannelId text FK → Channels ON DELETE CASCADE`, `SevenTvEmoteSetId text NOT NULL`, `ObservedFromUtc timestamptz NOT NULL`, `ObservedToUtc timestamptz NULL`, `ClosedBy text NULL` (Vokabular: `set-switch`, `leave`, `rename`, `merge`, `migration`); Index `(ChannelId, ObservedFromUtc)`; **partieller Unique-Index `(ChannelId) WHERE "ObservedToUtc" IS NULL`** | Muster `ChannelLiveDay` (`AppDbContext.cs:52-65`, keine inverse Navigation) |
| `VoteSession.EmoteSetId` | **neu**, `text NULL` (null = heutiges Verhalten) | `VoteSession.cs:13-30` |
| `VoteSessionEmote.NameAtCreation`, `.ImageUrlAtCreation` | **neu**, beide `text NULL`; für Set-Sessions Pflicht (Service-Invariante, nicht DB-Constraint) | `VoteSessionEmote.cs:7-14` |
| `Emote` | **unverändert** — kein Split, keine Spalte, kein Index | Regel 8, `AppDbContext.cs:26-36` |
| `AuditLogEntry` | **unverändert**; `TargetType`/`TargetId` (max 32/64, `AppDbContext.cs:135-136`) und `DetailsJson` tragen das Set | Konzept 4.6 |

SQL-Fragmente, die Mehrdeutigkeit beseitigen (Form, kein fertiger Migrationskörper):

```sql
-- neuer Covering-Index, exakt das Conflict-Target des Flush
CREATE UNIQUE INDEX "IX_UsageStats_EmoteId_EmoteSetId_Date"
    ON "UsageStats" ("EmoteId", "EmoteSetId", "Date") INCLUDE ("UseCount");

-- Invariante des Beobachtungs-Logs: höchstens eine offene Zeile je Kanal
CREATE UNIQUE INDEX "IX_ChannelEmoteSetObservations_ChannelId_Open"
    ON "ChannelEmoteSetObservations" ("ChannelId") WHERE "ObservedToUtc" IS NULL;
```

Der Flush (Abschnitt 5) schreibt danach `ON CONFLICT ("EmoteId", "EmoteSetId", "Date")` mit einem
vierten `UNNEST`-Array `@emoteSetIds` (`text[]`). Abfragen ohne Set-Filter nutzen weiter das Präfix
`EmoteId`, Abfragen mit Set-Filter das Präfix `(EmoteId, EmoteSetId)`; der Index-Only-Scan von
`GetUsageContextAsync` (`UsageStatQueryService.cs:74-84`, DECISIONS 2026-09-06 Task 4) bleibt, weil
`INCLUDE ("UseCount")` bleibt.

### 4.2 Die Migration `AddUsageStatEmoteSetId` — Reihenfolge, Eingabe, Abbruchgründe

Setzt auf `20260907080507_AddUsageStatSharedChatUseCount` auf. Läuft **einmal, von Hand, im
Wartungsfenster** (Abschnitt 10); Produktion wendet Migrationen nie beim Start an
(`PendingMigrationGuard.cs:11` bricht bei Pending ab — S3-34).

**Eingabe (E1):** `SetSwitchAssignments` ist seit dem 2026-09-20 **keine Liste der Wechsler mehr,
sondern eine lückenlose Klassifikation jedes Kanals, der mindestens eine `UsageStat`-Zeile trägt**
(Entscheidung des Betreibers zu Codex-Befund E, Abschnitt 24). Zwei Eintragsarten:

- **Wechseleintrag** — `(ChannelId, TwitchChannelId, OldEmoteSetId, NewEmoteSetId, BoundaryUtc,
  ExpectedArchivedCount)`, in zeitlicher Folge, mehrere je Kanal bei mehrfachem Wechsel.
  `ExpectedArchivedCount` ist die Zahl der IDs des alten Sets, die im neuen nicht liegen — vom
  Betreiber am Wechseltag mit der ID-Sonde aus Konzept 11.2 gemessen. Für HandOfBlood: alt
  `01GV88A38G0006FW5TVZVMG507`, neu `01J94NYQR0000D15QN0BDGN85E`, Grenze und Zahl aus V1.
- **Kein-Wechsel-Eintrag** — `(ChannelId, TwitchChannelId, ConfirmedEmoteSetId)`. Eine
  ausdrückliche Bestätigung des Betreibers, dass dieser Kanal nie gewechselt hat, **samt der
  Set-ID, die seine Zeilen bekommen sollen**. Die ID steht in der Liste und wird **nicht** aus
  `Channels."ActiveEmoteSetId"` abgeleitet — sonst prüfte die Migration gegen sich selbst und
  Prüfung 1 wäre eine Tautologie.

**Wo die Liste liegt (E1, entschieden am 2026-09-20).** Nicht im Repo. Die Klassifikation ist ein
`internal static partial class SetSwitchAssignments` neben der Migration, aufgeteilt auf zwei
Dateien nach dem Muster, das dieses Repo für `appsettings.Lan.json` schon fährt:

- **committet** — `Infrastructure/Migrations/SetSwitchAssignments.cs`: die beiden Eintragsarten als
  `record`s, ein Sammler-Typ, der Lader `Load()` und die klassische partielle Methode
  `static partial void AddLocalAssignments(SetSwitchClassificationBuilder builder)`. Eine partielle
  Methode dieser Form (Rückgabetyp `void`, kein Zugriffsmodifikator) **darf ohne Implementierung
  bleiben** — der Aufruf wird dann wegkompiliert. Genau daran hängt, dass CI, Dependabot und ein
  fremder Contributor ohne die Datei bauen und testen können.
- **gitignoriert** — `Infrastructure/Migrations/SetSwitchAssignments.Local.cs`: die implementierende
  Deklaration mit den echten Werten. Der Betreiber legt sie an (V5).
- **committet** — `SetSwitchAssignments.Local.cs.example`: dieselbe Datei mit Platzhaltern und der
  Anleitung im Kopf, wörtlich wie `appsettings.Lan.json.example`. Die Endung `.example` hält sie aus
  dem Standard-Glob `**/*.cs` heraus, sie wird also nicht mitkompiliert.

Damit bleibt die Liste **Teil des Builds**: `dotnet ef database update` baut das Projekt, die Werte
werden einkompiliert, und eine Korrektur verlangt einen neuen Build. Ein Weg, der ein Korrigieren
ohne Rebuild erlaubte, ist ausdrücklich nicht gewollt (E1).

**Fehlt die Datei, bricht die Migration sichtbar ab.** Die erste Anweisung im Rumpf der lokalen
Datei ist `builder.Confirm();` — so steht es in der `.example`, und daran erkennt `Load()`, dass
die Klassifikation **überhaupt einkompiliert** wurde. Ist sie es nicht, wirft `Load()` — **vor**
Schritt 2, also vor jeder Schemaänderung — eine `InvalidOperationException`, deren Meldung sagt,
**was** fehlt (die Klassifikation), **welche** Datei sie trägt und **woraus** sie entsteht
(`.example` kopieren, füllen, neu bauen). Das ist derselbe Fail-fast-Gedanke wie S3-34
(`PendingMigrationGuard.cs:11`): lieber ein benannter Abbruch als eine rätselhafte Folgemeldung
— hier wäre die Folgemeldung „unclassified channel …“ aus Prüfung 3 für den erstbesten Kanal,
was wie ein fehlender Einzeleintrag aussieht und nicht wie eine fehlende Datei.

**Warum die Marke und nicht „beide Listen leer“:** eine leere Klassifikation ist gegen eine
Datenbank **ohne** Nutzungszeilen völlig richtig — genau der Fall der lokalen Dev-Datenbank seit
dem Leerräumen am 2026-09-20. Würde `Load()` auf Leere abbrechen, wäre das lokale
`dotnet ef database update` aus dem Gate von T1.3b nicht mehr durchführbar. Die Marke trennt
„Datei fehlt“ von „Datei sagt: keine Kanäle“; für den zweiten Fall ist **Prüfung 3** zuständig und
bricht mit Nennung des Kanals ab, sobald doch einer Zeilen trägt.

**Nicht ins Image.** Die Images baut die CI aus einem sauberen Checkout, in dem die gitignorierte
Datei nicht existiert — das Image trägt also eine leere Klassifikation, was folgenlos ist, weil
Produktion Migrationen nie beim Start anwendet (S3-34). Die eine lokale Lücke schließt ein
`.dockerignore`-Eintrag: `docker compose build api` nimmt den Arbeitsbaum des Betreibers als
Build-Kontext und würde die Datei sonst mit einkompilieren. **Ehrlich dazu:** anders als bei
`appsettings.Lan.json` gibt es kein `CopyToPublishDirectory="Never"`-Gegenstück — eine `.cs`-Datei
lässt sich nach dem Kompilieren nicht mehr aus ihrer Assembly nehmen. Ein `dotnet publish` **auf
der Maschine des Betreibers** bäckt die Werte in sein lokales Publish-Verzeichnis; das ist kein
Veröffentlichungsweg dieses Projekts (die Images kommen aus der CI), bleibt aber als Restrisiko
benannt statt kaschiert.

**Die Tests bringen ihre eigene Klassifikation mit.** Die puren Prüf- und Zuordnungsfunktionen
(T1.3a) nehmen beide Listen als Parameter; die Migrationstests gegen den ephemeren Container
(T1.3b) setzen ihre Fixture über einen `internal` Testsitz — `InternalsVisibleTo
EmotePurge.Infrastructure.Tests` steht bereits in
`src/EmotePurge.Infrastructure/EmotePurge.Infrastructure.csproj:8`. Kein Test liest die Datei des
Betreibers, und kein Test hängt an ihrer Existenz.

**Was der Ortswechsel kostet — der Tausch, ausgeschrieben.** Bis zum 2026-09-20 war das stärkste
Argument für Konstanten, dass die Liste **im PR-Diff steht** und ein Zweiter sie Zeile für Zeile
gegenlesen kann, und dass der DECISIONS-Eintrag 1 sie wiederholt und damit dauerhaft
nachvollziehbar macht. **Beides fällt weg.** Kein Codex-Review, kein Nutzer und kein späterer Leser
sieht die Klassifikation je; der Eintrag 1 nennt nur noch das Verfahren. Das ist ein echter
Verlust an Prüfbarkeit durch einen Zweiten, und er wird bewusst in Kauf genommen, weil die
Alternative heißt: die Nutzerliste des Dienstes dauerhaft in einem öffentlichen Repo zu
veröffentlichen.

An die Stelle des Gegenlesens treten zwei Dinge, die schwächer, aber nicht nichts sind:

1. **Die sechs Abbruchprüfungen laufen gegen die Datenbank, nicht gegen den Build.** Ein
   Abschreibfehler, den ein Reviewer im Diff gesehen hätte, fällt jetzt an Prüfung 1, 3, 4 oder 5
   auf — an echten Daten und im Wartungsfenster, also später und teurer, aber er fällt auf. Was
   dabei **nicht** auffällt, bleibt dasselbe wie vorher: eine falsche, aber in sich stimmige
   Bestätigung („nie gewechselt“ für einen Kanal, der leise gewechselt hat) — die Grenze
   „Verantwortung, nicht Gewissheit“ gilt unverändert.
2. **Die Migrationsprobe T1.10** fährt genau diese Datei einmal vollständig vor, bevor sie ernst
   wird.

**Was das ablöst und warum.** Bis zum 2026-09-20 galten ungelistete Kanäle implizit als „nie
gewechselt", und der einzige Schutz dagegen war eine Entdeckungsprüfung mit Schwellen (≥ 10
Archivierungen oder ≥ 25 % des Bestands). Ein Wechsel zwischen stark überlappenden oder identischen
Sets bleibt unter diesen Schwellen oder erzeugt gar keine Archivierung; ein solcher Kanal lief
still durch, und die Migration schrieb **seine gesamte Historie auf das heute aktive Set**. Seit
der Klassifikation läuft **kein Kanal mehr stillschweigend durch**: jeder braucht eine Aussage,
und eine fehlende Aussage ist ein Abbruch mit Namen (Prüfung 3).

**Die Grenze dieser Entscheidung, ehrlich benannt:** ein Kein-Wechsel-Eintrag verschiebt die
**Verantwortung**, nicht die **Gewissheit**. Es gibt keine Historie, aus der sich „nie gewechselt"
beweisen ließe — vor dieser Migration hat niemand das beobachtete Set je aufgeschrieben, genau das
führt `ChannelEmoteSetObservation` (4.3) erst ein. Der Betreiber bestätigt aus dem Signaturtest
plus eigenem Wissen. Gewonnen ist nicht Beweisbarkeit, sondern dass jeder Kanal eine bewusste,
ausdrückliche Aussage trägt statt einer stillen Vorgabe. **Sichtbar ist diese Aussage seit dem
Ortswechsel der Liste nur noch für den Betreiber** — im PR-Diff steht sie nicht mehr (E1, oben).

**Woher die Kanalliste kommt.** Lückenlosigkeit lässt sich nur gegen eine Liste behaupten, die
jemand erhoben hat. Der Betreiber holt sie als **lesende Zählabfrage im selben Handgriff wie
Sonde 6** (Abschnitt 11, K0): je Kanal mit mindestens einer `UsageStat`-Zeile Name, `ChannelId`,
`TwitchChannelId`, `ActiveEmoteSetId` und Zeilenzahl. Ohne diese Abfrage kann niemand behaupten,
die Liste sei vollständig — die Migration prüft die Behauptung, sie erhebt sie nicht.

Der Testkanal und der Wegwerfkanal stehen in **keiner** der beiden Eintragsarten — beide sind vor
der Migration gepurgt (V2, V3), haben dann weder `Channel`-Zeile noch `UsageStats`, und es gibt
nichts zuzuordnen; sie tauchen deshalb auch in der Zählabfrage nicht mehr auf. Das ist eine andere
Liste als die **erwartete Trefferzahl der Gegenprobe** (Sonde 6: genau zwei Zeilen, Testkanal und
HandOfBlood) — die Gegenprobe läuft **vor** V2 und **nach** V3 und zählt deshalb den Testkanal
mit, den `SetSwitchAssignments` nie enthält.

**Reihenfolge innerhalb von `Up`, verbindlich:**

1. `SET LOCAL lock_timeout = '5s'` (E11).
2. **Zwei** temporäre Tabellen aus der Liste (`CREATE TEMP TABLE set_switch_assignments …` für die
   Wechseleinträge, `set_no_switch_assignments …` für die Kein-Wechsel-Einträge, beide
   `ON COMMIT DROP`, `INSERT … VALUES …`).
3. **Prüfung 1 — Endzustand** (erweitert die frühere Prüfung 1 auf **beide** Eintragsarten): jeder
   Eintrag findet seinen Kanal über `ChannelId` **und** `TwitchChannelId`; die `NewEmoteSetId` des
   jeweils letzten Wechseleintrags eines Kanals **bzw.** die `ConfirmedEmoteSetId` seines
   Kein-Wechsel-Eintrags ist gleich `Channels."ActiveEmoteSetId"`. Sonst
   `RAISE EXCEPTION 'set-switch assignment stale for channel %'`.
4. **Prüfung 2 — Grenztag-Signatur** (inhaltlich unverändert): an jedem `BoundaryUtc::date` steht
   die Signatur: `COUNT(*)` der `Emotes` des Kanals mit
   `("ArchivedAt" AT TIME ZONE 'UTC')::date = boundary_day` ist gleich `ExpectedArchivedCount`, und
   es gibt an diesem Tag **keinen** `AuditLogEntries`-Eintrag mit `Action = 'emotes.syncDeleted'`
   und diesem `ChannelName`. Sonst `RAISE EXCEPTION`.
5. **Prüfung 3 — Lückenlosigkeit** (ersetzt die frühere Prüfung 3): jeder Kanal mit mindestens
   einer `UsageStats`-Zeile hat einen Eintrag **einer der beiden Arten**. Sonst
   `RAISE EXCEPTION 'unclassified channel % (%) has usage rows'` — **mit Nennung des Kanals**,
   weil der Betreiber danach genau diesen einen Eintrag nachzutragen hat. Das ist die Prüfung, die
   Codex' Lücke schließt.
6. **Prüfung 4 — Widerspruch** (neu, die Umkehrung der früheren Prüfung 3): kein Kanal mit
   **Kein-Wechsel-Eintrag** schlägt am Signaturtest an — ein Tag mit ≥ 10 Archivierungen oder
   ≥ 25 % seines Bestands ohne `emotes.syncDeleted` am selben Tag (die Schwellen der Gegenprobe,
   Konzept 11.6). Sonst
   `RAISE EXCEPTION 'channel % confirmed unchanged, but shows a mass archive at %'`.
7. **Prüfung 5 — Kettenschluss** (neu, von Codex gefordert): trägt ein Kanal mehrere
   Wechseleinträge, sind ihre `BoundaryUtc` **streng aufsteigend sortiert** und die `NewEmoteSetId`
   des Eintrags *n* ist die `OldEmoteSetId` von *n+1*. Lücke oder Sprung ⇒
   `RAISE EXCEPTION 'set-switch chain broken for channel % at %'`.
8. **Prüfung 6 — leere `ActiveEmoteSetId`** (die frühere Prüfung 4, unverändert, neue Nummer):
   keine `UsageStats`-Zeile, deren Kanal eine leere `ActiveEmoteSetId` hat. Sonst
   `RAISE EXCEPTION`.
9. `ALTER TABLE "UsageStats" ADD COLUMN "EmoteSetId" text NULL` — **ohne Default**.
10. Backfill in **zwei** `UPDATE`s: (a) für Zeilen, deren Kanal einen **Wechseleintrag** hat, die
    Set-ID des Intervalls, in das `"Date"` fällt — `Date < boundary_day` → alt,
    `Date >= boundary_day` → neu (bei mehreren Grenzen: die jüngste Grenze ≤ Date entscheidet,
    davor die erste alte ID); (b) für Zeilen, deren Kanal einen **Kein-Wechsel-Eintrag** hat, dessen
    `ConfirmedEmoteSetId` **aus der Liste** — ausdrücklich **nicht** `Channels."ActiveEmoteSetId"`,
    auch wenn Prüfung 1 die beiden gleichgesetzt hat: die Quelle ist die bestätigte Aussage, nicht
    der heutige Zustand. Eine dritte Gruppe gibt es nach Prüfung 3 nicht mehr.
11. `ALTER TABLE "UsageStats" ALTER COLUMN "EmoteSetId" SET NOT NULL` — schlägt fehl, falls ein
    Backfill eine Zeile ausgelassen hat; das ist gewollt (kein Default, kein `COALESCE`).
12. `DROP INDEX "IX_UsageStats_EmoteId_Date"`; `CREATE UNIQUE INDEX … (EmoteId, EmoteSetId, Date)
    INCLUDE (UseCount)`.
13. `CREATE TABLE "ChannelEmoteSetObservations"` samt beiden Indizes; **Saat** (4.3).
14. `VoteSessions."EmoteSetId"`, `VoteSessionEmotes."NameAtCreation"/"ImageUrlAtCreation"` (additiv,
    nullbar).

Alles in **einer** Transaktion (EF-Standard); ab Schritt 9 hält sie `ACCESS EXCLUSIVE` auf
`UsageStats`, bis sie committet — der Grund, warum die Api im Fenster steht (Konzept 12.3, D3).

**Sechs Prüfungen, und wie sie aus den bisherigen vier hervorgehen** (die Nummern haben sich
verschoben; wer alte Notizen liest, braucht diese Tabelle):

| bisher | jetzt | was sich geändert hat |
|---|---|---|
| Prüfung 1 (Endzustand) | **Prüfung 1** | gilt jetzt für **beide** Eintragsarten, nicht nur für Wechsler |
| Prüfung 2 (Grenztag-Signatur) | **Prüfung 2** | unverändert |
| Prüfung 3 (Massenarchivierung außerhalb der Liste) | **Prüfung 3** (Lückenlosigkeit) | ersetzt — die alte Bedingung lebt als Prüfung 4 weiter, mit umgekehrter Rolle |
| — | **Prüfung 4** (Widerspruch) | neu |
| — | **Prüfung 5** (Kettenschluss) | neu |
| Prüfung 4 (leere `ActiveEmoteSetId`) | **Prüfung 6** | unverändert, neue Nummer |

**Der Rollentausch ist der Kern der Entscheidung.** Die Schwelle aus Konzept 11.6 bleibt Wort für
Wort dieselbe, aber ihre Aufgabe dreht sich um. Bis zum 2026-09-20 sollte sie einen Wechsel
**entdecken**, den niemand gemeldet hatte — eine Entdeckung, die unterhalb der Schwelle
systematisch ausfällt und die deshalb als alleiniger Schutz nicht taugte. Jetzt **widerspricht**
sie einer ausdrücklichen Bestätigung: der Betreiber hat „nie gewechselt" gesagt, die Daten zeigen
eine Massenarchivierung ohne `emotes.syncDeleted` am selben Tag. In dieser Rolle darf sie grob
sein. **Ein Fehlalarm ist hier billig** — der Betreiber sieht nach und korrigiert entweder den
Eintrag (es war doch ein Wechsel) oder bestätigt, dass es ein Massen-Edit war; ein *verpasster*
Wechsel ist dagegen nicht mehr das Loch, das er war, weil Prüfung 3 jeden Kanal ohnehin zu einer
Aussage zwingt.

**Ein-Tages-Unschärfe (verbindlich benannt, nicht kaschiert):** die Tageszeile des Grenztags kann
Nutzung beider Phasen enthalten und geht **ganz** an die neue Set-ID. Der DECISIONS-Eintrag 1 nennt
das als bekannte Unschärfe von höchstens einem Tag je Grenze.

**`Down` — zwei unabhängige Schranken, keine ersetzt die andere.** `Down` entfernt die drei
additiven Spalten und die Tabelle und stellt `(EmoteId, Date) INCLUDE (UseCount)` wieder her. Davor
stehen zwei Prüfungen, die **verschiedene Dinge** prüfen und deshalb **beide** stehenbleiben:

1. **Das Beobachtungs-Log, ausdrücklich geprüft — vor jedem zerstörenden Schritt**, also vor dem
   ersten `ALTER TABLE … DROP COLUMN`, vor `DROP TABLE "ChannelEmoteSetObservations"` und vor dem
   Indextausch: existiert **irgendeine** Zeile in `ChannelEmoteSetObservations` mit
   `ClosedBy = 'set-switch'`, bricht `Down` mit
   `RAISE EXCEPTION 'observed set switch after migration; rolling back would discard EmoteSetId'`
   ab. Der Test auf `'set-switch'` **ist** der Test auf „nach dem Migrationszeitpunkt", ohne dass
   `Down` einen Zeitstempel kennen muss: die Saat (4.3) schließt historische Intervalle
   ausschließlich mit `ClosedBy = 'migration'`, und `'set-switch'` schreibt allein der laufende
   Sync-Pfad (`emoteSetSwitched`). Jede solche Zeile ist damit per Konstruktion nach dem Deploy
   entstanden. **Auflage an 4.3 und an T1.3b:** die Saat darf `'set-switch'` nie vergeben — sonst
   sperrt diese Schranke sich selbst zu.
2. **Die Kollision des alten Unique-Index**, wie bisher: trägt ein `(EmoteId, Date)` Zeilen unter
   zwei Set-IDs, scheitert `CREATE UNIQUE INDEX "IX_UsageStats_EmoteId_Date"`.

Warum beide: Schranke 2 ist ein **Stellvertreter**, kein Synonym. Ein beobachteter Wechsel erzeugt
nicht zwangsläufig zwei Zeilen an demselben `(EmoteId, Date)` — der Wechsel kann zwischen zwei Tagen
liegen, die beiden Sets können disjunkt sein, oder ein gemeinsames Emote wurde nur auf einer Seite
benutzt. In all diesen realistischen Fällen ließe sich der alte Index anlegen und `Down` verwürfe
`EmoteSetId` **still**, obwohl die Rückrollgrenze längst überschritten ist; genau das fängt Schranke
1. Umgekehrt fängt Schranke 2 Fälle, die das Log nicht kennt — eine Kollision aus einer Quelle
außerhalb des `emoteSetSwitched`-Pfads (Saat mit mehreren Intervallen aus der Zuordnungsliste, ein
von Hand gesetztes Set, ein Log, das die #76-Blockade offengelassen hat, Abschnitt 22). Keine der
beiden deckt die andere ab.

Das ist die Rückrollbarkeitsgrenze aus Abschnitt 17; ein Summieren je `(EmoteId, Date)` ist ein
Handgriff des Betreibers, keine Migration — und er löst nur Schranke 2. Schranke 1 löst der
Betreiber, indem er die `'set-switch'`-Zeilen bewusst entfernt; dass er das tun muss, ist die
Absicht: es ist genau der Moment, in dem der Verlust der Set-Zuordnung eine Entscheidung wird statt
einer Nebenwirkung.

### 4.3 `ChannelEmoteSetObservation` — Öffnen, Schließen, Saat

Eine Zeile ist ein **Intervall, in dem EmotePurge dieses Set für diesen Kanal als aktiv beobachtet
hat**. Der Zeitstempel ist „wann wir es bemerkt haben", nicht „wann es geschah" (Konzept 4.4). Ein
Service `IChannelEmoteSetObservationService` (Core) / Implementierung (Infrastructure) trägt die
Regeln; kein Aufrufer schreibt die Tabelle direkt.

| Anlass | Wirkung | Wo |
|---|---|---|
| erfolgreicher `SyncChannelAsync`, keine offene Zeile | **öffnen** mit `ObservedFromUtc = now`, Set = gemeldete ID — auch wenn die zuletzt geschlossene dieselbe ID trug | nach `:86`, in derselben `SaveChangesAsync` wie `:108` |
| `emoteSetSwitched` | alte Zeile **schließen** (`ClosedBy = 'set-switch'`), neue öffnen, eine Transaktion | `SevenTvSyncService.cs:83-86` |
| `LeaveAsync` | schließen, `ClosedBy = 'leave'` | `ChannelService.cs:64` |
| Rename beim Join; `ChannelIdentityService.RenameAsync` | schließen (`'rename'`); der nächste erfolgreiche Sync öffnet neu | `ChannelService.cs:244-247`, `ChannelIdentityService.cs:338-342` |
| Merge | Verlierer kaskadiert; Überlebender schließt (`'merge'`), nächster Sync öffnet neu | `ChannelIdentityService.cs:448-450` |
| Purge | kaskadiert | `ChannelService.cs:105` |
| Delta-Pfad `ApplyEmoteSetUpdateAsync` | **schreibt nichts** | `:157-163` verwirft fremde Sets ohnehin |
| #76-Blockade (`TryGuardAgainstImplausibleWipeAsync`) | **nichts** — die alte Zeile bleibt offen (Abschnitt 22) | `:74-78` läuft vor `:83` |

**Saat durch die Migration**, aus derselben Eingabe wie 4.2: je Kanal mit nicht-leerer
`ActiveEmoteSetId` und `IsBotActive = true` — mit **Kein-Wechsel-Eintrag** eine offene Zeile für
dessen `ConfirmedEmoteSetId` ab `COALESCE("TrackingResumedAt", "CreatedAt")`; mit
**Wechseleinträgen** je Intervall eine Zeile (die erste ab `COALESCE(TrackingResumedAt,
CreatedAt)`, jede außer der letzten geschlossen mit `ObservedToUtc = BoundaryUtc`,
`ClosedBy = 'migration'`, die letzte offen). Inaktive Kanäle bekommen **keine** Zeile — auch dann
nicht, wenn sie `UsageStats` tragen und deshalb in 4.2 klassifiziert werden **müssen**: die
Klassifikation entscheidet über die Zuordnung der Zahlen, die Saat über beobachtete Intervalle, und
das sind zwei verschiedene Fragen (s. „Zweck ausdrücklich begrenzt" unten). Seit der lückenlosen
Klassifikation gibt es den früheren dritten Fall „Kanal ohne Listeneintrag" nicht mehr.
Die Saat vergibt als `ClosedBy` **ausschließlich** `'migration'` und **nie** `'set-switch'` — die
erste `Down`-Schranke aus 4.2 hängt genau daran, dass `'set-switch'` allein vom laufenden Sync-Pfad
kommt und damit per Konstruktion „nach dem Deploy" bedeutet.

**Zweck ausdrücklich begrenzt:** Preset „während dieses Set beobachtet wurde" (8.5), Tatsachenangabe
(8.4), spätere Backfill-Automatik (#69, nur innerhalb eines Intervalls). **Nicht** die Zuordnung
der Zahlen — die trägt `UsageStat.EmoteSetId` im Zählmoment.

---

## 5. Vertrag: Zählpfad — das lokal beobachtete Set reist mit dem Match-Cache

**Bedeutung des Feldes (Konzept 5.2, B8):** `UsageStat.EmoteSetId` ist das Set, für das der
Match-Cache im Moment des Matches gebaut war — gegenüber unserem Zustand eindeutig, gegenüber 7TV um
die Beobachtungsverzögerung versetzt (Sekunden über `user.*`, ≤ 60 s im REST-Fall, 10–30 min bei
7TVs REST-Cache-Lag, **unbefristet** während einer #76-Blockade). Ein Wechsel innerhalb eines
30-s-Flush-Fensters erzeugt zwei Schlüssel und zwei Zeilen, getrennt am **Cache-Tausch**.

**Signaturen (Core-Verträge, Regel 4/5):**

```
// Core/Services/IEmoteMatchCache.cs
readonly record struct EmoteMatchSnapshot(
    IReadOnlyDictionary<string, string> NameToEmoteId,
    string EmoteSetId,              // "" nur für den leeren Snapshot; der Chat-Pfad bricht dann ab wie heute bei Count == 0
    DateTimeOffset GeneratedAtUtc);

void ReplaceChannel(string channelName, string emoteSetId, IReadOnlyDictionary<string, string> nameToEmoteId);
void RemoveChannel(string channelName);
EmoteMatchSnapshot GetChannelSnapshot(string channelName);   // ersetzt GetChannelEmotes

// Worker/IEmoteUsageCounter.cs
readonly record struct UsageCounterKey(string EmoteId, string EmoteSetId);
void Increment(string emoteId, string emoteSetId, UsageCategory category);
void Merge(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> counts);
IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> DrainAndReset();
int PendingEmoteCount { get; }   // zählt weiterhin Schlüssel

// Core/Services/IUsageStatFlushService.cs
Task<IReadOnlyCollection<string>> FlushAsync(IReadOnlyDictionary<UsageCounterKey, EmoteUsageCounts> usageCounts, CancellationToken ct = default);
```

`UsageCounterKey` liegt in **Core** (neben `EmoteUsageCounts`, `IUsageStatFlushService.cs:26`), weil
Worker und Infrastructure ihn teilen; `IEmoteUsageCounter` bleibt im Worker (`IEmoteUsageCounter.cs:3`).

**Regeln:**

1. `TwitchChatManager` liest **einen** Snapshot je Nachricht (`:1024` wird `GetChannelSnapshot`) und
   zählt alle Treffer dieser Nachricht unter `(emoteId, snapshot.EmoteSetId)`. Ein Cache-Tausch
   zwischen zwei Nachrichten trennt sie; innerhalb einer Nachricht ist er unmöglich.
2. `RefreshMatchCacheAsync` (`:403-437`) übergibt `channel.ActiveEmoteSetId` an `ReplaceChannel`
   (`:436`); der Warmstart (`:280-296`) ebenso — beide haben die Kanalzeile in der Hand. **Log-Zeile
   beim Tausch** mit anderer Set-ID: `"Match cache for {Channel} switched from set {OldSetId}
   (generation {OldGeneratedAt}) to {NewSetId} (generation {NewGeneratedAt})"` auf `Information` —
   damit die Unschärfe im Betrieb messbar ist.
3. `EmoteUsageCounter` behält das `TArg`-Muster (`:10-14`); der Schlüssel ist ein `readonly record
   struct`, keine String-Konkatenation.
4. `UsageStatFlushService.FlushAsync`: Validierung weiter über `Emote.Id` (`:29-32`; die Projektion
   liefert jetzt je **Schlüssel** eine Zeile, Kanalname wie heute), vier `UNNEST`-Arrays
   (`@emoteIds text[]`, `@emoteSetIds text[]`, `@useCounts`, `@botUseCounts`,
   `@sharedChatUseCounts` — fünf Parameter plus `@date`), Conflict-Target dreispaltig. Das
   Zurückstellen fehlgeschlagener Batches (`UsageFlushWorker.cs:89-91`) bleibt korrekt, weil der
   Schlüssel das Set konserviert.
5. `WorkerStats.RecordFlushSuccess(counts.Count, …)` (`UsageFlushWorker.cs:82`) zählt Schlüssel wie
   heute.

**Verworfen (Konzept):** Set-ID erst im Flush aus `Channel.ActiveEmoteSetId` nachschlagen — bis
30 s, bei zurückgestellten Batches bis 2,5 min Verschiebung, systematisch falsch im Wechselmoment.

**Wechsel-Tests (Regel 11, `Infrastructure.Tests/Integration` + `Worker.Tests`):** Nachrichten vor,
während und nach dem Cache-Tausch — erste Gruppe altes Set, dritte neues, für die mittlere ist der
Tausch die einzige Grenze; dazu ein gleichnamiges Paar (`Stare` auf Zeile A vor, Zeile B nach dem
Tausch, nie beide). Details in Abschnitt 15.

**Lesepfade (`UsageStatQueryService`)** bekommen `string? emoteSetId`; `null` heißt
`Channel.ActiveEmoteSetId`:

| Methode | Änderung |
|---|---|
| `GetUsageContextAsync` (`:26-104`) | Set-Filter in der Ein-Tabellen-Abfrage (`:74-84`) **nach** der ID-Liste (`:43-53`, Regel 10). Aktives Set: `!IsArchived`-Liste, zero-filled wie heute. Nicht-aktives Set: Grundmenge = Zeilen des Kanals (archivierte eingeschlossen) mit ≥ 1 `UsageStat` unter `emoteSetId` (E16). `EmoteUsageContextDto.EmoteId` bleibt hier nicht-null (jede DB-Zeile hat eine); die `null`-Nutzlast entsteht erst in der Vereinigung im Frontend (Abschnitt 7). **Neu:** `NameTwinEmoteSetIds` (E24), `IsArchived` |
| `GetDailySeriesAsync` (`:106-171`) | Set-Filter auf `days` (`:133-137`) und `bounds` (`:142-150`); Aufruf weiter per Guid |
| `GetChannelSeriesAsync` (`:173-236`) | Set-Filter auf `rows` (`:222-226`); `EmoteSeriesEntryDto(string SevenTvEmoteId, …)` — Projektion liest `e.SevenTvEmoteId` aus der ID-Liste (`:195-198` projiziert `Id` **und** `SevenTvEmoteId`, in-memory gemappt); Grundmenge für ein nicht-aktives Set wie bei `/totals` |
| `GetTotalsByEmoteIdsAsync` (`:238-264`) | `string emoteSetId` Pflichtparameter (Aufrufer: `VoteSessionQueryService.cs:101-103`, übergibt `session.EmoteSetId ?? channel.ActiveEmoteSetId`) |
| `GetRowsAsync` (`:326-352`) | `SUM` je `(EmoteId, Date)` über alle Set-IDs (E15); DTO unverändert |
| `GetUsageStatsAsync` (`:12-24`, Debug-Rohliste) | unverändert; listet je Set-ID eine Zeile |
| `GetEarliestBotUsageDateAsync`, `GetEarliestSharedChatUsageDateAsync` (`:266-304`) | unverändert (set-übergreifend, gewollt) |

---
## 6. Vertrag: Api

Alle Shapes in `System.Text.Json`-Web-Defaults (camelCase), wie `ForeignEmoteSet`
(`IForeignEmoteSetService.cs:117-121`). Filter- und Policy-Angaben sind **getestete** Verträge
(Regel 11, `tests/EmotePurge.Api.Tests`). Neue Fehlercodes: E13.

### 6.1 `GET /api/channels/{channelName}/emote-sets` — Set-Liste eines getrackten Kanals

Gruppe `/api/channels/{channelName}/emotes`-Nachbar in `EmoteEndpoints.cs`: dieselben Filter wie
`/emotes` (`RequireAuthorization`, `ChannelNameValidationFilter`, `UsageStatsAccessAuthorizationFilter`,
`InteractiveRead`, `:24-29`), Route `/api/channels/{channelName}/emote-sets` (eigene `MapGet` in
derselben Datei, weil sie die Gruppe teilt — kein zweiter Filtersatz).

```
EmoteSetListResponse {
  activeEmoteSetId: string                 // Channel.ActiveEmoteSetId, "" vor dem ersten Sync
  sets: EmoteSetSummary[]                  // aktives zuerst, dann ordinal nach name
}
EmoteSetSummary {
  id: string
  name: string
  capacity: int | null                     // 0 → null wie SevenTvApiClient.cs:704
  kind: string                             // 7TVs EmoteSetKind, ordinal durchgereicht: NORMAL | PERSONAL | GLOBAL | SPECIAL (E7)
  isActive: bool                           // == Channel.ActiveEmoteSetId (E21)
  isPersonal: bool                         // kind == PERSONAL (E7) — nur für die Beschriftung; wählbar ist kind == NORMAL (8.6)
  ownerDisplayName: string | null          // owner.mainConnection.platformDisplayName (E7) — Anzeigename, kein Login; nur Anzeige, nie Abgleich
  observations: { fromUtc: string, toUtc: string | null }[]   // aus ChannelEmoteSetObservations, aufsteigend; [] wenn nie beobachtet
}
```

| Zustand | Antwort |
|---|---|
| Kanal unbekannt | 404 (bare, wie `/emotes :44`) |
| `TwitchChannelId` null (noch kein Sync) | 200, `sets: []`, `activeEmoteSetId: ""` |
| 7TV kennt den Account nicht — `userByConnection: null` bei **HTTP 200 ohne `errors`-Block** (gemessen am 2026-09-20 mit `platformId: 999999999999`, Analyzer `complexity 7, depth 4`) ⇒ `NoSevenTvAccount` | 200, `sets: []` |
| 7TV nicht erreichbar / 429 / Budget verweigert / Antwort fehlt / `userByConnection` ist da, trägt aber kein `emoteSets` ⇒ `Unavailable` | 503 `foreign_channel_seventv_unavailable` |

Quelle: `ISevenTvEmoteSetListService.ListByTwitchIdAsync(twitchChannelId)` → v4
`userByConnection(platform: TWITCH, platformId:)` (E7). `observations` kommt aus der Datenbank, nie
gecacht. Das `style.activeEmoteSetId` derselben Antwort bleibt hier **ungenutzt**: für einen
getrackten Kanal ist `Channel.ActiveEmoteSetId` die Quelle (E21).

**Härtung des Listen-Dienstes — derselbe Vertrag wie der Vorschaupfad, kein Nachbau.** Ein Cache und
ein Permit sind **nicht** genug: ohne Koaleszierung treffen gleichzeitige kalte Cache-Misses alle
v4, ohne Breaker wiederholt jeder Aufruf denselben Fehler, und ein v4-Ausfall kann so das
**gemeinsame** 60/min-Budget leeren und damit auch die Fremdkanal-Vorschau in 503 drücken. Der
Listen-Dienst läuft deshalb hinter derselben Wächterkette und in derselben Reihenfolge wie
`HardenedForeignEmoteSetService` (`HardenedForeignEmoteSetService.cs:76-150`):

| Wächter | Baustein | Wiederverwendung |
|---|---|---|
| 1. Cache | `IForeignEmoteSetCache`-Muster, Schlüsselraum `7tvsets:{twitchId}`, 60 s, fail-open (E12) | eigener Schlüsselraum im bestehenden Cache-Idiom (`ForeignEmoteSetCache.cs:17-20,24-25`) |
| 2. Singleflight | `ForeignEmoteSetRequestCoalescer` | **nur nach einer Anpassung wiederverwendbar:** die Klasse ist heute auf `ForeignEmoteSetLookupResult` festgelegt (`:31`, `:38`). Vorgabe: generisch schließen (`…Coalescer<TResult>`) und je Ergebnistyp eine geschlossene Singleton-Registrierung — genau das Muster, das `SevenTvLeaderboardStore<TValue>` (`:68`) bereits fährt („generic on purpose … without dragging in the … service's own result types"). Verhalten des Vorschaupfads bleibt bitgleich: er schließt den Typ auf seinen bisherigen |
| 3. Breaker | `ForeignSevenTvBreakerPolicy` — **dieselbe** typisierte Singleton-Instanz wie der Vorschaupfad (`ServiceCollectionExtensions.cs:106`), nicht die keyed zweite der Bestenliste (`:128`) | unverändert. Begründung: die Bestenliste bekam eine eigene Instanz, weil sie ein **eigenes** Budget hat (`SevenTvLeaderboardRequestBudget`); der Listen-Dienst teilt das Provider-Budget mit der Vorschau, also teilt er auch den Breaker. Ein v4-Ausfall soll beide Pfade anhalten, nicht nur einen |
| 4. Budget | `ForeignEmoteSetProviderBudget` — Nebenläufigkeits-Slot je Aufruf (`TryAcquireConcurrencySlotAsync`, `:109`, `MaxConcurrent = 2`) **und** ein Permit je Upstream-Request über `IForeignUpstreamRequestBudget` (`:16`, `:119`/`:126`, 60/min, F14, AK 24) | unverändert, beide Gesichter derselben Instanz (`ServiceCollectionExtensions.cs:107,113`) |
| 5. Request | `SevenTvApiClient.FetchV4PageAsync` (`:552`) | unverändert — es erkennt **beide** 429-Gestalten: HTTP 429 (`:566-574`) und den als HTTP 200 getarnten GraphQL-429 über `errors[].extensions.status == 429` (`IsRateLimited`, `:842-843`, ausgewertet `:597-608`) |

**Fehler werden gecacht, nicht wiederholt.** Ein negatives Ergebnis (Unavailable, RateLimited,
Breaker offen, Budget verweigert) bekommt eine **eigene, kurze** Haltbarkeit im selben Schlüsselraum,
nach dem Muster von `SevenTvLeaderboardTtlPolicy` (`:87-118`: Unavailable 60 s, RateLimited
≥ 60 s mit `Retry-After`, Budget verweigert 30 s). `NoSevenTvAccount` ist **kein** Fehler, sondern
eine Antwort, und wird wie ein Treffer gehalten (60 s). Ohne diese Regel fragt jede Wiederholung
eines Dialogs während eines Ausfalls erneut v4 an.

**Degradierte Sicht.** Antwortet 6.1 mit 503, fällt die Nutzungsseite auf das **aktive** Set zurück —
das kennt sie unabhängig von dieser Route aus `setStatus()?.activeEmoteSetId`
(`usage-stats-page.ts:337`). Der Dropdown-Auslöser ist dann gesperrt mit Grund („Set-Liste nicht
verfügbar"), die Seite zeigt unverändert die Zahlen des aktiven Sets; kein Banner, kein Wiederholen
im Hintergrund (8.1).

### 6.2 `GET /api/seventv/me/emote-set-targets` — Angebotsliste des Ziel-Pickers

Neue `MapGroup` `/api/seventv/me` in `SevenTvEndpoints.cs`: `RequireAuthorization()`, **kein**
Kanalfilter, Policy `ForeignEmoteLookup` (10/min je Nutzer, `RateLimitingOptions.cs:56`) — ein
Dialog-Öffnen ist ein Aufruf, und die Policy bemisst genau „ein Nutzer zieht 7TV-Requests".

```
EmoteSetTargetsResponse {
  accounts: EmoteSetTargetAccount[]        // eigener Account zuerst, dann editor_of ordinal nach twitchLogin
  sevenTvUnavailable: bool                 // true, wenn Grants oder mindestens eine Set-Liste nicht lesbar waren
}
EmoteSetTargetAccount {
  twitchChannelId: string
  twitchLogin: string                      // Twitch-Login aus dem Grant bzw. dem Principal — Sortierung und Papierspur (6.7), NICHT der angezeigte Besitzer
  isOwnAccount: bool
  trackedChannelName: string | null        // Channel-Zeile mit dieser TwitchChannelId und IsBotActive = true, sonst null
  activeEmoteSetId: string | null          // getrackt: Channel.ActiveEmoteSetId; sonst style.activeEmoteSetId derselben v4-Antwort (E7/E21) — kein zweiter Request
  sets: EmoteSetSummary[]                  // ohne `observations`; der angezeigte Besitzer steht je Set in ownerDisplayName
  setsUnavailable: bool                    // dieser Account konnte nicht gelesen werden; sets = []
}
```

Zusammensetzung: eigener Account = `ResolveSevenTvIdentityAsync(principal.TwitchUserId)` ist **nicht**
nötig — die Twitch-ID des Principals genügt für `userByConnection` (E7); `editor_of` aus
`ISevenTvEditorService.GetEditorGrantsAsync` (`SevenTvEditorService.cs:14-57`, gecacht). Grants
`Failed` ⇒ `accounts` nur der eigene, `sevenTvUnavailable: true`. Ein Account, dessen Twitch-ID
einen getrackten, aktiven Kanal hat, bekommt `trackedChannelName`; die Klassenzuordnung des Pickers
(getrackt/ungetrackt) liest allein dieses Feld. `listMine()` bleibt für die Übersicht unberührt.

### 6.3 `GET /api/seventv/channels/{channelName}/emote-sets` — Set-Liste eines fremden Kanals (K3)

In der bestehenden Gruppe `/api/seventv/channels/{channelName}` (`SevenTvEndpoints.cs:27-30`;
Filter `RequireAuthorization`, `ChannelNameValidationFilter`, `ForeignEmoteLookup`). Auflösung wie
die Fremdkanal-Vorschau (Helix by login → Twitch-ID, `ForeignEmoteSetService.cs:45 ff.`), dann
derselbe Listen-Dienst. Antwort `EmoteSetListResponse` mit `activeEmoteSetId` aus
`style.activeEmoteSetId` **derselben v4-Antwort** (E7/E21 — dieser Pfad löst über Helix auf und
schlägt keine `Channel`-Zeile nach, nimmt also 7TVs Sicht; kein zweiter Request, kein v3-Aufruf),
`observations: []`. Zustände wie `GET …/emotes` (`:47-69`): `ChannelNotOnTwitch` 404,
`TwitchUnavailable` 503, `NoSevenTvAccount` 404, `SevenTvUnavailable`/`RateLimited`/Budget 503.

### 6.4 `GET /api/seventv/channels/{channelName}/emotes?emoteSetId=…` — Set-Vorschau nach Set-ID

Bestehender Endpunkt (`SevenTvEndpoints.cs:32-70`) mit neuem optionalem Query-Parameter neben
`refresh`. Validierung durch `EmoteSetIdValidationFilter` (E14) → 400 `invalid_emote_set_id`. Mit
`emoteSetId`: **keine** Helix-/Identitätsauflösung, `HardenedForeignEmoteSetService` liest den
Schlüsselraum `7tvforeign:set:{setId}` (E12), Coalescing-Schlüssel `set:{setId}`, Breaker und
Budget wie heute (`:99-135`), Preview-Abfrage mit `capacity` und `name` (F6).

```
ForeignEmoteSet {                          // bestehend, IForeignEmoteSetService.cs:123-129, drei Felder geändert/neu
  channelName: string                      // Routenkanal, echo
  sevenTvUserId: string | null             // null im Set-ID-Modus (E8)
  emoteSetId: string
  emoteSetName: string | null              // neu; null, wenn 7TV keinen Namen liefert
  capacity: int | null                     // neu (F6)
  totalCount: int                          // Einträge inkl. #74-Duplikaten — occupiedSlots des Ziels
  truncated: bool
  emotes: ForeignEmoteRow[]                // jeden Eintrag behalten, nicht nach ID dedupliziert (Konzept 7.5)
}
```

`truncated: true` ist für den Ziel-Loader `failed` (8.6), für die Set-Ansicht ein Hinweis mit
`totalCount` (8.3). Ein unbekanntes Set (7TV `emoteSet: null`) → 404
`foreign_channel_no_active_emote_set` (vorhandener Code; der Text „kein aktives Emote-Set" ist für
diesen Fall unscharf, aber ein fünfter Code für einen Zustand, den nur ein manipulierter Query
erzeugt, ist Vokabular ohne Nutzen).

### 6.5 Set-Filter an `/usage-stats/{totals,daily,series}`

`UsageStatsEndpoints.cs:35-100`: optionaler Query-Parameter `emoteSetId` an allen drei Routen,
validiert durch `EmoteSetIdValidationFilter` (400 `invalid_emote_set_id`); fehlt er, gilt
`Channel.ActiveEmoteSetId`. Die Range-Leiter (`:107-128`) bleibt davor.

```
GET /totals?from&to[&emoteSetId]   → EmoteUsageContextDto[]     // + isArchived: bool, nameTwinEmoteSetIds: string[]; emoteId bleibt string (DB-Zeile)
GET /daily?emoteId&from&to[&emoteSetId]   → EmoteUsageSeriesDto   // unverändert bis auf den Filter
GET /series?from&to[&emoteSetId]   → ChannelUsageSeriesDto        // emotes[] trägt sevenTvEmoteId UND, übergangsweise, emoteId
```

**Wire-Format-Änderung an `/series` — additiv, in zwei Schritten.** Der Übergang folgt demselben
Muster wie die alte Body-Form `{ emoteIds }` von `sync-deleted`/`sync-restored` (E3): erst beide
Formen nebeneinander, das Fallenlassen der alten in einem **eigenen Schritt danach**. Die beiden
Übergänge sind damit ein Muster, keine zwei.

- **Schritt 1 (K1/T1.6):** `EmoteSeriesEntryDto(string SevenTvEmoteId, string EmoteId, IReadOnlyList<int[]> Days)`
  — `sevenTvEmoteId` **neu**, `emoteId` bleibt mit unverändertem Inhalt (die `Emote.Id`-Guid der
  DB-Zeile) daneben stehen. Ein Eintrag ohne DB-Zeile kann `/series` ohnehin nicht liefern (AK 20),
  also ist `emoteId` in Schritt 1 nie leer und das alte Frontend liest weiter, was es kennt.
- **Schritt 2 (Folge-Issue 5, nach K4):** `emoteId` fällt aus dem DTO, aus dem Frontend-Modell und
  aus den E2E-Mocks. **Eigener Schritt, eigener Commit, nicht im Merge von K4** — analog E3.
- `ChannelUsageSeriesWireFormatTests.Days_SerializeAsPairsOfNumbers_NotObjects` (`:23-37`) prüft ab
  Schritt 1 **beide** Felder; der zweite Fall (`:39-49`) bleibt. **Damit wird kein bestehender Test
  rot** — die frühere Fassung ließ ihn bewusst rot werden (F4); das ist mit dem additiven Weg
  gegenstandslos.

**Warum additiv, obwohl der Integrationsbranch nie deployt wird.** K1 mergt vor K4
(Abhängigkeitsgrafik des Plans), und zwischen beiden liegt ein Zwischenstand, in dem das noch nicht
umgestellte Frontend `entry.emoteId` als `undefined` läse und leere oder falsch zugeordnete Serien
renderte. Aus diesem Zwischenstand laufen die **Live-Verifikationen** des Plans (T2.7, T5.3, Regel
16): eine kaputte Serienansicht wäre dort Rauschen, das einen echten Befund verdecken kann. Der
additive Weg kostet ein Feld und macht diesen Zwischenstand benutzbar.

### 6.6 `sync-deleted` / `sync-restored` — neuer Body, Altform übergangsweise

`EmoteEndpoints.cs:47-115`; Filter und Policy `Bookkeeping` unverändert (`:79`, `:115`).

```
SyncDeletedRequest {                       // ein Record, zwei Formen
  emoteIds?: string[]                      // Altform: aktives Set, Guid-Match (heutiges Verhalten, E3)
  emoteSetId?: string                      // neue Form
  sevenTvEmoteIds?: string[]               // neue Form
}
SyncDeletedResponse {
  archivedCount: int                       // Zielzustand-Zähler wie heute; 0 im Papier-Fall
  notFoundIds: string[]                    // Guids (Altform) bzw. 7TV-Ids (neue Form)
  targetIsActiveSetOfChannel: bool         // neu; Altform: true
}
```

Validierungsleiter (400 vor jedem Service-Aufruf):

| Body | Antwort |
|---|---|
| beide Listen leer oder fehlend | 400 `emote_ids_empty` (heute `:56-59`) |
| `emoteIds` **und** `sevenTvEmoteIds` nicht leer | 400 `emote_ids_invalid` (vorhanden, `ApiErrorCodes.cs:14`) |
| `sevenTvEmoteIds` nicht leer, `emoteSetId` fehlt/leer | 400 `emote_set_id_empty` |
| `emoteSetId` mit ungültigem Format | 400 `invalid_emote_set_id` |

Service (`EmoteService.MarkDeletedAsync`, `:10-59`; `MarkRestoredAsync`, `:61-105`) bekommt eine
Überladung mit `(channelName, emoteSetId, sevenTvEmoteIds, actor)`:

- `emoteSetId == channel.ActiveEmoteSetId` → Match über `(ChannelId, SevenTvEmoteId)` statt
  `emoteIds.Contains(e.Id)` (`:25`, `:75`) — dank Unique-Index (`AppDbContext.cs:30`) dieselbe
  Präzision; archivieren/reaktivieren und Audit wie heute, Audit-Details zusätzlich `emoteSetId`,
  `targetIsActiveSetOfChannel: true`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`.
- `emoteSetId != channel.ActiveEmoteSetId` → **nur Papier**: keine Zeile berührt,
  `emotes.syncDeleted`/`emotes.syncRestored` mit `{ emoteCount: <Anzahl 7TV-Ids, dedupliziert>,
  emoteSetId, targetIsActiveSetOfChannel: false }`, `TargetType`/`TargetId` wie oben; Antwort
  `archivedCount: 0, notFoundIds: [], targetIsActiveSetOfChannel: false`. `channel.synced`
  (`:68-71`, `:106-109`) feuert nicht — es bleibt an `NewlyArchivedCount > 0` gebunden.
- Altform → heutiger Pfad, plus die Log-Zeile aus E3.

### 6.7 `sync-imported` — `TargetEmoteSetId` und der set-zentrierte Endpunkt

`SyncImportedRequest` (`EmoteEndpoints.cs:283-284`) bekommt `string? TargetEmoteSetId = null` (E5),
validiert durch den Set-ID-Filter, wenn gesetzt. `MarkImportedAsync` (`EmoteService.cs:107-138`)
schreibt zusätzlich `targetEmoteSetId` und `targetIsActiveSetOfChannel` (Vergleich mit
`channel.ActiveEmoteSetId` im Schreibmoment, `null` bei fehlendem Feld) in `DetailsJson`, dazu
`TargetType = "emoteSet"`, `TargetId = targetEmoteSetId` (nur wenn gesetzt). Die Vokabeltabelle
(`:171-195`) bleibt unverändert.

**Neu: `POST /api/seventv/emote-sets/{emoteSetId}/sync-imported`** — in einer neuen `MapGroup`
`/api/seventv/emote-sets/{emoteSetId}` (`RequireAuthorization()`, `EmoteSetIdValidationFilter`,
Policy `Bookkeeping`). Body = `SyncImportedRequest` ohne `TargetEmoteSetId` (die Route trägt es),
dieselbe Vokabeltabelle wie `:146-195` (herausgezogen in eine gemeinsame statische Prüfmethode, damit
sie nicht zweimal existiert).

| Reihenfolge | Wirkung |
|---|---|
| 1 Middleware | nicht eingeloggt → 401; Bookkeeping-Budget → 429 |
| 2 `EmoteSetIdValidationFilter` | ungültige Set-ID → 400 `invalid_emote_set_id` |
| 3 Body-Vokabeltabelle | wie `:129-195` (400 `emote_ids_empty` / `invalid_source_kind` / `invalid_channel_name` / `invalid_leaderboard_sort`) |
| 4 Besitzer-Prüfung (E22) | `GetEmoteSetOwnerIdAsync(emoteSetId)` null → 404 `emote_set_not_found`; Besitzer ∉ {7TV-ID des Akteurs} ∪ {7TV-IDs der `editor_of`-Grants} → **403 bare** (`Results.Forbid()`, wie die vier Autorisierungsfilter); 7TV nicht erreichbar → 503 `foreign_channel_seventv_unavailable`, **kein** Eintrag |
| 5 Service | `IEmoteService.MarkImportedToSetAsync(emoteSetId, ownerSevenTvUserId, sevenTvEmoteIds, sourceChannelName, sourceKind, leaderboardSort, actor)` → `AuditLogEntry` mit `ChannelName = null`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`, Details `{ emoteCount, sourceKind, sourceChannelName, leaderboardSort, targetEmoteSetId, targetOwnerSevenTvUserId, targetOwnerTwitchLogin }` → 204 |

`targetOwnerTwitchLogin` ist der Login des passenden Grants bzw. des Akteurs; ein 7TV-Name wird
nicht abgefragt. **Dialog und Audit nennen den Besitzer bewusst aus verschiedenen Quellen:** der
Bestätigungsdialog zeigt `ownerDisplayName` aus der v4-Antwort — den Namen, den ein Mensch auf 7TV
wiedererkennt —, die Papierspur schreibt den Twitch-Login, weil sie eine maschinenlesbare Spur ist
und ein Anzeigename sich ändern kann, ohne dass ein Konto wechselt. Identifiziert wird in beiden
Fällen über IDs (`targetOwnerSevenTvUserId`, `TargetId`), nie über einen der beiden Namen. `AuditLogQueryService.ProjectDetail` (`:129-152`, `TryProjectImportDetail :181-193`)
liest `targetEmoteSetId`/`targetIsActiveSetOfChannel`/`targetOwnerTwitchLogin` und liefert sie in
`AuditLogDetail` als neues Feld `TargetEmoteSet { id, isActiveSetOfChannel: bool | null, ownerLogin: string | null }`
neben `Kind`/`Count`/`Text` — sonst verlieren die Zeilen ihre Herkunft still (Warnung `EmoteEndpoints.cs:143-145`).
Ein Eintrag mit `ChannelName = null` erscheint in der globalen Admin-Ansicht, nicht in der
kanalgebundenen (bewusster Rest, Konzept 7.4).

### 6.8 `GET /api/channels/{channelName}/emotes/set-warning?emoteSetId=…`

Bestehender Endpunkt (`EmoteEndpoints.cs:212-221`) mit optionalem `emoteSetId` (E9);
`IEmoteSetOwnershipService.CheckAsync(channelName, principal, emoteSetId?)`. Antwort `EmoteSetWarningDto`
unverändert; `available: false` auch, wenn die Set-ID nicht `ActiveEmoteSetId` **und** nicht in der
Set-Liste des Kanals ist (die Prüfung fragt dann trotzdem Tier 1–3 — die Set-ID stammt vom Client).

### 6.9 `POST /api/channels/{channelName}/vote-sessions` — Set-Session

```
CreateVoteSessionRequest {                 // VoteSessionEndpoints.cs:345-347, additiv
  title, allowedVoterRoles, startedAt?, hideResultsUntilEnd?
  emoteIds?: string[]                      // Null-Session, Guids (E4)
  emoteSetId?: string                      // Set-Session
  sevenTvEmoteIds?: string[]               // Set-Session, Pflicht wenn emoteSetId gesetzt
}
```

Ausschlussregel (400 `vote_session_set_ballot_invalid`): `emoteSetId` gesetzt ⇒ `sevenTvEmoteIds`
nicht leer **und** `emoteIds` null; `emoteSetId` null ⇒ `sevenTvEmoteIds` null. Format der Set-ID:
`EmoteSetIdValidationFilter` auf dem Body-Feld → 400 `invalid_emote_set_id`. Anlage und Ergebnisse:
Abschnitt 9. `VoteSessionResultDto` bekommt `eligible: bool` (`IVoteSessionQueryService.cs:21-22`),
`VoteSessionSummary`/Detail bekommen `emoteSetId: string | null` und `emoteSetName` **nicht** (der
Name kommt aus der Set-Liste der Seite, wie beim Dropdown).

### 6.10 Rate-Limit-Policies — keine neue

| Route | Policy | Beleg |
|---|---|---|
| `/api/channels/{c}/emote-sets`, `/usage-stats/*`, `/emotes/set-warning` | `InteractiveRead` (Gruppe) | `EmoteEndpoints.cs:29`, `UsageStatsEndpoints.cs:24` |
| `/api/seventv/me/emote-set-targets`, `/api/seventv/channels/{c}/emote-sets`, `…/emotes?emoteSetId=` | `ForeignEmoteLookup` (10/min) | `RateLimitingOptions.cs:56`, `Program.cs:188` |
| `…/sync-deleted`, `…/sync-restored`, `…/sync-imported`, `/api/seventv/emote-sets/{id}/sync-imported` | `Bookkeeping` (120/min) | `RateLimitingOptions.cs:41`, `Program.cs:167` |

`RateLimitPolicyNames`, `RateLimitingOptions.Validate()` (`:72-80`) bleiben unverändert;
`EmoteRoutePolicyTests` (1 Theory) bekommt die neuen Routen als `InlineData`.

---
## 7. Vertrag: Zeilenidentität, Frontend-Caches, Export (Konzept 6.5)

**Der Schlüssel einer Zeile der Set-Ansicht ist die `SevenTvEmoteId`; `Emote.Id` ist nullbare
Nutzlast ohne Aussagewert.** Eine Zelle je 7TV-Emote, nicht je Set-Eintrag (#74-Duplikate: eine Zelle,
Slot-Zahl 2).

### 7.1 Das Zeilenmodell der Seite

```ts
// web/src/app/core/usage-stats/usage-stat.model.ts — EmoteUsageTotal, geändert
interface EmoteUsageTotal {
  emoteId: string | null;            // Guid, null für Live-Mitglieder ohne Zeile (Klasse 3)
  emoteName: string;                 // Alias der Live-Liste, sonst Emote.Name
  sevenTvEmoteId: string;            // der Schlüssel
  imageUrl: string;
  totalUseCount: number | null;      // null ≠ 0: keine Zählungen unter diesem Set (E17)
  previousWindowUseCount: number | null;
  lastUsedDate: string | null;
  firstSeenAt: string | null;
  membership: 'live' | 'left';       // 'left' = Zeile mit Zahlen, nicht mehr im Set (E23) — in der aktiven Ansicht immer 'live'
  slotCount: number;                 // 1; 2 bei #74-Duplikat (nur nicht-aktive Ansicht, E20)
  aliases: string[];                 // alle Aliase des Live-Eintrags; [emoteName] sonst
  nameTwinEmoteSetIds: string[];     // E24
}
```

Die Vereinigung (E16) baut eine pure Funktion `mergeSetView(totals: EmoteUsageTotalDto[], live:
ForeignEmoteRow[] | null, isActiveSet: boolean): EmoteUsageTotal[]` in `core/usage-stats/`
(Regel 12: eigener Spec). Aktive Ansicht: `live === null`, jede Zeile `'live'`, `slotCount 1`.
Nicht-aktive Ansicht: Live-Einträge nach `sevenTvEmoteId` gruppiert; Zeile mit Zahlen + Live →
Zahlen; Live ohne Zeile → `emoteId: null, totalUseCount: null`; Zeile ohne Live → `'left'`.

### 7.2 Was den Schlüssel wechselt

| Stelle | Neu |
|---|---|
| `ListSelection` (`usage-stats-page.ts:549-553`) | `(emote) => emote.sevenTvEmoteId` |
| inneres `track` (`.html:585`) | `track emote.sevenTvEmoteId`; äußeres `trackBy: trackRow` (Index, `:524`) **unverändert** (DECISIONS 2026-08-30) |
| `inspectedId`, `usageRank`, `fillPercents`, `seriesByEmote` (`:561-565`, `:533-537`, `:488-494`, `:678-680`) | Maps nach `sevenTvEmoteId` |
| `DeletableEmote.emoteId` (`mass-delete-panel.ts:34-37`), `DeleteQueueEmote.emoteId` (`seven-tv-delete.service.ts:55-59`) | `emoteId?: string` |
| Queue-Key Delete/Restore (`:118`, `seven-tv-restore.service.ts:152`) | `key: emote.sevenTvEmoteId` — R3-Nachtrag: der Import-Lauf war das Muster |
| `RunResult` (`seven-tv-run-engine.ts:117-118`) | `doneIds` entfällt (E2); `doneKeys` einzige Rückmelde-Identität |
| Panel `deleted` (`mass-delete-panel.ts:297-303`) | emittiert `doneKeys` (E18); `usage-stats-page.ts:1349-1350` und `vote-session-detail-page.ts:690-695` filtern nach `sevenTvEmoteId` |
| Protokoll (`purge-run-export.ts:37-42`, `:154-162`) | `PurgeRunRow.emoteId: string \| null`; Panel-Filter `:344-352`/`:393-398` entfällt; Parser akzeptiert `null` **und** alte Protokolle mit Guid |
| Restore aus Protokoll (`restore-flow.ts:82-86`) | `emoteId: row.emoteId ?? undefined` |
| Delete-/Restore-Laufdatensatz (`DeleteRunInfo :79-83`, `RestoreRunInfo :62-66`) | `+ setId: string`, beim Start eingefroren; `reportDeleted`/`reportRestored` und beide Retries lesen Set-ID **und** Keys aus dem Datensatz |
| `emote-admin.service.ts:55-68` | `syncDeleted(channelName, { emoteSetId, sevenTvEmoteIds })`, `syncRestored` ebenso; die Altform sendet der Client **nie** |
| Drilldown (`usage-stats-page.ts`, `emote-drilldown-dialog.ts:319-321`) | nur für `emoteId !== null && totalUseCount !== null`; `data` trägt `emoteSetId` eingefroren |
| Voting-Draht (`:1375`) | Signal der Schlüssel (7TV-Ids); Null-Session: Auflösung in Guids im Absende-Moment über `selectedItems()`; Set-Session: `sevenTvEmoteIds` direkt (E4) |
| Vote-Session-Detailseite `ListSelection` (`:318-322`) | **bleibt** Guid — dort hat jede Zeile eine; nur `onDeleted` ändert sich (F12) |

### 7.3 Caches und Scope-Capture

- `usage-stat.service.ts:33` → `${channelName}|${emoteSetId}|${emoteId}|${from}|${to}`; `:59` →
  `${channelName}|${emoteSetId}|${from}|${to}`; beide Methoden bekommen `emoteSetId` als Parameter.
- `clearSeriesCache()` (`:73-76`) läuft zusätzlich beim Set-Wechsel im Dropdown.
- `CapturedExportScope.emoteSetId` (`usage-stats-page.ts:157-165`, `:1452`) und
  `CapturedImportScope.emoteSetId` (`:137`, `:1521-1531`) tragen das **gewählte** Set; alle vier
  Dialogöffner (Export, Übertragen, Löschen, Abstimmung) lesen ihren Scope vor dem Öffnen und nie
  wieder (bestehende Disziplin, `:1511-1518`).

### 7.4 Nutzungs-Export (Konzept 6.1, Codex D9)

- `UsageExportInput`/`UsageExportMeta` (`usage-export.ts:37-43`) bekommen `emoteSetId: string`,
  `emoteSetName: string | null`; `buildUsageExportPurposeDownload` (`usage-export-purposes.ts:86-105`)
  reicht beide aus dem Scope durch.
- Dateiname (`:45-47`): `emotepurge_{channel}_usage_{set-kennung}_{from}_{to}.{ext}`; Kennung =
  die letzten 6 Zeichen der Set-ID (`emote-list-export.ts:36` hält `sourceEmoteSetId` in `meta`,
  nicht im Namen — hier kommt sie in beides, weil zwei Set-Exporte desselben Kanals sonst gleich
  hießen).
- `UsageExportRow.totalUseCount`/`previousWindowUseCount` (`:29-30`) → `number | null`; CSV: leere
  Zelle (`encodeCell` `csv.ts:35-38` tut das für `null` schon), JSON: `null`, `trend: 'unknown'` für
  solche Zeilen. Keine 0, keine Auslassung.

---

## 8. Vertrag: UI

Regel 12 gilt: Verhalten ja, Vorlage nein. Die Testliste je Baustein steht in Abschnitt 15.
Designsprache: `docs/UI-Designsprache.md` (verbindlich), `DESIGN.md`; Muster: `date-range-menu.ts`
(`Popover` + `role="radiogroup"`, `:104-133`), `archivedBadge` (`de.json:781`),
`app-name-preview-list` (`import-confirm-dialog.ts:203-209`).

### 8.1 Set-Dropdown auf der Nutzungsseite

- Auslöser in der Kopfzeile neben dem Datumsbereich; Popover mit `role="radiogroup"`, eine Option je
  Set aus 6.1, das aktive vorausgewählt und als „aktiv" beschriftet; Sets mit `kind != NORMAL`
  **deaktiviert mit Beschriftung** (8.6), nicht ausgeblendet (dasselbe Idiom wie ungetrackte Kanäle heute,
  `import-target-options.ts:7-9`). Kein Eintrag „Gesamt (alle Sets)".
- Die Wahl ist eine **Sicht** wie der Zeitraum: Set-Wechsel ⇒ `selection.retainAmong(payload)` mit
  der #94-Meldung, **kein** `clear()`; `clearSeriesCache()`; Live-Liste neu laden (E16);
  Set-Liste **nicht** neu laden (E19).
- Zustand in der URL — und zwar **nicht** analog zum Zeitraum, wie eine frühere Fassung dieser
  Spec behauptet hat: der Zeitraum steht in keinem Query-Parameter, sondern in einem lokalen
  Signal (`usage-stats-page.ts:333`), und die Seite kennt überhaupt keinen `ActivatedRoute`
  (geprüft am 2026-09-20, null Treffer). Ob `core/routing/list-query-state` das Muster hergibt,
  ist Prüfaufgabe T4.0; es bedient heute fünf paginierte Listenseiten (page + Filter, `replaceUrl`,
  Defaults werden entfernt). Ein `emoteSetId` in der URL, das die Liste nicht kennt, fällt still
  auf das aktive Set zurück.
- **Set-Liste nicht lesbar** (6.1 antwortet 503): die Seite bleibt auf dem aktiven Set, das sie
  unabhängig von dieser Route aus `setStatus()?.activeEmoteSetId` (`usage-stats-page.ts:337`) kennt;
  der Auslöser ist gesperrt mit Grund, kein Banner, kein Wiederholen im Hintergrund. Das ist
  dieselbe Zurückhaltung wie bei der nicht lesbaren Live-Liste (8.3), eine Ebene höher.
- Gates, die heute auf `activeEmoteSetId()` stehen (`.html:108` Kopfzeile, `:890` Dock;
  `vote-session-detail-page.html:155`), stehen auf `selectedEmoteSetId()`; Kopfzeile und Dock
  bleiben auf `!isCoarse()`.

### 8.2 Die Set-Ansicht — Zeilenklassen

| Zeile | Zahlen | Kennzeichnung | Wählbar zum Löschen | Drilldown |
|---|---|---|---|---|
| Live, Zeile, Zahlen unter X | aus `/totals` | — | ja | ja |
| Live, keine Zahlen unter X (`totalUseCount: null`) — Klasse 2b oder 3 | keine | Beschriftung „keine Zählungen unter diesem Set" (E17); eigene Gruppe am Ende, nicht im Pareto, nicht in der Summe (F16) | ja | nein |
| `'left'` — Zeile mit Zahlen, nicht mehr im Set | aus `/totals` | Badge wie `archivedBadge` | **nein** | ja |
| `slotCount 2` (#74) | einmal | Slot-Zahl an der Zelle, beide Aliase im Tooltip; Slot-Projektion zählt 2 | **Sonde 5, Zweig A oder B** (Abschnitt 11) | wie Zeile 1 |
| `nameTwinEmoteSetIds` nicht leer | eigene | kleines Merkmal mit Tooltip „unter diesem Namen liegt in ‚<Setname>' ein anderes Emote"; kein Banner, kein Filter | wie sonst | wie sonst |

Sortierung, Bänder (`groupIntoUsageBands`), Verteilungsstreifen, Fill-Bars und Summenzeile rechnen
nur über Zeilen mit `totalUseCount !== null`; die `null`-Gruppe steht dahinter, in atlasOrder nach
Name. Der Namensfilter (`emote-usage-filter.ts:58-59`) behandelt `null` bereits als „außerhalb".

### 8.3 Nicht-aktive Ansicht: Laden, `truncated`, Kapazität

- Beim Wählen eines nicht-aktiven Sets: `/totals?emoteSetId=`, `/series?emoteSetId=` (DB) und
  `GET /api/seventv/channels/{name}/emotes?emoteSetId=` (7TV) parallel; die Vereinigung erst, wenn
  beide da sind. Bis dahin Skelett wie heute.
- Live-Liste **nicht** lesbar (503/429) ⇒ die Ansicht zeigt nur die DB-Zeilen mit einem Hinweis
  „Mitgliederliste nicht verfügbar" und **ohne** Löschweg (Dock gesperrt mit Grund) — eine Liste,
  die nur die Hälfte kennt, darf nicht löschen.
- `truncated: true` ⇒ Hinweis mit `totalCount`, Löschweg gesperrt (gleiche Regel).
- Kapazität und belegte Slots für das Dock: aus `capacity`/`totalCount` der Live-Liste, nicht aus
  `EmoteSetStatus` (`Channel.ActiveEmoteSetCapacity`, `Channel.cs:10-14`).
- Stiller Reload (`usage.flushed`): nur `/totals` und `/series`. Lauter Reload
  (`channel.synced`, Refresh-Knopf): zusätzlich die Live-Liste.

### 8.4 Tatsachenangabe

Wo heute `usageStats.trackedSince` steht (`.html:344-346`). **Zwei unabhängige Aussagen, nicht eine.**

Eine frühere Fassung dieses Abschnitts ließ aus einem fehlenden Beobachtungsintervall den Satz „für
dieses Set liegen keine Zählungen vor" folgen. Das war ein **Selbstwiderspruch zu 4.3**, wo der Zweck
des Beobachtungslogs ausdrücklich auf Preset (8.5), Tatsachenangabe und spätere Backfill-Automatik
begrenzt und die **Zuordnung der Zahlen** ausdrücklich ausgenommen ist — die trägt
`UsageStat.EmoteSetId`. Und er war faktisch falsch: die Migration legt für **inaktive** Kanäle
**keine** Beobachtungszeile an (4.3, Saat), backfillt deren `UsageStats` aber sehr wohl auf eine
Set-ID (4.2, Schritt 8b). Nach einem späteren Rejoin trägt ein historischer Zeitraum deshalb Zahlen,
während die Seite das Gegenteil behauptet hätte.

Verbindlich sind zwei getrennte Quellen:

- **beobachtet ja/nein** — allein aus `observations` des gewählten Sets gegen den gewählten Zeitraum;
- **Zahlen ja/nein** — allein aus den tatsächlich geladenen Totals (`/totals?emoteSetId=`), **nie**
  aus `observations`.

Drei Sätze, je einer pro Aussage, keiner impliziert den anderen:

- **B−** „Dieses Set war im gewählten Zeitraum nicht als aktiv beobachtet."
- **B~** „gezählt seit <ObservedFromUtc>" — über das bestehende `rangeStartsBeforeTracking`-Idiom
  (`usage-stats-page.ts:383-398`) mit dem Intervallbeginn statt `trackedSince`; nur wenn ein
  Intervall **innerhalb** des Zeitraums beginnt.
- **Z−** „Für dieses Set liegen im gewählten Zeitraum keine Zählungen vor."

| | **Zahlen vorhanden** | **keine Zahlen** |
|---|---|---|
| **beobachtet** (mindestens ein schneidendes Intervall) | kein Zusatz; beginnt das Intervall im Zeitraum, zusätzlich **B~** | **Z−** (und **B~**, falls das Intervall im Zeitraum beginnt) |
| **nicht beobachtet** (kein schneidendes Intervall) | **B−** allein — die Zahlen stehen und werden **nicht** relativiert; das ist der Migrations-/Rejoin-Fall | **B−**, dann **Z−** |

Für das aktive Set mit offenem Intervall ab Tracking-Beginn bleibt alles wie heute. Keine Schwelle,
keine Interaktion, kein Banner, kein Zurückhalten von Bändern — ein Satz je zutreffender Aussage,
sonst nichts (Designsprache, Zurückhaltung).

### 8.5 Datumsbereichs-Preset

`DateRangePreset` (`date-range-menu.ts:19`) bekommt `'set-observed'`; angeboten nur, wenn das
gewählte Set mindestens ein Intervall hat; Bereich = jüngstes Intervall (`fromUtc` … `toUtc ?? heute`).
Beschriftung „während dieses Set beobachtet wurde" — **beobachtet**, nicht „aktiv war".

### 8.6 Ziel-Picker und Bestätigungsdialog (Konzept 7.3, 7.5)

- `ImportTargetChoice { scope, emoteSetId, channelName: string | null, ownerDisplayName, setName, isTracked }`
  (`import-target-dialog.ts:29-32`) — `emoteSetId` ist der Schlüssel, `channelName` das Attribut der
  getrackten Klasse. `ownerDisplayName` hieß bis zum 2026-09-20 `ownerLogin`; v4 liefert je Set
  einen **Anzeigenamen**, keinen Login (E7), und der Name sagt das jetzt. Er steht im Dialogtext und
  sonst nirgends — jeder Abgleich läuft über `emoteSetId` bzw. 7TV-IDs.
- Der Picker lädt `GET /api/seventv/me/emote-set-targets` (6.2) statt `listMine()` (`:211`); Sets
  klappen unter ihrem Account auf; **getrackte** Accounts oben, unter ihrem Kanalnamen, aktives Set
  beschriftet und vorausgewählt (der Ein-Klick-Weg „in Kanal X" bleibt); **ungetrackte** darunter,
  als „nicht getrackt" gekennzeichnet. **Wählbar ist allein `kind == NORMAL`**: `PERSONAL`, `GLOBAL`
  und `SPECIAL` erscheinen sichtbar, aber deaktiviert und beschriftet — nie kommentarlos wählbar,
  nie ausgeblendet. Die Beschriftung unterscheidet nur dort, wo sie etwas erklärt: `PERSONAL` sagt
  „persönliches Set", `GLOBAL`/`SPECIAL` sagen „kein Zielset". Die Regel ist positiv formuliert,
  damit ein künftiger fünfter `kind`-Wert auf der sicheren Seite landet statt wählbar zu sein
  (`isPersonal` bleibt daneben stehen, aber nur für die Beschriftung).
- **Der eigene Kanal bleibt in der Liste**; deaktiviert („das ist die Quelle") ist nur das Set der
  Quelle (`CapturedImportScope.emoteSetId`). `import-target-options.ts` wird durch eine pure Funktion
  über die neue Antwort ersetzt; der Kommentar an `sameChannelFile` (`import-confirm-dialog.ts:421-426`)
  wird angepasst, die Prüfung bleibt datei-only.
- Wahl eines **ungetrackten** Sets ⇒ Bestätigung „In das Set ‚<setName>' von ‚<ownerDisplayName>'
  kopieren?" vor dem Schließen des Pickers; ohne Bestätigung keine Wahl. Getrackte Ziele ohne
  zweiten Schritt.
- `loadImportTarget(emoteAdminService, foreignEmoteSetService, choice)`: getracktes Ziel **und**
  `emoteSetId === activeEmoteSetId` ⇒ heutiger Weg (`import-target-loader.ts:70-93`); sonst Live-Liste
  nach Set-ID (6.4): `emotes` = Einträge mit Alias als `name`, `occupiedSlots = totalCount`,
  `capacity`, `setName`; `truncated ⇒ failed`; Warnung: getracktes Ziel ⇒
  `set-warning?emoteSetId=` (E9), ungetracktes ⇒ `UNAVAILABLE_WARNING`.
- Dialogkopf nennt Kanal **und Setname** (`:100-105`), bei ungetrackten Zielen Besitzer und Setname.
- `buildImportPreview` (`import-preview.ts:30-55`): Namenskollisionen (gleicher Name, andere ID)
  **aus `toAdd` heraus** und als eigene Gruppe „N Emotes tragen einen Namen, der im Zielset schon
  vergeben ist — werden nicht übertragen" mit `app-name-preview-list`; Alias-Abweichungen (gleiche
  ID, anderer Alias, bei #74-Duplikaten nur wenn **kein** Alias gleich) weiter übersprungen, aber als
  Gruppe „N sind vorhanden, heißen dort aber anders" mit Quellname/Zielalias je Zeile;
  `alreadyPresent` zählt sie nicht doppelt; `invalidNames` bleibt informativ und im Lauf.
  `projectSlots(occupied, capacity, toAdd.length)` rechnet damit ohne Kollisionen (687 + 232 = 919
  statt 687 + 424 = 1111). `filterAlreadyPresent` (`already-present-filter.ts:144-156`) bleibt
  ID-Vergleich.
- Nach dem Lauf: `reportImported` an `run.targetChannelName` mit `targetEmoteSetId` (getrackt) bzw. an
  den set-zentrierten Endpunkt (ungetrackt); Nachlauf-Resync (`seven-tv-import.service.ts:283-293`)
  nur für getrackte Ziele.
- Die drei anderen Türen (Datei, Fremdkanal, Bestenliste; `import-trigger.ts:120-132`,
  `file-import-step.ts:80-82`) zielen auf das **gewählte** Set der Seite (K4), und das
  Purge-Protokoll wird gegen `meta.emoteSetId` = gewähltes Set geprüft (`file-import-step.ts:136-140`).

### 8.7 Quell-Set-Picker beim fremden Kanal (K3)

Im `ForeignChannelStep` nach der Kanalauflösung ein Radiogroup der Sets aus 6.3 (aktives
vorausgewählt und beschriftet; wählbar ist auch hier nur `kind == NORMAL`, alles andere deaktiviert
und beschriftet — 8.6); die Vorschau lädt
`…/emotes?emoteSetId=<gewählt>`; `ForeignChannelImportResult.emoteSetId` (`foreign-channel-step.ts:29`)
trägt das gewählte Set. Kein zweiter Request, wenn das aktive Set gewählt bleibt.

### 8.8 Lösch- und Restore-Bestätigung nennen das Set

`DeleteConfirmDialogData` (`delete-confirm-dialog.ts:15-25`) und `RestoreConfirmDialogData` bekommen
`setName: string` und `isActiveSet: boolean`; der Dialog nennt „aus dem Set ‚<Name>'" und bei
nicht-aktivem Set zusätzlich „dieses Set ist gerade nicht aktiv". Kein Bestätigungsschritt mehr als
heute (Konzept 9: keine Schutzmechanik).

### 8.9 Duplikat-Zellen und die Lösch-Vorprüfung

Bis Sonde 5 gemessen ist: Zellen mit `slotCount > 1` sind wählbar, werden aber vom Delete-Lauf
**ausgenommen** — der Dialog zeigt sie als eigene Gruppe „N Emotes liegen doppelt im Set und werden
nicht gelöscht" (Konzept 6.2). Nach der Messung: Abschnitt 11, Sonde 5.

### 8.10 Audit-Ansicht

`audit-row.ts` zeigt für `emotes.syncImported`/`syncDeleted`/`syncRestored` mit `targetEmoteSet` den
Zusatz „in Set <id-Kurzform>" und, wenn `isActiveSetOfChannel === false`, „(nicht das aktive Set)";
für den set-zentrierten Eintrag „für <ownerLogin>" — das ist der **Twitch-Login** aus der
Papierspur, nicht der Anzeigename des Dialogs (6.7). Locale-Schlüssel unter `audit.details`.

---
## 9. Vertrag: Voting für Set-Sessions (Konzept 8)

**Invariante:** `VoteSession.EmoteSetId != null` ⇒ `SessionEmotes` nicht leer (fester Wahlzettel);
eine dynamische „alle Emotes"-Set-Session gibt es nicht (400 `vote_session_set_ballot_invalid`).

**Anlegen (`VoteSessionService.CreateAsync`, `:14 ff.`), Set-Session:**

1. Live-Mitgliederliste des Sets über `IForeignEmoteSetService` nach Set-ID (6.4); nicht lesbar ⇒
   Ergebnis `SevenTvUnavailable` → 503 `foreign_channel_seventv_unavailable`, keine Session.
2. All-or-nothing auf der **7TV-Identität**: jede `sevenTvEmoteIds`-Id muss Live-Mitglied sein,
   sonst 400 `emote_ids_invalid` (heutiges Verhalten der Guid-Prüfung, `:41-53`, jetzt gegen die
   Live-Liste statt `!IsArchived`).
3. `INSERT INTO "Emotes" (…) … ON CONFLICT ("ChannelId", "SevenTvEmoteId") DO NOTHING` über die
   Wahlzettel-Ids: neue Zeilen mit `IsArchived = true`, `ArchivedAt = null` („nie aktiv"),
   `FirstSeenAt` aus dem Set-Eintrag, wenn 7TV es liefert, `Name`/`ImageUrl` aus dem Live-Eintrag.
4. Zeilen des Kanals nach `SevenTvEmoteId` **nachlesen**, Wahlzettel aus dem Gelesenen bauen;
   `VoteSessionEmote.NameAtCreation`/`ImageUrlAtCreation` aus dem Live-Eintrag (Pflicht für
   Set-Sessions, `null` für Null-Sessions).
5. `VoteSession.EmoteSetId` setzen. Audit wie heute.

Damit ist die Api konfliktverträglich; der Worker wird es über E10 (Wiederholen einmal, Test mit
erzwungener Verschränkung).

**Abstimmen (`IsEmoteVotableAsync`, `:314-320`):** für Null-Sessions unverändert (`!IsArchived`); für
Set-Sessions „steht auf dem Wahlzettel", `IsArchived` kein Kriterium.

**Ergebnisse (`VoteSessionQueryService.GetResultsAsync`):**

- `eligible` je Zeile: Null-Session `!IsArchived`, Set-Session `true`. Die Seite gatet Votes und
  Badge auf `eligible`, nicht auf `isArchived`. Set-Sessions berechnen **kein** Mid-Session-Badge.
- `useCount` (`:191`): für Manager einer Set-Session **berechnet** über
  `GetTotalsByEmoteIdsAsync(ids, from, to, session.EmoteSetId)`; `null` nur für Zeilen ohne
  `UsageStat` unter dieser Set-ID **und** für Nicht-Manager; das `!emote.IsArchived`-Gate in `:191`
  fällt für Set-Sessions.
- Name/Bild: `NameAtCreation ?? Emote.Name`, `ImageUrlAtCreation ?? Emote.ImageUrl`.

**Detailseite (`vote-session-detail-page.ts`):** `canSelectForDelete = canManage()` (`:220`, aus
`:203-205`); `hasUsageData` gatet nur noch Nutzungsspalte und Drilldown; das Panel bekommt
`session.emoteSetId ?? activeEmoteSetId()` (`.html:155-157`); `onDeleted` filtert nach
`sevenTvEmoteId` (F12). Lösch-Bestätigung nennt das Set (8.8).

**Set-Ansicht nach dem Anlegen (Klasse 2b):** ein vorher zeilenloses Mitglied hat jetzt eine Guid;
die Ansicht zeigt weiterhin `null`, nicht 0 (E17, AK 63).

---

## 10. Vertrag: Wartungsfenster (Konzept 12.3)

Einmalig, **hinter** dem 2026-10-08, nach Freigabe gegen das Harness-Runbook in `infra-docs`.

**Eine Vorbedingung liegt seit dem 2026-09-20 nicht mehr im Repo:** Schritt 4 baut das Projekt aus
dem lokalen Checkout, und die Klassifikation steckt in der gitignorierten
`SetSwitchAssignments.Local.cs` (E1). Sie muss dort **vor** Schritt 4 stehen und gefüllt sein
(V5) — sonst bricht `Up` mit der benannten Meldung ab (AK 91). Das ist kein Sonderfall des
Fensters, sondern der Normalfall: ein frischer Klon, ein Worktree oder eine andere Maschine hat die
Datei nicht.

Reihenfolge, weil sie zählt:

| Schritt | Handlung | Warum in dieser Position |
|---|---|---|
| 0 | Uptime-Kuma-Monitor pausieren oder Fenster ankündigen | ein Alarm für einen geplanten Zustand entwertet die echten |
| 1 | **Worker stoppen** (Portainer, Stack `worker`) | Shutdown-Flush schreibt die letzten < 30 s gegen das **alte** Schema (`UsageFlushWorker.cs:30-38`); `stop_grace_period: 60s` (`docker-compose.prod.yml:95`) |
| 2 | **Api stoppen** | ab Schritt 7 der Migration hält die Transaktion `ACCESS EXCLUSIVE` auf `UsageStats`; jede Seite hinge unbestimmt lange |
| 3 | Tunnel `ssh -N -L 15432:127.0.0.1:5433 vps`; `dotnet ef migrations list --connection '…'` — genau **eine** Pending erwartet | mehr als eine heißt: Prod hängt Runden zurück, erst durchsehen |
| 4 | `dotnet ef database update --connection 'Host=localhost;Port=15432;…;Options=-c lock_timeout=5s'` | Prüfungen 1–6 und `SET LOCAL lock_timeout` (4.2, E11) brechen ab, statt zu schätzen oder zu warten |
| 5 | `dotnet ef migrations list` — keine Pending | Gegenprobe |
| 6 | **Neue Images starten**, Api und Worker | Worker rejoined (Boot-Recovery, `Worker.cs:95-115`), Match-Cache kommt aus dem Warmstart **mit** Set-ID |
| 7 | Live-Verifikation auf Prod (AK 84–86) | Set-Ansicht, eine Zeile mit `EmoteSetId`, `/api/health` 200, Picker am Testkanal |

**Was bewusst verloren geht:** Chat-Zählung während des Fensters (Minuten, alle Kanäle); ein
Helix-Poll-Intervall `ChannelLiveDay` (5-min-Raster); Erreichbarkeit der Site. Nichts davon ist
rekonstruierbar außer über einen künftigen Chat-Log-Backfill (#69).

**Rollback-Reihenfolge:** neuen Worker **und** neue Api stoppen, `Down` (nur bis zum ersten
beobachteten Set-Wechsel, 4.2), alte Images starten. Die Beobachtungs-Tabelle und die
Voting-Spalten stören ein altes Image nicht; das Argument gilt nur für alte Images gegen neues
Schema, nie umgekehrt.

---

## 11. Sonden — T0-Aufgaben des Betreibers, fallunterscheidend

Der Klassifikator blockt 7TV-Sonden aus einer Session; jede Sonde ist ein fertiges Kommando mit
Platzhaltern (`<TWITCH-ID>`, `<SET-ID>`, `<7TV-TOKEN>`, `<PROD-PW>`), das der Betreiber ausführt.
Jede Sonde hat **zwei Zweige**, die Regel, welcher gilt, und was nach der Messung aus dieser Spec zu
streichen ist. Sonden 1 und 2 sind gemessen (Konzept 11.1, 11.2); Sonde 1 (T0.1) und Sonde 7 (T0.5)
sind am 2026-09-20 gemessen und tragen ihr Ergebnis unten. **T0.6** (`editor_of { user { id } }`) ist
ebenfalls am 2026-09-20 gemessen, steht aber nicht hier, sondern als Prüfaufgabe in Abschnitt 19 und
im Plan (0.2). **Sonde 4 (T0.2), 5 (T0.3) und 6 (T0.4) stehen aus.**

### Sonde 1 (Rest) — enthält HandOfBloods eigene Set-Liste das Halloween-Set mit Namen? (T0.1)

```
curl -s https://7tv.io/v3/users/twitch/49140130 | jq '{active: .emote_set_id, sets: [.user.emote_sets[] | {id, name, capacity, flags}]}'
```

**Gemessen am 2026-09-20: Zweig A.** Die Antwort trägt aktiv `01GV88A38G0006FW5TVZVMG507` und drei
Sets, alle mit Namen, alle Kapazität 1000, alle `flags: 0`:

| Set-ID | Name |
|---|---|
| `01J94Y3JDR0005G1FWF2H9ZHJT` | `Christmas Set` |
| `01GV88A38G0006FW5TVZVMG507` | `HandOfBlood's Emotes` (aktiv) |
| `01J94NYQR0000D15QN0BDGN85E` | `Halloween Set` |

| Zweig | Bedingung | Vertrag |
|---|---|---|
| A (gemessen) | `01J94NYQR0000D15QN0BDGN85E` steht mit `name` in der Liste | 6.1/6.2 wie geschrieben |

Die drei Set-IDs samt Namen sind damit belegt und stehen für die Zuordnungsliste bereit (V1, V5).
**Die Liste ist trotzdem unvollständig:** das persönliche Set fehlt in v3 ganz — Sonde 7 hat es
gemessen, und E7 liest die Liste deshalb aus v4. Zweig B ist gestrichen.

### Sonde 4 — Reload-Frequenz der Nutzungsseite (T0.2, Dev-Box, kein Prod)

Einen Kanal mit Chat-Betrieb im Dev-Stack tracken, Nutzungsseite öffnen, im Netzwerk-Panel den
`EventSource` auf `/api/live/…` beobachten und über **10 Minuten** zählen: `usage.flushed`,
`channel.synced`.

| Zweig | Bedingung | Vertrag |
|---|---|---|
| A | `channel.synced` ≤ 1 je Minute im Mittel | TTL des Set-ID-Lesepfads bleibt **60 s** (E12); ein Betrachter eines 900er-Sets kostet ≤ 2 Permits/min |
| B | > 1 je Minute | TTL für `7tvforeign:set:*` **300 s** (eigene Konstante neben `ForeignEmoteSetCache.Ttl`); `refresh=true` umgeht sie wie heute (`SevenTvEndpoints.cs:39-42`) |

`usage.flushed` ist für die Kosten irrelevant (E16: stille Reloads laden keine Live-Liste) und wird
nur mitgezählt, um die Regel zu belegen. Nach der Messung streichen: den anderen Zweig aus E12.

### Sonde 5 — entfernt ein `REMOVE` bei doppelt eingetragener Emote-ID einen oder beide Einträge? (T0.3)

Braucht ein **eigenes Testset** (Kapazität egal), einen 7TV-Token mit Schreibrecht und ein Emote
`<EMOTE-ID>`, das nicht im Set liegt. Vier Aufrufe, je einer je Frage; eine GraphQL-Fehlermeldung
ist ein Ergebnis, kein Anlass für Varianten.

```
# 1) erster Eintrag
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -H 'Authorization: Bearer <7TV-TOKEN>' -d '{"query":"mutation($s: Id!, $e: Id!) { emoteSets { emoteSet(id: $s) { addEmote(id: { emoteId: $e, alias: \"probeA\" }) { id } } } }","variables":{"s":"<SET-ID>","e":"<EMOTE-ID>"}}'
# 2) zweiter Eintrag, anderer Alias — Ergebnis 5a: nimmt 7TV das an?
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -H 'Authorization: Bearer <7TV-TOKEN>' -d '{"query":"mutation($s: Id!, $e: Id!) { emoteSets { emoteSet(id: $s) { addEmote(id: { emoteId: $e, alias: \"probeB\" }) { id } } } }","variables":{"s":"<SET-ID>","e":"<EMOTE-ID>"}}'
curl -s "https://7tv.io/v3/emote-sets/<SET-ID>" | jq '[.emotes[] | select(.id=="<EMOTE-ID>") | .name]'
# 3) REMOVE per ID — Ergebnis 5b: wie viele Einträge bleiben?
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -H 'Authorization: Bearer <7TV-TOKEN>' -d '{"query":"mutation($s: Id!, $e: Id!) { emoteSets { emoteSet(id: $s) { removeEmote(id: { emoteId: $e }) { id } } } }","variables":{"s":"<SET-ID>","e":"<EMOTE-ID>"}}'
curl -s "https://7tv.io/v3/emote-sets/<SET-ID>" | jq '[.emotes[] | select(.id=="<EMOTE-ID>") | .name]'
# 4) nur wenn einer bleibt: REMOVE mit Alias — Ergebnis 5c: trifft der Alias den Eintrag?
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -H 'Authorization: Bearer <7TV-TOKEN>' -d '{"query":"mutation($s: Id!, $e: Id!) { emoteSets { emoteSet(id: $s) { removeEmote(id: { emoteId: $e, alias: \"probeB\" }) { id } } } }","variables":{"s":"<SET-ID>","e":"<EMOTE-ID>"}}'
```

| Zweig | Bedingung | Vertrag der Lösch-Vorprüfung und des Protokolls |
|---|---|---|
| A | 5b: **beide** Einträge weg | Duplikat-Zellen laufen im Delete-Lauf mit **einer** Queue-Zeile (Key `sevenTvEmoteId`); die Protokollzeile trägt `aliases: string[]`; der Restore gibt je Alias ein `ADD` (Queue-Key `sevenTvEmoteId#alias` **nur** im Restore-Lauf, weil dort mehrere Zeilen je ID nötig sind; 5a muss dafür „angenommen" ergeben, sonst nur der erste Alias); 8.9 entfällt |
| B | 5b: **ein** Eintrag bleibt | Delete-Lauf braucht je Eintrag ein `REMOVE`; 5c entscheidet die Form: trifft der Alias (`removeEmote(id: { emoteId, alias })`), bekommt die Queue je Alias eine Zeile mit Key `sevenTvEmoteId#alias` und `REMOVE_OPERATION` sendet den Alias; trifft er nicht, bleibt 8.9 **dauerhaft** (Duplikate ausgenommen, Hinweis) und die Frage geht als Notiz an #74 |
| — | 5a: zweiter `ADD` **abgelehnt** | die Duplikate im Bestand sind dann nicht per API reproduzierbar; Sonde an einem Set mit vorhandenem Duplikat wiederholen (Schritt 3/4 nur), sonst Zweig B ohne 5c |

Nach der Messung streichen: 8.9 (bei A) bzw. den A-Zweig; der Schlüssel-Vertrag aus Abschnitt 7 bleibt
bei „eine Zelle je 7TV-Emote" in beiden Zweigen.

### Sonde 6 — Gegenprobe vor der Migration (T0.4, lesend gegen Prod, unmittelbar vor K7)

Die SQL-Datei aus Konzept 11.6 (`set-wechsel-pruefung.sql`), ausgeführt wie dort:
`ssh -N -L 15432:127.0.0.1:5433 vps`, dann
`psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge password=<PROD-PW>' -f set-wechsel-pruefung.sql`.

| Zweig | Bedingung | Vertrag |
|---|---|---|
| A | genau zwei Zeilen — Testkanal (Positivkontrolle) und HandOfBlood mit Tag = `BoundaryUtc::date` und `archived_that_day` = `ExpectedArchivedCount`. **Zwei bleibt es auch ohne V1**, weil der Wegwerfkanal vorher gepurgt ist bzw. gar nicht existiert (4.2) | Liste bestätigt; Testkanal purgen (V2); Migration läuft |
| B | Testkanal fehlt / HandOfBlood weicht ab / ein dritter Kanal erscheint | **Halt.** Der Betreiber klärt (Rückwechsel, Restore, unbekannter Wechsel, Massenlöschung auf 7TV), korrigiert die Liste oder nicht; Prüfung 2/3 der Migration bricht bei derselben Abweichung ab. Nichts wird geschätzt |

Diese Sonde ist ein Tor, kein Zweig, der aus der Spec gestrichen wird; ihr Ergebnis steht im
DECISIONS-Eintrag 1.

**Im selben Handgriff, zweite Abfrage: die Kanalliste für die Klassifikation.** Seit der
Entscheidung zu Codex-Befund E muss `SetSwitchAssignments` **jeden** Kanal mit Nutzungszeilen
nennen (4.2), und Prüfung 3 bricht bei jedem ab, der fehlt. Die Liste dieser Kanäle erhebt niemand
aus dem Code — sie kommt aus derselben Sitzung am Tunnel, rein lesend:

```sql
SELECT c."Name", c."Id" AS channel_id, c."TwitchChannelId", c."ActiveEmoteSetId",
       COUNT(u.*) AS usage_rows, MIN(u."Date") AS first_day, MAX(u."Date") AS last_day
FROM "Channels" c
JOIN "Emotes" e ON e."ChannelId" = c."Id"
JOIN "UsageStats" u ON u."EmoteId" = e."Id"
GROUP BY c."Id", c."Name", c."TwitchChannelId", c."ActiveEmoteSetId"
ORDER BY usage_rows DESC;
```

Jede Zeile wird zu genau einem Eintrag in `SetSwitchAssignments` (**V5**, Handgriff des
Betreibers): HandOfBlood zu Wechseleinträgen, alle übrigen zu **Kein-Wechsel-Einträgen** mit der
hier abgelesenen `ActiveEmoteSetId` als `ConfirmedEmoteSetId`. Dass die ID hier abgelesen und dann
als Konstante mitgeschrieben wird, ist der Punkt: die Migration liest sie aus der Liste, nicht aus
der Datenbank, und kann deshalb widersprechen. **Die Ausgabe dieser Abfrage bleibt beim Betreiber**
— sie nennt Kanalnamen und IDs, und weder das Repo noch ein PR-Text nimmt sie auf (E1); was in den
PR-Text gehört, ist allein die **Zahl** der Zeilen, damit AK 2 nachrechenbar bleibt. Kanäle, die Zeilen tragen, aber `IsBotActive = false` sind,
erscheinen hier ebenfalls und brauchen ebenfalls einen Eintrag — nur **keine** Saatzeile (4.3).

### Sonde 7 — trägt v4 am `EmoteSet` ein Merkmal für persönliche Sets? (T0.5)

E7 wählte v3 aus einem einzigen Grund: `isPersonal` kam dort aus `flags & 4`. Ob v4 ein
entsprechendes Feld führt, war laut Konzept (Zeilen 1884–1887) eine ausstehende Introspektion.
Die v4-Liste ist im Übrigen gemessen tauglich (Konzept 11.1) und trägt den Besitzer mit.

```
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -d '{"query":"query { __type(name: \"EmoteSet\") { fields { name type { name kind ofType { name kind } } } } }"}'
```

**Gemessen am 2026-09-20: Zweig A.** `__type(name: "EmoteSet")` liefert die Felder `id, name,
description, tags, capacity, ownerId, kind, updatedAt, searchUpdatedAt, emotes, owner`; `kind` ist
`EmoteSetKind!` (ENUM, NON_NULL), Analyzer `complexity 9, depth 5`. `__type(name: "EmoteSetKind")`
liefert die Werte **`NORMAL`, `PERSONAL`, `GLOBAL`, `SPECIAL`**.

End-to-end gegen `platformId: "49140130"` mit

```
query($pid: String!) { users { userByConnection(platform: TWITCH, platformId: $pid) { id style { activeEmoteSetId } emoteSets { id name capacity kind owner { id mainConnection { platformDisplayName } } } } } }
```

(Analyzer `complexity 14, depth 6`; ohne `style` waren es 12/6): `users.userByConnection.id` =
`01GQA28FCR0002Q9KS8SKQKVXX`, **vier** Sets, jedes mit Besitzer `HandOfBlood`:

| Set-ID | Name | Kapazität | `kind` |
|---|---|---|---|
| `01GV88A38G0006FW5TVZVMG507` | `HandOfBlood's Emotes` | 1000 | `NORMAL` |
| `01HMHSTX2G000CNKGAKWBJQA56` | `Personal Emotes` | **5** | **`PERSONAL`** |
| `01J94NYQR0000D15QN0BDGN85E` | `Halloween Set` | 1000 | `NORMAL` |
| `01J94Y3JDR0005G1FWF2H9ZHJT` | `Christmas Set` | 1000 | `NORMAL` |

**Der tragende Befund: v3 liefert drei Sets, v4 vier — v3 unterschlägt das persönliche Set.** Ein
Picker auf v3 hätte es nie gesehen und auch nicht ausschließen können.

**Nachmessung am 2026-09-20, drei Befunde:**

- **A — v4 liefert die aktive Set-ID mit.** `__type(name: "User")` trägt `style: UserStyle!`,
  `__type(name: "UserStyle")` trägt `activeEmoteSetId: Id` und `activeEmoteSet: EmoteSet`. Die
  Abfrage oben liefert für `platformId: "49140130"` `style.activeEmoteSetId` =
  `01GV88A38G0006FW5TVZVMG507` — **identisch** mit dem, was v3 `users/twitch/{id}` als
  `.emote_set_id` liefert (beides am selben Tag gegengeprüft). **Folge:** der zweite Request, den
  E21 für die aktive Set-ID ungetrackter Accounts vorsah (v3 `users/twitch/{id}`), entfällt;
  **ein** Request je
  Account deckt getrackte und ungetrackte (E7), und die E6-Rechnung „1 + *k* Permits" bleibt
  gültig. Für getrackte Kanäle bleibt `Channel.ActiveEmoteSetId` die Quelle (E21).
- **B — unbekannter Account.** Dieselbe Abfrage mit `platformId: "999999999999"` liefert
  `{"data":{"users":{"userByConnection":null}}}` bei **HTTP 200**, **ohne** `errors`-Block;
  Analyzer `complexity 7, depth 4`. **Folge:** `userByConnection: null` ist `NoSevenTvAccount`
  (200, `sets: []`) — ausdrücklich etwas anderes als „Antwort fehlt / Transportfehler", was
  `Unavailable` und 503 ist (6.1, AK 21).
- **C — `personalEmoteSet` und `specialEmoteSets`.** Der v4-`User`-Typ trägt sie als eigene Felder
  neben `emoteSets`. Gemessen liefert `emoteSets` das persönliche Set jedoch **mit** (vier Sets,
  darunter `Personal Emotes`/`PERSONAL`). Wir bleiben deshalb bei `emoteSets` + `kind`; die beiden
  anderen Felder sind hier nur festgehalten, falls ein Dokument einmal behauptet, v4 könne
  persönliche Sets nicht unterscheiden.

| Zweig | Bedingung | Vertrag |
|---|---|---|
| A (gemessen) | Die Feldliste trägt ein Merkmal für persönliche Sets | **E7 dreht auf v4**: `userByConnection … style { activeEmoteSetId } emoteSets { id name capacity kind owner { id mainConnection { platformDisplayName } } }` — ein Request, Besitzer **und** aktive Set-ID inklusive, kein `flags`-Rätsel, und kein Anteil an der #43-Fläche (F13). Der angezeigte Besitzer kommt je Set direkt aus der Antwort (`ownerDisplayName`) statt aus dem `editor_of`-Grant; für getrackte Kanäle liest der Dienst die aktive Set-ID weiter aus `Channel.ActiveEmoteSetId`, für ungetrackte aus `style.activeEmoteSetId` (E21) |

Zweig B ist gestrichen. Die Sonde war kein Tor: sie kostete einen Aufruf und hat zwischen zwei
gültigen Verträgen entschieden.

---

## 12. Kind-Issues unter #200 (Epic)

Schnitt entlang der Bauschritte 3–8 des Konzepts (E25), plus zwei Nicht-Code-Issues. Titel
englisch (Regel „Außenwirkung"), Umfang hier deutsch. Jedes Kind-Issue nennt die Spec und seinen
Abschnitt; #200 wird zum Epic mit dieser Liste (Regel: neue Issues gehören ins Epic).

| # | Titel | Umfang | Abhängigkeiten | Akzeptanzkriterien |
|---|---|---|---|---|
| K0 | **Preconditions: probes, assignment list, purges** (Betreiber, kein Code) | T0.1–T0.6 (Sonden in Abschnitt 11, T0.6 als Prüfaufgabe in Abschnitt 19; T0.1, T0.5 und T0.6 am 2026-09-20 gemessen); Zwischenweg V1 als **Empfehlung an das Mod-Team** vor dem 01.10.; Zuordnungsliste für HandOfBlood (Grenze, `ExpectedArchivedCount`) unabhängig davon; Purge Testkanal (V2) vor K7, Purge Wegwerfkanal (V3) **nur, falls V1 stattfindet** | — | AK 1–4 |
| K1 | **Count chat usage per emote set** (Schritt 3) | 4.1–4.3, 5, Migration, Beobachtungs-Log mit allen Schließstellen, `GetRowsAsync`-Summe, DECISIONS-Eintrag 1 | K0 nur für die Listenwerte (V5) und für T1.10 | AK 5–20, 90–91 |
| K2 | **Target set picker: any set of any account the user edits** (Schritt 4) | 6.1, 6.2, 6.4, 6.7, 6.8, 8.6, Preview-Kapazität, Set-ID-Lesepfad, set-zentrierter Endpunkt, Kollisions-/Alias-Gruppen, DECISIONS-Eintrag 2 | keine (parallel zu K1) | AK 21–46 |
| K3 | **Source set picker for foreign channels** (Schritt 5) | 6.3, 8.7 | K2 (Lesepfad, Listen-Dienst) | AK 47–49 |
| K4 | **Set view on the usage page: dropdown, per-set filters, union list, row identity** (Schritt 6) | 6.5, 7, 8.1–8.5, 8.9, Export, Türen auf das gewählte Set, DECISIONS-Eintrag 4 (erster Teil) | K1 (Set-Filter), K2 (Set-Liste, Lesepfad) | AK 50–66 |
| K5 | **Delete and restore in the selected set** (Schritt 7) | 6.6, 7.2 (Queue-Key, Protokoll, Laufdatensatz), 8.8, 8.10, DECISIONS-Eintrag 4 (Nachtrag) | K4 | AK 67–74 |
| K6 | **Vote sessions over a non-active set** (Schritt 8) | 6.9, 9, E10 Worker-Wiederholung, DECISIONS-Eintrag 3 | K1, K2, K4 | AK 75–83 |
| K7 | **Maintenance-window deploy** (Runbook, kein Code) | Abschnitt 10, Live-Verifikation auf Prod | K0–K6 gemergt; Harness-Runbook freigegeben | AK 84–86 |

Folge-Issue nach dem Deploy: Altform entfernen (E3, Abschnitt 21).

---

## 13. Aufgaben in Reihenfolge

Jeder Task läuft als eigener Subagent mit frischem Kontext (globale Regel); die Prüfliste aus
Abschnitt 19 wird **vor** dem jeweiligen Task abgearbeitet. „Fertig" heißt: die drei Suiten grün
(`dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e`
ohne Api auf `:5151`), dazu Regel 16 je Bauschritt.

**Vorbedingungen (Betreiber, außerhalb des Branchs):**

| # | Handlung | Termin |
|---|---|---|
| V1 | Zwischenweg (Konzept 12.4): Wegwerfkanal (nie getrackt) bestimmen, Halloween dort aktiv, tracken, übertragen, Kollisionen im Dialog abwählen. **Eine Empfehlung an HandOfBloods Mod-Team, keine Vorbedingung, die wir erfüllen können** — der Betreiber kann den Weg vorschlagen, nicht steuern (2026-09-20). Findet er nicht statt, entfällt **V3** ersatzlos. **An `SetSwitchAssignments` ändert das nichts** — der frühere Satz „dann hat die Zuordnungsliste einen Kanal weniger" war falsch: der Wegwerfkanal steht dort ohnehin nie, weil V3 ihn vor der Migration purgt (4.2). Auch die erwartete Trefferzahl der Gegenprobe bleibt bei zwei Zeilen, weil Sonde 6 **nach** V3 läuft. Was entfällt, ist eine Purge-Handlung, kein Listeneintrag. Der Schutz gegen den Fall „am Wegwerfkanal ist doch etwas passiert, von dem wir nichts wissen" liegt seit dem 2026-09-20 in **zwei** Prüfungen statt in einer: existiert der Kanal noch und trägt Nutzungszeilen, verlangt **Prüfung 3** (Lückenlosigkeit) eine ausdrückliche Aussage über ihn, und **Prüfung 4** (Widerspruch) bricht ab, wenn diese Aussage „nie gewechselt" lautet und die Signatur dagegen spricht (4.2) | Vorschlag vor dem 2026-10-01 |
| — | **Unabhängig von V1:** HandOfBlood wechselt am 01.10.; **am Wechseltag** `ExpectedArchivedCount` mit der ID-Sonde aus Konzept 11.2 messen und `BoundaryUtc` notieren. Beide Werte hängen am Wechsel, nicht am Zwischenweg, und werden so oder so gebraucht (V5) | am 2026-10-01 |
| V2 | Testkanal per Admin-Purge räumen | nach Sonde 6, vor K7 |
| V3 | Wegwerfkanal purgen — **entfällt, wenn V1 nicht stattfindet** | nach dem 01.10., vor Sonde 6 |
| V4 | **Datenbank-Kopie für die Migrationsprobe (T1.10) beschaffen.** Zwei Quellen, beide gültig: die Sicherung der Dev-Datenbank von vor dem Leerräumen (`~/projects/emotepurge-devdb-vor-purge-2026-09-20.sql.gz`, 766 K, 27 Kanäle, 9 214 Emotes, 6 022 Nutzungszeilen über 18 Tage ab 2026-08-29, 3 Nutzer — liegt bereits vor) und, **aussagekräftiger**, eine Kopie der Produktionsdatenbank über die bestehende Backup-Kette. Nur die Prod-Kopie trägt die echte Zeilenzahl und macht die gemessene `Up`-Dauer zur belastbaren Länge des Wartungsfensters. Das Beschaffen ist ein Handgriff des Betreibers, kein Task — der Plan verbindet sich nicht nach außen | vor T1.10 |
| V5 | **Die Klassifikationsdatei anlegen und füllen** (neu, 2026-09-20 — war bis dahin der Commit T1.9). `cp src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs.example src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs`, darin HandOfBloods Wechseleinträge (beide Set-IDs stehen fest, `BoundaryUtc` und `ExpectedArchivedCount` aus der ID-Sonde vom Wechseltag) **und** je einen Kein-Wechsel-Eintrag für jeden übrigen Kanal aus der Zählabfrage neben Sonde 6 — `ConfirmedEmoteSetId` aus deren Ausgabe **abgeschrieben**, nicht abgeleitet. Die Datei ist gitignoriert; nichts davon geht ins Repo, in einen PR-Text oder in den DECISIONS-Eintrag (E1, 4.2). Ins Repo geht **eine Zahl**: die Zahl der Einträge (AK 2). **Kein Task, kein Commit** — der Handgriff hängt an Wissen, das nur der Betreiber hat | nach dem 01.10. **und** nach T0.4; vor T1.10 und vor K7-Schritt 4 |
| T0.1–T0.6 | Sonden (Abschnitt 11; T0.6 steht als Prüfaufgabe in Abschnitt 19) — T0.1, T0.5 und T0.6 am 2026-09-20 gemessen | T0.3 vor K5, T0.2 vor K2, T0.4 vor K7 |

**Branch-Arbeit:**

| # | Task | Warum diese Position |
|---|---|---|
| T1.1 | `UsageCounterKey`, `EmoteMatchSnapshot`, Interfaces (5); `EmoteUsageCounter`, `EmoteMatchCache` mit Tests zuerst (`Worker.Tests`, `Infrastructure.Tests/Unit`) | pure Klassen, kein Container; die 17 bestehenden Fälle werden mit umgestellt |
| T1.2 | `TwitchChatManager` liest den Snapshot; `RefreshMatchCacheAsync`/Warmstart übergeben die Set-ID; Log beim Tausch | braucht T1.1 |
| T1.3 | Entitäten, `AppDbContext`, Migration mit Prüfungen 1–6, Backfill, Indextausch, Saat, `Down`; `PendingMigrationGuardTests` bleibt grün; dazu der **Lader** der Klassifikation samt Abbruch, `.example` und Ignore-Einträgen (E1) | braucht nichts; die Listen**werte** kommen außerhalb des Repos dazu (V5) |
| T1.4 | `UsageStatFlushService` (vier Arrays, dreispaltig) + Integrationstests inkl. zwei Set-IDs an einem Tag | braucht T1.1, T1.3 |
| T1.5 | `IChannelEmoteSetObservationService` + alle Schließ-/Öffnungsstellen (4.3) + Tests je Stelle | braucht T1.3 |
| T1.6 | `UsageStatQueryService`: Set-Filter überall, `GetTotalsByEmoteIdsAsync` mit Pflichtparameter, `GetRowsAsync`-Summe, `/series` **additiv** um `SevenTvEmoteId` erweitert (`emoteId` bleibt, 6.5 Schritt 1), `NameTwinEmoteSetIds`; Wire-Format-Test um das zweite Feld erweitert | braucht T1.3 |
| T1.7 | Wechsel-Tests (5) und Live-Verifikation an der Dev-Box: Set-Wechsel im Dev-Kanal, Zeilen beider Sets im Flush-Fenster, Leave/Rejoin ⇒ zwei Intervalle | braucht T1.2–T1.5 |
| T1.8 | DECISIONS-Eintrag 1, im selben Commit wie Migration + Flush | Regel 3 |
| T1.9 | **Entfällt als Commit** (2026-09-20, E1): die Werte leben in einer gitignorierten Datei, es gibt dort nichts zu committen. Der **Mechanismus** (committete Klasse, `Load()` mit Abbruch, `.example`, `.gitignore`/`.dockerignore`, Testsitz) ist Teil von **T1.3b**, weil dessen eigene Tests ihn brauchen; das **Füllen** ist der Betreiber-Handgriff **V5**. Die Nummer bleibt als Wegweiser stehen, damit keine andere wandert | die Aufteilung folgt dem Ort der Liste, nicht der Reihenfolge |
| T1.10 | **Migrationsprobe gegen eine wiederhergestellte Datenbank** (Wegwerfdatenbank im lokalen Postgres-Container): `Up` läuft durch **und die Dauer wird gemessen**; je Abbruchprüfung ein konstruierter Verstoß; `Down` verweigert an Schranke 1, ohne etwas entfernt zu haben; Sonde-6-Abfrage als Trockenlauf | letzter Schritt von K1 vor dem PR; braucht **V5** (die gefüllte Datei) und V4 |
| T2.1 | `ISevenTvEmoteSetListService` (v4 `userByConnection … style { activeEmoteSetId } emoteSets`, Cache `7tvsets:`, **Singleflight, Breaker, Budget** — die Wächterkette aus 6.1) + Client-Tests mit der Live-Fixture aus Sonde 7 | Grundlage für drei Routen |
| T2.2 | Preview-Abfrage mit `capacity`/`name`; `ForeignEmoteSet` erweitert (`sevenTvUserId` nullbar); Set-ID-Lesepfad im Hardened-Dekorator (zweiter Schlüsselraum) + Tests (die 8 `SevenTvForeignEmoteSetEndpointTests`, 9 Hardened-, 14 Client-Vorschautests bleiben grün) | braucht nichts; T2.1 parallel |
| T2.3 | Routen 6.1, 6.2, 6.4 (`?emoteSetId=`), 6.8 (`?emoteSetId=`), `EmoteSetIdValidationFilter`, Fehlercodes (E13) in `ApiErrorCodes.cs` + `api-error.ts` + beide Locales; `Api.Tests`-Matrix | braucht T2.1, T2.2 |
| T2.4 | `GqlEditorOfQuery` + `SevenTvUserId` im Grant (E22, F10); `SyncImportedRequest.TargetEmoteSetId`; set-zentrierter Endpunkt; `ProjectDetail`; `AuditLogDetail.TargetEmoteSet`; Tests | braucht T2.3 (Filter) |
| T2.5 | Frontend getrackte Klasse: `ImportTargetChoice`, Picker über 6.2, `loadImportTarget` mit Set-Ziel, `truncated ⇒ failed`, Setname im Kopf, Preview-Gruppen, Projektion ohne Kollisionen; Specs | braucht T2.3 |
| T2.6 | Frontend ungetrackte Klasse: Bestätigung, `channelName: null`, Report an den set-zentrierten Endpunkt; Audit-Ansicht; E2E „gleicher Kanal, anderes Set" | braucht T2.4, T2.5 |
| T2.7 | Live-Verifikation Dev-Box (Konzept 13.4) + DECISIONS-Eintrag 2 im Commit von T2.5 | Regel 16, Regel 3 |
| T3.1 | Route 6.3, Quell-Set-Radiogroup im `ForeignChannelStep`, Specs, E2E | braucht T2.1–T2.3 |
| T4.1 | `mergeSetView` (pur) + Zeilenmodell (7.1) + Spec — **vor** jeder Template-Änderung | reine Logik, Regel 12 |
| T4.2 | Set-Dropdown, URL-Zustand, `retainAmong` beim Wechsel, Set-Liste einmal je Kanal, `clearSeriesCache` | braucht T4.1, T2.3 |
| T4.3 | Schlüsselwechsel (7.2): `ListSelection`, inneres `track`, Maps, `DeletableEmote.emoteId?`, Drilldown-Gate, Voting-Draht mit Auflösung; `usage-stat.service` Cache-Schlüssel | braucht T4.1; in **einem** Commit mit T4.4, weil das Raster ab der ersten Guid-losen Zeile nicht mehr auf der Guid stehen darf |
| T4.4 | Nicht-aktive Ansicht: Live-Liste laden, Klassen, Badge, `null`-Gruppe, Duplikat-Zelle, Namensvetter-Merkmal, Tatsachenangabe, Preset, Kapazität aus der Live-Liste, Lade-/`truncated`-Sperren | braucht T1.6, T2.2 |
| T4.5 | Export mit Set und `null`; Türen (Datei/Fremdkanal/Bestenliste) und Protokollprüfung auf das gewählte Set; `CapturedExportScope`/`CapturedImportScope` | braucht T4.2 |
| T4.6 | DECISIONS-Eintrag 4 (Identität, `/series`, Export, Caches) im Commit von T4.3; E2E: Set-Ansicht mit gemockter Live-Liste, Guid-lose Zelle markieren | Regel 3 |
| T5.1 | `RunResult.doneIds` entfernen (E2), Queue-Keys, Protokoll (`emoteId` optional, Parser), Laufdatensatz mit Set-ID, Panel `deleted` als Keys (E18), Detailseite `onDeleted` (F12); Specs | braucht T4.3 |
| T5.2 | `sync-deleted`/`sync-restored` neue Form + Altform (6.6), Service-Überladung, Log-Zeile (E3), Papier-Fall; `Api.Tests` beide Formen; `EmoteServiceTests` | braucht T2.3 (Filter) |
| T5.3 | Lösch-/Restore-Bestätigung mit Setname; Duplikat-Gruppe (8.9 oder Sonde-5-Zweig); Audit-Ansicht; Live-Verifikation am Testkanal (Konzept 13.7); DECISIONS-Eintrag 4 Nachtrag im Commit von T5.2 | braucht T5.1, T5.2 |
| T6.1 | Entitäten/Migration sind aus K1; `VoteSessionService.CreateAsync` Set-Pfad (9), Ausschlussregel, Fehlercode; `IsEmoteVotableAsync`; Tests | braucht T1.3, T2.2 |
| T6.2 | Worker-Wiederholung (E10) + Wettlauftest mit erzwungener Verschränkung (zwei `AppDbContext`) | braucht T6.1 |
| T6.3 | `GetResultsAsync`: `eligible`, Set-Totals, eingefrorene Anzeigedaten; Detailseite `canSelectForDelete = canManage`, Panel-Set-ID, Badge auf `eligible`; Dialog „Zur Abstimmung stellen" mit Set-Session; Specs + E2E; DECISIONS-Eintrag 3 | braucht T6.1, T4.3 |
| T7 | `node scripts/coverage-local.mjs`; `/codex:review --model gpt-5.6-sol --scope branch`; PR; Merge durch den Nutzer; K7 nach Freigabe | Regel 22 |

**Abhängigkeiten:** T1.* ∥ T2.*; T3 nach T2.3; T4 nach T1.6 + T2.3; T5 nach T4.3; T6 nach T1.3 +
T2.2 + T4.3. Deployt wird **einmal** (K7).

---
## 14. Akzeptanzkriterien

Nummeriert, pass/fail. Gruppiert nach Kind-Issue.

**K0 — Vorbedingungen**

1. T0.1–T0.6 sind ausgeführt; je Sonde steht der gemessene Zweig (A/B) mit Datum in dieser Spec
   und der jeweils andere ist gestrichen. **Stand 2026-09-20 gemessen: T0.1** (Sonde 1),
   **T0.5** (Sonde 7) und **T0.6** (`editor_of { user { id } }` — die Prüfaufgabe aus Abschnitt 19;
   sie steht dort und im Plan, 0.2, nicht in Abschnitt 11). **Offen: T0.2** (Sonde 4),
   **T0.3** (Sonde 5), **T0.4** (Sonde 6).
2. Die Zuordnungsliste für HandOfBlood trägt `BoundaryUtc` und `ExpectedArchivedCount` aus der
   ID-Sonde vom Wechseltag. **Und sie ist lückenlos:** jeder Kanal aus der Zählabfrage neben
   Sonde 6 (Abschnitt 11) hat einen Eintrag einer der beiden Arten — HandOfBlood Wechseleinträge,
   alle übrigen einen Kein-Wechsel-Eintrag mit ausgeschriebener `ConfirmedEmoteSetId`. Die Zahl der
   Einträge ist gleich der Zahl der Zeilen der Zählabfrage. **Nachgewiesen wird das über die Zahl,
   nicht über die Liste:** die Werte stehen in der gitignorierten
   `SetSwitchAssignments.Local.cs` (V5, E1), nicht im Repo, nicht im PR-Text und nicht im
   DECISIONS-Eintrag 1 — dort steht nur das Verfahren. Der Betreiber führt V5 aus und nennt im
   PR-Text zwei Zahlen: Zeilen der Zählabfrage und Einträge der Klassifikation.
3. Sonde 6 liefert genau die zwei erwarteten Zeilen (Testkanal, HandOfBlood mit passendem Tag und
   passender Zahl); danach existiert der Testkanal in der Admin-Kanalliste nicht mehr.
4. Der Wegwerfkanal hat vor Sonde 6 keine `Channel`-Zeile mehr (Admin-Purge); die
   `emotes.syncImported`-Einträge unter seinem Namen sind die einzige Spur. **Findet V1 nicht
   statt, gibt es keinen Wegwerfkanal und das Kriterium entfällt** — an `SetSwitchAssignments` und
   an der erwarteten Trefferzahl von Sonde 6 (AK 3) ändert das nichts (4.2, V1).

**K1 — Zählen pro Set**

5. Die Migration gegen eine Datenbank mit einer `UsageStats`-Zeile, deren Kanal eine leere
   `ActiveEmoteSetId` hat, bricht mit einer Ausnahme ab und ändert **nichts** (Prüfung **6**; Test
   in `Infrastructure.Tests/Integration` gegen den ephemeren Container).
6. Fünf weitere Abbrüche, je ein Test (Prüfungen 1–5): eine veraltete Liste (letzte
   `NewEmoteSetId` bzw. `ConfirmedEmoteSetId` ≠ `ActiveEmoteSetId`, Prüfung 1); eine fehlende
   Signatur am Grenztag (Prüfung 2); **ein Kanal mit `UsageStats`-Zeilen ohne Eintrag beider Arten
   — die Ausnahme nennt den Kanal** (Prüfung 3); ein Kanal mit Kein-Wechsel-Eintrag, der die
   Massenarchivierungs-Signatur trägt (Prüfung 4); zwei Wechseleinträge eines Kanals, deren
   `NewEmoteSetId`/`OldEmoteSetId` nicht aneinanderschließen (Prüfung 5).
7. Nach erfolgreicher Migration hat jede `UsageStats`-Zeile eine nicht-leere `EmoteSetId`; für einen
   Kanal mit Wechseleinträgen tragen Zeilen mit `Date < boundary_day` die alte, mit
   `Date >= boundary_day` die neue ID; Kanäle mit Kein-Wechsel-Eintrag die **`ConfirmedEmoteSetId`
   aus der Liste** (im Test von der `ActiveEmoteSetId` unterscheidbar gemacht, damit die Assertion
   die Quelle prüft und nicht nur den Wert).
8. `IX_UsageStats_EmoteId_Date` existiert nicht mehr; `IX_UsageStats_EmoteId_EmoteSetId_Date` ist
   unique mit `INCLUDE ("UseCount")` (Assertion über `pg_indexes`).
9. Die Saat legt für jeden aktiven Kanal mit Kein-Wechsel-Eintrag genau eine offene
   Beobachtungszeile ab `COALESCE(TrackingResumedAt, CreatedAt)`, für Kanäle mit Wechseleinträgen
   je Intervall eine, für inaktive keine — auch dann nicht, wenn sie klassifiziert sind und
   Nutzungszeilen tragen. **Und, als eigener Fall:** nach `Up` existiert **keine**
   `ChannelEmoteSetObservation`-Zeile mit `ClosedBy = 'set-switch'`. Das ist die Invariante, an der
   die erste `Down`-Schranke hängt (4.2, 4.3); sie hatte bis zum 2026-09-20 nur eine Auflage im
   Text und keinen Test.
10. `Down` auf einer Datenbank ohne Set-Wechsel stellt den zweispaltigen Index wieder her; auf einer
    Datenbank mit zwei Set-IDs an einem `(EmoteId, Date)` schlägt sie fehl; und auf einer Datenbank
    mit einer `ClosedBy = 'set-switch'`-Zeile, **deren Wechsel kein einziges `(EmoteId, Date)`
    doppelt belegt** (disjunkte Sets, Wechsel zwischen zwei Tagen), schlägt sie **ebenfalls** fehl
    und hat **nichts** entfernt — weder Spalte noch Tabelle noch Index (drei Tests; der dritte ist
    der Fall, den die Index-Schranke allein durchlässt).
11. `FlushAsync` mit zwei Schlüsseln `(E, S1)` und `(E, S2)` am selben Tag schreibt zwei Zeilen; ein
    zweiter Flush auf `(E, S1)` addiert (`ON CONFLICT` dreispaltig); alle drei Zählerspalten
    unabhängig.
12. `EmoteUsageCounter.Increment(e, s, cat)` ×3 und `Merge` eines zurückgestellten Batches mit
    anderer Set-ID ergeben zwei Schlüssel mit korrekten Summen; `PendingEmoteCount` = 2.
13. `GetChannelSnapshot` liefert Wörterbuch, Set-ID und `GeneratedAtUtc` als ein Objekt; nach
    `ReplaceChannel` mit anderer Set-ID sehen zwei aufeinanderfolgende Aufrufe nie ein gemischtes
    Paar (Test mit Nebenläufigkeit über 1.000 Tausche).
14. Wechsel-Test: drei Nachrichten mit demselben Emote-Namen — vor, während (nach dem Tausch, vor dem
    Flush) und nach dem Tausch — landen als zwei Schlüssel; die erste unter der alten, die dritte
    unter der neuen Set-ID; ein gleichnamiges Paar (`Stare` Zeile A/B) zählt nie auf beide.
15. Der Cache-Tausch mit anderer Set-ID schreibt genau eine Log-Zeile mit beiden Set-IDs und beiden
    Generationszeitpunkten; ein Tausch mit gleicher Set-ID schreibt keine.
16. Nach `SyncChannelAsync` ohne offene Zeile existiert eine offene Beobachtungszeile; nach einem
    `emoteSetSwitched` ist die alte geschlossen (`ClosedBy = 'set-switch'`) und eine neue offen —
    in derselben Transaktion (Test: Ausnahme nach dem Schließen ⇒ beides zurückgerollt).
17. `LeaveAsync`, Rename beim Join, `RenameAsync`, Merge schließen die offene Zeile mit dem
    jeweiligen `ClosedBy`; ein Rejoin auf dasselbe Set öffnet eine **neue** Zeile (fünf Tests).
18. Der partielle Unique-Index verhindert zwei offene Zeilen je Kanal (Test: zweiter `INSERT` mit
    `ObservedToUtc IS NULL` scheitert).
19. `GetUsageContextAsync(…, emoteSetId: S2)` liefert für ein Emote mit Zeilen unter S1 und S2 nur die
    S2-Summe; `null` ⇒ aktives Set; `GetRowsAsync` liefert je `(EmoteId, Date)` **eine** Zeile mit
    der Summe über S1 und S2.
20. `GetChannelSeriesAsync` benennt Einträge nach `SevenTvEmoteId` und **führt `emoteId`
    übergangsweise weiter** (6.5, Schritt 1); mit einer aktiven, einer archivierten und einer
    Guid-losen Zeile (die es in der DB nicht gibt) erscheinen genau die ersten beiden unter ihrer
    7TV-Id, beide mit gefülltem `emoteId`. `ChannelUsageSeriesWireFormatTests` pinnt **beide**
    Felder, und **kein** bestehender Fall wird dabei rot.

**K2 — Ziel-Set-Picker**

21. `ListByTwitchIdAsync` liefert aus einer v4-Antwort je Set `{id, name, capacity, kind,
    ownerDisplayName}` und `isPersonal = kind == PERSONAL`, dazu `activeEmoteSetId` aus
    `style.activeEmoteSetId` derselben Antwort (E7/E21). **Wählbar ist nur `kind == NORMAL`:**
    `PERSONAL`, `GLOBAL` und `SPECIAL` werden gleich behandelt — sichtbar, deaktiviert,
    beschriftet (8.6), nie kommentarlos wählbar; `isPersonal` trennt nur die Beschriftung.
    Fehlerabbildung, gemessen am 2026-09-20: `userByConnection: null` bei **HTTP 200 ohne
    `errors`-Block** ⇒ `NoSevenTvAccount`; eine Antwort **mit** `userByConnection`, aber **ohne**
    `emoteSets` ⇒ `Unavailable`; fehlende Antwort, Transportfehler oder 429 ⇒ `Unavailable`. In
    keinem der Fälle eine stille leere Liste.
22. `GET /api/channels/{c}/emote-sets` antwortet 401 ohne Session, 403 ohne Rolle, 404 für einen
    unbekannten Kanal, 200 mit `sets: []` bei `TwitchChannelId == null`, 503
    `foreign_channel_seventv_unavailable` bei 7TV-Fehler (fünf `Api.Tests`).
23. Zwei Aufrufe innerhalb von 60 s für denselben Kanal erzeugen genau **einen** Upstream-Request
    (Cache `7tvsets:`); ein Redis-Ausfall degradiert zu „immer live", nicht zu 503. **Ebenso bei
    Gleichzeitigkeit:** *n* parallele kalte Cache-Misses für denselben Kanal erzeugen genau **einen**
    Upstream-Request (Singleflight, 6.1 Wächter 2) — der Fall, den ein Cache ohne Koaleszierung
    nicht abdeckt. Ein negatives Ergebnis (Unavailable/RateLimited/Breaker/Budget) wird mit kurzer
    Haltbarkeit gehalten: der zweite Aufruf innerhalb dieser Frist erzeugt **keinen** Upstream-
    Request; `NoSevenTvAccount` wird wie ein Treffer gehalten.
24. Jeder Upstream-Request der drei Listen-Routen und des Set-ID-Lesepfads zieht genau ein Permit
    des `IForeignUpstreamRequestBudget` (Test mit `RecordingForeignUpstreamRequestBudget`).
25. `GET /api/seventv/me/emote-set-targets` antwortet 401 ohne Session; 200 mit dem eigenen Account
    zuerst; ein `editor_of`-Account mit getrackter, aktiver `Channel`-Zeile trägt
    `trackedChannelName`, einer ohne `null`; Grants `Failed` ⇒ nur der eigene Account und
    `sevenTvUnavailable: true`.
26. `GET /api/seventv/channels/{c}/emotes?emoteSetId=X` löst keine Identität auf (der
    Helix-Ersatz wird nicht aufgerufen), liest den Cache-Schlüssel `7tvforeign:set:X`, und die
    Antwort trägt `capacity`, `emoteSetName`, `sevenTvUserId: null`; ein Eintrag desselben Kanals
    ohne `emoteSetId` überschreibt ihn nicht (zwei Schlüsselräume).
27. `emoteSetId=` mit Leerzeichen, 33 Zeichen oder `../x` ergibt 400 `invalid_emote_set_id` ohne
    Service-Aufruf (drei `Api.Tests`).
28. Die Preview-Antwort behält #74-Duplikate als zwei Einträge (`totalCount` zählt beide); die
    bestehenden 14 Vorschau-Client-Tests, 9 Hardened-Tests und 8 Endpunkt-Tests bleiben ohne
    Änderung an ihren Dateien grün.
29. `SyncImportedRequest` ohne `targetEmoteSetId` ist gültig; mit ungültigem Format 400; der
    Audit-Eintrag trägt `targetEmoteSetId`, `targetIsActiveSetOfChannel` (true/false/null),
    `TargetType = "emoteSet"`.
30. `POST /api/seventv/emote-sets/{id}/sync-imported`: 401 ohne Session; 400 für die
    Vokabeltabelle in allen Richtungen (die sechs Fälle aus `AuthFilterMatrixTests:394-503`
    gespiegelt); 404 `emote_set_not_found` für ein unbekanntes Set; **403 bare** für ein Set, dessen
    Besitzer weder der Akteur noch ein `editor_of`-Account ist; 503 bei 7TV-Fehler ohne Eintrag;
    204 sonst mit `ChannelName = null`.
31. Ein Grant-Eintrag ohne `SevenTvUserId` (Legacy-Payload) wird live nachgelöst und zählt danach als
    Editor; er wird nie als „kein Editor" gelesen.
32. `ProjectDetail` liefert für einen Import-Eintrag mit `targetEmoteSetId` ein `TargetEmoteSet`;
    ohne bleibt es `null`; die 19 `AuditLogQueryServiceTests` bleiben grün.
33. `set-warning?emoteSetId=X` für ein nicht-aktives X: Tier 1 fragt den Besitzer von X, Tier 2
    findet einen anderen Kanal mit `ActiveEmoteSetId == X`, Tier 3 vergleicht gegen X (drei Tests);
    ohne Parameter wie heute (die 5 bestehenden Tests grün).
34. Der Picker ruft `listMine()` nicht mehr; getrackte Accounts stehen oben unter ihrem Kanalnamen
    mit aufklappbaren Sets, das aktive beschriftet und vorausgewählt; Sets mit `kind != NORMAL`
    (`PERSONAL`, `GLOBAL`, `SPECIAL`) sind sichtbar, aber deaktiviert und beschriftet (8.6); der
    eigene Kanal ist gelistet, nur sein Quell-Set deaktiviert.
35. Die Wahl eines ungetrackten Sets öffnet eine Bestätigung mit Setname und
    `ownerDisplayName` (Anzeigename, kein Login — E7); Abbruch
    lässt die Wahl unverändert; Bestätigung schließt den Picker mit `channelName: null`.
36. `loadImportTarget` für ein nicht-aktives Set liest Belegung (`totalCount`) und Kapazität aus der
    Live-Liste, nicht aus `EmoteSetStatus`; `truncated ⇒ failed`; für das aktive Set ändert sich
    kein Request (die 8 bestehenden Loader-Tests grün).
37. `buildImportPreview`: gleicher Name, andere ID ⇒ **nicht** in `toAdd`, in `nameCollisions`;
    gleiche ID, anderer Alias ⇒ nicht in `toAdd`, in `aliasMismatches` mit Quellname/Zielalias, nicht
    in `alreadyPresent`; #74-Duplikat mit einem gleichen Alias ⇒ `alreadyPresent`; `invalidNames`
    bleibt in `toAdd`.
38. Mit den Zahlen des Anlasses (762 Quellzeilen, Halloween-Liste) zeigt der Dialog 338 vorhanden
    (davon ~10 als Alias-Abweichung), 192 Kollisionen, ~232 in `toAdd`, Projektion 687 + 232 = 919
    ohne Überlauf-Banner (Spec mit synthetischer Liste dieser Größen).
39. Der Dialogkopf nennt Kanal und Setname; für ein ungetracktes Ziel Besitzer und Setname.
40. Der Lauf enthält keine Kollisions- und keine Alias-Zeile; die `failed`-Zahl im Dock ist für einen
    Lauf mit gemockten Kollisionen 0.
41. `reportImported` sendet `targetEmoteSetId` für getrackte Ziele und ruft für ungetrackte den
    set-zentrierten Endpunkt **ohne** Nachlauf-Resync auf.
42. Live-Verifikation Dev-Box (Regel 16): Import in ein nicht-aktives Set des Testkanals; die
    Dialogzahlen stimmen mit `GET /v3/emote-sets/{id}` überein; der Audit-Eintrag trägt Set-ID und
    `targetIsActiveSetOfChannel: false`; eine absichtliche Namenskollision und eine
    Alias-Abweichung erscheinen als Gruppen, der Lauf meldet keine `failed`-Zeile.
43. E2E „gleicher Kanal, anderes Set" mit gemockter Set-Liste und Set-Vorschau: der Lauf-`setId`
    ist das gewählte Set (Request-Assertion auf die GQL-Mutation), nicht das aktive.
44. Der Client sendet `targetEmoteSetId` bei **jedem** `sync-imported` (Vitest-Spec pinnt es).
45. Die vier neuen Fehlercodes stehen in `ApiErrorCodes.cs`, `api-error.ts` und beiden Locales;
    `api-error-locales.spec.ts` grün.
46. `EmoteRoutePolicyTests` deckt die neuen Routen mit ihrer Policy (`InteractiveRead`,
    `ForeignEmoteLookup`, `Bookkeeping`).

**K3 — Quell-Set-Picker**

47. `GET /api/seventv/channels/{c}/emote-sets` löst per Helix auf, liest die Set-Liste aus v4 (E7),
    antwortet mit `activeEmoteSetId` aus `style.activeEmoteSetId` **derselben** v4-Antwort
    (E7/E21) — kein zweiter Request, kein v3-Aufruf; die Zustandstabelle aus
    `SevenTvEndpoints.cs:47-69` gilt (fünf `Api.Tests`).
48. Der `ForeignChannelStep` zeigt nach der Auflösung ein Radiogroup der Sets; die Vorschau lädt für
    ein gewähltes nicht-aktives Set mit `?emoteSetId=`; bleibt das aktive gewählt, gibt es keinen
    zweiten Request.
49. `ForeignChannelImportResult.emoteSetId` trägt das gewählte Set; der Bestätigungsdialog rechnet
    Kollisionen gegen das Zielset (Regressionsfall aus #147 AK 17 bleibt grün).

**K4 — Set-Ansicht und Zeilenidentität**

50. Das Dropdown listet die Sets aus 6.1, aktives vorausgewählt und beschriftet, Sets mit
    `kind != NORMAL` deaktiviert und beschriftet (8.6); kein Eintrag „Gesamt".
51. Ein Set-Wechsel ruft `retainAmong` (Markierungen von Emotes, die in beiden Sets liegen, überleben;
    die #94-Meldung erscheint für weggefallene), `clearSeriesCache()`, und lädt `/totals`, `/series`
    mit `emoteSetId` sowie die Live-Liste; die Set-Liste wird **nicht** neu geladen.
52. `usage.flushed` lädt nur `/totals` und `/series`; `channel.synced` zusätzlich die Live-Liste
    (Request-Zählung im Spec mit `HttpTestingController`).
53. `mergeSetView`: Live-Mitglied ohne Zeile ⇒ `emoteId: null, totalUseCount: null, membership:
    'live'`; Zeile ohne Live ⇒ `'left'`; zwei Live-Einträge derselben ID ⇒ eine Zeile mit
    `slotCount 2` und beiden Aliasen; aktive Ansicht ⇒ jede Zeile `'live'`, `slotCount 1`.
54. `ListSelection` mit zwei Guid-losen Zeilen: beide einzeln wählbar und unterscheidbar;
    `retainAmong` behält die richtige.
55. Das innere `track` erzeugt bei zwei Guid-losen Zeilen keine NG0955-Warnung (Spec prüft
    eindeutige Schlüssel über das Zeilenmodell; E2E prüft die Konsole).
56. `null`-Zeilen stehen nicht in Summe, Pareto-Nenner oder Bändern; sie bilden eine eigene Gruppe am
    Ende in Namensreihenfolge; die Beschriftung „keine Zählungen unter diesem Set" hängt an
    `totalUseCount === null`, nicht an `emoteId === null` (Klasse 2b, AK 63).
57. `'left'`-Zeilen tragen das Badge und sind nicht wählbar (Sperrgrund im Dialog benannt); sie
    zählen in Summe und Nenner.
58. Duplikat-Zellen tragen die Slot-Zahl; die Slot-Projektion des Docks zählt 2.
59. Eine Zeile mit `nameTwinEmoteSetIds` zeigt das Merkmal mit dem Setnamen aus der Dropdown-Liste
    im Tooltip; keine Zusammenrechnung (Summe der Halloween-Ansicht enthält nur Halloween-Zeilen).
60. Tatsachenangabe, als Matrix aus 8.4 geprüft (vier Fälle): ohne schneidendes Intervall **und**
    ohne Zahlen stehen **B−** und **Z−**; ohne schneidendes Intervall, aber **mit** Zahlen steht
    **B−** **allein** und keine Aussage über Zahlen — das ist der Fall „zuvor inaktiver Kanal, von
    der Migration auf eine Set-ID backfillt, später rejoined: ein historischer Zeitraum trägt Zahlen
    ohne Beobachtungsintervall"; mit Intervallbeginn im Zeitraum **B~**; für das aktive Set mit
    offenem Intervall ab Tracking-Beginn wie heute. **Z−** stammt in keinem Fall aus
    `observations`, sondern ausschließlich aus den geladenen Totals.
61. Das Preset `'set-observed'` erscheint nur bei vorhandenem Intervall und setzt Beginn/Ende des
    jüngsten.
62. Live-Liste 503 oder `truncated` ⇒ Hinweis, Löschweg gesperrt mit Grund, DB-Zeilen sichtbar.
    **Set-Liste** 503 (6.1) ⇒ die Seite bleibt auf dem aktiven Set aus `setStatus()`, der
    Dropdown-Auslöser ist gesperrt mit Grund, kein Banner (8.1).
63. Nach dem Anlegen einer Set-Session (K6) zeigt die Set-Ansicht für ein vorher zeilenloses
    Mitglied weiterhin `null`, nicht 0 (Integrationsfall über `/totals?emoteSetId=`).
64. `/daily`-Cache-Schlüssel und `/series`-Cache-Schlüssel enthalten die Set-ID: zwei Sets, gleicher
    Zeitraum ⇒ zwei Requests; der Drilldown liest die Set-ID aus `data`, ein Dropdown-Wechsel bei
    offenem Dialog ändert seinen Request nicht.
65. Export: Dateiname und `meta` tragen `emoteSetId`/`emoteSetName`; eine `null`-Zeile wird als leere
    CSV-Zelle, JSON `null`, `trend: 'unknown'` serialisiert, nicht ausgelassen.
66. Datei-, Fremdkanal- und Bestenlisten-Import zielen auf das gewählte Set; ein Purge-Protokoll mit
    `meta.emoteSetId` des Halloween-Sets wird in der Halloween-Ansicht angenommen und in der
    Hauptset-Ansicht abgewiesen.

**K5 — Löschen und Wiederherstellen**

67. `RunResult` hat kein `doneIds` mehr; der Build ist grün (jede Stelle umgestellt).
68. Delete- und Restore-Queue-Key ist `sevenTvEmoteId`; ein Lauf mit `emoteId: undefined` erzeugt
    eine Protokollzeile mit `emoteId: null` und meldet den Key in `doneKeys`.
69. `parsePurgeRunProtocol` akzeptiert `emoteId: null` und alte Protokolle mit Guid; beide sind
    wiederherstellbar.
70. `sync-deleted` neue Form, aktives Set: matcht über `(ChannelId, SevenTvEmoteId)`, archiviert,
    Audit mit `emoteSetId` und `targetIsActiveSetOfChannel: true`, `channel.synced` bei
    `NewlyArchivedCount > 0`. Nicht-aktives Set: keine Zeile geändert, Audit mit
    `targetIsActiveSetOfChannel: false`, kein `channel.synced`, Antwort `archivedCount: 0`.
    Altform: heutiges Verhalten plus Log-Zeile. Beide Listen leer 400 `emote_ids_empty`; beide
    gesetzt 400 `emote_ids_invalid`; neue Form ohne Set-ID 400 `emote_set_id_empty`. `sync-restored`
    spiegelbildlich (`Api.Tests` + `EmoteServiceTests`).
71. Der Laufdatensatz friert die Set-ID beim Start ein; Erstbericht und `retrySyncReport` senden
    dieselbe Set-ID auch nach einem Dropdown-Wechsel (Spec).
72. Das Panel emittiert `deleted` als 7TV-Ids; die Nutzungsseite entfernt die Zellen und
    reduziert `occupiedSlots`; die Vote-Detailseite entfernt die Zeilen über `sevenTvEmoteId`.
73. Lösch- und Restore-Bestätigung nennen den Setnamen und, bei nicht-aktivem Set, den Zusatz.
74. Live-Verifikation am Testkanal (Regel 16): Set mit einem nie aktiven Emote; löschen; Protokoll
    enthält die Zeile mit `emoteId: null`; Audit-Eintrag trägt Set-ID und 7TV-Id-Anzahl; Restore aus
    genau diesem Protokoll.

**K6 — Voting**

75. `CreateVoteSessionRequest` mit `emoteSetId` und leeren `sevenTvEmoteIds`, oder mit `emoteSetId`
    **und** `emoteIds`, oder mit `sevenTvEmoteIds` ohne `emoteSetId` ⇒ 400
    `vote_session_set_ballot_invalid` (drei `Api.Tests`).
76. Set-Session mit einer Id, die nicht Live-Mitglied ist ⇒ 400 `emote_ids_invalid`, keine Session,
    keine Zeile.
77. Set-Session über ein Mitglied ohne Zeile legt eine archivierte Zeile mit `ArchivedAt = null` an,
    liest nach und baut den Wahlzettel; `NameAtCreation`/`ImageUrlAtCreation` gefüllt; eine
    bestehende aktive Zeile wird übernommen und nicht verändert.
78. Wettlauftest mit **erzwungener Verschränkung** (zwei `AppDbContext`): der Worker hat gelesen, die
    Api fügt ein, der Worker speichert ⇒ der Worker wiederholt einmal, beide kommen durch, die Zeile
    trägt danach den Zustand des Syncs (`IsArchived = false`, Name des Syncs); ein zweiter Konflikt
    im Wiederholungslauf propagiert.
79. Abstimmen in einer Set-Session auf ein archiviertes Wahlzettel-Mitglied ist erlaubt; in einer
    Null-Session bleibt es gesperrt.
80. `GetResultsAsync`: `eligible = true` für jede Set-Session-Zeile; `useCount` für Manager aus den
    Set-Totals (Emote mit Zahlen unter Hauptset und Halloween zeigt in der Halloween-Session nur
    Halloween); `null` für Zeilen ohne `UsageStat` unter der Set-ID; Name aus `NameAtCreation`
    auch nach einem Sync, der `Emote.Name` überschreibt.
81. Detailseite: `canSelectForDelete` folgt `canManage`, nicht `hasUsageData` (Manager einer
    Set-Session mit lauter `null`-Zeilen sieht das Panel); das Panel erhält `session.emoteSetId`.
82. Badge und Vote-Sperre hängen an `eligible`; Set-Sessions zeigen kein Mid-Session-Badge.
83. E2E: Set-Session aus der Halloween-Ansicht anlegen (gemockt), Detailseite zeigt Wahlzettel mit
    eingefrorenen Namen, Löschen aus der Session sendet die Session-Set-ID.

**K7 — Deploy**

84. Nach dem Fenster: `GET /api/health` 200; eine Set-Ansicht eines nicht-aktiven Sets lädt; eine
    `UsageStats`-Zeile des Tages trägt die aktive `EmoteSetId`; das Worker-Log zeigt den Warmstart
    mit Set-ID.
85. Picker am Testkanal: Sets mit Namen; ein nicht-aktives Set gewählt ⇒ der Dialog zeigt dessen
    Belegung und Kapazität, nicht die des aktiven.
86. `dotnet ef migrations list` zeigt keine Pending; das Fenster hat weniger als 15 Minuten gedauert
    (gemessen von Schritt 1 bis Schritt 6) — sonst steht die Dauer im DECISIONS-Eintrag 1 als
    Befund.

**K1 — Migrationsprobe gegen eine wiederhergestellte Datenbank** (T1.10, nachgetragen am
2026-09-20; hinten angehängt, damit keine Nummer wandert)

87. `Up` läuft gegen die in eine **Wegwerfdatenbank** wiederhergestellte Sicherung durch, und die
    **Dauer ist gemessen und notiert** (Quelle, Zeilenzahl, Sekunden). Läuft die Probe gegen eine
    Prod-Kopie, ist diese Zahl die erwartete Länge des Wartungsfensters und steht im PR-Text; läuft
    sie nur gegen die Dev-Sicherung, steht ausdrücklich dabei, dass die Zahl **nicht** überträgt.
88. Jede der sechs Abbruchprüfungen feuert gegen dieselbe wiederhergestellte Datenbank, wenn man
    ihren Fall herstellt — je Prüfung ein konstruierter Verstoß, je Verstoß die erwartete
    Fehlermeldung, und danach ist die Datenbank unverändert (die Transaktion ist zurückgerollt).
89. `Down` verweigert an Schranke 1 (`ClosedBy = 'set-switch'`), **ohne etwas entfernt zu haben**
    (Spalte, Tabelle und neuer Index stehen danach noch); die Gegenprobe-Abfrage aus Sonde 6 läuft
    gegen dieselbe Datenbank und liefert die erwartete Form; die Wegwerfdatenbank ist danach
    gelöscht, der laufende Dev-Stack unberührt.

**K1 — Die Klassifikation liegt außerhalb des Repos** (nachgetragen am 2026-09-20; hinten
angehängt, damit keine Nummer wandert)

90. **Ohne** `SetSwitchAssignments.Local.cs` baut die Solution und alle drei Suiten sind grün —
    inklusive der Migrationstests gegen den ephemeren Container, die ihre Klassifikation als
    Fixture mitbringen. `git status` ist nach einem gefüllten V5 sauber (die Datei ist ignoriert),
    und `SetSwitchAssignments.Local.cs.example` ist committet.
91. Ist die Klassifikation **nicht einkompiliert** (Datei fehlt, Marke `Confirm()` nie gesetzt),
    bricht `Up` **vor** jeder Schemaänderung mit einer Meldung ab, die die fehlende Klassifikation,
    die Datei und die `.example` benennt — nicht mit dem Folgefehler „unclassified channel …“ aus
    Prüfung 3; danach ist die Datenbank unverändert. Eine **einkompilierte, aber leere**
    Klassifikation ist davon unberührt und legitim (Datenbank ohne Nutzungszeilen); dort greift
    Prüfung 3, sobald ein Kanal Zeilen trägt. Zwei Testfälle in
    `Integration/AddUsageStatEmoteSetIdMigrationTests.cs` halten beides fest.

---
## 15. Testpyramide

Nur **Verhalten**, keine Vorlage (Regel 12). Zahlen sind Zielwerte je Datei; „rot" nennt die
bestehenden Fälle, die durch die Vertragsänderung nicht mehr kompilieren oder nicht mehr gelten
und **mit umgestellt** werden — keiner davon wird gelöscht, um grün zu werden.

### 15.1 `tests/EmotePurge.Infrastructure.Tests`

| Ebene | Datei | Was | Anzahl | Bestand rot |
|---|---|---|---|---|
| Unit | `Unit/EmoteMatchCacheTests.cs` | Snapshot als ein Objekt, Set-ID und Generation, Tausch-Konsistenz (AK 13), leerer Snapshot | +4 | **7 von 7** (Signatur `ReplaceChannel`/`GetChannelEmotes`) |
| Unit | `Unit/EmoteSetIdValidationFilterTests.cs` (neu) | Format-Schranke, ordinal, leer, 33 Zeichen | +4 | — |
| Unit | `Unit/SevenTvApiClientEmoteSetListTests.cs` (neu) | v4 `emoteSets` parsen (Fixture aus der gemessenen v4-Antwort, Sonde 7), `kind == PERSONAL`, `ownerDisplayName` aus `owner.mainConnection.platformDisplayName`, `style.activeEmoteSetId` durchgereicht; **die drei Fehlerfälle getrennt** (AK 21): `userByConnection: null` bei HTTP 200 ⇒ `NoSevenTvAccount`, `userByConnection` ohne `emoteSets` ⇒ `Unavailable`, fehlende Antwort ⇒ `Unavailable` | +7 | — |
| Unit | `Unit/SevenTvApiClientEmoteSetPreviewTests.cs` | `capacity`/`name` am Set-Objekt, 0 → `null`; Duplikate bleiben zwei Einträge | +3 | **0** von 14 (additiv) |
| Unit | `Unit/SevenTvApiClientResolveIdentityTests.cs` / neu `EditorOfTests` | `editor_of { user { id } }` gemappt; fehlendes `id` ⇒ `null` | +2 | 0 |
| Unit | `Unit/ForeignEmoteSetServiceTests.cs` | Set-ID-Modus ohne Helix, `sevenTvUserId: null` | +2 | 0 von 14 |
| Unit | `Unit/SevenTvEmoteSetListServiceTests.cs` (neu) | Cache-Treffer, Miss, Redis-Ausfall fail-open, ein Permit je Request (AK 23/24); **Wächterkette aus 6.1**: *n* parallele kalte Misses ⇒ **ein** Upstream-Request (Singleflight); GraphQL-429 als HTTP 200 (`extensions.status: 429`) ⇒ `RateLimited`, nicht `Ok`; offener Breaker ⇒ 503 **ohne** Upstream-Request; negatives Ergebnis wird gehalten (zweiter Aufruf in der Frist ohne Upstream-Request), `NoSevenTvAccount` wie ein Treffer | +10 | — |
| Unit | `Unit/UsageStatMigrationChecksTests.cs` (neu, pur) | die **sechs** Prüfbedingungen als pure Funktionen über Listen (Prüfung 1–6, je ein Abbruch- und ein Durchlauffall = 12), dazu die vier Zuordnungsfälle `Date` → Set-ID inkl. Grenztag | +16 | — |
| Integration | `Integration/AddUsageStatEmoteSetIdMigrationTests.cs` (neu) | Migration gegen Container: Abbruch 1–6 (AK 5/6 — sechs Fälle), Backfill (AK 7), Index (AK 8), Saat (AK 9), **Saat vergibt nie `ClosedBy = 'set-switch'`** (AK 9, eigener Fall — die Invariante, an der `Down`-Schranke 1 hängt), `Down` (AK 10 — drei Fälle, darunter **Set-Wechsel ohne `(EmoteId, Date)`-Kollision ⇒ `Down` bricht trotzdem ab und hat nichts entfernt**), **nicht einkompilierte Klassifikation ⇒ benannter Abbruch vor jeder Schemaänderung, einkompiliert-aber-leer läuft** (AK 91, zwei Fälle); die Klassifikation kommt in jedem Fall aus einer **Fixture über den `internal` Testsitz**, nie aus der Datei des Betreibers (AK 90) | +14 | — |
| Integration | `Integration/UsageStatFlushServiceTests.cs` | zwei Set-IDs an einem Tag, dreispaltiges Addieren, zurückgestellter Batch mit anderer Set-ID (AK 11) | +3 | **14 von 14** (Signatur) |
| Integration | `Integration/UsageStatQueryServiceTests.cs` | Set-Filter in Context/Daily/Series/Totals, `null` = aktiv, `GetRowsAsync`-Summe, `/series` nach 7TV-Id (AK 19/20), `NameTwinEmoteSetIds`, nicht-aktive Grundmenge (archivierte mit Zahlen) | +10 | die Fälle für `GetChannelSeriesAsync` und `GetRowsAsync` (Teilmenge der 54; Zahl nicht verifiziert — beim Umstellen zählen) |
| Integration | `Integration/ChannelEmoteSetObservationServiceTests.cs` (neu) | Öffnen, Set-Wechsel in einer Transaktion (AK 16), fünf Schließstellen (AK 17), partieller Index (AK 18), Rejoin öffnet neu | +9 | — |
| Integration | `Integration/SevenTvSyncServiceTests.cs` | Set-ID reist in den Cache; Wechsel-Tests (AK 14); Wiederholung bei 23505 (AK 78) | +5 | **nicht verifiziert** — hängt an den Fakes für `IEmoteMatchCache`; prüfen in T1.2 |
| Integration | `Integration/ChannelServiceTests.cs`, `ChannelIdentityServiceTests.cs` | Leave/Rename/Merge schließen (Teil von AK 17) | +3 | 0 |
| Integration | `Integration/EmoteServiceTests.cs` | neue Form aktiv/nicht-aktiv, Altform, Log-Zeile, Audit-Details (AK 70) | +6 | **12 von 12** (Signatur der Überladung — oder 0, wenn die alte Signatur bleibt; T5.2 entscheidet und meldet) |
| Integration | `Integration/EmoteSetOwnershipServiceTests.cs` | `emoteSetId`-Parameter in drei Tiers (AK 33) | +3 | 0 von 5 |
| Integration | `Integration/AuditLogQueryServiceTests.cs` | `TargetEmoteSet` projiziert / `null` (AK 32) | +2 | 0 von 19 |
| Integration | `Integration/VoteSessionServiceTests.cs` | Set-Session Anlage (AK 76/77), Ausschlussregel, Votable in Set-Session (AK 79), Wettlauf (AK 78) | +6 | 0 von 25 (additive Felder) |
| Integration | `Integration/VoteSessionQueryServiceTests.cs` | `eligible`, Set-Totals, eingefrorene Namen (AK 80) | +4 | **nicht verifiziert** — ob Tests `VoteSessionResultDto` positional konstruieren |
| Integration | `Integration/HardenedForeignEmoteSetServiceTests.cs` | zweiter Schlüsselraum (AK 26) | +2 | 0 von 9 |
| Integration | `Integration/SevenTvEditorServiceTests.cs` (neu) oder `ModRoleCacheTests.cs` | Legacy-Payload ohne 7TV-ID wird nachgelöst (AK 31) | +2 | 0 |

### 15.2 `tests/EmotePurge.Worker.Tests`

| Datei | Was | Anzahl | Bestand rot |
|---|---|---|---|
| `EmoteUsageCounterTests.cs` | zusammengesetzter Schlüssel, Merge mit anderer Set-ID, `PendingEmoteCount` (AK 12) | +3 | **10 von 10** (Signatur) |
| `HarnessRunnerTests.cs`, `ReplayDayCounterTests.cs` | — (DTO unverändert, E15) | 0 | 0 von 74 |
| `WorkerServiceRegistrationTests.cs` | — | 0 | 0 |

### 15.3 `tests/EmotePurge.Api.Tests`

| Datei | Was | Anzahl | Bestand rot |
|---|---|---|---|
| `ChannelUsageSeriesWireFormatTests.cs` | `"sevenTvEmoteId"` **und** `"emoteId"` nebeneinander (6.5, Schritt 1) | +1 (erweitert) | **0 von 2** — der additive Weg macht das frühere „bewusst rot" gegenstandslos |
| `AuthFilterMatrixTests.cs` | Set-Listen-Route (AK 22), `set-warning?emoteSetId`, `sync-deleted`/`sync-restored` beide Formen und die drei 400-Fälle (AK 70), `/usage-stats/*?emoteSetId` 400 (AK 27), Vote-Ausschlussregel (AK 75) | +14 | 0 von 45 (`SyncRestored_Answers400_WhenTheBodyCarriesNoEmoteIds` bleibt gültig) |
| `SevenTvForeignEmoteSetEndpointTests.cs` | `?emoteSetId=` (400/200/Set-Modus), `/emote-sets` fremd (AK 47), `/me/emote-set-targets` (AK 25) | +9 | 0 von 8 |
| `SevenTvEmoteSetSyncImportedEndpointTests.cs` (neu) | die Leiter aus 6.7 (AK 30) | +9 | — |
| `EmoteRoutePolicyTests.cs` | neue Routen (AK 46) | +5 `InlineData` | 0 |
| `ApiFactory.cs` | Substitute für `ISevenTvEmoteSetListService`, `IEmoteSetOwnershipService`, `IUsageStatQueryService` (falls nicht schon über `IChannelService` abgedeckt — nicht verifiziert) | — | — |

### 15.4 Vitest (`web/`)

| Datei | Was | Anzahl | Bestand rot |
|---|---|---|---|
| `core/usage-stats/merge-set-view.spec.ts` (neu) | AK 53, Klassen, Duplikate, aktive Ansicht | +8 | — |
| `core/usage-stats/usage-stat.service.spec.ts` | Cache-Schlüssel mit Set (AK 64) | +2 | **5 von 5** (Signaturen) |
| `core/emotes/import-target-loader.spec.ts` | Set-Ziel, `truncated ⇒ failed`, Warnung je Klasse (AK 36) | +5 | **8 von 8** (Signatur) |
| `core/emotes/emote-admin.service.spec.ts` | neue Bodies, `targetEmoteSetId` immer gesendet (AK 44) | +4 | 4 von 9 (`syncDeleted`/`syncRestored`/`syncImported`-Bodies) |
| `core/seven-tv/seven-tv-emote-set.service.spec.ts` (neu) | drei Listen-Routen, `?emoteSetId=` | +4 | — |
| `core/seven-tv/seven-tv-run-engine.spec.ts` | `doneKeys` einzige Identität; Delete-Lauf ohne `emoteId` (AK 68) | +2 | Fälle, die `doneIds` lesen (Teilmenge von 22; nicht verifiziert) |
| `core/seven-tv/seven-tv-delete.service.spec.ts`, `seven-tv-restore.service.spec.ts` | Key `sevenTvEmoteId`, Set-ID im Datensatz, Retry (AK 71), Body neue Form | +6 | Teilmenge von 31 + 30 (Key- und Body-Assertions) |
| `core/seven-tv/seven-tv-import.service.spec.ts` | set-zentrierter Report ohne Resync (AK 41) | +2 | 0 von 25 |
| `shared/export/purge-run-export.spec.ts` | `emoteId: null`, altes Protokoll (AK 69) | +3 | Fälle mit `emoteId: string`-Pflicht (Teilmenge von 14) |
| `shared/export/usage-export.spec.ts`, `usage-export-purposes.spec.ts` | Set in Name/Meta, `null`-Zeile (AK 65) | +4 | Fixtures ohne Set (5 + 8, Signatur) |
| `shared/seven-tv/mass-delete-panel.spec.ts` | `deleted` als Keys, kein Protokoll-Filter, Setname in Dialogdaten (AK 72/73) | +4 | Fälle zu `doneIds`/Protokoll-Filter (Teilmenge von 29) |
| `shared/seven-tv/import-target-dialog.spec.ts`, neu `import-target-choices.spec.ts` (ersetzt `import-target-options.spec.ts`) | Klassen, Vorauswahl, eigener Kanal, Bestätigung ungetrackt (AK 34/35) | +10 | **28 + 4** (Datenquelle wechselt von `listMine` auf 6.2) |
| `shared/seven-tv/import-preview.spec.ts` | drei Gruppen, Zahlen des Anlasses (AK 37/38) | +5 | Fälle „Kollisionen bleiben in `toAdd`" (Teilmenge von 11) |
| `shared/seven-tv/import-confirm-dialog.spec.ts` | Setname im Kopf, Gruppen, Projektion ohne Kollisionen (AK 38/39) | +4 | Fälle mit `setId` im Kopf (Teilmenge von 32) |
| `shared/seven-tv/foreign-channel-step.spec.ts` | Quell-Radiogroup, kein zweiter Request (AK 48/49) | +3 | 0 von 9 |
| `shared/seven-tv/delete-confirm-dialog.spec.ts`, `restore-flow.spec.ts` | Setname, `emoteId` optional | +3 | Fixtures (Teilmenge von 14 + 16) |
| `shared/selection/list-selection.spec.ts` | zwei Guid-lose Zeilen (AK 54) — als Konsument-Spec über `sevenTvEmoteId`-Keys | +2 | 0 von 26 |
| `shared/datetime/date-range-menu.spec.ts` | Preset `'set-observed'` (AK 61) | +2 | 0 von 4 |
| `features/usage-stats/usage-stats-page.spec.ts` | Dropdown, Reload-Regeln (AK 51/52), `null`-Gruppe (AK 56), Badge (AK 57), Tatsachenangabe als **Matrix** (AK 60 — alle vier Fälle, darunter **Zahlen ohne Beobachtungsintervall: nur B−, keine Aussage über Zahlen**), Sperren (AK 62), Voting-Auflösung, `onDeleted` nach Key | +14 | Fälle, die `emoteId`-Keys oder `totalUseCount: number` voraussetzen (Teilmenge von 42) |
| `features/usage-stats/create-vote-session-dialog.spec.ts` | Set-Session-Body | +2 | Fälle zu `emoteIds` (Teilmenge von 14) |
| `features/voting/vote-session-detail-page.spec.ts` | `canSelectForDelete = canManage`, Panel-Set-ID, `eligible`, `onDeleted` nach 7TV-Id (AK 81/82) | +4 | 2 von 8 (`hasUsageData`-Gate) |
| `shared/audit/audit-row.spec.ts` | `targetEmoteSet`-Zusatz (8.10) | +3 | 0 |
| `core/i18n/api-error-locales.spec.ts` | — (erzwingt AK 45 ohne Änderung) | 0 | 0 |

### 15.5 Playwright E2E

| Datei | Was | Anzahl |
|---|---|---|
| `e2e/usage-atlas.e2e.spec.ts` | Set-Ansicht mit gemockter Live-Liste: Guid-lose Zelle markieren, Bestätigung nennt das Set; `null`-Gruppe; keine NG0955 in der Konsole (AK 55) | +3 (Mock-Helfer `mockUsageChannelSeries` auf `sevenTvEmoteId`; die 30 bestehenden Fälle laufen mit umgestelltem Mock) |
| `e2e/emote-import.e2e.spec.ts` | gleicher Kanal, anderes Set (AK 43); ungetracktes Ziel mit Bestätigung und set-zentriertem Report; Quell-Set-Picker | +3 |
| `e2e/vote-ballot.e2e.spec.ts` | Set-Session (AK 83) | +1 |

**Summe:** rund **+237** Fälle. Gegenrechnung zur Erstfassung: **+9** aus dem Codex-Review vom
2026-09-20 (je ein Fall für `Down`, `/series` und die Tatsachenangabe-Matrix ×2, fünf für die
Wächterkette des Listen-Dienstes), **+12** aus der Einarbeitung von Befund E und seiner Nachlese
(acht in `UsageStatMigrationChecksTests` durch zwei zusätzliche Prüfungen bei nun ausgeschriebener
Rechnung 6 × 2 + 4, zwei Abbruchfälle in den Migrationstests, ein eigener Fall für die
`'set-switch'`-Invariante der Saat, ein zweiter Fall in T1.9 für einen falschen
Kein-Wechsel-Eintrag). Die Erstfassung ließ bei `UsageStatMigrationChecksTests` offen, ob die +8
die vier Zuordnungsfälle enthielten; jetzt steht die Rechnung da. Bestand rot beim Umstellen:
**≥ 60** (die sicheren: 7 + 14 + 10 +
5 + 8 + 28 + 4 = 76 aus Signatur- und Datenquellenwechseln — der `/series`-Wire-Format-Test ist
**nicht** mehr darunter, weil der Übergang additiv ist; dazu Teilmengen, die T-Tasks beim
Umstellen zählen und im PR nennen).

**Coverage:** K1 und K2 legen viele **neue** Dateien an (Dienst, Filter, Migration-Prüfungen,
`mergeSetView`) — dort ist `coverage-local.mjs` nah an Sonar. Die chirurgischen Änderungen in
`usage-stats-page.ts` (1.904 Zeilen) und `SevenTvSyncService.cs` (543) sind der Fall „kleine
Änderung in großer Datei", wo die lokale Näherung am schwächsten ist; die neuen Zeilen dort sind
durch die genannten Fälle gedeckt. Sonar misst `new_coverage` zeilen- **und** zweiggenau — die
Migration mit ihren **sechs** `RAISE`-Zweigen braucht je Zweig einen Test (AK 5/6), nicht nur den
Erfolgspfad.

---

## 16. Aufwand

Je Bauschritt, nicht als Summe zuerst. „CC" ist Wandzeit der Subagenten.

| Bauschritt | Mensch | CC |
|---|---|---|
| K0 Sonden, Zuordnungsliste (V5), Purges (Betreiber) | ~2 h verteilt | — |
| K1 Zählpfad: Snapshot/Schlüssel (T1.1–T1.2) | ~4 h | ~30 min |
| K1 Migration mit sechs Prüfungen, Backfill, Saat, `Down` + Container-Tests (T1.3) | ~8 h | ~55 min |
| K1 Flush, Query-Filter, `/series`-Identität, Beobachtungs-Log (T1.4–T1.6) | ~8 h | ~50 min |
| K1 Wechsel-Tests, Live-Verifikation, DECISIONS 1 (T1.7, T1.8; T1.9 entfällt — Mechanismus in T1.3, Füllen als V5) | ~3 h | ~20 min |
| K1 Migrationsprobe gegen die wiederhergestellte Datenbank (T1.10) | ~2 h | ~25 min |
| K2 Listen-Dienst, Preview-Kapazität, Set-ID-Lesepfad, Routen, Filter, Codes (T2.1–T2.3) | ~9 h | ~55 min |
| K2 Grants-7TV-ID, `TargetEmoteSetId`, set-zentrierter Endpunkt, Projektion (T2.4) | ~5 h | ~30 min |
| K2 Picker, Loader, Preview-Gruppen, Bestätigung, Audit-Ansicht (T2.5–T2.6) | ~10 h | ~60 min |
| K2 Live-Verifikation, DECISIONS 2 (T2.7) | ~2 h | ~10 min |
| K3 Quell-Set-Picker (T3.1) | ~4 h | ~25 min |
| K4 `mergeSetView`, Dropdown, URL-Zustand, Reload-Regeln (T4.1–T4.2) | ~7 h | ~45 min |
| K4 Schlüsselwechsel + nicht-aktive Ansicht (T4.3–T4.4) | ~12 h | ~70 min |
| K4 Export, Türen, Scope, DECISIONS 4, E2E (T4.5–T4.6) | ~5 h | ~30 min |
| K5 `doneIds`-Abbau, Keys, Protokoll, Laufdatensatz, Panel-Output (T5.1) | ~5 h | ~30 min |
| K5 `sync-*`-Bodies, Papier-Fall, Altform + Log (T5.2) | ~4 h | ~25 min |
| K5 Bestätigungen, Duplikat-Regel, Live-Verifikation, DECISIONS-Nachtrag (T5.3) | ~3 h | ~20 min |
| K6 Set-Session Anlage, Votable, Fehlercode (T6.1) | ~5 h | ~30 min |
| K6 Worker-Wiederholung + Wettlauftest (T6.2) | ~4 h | ~25 min |
| K6 Ergebnisse, Detailseite, Dialog, E2E, DECISIONS 3 (T6.3) | ~6 h | ~35 min |
| Tests über alle Ebenen (+236, Bestand ≥ 60 umgestellt) — in den Zeilen oben enthalten | — | — |
| T7 Coverage, Codex-Review, PR | ~2 h | ~15 min |
| K7 Wartungsfenster inkl. Live-Verifikation Prod | ~1,5 h | — |
| **Summe** | **~111 h** | **~11,5 h** |

Größer als #147 (~29 h) und #148 (~33 h) zusammen, aus drei Gründen: die einzige nicht-additive
Migration an der heißesten Tabelle, ein Schlüsselwechsel quer durch das Raster und seine Rückwege,
und drei Verträge (Zählen, Ziel, Voting), die je einen DECISIONS-Eintrag brauchen.

---

## 17. Rollback

**Bis zum ersten beobachteten Set-Wechsel nach dem Deploy** ist die Migration umkehrbar: neuen
Worker **und** neue Api stoppen, `dotnet ef database update 20260907080507_AddUsageStatSharedChatUseCount
--connection '…'`, alte Images starten. `Down` entfernt Spalte, Tabelle und Voting-Spalten und
stellt `(EmoteId, Date) INCLUDE (UseCount)` her.

**Danach bricht `Down` ab, und zwar an zwei voneinander unabhängigen Schranken** (4.2):

1. **Log-Schranke** — eine `ChannelEmoteSetObservations`-Zeile mit `ClosedBy = 'set-switch'` belegt
   direkt, dass seit dem Deploy ein Kanal das Set gewechselt hat. Sie greift **auch dann**, wenn
   kein `(EmoteId, Date)` doppelt belegt ist: der Wechsel kann zwischen zwei Tagen liegen, die Sets
   können disjunkt sein, oder ein gemeinsames Emote wurde nur auf einer Seite benutzt. Ohne diese
   Schranke verwürfe `Down` in genau diesen Fällen `EmoteSetId` still.
2. **Index-Schranke** — trägt ein `(EmoteId, Date)` Zeilen unter zwei Set-IDs, scheitert das Anlegen
   des alten Unique-Index. Sie fängt Kollisionen, die das Log nicht kennt.

Der Rückweg ist dann ein Handgriff des Betreibers vor `Down` — Zeilen je `(EmoteId, Date)` summieren
und die Duplikate löschen (löst Schranke 2), die `'set-switch'`-Zeilen entfernen (löst Schranke 1) —,
der genau die Zuordnung verliert, die das Vorhaben eingeführt hat. Beide Schranken und beide
Handgriffe stehen im DECISIONS-Eintrag 1.

**Nachwirkungen, die ein Revert nicht beseitigt:** Audit-Zeilen mit `targetEmoteSetId`,
`targetIsActiveSetOfChannel`, `ChannelName = null` (set-zentrierter Endpunkt) und `emoteSetId`
in `syncDeleted`/`syncRestored`; nach einem Revert fällt `ProjectDetail` für sie auf den
`EmoteCount`-Zweig (`AuditLogQueryService.cs:141-144`) — Anzeigequalität, kein Datenverlust.
Purge-Protokolle mit `emoteId: null` sind nach einem Revert vom alten Parser (`:157`) nicht
wiederherstellbar — deshalb liegt K5 als letzter Code-Schritt vor K6 und der Betreiber wird im
Runbook auf diese Nachwirkung hingewiesen.

---

## 18. Dateireferenz

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Core/Entities/UsageStat.cs` | `EmoteSetId` |
| `src/EmotePurge.Core/Entities/ChannelEmoteSetObservation.cs` | **neu** |
| `src/EmotePurge.Core/Entities/VoteSession.cs`, `VoteSessionEmote.cs` | `EmoteSetId`; `NameAtCreation`, `ImageUrlAtCreation` |
| `src/EmotePurge.Core/Services/IEmoteMatchCache.cs` | `EmoteMatchSnapshot`, `ReplaceChannel(…, emoteSetId, …)`, `GetChannelSnapshot` |
| `src/EmotePurge.Core/Services/IUsageStatFlushService.cs` | `UsageCounterKey`, `FlushAsync` über den Schlüssel |
| `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` | `emoteSetId` an fünf Methoden, `EmoteSeriesEntryDto.SevenTvEmoteId`, `EmoteUsageContextDto` + `IsArchived`, `NameTwinEmoteSetIds` |
| `src/EmotePurge.Core/Services/IChannelEmoteSetObservationService.cs` | **neu** |
| `src/EmotePurge.Core/Services/ISevenTvEmoteSetListService.cs` | **neu** — `EmoteSetSummary`, `ListByTwitchIdAsync` |
| `src/EmotePurge.Core/Services/IEmoteService.cs` | Überladungen mit `(emoteSetId, sevenTvEmoteIds)`, `MarkImportedAsync` + `targetEmoteSetId`, `MarkImportedToSetAsync` |
| `src/EmotePurge.Core/Services/IEmoteSetOwnershipService.cs` | `emoteSetId?` |
| `src/EmotePurge.Core/Services/IForeignEmoteSetService.cs:123-129` | `SevenTvUserId` nullbar, `EmoteSetName`, `Capacity`; `GetEmoteSetByIdAsync` |
| `src/EmotePurge.Core/Services/ISevenTvEditorService.cs:12,35` | `SevenTvUserId` an `SevenTvEditorGrantEntry` |
| `src/EmotePurge.Core/Services/IVoteSessionService.cs:56-58`, `IVoteSessionQueryService.cs:21-22` | `EmoteSetId`, `SevenTvEmoteIds`; `Eligible` |
| `src/EmotePurge.Core/Services/IAuditLogQueryService.cs:17-34` | `TargetEmoteSet` |
| `src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs`, `SevenTvModels.cs:207,503` | `GetEmoteSetListForTwitchUserAsync`; `SevenTvEditorGrant.SevenTvUserId`; `SevenTvEmoteSetPreview.Capacity/Name` |
| `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs:38-50` | neuer Index, neue Entität, partieller Index |
| `src/EmotePurge.Infrastructure/Migrations/<stamp>_AddUsageStatEmoteSetId.cs` | **neu** (4.2) |
| `src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.cs` | **neu**, committet — Eintragsarten, `Load()` mit Abbruch, `static partial void AddLocalAssignments`, `internal` Testsitz (E1, 4.2) |
| `src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs` | **neu, gitignoriert** — die Werte; legt der Betreiber an (V5) |
| `src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs.example` | **neu**, committet — Platzhalter plus Anleitung, Muster `appsettings.Lan.json.example` |
| `.gitignore`, `.dockerignore` | je ein Eintrag für `SetSwitchAssignments.Local.cs`, neben den vorhandenen für `appsettings.Lan.json` (`.gitignore:45-46`, `.dockerignore:27-29`) |
| `src/EmotePurge.Infrastructure/Services/EmoteMatchCache.cs`, `UsageStatFlushService.cs:67-87`, `UsageStatQueryService.cs`, `SevenTvSyncService.cs:83-109,280-296,403-437,440-477`, `EmoteService.cs`, `EmoteSetOwnershipService.cs:21-60`, `VoteSessionService.cs:14-113,302-320`, `VoteSessionQueryService.cs:64-125,175-195`, `AuditLogQueryService.cs:129-193`, `ChannelService.cs:46-78,237-254`, `ChannelIdentityService.cs:325-350,445-455` | wie in 4–9 |
| `src/EmotePurge.Infrastructure/Services/ChannelEmoteSetObservationService.cs`, `SevenTvEmoteSetListService.cs` | **neu** |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:57-58,67-68,163-241,323-367,369-414` | `editor_of { user { id } }`, Preview mit `capacity`/`name`, v4-Set-Listen-Abfrage (E7) |
| `src/EmotePurge.Infrastructure/SevenTv/HardenedForeignEmoteSetService.cs:71-97`, `ForeignEmoteSetCache.cs` | Set-ID-Modus, zweiter Schlüsselraum |
| `src/EmotePurge.Infrastructure/SevenTv/ForeignEmoteSetRequestCoalescer.cs:30-38` | generisch geschlossen (`<TResult>`), damit der Listen-Dienst dieselbe Koaleszierung nutzt; Vorschaupfad verhaltensgleich (6.1, Wächter 2) |
| `src/EmotePurge.Infrastructure/Redis/ModRoleCache.cs:41` | additives Feld tolerant lesen |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs:88-121` | Registrierungen |
| `src/EmotePurge.Worker/IEmoteUsageCounter.cs`, `EmoteUsageCounter.cs`, `UsageFlushWorker.cs:68-82`, `TwitchChatManager.cs:1024-1055` | Schlüssel, Snapshot |
| `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs`, `UsageStatsEndpoints.cs`, `SevenTvEndpoints.cs`, `VoteSessionEndpoints.cs:345-347` | Routen aus Abschnitt 6 |
| `src/EmotePurge.Api/Validation/EmoteSetIdValidationFilter.cs` | **neu** |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | vier Codes (E13) |
| `web/src/app/core/usage-stats/{usage-stat.model,usage-stat.service}.ts`, `merge-set-view.ts` (**neu**) | 7.1, 7.3 |
| `web/src/app/core/seven-tv/{seven-tv-run-engine,seven-tv-delete.service,seven-tv-restore.service,seven-tv-import.service,foreign-emote-set.model,foreign-emote-set.service}.ts`, `seven-tv-emote-set.service.ts` (**neu**) | 7.2, 6.1–6.4 |
| `web/src/app/core/emotes/{emote-admin.service,import-target-loader,emote-set-status.model}.ts` | Bodies, Set-Ziel |
| `web/src/app/core/i18n/api-error.ts:10-46` | vier Codes |
| `web/src/app/shared/seven-tv/{import-target-dialog,import-target-choices (neu, ersetzt import-target-options),import-confirm-dialog,import-preview,mass-delete-panel,delete-confirm-dialog,restore-confirm-dialog,restore-flow,import-flow,file-import-step,import-trigger,foreign-channel-step}.ts` | 8.6–8.9 |
| `web/src/app/shared/export/{purge-run-export,usage-export,usage-export-purposes}.ts` | 7.2, 7.4 |
| `web/src/app/shared/datetime/date-range-menu.ts:19-30` | Preset |
| `web/src/app/shared/emotes/emote-set-menu.ts` (**neu**) | Dropdown nach dem Muster `date-range-menu` |
| `web/src/app/shared/audit/{audit-row,audit-actions}.ts`, `core/audit/audit.model.ts` | 8.10 |
| `web/src/app/features/usage-stats/{usage-stats-page.ts,usage-stats-page.html,create-vote-session-dialog.ts}` | 7, 8 |
| `web/src/app/features/voting/{vote-session-detail-page.ts,.html}` | 9 |
| `web/public/i18n/de.json`, `en.json` | alle Schlüssel aus 8, vier Fehlercodes |
| `web/e2e/support/mocks.ts:680-703` u. a. | Mocks für Set-Liste, Set-Vorschau, `/series` nach 7TV-Id, Set-Filter |
| `docs/DECISIONS.md` | vier Einträge (Abschnitt 23) |
| `docs/Feature-Ideen-2026-08-01.md` | Statuszeile der berührten Ideen (A16-Umfeld), falls dort geführt — Prüfaufgabe T4.6 |

**Nicht angefasst:** `Emote.cs`, der Unique-Index `(ChannelId, SevenTvEmoteId)`, `SevenTvEventClient.cs`,
`SevenTvSubscriptionRegistry.cs` (keine Subscriptions für nicht-aktive Sets), `HarnessRunner.cs`,
`ReplayDayCounter.cs`, `ForeignEmoteSetProviderBudget.cs`, `ForeignSevenTvBreakerPolicy`
(beide werden vom Listen-Dienst **benutzt**, nicht geändert — 6.1), `already-present-filter.ts` (ID-Vergleich bleibt), `list-selection.ts` (Klasse unverändert, nur die
`keyFn` der Seite), `atlas-grid.ts`, `slot-projection.ts`, `usage-bands.ts`, `RateLimitPolicyNames.cs`,
`RateLimitingOptions.cs`.

---
## 19. Wiederverwendet — als Prüfliste, nicht als Behauptung

Jede Zeile trägt einen **Beleg** (heute nachgeprüft) oder eine **Prüfaufgabe** vor dem
betreffenden Task. Eine frühere Runde hat Wiederverwendung behauptet, die nicht am Code stand — das
ist der Grund für die Spalte.

| Baustein | Anspruch | Status |
|---|---|---|
| Schreibseite set-agnostisch: `SevenTvRunEngine.start(setId, …)`, `REMOVE_OPERATION`, `ADD_OPERATION`, Import-Operation | Mutationen reisen mit `setId` + `sevenTvEmoteId`, keine Guid | **Belegt:** `seven-tv-run-engine.ts:239-244`; `seven-tv-delete.service.ts:42-48`; `seven-tv-restore.service.ts:35-41`; `seven-tv-import.service.ts:124-128` |
| `doneKeys` aus #70 | Rückmelde-Identität ohne Guid existiert schon | **Belegt:** Import-Queue `key: row.sevenTvEmoteId` (`seven-tv-import.service.ts:204-208`), `reportImported` liest `doneKeys` (`:297`). Delete/Restore sind die Nachzügler |
| `app-name-preview-list` | Namensgruppen im Bestätigungsdialog | **Belegt:** `import-confirm-dialog.ts:203-209`; Spec `shared/ui/name-preview-list.spec.ts` existiert |
| `ForeignEmoteSetCache`, `HardenedForeignEmoteSetService`, `ForeignEmoteSetRequestCoalescer`, `ForeignSevenTvBreakerPolicy`, `ForeignEmoteSetProviderBudget` | Set-ID-Lesepfad (T2.2) hinter demselben Dekorator | **Belegt mit Falle:** Cache nach Login geschlüsselt (`ForeignEmoteSetCache.cs:24,63`), Dekorator und Coalescer nach `normalized` (`:78`, `:95-96`) — zweiter Schlüsselraum nötig (E12, F6). Breaker/Budget sind schlüsselfrei und tragen unverändert (`:99-135`). **Prüfaufgabe T2.2:** der Coalescer koalesziert `set:{id}` und `{login}` getrennt |
| `ForeignEmoteSetRequestCoalescer`, `ForeignSevenTvBreakerPolicy`, `ForeignEmoteSetProviderBudget`, `SevenTvApiClient.FetchV4PageAsync` | **Listen**-Dienst (T2.1) hinter derselben Wächterkette wie der Vorschaupfad (6.1) | **Teils belegt, teils Auflage:** Breaker und Budget tragen unverändert und sind schlüsselfrei — dieselbe typisierte Singleton-Instanz wie die Vorschau (`ServiceCollectionExtensions.cs:106,107,113`), **nicht** die keyed Bestenlisten-Instanz (`:128`, die ein eigenes Budget hat). `FetchV4PageAsync` (`SevenTvApiClient.cs:552`) erkennt beide 429-Gestalten (`:566-574`, `:597-608`, `IsRateLimited :842-843`) — belegt. **Nicht wiederverwendbar wie er ist:** `ForeignEmoteSetRequestCoalescer` ist auf `ForeignEmoteSetLookupResult` festgelegt (`:31`, `:38`); Auflage an T2.1: generisch schließen, Muster `SevenTvLeaderboardStore<TValue>` (`:68`), Vorschaupfad bitgleich. **Nicht wiederverwendet:** `HardenedForeignEmoteSetService` selbst — es implementiert `IForeignEmoteSetService`, ein anderer Ergebnistyp; wiederverwendet werden seine Kollaboratoren und seine Reihenfolge (`:76-150`), nicht die Klasse |
| `date-range-menu` als Muster | Dropdown mit `Popover` + `role="radiogroup"` | **Belegt:** `date-range-menu.ts:104-133`; `DateRangePreset` (`:19`) ist erweiterbar |
| `archivedBadge` | Badge „nicht mehr im Set" | **Belegt:** `de.json:781`; DECISIONS 2026-08-01 (`:7028`) |
| `EmoteSetOwnershipService.CheckAsync` | parametrisierbar auf eine Set-ID | **Belegt:** liest die aktive ID an vier Stellen (`:30,37,50,57`), Tier 3 vergleicht die übergebene ID (`:105`) — E9 |
| `emote-usage-filter.ts` | kennt `totalUseCount: number \| null` | **Belegt:** `:9`, `:58-59` — der einzige vorbereitete Leser (F16) |
| `usage-bands.ts`, `usageFillPercent`, Sortierung | tragen `null` **nicht** | **Belegt** (F16); `null`-Zeilen werden vorher abgetrennt |
| `encodeCell` | serialisiert `null` als leere Zelle | **Belegt:** `csv.ts:35-38` |
| `UNAVAILABLE_WARNING` | „nicht geprüft"-Idiom für ungetrackte Ziele | **Belegt:** `import-target-loader.ts:27-34` |
| `rangeStartsBeforeTracking` | Idiom für „gezählt seit" mit Intervallbeginn | **Belegt:** `usage-stats-page.ts:383-398` |
| `ChannelLiveDay`-Muster | Beobachtungs-Tabelle ohne inverse Navigation, Cascade | **Belegt:** `AppDbContext.cs:52-65` |
| `ListSelection.retainAmong` | Set-Wechsel als Sicht | **Belegt:** `list-selection.ts:181 ff.`; die Klasse ist schlüsselneutral (`keyFn`) |
| `TryBuildAuditActor`, `db.AddAuditEntry`, `TargetType/TargetId` | Papier-Einträge | **Belegt:** `EmoteService.cs:46-51`, `AuditLogEntry.cs:66-73` |
| v4 `userByConnection … style { activeEmoteSetId } emoteSets { … kind owner }` | Set-Liste je Account, aktive Set-ID inklusive | **Nicht wiederverwendet:** seit E7 (2026-09-20) kommt die Liste aus v4, nicht aus v3 `users/twitch/{id}` + `SevenTvUserRestDto`; der v3-Weg ist gestrichen, die additive DTO-Klasse entfällt (F13). v4 ist als Lesepfad **für Listen** neu — die Set-Vorschau spricht v4 bereits |
| `GqlEditorOfQuery` + `SevenTvEditorService` | Angebotsliste und Besitzer-Prüfung | **Belegt mit Falle:** Grants tragen nur Twitch-IDs (`:355-360`); Cache-Payload (F10). **Prüfaufgabe erledigt (T0.6, 2026-09-20):** `user(id:) { editor_of { user { id connections { platform id username } } } }` liefert je Grant `user.id` (7TV-Account-ID, z. B. `01FY9A4ZG8000BH1HKPGP0R1S0`) **und** die Connections — das `id`-Feld am `user` existiert. Die redigierte Antwort ist die Fixture des Client-Tests in T2.4 |
| `import-target-options.ts` | Ziel-Kandidaten | **Nicht wiederverwendet** — ersetzt durch eine Funktion über 6.2 (Datenquelle wechselt); ihre 4 Specs wandern mit |
| `mass-delete-panel` in zwei Seiten | Panel unverändert einsetzbar | **Belegt mit Falle:** `deleted`-Identität wechselt (E18, F12) — beide Hosts anpassen |
| `PendingMigrationGuard` | Fail-fast, S3-34 | **Belegt:** `PendingMigrationGuard.cs:11`; `PendingMigrationGuardTests` bleibt |
| `HarnessRunner` / `GetRowsAsync` | Harness unverändert | **Belegt mit Falle:** eine Zeile je `(EmoteId, Date)` angenommen (F11) — E15 |

---

## 20. Nicht in dieser Runde

Aus Konzept 9, verbindlich: **keine EventAPI-Subscriptions für nicht-aktive Sets** (Registry hält ein
Ziel je Kanal, `SevenTvSubscriptionRegistry.cs:8,37`; `subscription_limit` 500,
`SevenTvEventClient.cs:259-262`); **keine `EmoteSet`-Entität, keine Zugehörigkeits-Tabelle**; **keine
Sicht „alle Sets zusammen"** (vom Betreiber verworfen — kein Dropdown-Eintrag „Gesamt (alle Sets)");
**keine automatische Teilung eines Backfill-Laufs**; **keine Schutzmechanik in der Set-Ansicht**
(keine Schwelle, kein Zurückhalten von Bändern, kein Bestätigungsschritt beim Löschen aus einem nie
beobachteten Set — nur die Tatsachenangabe); **kein Rückweg** der Set-Zahlen in einen Score.

Dazu:

- **Umbenennung auf 7TV** (Alias-Abweichungen auflösen, zweispaltige Tabelle Quellname/Zielname)
  — Ausblick unter **#201**, nicht hier (Konzept 7.5, D10).
- **Backfill-Parameter** (Set-ID als Pflichtparameter des Backfill-Aufrufs, Teilung an Set-Grenzen,
  Harness bekommt die Set-ID) — **Auflage an den #69-Plan**, Konzept 5.3; E15 hält den
  Harness-Vertrag bis dahin stabil.
- **Ein v3-Fallback für die Set-Liste oder die aktive Set-ID** — seit E7 (2026-09-20) liest der
  Listen-Dienst beides aus v4; ein Rückweg auf `user.emote_sets` oder `emote_set_id` wird nicht
  vorgebaut (F13, Abschnitt 22).
- **Anzeige eines Set-Wechsels im Audit-Log** — der Wechsel schreibt weiter keinen Eintrag
  (Konzept 2); das Beobachtungs-Log ist die Historie.
- **Der Zwischenweg** (Konzept 12.4) — kein Code, und seit dem 2026-09-20 auch keine Vorbedingung,
  die wir erfüllen können: der Betreiber kann ihn HandOfBloods Mod-Team **empfehlen**, nicht
  steuern (V1, Abschnitt 13).
- **Sets mit `kind != NORMAL` als Ziel** (`PERSONAL`, `GLOBAL`, `SPECIAL`) — alle drei gleich
  behandelt: sichtbar, deaktiviert, beschriftet, nie kommentarlos wählbar (8.6, AK 21); ein
  Freischalten ist keine Aufgabe.

---

## 21. Folge-Issues

1. **Altform `{ emoteIds }` von `sync-deleted`/`sync-restored` entfernen** (E3) — frühestens 14 Tage
   nach dem Deploy, nur bei null Altform-Log-Zeilen in diesem Fenster; entfernt `emoteIds` aus
   beiden Records, den Guid-Match in `EmoteService` und die Log-Zeile; `Api.Tests` für die
   Altform kippen auf 400 `emote_ids_empty`. Kein Frontend-Anteil (der Client sendet die Altform
   nie).
2. **#201** — Auswahl aus Protokoll/Emote-Liste laden: der Abgleich läuft über `sevenTvEmoteId`
   gegen das **gewählte** Set; Protokollzeilen können `emoteId: null` tragen (Konzept 12.1). Die
   zweispaltige Alias-Auflösung wird dort nachgehalten.
3. **#69** — Backfill mit Set-ID als Pflichtparameter (Konzept 5.3); das Beobachtungs-Log darf
   Teilungen nur innerhalb eines Intervalls vorschlagen.
4. **#74** — je nach Sonde 5: bei Zweig B ohne Alias-Treffer wandert die Frage „Duplikate per API
   löschbar?" als Befund dorthin.
5. **`emoteId` aus `/series` entfernen** (6.5, Schritt 2) — **nach** dem Merge von K4, in einem
   eigenen Commit, nicht in K4 selbst; entfernt das Feld aus `EmoteSeriesEntryDto`, aus
   `EmoteSeriesEntry` (`usage-stat.model.ts:51-55`) und aus `mockUsageChannelSeries`
   (`e2e/support/mocks.ts:688-703`) und streicht die `emoteId`-Assertion aus
   `ChannelUsageSeriesWireFormatTests`. Dasselbe Muster wie Folge-Issue 1 (E3), aus demselben Grund:
   ein Deploy kann keinen offenen Tab schließen, und ein Merge kein ausgeliefertes Frontend
   ersetzen.

Kein weiteres. Ein „Gesamt"-Eintrag und ein Set-Wechsel-Audit sind ausdrücklich keine.

---

## 22. Risiken

| Risiko | Umgang |
|---|---|
| **#76 nicht rechtzeitig fertig** (läuft auf eigenem Branch **vor** #200). Solange `TryGuardAgainstImplausibleWipeAsync` (`SevenTvSyncService.cs:351-370`) greift, wird `:83` nie erreicht: `ActiveEmoteSetId` bleibt alt, der Match-Cache behält die alte Generation, **Zählung und Beobachtungs-Log buchen unbefristet auf das alte Set**, obwohl 7TV längst gewechselt hat | Diese Spec setzt #76 **nicht** voraus; sie benennt die Folge: im Modell ist das korrekt („was wir für aktiv halten", 5.2), im Betrieb eine leise Fehlbuchung ohne Ende. Ohne #76 gilt vor dem Deploy: der Betreiber prüft in der Admin-Kanalliste, dass kein getrackter Kanal ein wirklich geleertes Set hat (Sync-Fehlergrund / „0 aktive Emotes"-Warnung im Worker-Log), und der DECISIONS-Eintrag 1 nennt die Blockade als offene Fehlerquelle. Mit #76 entfällt der Vorbehalt |
| Migration schreibt eine Fehlbuchung aus einer falschen Liste | Prüfungen 1–6 über einer **lückenlosen** Klassifikation (kein Kanal läuft stillschweigend durch), Sonde 6 als Tor, `ExpectedArchivedCount` gemessen, kein Default, kein Raten (AK 5–7); die Probe gegen eine wiederhergestellte Datenbank fährt jeden Abbruch einmal vor dem Ernstfall (T1.10, AK 87–89). **Seit dem 2026-09-20 fällt dabei eine Schutzschicht weg:** die Liste steht nicht mehr im PR-Diff, ein Zweiter liest sie nicht gegen, und der DECISIONS-Eintrag 1 wiederholt sie nicht (E1, 4.2). Was bleibt, sind die sechs Prüfungen an echten Daten und T1.10 — später und teurer als ein Review, aber wirksam gegen jeden Abschreibfehler, der sich mit der Datenbank schlägt. Unentdeckt bleibt weiterhin nur die falsche, in sich stimmige Bestätigung |
| `Down` nach dem ersten Set-Wechsel unmöglich | Abschnitt 17; Handgriff dokumentiert; K7 nennt die Grenze |
| Alter Worker gegen neues Schema (gestaffelter Deploy) | Wartungsfenster (F1, Abschnitt 10); `stop_grace_period` belegt |
| Api hängt an `ACCESS EXCLUSIVE` | Api steht im Fenster; `lock_timeout` 5 s (E11) |
| Guid-lose Zeile fällt auf `null`-Schlüssel, NG0955, Sprite-Neuaufbau | Schlüsselwechsel **im selben Commit** wie die Vereinigungsliste (T4.3/T4.4), AK 54/55 |
| Löschung ohne Protokollzeile (unumkehrbar) | E2, `emoteId` optional im Protokoll, AK 68/69/74 |
| Alter Tab postet `{ emoteIds }` nach dem Deploy → 400 nach der Mutation | Altform bleibt gültig (6.6), Log-Zeile, E3 |
| Alter Tab postet `sync-imported` ohne `targetEmoteSetId` | E5: dauerhaft nullbar |
| `sync-imported` an ungetracktes Ziel → 404, Papier weg | set-zentrierter Endpunkt (6.7, F7) |
| Falsche Papierspur für ein Set, das der Akteur nicht besitzt | Besitzer-Prüfung (E22), 403 ohne Eintrag; Legacy-Grants nachgelöst (F10) |
| Picker schreibt ins falsche Set (naiver Picker) | Loader mit Set-Ziel (F5), AK 36/43 |
| Kollisionsvorschau rechnet auf der falschen Liste — 192 rote Zeilen, ~190 Tickets | Zielliste aus dem gewählten Set, Kollisionen aus `toAdd` (8.6), AK 37/38/40 |
| `truncated` unterschätzt Belegung und Kollisionen | `truncated ⇒ failed` im Loader, Sperre in der Set-Ansicht (AK 36/62) |
| 7TV-Budget läuft durch Picker + Set-Ansicht leer | ein Permit je Request (F14, AK 24), 60-s-Caches (E12), Set-Liste nicht bei stillen Reloads (E19), Live-Liste nicht bei stillen Reloads (E16); Rechnung in E6 |
| v4 ist für uns ein **neuer** Lesepfad für Listen (die Set-Vorschau spricht v4 schon, die Listen bisher nicht) | Ein Request je Account inkl. `style.activeEmoteSetId`, Antwortform gemessen (Sonde 7); fehlt `emoteSets`, ist die Antwort `Unavailable` statt einer leeren Liste (AK 21), nicht ein stilles „keine Sets" — ein `userByConnection: null` dagegen ist `NoSevenTvAccount` (gemessen 2026-09-20, Befund B) |
| `kind` trägt vier Werte, wir haben nur zwei gesehen | `NORMAL` und `PERSONAL` stehen je in einer echten Antwort; **`GLOBAL` und `SPECIAL` werden wie persönliche Sets behandelt** — sichtbar, deaktiviert, beschriftet (8.6, AK 21). Die Regel ist positiv formuliert (**wählbar ist nur `kind == NORMAL`**), damit auch ein unbekannter fünfter Wert auf der sicheren Seite landet |
| Set-Wechsel im Dropdown löscht Markierungen | `retainAmong`, nicht `clear()` (8.1); Lösch-/Restore-Bestätigung nennt das Set (8.8) — DECISIONS 2026-09-19 |
| Worker verliert eine Sync-Runde am Voting-Upsert | Wiederholen einmal (E10), AK 78 |
| Voting-Session über nie aktives Set ohne Löschweg | `canSelectForDelete = canManage` (9), AK 81 |
| `VoteSessionResultDto`-Konstruktoren in Tests brechen | nicht verifiziert; T6.3 zählt und meldet |
| Namensvetter-Zahlen werden zusammengerechnet | E24: nur Merkmal, nie Summe (AK 59); die Produktfrage „dasselbe Emote?" bleibt offen (Konzept 6.2) |
| Coverage-Gate reißt an den sechs `RAISE`-Zweigen | je Zweig ein Test (AK 5/6); `coverage-local.mjs` vor dem PR |
| Sonar `new_coverage` in `usage-stats-page.ts` | kleine Änderung in großer Datei — lokale Schätzung unscharf; die neuen Zeilen sind über AK 51–62 gedeckt |
| Deploy vor dem Harness-Lauf | Kein Termin in dieser Spec; K7 nur nach Freigabe gegen das Runbook in `infra-docs` |

---

## 23. Die vier fälligen DECISIONS-Einträge (Regel 3)

Neue Einträge sind **englisch** (Sprachregel seit #152). Jeder liegt im Commit, der den Vertrag
ändert; die Zeile „Betrifft" nennt die Dateien aus Abschnitt 18.

| # | Titel (Arbeitstitel) | Inhalt | Commit |
|---|---|---|---|
| 1 | *Usage is counted per emote set; the observed set travels with the match cache* | `UsageStat.EmoteSetId` als **lokal beobachtetes** Set; Conflict-Target dreispaltig; Migration der Bestandszeilen nach einer **lückenlosen Klassifikation jedes Kanals mit Nutzungszeilen** — Wechseleinträge und ausdrückliche Kein-Wechsel-Einträge mit mitgeschriebener `ConfirmedEmoteSetId`; **die Werte selbst stehen nicht im Eintrag** — statt ihrer das Verfahren: die Klassifikation lebt in der gitignorierten `Migrations/SetSwitchAssignments.Local.cs` neben einer committeten `.example`, sie ist Teil des **Builds** (eine Korrektur heißt Rebuild, nie Raten), CI/Dependabot/Fremd-Contributor bauen und testen **ohne** sie, ihr Fehlen bricht `Up` mit benannter Meldung ab (Fail-fast wie S3-34), sie gerät über `.dockerignore` und den sauberen CI-Checkout nicht ins Image, und die Migrationstests bringen ihre eigene Fixture mit — **samt Grund für den Ortswechsel**: die lückenlose Klassifikation nennt jeden Kanal mit Nutzungszeilen und ist damit die Nutzerliste des Dienstes, das Repo ist öffentlich, und die Kennung lässt sich nicht weglassen, weil die Set-IDs der Inhalt der Einträge sind und 7TV zu jeder Set-ID den Besitzer nennt — **und samt Preis**: die Liste ist nicht mehr im PR-Diff prüfbar und wird hier nicht wiederholt, an ihre Stelle treten die sechs Abbruchprüfungen gegen die Datenbank und die Migrationsprobe T1.10 (E1, 4.2); warum die frühere Vorgabe „ohne Eintrag heißt nie gewechselt" fiel, dass ein Kein-Wechsel-Eintrag Verantwortung und nicht Gewissheit trägt, Gegenprobe 11.6 und **sechs** Abbruchgründe samt dem Rollentausch der Massenarchivierungs-Schwelle (entdeckt nicht mehr, widerspricht), Ein-Tages-Unschärfe, Verbleib von Testkanal und Wegwerfkanal (gepurgt); `ChannelEmoteSetObservation` als Intervalle mit Öffnungs-/Schließregeln, partiellem Index, begrenztem Zweck und Saat; Wartungsfenster mit Worker- **und** Api-Stopp, Verluste, Rückrollgrenze; dass das #69-Design den breiteren Key abgelehnt hatte (`docs/designs/Chat-Log-Backfill-69-2026-09-05.md:306-308`) und warum er hier trotzdem kommt; dass die zwei früheren Zähler-Migrationen additiv waren, diese nicht; `GetRowsAsync` summiert (E15); #76-Blockade als offene Fehlerquelle | K1, Commit von T1.3 + T1.4 (Migration und Flush zusammen: `feat(usage): count chat usage per emote set`) |
| 2 | *An import may target any set of an account the user edits; the confirm dialog keeps collisions out of the run* | revidiert „das Zielset bleibt ein getrackter Kanal aus `listMine()`" (DECISIONS 2026-09-09, `:2045-2048`) in der abgeschwächten Form: getrackt als Vorgabe, ungetrackt nach Bestätigung mit Besitzer-Login und Setname, Editor nachgewiesen über `editor_of`; **R2 (2026-09-06, `:4255`) unberührt** — Token erst vor dem Lauf; revidiert die informative Vorschau (2026-09-06, #72): Namenskollisionen draußen und als Gruppe, Alias-Abweichungen übersprungen und als Gruppe, keine Umbenennung (→ #201); Audit-Vertrag beider Wege (`TargetType`/`TargetId`, `targetIsActiveSetOfChannel`, set-zentrierter Endpunkt mit Besitzer-Prüfung); korrigiert die Annahme, `sync-imported` laufe gegen den Seitenkanal; E5 (dauerhaft nullbar); E6/E7 (drei Routen, ein Dienst, **v4** — ein Request je Account, aktive Set-ID und Besitzer-Anzeigename inklusive); `ownerDisplayName` ist ein Anzeigename, die Papierspur führt daneben den Twitch-Login (6.7) | K2, Commit von T2.5 |
| 3 | *Voting: "member of the session's set" replaces "not archived"; permission comes from permission* | revidiert den Archiviert-Badge-Absatz vom 2026-08-01 (`:7028`); Falle Anlegen **und** Abstimmen; Invariante Set-Session ⇒ fester Wahlzettel; kein Mid-Session-Badge für Set-Sessions; eingefrorene Anzeigedaten; datenbankseitiges Upsert; warum `ChannelSyncGate` es nicht sein konnte; Worker wiederholt einmal bei 23505 (E10); `canSelectForDelete` ↔ `hasUsageData` entkoppelt; `eligible`; beide Felder im Request (E4) | K6, Commit von T6.3 |
| 4 | *A row of the set view is identified by its 7TV id; bookkeeping speaks 7TV ids* | Schlüssel des Rasters und des inneren `track` ist `SevenTvEmoteId`, `Emote.Id` nullbare Nutzlast ohne Aussagewert; Queue-Key Delete/Restore `sevenTvEmoteId` (Nachtrag zu R3 vom 2026-09-05 — der Import-Lauf war das Muster; `doneIds` entfällt, E2); Protokollzeile `emoteId` optional, Parser akzeptiert `null`; **`/series` benennt nach `SevenTvEmoteId`** und nimmt `emoteSetId` (revidiert den Wire-Format-Satz des Eintrags zu `/usage-stats/series`, `:5916-5922`) — **additiv**: `emoteId` bleibt bis zum Folge-Issue 5 daneben stehen, gleiches Muster wie die Altform in E3, damit der Integrationsbranch zwischen K1 und K4 benutzbar bleibt; beide Caches mit Set; Export mit Set-ID/Setname und `null`-Serialisierung; Set-Ansicht legt keine Zeilen an, Voting schon (Fremdschlüssel); Set-Wechsel ist Sicht (`retainAmong`), Lösch-/Restore-Bestätigung nennt das Set; die zwei korrigierten Konzeptsätze (6.2, 7.1) | **Zwei Commits, ein Eintrag:** geschrieben in K4 (Commit von T4.3, `/series`, Schlüssel, Export, Caches); **Nachtrag** im selben Eintrag in K5 (Commit von T5.2): Body `{ emoteSetId, sevenTvEmoteIds }`, Match über `(ChannelId, SevenTvEmoteId)`, Papier-Variante als Nachtrag zum Soft-Archive-Eintrag vom 2026-07-26 (`:7379`), Altform übergangsweise gültig (E3), Set-ID im Laufdatensatz |

Ein fünfter Eintrag ist nicht nötig: der Quell-Set-Picker (K3) ändert keinen Vertrag, er
parametrisiert einen Lesepfad.


---

## 24. Nachtrag: Codex-Review vom 2026-09-20

Adversariale Zweitmeinung (`/codex:adversarial-review --model gpt-5.6-sol`) über diese Spec und
[Plan-200](../../plans/Plan-200-Emote-Sets.md). Fünf Befunde, **alle fünf eingearbeitet** — Befund
E am 2026-09-20, nachdem der Betreiber entschieden hatte. Die Einarbeitung steht oben im Text, hier
steht nur, was der Befund war und was daraus wurde; darunter zwei Nachzügler, die beim Einarbeiten
am Code aufgefallen sind und **nicht** aus dem Review stammen.

| # | Befund | Ergebnis |
|---|---|---|
| **A** | `Down` blockiert nicht zuverlässig [high]: die Kollision des alten Unique-Index ist ein **Stellvertreter** für „es hat einen Set-Wechsel gegeben" und kein Synonym — bei disjunkten Sets, einem Wechsel zwischen zwei Tagen oder einem nur einseitig benutzten Emote entsteht kein doppeltes `(EmoteId, Date)`, der alte Index lässt sich anlegen und `Down` verwirft `EmoteSetId` **still**. | **Eingearbeitet.** `Down` prüft jetzt **vor jedem zerstörenden Schritt** ausdrücklich auf eine `ChannelEmoteSetObservations`-Zeile mit `ClosedBy = 'set-switch'` und bricht mit `RAISE EXCEPTION` ab (4.2); die Index-Kollision bleibt als **zweite, unabhängige** Schranke daneben. Die Saat vergibt `'set-switch'` nie (4.3), womit der Test auf `'set-switch'` genau „nach dem Deploy" bedeutet. AK 10 hat einen dritten Fall, die Testpyramide einen zusätzlichen Migrationstest. |
| **B** | Der v4-Listenpfad umgeht die vorhandene Härtung [medium]: Cache und Permit allein liefern weder Singleflight noch Breaker; gleichzeitige kalte Misses treffen alle v4, Fehler werden bei jedem Aufruf wiederholt, und ein v4-Ausfall kann das **gemeinsame** 60/min-Budget leeren und andere 7TV-Lesepfade in 503 drücken. Die Kostenaussage „ab dem zweiten trifft jeder den Cache" war damit falsch. | **Eingearbeitet.** 6.1 legt den Listen-Dienst hinter **dieselbe** Wächterkette und Reihenfolge wie `HardenedForeignEmoteSetService`; Breaker und Budget sind dieselben typisierten Singletons wie der Vorschaupfad, `FetchV4PageAsync` erkennt beide 429-Gestalten. Der Coalescer ist heute typgebunden und wird generisch geschlossen (Muster `SevenTvLeaderboardStore<TValue>`) — das ist als **Auflage** benannt, nicht als vorhandene Wiederverwendung (19). Negative Ergebnisse werden kurz gehalten; die Rechnung in E6 hängt jetzt ausdrücklich an der Koaleszierung; degradierte Sicht in 8.1; AK 23 und die Testpyramide erweitert. |
| **C** | Integrationsbranch zwischen K1 und K4 wire-inkompatibel [medium]: K1 ersetzte `/series.emotes[].emoteId` durch `sevenTvEmoteId`, das Frontend folgt erst in K4 — dazwischen läse das ausgelieferte Frontend `undefined`. | **Eingearbeitet, additiver Weg.** `/series` liefert ab K1 **beide** Felder; `emoteId` fällt erst **nach** K4 in einem eigenen Schritt (Folge-Issue 5) — dasselbe Muster wie die Altform `{ emoteIds }` in E3. Der Grund steht mit dabei: der Branch wird nie deployt, aber die Live-Verifikationen (T2.7, T5.3) laufen aus ihm. F4, 6.5, AK 20, der Wire-Format-Test und E25 sind nachgezogen; **kein** Bestandstest wird mehr bewusst rot. |
| **D** | Fehlende Beobachtung wurde als fehlende Zählung ausgegeben [medium]: 8.4 leitete aus einem fehlenden Intervall „für dieses Set liegen keine Zählungen vor" ab — ein Selbstwiderspruch zu 4.3, wo der Zweck des Beobachtungslogs die Zuordnung der Zahlen ausdrücklich ausnimmt, und faktisch falsch für inaktive Kanäle, deren `UsageStats` die Migration backfillt, ohne eine Beobachtungszeile anzulegen. | **Eingearbeitet.** 8.4 ist als **Matrix** (beobachtet ja/nein × Zahlen ja/nein) neu geschrieben, mit drei getrennten Sätzen und je einem Satz pro zutreffender Aussage. „Keine Zählungen" kommt ausschließlich aus den geladenen Totals. AK 60 prüft alle vier Felder, darunter ausdrücklich „zuvor inaktiver Kanal, backfillt, später rejoined". |
| **E** | Die vier Abbruchprüfungen der Migration [high]: ungelistete Kanäle galten implizit als „nie gewechselt", und Prüfung 3 erkannte einen Wechsel nur ab 10 Archivierungen oder 25 %. Ein Wechsel zwischen stark überlappenden oder identischen Sets bleibt darunter oder erzeugt gar keine Archivierung — dann lief die Migration durch und schrieb sämtliche historischen `UsageStats` dieses Kanals auf das heute aktive Set. | **Eingearbeitet — Entscheidung des Betreibers vom 2026-09-20: vollständige Klassifikation.** `SetSwitchAssignments` ist keine Liste der Wechsler mehr, sondern eine **lückenlose Klassifikation jedes Kanals mit mindestens einer `UsageStat`-Zeile**, mit einer zweiten Eintragsart `(ChannelId, TwitchChannelId, ConfirmedEmoteSetId)` für „nie gewechselt" — die Set-ID wird mitgeschrieben statt abgeleitet, sonst prüfte die Migration gegen sich selbst. Aus vier Prüfungen werden **sechs** (4.2, mit Umrechnungstabelle): **Lückenlosigkeit** ersetzt die alte Prüfung 3 und bricht mit Nennung des Kanals ab; die alte Schwelle lebt als **Widerspruch** weiter, mit umgekehrter Rolle — sie entdeckt keinen Wechsel mehr, sie widerspricht einer Bestätigung, und ein Fehlalarm ist dort billig; dazu der **Kettenschluss** über mehrere Wechseleinträge; **Endzustand** gilt jetzt für beide Eintragsarten. Die Grenze ist ausdrücklich benannt: das verschiebt **Verantwortung**, nicht **Gewissheit** — gewonnen ist, dass kein Kanal mehr stillschweigend durchläuft. E1, 4.3 (Saat), Sonde 6 (Zählabfrage für die Kanalliste), AK 2/5/6/7, Testpyramide und Abschnitt 13 sind nachgezogen. |

**Drei Nachzügler aus der Einarbeitung — Funde, nicht Review-Befunde.** Alle drei sind beim
Nachprüfen am Code bzw. beim Gegenlesen der eingearbeiteten Entscheidung aufgefallen, nachdem
Codex seine fünf Befunde abgegeben hatte; sie stehen hier, damit später niemand sie dem Review
zurechnet:

| # | Fund | Ergebnis |
|---|---|---|
| **N1** | Die erste `Down`-Schranke hängt an der Invariante „die Saat vergibt `ClosedBy` nie `'set-switch'`". Die stand als **Auflage** in 4.2/4.3 und in T1.3b, hatte aber keinen eigenen Testfall — eine Auflage, die niemand prüft, ist eine Hoffnung. | AK 9 bekommt einen eigenen Fall: **nach `Up` existiert keine `ChannelEmoteSetObservation`-Zeile mit `ClosedBy = 'set-switch'`**. Migrationstests **+12 → +13**. |
| **N2** | `SevenTvApiClient` liest jeden GraphQL-Fehler **ohne** `extensions.status: 429` als `Unavailable` (`:842-843`, `:446-451`, `:513`) — eine **falsch geformte Abfrage** ist damit von „7TV ist weg" nicht zu unterscheiden. Genau so war die Bestenliste einmal permanent „unavailable" (`:75-82`). | Als **F17** in Abschnitt 3 aufgenommen (hinten angehängt, keine Nummer verschoben), mit der Folge für den neuen v4-Listenpfad: die **Live-Sonde ist das Einzige**, was die beiden Fälle trennt — die Abfrage aus Sonde 7 ist am 2026-09-20 live gemessen, die Fixture für T2.1 stammt aus einer echten Antwort und ist kein Papierentwurf. |
| **N3** | **Die Klassifikation ist die Nutzerliste des Dienstes.** Kein Review-Befund und keine Folge von Befund E als solchem, sondern eine Folge der **lückenlosen** Klassifikation, die erst beim Gegenlesen von E1 auffiel: solange die Liste nur die Wechsler nannte, waren das ein bis zwei Kanäle; seit sie **jeden Kanal mit Nutzungszeilen** nennt, ist sie die vollständige Liste der Kanäle, die der Dienst trackt. E1 legte sie als Konstanten „sichtbar im PR-Diff“ ab und ließ sie im DECISIONS-Eintrag 1 wiederholen — in einem öffentlichen Repo (AGPL-3.0). Die Kennung wegzulassen geht nicht: die Set-IDs **sind** der Inhalt, und 7TV nennt zu jeder Set-ID den Besitzer. | **Entscheidung des Betreibers vom 2026-09-20: die Liste kommt nicht ins Repo.** Sie folgt dem Muster von `appsettings.Lan.json` — gitignorierte Datei, committete `.example`, `.dockerignore`-Eintrag —, bleibt als kompilierte Konstanten aber **Teil des Builds** (kein Korrigieren ohne Rebuild), bricht bei Fehlen mit benannter Meldung ab (Fail-fast wie S3-34), lässt CI, Dependabot und einen fremden Contributor ohne sie bauen und testen und die Migrationstests ihre eigene Fixture mitbringen. **E1** neu gefasst, **4.2** um Ort, Abbruch, Image-Weg und den ausgeschriebenen Tausch ergänzt, **Sonde 6** (Ausgabe bleibt lokal, in den PR-Text geht nur die Zahl), **AK 2** umgeschrieben, **AK 90/91** hinten angehängt, **Abschnitt 10** um die Vorbedingung, **13** um **V5** (T1.9 entfällt als Commit), **15.1** +13 → +14, **18**, **22** und **23** nachgezogen. Der Preis steht ungeschönt in 4.2 und 22: **Prüfbarkeit durch einen Zweiten gegen Nichtveröffentlichung der Nutzerliste.** |
