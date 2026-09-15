# Shared Chat in der Nutzungszählung — Entwurf

**Datum:** 2026-09-06 · **Issue:** [#73](https://github.com/emotepurge/EmotePurge/issues/73) · **Status:** entworfen, nach Opus-Review und Codex-Adversarial-Review (Sol) überarbeitet, noch nicht geplant · **Geschwisterfall:** [Bot-Erkennung #31](2026-09-01-bot-erkennung-usage-analytics-design.md)

## Warum jetzt

Twitch spiegelt in einer Stream-Together-Session („Shared Chat") die Nachrichten aller
beteiligten Kanäle in jeden dieser Chats. Unser Worker sieht diese gespiegelten Nachrichten im
IRC des gejointen Kanals und zählt sie **unterschiedslos** als Nutzung dieses Kanals. Ein Emote
bekommt damit Nutzung, weil ein *fremder* Chat ein namensgleiches Emote verwendet hat — und wer
nach Nutzung entscheidet, was gelöscht wird, entscheidet dann auf Basis fremder Zahlen. Das ist
der Kern des Werkzeugs, nicht ein Randfall.

Wie groß der Effekt ist, ist gemessen, nicht geschätzt. Harness-Probeläufe vom 2026-09-06 (Prod,
7 Tage je Kanal, Anteil gespiegelter Nachrichten an allen Nachrichten):

| Kanal | Shared-Chat-Anteil |
|---|---|
| brudivoeller_tv | 84 % |
| ronnyberger | 66 % |
| vassilly | 20 % |
| knirpz | 0 % |
| papaplatte | 0 % |

Drei von sechs Harness-Kandidaten sind substanziell betroffen; in einem davon stammen fünf von
sechs Nachrichten aus fremden Räumen.

**Termindruck, und zwar ein konkreter.** #73 ist der einzige offene Posten, der den Termin der
„Scharfschaltung" von [#69](https://github.com/emotepurge/EmotePurge/issues/69) (Genauigkeits-Harness
für den Chat-Log-Backfill) bestimmt. Der Harness misst Replay-Zählung gegen Live-Zählung; heute
zählen **beide** Seiten Shared Chat mit, und zwar gleich falsch. Ändert man nur eine Seite, misst
der bindende 30-Tage-Lauf die eigene Änderung statt der Genauigkeit. Ändert man beide, gilt der
Vergleich erst für Tage, an denen die Live-Seite **den ganzen Tag** mit der neuen Regel gezählt
hat.

Daraus folgt der früheste Starttermin, und er ist einen Tag später, als man im Kopf rechnet:
`HarnessRunner` setzt `to = heute − 1` und `from = to − (days − 1)` (`HarnessRunner.cs:173-179`).
Ein Lauf am Kalendertag D+30 nach dem Prod-Deploy D bekommt `from = D` — den Deploy-Tag selbst
als ersten Fenstertag. Der Deploy-Tag ist aber ein **gemischter** Tag (D3): seine `UsageStat`-
Zeilen tragen Vormittags-Flushes nach alter und Nachmittags-Flushes nach neuer Regel. Der erste
saubere Fenstertag ist D+1, der bindende Lauf startet damit frühestens an **D+31** — und D4 sorgt
dafür, dass der Deploy-Tag strukturell aus der Bewertung fällt, statt das einer Terminregel im
Kopf zu überlassen. Der Termin hängt am **Prod-Deploy**, nicht am Merge, nicht am Review. Jeder
Tag, den dieser Entwurf und seine Umsetzung länger brauchen, verschiebt den Oktober-Lauf um
denselben Tag.

Wie bei #31 gilt: Bestandsdaten lassen sich **nicht** nachträglich bereinigen. In den bereits
geschriebenen Zeilen steckt keine Raum-Dimension mehr. Der Entwurf macht den Bruch sichtbar,
statt ihn zu verschweigen — in zwei Zügen (D1), und bis der zweite kommt, zeigt die Oberfläche
weiter die gewohnte Summe (D5), damit kein Manager in der Zwischenzeit auf einen unerklärten
Sturz hin löscht.

## Ist-Zustand, verifiziert am 2026-09-06

Alle Befunde sind am Code oder am installierten Paket belegt; die Zeilenangaben wurden im Review
nachgeprüft.

### Live-Pfad

| Ort | Befund |
|---|---|
| `TwitchChatManager.OnMessageReceived` (`src/EmotePurge.Worker/TwitchChatManager.cs:489-530`) | liest aus `ChatMessage` nur `Channel`, `Username`, `Message`, `UserId`, `Badges`. `RoomId` und `UndocumentedTags` kommen in der ganzen Datei nicht vor. Ablauf: `emoteMatchCache.GetChannelEmotes(Channel)` → früher Ausstieg bei leerem Set → `isBot = botChatterDetector.IsBot(UserId, Badges)` **einmal pro Nachricht** → `EmoteNameMatching.MatchEmoteIds(...)` → je Treffer `usageCounter.Increment(emoteId, isBot)`. |
| Kanal-Identität im Nachrichtenpfad | **Der Live-Pfad kennt die Twitch-ID des gejointen Kanals nicht aus der Datenbank.** `TwitchChatManager` ist durchgehend namensbasiert (`_desiredChannels`, `_lastMessageByChannelTicks`, `JoinChannelAsync(string channelName)`, `EnsureJoinedAsync`); alle Join-Aufrufer liefern Namen (Boot-Recovery über `IChannelService.ListActiveChannelNamesAsync()`, Redis-Kommandos schneiden den Namen aus dem Pub/Sub-String, `Worker.cs:34-37`). `Channel.TwitchChannelId` existiert seit #44 (`src/EmotePurge.Core/Entities/Channel.cs:6`), wird aber nie in den Nachrichtenpfad gefädelt. `EmoteMatchCache.GetChannelEmotes` ist ebenfalls namensverschlüsselt. **An jeder Nachricht liegt die ID dagegen an:** `ChatMessage.RoomId` ist der `room-id`-Tag, und der ist die Twitch-ID des Raums, in dem die Nachricht ankommt. |
| TwitchLib.Client **4.0.1** (`EmotePurge.Worker.csproj:12`) | Per Reflection am installierten `TwitchLib.Client.Models.dll` **und** an der TwitchLib-Quelle zum Tag 4.0.1 geprüft: `ChatMessage.RoomId` existiert **typisiert** (`Tags.cs` kennt `room-id`). `source-room-id` kennt `Tags.cs` **nicht**; es fällt im `ChatMessage`-Konstruktor in den `default`-Zweig, der unbekannte Tags nach `UndocumentedTags` (`Dictionary<string,string>`) schreibt. Dasselbe gilt für die übrigen Shared-Chat-Marker `source-id`, `source-badges`, `source-badge-info`. |
| `ChatMessage.UndocumentedTags` ist **`null`, nicht leer** | Per Konstruktionsprobe am installierten Paket belegt: das Dictionary wird **nur angelegt, wenn mindestens ein unbekannter Tag vorhanden ist**. Eine Nachricht mit dem Standard-Tagsatz (`room-id`, `user-id`, `badges`, `display-name`, `tmi-sent-ts`) hat `UndocumentedTags == null`. Dass Twitch meist `client-nonce` oder `flags` mitschickt und das Dictionary deshalb oft existiert, ist Zufall der Tag-Zusammensetzung, kein Vertrag — `client-nonce` kommt nur bei Nachrichten mit Nonce. |
| `IEmoteUsageCounter` / `EmoteUsageCounter` | `Increment(string emoteId, bool isBot)`, `Merge(...)`, `DrainAndReset()`, `PendingEmoteCount`. Wert ist `EmoteUsageCounts` = `readonly record struct (int Human, int Bot)` in `src/EmotePurge.Core/Services/IUsageStatFlushService.cs:15`. Das Dictionary ist **nur nach Emote-ID verschlüsselt**; bot/human ist ein Feld im Wert, keine zweite Schlüsseldimension. `AddOrUpdate` mit `TArg`-Overload, allokationsfrei auf dem Hot Path; `Merge` arbeitet über den Werttyp als Ganzes. |
| `UsageFlushWorker` | tickt 30 s, `DrainAndReset()` → `IUsageStatFlushService.FlushAsync`. |

**Damit ist eine Behauptung des Issues widerlegt:** man muss `RawIrcMessage` **nicht** selbst
parsen. Der Zugriff geht über `UndocumentedTags` — aber **null-sicher**, s. B1. Ein
`TryGetValue` direkt auf der Property wirft bei jeder gewöhnlichen Nachricht ohne unbekannte
Tags; ein Indexer wirft zusätzlich bei jeder Nachricht ohne `source-room-id`. Beides ist nicht
„selten", sondern der Normalfall außerhalb einer Session.

### Replay-/Harness-Pfad

| Ort | Befund |
|---|---|
| `ReplayDayCounter.Count` (`src/EmotePurge.Worker/Harness/ReplayDayCounter.cs:104-159`) | Signatur `(DateTime sentAtUtc, string? userId, IReadOnlyList<KeyValuePair<string,string>> badges, string? roomId, string? sourceRoomId, string text)`. |
| Shared-Chat-Behandlung dort (:116-119) | `sourceRoomId` gesetzt **und** ordinal ungleich `roomId` → `_sharedChatMessageCount++`. **Rein diagnostisch.** Die Nachricht durchläuft danach denselben Pfad wie jede andere: `_botMessageCount++` bzw. `_humanChatters.Add(chatter)` **pro Nachricht** (:130-135, vor jedem Matching), Treffer in `_humanCounts`/`_botCounts` (:137-140), Zellen der k-Verteilung (:147-155), Tokenisierung (:158). Der Docstring (:99-102) sagt es ausdrücklich: „A shared-chat message also still counts, the way the live worker counts it today, and is marked." |
| Verbreitete Fehlannahme | Die Zweiwegverzweigung bei :137 ist `isBot ? _botCounts : _humanCounts` — **Bot/Mensch, nicht Shared Chat**. Es gibt heute keinen Shared-Chat-Ausschluss, nirgends. |
| `_sharedChatMessageCount` | ein **Nachrichten**zähler, kein Pro-Emote-Split. Welche Emote-Treffer aus fremden Räumen kamen, ist heute nicht rekonstruierbar. |
| `ReplayDayLine` (`ReplayModels.cs:109-126`) | 17 Felder, darunter `MessageCount`, `BotMessageCount`, `SharedChatMessageCount`, `HumanCounts`, `BotCounts`, `DistinctChatters` (= `_humanChatters.Count`, `ReplayDayCounter.cs:178`). |
| Zweiter Chatter-Zähler | `HarnessRunner` führt daneben einen **fensterweiten** `distinctChatters`-HashSet (:261, :296, :395 → Markdown-Bericht :562, „Distinkte Chatter im Fenster"), der heute **jeden** Chatter mit User-ID zählt — Bots und Fremde inklusive. |
| `ReplayUsageRow` (`ReplayModels.cs:28`) und `UsageStatRowDto` (`IUsageStatQueryService.cs:138`) | beide `(EmoteId, Date, UseCount, BotUseCount)` — die Live-Seite des Vergleichs kennt nur zwei Spalten. |
| `HarnessRunner.AlgorithmVersion` = `"harness-1"` (`HarnessRunner.cs:60`) | Docstring: „Part of the run identity: a changed counting rule must not resume a file counted by the old one. Bump it whenever the matching, the day boundaries or the day-line shape change." Vergleich in `FindFrozenWindow` (:446) disqualifiziert Resume-Kandidaten; `HarnessReportFile.ReadHeader` (:199-206) vergleicht die volle Identität byte-genau und wirft `HarnessReportIdentityMismatchException` → `ExitPreconditionViolated` (3) in `ExecuteAsync` (:298-306). Die Version geht über `BuildFileName` (:136-146) in den sha256-Dateinamen-Hash ein. |
| Exit-Codes (`HarnessRunner.cs:63-89`) | `ExitSuccess` 0, `ExitInvalidArguments` 2, `ExitPreconditionViolated` 3, `ExitAbortedWithResumePoint` 4, `ExitUndecidable` 5, `ExitUnexpectedError` 6. |
| `HarnessCommandLine` | akzeptiert genau `harness <kanal> [--days <n>]` (`HarnessCommandLine.cs:44-65`), jede andere Form endet mit deutscher stderr-Zeile und Exit 2; getestet in `HarnessCommandLineTests`. |
| `HarnessInputHash.Compute` | hasht die Live-Zeilen als `r\|<id>\|<date>\|<UseCount>\|<BotUseCount>` (:70) und ist Teil von `HarnessRunIdentity` und damit des Dateinamens. Klassendoc: „a changed usage row … starts a new run instead of continuing a meaningless one." **Die dritte Spalte kennt der Hash nicht.** |
| `HarnessRunIdentity` (`HarnessReportFile.cs:18-28`) | `ChannelId, TwitchChannelId, ChannelName, WindowFrom, WindowTo, BotSplitCutover, BotAccountIds, AlgorithmVersion, InputHash`. |
| `HarnessOptions` (`src/EmotePurge.Worker/Harness/HarnessOptions.cs`) | POCO, gebunden aus dem Konfigurationsabschnitt `Harness:*` — der natürliche Ort für eine explizite Laufeinstellung. |
| `.jsonl`-Format | camelCase, `WhenWritingNull` ausgelassen, eine Zeile je Eintrag, Hülle `HarnessJsonLine` mit `kind` (`header\|day\|event`). |
| **Drei** `LogTotal`/`LiveTotal`-Paare in `ReplayFidelityCalculator` | (1) `BuildDayFacts` (:107-152) → `ReplayDayRatio`: `Sum(HumanCounts) + Sum(BotCounts)` gegen `Σ(UseCount + BotUseCount)`; daraus `Ratio`, `LiveGapSuspected`, `CoverageQuestionable`. (2) `BuildPlausibility` (:283-307) → `LogTotalWithBots`, ausdrücklich nicht bindend. (3) **`BuildGate` (:197-260) über `BuildPopulation` (:160-195) → `ReplayGateMetrics.HumanLogTotal`/`HumanLiveTotal`** und daraus `TotalDeviation`, `Top20Recall`, `BottomQuartilePrecision` — **die präregistrierten, bindenden Zahlen.** `BuildPopulation` ist **human-only auf beiden Seiten**: `HumanCounts` gegen `UseCount`. Docstring (:155-158): „human-only on both sides: live is `UseCount` (what the grid actually shows), log is the human hit count." |
| `Rated` (:149) | verlangt `HasLog`, `HumanOnly`, `!LiveGapSuspected`, `!CoverageQuestionable`. `HumanOnly` ist `Day >= ReplayWindow.BotSplitCutover` (:127); der Stichtag kommt aus `IUsageStatQueryService.GetEarliestBotUsageDateAsync` (`HarnessRunner.cs:196`) und steht als `BotSplitCutover` in der Identität. Ist er `null`, gibt es **keinen bewerteten Tag**. Der Harness rechnet durchgehend in **UTC-Tagen** (`DateOnly.FromDateTime(sentAtUtc)`, UTC-Tag der `UsageStat`-Zeile). |
| Gate-Gründe (`ReplayGateIneligibleReasons`, `ReplayModels.cs:84 ff.`) | `run-incomplete`, `window-not-30-days`, `RatedDaysBelowTwenty`, `LiveTotalZero`. |
| `BuildDiagnostics` (:385) | reicht `Sum(SharedChatMessageCount)` als `ReplayDiagnostics.SharedChatMessages` durch — die **einzige** Stelle, an der Shared Chat den Rechenkern verlässt. |
| Präregistrierte Schwellen (Klassendoc :10-13, Konstanten :18-25) | `RequiredWindowDays=30`, `RequiredRatedDays=20`, `TopSize=20`, `RequiredQualifiedEmotes=30`, `MinLiveUsesPerThirtyDays=20`, `MinLiveUsesFloor=5`; Abweichung ≤ 10 %, Top-20-Recall ≥ 0,9, Bottom-Quartil-Precision ≥ 0,8; „published in issue #69 and are read against these numbers by a human". **Keine nimmt Bezug auf Shared Chat.** |
| Golden-Files | **keine committeten.** `harness-reports/` ist gitignored (`.gitignore:60-62`), im Repo liegt keine `.jsonl`/`.report.json`. Alle Referenzwerte fürs Feldformat stehen ausschließlich in Tests, und die liegen **flach** in `tests/EmotePurge.Worker.Tests/` (kein `Harness/`-Unterordner): `ReplayDayCounterTests.cs:124-136` (`SharedChatMessage_IsCountedAndMarked`), `ReplayFidelityCalculatorTests.cs:340-372`, `HarnessRunnerTests.cs:330-385`, `HarnessReportFileTests.cs`, `HarnessInputHashTests.cs:117`. |
| `JustlogRawLineParser` (`src/EmotePurge.Infrastructure/ChatLogArchive/JustlogRawLineParser.cs:115-126`) | liest `room-id` und `source-room-id` bereits, normalisiert leer → `null` und füttert beide als 4./5. Argument in `ChatLogMessage`. Die übrigen `source-*`-Marker liest er **nicht**. |
| `ChatLogMessage` (`src/EmotePurge.Core/ChatLogArchive/ChatLogArchiveModels.cs:11-17`) | Record mit `RoomId` und `SourceRoomId` — nachgeprüft, die Namen stehen fest. `HarnessRunner.cs:304-305` reicht sie positional an `Count` weiter. |
| Covering-Index (`src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs:40-44`) | `(EmoteId, Date)` unique, `INCLUDE (UseCount)`, Kommentar: „so range-sum queries over (EmoteId, Date) can be answered as an index-only scan." |
| Dependabot (`.github/dependabot.yml:39-41`) | hat eine `ignore`-Liste (heute nur `Microsoft.OpenApi`, major). TwitchLib steht nicht darin. |
| `docs/Architectur.md` | `:80` („getrennt nach Mensch und Bot"), `:232` („`BotUseCount` steht bewusst nicht im Include, weil ihn heute keine Aggregat-Query liest"), `:270` (Feldliste von `UsageStat`) — alle drei werden durch eine dritte Spalte unvollständig. |

### Bot-Split #31 als Präzedenzfall

Der Bot-Split ist der direkte Geschwisterfall: eine Chatter-Dimension, die nachträglich nicht
rekonstruierbar ist, wird als zusätzliche Spalte derselben Zeile getrennt. Er wurde am
**2026-09-01** in Prod migriert und deployt — also vor #73, was B5 ausnutzt. Dieser Entwurf baut
auf demselben Grundriss, und alles Folgende ist am Bestand belegt:

- **Entity** `UsageStat` (`src/EmotePurge.Core/Entities/UsageStat.cs:12-15`), Kommentar: „A row
  stays (EmoteId, Date)-unique regardless of this column: a row can carry UseCount = 0 while
  BotUseCount > 0 … the read queries in UsageStatQueryService filter on UseCount > 0 to keep such
  a row from reading as 'used'."
- **Migration** `20260901193259_AddUsageStatBotUseCount.cs:18-23`: `AddColumn<int>(nullable:
  false, defaultValue: 0)`. Die Begründung (:13-17) gilt wörtlich weiter: „NOT NULL DEFAULT 0 on
  the column itself, not just the CLR default: Postgres >= 11 adds a NOT NULL DEFAULT <constant>
  column as a catalog-only change (no table rewrite of the largest table), and the still-running
  old image's UNNEST upsert writes without this column at all until it is redeployed — it needs
  the default to come from the column, not from application code it does not yet have."
- **Flush** `UsageStatFlushService.cs:48-49, 59-68`: getrennte Arrays je Spalte, atomarer
  `UNNEST`-Upsert, `ON CONFLICT (EmoteId, Date) DO UPDATE` addiert jede Spalte für sich. Kein
  „nur Human > 0"-Filter: ein reiner Bot-Batch erzeugt `UseCount = 0, BotUseCount = n`. Weil
  innerhalb eines Tages **addiert** wird, trägt die Zeile eines Tages alle Flushes dieses Tages —
  auch solche, die nach verschiedenen Regeln gezählt wurden (relevant für den Deploy-Tag und für
  einen Rollback-Tag, D3/D4).
- **Lesepfad** `UsageStatQueryService`: `UseCount > 0` ist überall der Filter
  (`GetUsageContextAsync` :71, `GetDailySeriesAsync` :124, `GetChannelSeriesAsync` :212);
  `GetTotalsByEmoteIdsAsync` (:244-248) braucht ihn nicht, weil es nur `UseCount` summiert.
  `GetEarliestBotUsageDateAsync` (:251-270) ist die einzige Stelle mit `BotUseCount > 0`
  (`MIN(Date)` über die **gesamte** Historie, inklusive archivierter Emotes). `GetRowsAsync`
  (:292-318) gibt als einzige beide Spalten roh heraus — bewusst ungefiltert, für den Harness.
- **`UseCount` ist exklusiv, nicht inklusive `BotUseCount`** — belegt über den Entity-Kommentar,
  `Increment` (erhöht entweder/oder) und `UsageStatQueryServiceTests.cs:126-146`, das
  `UseCount = 0, BotUseCount = 3` seedet und erwartet, dass die Zeile nicht als Nutzung zählt.
- **DECISIONS 2026-09-01** „Bot-Nutzung bekommt eine zweite Spalte, keine zweite Zeile"
  (`docs/DECISIONS.md:1496`), Betrifft-Zeile führt `docs/Architectur.md` mit. **E1** verwarf die
  dritte Index-Dimension („verdoppelt die Zeilenzahl und zwingt alle fünf Aggregat-Queries in eine
  neue Form, inklusive des `GroupBy`-Übersetzungsrisikos aus Regel 10"); **E3** verwarf den
  Frontend-Toggle („beantwortet eine Frage, die der Erstbesuch nicht stellt … Stattdessen ein
  reiner Hinweis mit Datum … eine Ehrlichkeitsaussage, kein Steuerelement"); **E4** hält fest,
  dass das abgeleitete Datum „die erste *Sichtung* eines Bots in den Daten [ist], nicht der
  Beginn der Trennung selbst — der eigentliche Bruch ist ein Deploy-Ereignis, kein Datenereignis."
- **Frontend-Präzedenz**: kein Bedienelement, nur die reine Funktion
  `web/src/app/core/emotes/bots-excluded-caption.ts` plus i18n-Key `usageStats.botsExcludedSince`
  (DE: „Nachrichten bekannter Bots zählen seit dem {{ date }} nicht mit; Zahlen davor enthalten
  sie noch.").
- **DECISIONS 2026-09-06** (Harness) setzt den Präzedenzfall fürs Teilen: geteilte Regeln „laufen
  über dieselben Funktionen wie der Live-Pfad, nicht über Kopien" — Fensterstart und Bot-Stichtag
  sind aus `EmoteSetStatusService` herausgezogen; das Matching ist „geteilt, nicht nachgebaut".

## Entscheidungen

D1 bis D5 sind vom Nutzer bestätigt; D4 in der fail-closed-Fassung nach dem Codex-Review. Alle
stehen hier als **entschieden**. Die verworfenen Optionen stehen mit ihrem Preis dabei, damit
niemand sie erneut durchrechnet. Zwei Dinge dürfen sich dabei nicht vermischen: **D3 regelt, was
der Harness vergleicht; D5 regelt nur, was die UI-Lesequeries liefern.** Der Harness liest über
`GetRowsAsync` Rohzeilen, die UI über die Aggregat-Queries — zwei Wege, die sich in Zug 1 nicht
berühren.

### D1 — Zwei Lieferungen, Zählung zuerst

**Zug 1** ist die Zählung: Tag-Erkennung im Live-Pfad, dritte Spalte samt Migration, Flush,
Harness-Gegenstück samt Stichtag (D4), der `AlgorithmVersion`-Bump und der DECISIONS-Eintrag —
bei **unverändert sichtbaren Zahlen** (D5). Er wird gemergt und deployt; **mit dem Prod-Deploy
beginnt die 30-Tage-Uhr** (erster sauberer Fenstertag D+1, frühester bindender Lauf D+31, s.
„Warum jetzt"). Die Uhr läuft, obwohl die UI noch summiert, weil der Harness die Rohzeilen liest
und die Trennung dort ab dem ersten Tag steht.

**Zug 2** ist die Anzeige und kommt danach: Er dreht den Lesepfad um (D5) **und** liefert
Caption oder Toggle im selben Zug. Er ist **taktneutral**: eine Änderung an Lesepfad und
Oberfläche ändert die Zählung nicht und setzt die Uhr nicht zurück — dieselbe Einordnung, die
bereits für #82, #83 und #87 gilt.

Verworfen:

- **Alles in einem Zug.** Jeder Tag Frontend-Arbeit (API-Feld, Query, Vitest-Spec, E2E-Fall,
  Übersetzung in zwei Sprachen, Review) hängt sich direkt an den Starttermin des Messfensters.
- **Nur die Caption schon in Zug 1.** Ehrlich ab Tag 1, kostet aber die Kette API-Feld → Query →
  Spec → E2E **vor** dem Deploy — ein bis zwei Tage späterer Uhrenstart. D5 erreicht die
  Ehrlichkeit auf anderem Weg: Es gibt nichts zu erklären, solange sich nichts sichtbar ändert.

### D2 — Fremd hat Vorrang vor Bot

Drei **disjunkte** Spalten, und die Raum-Frage wird **zuerst** gestellt:

| Spalte | Bedeutung ab dem Deploy |
|---|---|
| `UseCount` | Menschen im eigenen Raum |
| `BotUseCount` | Bots im eigenen Raum |
| `SharedChatUseCount` | alles aus fremden Räumen, **Bots inklusive** — und nach B1 auch das Unbestimmbare |

Begründung: Das deckt sich mit dem, was der Harness heute als `SharedChatMessageCount` zählt —
alle gespiegelten Nachrichten, ohne Bot-Unterscheidung. Beide Seiten müssen sich decken; das ist
der Zweck der ganzen Übung (D3). Eine Nachricht aus einem fremden Raum ist für diesen Kanal
fremd, egal wer sie geschrieben hat.

**Nebenwirkung, die ausdrücklich in den DECISIONS-Eintrag gehört:** `BotUseCount` bedeutet ab
dem Deploy „Bots im eigenen Raum" statt „Bots überall". `GetEarliestBotUsageDateAsync` sieht
damit nur noch **eigene** Bots — mit einer Einschränkung, die den Fall klein, aber nicht leer
macht: Die Methode ist `MIN(Date)` über die **gesamte** Historie. Bestandskanäle tragen aus der
Zeit vor #73 bereits Zeilen mit gespiegelten Bots in `BotUseCount`; ihr Minimum bewegt sich
nicht. Betroffen ist ein Kanal, der **nach dem #73-Deploy erstmals getrackt** wird und dessen
Bot-Nachrichten praktisch alle gespiegelt sind: dort bliebe das Datum `null`, und die
**Bot-Caption** erschiene nie, obwohl Bots ausgeschlossen werden. Das ist ein stiller Bruch in
einer Spalte, auf die niemand schaut, und er steht hier, damit er nicht in einem halben Jahr als
Bug wiederentdeckt wird.

Für das **Harness-Gate** ist dieselbe Nebenwirkung dagegen **erledigt**, nicht hingenommen: B5
definiert `HumanOnly` ab `harness-2` gegen den expliziten Shared-Chat-Stichtag aus D4, nicht
mehr gegen `BotSplitCutover`. Ein Null-Fall dort kann keinen Kanal mehr aussperren.

Verworfen:

- **Bot hat Vorrang.** `BotUseCount` behielte seine Bedeutung, aber fremde Bot-Nutzung bliebe in
  *unseren* Bot-Zahlen stehen — genau das, wogegen das Issue argumentiert. Und der Harness müsste
  seinen Nachrichtenzähler umdefinieren, um sich mit der Live-Seite zu decken.
- **Vierte Spalte `SharedChatBotUseCount`.** Erhält beide Dimensionen verlustfrei, kostet aber
  eine Spalte in Migration, Upsert, Counter-Record, Harness-Tageszeile, `ReplayUsageRow`,
  `UsageStatRowDto`, Input-Hash und allen zugehörigen Tests — für eine Unterscheidung, die
  **keine Lesequery stellt**. E1 hat die teurere Struktur schon einmal verworfen; dieser Entwurf
  wiederholt die Abwägung nicht. Der Codex-Vorschlag, D2 umzukehren oder die vierte Spalte zu
  nehmen, um den `BotSplitCutover`-Nullfall zu beheben, ist damit ebenfalls erledigt — B5 löst
  ihn ohne beides.

### D3 — Tagessummen über drei Komponenten, das Gate bleibt human-only, Shared Chat wird separat ausgewiesen und muss symmetrisch sein

Es gibt im Rechenkern **drei** Paare aus Log- und Live-Summe (Ist-Zustand), und die Regel ist für
jedes einzeln festzuhalten, weil „alle drei Komponenten" sonst auf jedes der drei Paare passt:

| Paar | Regel ab `harness-2` |
|---|---|
| `BuildDayFacts` → `ReplayDayRatio` | **drei** Komponenten auf beiden Seiten: `Human + Bot + SharedChat` gegen `UseCount + BotUseCount + SharedChatUseCount`. Dient `Ratio`, `LiveGapSuspected`, `CoverageQuestionable` — also der Frage „hat das Archiv den Tag überhaupt?" |
| `BuildPlausibility` → `LogTotalWithBots` | ebenfalls drei Komponenten, weiter nicht bindend |
| `BuildPopulation`/`BuildGate` → `HumanLogTotal`/`HumanLiveTotal`, `TotalDeviation`, `Top20Recall`, `BottomQuartilePrecision` | **bleibt human-only**: `HumanCounts` gegen `UseCount`. Es misst den **Zielvertrag** — eigene Menschen, das, was das Raster nach Zug 2 zeigt und was die Löschentscheidung tragen soll. Dass das Raster während der Übergangszeit aus D5 noch die Summe zeigt, ändert daran nichts: der Harness liest Rohzeilen, nicht das Raster, und misst gegen das Ziel, nicht gegen die Brücke. Der Docstring-Satz „what the grid actually shows" ist in Zug 1 entsprechend auf den Zielvertrag umzuformulieren. |

Zusätzlich wird die Shared-Chat-Summe **beider Seiten** getrennt berichtet — Tag für Tag und als
Fenstersumme — **und ihre Symmetrie ist Gate-Voraussetzung** (B5): weichen die beiden Summen
über die bewerteten Tage voneinander ab, ist der Lauf gate-untauglich, mit eigenem Grund. Das ist
kein viertes präregistriertes Gate mit eigener Schwelle, sondern eine Eignungsbedingung, wie
`RatedDaysBelowTwenty` eine ist.

Begründung für das Berichten und die Symmetriebedingung: Live- und Replay-Seite leiten die
Klassifikation **unabhängig** her (IRC-Tags via TwitchLib `RoomId`/`UndocumentedTags` gegen
Justlog-Tags via eigenem Parser). Ein Auseinanderlaufen der beiden Shared-Chat-Summen macht eine
falsche oder ungleich angewandte Regel messbar. Schlösse man Shared Chat auf beiden Seiten stumm
aus, bliebe eine falsche Regel unbemerkt — und zwar ausgerechnet in dem Lauf, der sie absegnen
soll.

**Was das Gate prüft und was nicht — ehrlich.** Codex hat richtig beobachtet, dass die
Bot/Fremd-Grenze von keiner bindenden Zahl geprüft wird: Ein fremder Bot, der auf einer Seite als
eigener Bot und auf der anderen als fremd einsortiert wird, bewegt weder die
Dreikomponenten-Summe noch das human-only-Gate. Der Schluss, der Lauf könne damit den
Vertragsbruch absegnen, den er sichtbar machen soll, ist aber überzogen: **Diese Grenze ist
produktseitig folgenlos**, weil beide Kategorien vom Zielraster ausgeschlossen sind — ob ein
fremder Bot in `BotUseCount` oder `SharedChatUseCount` steht, sieht kein Nutzer und entscheidet
keine Löschung. Die Grenze, die zählt — **eigener Mensch gegen fremder Mensch** — prüft das
human-only-Gate sehr wohl: Eine live fälschlich als eigen gezählte fremde Nachricht landet in
`UseCount` und im Replay nicht, und das bewegt `TotalDeviation` direkt. Die Symmetriebedingung
deckt zusätzlich die Fremd-Grenze als Ganzes ab; nur die Bot-Unterscheidung *innerhalb* des
Fremden bleibt ungeprüft, und das ist tragbar, weil sie nichts trägt.

**Was die Dreikomponenten-Summe leistet und was nicht.** Die **Tagessumme** ist invariant
gegenüber dem Split: Der Split verschiebt Masse zwischen Spalten, er erzeugt und vernichtet
keine. Deshalb bleibt `Ratio` an jedem Tag aussagekräftig, auch vor dem Deploy. **Für die
bindende Gate-Zahl gilt das nicht:** Sie ist human-only, und an Tagen vor dem Deploy enthält
`UseCount` fremde Menschen-Treffer, die `HumanCounts` nach neuer Regel nicht mehr enthält.
`TotalDeviation` stiege dort um den Fremdanteil — in `brudivoeller_tv` (84 %) reißt das die
10-%-Schwelle allein. Genau deshalb braucht das Gate einen Stichtag (D4), und deshalb hat die
Shared-Chat-Differenz **drei** Signaturen, nicht zwei:

| Tag | live `SharedChatUseCount` | Replay Shared-Chat-Treffer | Lesart |
|---|---|---|---|
| **vor** dem Deploy | 0 | > 0 (bei Session) | erwartet; alte Regel live, Tag nicht bewertbar |
| **am** Deploy-Tag | 0 < live < Replay | > 0 | gemischt: Vormittags-Flushes alt, Nachmittags-Flushes neu, in derselben Zeile addiert; Tag nicht bewertbar |
| **nach** dem Deploy | = Replay (bis auf Archiv-/Live-Lücken) | | Symmetrieprobe (B6); nur diese Tage sind bewertbar |

Ein Rollback-Tag im Fenster (D4, Restrisiko) hätte die mittlere Signatur — die Tabelle macht ihn
sichtbar, verhindert ihn aber nicht.

Verworfen:

- **Auch das Gate über drei Komponenten.** Dann misst das Gate eine Zahl, die kein Nutzer je
  sieht, und eine falsche Raum-Regel würde sich in der bindenden Zahl gegenseitig aufheben —
  das Gegenteil dessen, was das Berichten der Differenz erreichen soll.
- **Ein viertes präregistriertes Gate mit eigener Schwelle auf der Shared-Chat-Summe** (Codex).
  Es würde eine Grenze mit einer Schwelle versehen, die produktseitig nichts trägt, und die
  präregistrierte Liste um eine Zahl erweitern, für die es keine Messbasis gibt. Die
  Eignungsbedingung leistet das Nötige — ein Auseinanderlaufen macht den Lauf untauglich, statt
  ihn mit einer weiteren Zahl zu bewerten.
- **Nur eigene Nutzung, auch in den Tagessummen.** Schärfer interpretierbar, aber die neue Regel
  bliebe ungeprüft, und `LiveGapSuspected`/`CoverageQuestionable` würden an Vor-Deploy-Tagen
  falsch anschlagen, weil die Live-Seite dort mehr trägt.
- **Beides berichten, eine davon nachträglich bindend erklären.** Lädt dazu ein, nach dem Lauf
  die günstigere Zahl zur bindenden zu erklären — wogegen Präregistrierung gerade schützt.

### D4 — Ein explizit konfigurierter Shared-Chat-Stichtag, fail-closed

Ein Stichtag `SharedChatCutover` als **UTC-Tag** (der Harness rechnet in UTC-Tagen, Ist-Zustand;
eine Zeitzonen-Fehldeutung verschöbe genau einen Tag, und ein Tag ist hier die ganze Sache),
**explizit konfiguriert** (Ort: `HarnessOptions`, Abschnitt `Harness:*`), gesetzt auf den **Tag
nach dem Prod-Deploy**, D+1. Er wirkt nach dem Muster von `BotSplitCutover`, mit drei
Verschärfungen:

- **`HumanOnly` ist ab `harness-2` `Day >= SharedChatCutover`** — und nur das (B5). Der
  Deploy-Tag (gemischt, D3) und alle Tage davor fallen damit strukturell aus der Population des
  Gates.
- Der Stichtag ist **Teil von `HarnessRunIdentity`** und damit des Kopfes und des Dateinamens.
  Ein Lauf mit anderem Stichtag setzt keine alte Datei fort; der byte-genaue Identitätsvergleich
  in `ReadHeader` fängt ihn wie jede andere Abweichung.
- **Ohne gültigen Stichtag bricht ein Lauf ab**, mit Nicht-Null-Exit nach dem Muster
  `ExitPreconditionViolated` (3) — fehlend, leer oder nicht als UTC-Datum parsebar sind
  gleichbedeutend, mit deutscher stderr-Zeile, die den Konfigschlüssel nennt. Ein Lauf ohne
  Gate-Anspruch braucht einen **ausdrücklichen Diagnosemodus** (ein zusätzliches
  Kommandozeilen-Argument neben `--days`, Form legt der Plan fest, Vertrag in
  `HarnessCommandLine`): nur der darf ohne Stichtag laufen, und er **weist dann kein Gate-Urteil
  aus** — im Bericht steht an dessen Stelle, dass es ein Diagnoselauf war. Der Markdown-Bericht
  bekommt eine Zeile für den Stichtag, analog zu „Bot-Split-Stichtag" (`HarnessRunner.cs:553`).

Warum fail-closed und nicht, wie zunächst vorgesehen, „läuft trotzdem, Gate `ineligible` mit
Grund": Ein vergessener Konfigwert und ein gewollter Diagnoselauf sind **maschinell nicht
unterscheidbar**, wenn beide mit Exit 0 enden. Der bindende Lauf wird einmal gestartet und
dreißig Tage später gelesen; die Person, die ihn liest, ist nicht die Einstellung, die ihn
gestartet hat. Ein Exit 3 am Start kostet eine Minute, ein Exit 0 mit `ineligible` kostet das
Fenster.

**Dokumentiertes Restrisiko, kein Baustein:** Die Identität beweist nur, *welcher* Wert
eingetragen wurde — nicht, dass die neue Live-Regel seit diesem Tag lückenlos aktiv war. Ein
Rollback auf das alte Image nach dem Stichtag zählt wieder nach alter Regel, während der
Stichtag Trennung behauptet; das alte Image ist ja bewusst schema-kompatibel (B3). Codex schlägt
vor, den Stichtag an persistierte Writer- oder Deployment-Versionen zu koppeln. Das wird in
Zug 1 **nicht** gebaut: Es hieße, jeden Flush mit einer Versionsmarke zu schreiben oder eine
Deployment-Tabelle zu pflegen — Aufwand gegen ein Ereignis, das wir selbst auslösen und
kontrollieren. Stattdessen eine **betriebliche Regel**, die in die DoD und ins Issue #69 gehört:
**kein Rollback des Worker-Images während des Messfensters; passiert er doch, ist der Lauf
ungültig, der Stichtag wird auf den Tag nach dem erneuten Deploy gesetzt, und die Uhr beginnt
neu.** Die Drei-Signaturen-Tabelle aus D3 würde einen solchen Tag als gemischt sichtbar machen —
das ist ein Erkennungsmittel im Nachhinein, kein Schutz.

Verworfen:

- **Aus den Daten abgeleitet** (`MIN(Date) WHERE SharedChatUseCount > 0`, E4-Muster). In `knirpz`
  und `papaplatte` (0 % Shared Chat) bliebe der Stichtag **dauerhaft `null`**. Durchgerechnet:
  `HumanOnly` = false für alle Tage → keine bewerteten Tage → leere Population →
  `PopulationSize = 0`, `TotalDeviation`/`Top20Recall`/`BottomQuartilePrecision` = `null`, Gründe
  `RatedDaysBelowTwenty` + `LiveTotalZero`, `GateEligible` = false, Exit 0. **Ausgerechnet die
  sauberen Kanäle fielen heraus**, mit einem Bericht, der nach Datenmangel aussieht. Für den
  Bot-Stichtag ist die Ableitung tragbar, weil „nie ein Bot gesehen" dort tatsächlich „nichts zu
  trennen" bedeutet; für Shared Chat bedeutet „nie gesehen" nur „keine Session", die Regel gilt
  trotzdem.
- **Weich: ohne Stichtag laufen, Gate `ineligible` mit eigenem Grund.** War die Fassung vor dem
  Codex-Review; verworfen aus dem Grund oben.
- **Gar kein Stichtag, dafür eine harte Vorbedingung im Runner** („Fensterstart ≥ D+1, sonst
  Exit 3"). Schützt nur den Gesamtlauf, nicht einzelne Tage, und weicht vom direkt
  danebenstehenden `BotSplitCutover`-Muster ab, ohne etwas zu gewinnen.
- **Kopplung an persistierte Writer-Versionen** (Codex) — s. Restrisiko.

### D5 — Der Lesepfad summiert übergangsweise weiter

Codex' schwerster Befund, und er trifft: Zug 1 in der vorigen Fassung ließe angezeigte
Nutzungszahlen laut unserer eigenen Messung um bis zu 84 % fallen, ohne dass die Oberfläche sagt
warum — in einem Werkzeug, mit dem über Löschungen entschieden wird, lesbar als „unbenutzt". Zug 2
hatte weder Termin noch Platz in der DoD; „einige Tage" war keine belastbare Obergrenze.

Entscheidung: **Zug 1 schreibt drei Spalten, aber die produktiven Lesequeries liefern bis Zug 2
weiterhin `UseCount + SharedChatUseCount`.** Sichtbar ändert sich nichts; es gibt keinen
unerklärten Sturz. Die 30-Tage-Uhr startet trotzdem, weil der Harness über `GetRowsAsync` die
**Rohzeilen** liest und die Trennung ab dem ersten Tag sieht — der Lesepfad der UI und der des
Harness sind verschiedene Wege, und D3 bleibt von D5 unberührt.

Im Einzelnen, alles in `UsageStatQueryService`:

- Die Summen in `GetUsageContextAsync`, `GetDailySeriesAsync`, `GetChannelSeriesAsync` und
  `GetTotalsByEmoteIdsAsync` laufen über `UseCount + SharedChatUseCount`.
- **Die Filter `UseCount > 0`** (:71, :124, :212) **prüfen ebenfalls gegen die Summe.** Sonst
  verschwände eine Zeile mit ausschließlich fremder Nutzung aus der Ansicht — wieder ein
  sichtbarer Sprung, nur ein kleinerer. Die Bot-only-Mechanik bleibt: eine Zeile mit nur
  `BotUseCount` liest sich weiter als unbenutzt.
- `GetRowsAsync` bleibt roh und unverändert (außer der dritten Spalte im DTO, B4).
  `GetEarliestBotUsageDateAsync` bleibt unberührt.

**Das ist eine befristete Konstruktion mit Entfernungsauslöser.** Der Auslöser ist Zug 2: Er
dreht den Lesepfad auf `UseCount` allein und liefert Caption oder Toggle **im selben Zug** — beides
zusammen, weil das eine ohne das andere entweder den Sturz oder die Erklärung ohne Anlass
liefert. Der Preis, ehrlich: Bleibt Zug 2 liegen, bleibt die Summe stehen, und das Werkzeug zählt
für seine Nutzer weiter fremde Nutzung mit — nur eben ohne Sprung. Damit man sieht, dass die
Brücke noch drin ist, gibt es **einen Test, der die Übergangssummierung festnagelt** (B7): Er
seedet eine Zeile mit `UseCount = 0, SharedChatUseCount = n` und erwartet, dass sie in jedem
Lesepfad als benutzt gilt. Zug 2 muss diesen Test **bewusst brechen und umdrehen** — er ist der
Marker, nicht ein Kommentar.

**Zwei Nebenwirkungen, beide in den DECISIONS-Eintrag:**

1. Die Übergangssumme enthält nach D2 auch **fremde Bots**, die vor #73 in `BotUseCount` lagen
   und damit unsichtbar waren. Die angezeigte Zahl kann in der Übergangszeit also geringfügig
   **höher** liegen als vor dem Deploy — um genau die Bot-Nachrichten aus fremden Räumen. Das ist
   klein (Bots sind ein kleiner Anteil, und nur der gespiegelte davon) und in der Richtung
   harmlos (nichts fällt auf „unbenutzt"), aber es ist eine Änderung, nicht Stillstand.
2. Der **Covering-Index** `INCLUDE (UseCount)` deckt Queries, die `SharedChatUseCount` mitlesen,
   **nicht mehr** — der Index-Only-Scan, den der Kommentar in `AppDbContext.cs:40-41` als Zweck
   nennt, greift in der Übergangszeit nicht. Ob die Spalte für die Übergangszeit ins `INCLUDE`
   kommt (Index-Rewrite jetzt und Rückbau in Zug 2, oder dauerhaft, falls Zug 2 eine
   Drilldown-Serie über die Spalte baut) oder der Heap-Zugriff für einige Wochen hingenommen
   wird, ist eine offene Frage für den Plan — mit `EXPLAIN` auf der Dev-DB zu entscheiden, nicht
   aus der Erinnerung an #31.

Verworfen:

- **Caption schon in Zug 1.** Ehrlich ab Tag 1, kostet ein bis zwei Tage späteren Uhrenstart
  (API-Feld, Query, Spec, E2E vor dem Deploy). D5 kauft dieselbe Ehrlichkeit ohne diese Tage —
  solange Zug 2 nicht liegen bleibt.
- **Bei der reinen Zweiteilung bleiben und Zug 2 nur verbindlich terminieren.** Schnellster
  Start, aber das Fenster mit unerklärten Zahlen bleibt real, und ein Termin im Kopf ist keine
  Obergrenze.

## Bausteine — Zug 1

### B1 — Die Regel, einmal, mit drei Ausgängen

Eine **pure, TwitchLib-freie Funktion** entscheidet die Raum-Frage. Sie wird aus
`ReplayDayCounter.cs:116-119` **herausgezogen, nicht neu geschrieben** (Präzedenz DECISIONS
2026-09-06), beide Seiten rufen dieselbe Funktion, und sie hat **drei** Ausgänge:

| Befund | Ausgang |
|---|---|
| `source-room-id` gesetzt und ordinal ungleich `room-id` | **fremd** |
| `source-room-id` gesetzt und gleich `room-id` | **eigen** — der Ursprungskanal einer Session, dessen eigene Nachrichten Twitch ebenfalls taggt |
| kein `source-*`-Marker vorhanden | **eigen** — der Normalfall außerhalb jeder Session; alles andere würde jede gewöhnliche Nachricht verdächtig machen |
| **andere `source-*`-Marker vorhanden** (`source-id`, `source-badges`, `source-badge-info`), aber `source-room-id` fehlt oder ist leer | **unbestimmbar** — die Nachricht sagt „ich bin Teil einer Session", verrät aber nicht, wessen |

Der Codex-Befund dazu ist eng zu nehmen, und die Tabelle tut das: Ein fehlender Marker ist kein
Verdacht. Erst ein *widersprüchlicher* Tagsatz ist einer. Unbestimmbare Nachrichten werden **nicht
als eigen** gezählt — auf beiden Seiten landen ihre Treffer in der Shared-Chat-Komponente
(konservativ: fremd, bis das Gegenteil belegt ist; eine vierte Spalte dafür verwirft D2), und der
Fall wird **gezählt und ausgewiesen**: im Harness als eigener Nachrichtenzähler in Tageszeile
und Diagnostik, live als Zähler, den der Worker sichtbar macht (Vehikel — Health-Publish,
Log-Zeile je Flush oder `WorkerStats` — legt der Plan fest; das Minimum ist eine Log-Zeile mit
der Anzahl je Flush). Damit steht das Enum der Zählkategorien (B2) bei **drei** Werten; die
Raum-Regel hat drei Ausgänge, und die Abbildung „unbestimmbar → Shared-Chat-Komponente" passiert
an derselben einen Klassifikationsstelle wie der Vorrang aus D2.

Weitere Randfälle, die der Test der Regel abdeckt: leerer String in `source-room-id` (→ wie
fehlend zu behandeln; der Justlog-Parser normalisiert leer bereits zu `null`, die Live-Seite
muss dasselbe tun, und bei vorhandenen anderen `source-*`-Markern ist es der unbestimmbare
Fall); `room-id` selbst fehlt bei gesetztem `source-room-id` (→ fremd; ein Vergleich gegen
nichts kann nicht „gleich" sein — als Vertrag zu testen, nicht dem Zufall der
`string.Equals`-Semantik zu überlassen).

Geteilt wird die **Entscheidung**, nicht das Einlesen. Die Tag-*Extraktion* bleibt seitenlokal,
weil die Quellen verschieden sind:

- **live:** `ChatMessage.RoomId` (typisiert) plus die `source-*`-Marker aus `UndocumentedTags` in
  `TwitchChatManager`, wo heute schon `UserId` und `Badges` aus dem TwitchLib-Typ gezogen werden.
  **Null-sicher, in dieser Reihenfolge:** erst prüfen, ob `UndocumentedTags` überhaupt ein
  Dictionary ist (bei Nachrichten ohne einen einzigen unbekannten Tag ist es `null`, Ist-Zustand),
  dann `TryGetValue` auf `source-room-id` und die Anwesenheit der anderen Marker, und erst daraus
  die Eingabe für die Regel. Fehlt das Dictionary, ist das Ergebnis „eigen" — dasselbe wie ein
  fehlender Tag im Justlog-Parser. Ein `TryGetValue` direkt auf der Property wäre auf dem Hot
  Path eine `NullReferenceException` bei gewöhnlichen Nachrichten, und dass es „meistens
  gutgeht", weil Twitch oft `client-nonce`/`flags` mitschickt, ist Zufall, kein Vertrag.
- **Replay:** `RoomId`/`SourceRoomId` aus `ChatLogMessage`, die der Parser bereits liefert. Für
  den unbestimmbaren Fall braucht der Parser zusätzlich die **Anwesenheit** der anderen
  `source-*`-Marker (ein Bool genügt, nicht ihre Werte), und `ChatLogMessage` sowie `Count`
  bekommen dieses eine Feld dazu — ohne Default (B2).

**Der Parser-Vertrag von TwitchLib ist Teil der Regel, und TwitchLib-freie Tests sehen ihn
nicht.** Deshalb zusätzlich zu den puren Tests **Fixtures aus echten IRC-Zeilen durch die
installierte TwitchLib-Version**: Roh-`PRIVMSG`-Zeilen, wie Twitch sie sendet, werden mit
TwitchLibs eigenem Parser zu `ChatMessage` gemacht und dann durch die Live-Extraktion
geschickt. Mindestens drei Zeilen: eine Shared-Chat-Nachricht mit vollem `source-*`-Satz, eine
gewöhnliche Nachricht **ohne einen einzigen unbekannten Tag** (das ist die `null`-Probe), eine
Nachricht mit `source-*`-Markern, aber ohne brauchbare `source-room-id`. Diese Tests sind
bewusst TwitchLib-gebunden — eine Ausnahme von der Regel „Policies TwitchLib-frei", weil hier
genau das Bibliotheksverhalten der Prüfgegenstand ist. Sie bleiben containerfrei und liegen in
`tests/EmotePurge.Worker.Tests`.

Verworfen:

- **Vergleich gegen `Channel.TwitchChannelId`.** Bräuchte einen neuen Datenfluss von der
  Datenbank bis in den Hot Path — der Live-Pfad ist heute durchgehend namensbasiert (Ist-Zustand).
  Und `room-id` **ist** bereits die Twitch-ID des Raums, in dem die Nachricht ankommt; der
  Vergleich gegen die DB-Spalte wäre nur eine umständlichere Formulierung derselben Frage, mit dem
  Zusatzrisiko, bei einem Rename mitten in der Session falsch zu liegen.
- **Eigenes `RawIrcMessage`-Parsing.** Nicht nötig (Ist-Zustand), und ein zweiter Tag-Parser
  neben dem von TwitchLib wäre eine Kopie mit eigener Fehlerklasse.
- **„Unbestimmbar" bei jedem fehlenden `source-room-id`.** Würde jede gewöhnliche Nachricht
  verdächtig machen; der Codex-Befund trägt nur in der engen Form.

### B2 — Eine Kategorie statt zweier Bools, und keine Defaultwerte

Ein Enum mit drei Werten — Mensch, Bot, Shared Chat — und `EmoteUsageCounts` wächst um ein
drittes Feld. `Increment` nimmt die Kategorie statt `bool isBot`; `Merge` und `DrainAndReset`
tragen den erweiterten Typ (`Merge` arbeitet über den Werttyp als Ganzes und braucht nur die eine
mitwachsende Addition; der `TArg`-Overload bleibt allokationsfrei, ein Enum ist Werttyp und boxt
nicht). `PendingEmoteCount` bleibt semantisch unverändert (Zahl **verschiedener** Emotes, nicht
Summe der Treffer — eine nur aus fremden Räumen gesehene Emote zählt als ein gepuffertes Emote,
analog zur Bot-Regel dort).

Begründung: Zwei Bools (`isBot`, `isShared`) ließen den vierten Zustand *bot && shared*
darstellbar — genau den, den D2 ausschließt. Mit dem Enum fällt der Vorrang an **genau einer
Stelle** (Klassifikation in `OnMessageReceived` bzw. `Count`: erst Raum — inklusive der
Abbildung „unbestimmbar → Shared Chat" —, dann Bot) und ist danach nicht mehr falsch
repräsentierbar. Wer später eine vierte Kategorie braucht, erweitert das Enum und der Compiler
findet jeden Switch.

**Keine Defaultwerte für die neuen Felder in den positionellen Records** — `EmoteUsageCounts`,
`UsageStatRowDto`, `ReplayUsageRow`, `ReplayDayLine`, `ReplayDiagnostics`, `ReplayGateMetrics`,
`ChatLogMessage` (Marker-Bool aus B1) und `HarnessRunIdentity` für den Stichtag aus D4. Mit
Default kompiliert der gesamte Bestand unverändert weiter, und keine Bestandssuite bemerkt die
dritte Kategorie; ohne Default bricht jede Aufrufstelle kompilierend auf — der Compiler als
Suchhilfe, dasselbe Argument wie fürs Enum. Das ist eine Entwurfsentscheidung, keine Stilfrage:
Ein still weiterkompilierender Bestand ist genau die Art, wie eine vergessene Aufrufstelle den
Harness-Vergleich unbemerkt schief stellt. (Ausgenommen ist die `.jsonl`-Deserialisierung, wo
ein `harness-1`-Kopf ohnehin über den Identitätsvergleich abgewiesen wird, s. B5.)

Die Reihenfolge in `OnMessageReceived` bleibt die aus #31: Watchdog-Buchführung **zuerst**, dann
Klassifikation **einmal pro Nachricht**, dann die Token-Schleife. Eine gespiegelte Nachricht
beweist genau wie eine Bot-Nachricht, dass der Socket lebt — die Warnung aus dem #31-Entwurf
(„Do not reorder") gilt unverändert. Die Raum-Prüfung kostet auf dem Hot Path eine Null-Prüfung
und wenige Dictionary-Lookups pro Nachricht und keine eigene Allokation — das Dictionary baut
TwitchLib ohnehin, und nur dann, wenn es unbekannte Tags gibt.

Umbaukosten, ehrlich benannt: alle `Increment`-Aufrufer (heute genau einer,
`TwitchChatManager.cs:526`), das Interface, der Counter selbst, `IUsageStatFlushService` als
Träger des Werttyps, die Counter-Tests in `EmotePurge.Worker.Tests`, die heute mit `bool`
arbeiten — und, wegen der Default-Entscheidung, jede Konstruktionsstelle der genannten Records in
Produktiv- und Testcode.

### B3 — Schema

`UsageStat.SharedChatUseCount` (`int`, `NOT NULL DEFAULT 0`), additive Migration nach dem Muster
von `20260901193259` — der Default liegt **auf der Spalte**, mit derselben Redeploy-Begründung:
das noch laufende alte Image schreibt sein `UNNEST`-Upsert ohne diese Spalte, bis es ersetzt ist,
und braucht den Default aus dem Katalog, nicht aus Code, den es noch nicht hat. (Der
Spalten-Default und die „keine Defaults"-Regel aus B2 widersprechen sich nicht: der eine gilt
dem alten Image in Postgres, die andere dem Compiler.) Dieselbe Schema-Kompatibilität ist auch
der Grund, warum ein Rollback im Messfenster **technisch** klaglos liefe und deshalb
**betrieblich** verboten ist (D4).

Conflict-Target `(EmoteId, Date)` bleibt **unverändert**. Der Covering-Index ist unter D5 eine
offene Frage (dort, Nebenwirkung 2); die #31-Begründung „für eine Spalte, die niemand liest"
trägt in der Übergangszeit nicht mehr, weil die Aggregat-Queries die Spalte lesen.

`UsageStatFlushService` bekommt ein drittes Array und eine dritte `ON CONFLICT`-Addition. Der
Upsert bleibt atomar; der bestehende Kommentar zur Atomarität gilt für alle drei Spalten. Kein
„nur eigene > 0"-Filter: ein Batch, der ein Emote ausschließlich aus fremden Räumen sah, erzeugt
`UseCount = 0, BotUseCount = 0, SharedChatUseCount = n` — und genau diese Zeile ist die
Negativprobe aus B6.

Der Entity-Kommentar, der heute die Bot-only-Zeile erklärt, wird auf die Shared-only-Zeile
erweitert, nicht dupliziert — samt dem Hinweis, dass die Lesequeries sie **bis Zug 2** als
benutzt lesen (D5). `docs/Architectur.md` wird an den drei Stellen aus dem Ist-Zustand
nachgezogen (`:80`, `:232`, `:270`).

### B4 — Lesepfad: Übergangssumme für die UI, Rohzeilen für den Harness

Für die Oberfläche gilt D5: Die produktiven Queries liefern und filtern über
`UseCount + SharedChatUseCount`, bis Zug 2 den Pfad umdreht. Kein DTO, kein Api-Vertrag ändert
sich für die Oberfläche; das Frontend merkt von Zug 1 nichts. **Das Zielraster zeigt Shared Chat
nicht** — und das Zielraster ist der Maßstab des Harness-Gates (D3), nicht die Brücke.

Für den Harness: `GetRowsAsync` und `UsageStatRowDto` reichen die dritte Spalte **roh** durch,
`ReplayUsageRow` trägt sie weiter — eine reine Ergänzung an dem Pfad, der ohnehin „bewusst
ungefiltert, für den Harness" ist. **Und `HarnessInputHash.Compute` hasht die dritte Spalte
mit**, in der Zeilen-Signatur neben `UseCount` und `BotUseCount`. Ohne das könnte ein Resume auf
einem veralteten Snapshot aufsetzen, obwohl sich fremde Nutzung in der Datenbank bewegt hat —
genau der Ausfall, gegen den der Hash existiert; `HarnessInputHashTests.cs:117` bliebe dabei
still grün, also braucht es dort einen Fall, der eine geänderte dritte Spalte den Hash bewegen
sieht.

`GetEarliestBotUsageDateAsync` bleibt **unverändert** — die Bedeutungsverschiebung aus D2 passiert
in den Daten, nicht im Query, und für das Gate wird die Methode ab `harness-2` nicht mehr
gebraucht (B5). Ein `GetEarliestSharedChatUsageDateAsync` als Gegenstück ist **Zug 2** (Caption)
und kommt in Zug 1 nicht; für den Harness ist es nach D4 ausdrücklich nicht die Quelle.

### B5 — Harness-Gegenstück

**Die Dreiteilung gilt durchgehend auf Nachrichtenebene, nicht nur bei Treffern.** Eine fremde
Nachricht zählt überall als fremd: `_botMessageCount` meint danach eigene Bots, `_humanChatters`
sammelt eigene menschliche Chatter, ein drittes Zähl-Dictionary nimmt die fremden Treffer (die
unbestimmbaren aus B1 eingeschlossen, plus deren eigener Nachrichtenzähler), und die Verzweigung
bei :137 wird dreiwertig über dasselbe Enum aus B2 mit der geteilten Regel aus B1. Das ist so zu
formulieren, weil `_humanChatters.Add(chatter)` **pro Nachricht** läuft
(`ReplayDayCounter.cs:134`), vor jedem Matching und unabhängig von Treffern — eine Regel „fremde
*Treffer* gehen nicht in die Chatter-Menge" wäre nicht ausführbar. Begründung für die
Durchgängigkeit: Sonst beschreiben `BotMessageCount` und `BotCounts` ab `harness-2` verschiedene
Populationen, und die Kontrollrechnung unten setzt voraus, dass Nachrichten- und Trefferzähler
dieselbe Population meinen.

Zwei Zahlen ändern dadurch still ihre Bedeutung, und beides steht im DECISIONS-Eintrag:
`ReplayDayLine.DistinctChatters` (aus `_humanChatters.Count`) zählt ab `harness-2` **eigene
menschliche** Chatter; und der fensterweite `distinctChatters`-HashSet in `HarnessRunner` (:261,
:296 → „Distinkte Chatter im Fenster", :562), der heute jeden Chatter mit User-ID zählt, folgt
**derselben Regel** — sonst stehen zwei gleichnamige Zahlen mit zwei Populationen im selben
Bericht. Das bedeutet, dass die Klassifikation im Runner-Callback vor dem `Add` bekannt sein muss,
nicht erst im Counter.

`_sharedChatMessageCount` bleibt **daneben** bestehen. Es zählt Nachrichten, nicht Emote-Treffer,
und ist damit eine unabhängige Kontrollzahl: Ein Tag mit vielen gespiegelten Nachrichten, aber
null fremden Emote-Treffern, ist plausibel (fremder Chat ohne namensgleiche Emotes); ein Tag mit
fremden Treffern, aber null gespiegelten Nachrichten, ist ein Fehler im Zähler.

**`HumanOnly` gegen den Shared-Chat-Stichtag allein.** Ab `harness-2` ist ein Tag `HumanOnly`,
wenn `Day >= SharedChatCutover` — `BotSplitCutover` geht in die Bedingung nicht mehr ein. Das ist
zulässig, weil der Bot-Split am 2026-09-01 deployt wurde, also vor #73: Jeder Tag ab dem
Shared-Chat-Stichtag liegt zwangsläufig auch nach dem Bot-Split-Deploy, und seine Zeilen tragen
Bots getrennt — unabhängig davon, wann der Kanal seinen ersten Bot *gesehen* hat. Das ist
zugleich *korrekter* als der heutige Zustand: Der ausgerollte **Vertrag** entscheidet, nicht der
Zeitpunkt der ersten Sichtung in den Daten — genau die E4-Unschärfe, die der Bot-Stichtag trägt.
Der Null-Fall aus D2 kann damit keinen Kanal mehr aus dem Gate sperren. **`BotSplitCutover`
selbst bleibt unverändert** — in Identität, Kopf, Berichtszeile und `GetEarliestBotUsageDateAsync`
(Freeze, B8); es wird für das Gate nur nicht mehr gebraucht. Der Docstring an `ReplayWindow`, der
`HumanOnly` heute über den Bot-Stichtag erklärt, wird umgeschrieben, nicht ergänzt.

**Shared-Chat-Symmetrie als Eignungsbedingung.** Über die bewerteten Tage werden die
Shared-Chat-Summen beider Seiten (live `SharedChatUseCount`, Replay dritte Komponente) als
Fenstersumme verglichen. Weichen sie relativ zueinander stärker ab als die bestehende
Abweichungstoleranz des Gates (dieselbe 10-%-Konstante, **keine neue Schwelle**), ist der Lauf
gate-untauglich mit einem neuen Grund in `ReplayGateIneligibleReasons`. Tage mit 0 auf beiden
Seiten sind trivial symmetrisch; ein Kanal ohne jede Session erfüllt die Bedingung leer. Die
Bedingung wird **vor dem bindenden Lauf in #69 nachregistriert**, wie die übrigen Kriterien
dort stehen — sonst wäre sie eine nach dem Lauf erfundene Regel (DoD).

`ReplayDayLine` und die `.jsonl`-Tageszeile wachsen um das dritte Dictionary (benannt wie die
Spalte) und den Zähler der unbestimmbaren Nachrichten. `ReplayFidelityCalculator` nach D3:
Tagessummen und Plausibilität über drei Komponenten, Gate human-only wie heute, Shared-Chat-Summe
beider Seiten als eigenes Berichtsfeld, tageweise und als Fenstersumme, Symmetrie als
Eignungsbedingung, `Rated` und die Identität nach D4, Diagnosemodus ohne Gate-Urteil. Die
präregistrierten Schwellen bleiben unverändert.

`AlgorithmVersion` → `harness-2`. Das ist Pflicht, nicht Vorsicht: Die Tageszeile ändert ihre
Form und die Zählregel ändert sich — beides nennt der Docstring als Bump-Grund. Eine `harness-1`-
Datei darf nicht wiederaufgenommen werden; der Identitätsvergleich sorgt dafür, und der
Dateiname trägt den neuen Hash — den Stichtag aus D4 und die dritte Spalte im Input-Hash
inklusive.

### B6 — Verifikation: die Negativprobe ist der Beleg

Die Suiten sind Pflicht, aber der eigentliche Beleg ist die **Negativprobe** — und die braucht
**keinen** Zugang zum VPS. Der Worker läuft auf der Devbox gegen das lokale Postgres und joint
trotzdem den **echten** Twitch-Kanal; `brudivoeller_tv` (84 %) und `papaplatte` (0 %) sind von
hier aus beobachtbar, in der lokalen Datenbank, ohne SSH. Dieselbe Probe wiederholt sich nach dem
Prod-Deploy in Prod als Gegenprobe, aber sie entscheidet sich lokal, vor dem Merge.

1. In `brudivoeller_tv` (84 %) und `ronnyberger` (66 %) **müssen** Zeilen mit
   `SharedChatUseCount > 0` entstehen, während eine Session läuft.
2. In `knirpz` und `papaplatte` (0 %) darf **keine einzige** entstehen.
3. Dieselbe Zeile darf nicht in zwei Kategorien aus derselben Nachricht wachsen: `UseCount` einer
   Zeile, die fremde Treffer bekommt, darf sich nur durch eigene Nachrichten bewegen.
4. **Die Oberfläche zeigt keinen Sprung** (D5): Die Usage-Seite eines betroffenen Kanals vor und
   nach dem lokalen Deploy — Summen, Bänder, Sortierung — unterscheidet sich nur um das, was in
   der Zwischenzeit gechattet wurde. Eine Zeile aus Punkt 1 mit `UseCount = 0` erscheint weiter
   als benutzt.
5. Der Zähler unbestimmbarer Nachrichten (B1) bleibt in beiden Kanalgruppen bei null oder
   erklärt sich — ein Anschlagen wäre ein Befund über Twitchs Tagsatz, nicht über uns, und
   gehört dann ins Issue.

Eine **invertierte** Regel (eigen ↔ fremd vertauscht) besteht den Positivtest problemlos — sie
erzeugt ebenfalls Zeilen mit `SharedChatUseCount > 0` — und fällt erst an Punkt 2 durch. Deshalb
ist Punkt 2 der eigentliche Nachweis. Voraussetzung ist, dass die 0-%-Kanäle im Prüfzeitraum
überhaupt live sind; ein Kanal ohne Session erzeugt in keiner Kategorie eine Zeile und beweist
nichts.

Dazu die **Symmetrieprobe des Harness**: ein Probelauf im Diagnosemodus (D4) über dieselben
Kanäle nach dem Deploy muss an Nach-Deploy-Tagen eine Shared-Chat-Summe zeigen, die sich mit der
Live-Seite deckt — beide Seiten leiten unabhängig her, und das ist die erste Gelegenheit, das zu
sehen. Vor-Deploy-Tage und der Deploy-Tag zeigen die beiden anderen Signaturen aus D3 und sind
dann als solche zu erkennen, nicht als Abweichung.

Zusätzlich wie bei #31: Migration auf der Dev-DB fahren und prüfen, dass Api und Worker aus dem
**alten** Stand gegen das migrierte Schema weiterlaufen — das ist die Annahme, auf der die
Prod-Reihenfolge beruht.

### B7 — Testlandschaft

Nur benannt; die Fälle schreibt der Plan aus. Alle Worker-Tests liegen flach in
`tests/EmotePurge.Worker.Tests/`.

| Datei / Ort | Fälle |
|---|---|
| geteilte Regel (neu, pur) | kein Marker → eigen · gleich → eigen · ungleich → fremd · leer wie fehlend · `room-id` fehlt bei gesetztem `source-room-id` → fremd · andere `source-*` ohne `source-room-id` → unbestimmbar |
| Live-Extraktion, pur | `UndocumentedTags == null` → eigen, keine Ausnahme · Dictionary vorhanden, Schlüssel fehlt · Schlüssel vorhanden · nur Nebenmarker vorhanden |
| Live-Extraktion, **TwitchLib-gebunden** (Fixtures aus echten IRC-Zeilen, B1) | Shared-Chat-Nachricht mit vollem `source-*`-Satz · gewöhnliche Nachricht ohne einen einzigen unbekannten Tag · `source-*` ohne brauchbare `source-room-id` |
| `EmoteUsageCounterTests` | drei Kategorien getrennt · Vorrang fremd vor Bot und unbestimmbar → Shared Chat an der Klassifikationsstelle · `Merge` erhält alle drei · Drain-Swap gibt alle drei zurück und leert · `PendingEmoteCount` bei shared-only |
| `ReplayDayCounterTests` | `SharedChatMessage_IsCountedAndMarked` **kehrt sich um**: die Nachricht zählt in der dritten Komponente, nicht in `HumanCounts`, `SharedChatMessageCount` bleibt 1 · fremder Bot zählt weder in `BotMessageCount` noch in `BotCounts` · fremder Chatter erscheint nicht in `DistinctChatters`/Zellen · unbestimmbare Nachricht: Treffer in der dritten Komponente, eigener Zähler 1 |
| `ReplayFidelityCalculatorTests` | Tagessumme über drei Komponenten · Gate bleibt human-only · Shared-Chat-Summe beider Seiten im Bericht · die drei Signaturen aus D3 verändern `Ratio` nicht · `HumanOnly` allein aus dem Shared-Chat-Stichtag, `BotSplitCutover = null` sperrt nichts mehr · Symmetrie innerhalb/außerhalb der Toleranz → eligible/ineligible mit Grund · leerer Fall (beide 0) ist symmetrisch |
| `HarnessCommandLineTests` | Diagnosemodus-Argument wird erkannt · jede andere Form weiter Exit 2 |
| `HarnessRunnerTests` | End-to-End-Fall: Tageszeile mit neuen Feldern · `harness-1`-Datei wird nicht wiederaufgenommen · anderer Stichtag ⇒ andere Identität · **fehlender/ungültiger Stichtag ⇒ Exit 3 mit Klartext, kein Bericht** · Diagnosemodus ohne Stichtag läuft und weist kein Gate-Urteil aus · fensterweite Chatter-Zahl zählt nur eigene Menschen · Berichtszeile für den Stichtag |
| `HarnessReportFileTests` | Round-Trip der neuen Felder in Kopf und Tageszeile |
| `HarnessInputHashTests` | geänderte dritte Spalte bewegt den Hash |
| `tests/EmotePurge.Infrastructure.Tests/Integration` | Upsert: dritte Spalte akkumuliert unabhängig · Konflikt addiert in **alle drei** · Batch mit ausschließlich fremden Treffern |
| `tests/EmotePurge.Infrastructure.Tests/Integration` | `UsageStatQueryService`, **Übergangstest (D5), in Zug 2 bewusst zu brechen und umzudrehen**: `UseCount = 0, SharedChatUseCount = n` gilt in jedem Lesepfad als benutzt und die Summen enthalten n · Bot-only-Zeile gilt weiter als unbenutzt · `GetRowsAsync` liefert die dritte Spalte roh |
| `JustlogRawLineParser`-Tests (Infrastructure, Unit) | Marker-Bool gesetzt/nicht gesetzt · `source-room-id` leer bei gesetzten Markern |

`tests/EmotePurge.Api.Tests` ist **nicht** betroffen: keine neue Route, kein neuer
`IEndpointFilter`, keine geänderte Filter-Reihenfolge (Regel 11). Das Frontend ist in Zug 1
**nicht** betroffen; E2E entfällt damit für Zug 1.

### B8 — Was eingefroren bleibt

Unverändert in Zug 1 und bis zum Ende des bindenden Laufs, mit Absicht: `EmoteNameMatching`,
`BotChatterDetector`, die statische Bot-Liste, `Twitch:AdditionalBotAccountIds`,
`BotSplitCutover` samt `GetEarliestBotUsageDateAsync`, die präregistrierten Schwellen des
Harness, die Tagesgrenzen — **und die TwitchLib-Version (`TwitchLib.Client` 4.0.1)**.

**Warnung, ausdrücklich, für zwei stille Fallen derselben Klasse:**

- Eine neue Bot-Regel, die *nicht* auf der ID-Liste beruht (etwa eine Heuristik über Badges oder
  Nachrichtenmuster), würde die Zählung ändern, **ohne** den `InputHash` oder den Berichtskopf zu
  verändern — die ID-Liste steht in der Identität, eine Heuristik nicht.
- Ein **TwitchLib-Update** während des Messfensters könnte `source-room-id` (oder einen der
  anderen Marker) von „undocumented" zu typisiert machen. Dann stünde der Wert nicht mehr in
  `UndocumentedTags`, die Live-Extraktion sähe ihn nicht, jede Session-Nachricht würde „eigen" —
  und weder Berichtskopf noch Input-Hash bewegten sich. Die TwitchLib-gebundenen Fixtures aus
  B1 würden rot, aber nur, wenn jemand sie laufen lässt, bevor das Image gebaut ist. Deshalb:
  Dependabot-`ignore` für `TwitchLib.*` für die Dauer des Fensters (die `ignore`-Liste existiert,
  `.github/dependabot.yml:39-41`) oder die Regel, solche PRs nicht zu mergen — der Plan wählt;
  die Konfigvariante hat den Vorteil, dass sie nicht am Gedächtnis hängt.

In beiden Fällen würde der Harness die Änderung nicht bemerken und sie als Genauigkeitsverlust
verbuchen. Wer eines davon anfassen muss, bumpt `AlgorithmVersion`, setzt den Stichtag neu und
die Uhr zurück.

## Zug 2 — nur benannt, bewusst offen

Nichts davon wird in Zug 1 entschieden oder gebaut. Es steht hier, damit der Plan für Zug 1 die
Türen nicht zuschlägt — und weil D5 Zug 2 zur Pflicht macht, nicht zur Option.

- **Der Lesepfad wird umgedreht** (D5): Summen und Filter zurück auf `UseCount` allein, der
  Übergangstest aus B7 wird gebrochen und umgedreht, der Covering-Index nach der Antwort auf die
  offene Frage nachgezogen. Das ist der erste Task von Zug 2, und er geht nicht ohne den zweiten.
- **Toggle gegen Hinweissatz.** Das Issue skizziert einen Toggle; E3 und die Projekt-Leitlinie
  zur Frontend-Zurückhaltung sprechen für einen reinen Hinweis nach dem Muster der Bot-Caption.
  **Offene Entscheidung** — mit einem Unterschied zu #31, der sie schwerer macht: Bei 84 %
  Fremdanteil ist die fremde Zahl für einen Manager vielleicht tatsächlich interessant
  („dieses Emote nutzt niemand bei uns, aber alle drüben").
- **`SharedChatSeparatedSince`** als datenabgeleitetes Datum erbt die E4-Unschärfe (erste
  *Sichtung*, nicht Deploy) und hat denselben Sonderfall, der in D4 gegen die Ableitung
  entschieden hat: In 0-%-Kanälen bleibt es **dauerhaft** `null`, obwohl die Trennung dort
  genauso gilt. Die Caption wäre dort korrekt abwesend — es gibt nichts zu erklären —, aber die
  Ableitung „`null` = keine Trennung" wäre falsch, und ein späterer Konsument darf sie nicht so
  lesen. Ob Zug 2 die Ableitung trotzdem nimmt oder das Deploy-Datum aus D4 wiederverwendet, ist
  dann zu entscheiden. Mit D5 verschiebt sich zudem, *welches* Datum die Caption meint: nicht das
  Deploy von Zug 1 (da änderte sich nichts Sichtbares), sondern das von Zug 2.
- **Eigene Drilldown-Serie** über `SharedChatUseCount` — dann wäre der Covering-Index dauerhaft
  neu zu bewerten.
- **Darstellung** in der Art `12× (+3 Shared Chat)` neben der Karte.
- **Bänder und Sortierung** rechnen nach Zug 2 nur mit eigener Nutzung; das ist keine offene
  Frage, sondern die Konsequenz aus dem Zielvertrag.

## Ausdrücklich nicht in dieser Runde

- kein Frontend, keine i18n-Schlüssel, keine Api-Vertragsänderung
- keine vierte Spalte für fremde Bots oder für Unbestimmbares
- kein datenabgeleiteter Shared-Chat-Stichtag (D4)
- keine Kopplung des Stichtags an persistierte Writer-Versionen (D4, Restrisiko)
- kein viertes präregistriertes Gate mit eigener Schwelle (D3)
- keine Änderung an Matching, Bot-Erkennung, Schwellen oder TwitchLib-Version (B8)
- keine Rückwirkung auf Bestandsdaten (technisch unmöglich)
- kein Vergleich gegen `Channel.TwitchChannelId`, kein eigenes IRC-Parsing

## Definition of Done und Randbedingungen

- **Regel 3:** Der DECISIONS-Eintrag gehört in **denselben** Commit wie die Vertragsänderung —
  und er enthält **alles davon**: den zweiten Historienbruch (Zahlen vor dem Deploy enthalten
  fremde Nutzung), die `BotUseCount`-Nebenwirkung aus D2 für die Bot-Caption, die Übergangssumme
  aus D5 samt ihren zwei Nebenwirkungen und dem Entfernungsauslöser, die Bedeutungsänderung der
  beiden Chatter-Zahlen aus B5, `HumanOnly` gegen den Shared-Chat-Stichtag allein, den
  fail-closed-Stichtag und das Rollback-Verbot aus D4, den unbestimmbaren Fall aus B1 und die
  TwitchLib-Freeze-Falle aus B8. Die Betrifft-Zeile führt `docs/Architectur.md` mit, wie der
  #31-Eintrag.
- **`docs/Architectur.md`** an `:80`, `:232`, `:270` nachziehen, im selben Commit wie das Schema.
- **Regel 16:** live gegen echte Postgres/Redis/Twitch, nach B6 — lokal auf der Devbox, ohne
  VPS-Zugang; „läuft durch" ist kein Nachweis.
- **Suiten:** `dotnet test EmotePurge.slnx` (Docker läuft, Testcontainers) und
  `npm --prefix web test -- --watch=false`. E2E nur bei UI-Änderungen, also in Zug 1 **nicht** —
  und wenn in Zug 2, nur ohne laufende Api auf `:5151`.
- **Vor dem PR:** `node scripts/coverage-local.mjs` — es misst nur Committetes; erst committen,
  dann laufen lassen. Die Migration und die neuen Harness-Felder sind neue Zeilen in gut
  gedeckten Dateien; das Gate (80 % auf neuem Code, required check) wird beißen, wenn die
  Integrationstests aus B7 fehlen.
- **Vor dem Merge:** `/codex:review --model gpt-5.6-sol --scope branch --base origin/main`
  (Regel 22; `--scope` ausdrücklich, sonst reviewt Codex bei sauberem Tree nichts und meldet
  Entwarnung).
- **Prod-Reihenfolge, vollständig:**
  1. Migration von Hand **vor** dem Deploy (Tunnel + `--connection`, Standardweg aus
     `CLAUDE.md`).
  2. Images deployen; das ist Tag D.
  3. **Stichtag D+1 setzen** — in der Prod-Konfiguration des Harness-Containers (`Harness:*`),
     als UTC-Datum.
  4. B6 als Gegenprobe in Prod.
  5. **Symmetriebedingung in #69 nachregistrieren** (B5), vor dem bindenden Lauf.
  6. **Rollback-Verbot** für das Worker-Image ab D bis zum Ende des bindenden Laufs; falls
     doch: Lauf ungültig, Stichtag neu auf den Tag nach dem erneuten Deploy, Uhr neu.
  7. Frühester bindender Lauf **D+31**, ohne Diagnosemodus.
  8. **Zug 2 als Auslöser für die Entfernung der Übergangssumme** (D5) einplanen — mit Termin
     im Issue, nicht nur als Absicht. Bis dahin bleibt der Übergangstest aus B7 grün und ist der
     Beleg, dass die Brücke noch steht.

  D, D+1, D+31, das Rollback-Verbot und der Zug-2-Termin gehören als Kommentar ins Issue #69,
  nicht nur ins Gedächtnis.
- **Regel 1:** Vor jedem Commit fragen.
- **Keine** Statuszeile im Backlog: #73 kam als Issue herein, nicht über
  `docs/Feature-Ideen-2026-08-01.md`. Der Umsetzungsstand steht am Issue.

## Offene Fragen

Was der Plan klären muss, bevor der erste Task startet. Keine davon blockiert die Planung; sie
sind Entscheidungen, die der Plan an der jeweiligen Task-Stelle trifft und benennt.

1. **Wohin gehören Enum und Regel?** `EmoteUsageCounts` lebt in `Core` (`IUsageStatFlushService`
   führt es in der Signatur), die geteilte Regel aus B1 wird von Worker-Live-Pfad und Harness
   gerufen — beide im Worker. Ein Enum ist reine BCL und darf nach `Core`; die Regel auch. Gegen
   `Core` spricht, dass die Regel eine IRC-Tag-Semantik ist, die außerhalb des Workers niemand
   braucht; dafür spricht, dass `ChatLogMessage` bereits in `Core/ChatLogArchive` liegt, jetzt
   das Marker-Bool trägt und Infrastructure ihn füllt. Die Schichtentreue-Tabelle in `CLAUDE.md`
   erlaubt beides; `CoreAssemblyReferenceTests` muss grün bleiben. Zu entscheiden, nicht zu
   erraten.
2. **Covering-Index in der Übergangszeit** (D5, Nebenwirkung 2): `SharedChatUseCount` ins
   `INCLUDE` — befristet, dauerhaft — oder Heap-Zugriff hinnehmen. Mit `EXPLAIN` auf der Dev-DB
   gegen die drei Aggregat-Queries zu entscheiden; die Antwort bestimmt, ob die Migration aus B3
   den Index anfasst.
3. **`ReplayDiagnostics.SharedChatMessages`** — behält es seinen Namen (Nachrichten) und der neue
   Emote-Treffer-Zähler sowie der Zähler unbestimmbarer Nachrichten treten daneben, oder wird
   umbenannt? Empfehlung: behalten und ergänzen, weil der Nachrichtenzähler als Kontrollzahl
   gebraucht wird (B5). Der Plan legt die Namen aller Felder in Tageszeile und Bericht fest,
   camelCase, und prüft sie in `HarnessReportFileTests` per Round-Trip.
4. **Tokenisierung fremder und unbestimmbarer Nachrichten.** `ClassifyTokens` (:158) läuft heute
   über jede Nachricht und speist `UnmatchedByReason`. Nach der Nachrichtenebenen-Regel aus B5
   liegt nahe, beide auch hier auszuschließen (sie werden gegen die eigene Day-Map klassifiziert
   und würden `UnknownName` aufblähen); der Plan entscheidet es und der Test benennt es.
5. **Namen und Formen:** der Konfigschlüssel für den Stichtag in `HarnessOptions` (ISO-Datum,
   UTC); das Diagnosemodus-Argument in `HarnessCommandLine`; der Wortlaut des neuen
   Symmetrie-Grundes in `ReplayGateIneligibleReasons`, in der Form der bestehenden
   Kebab-Case-Konstanten; das Vehikel für den Live-Zähler unbestimmbarer Nachrichten (B1).
6. **Wo der Deploy-Tag D festgehalten wird**, damit Stichtag und Termin nicht auseinanderlaufen:
   Issue-Kommentar an #69 ist gesetzt (DoD); ob zusätzlich `.env.example`/`docker-compose.prod.yml`
   den Schlüssel als Platzhalter führen, entscheidet der Plan mit Blick auf den bestehenden
   Harness-Compose-Eintrag.
