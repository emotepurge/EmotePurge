# Plan #516: Join-Fehler erkennen und melden, danach die Kanaldecke anheben

Erstellt am 2026-11-25 im Worktree `EmotePurge-wt-516` (Branch `feat/516-join-failure-detection`, Basis `origin/main` @
`e1d6210f`, PR #515 gemergt). Alle Codestellen dieses Plans sind an diesem Stand gelesen.
**Status: Revision 2, nach der adversarialen Prüfung (Sol, „needs-attention", acht Befunde, alle angenommen und am Code
nachgeprüft — Abschnitt 0). Nichts umgesetzt.**

Quellen: Issue #516 (Teil A, Teil B, Akzeptanzkriterien), DECISIONS 2026-11-25 („Twitch channel capacity …"), 2026-09-23
(Kanaldecke 80), 2026-09-16 (Forwarded-Headers-Vertrauensliste, `PublicHealth` je IP), 2026-08-05 (Z1-Rest/S3-35:
payloadfreier `/api/health`, Kuma als Dead-Man's-Switch), 2026-08-02 (Schwellen-Leiter, „das Roster-Badge bekommt keine
nearing-capacity-Stufe"), 2026-08-01 (Roster-Key `worker:roster`), #245 (D2, Frist für unauflösbare Logins), `CLAUDE.md`
(Regeln 3, 4, 5, 7, 11, 12, 16, 19, Tests, Sprache), `web/.claude/CLAUDE.md`, die Messung der JOIN-Sonde vom 2026-09-08
(`~/projects/emotepurge-joinprobe/messungen-2026-09-08/lauf-B-named120.log.summary.txt`) und TwitchLib.Client 4.0.1 sowie
TwitchLib.Communication 2.0.1, dekompiliert aus `~/.nuget/packages/`.

**Was dieser Plan trägt:** Befund, Entscheidungen mit verworfenen Alternativen, die Verträge (Typen, Felder, Route,
Statuscodes, Redis-Payload, Konfiguration), den Schnitt in Tasks mit Grenzfällen, Tests, Live-Prüfung und Fertig-Prüfung,
die Gates, Konfliktrisiken und die offenen Betreiberfragen. **Kein Code**; Namen und Signaturen nur als Form.
Jeder Implementer liest seinen Task, Abschnitt 2 und 3 **und** die genannten Bestandsdateien, bevor er schreibt.

**Lesehinweis:** **[E]** Planer-Entscheidung (widersprechbar) · **[B]** Betreiberentscheidung (vorläufig, überstimmbar) ·
**[P]** Präzisierung ohne Entscheidungsgehalt · **[F*n*]** offene Betreiberfrage (Abschnitt 8).

---

## 0. Revision 2: Befunde der Sol-Prüfung und ihre Auflösung

Jeder Befund ist am Code nachgeprüft; keiner war sachlich falsch. Zwei sind einfacher lösbar als empfohlen (Spalte
„Vereinfachung").

| # | Befund (Kurzform) | Am Code bestätigt | Auflösung | Vereinfachung |
|---|---|---|---|---|
| 1 | [high] Kurze Reconnects setzen die Frist immer neu, beide Monitore grün | E4 Rev. 1 rechnete ab `max(Stempel, Sitzungsbeginn)`; `WorkerHealthSnapshot` kennt keine Kanäle | E1/E4: Obergrenze `MaxUnconfirmedSeconds` (1800 s) ab dem eigenen Stempel, unabhängig von Sitzungen; Stempel wird nur für durch Trennung verlorene Bestätigungen beim nächsten Handshake neu gesetzt | — |
| 2 | Roster und Sitzung nicht atomar, Instanzwechsel unbemerkt | `GetRoster()` liest `_desiredChannels` ohne Riegel (`TwitchChatManager.cs:294`), Sitzung getrennt; `WorkerInstanceId` steht in beiden Payloads (`IWorkerHealthReader.cs:54`, `IWorkerRosterReader.cs:50`), wird nirgends verglichen | E3: Erfassung unter `_clientStateGate`; E4: Instanzvergleich mit 180 s Toleranz, danach `Inconsistent` → 503 | Kein Sitzungsabgleich *zwischen* den Payloads nötig: bewertet wird zur Erfassungszeit des Rosters, das in sich konsistent ist; sein Alter begrenzt die TTL |
| 3 | Gelöschter Kanal hält den Alarm dauerhaft | `ChannelIdentityService.cs:253-260` (gespeicherte ID unauflösbar → nichts geschrieben, bleibt aktiv), `:340-363` + `:1402-1406` (Login ohne ID unbekannt → erst nach 7 Tagen Alter **und** 7 Tagen ohne erfolgreichen 7TV-Sync deaktiviert). Rev. 1 behauptete „nach rund einer Stunde"; auch T8 Rev. 1 nahm an, der erste Abgleich deaktiviere die Sonde | E11 [B]: `channelSuspended`/`banned` zählen nicht für 503, nur Karte + Warning; Abhilfe ist der auditierte Leave in `/admin/channels` | — |
| 4 | Kanalcheck teilt das Budget von `/api/health` | T5 Rev. 1 verlangte ausdrücklich den geteilten Topf; Partition je IP (`RateLimitRejection.ResolveUserKey`, Vertrauensliste `Program.cs:344-351`) begrenzt die Gesamtlast nicht | E5: eigene Policy `PublicChannelHealth`, Ergebnis 5 s geteilt mit Single-Flight über das vorhandene `TtlCache<T>` | Kein neuer Cache-Baustein: `TtlCache<T>` + Muster `LogStatusReader` existieren |
| 5 | Präfixfilter `@msg-id=` verwirft gültige NOTICEs; Test umgeht den Empfangspfad | `OnReadLineTestAsync` geht an `OnSendReceiveData` vorbei (Rev. 1, Befund 4) | E2: Befehl erkennen, Tag-Block reihenfolgeunabhängig lesen; T3: Fake-Transport (`TwitchLib.Communication.IClient`) treibt den echten Empfangspfad bis zu Grund und EventId; fehlende Bestätigung ohne NOTICE eigener Fall | — |
| 6 | Admin-Karte bleibt grün, wenn der Publisher hängt; `starting` verdeckt `failing` | `admin-roster-card.ts:237` (Alter nur aus `ageSeconds` beim Abruf), `:342` (Nachladen nur per SSE), `:324-326` (`bootRecoveryCompleted` vor allem) | E6: sichtbares Alter läuft weiter, einmaliges Nachladen bei überfälligem Ereignis, Status aus `joinHealth.status` | — |
| 7 | Grace unterschätzt TwitchLibs JOIN-Warteschlange | Dekompiliert: nächster JOIN erst nach `366` oder, bei stiller Ablehnung, nach 5-s-Timeout plus Timer-Ticks (`QueueingJoinCheckAsync`, `JoinChannelTimeout`); `msg_channel_suspended` schiebt sofort weiter; die 600-ms-Drossel des Managers begrenzt nur das Einreihen | E4: Formel mit Term `7 s × K`; T8: Messung mit Produktions-Grace und realistischer Kanalzahl, bevor das Runbook eine Dauer nennt | — |
| 8 | Variable allein stellt die Erfassung beim Rückweg nicht wieder her | `ChannelService.cs:826-827` (Decke nur bei `activatesChannel`) | E9 + T10: Rückweg zweiteilig (Aufnahme stoppen / Erfassung wiederherstellen per Leave), danach Soll gegen bestätigte Joins prüfen | — |

Dazu aus „Next steps": Live-Prüfung (Regel 16) **je Backend-Task vor dessen Commit** und ein Live-Gate mit Messung **vor**
dem Frontend-Task (E12); jeder DECISIONS-Absatz im Commit seiner Vertragsänderung (Abschnitt 4, Kopf); die Statusentscheidung
vollständig im getesteten Infrastructure-Dienst, die Api bildet nur ab (E5).

---

## 1. Befund am Code

1. **Wunsch statt Erfolg.** `TwitchChatManager._desiredChannels` (`ConcurrentDictionary<string, bool>`, Wert = „Twitch hat
   bestätigt") ist die einzige Join-Buchführung (`TwitchChatManager.cs:83`). Gesetzt wird `true` nur in
   `OnJoinedChannel` unter `_clientStateGate` mit Identitätsprüfung (`:991-1021`); `MarkAllChannelsUnconfirmed` setzt bei
   jedem Clientwechsel und `OnDisconnected` alles auf `false` (`:1168-1173`), beide Male unter `_clientStateGate`. Es gibt
   **keinen Zeitstempel**, seit wann ein Kanal unbestätigt ist, und **keinen Grund**.
2. **`OnFailureToReceiveJoinConfirmation` loggt nur** (`:920-931`) und prüft keine Identität. TwitchLib feuert es:
   - nach dem eigenen Timeout (`JoinChannelTimeout`, 1-s-`System.Timers`-Tick, Schwelle > 5 s), `Details = null`;
   - sofort bei `NOTICE` mit `msg-id=msg_channel_suspended` (`HandleChannelSuspended`), `Details` = Twitchs Text. Dieser
     Zweig **entfernt den Kanal aus `_awaitingJoins` und schiebt die Warteschlange sofort weiter** — es folgt kein Timeout.
3. **`msg_concurrent_channel_limit_reached` hat in TwitchLib keinen eigenen Zweig** (`HandleNotice`, Fallback
   `OnUnaccountedFor` bzw. `LogUnaccountedFor`, redigiert über `RedactingTwitchClientLoggerFactory`, #246). Die Sonde vom
   2026-09-08 hat die Zeile aufgezeichnet: `@msg-id=msg_concurrent_channel_limit_reached :tmi.twitch.tv NOTICE #<kanal> :You
   are connected to too many chat channels. …`. Für denselben Kanal folgt TwitchLibs Timeout. `msg_banned` geht an
   `OnBanned`, ebenfalls ohne Entfernen aus `_awaitingJoins` (Timeout folgt); für die anonyme, nur lesende Verbindung ist er
   praktisch nicht zu erwarten, wird aber mitgeführt.
4. **`OnSendReceiveData` sieht jede empfangene Zeile vor dem Parsen** (`TwitchClient._client_OnMessage`: erst
   `OnSendReceiveData`, dann `IrcParser.ParseMessage`, dann die Behandlung). Unser Handler (`:1031-1048`) ist über `Bind`
   verdrahtet, prüft keine Identität, ist heiß und allokationsfrei. `OnReadLineTestAsync` umgeht `OnSendReceiveData`.
   `TwitchClient` nimmt seinen Transport als `TwitchLib.Communication.Interfaces.IClient` (Konstruktor, `CreateClient`
   `:1182-1201` baut einen `WebSocketClient`); `IClient` ist mit vertretbarem Aufwand fälschbar (Ereignisse `OnMessage`,
   `OnConnected` …, `OpenAsync`, `SendAsync`).
5. **Sitzungsbeginn:** `_sessionStartedUtcTicks`, gesetzt in `OnConnected` (`:865`), auf 0 beim Start eines Wiederaufbaus
   (`:350`), beides unter `_clientStateGate`. Nach `OnDisconnected` bleibt der Wert bis zum Wiederaufbau stehen — „Sitzung
   läuft" ist `IsConnected && ticks != 0`.
6. **TwitchLibs JOIN-Warteschlange (4.0.1):** `JoinChannelAsync` reiht ein und kehrt sofort zurück, solange ein JOIN läuft
   (`_currentlyJoiningChannels`). Der nächste JOIN geht erst raus bei `366` des vorigen (gemessen 0,13–0,23 s, Kommentar
   `:34-39`), bei `msg_channel_suspended` sofort, und **ohne Antwort erst, wenn der Timer-Tick den > 5 s alten Eintrag
   verworfen hat und ein weiterer Tick die leere Warteliste sieht — ≈ 6–7 s je stille Ablehnung**. Die 600-ms-Drossel des
   Managers (`MinIntervalBetweenJoins`, `:44`) begrenzt nur das Einreihen; staut TwitchLib, wartet jeder gesunde Kanal
   dahinter mit. `JoinedChannels` enthält einen Kanal erst ab dem Senden — ein eingereihter, noch nicht gesendeter Kanal
   kann vom Konvergenznetz ein zweites Mal eingereiht werden (Messpunkt T8).
7. **Roster:** `WorkerRosterPublisher` (60 s, `do … while`, sofort beim Start) baut `WorkerRosterSnapshot` mit
   `GeneratedAtUtc`, `WorkerInstanceId`, `ProcessStartedUtc`, `BootRecoveryCompleted`, `Truncated` (Kappung 500), `Channels`.
   Key `worker:roster`, TTL 180 s, beim Stopp nicht gelöscht. `GetRoster()` wird außerdem von
   `SevenTvPeriodicResyncWorker:130`, `TwitchConnectionWatchdog:206` und `WishCaptureWorker:253` gelesen.
8. **Health:** `WorkerHealthSnapshot` trägt `WorkerInstanceId` und `ProcessStartedUtc` (beide nullable, gleiche Quelle
   `WorkerIdentity.InstanceId` wie das Roster), Takt 20 s. `WorkerHealthStatus` (`src/EmotePurge.Api/Health/`, `internal
   static`, rein) ist die einzige Ableitung von „connected"; Nutzer: `WorkerHealthEndpoints.cs`, `AdminEndpoints.cs`.
9. **Konvergenznetz:** `SevenTvPeriodicResyncWorker` ruft je aktivem Kanal `EnsureJoinedAsync`, Takt
   `SevenTv:ResyncIntervalSeconds` (Default 60), Runde ≈ 0,85 s/Kanal (#272). Ein dauerhaft abgelehnter Kanal bekommt jede
   Runde einen JOIN und heute jede Runde eine Warning.
10. **Api-Seite:** `/api/admin/roster` (`AdminEndpoints.cs:245-315`) vergleicht inline gegen
    `IAdminChannelQueryService.ListActiveChannelNamesAsync` (alle `IsBotActive`, **ohne** Sperr- und Widerspruchsfilter) und
    liefert `missingFromIrc`, `generatedAtUtc`, `ageSeconds`. Der Worker joint nach
    `IChannelService.ListActiveChannelNamesAsync` (`ChannelService.cs:393`, **mit** Broadcaster-Sperre und
    `ExcludedChannelFilter`). `Channel.TrackingResumedAt` (sonst `CreatedAt`) ist die letzte (Re-)Aktivierung.
11. **`/api/health`** (`WorkerHealthEndpoints.cs:57-67`): payloadfrei, 200 nur bei „connected", Policy `PublicHealth`
    (30/min). Partitioniert wird anonym nach `Connection.RemoteIpAddress`, das hinter nginx dank der Vertrauensliste
    `172.16.0.0/12` (DECISIONS 2026-09-16) aus `X-Forwarded-For` kommt. Ziel des Container-HEALTHCHECK und des Kuma-Monitors.
12. **Identitätsabgleich** (`ChannelIdentityService`): gespeicherte Twitch-ID, die Helix nicht mehr liefert (gelöscht oder
    gesperrt) → **„nothing is written"**, die Zeile bleibt aktiv, unbefristet (`:253-260`). Login ohne gespeicherte ID, den
    Helix nicht kennt → Warning, Deaktivierung erst wenn `CreatedAt` **und** `LastSyncedAtUtc` älter als 7 Tage
    (`UnresolvableLoginGracePeriod`, `:43`, `:1402-1406`). Ein gesperrter oder gelöschter Kanal kann also dauerhaft aktiv
    bleiben.
13. **Abhilfewege des Betreibers:** `/admin/channels` hat je Zeile „Verlassen" (`DELETE /api/channels/{name}` →
    `ChannelService.LeaveAsync` mit Akteur, Audit `channel.leave`, Reason `Leave`, weiche Deaktivierung, Historie bleibt) und
    „Purge" (`DELETE /api/channels/{name}/purge`, globaler Admin, löscht samt Historie). Ein Leave entfernt den Wunsch im
    Worker per Redis-`LEAVE`.
14. **Die 20:** `WorkerCapacity.TwitchJoinBudgetChannels` (`WorkerCapacity.cs:41`) → `ceilings.twitchJoinBudgetChannels`
    (`AdminEndpoints.cs:542`) → Nenner in `admin-roster-card.ts` (`rawBudgetPercent`, `budgetTone`, `budgetWarningKey`,
    `budgetWarningParams`, `budgetPercent`), Modell `admin.model.ts:137`, Keys `admin.roster.joinBudget*`, Specs
    (`admin-roster-card.spec.ts`, `admin.service.spec.ts`, `admin-monitoring-page.template.spec.ts:151`), Mocks
    (`web/e2e/support/mocks.ts:209`, `web/e2e/audit/ui-audit.audit.ts:1257`), Api-Test
    `AdminDashboardActivityEndpointTests.cs:177`. Der Doc-Kommentar von `TwitchConcurrentChannelLimit` behauptet noch, ein
    verifizierter Bot hebe die Decke (seit DECISIONS 2026-11-25 falsch).
15. **Kanaldecke:** `ChannelService.CompleteJoinAsync` (`:819-847`) prüft `MaxActiveChannels` **nur, wenn der Join einen
    Kanal aktiviert** (neue Zeile oder inaktive Zeile), loggt die Ablehnung als Warning ohne `EventId`. Ein Absenken der
    Variable deaktiviert also nichts. Der Log-Viewer filtert mit `event:<Name>` (`LogFilterParser.cs:160`) und nimmt
    `q`/`range` als Query-Parameter (`admin-log-errors-row.ts:86-87`). Eigene EventIds benutzt der Code bisher nirgends.
16. **Admin-Karte:** `admin-roster-card.ts` lädt nur bei `LIVE_EVENT_TYPES.workerRoster` nach (`:342`), `isStale` liest
    `ageSeconds` des letzten Abrufs (`:237`, Schwelle 180 s), `statusKey` stellt `bootRecoveryCompleted === false` vor alles
    (`:324-326`). `pollWhileVisible` (`core/live/poll-while-visible.ts`) ist der vorhandene sichtbarkeitsgebundene Takt.
17. **Cache-Baustein:** `TtlCache<T>` (`Infrastructure/Services/TtlCache.cs`, Single-Flight, Fehler nie gecacht) mit dem
    Singleton-Muster `LogStatusReader` (eigener Scope über `IServiceScopeFactory`).
18. **Schwellen-Leiter** `utilizationTone` (`shared/ui/utilization-tone.ts`, 80/95) teilt sich der Admin-Balken mit dem
    Slot-Budget der Emote-Domäne.
19. **Reconnect-Backoff:** `TwitchReconnectBackoffPolicy.MaxDelay` 30 s (Jitter bis +20 %).

---

## 2. Entscheidungen

### E1 [E] Zustand je Kanal lebt im Worker, in einer reinen Klasse `ChannelJoinTracker`

`_desiredChannels` wird durch eine neue, reine, thread-sichere Klasse **`ChannelJoinTracker`** (`src/EmotePurge.Worker/`)
ersetzt. Je gewünschtem Kanal ein unveränderlicher Zustand: **bestätigt ja/nein, `UnconfirmedSinceUtc` (nullable),
`FailureReason`, `FailureAtUtc`**. Die Uhrzeit kommt als Parameter, der Tracker kennt keine Uhr. Eigenes internes `Lock`, nie
ein `await` darin, ruft nie nach außen — damit ist er immer der innerste Riegel unter `_intentGate` und `_clientStateGate`.

**Stempel-Regel (bindend, Grundlage der Obergrenze aus E4):** `UnconfirmedSinceUtc` heißt „seit wann der Worker bei
laufender Sitzung eine Bestätigung schuldet".

| Übergang | Wirkung |
|---|---|
| neuer Wunsch bei laufender Sitzung | unbestätigt, Stempel = jetzt |
| neuer Wunsch ohne Sitzung (Boot, Ausfall) | unbestätigt, Stempel `null` („wartet auf Sitzung") |
| bestätigt → unbestätigt durch Trennung oder Wiederaufbau (`MarkAllUnconfirmed`) | Stempel `null`, Grund bleibt leer |
| schon unbestätigt, dann Trennung | **unverändert** (Stempel und Grund bleiben) |
| Handshake einer neuen Sitzung (`BeginSession(now)`) | nur Kanäle mit Stempel `null` bekommen Stempel = jetzt |
| Bestätigung | Stempel, Grund und Grundzeit gelöscht |

Folgen: Ein langer Twitch-Ausfall altert keinen Stempel (die durch die Trennung verlorenen Bestätigungen beginnen beim
nächsten Handshake neu). Ein Kanal, der eine **ganze Sitzung lang** unbestätigt blieb, behält seinen Stempel über jede
weitere Sitzung — genau das macht wiederholte kurze Reconnects sichtbar (Befund 1 der Prüfung).

**Vorrang der Gründe [E]:** Ein konkreter NOTICE-Grund wird durch `noConfirmation` nur ersetzt, wenn er **älter als 15 s**
ist (Konstante im Tracker: TwitchLibs Timeout > 5 s plus zwei Ticks plus Reserve). So bleibt `concurrentChannelLimit` stehen,
wenn 5–7 s später der Timeout desselben Versuchs folgt; ein veralteter `channelSuspended` (der Kanal ist wieder frei, scheitert
aber still) wird beim nächsten Versuch zu `noConfirmation` und zählt dann für den Alarm (E11). Ein konkreter Grund ersetzt
immer `noConfirmation` und einen anderen konkreten Grund. Gebucht wird nur für einen **gewünschten, unbestätigten** Kanal.

Verworfen: ein zweites Dictionary neben `_desiredChannels`; die Zeit im Api ableiten (sieht nur Minutenschnappschüsse); ein
Zähler „Sitzungen seit Stempel" statt der Stempel-Regel (zweites Feld im Vertrag, und die Obergrenze wäre keine Zeit mehr);
Stempel bei Trennung behalten (ein langer Ausfall löste beim Reconnect sofort die Obergrenze aus).

### E2 [E] Gründe: NOTICE aus `OnSendReceiveData`, nie `OnUnaccountedFor`; feste Codes; einmal laut loggen

- **Erkennung, Heißpfad:** Im bestehenden `OnSendReceiveData`-Handler (Richtung `Received`) wird zuerst wie bisher der
  Frame-Stempel gesetzt. Dann ein allokationsfreier Befehlstest: beginnt die Zeile mit `@`, den Tag-Block bis zum ersten
  Leerzeichen überspringen, dann das Präfix (`:…`) bis zum nächsten; das folgende Token ordinal mit `NOTICE` vergleichen.
  Nur bei Treffer läuft der reine Parser **`TwitchJoinNoticeParser`** (Worker). Er zerlegt den Tag-Block an `;`, sucht den
  Schlüssel `msg-id` **unabhängig von der Position**, liest den Kanal aus `NOTICE #<kanal> :…`, wirft nie und gibt nie Rohtext
  weiter. NOTICEs ohne Tag-Block tragen kein `msg-id` und werden ignoriert.
- **Warum nicht `OnUnaccountedFor` (bleibt):** Ein angehängter Handler schaltet TwitchLibs `LogUnaccountedFor` für **alle**
  unbekannten Zeilen ab (`OnUnaccountedFor?.Invoke(...) ?? UnaccountedFor(text)`) — die redigierte Warnkette aus #246 und
  ihre Tests würden still wirkungslos. Verworfen ebenso: `OnFailureToReceiveJoinConfirmation.Details` (Freitext, kein
  `msg-id`, der Limit-NOTICE kommt dort gar nicht an).
- **Codes** (Konstantenklasse **`TwitchJoinFailureReasons`** in `src/EmotePurge.Core/Services/`, Muster
  `SevenTvSyncFailureReasons`): `noConfirmation` (Timeout ohne NOTICE), `channelSuspended` (`msg_channel_suspended`, deckt
  „existiert nicht"), `concurrentChannelLimit` (`msg_concurrent_channel_limit_reached`), `banned` (`msg_banned`). Andere
  `msg-id` ignoriert.
- **Identität:** Beide Grundquellen schreiben Manager-Zustand und folgen der Regel aus `WireUpClient`: Bindung an Client und
  Generation zur Verdrahtungszeit, Prüfung und Schreiben in **einem** `lock (_clientStateGate)`. Für `OnSendReceiveData` gilt
  die Sperre **nur im NOTICE-Zweig**.
- **Logzeilen [E]:** eine strukturierte Zeile je **erstem Auftreten oder Wechsel** des Grundes (der Tracker meldet
  „neu/geändert" vs. „Wiederholung" vs. „ignoriert"), Wiederholungen auf Debug. EventId-Namen: `TwitchJoinRejected`
  (NOTICE-Gründe; `concurrentChannelLimit` auf **Error**, damit es in der Fehlerzeile von `/admin/monitoring` erscheint;
  `channelSuspended` und `banned` auf Warning) und `TwitchJoinUnconfirmed` (`noConfirmation`, Warning). Die bisherige Warning
  „Twitch hat den Join … nicht bestätigt" geht darin auf. Geloggt werden Kanal und Code, nie Twitchs Text. Die
  Deduplizierung gilt je Prozess; nach einem Neustart erscheint je Kanal wieder eine Zeile (gewollt).

### E3 [E] Roster-Vertrag: additive Felder, atomare Erfassung, keine Migration

| Ort | Feld (JSON camelCase) | Bedeutung |
|---|---|---|
| `TwitchRosterEntry` (Worker) | `UnconfirmedSinceUtc`, `JoinFailureReason`, `JoinFailureAtUtc` | optionale Parameter **am Ende mit Default `null`**, damit die drei anderen Leser und ihre Tests unverändert kompilieren |
| `WorkerRosterChannel` (Core) | `ircUnconfirmedSinceUtc`, `ircJoinFailureReason`, `ircJoinFailureAtUtc` | `null`, solange bestätigt bzw. kein Grund bzw. „wartet auf Sitzung" (E1) |
| `WorkerRosterSnapshot` (Core) | `ircSessionStartedUtc` | Handshake der Sitzung zur Erfassungszeit, `null` ohne laufende Sitzung |

- **Atomare Erfassung:** neue Methode **`ITwitchChatManager.CaptureRoster()`** → `TwitchRosterCapture(SessionStartedUtc?,
  Entries)`. Sie liest Tracker-Momentaufnahme, Sitzungsbeginn und Verbindungsflag **in einem `lock (_clientStateGate)`** —
  demselben Riegel, unter dem `MarkAllUnconfirmed`, `BeginSession`, das Setzen und Löschen von `_sessionStartedUtcTicks` und
  jede Bestätigung laufen. Eine Erfassung kann damit nie Bestätigungen der alten mit dem Handshake der neuen Sitzung mischen.
  Die Arbeit unter dem Riegel ist eine Kopie von ≤ 500 Einträgen, einmal je Minute. `WorkerRosterPublisher` nutzt nur noch
  `CaptureRoster()`. `GetRoster()` bleibt für die drei anderen Leser mit unveränderter Bedeutung (darf intern delegieren).
- Verworfen: eine Eigenschaft `CurrentSessionStartedUtc` neben `GetRoster()` (Rev. 1; zwei Lesungen, Befund 2 der Prüfung);
  eine Sitzungs- oder Generationsnummer im Vertrag (der Sitzungsbeginn identifiziert die Sitzung bereits).
- **Mischbetrieb:** neue Api + alter Worker liest `ircSessionStartedUtc = null` und keine Stempel → `NoSession`, nichts
  überfällig (fail-open bis zum Worker-Deploy); alter Api + neuer Worker ignoriert die Zusatzfelder. Api und Worker kommen
  ohnehin im selben Deploy.

### E4 [E] Bewertung in der Infrastructure: reine Policy, Stichzeit ist die Erfassung

- **Soll-Liste:** neue Methode **`IChannelService.ListActiveChannelActivationsAsync`** → `(ChannelName, ActivatedAtUtc =
  TrackingResumedAt ?? CreatedAt)`, **dieselben Filter** wie `ListActiveChannelNamesAsync`; Letztere wird darauf
  zurückgeführt. Verworfen: `IAdminChannelQueryService` (ohne Sperr- und Widerspruchsfilter).
- **Policy** **`ChannelJoinHealthPolicy`** (Infrastructure, rein, statisch). Eingaben: Health-Schnappschuss (oder `null`),
  Roster (oder `null`), Soll-Liste, Optionen, `nowUtc`. Bewertet wird das Roster zu seiner Erfassungszeit
  `t = roster.GeneratedAtUtc` — es ist in sich konsistent (E3), sein Alter begrenzt die TTL (180 s). Stufen in dieser
  Reihenfolge:
  1. Roster fehlt → `RosterUnavailable`.
  2. **Instanzwechsel:** Health trägt eine `WorkerInstanceId` und sie weicht von der des Rosters ab → solange
     `nowUtc − health.ProcessStartedUtc ≤ 180 s` (Konstante = Roster-TTL; bei fehlendem `ProcessStartedUtc` gilt die Stufe
     als bestanden) → `Starting`; sonst → **`Inconsistent`**. Fehlt die Instanz im Health (alter Worker) → Stufe übersprungen.
     Deckt Deploy (altes Roster noch gültig), zwei gleichzeitig laufende Worker und einen hängenden Roster-Publisher eines
     neuen Prozesses.
  3. `BootRecoveryCompleted == false` und `t − ProcessStartedUtc ≤ BootRecoveryMax` → `Starting`, nichts überfällig.
     Darüber hinaus wird normal bewertet.
  4. Je Soll-Kanal (Namensvergleich `OrdinalIgnoreCase`):
     - im Roster und bestätigt → in Ordnung;
     - im Roster, unbestätigt, mit Sitzung → **geschuldet seit** `max(ircUnconfirmedSinceUtc ?? ircSessionStartedUtc,
       ircSessionStartedUtc)`; **überfällig**, wenn `t − geschuldetSeit > Grace` **oder** (Stempel vorhanden und
       `t − ircUnconfirmedSinceUtc > MaxUnconfirmed`);
     - im Roster, unbestätigt, **ohne** Sitzung → nur die Obergrenze gilt (Stempel vorhanden und älter als `MaxUnconfirmed`);
     - nicht im Roster → geschuldet seit `max(ActivatedAtUtc, ProcessStartedUtc)`, überfällig nach `Grace`; bei `Truncated`
       nie überfällig.
  5. Je überfälligem Eintrag: **zählt für den Alarm**, außer der Grund ist `channelSuspended` oder `banned` (E11).
  6. Status: mindestens ein alarmierender Eintrag → `Failing`; sonst `NoSession`, falls das Roster keine Sitzung hat; sonst
     `Healthy`.
- **Öffentliche Entscheidung** (`IsAlarming`, ebenfalls in der Policy): Worker-Health fehlt oder ist nicht „connected" →
  nicht alarmieren (das meldet `/api/health`); sonst alarmieren bei `Failing`, `RosterUnavailable`, `Inconsistent`.
- **Warum Grace ab Sitzungsbeginn *und* Obergrenze ab Stempel:** Die Grace darf erst laufen, wenn der Worker wieder
  verbunden ist, sonst alarmiert jeder Reconnect. Die Obergrenze verhindert, dass eine Folge kurzer Reconnects die Frist
  endlos neu startet: ein Kanal, der eine Sitzung lang nicht bestätigt wurde, behält seinen Stempel (E1), und nach
  `MaxUnconfirmed` ist er überfällig, egal wie viele Handshakes dazwischen lagen.
- **Grace-Default 360 s [E], neu gerechnet mit TwitchLibs Warteschlange (Befund 6):** Rejoin-Runde nach einem Reconnect
  = `max(0,6 s × N, Σ Antwortzeiten) + ≈ 7 s × K` (K = still abgelehnte Kanäle, je Timeout plus Ticks; `channelSuspended`
  kostet nur eine Antwortzeit) + 5 s Timeout des letzten. Dazu ein verpasster Takt des Konvergenznetzes (60 s) und dessen
  Lauf (0,85 s × N). Bei N = 120, K = 5: 72 + 35 + 5 + 60 + 102 = 274 s → 360 s lässt ≈ 86 s Reserve. Faustformel für
  Operations.md: `Grace ≥ 1,45 s × N + 7 s × K + 5 s + SevenTv:ResyncIntervalSeconds + Reserve`. **Der Default ist eine
  Rechnung, keine Messung:** T8 misst Sende- und Bestätigungszeiten mit Produktions-Grace und realistischer Kanalzahl; erst
  danach nennt das Runbook eine Erholungsdauer (T10). Liegt die Messung über 360 s minus 60 s Reserve, hebt der Orchestrator
  den Default vor T9 in einem eigenen Commit samt DECISIONS-Nachtrag an.
- **Grenze der Warteschlange:** Re-JOINt das Konvergenznetz je Runde K still abgelehnte Kanäle, kostet jede Runde ≈ 7 s × K
  Warteschlangenzeit; erreicht das die Rundenlänge (K ≈ 20 bei N = 120, also genau der Fall der benannten Decke), läuft die
  Warteschlange nicht mehr leer. Dieser Fall alarmiert ohnehin (`concurrentChannelLimit`, Error-Zeile, 503). Kein Umbau des
  Konvergenznetzes in diesem Plan; T8 misst, ob Doppel-Einreihungen (Befund 6) auftreten, und meldet sie als Befund.
- **Obergrenze-Default 1800 s [E]:** Ein einzelner Reconnect im ungünstigsten Moment (Kanal knapp vor Ablauf der Grace,
  dann Trennung, Backoff bis 36 s, neue Grace) ergibt höchstens `2 × Grace + 36 s ≈ 756 s` — 1800 s toleriert ihn mit mehr als
  doppelter Reserve und begrenzt einen unentdeckten Erfassungsausfall unter Flattern auf 30 min. Validierung: `MaxUnconfirmed
  ≥ 2 × Grace + 60 s`.
- **Bekannte, akzeptierte Grenze:** Fällt eine Erfassung genau in eine Lücke ohne Sitzung, gilt dort nur die Obergrenze; bei
  Kuma mit „Retries 2" zählt erst die dritte Ablehnung in Folge. Unter kurzen Reconnects (Sekunden) gegen einen 60-s-Takt
  liegen fast alle Erfassungen in einer Sitzung.

Verworfen: Policy im Api-Projekt (braucht die Datenbank-Soll-Liste, gehört nach Regel 11 getestet in die Infrastructure);
Überfälligkeit im Worker entscheiden (Grace in zwei Prozessen); Sitzungsbeginn zusätzlich in den Health-Schnappschuss und
Abgleich beider Payloads (nach E3 unnötig: ein älteres Roster ist ältere, aber stimmige Information, und eine zur damaligen
Zeit überfällige Zeile bleibt eine wahre Aussage).

### E5 [E] Neuer Endpunkt `GET /api/health/channels`, Entscheidung in der Infrastructure, eigenes Budget, geteiltes Ergebnis

- Anonym, kein Körper, nur Statuscode, außerhalb der Admin-Gruppe, in `WorkerHealthEndpoints.cs` neben `/api/health`.
- **Die Api entscheidet nichts.** Neuer Singleton **`IChannelJoinHealthCheck`** (Core-Interface, Infrastructure-Implementierung
  nach dem Muster `LogStatusReader`): `IsAlarmingAsync(ct)` liest über einen eigenen Scope Health, Roster und Soll-Liste,
  ruft die Policy (E4) und liefert nur die öffentliche Entscheidung. Das Ergebnis liegt **5 s** in einem `TtlCache<T>` mit
  Single-Flight (Konstante, keine Konfiguration): gleich wie viele Aufrufer, höchstens eine Auswertung (zwei Redis-Lesungen,
  eine kleine indizierte Abfrage über ≤ 120 Zeilen) je 5 s. Eine Ausnahme in der Auswertung wird nicht gecacht (Eigenschaft von
  `TtlCache`), geloggt (Warning, ohne Kanalnamen) und als **503** beantwortet — „nicht auswertbar" zählt als Alarm.
- **`WorkerHealthStatus` zieht in die Infrastructure** (`src/EmotePurge.Infrastructure/Health/`, rein, öffentlich), weil die
  Entscheidung „Worker connected?" Teil der öffentlichen Entscheidung ist. Reine Verschiebung, Verhalten und Schwellen
  unverändert; die beiden Api-Nutzer bekommen nur den neuen Namensraum.
- **Rate-Limit:** eigene Policy **`PublicChannelHealth`** (FixedWindow, 10/min, anonym nach IP wie `PublicHealth`), eigener
  Zähler. Kuma ruft 1/min plus Retries. Die Partition je IP schützt `/api/health` vor Verdrängung (eigener Zähler); die
  Gesamtlast begrenzt der 5-s-Cache, nicht das Limit. Damit gilt hinter Cloudflare und nginx auch dann, wenn viele Aufrufer
  eine Partition teilen: `/api/health` bleibt unberührt.
- **Prinzip, das Doppelalarme verhindert:** Der Kanal-Monitor meldet nur, was `/api/health` nicht meldet. Ist der Worker tot
  oder getrennt, ist Monitor 1 rot und Monitor 2 grün — **ein** Telegram-Alarm für **eine** Ursache. Fehlt nur das Roster oder
  passt es nicht zur Instanz, während die Health „connected" sagt, ist die Kanalüberwachung blind → 503.
- `/api/health` bleibt **unverändert**, ebenso der Container-HEALTHCHECK. **Der neue Endpunkt darf nie in einen
  HEALTHCHECK.**
- Verworfen: den `PublicHealth`-Topf teilen (Rev. 1, Befund 4 der Prüfung); globaler Gleichzeitigkeitsdeckel statt Cache
  (begrenzt Last, liefert aber unter Last 503 statt der Antwort); Kanalzustand in `/api/health` einrechnen; Payload mit
  Kanalnamen (öffentlich).

### E6 [E] Admin-Monitoring: in der bestehenden Roster-Karte

- `/api/admin/roster` nutzt den scoped **`IChannelJoinHealthService.EvaluateAsync`** (ungecacht — authentifiziert, wenige
  Aufrufe, frische Werte gewollt; derselbe Dienst, den der Check aus E5 im eigenen Scope ruft) und liefert zusätzlich
  `joinHealth` und `ircUnjoined` (Vertrag 3.3); **`missingFromIrc`/`missingFromIrcTotal` entfallen**.
- Je Kanal: Link auf `/admin/channels/{name}`, Grund (übersetzt, unbekannter Code → generischer Text mit Code), „unbestätigt
  seit" / „wartet auf Twitch-Sitzung" (Stempel `null`) / „nicht im Worker, erwartet seit", und **als Text** „überfällig",
  „wartet auf Bestätigung" oder — bei `channelSuspended`/`banned` — „von Twitch abgelehnt, zählt nicht für den Alarm" mit
  dem Hinweis auf „Verlassen" (Farbe nie allein, WCAG 1.4.1). Sortierung: alarmierend zuerst, dann ausgenommen-überfällig,
  dann nach „geschuldet seit" aufsteigend.
- **Status der Karte kommt aus `joinHealth.status`**, nicht aus `bootRecoveryCompleted`: Vorrang `unknown` > `unavailable`
  > `stale` > `inconsistent` > `failing` > `starting` > `noSession` > `incomplete` > `complete`. `starting` erscheint nur,
  wenn die Api `starting` meldet — nach `BootRecoveryMax` meldet sie `failing`, und die Karte zeigt das.
- **Sichtbares Altern:** Das Alter des Stands läuft auf der Seite weiter: `ageSeconds` beim Empfang plus seitdem vergangene
  Zeit (so bleibt die Uhr des Browsers aus dem Spiel; gleichwertig zu „seit `generatedAtUtc`" in Serverzeit). Ein
  sichtbarkeitsgebundener Takt (`pollWhileVisible`, 10 s) treibt nur diese Rechnung. Überschreitet das Alter 90 s (Takt 60 s
  plus Reserve) ohne neues `worker.roster`-Ereignis, lädt die Karte **einmal** nach; bleibt der Stand alt, zeigt sie ab 180 s
  `stale`. Ein späteres Ereignis oder ein jüngerer Stand setzt die Einmal-Sperre zurück.
- Neuer Status `failing` (Ton `danger`), `inconsistent` (Ton `danger`), `noSession` (Ton `warning`).
- Die Kanal-Detailseite zeigt Grund und „seit" unter dem IRC-Marker.
- [P] `trackedChannelCount` bleibt die Admin-Zählung (alle `IsBotActive`); die Unjoined-Liste nutzt die Worker-Sicht (E4).

### E7 [E] Teil B, Anzeige: Nenner ist unsere Decke, Twitchs 100 nur Referenz, Frühwarnung bei 90 %

- Der Balken zeigt `trackedChannelCount / ceilings.maxActiveChannels`. `TwitchJoinBudgetChannels` entfällt vollständig
  (Konstante, API-Feld, Modell, Keys, Specs, Mocks, Api-Test, Kommentare).
- **Schwellen dieses Balkens: `warning` ab 90 %, `danger` ab 100 %**, als optionales Schwellenargument von
  `utilizationTone`; der Default (80/95) bleibt, das Slot-Budget ist unberührt.
- Textzeilen ab `warning`: nahe der Grenze / Grenze erreicht (neue Kanäle werden abgelehnt, globale Admins ausgenommen) /
  über der Grenze (nur durch Admin-Joins möglich).
- Twitchs 100 erscheint als Hinweistext mit Herkunft („gilt für benannte Konten; für die anonyme Verbindung am 2026-09-08
  bis 120 ohne Ablehnung gemessen, undokumentiert"), nicht als Balken.
- **Kein Alarm für 90 % [E]:** Die Decke verliert keine Daten, sie lehnt nur neue Kanäle mit Begründung ab. Das
  Roster-Badge bekommt weiterhin keine Kapazitätsstufe (DECISIONS 2026-08-02).

### E8 [E] Abgelehnte Joins zählen: EventId plus Log-Viewer, keine neue Tabelle

Die Warning in `ChannelService.CompleteJoinAsync` bekommt die EventId **`ChannelCapacityReached`**. Gezählt wird im
Log-Viewer (`event:ChannelCapacityReached`, Histogramm, 7 Tage); die Kapazitätszeile der Roster-Karte verlinkt
`/admin/logs?q=event:ChannelCapacityReached&range=7d`. Verworfen: eigener Zähler in Redis/Postgres; Audit-Eintrag je
Ablehnung. Grenze: ohne `LogCapture:Enabled` nur stdout — in Operations.md benannt.

### E9 [E] Anhebung auf 120 ist ein Betreiberschritt, der Default bleibt 80; der Rückweg hat zwei Teile

Kein Code-Default ändert sich. Operations.md beschreibt (T10):

- **Anheben:** nach Teil A live und Beobachtungszeit ([F2]) `Channels__MaxActiveChannels=120` im Stack, Api neu starten.
  Nach dem ersten Worker-Neustart Rejoin-Dauer, Boot-Recovery und Grace gegen die **in T8 gemessenen** Werte und die Formel
  aus E4 prüfen.
- **Rückweg, Teil 1 — Aufnahme stoppen:** Variable zurück auf 80, Api neu starten. Das blockt nur weitere Aktivierungen
  (Befund 15); kein aktiver Kanal wird dadurch verlassen.
- **Rückweg, Teil 2 — Erfassung wiederherstellen:** Zeigt die Roster-Karte `concurrentChannelLimit` oder dauerhaft
  unbestätigte Kanäle, wählt der Betreiber Kanäle aus (Vorschlag: zuletzt aktivierte zuerst, `TrackingResumedAt`) und
  verlässt sie über „Verlassen" in `/admin/channels` (auditiert, `channel.leave`, Historie bleibt, Manager können später
  wieder beitreten, sobald Platz ist), bis die Zahl aktiver Kanäle unter der funktionierenden Grenze liegt. Danach prüfen:
  Roster-Karte zeigt keine Unjoined-Einträge, `ircConfirmedCount` = `trackedChannelCount`, `/api/health/channels` 200 über
  mindestens Grace plus zwei Roster-Takte. Wen das betrifft (Manager der verlassenen Kanäle), entscheidet und informiert der
  Betreiber.

### E10 [E] Konfiguration

Neue Klasse **`ChannelJoinHealthOptions`**, Abschnitt `Channels:JoinHealth`, Muster `ChannelCapacityOptions` (POCO, eager
gebunden und validiert in `AddEmotePurgeInfrastructure`, Singleton):

| Schlüssel | Default | Validierung | Env-Variable |
|---|---|---|---|
| `Channels:JoinHealth:GraceSeconds` | 360 | 60 … 3600 | `Channels__JoinHealth__GraceSeconds` |
| `Channels:JoinHealth:MaxUnconfirmedSeconds` | 1800 | 600 … 86400 und ≥ 2 × `GraceSeconds` + 60 | `Channels__JoinHealth__MaxUnconfirmedSeconds` |
| `Channels:JoinHealth:BootRecoveryMaxSeconds` | 900 | 60 … 7200 | `Channels__JoinHealth__BootRecoveryMaxSeconds` |
| `RateLimiting:PublicChannelHealth:PermitLimit` | 10 | bestehende `FixedWindowPolicy.Validate` | `RateLimiting__PublicChannelHealth__PermitLimit` |

Konstanten, keine Konfiguration: Ergebnis-Cache 5 s (E5), Instanzwechsel-Toleranz 180 s (= Roster-TTL, E4), Vorrangfenster
15 s (E1). „Roster veraltet" bleibt der TTL-Ablauf. Kein Eintrag in `docker-compose*.yml` nötig; `appsettings.json` der Api
bekommt den Rate-Limit-Eintrag neben `PublicHealth`.

### E11 [B] Gesperrte und gelöschte Kanäle: sichtbar, geloggt, kein 503

Betreiberentscheidung (vorläufig, überstimmbar; ersetzt [F1] aus Revision 1):

- Überfällige Kanäle mit Grund `channelSuspended` oder `banned` machen `/api/health/channels` **nicht** rot. Sie stehen mit
  Grund in der Roster-Karte (E6) und erzeugen je Prozess eine Warning `TwitchJoinRejected` (E2). Für 503 zählen nur
  `noConfirmation`, `concurrentChannelLimit`, fehlende Kanäle sowie `RosterUnavailable` und `Inconsistent`.
- **Warum:** Der Identitätsabgleich räumt solche Kanäle nicht verlässlich ab (Befund 12: gespeicherte ID → nie; Login ohne
  ID → frühestens nach 7 Tagen). Ein binärer Monitor, der dauerhaft rot steht, meldet jeden weiteren Ausfall nicht mehr.
- **Späterer unabhängiger Ausfall:** Weil ausgenommene Kanäle den Monitor nie halten, schaltet ein danach scheiternder
  anderer Kanal ihn auf DOWN (Test in T5/T6). Wird ein ausgenommener Kanal wieder frei und scheitert dann still, ersetzt
  `noConfirmation` den veralteten Grund nach 15 s (E1) und er zählt.
- **Abhilfe des Betreibers:** „Verlassen" in `/admin/channels` (Befund 13). Bleibt ein Kanal mit `noConfirmation` dauerhaft
  rot (etwa ein gelöschter Kanal, für den Twitch keinen NOTICE schickt — T8 misst, welcher Fall eintritt), ist derselbe Leave
  das Quittieren. Kein eigener Quittier-Mechanismus.
- Überstimmt der Betreiber das, entfällt nur Stufe 5 der Policy (E4); Verträge bleiben.

### E12 [E] Live-Verifikation je Backend-Task, Live-Gate vor dem Frontend

Regel 16 verlangt Live-Prüfung vor dem Commit. Deshalb hat **jeder Backend-Task mit externem System einen eigenen kurzen
Live-Schritt vor seinem Commit** (T2, T3, T4, T6, T7); T5 ist reine Logik gegen Testcontainers und wird mit T6 live geprüft,
bevor irgendetwas gepusht wird — ein in T6 gefundener T5-Fehler wird in einem eigenen Fix-Commit behoben. **T8 ist das
Live-Gate mit Messung** (Produktions-Grace, realistische Kanalzahl); T9 (Frontend) beginnt erst, wenn T8 bestanden oder jeder
Befund mit Folge entschieden ist. Für jeden Live-Schritt gilt: Orchestrator stellt vorher sicher, dass kein anderer
Dev-Worker läuft (Worktree 487, Compose-`worker` des Haupt-Checkouts); `docker compose up -d postgres redis` nur im
Haupt-Checkout, nie aus dem Worktree; Api und Worker per `dotnet run` aus dem Worktree; Redis-Befehle per `docker exec
<redis-container> redis-cli` (Name mit `docker ps` prüfen).

---

## 3. Verträge im Überblick

### 3.1 Worker-intern

- `ChannelJoinTracker` (öffentlich im Worker, keine DI-Registrierung, vom Manager besessen): `RecordIntent(name, nowUtc,
  sessionRunning)`, `MarkConfirmed(name)` (nur vorhanden + unbestätigt, fügt nie ein), `MarkAllUnconfirmed()`,
  `BeginSession(nowUtc)`, `RecordFailure(name, reason, nowUtc)` → „neu oder geändert" / „Wiederholung" / „ignoriert",
  `Remove(name)`, `Contains`, `IsConfirmed`, `Names`, `Snapshot()` (nach Name ordinal sortiert). Namen `OrdinalIgnoreCase`.
  Stempel- und Vorrangregeln nach E1.
- `TwitchJoinNoticeParser`: `IsNotice(line)` (allokationsfrei, E2) und `TryParse(line, out reasonCode, out channelName)`
  (`true` nur für die vier Codes mit Kanal; Kanal ohne `#`, kleingeschrieben; wirft nie).
- `ITwitchChatManager.CaptureRoster()` → `TwitchRosterCapture(SessionStartedUtc?, Entries)` (E3). `TwitchRosterEntry`
  erweitert (E3). `GetRoster()` unverändert.
- Interne Test-Naht für den Transport (T3): Produktion baut weiterhin `WebSocketClient`; DI-Registrierung unverändert.

### 3.2 Redis `worker:roster` (additiv, E3)

`channels[].ircUnconfirmedSinceUtc`, `channels[].ircJoinFailureReason`, `channels[].ircJoinFailureAtUtc`,
`ircSessionStartedUtc` auf Schnappschuss-Ebene. `null`-Felder werden weggelassen (`WhenWritingNull`). `worker:health`
unverändert.

### 3.3 Api und Infrastructure

- `GET /api/health/channels`: anonym, Policy `PublicChannelHealth`, Körper leer, 200/503 nach `IsAlarmingAsync` (E5);
  `Cache-Control: no-store` aus der bestehenden Middleware.

  | Worker-Health | Join-Status | Antwort |
  |---|---|---|
  | fehlt / nicht „connected" | beliebig | 200 |
  | „connected" | `Healthy`, `Starting`, `NoSession` | 200 |
  | „connected" | `Failing`, `RosterUnavailable`, `Inconsistent` | 503 |
  | Auswertung wirft | — | 503 |

- `IChannelJoinHealthService.EvaluateAsync(ct)` (Core-Interface, Infrastructure, scoped) → `ChannelJoinHealthReport { Status,
  WorkerStatus, EvaluatedAtUtc?, GraceSeconds, MaxUnconfirmedSeconds, Roster (gelesen oder null), Entries, AlarmingCount,
  ExcludedCount }`; `ChannelJoinHealthStatus` = `Healthy | Starting | NoSession | RosterUnavailable | Inconsistent |
  Failing`; `ChannelJoinHealthEntry { ChannelName, InRoster, UnconfirmedSinceUtc?, OwedSinceUtc?, Reason?, ReasonAtUtc?,
  Overdue, CountsForAlarm }` (alle unbestätigten bzw. fehlenden Soll-Kanäle, auch innerhalb der Frist).
- `IChannelJoinHealthCheck.IsAlarmingAsync(ct)` (Core-Interface, Infrastructure-Singleton, 5-s-Cache).
- `IChannelService.ListActiveChannelActivationsAsync(ct)` → `IReadOnlyList<ActiveChannelActivation(ChannelName,
  ActivatedAtUtc)>` (Core-Record).
- `WorkerHealthStatus` im Namensraum der Infrastructure (E5).
- `/api/admin/roster`, **beide Zweige**: `joinHealth: { status: 'healthy'|'starting'|'noSession'|'rosterUnavailable'|
  'inconsistent'|'failing', graceSeconds, maxUnconfirmedSeconds }`. Zweig mit Schnappschuss zusätzlich: `ircUnjoined: [{
  channelName, inRoster, unconfirmedSinceUtc|null, owedSinceUtc|null, reason|null, reasonAtUtc|null, overdue,
  countsForAlarm }]` (gekappt auf `DeficitListLimit` = 50, Sortierung E6), `ircUnjoinedTotal`, `ircAlarmingTotal`,
  `ircExcludedTotal`. Bleibt: `generatedAtUtc`, `ageSeconds`, `ceilings.twitchConcurrentChannelLimit` (Referenz),
  `ceilings.maxActiveChannels`. Entfällt: `missingFromIrc`, `missingFromIrcTotal`, `ceilings.twitchJoinBudgetChannels`. Der
  Status wird explizit auf den camelCase-String abgebildet.
- Rate-Limit-Übersicht der Admin-Seite (`AdminEndpoints.cs:555-570`) führt `PublicChannelHealth` mit Partition `remote-ip`.
- Keine neuen `ApiErrorCodes` (Regel 7 nicht berührt).

### 3.4 Frontend

`RosterCeilings` ohne `twitchJoinBudgetChannels`; `AdminRoster` mit `joinHealth`, `ircUnjoined`, `ircUnjoinedTotal`,
`ircAlarmingTotal`, `ircExcludedTotal`, ohne `missingFromIrc*`; `RosterChannel` mit den drei neuen optionalen Feldern;
`utilizationTone(percent, thresholds?)`. Locale-Keys: entfallen `admin.roster.joinBudget`, `joinBudgetWarning`,
`joinBudgetCritical`, `joinBudgetExceeded`, `joinBudgetHint`, `missingIrc`; neu `admin.roster.capacity.*` (Label, drei
Warnstufen, Hinweis, Link), `admin.roster.unjoined.*` (Überschrift mit Zahl, überfällig, wartet, wartet auf Sitzung, seit,
nicht im Worker, ausgenommen mit Leave-Hinweis),
`admin.roster.joinFailure.reason.{noConfirmation,channelSuspended,concurrentChannelLimit,banned,unknown}`,
`admin.roster.status.{failing,inconsistent,noSession}`; `twitchLimitHint` neu formuliert. de und en in Parität.

---

## 4. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander** (geteiltes `obj/`, geteilte Locale-Dateien). Jeder
Backend-Task endet mit grünem `dotnet build EmotePurge.slnx`, `dotnet format EmotePurge.slnx --verify-no-changes`, den Tests
seines Testprojekts **und seinem Live-Schritt (E12)**; jeder Frontend-Task mit `npm --prefix web test -- --watch=false`,
`lint` und `format:check`. Jeder Task ein eigener Conventional Commit (englisch). Member-Reihenfolge nach Regel 19. Neue
Kommentare und Logtexte englisch. **Ein** DECISIONS-Eintrag (englisch, ganz oben) „Join-failure detection and capacity
display (#516)": **T3 legt ihn an** (erste Vertragsänderung: EventIds als neue Konvention); T4, T5, T6, T7 und T9 ergänzen je
ihren Absatz und die `**Betrifft:**`-Zeile **im selben Commit wie ihre Vertragsänderung** (Regel 3). Live-Ergebnisse der
Tasks T2–T7 gehen als kurze Abschnitte in das Protokoll `docs/nachtlaeufe/live-516-<datum>.md` (deutsch, Muster
`live-486-2026-11-20.md`), das T2 anlegt und T8 abschließt; **keine Namen echter Kanäle im Protokoll** (öffentliches Repo),
nur Zahlen und erfundene Sondennamen.

### T1 — Reine Worker-Bausteine: `ChannelJoinTracker`, `TwitchJoinNoticeParser`, `TwitchJoinFailureReasons`

- **Lesen:** E1, E2, 3.1; `TwitchChatManager.cs` (alle `_desiredChannels`-Stellen, `TallyRejoin`, `AllConfirmed`,
  `MarkAllChannelsUnconfirmed`), `ChannelLeaveLedger.cs` und Tests als Stilvorbild, `SevenTvSyncFailureReasons`,
  `TwitchTagKeyAllowlistTests.cs:320` (NOTICE-Zeilenform).
- **Liefert:** die drei Typen; `TwitchJoinFailureReasons` in `src/EmotePurge.Core/Services/`, die anderen beiden in
  `src/EmotePurge.Worker/`. Noch keine Verdrahtung.
- **Grenzfälle Tracker:** jede Zeile der Stempel-Tabelle aus E1 einzeln; Wunsch auf bestätigtem Kanal ändert nichts;
  Bestätigung nach `Remove` fügt nicht ein; `BeginSession` stempelt nur `null`-Stempel und lässt vorhandene unberührt;
  **mehrere Folgen `MarkAllUnconfirmed` → `BeginSession` ohne Bestätigung: der Stempel eines nie bestätigten Kanals bleibt
  der erste**, der eines vorher bestätigten Kanals ist der letzte Sitzungsbeginn; Vorrang: `noConfirmation` 5 s nach
  konkretem Grund → Grund bleibt, „Wiederholung"; `noConfirmation` 16 s nach konkretem Grund → ersetzt, „geändert"; Grenze
  genau 15 s; konkreter Grund ersetzt `noConfirmation` und einen anderen konkreten; gleicher Grund zweimal → „Wiederholung",
  Grundzeit aktualisiert; Grund auf bestätigtem oder unbekanntem Kanal → „ignoriert"; `Remove` und erneuter Wunsch →
  frischer Zustand; Groß-/Kleinschreibung; Momentaufnahme sortiert; parallele Aufrufe aus mehreren Threads ohne Ausnahme
  und ohne verlorene Übergänge.
- **Grenzfälle Parser:** die vier Codes mit Kanal; aufgezeichnete Limit-Zeile (Kanal erfunden); **dieselbe Zeile mit
  `msg-id` an zweiter und letzter Stelle hinter erfundenen Tags** → erkannt; Tag-Wert mit maskierten Zeichen; `NOTICE *
  :…` ohne Kanal; NOTICE ohne Tag-Block; anderer `msg-id` (`emote_only_on`, `msg_ratelimit`); PRIVMSG mit `msg-id`-ähnlichem
  Tag und Text „NOTICE" im Nachrichtentext → kein NOTICE; USERNOTICE → kein NOTICE; abgeschnittene Zeile; leerer String;
  Großbuchstaben im Kanal → klein.
- **Tests:** `tests/EmotePurge.Worker.Tests/ChannelJoinTrackerTests.cs`, `TwitchJoinNoticeParserTests.cs` (container-frei,
  Regel 11). Nur erfundene Kanäle.
- **Live:** entfällt (reine Klassen ohne externes System).
- **Fertig:** Build, Format, `dotnet test tests/EmotePurge.Worker.Tests` grün. Commit `feat(worker): add a per-channel join
  tracker and a join-notice parser`.

### T2 — `TwitchChatManager` auf den Tracker umstellen, Sitzungsbeginn, atomare Erfassung

- **Lesen:** E1, E3, Befund 1, 5, 7; `TwitchChatManager.cs` vollständig (Klassen-Doku zu `WireUpClient`, Gate-Regeln,
  `ReconnectOnceAsync`, `OnConnected`, `OnDisconnected`), `ITwitchChatManager.cs`, `WorkerRosterPublisher.cs`,
  `TwitchChatManagerLeaveRaceTests.cs`.
- **Liefert:** `_desiredChannels` vollständig durch `ChannelJoinTracker` ersetzt, Verhalten aller bisherigen Pfade gleich
  (Intent vor JOIN, Re-Check nach dem Gate, Leave-Ledger, `TallyRejoin`, `AllConfirmed`, `EnsureJoinedAsync`-Abkürzung);
  `OnJoinedChannel` → `MarkConfirmed`; Clientwechsel und `OnDisconnected` → `MarkAllUnconfirmed()`; `OnConnected` →
  `BeginSession(now)` im selben Riegel wie das Setzen von `_sessionStartedUtcTicks`; `RecordIntent` mit „Sitzung läuft"
  (Befund 5); `CaptureRoster()` unter `_clientStateGate` (E3); `GetRoster()` unverändert in der Bedeutung. Der Publisher
  bleibt in diesem Task unverändert (er ruft weiter `GetRoster()`). Noch keine Gründe.
- **Grenzfälle:** Join bei getrenntem Manager → Stempel `null`; Bestätigung einer ersetzten Sitzung bucht nichts (bestehende
  Identitätsprüfung); `CaptureRoster` während eines Wiederaufbaus liefert entweder den alten oder den neuen Stand, nie eine
  Mischung (Begründung im Code-Kommentar, da per Test schwer erzwingbar); `CaptureRoster` ohne Sitzung → `SessionStartedUtc
  = null`.
- **Tests (Worker.Tests):** `TwitchChatManagerJoinStateTests.cs` am nie verbundenen Manager (wie
  `TwitchChatManagerLeaveRaceTests`): Join → Stempel `null`, Leave entfernt, Ensure nach Leave mit altem Stempel legt nichts
  an, `CaptureRoster` ohne Sitzung. Bestehende Worker-Tests (`TwitchChatManagerLeaveRaceTests`, `CaptureGapTrackerTests`,
  `RosterPrunePolicyTests`, `WishCaptureWorkerTests`, `WorkerBootSequenceTests`, `SevenTvPeriodicResyncWorkerTests`)
  unverändert grün. Die Sitzungsübergänge mit echtem Handshake prüft T3 über den Fake-Transport.
- **Live (vor dem Commit, E12):** Worker gegen echtes Twitch mit den Kanälen der Dev-Datenbank: Boot → alle Kanäle bestätigt
  (`redis-cli GET worker:roster`, `ircJoinConfirmed`); `PUBLISH channel:bot:commands DEBUG:TWITCH-RECONNECT` (Worker mit
  `Worker__Debug__AllowTwitchReconnectTrigger=true`) → nach der Rejoin-Runde wieder alle bestätigt; Worker-Log ohne neue
  Fehler. Ergebnis als Abschnitt „T2" ins Protokoll (Datei anlegen).
- **Fertig:** Build, Format, Worker.Tests grün, Live bestanden. Commit `refactor(worker): track join state per channel with
  session-aware timestamps`.

### T3 — Gründe erfassen: NOTICE und Timeout, Fake-Transport-Tests, DECISIONS-Eintrag

- **Lesen:** E1, E2, Befund 2–4, 6; `TwitchChatManager.cs` (`WireUpClient`, `OnSendReceiveData`,
  `OnFailureToReceiveJoinConfirmation`, `CreateClient`, `OpenAndAwaitHandshakeAsync`), `RedactingTwitchClientLoggerFactory.cs`
  und Tests, `RewardRedemptionTwitchLibTests.cs` (Muster „TwitchLib-gebundener Test"), `IrcLineSpliceRuleTwitchLibTests.cs`;
  TwitchLib.Client 4.0.1 `TwitchClient` (`_client_OnMessage`, `HandleNotice`, `HandleChannelSuspended`, `JoinChannelTimeout`)
  und TwitchLib.Communication 2.0.1 `IClient` dekompiliert (`ilspycmd`).
- **Liefert:** `OnFailureToReceiveJoinConfirmation` mit Identitätsprüfung und `RecordFailure(noConfirmation)` in einem
  `lock (_clientStateGate)`; NOTICE-Zweig in `OnSendReceiveData` nach E2 (Frame-Stempel unverändert davor, Befehlstest
  allokationsfrei, Sperre nur bei NOTICE); Logzeilen mit EventIds und Dedupe nach E2; `TwitchRosterEntry`-Felder befüllt
  (`GetRoster`/`CaptureRoster`); **interne Transport-Naht**: ein Test kann den `IClient` des `TwitchClient` ersetzen, für den
  Boot-Client und jeden Wiederaufbau. Form wählt der Implementer; Bedingungen: nicht öffentlich, Produktion baut weiterhin
  `WebSocketClient` mit `NoReconnectionPolicy`, `WorkerServiceRegistrationTests` unverändert grün. **DECISIONS-Eintrag
  anlegen** mit Absatz „worker join state": Stempel-Regel E1, Gründe und Vorrangfenster, EventIds als neue Konvention
  (Namen, Stufen, Dedupe je Prozess), warum nicht `OnUnaccountedFor`.
- **Grenzfälle:** NOTICE eines bereits ersetzten Clients bucht nichts; Timeout-Ereignis vom Timer-Thread während eines
  Clientwechsels; Limit-NOTICE gefolgt vom Timeout → **ein** Grund `concurrentChannelLimit`, **eine** laute Zeile;
  gesperrter Kanal mit minütlichem Retry → eine Warning, danach Debug; Leave zwischen NOTICE und Buchung → nichts gebucht;
  `OnSendReceiveData` wirft nie (eine Ausnahme trennt TwitchLibs Multicast-Kette) und sperrt nur im NOTICE-Zweig.
- **Tests (Worker.Tests):**
  - `TwitchJoinNoticeTwitchLibTests.cs` (bewusst TwitchLib-gebunden, über `OnReadLineTestAsync`): `msg_channel_suspended`
    löst `OnFailureToReceiveJoinConfirmation` sofort aus; `msg_concurrent_channel_limit_reached` löst ihn **nicht** aus.
    Pinnt die TwitchLib-Fakten, auf denen der Vorrang beruht — **prüft nicht** unseren Empfangspfad.
  - `TwitchChatManagerJoinFailureTransportTests.cs` (Fake-`IClient` über die Naht; der Test fährt den Handshake bis „004",
    damit der Manager eine echte Sitzung hat, und beantwortet JOINs nach Vorgabe):
    (a) **aufgezeichnete Limit-Zeile** (Kanal erfunden) über `OnMessage` des Transports → `CaptureRoster` trägt
    `concurrentChannelLimit` mit Zeit, ein Log-Eintrag `TwitchJoinRejected` auf Error; nach TwitchLibs Timeout bleibt der
    Grund, keine zweite laute Zeile; dieselbe Zeile mit `msg-id` hinter anderen Tags wirkt gleich;
    (b) **JOIN ohne jede Antwort** → nach TwitchLibs Timeout `noConfirmation`, ein Log-Eintrag `TwitchJoinUnconfirmed` auf
    Warning, kein NOTICE beteiligt;
    (c) `msg_channel_suspended` → `channelSuspended`, Warning;
    (d) JOIN mit `366` → bestätigt, Stempel und Grund weg;
    (e) Trennung und neuer Handshake über den Transport: vorher bestätigter Kanal bekommt den neuen Sitzungsbeginn als
    Stempel, ein nie bestätigter behält seinen.
    Die Fälle mit Timeout warten auf TwitchLibs echten 5-s-Timer (≈ 7 s je Fall, im Kopf des Tests begründet; kein
    Uhren-Shim in TwitchLib möglich). Log-Prüfung über einen Logger-Fake wie in
    `tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvWriteClientLoggingTests.cs` (EventId-Name und Stufe, nie Twitchs Text).
  - Bestehende Worker-Tests unverändert grün, insbesondere `RedactingTwitchClientLoggerFactoryTests` und
    `TwitchLibRawLineRedactionTests` (beweisen, dass `LogUnaccountedFor` weiter greift).
- **Live (vor dem Commit):** eine Sonden-Zeile in `Channels` (erfundener, gültig geformter Login, der auf Twitch nicht
  existiert, z. B. `ep516probe` plus Ziffern; `IsBotActive = true`, ohne `TwitchChannelId`, Pflichtspalten nach Schema),
  `PUBLISH channel:bot:commands JOIN:<login>` → Worker-Log zeigt genau eine Zeile `TwitchJoinRejected`/`channelSuspended`
  **oder** `TwitchJoinUnconfirmed`/`noConfirmation` — **welcher Fall eintritt, ist ein Messergebnis und kommt ins
  Protokoll**; danach nur Debug-Wiederholungen. Der Identitätsabgleich deaktiviert die Sonde nicht (Befund 12, 7-Tage-Frist),
  ein Override seines Intervalls ist nicht nötig; seine Warning „Twitch kennt den Login … nicht" ist erwartet. Sonde bleibt
  für T4 stehen oder wird per SQL deaktiviert und `LEAVE:<login>` publiziert.
- **Fertig:** Build, Format, Worker.Tests grün, Live bestanden. Commit `feat(worker): record why a join is unconfirmed and
  log it once`.

### T4 — Roster-Vertrag erweitern, Publisher auf die atomare Erfassung

- **Lesen:** E3, 3.2; `IWorkerRosterReader.cs`, `WorkerRosterPublisher.cs`, `WorkerHealthReader.cs` (Roster-Teil),
  `WorkerRosterReaderTests.cs`, DECISIONS 2026-08-01 (Roster-Key), der Eintrag aus T3.
- **Liefert:** neue Felder an `WorkerRosterChannel` (am Ende, Default `null`) und `WorkerRosterSnapshot`
  (`IrcSessionStartedUtc`, Default `null`; Position nach Prüfung der Aufrufer, im Zweifel ans Ende); Publisher über
  `CaptureRoster()` (IRC-Seite gewinnt wie bisher, 7TV-only-Zeilen tragen `null`); **DECISIONS-Absatz** „roster contract"
  (Felder, atomare Erfassung, Mischbetrieb).
- **Grenzfälle:** Payload eines alten Workers liest sich mit `null`; Kanal nur auf der 7TV-Seite; `WhenWritingNull` lässt die
  Felder für bestätigte Kanäle weg; Kappung bei 500 unverändert.
- **Tests:** `WorkerRosterReaderTests` (Infrastructure, echte Redis): Rundreise mit allen neuen Feldern, alter Payload ohne
  sie. Publisher-Abbildung, falls es einen Publisher-Test gibt, dort; sonst über den Rundreise-Test.
- **Live (vor dem Commit):** Worker läuft mit Sonde aus T3: `redis-cli GET worker:roster` zeigt `ircSessionStartedUtc`, für
  die Sonde `ircUnconfirmedSinceUtc` und `ircJoinFailureReason`, für bestätigte Kanäle keine Zusatzfelder; nach einem
  `DEBUG:TWITCH-RECONNECT` hat die Sonde denselben Stempel wie vorher, `ircSessionStartedUtc` ist neu.
- **Fertig:** Build, Format, Worker.Tests und `dotnet test tests/EmotePurge.Infrastructure.Tests --filter WorkerRoster` grün,
  Live bestanden. Commit `feat: publish unconfirmed-since and the join failure reason in the worker roster`.

### T5 — Bewertung: Optionen, Policy, Dienst, Check mit Cache, Soll-Liste, `WorkerHealthStatus` verschieben

- **Lesen:** E4, E5 (Check, Cache, Verschiebung), E10, E11, 3.3; `ChannelService.cs:380-420` (Filter),
  `ChannelCapacityOptions.cs`, `ServiceCollectionExtensions.cs:70-120` (Registrierungen, `LogStatusReader`),
  `TtlCache.cs` und seine Tests, `LogStatusReader.cs`, `WorkerHealthStatus.cs` und seine beiden Nutzer, `IChannelService.cs`,
  `ChannelServiceCapacityTests.cs`, `ChannelServiceBroadcasterLockTests.cs`.
- **Liefert:** `ChannelJoinHealthOptions` (Bindung, `Validate`, Registrierung); Core-Typen aus 3.3;
  `ListActiveChannelActivationsAsync` mit `ListActiveChannelNamesAsync` als Projektion; `WorkerHealthStatus` nach
  `src/EmotePurge.Infrastructure/Health/` verschoben (Api-Nutzer angepasst, sonst nichts); `ChannelJoinHealthPolicy` (rein,
  inkl. `IsAlarming`); `ChannelJoinHealthService` (scoped: liest Health, Roster, Soll-Liste, ruft die Policy);
  `ChannelJoinHealthCheck` (Singleton, eigener Scope, `TtlCache` 5 s, Ausnahme → alarmierend + Warning). **DECISIONS-Absatz**
  „join health evaluation": Stufen, Grace und Obergrenze mit Herleitung, Ausnahme für `channelSuspended`/`banned` als
  vorläufige Betreiberentscheidung, Instanzwechsel, Cache, Verschiebung von `WorkerHealthStatus`.
- **Grenzfälle Policy:** jede Stufe aus E4 einzeln; Grace-Grenze genau und eine Sekunde darüber; **mehrere kurze Sitzungen:
  Stempel 31 min alt, Sitzung 30 s alt → überfällig über die Obergrenze, obwohl die Grace nicht abgelaufen ist; derselbe
  Kanal mit Stempel 20 min → nicht überfällig**; ein einzelner Reconnect im ungünstigsten Moment (Stempel `Grace − 1 s` vor
  dem Sitzungsbeginn, Bewertung `Grace` nach Sitzungsbeginn) → nicht überfällig; ohne Sitzung: Stempel älter als Obergrenze
  → `Failing`, jünger → `NoSession`; Stempel `null` ohne Sitzung → nie überfällig; Kanal vor Sitzungsbeginn unbestätigt →
  Grace ab Sitzungsbeginn; fehlender Kanal nach frischer Aktivierung → nicht überfällig; fehlender Kanal bei `Truncated`;
  Boot-Recovery innerhalb und jenseits von `BootRecoveryMax`; **Instanzwechsel** innerhalb 180 s → `Starting`, danach →
  `Inconsistent`, Health ohne Instanz → übersprungen; Roster-Kanal nicht im Soll zählt nicht; Groß-/Kleinschreibung;
  leere Soll-Liste → `Healthy`; alter Worker ohne Stempel bei Sitzung → Grace ab Sitzungsbeginn, keine Obergrenze;
  **Ausnahme:** überfälliger `channelSuspended` allein → `Healthy` mit `ExcludedCount = 1`, dazu ein zweiter Kanal
  `noConfirmation` überfällig → `Failing`; veralteter Grund ist Sache des Trackers (T1), hier nicht erneut.
  `IsAlarming`: Tabelle aus 3.3 vollständig.
- **Grenzfälle Check:** zwei gleichzeitige Aufrufe → eine Auswertung; innerhalb 5 s gecacht, danach neu (über
  `TimeProvider`-Fake); Auswertung wirft → alarmierend, nicht gecacht, nächster Aufruf wertet neu aus.
- **Grenzfälle Soll-Liste:** gesperrter und widersprochener Kanal fehlen in beiden Listen gleich; `TrackingResumedAt` gesetzt
  vs. `null`; beide Methoden liefern dieselben Namen in derselben Reihenfolge.
- **Tests:** `tests/EmotePurge.Infrastructure.Tests/Unit/ChannelJoinHealthPolicyTests.cs`,
  `Unit/ChannelJoinHealthOptionsTests.cs`, `Unit/WorkerHealthStatusTests.cs` (die drei Status-Ableitungen, jetzt
  Infrastructure-Logik nach Regel 11), `Unit/ChannelJoinHealthCheckTests.cs` (substituierter Dienst über Scope),
  `Integration/ChannelServiceActiveActivationsTests.cs` (Postgres), `Integration/ChannelJoinHealthServiceTests.cs` (Postgres,
  substituierte Reader).
- **Live:** in T6 (E12) — vor jedem Push.
- **Fertig:** Build, Format, Infrastructure.Tests der neuen Klassen, die bestehenden `ChannelService*`-Tests und
  `dotnet test tests/EmotePurge.Api.Tests` (Verschiebung) grün. Commit `feat: evaluate desired against confirmed joins with
  a grace period and an upper bound`.

### T6 — Api: `/api/health/channels`, eigene Policy, Admin-Roster, `TwitchJoinBudgetChannels` entfernen

- **Lesen:** E5, E6 (Api-Teil), E7 (Api-Teil), E11, 3.3; `WorkerHealthEndpoints.cs`, `AdminEndpoints.cs:245-315` und
  `:537-570`, `WorkerCapacity.cs`, `Program.cs:183-230` (Policies), `RateLimitingOptions.cs`, `RateLimitPolicyNames.cs`,
  `RateLimitRejection.cs:270-290`, `appsettings.json` (RateLimiting), `ApiFactory.cs`, `ApiHealthEndpointTests.cs`,
  `AdminDashboardActivityEndpointTests.cs`, `AdminRateLimitsEndpointTests.cs`, `RateLimitRejectionTests.cs:400-445`,
  `docs/Architectur.md:179`.
- **Liefert:** Route nach E5 (Handler bildet `IsAlarmingAsync` auf 200/503 ab, sonst nichts); Policy `PublicChannelHealth`
  (Name, Option mit Default 10, Registrierung neben `PublicHealth` mit Kommentar, `appsettings.json`, Eintrag in der
  Rate-Limit-Übersicht); `/api/admin/roster` nach 3.3 über `IChannelJoinHealthService` (eine Roster-Lesung, der Bericht
  trägt den Schnappschuss); `TwitchJoinBudgetChannels` gelöscht, Doc-Kommentar von `TwitchConcurrentChannelLimit` berichtigt;
  `Architectur.md:179` ergänzt (englisch); **DECISIONS-Absatz** „endpoint contract" (Route, 200/503-Tabelle, Prinzip, eigene
  Policy und warum nicht geteilt, nicht im HEALTHCHECK).
- **Grenzfälle:** anonym ohne Cookie; Körper leer; jede Zeile der Tabelle 3.3; `/api/health` bleibt bei überfälligem Kanal
  200; **Budget getrennt: `PublicChannelHealth` (per Override klein) erschöpft → `/api/health/channels` 429, `/api/health`
  aus derselben IP weiter mit seinem regulären Status (200 bzw. 503 nach Health)**, und umgekehrt; `/api/admin/roster` ohne
  Schnappschuss → `joinHealth.status = rosterUnavailable`; Status als String.
- **Tests (Api.Tests):** `ApiChannelHealthEndpointTests.cs` — der öffentliche Vertrag: Statuscodes und leerer Körper gegen
  einen substituierten `IChannelJoinHealthCheck` (die Statuslogik selbst ist in T5 getestet, Regel 11), plus ein Fall mit
  dem echten Check über substituierte Reader, der die Verdrahtung beweist; `ApiHealthEndpointTests` um den Regressionsfall;
  Roster-Formtest in `AdminDashboardActivityEndpointTests`; `RateLimitRejectionTests` um den Fall „getrennte Budgets" nach
  dem Muster `PublicLegalBudget_ExhaustsIndependently_…`; `AdminRateLimitsEndpointTests` um die neue Policy. Kein neuer
  Filter, keine Filter-Reihenfolge berührt.
- **Live (vor dem Commit, deckt T5 mit):** Api und Worker aus dem Worktree, Sonde aus T3 vorhanden.
  1. `curl -s -o /dev/null -w '%{http_code}' localhost:5151/api/health/channels` und `/api/health` → je nach Sondengrund
     (Protokoll T3): `channelSuspended` → beide 200, `/api/admin/roster` zeigt die Sonde mit `countsForAlarm = false`;
     `noConfirmation` → nach Grace plus Roster-Takt 503, `/api/health` 200.
  2. `redis-cli DEL worker:roster` → sofort 503 (Cache ≤ 5 s), nach dem nächsten Publish wieder wie vorher.
  3. Instanzwechsel: Roster lesen, `workerInstanceId` mit `jq` ändern, mit `SET … EX 180` zurückschreiben → 503 (Prozess
     älter als 180 s), nach dem nächsten Publish wie vorher.
  4. Budget: elf Abrufe von `/api/health/channels` binnen einer Minute → der elfte 429; `/api/health` direkt danach 200.
  5. Sonde aufräumen: in `/admin/channels` „Verlassen" (der in E11 dokumentierte Abhilfeweg) → Audit-Eintrag
     `channel.leave`, Sonde verschwindet nach dem nächsten Roster-Takt aus `ircUnjoined`, Endpunkt 200; Zeile löschen.
- **Fertig:** Build, Format, `dotnet test tests/EmotePurge.Api.Tests` grün, Live bestanden. Commit `feat(api): add GET
  /api/health/channels and report unjoined channels in the admin roster`.

### T7 — Teil B Backend: `ChannelCapacityReached` als EventId

- **Lesen:** E8; `ChannelService.cs:819-850`, `ChannelServiceCapacityTests.cs`, `SevenTvWriteClientLoggingTests.cs` (Muster
  Logger-Fake), `LogFilterParser.cs:150-175`.
- **Liefert:** EventId mit Namen `ChannelCapacityReached` an der bestehenden Warning (Text und Platzhalter unverändert; Nummer
  als Konstante in der Klasse); Kommentar von `ChannelCapacityOptions.MaxActiveChannels` angepasst; **DECISIONS-Absatz**
  „capacity demand signal" (E8, E9 inkl. zweiteiligem Rückweg).
- **Grenzfälle:** Ablehnung loggt genau einmal mit dem EventId-Namen; Admin-Join über der Decke und idempotenter Join auf
  aktivem Kanal loggen nichts.
- **Tests:** `ChannelServiceCapacityTests` um die Logzeile (EventId-Name und Platzhalterwerte).
- **Live (vor dem Commit):** Api mit `Channels__MaxActiveChannels=<aktuelle aktive Zahl>`; Join eines neuen Kanals mit einem
  **Nicht-Admin**-Login (Testkonto `olaf_olaf_son` auf seinem eigenen Kanal) → 409 `channel_capacity_reached`, eine Warning
  mit EventId `ChannelCapacityReached`; mit `LOG_CAPTURE_ENABLED` findet `/admin/logs?q=event:ChannelCapacityReached&range=7d`
  sie. Ist kein Nicht-Admin-Login verfügbar, im Protokoll vermerken; der Test deckt die Zeile.
- **Fertig:** Build, Format, Capacity-Tests grün, Live bestanden. Commit `feat: tag active-channel cap rejections with a
  stable event id`.

### T8 — Live-Gate mit Messung (Regel 16), Protokoll abschließen

Läuft lokal gegen echtes Twitch (anonym), echtes Postgres und Redis, mit **Produktions-Defaults** (Grace 360 s, Obergrenze
1800 s, Resync 60 s; keine Overrides außer dem Reconnect-Trigger und erhöhten Log-Stufen für `EmotePurge.Worker.
TwitchChatManager` und `TwitchLib.Client`, damit Sende- und Bestätigungszeitpunkte im Log stehen — welche Stufe nötig ist,
prüft der Implementer an einem Probelauf). Dauer ≈ 1,5 h. Ergebnisse in Abschnitt „T8" des Protokolls.

1. **Aufbau und Kanalzahl:** Dev-Datenbank auf **mindestens 65 aktive, echte Kanäle** bringen (Ziel 120): fehlende per
   Admin-Join mit Admin-Session (globale Admins sind von der Decke ausgenommen; `Bookkeeping` 120/min beachten, also
   gestaffelt), Logins aus der Helix-Liste aktueller Streams (`GET helix/streams`, App-Token der Dev-Zugangsdaten). Die
   hinzugefügten Kanäle als Liste lokal (scratchpad, **nicht** ins Repo) festhalten, um sie in Schritt 9 wieder zu verlassen.
   **Abbruchkriterium:** wiederholte 7TV-429 im Worker-Log → Kanalzahl senken, im Protokoll vermerken.
2. **Boot ohne Fehlalarm:** Worker neu starten; ab Start alle 5 s `/api/health/channels` bis 2 min nach Ende der
   Boot-Recovery → durchgehend 200; Boot-Recovery-Dauer festhalten.
3. **Ein Reconnect ohne Fehlalarm, Messung gesund:** `DEBUG:TWITCH-RECONNECT`; 10 min alle 5 s pollen → durchgehend 200.
   Aus den Logs: Zeit vom Sitzungsbeginn bis zur letzten Bestätigung (Rejoin-Dauer), Verteilung der Abstände zwischen
   TwitchLib-Senden und Bestätigung.
4. **Sonden:** drei Sonden-Zeilen wie in T3 anlegen und joinen. Festhalten: Grund (NOTICE oder still); Abstand zwischen dem
   Senden eines Sonden-JOIN und dem nächsten gesendeten JOIN (misst die Warteschlangen-Sperre aus Befund 6); Endpunkt
   gemäß Grund (E11).
5. **Reconnect mit Sonden, Messung mit Ablehnungen:** `DEBUG:TWITCH-RECONNECT`; Rejoin-Dauer bis zur letzten Bestätigung
   eines gesunden Kanals, Vergleich mit der Formel aus E4 (`max(0,6 s × N, …) + 7 s × K`). Sind die Sonden still abgelehnt,
   ist das die Messung des 7-s-Terms; antwortet Twitch mit NOTICE, bleibt der Term gerechnet und das Protokoll sagt das.
   Doppel-Einreihungen (zwei Sende-Zeilen für denselben Kanal ohne Timeout dazwischen) zählen und als Befund melden.
6. **Mehrere kurze Reconnects:** vier `DEBUG:TWITCH-RECONNECT` im Abstand von je 3 min (kürzer als die Grace). Erwartet: für
   gesunde Kanäle durchgehend 200; die Stempel der Sonden ändern sich über alle Sitzungen nicht (`GET worker:roster` vor
   und nach jedem Reconnect). Sind die Sonden still abgelehnt, die Reconnects alle 3 min fortsetzen, bis der älteste Stempel
   30 min alt ist → `/api/health/channels` 503, `/api/health` 200 — das ist die Live-Prüfung der Obergrenze. Sonst (NOTICE)
   bleibt der 503 durch T5/T6 belegt.
7. **Boot mit Sonden:** Worker neu starten → während der Boot-Recovery 200, danach gemäß Grund.
8. **Admin-Karte (nur Sichtprüfung des Vertrags):** `GET /api/admin/roster` zeigt `joinHealth`, `ircUnjoined` mit Gründen,
   `countsForAlarm`, Totale. (Die Karte selbst prüft T9.)
9. **Aufräumen:** Sonden über „Verlassen" in `/admin/channels`, Zeilen löschen; die in Schritt 1 hinzugefügten Kanäle wieder
   verlassen; Endpunkt 200.
10. **Nicht live prüfbar** und so benannt: der echte Limit-NOTICE an der anonymen Verbindung — belegt durch die
    aufgezeichnete Zeile im Fake-Transport-Test (T3).

- **Fertig:** Protokoll mit allen Messwerten (Boot-Recovery, Rejoin-Dauer gesund und mit K Sonden, Sendeabstände,
  Doppel-Einreihungen ja/nein, Sondengrund) committet (`docs: add live verification protocol for #516`); jeder Schritt
  bestanden oder als Befund mit Folge benannt. Übersteigt die gemessene Erholung `Grace − 60 s`, entscheidet der Orchestrator
  vor T9 über die Anhebung des Defaults (E4).

### T9 — Frontend: Kapazitätszeile, Unjoined-Liste, Status aus `joinHealth`, Altern, Detailseite, Mocks

- **Lesen:** E6, E7, E8, 3.4; `admin-roster-card.ts` + Spec, `utilization-tone.ts` + Spec, `poll-while-visible.ts`,
  `admin.model.ts:110-180`, `admin.service.spec.ts:130-170`, `admin-monitoring-page.template.spec.ts:140-160`,
  `admin-channel-detail-page.ts:200-320`, `admin-log-errors-row.ts:80-90`, `core/channels/backfill-window.ts`
  (`formatInstant`), beide Locale-Dateien, `web/e2e/support/mocks.ts:185-230`, `web/e2e/admin-monitoring.e2e.spec.ts`,
  `web/e2e/audit/ui-audit.audit.ts:1245-1265`; vor jeder UI-Arbeit `docs/UI-Designsprache.md` §11 und `DESIGN.md`.
- **Liefert:** Modelländerungen; `utilizationTone` mit optionalen Schwellen; Roster-Karte: Kapazitätsblock (Balken gegen
  `maxActiveChannels`, 90/100, drei Textstufen, Twitch-Referenzhinweis, Log-Link), Unjoined-Liste nach E6 (inkl.
  Ausgenommen-Zeilen mit Leave-Hinweis), Status aus `joinHealth.status` mit Vorrang nach E6, sichtbares Altern mit
  einmaligem Nachladen; Detailseite mit Grund und „seit"; Locale-Keys nach 3.4; Mocks und Audit auf den neuen Vertrag;
  **DECISIONS-Absatz** „capacity display and roster card" (E7-Schwellen, Statusvorrang, Altern).
- **Grenzfälle:** `maxActiveChannels` 0 oder fehlend → kein Ton, kein Warntext; genau 90 % → `warning`; genau 100 % →
  `danger` mit „Grenze erreicht"; über 100 % → „über der Grenze"; Balkenbreite gekappt; unbekannter Grundcode → generischer
  Text; Zeile ohne Grund; Stempel `null` → „wartet auf Twitch-Sitzung"; fehlender Kanal mit „erwartet seit"; Liste gekappt mit
  Total > Länge; `bootRecoveryCompleted = false` bei `joinHealth.status = failing` → `failing`; Stand erreicht 90 s ohne
  Ereignis → genau ein Nachladen, auch wenn er danach weiter altert; neues Ereignis setzt zurück; Alter > 180 s → `stale`,
  auch ohne neuen Abruf; verborgene Seite tickt nicht.
- **Tests (Vitest, Regel 12 — Verhalten und Zugänglichkeit, keine Vorlage):** `utilization-tone.spec.ts` (Default
  unverändert, eigene Schwellen, Grenzwerte); `admin-roster-card.spec.ts` mit **kontrollierter Uhr** (Vitest-Fake-Timer und
  gesteuerter Sichtbarkeit): Altern bis `stale` ohne Ereignis, genau ein `getRoster`-Aufruf zusätzlich nach 90 s,
  Zurücksetzen nach Ereignis; Statusvorrang inkl. `failing` vor `starting`; Kapazitäts-Computeds; Unjoined-Sortierung und
  -Texte als Meldungsreihenfolge (Vertrag E6); zugänglicher Name und `aria-valuemax` des Balkens; Statusmeldung als Text
  (nicht nur Ton). `admin.service.spec.ts` und `admin-monitoring-page.template.spec.ts` auf den neuen Vertrag. **E2E:**
  `mockAdminRoster` mit `ircUnjoined`, `joinHealth` und `maxActiveChannels`-Override; in `admin-monitoring.e2e.spec.ts` drei
  Fälle — überfälliger Kanal zeigt Namen, Grund und Status „fehlt"; ausgenommener Kanal zeigt „zählt nicht für den Alarm";
  90-%-Auslastung zeigt die Warnzeile.
- **Live (UI, nach Vitest und E2E):** E2E läuft **ohne Api auf `:5151`**; danach kurz Api und Worker starten, eine Sonde wie
  in T3 anlegen, `/admin/monitoring` im Browser: Sonde mit Grund und Text, Status gemäß Grund; Worker stoppen → Karte altert
  sichtbar und zeigt nach 180 s `stale`, ohne Neuladen der Seite. Aufräumen wie T6 Schritt 5, Api beenden. Ergebnis als
  Abschnitt „T9" ins Protokoll.
- **Fertig:** Vitest, Lint, `format:check`, `npm --prefix web run e2e` grün, Live bestanden. Commit `feat(web): grade active
  channels against our own cap and list unjoined channels`.

### T10 — Doku-Abschluss

- **Lesen:** E4, E5, E8, E9, E10, E11; das Protokoll aus T2–T9; `docs/Operations.md` (Aufbau, „Running behind a reverse
  proxy", „Log viewer"), `CLAUDE.md` („Umsetzungsstand" Zeile Admin, „Bekannte offene Grenzen" erster Punkt), der
  DECISIONS-Eintrag.
- **Liefert (englisch in Operations.md, deutsch in CLAUDE.md):**
  - Operations.md, neuer Abschnitt **„Channel join health"** nach „Admin dashboard": was `/api/health/channels` prüft,
    Tabelle 3.3, Prinzip „never alarms for what `/api/health` already reports"; **zweiter Uptime-Monitor** (HTTP-Monitor auf
    `https://<host>/api/health/channels`, 60 s, „Retries" 2, eigene Benachrichtigung, 200 = „up"); **nicht** in einen
    HEALTHCHECK; Konfiguration (E10) mit Grace-Formel inkl. `7 s × K` und **den in T8 gemessenen Werten**, Hinweis auf
    `SevenTv:ResyncIntervalSeconds`; Obergrenze und wofür sie da ist; **gesperrte/gelöschte Kanäle**: zählen nicht für den
    Alarm (E11), wie der Identitätsabgleich sie tatsächlich behandelt (gespeicherte ID: nie deaktiviert; Login ohne ID: nach 7
    Tagen), Abhilfe „Leave" in `/admin/channels`, und dass ein dauerhaft roter `noConfirmation`-Kanal denselben Weg braucht,
    weil ein roter Monitor weitere Ausfälle verdeckt; Log-Filter `event:TwitchJoinRejected`, `event:TwitchJoinUnconfirmed`,
    `event:ChannelCapacityReached`.
  - Operations.md, Unterabschnitt **„Raising the active-channel cap"**: Voraussetzungen ([F2]), Setzen, Nachprüfung gegen
    die T8-Messwerte; **Rückweg in zwei Teilen nach E9** (Aufnahme stoppen; Erfassung wiederherstellen per Leave, dann Soll
    gegen bestätigte Joins prüfen).
  - `CLAUDE.md`: „Bekannte offene Grenzen" — der Satz „der Worker erkennt und meldet eine solche Ablehnung bisher nicht"
    wird durch den neuen Stand ersetzt; Admin-Zeile im Umsetzungsstand um einen Halbsatz (Join-Health in der Roster-Karte,
    `/api/health/channels` als zweiter Kuma-Monitor).
  - DECISIONS-Eintrag auf Vollständigkeit der `**Betrifft:**`-Zeile prüfen; historische Doku bleibt unverändert, der neue
    Eintrag sagt, was er überholt.
- Die eigene Kuma-Einrichtung ist Betreiberhandgriff und gehört nach `infra-docs`.
- **Fertig:** relative Links und Abschnittsanker geprüft. Commit `docs: document channel join health and raising the
  channel cap`.

**Reihenfolge:** T1 → T2 → T3 → T4 → T5 → T6 → T7 → **T8 (Live-Gate)** → T9 → T10 → Gates (Abschnitt 6). T7 hängt an keinem
anderen Task und dürfte früher laufen; der Einfachheit halber bleibt die Reihe strikt.

---

## 5. Abdeckung der Akzeptanzkriterien

| AK (Issue) | Task |
|---|---|
| A1 „seit wann unbestätigt" + Grund im Roster | T1, T2, T3, T4 |
| A2 NOTICE geloggt, Erkennung ohne NOTICE | T3 (Gründe, Dedupe, Fake-Transport a/b), T5 (Erkennung über fehlende Bestätigung), T3/T8 live |
| A3 Gnadenfrist, kein Fehlalarm bei Boot und Reconnect | T1, T5, T8 Schritte 2, 3, 6, 7 |
| A4 `/admin/monitoring` listet mit Grund und seit | T6, T9 |
| A5 neuer payloadfreier Endpunkt, `/api/health` unverändert | T5, T6 |
| A6 Tests + Live | T1–T9, Live je Task, T8 |
| A7 Operations.md + DECISIONS | T3–T7, T9, T10 |
| B1 Nenner `MaxActiveChannels`, 100 nur Referenz | T6, T9 |
| B2 `TwitchJoinBudgetChannels` und Kommentare entfernt | T6, T7, T9 |
| B3 Frühwarnung 90 %, Alarmentscheidung festgehalten | T9, T7 (DECISIONS) |
| B4 Ablehnungen geloggt und gezählt | T7, T9 (Link) |
| B5 Anhebung auf 120 nach A live | T10 (Runbook mit zweiteiligem Rückweg); die Umsetzung ist Betreiberhandgriff nach dem Deploy |

---

## 6. Gates (vor dem PR)

1. `dotnet test EmotePurge.slnx` grün (braucht laufendes Docker, Testcontainers).
2. `npm --prefix web test -- --watch=false` grün.
3. `npm --prefix web run e2e` grün — **nur ohne Api auf `:5151`**.
4. `node scripts/coverage-local.mjs` — neue Dateien (Tracker, Parser, Policy, Dienst, Check, Endpunkt) sollen deutlich über
   80 % liegen; das Ergebnis ist eine Näherung, kein Urteil.
5. `dotnet format EmotePurge.slnx --verify-no-changes`, `npm --prefix web run format:check`, `npm --prefix web run lint`.
6. Protokoll `docs/nachtlaeufe/live-516-<datum>.md` vollständig (Abschnitte T2–T9).
7. Danach (Orchestrator, nicht Teil dieses Plans): Code-Review und `/codex:review --model gpt-6.2-sol` über den Branch.

**Deploy:** keine Migration; Api und Worker gemeinsam; danach zweiter Kuma-Monitor einrichten; Anhebung der Decke erst nach
[F2].

---

## 7. Konfliktrisiken mit dem parallelen Branch `feat/487-wish-bot-runner` (Worktree 487, PR 2 des Bot-Plans)

Gegenüber Revision 1 neu: Dieser Plan **berührt jetzt `Program.cs` der Api, `RateLimitingOptions`, `RateLimitPolicyNames`
und `appsettings.json`** (Befund 4 der Prüfung verlangt eine eigene Policy) und verschiebt `WorkerHealthStatus`.
`WorkerServiceRegistration.cs`, `Program.cs` des Workers und `WorkerHealthPublisher` bleiben unberührt.

| Datei | Risiko | Umgang |
|---|---|---|
| `docs/DECISIONS.md` (Kopf) | sicher textuell | beim Rebase beide Einträge behalten, nach Datum ordnen |
| `CLAUDE.md` Umsetzungsstand | wahrscheinlich: verschiedene Zeilen | von Hand zusammenführen |
| `docs/Operations.md` | gering: getrennte Abschnitte | — |
| `web/public/i18n/{de,en}.json` | gering bis mittel | wir ändern nur `admin.roster.*` |
| `TwitchChatManager.cs`, `ITwitchChatManager.cs` | gering (Bot-Plan nennt die Klasse nur als Vorbild) | unser Umbau ersetzt alle `_desiredChannels`-Stellen — wer zuletzt merged, rebased |
| `src/EmotePurge.Api/Program.cs`, `RateLimitingOptions.cs`, `RateLimitPolicyNames.cs`, `appsettings.json`, Rate-Limit-Übersicht in `AdminEndpoints.cs` | **mittel, erst mit PR 3** des Bot-Plans (Policy `PublicBotAccountHealth`) | gleiche Stellen, gleiches Muster; wer zweiter merged, fügt seine Policy neben die andere |
| `WorkerHealthEndpoints.cs`, `AdminEndpoints.cs`, `WorkerHealthStatus` (verschoben) | **mittel, PR 3** (`/api/health/bot-account`, Admin-Routen) | PR 3 nutzt `WorkerHealthStatus` dann aus der Infrastructure; bei PR 3 bewusst prüfen, ob sein Alarm-Endpunkt ebenfalls „nur melden, was `/api/health` nicht meldet" will und ein eigenes Budget braucht |
| `tests/EmotePurge.Api.Tests/ApiFactory.cs`, `RateLimitRejectionTests.cs` | gering | — |

---

## 8. Offene Fragen an den Betreiber

- **[F1] erledigt (vorläufig):** gesperrte und gelöschte Kanäle lösen keinen 503 aus (E11). Wer das überstimmt, ändert nur
  Stufe 5 der Policy.
- **[F2] Wie lange soll Teil A auf Prod ohne Fehlalarm laufen, bevor die Decke auf 120 geht?** Vorschlag: 7 Tage, mindestens
  ein Deploy und ein echter Reconnect darin.
- **[F3] Grace 360 s** passt zum heutigen Resync-Takt (60 s). Ist geplant, `SevenTv:ResyncIntervalSeconds` auf Prod zu
  strecken? Dann müssen `Channels__JoinHealth__GraceSeconds` und, falls nötig, `Channels__JoinHealth__MaxUnconfirmedSeconds`
  im selben Schritt mit (Formel und Validierung E4/E10).
