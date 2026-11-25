# Plan #516: Join-Fehler erkennen und melden, danach die Kanaldecke anheben

Erstellt am 2026-11-25 im Worktree `EmotePurge-wt-516` (Branch `feat/516-join-failure-detection`, Basis `origin/main` @
`e1d6210f`, PR #515 gemergt). Alle Codestellen sind an diesem Stand gelesen.
**Status: Revision 4, gezielter Nachtrag zur dritten Prüfung (Sol, 4 high). Rev. 3 hatte den Entwurf vereinfacht (eine
Anrechnungsuhr je Kanal, Bewertung über das Roster); Rev. 4 ergänzt einen begrenzten Datenbankabgleich, bindet Versuch und
Bestätigung an die Leitung und führt eine Schema-Marke im Roster ein. Nichts umgesetzt.**

Quellen: Issue #516 (Teil A, Teil B, Akzeptanzkriterien), DECISIONS 2026-11-25 („Twitch channel capacity …"), 2026-09-23
(Kanaldecke 80), 2026-09-16 (Forwarded-Headers, `PublicHealth` je IP), 2026-08-05 (payloadfreier `/api/health`, Kuma),
2026-08-02 (Schwellen-Leiter, keine Kapazitätsstufe im Roster-Badge), 2026-08-01 (Roster-Key), `CLAUDE.md` (Regeln 3, 4, 5,
7, 11, 12, 16, 19, Sprache), `web/.claude/CLAUDE.md`, JOIN-Sonde vom 2026-09-08
(`~/projects/emotepurge-joinprobe/messungen-2026-09-08/`), TwitchLib.Client 4.0.1 und TwitchLib.Communication 2.0.1,
dekompiliert (`ilspycmd`).

**Was dieser Plan trägt:** Befund, Entscheidungen, Verträge, Tasks mit Grenzfällen, Tests, Live-Prüfung, Gates. **Kein
Code**; Namen und Signaturen nur als Form. Jeder Implementer liest seinen Task, Abschnitt 2 und 3 und die genannten
Bestandsdateien, bevor er schreibt.

**Lesehinweis:** **[E]** Planer-Entscheidung · **[B]** Betreiberentscheidung · **[P]** Präzisierung.

---

## 0. Prüfbefunde und ihre Auflösung (Rev. 3 und 4)

R1 = erste Prüfung (Rev. 1), R2 = zweite (Rev. 2), R3 = dritte (Rev. 3, aufgelöst in Rev. 4). Jeder Befund ist am Code
nachgeprüft.

| # | Befund (Kurzform) | Auflösung |
|---|---|---|
| R1-1 | Kurze Reconnects setzen die Frist immer neu | Anrechnungsuhr (E1): nur eine Bestätigung setzt auf null, Trennungen frieren ein |
| R1-2 | Roster und Sitzung nicht atomar, Instanzwechsel unbemerkt | Uhr, Wunsch und Verbindungszustand leben in **einem** Riegel (Tracker); kein Abgleich zweier Keys mehr (E4) |
| R1-3 | Gelöschter Kanal hält den Alarm dauerhaft | `channelSuspended`/`banned` alarmieren nicht (E11); Abhilfe Leave; Kuma-Erinnerung stündlich (E11) |
| R1-4 | Kanalcheck teilt das Budget von `/api/health` | eigene Policy `PublicChannelHealth`, 5-s-Ergebnis mit eigener Füllfrist (E5) |
| R1-5 | Präfixfilter verwirft NOTICEs; Test umgeht den Empfangspfad | Tag-Block reihenfolgeunabhängig; Fake-Transport treibt den echten Empfangspfad (T3) |
| R1-6 | Admin-Karte bleibt grün bei hängendem Publisher; `starting` verdeckt `failing` | sichtbarkeitsgebundener Minutentakt; `failing` vor `starting` (E6) |
| R1-7 | Grace unterschätzt TwitchLibs JOIN-Warteschlange | Herleitung mit `7 s × K` und Übertrag über einen Reconnect; Messung bei 120 Kanälen (E4, T8) |
| R1-8 | Variable allein stellt die Erfassung beim Rückweg nicht wieder her | zweiteiliger Rückweg (E9) |
| R2-1 | Beide Monitore grün über die Obergrenze hinaus (Health-Stichprobe, „200 wenn nicht connected") | Ausnahme „nicht connected → 200" gestrichen; ein überfälliger Kanal bleibt während Trennungen überfällig (Uhr eingefroren) → 503 durchgehend. Doppelalarm bei Totalausfall akzeptiert (E5) |
| R2-2 | Stempel macht aus langem Ausfall einen Sofortalarm; Race `_intentGate` vs. `_clientStateGate` | getrennte Zeit zählt nie; Wunsch-Einfügen und Verbindungswechsel serialisieren am Tracker-Riegel (E1) |
| R2-3 | 15-s-Fenster macht aus einer Sperre einen stillen Fehler (und umgekehrt) | Fenster gestrichen; Unterscheidung über `Details` des Callbacks; ein Grund weicht nur dem Ausgang eines **neueren Versuchs** (E2) |
| R2-4 | Stiller gelöschter Kanal verbraucht den einzigen Alarm | Kuma „Resend Notification" stündlich während DOWN [B]; Karte listet jeden überfälligen Kanal; Testfall „still + Limit" (E11, T3, T5, T9) |
| R2-5 | Instanz-Toleranz deckt nur eine Publish-Reihenfolge | Instanzvergleich, `WorkerStatus`, Verschiebung von `WorkerHealthStatus` gestrichen; nur Roster und Frische; Kaltstartfenster (E4) |
| R2-6 | 7TV-Zeile bekommt die falsche Uhr | neues Feld `ircIntent`; bewertet werden nur Zeilen mit IRC-Wunsch (E3, E4) |
| R2-7 | Single-Flight begrenzt bei Fehlern nichts | Füllung mit eigener Frist, „nicht auswertbar" 5 s gecacht, eine Logzeile je Füllung (E5) |
| R2-8 | Live-Gate überschreitet sein Limit, stiller Fall kann fehlen | Abfrage alle 10 s (6/min); stiller JOIN kontrolliert über die Transport-Naht im Live-Worker; Zeitmessung bei Ziel 120 (T8) |
| R2-9 | Runbook: Decke erreicht den Api-Container nicht | Mapping `Channels__MaxActiveChannels` in die Compose-Dateien (T7), Container neu erstellen, `ceilings.maxActiveChannels` prüfen (E9) |
| R2-NS | „Next steps": T3(d) falsch; Einmal-Nachladen; Detailseite, `reasonAtUtc` | T3 prüft JOIN-Echo und `366` getrennt; Minutentakt; Detailseite und `reasonAtUtc` zurückgestellt (E6) |
| R3-H1 | Frisches Roster ohne den aktiven Kanal bleibt grün (verlorenes `JOIN:`, Worker-Abfrage scheitert dauerhaft) | begrenzter Datenbankabgleich in der Policy: aktiver Kanal ohne IRC-Wunsch ist überfällig, sobald `max(Aktivierung, Worker-Start) + Grace` vorbei ist; eine Regel, kein Bootfenster (E4, Befund 19) |
| R3-H2 | Doppelter `TryJoinAsync` zwischen Bann-NOTICE und Timeout macht aus `banned` ein `noConfirmation` | ein Versuch beginnt erst mit dem JOIN **auf der Leitung** (Transport-Naht sieht ihn, Befund 3); ein von TwitchLib verworfener Doppel-JOIN berührt den Tracker nicht (E1, E2, T3) |
| R3-H3 | Spätes JOIN-Echo bestätigt einen neuen Wunsch nach Leave und Reaktivierung | Echo bestätigt nur bei offenem Versuch; eigenes PART schließt ihn; eigenes PART-Echo für einen gewünschten, bestätigten Kanal hebt die Bestätigung auf (Uhr 0, Retry möglich) (E1, T3) |
| R3-H4 | Alter Worker neben neuer Api hält den Kanal-Monitor dauerhaft grün | Schema-Marke `schemaVersion` am Roster (auch leer); fehlt sie oder ist sie älter → „nicht auswertbar" → 503; Kuma-Monitor erst nach beiden Images (E3, E4, T10) |

Gestrichen gegenüber Rev. 2: Stempel-Regel, Sitzungsbeginn im Vertrag, `MaxUnconfirmedSeconds`, `BootRecoveryMaxSeconds`,
Vorrangfenster, Instanzvergleich, `CaptureRoster()`, Verschiebung von `WorkerHealthStatus`, Status `inconsistent`/`noSession`,
Grundzeit im Vertrag, Detailseite. Die Soll-Liste aus der Datenbank kehrt in Rev. 4 **nur** als Abgleich „aktiv, aber ohne
Wunsch" zurück (E4), ohne Instanz- oder Sitzungsbezug.

---

## 1. Befund am Code

1. **Wunsch statt Erfolg.** `TwitchChatManager._desiredChannels` (`ConcurrentDictionary<string, bool>`, `:83`) ist die
   einzige Join-Buchführung. Einfügen und Entfernen unter `_intentGate` (`JoinChannelAsync :206-212`, `EnsureJoinedAsync
   :230-250`, `LeaveChannelAsync :262-266`); Bestätigung in `OnJoinedChannel` unter `_clientStateGate` (`:1000-1016`);
   `MarkAllChannelsUnconfirmed` (`:1168`) beim Wiederaufbau (`:348-351`) und in `OnDisconnected` (`:911-912`), ebenfalls
   unter `_clientStateGate`. Verbindungswechsel laufen über `SetConnected` (`:349`, `:864`, `:911`, `:978`), jedes Mal unter
   `_clientStateGate`. Kein Zeitbegriff, kein Grund.
2. **Ein Sendeort.** Jeder JOIN geht durch `TryJoinAsync` → nach der 600-ms-Drossel `_client.JoinChannelAsync` (`:558`).
3. **TwitchLib 4.0.1, Join-Warteschlange (dekompiliert):** `JoinChannelAsync` reiht ein; ein JOIN geht erst raus, wenn der
   vorige abgeschlossen ist. `366` (`Handle366`) schiebt weiter. Ohne Antwort verwirft der 1-s-Timer (`JoinChannelTimeout`)
   den Eintrag ab > 5 s, und erst der **nächste** Tick sieht die leere Liste und sendet weiter — **≈ 6–7 s je stille
   Ablehnung**. Die Warteschlange sendet per `_client.SendAsync` direkt; `OnSendReceiveData` sieht ausgehende JOINs nicht
   (nur `SendRawAsync` meldet `Send`). **Der Transport (`IClient.SendAsync`) sieht dagegen jeden JOIN
   (`QueueingJoinCheckAsync`) und jeden PART (`LeaveChannelAsync`) auf der Leitung.** `JoinChannelAsync` verwirft einen JOIN
   still, solange der Kanal in `JoinedChannels` steht — dort steht er vom Senden bis zu Timeout, Suspended-NOTICE oder
   eigenem PART (auch nach einer Bestätigung); ein noch nicht gesendeter Kanal kann doppelt eingereiht werden und geht dann
   zweimal auf die Leitung. `msg_banned` entfernt nichts; der Timeout desselben JOINs folgt.
4. **`OnFailureToReceiveJoinConfirmation`** hat zwei Quellen: Timer-Ablauf mit `FailureToReceiveJoinConfirmationException(
   channel)` → `Details = null`; `msg_channel_suspended` (`HandleChannelSuspended`) entfernt aus `_awaitingJoins`, wartet
   auf den nächsten Warteschlangen-Send und feuert dann mit `Details` = Twitchs Text. Unser Handler (`:920-931`) loggt nur,
   ohne Identitätsprüfung.
5. **Bestätigung:** `HandleJoin` feuert `OnJoinedChannel` auf das **eigene JOIN-Echo** (`ircMessage.User` = eigener
   Benutzername); `366` bestätigt nichts, sondern schiebt nur die Warteschlange.
6. **`msg_concurrent_channel_limit_reached`** hat keinen Zweig in TwitchLib (Fallback `OnUnaccountedFor` bzw.
   `LogUnaccountedFor`, redigiert über #246); danach folgt der Timeout desselben Kanals. Die Sonde vom 2026-09-08 hat die
   Zeile aufgezeichnet. `msg_banned` → `OnBanned`, danach Timeout.
7. **`OnSendReceiveData`** sieht jede empfangene Zeile vor dem Parsen; unser Handler (`:1031-1048`) ist heiß,
   allokationsfrei und darf nie werfen. `OnReadLineTestAsync` umgeht ihn. Der Transport ist `TwitchLib.Communication
   .IClient` (`CreateClient :1182-1201` baut `WebSocketClient`) und fälschbar.
8. **Roster:** `WorkerRosterPublisher` (60 s, sofort beim Start) baut die **Vereinigung** aus IRC-Wunschliste und
   7TV-Registry (`:78-105`); 7TV-only-Zeilen tragen `IrcJoinConfirmed = false`, ein IRC-Wunsch ist im Vertrag nicht
   erkennbar. Key `worker:roster`, TTL 180 s (`WorkerHealthKeys.RosterTtl`), beim Stopp nicht gelöscht.
   `IWorkerRosterReader` ist Singleton. `GetRoster()` lesen außerdem `SevenTvPeriodicResyncWorker`,
   `TwitchConnectionWatchdog`, `WishCaptureWorker`.
9. **Boot-Recovery** (`Worker.cs:51-53`, `:145-190`): erst `ConnectAsync`, dann **alle** Joins in einer Runde, danach der
   serielle 7TV-Sync. Die Joins sind also eine einzige Warteschlangen-Runde.
10. **Konvergenznetz:** `SevenTvPeriodicResyncWorker` ruft je aktivem Kanal aus `IChannelService
    .ListActiveChannelNamesAsync` `EnsureJoinedAsync` (Takt 60 s, ≈ 0,85 s/Kanal, #272). Jeder aktive Kanal bekommt so
    binnen einer Runde einen IRC-Wunsch; ein fehlender Wunsch heilt sich selbst.
11. **`/api/admin/roster`** (`AdminEndpoints.cs:245-315`) liest das Roster einmal und vergleicht inline gegen die
    Admin-Kanalliste (`missingFromIrc`, `missingFromSevenTv`, `unknownToDatabase`).
12. **`/api/health`**: payloadfrei, 200 nur bei „connected", Policy `PublicHealth` (30/min, Partition je IP).
13. **Identitätsabgleich:** eine gespeicherte Twitch-ID, die Helix nicht mehr liefert, bleibt unbefristet aktiv
    (`ChannelIdentityService.cs:253-260`); ein Login ohne ID erst nach 7 Tagen (`:1402-1406`).
14. **Abhilfe:** `/admin/channels` → „Verlassen" (`ChannelService.LeaveAsync`, Audit `channel.leave`, Historie bleibt).
15. **Die 20:** `WorkerCapacity.TwitchJoinBudgetChannels` → `ceilings.twitchJoinBudgetChannels` → Nenner in
    `admin-roster-card.ts`, Modell, Keys `admin.roster.joinBudget*`, Specs, Mocks (`web/e2e/support/mocks.ts:209`,
    `web/e2e/audit/ui-audit.audit.ts:1257`), `AdminDashboardActivityEndpointTests.cs:177`. Der Kommentar an
    `TwitchConcurrentChannelLimit` behauptet noch, ein verifizierter Bot hebe die Decke.
16. **Kanaldecke:** `ChannelService.CompleteJoinAsync` (`:819-847`) prüft `MaxActiveChannels` nur bei einer Aktivierung,
    Warning ohne `EventId`. **`docker-compose.prod.yml` mappt `Channels__MaxActiveChannels` nicht** (nur
    `Channels__ExcludedChannelIds`, `:114`); eine Stack-Variable allein erreicht den Container nicht.
17. **Admin-Karte:** lädt nur bei `worker.roster`-SSE nach (`admin-roster-card.ts:342`), `isStale` liest `ageSeconds`
    (`:236`), `statusKey` stellt `bootRecoveryCompleted === false` vor alles (`:325`). `pollWhileVisible` existiert.
18. **`TtlCache<T>`**: Single-Flight; die Füllung bekommt kein Token (ein abbrechender Wartender bricht sie nicht ab), hat
    aber **keine Frist**, und ein Fehler wird nie gecacht.
19. **Aktivierung vor Kommando (R3-H1):** `CompleteJoinAsync` speichert `IsBotActive`/`TrackingResumedAt` und publiziert
    **danach** `JOIN:`; geht das Kommando verloren und scheitert die Abfrage des Workers dauerhaft (der Resync fängt den
    Fehler, der Roster-Publisher läuft unabhängig weiter), entsteht nie ein Wunsch. `ListActiveChannelNamesAsync`
    (Boot-Recovery und Resync, Filter `IsBotActive`, Broadcaster-Sperre, Ausschlussliste) liefert nur Namen, keine
    Aktivierungszeit. Die Boot-Recovery legt die Wünsche auch ohne Twitch-Verbindung an (`Worker.cs:47-53`).

---

## 2. Entscheidungen

### E1 [E] Eine Anrechnungsuhr je Kanal in der reinen Klasse `ChannelJoinTracker`

`_desiredChannels` wird durch **`ChannelJoinTracker`** (`src/EmotePurge.Worker/`, rein, thread-sicher, eigenes `Lock`, nie
ein `await` darin, ruft nie nach außen — innerster Riegel) ersetzt. Der Tracker kennt keine Uhr; die Zeit kommt als
Parameter.

**Zustand:** ein Flag „verbunden" für den ganzen Tracker; je gewünschtem Kanal: bestätigt ja/nein, **angerechnete Sekunden
bis zum letzten Halt** plus **„rechnet seit"** (nur gesetzt, solange verbunden und unbestätigt), der **Grund**, das Flag
„Versuch offen" und das Flag „der laufende Versuch hat schon einen konkreten Grund".

**Regel (bindend):** Angerechnet wird nur Zeit, in der der Client verbunden ist (Handshake „004" erfolgt) **und** der Kanal
unbestätigt ist. Getrennte Zeit zählt nie. Nur eine Bestätigung setzt die Uhr auf null; Trennungen frieren sie ein.

| Übergang | Wirkung |
|---|---|
| Wunsch für neuen Kanal | unbestätigt, Uhr 0, läuft sofort, falls verbunden |
| Wunsch für vorhandenen Kanal | nichts |
| verbunden (`004`) | jede unbestätigte Uhr läuft ab jetzt |
| getrennt / Wiederaufbau beginnt | laufende Uhren halten an (Zeit aufaddiert); **alle** Kanäle unbestätigt, alle Versuche geschlossen; ein vorher bestätigter beginnt bei 0 |
| JOIN auf der Leitung (R3-H2) | für gewünschten, unbestätigten Kanal: Versuch offen, Flag „konkreter Grund" gelöscht; sonst nichts |
| eigenes PART auf der Leitung | Versuch geschlossen (falls der Eintrag noch besteht) |
| eigenes JOIN-Echo | **nur bei offenem Versuch:** bestätigt, Uhr 0, Grund weg, Versuch geschlossen; ohne offenen Versuch nichts (R3-H3) |
| eigenes PART-Echo, Kanal gewünscht | war bestätigt: unbestätigt, Uhr 0, läuft, falls verbunden — der Konvergenz-Retry greift wieder; war unbestätigt: nichts (R3-H3) |
| Leave | Eintrag weg; ein späterer Wunsch beginnt frisch |

Momentaufnahme zur Zeit `t`: je Kanal `angerechnet = bisher + (läuft ? t − seit : 0)`, negativ wird zu 0 (Uhrsprung).

**Versuch und Bestätigung hängen an der Leitung [E] (R3-H2, R3-H3):** Ein Timeout oder ein Suspended-NOTICE schließt den
Versuch **nicht** — ein Echo, das nach TwitchLibs 5-s-Frist kommt, bestätigt weiter wie heute. Twitch antwortet auf einer
Verbindung in Sende-Reihenfolge; das alte JOIN-Echo nach Leave und Reaktivierung kommt daher vor dem PART-Echo. Fällt es in
den neuen offenen Versuch, macht das folgende PART-Echo die Bestätigung rückgängig. [P] Rest: fehlt Twitchs PART-Echo **und**
fällt das alte JOIN-Echo in den neuen Versuch, bleibt eine falsche Bestätigung bis zum nächsten Wiederaufbau — zwei
unabhängige Ausnahmen; T2 hält live fest, ob Twitch PART echot.

**Ein Riegel für Uhr und Wunsch (R2-2):** Das Verbunden-Flag des Trackers wird an **jeder** Stelle mitgesetzt, an der der
Manager `SetConnected(…)` aufruft, im selben `lock (_clientStateGate)` (mit dem Wiederaufbau zusammen mit dem bisherigen
`MarkAllChannelsUnconfirmed`). Der Wunsch wird unter `_intentGate` eingefügt. Beide Wege enden im Tracker-Riegel, und
**dort** liest das Einfügen das Verbunden-Flag. Ein Wunsch, der vor dem Handshake einfügt, wird vom Handshake gestartet; einer
danach startet selbst. Einen „Wunsch ohne Uhr in einer laufenden Sitzung" gibt es nicht mehr. Reihenfolge der Riegel
unverändert: `_intentGate` bzw. `_clientStateGate` außen, Tracker innen.

**Folgen:** Ein langer Ausfall direkt nach einer Aktivierung kostet keine Frist (R2-2). Wiederholte kurze Reconnects setzen
nichts zurück (R1-1). Ein schon überfälliger Kanal bleibt während eines Ausfalls überfällig (R2-1). Boot-Recovery braucht
keinen Sonderfall: Wünsche vor dem Connect warten, und die Joins sind eine Runde (Befund 9) — `BootRecoveryMaxSeconds`
entfällt.

Verworfen: Stempel plus Sitzungsbeginn plus Obergrenze (Rev. 2: drei Regeln, jede mit eigenem Randfall); Uhr im Api ableiten
(sieht nur Minutenschnappschüsse).

### E2 [E] Gründe: klassifiziert nach Quelle, ersetzt nur durch einen neueren Versuch

- **Codes** (`TwitchJoinFailureReasons`, `src/EmotePurge.Core/Services/`, Muster `SevenTvSyncFailureReasons`):
  `noConfirmation`, `channelSuspended`, `concurrentChannelLimit`, `banned`.
- **Quellen:**
  - `OnFailureToReceiveJoinConfirmation` mit `Details` **nicht null** → `channelSuspended` (das ist nur der Suspended-Zweig,
    Befund 4); mit `Details == null` → `noConfirmation` (Timer). Der Text von `Details` wird nie gelesen oder geloggt.
  - NOTICE-Zweig in `OnSendReceiveData` (Richtung `Received`, nach dem unveränderten Frame-Stempel): allokationsfreier
    Befehlstest (Tag-Block und Präfix überspringen, Token ordinal gegen `NOTICE`), nur bei Treffer der reine Parser
    **`TwitchJoinNoticeParser`**: `msg-id` positionsunabhängig im Tag-Block, Kanal aus `NOTICE #<kanal> :…`, wirft nie.
    Er liefert nur `concurrentChannelLimit` und `banned` (TwitchLib leitet beide nicht in den Callback); `msg_channel_suspended`
    kommt über `Details`, damit nichts doppelt bucht.
  - Nicht `OnUnaccountedFor` (ein Handler schaltete TwitchLibs redigierte Warnkette aus #246 ab).
- **Versuch:** beginnt erst, wenn ein `JOIN #<kanal>` **auf der Leitung** liegt: die Transport-Naht (3.1) meldet jeden
  ausgehenden JOIN und PART vor dem Weiterreichen an den Manager (Befund 3). `TryJoinAsync` meldet nichts; ein von TwitchLib
  verworfener Doppel-JOIN (Rejoin und Resync für denselben Kanal) ist für den Tracker ein No-op (R3-H2). Regeln: ein
  konkreter Grund ersetzt jeden Grund; `noConfirmation` ersetzt einen konkreten Grund
  **nur**, wenn der laufende Versuch noch keinen konkreten Grund hat. So bleibt `concurrentChannelLimit` stehen, wenn der
  Timeout desselben Versuchs folgt; ein früher gesperrter Kanal, der beim nächsten Versuch still scheitert, wird
  `noConfirmation` und alarmiert. **Keine Zeitfenster.** Bis zum Ausgang des neuen Versuchs bleibt der alte Grund sichtbar.
  Gebucht wird nur für gewünschte, unbestätigte Kanäle.
- **Identität:** alle Quellen (Callback, NOTICE-Zweig, Leitungs-Hook, JOIN- und PART-Echo) binden Client und Generation zur
  Verdrahtungszeit und prüfen und buchen in einem `lock (_clientStateGate)`; in `OnSendReceiveData` nur im NOTICE-Zweig. Der
  Leitungs-Hook läuft im Sendepfad von TwitchLib; unter `_clientStateGate` darf deshalb nie synchron in TwitchLib gerufen
  werden (sonst Verklemmung) — T2 prüft das am Bestand und hält es im Kommentar fest.
- **Logzeilen:** eine je erstem Auftreten oder Wechsel des Grundes (der Tracker meldet „neu/geändert" / „Wiederholung" /
  „ignoriert"), Wiederholungen auf Debug, je Prozess. EventIds: `TwitchJoinRejected` (`concurrentChannelLimit` auf
  **Error**, `channelSuspended`/`banned` auf Warning), `TwitchJoinUnconfirmed` (`noConfirmation`, Warning). Kanal und Code,
  nie Twitchs Text. Die bisherige Warning in `OnFailureToReceiveJoinConfirmation` geht darin auf.

### E3 [E] Roster-Vertrag: drei additive Felder und eine Schema-Marke, keine Migration

| Ort | Feld (JSON camelCase) | Bedeutung |
|---|---|---|
| `TwitchRosterEntry` (Worker) | `UnconfirmedSeconds`, `JoinFailureReason` | optional am Ende, Default `null` — die drei anderen Leser kompilieren unverändert |
| `WorkerRosterChannel` (Core) | `ircIntent` (bool, Default `false`), `ircUnconfirmedSeconds` (int, `null` wenn bestätigt oder kein Wunsch), `ircJoinFailureReason` | am Ende mit Default |
| `WorkerRosterSnapshot` (Core) | `schemaVersion` (int, Default `0`) | am Snapshot, nicht je Zeile — auch ein leeres Roster trägt sie; der Publisher schreibt die Konstante `WorkerRosterSnapshot.CurrentSchemaVersion` = 1 (R3-H4) |

- `GetRoster()` füllt die Felder aus **einer** Tracker-Momentaufnahme zur Zeit des Aufrufs (in sich konsistent, ein
  Riegel). Kein `CaptureRoster()`, kein Sitzungsfeld. Der Publisher setzt `ircIntent = true` für jede Zeile aus `GetRoster()`;
  7TV-only-Zeilen bleiben `false`.
- **Mischbetrieb (R3-H4):** neue Api + alter Worker liest `schemaVersion = 0` → „nicht auswertbar" → **503**, ohne
  Übergangsfenster (fail-closed; Prod zieht die Images getrennt, ein Mischbetrieb kann sonst unbegrenzt grün bleiben). Alte
  Api + neuer Worker ignoriert die Felder. Api und Worker kommen im selben Deploy; der Kuma-Monitor wird erst danach
  eingerichtet, ein Rollback nur des Workers macht ihn absichtlich rot (T10).

### E4 [E] Bewertung über das Roster plus Abgleich „aktiv ohne Wunsch", reine Policy in der Infrastructure

**`ChannelJoinHealthPolicy`** (Infrastructure, rein, statisch). Eingaben: Roster oder `null`, die **Soll-Liste** (aktive
Kanäle mit Aktivierungszeit, oder `null`, wenn die Abfrage scheiterte), `nowUtc`, Startzeit des Api-Prozesses, Grace. Stufen:

1. Roster fehlt oder `nowUtc − GeneratedAtUtc > RosterTtl` → **`Starting`**, solange der Api-Prozess höchstens `RosterTtl`
   (180 s) läuft (Kaltstart ohne früheres Roster: der Worker veröffentlicht sofort beim Start); sonst **`Unavailable`**.
   Frisches Roster mit `schemaVersion < CurrentSchemaVersion`, Soll-Liste `null` oder `Truncated` → **`Unavailable`**
   („nicht auswertbar"; `Truncated` kommt bei der Decke 120 ≪ 500 nicht vor).
2. Bewertet wird **jede Zeile mit `ircIntent` und ohne Bestätigung** (R2-6); 7TV-only-Zeilen nie. Überfällig, wenn
   `ircUnconfirmedSeconds > Grace`. **Zählt für den Alarm**, außer der Grund ist `channelSuspended` oder `banned` (E11).
3. **Aktiv, aber ohne Wunsch (R3-H1):** jeder Kanal der Soll-Liste ohne Roster-Zeile mit `ircIntent` (Namensvergleich
   `OrdinalIgnoreCase`) wartet seit `max(ActivatedAtUtc, ProcessStartedUtc des Rosters)`, mit `ActivatedAtUtc =
   TrackingResumedAt ?? CreatedAt`. Überfällig, wenn `GeneratedAtUtc − seit > Grace`; **zählt immer für den Alarm**. Eine
   Regel, kein eigenes Bootfenster: die Boot-Recovery legt alle Wünsche in einer Runde an, auch ohne Twitch-Verbindung
   (Befund 9, 19), und eine Runde ist kürzer als die Grace (≤ P ≈ 112 s). Roster-Zeilen mit Wunsch, die nicht in der
   Soll-Liste stehen (Leave unterwegs), bewertet Stufe 2 unverändert.
4. Mindestens ein alarmierender Eintrag → **`Failing`**, sonst **`Healthy`**.

Öffentlich: **503 bei `Unavailable` und `Failing`**, 200 bei `Healthy` und `Starting`. Bewertet wird das Roster zu seiner
Erfassungszeit; seine Verzögerung (≤ 60 s) verlängert nur die Erkennungszeit. `BootRecoveryCompleted`, `Truncated` (erst ab
500 Zeilen, die Decke liegt bei 120), Instanz und `worker:health` spielen keine Rolle.

**Soll-Liste [E]:** neue Abfrage `IChannelService.ListActiveChannelActivationsAsync` (Name, `ActivatedAtUtc`) mit
**demselben** Filter wie `ListActiveChannelNamesAsync`; Letztere wird daraus abgeleitet, damit der Filter nur einmal
existiert. Rev. 3 verließ sich allein auf das Konvergenznetz (Befund 10) — das trägt nicht, wenn genau dessen Abfrage
dauerhaft scheitert (Befund 19). Die Last trägt der 5-s-Cache (E5).

**Grace-Default 300 s [E], neu hergeleitet (D1):** Eine Rejoin-Runde bei N Kanälen und K still abgelehnten dauert
`P ≈ 0,6 s × N + 7 s × K + 5 s` (Drossel dominiert die `366`-Zeiten von 0,13–0,23 s; `channelSuspended` kostet nur eine
Antwortzeit). Bei N = 120, K = 5: **P ≈ 112 s**. Weil getrennte Zeit nicht zählt, muss die Grace nur Runden decken, nicht
Ausfälle — aber zwei Fälle verlängern eine Runde, ohne dass ein Kanal kaputt ist:
- **Übertrag:** Reißt die Verbindung kurz vor Ende einer Runde, nimmt der letzte Kanal fast `P` mit und wartet in der nächsten
  Runde wieder bis zu `P` → `≤ 2P ≈ 224 s`.
- **Einmal verlorener JOIN**, den das Konvergenznetz nachholt: `P + 60 s + 0,85 s × N ≈ 274 s`.

300 s deckt beide mit Reserve; Kumas Retries (2) geben vor der Meldung zusätzlich ≈ 2 min. Faustformel für Operations.md:
`Grace ≥ max(2P, P + SevenTv:ResyncIntervalSeconds + 0,85 s × N) + Reserve`. **Rechnung, keine Messung:** T8 misst `P` bei
Ziel 120; liegt die Messung über `Grace − 60 s`, hebt der Orchestrator den Default vor T9 in einem eigenen Commit samt
DECISIONS-Nachtrag an. Unter Flattern mit Reconnects kürzer als eine Runde alarmieren Kanäle am Ende der Warteschlange — das
ist ein echter Erfassungsausfall, kein Fehlalarm.

**Grenze der Warteschlange:** Re-JOINt das Konvergenznetz je Runde K still abgelehnte Kanäle, kostet das ≈ 7 s × K; bei
K ≈ 20 (N = 120) läuft die Warteschlange nicht mehr leer. Dieser Fall alarmiert ohnehin. Kein Umbau des Konvergenznetzes.

### E5 [E] `GET /api/health/channels`: eigenes Budget, geteiltes Ergebnis mit eigener Frist

- Anonym, kein Körper, nur Statuscode, in `WorkerHealthEndpoints.cs` neben `/api/health`; der Handler bildet nur ab.
- **`IChannelJoinHealthService`** (Core-Interface, Infrastructure-**Singleton**; der Roster-Leser ist Singleton):
  - `EvaluateAsync(snapshot, ct)` → Bericht, ungecacht (für `/api/admin/roster`, das das Roster ohnehin schon liest — eine
    Lesung); liest die Soll-Liste selbst, in einem eigenen Scope (`IServiceScopeFactory`, Muster `LogStatusReader`), ein
    Abfragefehler wird zur Soll-Liste `null`;
  - `IsAlarmingAsync(ct)` → öffentliche Entscheidung über `TtlCache<T>` mit **5 s** (Konstante); die Füllung liest Roster
    **und** Soll-Liste. Die **Füllung hat eine eigene Frist von 2 s** (Konstante), unabhängig vom Aufrufer und unabhängig davon, ob der Leser ein Token beachtet; eine
    Ausnahme oder Fristüberschreitung wird **in der Füllung** zum Ergebnis „nicht auswertbar" (→ 503) und wie jedes Ergebnis
    5 s gecacht; je Füllung höchstens eine Warning, ohne Kanalnamen. Ein abbrechender Wartender bricht die Füllung nicht ab
    (Eigenschaft von `TtlCache`). `TtlCache` selbst bleibt unverändert.
  - Startzeit des Api-Prozesses: beim Start erfasst, nicht lazy beim ersten Aufruf.
- **Rate-Limit:** eigene Policy **`PublicChannelHealth`** (FixedWindow, 10/min, Partition je IP wie `PublicHealth`), eigener
  Zähler; die Gesamtlast begrenzt der Cache.
- **Doppelalarm akzeptiert [E]:** Ist der Worker tot, fehlt nach 180 s das Roster → beide Monitore rot (zwei Meldungen, eine
  Ursache). Ist er nur von Twitch getrennt, frieren die Uhren ein → nur `/api/health` rot, außer ein Kanal war schon vorher
  überfällig. Fällt Postgres aus, ist der Kanalcheck „nicht auswertbar" → 503. Alles steht in Operations.md. Verworfen: „200, wenn `/api/health` nicht connected" (R2-1: Stichproben beider
  Monitore können sich so dauerhaft ausweichen).
- `/api/health` und der Container-HEALTHCHECK bleiben unverändert; **der neue Endpunkt darf nie in einen HEALTHCHECK.**

### E6 [E] Admin-Monitoring in der bestehenden Roster-Karte

- `/api/admin/roster` liefert zusätzlich `joinHealth` und `ircUnjoined` (Vertrag 3.3). `missingFromIrc*` bleibt unverändert
  (Datenbanksicht). Die Karte zeigt zwei getrennte Abschnitte: **Join-Status** (`ircUnjoined`: Kanal mit Link auf
  `/admin/channels/{name}`, Grund übersetzt, angerechnete Zeit, als Text „überfällig" / „wartet auf Bestätigung" / „von
  Twitch abgelehnt, zählt nicht für den Alarm" mit Hinweis auf „Verlassen") und **Nicht im Worker** (`missingFromIrc` ohne
  die Namen aus `ircUnjoined`). **Nicht im Worker speist jetzt den Alarm (R3-H1):** zusätzlich zu `missingFromIrc` stehen dort
  die Namen aus `missingIntentOverdue`, als Text „überfällig, zählt für den Alarm" markiert und zuerst. Farbe nie allein
  (WCAG 1.4.1). Sortierung: alarmierend, dann ausgenommen-überfällig, dann
  wartend; innerhalb nach angerechneter Zeit absteigend. **Jeder überfällige Kanal** steht in der Liste (R2-4).
- **Status:** Vorrang `unavailable` > `stale` > `failing` > `starting` > `incomplete` > `complete`; `failing` kommt aus
  `joinHealth.status` und schlägt `bootRecoveryCompleted === false`.
- **Frische:** Neben dem SSE-Nachladen lädt die Karte **jede Minute nach, solange sichtbar** (`pollWhileVisible`); `stale`
  bleibt aus dem `ageSeconds` des Servers (Schwelle 180 s). Kein lokal tickendes Alter, kein Einmal-Nachladen.
- Zurückgestellt (nicht für Erklärung oder Alarm nötig): Anzeige auf der Kanal-Detailseite, Feld `reasonAtUtc`.

### E7 [E] Teil B, Anzeige: Nenner ist unsere Decke, Twitchs 100 nur Referenz, Frühwarnung bei 90 %

- Balken `trackedChannelCount / ceilings.maxActiveChannels`; `TwitchJoinBudgetChannels` entfällt vollständig (Konstante,
  Feld, Modell, Keys, Specs, Mocks, Api-Test, Kommentare).
- Schwellen dieses Balkens `warning` ab 90 %, `danger` ab 100 % als optionales Argument von `utilizationTone`; der Default
  (80/95) und das Slot-Budget bleiben.
- Texte ab `warning`: nahe der Grenze / erreicht (neue Kanäle abgelehnt, globale Admins ausgenommen) / über der Grenze.
- Twitchs 100 als Hinweistext mit Herkunft („gilt für benannte Konten; anonym am 2026-09-08 bis 120 ohne Ablehnung gemessen,
  undokumentiert"). Kein Alarm für 90 % (die Decke verliert keine Daten).

### E8 [E] Abgelehnte Joins zählen: EventId plus Log-Viewer

Die Warning in `CompleteJoinAsync` bekommt die EventId **`ChannelCapacityReached`**. Gezählt wird im Log-Viewer; die
Kapazitätszeile verlinkt `/admin/logs?q=event:ChannelCapacityReached&range=7d`. Ohne `LogCapture:Enabled` nur stdout (in
Operations.md benannt). Keine Tabelle, kein Audit je Ablehnung.

### E9 [B] Anhebung auf 120 ist ein Betreiberschritt; der Rückweg hat zwei Teile

Kein Code-Default ändert sich (80). Neu: `Channels__MaxActiveChannels=${CHANNELS_MAX_ACTIVE_CHANNELS:-80}` im
Api-Service beider Compose-Dateien (T7). Operations.md (T10):

- **Voraussetzung [B]:** Teil A läuft **mindestens 7 Tage** auf Prod, und in dieser Zeit sind **mindestens ein Deploy und ein
  echter Twitch-Reconnect ohne Fehlalarm** durchgelaufen. Wird es vorher eng, ist **ein Zwischenschritt auf ≈ 90** erlaubt,
  sobald Teil A live ist.
- **Anheben:** Stack-Variable `CHANNELS_MAX_ACTIVE_CHANNELS` setzen, **Api-Container neu erstellen** (Stack-Update, kein
  Restart), dann `ceilings.maxActiveChannels` in `/api/admin/roster` prüfen. Nach dem nächsten Worker-Neustart Rejoin-Dauer
  und Grace gegen die T8-Werte.
- **Rückweg 1 — Aufnahme stoppen:** Variable zurück, Container neu erstellen, `ceilings.maxActiveChannels` prüfen. Das
  blockt nur weitere Aktivierungen (Befund 16).
- **Rückweg 2 — Erfassung wiederherstellen:** Zeigt die Karte `concurrentChannelLimit` oder überfällige Kanäle, Kanäle über
  „Verlassen" abbauen (Vorschlag: zuletzt aktivierte zuerst), bis die Zahl unter der funktionierenden Grenze liegt. Danach
  **je Kanalname** prüfen: jeder Kanal der Admin-Liste steht im Roster mit `ircJoinConfirmed`; `ircUnjoined` leer;
  `/api/health/channels` 200 über mindestens Grace plus zwei Roster-Takte. Wen das betrifft, informiert der Betreiber.

### E10 [E] Konfiguration

| Schlüssel | Default | Validierung | gelesen von |
|---|---|---|---|
| `Channels:JoinHealth:GraceSeconds` | 300 | 60 … 3600 | Api (`ChannelJoinHealthOptions`, Muster `ChannelCapacityOptions`, eager validiert) |
| `RateLimiting:PublicChannelHealth:PermitLimit` | 10 | `FixedWindowPolicy.Validate` | Api, Eintrag in `appsettings.json` neben `PublicHealth` |
| `Worker:Debug:SilentJoinChannels` | leer | kommagetrennte Logins | Worker, nur Live-Prüfung (T3, T8); nicht leer → eine Warning beim Start |
| `Channels:MaxActiveChannels` (bestehend) | 80 | bestehend | Api; **neu gemappt** in `docker-compose*.yml` (E9) |

Konstanten: Ergebnis-Cache 5 s, Füllfrist 2 s, Kaltstartfenster = `RosterTtl` (180 s). `GraceSeconds` wird nicht in die
Compose-Dateien gemappt; wer es ändern will, braucht ein Mapping. **Wer `SevenTv:ResyncIntervalSeconds` ändert, prüft die
Grace nach der Formel aus E4 neu** [B: auf Prod ist keine Streckung geplant].

### E11 [B] Gesperrte und gelöschte Kanäle: sichtbar, geloggt, kein 503; Erinnerung über Kuma

- Überfällige Kanäle mit `channelSuspended` oder `banned` machen den Endpunkt nicht rot; sie stehen in der Karte und erzeugen
  je Prozess eine Warning `TwitchJoinRejected`. Grund: der Identitätsabgleich räumt sie nicht verlässlich ab (Befund 13),
  und ein dauerhaft roter Monitor meldet weitere Ausfälle nicht.
- Ein Grund weicht nur dem Ausgang eines neueren Versuchs (E2) — wird ein gesperrter Kanal frei und scheitert still, zählt er.
- **Stiller gelöschter Kanal (R2-4):** bleibt `noConfirmation` und hält den Monitor DOWN. Ein späterer, unabhängiger Ausfall
  erzeugt dann keine neue DOWN-Meldung. Deshalb [B]: **Kuma „Resend Notification if Down X times consecutively" = 60**
  (stündlich während DOWN), und die Karte listet jeden überfälligen Kanal. Abhilfe und Quittung ist der Leave.

### E12 [E] Live-Verifikation je Backend-Task, Live-Gate vor dem Frontend

Jeder Backend-Task mit externem System hat einen kurzen Live-Schritt vor seinem Commit (T2, T3, T4, T6, T7); T5 ist reine
Logik und wird mit T6 live geprüft. **T8 ist das Live-Gate mit Messung**; T9 beginnt erst nach bestandenem T8 oder
entschiedenen Befunden. Vor jedem Live-Schritt stellt der Orchestrator sicher, dass kein anderer Dev-Worker läuft (Worktree
487, Compose-`worker` des Haupt-Checkouts); `docker compose up -d postgres redis` nur im Haupt-Checkout; Api und Worker per
`dotnet run` aus dem Worktree; Redis per `docker exec <redis-container> redis-cli`. Abfragen von `/api/health/channels`
höchstens alle 10 s (unter dem Budget).

---

## 3. Verträge im Überblick

### 3.1 Worker-intern

- `ChannelJoinTracker` (vom Manager besessen, keine DI): `RecordIntent(name, now)`, `SetConnected(now)`,
  `SetDisconnected(now)` (hält an, macht alles unbestätigt, schließt alle Versuche), `BeginAttempt(name)` und
  `RecordPartSent(name)` (nur vom Leitungs-Hook), `MarkConfirmed(name)` (fügt nie ein, nur bei offenem Versuch),
  `MarkParted(name, now)` (eigenes PART-Echo), `RecordFailure(name, reason)` → neu-oder-geändert / Wiederholung /
  ignoriert, `Remove(name)`, `Contains`, `IsConfirmed`,
  `Names`, `Snapshot(now)` (Name, bestätigt, angerechnete Sekunden, Grund; nach Name ordinal). Namen `OrdinalIgnoreCase`.
- `TwitchJoinNoticeParser`: `IsNotice(line)` (allokationsfrei), `TryParse(line, out reason, out channel)` (`true` nur für
  `concurrentChannelLimit`/`banned` mit Kanal; Kanal ohne `#`, klein; wirft nie).
- `TwitchRosterEntry` erweitert (E3); `GetRoster()` in der Bedeutung unverändert.
- Transport-Naht (T2, intern): Produktion baut weiter `WebSocketClient` mit `NoReconnectionPolicy`, **immer** in einem
  Dekorierer. Er meldet jeden ausgehenden `JOIN #<kanal>` und `PART #<kanal>` vor dem Weiterreichen an den Manager
  (Leitungs-Hook, an Client und Generation gebunden) und reicht sonst alles unverändert durch. Ab T3 verwirft er zusätzlich
  ausgehende JOINs für die Logins aus `Worker:Debug:SilentJoinChannels` — **nach** der Meldung, damit ein verschluckter JOIN
  als Versuch zählt wie ein echter stiller.

### 3.2 Redis `worker:roster` (additiv)

`schemaVersion` (Snapshot), `channels[].ircIntent`, `channels[].ircUnconfirmedSeconds`, `channels[].ircJoinFailureReason`;
`null` weggelassen. `worker:health` unverändert.

### 3.3 Api und Infrastructure

- `GET /api/health/channels`: anonym, Policy `PublicChannelHealth`, Körper leer, `Cache-Control: no-store` aus der
  bestehenden Middleware. 200 bei `Healthy`/`Starting`, 503 bei `Failing`/`Unavailable`/„nicht auswertbar".
- `IChannelService.ListActiveChannelActivationsAsync(ct)` → `IReadOnlyList<ActiveChannelActivation(ChannelName,
  ActivatedAtUtc)>` (Core-Record), Filter wie `ListActiveChannelNamesAsync` (E4).
- `IChannelJoinHealthService` (Singleton): `EvaluateAsync(WorkerRosterSnapshot?, ct)` → `ChannelJoinHealthReport { Status,
  GraceSeconds, Entries, MissingIntent, AlarmingCount, ExcludedCount }`; `MissingIntent` = `{ ChannelName, WaitingSeconds,
  Overdue }` je aktivem Kanal ohne Wunsch; `ChannelJoinHealthStatus` = `Healthy | Starting | Unavailable |
  Failing`; `ChannelJoinHealthEntry { ChannelName, UnconfirmedSeconds, Reason?, Overdue, CountsForAlarm }` (alle
  unbestätigten Wunsch-Zeilen, auch innerhalb der Frist). `IsAlarmingAsync(ct)` (5-s-Cache, E5).
- `/api/admin/roster`, beide Zweige: `joinHealth: { status: 'healthy'|'starting'|'unavailable'|'failing', graceSeconds,
  alarmingTotal, excludedTotal }`. Mit Schnappschuss zusätzlich `ircUnjoined: [{ channelName, unconfirmedSeconds, reason|null,
  overdue, countsForAlarm }]` (gekappt auf `DeficitListLimit` = 50, Sortierung E6) und `ircUnjoinedTotal`, dazu
  `missingIntentOverdue: string[]` (gekappt auf 50) und `missingIntentOverdueTotal`; `alarmingTotal` zählt beide Arten.
  Ein Legacy-Roster (R3-H4) zeigt `joinHealth.status = 'unavailable'`. Entfällt:
  `ceilings.twitchJoinBudgetChannels`. Status explizit auf camelCase-String abgebildet.
- Rate-Limit-Übersicht (`AdminEndpoints.cs:555-570`) führt `PublicChannelHealth` mit Partition `remote-ip`.
- Keine neuen `ApiErrorCodes`.

### 3.4 Frontend

`RosterCeilings` ohne `twitchJoinBudgetChannels`; `AdminRoster` mit `joinHealth`, `ircUnjoined`, `ircUnjoinedTotal`,
`missingIntentOverdue`, `missingIntentOverdueTotal`;
`utilizationTone(percent, thresholds?)`. Locale-Keys: entfallen `admin.roster.joinBudget*`; neu `admin.roster.capacity.*`,
`admin.roster.unjoined.*`, `admin.roster.notInWorker.*` (ersetzt die Verwendung von `missingIrc`, mit `.overdue`),
`admin.roster.joinFailure.reason.{noConfirmation,channelSuspended,concurrentChannelLimit,banned,unknown}`,
`admin.roster.status.failing`; `twitchLimitHint` neu formuliert. de und en in Parität.

---

## 4. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander**. Backend-Tasks enden mit grünem `dotnet build
EmotePurge.slnx`, `dotnet format EmotePurge.slnx --verify-no-changes`, den Tests ihres Projekts und ihrem Live-Schritt;
Frontend mit `npm --prefix web test -- --watch=false`, `lint`, `format:check`. Je Task ein Conventional Commit (englisch),
Regel 19, neue Kommentare und Logtexte englisch. **Ein** DECISIONS-Eintrag „Join-failure detection and capacity display
(#516)": T3 legt ihn an, T4–T7 und T9 ergänzen je ihren Absatz und die `**Betrifft:**`-Zeile im Commit ihrer
Vertragsänderung (Regel 3). Live-Ergebnisse gehen in `docs/nachtlaeufe/live-516-<datum>.md` (deutsch, Muster
`live-486-2026-11-20.md`; T2 legt an, T8 schließt ab), **ohne Namen echter Kanäle**.

### T1 — Reine Bausteine: `ChannelJoinTracker`, `TwitchJoinNoticeParser`, `TwitchJoinFailureReasons`

- **Lesen:** E1, E2, 3.1; alle `_desiredChannels`-Stellen in `TwitchChatManager.cs`, `ChannelLeaveLedger.cs` + Tests
  (Stilvorbild), `SevenTvSyncFailureReasons`, `TwitchTagKeyAllowlistTests.cs:320` (NOTICE-Form).
- **Liefert:** die drei Typen (Reasons in Core, die anderen im Worker), keine Verdrahtung.
- **Grenzfälle Tracker:** jede Zeile der Tabelle aus E1; Anrechnung über mehrere Trenn-/Verbunden-Folgen (nur verbundene
  Abschnitte summiert); langer getrennter Abschnitt direkt nach dem Wunsch zählt 0; vorher bestätigter Kanal beginnt nach
  Trennung bei 0, nie bestätigter behält seine Summe; Wunsch während getrennt, dann verbunden → läuft ab Verbunden;
  `SetConnected` zweimal → keine Doppelzählung; Momentaufnahme mitten im laufenden Abschnitt; Uhr rückwärts → 0, nie
  negativ; Bestätigung nach `Remove` fügt nicht ein; Versuchsregel: Limit, dann `noConfirmation` im selben Versuch → Limit
  bleibt („Wiederholung"); `channelSuspended`, neuer Versuch, `noConfirmation` → ersetzt („geändert"); `noConfirmation`
  zweimal → „Wiederholung"; Grund auf bestätigtem oder unbekanntem Kanal → „ignoriert"; **Versuchsfenster:** Echo ohne
  offenen Versuch bestätigt nicht; Echo nach Timeout im selben Versuch bestätigt; `RecordPartSent` schließt; `BeginAttempt`
  auf bestätigtem Kanal ändert nichts; `MarkParted` auf bestätigtem Kanal → unbestätigt, Uhr 0, läuft; auf unbestätigtem →
  Uhr und Versuch unverändert; Trennung schließt alle Versuche; Groß-/Kleinschreibung; parallele
  Aufrufe aus mehreren Threads (Wunsch gegen Verbunden-Wechsel) ohne verlorene Übergänge und ohne Kanal ohne laufende Uhr bei
  bestehender Verbindung.
- **Grenzfälle Parser:** Limit- und `msg_banned`-Zeile mit Kanal; aufgezeichnete Limit-Zeile (Kanal erfunden); `msg-id` an
  zweiter und letzter Stelle; maskierte Tag-Werte; `NOTICE *` ohne Kanal; NOTICE ohne Tag-Block; `msg_channel_suspended`
  und andere `msg-id` → `false`; PRIVMSG mit „NOTICE" im Text; USERNOTICE; abgeschnittene Zeile; leer; Großbuchstaben.
- **Tests:** `tests/EmotePurge.Worker.Tests/ChannelJoinTrackerTests.cs`, `TwitchJoinNoticeParserTests.cs`. Live entfällt.
- **Commit:** `feat(worker): add a per-channel join tracker and a join-notice parser`.

### T2 — `TwitchChatManager` auf den Tracker umstellen

- **Lesen:** E1, E2 (Versuch, Identität), E3, 3.1; `TwitchChatManager.cs` vollständig (Gate-Regeln, `WireUpClient`,
  `ReconnectOnceAsync`, `OnConnected`, `OnDisconnected`, `OnReconnected`, `TryJoinAsync`, `CreateClient`, `OnLeftChannel`),
  `ITwitchChatManager.cs`, `WorkerRosterPublisher.cs`, `TwitchChatManagerLeaveRaceTests.cs`; TwitchLib dekompiliert
  (`JoinChannelAsync`, `QueueingJoinCheckAsync`, `LeaveChannelAsync`, `HandlePart`).
- **Liefert:** `_desiredChannels` vollständig ersetzt, alle bisherigen Pfade gleich (Intent vor JOIN, Re-Check nach dem Gate,
  Leave-Ledger, `TallyRejoin`, `AllConfirmed`, Abkürzung in `EnsureJoinedAsync`); jeder `SetConnected`-Aufruf setzt im selben
  Riegel den Tracker (E1); **Transport-Naht als durchreichender Dekorierer mit Leitungs-Hook** (3.1) → `BeginAttempt` bzw.
  `RecordPartSent`; `OnJoinedChannel` → `MarkConfirmed`; `OnLeftChannel` → `MarkParted` (mit Identitätsprüfung);
  `GetRoster()` mit den neuen Feldern aus einer Momentaufnahme. Noch keine Gründe, noch keine Debug-Liste.
- **Grenzfälle:** Join bei nie verbundenem Manager → Uhr steht; Bestätigung, PART-Echo oder Leitungs-Meldung eines
  ersetzten Clients bucht nichts; der Hook erkennt nur `JOIN #`/`PART #` am Zeilenanfang und wirft nie; die
  Riegel-Reihenfolge und „unter `_clientStateGate` nie synchron in TwitchLib" sind im Code-Kommentar begründet.
- **Tests:** `TwitchChatManagerJoinStateTests.cs` am nie verbundenen Manager (Join → Uhr 0 und steht, Leave entfernt, Ensure
  nach Leave legt nichts an). Bestehende Worker-Tests unverändert grün. Handshake-Übergänge prüft T3.
- **Live:** Worker gegen Twitch mit den Dev-Kanälen: Boot → alle bestätigt (`GET worker:roster`);
  `DEBUG:TWITCH-RECONNECT` (mit `Worker__Debug__AllowTwitchReconnectTrigger=true`) → wieder alle bestätigt; Leave und
  sofortiger Join eines Dev-Kanals → bestätigt; keine neuen Fehler. Im Protokoll festhalten, ob Twitch das eigene PART echot
  (E1, [P] Rest). Protokoll anlegen, Abschnitt „T2".
- **Commit:** `refactor(worker): track join state per channel with an accrual clock`.

### T3 — Gründe erfassen, Transport-Naht, Fake-Transport-Tests, DECISIONS-Eintrag

- **Lesen:** E1, E2, Befund 3–7; `TwitchChatManager.cs` (`WireUpClient`, `OnSendReceiveData`,
  `OnFailureToReceiveJoinConfirmation`, `CreateClient`), `RedactingTwitchClientLoggerFactory.cs` + Tests,
  `RewardRedemptionTwitchLibTests.cs`, `IrcLineSpliceRuleTwitchLibTests.cs`,
  `tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvWriteClientLoggingTests.cs` (Logger-Fake); TwitchLib dekompiliert
  (`HandleJoin`, `Handle366`, `HandleChannelSuspended`, `JoinChannelTimeout`, `QueueingJoinCheckAsync`).
- **Liefert:** Callback mit Identitätsprüfung und Klassifikation über `Details` (E2); NOTICE-Zweig in `OnSendReceiveData`;
  Logzeilen mit EventIds und Dedupe; Test-Zugang zur Transport-Naht aus T2 (Test ersetzt den `IClient` für Boot-Client und
  jeden Wiederaufbau) plus das Verschlucken für `Worker:Debug:SilentJoinChannels` (3.1, E10); `WorkerServiceRegistrationTests`
  unverändert grün. **DECISIONS-Eintrag anlegen**, Absatz „worker join state": Anrechnungsuhr, ein Riegel, Gründe nach
  Quelle, Versuch ab der Leitung (warum nicht `TryJoinAsync`: TwitchLibs stilles Dedupe), Bestätigung nur im offenen
  Versuch und PART-Echo-Regel samt Rest, EventIds als neue Konvention, warum nicht `OnUnaccountedFor`, Debug-Naht.
- **Grenzfälle:** NOTICE oder Timeout eines ersetzten Clients bucht nichts; Leave zwischen NOTICE und Buchung → nichts;
  `OnSendReceiveData` wirft nie und sperrt nur im NOTICE-Zweig; Debug-Liste leer → Dekorierer reicht alles durch.
- **Tests (Worker.Tests):**
  - `TwitchJoinNoticeTwitchLibTests.cs` (TwitchLib-gebunden, pinnt Bibliotheksfakten): Suspended-NOTICE → Callback mit
    `Details` ≠ null; Limit-NOTICE → kein Callback; Timer-Ablauf → `Details == null`.
  - `TwitchChatManagerJoinFailureTransportTests.cs` (Fake-`IClient`, Handshake bis „004"): (a) aufgezeichnete Limit-Zeile
    → `concurrentChannelLimit`, eine Error-Zeile `TwitchJoinRejected`, nach dem Timeout bleibt der Grund, keine zweite laute
    Zeile; (b) JOIN ohne Antwort → `noConfirmation`, eine Warning `TwitchJoinUnconfirmed`; (c) Suspended-NOTICE →
    `channelSuspended`; (d) **eigenes JOIN-Echo** (`:<eigener Benutzer>!…JOIN #kanal`) → bestätigt, Uhr 0, Grund weg;
    (e) **`366` getrennt davon:** schiebt die Warteschlange (der Fake sieht den nächsten `JOIN` gesendet), bestätigt aber
    nichts; ohne `366` und ohne Antwort geht der nächste JOIN erst nach dem Timeout raus; (f) Trennung und neuer Handshake:
    Uhr steht während der Trennung, läuft danach weiter, vorher bestätigter Kanal beginnt bei 0; (g) **Kanal A still, Kanal
    B mit Limit-Zeile** → beide mit eigenem Grund im Roster; (h) Debug-Dekorierer verschluckt den JOIN eines gelisteten
    Kanals → Verlauf wie (b); (i) **Doppel-JOIN (R3-H2):** JOIN auf der Leitung, `msg_banned`-NOTICE, dann
    `EnsureJoinedAsync` und `JoinChannelAsync` für denselben Kanal, dann Timeout → Grund bleibt `banned`, der Fake sah genau
    einen ausgehenden JOIN; (j) **spätes Echo (R3-H3):** Kanal mit offenem JOIN, Leave, Reaktivierung, dann das alte
    JOIN-Echo, dessen PART-Echo, der neue JOIN bleibt still → unbestätigt mit `noConfirmation`, die angerechnete Zeit
    wächst zwischen zwei Roster-Lesungen weiter und fällt nie auf 0 zurück (nach Grace also überfällig; die Schwelle selbst
    prüft T5); (k) Twitch-initiiertes PART-Echo für einen bestätigten Kanal → unbestätigt, der nächste `EnsureJoinedAsync`
    sendet wieder. Fälle mit Timeout warten auf TwitchLibs echten Timer (≈ 7 s, im Testkopf begründet).
  - `RedactingTwitchClientLoggerFactoryTests`, `TwitchLibRawLineRedactionTests` unverändert grün.
- **Live:** Sonden-Zeile in `Channels` (erfundener, gültig geformter Login, der auf Twitch nicht existiert, z. B.
  `ep516probe` plus Ziffern; `IsBotActive = true`, ohne `TwitchChannelId`), `JOIN:<login>` publizieren → genau eine laute
  Zeile; **welcher Grund kommt, ist Messergebnis** (Protokoll). Zusätzlich ein, zwei weitere Kandidaten für eine stille
  Antwort (etwa ein Login mit 25 Zeichen); antwortet Twitch überall mit NOTICE, trägt T8 den stillen Fall über den
  Debug-Dekorierer. Danach ein echter Dev-Kanal in `Worker__Debug__SilentJoinChannels` → `noConfirmation`, eine Warning.
  Sonde bleibt für T4 oder wird verlassen.
- **Commit:** `feat(worker): record why a join is unconfirmed and log it once`.

### T4 — Roster-Vertrag erweitern

- **Lesen:** E3, 3.2; `IWorkerRosterReader.cs`, `WorkerRosterPublisher.cs`, `WorkerHealthReader.cs` (Roster-Leser),
  `WorkerRosterReaderTests.cs`, DECISIONS 2026-08-01, der Eintrag aus T3.
- **Liefert:** drei Felder an `WorkerRosterChannel` (am Ende, Default); `schemaVersion` samt Konstante an
  `WorkerRosterSnapshot`; Publisher setzt `ircIntent`, übernimmt Sekunden und Grund und schreibt die Marke immer;
  **DECISIONS-Absatz** „roster contract" (Felder, `ircIntent` statt Vereinigung als Bewertungsbasis, Schema-Marke und
  Mischbetrieb fail-closed, E3).
- **Grenzfälle:** alter Payload liest sich mit `false`/`null`/`schemaVersion = 0`; leeres Roster trägt die Marke;
  7TV-only-Zeile `ircIntent = false`; bestätigte Kanäle ohne Zusatzfelder außer `ircIntent`; Kappung 500 unverändert.
- **Tests:** `WorkerRosterReaderTests` (Infrastructure, echtes Redis): Rundreise mit allen Feldern und der Marke, leeres
  Roster mit Marke, alter Payload ohne sie.
- **Live:** `GET worker:roster` zeigt `schemaVersion: 1` und für die Sonde `ircIntent`, wachsende `ircUnconfirmedSeconds`, den Grund; nach
  `DEBUG:TWITCH-RECONNECT` wächst ihre Zahl nicht während der Trennung und fällt nicht zurück.
- **Commit:** `feat: publish accrued unconfirmed time and the join failure reason in the worker roster`.

### T5 — Bewertung: Optionen, Policy, Dienst mit Cache

- **Lesen:** E4, E5, E10, E11, 3.3; `ChannelCapacityOptions.cs`, `ServiceCollectionExtensions.cs:70-120` und `:396`,
  `TtlCache.cs` + Tests, `LogStatusReader.cs` (Singleton mit Scope), `WorkerHealthReader.cs:92` (`RosterTtl`),
  `ChannelService.cs:393-420` (`ListActiveChannelNamesAsync`) und `IChannelService.cs:285-303`.
- **Liefert:** `ListActiveChannelActivationsAsync` samt `ActiveChannelActivation`, `ListActiveChannelNamesAsync` daraus
  abgeleitet (ein Filter); `ChannelJoinHealthOptions` (Bindung, Validierung, Registrierung); Core-Typen aus 3.3;
  `ChannelJoinHealthPolicy`; `ChannelJoinHealthService` (Singleton, Scope für die Soll-Liste, Cache, Füllfrist, Startzeit).
  **DECISIONS-Absatz** „join health evaluation": Roster plus begrenzter Datenbankabgleich (warum Rev. 3 ohne ihn eine Lücke
  hatte), Schema-Marke, Stufen, Grace-Herleitung, Ausnahme `channelSuspended`/`banned`, Doppelalarm akzeptiert,
  Cache und Füllfrist.
- **Grenzfälle Policy:** Roster fehlt bei Api-Alter 179 s → `Starting`, 181 s → `Unavailable`; Roster älter als 180 s →
  wie fehlend; Grace genau und eine Sekunde darüber; 7TV-only-Zeile mit hoher Zahl wird von Stufe 2 ignoriert;
  überfälliger `channelSuspended` allein → `Healthy` mit `ExcludedCount = 1`, plus ein
  überfälliger `noConfirmation` → `Failing`; **still überfällig plus ein zweiter Kanal mit `concurrentChannelLimit` →
  `Failing`, beide Einträge alarmierend**; `BootRecoveryCompleted = false` ändert nichts; leeres Roster mit leerer
  Soll-Liste → `Healthy`. **Legacy-Roster (R3-H4):** frisch, `schemaVersion = 0`, leer oder mit Zeilen → `Unavailable`.
  **Ohne Wunsch (R3-H1):** aktiver Kanal ohne Roster-Zeile, Aktivierung Grace + 1 s vor `GeneratedAtUtc` → `Failing`;
  genau Grace → nicht überfällig; alte Aktivierung, aber Worker-Start erst vor 100 s → nicht überfällig (Boot); maßgeblich
  `TrackingResumedAt` vor `CreatedAt`; Kanal mit 7TV-only-Zeile zählt als ohne Wunsch; Groß-/Kleinschreibung; Soll-Liste
  `null` → `Unavailable`; `Truncated` → `Unavailable`.
- **Grenzfälle Dienst:** Abfrage der Soll-Liste wirft → „nicht auswertbar"; zwei gleichzeitige Aufrufe → eine Füllung; innerhalb 5 s gecacht (`TimeProvider`-Fake); Leser wirft
  → „nicht auswertbar", 5 s gecacht, **wiederholte Fehler: eine Füllung und eine Warning je 5 s**, nicht je Aufruf; Leser
  hängt → nach 2 s „nicht auswertbar"; abbrechender Wartender → seine Ausnahme, die Füllung läuft weiter und bedient den
  nächsten Aufrufer.
- **Tests:** `Unit/ChannelJoinHealthPolicyTests.cs`, `Unit/ChannelJoinHealthOptionsTests.cs`,
  `Unit/ChannelJoinHealthServiceTests.cs` (substituierter Roster-Leser und `IChannelService`); in
  `Integration/ChannelServiceTests.cs` die neue Abfrage gegen echtes Postgres (gleicher Filter wie die Namensliste:
  inaktiv, gesperrt, ausgeschlossen fehlen; `TrackingResumedAt` schlägt `CreatedAt`). Live in T6.
- **Commit:** `feat: evaluate the worker roster's unconfirmed time against a grace period`.

### T6 — Api: `/api/health/channels`, Policy, Admin-Roster, `TwitchJoinBudgetChannels` entfernen

- **Lesen:** E5, E6, E7 (Api-Teil), 3.3; `WorkerHealthEndpoints.cs`, `AdminEndpoints.cs:245-315` und `:537-570`,
  `WorkerCapacity.cs`, `Program.cs:183-230`, `RateLimitingOptions.cs`, `RateLimitPolicyNames.cs`, `appsettings.json`,
  `ApiFactory.cs`, `ApiHealthEndpointTests.cs`, `AdminDashboardActivityEndpointTests.cs`,
  `AdminRateLimitsEndpointTests.cs`, `RateLimitRejectionTests.cs:400-445`, `docs/Architectur.md:179`.
- **Liefert:** Route; Policy `PublicChannelHealth` (Name, Option, Registrierung neben `PublicHealth`, `appsettings.json`,
  Rate-Limit-Übersicht); `/api/admin/roster` nach 3.3 über `EvaluateAsync(snapshot, ct)`, samt `missingIntentOverdue`; `TwitchJoinBudgetChannels` gelöscht,
  Kommentar an `TwitchConcurrentChannelLimit` berichtigt; `Architectur.md:179` ergänzt; **DECISIONS-Absatz** „endpoint
  contract" (Route, Statuscodes, eigene Policy, nie im HEALTHCHECK).
- **Grenzfälle:** anonym; Körper leer; jede Statuszeile aus 3.3; `/api/health` bleibt bei überfälligem Kanal 200; getrennte
  Budgets (erschöpftes `PublicChannelHealth` → 429, `/api/health` derselben IP unberührt, und umgekehrt); Admin ohne
  Schnappschuss → `joinHealth.status` `unavailable` bzw. `starting`.
- **Tests (Api.Tests):** `ApiChannelHealthEndpointTests.cs` (Statuscodes und leerer Körper gegen substituierten Dienst, plus
  Fälle mit echtem Dienst über substituierten Leser und `IChannelService`: **Legacy-Snapshot ohne `schemaVersion` → 503**,
  aktiver Kanal ohne Wunsch nach Grace → 503); Regressionsfall in `ApiHealthEndpointTests`; Roster-Form in
  `AdminDashboardActivityEndpointTests`; getrennte Budgets in `RateLimitRejectionTests` (Muster `PublicLegalBudget_…`);
  `AdminRateLimitsEndpointTests`.
- **Live (deckt T5):** Api und Worker aus dem Worktree, Sonde aus T3 und ein Kanal in der Debug-Liste: nach Grace plus
  Roster-Takt 503, `/api/health` 200, `/api/admin/roster` listet beide mit Grund und `countsForAlarm`; Debug-Liste leeren
  und Worker neu starten → 200 nach Bestätigung (sofern die Sonde `channelSuspended` ist, sonst nach ihrem Leave);
  `redis-cli DEL worker:roster` → 503 nach ≤ 5 s, nach dem nächsten Publish wie vorher. **Ohne Wunsch (R3-H1):** Worker mit
  `SevenTv__ResyncIntervalSeconds=3600` neu starten, nach der Boot-Recovery einen Dev-Kanal per SQL aktivieren (ohne
  `JOIN:`-Kommando) → nach Grace plus Roster-Takt 503, der Kanal steht in `missingIntentOverdue`; Kanal per „Verlassen"
  zurücknehmen → 200. **Legacy (R3-H4):** `worker:roster` per `redis-cli SET` mit einem Payload ohne `schemaVersion`
  und frischem `generatedAtUtc` überschreiben (Worker gestoppt) → 503 nach ≤ 5 s. Elf Abrufe binnen einer Minute → der
  elfte 429, `/api/health` direkt danach 200; Sonde über „Verlassen" aufräumen (Audit `channel.leave`).
- **Commit:** `feat(api): add GET /api/health/channels and report unjoined channels in the admin roster`.

### T7 — Teil B Backend: EventId `ChannelCapacityReached`, Compose-Mapping der Decke

- **Lesen:** E8, E9; `ChannelService.cs:819-850`, `ChannelServiceCapacityTests.cs`, `SevenTvWriteClientLoggingTests.cs`,
  `LogFilterParser.cs:150-175`, `docker-compose.prod.yml` und `docker-compose.yml` (Api-Service, Muster
  `Channels__ExcludedChannelIds`), `.env.example`.
- **Liefert:** EventId an der bestehenden Warning (Text unverändert); Kommentar an `ChannelCapacityOptions.MaxActiveChannels`;
  **Mapping** `Channels__MaxActiveChannels=${CHANNELS_MAX_ACTIVE_CHANNELS:-80}` im Api-Service beider Compose-Dateien mit
  kurzem Kommentar, Platzhalter in `.env.example`; **DECISIONS-Absatz** „capacity demand signal" (E8, Mapping, E9 mit
  zweiteiligem Rückweg).
- **Tests:** `ChannelServiceCapacityTests` um die Logzeile. Compose: `docker compose -f docker-compose.prod.yml config`
  zeigt die Variable mit Default 80 und mit gesetztem Wert.
- **Live:** Api mit `Channels__MaxActiveChannels=<aktive Zahl>`; Join mit Nicht-Admin (`olaf_olaf_son` auf dem eigenen
  Kanal) → 409 `channel_capacity_reached`, eine Warning mit der EventId, mit `LOG_CAPTURE_ENABLED` im Log-Viewer auffindbar.
  Dazu der Compose-Weg lokal: `docker compose up -d --build --force-recreate api` mit gesetzter Variable →
  `ceilings.maxActiveChannels` zeigt den Wert.
- **Commit:** `feat: tag active-channel cap rejections and map the cap into the api container`.

### T8 — Live-Gate mit Messung bei Ziel 120 Kanälen

Lokal gegen echtes Twitch (anonym), Postgres, Redis, mit Produktions-Defaults (Grace 300 s, Resync 60 s); Overrides nur
Reconnect-Trigger, Debug-Liste und Log-Stufen für `EmotePurge.Worker.TwitchChatManager` und `TwitchLib.Client` (Sende- und
Bestätigungszeitpunkte). Abfragen von `/api/health/channels` alle 10 s (6/min). Dauer ≈ 1,5 h. Abschnitt „T8" im Protokoll.

1. **Kanalzahl:** Dev-Datenbank auf **120 aktive, echte Kanäle** bringen (Admin-Join, gestaffelt unter `Bookkeeping`
   120/min, Logins aus `helix/streams`); Liste nur lokal (scratchpad) für das Aufräumen. Abbruch bei wiederholten 7TV-429 →
   Zahl senken. **Wird 120 nicht erreicht:** gemessene N, gemessene Kosten je Kanal und je stiller Ablehnung, daraus `P(120)`
   als **ausgewiesene Hochrechnung**; die Anhebung auf 120 (E9) braucht dann die ausdrückliche Zustimmung des Betreibers
   zur Hochrechnung, der Zwischenschritt ≈ 90 nur, wenn N ≥ 90 gemessen ist.
2. **Boot ohne Fehlalarm:** Worker neu starten → bis 2 min nach Ende der Join-Runde durchgehend 200 (deckt auch den
   Datenbankabgleich während der Runde, E4 Stufe 3); Rundendauer messen.
3. **Reconnect:** `DEBUG:TWITCH-RECONNECT`, 10 min → 200; `P` gesund messen (Handshake bis letzte Bestätigung).
4. **Übertrag:** zweiten Reconnect etwa `P/2` nach dem Handshake des ersten auslösen → durchgehend 200; die größte
   `ircUnconfirmedSeconds` eines gesunden Kanals im Roster festhalten (erwartet ≤ 2P).
5. **Kontrollierter stiller JOIN:** Worker mit fünf der echten Kanäle in `Worker__Debug__SilentJoinChannels` neu starten →
   `P` mit K = 5 messen und je stiller Ablehnung den Abstand bis zum nächsten gesendeten JOIN (erwartet 6–7 s); nach Grace
   plus ≤ 65 s 503, `/api/health` 200, Admin-Roster listet die fünf mit `noConfirmation` und `countsForAlarm`; je Kanal eine
   Warning `TwitchJoinUnconfirmed`.
6. **Defizit über Trennungen:** zwei Reconnects → 503 bleibt; die Zahlen der fünf wachsen nicht während der Trennung und
   fallen nie zurück.
7. **Sonde (nicht existierender Login):** Grund festhalten; bei `channelSuspended` zählt sie nicht. Debug-Liste leeren,
   Worker neu starten → 200, sobald die übrigen bestätigt sind.
8. **Kaltstart:** Api und Worker stoppen, `redis-cli DEL worker:roster`, nur die Api starten → 200 bis 180 s, danach 503;
   Worker starten → 200 nach dem ersten Roster.
9. **Admin-Vertrag:** `GET /api/admin/roster` zeigt `joinHealth`, `ircUnjoined`, Totale (die Karte prüft T9).
10. **Aufräumen:** Sonde über „Verlassen", Zeile löschen; hinzugefügte Kanäle wieder verlassen; Endpunkt 200.
11. **Nicht live prüfbar:** der echte Limit-NOTICE an der anonymen Verbindung — belegt durch die aufgezeichnete Zeile (T3
    a, g).

- **Fertig:** Protokoll mit N, `P` gesund und mit K = 5, Übertrag, Kosten je stiller Ablehnung, Sondengrund,
  Doppel-Einreihungen ja/nein; committet (`docs: add live verification protocol for #516`). Übersteigt `max(2P, P + Runde)`
  bei 120 den Wert `Grace − 60 s`, entscheidet der Orchestrator vor T9 über die Anhebung (E4).

### T9 — Frontend: Kapazität, Join-Status, Status aus `joinHealth`, Minutentakt, Mocks

- **Lesen:** E6, E7, E8, 3.4; `admin-roster-card.ts` + Spec, `utilization-tone.ts` + Spec, `poll-while-visible.ts`,
  `admin-monitoring-page.ts:880-890` (Verwendung), `admin.model.ts:110-180`, `admin.service.spec.ts:130-170`,
  `admin-monitoring-page.template.spec.ts:140-160`, `admin-log-errors-row.ts:80-90`, beide Locale-Dateien,
  `web/e2e/support/mocks.ts:185-230`, `web/e2e/admin-monitoring.e2e.spec.ts`, `web/e2e/audit/ui-audit.audit.ts:1245-1265`;
  vorher `docs/UI-Designsprache.md` §11 und `DESIGN.md`.
- **Liefert:** Modelle; `utilizationTone` mit Schwellen; Kapazitätsblock (Balken gegen `maxActiveChannels`, 90/100, drei
  Texte, Twitch-Hinweis, Log-Link); Abschnitte Join-Status und Nicht im Worker (E6); Status mit Vorrang aus E6;
  Minuten-Nachladen per `pollWhileVisible`; Locale-Keys; Mocks und Audit; **DECISIONS-Absatz** „capacity display and roster
  card".
- **Grenzfälle:** `maxActiveChannels` 0 oder fehlend → kein Ton; genau 90 % → `warning`; genau 100 % → `danger`; über 100 %;
  Balken gekappt; unbekannter Grundcode → generischer Text; Zeile ohne Grund; Liste gekappt mit Total > Länge;
  `bootRecoveryCompleted = false` bei `failing` → `failing`; Name in `missingFromIrc` und `ircUnjoined` erscheint nur im
  Join-Status; Name aus `missingIntentOverdue` steht in „Nicht im Worker" markiert und zuerst, auch wenn er in
  `missingFromIrc` fehlt; verborgene Seite lädt nicht nach.
- **Tests (Vitest, Regel 12):** `utilization-tone.spec.ts`; `admin-roster-card.spec.ts` mit Fake-Timern und gesteuerter
  Sichtbarkeit (ein `getRoster` je Minute nur bei Sichtbarkeit), Statusvorrang, Kapazitäts-Computeds, Sortierung als
  Meldungsreihenfolge (Vertrag E6), Abschnittstrennung, zugänglicher Name und `aria-valuemax`. `admin.service.spec.ts` und
  `admin-monitoring-page.template.spec.ts` auf den neuen Vertrag. **E2E** in `admin-monitoring.e2e.spec.ts`: überfälliger
  stiller Kanal **und** ein Kanal mit `concurrentChannelLimit` stehen beide mit Grund und Status „fehlt"; ausgenommener Kanal
  zeigt „zählt nicht für den Alarm"; ein überfälliger Kanal ohne Wunsch steht markiert in „Nicht im Worker"; 90 % zeigt die
  Warnzeile.
- **Live:** E2E **ohne Api auf `:5151`**; danach Api und Worker mit einem Kanal in der Debug-Liste, `/admin/monitoring`:
  Kanal mit Grund und Status; Worker stoppen → Karte zeigt nach spätestens 180 s plus eine Minute `stale`, ohne Neuladen.
  Abschnitt „T9" im Protokoll.
- **Commit:** `feat(web): grade active channels against our own cap and list unjoined channels`.

### T10 — Doku-Abschluss

- **Lesen:** E4, E5, E8–E11; das Protokoll; `docs/Operations.md` („Admin dashboard", „Log viewer"), `CLAUDE.md`
  („Umsetzungsstand" Admin, „Bekannte offene Grenzen" erster Punkt), der DECISIONS-Eintrag.
- **Liefert (Operations.md englisch, CLAUDE.md deutsch):**
  - Abschnitt **„Channel join health"**: was der Endpunkt prüft (Roster mit Anrechnungsuhr plus Abgleich „aktiv ohne
    Wunsch"), Statuscodes, Doppelalarm bei Totalausfall und bei Postgres-Ausfall; **Reihenfolge:** den Monitor erst
    einrichten, wenn Api- **und** Worker-Image mit #516 laufen; ein Rollback nur des Workers (Roster ohne
    `schemaVersion`) macht ihn absichtlich rot, ebenso eine Api ohne #516 (404); **zweiter Uptime-Monitor** (HTTP auf `https://<host>/api/health/channels`, 60 s, Retries 2, **Resend
    Notification if Down 60× = stündlich**, 200 = up), nie im HEALTHCHECK; Grace mit Formel und **den T8-Messwerten**, Satz zu
    `SevenTv:ResyncIntervalSeconds`; gesperrte/gelöschte Kanäle (E11) samt Leave; Log-Filter `event:TwitchJoinRejected`,
    `event:TwitchJoinUnconfirmed`, `event:ChannelCapacityReached`.
  - Unterabschnitt **„Raising the active-channel cap"**: Voraussetzung und Zwischenschritt (E9), Compose-Mapping,
    Container neu erstellen, `ceilings.maxActiveChannels` prüfen, zweiteiliger Rückweg mit Prüfung je Kanalname.
  - `CLAUDE.md`: Satz in „Bekannte offene Grenzen" auf den neuen Stand; Admin-Zeile um einen Halbsatz.
  - `**Betrifft:**`-Zeile vollständig. Die Kuma-Einrichtung selbst gehört nach `infra-docs`.
- **Commit:** `docs: document channel join health and raising the channel cap`.

**Reihenfolge:** T1 → T2 → T3 → T4 → T5 → T6 → T7 → **T8 (Live-Gate)** → T9 → T10 → Gates.

---

## 5. Abdeckung der Akzeptanzkriterien

| AK (Issue) | Task |
|---|---|
| A1 „seit wann unbestätigt" + Grund im Roster | T1–T4 (als angerechnete Zeit bei bestehender Verbindung statt Zeitstempel, E1) |
| A2 NOTICE geloggt, Erkennung ohne NOTICE | T3 (a, b, g, h), T5, T3/T8 live |
| A3 Gnadenfrist, kein Fehlalarm bei Boot und Reconnect | T1, T5, T8 Schritte 2–4, 8 |
| A4 `/admin/monitoring` listet mit Grund und seit | T6, T9 |
| A5 neuer payloadfreier Endpunkt, `/api/health` unverändert | T5, T6 |
| A6 Tests + Live | T1–T9, T8 |
| A7 Operations.md + DECISIONS | T3–T7, T9, T10 |
| B1 Nenner `MaxActiveChannels`, 100 nur Referenz | T6, T9 |
| B2 `TwitchJoinBudgetChannels` und Kommentare entfernt | T6, T7, T9 |
| B3 Frühwarnung 90 %, Alarmentscheidung festgehalten | T9, T7 |
| B4 Ablehnungen geloggt und gezählt | T7, T9 |
| B5 Anhebung auf 120 nach A live | T7 (Mapping), T10 (Runbook); Umsetzung ist Betreiberhandgriff |

---

## 6. Gates (vor dem PR)

1. `dotnet test EmotePurge.slnx` grün (Docker, Testcontainers).
2. `npm --prefix web test -- --watch=false` grün.
3. `npm --prefix web run e2e` grün — nur ohne Api auf `:5151`.
4. `node scripts/coverage-local.mjs` (neue Dateien deutlich über 80 %; Näherung).
5. `dotnet format EmotePurge.slnx --verify-no-changes`, `npm --prefix web run format:check`, `npm --prefix web run lint`.
6. Protokoll vollständig (T2–T9).
7. Danach (Orchestrator): Code-Review und `/codex:review --model gpt-6.2-sol` über den Branch.

**Deploy:** keine Migration; Api und Worker gemeinsam, **Api-Container neu erstellen** (neues Compose-Mapping); erst wenn
beide Images laufen, zweiter Kuma-Monitor (E3, R3-H4); Anhebung erst nach der Voraussetzung aus E9.

---

## 7. Konfliktrisiken mit dem parallelen Bot-Branch (Worktree 487)

`WorkerHealthStatus`, `WorkerHealthPublisher`, `WorkerServiceRegistration.cs` und `Program.cs` des Workers bleiben
unberührt (Rev. 3 verschiebt nichts mehr).

| Datei | Risiko | Umgang |
|---|---|---|
| `docs/DECISIONS.md` (Kopf), `CLAUDE.md` | sicher textuell | beide Einträge behalten, von Hand zusammenführen |
| `docs/Operations.md`, `web/public/i18n/{de,en}.json` | gering | getrennte Abschnitte; wir ändern nur `admin.roster.*` |
| `TwitchChatManager.cs`, `ITwitchChatManager.cs`, `IChannelService.cs`, `ChannelService.cs` | gering | wer zuletzt merged, rebased |
| `docker-compose.prod.yml`, `docker-compose.yml`, `.env.example` | gering bis mittel, falls der Bot-Branch Variablen ergänzt | Zeilen nebeneinander |
| Api `Program.cs`, `RateLimitingOptions.cs`, `RateLimitPolicyNames.cs`, `appsettings.json`, `WorkerHealthEndpoints.cs`, `AdminEndpoints.cs` | mittel, wenn der Bot-Plan eine eigene öffentliche Policy und Admin-Routen bringt | gleiches Muster, wer zweiter merged, fügt seine Policy daneben |
| `tests/EmotePurge.Api.Tests/ApiFactory.cs`, `RateLimitRejectionTests.cs` | gering | — |

---

## 8. Betreiberentscheidungen (alle erledigt)

- **[F1]** Gesperrte und gelöschte Kanäle lösen keinen 503 aus (E11). Wer das überstimmt, ändert nur Stufe 2 der Policy.
- **[F2]** Die Decke geht auf 120 erst, wenn Teil A **mindestens 7 Tage** auf Prod lief und darin **mindestens ein Deploy und
  ein echter Twitch-Reconnect ohne Fehlalarm** lagen; wird es vorher eng, ist ein Zwischenschritt auf ≈ 90 erlaubt, sobald
  Teil A live ist (E9).
- **[F3]** `SevenTv:ResyncIntervalSeconds` wird auf Prod nicht gestreckt; wer es ändert, prüft die Grace neu (E10).
- **[F4]** Kuma erinnert stündlich, solange der Kanal-Monitor DOWN ist (E11, T10).
