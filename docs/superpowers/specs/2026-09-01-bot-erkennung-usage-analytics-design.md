# Bot-Erkennung in der Usage-Analytics — Entwurf

**Datum:** 2026-09-01 · **Issue:** [#31](https://github.com/emotepurge/EmotePurge/issues/31) · **Status:** entworfen, noch nicht geplant

## Warum jetzt

Jede Chat-Nachricht eines Bots wird heute wie die eines Menschen in `UsageStat.UseCount`
verbucht. Bot und Mensch stecken danach in **derselben Zahl** und sind nicht mehr trennbar —
es gibt keine Chatter-Dimension, an der man das nachträglich aufdröseln könnte.

Daraus folgt das Einzige, was dieses Issue zeitkritisch macht: **Warten kostet
unwiederbringlich Daten.** Jeder Tag ohne Trennung erzeugt weitere Zeilen, die für immer
gemischt bleiben. Kein anderes offenes Issue wird durch Verzögerung teurer.

Bestandsdaten lassen sich **nicht** nachträglich bereinigen. Das ist keine Einschränkung
dieses Entwurfs, sondern eine Eigenschaft der bereits geschriebenen Zeilen. Der Entwurf
macht den Bruch deshalb sichtbar, statt ihn zu verschweigen (Abschnitt 5).

## Ist-Zustand, verifiziert am 2026-09-01

| Ort | Befund |
|---|---|
| `TwitchChatManager.OnMessageReceived` | liest aus `ChatMessage` nur `Channel`, `Username` (nur Debug-Log) und `Message`. Kein eigenes Nachrichtenmodell; `UserId` und `Badges` existieren auf dem TwitchLib-Typ, werden aber nie gelesen. |
| `EmoteUsageCounter` | `ConcurrentDictionary<string, int>`, ausschließlich auf `emoteId` verschlüsselt — weder pro Chatter noch pro Channel. |
| `UsageStat` | `(EmoteId, Date, UseCount)`, Unique-Index `(EmoteId, Date)` **als Covering-Index** mit `IncludeProperties(u => u.UseCount)`. |
| `UsageStatFlushService.FlushAsync` | `UNNEST`-Upsert mit `ON CONFLICT ("EmoteId","Date") DO UPDATE SET "UseCount" = … + EXCLUDED."UseCount"`. |
| `UsageStatQueryService` | fünf Lesemethoden, alle über `UseCount`. |
| Ignorierlisten | existieren nirgends im Repo. |

## Entscheidungen

### E1 — Bot-Nutzung wird erhalten, nicht verworfen

`UsageStats` bekommt eine **zweite Spalte** `BotUseCount`. Der Unique-Index bleibt
`(EmoteId, Date)`.

Verworfen wurde die im Issue skizzierte Variante, die Dimension in den Unique-Index zu
ziehen (`(EmoteId, Date, IsBot)`): sie leistet dasselbe, verdoppelt aber die Zeilenzahl und
zwingt alle fünf Aggregat-Queries in eine neue Form — inklusive des `GroupBy`-Risikos aus
Regel 10. Ebenfalls verworfen wurde „Bots einfach verwerfen": eine Fehlklassifikation wäre
dann ein endgültiger Datenverlust.

Mit der zweiten Spalte ist **beide Fehlerrichtungen reparierbar**, und das ist der eigentliche
Grund für die Wahl:

- Ein **übersehener** Bot landet in `UseCount` — exakt der heutige Zustand, kein Rückschritt.
- Ein **fälschlich erkannter** Mensch landet in `BotUseCount` und ist nicht verloren.

Das senkt das Risiko eines kleinen ersten Wurfs so weit, dass er vertretbar ist.

### E2 — Erkennung in dieser Runde: statische Liste, dann messen

Zwei Schichten: `bot-badge` → statische ID-Menge. Die dritte Schicht aus dem Issue (dynamische
Bot-Datenbank) **entfällt vorerst**.

Grund ist eine Messung, nicht eine Vermutung — siehe Abschnitt „Messung twitchbots.info".
Sobald `BotUseCount` in echten Zahlen vorliegt, entscheidet **diese Messung**, ob ein Import
oder eine Pflegeoberfläche überhaupt lohnt.

### E3 — Kein Toggle im Frontend

Die Standardanzeige zählt Menschen. Ein dauerhaftes Bedienelement „mit Bots" wird **nicht**
gebaut: Es beantwortet eine Frage, die der Erstbesuch nicht stellt, und ohne Messung ist
unbekannt, ob die Bot-Zahl überhaupt interessant ist. Die zweite Spalte hält die Option offen,
falls die Zahlen später dafür sprechen.

Stattdessen ein reiner **Hinweis mit Datum** nach dem Muster der bestehenden
`liveDayCoverage`-Bildunterschriften — eine Ehrlichkeitsaussage, kein Steuerelement.

### E4 — Das Datum des Hinweises wird aus den Daten abgeleitet

Pro Channel `MIN(Date)` über Zeilen mit `BotUseCount > 0`, statt einer gepflegten Konstante.

Eine Konstante behauptet den Bruch auch für Channels, in denen nie ein Bot erkannt wurde,
und läuft auseinander, sobald Dev und Prod an verschiedenen Tagen deployen. Der abgeleitete
Wert ist selbstpflegend; hat ein Channel keine Bot-Zeilen, gibt es auch keinen Bruch zu
erklären, und der Hinweis entfällt.

## Messung twitchbots.info (2026-09-01, live geprüft)

| Frage | Antwort |
|---|---|
| API erreichbar? | ja — HTTP 200 in 0,16 s |
| Bots gesamt | 18.450 (18.684 inkl. inaktiv) |
| davon **Multi-Channel** | **824** (903 inkl. inaktiv); der Rest hängt je an genau einer `channelID` |
| Pflegestand Multi-Channel | **17,6 % seit 2022 aktualisiert**; Gipfel 2018 (277), je 17 in 2024 und 2025 |
| `/v2/channel/{id}/bots` | funktioniert, lieferte für eine getestete große Channel-ID aber `total: 0` |

Zwei Konsequenzen:

1. Der im Issue erwähnte „18k in Batches zu 100"-Import ist **nicht nötig**. Die brauchbare
   Teilmenge sind die 903 Multi-Channel-Bots — neun Requests, nicht 185.
2. Die Liste verrottet. Was ein Channel an **eigenem** Bot betreibt, steht mit hoher
   Wahrscheinlichkeit nicht darin. Genau diese Lücke schließt der Konfigschlüssel aus A1.

### Falle: `?username=` wird stillschweigend ignoriert

Ein Aufruf mit `?username=sery_bot` liefert die **ungefilterte** 18.450er-Liste zurück,
inklusive `total: 18450` — also eine Antwort, die auf den ersten Blick wie ein Treffer
aussieht. Namenssuche geht nur über die Vollliste. Hier festgehalten, damit das niemand
erneut herausfindet.

## Bausteine

### A1 — `IBotChatterDetector` (`EmotePurge.Worker`)

Neue Klasse mit Interface nach Regel 5. **TwitchLib-frei**, wie `ReconnectPolicy` und
`TwitchWatchdogPolicy`: Sie nimmt die Chatter-ID und die Badge-Set-Ids als reine `string`-Werte,
das Mapping aus `ChatMessage` macht `TwitchChatManager`. Nur so liegt ihr Test im
containerfreien `tests/EmotePurge.Worker.Tests` (Regel 11).

Reihenfolge der Prüfungen: `bot-badge` → statische ID-Menge → Konfig-Ergänzung.

**Statische Liste — sechs am 2026-09-01 gegen die API verifizierte IDs:**

| Bot | Twitch-User-ID |
|---|---|
| nightbot | `19264788` |
| streamelements | `100135110` |
| fossabot | `237719657` |
| moobot | `1564983` |
| streamlabs | `105166207` |
| sery_bot | `402337290` |

Nichts Unverifiziertes wandert in diese Liste.

**Konfigschlüssel `Twitch:AdditionalBotAccountIds`** ergänzt die statische Liste, ersetzt sie
nicht. Er existiert für channel-eigene Bots, die in keiner Fremdliste stehen — die Lücke, die
E2 bewusst offenlässt — und macht sie ohne Release nachtragbar.

Randfälle, die der Test abdecken muss: leere oder fehlende Chatter-ID (→ kein Bot, nie eine
Ausnahme), unbekannte ID, Badge ohne ID, Konfigschlüssel leer/fehlend, Konfigwert mit
Leerzeichen oder Duplikat einer statischen ID.

### A2 — `EmoteUsageCounter` trägt ein Paar

`Increment(string emoteId, bool isBot)`. Intern wird der Wert des Dictionaries zu einem
`readonly record struct EmoteUsageCounts(int Human, int Bot)`; `Merge` und `DrainAndReset`
tragen denselben Typ. `PendingEmoteCount` bleibt semantisch unverändert (Zahl **verschiedener**
Emotes, nicht Summe der Treffer).

Der Typ gehört nach `EmotePurge.Core/Services/`, weil `IUsageStatFlushService.FlushAsync` ihn
in der Signatur führt. Ein `record struct` ist reine BCL — die Schichtentreue von Core bleibt
gewahrt und `CoreAssemblyReferenceTests` bleibt grün.

**Gefahrenstelle:** Die Bot-Prüfung gehört in `OnMessageReceived` **nach** die
Watchdog-Buchführung. Eine Bot-Nachricht beweist, dass der Socket lebt, und muss
`_lastMessageReceivedUtcTicks` und `_lastMessageByChannelTicks` weiter aktualisieren — sonst
erfindet der Watchdog stille Verbindungen und erzwingt Reconnects, genau das Fehlerbild, das
am 2026-08-03 behoben wurde.

Die Erkennung läuft **einmal pro Nachricht**, vor der Token-Schleife, nicht je Emote.

### A3 — Persistenz

`UsageStat.BotUseCount` (`int`, `NOT NULL DEFAULT 0`), additive Migration.

Das `UNNEST` im Upsert bekommt ein drittes Array; `DO UPDATE SET` addiert beide Spalten
getrennt. Der bestehende Kommentar zur Atomarität des Upserts bleibt gültig und gilt
unverändert für beide Spalten.

**Der Covering-Index bleibt unverändert.** `BotUseCount` kommt **nicht** in
`IncludeProperties`: Die fünf Aggregat-Queries lesen weiter nur `UseCount`, der
Index-Only-Scan bleibt erhalten, und der Index wird nicht für eine Spalte verbreitert, die
heute niemand liest. Käme je der Toggle aus E3, ist das die Stelle, die dann neu zu bewerten
ist.

**Prod-Reihenfolge:** Weil die Migration additiv ist, gilt der Standardweg aus `CLAUDE.md` —
erst von Hand über den Tunnel migrieren, dann die Images deployen. Das noch laufende alte
Image ignoriert die neue Spalte.

### A4 — Api

Die fünf Query-Methoden und alle bestehenden Api-Verträge bleiben **unangetastet**.
`UseCount` bedeutet ab jetzt „Menschen", und genau das zeigen die Seiten bereits an.

Einzige Vertragsänderung: `EmoteSetStatusDto` bekommt ein nullbares Feld für das
Bot-Trenndatum. `/api/channels/{name}/active-set` ist dafür der richtige Ort und **kein neuer
Request** — der Endpunkt trägt laut seinem eigenen Kommentar bereits Slot-Budget und
`TrackedSince` „for exactly that reason: both are for the same audience, and both pages
already fetch this". Das Trenndatum ist dieselbe Art Aussage und hat dieselbe Zielgruppe.
Dass kein Request hinzukommt, ist zusätzlich für [#45](https://github.com/emotepurge/EmotePurge/issues/45) relevant.

Zwei Auflagen an die Umsetzung:

- **Die Abfrage wird unter derselben Bedingung übersprungen wie `occupiedSlots`**, also solange
  `ActiveEmoteSetId` leer ist. Genau in diesem Fenster pollt die Usage-Seite den Endpunkt in
  einer Schleife auf den ersten Sync; eine Query je Poll für ein garantiertes Nichts ist
  derselbe Fehler, den der bestehende Kommentar dort bereits abwehrt.
- **Regel 10 mitdenken.** `MIN(Date)` über einen Navigations-Join ist kein `GroupBy`, aber
  `GetUsageContextAsync` dokumentiert genau an dieser Tabelle, dass EF Core stolpert, solange
  die gefilterte Quelle die Navigation noch mitträgt. Der Zuschnitt folgt dem dortigen
  Vorbild: erst auf eine skalare Emote-ID-Liste reduzieren. Die Übersetzung ist beim
  Implementieren zu **prüfen**, nicht anzunehmen.

### A5 — Frontend

Ein Hinweissatz auf der Usage-Statistik im Stil der bestehenden Coverage-Unterschriften:
sinngemäß „Bot-Nachrichten werden seit *Datum* nicht mitgezählt; Zahlen davor enthalten sie."

Ist das Feld `null`, erscheint **kein** Hinweis. Neue i18n-Schlüssel in beiden Locale-Dateien.
Kein Toggle, kein neuer Dauerzustand, keine neue Route.

## Tests

| Projekt | Fälle |
|---|---|
| `tests/EmotePurge.Worker.Tests` | Detektor: Badge · statische ID · Konfig-Ergänzung · Unbekannter · leere/fehlende ID · Konfig leer/mit Leerzeichen/Duplikat |
| `tests/EmotePurge.Worker.Tests` | Zähler: beide Arten getrennt · `Merge` erhält beide · Drain-Swap gibt beide zurück und leert |
| `tests/EmotePurge.Infrastructure.Tests/Integration` | Upsert: neue Zeile · Konflikt addiert in **beide** Spalten · gemischter Batch · Batch mit ausschließlich Bot-Treffern |
| `tests/EmotePurge.Infrastructure.Tests/Integration` | `EmoteSetStatusService`: Datum vorhanden · kein Bot-Treffer → `null` · Sprung übersprungen bei leerem Set |
| Vitest | Sichtbarkeitslogik des Hinweises (`null` → nichts) |

Keine Api-Testfälle nötig: Es kommt kein `IEndpointFilter` hinzu und keine Filter-Reihenfolge
ändert sich (Regel 11).

## Live-Verifikation (Regel 16)

Keine Suite ersetzt sie hier, und „läuft durch" ist kein Nachweis. Der Nachweis ist:

1. Worker gegen einen getrackten Channel laufen lassen, in dem StreamElements oder Nightbot
   automatisiert postet.
2. In der DB `BotUseCount > 0` bei **unverändertem** `UseCount` derselben Zeile nachweisen.
3. Kommt binnen vertretbarer Zeit nichts zustande: testweise die ID eines tatsächlich aktiven
   Chatters in `Twitch:AdditionalBotAccountIds` eintragen, die Trennung nachweisen, Eintrag
   wieder entfernen.

Zusätzlich: Migration auf der Dev-DB fahren und prüfen, dass eine Api aus dem **alten** Stand
gegen das migrierte Schema weiterläuft — das ist die Annahme, auf der die Prod-Reihenfolge aus
A3 beruht.

## Ausdrücklich nicht in dieser Runde

- kein Toggle „mit Bots"
- kein Import der 903 Multi-Channel-IDs
- keine Pflegeoberfläche für Moderatoren
- keine Rückwirkung auf Bestandsdaten (technisch unmöglich)
- keine Nutzung von `/v2/channel/{id}/bots`

## Nicht vergessen

- **Regel 3:** Der Eintrag in `docs/DECISIONS.md` gehört in **denselben** Commit wie die
  Schemaänderung.
- **Regel 1:** Vor jedem Commit fragen.
- **Regel 22:** Vor dem Merge auf `main` eine unabhängige Zweitmeinung per Codex Sol.
- **Keine** Statuszeile im Backlog zu pflegen: `docs/Feature-Ideen-2026-08-01.md` führt für
  Bot-Filterung keine Idee — #31 kam als GitHub-Issue herein, nicht über den Backlog. Der
  Umsetzungsstand steht am Issue.
