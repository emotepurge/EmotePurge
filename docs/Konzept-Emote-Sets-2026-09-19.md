# Konzept: Emote-Sets statt „der Kanal ist sein aktives Set" (2026-09-19)

Auftrag: Entwurf zu Issue #200 — nicht-aktive 7TV-Sets eines Kanals ansehen, darin löschen, zwischen
eigenen Sets kopieren, beim Import Quell- und Ziel-Set wählen, und die Chat-Nutzung je Set zählen.
Kein Plan, kein Code: Verträge, Datenmodell, Grenzfälle, verworfene Alternativen, offene Messpunkte,
Reihenfolge. Methodik: Read der Sync-, Zähl-, Import-, Audit- und Voting-Pfade in `Infrastructure`,
`Worker`, `Api` und `web/`, Abgleich mit den Issues #69/#74/#76/#200/#201 und den DECISIONS-Einträgen
vom 2026-08-01, 2026-08-03, 2026-08-08, 2026-09-01 und 2026-09-09. Die Entscheidungen A–M stammen aus
der Brainstorming-Sitzung mit dem Betreiber vom 2026-09-19; jede Behauptung daraus wurde gegen den Code
geprüft, Abweichungen stehen an Ort und Stelle. Kennzeichnung wie in den Konzepten vom 2026-09-08 und
2026-09-18: **belegt** (Datei:Zeile) / **plausibel** (eigene Ableitung) / **offen** (nicht gemessen
oder nicht entschieden).

**Zweite Fassung.** Die erste Fassung vom selben Tag ist durch einen adversarialen Codex-Review
(`gpt-5.6-sol`, Verdict „needs-attention", acht Befunde) und vier anschließende Entscheidungen des
Betreibers gegangen. Was sich dadurch geändert hat, steht gesammelt im Abschnitt „Überarbeitung nach
adversarialem Review" direkt unter dem Fazit und an den betroffenen Stellen; das Dokument trägt
seine Entwicklung sichtbar, statt so zu tun, als wäre es gleich so gewesen.

**Dritte Fassung (2026-09-19, abends).** Zwei Dinge sind dazugekommen. Erstens sind die Sonden 1
und 2 aus Abschnitt 11 **live gegen 7tv.io gelaufen** — aus der Session heraus, der Klassifikator hat
diesmal nicht geblockt; die Abfrage der zweiten Fassung war syntaktisch falsch, `editableEmoteSetIds`
ist nur mit 7TV-Anmeldung lesbar, die Set-Liste eines Users dagegen ohne, und die Schnittmenge
zwischen Haupt- und Saison-Set ist an einem Kanal mit genau dem Muster des Anlasses gemessen
(11.1, 11.2, Folgen in 3, 6.1, 7.2, 7.3, 7.4). Zweitens gibt es jetzt eine **Vorabscheibe** (12.4):
HandOfBlood wechselt am 2026-10-01 auf sein Halloween-Set, und das Mod-Team will das Set vorher
befüllen — ein bewusst kleiner Teil des Vorhabens geht deshalb vor diesem Datum auf Produktion,
der Rest bleibt hinter dem 2026-10-08. *(In der fünften Fassung wieder entfallen — s. dort.)*

**Vierte Fassung (2026-09-19, spät).** Ein einzelner Messbefund, nachgereicht vom Betreiber:
HandOfBloods Halloween-Set enthält viele Emotes, die **denselben Namen** tragen wie ein
Hauptset-Emote, aber eine **andere 7TV-ID** — ein Halloween-`Stare` ist für den Zuschauer dasselbe
`Stare`, nur in der Saisonfassung. Die ersten drei Fassungen haben Sets nur nach IDs verglichen;
nach Namen sieht der Anlass anders aus (11.2 „Namen gegen IDs"). Fürs Zählen **bestätigt** der
Befund 5.2, für die Begründung von A in Abschnitt 3 präzisiert er die tragende Zahl, für die Anzeige
bringt er einen Fall mit, den 6.2 nicht kannte, und für den Anlass (12.4) ist er die operativ
wichtigste Korrektur: kollisionsfrei übertragbar sind **242** Hauptset-Emotes, nicht 422, und eine
Kollisionsvorschau gegen das Halloween-Set ist damit Pflicht, nicht Komfort — in der vierten Fassung
das Kernargument für die Vorabscheibe, in der fünften das Kernargument dafür, dass der Zwischenweg
trägt, weil er sie von selbst liefert. Beim Einarbeiten ist zusätzlich aufgefallen, dass 4.3 den
Set-Wechsel vom 2026-10-01 nicht mitgedacht hatte (Nachtrag dort).

**Fünfte Fassung (2026-09-19, Nacht).** Die Vorabscheibe **entfällt**. Der Betreiber hat einen Weg
gefunden, der das Bedürfnis des Mod-Teams — das Halloween-Set vor dem 2026-10-01 befüllen — ohne
jeden Deploy deckt: das Halloween-Set wird in einem **anderen getrackten Kanal aktiv** gesetzt, und
„Übertragen" von HandOfBloods Nutzungsseite zielt auf diesen Kanal (12.4, jetzt eine
Handlungsanweisung statt eines Bauauftrags). Damit gilt 12.2 wieder ohne Ausnahme, 13 ist wieder
**eine** Reihenfolge mit **einem** Deploy, und der Ziel-Set-Picker steht dort, wo er vor der dritten
Fassung stand — in der Hauptrunde, mit einer anderen Begründung als Termindruck (7.3). Die
technischen Befunde der Vorabscheibe (Zielvorschau aus dem gewählten Set, ID-Vergleich der
Vorprüfung, fehlende Kapazität in der Preview-Abfrage) hingen nie an ihr und stehen jetzt in 7.5.
Was sich für die Migration ändert, steht in 4.3: HandOfBlood wechselt trotzdem, und der Wechseltag
wird aus der Datenbank gelesen, nicht aus dem Kalender. *(Die Ableitung aus der Datenbank ist in
der siebten Fassung zurückgenommen — sie ist nicht eindeutig; 4.3 nimmt eine bestätigte Liste.)*

**Sechste Fassung (2026-09-19, spät nachts).** Ein zweiter Codex-Review (`gpt-5.6-sol`, adversarial)
ist **abgebrochen**: das Kontingent war erschöpft, bevor die Befundliste stand. Geliefert hat er
genau **einen** Satz, und der trifft: die Set-Ansicht eines nicht-aktiven Sets enthält Einträge ohne
lokale `Emote.Id`, während Auswahl, Löschen, Wiederherstellen und Buchhaltung diese Id verlangen —
das Dokument behauptete, diese Pfade bräuchten „nichts Neues", und der Code kann den Vertrag so nicht
ausführen. Nachgemessen in 6.5: der Befund ist richtig und **größer**, als er formuliert war (das
Purge-Protokoll, der optimistische Listen-Update, der Inspector und der Voting-Draht brechen aus
demselben Grund). Die Antwort ist kein „wir passen das an", sondern ein **Identitätsvertrag** für die
Zeile der Set-Ansicht: ihr Schlüssel ist die `SevenTvEmoteId`, die lokale Id wird Nutzlast, und der
Lauf samt seinen Rückwegen spricht 7TV-Ids (6.5, Folgen in 6.2, 7.1, 8, 10, 12.1, 13). Zwei Sätze
des Dokuments waren falsch und sind **korrigiert, nicht überschrieben** — mit Vermerk an Ort und
Stelle. Der Rest des Reviews stand aus und ist in der siebten Fassung nachgeholt.

**Siebte Fassung (2026-09-19, nachts, zweiter vollständiger Codex-Review).** Der wiederholte
adversariale Review (`gpt-5.6-sol`, Verdict „needs-attention", zehn Befunde, acht davon `[high]`) hat
drei Klassen von Fehlern gefunden: das Papier **leitete** Migrationseingaben aus einer Signatur ab,
die der Code nicht eindeutig macht (4.3, 4.4, 11.6); es **behauptete** Verträge als gelöst, die der
Code so nicht ausführt (Voting-Wettlauf in 8, Serien-Identität und Caches in 6.5, Rückmelde-Body
über einen Deploy hinweg in 6.5/7.1, Export in 6.1, Runbook in 12.3, Rückbau des Zwischenwegs in
12.4); und es **delegierte** drei Produktfragen an den Plan (Token-Zeitpunkt, Namenskollisionen im
Lauf, Alias-Abweichung). Die drei Produktfragen hat der Betreiber entschieden (7.3, 7.5); die
übrigen Befunde sind je am Code nachgeprüft und **ersetzen** die früheren Aussagen, statt daneben
zu stehen. Eine Behauptung des Reviews ist am Code enger als formuliert (Alias und Chat-Zählung,
5.2). Die Tabelle steht im Überarbeitungs-Abschnitt (siebte Runde).

**Achte Fassung (2026-09-20).** Eine Korrektur, kein Review: der Ziel-Picker zog seine Kandidaten
aus `editableEmoteSetIds`, einem Feld, das nur mit 7TV-Anmeldung lesbar ist — daraus folgten der
Token-Prompt **vor** dem Picker, die Abweichung von A16/R2 und Sonde 7. Unnötig: die
Editor-Beziehung fragt EmotePurge längst serverseitig ohne Nutzer-Token ab (`editor_of`), sie hängt
bei 7TV am Account, und die Set-Liste eines Accounts ist öffentlich (7.3). Der Betreiber hat die
Token-Entscheidung **zurückgenommen** (R2 gilt unverändert), den DECISIONS-Entwurf 2 näher an den
ursprünglichen Eintrag gerückt (10) und Sonde 7 durch eine benannte Annahme ersetzt; vor Schritt 4
steht kein Tor mehr (13).

Nichts hiervon wird vor dem bindenden Harness-Lauf am **2026-10-08** deployt (Abschnitt 12) — ohne
Ausnahme; der Bedarf vor dem 2026-10-01 wird ohne Deploy gedeckt (12.4). Kein DECISIONS-Eintrag in
diesem Papier — die kommen mit den Commits, die Vertrag und Topologie ändern (Regel 3, Abschnitt 10).

---

## 0. Fazit vorab

**Das Emote bleibt eine Zeile pro Kanal; die Set-Zugehörigkeit wird nicht gespeichert, sondern bei
Bedarf live von 7TV geholt; gezählt wird pro Set, indem jede `UsageStat`-Zeile das Set trägt, das
EmotePurge im Zählmoment als aktiv beobachtet hat.** Damit kommt kein zweites Emote-Objekt in die
Welt (das hätte Votes, Wahlzettel und Mass-Delete-Auswahl gespalten), kein zweiter Sync-Kreis und
keine weitere EventAPI-Subscription. Was neu entsteht, ist klein: eine Spalte auf `UsageStat`, ein
Beobachtungs-Log je Kanal, eine nullbare Spalte auf `VoteSession`, zwei Snapshot-Spalten auf
`VoteSessionEmote`, ein Set-Dropdown auf der Nutzungsseite, ein Ziel-Picker, der getrackte Ziele
vorschlägt und ungetrackte nach Bestätigung zulässt, ein set-zentrierter Audit-Endpunkt und ein
Quell-Set-Picker beim fremden Kanal.

Zwei Befunde korrigieren verbreitete Annahmen. Erstens **überlebt die Nutzungshistorie einen
Set-Wechsel heute schon** — der Sync löscht nie, er archiviert, und die Zeile wird beim Rückweg
reaktiviert (Abschnitt 2). Was fehlt, ist allein die Zugehörigkeit. Zweitens **kommt die Set-ID im
Zählpfad heute nirgends vor**: Match-Cache, Zähler und Flush arbeiten ausschließlich mit `Emote.Id`
(Abschnitt 5.1). Der Brief hielt das für „plausibel, aber nicht belegt"; es ist belegt, dass sie
fehlt, und der Weg, sie einzuführen, ist der eigentliche Kern des Backend-Anteils.

Die größte Falle liegt im Voting: eine Abstimmung über ein nicht-aktives Set wäre nach heutiger Regel
nicht bloß gesperrt, sondern **nicht einmal anlegbar** — und selbst wenn sie es wäre, könnte die
Detailseite ihren Löschweg nicht anzeigen, weil sie die Berechtigung aus dem Vorhandensein von
Nutzungszahlen ableitet (Abschnitt 8).

Der operative Punkt, den die erste Fassung übersehen hatte: **der Schema-Umstieg verträgt keinen
gestaffelten Deploy.** Ein alter Worker gegen das neue Schema verwirft nach fünf Versuchen seine
Zählungen. Die Antwort ist ein kurzes Wartungsfenster — Worker **und** Api anhalten, migrieren, neue
Images starten (seit der siebten Fassung beide, weil die Migration die `UsageStats`-Tabelle für ihre
Dauer exklusiv sperrt, 12.3) —, und es liegt hinter dem 2026-10-08 (Abschnitt 12).

**Vor dem 2026-10-08 wird trotzdem nichts deployt — der Bedarf vor dem 2026-10-01 ist ohne Deploy
gedeckt (12.4).** Die dritte und vierte Fassung hatten dafür eine Vorabscheibe vorgesehen; sie
entfällt. Ihr Grund war, dass das Ziel-Set nicht das aktive Set des Zielkanals war. Der Zwischenweg
des Betreibers dreht genau das um: das Halloween-Set wird in einem anderen getrackten Kanal **aktiv**
gesetzt — ein Set, das in mehreren Kanälen zugleich aktiv ist, ist im Repo kein Sonderfall, sondern
vorausgesetzt (`EmoteSetOwnershipService.cs:49-53`) — und „Übertragen" zielt auf diesen Kanal. Dann
ist Ziel-Set = aktives Set des Zielkanals, und Lauf-`setId`, Kollisionsvorschau und Slot-Projektion
stimmen ohne eine geänderte Zeile. Der Kanal ist seit der siebten Fassung festgelegt in seiner
**Art**, nicht im Namen: ein **noch nie getrackter Wegwerfkanal**, der nach dem 01.10. per
Admin-Purge wieder aus der Datenbank verschwindet — damit hinterlässt der Zwischenweg weder eine
falsch zuzuordnende Historie noch einen Kanal, der im Ziel-Picker beschreibbar bleibt (12.4).

**Nachgemessen in der vierten Fassung: Namen und IDs sind zwei verschiedene Sichten auf dasselbe
Set-Paar.** 520 Namen liegen in HandOfBloods Haupt- und Halloween-Set zugleich, aber nur 328 davon
mit derselben 7TV-ID; 192 sind Saisonvarianten gleichen Namens mit eigener ID — bei uns schon heute
eigene Zeilen mit eigenen Zählern (5.2). Für den Anlass zählt die Namenssicht, weil 7TV beim `ADD`
den Alias prüft: nach Namen fehlen dem Halloween-Set **242** Hauptset-Emotes, nicht die 422 der
ID-Sicht — und weil der ausgelieferte Lauf per **ID** überspringt, landen davon rund **232**: etwa
zehn der 242 liegen als dieselbe ID schon unter anderem Alias im Halloween-Set und werden still als
„bereits vorhanden" übersprungen (7.5, siebte Fassung). Die 192 Namensvettern zeigt der ausgelieferte
Dialog vorher an und lässt sie im Lauf (der das Mod-Team im Zwischenweg von Hand abwählt); nach dem
Deploy bleiben sie **draußen** und erscheinen als eigene Gruppe, ebenso die Alias-Abweichungen
(Entscheidungen des Betreibers, 7.5).

**Nachgetragen in der sechsten Fassung: eine Zeile der Set-Ansicht ist über ihre `SevenTvEmoteId`
identifiziert, nicht über `Emote.Id`.** Ein Set ist bei 7TV definiert, die lokale Id ist kanal-skopiert
(Regel 8), und für rund die Hälfte eines Saison-Sets gibt es sie schlicht nicht — bei HandOfBlood haben
alle Halloween-Emotes, die nicht als dieselbe ID auch im Hauptset liegen, heute keine Zeile im Kanal
(plausibel aus 11.2: 686 − 338 = 348, 51 %). Löschen aus einem solchen Set muss trotzdem gehen, weil
7TV dafür nur die 7TV-Id braucht — und geht heute nur bis zur Mutation; Auswahl, Rückmeldung,
Protokoll und Wiederherstellung hängen an der Guid. Der Vertrag steht in 6.5; er ändert den Body von
`sync-deleted`/`sync-restored` und den Queue-Key des Lösch-/Restore-Laufs, sonst keine Topologie.

---

## Überarbeitung nach adversarialem Review (2026-09-19)

Der Review hat acht Befunde geliefert; jeder wurde am Code nachgeprüft, bevor er übernommen wurde.
Sieben treffen zu, einer trifft im Kern zu und ist in einem Nebensatz falsch. Danach hat der
Betreiber vier Entscheidungen getroffen bzw. revidiert; eine davon (Altdaten) ist im Laufe des
Tages zweimal gedreht worden und steht hier in der Endform.

| # | Befund (Codex) | Prüfung am Code | Folge im Dokument |
|---|---|---|---|
| B1 | **[critical]** Schema-First-Deploy legt den laufenden Worker lahm | **Trifft zu.** `UsageStatFlushService.cs:71` schreibt `ON CONFLICT ("EmoteId", "Date")`; PostgreSQL verlangt für ein Conflict-Target einen exakt passenden Unique-Index. Ist der alte Index durch `(EmoteId, EmoteSetId, Date)` ersetzt, scheitert jeder Flush des alten Workers am fehlenden Index — unabhängig davon, ob die neue Spalte nullbar wäre (das hätte nur den zweiten Stolperstein, die Not-null-Verletzung, genommen). `UsageFlushWorker.cs:19,89-103`: fünf Versuche, dann verworfen; der Shutdown-Flush (`:30-39`) läuft gegen dasselbe Schema. | Abschnitt 12: Wartungsfenster statt gestaffelter Deploys, bewusst die einfache Lösung |
| B2 | **[high]** Migration macht aus unbekannter Historie belastbar aussehende Set-Analytik | **Trifft im Allgemeinen zu, greift hier aber nicht:** der Betreiber weiß, dass genau ein getrackter Kanal je gewechselt hat — sein eigener Testkanal. Für alle Produktivkanäle ist die Zuordnung nicht geraten, sondern richtig. | 4.3: Entscheidung D bleibt, jetzt mit benannter Ausnahme, Purge-Empfehlung und Prüfabfrage (11.6) als Positivkontrolle |
| B3 | **[high]** Aktivierungs-Log zeichnet Lücken als durchgehende Aktivierung auf | **Trifft zu.** `LeaveAsync` (`ChannelService.cs:46-77`) setzt nur `IsBotActive = false`; beide Rename-Pfade (`ChannelService.cs:236-252`, `ChannelIdentityService.cs:325-349`) und der Merge (`:361-483`) setzen `TrackingResumedAt`, schlössen aber nichts. Nach einem Rejoin auf dasselbe Set wäre `emoteSetSwitched` false — die alte Zeile bliebe über die ganze Lücke offen; wurde in der Lücke gewechselt, gälte das alte Set fälschlich bis zum Rejoin. | 4.4 modelliert **Beobachtungsintervalle** statt Aktivierungen |
| B4 | **[high]** Keine stabile Set-Eintrags-Identität | **Trifft zu.** `UpsertEmote` überschreibt `Emote.Name` bei jedem Sync (`SevenTvSyncService.cs:515-517`); `VoteSessionEmote` hält nur `EmoteId` (`VoteSessionEmote.cs:9-10`); die Set-Ansicht der ersten Fassung startete von der Live-Liste und hätte abgegangene Einträge samt ihren Zeilen verloren. | 6.2: Vereinigung Live-Liste ∪ lokal beobachtete Mitglieder, abgegangene gekennzeichnet; #74-Duplikate nicht mehr wegdedupliziert; 8: Anzeigedaten auf `VoteSessionEmote` eingefroren |
| B5 | **[high]** Autorisierter Schreibzugriff auf den falschen Mandanten; Audit deckt ihn nicht; Falschbehauptung zum heutigen Verhalten | **Trifft zu, einschließlich der Falschbehauptung.** `seven-tv-import.service.ts:296-301` postet `sync-imported` an `run.targetChannelName`, also den **Ziel**kanal; die Gruppe steht hinter `UsageStatsAccessAuthorizationFilter` (`EmoteEndpoints.cs:28`), und `MarkImportedAsync` lädt die Kanalzeile und liefert ohne sie `false` → 404 (`EmoteService.cs:112-116`, `EmoteEndpoints.cs:206`). Die erste Fassung hatte behauptet, der Eintrag laufe schon heute gegen den Seitenkanal — das war falsch. | 7.3: getrackte Ziele als Vorgabe, ungetrackte nur nach Bestätigung mit Besitzer und Setnamen; 7.4: set-zentrierter Endpunkt ohne `Channel`-Zeile |
| B6 | **[high]** `ChannelSyncGate` serialisiert nicht dienstübergreifend | **Trifft zu.** Die Klasse ist ein prozessweiter `SemaphoreSlim`-Halter (`ChannelSyncGate.cs:5-22`), registriert als Singleton (`ServiceCollectionExtensions.cs:88`). `SyncChannelAsync` wird ausschließlich aus dem Worker aufgerufen (`Worker.cs:140`, `SevenTvPeriodicResyncWorker.cs:75`, `SevenTvEventClient.cs:374,452`); die Vote-Erstellung liefe im Api-Prozess. Zwei Prozesse, zwei Gates, keine Sperre. | 8: Berufung gestrichen, datenbankseitiges Upsert mit Nachlesen, Wettlauftest mit zwei `DbContext`s |
| B7 | **[high]** Session über ein nie aktives Set kann ihren Mass-Delete-Weg nicht anzeigen | **Trifft zu.** `BuildResultRow` nullt `useCount` für archivierte Zeilen (`VoteSessionQueryService.cs:191`); `canSelectForDelete = hasUsageData` (`vote-session-detail-page.ts:211-220`) — obwohl dieselbe Seite `canManage` aus `/permissions` bereits in der Hand hat (`:198-205`). | 8: Berechtigung von Datenvorhandensein entkoppelt, session-relative Eignung statt kanalglobalem `IsArchived` |
| B8 | **[medium]** Die Momentaufnahme hält das **beobachtete** Set fest, nicht das beim Absenden aktive | **Trifft zu.** Die erste Fassung schrieb „die Zuordnung im Zählmoment ist nie mehrdeutig" — zu stark. Sie ist nie mehrdeutig **gegenüber unserem Cache**; gegenüber 7TV ist sie um die Beobachtungsverzögerung versetzt (60 s Resync, 10–30 min REST-Cache, #76-Blockade unbefristet). | 3 und 5.2: Feld als „lokal beobachtetes Set" definiert, Unschärfe an der Grenze benannt, Wechsel-Tests |

**Entscheidungen des Betreibers danach:**

- **Altdaten (D, revidiert und zurückrevidiert):** Zwischenzeitlich war ein `NULL`-Platzhalter
  („vor der Set-Trennung erfasst") beschlossen; das ist wieder verworfen, weil die Tatsache, die B2
  voraussetzt — unbekannte Wechselhistorie —, hier nicht vorliegt. Bestandszeilen gehen auf das beim
  Umstieg aktive Set; die einzige falsch zugeordnete Historie im Bestand wären Testdaten (4.3).
- **Strikte Trennung nach Set, kein Sammelview.** Ein Dropdown-Eintrag „Gesamt (alle Sets)" ist
  ausdrücklich verworfen, nicht mehr „offen" (Abschnitt 9).
- **Set-Ansicht eines nie aktiven Sets:** kein Zurückhalten der Tot-Klassifikation, keine Schwelle,
  keine Warnung — nur eine Tatsachenangabe aus dem Beobachtungs-Log (6.2). Eine frühere Fassung
  dieser Auflage mit Schutzmechanik hat der Betreiber zurückgewiesen.
- **Ziel-Picker:** getrackte Ziele als Vorgabe, ungetrackte nach ausdrücklicher Bestätigung (7.3).
  Die Lockerung von DECISIONS 2026-09-09 bleibt, fällt aber kleiner aus.
- **Zeitfenster:** Deploy **hinter** dem bindenden Harness-Lauf am 2026-10-08, nicht auf diesen
  Tag; auch die Teile ohne Worker-/Schema-Bezug werden nicht vorgezogen (Abschnitt 12).
  **Am Abend desselben Tages für genau einen Anlass revidiert:** HandOfBloods Set-Wechsel am
  2026-10-01 zieht eine kleine, klar abgegrenzte Vorabscheibe vor den Termin (12.4). Der Satz
  „nichts wird vorgezogen" in 12.2 bleibt für alles andere stehen; die Ausnahme ist dort benannt
  und begründet, nicht still gestrichen. **In der fünften Fassung wieder aufgehoben.** Die
  Ausnahme galt eine Runde lang. Sie entfällt nicht, weil das Risiko neu bewertet wäre, sondern
  weil ihr Anlass anders gedeckt ist: der Betreiber hat einen Weg gefunden, der ohne Deploy
  auskommt (12.4). 12.2 gilt wieder ohne Einschränkung, 13 hat wieder einen Deploy.
- **Wartungsfenster** statt dreier gestaffelter Deploys (Abschnitt 12).

**Vierte Runde (2026-09-19, spät) — kein Review, ein Messbefund.** Der Betreiber hat auf
gleichnamige Emotes mit verschiedener 7TV-ID zwischen Haupt- und Halloween-Set hingewiesen;
nachgemessen in 11.2 („Namen gegen IDs"). Eingearbeitet in 3 (tragende Zahl für A), 5.2
(ausdrückliche Bestätigung), 6.2 (neuer Anzeigefall „Namensvetter im anderen Set"), 7.2, 12.1, 12.4
(Kollisionsrechnung für den Anlass als zweites Argument für V4, Präzisierung der V4-Gefahr im
Gleichkanal-Fall, Rückfallebene, Live-Verifikation — die technischen Teile stehen seit der fünften
Fassung in 7.5) und 13. Drei frühere Formulierungen sind
dadurch falsch geworden und **korrigiert, nicht überschrieben** — jeweils mit Vermerk an Ort und
Stelle: „422 Emotes … die Menge, aus der der Anlass auswählt" (11.2); „die Vorprüfung … ist schon
set-skopiert … braucht nichts" (damals 12.4 V4, jetzt 7.5 — sie ist set-skopiert, vergleicht aber
nur IDs); und der
Satz in Abschnitt 3, an HandOfBlood sei nicht gemessen worden, den 11.2 derselben Fassung schon
widerlegt hatte. Dazu ein Nachtrag in 4.3, der nicht aus dem Namensbefund stammt, sondern beim
Einarbeiten aufgefallen ist: HandOfBlood ist am Migrationstag ein gewechselter Kanal.

**Fünfte Runde (2026-09-19, Nacht) — kein Review, eine Idee des Betreibers.** Statt EmotePurge
beizubringen, in ein nicht-aktives Set zu schreiben, wird das Halloween-Set woanders **aktiv**
gemacht: in einem anderen getrackten Kanal, der dann Ziel von „Übertragen" ist. Die Hauptsession hat
das am Code geprüft (12.4): ein Set kann in mehreren getrackten Kanälen zugleich aktiv sein, und ist
das Ziel-Set das aktive Set des Zielkanals, rechnet der ausgelieferte Dialog von selbst richtig.
Eingearbeitet: 12.4 (Handlungsanweisung statt Bauauftrag), 12.2 (Ausnahme aufgehoben, die beiden
Stütz-Tatsachen als Entwarnung behalten), 13 (ein Block, ein Deploy — die Bruch-Begründung der
vierten Fassung lautete „ein Schema-Umstieg, ein Deploy; die Vorabscheibe hat keinen", galt genau
eine Runde und ist mit ihrem Anlass entfallen), 7.3 (Begründung des Ziel-Set-Pickers: Dauerfall
statt Termin; der Zwischenweg ist ein Kniff, keine Lösung), 7.5 (neu — die technischen Befunde aus
dem alten 12.4, unverändert gültig), 4.3 (Wechseltag empirisch aus `ArchivedAt`, nicht aus dem
Kalender, samt Rückwechsel-Vorbehalt — in der siebten Fassung durch eine bestätigte Zuordnungsliste
ersetzt, D1), 6.1/7.2/7.4/10 (Verweise auf die Scheibe aufgelöst). Die
Verweise „V1"–„V5" in den Absätzen zur dritten und vierten Runde oben bezeichnen die Teile der
entfallenen Scheibe und bleiben als Geschichte stehen.

**Sechste Runde (2026-09-19, spät nachts) — zweiter Codex-Review, abgebrochen, ein Befund.** Der
Review (`gpt-5.6-sol`, adversarial, gegen die fünfte Fassung) hat sein Kontingent erreicht, bevor er
die Befundliste ausformulieren konnte; überliefert ist eine einzige Aussage, hier wörtlich:

> The strongest issue so far is not merely an open detail: the proposed non-active-set page includes
> entries with no local `Emote.Id`, while the existing selection, delete, restore, and bookkeeping
> paths require that ID. The document says those paths need almost no change, but the code cannot
> execute that contract as written.

| # | Befund (Codex) | Prüfung am Code | Folge im Dokument |
|---|---|---|---|
| C1 | **[abgebrochener Review, „strongest issue so far"]** Set-Einträge ohne lokale `Emote.Id`; Auswahl, Löschen, Wiederherstellen und Buchhaltung verlangen sie; „braucht fast keine Änderung" ist falsch | **Trifft zu, und reicht weiter als formuliert.** Die 7TV-Mutationen selbst brauchen die Guid nicht (`REMOVE`/`ADD` reisen mit `sevenTvEmoteId`, `seven-tv-delete.service.ts:43-46`, `seven-tv-restore.service.ts:36-39`) — insofern stimmte „Engine und Mutation brauchen nichts Neues". Alles um den Lauf herum hängt an der Guid: Auswahlschlüssel (`usage-stats-page.ts:548-552`), inneres `track` (`usage-stats-page.html:556`), Queue-Key (`seven-tv-delete.service.ts:120`, R3), `RunResult.doneIds` als einzige Rückmelde-Identität (`seven-tv-delete.service.ts:181-183`), Body und Match von `sync-deleted`/`sync-restored` (`EmoteEndpoints.cs:274-276`, `EmoteService.cs:25-27`), optimistischer Listen-Update (`mass-delete-panel.ts:297-302`, `usage-stats-page.ts:1251`), das **Purge-Protokoll**, das Zeilen ohne Guid vorher aussortiert (`mass-delete-panel.ts:344-351`, `purge-run-export.ts:37-42,157`), und der Voting-Draht (`usage-stats-page.ts:1276`, `VoteSessionService.cs:302-306`). Übertragen und Export sind **nicht** betroffen (`toImportRow`, `usage-export.ts` kennen nur `sevenTvEmoteId`). | Neu **6.5**: Identitätsvertrag (Schlüssel `SevenTvEmoteId`, Guid als Nutzlast, Lauf und Rückwege sprechen 7TV-Ids), drei Zeilenklassen plus die Zwischenklasse 2b aus dem Voting-Upsert, Messung je Stelle, Abgrenzung zu 8. Korrigiert: 7.1 („brauchen nichts Neues") und 6.2 („trotzdem benutzbar zum Löschen"). Nachgezogen: 10 (vierter Eintrag), 12.1 (#201), 13 (Umfang der Schritte 6 und 7) |

**Siebte Runde (2026-09-19, nachts) — wiederholter Codex-Review, zehn Befunde, und drei
Entscheidungen des Betreibers.** Jeder Befund ist am Code nachgeprüft; wo er enger ist als
formuliert, steht das in der Spalte.

| # | Befund (Codex) | Prüfung am Code | Folge im Dokument |
|---|---|---|---|
| D1 | **[high]** Die Migration kann gewöhnliche Massenbearbeitungen für einen Set-Wechsel halten | **Trifft zu.** `ReconcileAsync` archiviert **alles**, was eine vollständige Antwort nicht nennt (`SevenTvSyncService.cs:454-471`), mit gleichem `ArchivedAt` und ohne Audit-Eintrag — bei einer direkten Massenlöschung auf 7TV ebenso wie bei einer unvollständigen Antwort; die Plausibilitätssperre greift nur bei **null** Emotes (`:351-370`). Die Signatur aus Abschnitt 2 ist notwendig, nicht hinreichend; und die alte Set-ID steht nirgends in der Datenbank | 4.3: Eingabe der Migration ist eine vom Betreiber bestätigte Zuordnung (Kanal-Id, alte Set-ID, neue Set-ID, UTC-Grenze); 11.6 nur noch Gegenprobe mit Abbruch bei Abweichung |
| D2 | **[high]** Das Beobachtungs-Log widerspricht der geteilten Zuordnung | **Trifft zu.** 4.4 säte ein Intervall ab `TrackingResumedAt ?? CreatedAt` fürs heutige Set; nach 4.3 hätte HandOfBlood aber zwei Phasen | 4.4: die Migration sät je Zuordnungsintervall ein geschlossenes bzw. offenes Intervall; dieselbe Eingabe wie 4.3 |
| D3 | **[high]** Runbook nicht sicher für Verfügbarkeit und Rollback | **Trifft zu.** Backfill, `SET NOT NULL`, Indextausch laufen in einer Transaktion und halten `ACCESS EXCLUSIVE` auf `UsageStats`; jede Api-Abfrage über `UsageStats` (`UsageStatQueryService.cs`, `EmoteSetStatusService.cs`) blockiert so lange. Rollback ließ die neue Api gegen ein `Down`-Schema laufen. `stop_grace_period: 60s` steht schon in `docker-compose.prod.yml:95` | 12.3: Wartungsfenster für Worker **und** Api, Rollback stoppt beide, Timeouts, Grace-Period-Notiz gestrichen |
| D4 | **[high]** Kein sicherer Rückbau des Zwischenwegs | **Trifft zu.** `LeaveAsync` setzt nur `IsBotActive = false` (`ChannelService.cs:64`); `MyChannelsService` meldet die Zeile weiter als `IsTracked` (`:92`), der Picker deaktiviert nur `!isTracked` (`import-target-options.ts:30`); Tier 2 zählt inaktive Zeilen mit (`EmoteSetOwnershipService.cs:49-53`) | 12.4: Wegwerfkanal, der nie getrackt war, nach dem 01.10. per Admin-Purge entfernt — Entscheidung des Betreibers; 4.3/11.6/13 nachgezogen |
| D5 | **[high]** Namens-/ID-Arithmetik widerspricht dem ausgelieferten Filter; Alias-Zusammenfall macht Vorschau und Chat-Zählung unvollständig | **Trifft in der Arithmetik zu, in der Zählung nur für #74.** `filterAlreadyPresent` und `buildImportPreview` überspringen jede ID des Zielsets unabhängig vom Alias (`already-present-filter.ts:151`, `import-preview.ts:43-46`): 338 übersprungen, nicht 328. Die Chat-Zählung betrifft nur das **aktive** Set, und dessen Alias ist genau der gespeicherte; nicht-aktive Sets werden nie gematcht, ihr Alias reist mit dem Live-Abruf (5.2). Was bleibt, ist der #74-Fall **innerhalb** eines Sets (11.5) | 7.5 und 12.4: Zahlen korrigiert (338 / ~424 / 192 / ~232); Alias-Abweichung als eigene Dialoggruppe (Entscheidung); 5.2 grenzt die Zählfrage ein |
| D6 | **[high]** Serien-Identität und Cache-Vertrag inkonsistent | **Trifft zu.** `EmoteSeriesEntryDto(string EmoteId, …)` (`IUsageStatQueryService.cs:95`) und `seriesByEmote` sprechen Guid; die Caches in `usage-stat.service.ts:33,59` schlüsseln ohne Set | 6.5 Vertrag 2: `/series` spricht `SevenTvEmoteId`, beide Caches und der Drilldown tragen die Set-ID |
| D7 | **[high]** Rückmelde-Body über Deploy und Retry hinweg unsicher | **Trifft zu.** `DeleteRunInfo`/`RestoreRunInfo` halten keine Set-ID (`seven-tv-delete.service.ts:79-83`, `seven-tv-restore.service.ts:62-66`); ein altes Bundle postet `{ emoteIds }` | 6.5 Vertrag 3 / 7.1: beide Bodies für eine Übergangszeit, Laufdatensatz trägt die Set-ID |
| D8 | **[high]** Das Voting-Upsert verliert den Worker-first-Wettlauf | **Trifft zu.** `ON CONFLICT DO NOTHING` schützt nur die Api-Seite; der Worker liest `existing` (`:442-444`), staged `Add` (`:539`) und scheitert bei `SaveChangesAsync` (`:108`) am Unique-Index, `RefreshMatchCacheAsync` (`:109`) läuft nicht | 8: Worker wird konfliktverträglich (Nachlesen und einmal wiederholen), Test mit erzwungener Verschränkung |
| D9 | **[medium]** Export weder set-identifizierbar noch nullbar | **Trifft zu.** `buildUsageExportPurposeDownload` lässt `emoteSetId` für `usage-csv`/`usage-json` weg (`usage-export-purposes.ts:93-101`); `UsageExportRow.totalUseCount: number` (`usage-export.ts:29-30`) | 6.1: Set in Dateiname und Metadaten, `null`-Serialisierung festgelegt; 6.5 korrigiert „Export unberührt" |
| D10 | **[medium]** Produktentscheidungen an den Plan delegiert | **Trifft zu.** | Vom Betreiber entschieden: Token **vor** dem Ziel-Picker (7.3 — in der achten Fassung zurückgenommen), Kollisionen **draußen** aus der Warteschlange (7.5), Alias-Abweichung **übersprungen, aber sichtbar** (7.5); Umbenennung auf 7TV ausdrücklich nicht in #200 |

**Achte Runde (2026-09-20) — kein Review, eine Korrektur des Betreibers.** Die Herleitung des
Ziel-Pickers über `editableEmoteSetIds` (D10) war unnötig, `editor_of` liefert dieselbe Beziehung
serverseitig (Kopf, 7.3). Eingearbeitet: 7.3, 7.4, 7.5 (Baustein 1, Schnittstellen), 10 (Eintrag 2
ohne R2-Revision), 11 (Messpunkt 7 gestrichen), 13 (kein Tor vor Schritt 4).

Unverändert geblieben: `UsageStat.EmoteSetId` als Träger der Zuordnung; `Emote` eine Zeile pro
Kanal; nicht-aktive Sets nur auf Abruf, keine zusätzlichen Subscriptions, keine
Zugehörigkeits-Tabelle; die Papier-Variante von `sync-deleted`/`sync-restored`; Backfill (#69)
bekommt die Set-ID als Parameter; Voting für nicht-aktive Sets über nullbares
`VoteSession.EmoteSetId`.

---

## 1. Worum es geht

HandOfBloods Mod-Team nach der vollständigen Bereinigung am 2026-09-18: „you can only edit the active
set and can't transfer emotes between sets". Der Kanal führt mehrere 7TV-Sets (Standard, saisonale
Sets); EmotePurge sieht davon genau eines. Konkret gewünscht: ein nicht-aktives Set ansehen, darin
löschen, Emotes zwischen eigenen Sets kopieren, beim Import Quell- wie Ziel-Set wählen.

**Wo es heute stoppt — belegt:**

- Die Schreibseite ist bereits set-agnostisch. `seven-tv-run-engine.ts:239-244` nimmt
  `start(setId, emotes, operation, onComplete)`; `seven-tv-delete.service.ts:115` `startDelete(setId,
  channelName, emotes)`; `seven-tv-restore.service.ts:141-142` `startRestore(setId, …)`; die beiden
  Mutationen tragen `$setId` (`seven-tv-delete.service.ts:30-46`, `seven-tv-restore.service.ts:24-39`).
  `mass-delete-panel.ts:190`, `file-import-step.ts:82` und `import-trigger.ts:63` deklarieren `setId`
  als `input.required<string>()`. Die Annahme „aktives Set" entsteht erst im Template:
  `usage-stats-page.html:108` (Kopfzeile „Übertragen") und `:850` (Dock) gaten auf
  `activeEmoteSetId()`, und `vote-session-detail-page.html:155-157` reicht dasselbe Signal an das
  Panel — die letzte Stelle nennt der Brief nicht, sie ist für Abschnitt 8 relevant.
- Die Leseseite hängt vollständig am Kanal. `GetUsageContextAsync`
  (`UsageStatQueryService.cs:26-105`) liefert `TotalUseCount` je nicht-archiviertem Emote des Kanals;
  Sortierung, Pareto-Bänder, Verteilungsstreifen, Fill-Bars, „tote" Emotes und Summenzeile lesen
  diese Zahl. `/totals`, `/daily`, `/series` (`UsageStatsEndpoints.cs:35,56,85`) kennen nur
  `from`/`to`.
- Das Datenmodell kennt ein Set pro Kanal: `Channel.ActiveEmoteSetId` (`Channel.cs:8`), `Emote` ohne
  Set-Bezug mit Unique-Index `(ChannelId, SevenTvEmoteId)` (`AppDbContext.cs:30`), `UsageStat` mit
  Unique-Index `(EmoteId, Date)` (`AppDbContext.cs:42`).
- Es gibt keine Abfrage „alle Sets eines Users". `ResolveSevenTvIdentityAsync` liest per
  `userByConnection` nur `connections { platform id emote_set_id }` (`SevenTvApiClient.cs:29-30`,
  `SevenTvModels.cs:203`); `ForeignEmoteSetService.cs:95-103` nimmt daraus allein das aktive Set. 7TV
  kennt die Liste aber, und seit dem 2026-09-19 ist gemessen, wie (11.1): v4
  `User { emoteSets { id name capacity owner … } }` kommt **ohne Anmeldung** in einem Request; v4
  `User { editableEmoteSetIds }` (`docs/Feature-Ideen-2026-08-01.md:821`) dagegen **nur mit
  7TV-Anmeldung** — unauthentifiziert antwortet 7TV mit `LOGIN_REQUIRED` und nullt das ganze
  User-Objekt; v3 `.user.emote_sets` trägt `id, name, flags, tags, capacity` (DECISIONS 2026-09-01,
  gemessen: handofblood 3 Sets, pokimane 10).

**Importwege.** Der Brief zählt drei; im Audit-Vokabular sind es vier Herkunftsvokabeln
(`EmoteEndpoints.cs:146`: `channel`, `file`, `seventv-channel`, `seventv-leaderboard`). Für die
Set-Frage sind es drei *Quellformen*: **Datei** (kein Quell-Set), **fremder Kanal** (Quelle = dessen
aktives Set, `ForeignEmoteSetService` → `GetEmoteSetPreviewAsync`), **Bestenliste** (netzwerkweit,
gar kein Set, `SearchEmotesAsync`). Die vierte Vokabel `channel` ist das „Übertragen" von der eigenen
Nutzungsseite in einen anderen Kanal — derselbe Lauf, Ziel über `import-target-dialog.ts`, Kandidaten
aus `import-target-options.ts:22-33` (`listMine()`, Broadcaster oder 7TV-Editor, `disabled` wenn
nicht getrackt). Ihr Quell-Set ist das Set, das die Seite gerade zeigt — mit dem Dropdown aus
Abschnitt 6 also automatisch wählbar.

---

## 2. Was heute schon funktioniert

**Der Sync löscht nie, er archiviert — und ein Set-Wechsel ist für ihn eine gewöhnliche
Inhaltsänderung.** `ReconcileAsync` (`SevenTvSyncService.cs:440-477`) setzt Zeilen, die 7TV nicht
mehr nennt, auf `IsArchived = true`/`ArchivedAt` (`:470-471`); `UpsertEmote` (`:487-520`) reaktiviert
sie über `(ChannelId, SevenTvEmoteId)`, sobald sie wiederkommen (`:515-523`). `UsageStat` hängt an
`Emote.Id` (`UsageStat.cs:6`) und überlebt das Archivieren; nur ein Hard-Delete (Admin-Purge)
kaskadiert (`AppDbContext.cs:49`). Der Wechsel selbst wird an genau einer Stelle festgestellt:
`emoteSetSwitched` (`SevenTvSyncService.cs:83`) vergleicht die bisherige mit der gemeldeten Set-ID,
`:86` überschreibt sie, `:91` die Kapazität — das Flag fließt nur als `HasChanges` in das Ergebnis
(`:113-117`), es gibt **keine Historie, welches Set wann aktiv war**, keinen Audit-Eintrag. Die
zweite Stelle, an der ein Wechsel sichtbar wird — `HandleUserUpdateAsync`
(`SevenTvEventClient.cs:407-440`, Log „7TV-Set-Wechsel für …") — schreibt nichts, sie stößt nur den
Resync an, der wieder bei `:83` landet.

Folge: wer heute von Standard auf Halloween wechselt und zurück, verliert keine Zahl. Die
Standard-Emotes werden archiviert, ihre `UsageStat`-Zeilen bleiben, beim Rückwechsel sind sie wieder
aktiv und die Historie steht. Was fehlt, ist nicht die Historie, sondern die **Zuordnung**: die
Nutzung eines Emotes, das in beiden Sets liegt, ist heute eine Summe über beide Phasen. Alles
Folgende baut auf diesem Befund auf — deshalb ist keine Zugehörigkeits-Tabelle nötig.

Nebenbefund, der für 4.3 und 11.6 zählt — **und seit der siebten Fassung nur noch als Gegenprobe
taugt (Codex D1):** ein Set-Wechsel hinterlässt im Bestand eine Signatur. Er archiviert die gesamte
Differenz zwischen altem und neuem Set in **einem** Sync, also mit einem gemeinsamen
`ArchivedAt`-Tag, und schreibt keinen Audit-Eintrag; ein Mass-Delete-Lauf archiviert ebenfalls viele
Zeilen an einem Tag (`MarkDeletedAsync`, `EmoteService.cs:29-36`), schreibt aber
`emotes.syncDeleted` mit `emoteCount` (`:43-49`). Die Signatur ist aber **notwendig, nicht
hinreichend**: `ReconcileAsync` archiviert **alles**, was eine vollständige 7TV-Antwort nicht nennt
(`SevenTvSyncService.cs:454-471`) — eine direkte Massenlöschung im 7TV-Frontend, ein Editor, der
dort dreihundert Emotes von Hand entfernt, oder eine unvollständige, aber nicht leere Antwort sehen
in der Datenbank exakt gleich aus, und keiner davon schreibt einen Audit-Eintrag. Die
Plausibilitätssperre schützt nur den Fall **null Emotes** (`TryGuardAgainstImplausibleWipeAsync`,
`:351-370`). Dazu kommt, dass die Datenbank die **alte** Set-ID nirgends hält — `Channel.
ActiveEmoteSetId` wird überschrieben (`:86`), eine Historie gibt es nicht. Aus der Signatur allein
lässt sich also weder sicher sagen, *dass* gewechselt wurde, noch *wovon*. Was sie kann: eine
anderweitig bekannte Aussage („dieser Kanal hat an diesem Tag von Set X auf Set Y gewechselt")
**bestätigen oder widerlegen** — Tag und Zeilenzahl müssen passen. So wird sie in 4.3 und 11.6
benutzt.

---

## 3. Der gewählte Ansatz und die verworfenen Alternativen

Issue #200 stellt zwei Varianten zur Wahl: **`EmoteSetId` an `Emote`** mit erweitertem Unique-Index,
oder eine **eigene `EmoteSet`-Entität** mit 1:n zu `Emote`. Beide sind verworfen (Entscheidung B),
aus demselben Grund: **beide spalten das Emote.** `hbLUL` in Standard und Halloween wären zwei
Zeilen mit zwei `Emote.Id`s. Daran hängen `Vote.EmoteId` und `VoteSessionEmote.EmoteId` (beide
Cascade auf `Emote`, `AppDbContext.cs:83-121`), die Mass-Delete-Auswahl (`selectedKeys` über
`Emote.Id`), der Drilldown, der Export. Das Emote erschiene im Raster doppelt, eine Abstimmung müsste
entscheiden, über welche der beiden Zeilen sie geht, und ein Set-Wechsel würde die Historie nicht
mehr über eine Zeile tragen, sondern über zwei — der Befund aus Abschnitt 2 ginge verloren. Dazu
käme ein zweiter Sync-Kreis (welche Sets, wie oft, mit welchen Subscriptions), für einen Nutzen, den
das Zählen gar nicht braucht.

**Gewählt (A/B/C):**

- **A — Zählen pro Set über `UsageStat.EmoteSetId`.** Die Spalte trägt das Set, das **EmotePurge im
  Zählmoment als aktiv beobachtet hatte** — nicht notwendig das, das 7TV in diesem Moment führte.
  Gegenüber unserem eigenen Zustand ist die Zuordnung eindeutig (es gibt genau einen Match-Cache je
  Kanal); gegenüber 7TV ist sie um die Beobachtungsverzögerung versetzt (5.2). Die erste Fassung
  schrieb „nie mehrdeutig"; das war zu stark. Ein Emote in zwei Sets erhöht nur den Zähler des
  gerade beobachteten.
- **B — `Emote` bleibt eine Zeile pro Kanal.** Unique-Index `(ChannelId, SevenTvEmoteId)` unverändert.
- **C — Nicht-aktive Sets werden bei Bedarf live geholt** (`GetEmoteSetPreviewAsync`,
  `SevenTvApiClient.cs:369-411`) und über die 7TV-Emote-ID mit unseren Zeilen verbunden. Keine
  Zugehörigkeits-Tabelle, keine erweiterte Sync-Schleife, keine zusätzlichen Subscriptions.
  Begründung: fürs **Zählen** braucht es nicht-aktive Sets gar nicht — gezählt wird nur, was im Chat
  matcht, und das ist per Definition das aktive Set. Gebraucht werden sie nur für die **Anzeige**, und
  dafür genügt ein Abruf im Moment der Auswahl.

Der Preis: die Anzeige eines nicht-aktiven Sets kostet 7TV-Requests (Abschnitt 6.4), und für
Emotes dieses Sets, die im Kanal nie aktiv waren, gibt es keine Zeile und keine Zahl — das ist wahr
und wird so beschriftet, nicht als 0 getarnt (Abschnitt 6.2).

**Der Nutzen von A ist seit dem 2026-09-19 gemessen, nicht mehr nur plausibel (11.2) — und die
tragende Zahl ist die Schnittmenge nach ID, nicht nach Name.** Der Wert der Set-Zähler hängt an den
Emotes, die als **dieselbe 7TV-ID** in Haupt- **und** Saison-Set liegen: nur sie sind bei uns eine
Zeile (Unique-Index `(ChannelId, SevenTvEmoteId)`, `AppDbContext.cs:30`; `UpsertEmote` reaktiviert
über genau diesen Schlüssel und überschreibt dabei den Namen, `SevenTvSyncService.cs:515-517`), und
nur bei ihnen summiert der Kanal-Zähler heute zwei Zählphasen, die erst die Set-ID trennt. Bei
HandOfBlood sind das **338** der 686 Halloween-IDs (49 %), bei `papaplatte` (Twitch 50985620,
gleiches Muster: `Emotes` 983, `Halloween 2024` 863, `Christmas 2025` 940 Einträge / 939 IDs) 555
(64 %) bzw. 453 (48 %). Zwei Kanäle, dasselbe Bild: rund die Hälfte eines Saison-Sets ist genau der
Fall, in dem Kanal-Zähler und Set-Zähler auseinanderlaufen. Bei disjunkten Sets hätte A nichts
geliefert, was die heutige Summe nicht auch sagt.

*Korrektur der dritten Fassung:* Dieser Absatz behauptete, an HandOfBlood sei nicht gemessen worden,
weil seine Twitch-ID in der Session fehlte. 11.2 derselben Fassung hatte ihn längst direkt gemessen
(Twitch `49140130`); der Absatz war schlicht nicht nachgezogen.

*Präzisierung der vierten Fassung (11.2 „Namen gegen IDs"):* Nach **Namen** ist die Überlappung
größer — 520 der 687 Halloween-Namen stehen auch im Hauptset —, aber 192 davon sind gleichnamige
Emotes mit **anderer** ID (`5Head`, `AINTNOWAY`, `Aware`, `Bedge`, …). Für sie ist die Frage „ein
Zähler oder zwei" von vornherein gegenstandslos: zwei 7TV-IDs sind bei uns zwei `Emote`-Zeilen mit
zwei Zählern, und der Match-Cache trifft je nach aktivem Set die eine oder die andere (5.2). Die 520
sind also **nicht** die Zahl, an der A hängt. Die 338 sind es — und innerhalb der 338 sind es rund
zehn IDs (338 − 328, aus den Summen nicht exakt auflösbar, 11.2), die zwischen den Sets sogar den
Alias wechseln: bei denen trennt tatsächlich nur noch die Set-ID die Phasen, weil nicht einmal der
Chat-Name den Unterschied verrät. Für den **Import** sind dieselben zehn die Gruppe „ist vorhanden,
heißt dort anders" (7.5, siebte Fassung): per ID schon im Zielset, per Alias nicht.

---

## 4. Datenmodell

### 4.1 `UsageStat.EmoteSetId` (A)

Neue Spalte `EmoteSetId` (7TV-Set-ID, **nicht null**). Der Unique-Index wird von `(EmoteId, Date)` zu
**`(EmoteId, EmoteSetId, Date)`**. Zwei Dinge hängen an genau diesem Index und ziehen mit:

- Er ist ein **Covering-Index** mit `INCLUDE (UseCount)`, und `GetUsageContextAsync` verlässt sich auf
  den Index-Only-Scan (`UsageStatQueryService.cs:65-68`, DECISIONS 2026-09-06 Task 4). Der
  erweiterte Index behält das `INCLUDE`; Abfragen ohne Set-Filter nutzen weiterhin das Präfix
  `EmoteId`, Abfragen mit Set-Filter das Präfix `(EmoteId, EmoteSetId)`.
- Der Flush schreibt rohes SQL mit `ON CONFLICT ("EmoteId", "Date")` (`UsageStatFlushService.cs:71`).
  Das Conflict-Target wird dreispaltig, das `UNNEST` (`:70`) bekommt ein viertes Array. Das
  #69-Design hat einen breiteren Conflict-Key **abgelehnt**, weil er „das Conflict-Target des heißen
  Live-Pfads und jede Lesequery anfasst" (`docs/designs/Chat-Log-Backfill-69-2026-09-05.md:306-308`,
  damals für eine `Source`-Spalte). Dieses Konzept tut genau das — bewusst, weil die Set-Dimension
  anders als die Herkunft nicht in eine getrennte Tabelle passt: sie ist Teil des Schlüssels der
  Zählung, nicht ein Attribut daran. Wer #69 später persistiert, findet den Key dann schon
  dreispaltig vor und muss ihn nicht ein zweites Mal aufreißen.

**Der Indextausch ist der Grund für das Wartungsfenster (Abschnitt 12).** Ein Conflict-Target
braucht in PostgreSQL einen Unique-Index, der exakt seine Spalten trägt; sobald der alte Index weg
ist, schlägt das alte Flush-SQL fehl — nicht wegen der Spalte, sondern wegen des Index. Die beiden
früheren Zähler-Migrationen (`20260901193259_AddUsageStatBotUseCount`,
`20260907080507_AddUsageStatSharedChatUseCount`) waren additiv und haben den Index nie berührt;
das hier ist die erste Migration an dieser Tabelle, die ein laufendes altes Image nicht verträgt.

### 4.2 `Emote` unverändert (B)

Keine neue Spalte, kein neuer Index. Zeilen für Set-Mitglieder, die im Kanal nie aktiv waren,
entstehen nur in einem Fall — beim Anlegen einer Set-Abstimmung (Abschnitt 8) — und dann als ganz
gewöhnliche archivierte Zeilen.

### 4.3 Bestandszeilen (D) — bedingt richtig, Bedingung bekannt

Die Migration setzt `EmoteSetId` jeder bestehenden `UsageStat`-Zeile auf das **beim Umstieg aktive
Set des zugehörigen Kanals** (`Emote.ChannelId` → `Channel.ActiveEmoteSetId`). Diese Zuordnung ist
**genau dann exakt richtig, wenn der Kanal nie gewechselt hat** — und genau dann eine Fehlbuchung,
wenn doch, weil Nutzung aus einer früheren Set-Phase dann unter dem heutigen Set stünde. Das ist
nicht rekonstruierbar (Abschnitt 2: es gibt keine Historie) und wird nicht geraten.

**Die Bedingung ist nicht unbekannt, sondern bekannt und eingegrenzt.** Nach Auskunft des
Betreibers hat **genau ein** getrackter Kanal je sein Set gewechselt: sein eigener, der zu
Testzwecken existiert. Alle übrigen getrackten Kanäle haben nicht gewechselt. Damit ist die
Zuordnung für jeden Produktivkanal richtig, und die einzige falsch zugeordnete Historie im Bestand
wären Testdaten. Codex' Einwand (B2) — die Migration erzeuge belastbar aussehende Analytik aus
unbekannter Historie — setzt voraus, dass die Historie unbekannt ist; das ist sie hier nicht. Ein
zwischenzeitlich beschlossener `NULL`-Platzhalter, der die Altzeilen aus jeder Set-Ansicht
herausgehalten hätte, ist deshalb wieder verworfen: er hätte für alle Produktivkanäle richtige
Daten unerreichbar gemacht, um eine Fehlbuchung in Testdaten zu vermeiden.

**Zwei Handhabungen für den Testkanal, eine Empfehlung:**

1. *Nichts tun.* Die Testdaten tragen dann eine falsch zugeordnete Historie; sie stören niemanden,
   aber sie sind die eine Stelle, an der der DECISIONS-Eintrag „bekannte Fehlbuchung" schreiben
   müsste.
2. *Den Testkanal vor der Migration purgen.* **Empfohlen.** `DELETE /api/channels/{name}/purge`
   (`ChannelEndpoints.cs:269-282`, Admin-Kanalliste) → `ChannelService.PurgeAsync`
   (`ChannelService.cs:80-108`). Das ist der bewusste Hard-Delete: `db.Channels.Remove(channel)`
   kaskadiert über `Channel → Emote → UsageStat`, `Channel → VoteSession → Vote`/`VoteSessionEmote`,
   `Emote → Vote`/`VoteSessionEmote` und `Channel → ChannelLiveDay` (`AppDbContext.cs`, alle
   `DeleteBehavior.Cascade`; nur `Vote → User` ist `Restrict` und nicht betroffen). Vorher
   publiziert die Methode `LEAVE`, damit der Worker aufhört, für den Kanal zu matchen; der
   Audit-Eintrag `channel.purge` bleibt als einzige Spur, weil `AuditLogEntry.ChannelName` ein
   Snapshot-String und kein FK ist (`AuditLogEntry.cs:36-43`). **Was dabei verloren geht:** die
   gesamte Nutzungs-, Voting- und Live-Tage-Historie dieses einen Kanals. Danach gibt es nach dem
   Umstieg keine einzige falsch zugeordnete Zeile mehr, und der DECISIONS-Eintrag braucht keinen
   Vorbehalt.

**Die Behauptung wird vor der Migration geprüft, nicht geglaubt** — mit der Prüfabfrage in 11.6, für
die der Testkanal die Positivkontrolle ist. Was die Abfrage kann und was nicht, steht dort.

**Die Bedingung gilt ab dem 2026-10-01 für HandOfBlood nicht mehr — und die Migration bekommt
dafür eine bestätigte Zuordnung, keine Ableitung (siebte Fassung, Codex D1).** HandOfBlood wechselt
am **2026-10-01** auf sein Halloween-Set (12.4), und die Migration liegt hinter dem 2026-10-08
(12.2). Am Migrationstag ist er also ein Kanal, der gewechselt hat: die Regel „Bestandszeilen aufs
beim Umstieg aktive Set" schriebe seine **gesamte** Hauptset-Historie unter die Halloween-Set-ID —
die Fehlbuchung aus B2, am Kanal, für den das Vorhaben gebaut wird. Ihn wie den Testkanal zu purgen
scheidet aus; sein Verlauf ist der Zweck des Ganzen.

Die vierte und fünfte Fassung wollten den Wechseltag aus der Datenbank **ableiten** — aus der
Massenarchivierung ohne Audit-Eintrag (Abschnitt 2). Das ist zurückgenommen: die Signatur ist nicht
spezifisch (eine direkte Massenlöschung auf 7TV oder eine unvollständige Antwort sieht identisch
aus, `SevenTvSyncService.cs:454-471`; nur der Null-Fall ist gesperrt, `:351-370`), `ArchivedAt`
hält nur die **letzte** Archivierung (ein Rückwechsel oder ein Restore nullt sie, `:515-523`,
`EmoteService.cs:81-85`), und die **alte** Set-ID steht ohnehin nirgends in der Datenbank. Eine
Migration, die daraus rät, würde bei einem falsch positiven Treffer die Historie eines ganzen Kanals
einem falschen Set zuschreiben — leise, in Analytik, die belastbar aussieht.

**Vertrag: die Eingabe ist eine Zuordnungsliste, vom Betreiber bestätigt, im PR gelesen.** Je
Kanal, der gewechselt hat, ein Eintrag aus **stabiler Kanal-Identität** (`Channel.Id`, zur Kontrolle
`TwitchChannelId` — nicht der Login, der sich ändern kann, `ChannelIdentityService`), **alter
Set-ID**, **neuer Set-ID** und **UTC-Grenze**; hat ein Kanal mehrfach gewechselt, mehrere Grenzen in
zeitlicher Folge. Für HandOfBlood: alte Set-ID `01GV88A38G0006FW5TVZVMG507`, neue Set-ID
`01J94NYQR0000D15QN0BDGN85E` (beide gemessen, 11.2), Grenze = der Zeitpunkt des Wechsels, den das
Mod-Team am 01.10. selbst herbeiführt und mitteilt. Die Migration ordnet `UsageStat`-Zeilen mit
`Date` **vor** dem Tag der Grenze der alten und **ab** dem Tag der neuen Set-ID zu; die Tageszeile
des Grenztags selbst kann beide Phasen enthalten und geht an die neue Set-ID — das wird im
DECISIONS-Eintrag als bekannte Unschärfe von höchstens einem Tag benannt, nicht kaschiert. Für
alle Kanäle **ohne** Eintrag gilt die Regel oben (beim Umstieg aktives Set), unter der Bedingung,
dass sie nie gewechselt haben. Wo die Liste lebt — Konstanten in der Migrationsklasse oder eine
kleine, von ihr gelesene Tabelle —, entscheidet der Plan; gebunden ist er daran, dass sie im PR
sichtbar und im DECISIONS-Eintrag wiederholt ist.

**Die Migration prüft die Liste und bricht ab, statt zu glauben (Fail-fast, S3-34).** Vier
Prüfungen, jede ein Abbruchgrund:

1. Jeder Eintrag findet seinen Kanal über die Id; die **neue** Set-ID des letzten Intervalls muss
   `Channel.ActiveEmoteSetId` im Migrationsmoment sein — sonst hat der Kanal seit der Bestätigung
   noch einmal gewechselt, und die Liste ist veraltet.
2. Am Tag jeder Grenze muss die Signatur stehen: eine Massenarchivierung dieses Kanals mit
   `ArchivedAt` an diesem Tag und ohne `emotes.syncDeleted` — die Prüfabfrage 11.6, jetzt als
   **Gegenprobe**. Die erwartete Zeilenzahl ist die Zahl der IDs des alten Sets, die im neuen Set
   nicht liegen — bei HandOfBlood **nach** der Vorbefüllung durch das Mod-Team (12.4) also weniger
   als die 422 aus 11.2; der Betreiber misst sie am Wechseltag mit der ID-Sonde aus 11.2 und trägt
   sie in die Liste ein. Fehlt die Signatur (Rückwechsel, Restore — s. o.), bricht die Migration
   ab, und der Betreiber entscheidet **vor** einem zweiten Anlauf, ob die Liste um eine weitere
   Grenze zu ergänzen ist; die Migration schätzt dann nichts.
3. Ein Kanal **mit** Massenarchivierungs-Signatur, der **nicht** in der Liste steht, ist ein Abbruch
   — das ist der Ausgang „die Abfrage findet mehr" aus 11.6; auch er wird vom Betreiber bewertet,
   nicht von der Migration aufgelöst.
4. Eine `UsageStat`-Zeile, deren Kanal eine **leere** `ActiveEmoteSetId` hat, ist ein Abbruch (unten).

Die über den Zwischenweg vorab übertragenen Emotes (12.4, rund 232) sind von der Zuordnung nicht
berührt: sie liegen mit derselben 7TV-ID in beiden Sets, ihre Zeile bleibt aktiv, ihre Historie
läuft weiter (Abschnitt 2) — nur ihre Zuordnung wechselt an der Grenze von der Hauptset- auf die
Halloween-ID, wie bei den 338, die schon vorher in beiden lagen. Der Wegwerfkanal des Zwischenwegs
steht **nicht** in der Liste: er wird vor der Migration gepurgt (12.4) und hat dann keine Zeile
mehr.

Zu Prüfung 4: trifft die Migration eine `UsageStat`-Zeile, deren Kanal eine **leere**
`ActiveEmoteSetId` hat, bricht sie ab, statt einen Leerstring zu schreiben. Plausibel ist, dass es
diesen Fall nicht gibt (Emote-Zeilen entstehen erst durch einen Sync, der die Set-ID setzt;
`RecordFailedAttemptAsync` rührt sie nie an, `SevenTvSyncService.cs:372-376`) — belegt ist es
nicht, also prüft die Migration es. Reihenfolge innerhalb der Migration: Spalte nullbar anlegen,
aus dem Join befüllen, auf `NOT NULL` ziehen, alten Index fallen lassen, neuen anlegen — die Spalte
darf nie mit einem Default gefüllt werden, weil ein Default die Prüfung aushebeln würde.

### 4.4 `ChannelEmoteSetObservation` (E) — Beobachtungsintervalle, nicht Aktivierungen

Die erste Fassung nannte die Tabelle `ChannelEmoteSetActivation` und behauptete, eine Lücke im Log
heiße „wir haben nicht hingesehen". Codex hat zu Recht eingewandt (B3), dass das Modell genau das
nicht abbildete: `LeaveAsync` (`ChannelService.cs:46-77`) setzt nur `IsBotActive = false` und hätte
die offene Zeile nicht geschlossen; die beiden Rename-Pfade (`ChannelService.cs:236-252` beim Join
auf einen umbenannten Kanal, `ChannelIdentityService.RenameAsync` `:325-349`) und der Merge
(`:361-483`) setzen `TrackingResumedAt`, schlössen aber ebenfalls nichts; und beim Wiederaufnehmen
auf dasselbe Set wäre `emoteSetSwitched` (`SevenTvSyncService.cs:83`) false — die alte Zeile bliebe
über die gesamte unbeobachtete Lücke offen und läse sich als durchgehende Aktivierung. Wurde
während der Abwesenheit gewechselt, gälte das alte Set fälschlich bis zum Rejoin. Das Modell wird
deshalb umgestellt:

**Tabelle `ChannelEmoteSetObservation(Id, ChannelId, SevenTvEmoteSetId, ObservedFromUtc,
ObservedToUtc?, ClosedBy?)`**, FK auf `Channel` mit Cascade (wie `ChannelLiveDay`,
`AppDbContext.cs:52-65`), Index `(ChannelId, ObservedFromUtc)`. Eine Zeile ist ein **Intervall, in
dem EmotePurge dieses Set für diesen Kanal als aktiv beobachtet hat** — nicht mehr, nicht weniger.

- **Invariante:** höchstens eine offene Zeile (`ObservedToUtc` null) je Kanal, **erzwungen per
  partiellem Unique-Index** `(ChannelId) WHERE ObservedToUtc IS NULL`, nicht nur per Disziplin.
- **Öffnen:** nach jedem **erfolgreichen** `SyncChannelAsync`, wenn für den Kanal keine offene Zeile
  existiert — unabhängig davon, ob die Set-ID gleich der zuletzt geschlossenen ist. Das ist die
  Regel, die B3 schließt: nach einem Rejoin auf dasselbe Set entsteht ein **neues** Intervall, die
  Lücke bleibt als Lücke sichtbar. Der Erstsync (leer → ID) öffnet das erste.
- **Schließen (`ClosedBy` nennt den Anlass):** bei einem Set-Wechsel an der Stelle, an der
  `emoteSetSwitched` berechnet wird (`SevenTvSyncService.cs:83`: alte Zeile schließen, neue öffnen,
  in derselben `SaveChangesAsync`); in `LeaveAsync`; in beiden Rename-Pfaden und im Merge (der
  Verlierer der Zusammenführung verliert seine Zeilen ohnehin über die Kaskade, der Überlebende
  schließt und öffnet neu, weil `TrackingResumedAt` dort denselben Bruch markiert,
  `ChannelIdentityService.cs:450`); der Purge kaskadiert. Der Delta-Pfad (`ApplyEmoteSetUpdateAsync`)
  schreibt nichts — er verwirft Dispatches fremder Sets ohnehin (`:157-163`), und
  `HandleUserUpdateAsync` mündet im Vollsync.
- **Nicht abgebildet:** die Downtime des Workers selbst. In ihr wird nichts gezählt, aber auch nichts
  falsch zugeordnet; „seit wann wir zählen" ist trotzdem um sie zu ungenau. Das Log ist ein
  Kanal-Log, keine Betriebsuhr — für Letzteres steht `WorkerStats`/`/api/health`. Bewusst so, weil
  ein Log, das jeden Worker-Neustart schreibt, bei jedem Deploy alle Kanäle auf einmal anfasst.

**Die Migration** sät die Intervalle aus **derselben Eingabe wie 4.3**, sonst widersprächen
Zähler und Preset einander (Codex D2 — die sechste Fassung säte für jeden Kanal ein einziges offenes
Intervall des heutigen Sets und hätte für HandOfBlood behauptet, Halloween sei seit Tracking-Beginn
beobachtet worden, während die Zähler die frühere Phase dem Hauptset zuschreiben). Je Kanal mit
nicht-leerer `ActiveEmoteSetId` und `IsBotActive = true`: steht er **nicht** in der Zuordnungsliste,
eine offene Zeile fürs heutige Set ab `TrackingResumedAt ?? CreatedAt`; steht er darin, je
Intervall der Liste eine Zeile — geschlossen mit `ObservedToUtc` = Grenze und `ClosedBy =
"migration"` für jede Phase vor der letzten Grenze, offen für die letzte —, die erste ab
`TrackingResumedAt ?? CreatedAt`. Für inaktive Kanäle (`IsBotActive = false`) entsteht keine offene
Zeile — sie werden gerade nicht beobachtet — und auch keine geschlossene, weil das Ende der
Beobachtung nirgends steht (`LeaveAsync` schreibt kein Datum; der Audit-Eintrag `channel.leave`
hätte es, aber eine Migration, die aus dem Audit-Log rekonstruiert, ist genau die Sorte Raten, die
dieses Papier vermeidet). Der Wegwerfkanal aus 12.4 ist zu diesem Zeitpunkt gepurgt und bekommt
nichts.

**Ein Vorbehalt bleibt und gehört in den Vertrag:** Der Zeitstempel ist **„wann wir es bemerkt
haben"**, nicht „wann es geschah". Über `user.*` (`SevenTvEventClient.cs:407-440`) fast sofort; im
reinen REST-Fall bis zu ein Resync-Intervall (`SevenTv:ResyncIntervalSeconds`, 60 s) später; und
während einer #76-Blockade (`TryGuardAgainstImplausibleWipeAsync`, `:351-370`, läuft **vor** `:83`)
bleibt die alte Zeile offen, obwohl 7TV längst gewechselt hat. Dazu 7TVs eigener REST-Cache-Lag
(10–30 min, SevenTV/SevenTV#81). Die Regel „eine fehlende Zeile heißt keine Daten" ist dieselbe wie
bei `ChannelLiveDay` (`ChannelLiveDay.cs:8-9`, DECISIONS 2026-08-03 und 2026-08-08 Punkt 3); der Brief
nannte hier einen Eintrag vom 2026-09-04; einen solchen gibt es nicht.

**Zweck ausdrücklich begrenzt:** das Log dient dem Datumsbereichs-Preset „während dieses Set
beobachtet wurde" (6.3), der Tatsachenangabe in der Set-Ansicht (6.2) und einer späteren
Backfill-Automatik (5.3) — mit der Regel, dass ein Intervall per Konstruktion **nie** eine
Beobachtungslücke überspannt und ein automatischer Backfill deshalb nur innerhalb eines Intervalls
eine Set-ID vorschlagen darf, nie über dessen Rand hinaus. Es dient **nicht** der Zuordnung der
Zahlen — die trägt `UsageStat.EmoteSetId` direkt, im Zählmoment, und ist damit genauer als jede
nachträgliche Ableitung aus dem Log.

### 4.5 `VoteSession.EmoteSetId` (L) und Snapshot-Spalten auf `VoteSessionEmote`

Nullbare Spalte `VoteSession.EmoteSetId`; null = heutiges Verhalten (aktives Set, dynamisch oder
fester Wahlzettel). Dazu — Folge von B4 — zwei nullbare Snapshot-Spalten auf `VoteSessionEmote`
(`NameAtCreation`, `ImageUrlAtCreation`), damit ein Wahlzettel seine Anzeigedaten nicht aus
`Emote.Name` bezieht, das jeder Sync überschreibt (`SevenTvSyncService.cs:515-517`). Details und
die Falle in Abschnitt 8.

### 4.6 Audit ohne Schemaänderung (K)

`AuditLogEntry` hat bereits `TargetType`/`TargetId` als Zeiger („z. B. `("voteSession", "42")`",
`AuditLogEntry.cs:66-68`) und `DetailsJson` als freies jsonb (`:70-73`), und `ChannelName` ist
ausdrücklich nullbar für Aktionen ohne Kanalbezug (`:63-64`). Das Ziel-Set passt dort hinein, und
ein Eintrag ohne getrackten Kanal ist im Modell bereits vorgesehen; Abschnitt 7.4.

---

## 5. Zählen

### 5.1 Befund: die Set-ID kommt im Schreibpfad heute nicht an — belegt

Der Brief fragte, ob die Set-ID „sauber ankommt". Sie kommt gar nicht vor:

- `IEmoteMatchCache` (`IEmoteMatchCache.cs:5-9`) hält je Kanal ein Wörterbuch Name → `Emote.Id`,
  gefüllt aus `RefreshMatchCacheAsync` (`SevenTvSyncService.cs:403-437`), das nur `Name` und `Id`
  projiziert (`:405-408`).
- `TwitchChatManager` (`:1024`) liest dieses Wörterbuch, matcht und ruft `usageCounter.Increment(
  emoteId, category)` (`:1054`); `IEmoteUsageCounter.Increment(string emoteId, UsageCategory)`
  (`IEmoteUsageCounter.cs:7`) puffert in einem `ConcurrentDictionary<string, EmoteUsageCounts>`
  (`EmoteUsageCounter.cs:8`).
- `UsageFlushWorker` reicht das Wörterbuch an `IUsageStatFlushService.FlushAsync(IReadOnlyDictionary<
  string, EmoteUsageCounts>)` (`IUsageStatFlushService.cs:39`), das nach `Emote.Id` validiert
  (`UsageStatFlushService.cs:29-32`) und mit Conflict-Target `(EmoteId, Date)` schreibt (`:71`).

Also: **kein Feld, kein Parameter, kein Schlüsselbestandteil trägt ein Set.** Die Zuordnung „an das
im Zählmoment beobachtete Set" muss eingeführt werden — das ist die eigentliche Backend-Arbeit
dieses Konzepts.

### 5.2 Vertrag: das lokal beobachtete Set reist mit dem Match-Cache

**Gewählt (plausibel, aus Belegtem abgeleitet):** Der Match-Cache trägt je Kanal neben dem Wörterbuch
die **Set-ID, für die es gilt**, und den Zeitpunkt, zu dem diese Generation gebaut wurde.
`RefreshMatchCacheAsync` läuft nach `SaveChangesAsync` in derselben Methode, die `ActiveEmoteSetId`
schreibt (`SevenTvSyncService.cs:86,108-109`), und auch der Warmstart aus Postgres
(`WarmMatchCacheIfEmptyAsync`, `:280-296`) hat die Zeile mit der Set-ID in der Hand — Cache und
Set-ID können nicht auseinanderlaufen. Der Chat-Pfad nimmt beides aus **einem** Snapshot und zählt
unter dem zusammengesetzten Schlüssel `(Emote.Id, SetId)`; der Zähler hält diesen Schlüssel bis zum
Flush; der Flush schreibt ihn in die neue Spalte. Verträge:

- `IEmoteMatchCache.ReplaceChannel(channelName, emoteSetId, nameToId)`; der Lesezugriff liefert
  Wörterbuch, Set-ID **und** Generationszeitpunkt als ein Objekt, nicht als getrennte Aufrufe
  (getrennte Aufrufe könnten einen Wechsel dazwischen sehen).
- `IEmoteUsageCounter.Increment(emoteId, emoteSetId, category)`, `Merge`/`DrainAndReset` über den
  zusammengesetzten Schlüssel; `PendingEmoteCount` zählt weiterhin Schlüssel.
- `IUsageStatFlushService.FlushAsync` nimmt den zusammengesetzten Schlüssel; Validierung weiter über
  `Emote.Id`; `UNNEST` mit viertem Array; Conflict-Target dreispaltig.
- Harness (#69): `ReplayDayCounter` zählt heute ohne Set und schreibt nichts
  (`HarnessRunner.cs:13-16`); er bekommt die Set-ID als Eingabe (5.3), damit sein Vergleich gegen die
  Live-Zeilen weiter Zeile für Zeile passt.

**Was das Feld bedeutet — und was nicht (B8).** `EmoteSetId` ist das **lokal beobachtete** Set: das,
für das der Match-Cache im Moment des Matches gebaut war. Nach einem echten Wechsel auf 7TV bleibt
die alte Generation maßgeblich, bis das `user.*`-Event oder der Vollsync sie ersetzt — im Regelfall
Sekunden, im REST-Fall bis 60 s, bei 7TVs REST-Cache-Lag 10–30 min, während einer #76-Blockade
unbefristet (4.4). Nutzung eines Emotes, das in beiden Sets liegt, wird in diesem Fenster auf das
alte Set gebucht. Ein Wechsel innerhalb eines 30-s-Flush-Fensters erzeugt zwei Schlüssel und zwei
Zeilen — getrennt am **Cache-Tausch**, nicht notwendig am echten Wechsel. Nichts geht verloren,
nichts wird doppelt gebucht; die Grenze zwischen den beiden Zeilen ist um die Beobachtungsverzögerung
unscharf. Das ist die genaueste Zuordnung, die ohne Zeitstempel je Nachricht möglich ist, und sie
wird so benannt: in der Oberfläche heißt das Preset „während dieses Set **beobachtet** wurde", nicht
„aktiv war" (6.3), und die Log-Zeile beim Cache-Tausch nennt den Generationszeitpunkt der alten und
der neuen Generation, damit die Unschärfe im Betrieb messbar ist statt nur behauptet.

**Bestätigung am Namensbefund (11.2 „Namen gegen IDs") — belegt.** Die 192 gleichnamigen
Saisonvarianten mit anderer 7TV-ID brauchen die neue Spalte nicht, um getrennt gezählt zu werden;
sie sind es heute schon. Der Chat trägt den **Namen**, und `EmoteMatchCache` hält je Kanal ein
Wörterbuch Name → `Emote.Id` (`EmoteMatchCache.cs:11-20`), das `RefreshMatchCacheAsync` allein aus
den **nicht archivierten** Zeilen des Kanals baut (`SevenTvSyncService.cs:405-408`, `!e.IsArchived`)
— derselbe Sync, der beim Set-Wechsel die abgegangenen Zeilen archiviert und die neuen anlegt oder
reaktiviert (`ReconcileAsync`/`UpsertEmote`, Abschnitt 2) und den Cache danach in derselben Methode
ersetzt (`:108-109`). Läuft Halloween, steht unter `Stare` die Halloween-Zeile im Wörterbuch; läuft
das Hauptset, die andere — die jeweils andere Zeile ist archiviert und kommt gar nicht erst hinein.
Für diese 192 Paare passiert die Trennung nach Set also von selbst, Zeile für Zeile, ganz ohne
`UsageStat.EmoteSetId`. Das ist eine **Bestätigung** von 5.2, keine Einschränkung: die Spalte
trennt die 338 Emotes, die als dieselbe ID in beiden Sets liegen (Abschnitt 3), und für die 192
Namensvettern macht sie den Set-Bezug explizit, den ihre Zeilen ohnehin implizit tragen. Die
Unschärfe an der Wechselgrenze (B8, oben) ist für beide Gruppen dieselbe und dasselbe Fenster — sie
zeigt sich nur anders: bei den 338 als Buchung auf die alte Set-ID **derselben** Zeile, bei den 192
als Buchung auf die **alte Zeile**, bis der Sync den Wechsel bemerkt und das Wörterbuch tauscht.
Die Wechsel-Tests (unten) nehmen deshalb ein gleichnamiges Paar mit auf: vor dem Tausch zählt
`Stare` auf Zeile A, danach auf Zeile B, nie auf beide.

**Alias und Zählung — enger als im Review formuliert (siebte Fassung, Codex D5).** Codex hat
eingewandt, das Zusammenfallen mehrerer Aliase auf ein `Emote.Name` mache die Chat-Zählung für Sets
unvollständig. Am Code ist das auf einen Fall begrenzt, und der ist alt. Gezählt wird ausschließlich
das **aktive** Set — der Match-Cache wird allein aus den nicht archivierten Zeilen gebaut
(`SevenTvSyncService.cs:403-408`), und `UpsertEmote` schreibt in `Emote.Name` genau den Alias, den
7TV für das aktive Set liefert (`:515-517`). Ein Emote, das im Hauptset `Stare` und im Halloween-Set
`StareHalloween` heißt (eine der rund zehn Alias-Abweichungen aus 11.2), trägt bei uns also immer
den Alias der gerade aktiven Phase — und das ist der, den der Chat tippt. Für **nicht-aktive** Sets
wird nichts gematcht, nichts gezählt und nichts gebraucht: ihre Einträge samt Alias kommen bei Bedarf
live von 7TV (Entscheidung C, 6.2), der Alias reist mit der Abfrage mit und muss nicht persistiert
werden. Was bleibt, ist der **#74-Fall innerhalb eines Sets**: liegt dieselbe 7TV-ID zweimal im
aktiven Set unter zwei Aliasen, ruft `ReconcileAsync` `UpsertEmote` je Eintrag auf (`:449-452`), der
spätere Alias überschreibt den früheren (`:515-517`), und der Cache hält je Zeile einen Namen —
Chat-Nutzung unter dem anderen Alias wird heute **nicht** gezählt. Das gilt seit es #74 gibt, hängt
nicht an diesem Konzept und wird von ihm weder verschlechtert noch behoben; es steht jetzt als
Tatsache in 11.5, damit niemand die Set-Zähler für vollständiger hält, als das aktive Set es heute
ist.

Eigenschaften, die der Schlüssel gratis mitbringt: Das Zurückstellen fehlgeschlagener Batches
(`UsageFlushWorker.cs:15-19,88-91`) bleibt korrekt, weil der Schlüssel das Set konserviert. Tests:
`EmoteUsageCounter` und der neue Snapshot-Typ im container-freien `Worker.Tests` (Regel 11); Flush
und Query in `Infrastructure.Tests/Integration`; **Wechsel-Tests** für Nachrichten vor, während und
nach dem Cache-Tausch — die dritte Gruppe muss auf dem neuen Set landen, die erste auf dem alten,
und für die mittlere ist genau der Cache-Tausch die Grenze, nichts anderes.

**Verworfen:** die Set-ID erst im Flush aus `Channel.ActiveEmoteSetId` nachschlagen. Das wäre das
Set zum Flush-Zeitpunkt, nicht zum Zählzeitpunkt — bis zu 30 s Verschiebung, bei zurückgestellten
Batches bis zu 2,5 min, und genau in dem Moment, in dem es zählt (dem Wechsel), systematisch falsch.

### 5.3 Chat-Log-Backfill (#69) bekommt die Ziel-Set-ID als Parameter (F)

Der heutige Harness schreibt nichts; ein persistierender Backfill existiert noch nicht
(`docs/designs/Chat-Log-Backfill-69-2026-09-05.md`, Wedge A vs. B). Wenn er kommt, gilt: **die
Set-ID ist ein Pflichtparameter des Aufrufs.** Was die Maschine nicht rekonstruieren kann — welches
Set an einem vergangenen Tag aktiv war —, verantwortet der Aufrufer. Bekannte Einschränkung, die in
den Vertrag gehört: **wurde innerhalb des Backfill-Zeitraums gewechselt, muss der Lauf geteilt
werden** — je Set-Phase ein Lauf mit eigener Set-ID; sonst leise falsche Zahlen. Das Beobachtungs-Log
(4.4) kann diese Teilung später vorschlagen, sobald es Intervalle kennt — und nur **innerhalb** eines
Intervalls, nie über eine Lücke hinweg; für Zeiträume vor seiner Einführung kann es das nie.

---

## 6. Anzeigen

### 6.1 Set-Dropdown auf der Nutzungsseite (G)

Nach dem Muster von `shared/datetime/date-range-menu.ts` (`Popover` + `role="radiogroup"`,
`:105-130`): ein Auslöser in der Kopfzeile, ein Popover mit den Sets des Kanals, das aktive
vorausgewählt und als aktiv beschriftet. Die Set-Liste kommt aus einem neuen Endpunkt
`GET /api/channels/{name}/emote-sets` (Id, Name, Kapazität, `isActive`), hinter denselben Filtern
wie `/emotes` (`EmoteEndpoints.cs:24-28`); Quelle ist 7TV, und seit 11.1 gemessen: die v4-Abfrage
`userByConnection(platform: TWITCH, platformId: <Twitch-ID des Kanals>) { emoteSets { id name
capacity owner … } }` liefert **ohne Anmeldung in einem Request** alles, was das Dropdown braucht;
`isActive` ist der Vergleich mit `Channel.ActiveEmoteSetId`. **Persönliche Sets** (v3 `flags: 4`,
Kapazität 5 — 11.1) werden gekennzeichnet oder ausgelassen; sie sind keine Kanal-Sets. **Denselben
Endpunkt liest der Ziel-Set-Picker** (7.3, 7.5 Baustein 1) — ein Endpunkt, zwei Leser, kein zweiter
Bau. Kein weiteres Dauer-Control: die Wahl eines Sets ist eine *Sicht*, wie der
Zeitraum — dieselbe Einordnung wie im Konzept vom 2026-09-18 (Zeitraum = Sicht, Auswahl = Zustand).
Folge: ein Set-Wechsel im Dropdown ist **kein** Kontextwechsel für `ListSelection`; er läuft über
`retainAmong(payload)` mit der bestehenden #94-Meldung, weil sich die Grundmenge ändert.

**Kein Eintrag „Gesamt (alle Sets)".** Vom Betreiber ausdrücklich verworfen: jede Set-Ansicht
filtert schlicht auf ihre Set-ID. Da nach 4.3 jede Bestandszeile ein Set trägt, gibt es keine
Zeile, die keiner Ansicht zugeordnet wäre.

`CapturedExportScope` (`usage-stats-page.ts:157-165`) trägt schon `emoteSetId`, heute aus
`activeEmoteSetId()` (`:1353`). Es trägt künftig das **gewählte** Set, sinngemäß für alles, was
einen Dialog öffnet: Export, Übertragen, Löschen, „Zur Abstimmung stellen". Ein Set-Wechsel bei
offenem Dialog verfälscht dann nichts. Nebeneffekt, gewollt: das Purge-Protokoll trägt
`meta.emoteSetId` (`purge-run-export.ts:23`), `file-import-step.ts:137-140` prüft eine hochgeladene
Datei gegen `setId` — ein Halloween-Protokoll wird damit in das Halloween-Set zurückgespielt, nicht
in das aktive.

**Der Nutzungs-Export bekommt das Set und die `null`-Zahl (siebte Fassung, Codex D9).** Heute
reicht `buildUsageExportPurposeDownload` die `emoteSetId` des Scopes nur an die Emote-Liste weiter;
für `usage-csv`/`usage-json` baut es den `UsageExportInput` ohne sie (`usage-export-purposes.ts:93-
101`), der Dateiname trägt Kanal und Zeitraum (`usage-export.ts:45-47`) und `UsageExportMeta` weder
Set-ID noch Setnamen (`:37-43`). Sobald die Zahlen set-spezifisch sind, wäre ein solcher Export
mehrdeutig. Vertrag: Dateiname und Metadaten nennen **Set-ID und Setnamen** des exportierten Sets
(`emoteSetId`, `emoteSetName` in `meta`; ein kurzes Set-Kennzeichen im Dateinamen, wie es die
Emote-Liste über `sourceEmoteSetId` schon hält, `emote-list-export.ts:36`). Und weil
`TotalUseCount`/Vorfenster nach 6.2 `null` sein können, werden `UsageExportRow.totalUseCount` und
`previousWindowUseCount` `number | null` (`usage-export.ts:29-30` sind heute Pflicht-Zahlen): in
der CSV eine **leere Zelle** — genau so rendert `encodeCell` bereits `null` für `lastUsedDate`
(`csv.ts:35-37`) —, im JSON `null`, und `trend` für solche Zeilen `unknown`, das die Envelope
ohnehin als „bewusst nicht ausgesagt" definiert (`:33`). Keine 0, keine Auslassung der Zeile: der
Export sagt dasselbe wie die Seite.

### 6.2 Was die Seite für ein Set zeigt

**Aktives Set:** wie heute, nur dass `TotalUseCount`, Vorfenster und `LastUsedDate` auf
`EmoteSetId = <aktiv>` gefiltert sind. Der Set-Filter sitzt in der Ein-Tabellen-Abfrage über
`UsageStats` (`UsageStatQueryService.cs:74-84`), **nach** der Reduktion auf die ID-Liste (`:44-53`) —
kein Navigations-Join vor dem `GroupBy`, Regel 10 bleibt unberührt. Gleiches für
`GetDailySeriesAsync` (`:106`), `GetChannelSeriesAsync` (`:173`, `/series` bekommt den Parameter —
und seit der siebten Fassung eine andere Zeilenidentität, 6.5 Vertrag 2; das per Test fixierte
Wire-Format ändert sich damit, und der Test mit ihm) und `GetTotalsByEmoteIdsAsync` (`:238`,
Voting-Ergebnisse).

**Nicht-aktives Set — Mitgliedschaft ist eine Vereinigung, keine Live-Liste (B4).** Die erste
Fassung startete von der heutigen Live-Mitgliedschaft und hätte damit jedes Emote verloren, das
nach seiner Zählphase aus dem Set entfernt wurde — samt seinen `UsageStat`-Zeilen unter dieser
Set-ID, die Summe und Pareto-Nenner dann still verkürzt hätten. Die Grundmenge der Ansicht ist
deshalb:

> **(Live-Mitglieder des Sets von 7TV) ∪ (Emote-Zeilen des Kanals, die mindestens eine
> `UsageStat`-Zeile unter dieser Set-ID tragen)**

Die zweite Menge kommt aus unserer Datenbank: erst die ID-Liste des Kanals (Regel 10), dann
`UsageStats` mit `EmoteSetId = <gewählt>` darüber — das Präfix `(EmoteId, EmoteSetId)` des neuen
Index trägt das. Für das **aktive** Set ist die Vereinigung identisch mit der heutigen
`!IsArchived`-Liste plus archivierten Zeilen mit Zählungen unter dieser ID — Letztere zeigt die
Seite heute nicht (`UsageStatQueryService.cs:43`), und das bleibt so: für das aktive Set gilt weiter
„archiviert = nicht im Raster", weil dort das Löschen die Hauptaufgabe ist und ein archiviertes
Emote nicht gelöscht werden kann. Für ein **nicht-aktives** Set gibt es diesen Kurzschluss nicht:
Zeilen sind dort fast alle archiviert, und Mitgliedschaft heißt Live-Liste ∪ Zählhistorie. Klassen:

| Mitglied des gewählten Sets … | Zeile bei uns | Zahlen | Kennzeichnung |
|---|---|---|---|
| … ist heute im Set und war im Kanal schon aktiv | ja | aus `UsageStat` mit `EmoteSetId = <gewählt>` | — |
| … ist heute im Set, war im Kanal aber nie aktiv | nein | **keine** — `TotalUseCount` ist `null`, nicht 0 | „nie im aktiven Set gezählt" |
| … ist heute **nicht mehr** im Set, hat aber Zählungen unter dieser Set-ID | ja | aus `UsageStat` mit `EmoteSetId = <gewählt>` | „nicht mehr im Set" — dasselbe Badge-Idiom wie `archivedBadge` auf Wahlzetteln; nicht wählbar zum Löschen (es ist nicht da), zählt aber in Summe und Pareto-Nenner |
| … liegt doppelt im Set (zwei Aliase, eine Emote-ID — #74; am 2026-09-19 in **allen** untersuchten Sets gemessen, u. a. HandOfBloods **aktivem** Set mit 762 Einträgen bei 760 eindeutigen IDs) | eine | einmal | **nicht** wegdedupliziert: die Zelle trägt eine Slot-Zahl (2), die Slot-Projektion zählt beide Einträge, und beide Aliase stehen an der Zelle. Ob ein `REMOVE` per Emote-ID in 7TV einen oder beide Einträge entfernt, ist **offen** (11.5) — bis dahin blendet die Lösch-Vorprüfung Duplikat-Zellen mit einem Hinweis aus, statt es zu raten |
| … trägt einen Namen, unter dem im **anderen** Set des Kanals ein **anderes** Emote (andere 7TV-ID) liegt — bei HandOfBlood 192 der 687 Halloween-Einträge (11.2 „Namen gegen IDs") | eine je Set, also zwei im Kanal | jede Zeile nur ihre eigenen — **nicht** zusammengerechnet | zurückhaltende Kennzeichnung an der Zelle: „unter diesem Namen liegt in ‚<Set>' ein anderes Emote" (unten); das Duplikat-Banner greift hier **nicht** |

`EmoteUsageContextDto.TotalUseCount` wird dafür nullbar — dasselbe Mittel wie `TotalUseCount`
nullable im Voting (DECISIONS 2026-08-01). Sortierung, Bänder und Summenzeile behandeln `null` als
„unbekannt", nicht als „0": nicht im Pareto-Band, nicht in der Summe, in einer eigenen Gruppe am
Ende. Für ein Set, das nie aktiv war, ist die Seite damit ehrlich leer an Zahlen und trotzdem
benutzbar zum Übertragen — **und zum Löschen erst mit dem Identitätsvertrag aus 6.5.**

*Korrektur der sechsten Fassung (Codex C1):* Dieser Absatz schloss bis zur fünften Fassung mit
„benutzbar zum Löschen und Übertragen", als folgte das aus der nullbaren Zahl. Es folgt nicht. Für
Übertragen stimmt es — `toImportRow` (`usage-export-purposes.ts:45-48`) kennt nur `sevenTvEmoteId`
und Namen. Für Löschen stimmt es nur bis zur 7TV-Mutation; Auswahl, Rückmeldung, Protokoll und
Wiederherstellung hängen an `Emote.Id`, die eine Zeile der zweiten Tabellenklasse nicht hat (6.5,
Messung). Zwei weitere Dinge an der Tabelle sind nachzuziehen: **`EmoteId` wird im DTO ebenfalls
nullbar** (Klasse 3 hat keine), und die Spalte „Zeile bei uns" ist eine Beschreibung des heutigen
Bestands, **kein Klassenkriterium** — sobald Abschnitt 8 beim Anlegen einer Set-Abstimmung archivierte
Zeilen für nie aktive Mitglieder anlegt, gibt es Zeilen *mit* Guid und *ohne* Historie (6.5, Klasse
2b). Die Kennzeichnung „nie im aktiven Set gezählt" muss deshalb aus der Historie kommen — keine
`UsageStat`-Zeile unter irgendeiner Set-ID, Beobachtungs-Log ohne Intervall —, nicht aus dem Fehlen
der Zeile.

**Gleicher Name, andere ID, anderes Set — der Fall, den die dritte Fassung nicht kannte (11.2).**
Ein Halloween-`Stare` und das Hauptset-`Stare` sind bei uns zwei Zeilen mit getrennten Historien;
wer im Juni die Halloween-Ansicht öffnet, sieht unter `Stare` nur die Oktober-Zahlen der
Halloween-Zeile, und in der Hauptset-Ansicht unter demselben Namen nur die der anderen. Das ist
wahr, aber erklärungsbedürftig, und bei HandOfBlood kein Randfall: 192 der 687 Halloween-Einträge
(28 %) und 192 der 762 Hauptset-Einträge (25 %) haben so einen Namensvetter. Der #74-Fall in der
Tabelle ist etwas anderes — dort zwei Aliase auf **eine** ID innerhalb **eines** Sets, hier ein Name
auf **zwei** IDs in **zwei** Sets. Das bestehende Duplikat-Banner greift hier **nicht**, und das ist
am Code eindeutig: `FindDuplicateNamesAsync` gruppiert nur die nicht archivierten Zeilen des Kanals
nach Namen (`EmoteSetStatusService.cs:66-71`, `!e.IsArchived`), sieht also ausschließlich das
aktive Set; die Namensvettern des anderen Sets sind archiviert (oder, für ein nie aktives Set, gar
nicht vorhanden) und fallen aus der Gruppe heraus. Der Match-Cache meldet aus demselben Grund
nichts (`SevenTvSyncService.cs:405-408`) — und soll es auch nicht, denn für ihn ist der Name ja
eindeutig.

Vorgeschlagen — **zurückhaltend**: eine Kennzeichnung an der Zelle, nicht mehr. Die Set-Ansicht
kennt je Zeile Namen und 7TV-ID; ein zweiter Blick in dieselbe Kanal-Zeilenmenge („gibt es eine
andere Zeile dieses Kanals mit gleichem Namen, anderer `SevenTvEmoteId` und mindestens einer
`UsageStat`-Zeile unter einer anderen Set-ID") liefert den Namensvetter samt dem Set, unter dem er
gezählt wurde — ohne 7TV-Request, ordinal wie das Chat-Matching (`EmoteNameMatching.cs:89`). Die
Zelle bekommt dafür ein kleines Merkmal mit Tooltip („unter diesem Namen liegt in ‚Halloween Set' ein
anderes Emote"), in beiden Richtungen — auch in der Hauptset-Ansicht, wenn im Saison-Set ein
Namensvetter Zahlen hat. Kein Banner, kein Zähler, kein neuer Filter: dieselbe Zurückhaltung wie
beim Duplikat-Banner, das nach #45 bewusst in den Kanalstatus gefaltet wurde statt ein eigenes
Control zu werden. Ob `Emote.Name` als Vergleichsbasis reicht, obwohl der Sync es überschreibt
(`:515-517`), ist plausibel, nicht belegt: eine archivierte Zeile behält den Namen ihres letzten
Syncs, also den Alias, unter dem sie zuletzt aktiv war — für die Frage „gleicher Chat-Name" genau
der richtige.

**Ausdrücklich nicht:** die beiden Historien zusammenrechnen — weder in der Zelle noch in Summe,
Bändern oder Export. Erstens wäre es ein Rückweg zu der Summe über Set-Phasen, die dieses Vorhaben
gerade auflöst (strikte Trennung, Abschnitt 9). Zweitens ist die Vorfrage nicht technisch, sondern
produktseitig: **ob ein Zuschauer das Halloween-`Stare` und das Hauptset-`Stare` als dasselbe Emote
empfindet, entscheidet dieser Entwurf nicht.** Für ein `5Head` mit Kürbis mag es so sein; für einen
Namen, der in der Saison auf ein ganz anderes Bild zeigt, sicher nicht — und die Daten geben den
Unterschied nicht her. Die Frage bleibt **offen** und ist hiermit benannt; bis sie entschieden ist,
zeigt die Kennzeichnung, dass es den Namensvetter gibt, und die Zahlen bleiben, was sie sind.

**Tatsachenangabe für nie beobachtete Sets.** Öffnet jemand ein Set, für das das Beobachtungs-Log
(4.4) im gewählten Zeitraum **kein** Intervall kennt, tragen alle Mitglieder mit Zeile die 0 (die
Abfrage füllt für aktive Zeilen mit 0 auf, `UsageStatQueryService.cs:89-91`, und für dieses Set
gibt es schlicht keine Zeilen). Die Seite sagt dann in der Zeile, in der heute „gezählt seit …"
steht (`usageStats.trackedSince`, `usage-stats-page.html:320-322`), stattdessen: **„Dieses Set war
im gewählten Zeitraum nicht aktiv; für dieses Set liegen keine Zählungen vor."** Ein Satz, keine
Interaktion, keine Schwelle, kein Zurückhalten von Bändern oder Zählern. Die Begründung ist nicht
ein möglicher Nutzerfehler — die Mods eines Kanals wissen, welches Set aktiv war —, sondern dieselbe
Zurückhaltung, die die Seite an der Live-Zeile schon übt: `ChannelLiveDay.cs:8-9` verbietet, eine
fehlende Zeile als „offline" zu lesen, und die Live-Zeile schweigt bei null Live-Tagen, statt „0"
zu sagen (`liveDaysInRangeKey`, `usage-stats-page.ts:728-731`; DECISIONS 2026-08-03 und 2026-08-08
Punkt 3). Hier heißt die fehlende Zeile „nicht beobachtet", und die Seite sagt das, statt die 0
unkommentiert stehen zu lassen. Kennt das Log ein Intervall, das **innerhalb** des gewählten
Zeitraums beginnt, steht dort wie heute „gezählt seit <ObservedFromUtc>" — das ist das bestehende
`rangeStartsBeforeTracking`-Idiom (`usage-stats-page.ts:380-400`), nur mit dem Intervallbeginn
des Sets statt `trackedSince` des Kanals.

Kapazität und Slot-Projektion (`slot-projection.ts`, `projectedSlots()` im Dock) lesen heute
`Channel.ActiveEmoteSetCapacity` (`Channel.cs:10-14`); für ein anderes Set kommt die Kapazität aus
dem Live-Abruf, den die Preview-Abfrage heute **nicht** mitholt (`SevenTvApiClient.cs:67-68` fragt
`totalCount`, `pageCount`, Items) — das Set-Objekt selbst (`capacity`, `name`, und für 7.3 der
Besitzer) muss dazu.

### 6.3 Datumsbereichs-Preset aus dem Beobachtungs-Log

Ein zusätzlicher Eintrag im `date-range-menu`: „während dieses Set beobachtet wurde" — Beginn des
jüngsten Intervalls des gewählten Sets aus `ChannelEmoteSetObservation`, Ende `ObservedToUtc` oder
heute. Nur angeboten, wenn ein Intervall existiert. Ein offenes Intervall, das mit
`TrackingResumedAt ?? CreatedAt` gesät wurde (4.4), ist dabei nicht von einem gemessenen
unterscheidbar — und das ist auch nicht nötig, weil die Zahlen ihre Zuordnung nicht aus dem Log
beziehen. Gibt es mehrere Intervalle (Set war zweimal aktiv), nimmt das Preset das jüngste; die
älteren sind über den freien Zeitraum erreichbar, ein Preset je Intervall wäre ein Control zu viel.

### 6.4 Kosten des Bedarfsabrufs — belegte Konstanten, offene Frequenz

`GetEmoteSetPreviewAsync` liest `SetEntriesPerPage = 500` Einträge je Seite, höchstens
`MaxSetEntryPages = 10` (`SevenTvApiClient.cs:46-47`), und **belastet das providerweite Budget je
Seite** (`ForeignEmoteSetService.cs:30-33`); das Budget ist `MaxRequestsPerWindow = 60` pro Minute
bei `MaxConcurrent = 2` (`ForeignEmoteSetProviderBudget.cs:77-83`), geteilt mit der
Fremdkanal-Vorschau. Ein ~900er-Set (HandOfBlood: 933 gemessen am 2026-09-01) sind zwei Seiten,
also **zwei Permits je Abruf**. Der `ForeignEmoteSetCache` hält 60 s und ist nach Kanalname
geschlüsselt (`ForeignEmoteSetCache.cs:25,63`).

Ohne Caching wäre das ein Problem, nicht wegen des Umschaltens, sondern wegen der **Reloads**: die
Nutzungsseite lädt bei jedem `usage.flushed` (alle 30 s bei Chat-Betrieb) und `channel.synced` neu.
Würde jeder Reload die Mitgliederliste neu holen, kostete ein Betrachter eines 900er-Sets 4 Permits
pro Minute; zehn Betrachter fräßen zwei Drittel des Budgets. **Deshalb:** ein Lesepfad nach Set-ID
hinter demselben Härtungs-Dekorator (Cache, Coalescing, Breaker, Budget — DECISIONS 2026-09-09
„Fremdset-Vorschau wird gehärtet"), Cache-Schlüssel **Set-ID** statt Kanalname, TTL zunächst die
bestehenden 60 s; die Seite holt die Mitgliederliste beim Wählen des Sets und bei einem lauten
Reload, **nicht** bei stillen Reloads — die aktualisieren nur die Zahlen, und die kommen aus unserer
Datenbank. Ob 60 s für eigene, selten geänderte Sets nicht zu kurz sind (Argument aus M: ein
inaktives Set ändert sich selten), ist **offen** und wird gemessen, nicht geraten.

### 6.5 Zeilenidentität in der Set-Ansicht — der Vertrag (sechste Fassung, Codex C1)

**Ausgangspunkt.** Die Set-Ansicht (6.2) ist eine Vereinigung aus Live-Liste und Zählhistorie. Ein
Live-Mitglied, das in diesem Kanal nie aktiv war, hat bei uns keine `Emote`-Zeile und damit keine
`Emote.Id`. Codex' Befund: die Pfade, die das Dokument für „fast unverändert" erklärt hat, verlangen
genau diese Id. Der Befund ist am Code geprüft; das Ergebnis steht zuerst, der Vertrag danach.

**Wie viele Zeilen das betrifft — kein Randfall, sondern der Anlass.** Plausibel aus 11.2: von
HandOfBloods 686 Halloween-IDs liegen 338 als dieselbe ID auch im Hauptset und sind damit Zeilen des
Kanals; die übrigen **348 (51 %)** — darunter die 192 Namensvettern mit eigener ID — waren seit
Tracking-Beginn nie im aktiven Set und haben **keine Zeile**. Die Halloween-Ansicht im September wäre
zur Hälfte Klasse 3; nach der Vorbefüllung durch das Mod-Team (12.4) noch mehr, weil jeder neu
eingefügte Eintrag, der nicht schon im Hauptset lag, ebenfalls ohne Zeile ist. Wer diese Ansicht zum
Löschen nutzen soll, löscht überwiegend Zeilen ohne Guid.

**Messung: woran `Emote.Id` hängt, woran nicht.** Jede Stelle mit dem Befund für eine Zeile ohne Guid:

| Stelle | Identität heute | Ohne `Emote.Id` |
|---|---|---|
| 7TV-Mutationen: `REMOVE_OPERATION` (`seven-tv-delete.service.ts:43-46`), `ADD_OPERATION` (`seven-tv-restore.service.ts:36-39`), Import (`seven-tv-import.service.ts:127`); Engine `start(setId, …)` (`seven-tv-run-engine.ts:239-244`) | `setId` + `emote.sevenTvEmoteId` | **Trägt.** Die Guid kommt in keiner Mutation vor — das ist der wahre Kern von „Engine und Mutation brauchen nichts Neues" |
| Übertragen (`openImportTarget` → `toImportRow`, `usage-export-purposes.ts:45-48`); Export (`usage-export.ts:28,52,77`, Spalte `seven_tv_emote_id`) | `sevenTvEmoteId` + Name | **Trägt.** Beide Pfade sind schon 7TV-identifiziert |
| Auswahlschlüssel: `new ListSelection(atlasOrder, (emote) => emote.emoteId, emotes)` (`usage-stats-page.ts:548-552`) | `Emote.Id` | **Bricht.** Alle Guid-losen Zeilen fielen auf denselben Schlüssel (`null`); eine markieren hieße alle markieren, `retainAmong` könnte sie nicht auseinanderhalten |
| `trackBy` des Rasters, **zwei Ebenen**: außen `*cdkVirtualFor … trackBy: trackRow` = **Index** (`usage-stats-page.html:500`, `usage-stats-page.ts:1049-1051`; DECISIONS 2026-08-30: gechunkte Zeilen tracken nach Index, sonst View-Recycling bis in `EmoteSprite`); innen `@for (emote of row.items; track emote.emoteId)` (`usage-stats-page.html:556`) | außen Index, innen `Emote.Id` | Außen **unberührt** — die Regel vom 2026-08-30 hängt nicht am Emote-Schlüssel. Innen **bricht**: doppelte `track`-Schlüssel (Angular meldet NG0955 und fällt auf Neuaufbau zurück) — genau der Zellen-Neuaufbau samt Sprite-Blitz, den der Fix vom 2026-08-30 beseitigt hat, diesmal von innen |
| Inspector `inspectedId` (`usage-stats-page.ts:560-564`), `fillPercents` (`:491`), `usageRank` (`:535`), `seriesByEmote` (`:678,695`) | Maps nach `Emote.Id` | **Bricht leise.** Der Inspector kann eine Guid-lose Zelle nicht halten; Rang und Füllgrad kollidieren auf `null`. Die `/series`-Map trägt zufällig, weil Klasse 3 keine Serie hat und ein `get(null)` nur leer läuft — solange `null` nicht zu einem String wird |
| Queue-Key des Laufs: `startDelete` spiegelt `key: emote.emoteId` (`seven-tv-delete.service.ts:120`), `startRestore` ebenso (`seven-tv-restore.service.ts:159`) — R3 (DECISIONS 2026-09-05); `DeleteQueueEmote.emoteId: string` Pflicht (`:56-60`), `DeletableEmote.emoteId: string` Pflicht (`mass-delete-panel.ts:34-37`) | `Emote.Id` | **Bricht am Typ.** Der Import-Lauf macht es längst anders: `key: row.sevenTvEmoteId` (`seven-tv-import.service.ts:203-206`), und `doneKeys` wurde in #70 genau für Zeilen ohne Guid eingeführt (`seven-tv-run-engine.ts:105-114`). Delete und Restore sind die Nachzügler |
| Rückmeldung: `onRunComplete` ruft `reportDeleted` nur bei `doneIds.length > 0` (`seven-tv-delete.service.ts:181-183`); `doneIds` enthält nur Zeilen mit Guid; `sync-deleted` verlangt `EmoteIds` (`EmoteEndpoints.cs:274`), 400 bei leer (`:56-59`); `MarkDeletedAsync` matcht `emoteIds.Contains(e.Id)` (`EmoteService.cs:25-27`); `sync-restored` spiegelbildlich (`:61-104`) | `Emote.Id` | **Bricht — und zwar gegen K.** Ein Lauf nur über Klasse 3 meldet sich **gar nicht** zurück: keine `sync-deleted`-Anfrage, kein Audit-Eintrag. 7.1 verlangt für nicht-aktive Sets genau das Papier, das hier nie geschrieben würde |
| Optimistischer Listen-Update: Panel emittiert `deleted(doneIds)` (`mass-delete-panel.ts:297-302`), Seite filtert `!deletedIds.includes(item.emoteId)` (`usage-stats-page.ts:1251`) | `Emote.Id` | **Bricht.** Gelöschte Guid-lose Zellen blieben im Raster stehen, bis ein Reload mit frischer Live-Liste kommt |
| **Purge-Protokoll:** `buildPurgeRunProtocol` verlangt `emoteId: string` je Zeile (`purge-run-export.ts:37-42`); das Panel filtert Zeilen ohne Guid **vorher heraus** (`mass-delete-panel.ts:344-351`) — der Kommentar dort nennt den Fall selbst „silently short protocol"; der Restore-Knopf filtert genauso (`:393-397`); `parsePurgeRunProtocol` akzeptiert nur `typeof row.emoteId === 'string'` (`:157`) | `Emote.Id` Pflicht | **Bricht — der teuerste Fall.** Das Protokoll ist die einzige Rückwegdatei (A6). Ein Halloween-Lauf enthielte seine Klasse-3-Löschungen nicht, und selbst ein handgeschriebenes Protokoll fiele am Parser durch: die Löschung wäre **unumkehrbar, ohne dass irgendwer es sähe** |
| Wiederherstellen: `restore-flow.ts:82-86` baut `emoteId: row.emoteId` aus dem Protokoll, `startRestore` spiegelt den Key; `filterAlreadyPresent` vergleicht `sevenTvEmoteId` (`already-present-filter.ts:151`) | Key `Emote.Id`, Prüfung 7TV-Id | **Bricht am Key, trägt an der Prüfung** — die Guid wird nur durchgereicht, gebraucht wird sie nirgends |
| Voting-Draht: `openCreateVoteSession` reicht `selection.selectedKeys` als `emoteIds` (`usage-stats-page.ts:1276`, live als Signal seit #132); `CreateVoteSessionRequest.EmoteIds` (`VoteSessionEndpoints.cs:345-347`); `AllEmoteIdsEligibleAsync` prüft `e.Id` (`VoteSessionService.cs:302-306`) | `Emote.Id` | **Bricht** — Abschnitt 8 löst es datenseitig (Upsert über `SevenTvEmoteId`), nur der Draht sprach noch Guid |
| Drilldown `/daily` (`usage-stats-page.ts:1299`, `emote-drilldown-dialog.ts:321`) | `Emote.Id` | **Kein Bruch, eine Sperre.** Klasse 3 hat keine Tageswerte; der Inspector bietet den Drilldown für sie nicht an |
| Vote-Session-Detailseite: eigene `ListSelection` auf `emoteId` (`vote-session-detail-page.ts:318-322`), `retainAmong(results.emotes)` (`:819`), inneres `track emote.emoteId` | `Emote.Id` | **Bleibt.** Dort hat jede Zeile per Konstruktion eine Guid (8: Upsert vor dem Wahlzettel). Zwei Seiten, zwei Schlüssel — unkritisch, weil `ListSelection` seitenlokal ist; kritisch wäre ein zweiter Schlüsselraum **auf einer** Seite, und den gibt es nicht |

Ergebnis: **Codex hat recht, und die Liste ist länger als seine vier Wörter.** Von den vier genannten
Pfaden bräuchte die Mutation selbst die Guid nicht; dafür brechen fünf weitere, die er nicht nannte
(inneres `track`, Inspector-Maps, optimistischer Update, Protokoll samt Parser, Voting-Draht) — und
das Protokoll ist der schwerste, weil er die Umkehrbarkeit einer Löschung kostet.

**Der Vertrag.**

1. **Der Schlüssel einer Zeile in der Set-Ansicht ist die `SevenTvEmoteId`.** Begründung, in dieser
   Reihenfolge: Ein Set ist bei 7TV definiert, nicht bei uns — seine Einträge existieren unabhängig
   davon, ob wir je eine Zeile dafür hatten. Die lokale Id ist kanal-skopiert (Regel 8) und für einen
   Set-Eintrag ohne Zeile nicht vorhanden. Und innerhalb eines Kanals sind beide Schlüssel bijektiv
   (Unique-Index `(ChannelId, SevenTvEmoteId)`, `AppDbContext.cs:30`) — für Klasse 1 und 2 verliert
   der Wechsel nichts, und die Set-Ansicht **ist** per Route genau ein Kanal. Der Vorschlag der
   Hauptsession, den Schlüssel dort zu suchen, ist damit bestätigt, nicht verworfen. **Einschränkung:**
   eine Zelle je 7TV-Emote, nicht je Set-Eintrag. Die #74-Duplikate (zwei Aliase, eine ID) bleiben
   eine Zelle mit Slot-Zahl 2, wie 6.2 es will — wären sie zwei Zellen, wäre der Schlüssel nicht
   eindeutig, und das innere `track` fiele in denselben Neuaufbau, den der Vertrag vermeiden soll.
2. **`Emote.Id` wird Nutzlast — und die Serien sprechen die 7TV-Id (siebte Fassung, Codex D6).**
   Nullbar in `EmoteUsageContextDto` und `EmoteUsageTotal`; gelesen nur vom Drilldown `/daily` (der
   die Guid in der Route trägt und für Klasse 3 gesperrt bleibt) und vom Voting-Draht für
   Null-Sessions. Die sechste Fassung ließ `/series` bei der Guid und wollte „mit dem Zeilenschlüssel
   nachschlagen" — das hätte **jede** Zeile flach gelegt, nicht nur die Guid-losen:
   `EmoteSeriesEntryDto(string EmoteId, …)` (`IUsageStatQueryService.cs:95`) und `seriesByEmote`
   (`usage-stats-page.ts:678,695`) sprechen Guid, der neue Zeilenschlüssel 7TV-Id, und ein `get`
   über zwei Schlüsselräume trifft nie. **Entschieden: die Einträge von `/series` sind nach
   `SevenTvEmoteId` benannt**, der Endpunkt nimmt `emoteSetId` als Parameter (6.2), und der per Test
   fixierte Wire-Format-Vertrag wird mit ihm geändert — die Alternative, eine Guid→7TV-Id-Brücke in
   der Seite zu pflegen, hätte einen zweiten Schlüsselraum auf derselben Seite eingeführt, den dieser
   Vertrag gerade vermeidet. Dazu die **Caches**: `usage-stat.service.ts` schlüsselt `/daily` heute
   nach `channel|emoteId|from|to` (`:33`) und `/series` nach `channel|from|to` (`:59`), beide ohne
   Set — ein Set-Wechsel im Dropdown würde die Serie des anderen Sets wiederverwenden. Beide
   Schlüssel bekommen die Set-ID; der Drilldown-Dialog bekommt sie als eingefrorenen Teil seiner
   Eingabe, nicht aus dem aktuellen Dropdown-Zustand (dieselbe Regel wie `CapturedExportScope`,
   6.1); `clearSeriesCache()` läuft auch beim Set-Wechsel. **Das Vorhandensein der Guid ist kein
   Aussagemerkmal**: weder „hat Historie" noch „war aktiv" darf daraus gelesen werden (Klasse 2b).
3. **Der Lauf und seine Rückwege sprechen 7TV-Ids.** Queue-Key des Delete- und Restore-Laufs ist
   `sevenTvEmoteId` — R3 (DECISIONS 2026-09-05) wird dahin nachgetragen, dass der Import-Lauf nicht
   die Ausnahme war, sondern das Muster; `doneKeys` ist die Rückmelde-Identität, `doneIds` verliert
   seine Rolle (ob es als Guid-Teilmenge bleibt oder fällt, entscheidet der Plan). `deleted` emittiert
   Keys, `onDeleted` filtert nach `sevenTvEmoteId`. `DeleteQueueEmote.emoteId` und
   `DeletableEmote.emoteId` werden optional. Die Protokollzeile trägt `emoteId` optional; der Parser
   akzeptiert `null` — alte Protokolle mit Guid bleiben lesbar, weil der Restore die Guid ohnehin nur
   durchgereicht hat. **`sync-deleted` und `sync-restored` bekommen den Body `{ emoteSetId,
   sevenTvEmoteIds }`**; der Service matcht auf `(ChannelId, SevenTvEmoteId)` — dank des Unique-Index
   dieselbe Präzision wie heute per Guid —, `notFoundIds` sind 7TV-Ids, `archivedCount`/`partial`
   rechnen wie heute. `sync-imported` spricht schon `SevenTvEmoteIds` (`EmoteEndpoints.cs:280-281`):
   drei Buchhaltungsendpunkte, **eine** Identität. **Der heutige Body bleibt für eine Übergangszeit
   gültig (siebte Fassung, Codex D7):** ein Tab mit dem alten Bundle, der über den Deploy hinweg
   offen bleibt, mutiert 7TV erfolgreich und postet danach `{ emoteIds }` — ein 400 an dieser Stelle
   kostete Archivierung **und** Audit-Eintrag, genau die Schräglage, gegen die 7.4 den Import mit
   einem nullbaren Feld schützt. Der Service nimmt deshalb beide Formen: `{ emoteIds }` heißt
   „aktives Set, Guid-Match" (heutiges Verhalten), `{ emoteSetId, sevenTvEmoteIds }` die neue Form;
   400 nur, wenn keine der beiden Listen etwas trägt. Wann die alte Form fällt, entscheidet ein
   späterer Commit — nach einem Deploy, nicht mit ihm. Und der **Laufdatensatz trägt die Set-ID**:
   `DeleteRunInfo`/`RestoreRunInfo` halten heute nur `channelName` und `result`
   (`seven-tv-delete.service.ts:79-83`, `seven-tv-restore.service.ts:62-66`), die `setId` erreicht
   `onRunComplete` nur als Closure-Argument (`:174`), und der manuelle Retry des Berichts
   (`retrySyncReport`, `seven-tv-restore.service.ts:191-205`) liest aus dem Datensatz — ohne Set-ID
   darin könnte er nach einem Dropdown-Wechsel gegen das falsche Set melden. Die Set-ID wird beim
   Start eingefroren und für Erstbericht wie Retry aus demselben Datensatz gelesen. Das ist eine
   Vertragsänderung an der Api und ein eigener DECISIONS-Eintrag (Abschnitt 10, Nr. 4).
4. **Die Set-Ansicht legt keine Zeilen an.** Begründung unten, in der Abgrenzung zu 8.

**Die drei Klassen — und was in jeder geht.** Klassifiziert nach **Historie und Live-Mitgliedschaft**,
nicht nach dem Vorhandensein der Zeile (s. 2b):

| | 1 — Zeile, nicht archiviert | 2 — Zeile, archiviert, war aktiv (`ArchivedAt` gesetzt) | 2b — Zeile, archiviert, **nie** aktiv (`ArchivedAt` null; entsteht erst durch das Voting-Upsert aus 8) | 3 — keine Zeile |
|---|---|---|---|---|
| Wann | Emote ist auch im **aktiven** Set (bei HandOfBlood die 338), oder das gewählte Set ist das aktive | Emote war in einer früheren Phase aktiv (Set-Wechsel, Löschung); im nicht-aktiven Set der Normalfall für Zeilen mit Zählungen | Ein Mitglied eines nie aktiven Sets, das schon einmal auf einem Set-Wahlzettel stand | Live-Mitglied, in diesem Kanal nie aktiv (Halloween 348) |
| Auswahl | ja (Schlüssel 7TV-Id) | ja | ja | ja — **nur** mit dem Vertrag |
| Löschen aus dem gewählten Set | ja, `REMOVE` per 7TV-Id. Nicht-aktives Set: **Zeile bleibt unberührt** (Regel H), das Emote existiert im aktiven Set weiter | ja, wenn Live-Mitglied; „nicht mehr im Set" (6.2) nicht wählbar — es ist nicht da | ja, wenn Live-Mitglied | **ja — fachlich zwingend.** Der Set-Eintrag existiert unabhängig von unserer Kenntnis, und 7TV braucht nur die 7TV-Id. Ein Löschweg, der Klasse 3 ausließe, könnte das Halloween-Set zur Hälfte nicht bearbeiten — genau das Set, für das das Mod-Team gefragt hat |
| Wiederherstellen | ja (`ADD` per 7TV-Id + Alias aus dem Protokoll) | ja | ja | ja — setzt voraus, dass das Protokoll die Zeile **hatte** (Vertrag 3) |
| Nutzungszahlen | aus `UsageStat` unter der gewählten Set-ID; 0, wenn das Set beobachtet wurde und keine Zeile existiert; Tatsachenangabe (6.2), wenn nicht | aus `UsageStat` unter der gewählten Set-ID | **`null`** — keine Historie unter irgendeiner Set-ID; Kennzeichnung wie 3 | **`null`**, Kennzeichnung „nie im aktiven Set gezählt" |
| Buchhaltung nach dem Lauf | aktives Set: Zeile archiviert/reaktiviert + Audit. Nicht-aktives Set: **nur Papier** (H), per 7TV-Id + `emoteSetId` | nur Papier | nur Papier | nur Papier — **und ohne den Vertrag gar keines** (Messung: keine Rückmeldung bei `doneIds = []`) |
| Voting-Wahlzettel | Null-Session und Set-Session, bestehende Zeile | nur Set-Session (8: Mitgliedschaft statt `!IsArchived`) | nur Set-Session; die Zeile ist ja das Ergebnis eines früheren Anlegens | nur Set-Session; das Upsert legt die Zeile an, die Klasse wird zu 2b — **die Set-Ansicht ändert dafür nichts an ihrer Darstellung** |
| Drilldown | ja | ja | nein (keine Daten) | nein (keine Daten, keine Guid) |

**Abgrenzung zu Abschnitt 8: derselbe Schlüssel, bewusst verschiedene Wege.** Das Voting legt für
Wahlzettel-Mitglieder ohne Zeile einmalig archivierte Zeilen an (8, Upsert über
`(ChannelId, SevenTvEmoteId)`); die Set-Ansicht tut das **nicht**, obwohl sie dieselben Mitglieder
sieht. Der Unterschied ist kein Geschmack, sondern ein Fremdschlüssel: `Vote.EmoteId` und
`VoteSessionEmote.EmoteId` kaskadieren auf `Emote` (`AppDbContext.cs:83-121`) — eine Stimme hängt
dauerhaft an einer Zeile, der Wahlzettel ist ab Erstellung fix, also **muss** die Zeile existieren,
bevor die erste Stimme fällt. An der Set-Ansicht hängt nichts dauerhaft: sie ist eine Sicht (6.1), ihr
Lesepfad ist ein `GET`, und Löschung wie Wiederherstellung schreiben nach 7TV, nicht in `Emote`. Würde
sie Zeilen anlegen, entstünden bei jedem Öffnen eines fremden Sets Hunderte archivierte Zeilen ohne
Historie (Halloween 348, `papaplatte` drei Saison-Sets), der Wettlauf mit dem Worker-Sync aus B6
würde vom Sonderfall beim Anlegen einer Session zum Normalfall beim bloßen Ansehen, und die Konvention
„`ArchivedAt` null = nie aktiv" trüge plötzlich den Großteil der Tabelle. Beide Wege teilen den
Schlüssel — das Upsert schlüsselt über `SevenTvEmoteId`, die Ansicht auch —, sie unterscheiden sich
nur darin, ob am Ende eine Zeile steht. Der Berührungspunkt ist Klasse 2b: nach dem Anlegen einer
Set-Session hat ein vorher zeilenloses Mitglied eine Guid, und die Ansicht darf daraus **nichts**
ableiten; ihre Zahlen kommen aus `UsageStat`, ihre Kennzeichnung aus der Historie, nicht aus der
Tabelle `Emote`. Genau deshalb steht in Vertrag 2 der Satz vom Aussagemerkmal.

**Wechselwirkung mit Regel H.** H sagt, `sync-deleted` verändert Zeilen nur, wenn das gewählte Set
das aktive ist, sonst schreibt es Papier. Mit 7TV-Ids im Body ändert sich an H nichts — nur, dass
das Papier jetzt **geschrieben werden kann**: der Audit-Eintrag trägt `emoteSetId`,
`targetIsActiveSetOfChannel: false` und die Zahl der gemeldeten 7TV-Ids, wie K es für den Import
schon verlangt (7.4). Für das aktive Set findet der Service über `(ChannelId, SevenTvEmoteId)`
dieselben Zeilen, die er heute über die Guid findet. Es gibt keinen dritten Fall.

**Auswahl-Abgleich (#94, #132, #133) unter dem neuen Schlüssel.** `retainAmong(payload)` arbeitet
über die `keyFn` der Seite (`list-selection.ts`), und die Seite hat **eine** — es entsteht kein
zweiter Schlüsselraum auf derselben Seite; die Mechanik aus #94 (stiller Reload gleicht ab und sagt
es) und #132 (Dialog liest die Auswahl live) trägt unverändert. Neu **relevant** wird eine
Eigenschaft, die sie schon hatte: ein Set-Wechsel im Dropdown ist nach 6.1 eine Sicht, also
`retainAmong` statt `clear()`, und ein Emote, das in **beiden** Sets liegt (bei HandOfBlood 338),
überlebt den Wechsel als markiert — mit der Guid als Schlüssel wäre es dieselbe Zeile, mit der 7TV-Id
derselbe Eintrag, das Verhalten ist identisch. Was sich ändert, ist die **Wirkung** der Markierung:
„Löschen" nach dem Wechsel entfernt aus dem *anderen* Set. Das ist nach DECISIONS 2026-09-19
(„die Sicherheit wandert an den Punkt der Handlung") kein Grund, die Auswahl zu löschen, sondern ein
Grund, den Punkt der Handlung sprechen zu lassen: **Lösch- und Restore-Bestätigung nennen das Set**
— heute tun sie es nicht (`delete-confirm-dialog.ts`, `restore-confirm-dialog.ts` kennen weder
`setId` noch Setnamen), weil es nur eines gab. Die Alternative, beim Set-Wechsel zu leeren, ist
verworfen: 6.1 hat den Wechsel als Sicht eingeordnet, und der Mod-Handgriff „im Hauptset markieren,
in die Halloween-Ansicht wechseln, prüfen, ob dieselben Emotes dort auch liegen" ist das Set-Analogon
zur Zeitraumprüfung, für die der Eintrag vom 2026-09-19 das Leeren gerade abgeschafft hat.

**Voting-Draht.** Der Dialog liest die Auswahl weiter live (#132) — als Signal von Schlüsseln, jetzt
7TV-Ids. Für eine **Set-Session** ist das auch die richtige Wire-Identität, weil der Service ohnehin
über `SevenTvEmoteId` upsertet (8): `CreateVoteSessionRequest` bekommt neben `EmoteSetId` eine Liste
`SevenTvEmoteIds`, und die All-or-nothing-Prüfung läuft auf derselben Identität wie das Upsert. Für
eine **Null-Session** bleibt `EmoteIds`; die Seite löst die Schlüssel live über `selectedItems()` in
Guids auf — im aktiven Set ist jede Zeile Klasse 1 und hat eine. Ob der Plan beide Felder behält oder
den Service auch für Null-Sessions über die 7TV-Id gehen lässt, ist ihm überlassen; gebunden ist er
nur an zwei Sätze: der Dialog friert nichts ein, und die Prüfung läuft auf der Identität, die der
Draht trägt.

**Was der Vertrag nicht ändert.** Keine Schemaänderung — `Emote` bleibt unverändert (4.2), der
Unique-Index ist der Grund, warum der Schlüsselwechsel verlustfrei ist. Keine Änderung am äußeren
`trackBy` (Index, DECISIONS 2026-08-30). Keine Änderung der **Identität** an Übertragen, Export,
`filterAlreadyPresent`, Kollisionsvorschau — sie sprachen schon 7TV; die Set-Dimension erreicht den
Export trotzdem (6.1, Codex D9), und Vorschau und Vorprüfung bekommen die Gruppen aus 7.5 — das sind
andere Änderungen, keine am Schlüssel. Keine Änderung an der Vote-Session-Detailseite. Kein
zweites Raster: das bestehende wird auf einen anderen Schlüssel gestellt, nicht kopiert — der Hinweis
aus der Designdoku-Prüfung vom 2026-09-09, das Auswahlraster hänge an `emoteId` und sei deshalb nicht
wiederverwendbar, ist mit diesem Vertrag zur Hälfte erledigt; die andere Hälfte (`totalUseCount` als
Grundlage von Bändern und Füllbalken) bleibt und ist hier gewollt.

**Test-Auflagen (Regel 11/12), damit das nicht wieder unbemerkt bricht.** Spec für `ListSelection`
und Seite mit zwei Guid-losen Zeilen (unterscheidbar, einzeln wählbar, `retainAmong` behält die
richtige); Engine-Spec: Delete-Lauf mit `emoteId: undefined` erzeugt eine Protokollzeile und meldet den
Key in `doneKeys`; Parser-Spec: Protokoll mit `emoteId: null` ist wiederherstellbar, altes Protokoll
mit Guid ebenso; Api-Test für den neuen Body von `sync-deleted`/`sync-restored` (400-Vertrag bei
leerer Liste, Match über 7TV-Id, Papier-Fall bei fremdem Set, **alter Body `{ emoteIds }` weiter
angenommen**); Query-Test für `/series` mit einer aktiven, einer archivierten und einer Guid-losen
Zeile (die dritte kommt nicht vor, die ersten beiden unter ihrer 7TV-Id); Spec für die beiden
Cache-Schlüssel mit Set-Wechsel bei gleichem Zeitraum; Spec für den Export mit einer `null`-Zeile
(leere CSV-Zelle, JSON `null`, `trend: unknown`); E2E: Set-Ansicht mit gemockter Live-Liste, eine
Guid-lose Zelle markieren, Bestätigung nennt das Set.

---

## 7. Schreiben: Löschen, Übertragen, Import

### 7.1 Löschen und Wiederherstellen im gewählten Set (H)

Das Mass-Delete-Panel bekommt `setId` aus dem Dropdown statt aus `activeEmoteSetId()`
(`usage-stats-page.html:850`); Engine und **7TV-Mutation** brauchen nichts Neues — sie reisen mit
`setId` und `sevenTvEmoteId`. *Korrektur der sechsten Fassung (Codex C1):* Bis zur fünften Fassung
stand hier „Engine und Mutation brauchen nichts Neues" und meinte damit den ganzen Löschweg. Das war
falsch. Alles **um** die Mutation herum — Auswahlschlüssel, Queue-Key, Rückmeldung, optimistischer
Listen-Update, Protokoll und Wiederherstellung — hängt heute an `Emote.Id`, die eine Zeile der
Klasse 3 nicht hat; ein Lauf über solche Zeilen liefe zwar gegen 7TV durch, meldete sich aber nicht
zurück, verschwände nicht aus dem Raster und fehlte im Protokoll. Was davon wie umgestellt wird,
steht als Vertrag in 6.5; hier bleibt der Teil, der die Rückmeldung selbst betrifft. Die
Rückmeldung nach dem Lauf ist der Punkt: `reportDeleted` (`seven-tv-delete.service.ts:191-199`) ruft
`sync-deleted`, und `MarkDeletedAsync` archiviert die Zeilen des Kanals. **`sync-deleted` darf Zeilen nur verändern, wenn
das gewählte Set das aktive ist.** Sonst würde ein Emote, das in Halloween **und** im aktiven Set
liegt, archiviert, obwohl es im aktiven Set weiter existiert — der Match-Cache verlöre es beim
nächsten Refresh (`RefreshMatchCacheAsync` filtert `!IsArchived`, `SevenTvSyncService.cs:406`), und
bis zum nächsten Resync (der es reaktiviert) fehlten Zählungen: stiller Datenverlust. Analog
`sync-restored`.

**Vom Betreiber bestätigt am 2026-09-19 (Nachtrag zur Sitzung):** Beide Endpunkte
bekommen `emoteSetId` im Body. Ist es das aktive Set, Verhalten wie heute. Ist es ein anderes,
schreibt der Service **nur den Audit-Eintrag** — „schreibt nur Papier", exakt das Muster von
`sync-imported` (`EmoteEndpoints.cs:117-121`, DECISIONS 2026-09-05 „`sync-imported` schreibt nur
Papier"). **Sechste Fassung, aus 6.5:** die Liste im neuen Body heißt `sevenTvEmoteIds` — für
Klasse-3-Zeilen gibt es keine Guid, und ohne die Umstellung käme für einen Lauf über sie **gar
keine** Rückmeldung (`doneIds` leer ⇒ `reportDeleted` wird nicht aufgerufen,
`seven-tv-delete.service.ts:181-183`), also auch kein Papier. **Siebte Fassung:** der heutige Body
`{ emoteIds }` bleibt übergangsweise gültig und heißt „aktives Set", damit ein altes Bundle nach dem
Deploy seine Buchhaltung nicht verliert; die Set-ID des Laufs liegt eingefroren im Laufdatensatz,
auch für den manuellen Retry (6.5, Vertrag 3). Der Service matcht für das aktive Set
auf `(ChannelId, SevenTvEmoteId)` — der Unique-Index macht das so präzise wie die Guid —, für ein
anderes Set schreibt er `emoteSetId`, `targetIsActiveSetOfChannel: false` und die Anzahl in den
Eintrag. Das Papier für ein nicht-aktives Set trägt damit dieselbe Set-ID, die das Purge-Protokoll in
`meta.emoteSetId` führt; beide sagen dasselbe Set. Die Alternative — die Rückmeldung bei fremden Sets gar nicht senden — ließe einen
Löschlauf im Halloween-Set spurlos, und Entscheidung K verlangt ausdrücklich das Gegenteil. Das
`channel.synced`-Publish nach `sync-deleted` (`EmoteEndpoints.cs:68-71`) bleibt an „Zeilen
archiviert" gebunden und feuert im Papier-Fall nicht.

### 7.2 Quell-Set: nur beim fremden Kanal (I)

Datei und Bestenliste haben kein Quell-Set. Beim fremden Kanal ist die Quelle heute dessen aktives
Set, festgelegt in `ForeignEmoteSetService.cs:95-103` und in der Antwort als `EmoteSetId`
mitgeführt (`IForeignEmoteSetService.cs:123-126`, `foreign-channel-step.ts:29,207`). Für einen
Quell-Set-Picker muss der Dienst **alle Sets des fremden Users** liefern (Name, Id, Kapazität,
welches aktiv ist) und die Vorschau für eine gewählte Set-ID lesen können: `GET
/api/seventv/channels/{name}/emotes?emoteSetId=…` (`SevenTvEndpoints.cs:27-42`), Cache-Schlüssel um
die Set-ID erweitert. Die Set-Liste kostet einen weiteren Request im selben Budget; die Abfrage ist
die aus 11.1 (v4 `emoteSets` am User, ohne Anmeldung, ein Request) — dieselbe, die 6.1 und der
Ziel-Set-Picker (7.3) benutzen, nur mit der Twitch-ID des **fremden** Kanals. Der Lesepfad nach
Set-ID mit `?emoteSetId=` ist derselbe, den die Zielvorschau des Ziel-Set-Pickers braucht (7.5,
Baustein 1); 7.2 hängt den Quell-Picker davor.

*Vierte Fassung:* Der Namensbefund (11.2) ändert an 7.2 nichts, was Set-Liste oder Lesepfad
betrifft — er sagt nur, was ein Quell-Set-Picker beim fremden Kanal **sichtbar** machen wird: wählt
jemand dort ein Saison-Set als Quelle, kollidiert erfahrungsgemäß ein gutes Viertel davon per Name
mit dem eigenen Zielset (bei HandOfBlood 192 von 687), und der Bestätigungsdialog zeigt das über
`nameCollisions` (`import-preview.ts:35,48-49`) nur dann richtig, wenn seine Zielliste aus dem
**gewählten Zielset** kommt (7.5, Baustein 3). Die Quell-Vorschau liefert dafür schon heute den Alias je
Eintrag als `name` — das ist die Vergleichsbasis, die 7TV selbst benutzt, nicht der Originalname.

### 7.3 Ziel-Set: getrackte Ziele als Vorgabe, ungetrackte nur nach Bestätigung (J, abgeschwächt)

Heute: Kandidaten aus `listMine()` (`import-target-dialog.ts:38`), gefiltert auf Broadcaster oder
7TV-Editor, deaktiviert wenn nicht getrackt (`import-target-options.ts:22-33`); die Set-ID kommt aus
dem Status des Zielkanals (`import-target-loader.ts:84-89`).

**Warum der Picker in der Hauptrunde steht — und warum die Begründung nicht mehr der Termin ist.**
Die dritte und vierte Fassung hatten den Ziel-Set-Picker als Vorabscheibe vor den 2026-10-01
gezogen, weil das Mod-Team in das damals nicht-aktive Halloween-Set schreiben wollte. Dieser Anlass
ist seit der fünften Fassung ohne Deploy gedeckt (12.4): das Set wird in einem anderen Kanal aktiv
gesetzt, und der heutige Picker reicht. Das ist ein **Kniff, keine Lösung** — er setzt einen
nie getrackten Wegwerfkanal voraus, dessen aktives Set für die Dauer des Befüllens Halloween ist,
und er endet mit einem Purge (12.4, Rückbau). Der Picker löst den **Dauerfall**: in ein nicht-aktives Set schreiben, **ohne** es
vorher woanders aktivieren zu müssen — für jeden Kanal, der Saison-Sets pflegt, nicht nur für den
einen mit einem Wegwerfkanal. Die technischen Befunde dazu — die Zielvorschau muss aus dem
**gewählten** Set kommen, die Vorprüfung vergleicht nur IDs, die Preview-Abfrage holt keine
Kapazität — stehen in 7.5; sie waren nie an der Vorabscheibe aufgehängt und gelten unverändert.

Die erste Fassung wollte **alle** Sets, in die 7TV den Nutzer schreiben lässt, gleichberechtigt
anbieten. Codex hat dagegen den Fall des Editors gehalten, der mehrere Broadcaster betreut und
hunderte Emotes ins falsche, ungetrackte Kundenset kopiert — **autorisiert, aber ungewollt** (B5).
7TVs Rechteprüfung verhindert genau das nicht: sie prüft, ob der Akteur schreiben *darf*, nicht, ob
er *dorthin* wollte. Der Betreiber hat den Einwand angenommen. Neuer Vertrag:

- **Kandidaten kommen serverseitig, ohne Nutzer-Token (achte Fassung).** Die siebte Fassung zog
  sie aus `editableEmoteSetIds`, das eine 7TV-Anmeldung verlangt (11.1), und baute darauf den
  Token-Prompt vor dem Picker und Sonde 7. Beides ist entfallen, weil EmotePurge die
  Editor-Beziehung längst abfragt: `GetEditorOfChannelsAsync` stellt eine gewöhnliche
  GraphQL-Anfrage auf `editor_of` des 7TV-Users und bildet jede Zuweisung auf Twitch-Login und
  Twitch-ID ab (`SevenTvApiClient.cs:57-58,323-360`, `SevenTvEditorGrant`);
  `SevenTvEditorService.GetEditorGrantsAsync` cacht das (`SevenTvEditorService.cs:14-40`), und
  `MyChannelsService` (`:52`) teilt sich den Cache mit dem Autorisierungspfad. Editor-Rechte hängen
  bei 7TV am **Account**, nicht am Set. Die Angebotsliste ist deshalb: der **eigene** Account plus
  alle Accounts aus `editor_of`, je Account die **öffentliche** Set-Liste (11.1 — der v4-Aufruf
  mit `platformId`, genau die Twitch-ID, die der Grant trägt; ein Account ohne Twitch-Verbindung
  fällt heraus, `SevenTvApiClient.cs:357`), persönliche Sets ausgefiltert (`flags: 4`, Kapazität 5,
  11.1). Wie viele Endpunkte das sind, entscheidet der Plan; fest steht, dass der Browser dafür
  keinen Token braucht.
- **Zurückgenommen: der Token vor dem Ziel-Picker.** Die siebte Fassung hatte entschieden, den
  7TV-Token vor dem Öffnen des Pickers abzufragen, und das als bewusste Abweichung von A16/R2
  (`import-flow.ts:34-41`, DECISIONS 2026-09-06 „Token-Prompt nach der Bestätigung", #72 K3)
  begründet — ausschließlich damit, dass die **Liste der Angebote** an einem angemeldeten Feld
  hing. Das tut sie nicht mehr; der Betreiber hat die Entscheidung zurückgenommen. Der Token wird
  wieder erst vor dem Lauf fällig, genau wie heute — **keine Abweichung von A16/R2**, kein
  nachzuziehender Kommentar in `import-flow.ts`, kein R2-Absatz im DECISIONS-Eintrag 2.
- **Benannte Annahme statt Messung (Entscheidung des Betreibers).** Ob 7TV Editor-Rechte feiner
  abstuft — Emotes verwalten gegen Sets verwalten —, wird **nicht gemessen**; die frühere Sonde 7
  ist gestrichen. Das gesamte heutige 7TV-Management setzt schon voraus, dass ein Editor Sets
  bearbeiten darf; fehlt das Recht, antwortet 7TV auf die Mutation mit `LACKING_PRIVILEGES` über
  HTTP 200, und der Lauf bricht sichtbar ab (`seven-tv-import.service.ts:56-75`,
  `seven-tv-run-engine.ts:95`). Ein Set, das im Picker steht und sich nicht beschreiben lässt, ist
  damit ein sichtbarer Fehlschlag, kein stiller.
- **Zwei Klassen im Picker.** *Getrackte Ziele* — **alle** Sets der Kanäle aus `listMine()`, nicht
  nur deren `ActiveEmoteSetId` — stehen oben, unter ihrem Kanal gruppiert; das aktive Set trägt die
  Beschriftung „aktiv" und ist vorausgewählt, damit der heutige Ein-Klick-Weg „in Kanal X"
  unverändert bleibt (Aufklappen und Vorauswahl in 7.5, Baustein 2). Die frühere Formulierung
  „Sets, die `ActiveEmoteSetId` eines Kanals sind" stammt aus einer Fassung vor dem Set-Picker und
  hätte genau die Einschränkung festgeschrieben, die dieses Konzept aufhebt. *Ungetrackte Ziele* stehen darunter, als
  „nicht getrackt" gekennzeichnet, und lösen beim Wählen eine **Bestätigung** aus, die **Besitzer
  (7TV-Anzeigename des Set-Eigentümers) und Setnamen** nennt: „In das Set ‚Halloween' von
  ‚HandOfBlood' kopieren?" Ohne Bestätigung kein Lauf. Kein zweiter Bestätigungsschritt für
  getrackte Ziele — dort trägt schon die Kanalbeschriftung die Antwort auf „wohin".
- Der Besitzer ist am Set-Objekt lesbar — **gemessen** (11.1): `emoteSet { owner { mainConnection {
  platformDisplayName } } }` kommt ohne Anmeldung. Die Bestätigung kann also Besitzer **und**
  Setnamen nennen; der Fallback „Set-ID statt Name" aus der zweiten Fassung ist nicht mehr nötig.

Das **lockert DECISIONS 2026-09-09** („Fremd ist die Quelle, nie das Ziel … das Zielset bleibt ein
getrackter Kanal aus `listMine()`", Zeilen 1914 f.) — kleiner als in der ersten Fassung gedacht,
und seit der achten Fassung näher am ursprünglichen Eintrag, als die siebte es war: das Ziel darf
ein Set eines Accounts sein, **dessen Editor der Nutzer nachweislich ist** — nachgewiesen über
dieselbe `editor_of`-Abfrage, aus der `listMine()` seine Editor-Kanäle bezieht —, nach Bestätigung
mit Besitzer und Setname; die Vorgabe bleibt das getrackte Ziel. Gelockert wird also nur die
Bedingung „getrackt", nicht die Bedingung „Editor". Der Eintrag (Abschnitt 10) beschreibt genau
diese Form. Die Schreibrechte selbst hat immer 7TV geprüft (`LACKING_PRIVILEGES`, oben); unsere
eigenen Filter (`UsageStatsAccessAuthorizationFilter`, `EmoteEndpoints.cs:28`) sichern nur den
Lesezugriff auf die Chat-Statistik und sind kanal-skopiert. Die Angebotsliste ist keine
Rechteprüfung und kein Ersatz für die Frage „meinst du wirklich dieses?" — die stellt der Picker.

Für ein ungetracktes Ziel liefert unsere Datenbank nichts: belegte Slots, Kapazität und die Liste
schon vorhandener Emotes (`already-present-filter.ts`) kommen dann aus dem Live-Abruf des Zielsets —
dieselbe Quelle, die die Restore-Vorprüfung aus gutem Grund schon nutzt (DECISIONS, Zeile 1638: „the
check asks 7TV, not our own database"). Der Nachlauf-Resync des Zielkanals entfällt, wenn kein
getrackter Kanal das Set aktiv hat — es gibt nichts abzugleichen.

### 7.4 Audit: der Vertrag von heute, und warum er für ungetrackte Ziele nicht reicht (K)

**Korrektur einer Falschbehauptung der ersten Fassung.** Dort stand, der Import-Eintrag werde
„weiterhin gegen den Kanal geschrieben, auf dessen Seite gehandelt wurde", als setze das das heutige
Verhalten fort. Das ist falsch, und Codex hat es zu Recht angemerkt (B5). Heute ist der Zielkanal
der Routenkanal: `reportImported` postet an `run.targetChannelName`
(`seven-tv-import.service.ts:296-301`), die Gruppe `/api/channels/{channelName}/emotes` steht hinter
`UsageStatsAccessAuthorizationFilter` (`EmoteEndpoints.cs:24-28`), der eine Kanalrolle **für den
Zielkanal** verlangt, und `MarkImportedAsync` lädt die Kanalzeile und antwortet ohne sie mit `false`,
was der Endpunkt als 404 durchreicht (`EmoteService.cs:112-116`, `EmoteEndpoints.cs:206`). Für ein
ungetracktes Ziel gibt es keine Kanalzeile — der heutige Pfad würde die Rückmeldung mit 404
verwerfen, und der Vorgang wäre spurlos. Also zwei Wege, nach Zielklasse:

**Getracktes Ziel — heutiger Weg, um ein Feld erweitert:**

- `SyncImportedRequest` (`EmoteEndpoints.cs:283-284`) bekommt ein Feld `TargetEmoteSetId`,
  **nullbar** — ein Tab mit dem alten Bundle, der über den Deploy hinweg offen bleibt, postet ohne
  das Feld, und ein 400 nach gelaufener Mutation kostete genau die Papierspur, die das Feld
  verbessern soll (Kommentar `:208-209`). Ob ein späterer Schritt es zur Pflicht macht, entscheidet
  der Plan; nötig ist es nicht. Keine Formatprüfung über das hinaus, was 7TV bereits angenommen
  hat. Details in 7.5, Baustein 4.
- Der Eintrag läuft wie heute gegen den **Zielkanal** (`ChannelName` = Routenkanal; dort listet ihn
  die Audit-Ansicht über den Index `(ChannelName, OccurredAtUtc)`, `AppDbContext.cs:151`). Neu:
  `TargetType = "emoteSet"`, `TargetId = <Set-ID>`, in den Details `targetEmoteSetId` und
  `targetIsActiveSetOfChannel` zum Schreibzeitpunkt. So ist nachvollziehbar, ob in das aktive oder
  ein anderes Set desselben Kanals importiert wurde.

**Ungetracktes Ziel — neuer, set-zentrierter Endpunkt:**

- `POST /api/seventv/emote-sets/{emoteSetId}/sync-imported` in der `/api/seventv`-Gruppe
  (`RequireAuthorization()`, keine Kanalrolle, `Bookkeeping`-Rate-Limit wie die drei Geschwister).
  Er kommt **ohne `Channel`-Zeile aus**: keine Kanal-Lookups, kein 404 für unbekannte Kanäle.
- Er **prüft die Berechtigung erneut**, statt dem Client zu glauben — aber **nicht** über
  `editableEmoteSetIds`, wie die zweite Fassung wollte: das Feld ist serverseitig nicht lesbar
  (11.1, `LOGIN_REQUIRED`), und den 7TV-Token zum Nachprüfen an das Backend zu schicken verletzte
  den Zero-Knowledge-Grundsatz (DECISIONS 2026-07-25, 2026-09-09). Was öffentlich lesbar ist und
  reicht: der **Besitzer** des Sets (`emoteSet { owner { id } }`, 11.1) muss der 7TV-User des Akteurs
  sein oder einer, für den der Akteur `editor_of` ist — dieselbe Abfrage, die `isSevenTvEditor`
  heute speist (`GqlEditorOfQuery`, `SevenTvApiClient.cs:57-58`) und seit der achten Fassung auch
  die Angebotsliste des Pickers (7.3). Das ist eine Prüfung auf **Besitzer-Ebene**, nicht auf
  Set-Ebene; dass 7TV Editor-Rechte nicht je Set einschränkt, ist die benannte Annahme aus 7.3 —
  träfe sie nicht zu, hätte die Mutation vorher schon `LACKING_PRIVILEGES` geliefert und der Lauf
  wäre abgebrochen (`seven-tv-run-engine.ts:95`), die Papierspur bliebe leer, weil nichts passiert
  ist. Nicht Besitzer und nicht Editor → 403, kein Eintrag. Das ist die einzige Stelle, an
  der EmotePurge für ein ungetracktes Set selbst eine Rechtefrage stellt, und sie stellt sie nur,
  um keine falsche Papierspur zu schreiben.
- Er **protokolliert Akteur, Ziel-Set-ID und den aufgelösten Besitzer**: `ChannelName = null`
  (Aktion ohne getrackten Kanalbezug — der Fall ist in `AuditLogEntry.cs:63-64` vorgesehen),
  `TargetType = "emoteSet"`, `TargetId = <Set-ID>`, Details `targetEmoteSetName`,
  `targetOwnerDisplayName` (oder `targetOwnerSevenTvUserId`, wenn kein Name lesbar ist),
  `emoteCount`, `sourceKind`, `sourceChannelName`, `leaderboardSort` — dieselbe Herkunftsvokabel wie
  heute (`EmoteEndpoints.cs:146`).
- **Bewusster Rest:** ein solcher Eintrag erscheint in der globalen Admin-Audit-Ansicht, nicht in
  der kanalgebundenen Ansicht des Quellkanals (die filtert auf `ChannelName`). Wer von der
  Nutzungsseite eines getrackten Kanals in ein ungetracktes Set überträgt, findet die Spur also
  beim Admin, nicht beim Kanal. Ein zweiter Eintrag unter dem Quellkanal wäre möglich, verdoppelt
  aber jeden Vorgang im Log; verworfen, bis der Betrieb zeigt, dass jemand danach sucht.
- `AuditLogQueryService.ProjectDetail` (`:129`, Vokabular `:20-25`) muss die neuen Felder lesen —
  die Warnung dazu steht wörtlich am Endpunkt (`EmoteEndpoints.cs:144-145`): eine Vokabel, die dort
  nicht ankommt, verliert ihre Herkunft still. Api-Test für die neue Route (Regel 11): 401 ohne
  Session, 403 für ein Set, dessen Besitzer weder der Akteur noch einer seiner `editor_of`-Accounts
  ist, 204 sonst.
- Dieselbe Form (`TargetType`/`TargetId` + Details) für die Papier-Einträge aus 7.1.

### 7.5 Der Ziel-Set-Picker im Einzelnen — Bausteine, und die Stelle, an der eine naive Umsetzung gefährlich wird

*Fünfte Fassung: dieser Abschnitt trägt die technischen Befunde, die in der dritten und vierten
Fassung unter 12.4 („Vorabscheibe", dort V2–V5) standen. Sie hingen nie an der Scheibe und gelten
unverändert; was dort Deploy- und Terminsache war, ist weg. Die Begründung, warum der Picker gebaut
wird, steht in 7.3; hier steht, woraus er besteht und wo er kippt.*

**Heute geht „gleicher Kanal, anderes Set" an keiner Stelle.** Der Ziel-Picker kennt nur Kanäle
(`import-target-dialog.ts:29-32`, `ImportTargetChoice { scope, channelName }`), die Set-ID des Ziels
kommt aus dem Kanalstatus (`import-target-loader.ts:84-89`), und der Picker schließt den eigenen
Kanal aus (`import-target-options.ts:30`) — „HandOfBlood → HandOfBlood, anderes Set" ist nicht
einmal auswählbar. Der Dateiweg zielt immer auf das aktive Set des aktuellen Kanals (DECISIONS
2026-09-06, Restore-Panel), Kopfzeile und Dock gaten auf `activeEmoteSetId()` (Abschnitt 1).

**Baustein 1 — Backend: Set-Liste je Kanal, Kapazität in der Set-Vorschau, Lesepfad nach Set-ID.**
Alles lesend, alles additiv:

- **`GET /api/channels/{name}/emote-sets` — der Endpunkt aus 6.1, kein zweiter:** Id,
  Name, Kapazität, `isActive` (Vergleich mit `Channel.ActiveEmoteSetId`), `isPersonal`. Aufgelöst
  über `Channel.TwitchChannelId` (`Channel.cs:6`) mit der Abfrage aus 11.1. Hinter denselben Filtern
  wie `/emotes` (`EmoteEndpoints.cs:24-28`): wer den Picker öffnet, hat eine Rolle im Zielkanal,
  sonst stünde der Kanal nicht in `listMine()`. Der Endpunkt ist je **Kanal** geschnitten, nicht
  je Nutzer: die getrackte Klasse des Pickers braucht die Sets **des Kanal-Besitzers**, und die sind
  öffentlich — die eigenen v3-`emote_sets` eines Editors enthalten die Sets des Broadcasters nicht
  (Abschnitt 11, Fremdset-Sonde), und HandOfBloods Mod-Team besteht aus Editoren. Die account-weite
  Angebotsliste samt ungetrackter Klasse steht in 7.3 (achte Fassung) und liest dieselbe öffentliche
  Set-Abfrage über die `editor_of`-Accounts. Ob der einzelne Editor in ein bestimmtes Set des
  Besitzers schreiben darf, prüft 7TV bei der Mutation
  (`LACKING_PRIVILEGES` → Abbruch, `seven-tv-run-engine.ts:95`), wie heute. Kein Cache über die
  Sitzung hinaus: der Picker lädt die Liste beim Wählen eines Kanals, wenige Requests je Dialog.
  Regel 4/5/11: Interface in `Core/Services`, Implementierung in `Infrastructure/Services`, Test —
  wohl wissend, dass der Test das Wire-Format nicht beweist; das hat die Sonde getan.
- **Persönliche Sets.** Gemessen am Testkanal (11.1): drei Sets, eines davon
  `{"name":"Personal Emote Set","capacity":5,"flags":4}`, die beiden normalen `flags: 0`. Ein
  Fünf-Plätze-Set ist kein Ziel für Kanal-Emotes. `isPersonal` wird aus dem v3-Flag abgeleitet (Bit
  4 gesetzt) — **mit Vorbehalt**: gemessen ist nur, dass das Flag das persönliche Set von den beiden
  anderen unterscheidet, nicht, was Bit 4 heißt. Trägt v4 ein entsprechendes Feld am `EmoteSet`,
  ersetzt es das Flag; das ist eine Introspektion, kein Umbau. Solange die Liste aus v4 kommt und
  das Flag aus v3, kostet die Ableitung einen zweiten Request je Kanal — oder die Liste kommt
  ganz aus v3 (`users/twitch/{id}`, der Endpunkt, den der Sync ohnehin liest), der
  alle vier gebrauchten Felder in einem Request trägt. **Plan entscheidet**; das Papier legt nur
  fest, dass ein persönliches Set nie kommentarlos wählbar ist.
- **Kapazität in der Set-Vorschau.** `GqlEmoteSetPreviewQuery` fragt heute nur
  `emotes { totalCount pageCount items … }` (`SevenTvApiClient.cs:67-68`), und
  `SevenTvEmoteSetPreview(TotalCount, Truncated, Items)` (`SevenTvModels.cs:503`) hat kein
  Kapazitätsfeld — der Befund aus 6.2 („den die Preview-Abfrage heute **nicht** mitholt"). Die
  Abfrage fragt zusätzlich `capacity` (und `name`) am Set-Objekt, der Record bekommt `Capacity`
  nullbar (0 → null, wie `:704` es für den Sync schon hält), und `ForeignEmoteSet`
  (`IForeignEmoteSetService.cs:123-129`) wird um `Capacity` erweitert — additiv.
- **Lesepfad nach Set-ID:** `GET /api/seventv/channels/{name}/emotes?emoteSetId=…` — der Parameter,
  den 7.2 ohnehin vorsah (`SevenTvEndpoints.cs:27-42` kennt heute nur `refresh`), hinter demselben
  Härtungs-Dekorator, mit **Set-ID als Cache-Schlüssel** und den bestehenden 60 s (6.4). Für ein
  bekanntes Set läuft die Identitätsauflösung (Helix + `userByConnection`) nicht mit;
  `sevenTvUserId` darf in diesem Modus aus der Kanalzeile kommen oder fehlen — Plan entscheidet.
  Wozu, steht in Baustein 3.

**Baustein 2 — Frontend: Ziel-Picker über Kanal und Set (getrackte Klasse; die ungetrackte steht
in 7.3).**

- `ImportTargetChoice` bekommt `emoteSetId` (`import-target-dialog.ts:29-32`); `channelName` bleibt
  Pflicht — `startImport` und `sync-imported` brauchen ihn (`import-flow.ts:93-94`,
  `seven-tv-import.service.ts:296-301`). Die Sets eines Kanals klappen unter dem Kanal auf
  (Endpunkt aus Baustein 1 beim Wählen des Kanals); das aktive Set ist als aktiv beschriftet und vorausgewählt,
  damit der heutige Ein-Klick-Weg „in Kanal X" unverändert bleibt. Persönliche Sets stehen
  deaktiviert mit Beschriftung — dasselbe Idiom wie ungetrackte Kanäle heute
  (`import-target-options.ts:7-9`), nicht ausgeblendet, damit sichtbar bleibt, warum sie fehlen.
- **Der eigene Kanal bleibt in der Liste.** `import-target-options.ts:30` schließt ihn heute aus,
  mit gutem Grund — Quelle und Ziel wären dasselbe Set gewesen. Neu: der eigene Kanal steht drin,
  und deaktiviert („das ist die Quelle") ist nur sein **aktives Set**, die Quelle des Laufs
  (`CapturedImportScope.emoteSetId`, `usage-stats-page.ts:1353`). Genau dieser Eintrag ist der
  Anlass: HandOfBlood → HandOfBlood, Halloween. Der Kommentar an `sameChannelFile`
  (`import-confirm-dialog.ts:421-424`, „a picker cannot produce [the same channel]") wird damit
  teilweise falsch und ist anzupassen; die Prüfung selbst bleibt datei-only und richtig — sie warnt
  vor „Datei → gleicher Kanal", und ob das dasselbe Set ist, entscheidet jetzt die Set-Wahl.
- Die Kopfzeile des Bestätigungsdialogs nennt Kanal und Set-**ID** (`import-confirm-dialog.ts:103`);
  sie nennt künftig den Set**namen**. Eine ObjectID ist keine Antwort auf „wohin".
- **Die drei anderen Türen folgen dem Dropdown, nicht dem Picker.** Datei-, Fremdkanal- und
  Bestenlisten-Import zielen heute auf das aktive Set des aktuellen Kanals (`import-trigger.ts:132`,
  `foreign-import-flow.ts:25,69`, Restore-Panel) und bekommen die Set-Dimension mit dem Dropdown
  aus 6.1 (13, Schritt 6). Bis dahin ist die **Quelle** von „Übertragen" das aktive Set der Seite;
  die Gates auf `activeEmoteSetId()` bleiben.

**Baustein 3 — Die Zielvorschau kommt aus dem gewählten Set, nicht aus den Kanal-Endpunkten.** Das
ist die Stelle, an der eine naive Umsetzung gefährlich wird, und sie ist belegt:

- `loadImportTarget` (`import-target-loader.ts:66-98`) ruft `getSetStatus(channelName)` und
  `listEmotes(channelName)` (`:70-71`), also `GET /api/channels/{name}/emotes/active-set` und
  `GET /api/channels/{name}/emotes` (`emote-admin.service.ts:81,88-89`) — beides das **aktive** Set
  des Zielkanals. Aus dem Status kommen `setId`, `occupiedSlots` und `capacity` (`:89-91`), aus der
  Kanalliste die `emotes` (`:93`).
- Darauf rechnet der Dialog alles, was ihn zur Sicherung macht: `buildImportPreview` bildet
  `alreadyPresent` und `nameCollisions` aus genau dieser Liste (`import-preview.ts:30-35`),
  `projectSlots(target.occupiedSlots, target.capacity, …)` die Slot-Projektion
  (`import-confirm-dialog.ts:397`) — und `execute()` schließt mit **`target.setId`** (`:478`), das
  `import-flow.ts:94` als `setId` an `startImport` gibt.
- Folge einer Picker-Änderung ohne Loader-Änderung: wählt jemand Halloween, zeigt der Dialog
  Kollisionen und freie Plätze **des Hauptsets** — und der Lauf schriebe sogar **in das Hauptset**,
  weil `target.setId` aus dem Status kommt, nicht aus der Wahl. Das ist nicht kosmetisch: A16 hat
  die Set-Prüfung beim Import ausdrücklich entfernt und dem Dialog dafür die ganze Last aufgeladen
  (`docs/Feature-Ideen-2026-08-01.md:482-485`: „ein 900er-Set in ein Set mit 50 freien Slots zu
  kippen, muss vorher sichtbar sein statt als Fehlerregen danach"). Er **ist** die Sicherung. Ein
  900er-Set gegen ein Set mit wenigen freien Plätzen hieße sonst ein Lauf, der mittendrin Emote für
  Emote abprallt.

*Präzisierung (vierte Fassung):* Für den Anlass selbst — Quelle **und** Ziel sind HandOfBlood —
sähe die naive Umsetzung nicht einmal so aus. Die Zielliste käme aus dem aktiven Set, also aus genau
der Menge, aus der die Auswahl stammt: `buildImportPreview` zählte jede Zeile per ID als
`alreadyPresent` (`import-preview.ts:43-45`), `toAdd` wäre leer, `nothingToAdd` griffe
(`import-confirm-dialog.ts:419`), und selbst ein durchgewinkter Lauf fiele in `filterAlreadyPresent`
per ID auf null Zeilen (`already-present-filter.ts:151`). Der Dialog sagte „762 bereits vorhanden,
nichts hinzuzufügen" über ein Set, dem 242 fehlen. Das schwächt Baustein 3 nicht, es ist dieselbe
Fehlfunktion in leiser Form — ein Schutz, der die falsche Frage beantwortet. Das „schriebe sogar in
das Hauptset" oben bleibt richtig für ein nicht-aktives Set eines **anderen** Kanals, dessen
aktives Set die Quell-Emotes noch nicht enthält.

Vertrag:

- `loadImportTarget(emoteAdminService, { channelName, emoteSetId })`. Ist `emoteSetId` das aktive
  Set des Kanals (`activeEmoteSetId` aus dem Status), läuft der heutige Weg unverändert — unsere
  Zeilen tragen die EventAPI-Deltas zwischen zwei Syncs, das ist die bessere Zahl
  (`IEmoteSetStatusService.cs:12-17`). Ist es ein anderes Set, kommen `emotes` (Alias → `name`,
  Emote-ID → `sevenTvEmoteId`), `occupiedSlots` (= `totalCount`; Duplikate zählen als Einträge, wie
  6.2 es will) und `capacity` aus dem Lesepfad nach Set-ID (Baustein 1). Kostet zwei Permits für ein
  900er-Set **je Dialog-Öffnung**, nicht je Reload — hier gibt es keine stillen Reloads.
- `truncated` ⇒ `failed`, nicht „ready mit kleinerer Zahl": eine abgeschnittene Liste unterschätzt
  Belegung **und** Kollisionen, und der Dialog darf nichts zeigen, was er nicht weiß. Dieselbe
  Regel, die der Loader für die Warnung schon hat (`import-target-loader.ts:27-28`: „a failed
  check must read as ‚not verified', never as a false all-clear").
- `getSetWarning` beantwortet „ist das aktive Set des Kanals mit anderen geteilt"
  (`emote-admin.service.ts:70-74`) — für ein anderes Set ist das die falsche Frage. Für ein
  nicht-aktives Ziel gilt `UNAVAILABLE_WARNING` („nicht geprüft", `import-target-loader.ts:29-34`)
  oder eine billige Datenbankfrage „ist diese Set-ID `ActiveEmoteSetId` eines anderen getrackten
  Kanals" — plausibel, Plan entscheidet; verboten ist nur, aus der fehlenden Prüfung ein Grün zu
  machen.
- Die Vorprüfung unmittelbar vor dem Lauf ist **schon set-skopiert, vergleicht aber nur IDs** —
  *korrigiert in der vierten Fassung; die dritte schrieb „sie braucht nichts".*
  `filterAlreadyPresent(httpClient, outcome.targetSetId, …)` (`import-flow.ts:81`) liest 7TV direkt
  nach Set-ID, fragt dabei aber ausschließlich `emote { id }` ab (`already-present-filter.ts:13-14,19`)
  und filtert nach `sevenTvEmoteId` (`:151`). Die 328 Emotes, die als dieselbe ID schon im
  Halloween-Set liegen, fängt sie; die 192 Namensvettern **sieht sie nicht** — deren ID ist im
  Zielset frei, nur der Alias ist vergeben. Die einzige Stelle, die Namenskollisionen vor dem Lauf
  erkennt, ist `buildImportPreview` im Dialog (`import-preview.ts:35,48-49`, exakter `===`-Vergleich
  wie 7TV), und die rechnet auf der Zielliste, die erst Baustein 3 aus dem gewählten Set holt. Ohne ihn
  gibt es **keine** Stelle, die die 192 vor 7TV erkennt. Für die Set-Dimension braucht
  `filterAlreadyPresent` weiterhin nichts; was sie **nicht** leistet, steht hier, weil die
  Kollisionsrechnung unten daran hängt.
- Nach dem Lauf: `reportImported` postet an `run.targetChannelName` (Baustein 4), und der Nachlauf-Resync
  des Zielkanals (`seven-tv-import.service.ts:284-285`) läuft wie heute — findet für ein
  nicht-aktives Ziel nichts Neues, kostet einen Bookkeeping-Request, ist harmlos. In unserer
  Datenbank wird das Ergebnis erst sichtbar, wenn das Set aktiv wird — beim Wechsel archiviert der
  Sync die Differenz und legt die neuen Zeilen an (Abschnitt 2). Kein neuer Hinweistext dafür: 7TV
  zeigt das Set sofort, und ein Text für einen Zustand, den der nächste Sync auflöst, wäre ein
  Control zu viel.

**Die Kollisionsrechnung für den Anlass — das zweite, unabhängige Argument für Baustein 3 (vierte
Fassung, 11.2 „Namen gegen IDs"; Zahlen in der siebten Fassung an den ausgelieferten Filter
angepasst, Codex D5).** Das Halloween-Set hat 687 von 1000 Plätzen belegt, **313 frei**. Der
ausgelieferte Weg rechnet per **ID**: `buildImportPreview` zählt jede Quellzeile, deren
`sevenTvEmoteId` im Zielset liegt, als `alreadyPresent` und nimmt sie aus `toAdd`
(`import-preview.ts:43-46`), `filterAlreadyPresent` überspringt vor dem Lauf nach derselben Regel
(`already-present-filter.ts:151`) — **unabhängig vom Alias**. Übersprungen werden also die **338**
IDs der Schnittmenge, nicht die 328 mit gleichem Namen; die rund zehn IDs, die im Halloween-Set unter
anderem Alias liegen, werden **still** als vorhanden behandelt und bekommen den Hauptset-Alias dort
nie. Es bleiben rund **424** Zeilen in `toAdd`; davon tragen **192** einen Namen, unter dem im
Halloween-Set bereits ein **anderes** Emote liegt, und für die prallt ein `ADD` ab: 7TV lehnt mit
`this emote has a conflicting name` ab, sobald der Alias vergeben ist, und den Alias wegzulassen
hilft nicht, weil `EmoteSetEmote.alias` ein `String` ist und bei fehlender Angabe mit
`emote.default_name` gefüllt wird; der Vergleich ist case-sensitiv und unnormalisiert
(`docs/Feature-Ideen-2026-08-01.md:486-495`). Landen würden rund **232** (687 + 232 = 919 ≤ 1000) —
die 242 Namen, die dem Halloween-Set fehlen, abzüglich der etwa zehn, deren ID per Alias-Abweichung
schon drin ist. Wählte das Mod-Team das ganze Hauptset, wäre das ohne Kollisionsvorschau **ein Lauf,
bei dem 192 Zeilen mit Fehlern zurückkommen**; wählt es nur die beliebten, skaliert der Anteil mit:
rund ein Viertel jeder Auswahl aus dem Hauptset (192 von 762) hat im Halloween-Set einen
Namensvetter. Die Summen aus 11.2 lassen die Aufteilung der zehn nicht exakt zu (zwei der
Hauptset-IDs liegen dort selbst unter zwei Aliasen, #74); „rund" ist hier gemessen, nicht geschätzt.

Was so ein Lauf heute täte, am Code: eine `conflicting name`-Ablehnung ist eine gewöhnliche
Zeilen-Fehlermeldung, kein Abbruchgrund — `abortsForMissingPrivileges` stoppt nur bei
`LACKING_PRIVILEGES`/401/403 (`seven-tv-import.service.ts:51-71`), alles andere läuft weiter
(`seven-tv-run-engine.ts:261-269`). Die Zeilen enden als `failed` im Dock, der Lauf braucht mit
275 ms Pacing je Zeile (`RUN_DELAY_MS`, `:29`) rund zwei Minuten für 424 Zeilen, und jede abgelehnte
Mutation zieht laut dem Kommentar an derselben Stelle ein Ticket aus dem `emote_set_change`-Eimer
(`seven-tv-import.service.ts:52-53`) — rund 190 Tickets für nichts. Nichts davon ist ein
Datenschaden; es ist genau der „Fehlerregen danach", den A16 mit dem Dialog verhindern wollte
(`Feature-Ideen-2026-08-01.md:482-485`).

Die Vorschau kann das heute schon sagen — wenn sie auf der richtigen Liste rechnet.
`buildImportPreview` bildet `nameCollisions` per exaktem Namensvergleich gegen die Zielliste
(`import-preview.ts:35,48-49`), der Dialog zeigt sie mit „N Namen sind im Zielset schon vergeben —
7TV wird diese Emotes ablehnen:" samt Namensliste (`import-confirm-dialog.ts:203-208`,
`de.json:1035-1038`), informativ, die Zeilen bleiben in `toAdd` („7TV entscheidet",
`import-preview.ts:25`). **Damit sind es zwei unabhängige Gründe, warum Baustein 3 der Kern
des Pickers ist und nicht sein Komfort:** erstens der bereits dokumentierte — ohne ihn rechnet der
Dialog auf dem aktiven Set, ein naiver Picker schriebe ins falsche Set bzw. meldete für den
Gleichkanal-Fall „nichts zu tun" (oben); zweitens dieser — selbst mit richtigem Ziel-`setId` wäre
ohne die Zielliste aus dem gewählten Set die Kollisionsvorschau blind, und das Mod-Team erführe erst
nach rund 190 roten Zeilen, was ein Blick vorher gesagt hätte. Der erste Grund betrifft **wohin**,
der zweite **was ankommt**; Baustein 3 löst beide mit derselben Zielliste.

**Zwei Entscheidungen des Betreibers für den Dialog (siebte Fassung; Codex D5/D10) — nicht mehr
Plan-Sache:**

- **Namenskollisionen bleiben draußen aus der Warteschlange und werden als eigene Gruppe gezeigt.**
  Die heutige Regel „die Zeilen bleiben in `toAdd`, 7TV entscheidet" (`import-preview.ts:9-12,25`)
  wird für den Fall *gleicher Name, andere ID* aufgehoben: `buildImportPreview` nimmt diese Zeilen
  aus `toAdd` heraus, der Dialog zeigt sie als Gruppe „N Emotes tragen einen Namen, der im Zielset
  schon vergeben ist — werden nicht übertragen" mit Namensliste (das bestehende
  `app-name-preview-list`-Idiom, `import-confirm-dialog.ts:203-208`), und der Lauf enthält sie
  nicht. Folgen, die damit von selbst richtig werden: `projectSlots(occupied, capacity,
  toAdd.length)` (`import-confirm-dialog.ts:397`, `slot-projection.ts:26`) rechnet ohne sie — für
  das ganze Hauptset 687 + 232 = 919 statt der heute angezeigten 687 + 424 = 1111 mit
  Überlauf-Banner (`:170-176`); `emoteCount` im Audit-Eintrag zählt, was gesendet wurde; und der
  Lauf hat **keine erwartbaren Fehlschläge** mehr — jede `failed`-Zeile im Dock ist dann ein
  echter Befund, kein vorhergesagter. Der Grund für die Umkehr ist die Größenordnung: bei 192 von
  424 wäre der Lauf zur Hälfte ein Fehlerprotokoll und zöge rund 190 Tickets aus dem
  `emote_set_change`-Eimer für nichts (`seven-tv-import.service.ts:52-53`). Das ist eine
  Vertragsänderung am Bestätigungsdialog (Eintrag in DECISIONS 2026-09-06, #72, der die Vorschau als
  informativ festlegt) und gehört in den DECISIONS-Eintrag 2. **Unverändert:** `invalidNames`
  (`import-preview.ts:13-17`, Blockliste beobachteter Ablehnungen) bleibt informativ und im Lauf —
  eine andere Mechanik, von der Entscheidung nicht berührt.
- **Alias-Abweichungen werden weiter übersprungen, aber sichtbar als eigene Gruppe.** Für *gleiche
  ID, anderer Alias im Zielset* (bei HandOfBlood rund zehn) bleibt es beim Überspringen per ID —
  **keine Umbenennung auf 7TV in #200**. Neu ist die Sichtbarkeit: der Dialog zeigt sie getrennt von
  `alreadyPresent` als Gruppe „N sind vorhanden, heißen dort aber anders", je Zeile Quellname und
  Zielalias. Dafür braucht die Vorschau den **Alias je Zielset-Eintrag** — den liefert die Zielliste
  aus Baustein 3 (Alias → `name`), und der Lesepfad nach Set-ID muss dafür **jeden Eintrag**
  behalten, nicht nach ID deduplizieren, wie 6.2 es für #74 ohnehin verlangt; bei einem
  #74-Duplikat gilt „heißt anders" nur, wenn **kein** Alias des Eintrags dem Quellnamen gleicht.
  `filterAlreadyPresent` bleibt, wie sie ist (ID-Vergleich, `already-present-filter.ts:151`) —
  sie ist das Sicherheitsnetz vor dem Lauf, nicht die Anzeige. **Ausblick, nicht Umfang:** eine
  zweispaltige Auflösungstabelle (Quellname links, Zielname rechts, Auswahl je Zeile: behalten,
  umbenennen, überspringen) ist eine eigene, spätere Entwurfsrunde und wird unter Issue #201
  nachgehalten; dieses Konzept baut sie nicht.

*Fünfte Fassung, Zahlen in der siebten korrigiert:* Für den Anlass selbst liefert der Zwischenweg
(12.4) die Vorschau **ohne** Baustein 3, weil dort Ziel-Set und aktives Set des Zielkanals
zusammenfallen — aber mit den **ausgelieferten** Regeln, nicht mit den beiden Entscheidungen oben:
338 per ID vorhanden (davon rund zehn unter anderem Alias, ohne eigene Gruppe), rund 424
hinzuzufügen, davon 192 mit vergebenem Namen **im Lauf, bis das Mod-Team sie im Dialog abwählt**,
rund 232 landen.

**Baustein 4 — `sync-imported` trägt die Ziel-Set-ID mit.** `SyncImportedRequest` bekommt
`TargetEmoteSetId`, nullbar (7.4 sagt, warum nicht Pflicht). Der Service schreibt
`TargetType = "emoteSet"`, `TargetId = <Set-ID>` und in den Details `targetEmoteSetId` sowie
`targetIsActiveSetOfChannel` (Vergleich mit `Channel.ActiveEmoteSetId` im Schreibmoment) — genau die
Form, die 7.4 für getrackte Ziele festlegt. Ohne
Schemaänderung: 4.6. `AuditLogQueryService.ProjectDetail` liest die neuen Felder, sonst verlieren
sie still ihre Herkunft (Warnung am Endpunkt, `EmoteEndpoints.cs:144-145`); die Audit-Ansicht nennt
das Ziel-Set, wenn es nicht das aktive war. Regel 3: der Commit trägt den DECISIONS-Eintrag
(Abschnitt 10, Eintrag 2).

**Details für den Plan, beim Schreiben gefunden — Schnittstellen zwischen den Bausteinen und dem
Rest des Vorhabens:**

- *Ziel-Picker → ungetrackte Sets (7.3).* Bruchfrei, **wenn** `ImportTargetChoice`
  von Anfang an so geschnitten wird, dass `emoteSetId` der Schlüssel ist und `channelName` das Attribut der
  getrackten Klasse: heute Pflicht, später `string | null` — eine Typverbreiterung an drei
  Aufrufstellen (`import-flow.ts:94`, `seven-tv-import.service.ts:296-301`,
  `usage-stats-page.ts:1494-1514`), kein Umbau. Die zweite Klasse hängt darunter, aus derselben
  serverseitigen Angebotsliste (7.3). **Keine Kollision**, aber eine Form, die jetzt zu wählen ist.
- *Baustein 3 → Bedarfsabruf aus 6.x.* Der Lesepfad nach Set-ID mit Set-ID als Cache-Schlüssel hinter dem
  Härtungs-Dekorator **ist** der Pfad, den 6.4 fordert. Was er **nicht** beantwortet: die TTL-Frage aus 6.4/11.4 — der Dialog
  lädt einmal, die Nutzungsseite bei jedem `usage.flushed`; die 60 s reichen hier und sagen über
  dort nichts. Der `ForeignEmoteSetCache` ist heute nach Kanalname geschlüsselt (6.4); der Set-Pfad
  braucht einen zweiten Schlüsselraum im selben Cache, sonst überschriebe „Set X von Kanal A" den
  Eintrag „aktives Set von Kanal A". **Keine Kollision**, ein Detail für den Plan.
- *Baustein 4 → Audit (7.4).* Identische Form (`TargetType`/`TargetId`, `targetEmoteSetId`,
  `targetIsActiveSetOfChannel`); 7.4 stellt den set-zentrierten Endpunkt für ungetrackte Ziele
  daneben. Das Feld ist nullbar (7.4 sagt, warum).
- *Baustein 1 → 6.1.* Derselbe Endpunkt; 6.1 liest ihn, statt einen zweiten zu bauen. `isPersonal`
  kommt dazu, was 6.1 nicht vorgesehen hatte und brauchen wird.
- *Eine echte Spannung, beim Schreiben gefunden — in der achten Fassung endgültig aufgelöst:* 7.4
  verließ sich darauf, `editableEmoteSetIds` serverseitig prüfen zu können. Das geht nicht (11.1);
  die siebte Fassung stellte 7.4 auf eine Besitzer-Prüfung um, die achte den Picker auf dieselbe
  Quelle — beide lesen `editor_of` (7.3, 7.4); nichts daran ist offen.

---

## 8. Voting für nicht-aktive Sets (L) — samt der Falle

**Vertrag.** `VoteSession.EmoteSetId` nullbar. Null = heutiges Verhalten. Gesetzt = eine
**Set-Abstimmung**: immer ein fester Wahlzettel (`SessionEmotes` nicht leer — Invariante, keine
dynamische „alle Emotes"-Variante für ein nicht-aktives Set, weil „alle" dann live nachgeschlagen
werden müsste). Beim Anlegen wird die Mitgliederliste einmal live geholt; für Mitglieder ohne Zeile
im Kanal werden `Emote`-Zeilen **einmalig bei Erstellung** angelegt (`IsArchived = true`,
`FirstSeenAt` aus dem Set-Eintrag, wenn 7TV es liefert; `ArchivedAt` null = „nie aktiv gewesen",
konsistent mit der Null-heißt-unbekannt-Konvention in `Emote.cs:17-21`). Der Wahlzettel ist ab
Erstellung fix; eine dauerhafte Zugehörigkeit braucht es weiterhin nicht.

**Warum hier Zeilen entstehen und in der Set-Ansicht nicht (6.5).** Beides sieht dieselben Mitglieder
ohne Zeile, und beides schlüsselt über `SevenTvEmoteId`. Der Unterschied ist der Fremdschlüssel:
`Vote.EmoteId`/`VoteSessionEmote.EmoteId` kaskadieren auf `Emote`, eine Stimme braucht eine Zeile,
die Set-Ansicht braucht keine — sie schreibt nach 7TV, nicht in `Emote`. Der Draht vom Raster zum
Anlegen trägt für eine Set-Session deshalb `SevenTvEmoteIds` (die Identität, über die das Upsert
unten läuft), nicht `EmoteIds`; für Null-Sessions bleibt der heutige Draht. Und: eine Zeile, die
dieses Anlegen erzeugt hat (6.5, Klasse 2b), ist für die Set-Ansicht **keine** Information — ihre
Zahlen bleiben `null`, ihre Kennzeichnung bleibt „nie im aktiven Set gezählt".

**Eingefrorene Anzeigedaten (B4).** `VoteSessionEmote` hält heute nur `EmoteId`
(`VoteSessionEmote.cs:9-10`); Name und Bild kommen aus `Emote`, und `UpsertEmote` überschreibt
`Emote.Name` bei jedem Sync mit dem Alias des **aktiven** Sets (`SevenTvSyncService.cs:515-517`).
Ein Halloween-Wahlzettel zeigte damit den Standard-Alias und könnte sich nach der Erstellung noch
ändern. Deshalb `NameAtCreation`/`ImageUrlAtCreation` auf `VoteSessionEmote` (4.5): für
Set-Sessions Pflicht, gefüllt aus dem Set-Eintrag im Anlegemoment; für Sessions mit
`EmoteSetId = null` bleiben sie leer, und die Anzeige fällt wie heute auf `Emote` zurück — kein
Verhaltensbruch für bestehende Sessions. `GetResultsAsync` projiziert `NameAtCreation ?? Emote.Name`.

**Anlegen ohne Prozess-Sperre (B6).** Die erste Fassung wollte das Anlegen „unter dem
`ChannelSyncGate`" laufen lassen. Das kann nicht funktionieren: `ChannelSyncGate` ist ein
prozessweiter Semaphor-Halter (`ChannelSyncGate.cs:5-22`, Singleton
`ServiceCollectionExtensions.cs:88`), und `SyncChannelAsync` läuft ausschließlich im Worker
(`Worker.cs:140`, `SevenTvPeriodicResyncWorker.cs:75`, `SevenTvEventClient.cs:374,452`), während
die Vote-Erstellung im Api-Prozess läuft — zwei Prozesse, zwei Gates, keine Sperre. Die Berufung
ist gestrichen. Die Api-Seite schreibt **datenbankseitig** — `INSERT … ON CONFLICT ("ChannelId",
"SevenTvEmoteId") DO NOTHING` über die fehlenden Mitglieder, danach die Zeilen des Kanals nach
`SevenTvEmoteId` nachlesen und den Wahlzettel aus dem Gelesenen bauen. Damit ist die **Api**
konfliktverträglich: existiert die Zeile schon (vom Sync, aktiv), nimmt der Wahlzettel sie;
existiert sie nicht, legt das Upsert sie archiviert an, und der nächste Sync reaktiviert sie, falls
das Set inzwischen aktiv wurde.

**Der Worker ist es damit noch nicht — die sechste Fassung hat das als gelöst behauptet, und das
war falsch (Codex D8, am Code bestätigt).** `ReconcileAsync` liest zuerst alle Zeilen des Kanals in
ein Wörterbuch (`SevenTvSyncService.cs:442-444`), staged für jedes fehlende Live-Mitglied ein
gewöhnliches `db.Emotes.Add` (`:539`) und schreibt erst am Ende der Methode (`SaveChangesAsync`,
`:108`). Fügt die Api **zwischen** Lesen und Schreiben dieselbe `SevenTvEmoteId` ein — ihr `ON
CONFLICT` greift nicht, die Zeile fehlte ja noch —, scheitert der Worker bei `SaveChangesAsync` am
Unique-Index: die ganze Runde dieses Kanals ist verloren (Set-ID, Kapazität, alle anderen
Zeilenänderungen), und `RefreshMatchCacheAsync` (`:109`) läuft nicht. Die Aufrufer fangen die
Ausnahme je Kanal ab (`Worker.cs:105`, `SevenTvPeriodicResyncWorker.cs:98`), und der
nächste Tick nach 60 s liest die Api-Zeile als vorhanden und reaktiviert sie — der Schaden ist also
**eine verlorene Sync-Runde und bis zu 60 s alter Match-Cache**, nicht mehr, aber auch nicht das
„gleichgültig, wer gewinnt" der sechsten Fassung. Das Fenster braucht ein Emote, das gleichzeitig
neu im aktiven Set und auf einem Set-Wahlzettel ist; selten, aber genau der Moment eines
Set-Wechsels ist der, in dem Sets verglichen und Abstimmungen angelegt werden.

**Vertrag: beide Schreiber sind konfliktverträglich.** Der Worker behandelt eine Unique-Verletzung
auf `(ChannelId, SevenTvEmoteId)` beim Speichern nicht als Fehler der Runde, sondern als Signal,
dass ein zweiter Schreiber schneller war: er verwirft die gestagten Einfügungen, liest die Zeilen
des Kanals neu und wiederholt die Abgleichrunde **einmal**; erst ein zweiter Konflikt ist ein Fehler.
Die Alternative — ein datenbankseitiger Advisory-Lock je Kanal, den Api-Upsert und Worker-Abgleich
beide nehmen — wäre die stärkere Sperre über Prozessgrenzen hinweg (genau das, was `ChannelSyncGate`
nicht ist), verlängert aber jeden Sync um eine Sperre für einen Fall, der selten ist; der Plan darf
sie wählen, wenn das Wiederholen sich als unhandlich erweist. Gebunden ist er an den Test (Regel 11,
`Infrastructure.Tests/Integration`): zwei unabhängige `AppDbContext`-Instanzen, **die Verschränkung
erzwungen** — der Worker-Pfad hat gelesen, die Api fügt ein, der Worker speichert — nicht nur zwei
serielle Reihenfolgen, die den Fall gar nicht treffen. Beide müssen durchkommen, und die Zeile muss
danach den Zustand des Syncs tragen.

**Die Falle — belegt, und größer als im Brief.** Die Regel „archiviert = nicht wählbar" steht
zweimal im Code: `IsEmoteVotableAsync` (`VoteSessionService.cs:309-317`) sperrt Votes auf
archivierte Emotes, und **`AllEmoteIdsEligibleAsync` (`:298-307`) weist bereits beim Anlegen jeden
Wahlzettel ab, der eine archivierte Id enthält** („All-or-nothing"). DECISIONS 2026-08-01 (Zeile
6897) hat das für Subset-Sessions so festgelegt: amber-Badge „Nicht mehr im Set"
(`de.json:778 archivedBadge`), weitere Votes gesperrt. Eine Halloween-Abstimmung außerhalb der
Halloween-Phase bestünde per Definition aus archivierten Zeilen — sie wäre nach heutiger Regel
**nicht anlegbar**, und wäre sie es, von Anfang an vollständig gesperrt.

**Die Regel wird verschoben:** von „nicht archiviert" auf **„Mitglied des Sets, um das es in dieser
Session geht"**. Für Sessions mit `EmoteSetId = null` ist das Set das aktive, und Mitgliedschaft
heißt `!IsArchived` — heutiges Verhalten, Badge und Sperre unverändert. Für Set-Sessions heißt
Mitgliedschaft „steht auf dem Wahlzettel"; `IsArchived` ist dort kein Kriterium, weder beim Anlegen
noch beim Abstimmen. Was Set-Sessions **nicht** haben: das Live-Signal „hat das Set mid-session
verlassen" — dafür bräuchte es Subscriptions auf das Set (ausgeschlossen, Abschnitt 9). Das Badge
wird für Set-Sessions nicht berechnet; der DECISIONS-Eintrag sagt das. Das ist eine
**Vertragsänderung am Voting** und gehört in denselben Eintrag wie L (Abschnitt 10).

**Die zweite Falle: Berechtigung aus Datenvorhandensein (B7).** Selbst eine anlegbare Set-Session
könnte ihren Löschweg nicht zeigen. `BuildResultRow` nullt `useCount` für archivierte Zeilen
(`VoteSessionQueryService.cs:189-191`), und die Detailseite leitet die Löschberechtigung daraus
ab, ob **irgendein** Ergebnis eine Nutzung ungleich null trägt: `canSelectForDelete = hasUsageData`
(`vote-session-detail-page.ts:211-220`). Eine Session über ein nie aktives Set ist damit für den
Manager durchgehend `null` — keine Zelle wählbar, das Panel nie sichtbar, und die korrekte Set-ID
am Panel käme nie zum Einsatz. Der Code kennt das Problem bereits für den Sonderfall „vollständig
archivierter Subset-Wahlzettel" und holt sich **genau deshalb** `canManage` aus `/permissions`
(`:194-205`) — nutzt es aber nur für den Beenden-Knopf. Das ist derselbe Fehler wie in 6.2 in
anderem Gewand: Datenmangel als Aussage lesen. Vertrag:

- **Berechtigung kommt aus der Berechtigung.** `canSelectForDelete = canManage()` (die Antwort
  liegt schon vor); `hasUsageData` gatet nur noch, was tatsächlich Daten braucht (Drilldown,
  Nutzungsspalte). Der dokumentierte Kompromiss „7TV-Editor ohne Manager-Rolle verliert den
  Löschweg auf dieser Seite" (`:215-219`) bleibt bestehen — er wird nicht schlechter, nur nicht mehr
  über den Umweg der Daten erzwungen.
- **„Unbekannte Nutzung" ist ein eigener Zustand, keine Berechtigungsaussage.** `useCount = null`
  heißt in der Ergebnis-DTO künftig genau „nicht berechnet oder unbekannt"; für Manager einer
  Set-Session wird die Nutzung **berechnet** — über `GetTotalsByEmoteIdsAsync` gefiltert auf
  `EmoteSetId` der Session (`VoteSessionQueryService.cs:191`, `UsageStatQueryService.cs:238`), sonst
  zeigte eine Halloween-Abstimmung Standard-Nutzung — und nur für Mitglieder ohne jede Zeile unter
  dieser Set-ID bleibt sie `null`, mit der Kennzeichnung aus 6.2. Für Sessions mit `EmoteSetId =
  null` bleibt alles wie heute.
- **Session-relative Eignung statt kanalglobalem `IsArchived`.** Die Ergebniszeile bekommt
  `eligible` (darf in dieser Session noch gewählt werden) neben dem bestehenden `isArchived`. Für
  Null-Sessions ist `eligible = !isArchived` (heutiges Badge), für Set-Sessions `eligible = true`
  für jeden Wahlzettel-Eintrag. Die Seite gatet Votes und Badge auf `eligible`, nicht mehr auf
  `isArchived`.
- Die Detailseite reicht dem Mass-Delete-Panel heute `activeEmoteSetId()`
  (`vote-session-detail-page.html:155-157`, `.ts:844`); für eine Set-Session muss es
  `session.emoteSetId` sein, sonst löschte „aus der Abstimmung heraus löschen" im falschen Set.
  Der Kanalstatus wird dafür nicht mehr gebraucht, wenn die Session ihr Set trägt.

---

## 9. Was ausdrücklich draußen bleibt (M)

- **Keine EventAPI-Subscriptions für nicht-aktive Sets.** Die Registry hält ein Ziel je Kanal
  (`SevenTvSubscriptionTarget(EmoteSetId, SevenTvUserId)`, `SevenTvSubscriptionRegistry.cs:8,37`),
  zwei Subscriptions je Kanal (`:33-34`); 7TV meldet `subscription_limit` im Hello (gemessen 500,
  `SevenTvEventClient.cs:259`), die 90-%-Warnung steht in `:277`. Jedes zusätzliche Set kostete eine
  Subscription je Kanal und halbierte die Reichweite bis zum Sharding — für Sets, die sich selten
  ändern und an denen nichts zeitkritisch hängt. Ein Set-Wechsel selbst kommt weiter über `user.*`.
- **Keine `EmoteSet`-Entität, keine Zugehörigkeits-Tabelle** (Abschnitt 3).
- **Keine Sicht „alle Sets zusammen".** In der ersten Fassung „offen, nicht abgelehnt"; vom
  Betreiber inzwischen **verworfen**. Sie wäre billig (Set-Filter weglassen), aber ein Control mehr,
  und die strikte Trennung ist der Punkt des Vorhabens.
- **Keine automatische Teilung eines Backfill-Laufs** an Set-Grenzen (5.3) — der Aufrufer teilt.
- **Keine Schutzmechanik in der Set-Ansicht** — keine Schwelle, kein Zurückhalten von Bändern, kein
  Bestätigungsschritt beim Löschen aus einem nie beobachteten Set. Nur die Tatsachenangabe aus 6.2.
- **Kein Rückweg** der neuen Zahlen in irgendeinen Score (DECISIONS „kein Rückweg von Chat-Nutzung
  in den Beliebtheits-Score" gilt unverändert).

---

## 10. Fällige DECISIONS-Einträge

Regel 3: im selben Commit wie die Änderung. Mindestens drei, weil drei Verträge sich ändern:

1. **Zählen pro Set** (A/D/E): `UsageStat.EmoteSetId` als **lokal beobachtetes** Set,
   Conflict-Target dreispaltig, Migration der Bestandszeilen aufs beim Umstieg aktive Set — für
   Kanäle **ohne** Wechsel unter der Bedingung „nie gewechselt", für Kanäle **mit** Wechsel nach der
   vom Betreiber bestätigten Zuordnungsliste (Kanal-Id, alte/neue Set-ID, UTC-Grenzen; für
   HandOfBlood wiederholt der Eintrag die Liste), mit der Gegenprobe 11.6 und ihren vier
   Abbruchgründen (4.3), der Unschärfe des Grenztags, dem Verbleib des Testkanals und des
   Wegwerfkanals (beide gepurgt). `ChannelEmoteSetObservation` als Beobachtungsintervalle mit den
   Schließ-/Öffnungsregeln aus 4.4, dem partiellen Unique-Index, dem begrenzten Zweck und der
   Migrations-Saat aus derselben Liste. Der Deploy als **Wartungsfenster** mit Worker- **und**
   Api-Stopp, was darin verloren geht und wie zurückgerollt wird (12.3). Nennt ausdrücklich, dass
   das #69-Design den breiteren Key abgelehnt hatte und warum er hier trotzdem kommt (4.1), und dass
   die beiden früheren Zähler-Migrationen additiv waren, diese nicht.
2. **Das Ziel darf ein ungetracktes Set sein — nach Bestätigung; der Dialog nimmt Kollisionen
   heraus** (J/K): revidiert den Satz „das Zielset bleibt ein getrackter Kanal aus `listMine()`"
   aus dem Eintrag vom 2026-09-09 (#147) **in der abgeschwächten Form**: getrackte Ziele bleiben die
   Vorgabe, ungetrackte sind wählbar, wenn der Nutzer sie nach Ansage von Besitzer und Setnamen
   bestätigt hat (7.3) — genauer: ein Set eines Accounts, **dessen Editor der Nutzer nachweislich
   ist**, nachgewiesen über dieselbe `editor_of`-Abfrage, die schon `listMine()` und die
   Autorisierung speist. Das ist näher am ursprünglichen Eintrag, als die siebte Fassung es war:
   gelockert wird nur „getrackt", nicht „Editor"; die Rechteprüfung am Schreibvorgang lag immer bei
   7TV (`LACKING_PRIVILEGES`), und die Angebotsliste beantwortet nicht „wohin wolltest du" — das
   tut die Bestätigung. **R2 aus dem Eintrag vom 2026-09-06 (#72) bleibt unberührt:** der Token
   wird wie heute erst vor dem Lauf abgefragt (die siebte Fassung hatte hier eine Revision
   vorgesehen, die achte hat sie zurückgenommen, 7.3). **Revidiert die informative Vorschau des
   Eintrags vom 2026-09-06:** Namenskollisionen (gleicher Name, andere ID) bleiben
   draußen aus der Warteschlange und erscheinen als eigene Gruppe; Alias-Abweichungen (gleiche ID,
   anderer Alias) werden weiter übersprungen, aber als eigene Gruppe gezeigt; keine Umbenennung auf
   7TV, Ausblick unter #201 (7.5). Audit-Vertrag aus 7.4 in beiden Wegen: Zielkanal plus
   `TargetType`/`TargetId` für getrackte Ziele; set-zentrierter Endpunkt ohne Kanalzeile mit
   Besitzer-Prüfung und Besitzer im Eintrag für ungetrackte. Korrigiert ausdrücklich die Annahme,
   `sync-imported` laufe heute gegen den Seitenkanal — es läuft gegen den Zielkanal. Ein Eintrag,
   nicht zwei (Vorabscheibe/Hauptrunde) — die fünfte Fassung hat sie zusammengelegt.
3. **Voting: „Mitglied des Sets der Session" statt „nicht archiviert", Berechtigung statt
   Datenvorhandensein** (L): revidiert den Archiviert-Badge-Absatz vom 2026-08-01, benennt die
   Falle (Anlegen **und** Abstimmen), die Invariante „Set-Session ⇒ fester Wahlzettel", dass
   Set-Sessions kein Mid-Session-Badge haben, die eingefrorenen Anzeigedaten auf `VoteSessionEmote`,
   das datenbankseitige Upsert beim Anlegen, warum `ChannelSyncGate` es nicht sein konnte, **und
   dass der Worker eine Unique-Verletzung beim Abgleich als Wiederholungssignal behandelt** (8,
   Codex D8) — samt der Entkopplung `canSelectForDelete` ↔ `hasUsageData` und `eligible`.

4. **Zeilenidentität der Set-Ansicht und der Buchhaltung: `SevenTvEmoteId`** (6.5, sechste
   Fassung): der Schlüssel des Rasters und des inneren `track` ist die 7TV-Id, `Emote.Id` ist
   nullbare Nutzlast ohne Aussagewert; Queue-Key des Delete-/Restore-Laufs ist `sevenTvEmoteId`
   (Nachtrag zu R3 vom 2026-09-05: der Import-Lauf war das Muster, nicht die Ausnahme;
   `doneKeys` ist die Rückmelde-Identität); Protokollzeile mit optionalem `emoteId`, Parser
   akzeptiert `null`; **Body von `sync-deleted`/`sync-restored` = `{ emoteSetId, sevenTvEmoteIds }`**,
   Match auf `(ChannelId, SevenTvEmoteId)`, damit drei Buchhaltungsendpunkte eine Identität sprechen
   — **der alte Body `{ emoteIds }` bleibt übergangsweise gültig**, der Laufdatensatz friert die
   Set-ID ein (Codex D7); **`/series` benennt seine Einträge nach `SevenTvEmoteId`** und nimmt
   `emoteSetId`, beide Frontend-Caches schlüsseln mit Set (Codex D6, revidiert den Wire-Format-Satz
   des Eintrags zu `/usage-stats/series`); der Nutzungs-Export nennt Set-ID und Setnamen und
   serialisiert `null` als leere Zelle bzw. `null` (Codex D9); die Set-Ansicht legt keine Zeilen an,
   das Voting schon, und warum (Fremdschlüssel); Lösch- und Restore-Bestätigung nennen das Set.
   Nennt die beiden korrigierten Sätze (6.2, 7.1) und dass ein Lauf über Guid-lose Zeilen vor
   diesem Eintrag ohne Rückmeldung und ohne Protokollzeile geblieben wäre. Dieser Eintrag trägt auch
   die Papier-Variante von `sync-deleted`/`sync-restored` aus 7.1 als Nachtrag zum Eintrag vom
   2026-07-26 (Soft-Archive) — bis zur fünften Fassung war sie als eigener Nachtrag vorgesehen; da
   beide denselben Body ändern, ist es einer.

---

## 11. Offene Messpunkte vor dem Plan

Regel „Fremd-APIs live prüfen": Die Sonden 1 und 2 sind am **2026-09-19 live gegen 7tv.io
gelaufen** — aus der Session heraus; der Klassifikator, der solche Aufrufe sonst blockt, hat es
diesmal nicht getan. Ob er es beim nächsten Mal tut, ist nicht vorhersagbar; die verbleibenden
Sonden (4, 5, 6) führt im Zweifel der **Betreiber selbst** per `curl` aus. Je Frage ein Aufruf; eine
GraphQL-Fehlermeldung, die ein Feld nicht kennt, ist ein Ergebnis und kein Anlass für Varianten.
Platzhalter: `<TWITCH-ID>` (numerische Twitch-User-ID), `<SET-ID>`.

**1. Liefert 7TV die Sets eines Users mit Namen — und den Besitzer? Gemessen: ja, ohne Anmeldung.
`editableEmoteSetIds` dagegen nur mit Anmeldung.** Drei Befunde:

- **Die Abfrage der zweiten Fassung war syntaktisch falsch.** `userByConnection` nimmt kein Argument
  `id`, sondern `platformId` (Typ `String!`). 7TV wörtlich: `Field "userByConnection" argument
  "platformId" of type "UserQuery" is required but not provided` und `Unknown argument "id" on field
  "userByConnection"`. Die Sonde unten ist korrigiert.
- **`editableEmoteSetIds` verlangt eine Anmeldung.** Unauthentifiziert antwortet 7TV mit
  `LOGIN_REQUIRED you are not logged in`, `extensions.code = "LOGIN_REQUIRED"`, `status: 401`,
  `path: ["users","userByConnection","editableEmoteSetIds"]` — und der Fehler **nullt das ganze
  Elternobjekt**, die übrigen Felder kommen dann gar nicht mehr. Das Feld ist damit nur im Browser
  lesbar (das Backend sieht den Token nie, DECISIONS 2026-09-09) — und seit der achten Fassung
  braucht es niemand mehr: Picker und Besitzer-Prüfung lesen `editor_of` serverseitig (7.3, 7.4).
- **Die Sets eines Users samt Name, Kapazität und Besitzer kommen ohne Anmeldung, in einem
  Request** (Analyzer: `complexity: 11, depth: 6`, Antwort vollständig):

```
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -d '{"query":"query($pid: String!) { users { userByConnection(platform: TWITCH, platformId: $pid) { id emoteSets { id name capacity owner { id mainConnection { platformDisplayName } } } } } }","variables":{"pid":"<TWITCH-ID>"}}'
```

  liefert je Set `id`, `name`, `capacity` und `owner.mainConnection.platformDisplayName`. Der
  v3-Weg liefert dasselbe minus Besitzer, plus `flags` und `tags`, plus die aktive Set-ID:

```
curl -s https://7tv.io/v3/users/twitch/<TWITCH-ID> | jq '{active: .emote_set_id, sets: [.user.emote_sets[] | {id, name, capacity, flags}]}'
```

  **Persönliche Sets stehen mit in der Liste.** Am Testkanal: drei Sets, darunter
  `{"name":"Personal Emote Set","capacity":5,"flags":4}`; die beiden normalen Sets tragen
  `flags: 0`. Was Bit 4 genau bedeutet, ist **nicht verifiziert** — nur, dass es das persönliche
  Set von den beiden anderen unterscheidet. Ein Picker muss es daran ausschließen oder deutlich
  kennzeichnen (7.5 Baustein 2, 6.1); ob v4 am `EmoteSet` ein entsprechendes Feld trägt, ist eine
  Introspektion, die noch aussteht.

Für ein **einzelnes** Set (Besitzer-Prüfung, 7.4) gibt es den Einzelaufruf — Besitzer inklusive,
ohne Anmeldung:

```
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -d '{"query":"query($id: Id!) { emoteSets { emoteSet(id: $id) { id name capacity owner { id mainConnection { platformDisplayName } } } } }","variables":{"id":"<SET-ID>"}}'
```

Folgen: 6.1 und der Ziel-Set-Picker (7.3, 7.5) lesen die Set-Liste **je Account** über dessen
Twitch-ID — ein Request, kein Token; für die `editor_of`-Accounts ist das die Twitch-ID aus dem
Grant (7.3); 7.2 dieselbe Abfrage für den fremden Kanal; 7.4 prüft den Besitzer.

**2. Wie groß ist die Schnittmenge zwischen Haupt- und Saison-Set? Gemessen: rund die Hälfte.**
Am 2026-09-19 **direkt an HandOfBlood** gemessen (Twitch `49140130`, über die v3-GQL-Suche
aufgelöst). Seine drei Sets sind `HandOfBlood's Emotes` (aktiv, 762 Einträge / 760 eindeutig),
`Halloween Set` (687 / 686) und `Christmas Set` (841 / 840) — alle drei mit Kapazität 1000 und
`flags: 0`, kein persönliches Set darunter.

| Menge | Emotes | Anteil |
|---|---|---|
| Haupt ∩ Halloween | **338** | 49 % des Halloween-Sets |
| Haupt ∩ Christmas | **345** | 41 % des Christmas-Sets |
| nur in Halloween | 348 | |
| im Hauptset, **nicht** in Halloween | **422** | ID-Sicht — **nicht** die Menge des Anlasses, s. „Namen gegen IDs" unten |

*Korrektur (vierte Fassung):* Die dritte Fassung nannte die letzte Zeile „die operativ wichtigste:
422 Emotes des Hauptsets fehlen im Halloween-Set, und genau daraus will das Mod-Team übertragen".
Das ist die ID-Sicht, und sie führt für den Anlass in die Irre — 7TV prüft beim `ADD` den **Alias**,
nicht die ID. Nach Namen fehlen **242**; die Messung steht unten unter „Namen gegen IDs".

Gegenprobe an `papaplatte` (Twitch 50985620, `Emotes` 983 / `Halloween 2024` 863 / `Christmas 2025`
940 Einträge bei 939 eindeutigen): Haupt ∩ Halloween **555** (64 %), Haupt ∩ Christmas **453**
(48 %). Zwei unabhängige Kanäle, derselbe Befund — die Schnittmenge liegt zwischen 41 und 64 %.

Das trägt Entscheidung A (Abschnitt 3): genau die Emotes der Schnittmenge sind die, deren
Kanal-Zähler heute zwei Phasen zu einer Summe macht — bei disjunkten Sets hätte A nichts geliefert,
was die heutige Summe nicht auch sagt. `GET /v3/emote-sets/{id}` liefert das ganze Set ohne
Paginierung (933/933 gemessen am 2026-09-01; 841/841 am 2026-09-19). Die Sonde, wiederholbar:

```
for s in <SET-ID-1> <SET-ID-2> <SET-ID-3>; do curl -s "https://7tv.io/v3/emote-sets/$s" | jq -r '.emotes[].id' | sort -u > "/tmp/set-$s.txt"; done; wc -l /tmp/set-*.txt; comm -12 /tmp/set-<SET-ID-1>.txt /tmp/set-<SET-ID-2>.txt | wc -l
```

**Namen gegen IDs — nachgemessen am 2026-09-19 (spät), auf Hinweis des Betreibers.** Anlass war
die Beobachtung, dass HandOfBloods Halloween-Set viele Emotes **mit demselben Namen, aber anderer
7TV-ID** enthält — ein Halloween-`Stare` ist für den Zuschauer dasselbe `Stare`, nur in der
Saisonfassung. Die Messung oben verglich Sets nur nach IDs; hier dieselben beiden Sets nach dem
Alias (`.emotes[].name` aus `GET /v3/emote-sets/{id}`):

| Menge | Wert |
|---|---|
| Hauptset `HandOfBlood's Emotes` (`01GV88A38G0006FW5TVZVMG507`) | 762 Einträge, **762 eindeutige Namen**, 760 eindeutige IDs |
| Halloween-Set (`01J94NYQR0000D15QN0BDGN85E`) | 687 Einträge, **687 eindeutige Namen**, 686 eindeutige IDs |
| Namen in **beiden** Sets | **520** |
| … davon mit **gleicher** ID (dasselbe Emote in beiden Sets) | **328** |
| … davon mit **anderer** ID (Saisonvariante gleichen Namens) | **192** |
| Namen **nur** im Hauptset | **242** |
| Namen nur im Halloween-Set | 167 |

Beispiele für gleichnamige Varianten: `5Head`, `AINTNOWAY`, `ALARM`, `Aware`, `BASED`, `Bedge`,
`Binoculous`.

**Die beiden Sichten sind in beide Richtungen nicht deckungsgleich, und keine ist „die richtige".**
Nach IDs liegen 338 Emotes in beiden Sets, nach Namen mit gleicher ID nur 328: rund zehn IDs liegen
in beiden Sets, aber unter verschiedenem Alias (oder als einer der Doppel-Alias-Einträge, s. u.) —
aus den Summen allein ist das nicht exakt auflösbar. Umgekehrt hat das Hauptset 762 eindeutige
Namen bei nur 760 eindeutigen IDs: zwei IDs liegen dort unter je zwei Aliasen, der #74-Fall (11.5).
Ein Set ist also weder eine Menge von IDs noch eine Menge von Namen, sondern eine Liste von
(Alias, ID)-Einträgen, und jede Frage muss sagen, welche Achse sie meint:

- **Zählen** fragt nach Zeilen, und Zeilen hängen an der ID (`(ChannelId, SevenTvEmoteId)`): die
  Schnittmenge nach ID (338) ist die Zahl, an der Entscheidung A hängt (Abschnitt 3); die 192
  Namensvettern sind schon heute getrennte Zeilen und werden getrennt gezählt (5.2).
- **Anzeigen** fragt nach dem, was ein Nutzer unter einem Namen erwartet: die 192 sind der neue
  Anzeigefall in 6.2.
- **Schreiben** fragt nach dem, was 7TV prüft, und das ist der **Alias**: `ADD` lehnt mit `this
  emote has a conflicting name` ab, sobald der Alias im Zielset vergeben ist; den Alias wegzulassen
  hilft nicht, weil `EmoteSetEmote.alias` ein `String` ist und bei fehlender Angabe mit
  `emote.default_name` gefüllt wird; der Vergleich ist case-sensitiv und unnormalisiert
  (A16-Analyse aus 7TVs Quellcode, `docs/Feature-Ideen-2026-08-01.md:486-495`). Für den Anlass
  zählt deshalb die Namenssicht: **242** kollisionsfreie Namen, nicht 422 — von denen der
  ausgelieferte Lauf rund **232** überträgt, weil er per ID überspringt und etwa zehn der 242 als
  dieselbe ID unter anderem Alias schon im Zielset liegen (7.5, 12.4).

Die Sonde, wiederholbar (Namen sind je Set eindeutig — gemessen —, sonst müsste der `join`
vorher deduplizieren):

```
for s in <SET-ID-1> <SET-ID-2>; do curl -s "https://7tv.io/v3/emote-sets/$s" | jq -r '.emotes[] | "\(.name)\t\(.id)"' | LC_ALL=C sort > "/tmp/set-$s.tsv"; done
LC_ALL=C comm -12 <(cut -f1 /tmp/set-<SET-ID-1>.tsv) <(cut -f1 /tmp/set-<SET-ID-2>.tsv) | wc -l   # Namen in beiden
LC_ALL=C join -t $'\t' /tmp/set-<SET-ID-1>.tsv /tmp/set-<SET-ID-2>.tsv | awk -F'\t' '$2==$3' | wc -l   # davon gleiche ID
LC_ALL=C comm -23 <(cut -f1 /tmp/set-<SET-ID-1>.tsv) <(cut -f1 /tmp/set-<SET-ID-2>.tsv) | wc -l   # nur im ersten
```

**3. Kommt die Set-ID im Schreibpfad an?** Beantwortet in 5.1: nein, nirgends. Kein Messpunkt mehr,
sondern Arbeit (5.2).

**4. Kostenabschätzung des Bedarfsabrufs.** Konstanten belegt (6.4): zwei Permits je Abruf eines
900er-Sets aus 60 pro Minute. **Offen** ist die tatsächliche Reload-Frequenz der Nutzungsseite bei
Chat-Betrieb (wie viele `usage.flushed`/`channel.synced` pro Minute ein offener Kanal wirklich
sieht) — daraus folgt, ob 60 s TTL reichen. Messbar an der Dev-Box mit einem offenen Tab und dem
Netzwerk-Panel, ohne Prod.

**5. Doppelte Emote-ID im Set (#74) — kein Randfall, sondern die Regel.** Am 2026-09-19 gemessen:
**alle** untersuchten Sets tragen Duplikate. HandOfBlood: Hauptset 762 Einträge / 760 eindeutig
(**zwei** Duplikate), Halloween 687/686, Christmas 841/840. papaplatte: `Christmas 2025` 940/939.
Dazu der bekannte Fall `brudivoeller_tv`. Zwei Folgen: erstens ist 6.2 damit gemessen begründet und
nicht vorsorglich — Deduplizieren würde bei jedem dieser Sets echte Einträge samt Slot verschlucken.
Zweitens, und wichtiger: **HandOfBloods aktives Set ist betroffen**, #74 wirkt also heute im
Live-Sync, nicht erst in der geplanten Set-Ansicht — und zwar auch in der **Zählung** (siebte
Fassung, aus Codex D5): `ReconcileAsync` ruft `UpsertEmote` je Eintrag auf, der spätere Alias
überschreibt den früheren in `Emote.Name` (`SevenTvSyncService.cs:449-452,515-517`), und der
Match-Cache hält je Zeile einen Namen (`:403-408`); Chat-Nutzung unter dem anderen Alias eines
Duplikats wird heute nicht gezählt. Das ist eine Tatsache zu #74, nicht zu diesem Konzept (5.2).
Neu dazu, wegen B4: **entfernt ein `REMOVE` per Emote-ID einen oder beide Einträge?** Das lässt
sich nur an einem eigenen Testset mit einem absichtlich doppelt hinzugefügten Emote prüfen; bis
dahin gilt die Ausblendung aus 6.2.

**6. Gegenprobe vor der Migration: passt die bestätigte Zuordnungsliste zum Bestand, und gibt es
Massenarchivierungen außerhalb der Liste?** Seit der siebten Fassung ist die Abfrage **Gegenprobe,
nicht Quelle** (4.3, Codex D1): sie liefert weder das Wechseldatum noch die alte Set-ID, sondern
prüft eine Liste, die der Betreiber bestätigt hat. Sie ist **lesend** und läuft gegen die
Produktionsdatenbank; sie nutzt die Signatur aus Abschnitt 2 — Massenarchivierung an einem Tag ohne
`emotes.syncDeleted` —, die notwendig, aber nicht hinreichend ist. Tabellen- und Spaltennamen sind
die EF-Konventionen aus `AppDbContext.cs` (DbSets `Emotes`, `Channels`, `AuditLogEntries`;
`Emotes."ArchivedAt"` ist `timestamp with time zone`, Migration `20260802195616_AddEmoteArchivedAt`).

Zugang wie bei der Prod-Migration (CLAUDE.md „Prod-Migration"), **vom Betreiber ausgeführt**, nicht
von einer Session: in einer Shell `ssh -N -L 15432:127.0.0.1:5433 vps`, in einer zweiten

```
psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge password=<PROD-PW>' -f set-wechsel-pruefung.sql
```

mit folgendem Inhalt der Datei (alternativ auf dem VPS direkt `docker exec -i emotepurge-postgres
psql -U emotepurge -d emotepurge < set-wechsel-pruefung.sql`):

```sql
-- Read-only. Tage mit auffälliger Massenarchivierung je Kanal, daneben die Audit-Einträge des
-- Kanals an demselben Tag. Ein Tag mit vielen Archivierungen und OHNE emotes.syncDeleted ist die
-- Signatur eines Set-Wechsels. Schwellen (>= 10 Zeilen oder >= 25 % des Bestands) dienen nur der
-- Lesbarkeit; im Zweifel den WHERE-Block weglassen.
WITH archived_days AS (
    SELECT c."Id"          AS channel_id,
           c."ChannelName" AS channel_name,
           c."IsBotActive" AS is_bot_active,
           (e."ArchivedAt" AT TIME ZONE 'UTC')::date AS day,
           COUNT(*)        AS archived_that_day
    FROM "Emotes" e
    JOIN "Channels" c ON c."Id" = e."ChannelId"
    WHERE e."ArchivedAt" IS NOT NULL
    GROUP BY c."Id", c."ChannelName", c."IsBotActive", (e."ArchivedAt" AT TIME ZONE 'UTC')::date
),
row_totals AS (
    SELECT "ChannelId" AS channel_id, COUNT(*) AS emote_rows_total
    FROM "Emotes"
    GROUP BY "ChannelId"
),
audit_days AS (
    SELECT a."ChannelName" AS channel_name,
           (a."OccurredAtUtc" AT TIME ZONE 'UTC')::date AS day,
           string_agg(
               a."Action" || COALESCE(' (' || (a."DetailsJson"::jsonb ->> 'emoteCount') || ')', ''),
               ', ' ORDER BY a."OccurredAtUtc") AS audit_actions
    FROM "AuditLogEntries" a
    WHERE a."Action" IN ('emotes.syncDeleted', 'channel.resync', 'channel.join', 'channel.leave',
                         'channel.rename', 'channel.merge')
    GROUP BY a."ChannelName", (a."OccurredAtUtc" AT TIME ZONE 'UTC')::date
)
SELECT d.channel_name,
       d.is_bot_active,
       d.day,
       d.archived_that_day,
       t.emote_rows_total,
       round(100.0 * d.archived_that_day / t.emote_rows_total, 1) AS pct_of_rows,
       COALESCE(au.audit_actions, '-- kein Audit-Eintrag an diesem Tag --') AS audit_same_day
FROM archived_days d
JOIN row_totals t ON t.channel_id = d.channel_id
LEFT JOIN audit_days au ON au.channel_name = d.channel_name AND au.day = d.day
WHERE d.archived_that_day >= 10
   OR d.archived_that_day * 4 >= t.emote_rows_total
ORDER BY d.channel_name, d.day;
```

**Erwartetes Ergebnis.** Zwei Zeilen, keine dritte: der **Testkanal** des Betreibers (die
Positivkontrolle — mindestens ein Tag mit Massenarchivierung ohne `emotes.syncDeleted`; ein
`channel.resync` am selben Tag widerspricht dem nicht) und **HandOfBlood** mit dem Tag der
bestätigten Grenze aus der Zuordnungsliste (4.3), mit einer `archived_that_day`, die der vom
Betreiber am Wechseltag gemessenen Zahl entspricht (IDs des Hauptsets, die nach der Vorbefüllung
nicht im Halloween-Set liegen — ID-Sonde aus 11.2). Der Wegwerfkanal aus 12.4 erscheint **nicht**:
er ist zu diesem Zeitpunkt gepurgt, und ein Kanal ohne Zeilen hat keine Archivierungen. Danach wird
auch der Testkanal gepurgt (4.3), und die Migration läuft gegen einen Bestand, in dem genau ein
Kanal in der Liste steht. Abweichungen, jede ein Halt:

- **Der Testkanal fehlt.** Die Methode ist widerlegt, ihr Schweigen über andere Kanäle wertlos;
  Ursachen suchen (Wechsel vor der `ArchivedAt`-Spalte? Rückwechsel?), bevor irgendetwas migriert.
- **HandOfBlood fehlt, oder Tag bzw. Zahl passen nicht zur Liste.** Die Liste ist falsch oder
  veraltet (Rückwechsel, Restore, zweiter Wechsel) — der Betreiber korrigiert sie; die Migration
  bricht bei derselben Prüfung ab (4.3, Prüfung 2) und schätzt nichts.
- **Ein Kanal erscheint, der nicht in der Liste steht.** Entweder ein Wechsel, den niemand kannte,
  oder eine Massenlöschung auf 7TV (dieselbe Signatur, Abschnitt 2) — der Betreiber klärt, welches,
  und ergänzt die Liste oder nicht; die Migration bricht bis dahin ab (4.3, Prüfung 3).

**Grenzen der Methode.** Die Abfrage kann eine Behauptung **widerlegen, nicht beweisen.** Erstens
sieht sie nichts aus der Zeit vor der `ArchivedAt`-Spalte (2026-08-02): ältere Archivierungen tragen
`NULL` („archiviert, Datum unbekannt", `Emote.cs:17-21`). Zweitens erkennt sie keinen Wechsel
zwischen zwei Sets mit weitgehend gleichem Inhalt — die Differenz ist dann klein, die Signatur
fehlt. Drittens hält `ArchivedAt` nur die **letzte** Archivierung: ein Wechsel und Rückwechsel
reaktiviert die Zeilen und löscht das Datum (`SevenTvSyncService.cs:523`), die Spur ist dann weg.
Viertens unterscheidet sie einen Wechsel nicht von einer Massenlöschung auf 7TV. Genau deshalb ist
sie Gegenprobe zu einer bestätigten Liste und nicht deren Quelle. Beides gehört so in den
DECISIONS-Eintrag.

---

## 12. Berührte Issues, Timing und Deploy

### 12.1 Berührte Issues

- **#76** (Sync bleibt hängen, wenn ein Set wirklich geleert wird): nicht direkt berührt — die
  Plausibilitätssperre (`TryGuardAgainstImplausibleWipeAsync`, `SevenTvSyncService.cs:351-370`)
  zählt weiter `!IsArchived` je Kanal. Eine Wechselwirkung gibt es: solange die Sperre greift,
  bleibt die alte Set-ID stehen (`:86` wird nicht erreicht), Zählung und Beobachtungs-Log buchen auf
  das alte Set. Das ist im Modell korrekt („was wir für aktiv halten", 5.2), aber ein Grund, #76 vor
  diesem Ausbau zu schließen, damit eine Blockade nicht auch noch die Set-Zuordnung verfälscht.
- **#74** (Set mit doppelter Emote-ID gilt bei jedem Resync als geändert): berührt die Verbindung
  Live-Liste ↔ Zeilen in 6.2, die Duplikate jetzt **sichtbar** hält statt wegzudeduplizieren, und
  die offene Frage aus 11.5 zum `REMOVE`-Verhalten; am Defekt selbst ändert dieses Konzept nichts. Der Namensvetter-Fall aus 6.2 (ein Name, zwei IDs, zwei Sets — 11.2 „Namen gegen IDs") ist
  **kein** #74-Fall und wird dort nicht mitverhandelt.
- **#69** (Chat-Log-Backfill): Abschnitt 5.3 — Set-ID als Pflichtparameter, Teilung an Set-Grenzen,
  Harness bekommt die Set-ID als Eingabe; und der Unique-Key, den das #69-Design nicht anfassen
  wollte, ist nach diesem Konzept bereits dreispaltig.
- **#201** (Auswahl aus Protokoll laden): berührt an zwei Stellen — ein Protokoll trägt
  `meta.emoteSetId`; wird es als Auswahl geladen, ist der Abgleich gegen das **gewählte** Set zu
  führen, nicht gegen das aktive. Und (sechste Fassung, 6.5): der Abgleich läuft über
  `sevenTvEmoteId`, weil Protokollzeilen künftig `emoteId: null` tragen können und der Schlüssel der
  Auswahl ohnehin die 7TV-Id ist. Dazu, als **Ausblick** (siebte Fassung, Entscheidung des
  Betreibers): die zweispaltige Auflösungstabelle für Alias-Abweichungen — Quellname links,
  Zielname rechts, Auswahl je Zeile — wird dort nachgehalten; #200 zeigt die Abweichungen nur an
  (7.5). Sonst keine Berührung.

### 12.2 Timing: hinter dem 2026-10-08, nicht auf ihm

Laut #200 startet nichts vor dem **2026-10-08** (Messfenster der Chat-Analytics-Arbeit, Epic #118).
Genauer, und das ist eine Auflage: **der Deploy gehört hinter den bindenden Harness-Lauf dieses
Tages, nicht auf den Tag.** Der Umstieg hält den Worker an (12.3) und ändert den Schreibpfad von
`UsageStat` — genau das Messobjekt des Laufs. Das Runbook des Laufs liegt in `infra-docs`, nicht in
diesem Repo; **vor dem Deploy ist dort nachzusehen**, wann der Lauf abgeschlossen ist und ob er
Nachläufer hat (Vergleichslauf, Auswertung gegen Live-Zeilen), die den Zählpfad noch unverändert
brauchen. Dieses Papier kann das nicht nachschlagen und behauptet deshalb keinen Termin — nur die
Bedingung.

**Nichts wird vorgezogen** — auch nicht die Teile ohne Worker- oder Schema-Bezug (Sonden, Ziel-Picker,
Quell-Set-Picker, set-zentrierter Audit-Endpunkt). Die Sonden aus Abschnitt 11 sind reine Lesezugriffe
gegen 7TV und die Gegenprobe 11.6 ein reiner Lesezugriff gegen Prod; **ausführen** kann der Betreiber
sie jederzeit, sie sind kein Deploy. Gebaut und deployt wird das Vorhaben als ein Stück. **Das gilt
ohne Ausnahme.** Die dritte und vierte Fassung hatten hier eine benannt — eine Vorabscheibe vor dem
2026-10-01 für HandOfBloods Set-Wechsel; sie ist in der fünften Fassung entfallen, weil der Anlass
ohne Deploy gedeckt ist (12.4).

**Zwei Tatsachen, die die Ausnahme damals gestützt haben, bleiben als Entwarnung stehen — nicht als
Freibrief.** Erstens ist HandOfBlood vom bindenden Harness-Lauf **ausgenommen** (er ist einen Monat
im Urlaub); sein Set-Wechsel am 01.10. und alles, was der Zwischenweg an seinem Kanal und am
Wegwerfkanal bewegt, berührt die Messung also nicht — weder der Wechsel selbst noch die
Massenarchivierung, die er in unserer Datenbank auslöst (4.3), noch der Nachlauf-Resync des
Zielkanals, noch dessen Purge. Zweitens hätte auch ein reiner Api-/Web-Deploy Worker und Schema nicht angefasst.
Beides zusammen hieß nie „ein Deploy vor dem 08.10. ist harmlos": ein Api-Recreate trifft alle
Kanäle (SSE-Verbindungen, `/api/health`, `docker logs`), und der Publish-Job hätte auch das
Worker-Image mit neuem Digest versehen (DECISIONS 2026-09-08, Pfadfilter). Es heißt nur, dass der
**Zwischenweg** — der gar nichts deployt — die Messung an keiner Stelle berührt, und dass sich bei
einem künftigen Zwang zu einem Deploy vor dem Termin die Frage an diesen beiden Tatsachen entlang
stellen ließe. Solange kein solcher Zwang besteht, wird sie nicht gestellt.

### 12.3 Deploy: ein Wartungsfenster statt gestaffelter Deploys

**Warum überhaupt ein Fenster (B1, belegt).** Der alte Worker schreibt `ON CONFLICT ("EmoteId",
"Date")` (`UsageStatFlushService.cs:71`). Nach der Migration gibt es keinen Unique-Index mehr, der
genau diese zwei Spalten trägt, und PostgreSQL lehnt das Statement ab, bevor es eine Zeile
schreibt. `UsageFlushWorker` stellt den Batch fünfmal zurück und verwirft ihn dann
(`UsageFlushWorker.cs:19,89-103`); der Shutdown-Flush (`:30-39`) schreibt gegen dasselbe Schema und
rettet nichts. Anders als bei den beiden additiven Zähler-Migrationen macht hier kein Default beide
Conflict-Keys verträglich — die alte und die neue Schreibform schließen sich aus. Ein
„Schema zuerst, dann Images" wie bisher (CLAUDE.md „Prod-Migration") hieße: ab der Migration bis
zum neuen Worker-Image geht **jede** Zählung verloren, und nach 2,5 Minuten sogar die gepufferten.

**Die aufwendige Lösung — und warum sie nicht gewählt ist.** Codex empfiehlt drei gestaffelte
Deploys: erst ein Worker, der beide Schreibformen kann und den alten Index toleriert, dann die
Migration, dann der Rückbau. Das ist korrekt und für einen Dienst mit vielen Nutzern die richtige
Form. EmotePurge ist faktisch Closed Beta mit einer Handvoll getrackter Kanäle; drei koordinierte
Deploys mit einem Zwischenzustand im Code, der nur für die Dauer des Umstiegs existiert, kosten
mehr Fehlerfläche, als sie hier Zählungen retten.

**Gewählt: ein Wartungsfenster — Worker und Api anhalten, migrieren, neue Images starten.** Die
sechste Fassung ließ die Api weiterlaufen („sie liest nur gemappte Spalten, eine neue Spalte stört
ihre Abfragen nicht"). Das war spaltenweise richtig und sperrenweise falsch (Codex D3): Backfill,
`SET NOT NULL`, `DROP INDEX` und `CREATE UNIQUE INDEX` laufen in **einer** Migrationstransaktion und
halten ab dem ersten DDL ein `ACCESS EXCLUSIVE` auf `UsageStats`, bis sie committet — jede
Api-Abfrage über diese Tabelle (`/totals`, `/daily`, `/series` aus `UsageStatQueryService`,
Voting-Ergebnisse, die Kanalstatus-Daten aus `EmoteSetStatusService.cs:37-38`) blockiert so lange,
und wie lange das ist, hängt an der Zeilenzahl des Backfills, die niemand vorher gemessen hat. Eine
Api, deren Seiten für unbestimmte Sekunden hängen, ist kein „läuft weiter"; sie ehrlich zu stoppen
ist einfacher, sichtbarer und für den Rollback nötig (unten). Reihenfolge, weil sie zählt:

1. **Worker stoppen** (Portainer, Stack `worker`), **vor** der Migration. Der Shutdown-Flush läuft
   dann noch gegen das alte Schema und schreibt die letzten <30 s durch; `stop_grace_period: 60s`
   steht dafür seit #122 in `docker-compose.prod.yml:95` (die frühere Notiz, das stehe noch aus,
   war veraltet).
2. **Api stoppen.** Ab hier ist die Site nicht erreichbar; Uptime Kuma meldet es. Das Fenster wird
   angekündigt oder der Monitor pausiert — nicht, weil es lang wäre, sondern weil ein Alarm für
   einen geplanten Zustand die echten entwertet.
3. **Migration von Hand** über den Tunnel wie gewohnt: `list` (Pending prüfen), `update`, `list`.
   Die Migration selbst setzt für ihre Sitzung ein `lock_timeout` (über die Connection-String-
   `Options`, Plan entscheidet den Wert): trifft sie wider Erwarten auf eine offene Sperre — etwa
   eine vergessene `psql`-Sitzung —, bricht sie ab, statt hinter ihr zu warten und das Fenster
   still zu verlängern. Sie bricht außerdem nach den vier Prüfungen aus 4.3 ab, wenn die
   Zuordnungsliste nicht zum Bestand passt.
4. **Neue Images starten**, Api und Worker. Der Worker rejoined seine Kanäle (Boot-Recovery), der
   Match-Cache kommt aus dem Warmstart mit Set-ID.

**Was dabei verloren geht — und bewusst hingenommen wird:** die Chat-Zählung während des Fensters,
wenige Minuten, für alle Kanäle; ein Helix-Poll-Intervall für `ChannelLiveDay` (5-min-Raster, rundet
ohnehin); und die Erreichbarkeit der Site für dieselben Minuten. Nichts davon ist rekonstruierbar,
außer über einen künftigen Chat-Log-Backfill (#69), falls für diesen Zeitraum Logs existieren.

**Rückrollbarkeit.** Die `Down`-Migration entfernt die Spalte und stellt den zweispaltigen Index
wieder her; das gelingt genau so lange, wie kein Emote an einem Tag Zeilen unter **zwei** Set-IDs
trägt — also bis zum ersten beobachteten Set-Wechsel nach dem Deploy. Danach müsste ein Rollback
die Zeilen je `(EmoteId, Date)` summieren, bevor der alte Index wieder passt; das ist ein
Handgriff, keine Migration, und er verliert genau die Zuordnung, die das Vorhaben eingeführt hat.
Rollback-Reihenfolge ist die Umkehrung, **mit beiden Diensten**: neuen Worker **und** neue Api
stoppen, `Down`, alte Images starten. Die sechste Fassung stoppte nur den Worker — eine neue Api,
deren EF-Modell Spalten und Tabellen kennt, die `Down` gerade entfernt, hätte in dieser Minute jede
Anfrage mit Fehlern beantwortet oder gegen die laufende Migration geschrieben (Codex D3). Die
Beobachtungs-Tabelle und die Voting-Spalten sind additiv und stören ein **altes** Image nicht; das
Argument gilt nur für alte Images gegen neues Schema, nie umgekehrt.

### 12.4 Der Zwischenweg vor dem 2026-10-01 — Handlungsanweisung, kein Bauauftrag

**Anlass, unverändert.** HandOfBlood wechselt am **2026-10-01** auf sein Halloween-Set, und das
Mod-Team will es **vorher** an das Hauptset angleichen: die aktuell beliebten Hauptset-Emotes
hineinkopieren, solange Halloween noch inaktiv ist. Die dritte und vierte Fassung hatten dafür eine
Vorabscheibe vorgesehen — Ziel-Set-Picker, Zielvorschau aus dem gewählten Set, Audit-Feld, deployt
vor dem Termin. **Sie entfällt.** Nicht, weil der Bedarf kleiner geworden wäre, sondern weil der
Betreiber einen Weg gefunden hat, der ihn ohne jeden Deploy deckt. Was an ihr Technik war, steht
jetzt in 7.5 und gehört zur Hauptrunde.

**Der Zwischenweg.** Statt EmotePurge beizubringen, in ein nicht-aktives Set zu schreiben, wird das
Halloween-Set woanders **aktiv** gemacht:

1. Das Halloween-Set wird in einem **anderen Kanal** als aktives Set gesetzt (auf 7TV, nicht in
   EmotePurge). **Welcher Kanal, ist Betreibersache; welcher Art, ist festgelegt (siebte Fassung,
   Codex D4):** ein **Wegwerfkanal, der in EmotePurge noch nie getrackt war.** Prüfbar in der
   Admin-Kanalliste: es darf keine `Channel`-Zeile geben — ein einmal verlassener Kanal hat noch
   eine (`LeaveAsync` setzt nur `IsBotActive = false`, `ChannelService.cs:64`), und `JoinAsync`
   würde sie samt Historie reaktivieren. Der Grund steht unten beim Rückbau.
2. Dieser Kanal wird in EmotePurge **getrackt** (Join über die Übersicht). Der Sync liest dessen
   aktives Set, also Halloween, und legt die 687 Einträge als Zeilen des Wegwerfkanals an — 686 nach
   unserer Zählung, weil der Unique-Index `(ChannelId, SevenTvEmoteId)` das eine Duplikat aus 11.2
   zusammenfallen lässt (#74).
3. Auf **HandOfBloods** Nutzungsseite werden die beliebten Emotes ausgewählt — dort liegen die
   Nutzungszahlen, auf die es ankommt; die Quelle ist sein aktives Hauptset
   (`CapturedImportScope.emoteSetId`, `usage-stats-page.ts:1353`).
4. **„Übertragen"** mit Zielkanal = der andere Kanal. Ab hier läuft alles über vorhandene,
   ausgelieferte Wege: Ziel-Picker (`import-target-dialog.ts`), Bestätigungsdialog mit Vorschau,
   Vorprüfung gegen 7TV, Lauf, `sync-imported` an den Zielkanal, Nachlauf-Resync.
5. Am 2026-10-01 schaltet HandOfBlood seinen Hauptkanal auf das Halloween-Set. Die Emotes sind dann
   schon drin.

**Warum das trägt — am Code belegt.** Ein 7TV-Set kann in mehreren Kanälen gleichzeitig aktiv sein,
und das Repo behandelt das nicht als Sonderfall, sondern setzt es voraus:

- `EmoteSetOwnershipService` (`src/EmotePurge.Infrastructure/Services/EmoteSetOwnershipService.cs:49-53`)
  sucht in Tier 2 ausdrücklich „other channels WE already track that happen to share the same
  active set": `db.Channels.Where(c => c.ActiveEmoteSetId == channel.ActiveEmoteSetId && c.Id !=
  channel.Id)`. Das ist seit Commit `640d77b` (2026-07-28, „warn on shared 7TV emote sets before
  mass-delete") der Shared-Set-Hinweis, den Lösch- **und** Import-Dialog zeigen
  (`import-confirm-dialog.ts:138-160`, Schlüssel `massDelete.sharedSetWarningTitle`).
- Live belegt ist der Fall im DECISIONS-Eintrag vom 2026-07-30 zur 7TV-EventAPI (Zeile 7147): „zwei
  Channels mit demselben Set (live belegt: `sensitron`/`olaf_olaf_son`) ergeben eine Subscription,
  Refcount beim Abbestellen inklusive" — der Worker dedupliziert Subscriptions genau deshalb je
  `(type, object_id)`, und `docs/Architectur.md:105` führt es als Eigenschaft des Live-Pfads („shared
  sets yield one subscription"). Einen eigenen DECISIONS-Eintrag mit Überschrift hat der
  Shared-Set-Hinweis vom 2026-07-28 **nicht** — grep nach „geteilt"/„shared" findet nur den
  Redesign-Eintrag zur Dialog-Gestaltung (Zeile 5861) und den Audit-Harness (Zeile 7121); der
  Vertrag steht im Code und in den beiden genannten Stellen.
- 7TV selbst führt das aktive Set **je Verbindung** (`user_by_connection … connections { platform id
  emote_set_id }`, `SevenTvApiClient.cs:30`); unser Sync liest es für den Wegwerfkanal über dessen
  Twitch-ID (`users/twitch/{id}`, `:167`) und schreibt es nach `Channel.ActiveEmoteSetId`
  (`SevenTvSyncService.cs:86`). Zwei Kanäle mit derselben Set-ID sind für den Sync zwei Kanäle wie
  alle anderen.

**Und was dadurch von selbst richtig wird — der Kern.** Der ganze Grund für die gestrichene
Vorabscheibe war, dass Ziel-Set ≠ aktives Set des Zielkanals war; jede naive Umsetzung rechnete
deshalb auf der falschen Liste (7.5, Baustein 3). Im Zwischenweg **ist** das Ziel-Set das aktive Set
des Zielkanals, und damit stimmt ohne eine geänderte Zeile, was 7.5 für den Dauerfall erst bauen
muss:

- **Die Lauf-`setId`.** `loadImportTarget` nimmt sie aus dem Kanalstatus des Zielkanals
  (`import-target-loader.ts:84-89`, `status.value.activeEmoteSetId`), und `import-flow.ts:94` gibt
  genau diese an `startImport`. Aktives Set des Wegwerfkanals = Halloween ⇒ der Lauf schreibt ins
  Halloween-Set. Die Vorprüfung unmittelbar vor dem Lauf (`filterAlreadyPresent(httpClient,
  outcome.targetSetId, …)`, `import-flow.ts:81`, `already-present-filter.ts:144-151`) liest 7TV
  direkt nach dieser Set-ID und überspringt die **338** Emotes, die per ID schon im Halloween-Set
  liegen — einschließlich der rund zehn, die dort unter anderem Alias liegen (7.5; die fünfte
  Fassung schrieb 328, das war die Namens- statt der ID-Zahl, Codex D5).
- **Die Kollisionsvorschau.** `buildImportPreview(source, targetEmotes)` rechnet `alreadyPresent`
  per ID und `nameCollisions` per exaktem Namensvergleich gegen die Zielliste
  (`import-preview.ts:30-49`), und die Zielliste kommt aus `listEmotes(zielkanal)`
  (`import-target-loader.ts:71`), also aus **unseren Zeilen des Wegwerfkanals** — nach Schritt 2 die
  Halloween-Einträge mit ihrem Alias als `name` (`UpsertEmote` schreibt `live.Name`,
  `SevenTvSyncService.cs:516`). Die **192 Namensvettern** aus 11.2 erscheinen damit **vorher** im
  Dialog („N Namen sind im Zielset schon vergeben — 7TV wird diese Emotes ablehnen",
  `import-confirm-dialog.ts:203-208`), statt einzeln an 7TV abzuprallen. Der Dialog rechnet für den
  Anlass also nicht „762 bereits vorhanden, nichts hinzuzufügen" (die Fehlfunktion aus 7.5),
  sondern: 338 bereits vorhanden, rund 424 hinzuzufügen, davon 192 mit vergebenem Namen. **Eine
  Grenze hat die Vorschau hier:** unsere Zeilen tragen je ID **einen** Alias (den zuletzt
  geschriebenen); für das eine #74-Duplikat des Halloween-Sets (687 Einträge / 686 IDs) ist der
  andere Alias unsichtbar — höchstens **ein** Name kann so als Kollision fehlen und erst im Lauf
  abprallen. Die Alias-Abweichungen (rund zehn) zeigt der ausgelieferte Dialog nicht als Gruppe;
  sie zählen unter „bereits vorhanden".
- **Die Slot-Projektion.** `occupiedSlots` ist die Zahl der nicht-archivierten Zeilen des
  Zielkanals (`EmoteSetStatusService.cs:36`), `capacity` dessen `ActiveEmoteSetCapacity` (`:44`,
  vom Sync aus dem Set-Objekt gefüllt). Für den Wegwerfkanal mit aktivem Halloween-Set heißt das
  **687 belegt** (686 nach #74), Kapazität **1000**, **313 frei**; landen werden rund **232**
  (687 + 232 = 919 ≤ 1000, 7.5). Was 7.5 als Befund festhält, gilt hier unverändert: `projectSlots`
  zählt die 192 Kollisionen mit (`import-confirm-dialog.ts:397`, `slot-projection.ts:26`) — wählt
  das Mod-Team das **ganze** Hauptset, steht dort 687 + 424 = 1111 > 1000 und ein Überlauf-Banner,
  obwohl nur rund 232 landen. Das Banner ist eine Warnung, keine Sperre (`executeDisabled` hängt
  allein am Ladezustand, `:436-444,468`); wer nur die beliebten wählt, sieht eine kleinere Zahl
  mit demselben Viertel Kollisionsanteil. Pessimistisch ist die ehrliche Richtung, weil im
  ausgelieferten Dialog 7TV entscheidet.
- **Nach dem Lauf.** `reportImported` postet `sync-imported` an `run.targetChannelName`, also den
  Wegwerfkanal (`seven-tv-import.service.ts:296-301`); `MarkImportedAsync` findet dessen Kanalzeile
  und schreibt den Audit-Eintrag unter dem Wegwerfkanal (`EmoteService.cs:107-131`). Der
  Nachlauf-Resync des Wegwerfkanals (`:284-285`) findet — anders als bei einem nicht-aktiven Ziel —
  sofort etwas: die neuen Halloween-Einträge werden Zeilen des Wegwerfkanals. In HandOfBloods
  eigenen Zeilen ändert sich bis zum Wechsel nichts; dann archiviert sein Sync die
  Hauptset-Differenz und legt die Halloween-only-Einträge an (Abschnitt 2, 4.3). Die rund 232
  übertragenen Emotes behalten bei ihm ihre Zeile und ihre Historie — gleiche 7TV-ID, gleiche
  Zeile.

**Was der Lauf trotzdem an 7TV zurücklässt.** Im **ausgelieferten** Dialog bleiben Kollisionen im
Lauf (`import-preview.ts:25`, „7TV entscheidet" — nach dem Deploy nicht mehr, 7.5); wer die 192
nicht vorher abwählt, bekommt sie als `failed`-Zeilen im Dock, mit je einem Ticket aus dem
`emote_set_change`-Eimer (7.5, Kollisionsrechnung). Der Unterschied zum Zustand ohne Vorschau ist
nicht, dass die Ablehnungen ausbleiben, sondern dass die Namen **vorher** auf dem Schirm stehen und
das Mod-Team sie abwählen kann. Die rund zehn Alias-Abweichungen bekommen im Zwischenweg den
Hauptset-Alias nicht; wer ihn will, benennt sie im 7TV-Frontend um — kein Werkzeug dafür in #200.

**Zwei Vorbehalte und ein Rückbau, die dazugehören.**

- **Der Zielkanal muss getrackt sein**, sonst erscheint er im Ziel-Picker nur ausgegraut:
  `importTargetOptions` rendert jeden Kanal aus `listMine()` mit Broadcaster- oder 7TV-Editor-Rolle,
  setzt aber `disabled: !channel.isTracked` (`import-target-options.ts:28-31`; Doku `:7-9`: „the row
  still renders … but its radio is disabled"). Und er erscheint überhaupt nur, wenn der Handelnde
  dort **Broadcaster oder 7TV-Editor** ist (`:29`) — ein Mod-Team-Mitglied, das Editor von
  HandOfBloods 7TV-Account ist, ist nicht automatisch Editor des Wegwerfkanals, wenn der an einem
  **eigenen** 7TV-Account hängt (`isSevenTvEditor` kommt aus den `editor_of`-Grants,
  `MyChannelsService.cs`, `SevenTvApiClient.cs:58`). Hängt der Wegwerfkanal dagegen als zweite
  Twitch-Verbindung an **HandOfBloods** 7TV-Account, sind dieselben Editoren zuständig, und 7TV führt
  das aktive Set ohnehin je Verbindung (`SevenTvApiClient.cs:30`). Welcher Fall vorliegt, entscheidet
  sich mit dem Kanal — offen.
- **Ob 7TV erlaubt, das Set eines fremden Accounts im eigenen Kanal zu aktivieren:** der Betreiber
  geht am 2026-09-19 davon aus, dass ein Editor das kann („sollte gehen"). Das ist eine Einschätzung,
  keine Messung — es ist ein Schreibvorgang mit Anmeldung und deshalb nicht per Sonde aus einer
  Session zu klären. Beim Festlegen des Kanals einmal **am 7TV-Frontend** bestätigen (Set-Auswahl des
  Wegwerfkanals öffnen: erscheint HandOfBloods Halloween-Set?). Bei einem eigenen Zweitkanal desselben
  7TV-Accounts stellt sich die Frage nicht — den Fall gibt es hier allerdings nicht: gemessen am
  2026-09-19 hat HandOfBloods 7TV-Account (`01GQA28FCR0002Q9KS8SKQKVXX`) **genau eine**
  Twitch-Verbindung (`49140130`) und 14 Editoren. Der Wegwerfkanal hängt also zwangsläufig an einem
  fremden 7TV-Account, solange niemand HandOfBloods Account um eine Verbindung erweitert. Fällt die
  Bestätigung negativ aus, bleibt die Rückfallebene unten.
- **Rückbau: der Wegwerfkanal wird nach dem 2026-10-01 per Admin-Purge entfernt — zugesagt vom
  Betreiber, und der einzige Rückbau, der die Datenbank wirklich zurücksetzt (Codex D4).** Die
  fünfte und sechste Fassung sahen vor, den Zweitkanal „wieder auf ein eigenes Set zu stellen" und
  ihn wahlweise getrackt zu lassen. Am Code reicht das nicht: `LeaveAsync` setzt nur `IsBotActive =
  false` (`ChannelService.cs:64`), `MyChannelsService` meldet die Zeile weiter als getrackt
  (`IsTracked: trackedByName.ContainsKey`, `:92`), und der Ziel-Picker deaktiviert nur `!isTracked`
  (`import-target-options.ts:30`) — ein „verlassener" Kanal bliebe also als Ziel wählbar; Tier 2
  des Shared-Set-Hinweises zählt seine Zeile weiter mit (`EmoteSetOwnershipService.cs:49-53`); ein
  Leave **vor** dem Resync nach dem 7TV-Rückwechsel fröre `ActiveEmoteSetId` auf Halloween ein;
  und seine `Emote`-/`UsageStat`-Zeilen blieben liegen, für die Migration (4.3) ein Kanal mit
  Wechsel ohne Eintrag in der Liste. `DELETE /api/channels/{name}/purge` (`ChannelEndpoints.cs:269-
  282`, `ChannelService.PurgeAsync`, `:80-108`) löst alles davon auf einmal: `LEAVE` publiziert,
  Kanalzeile entfernt, Kaskade über `Emote`, `UsageStat`, `VoteSession`, `ChannelLiveDay`
  (`AppDbContext.cs`), Eintrag `channel.purge` als einzige Spur. Verloren geht **nichts, was
  jemand braucht** — genau deshalb die Auflage, dass der Kanal vorher nie getrackt war: ein Kanal
  mit Historie hätte sie beim Purge verloren, ein frischer hat keine. Was bleibt, sind die
  Audit-Einträge `emotes.syncImported` unter dem Namen des Wegwerfkanals (`AuditLogEntry.ChannelName`
  ist ein Snapshot-String, kein FK) — die Papierspur der Vorbefüllung — und HandOfBloods eigene
  Zeilen, sobald sein Sync den Wechsel verarbeitet hat. **Reihenfolge:** erst HandOfBloods Wechsel
  am 01.10. (der Purge berührt das 7TV-Set nicht, nur unsere Zeilen), dann der Purge, jedenfalls
  **vor** der Gegenprobe 11.6 und der Migration. Bis zum Purge steht der Shared-Set-Hinweis (Tier 2)
  in beiden Kanälen — richtig, ein Set ist dann in zweien aktiv. Was mit dem Wegwerfkanal auf 7TV
  danach geschieht, ist EmotePurge gleichgültig; nur wenn ein Mod-Team-Mitglied ihn weiter auf
  Twitch moderiert **und** Halloween dort aktiv bleibt, nennt Tier 3 des Hinweises
  (`CheckModeratedChannelsAsync`) ihn in HandOfBloods Löschdialog weiter — Betreibersache, kein
  Datenproblem.

Informativ, kein Vorbehalt: Tier 1 des Hinweises vergleicht den Set-Besitzer mit der 7TV-Identität des
**Zielkanals** (`EmoteSetOwnershipService.cs:40`). Hängt der Wegwerfkanal an einem eigenen 7TV-Account,
ist Halloween für ihn „nicht das eigene Set" (`!warning.isOwnSet`), und der Import-Dialog zeigt den
Shared-Set-Kasten schon **vor** dem 01.10. — richtig, nicht blockierend (`import-confirm-dialog.ts:142`).

**Rückfallebene, falls der Zwischenweg an einem Vorbehalt scheitert.** Der Dateiweg funktioniert schon
heute, ohne jeden Deploy und ohne Wegwerfkanal — mit einer Lücke. A16 ist am 2026-09-06 ausgeliefert,
und die `emote-list`-Envelope existiert genau dafür. Der Parser (`import-source-parser.ts:25-79`)
prüft `kind` (`:31-39`), `formatVersion` (`:40-42`) und die Zeilen (`:43-57`); `channelName` wird nur
in die Herkunftsanzeige kopiert (`:75`), `meta` nur für `rowCount` gelesen (`:62-63`), und
`meta.sourceEmoteSetId` — das der Export schreibt (`emote-list-export.ts:36`) — wird **nirgends**
gelesen. Der Kontrast ist das Purge-Protokoll, das gegen Kanal **und** Set geprüft wird
(`file-import-step.ts:136-140`); die Asymmetrie ist die A16-Entscheidung, kein Versehen. Also: **vor**
dem 01.10. auf HandOfBloods Nutzungsseite die beliebten Emotes markieren und als Emote-Liste
exportieren (oder den Nutzungs-Export nehmen, der als Quelle ebenfalls gilt,
`import-source-parser.ts:34,48`); **am** 01.10. umschalten; danach die Datei ins Restore-Panel ziehen
— der Import läuft in das dann aktive Halloween-Set, mit demselben Dialog, derselben Slot-Projektion
und derselben Kollisionsanzeige, denn dort sind Ziel und aktives Set wieder dasselbe. Bedingung: der
Sync hat den Wechsel schon verarbeitet (`user.*`-Event in Sekunden, sonst der 60-s-Resync, 4.4);
davor rechnete auch die Rückfallebene gegen das Hauptset und meldete „alles bereits vorhanden" — im
Zweifel den Kanalstatus (Belegung) einmal ansehen, bevor die Datei ins Panel geht. Preis: Minuten, in
denen Halloween ohne die Hauptset-Emotes live ist.

**Was der Zwischenweg nicht ist.** Keine Lösung für den Dauerfall (7.3): er braucht einen
Wegwerfkanal, macht dessen aktives Set für die Dauer des Befüllens zum Halloween-Set und endet mit
einem Purge. Er ist ein Kniff für genau diesen Anlass — und zugleich der Beleg dafür, dass der
ausgelieferte Dialog richtig rechnet, sobald Ziel-Set und aktives Set zusammenfallen. Der
Ziel-Set-Picker der Hauptrunde macht genau diese Bedingung entbehrlich.

---

## 13. Aufgabenreihenfolge (grob, mit Begründung)

**Ein Vorhaben, ein Deploy.** Der Grundsatz der zweiten Fassung gilt wieder ungebrochen: der
Schema-Umstieg verträgt keinen gestaffelten Deploy (12.3), und Teile ohne Schema-Bezug vorzuziehen
kauft nichts, was einen zweiten Deploy wert wäre. Die dritte und vierte Fassung hatten ihn für einen
Anlass mit Datum gebrochen; warum das eine Runde lang galt und wieder entfallen ist, steht im
Überarbeitungs-Abschnitt (Fünfte Runde) und in 12.4. Die Reihenfolge unten ist Bauordnung auf einem
Branch, nicht Deploy-Ordnung; deployt wird einmal, im Wartungsfenster hinter dem 2026-10-08.

**Vorweg, außerhalb des Branchs: der Zwischenweg (12.4).** Kein Task, nichts zu bauen — eine
Handlungsanweisung an Betreiber und Mod-Team vor dem 2026-10-01: einen nie getrackten Wegwerfkanal
bestimmen, Halloween dort aktiv setzen, Kanal tracken, von HandOfBloods Nutzungsseite übertragen
(Kollisionen im Dialog abwählen), am 01.10. wechseln, am Wechseltag die Zahl der archivierten
Hauptset-IDs mit der ID-Sonde aus 11.2 messen und in die Zuordnungsliste eintragen (4.3), danach
den Wegwerfkanal purgen. Er läuft parallel zu allem Folgenden und berührt Prod nur als Nutzer der
ausgelieferten Oberfläche.

1. **Sonden (Abschnitt 11, Punkte 4, 5, 6).** Punkte 1 und 2 sind gemessen (11.1, 11.2). Offen:
   Punkt 4 (TTL), Punkt 5 (`REMOVE`-Verhalten bei Duplikat, braucht ein eigenes Testset), Punkt 6
   (Gegenprobe, führt der Betreiber aus). **Vor Schritt 4 steht kein Tor mehr:** die Sonde 7 der
   siebten Fassung ist gestrichen, der Picker hängt an keiner Messung (7.3). Aus Sonde 1 nachzuholen, zehn Sekunden mit der Twitch-ID aus der Admin-Kanalseite: dass HandOfBloods
   eigene Set-Liste sein Halloween-Set mit Namen enthält — für den Picker (Schritt 4).
2. **Zuordnungsliste, Gegenprobe 11.6 gegen Prod und die beiden Purges — unmittelbar vor der
   Migration.** Der Betreiber bestätigt die Liste aus 4.3 (HandOfBlood: alte und neue Set-ID,
   UTC-Grenze, erwartete Archivierungszahl vom Wechseltag); die Gegenprobe erwartet den Testkanal
   und HandOfBlood mit passendem Tag und passender Zahl, sonst nichts — der Wegwerfkanal ist da
   schon gepurgt. Danach den Testkanal per Admin-Purge räumen (4.3). Jede Abweichung stoppt hier
   alles, bis die Liste vom Betreiber korrigiert ist; die Migration selbst prüft dieselben vier
   Punkte noch einmal und bricht ab, statt zu schätzen.
3. **Datenmodell und Zählpfad (A/D/E, Abschnitte 4 und 5.2) + DECISIONS-Eintrag 1.** Backend-only,
   unsichtbar: Migration (mit der Zuordnungsliste aus 4.3 als geprüfter Eingabe und der
   Intervall-Saat aus 4.4), Snapshot im Match-Cache mit Generationszeitpunkt, zusammengesetzter
   Zählerschlüssel, Flush, Query-Filter mit Default „aktives Set", Beobachtungs-Log mit allen
   Schließ-/Öffnungsstellen. Live-Verifikation
   (Regel 16) an der Dev-Box: Set-Wechsel im Dev-Kanal, Zeilen beider Sets im Flush-Fenster,
   Leave/Rejoin auf dasselbe Set ⇒ zwei Intervalle, Wechsel während der Abwesenheit ⇒ altes Intervall
   endet beim Leave. Dazu die Wechsel-Tests aus 5.2.
4. **Ziel-Set-Picker (J/K, 7.3–7.5) + DECISIONS-Eintrag 2.** In dieser inneren Reihenfolge, weil
   jeder Teil den vorigen braucht und der erste allein schon prüfbar ist:
   - *Backend:* Set-Listen-Endpunkt je Kanal mit `isActive`/`isPersonal` (6.1, 7.5 Baustein 1);
     Angebotsliste des Pickers aus eigenem Account plus `editor_of`-Accounts mit je öffentlicher
     Set-Liste (7.3; Zuschnitt der Endpunkte entscheidet der Plan); `capacity` in Preview-Abfrage, Record und `ForeignEmoteSet`; Lesepfad `?emoteSetId=` hinter dem
     Dekorator mit Set-ID als zweitem Schlüsselraum. `SyncImportedRequest.TargetEmoteSetId` nullbar,
     `TargetType`/`TargetId`, `ProjectDetail` (7.4, 7.5 Baustein 4). Tests nach Regel 11.
   - *Frontend, getrackte Klasse (7.5 Bausteine 2 und 3):* `ImportTargetChoice.emoteSetId` als
     Schlüssel, `channelName` als Attribut der getrackten Klasse; Picker mit Sets je Kanal, eigener
     Kanal ohne sein aktives Set, persönliche Sets deaktiviert; `loadImportTarget` mit
     Set-Ziel und `truncated ⇒ failed`; Setname im Dialogkopf; Kollisionsvorschau und
     Slot-Projektion auf dem gewählten Set, **Kollisionen aus `toAdd` heraus und als Gruppe**,
     **Alias-Abweichungen als eigene Gruppe** (7.5). Specs nach Regel 12 (Preview mit drei Gruppen, Projektion ohne Kollisionen), E2E
     für „gleicher Kanal, anderes Set" mit gemockter Set-Liste und Set-Vorschau.
   - *Frontend, ungetrackte Klasse (7.3, 7.4):* Rest der serverseitigen Angebotsliste (Name und
     Besitzer kommen mit ihr), Bestätigung, `channelName` nullbar; der set-zentrierte
     Audit-Endpunkt mit Besitzer-Prüfung, Api-Test für die neue Route (Regel 11).
   - *Live-Verifikation an der Dev-Box (Regel 16):* Testkanal mit zwei Sets: Import ins nicht-aktive,
     Dialogzahlen gegen 7TV, Audit-Eintrag mit Set-ID und `targetIsActiveSetOfChannel: false`; ein
     Import mit absichtlicher Namenskollision und einer absichtlichen Alias-Abweichung (Dialog zeigt
     beide Gruppen, der Lauf enthält keine der beiden Zeilen und meldet **keine** `failed`-Zeile).

   Unabhängig von 3 baubar, deployt aber zusammen.
5. **Quell-Set-Picker beim fremden Kanal (I, 7.2).** Der Lesepfad `?emoteSetId=` existiert aus 4;
   hier kommt der Quell-Picker davor, mit derselben Set-Listen-Abfrage für den fremden Kanal.
6. **Set-Dropdown, Set-Filter in `/totals`/`/daily`/`/series`, Vereinigungs-Mitgliederliste,
   Tatsachenangabe, Preset (G, 6) — und die Zeilenidentität (6.5).** Das Dropdown liest den
   Endpunkt aus 4. Inklusive Captured-Scope-Umstellung, der nullbaren `TotalUseCount`-**und**
   `EmoteId`-Semantik, dem „nicht mehr im Set"-Badge und der Duplikat-Zelle im Raster; hier auch die
   drei anderen Import-Türen (Datei, Fremdkanal, Bestenliste) auf das gewählte Set. **Seit der
   sechsten Fassung gehört hierher der Schlüsselwechsel des Rasters:** `ListSelection` und inneres
   `track` auf `sevenTvEmoteId`, Inspector-/Rang-/Füllgrad-Maps nachgezogen, `DeletableEmote.emoteId`
   optional, Drilldown für Guid-lose Zeilen gesperrt, Voting-Draht als Schlüssel-Signal mit
   Auflösung für Null-Sessions; **`/series` nach `SevenTvEmoteId` mit `emoteSetId`, beide
   Frontend-Caches mit Set im Schlüssel, Drilldown mit eingefrorener Set-ID; Nutzungs-Export mit
   Set-ID/Setname und `null`-Serialisierung** (6.1, 6.5 Vertrag 2 — siebte Fassung). Er steht
   **vor** 7, weil 7 ohne ihn keine Klasse-3-Zeile in die
   Queue bekommt — und **in** 6 statt als eigener Schritt, weil das Raster mit der
   Vereinigungs-Mitgliederliste zum ersten Mal Guid-lose Zeilen sieht und ab dieser Sekunde nicht mehr
   auf der Guid stehen darf (sonst NG0955 und Sprite-Neuaufbau, 6.5). Specs nach 6.5, letzter
   Absatz.
7. **Löschen/Wiederherstellen im gewählten Set (H, 7.1) + DECISIONS-Eintrag 4.** Braucht das
   Dropdown und den Schlüsselwechsel aus 6 und die Papier-Variante der Rückmeldung (7.1, bestätigt).
   Umfang seit der sechsten Fassung größer als „`setId` aus dem Dropdown": Queue-Key
   `sevenTvEmoteId` für Delete und Restore (R3-Nachtrag), `deleted`-Output als Keys, `onDeleted`
   nach 7TV-Id, Protokollzeile mit optionalem `emoteId` samt Parser, `sync-deleted`/`sync-restored`
   mit `{ emoteSetId, sevenTvEmoteIds }` **neben** dem übergangsweise weiter gültigen `{ emoteIds }`
   und Match über `(ChannelId, SevenTvEmoteId)`, Set-ID im Laufdatensatz für Erstbericht und Retry,
   Setname in Lösch- und Restore-Bestätigung; Api-Test für beide Bodies (Regel 11). Live-Verifikation
   (Regel 16) am Testkanal mit einem Set, das ein nie aktives Emote enthält: löschen, Protokoll
   enthält die Zeile mit `emoteId: null`, Audit-Eintrag trägt Set-ID und 7TV-Id-Anzahl, Restore aus
   genau diesem Protokoll.
8. **Voting für Set-Sessions (L, 8) + DECISIONS-Eintrag 3.** Zuletzt, weil es die meisten Verträge
   bewegt (Anlegen mit Upsert, Abstimmen mit `eligible`, Ergebnisse mit Set-Filter, eingefrorene
   Anzeigedaten, `canSelectForDelete` aus `canManage`, Detailseite) und alle vorherigen Bausteine
   voraussetzt (Live-Liste, Set-Filter der Totals, Set-ID am Panel, Schlüssel-Signal aus 6 —
   `CreateVoteSessionRequest.SevenTvEmoteIds` für Set-Sessions, 6.5). Worker-seitige
   Konfliktverträglichkeit beim Abgleich (8) und der Wettlauftest mit **erzwungener Verschränkung**
   zwischen Worker-Lesen und Worker-Speichern; dazu der Fall aus 6.5, Klasse 2b: nach dem Anlegen
   zeigt die Set-Ansicht für das vorher zeilenlose Mitglied weiterhin `null`, nicht 0.
9. **Deploy im Wartungsfenster (12.3)**, nach Freigabe gegen das Harness-Runbook (12.2): Worker
   und Api stoppen, migrieren, neue Images. Vorher `coverage-local`, Codex-Review (Regel 22), PR. Danach die
   Live-Verifikation auf Prod wiederholen: eine Set-Ansicht öffnen, eine Zeile mit `EmoteSetId`
   prüfen, `/api/health` wieder 200; und einmal den Picker am Testkanal: Sets mit Namen, ein
   nicht-aktives Set wählen, Dialog zeigt dessen Belegung und Kapazität, nicht die des aktiven.
10. **Backfill-Parameter (F, 5.3)** — nicht Teil dieses Vorhabens, sondern Auflage an den Plan, der
    #69 persistiert.

Schritte 3 bis 8 sind Branch-Arbeit ohne Prod-Berührung. Schreibend auf Prod geht nur 9
(Wartungsfenster); lesend nur 2 — beides führt der Betreiber aus. Der Zwischenweg berührt Prod als
Nutzer, nicht als Deploy.
