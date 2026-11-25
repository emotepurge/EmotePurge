# Plan #516: Join-Fehler erkennen und melden, danach die Kanaldecke anheben

Erstellt am 2026-11-25 im Worktree `EmotePurge-wt-516` (Branch `feat/516-join-failure-detection`, Basis `origin/main` @
`e1d6210f`, PR #515 gemergt). Alle Codestellen dieses Plans sind an diesem Stand gelesen.
**Status: Revision 1, vor der adversarialen Prüfung (Sol). Nichts umgesetzt.**

Quellen: Issue #516 (Teil A, Teil B, Akzeptanzkriterien), DECISIONS 2026-11-25 („Twitch channel capacity …"), 2026-09-23
(Kanaldecke 80), 2026-08-05 (Z1-Rest/S3-35: payloadfreier `/api/health`, Kuma als Dead-Man's-Switch), 2026-08-02
(Schwellen-Leiter, „das Roster-Badge bekommt keine nearing-capacity-Stufe"), 2026-08-01 (Roster-Key `worker:roster`),
`CLAUDE.md` (Regeln 3, 4, 5, 7, 11, 12, 16, 19, Tests, Sprache), `web/.claude/CLAUDE.md`, die Messung der JOIN-Sonde vom
2026-09-08 (`~/projects/emotepurge-joinprobe/messungen-2026-09-08/lauf-B-named120.log.summary.txt`) und TwitchLib.Client
4.0.1, dekompiliert aus `~/.nuget/packages/twitchlib.client/4.0.1/lib/net10.0/TwitchLib.Client.dll`.

**Was dieser Plan trägt:** Befund, Entscheidungen mit verworfenen Alternativen, die Verträge (Typen, Felder, Route,
Statuscodes, Redis-Payload, Konfiguration), den Schnitt in Tasks mit Grenzfällen, Tests und Fertig-Prüfung, die
Live-Verifikation, Gates, Konfliktrisiken und die offenen Betreiberfragen. **Kein Code**; Namen und Signaturen nur als Form.
Jeder Implementer liest seinen Task, Abschnitt 2 und 3 **und** die genannten Bestandsdateien, bevor er schreibt.

**Lesehinweis:** **[E]** Planer-Entscheidung (widersprechbar) · **[P]** Präzisierung ohne Entscheidungsgehalt ·
**[F*n*]** offene Betreiberfrage (Abschnitt 8).

---

## 1. Befund am Code

1. **Wunsch statt Erfolg.** `TwitchChatManager._desiredChannels` (`ConcurrentDictionary<string, bool>`, Wert = „Twitch hat
   bestätigt") ist die einzige Join-Buchführung (`TwitchChatManager.cs:83`). Gesetzt wird `true` nur in
   `OnJoinedChannel` unter `_clientStateGate` mit Identitätsprüfung (`:991-1021`); `MarkAllChannelsUnconfirmed` setzt bei
   jedem Clientwechsel und `OnDisconnected` alles auf `false` (`:1168-1173`). 18 Zugriffe auf `_desiredChannels` in der Datei.
   Es gibt **keinen Zeitstempel**, seit wann ein Kanal unbestätigt ist, und **keinen Grund**.
2. **`OnFailureToReceiveJoinConfirmation` loggt nur** (`:920-931`), ist **nicht** an Client und Generation gebunden (`WireUpClient`, `:794`), und feuert in zwei Fällen (TwitchLib dekompiliert):
   - nach TwitchLibs eigenem 5-s-Timeout (`JoinChannelTimeout`, auf einem `System.Timers`-Thread), `Details = null`;
   - sofort bei `NOTICE` mit `msg-id=msg_channel_suspended` (`HandleChannelSuspended`), `Details` = Twitchs Text
     („This channel does not exist" bzw. „… has been suspended"). Ein `msg-id` ist in den Argumenten nicht enthalten.
3. **`msg_concurrent_channel_limit_reached` hat in TwitchLib keinen eigenen Zweig.** `HandleNotice` kennt u. a.
   `msg_channel_suspended`, `msg_banned` (→ `OnBanned`), `msg_suspended`; alles andere geht an `OnUnaccountedFor`
   (`Location = "NoticeHandling"`) — und **nur wenn kein Handler daran hängt**, an TwitchLibs eigenes
   `LogUnaccountedFor`, das `RedactingTwitchClientLoggerFactory` (#246) redigiert. Die Sonde vom 2026-09-08 hat die echte
   Zeile aufgezeichnet: `@msg-id=msg_concurrent_channel_limit_reached :tmi.twitch.tv NOTICE #<kanal> :You are connected to too
   many chat channels. …` — sie trägt den Kanal. Nach dem NOTICE folgt für denselben Kanal 5 s später TwitchLibs Timeout.
4. **`OnSendReceiveData` sieht jede empfangene Zeile vor dem Parsen** (`_client_OnMessage`: erst `OnSendReceiveData`, dann
   `IrcParser.ParseMessage`, dann `HandleIrcMessageAsync`). Der Handler ist heute heiß, ungebunden und allokationsfrei
   (`:1031-1048`). `OnReadLineTestAsync` dagegen umgeht `OnSendReceiveData` (geht direkt in `HandleIrcMessageAsync`).
5. **Sitzungsbeginn existiert schon:** `_sessionStartedUtcTicks` (gesetzt in `OnConnected`, gelöscht beim Start eines
   Wiederaufbaus). Nach `OnDisconnected` bleibt der Wert bis zum nächsten Wiederaufbau stehen (Backoff liest ihn) — „Sitzung
   läuft" ist also `IsConnected && ticks != 0`, nicht der Wert allein.
6. **Roster:** `WorkerRosterPublisher` (60 s, `do … while`, also sofort beim Start) baut `WorkerRosterSnapshot` mit
   `GeneratedAtUtc`, `ProcessStartedUtc`, `BootRecoveryCompleted`, `Truncated`, `Channels` (`WorkerRosterChannel`:
   `IrcJoinConfirmed`, `LastMessageUtc`, 7TV-Felder). Key `worker:roster`, TTL 180 s (`WorkerHealthReader.cs:79-92`), wird
   beim Stopp bewusst nicht gelöscht. Danach `LiveEvents.WorkerRoster` auf `live:events`. `TwitchRosterEntry` wird außerdem
   von `RosterPrunePolicy` und `CaptureGapTracker` (Wunsch-Erfassung, liest `JoinConfirmed`) gelesen.
7. **Konvergenznetz:** `SevenTvPeriodicResyncWorker` ruft je aktivem Kanal `EnsureJoinedAsync`, Takt
   `SevenTv:ResyncIntervalSeconds` (Default 60), die Runde läuft je Kanal mit 7TV-Sync (gemessen ≈ 0,85 s/Kanal, #272). Ein
   einmal gescheiterter Join wird also frühestens im nächsten Takt wiederholt; ein dauerhaft gesperrter Kanal bekommt jede
   Minute einen JOIN und heute jede Minute eine Warning.
8. **Api-Seite:** `/api/admin/roster` (`AdminEndpoints.cs:245-315`) vergleicht inline gegen
   `IAdminChannelQueryService.ListActiveChannelNamesAsync` (alle `IsBotActive`, **ohne** Sperr- und Widerspruchsfilter) und
   liefert `missingFromIrc` (nur Namen). Der Worker selbst joint nach `IChannelService.ListActiveChannelNamesAsync`
   (`ChannelService.cs:393`, **mit** Broadcaster-Sperre und `ExcludedChannelFilter`). `Channel.TrackingResumedAt` (sonst
   `CreatedAt`) ist der Zeitpunkt der letzten (Re-)Aktivierung; alle vier Aktivierungspfade setzen ihn
   (`ChannelService.cs:724`, `:856`, `ChannelIdentityService.cs:778`, `:992`).
9. **`/api/health`** (`WorkerHealthEndpoints.cs:57-67`): payloadfrei, 200 nur bei `WorkerHealthStatus.Derive(...) ==
   "connected"`, Policy `PublicHealth` (30/min je IP). Ziel des Api-Container-HEALTHCHECK (`Dockerfile:46-47`) und des
   Kuma-Monitors.
10. **Die 20:** `WorkerCapacity.TwitchJoinBudgetChannels` (`WorkerCapacity.cs:41`) → `ceilings.twitchJoinBudgetChannels`
    (`AdminEndpoints.cs:542`) → Nenner in `admin-roster-card.ts` (`rawBudgetPercent`, `budgetTone`, `budgetWarningKey`,
    `budgetWarningParams`, `budgetPercent`), Modell `admin.model.ts:137`, Keys `admin.roster.joinBudget*`, Specs
    (`admin-roster-card.spec.ts`, `admin.service.spec.ts`, `admin-monitoring-page.template.spec.ts:151`), Mocks
    (`web/e2e/support/mocks.ts:209`, `web/e2e/audit/ui-audit.audit.ts:1257`), Api-Test
    `AdminDashboardActivityEndpointTests.cs:177`. Der Doc-Kommentar von `TwitchConcurrentChannelLimit` behauptet noch, ein
    verifizierter Bot hebe die Decke (seit DECISIONS 2026-11-25 falsch).
11. **Kanaldecke:** `ChannelService.cs:839-846` loggt die Ablehnung bereits als Warning mit `{ChannelName}`,
    `{MaxActiveChannels}`, `{ActiveChannelCount}`, aber ohne `EventId`. Der Log-Viewer filtert mit `event:<Name>` auf den
    EventId-Namen (`LogFilterParser.cs:160`) und nimmt `q`/`range` als Query-Parameter (`admin-log-errors-row.ts:86-87`,
    Bereich `7d` vorhanden). Eigene EventIds benutzt der Code bisher nirgends.
12. **Schwellen-Leiter** `utilizationTone` (`shared/ui/utilization-tone.ts`, 80/95) teilt sich der Admin-Balken mit dem
    Slot-Budget der Emote-Domäne.

---

## 2. Entscheidungen

### E1 [E] Zustand je Kanal lebt im Worker, in einer reinen Klasse `ChannelJoinTracker`

`_desiredChannels` wird durch eine neue, reine, thread-sichere Klasse **`ChannelJoinTracker`** (`src/EmotePurge.Worker/`)
ersetzt, die je gewünschtem Kanal einen unveränderlichen Zustand hält: **bestätigt ja/nein, `UnconfirmedSinceUtc`,
`FailureReason`, `FailureAtUtc`**. Der Manager reicht die Uhrzeit hinein (`nowUtc`-Parameter), der Tracker kennt keine Uhr.
Ein eigenes internes `Lock`, nie ein `await` darin, ruft nie nach außen — damit ist er immer der innerste Riegel unter
`_intentGate` und `_clientStateGate` und kann keinen Zyklus bilden.

**Zeitstempel-Regel:** `UnconfirmedSinceUtc` wird **nur beim Übergang** nach „unbestätigt" gestempelt (neuer Wunsch;
bestätigt → unbestätigt durch Trennung/Wiederaufbau). Ein schon unbestätigter Kanal behält Stempel und Grund über Reconnects
hinweg — ein Kanal, der seit drei Stunden fehlt, zeigt drei Stunden, nicht „seit dem letzten Reconnect". Bestätigung löscht
Stempel, Grund und Grundzeit.

Verworfen: ein zweites Dictionary neben `_desiredChannels` (zwei Wahrheiten, die beim Leave auseinanderlaufen); die Zeit im
Api ableiten (die Api sieht nur Minutenschnappschüsse, nicht den Übergang); `TimeProvider` im Tracker (reine Funktion mit
`now` ist einfacher zu testen, der Manager hat ohnehin keine Uhr-Abstraktion).

### E2 [E] Gründe: NOTICE aus `OnSendReceiveData`, nie `OnUnaccountedFor`; feste Codes; Vorrang; einmal laut loggen

- **Erkennung:** Im bestehenden `OnSendReceiveData`-Handler (Richtung `Received`) genau **ein** zusätzlicher ordinaler
  Präfix-Test `@msg-id=`; nur bei Treffer läuft ein reiner Parser **`TwitchJoinNoticeParser`** (Worker), der `msg-id` und
  Kanal aus `… NOTICE #<kanal> :…` liest. Nie werfen, nie den Rohtext weitergeben.
- **Warum nicht `OnUnaccountedFor`:** Ein angehängter Handler schaltet TwitchLibs `LogUnaccountedFor` für **alle**
  unbekannten Zeilen ab (`OnUnaccountedFor?.Invoke(...) ?? UnaccountedFor(text)`) — die redigierte Warnkette aus #246 und
  ihre Tests würden still wirkungslos. Verworfen ebenso: `OnFailureToReceiveJoinConfirmation.Details` auswerten (Freitext,
  kein `msg-id`, und der Limit-NOTICE kommt dort gar nicht an).
- **Codes** (neue Konstantenklasse **`TwitchJoinFailureReasons`** in `src/EmotePurge.Core/Services/`, Muster wie
  `SevenTvSyncFailureReasons` daneben): `noConfirmation` (TwitchLibs 5-s-Timeout ohne NOTICE), `channelSuspended`
  (`msg_channel_suspended`, deckt auch „existiert nicht"), `concurrentChannelLimit` (`msg_concurrent_channel_limit_reached`),
  `banned` (`msg_banned`). Andere `msg-id` werden ignoriert (TwitchLib behandelt sie wie bisher).
- **Vorrang:** Ein konkreter NOTICE-Grund wird nie durch `noConfirmation` überschrieben (der Timeout folgt dem Limit-NOTICE
  5 s später); ein konkreter Grund überschreibt `noConfirmation` und einen anderen konkreten Grund. Ein Grund wird nur für
  einen **gewünschten, unbestätigten** Kanal gebucht; für einen bestätigten oder unbekannten Kanal verfällt er.
- **Identität:** Beide Grundquellen schreiben Manager-Zustand und folgen deshalb der Regel aus `WireUpClient`: Bindung an
  Client und Generation zur Verdrahtungszeit, Prüfung und Schreiben in **einem** `lock (_clientStateGate)`. Für
  `OnSendReceiveData` gilt die Sperre **nur im NOTICE-Zweig**; der Frame-Stempel bleibt lock- und allokationsfrei.
- **Logzeilen [E]:** eine strukturierte Zeile je **erstem Auftreten oder Wechsel** des Grundes (der Tracker meldet
  „neu/geändert" vs. „Wiederholung"), Wiederholungen nur auf Debug — sonst schreibt ein gesperrter Kanal jede Minute eine
  Warning. EventId-Namen: `TwitchJoinRejected` (NOTICE-Gründe; `concurrentChannelLimit` auf **Error**, damit es zusätzlich in
  der Fehlerzeile von `/admin/monitoring` erscheint — genau das Ereignis, auf das DECISIONS 2026-11-25 wartet; `channelSuspended`
  und `banned` auf Warning) und `TwitchJoinUnconfirmed` (`noConfirmation`, Warning beim ersten Mal). Die bisherige
  Warning „Twitch hat den Join … nicht bestätigt" geht darin auf. Geloggt werden Kanal und Code, nie Twitchs Text.

### E3 [E] Roster-Vertrag: additive Felder, keine Migration, Reihenfolge beim Deploy egal

| Ort | Feld (JSON camelCase) | Bedeutung |
|---|---|---|
| `TwitchRosterEntry` (Worker) | `UnconfirmedSinceUtc`, `JoinFailureReason`, `JoinFailureAtUtc` | neue optionale Parameter **am Ende mit Default `null`**, damit `RosterPrunePolicy`, `CaptureGapTracker` und ihre Tests unverändert kompilieren |
| `WorkerRosterChannel` (Core) | `ircUnconfirmedSinceUtc`, `ircJoinFailureReason`, `ircJoinFailureAtUtc` | `null`, solange bestätigt bzw. kein Grund; Grund ist ein Code aus `TwitchJoinFailureReasons` |
| `WorkerRosterSnapshot` (Core) | `ircSessionStartedUtc` | Handshake der laufenden Sitzung, `null` ohne laufende Sitzung (`!IsConnected` oder kein „004") |

Neue `ITwitchChatManager`-Eigenschaft **`CurrentSessionStartedUtc`**: liefert `null`, wenn nicht verbunden (Befund 5), sonst
den Handshake-Zeitpunkt. Mischbetrieb: neue Api + alter Worker liest `ircSessionStartedUtc = null` → „keine Sitzung" → nie
überfällig (fail-open bis zum Worker-Deploy); alter Api + neuer Worker ignoriert die Zusatzfelder. Api und Worker kommen
ohnehin im selben Deploy.

### E4 [E] Bewertung in der Api: reine Policy, Soll aus der Worker-Sicht der Datenbank, Stichzeit ist der Schnappschuss

- **Soll-Liste:** neue Methode **`IChannelService.ListActiveChannelActivationsAsync`** → `(ChannelName, ActivatedAtUtc =
  TrackingResumedAt ?? CreatedAt)`, **dieselben Filter** wie `ListActiveChannelNamesAsync`; Letztere wird darauf
  zurückgeführt (Projektion), damit die Filter nur einmal existieren. Verworfen: `IAdminChannelQueryService` (ohne Sperr-
  und Widerspruchsfilter — ein absichtlich nicht gejointer Kanal würde dauerhaft alarmieren).
- **Policy** **`ChannelJoinHealthPolicy`** (Infrastructure, rein, statisch) bewertet zur Stichzeit `t = roster.GeneratedAtUtc`
  (nicht „jetzt": der Schnappschuss kann bis zu 180 s alt sein, und gemessen wird, was der Worker damals wusste):
  1. Schnappschuss fehlt → `RosterUnavailable`.
  2. `BootRecoveryCompleted == false` und `t − ProcessStartedUtc ≤ BootRecoveryMax` → `Starting`, nichts überfällig.
     Darüber hinaus wird normal bewertet (eine hängende Boot-Recovery darf nicht ewig verschweigen).
  3. `ircSessionStartedUtc == null` → `NoSession`, nichts überfällig (das meldet `/api/health`, nicht wir).
  4. Je Soll-Kanal (Namensvergleich `OrdinalIgnoreCase`):
     - im Roster und bestätigt → in Ordnung;
     - im Roster, unbestätigt → **geschuldet seit** `max(ircUnconfirmedSinceUtc ?? ircSessionStartedUtc, ircSessionStartedUtc)`;
     - nicht im Roster → geschuldet seit `max(ActivatedAtUtc, ProcessStartedUtc)`; bei `Truncated` nie überfällig;
     - **überfällig**, wenn `t − geschuldetSeit > Grace` (strikt).
  5. Mindestens ein überfälliger Kanal → `Failing`, sonst `Healthy`.
- **Warum „geschuldet seit Sitzungsbeginn":** Nach jedem Reconnect rejoint der Worker gedrosselt 0,6 s × N; die Frist darf
  erst laufen, wenn der Worker wieder verbunden ist, sonst alarmiert jeder Reconnect. Verbindungsabbruch, Backoff und
  Twitch-Ausfall gehen so vollständig an `/api/health`.
- **Grace-Default 360 s [E]:** deckt bei N = 120 die Rejoin-Runde (0,6 s × 120 + 5 s ≈ 77 s), einen verpassten Takt des
  Konvergenznetzes (60 s) und dessen Lauf (0,85 s × 120 ≈ 102 s) = ≈ 239 s, plus Reserve für eine zweite Wiederholung.
  Faustformel für Operations.md: `Grace ≥ 1,45 s × N + 5 s + SevenTv:ResyncIntervalSeconds + Reserve`. Wer den Resync-Takt
  streckt (Kommentar in `SevenTvPeriodicResyncWorker` sieht 300 s vor), muss die Grace mitziehen.
- **Bekannte, akzeptierte Grenze:** Flattert die Verbindung schneller als die Grace, setzt jeder neue Sitzungsbeginn die
  Frist zurück und der Kanal-Monitor bleibt still — Flattern ist Sache der Verbindungsüberwachung.

Verworfen: Policy im Api-Projekt (`Api/Health`) — sie braucht die Datenbank-Soll-Liste und gehört nach Regel 11 getestet in
die Infrastructure; Überfälligkeit im Worker entscheiden (Grace in zwei Prozessen, und nur die Api kennt die Datenbank);
zwei aufeinanderfolgende Schnappschüsse als Anker für fehlende Kanäle (Zustand in der Api, bricht bei Neustart).

### E5 [E] Neuer Endpunkt `GET /api/health/channels`

- Anonym, kein Körper, nur Statuscode, außerhalb der Admin-Gruppe, in `WorkerHealthEndpoints.cs` neben `/api/health`.
- **Ablauf:** (a) Health-Schnappschuss lesen; fehlt er oder ist `WorkerHealthStatus.Derive(...) != "connected"` → **200**.
  (b) Sonst `IChannelJoinHealthService.EvaluateAsync`; `Failing` oder `RosterUnavailable` → **503**, alles andere → **200**.
- **Prinzip, das Doppelalarme verhindert:** Der Kanal-Monitor meldet nur, was `/api/health` nicht meldet. Ist der Worker
  tot oder getrennt, ist Monitor 1 rot und Monitor 2 bleibt grün — **ein** Telegram-Alarm für **eine** Ursache. Fehlt nur das
  Roster, während die Health „connected" sagt (Roster-Publisher hängt), ist die Kanalüberwachung blind → 503: das sieht
  `/api/health` nicht.
- `/api/health` bleibt **unverändert**, ebenso der Container-HEALTHCHECK. **Der neue Endpunkt darf nie in einen
  HEALTHCHECK** — ein gesperrter Kanal machte sonst den Api-Container „unhealthy".
- **Rate-Limit [E]:** dieselbe Policy `PublicHealth`. Kuma ist derselbe Maschinen-Aufrufer mit derselben Kadenz (2 Abrufe/min
  aus einer IP bei 30/min); die Trennung aus #247 galt unterschiedlichen Aufrufern (Browser vs. Maschine). Verworfen: eigene
  Policy (mehr Konfigurationsfläche, Konflikt mit #487 PR 3 in `Program.cs`/`RateLimitingOptions`); Ergebnis-Cache (eine
  Redis-Lesung plus eine kleine indizierte Abfrage über ~65–120 Zeilen; der Roster ändert sich ohnehin nur minütlich, ein
  Cache verzögert nur den Alarm).
- Verworfen: Kanalzustand in `/api/health` einrechnen (ändert dessen Bedeutung „Seite down", HEALTHCHECK würde rot);
  Payload mit Kanalnamen (öffentlich, verrät den Bestand).

### E6 [E] Admin-Monitoring: in der bestehenden Roster-Karte, Aktualisierung per vorhandenem SSE-Ereignis

- `/api/admin/roster` ruft künftig den Dienst (eine Roster-Lesung, der Bericht trägt den Schnappschuss mit) und liefert
  zusätzlich `joinHealth` und `ircUnjoined` (Vertrag in 3.3); **`missingFromIrc`/`missingFromIrcTotal` entfallen** (ersetzt,
  nicht doppelt geführt).
- Die Karte zeigt je Kanal: Link auf `/admin/channels/{name}`, Grund (übersetzt, unbekannter Code → generischer Text mit
  Code), „unbestätigt seit" bzw. „nicht im Worker, erwartet seit", und **als Text** „überfällig" vs. „wartet auf
  Bestätigung" (Farbe nie allein, WCAG 1.4.1). Sortierung: überfällig zuerst, dann nach „geschuldet seit" aufsteigend.
- Neuer Status `failing` (Ton `danger`) vor `incomplete`; `starting` und `stale` behalten Vorrang (wie heute).
- Aktualisierung: wie bisher über `LIVE_EVENT_TYPES.workerRoster` (der Worker publiziert jede Minute) — kein Polling.
- Die Kanal-Detailseite zeigt Grund und „seit" unter dem IRC-Marker (die Felder kommen mit der Roster-Zeile ohnehin an).
- [P] `trackedChannelCount` bleibt die Admin-Zählung (alle `IsBotActive`); die Unjoined-Liste nutzt die Worker-Sicht (E4).
  Sie unterscheiden sich nur im Übergangszustand „aktiv, aber gesperrt/widersprochen" bis zum nächsten Identitätsabgleich.

### E7 [E] Teil B, Anzeige: Nenner ist unsere Decke, Twitchs 100 nur Referenz, Frühwarnung bei 90 %

- Der Balken zeigt `trackedChannelCount / ceilings.maxActiveChannels`. `TwitchJoinBudgetChannels` entfällt vollständig
  (Konstante, API-Feld, Modell, Keys, Specs, Mocks, Api-Test, Kommentare).
- **Schwellen dieses Balkens: `warning` ab 90 %, `danger` ab 100 %** (bei 100 % lehnt die Api neue Kanäle ab). Umsetzung als
  optionales Schwellenargument von `utilizationTone`; der Default (80/95) bleibt, das Slot-Budget ist unberührt.
  Verworfen: die geteilte Leiter ändern (trifft die Emote-Domäne); eine Kopie der Funktion im Admin-Bereich.
- Textzeilen ab `warning` (WCAG 1.4.1): nahe der Grenze / Grenze erreicht (neue Kanäle werden abgelehnt, globale Admins
  ausgenommen) / über der Grenze (nur durch Admin-Joins möglich).
- Twitchs 100 erscheint als Hinweistext mit Herkunft („gilt für benannte Konten; für die anonyme Verbindung am 2026-09-08 bis
  120 ohne Ablehnung gemessen, undokumentiert"), nicht als Balken.
- **Kein Alarm für 90 % [E]:** Die Decke verliert keine Daten, sie lehnt nur neue Kanäle mit Begründung ab — ein
  Nachfragesignal, kein Ausfall. Das Roster-Badge bekommt weiterhin keine Kapazitätsstufe (DECISIONS 2026-08-02).

### E8 [E] Abgelehnte Joins zählen: EventId plus Log-Viewer, keine neue Tabelle

Die vorhandene Warning in `ChannelService.JoinAsync` bekommt die EventId **`ChannelCapacityReached`**. Gezählt wird im
Log-Viewer (`event:ChannelCapacityReached`, Histogramm, Bereich 7 Tage); die Kapazitätszeile der Roster-Karte verlinkt genau
diese Abfrage (`/admin/logs?q=event:ChannelCapacityReached&range=7d`). Verworfen: eigener Zähler in Redis oder Postgres
(neuer Zustand, neue Aufbewahrungsfrage, für ein grobes Nachfragesignal ohne Mehrwert); Audit-Eintrag je Ablehnung (Audit ist
für Schreibhandlungen). Grenze: ohne `LogCapture:Enabled` (Default für fremde Betreiber) gibt es nur stdout — in
Operations.md benannt. Die Log-Aufbewahrung von 7 Tagen reicht für die Frage „stoßen wir an?".

### E9 [E] Anhebung auf 120 ist ein Betreiberschritt, der Default bleibt 80

Kein Code-Default ändert sich. 80 bleibt für fremde Betreiber unter Twitchs dokumentierter 100 (deren anonymes Verhalten
ist ungemessen). Operations.md beschreibt den Schritt (`Channels__MaxActiveChannels=120` nach Teil A live, siehe [F2]) und
die Nachmessung nach dem ersten Neustart: Rejoin-Dauer (≈ 72 s), Boot-Recovery (≈ 1,7 min), 7TV-EventAPI-Grenze bei ≈ 250
außer Reichweite, und die Grace-Formel aus E4.

### E10 [E] Konfiguration

Neue Klasse **`ChannelJoinHealthOptions`**, Abschnitt `Channels:JoinHealth`, Muster wie `ChannelCapacityOptions` (POCO,
eager gebunden und validiert in `AddEmotePurgeInfrastructure`, Singleton):

| Schlüssel | Default | Validierung | Env-Variable |
|---|---|---|---|
| `Channels:JoinHealth:GraceSeconds` | 360 | 60 … 3600 | `Channels__JoinHealth__GraceSeconds` |
| `Channels:JoinHealth:BootRecoveryMaxSeconds` | 900 | 60 … 7200 | `Channels__JoinHealth__BootRecoveryMaxSeconds` |

Keine weitere Schwelle: „Roster veraltet" ist der TTL-Ablauf (180 s), wie im Frontend. Kein Eintrag in
`docker-compose*.yml` nötig (Defaults); Operations.md nennt die Variablen.

---

## 3. Verträge im Überblick

### 3.1 Worker-intern

- `ChannelJoinTracker` (öffentlich im Worker, keine DI-Registrierung, vom Manager besessen): `RecordIntent(name, nowUtc)`
  (neu → unbestätigt mit Stempel; vorhanden → unverändert), `MarkConfirmed(name)` (nur vorhanden + unbestätigt, fügt nie
  ein), `MarkAllUnconfirmed(nowUtc)`, `RecordFailure(name, reason, nowUtc)` → Ergebnis „neu oder geändert" / „Wiederholung" /
  „ignoriert", `Remove(name)`, `Contains`, `IsConfirmed`, `Names` (Momentaufnahme), `Snapshot()` (nach Name ordinal
  sortiert, für `GetRoster`). Namen `OrdinalIgnoreCase`.
- `TwitchJoinNoticeParser.TryParse(line, out reasonCode, out channelName)`: `true` nur für die drei NOTICE-Codes aus E2 mit
  Kanal; Kanal ohne `#`, kleingeschrieben; wirft nie.
- `ITwitchChatManager.CurrentSessionStartedUtc` (E3). `TwitchRosterEntry` erweitert (E3).

### 3.2 Redis `worker:roster` (additiv, E3)

`channels[].ircUnconfirmedSinceUtc`, `channels[].ircJoinFailureReason`, `channels[].ircJoinFailureAtUtc`,
`ircSessionStartedUtc` auf Schnappschuss-Ebene. `null`-Felder werden wie bisher weggelassen (`WhenWritingNull`).

### 3.3 Api

- `GET /api/health/channels`: anonym, `PublicHealth`, Körper leer, 200/503 nach E5; `Cache-Control: no-store` kommt aus der
  bestehenden Middleware.
- `IChannelJoinHealthService.EvaluateAsync(ct)` (Core-Interface, Infrastructure-Implementierung, scoped) →
  `ChannelJoinHealthReport { Status, EvaluatedAtUtc?, GraceSeconds, Roster (der gelesene Schnappschuss oder null), Entries,
  OverdueCount }`; `ChannelJoinHealthStatus` = `Healthy | Starting | NoSession | RosterUnavailable | Failing`;
  `ChannelJoinHealthEntry { ChannelName, InRoster, UnconfirmedSinceUtc?, OwedSinceUtc?, Reason?, ReasonAtUtc?, Overdue }`
  (alle unbestätigten bzw. fehlenden Soll-Kanäle, auch die innerhalb der Frist).
- `IChannelService.ListActiveChannelActivationsAsync(ct)` → `IReadOnlyList<ActiveChannelActivation(ChannelName,
  ActivatedAtUtc)>` (Core-Record).
- `/api/admin/roster`, **beide Zweige**: `joinHealth: { status: 'healthy'|'starting'|'noSession'|'rosterUnavailable'|'failing',
  graceSeconds }`. Zweig mit Schnappschuss zusätzlich: `ircUnjoined: [{ channelName, inRoster, unconfirmedSinceUtc|null,
  owedSinceUtc|null, reason|null, reasonAtUtc|null, overdue }]` (gekappt auf `DeficitListLimit` = 50, Sortierung E6),
  `ircUnjoinedTotal`, `ircOverdueTotal`. Entfällt: `missingFromIrc`, `missingFromIrcTotal`,
  `ceilings.twitchJoinBudgetChannels`. Bleibt: `ceilings.twitchConcurrentChannelLimit` (Referenz), `ceilings.maxActiveChannels`.
  Der Status wird im Endpunkt explizit auf den camelCase-String abgebildet (Enum nie als Zahl).
- Keine neuen `ApiErrorCodes` (Regel 7 nicht berührt).

### 3.4 Frontend

`RosterCeilings` ohne `twitchJoinBudgetChannels`; `AdminRoster` mit `joinHealth`, `ircUnjoined`, `ircUnjoinedTotal`,
`ircOverdueTotal`, ohne `missingFromIrc*`; `RosterChannel` mit den drei neuen optionalen Feldern; `utilizationTone(percent,
thresholds?)`. Locale-Keys: entfallen `admin.roster.joinBudget`, `joinBudgetWarning`, `joinBudgetCritical`,
`joinBudgetExceeded`, `joinBudgetHint`, `missingIrc`; neu `admin.roster.capacity.*` (Label, drei Warnstufen, Hinweis, Link),
`admin.roster.unjoined.*` (Überschrift mit Zahl, überfällig, wartet, seit, nicht im Worker),
`admin.roster.joinFailure.reason.{noConfirmation,channelSuspended,concurrentChannelLimit,banned,unknown}`,
`admin.roster.status.failing`; `twitchLimitHint` neu formuliert. de und en in Parität.

---

## 4. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander** (geteiltes `obj/`, geteilte Locale-Dateien). Jeder
Backend-Task endet mit grünem `dotnet build EmotePurge.slnx`, `dotnet format EmotePurge.slnx --verify-no-changes` und den
Tests seines Testprojekts, jeder Frontend-Task mit `npm --prefix web test -- --watch=false`, `lint` und `format:check`. Jeder
Task ein eigener Conventional Commit (englisch). Member-Reihenfolge nach Regel 19. Neue Kommentare und Logtexte englisch.
**Ein** DECISIONS-Eintrag (englisch, ganz oben) „Join-failure detection and capacity display (#516)": T3 legt ihn an, T5 und
T6 ergänzen Absatz und `**Betrifft:**`-Zeile im selben Commit (Regel 3). `docker compose` nie aus dem Worktree.

### T1 — Reine Worker-Bausteine: `ChannelJoinTracker`, `TwitchJoinNoticeParser`, `TwitchJoinFailureReasons`

- **Lesen:** E1, E2, 3.1; `TwitchChatManager.cs` (alle `_desiredChannels`-Stellen, `TallyRejoin`, `AllConfirmed`,
  `MarkAllChannelsUnconfirmed`), `ChannelLeaveLedger.cs` und Tests als Stilvorbild, `SevenTvSyncFailureReasons` (Muster der
  Konstantenklasse), `TwitchTagKeyAllowlistTests.cs:320` (NOTICE-Zeilenform).
- **Liefert:** die drei Typen; `TwitchJoinFailureReasons` in `src/EmotePurge.Core/Services/` (reine Konstanten), die anderen beiden in
  `src/EmotePurge.Worker/`. Noch **keine** Verdrahtung.
- **Grenzfälle Tracker:** Wunsch auf bestätigtem Kanal ändert nichts; Bestätigung nach `Remove` fügt nicht ein; Bestätigung
  löscht Stempel und Grund; `MarkAllUnconfirmed` stempelt nur vorher bestätigte, behält Stempel und Grund der schon
  unbestätigten; Grund auf bestätigtem oder unbekanntem Kanal → „ignoriert"; Vorrangtabelle aus E2 vollständig
  (`noConfirmation` nach konkretem Grund → Grund bleibt, Ergebnis „Wiederholung"); gleicher Grund zweimal → „Wiederholung",
  Grundzeit aktualisiert; anderer konkreter Grund → „geändert"; `Remove` und erneuter Wunsch → frischer Stempel; Groß-/
  Kleinschreibung; Momentaufnahme sortiert; parallele Aufrufe aus mehreren Threads ohne Ausnahme und ohne verlorene
  Übergänge.
- **Grenzfälle Parser:** die drei Codes mit Kanal; aufgezeichnete Limit-Zeile (Kanal durch einen erfundenen ersetzt);
  `msg-id` nicht an erster Stelle → `false` (dokumentiert, die Erkennung hängt nicht am Grund); `NOTICE * :…` ohne Kanal;
  anderer `msg-id` (`emote_only_on`, `msg_ratelimit`); PRIVMSG; abgeschnittene Zeile; leerer String; Großbuchstaben im
  Kanal → klein.
- **Tests:** `tests/EmotePurge.Worker.Tests/ChannelJoinTrackerTests.cs`, `TwitchJoinNoticeParserTests.cs` (container-frei,
  Regel 11). Nur erfundene Kanäle.
- **Fertig:** Build, Format, `dotnet test tests/EmotePurge.Worker.Tests` grün. Commit `feat(worker): add a per-channel join
  tracker and a join-notice parser`.

### T2 — `TwitchChatManager` auf den Tracker umstellen, Gründe erfassen

- **Lesen:** E1, E2, E3 (nur `CurrentSessionStartedUtc`, `TwitchRosterEntry`), Befund 2–5; `TwitchChatManager.cs` vollständig
  (insbesondere die Klassen-Doku zu `WireUpClient`, die Gate-Regeln, `OnSendReceiveData`), `ITwitchChatManager.cs`,
  `RedactingTwitchClientLoggerFactory.cs`, `TwitchChatManagerLeaveRaceTests.cs`, `RewardRedemptionTwitchLibTests.cs` (Muster
  „TwitchLib-gebundener Test").
- **Liefert:** `_desiredChannels` vollständig durch `ChannelJoinTracker` ersetzt, Verhalten aller bisherigen Pfade gleich
  (Intent vor JOIN, Re-Check nach dem Gate, Leave-Ledger, `TallyRejoin`, `AllConfirmed`, `EnsureJoinedAsync`-Abkürzung);
  `OnJoinedChannel` → `MarkConfirmed`; Reset beim Clientwechsel und in `OnDisconnected` → `MarkAllUnconfirmed(now)`;
  `OnFailureToReceiveJoinConfirmation` an Client und Generation gebunden, Identitätsprüfung und `RecordFailure(noConfirmation)`
  in einem `lock (_clientStateGate)`; `OnSendReceiveData` an Client und Generation gebunden, NOTICE-Zweig nach E2;
  Logzeilen mit EventIds und Dedupe nach E2; `GetRoster` füllt die neuen Felder; `CurrentSessionStartedUtc`.
- **Grenzfälle:** NOTICE eines bereits ersetzten Clients bucht keinen Grund; Timeout-Ereignis vom Timer-Thread während
  eines Clientwechsels; Limit-NOTICE gefolgt vom 5-s-Timeout → ein Grund `concurrentChannelLimit`, **eine** laute Zeile;
  gesperrter Kanal mit minütlichem Retry → eine Warning, danach Debug; Leave zwischen NOTICE und Buchung → nichts gebucht;
  `OnSendReceiveData` bleibt ohne Sperre und ohne Allokation, solange die Zeile nicht mit `@msg-id=` beginnt, und wirft nie
  (eine Ausnahme dort trennt TwitchLibs Multicast-Kette); `CurrentSessionStartedUtc` ist `null` vor dem ersten Handshake,
  nach `OnDisconnected` und während eines Wiederaufbaus.
- **Tests (Worker.Tests):**
  - `TwitchChatManagerJoinStateTests.cs` am nie verbundenen Manager (wie `TwitchChatManagerLeaveRaceTests`): Join stempelt
    `UnconfirmedSinceUtc`, Leave entfernt, Ensure nach Leave mit altem Stempel legt nichts an, `CurrentSessionStartedUtc`
    ist `null`.
  - `TwitchJoinNoticeTwitchLibTests.cs` (bewusst TwitchLib-gebunden, Begründung im Kopf wie bei
    `RewardRedemptionTwitchLibTests`): über `OnReadLineTestAsync` an einem initialisierten, nicht verbundenen `TwitchClient`
    — `msg_channel_suspended` löst `OnFailureToReceiveJoinConfirmation` mit Kanal und Details aus;
    `msg_concurrent_channel_limit_reached` löst ihn **nicht** aus. Pinnt die beiden TwitchLib-Fakten, auf denen der Vorrang
    beruht.
  - Bestehende Worker-Tests (`TwitchChatManagerLeaveRaceTests`, `CaptureGapTrackerTests`, `RosterPrunePolicyTests`,
    `WishCaptureWorkerTests`, `WorkerBootSequenceTests`) unverändert grün.
  - Die Verdrahtung selbst (NOTICE über `OnSendReceiveData` am echten Socket) wird live verifiziert (T8), wie Regel 11 es
    für Transportklassen vorsieht.
- **Fertig:** Build, Format, Worker.Tests grün. Commit `feat(worker): track since when a join is unconfirmed and why`.

### T3 — Roster-Vertrag erweitern

- **Lesen:** E3, 3.2; `IWorkerRosterReader.cs`, `WorkerRosterPublisher.cs`, `WorkerHealthReader.cs` (Roster-Teil),
  `WorkerRosterReaderTests.cs`, DECISIONS 2026-08-01 (Roster-Key).
- **Liefert:** die neuen Felder an `WorkerRosterChannel` (am Ende, Default `null`) und `WorkerRosterSnapshot`
  (`IrcSessionStartedUtc`, Default `null`, **vor** dem `Channels`-Parameter einordnen nur, wenn alle Aufrufer benannt
  übergeben — sonst ans Ende; der Implementer prüft die Aufrufer); Abbildung im Publisher (IRC-Seite gewinnt wie bisher,
  7TV-only-Zeilen tragen `null`); den **DECISIONS-Eintrag** (Roster-Felder, Zeitstempel-Regel E1, Gründe und Vorrang E2,
  Mischbetrieb).
- **Grenzfälle:** Payload eines alten Workers (ohne neue Felder) liest sich mit `null`; Kanal nur auf der 7TV-Seite;
  `WhenWritingNull` lässt die Felder für bestätigte Kanäle weg.
- **Tests:** `WorkerRosterReaderTests` (Infrastructure, echte Redis) — Rundreise mit allen neuen Feldern, alter Payload ohne
  sie.
- **Fertig:** Build, Format, Worker.Tests und `dotnet test tests/EmotePurge.Infrastructure.Tests --filter WorkerRoster`
  grün. Commit `feat: publish unconfirmed-since and the join failure reason in the worker roster`.

### T4 — Bewertung: Optionen, Policy, Dienst, Soll-Liste mit Aktivierungszeit

- **Lesen:** E4, E10, 3.3 (Dienst, Soll-Liste); `ChannelService.cs:380-420` (Filter), `ChannelCapacityOptions.cs`,
  `ServiceCollectionExtensions.cs:70-80` und die Registrierung von `IWorkerRosterReader`, `IChannelService.cs`,
  `ChannelServiceCapacityTests.cs` (Testaufbau), `ChannelServiceBroadcasterLockTests.cs`.
- **Liefert:** `ChannelJoinHealthOptions` (Bindung, `Validate`, Registrierung); Core-Typen aus 3.3;
  `ListActiveChannelActivationsAsync` mit `ListActiveChannelNamesAsync` als Projektion darauf; `ChannelJoinHealthPolicy`
  (rein); `ChannelJoinHealthService` (liest Roster und Soll-Liste, ruft die Policy, scoped, registriert in
  `AddEmotePurgeInfrastructure`).
- **Grenzfälle Policy:** jede Stufe aus E4 einzeln; Grenze genau bei `Grace` (nicht überfällig) und eine Sekunde darüber;
  Kanal vor Sitzungsbeginn unbestätigt → Frist ab Sitzungsbeginn; Kanal nach Sitzungsbeginn hinzugekommen → ab eigenem
  Stempel; fehlender Kanal nach frischer Aktivierung (jünger als der Schnappschuss) → nicht überfällig; fehlender Kanal bei
  `Truncated`; Boot-Recovery innerhalb und jenseits von `BootRecoveryMax`; Roster-Kanal, der nicht im Soll steht (zählt nicht);
  Groß-/Kleinschreibung zwischen Roster und Datenbank; leere Soll-Liste → `Healthy`; unbestätigte Zeile ohne
  `ircUnconfirmedSinceUtc` (alter Worker) bei laufender Sitzung → ab Sitzungsbeginn.
- **Grenzfälle Soll-Liste:** gesperrter Kanal (`BroadcasterChannelLocks`) und widersprochener Kanal fehlen in beiden Listen
  gleich; `TrackingResumedAt` gesetzt vs. `null` (→ `CreatedAt`); beide Methoden liefern dieselben Namen in derselben
  Reihenfolge.
- **Tests:** `tests/EmotePurge.Infrastructure.Tests/Unit/ChannelJoinHealthPolicyTests.cs`,
  `Unit/ChannelJoinHealthOptionsTests.cs` (Validierung), `Integration/ChannelServiceActiveActivationsTests.cs` (Postgres),
  `Integration/ChannelJoinHealthServiceTests.cs` (Postgres plus substituierter `IWorkerRosterReader`).
- **Fertig:** Build, Format, Infrastructure.Tests der neuen Klassen und die bestehenden `ChannelService*`-Tests grün.
  Commit `feat: evaluate desired against confirmed joins with a grace period`.

### T5 — Api: `/api/health/channels`, Admin-Roster, `TwitchJoinBudgetChannels` entfernen

- **Lesen:** E5, E6, E7 (nur Api-Teil), 3.3; `WorkerHealthEndpoints.cs`, `WorkerHealthStatus.cs`, `AdminEndpoints.cs:245-315`
  und `:537-547`, `WorkerCapacity.cs`, `ApiFactory.cs`, `ApiHealthEndpointTests.cs`, `AdminDashboardActivityEndpointTests.cs`,
  `RateLimitRejectionTests.cs:400-445`, `docs/Architectur.md:179`.
- **Liefert:** Route nach E5 (Handler dünn: Health ableiten, Dienst rufen, Status abbilden); `/api/admin/roster` nach 3.3 über
  den Dienst; `TwitchJoinBudgetChannels` gelöscht, Doc-Kommentar von `TwitchConcurrentChannelLimit` berichtigt (Referenz für
  benannte Konten, Messung anonym bis 120, keine Bot-Ausnahme mehr); `Architectur.md:179` um den neuen Endpunkt ergänzt
  (englisch); DECISIONS-Absatz „endpoint contract" (Route, 200/503-Tabelle, Prinzip „meldet nur, was `/api/health` nicht
  meldet", Policy, nicht im HEALTHCHECK).
- **Grenzfälle:** anonym ohne Cookie erreichbar; Körper leer (`Content-Length: 0` bzw. kein Body); Health fehlt → 200; Health
  `disconnected`/`stale` → 200 auch bei überfälligem Kanal; Health `connected` + Roster fehlt → 503; + `Starting` → 200;
  + `NoSession` → 200; + `Failing` → 503; `/api/health` bleibt bei überfälligem Kanal 200 (Regressionsfall); der Endpunkt
  verbraucht den `PublicHealth`-Topf (nach Erschöpfen 429 auch auf `/api/health`); `/api/admin/roster` ohne Schnappschuss
  liefert `joinHealth.status = rosterUnavailable`.
- **Tests (Api.Tests):** `ApiChannelHealthEndpointTests.cs` (alle Fälle oben, echter Dienst mit substituiertem
  `IWorkerRosterReader` und `IChannelService` aus `ApiFactory` — `ApiFactory` bekommt dafür nur Default-Antworten der
  Substitute, keinen neuen Substitut für den Dienst); `ApiHealthEndpointTests` um den Regressionsfall; Roster-Formtest in
  `AdminDashboardActivityEndpointTests` (kein `twitchJoinBudgetChannels` mehr, `joinHealth` und `ircUnjoined` vorhanden, Status
  als String); `RateLimitRejectionTests` um den geteilten Topf. Kein neuer Filter, keine Filter-Reihenfolge berührt.
- **Fertig:** Build, Format, `dotnet test tests/EmotePurge.Api.Tests` grün. Commit `feat(api): add GET /api/health/channels
  and report unjoined channels in the admin roster`.

### T6 — Teil B Backend: `ChannelCapacityReached` als EventId

- **Lesen:** E8; `ChannelService.cs:820-850`, `ChannelServiceCapacityTests.cs`, ein Test mit Logger-Fake als Vorbild
  (`tests/EmotePurge.Infrastructure.Tests/Unit/SevenTvWriteClientLoggingTests.cs`), `LogFilterParser.cs:150-175`.
- **Liefert:** EventId mit Namen `ChannelCapacityReached` an der bestehenden Warning (Text und Platzhalter unverändert; die
  Nummer als Konstante in der Klasse, kein neues zentrales Register); Kommentar von `ChannelCapacityOptions.MaxActiveChannels`
  angepasst („silent TwitchLib rejoin failure" stimmt nach Teil A nicht mehr); DECISIONS-Absatz „capacity display and demand
  signal" (E7, E8, E9).
- **Grenzfälle:** Ablehnung loggt genau einmal mit dem EventId-Namen; Admin-Join über der Decke und idempotenter Join auf
  aktivem Kanal loggen nichts.
- **Tests:** `ChannelServiceCapacityTests` um die Logzeile (EventId-Name und Platzhalterwerte).
- **Fertig:** Build, Format, die Capacity-Tests grün. Commit `feat: tag active-channel cap rejections with a stable event id`.

### T7 — Frontend: Kapazitätszeile, Unjoined-Liste, Detailseite, Mocks

- **Lesen:** E6, E7, E8, 3.4; `admin-roster-card.ts` + Spec, `utilization-tone.ts` + Spec, `admin.model.ts:110-180`,
  `admin.service.spec.ts:130-170`, `admin-monitoring-page.template.spec.ts:140-160`, `admin-channel-detail-page.ts:200-320`,
  `admin-log-errors-row.ts:80-90` (Link-Muster), `core/channels/backfill-window.ts` (`formatInstant`), beide Locale-Dateien
  (`web/public/i18n/`), `web/e2e/support/mocks.ts:185-230`, `web/e2e/admin-monitoring.e2e.spec.ts`,
  `web/e2e/audit/ui-audit.audit.ts:1245-1265`; vor jeder UI-Arbeit `docs/UI-Designsprache.md` §11 und `DESIGN.md`.
- **Liefert:** Modelländerungen; `utilizationTone` mit optionalen Schwellen; Roster-Karte: Kapazitätsblock (Balken gegen
  `maxActiveChannels`, 90/100, drei Textstufen, Twitch-Referenzhinweis, Log-Link), Unjoined-Liste nach E6, Status `failing`;
  Detailseite mit Grund und „seit"; Locale-Keys nach 3.4 (entfernte Keys wirklich entfernt, de/en gleich); Mocks und Audit auf
  den neuen Vertrag.
- **Grenzfälle:** `maxActiveChannels` 0 oder fehlend → kein Ton, kein Warntext (bestehende Ehrlichkeitsregel); genau 90 %
  → `warning`; genau 100 % → `danger` mit „Grenze erreicht"; über 100 % → „über der Grenze"; Balkenbreite gekappt; unbekannter
  Grundcode → generischer Text; Zeile ohne Grund; fehlender Kanal (`inRoster = false`) mit „erwartet seit"; Liste gekappt mit
  `ircUnjoinedTotal` > Länge; Status-Vorrang `unknown` > `unavailable` > `starting` > `stale` > `failing` > `incomplete` >
  `complete`.
- **Tests (Vitest, Regel 12, Verhalten statt Vorlage):** `utilization-tone.spec.ts` (Default unverändert, eigene Schwellen,
  Grenzwerte); `admin-roster-card.spec.ts` neu für Kapazitäts-Computeds, Unjoined-Sortierung und -Defaults, Status-Vorrang,
  zugänglicher Name des Balkens (`aria-label`, `aria-valuemax` = Decke); `admin.service.spec.ts` und
  `admin-monitoring-page.template.spec.ts` auf den neuen Vertrag. **E2E:** `mockAdminRoster` mit `ircUnjoined` und
  `maxActiveChannels`-Override; in `admin-monitoring.e2e.spec.ts` zwei Fälle — überfälliger Kanal zeigt Namen, Grund und
  Status „fehlt", und 90-%-Auslastung zeigt die Warnzeile.
- **Fertig:** Vitest, Lint, `format:check` grün; `npm --prefix web run e2e` grün (**nichts auf `:5151`**). Commit
  `feat(web): grade active channels against our own cap and list unjoined channels`.

### T8 — Live-Verifikation (Regel 16) und Protokoll

Läuft lokal gegen echtes Twitch (anonyme Verbindung), echtes Postgres und Redis. Protokoll deutsch unter
`docs/nachtlaeufe/live-516-<datum>.md` (Muster `live-486-2026-11-20.md`), je Schritt Befehl, erwartetes und gesehenes
Ergebnis. **Vor dem Start mit dem Orchestrator klären, dass kein anderer Dev-Worker läuft** (Worktree 487, Compose-Worker
des Haupt-Checkouts): zwei Worker auf derselben Dev-Datenbank und demselben Redis überschreiben sich das Roster.

1. **Aufbau:** `docker compose up -d postgres redis` **im Haupt-Checkout** (nie aus dem Worktree); Compose-`worker` und
   `api` dort gestoppt. Api aus dem Worktree mit `dotnet run --project src/EmotePurge.Api` und
   `Channels__JoinHealth__GraceSeconds=60`; Worker aus dem Worktree mit `dotnet run --project src/EmotePurge.Worker`,
   `Worker__Debug__AllowTwitchReconnectTrigger=true` und `Twitch__IdentityReconcileIntervalMinutes=600` (der erste
   Identitätsabgleich läuft sofort nach der Boot-Recovery und würde die Sonde unten deaktivieren — deshalb Sonde **danach**
   anlegen, und das lange Intervall verhindert den zweiten Lauf).
2. **Boot ohne Fehlalarm:** ab Worker-Start alle 5 s `curl -s -o /dev/null -w '%{http_code}' localhost:5151/api/health/channels`
   bis 2 min nach Ende der Boot-Recovery → durchgehend 200; `/admin/monitoring` zeigt erst „Startet", dann „Vollständig".
3. **Reconnect ohne Fehlalarm:** `PUBLISH channel:bot:commands DEBUG:TWITCH-RECONNECT` (per `docker exec <redis-container>
   redis-cli`, Containername mit `docker ps` prüfen), danach 3 min alle 5 s pollen → durchgehend 200; im Roster bekommen die
   Kanäle kurz neue `ircUnconfirmedSinceUtc`, die nach der Rejoin-Runde wieder verschwinden.
4. **Abgelehnter Join:** eine Sonden-Zeile in `Channels` anlegen (erfundener, gültig geformter Login, der auf Twitch nicht
   existiert, z. B. `ep516probe` plus Ziffern; `IsBotActive = true`, ohne `TwitchChannelId`, Pflichtspalten nach Schema), dann
   `PUBLISH channel:bot:commands JOIN:<login>`. Erwartet: Worker-Log eine Zeile `TwitchJoinRejected`/`channelSuspended`
   (oder, falls Twitch der anonymen Verbindung keinen NOTICE schickt, `TwitchJoinUnconfirmed`/`noConfirmation` — **welcher
   Fall eintritt, ist selbst ein Messergebnis und kommt ins Protokoll**); in den folgenden Minuten nur Debug-Wiederholungen;
   im Roster `ircJoinFailureReason`; nach Grace (60 s) plus nächstem Roster-Takt `/api/health/channels` → 503, `/api/health`
   bleibt 200; `/admin/monitoring` listet die Sonde mit Grund, „seit" und „überfällig", Status „fehlt"; die Detailseite zeigt
   Grund und Zeit.
5. **Boot mit hängender Sonde:** Worker neu starten → während der Boot-Recovery 200, danach nach Grace wieder 503.
6. **Aufräumen:** Sonde per Leave (Api, Admin) oder SQL deaktivieren und Zeile löschen, `LEAVE:<login>` publizieren →
   nach dem nächsten Roster-Takt 200. Danach Grace-Override weg, Api neu starten, 200.
7. **Kanaldecke (Teil B):** Api mit `Channels__MaxActiveChannels=<aktuelle aktive Zahl>`; Join eines neuen Kanals mit einem
   **Nicht-Admin**-Login (z. B. Testkonto `olaf_olaf_son` auf seinem eigenen Kanal) → 409 `channel_capacity_reached`, eine
   Warning mit EventId `ChannelCapacityReached`; mit eingeschalteter Log-Erfassung (`LOG_CAPTURE_ENABLED`) findet
   `/admin/logs?q=event:ChannelCapacityReached&range=7d` sie, und der Link der Roster-Karte führt dorthin. Die Karte zeigt
   „Grenze erreicht". Ist kein Nicht-Admin-Login verfügbar, im Protokoll vermerken; der Integrationstest aus T6 deckt die
   Zeile.
8. **Nicht live prüfbar** und im Protokoll so benannt: der echte Limit-NOTICE an der anonymen Verbindung (er kommt nur bei
   benanntem Konto ab Kanal 101 oder wenn Twitch die anonyme Verbindung deckelt) — abgedeckt durch die aufgezeichnete Zeile
   im Parser-Test und den TwitchLib-gebundenen Test.

- **Fertig:** Protokoll committet (`docs: add live verification protocol for #516`); jeder Schritt bestanden oder als Befund
  mit Folge benannt.

### T9 — Doku-Abschluss

- **Lesen:** E5, E8, E9, E10; `docs/Operations.md` (Aufbau, „Running behind a reverse proxy", „Log viewer"), `CLAUDE.md`
  („Umsetzungsstand" Zeile Admin, „Bekannte offene Grenzen" erster Punkt), DECISIONS-Eintrag aus T3/T5/T6.
- **Liefert (englisch in Operations.md, deutsch in CLAUDE.md):**
  - Operations.md, neuer Abschnitt **„Channel join health"** nach „Admin dashboard": was `/api/health/channels` prüft,
    200/503-Tabelle und das Prinzip „never alarms for what `/api/health` already reports"; **zweiter Uptime-Monitor**
    (generisch: HTTP-Monitor auf `https://<host>/api/health/channels`, 60 s Intervall, „Retries" 2, eigene Benachrichtigung,
    z. B. Telegram über die Notification des Monitors; Statuscode 200 als „up"); **nicht** in einen Container-HEALTHCHECK;
    Konfiguration (E10) mit Grace-Formel aus E4 und dem Hinweis auf `SevenTv:ResyncIntervalSeconds`; was ein
    `channelSuspended`-Alarm bedeutet und wie er endet (Identitätsabgleich deaktiviert unauflösbare Logins); Log-Filter
    `event:TwitchJoinRejected`, `event:TwitchJoinUnconfirmed`, `event:ChannelCapacityReached`.
  - Operations.md, Unterabschnitt **„Raising the active-channel cap"**: erst nach Teil A live und Beobachtungszeit ([F2]),
    `Channels__MaxActiveChannels` im Stack setzen, Api neu starten; nach dem ersten Worker-Neustart Rejoin-Dauer (≈ 0,6 s × N),
    Boot-Recovery (≈ 0,85 s × N) und Grace gegenprüfen; Rückweg = Variable zurücksetzen.
  - `CLAUDE.md`: „Bekannte offene Grenzen" — der Satz „der Worker erkennt und meldet eine solche Ablehnung bisher nicht"
    wird durch den neuen Stand ersetzt; Admin-Zeile im Umsetzungsstand um einen Halbsatz (Join-Health in der Roster-Karte,
    `/api/health/channels` als zweiter Kuma-Monitor).
  - DECISIONS-Eintrag auf Vollständigkeit der `**Betrifft:**`-Zeile prüfen.
  - Historische Doku (`docs/Konzept-Worker-Reconnect-2026-09-08.md` 7.2, ältere DECISIONS-Einträge) bleibt unverändert; der
    neue Eintrag sagt, was er überholt.
- Die eigene Kuma-Einrichtung (Monitorname, Telegram-Chat) ist Betreiberhandgriff und gehört nach `infra-docs`, nicht ins
  Repo.
- **Fertig:** relative Links und Abschnittsanker geprüft (Prettier deckt `docs/` nicht ab). Commit `docs: document channel join
  health and raising the channel cap`.

**Reihenfolge:** T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9 → Gates (Abschnitt 6). T6 hängt an keinem anderen Task und dürfte
früher laufen; der Einfachheit halber bleibt die Reihe strikt.

---

## 5. Abdeckung der Akzeptanzkriterien

| AK (Issue) | Task |
|---|---|
| A1 „seit wann unbestätigt" + Grund im Roster | T1, T2, T3 |
| A2 NOTICE geloggt, Erkennung ohne NOTICE | T2 (Gründe, Dedupe), T4 (Erkennung nur über fehlende Bestätigung), T8 Schritt 4 |
| A3 Gnadenfrist, kein Fehlalarm bei Boot und Reconnect | T4, T8 Schritte 2, 3, 5 |
| A4 `/admin/monitoring` listet mit Grund und seit | T5, T7, T8 Schritt 4 |
| A5 neuer payloadfreier Endpunkt, `/api/health` unverändert | T5 |
| A6 Tests + Live | T1–T7, T8 |
| A7 Operations.md + DECISIONS | T3, T5, T6, T9 |
| B1 Nenner `MaxActiveChannels`, 100 nur Referenz | T5, T7 |
| B2 `TwitchJoinBudgetChannels` und Kommentare entfernt | T5, T6, T7 |
| B3 Frühwarnung 90 %, Alarmentscheidung festgehalten | T7, T6 (DECISIONS) |
| B4 Ablehnungen geloggt und gezählt | T6, T7 (Link), T8 Schritt 7 |
| B5 Anhebung auf 120 nach A live | T9 (Runbook); die Umsetzung ist Betreiberhandgriff nach dem Deploy |

---

## 6. Gates (vor dem PR)

1. `dotnet test EmotePurge.slnx` grün (braucht laufendes Docker, Testcontainers).
2. `npm --prefix web test -- --watch=false` grün.
3. `npm --prefix web run e2e` grün — **nur ohne Api auf `:5151`** (nach T8 die lokale Api beenden).
4. `node scripts/coverage-local.mjs` — neue Dateien (Tracker, Parser, Policy, Dienst, Endpunkt) sollen deutlich über 80 %
   liegen; das Ergebnis ist eine Näherung, kein Urteil.
5. `dotnet format EmotePurge.slnx --verify-no-changes`, `npm --prefix web run format:check`, `npm --prefix web run lint`.
6. T8-Protokoll vollständig.
7. Danach (Orchestrator, nicht Teil dieses Plans): Code-Review und `/codex:review --model gpt-6.2-sol` über den Branch.

**Deploy:** keine Migration; Api und Worker gemeinsam; danach zweiter Kuma-Monitor einrichten; Anhebung der Decke erst nach
[F2].

---

## 7. Konfliktrisiken mit dem parallelen Branch `feat/487-wish-bot-runner` (Worktree 487, PR 2 des Bot-Plans)

Der Plan hält die Worker-Änderungen in `TwitchChatManager`, `ITwitchChatManager`, `WorkerRosterPublisher` und zwei neuen
Dateien; **`WorkerServiceRegistration.cs`, `Program.cs` (beide Projekte), `WorkerHealthPublisher` und `RateLimitingOptions`
bleiben unberührt** (keine neue DI-Registrierung im Worker, keine neue Rate-Limit-Policy).

| Datei | Risiko | Umgang |
|---|---|---|
| `docs/DECISIONS.md` (Kopf) | sicher textuell: beide setzen einen Eintrag ganz oben | beim Rebase beide Einträge behalten, nach Datum ordnen |
| `CLAUDE.md` Umsetzungsstand | wahrscheinlich: PR 2 pflegt die Wunsch-Zeile, wir die Admin-Zeile und „Bekannte offene Grenzen" | verschiedene Zeilen, von Hand zusammenführen |
| `docs/Operations.md` | gering: PR 2 schreibt Runbook Teil 2 unter „Bot account", wir einen neuen Abschnitt nach „Admin dashboard" | getrennte Abschnitte |
| `web/public/i18n/{de,en}.json` | gering bis mittel: PR 2 (T6/T7) kann Wunsch-Keys ergänzen | wir ändern nur `admin.roster.*`; JSON-Konflikt von Hand |
| `TwitchChatManager.cs` | gering: der Bot-Plan nennt die Klasse nur als Vorbild für Live-Verifikation | falls doch berührt: unser Umbau ersetzt alle `_desiredChannels`-Stellen — wer zuletzt merged, rebased |
| `ITwitchChatManager.cs` / `TwitchRosterEntry` | gering; Erweiterung nur mit Default-Parametern | — |
| `tests/EmotePurge.Api.Tests/ApiFactory.cs` | gering; wir setzen nur Default-Antworten bestehender Substitute | — |
| `WorkerHealthEndpoints.cs`, `AdminEndpoints.cs` | **erst PR 3** des Bot-Plans (`/api/health/bot-account`, Policy `PublicBotAccountHealth`, Admin-Routen) | gleiche Namensfamilie `/api/health/<aspekt>`; wer zweiter merged, rebased die Routenliste; bei PR 3 bewusst prüfen, ob dessen Alarm-Endpunkt ebenfalls „nur melden, was `/api/health` nicht meldet" will |

---

## 8. Offene Fragen an den Betreiber

- **[F1] Soll ein gesperrter oder gelöschter Kanal (`channelSuspended`) den Telegram-Alarm auslösen?** Plan-Default: **ja** —
  der Kanal fällt tatsächlich aus der Erfassung, und der Alarm endet von selbst, wenn der Identitätsabgleich (stündlich) den
  unauflösbaren Login deaktiviert, also nach höchstens rund einer Stunde plus Grace. Alternative: `channelSuspended` nur in
  der Admin-Liste zeigen und vom 503 ausnehmen (weniger Lärm, aber dann prüft T8 Schritt 4 den 503 nur, falls Twitch keinen
  NOTICE schickt).
- **[F2] Wie lange soll Teil A auf Prod ohne Fehlalarm laufen, bevor die Decke auf 120 geht?** Vorschlag: 7 Tage, mindestens
  ein Deploy und ein echter Reconnect darin.
- **[F3] Grace 360 s** passt zum heutigen Resync-Takt (60 s). Ist geplant, `SevenTv:ResyncIntervalSeconds` auf Prod zu
  strecken? Dann muss `Channels__JoinHealth__GraceSeconds` im selben Schritt mit (Formel E4).
