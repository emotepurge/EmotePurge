# Untersuchung: Twitch IRC → EventSub (`channel.chat.message`) — 2026-08-01

Auftrag: Bewertung eines Wechsels des Chat-Transports von der heutigen anonymen IRC-Verbindung
(`TwitchLib.Client`, `justinfan`) auf Twitch EventSub `channel.chat.message`.
Methodik: vollständiger Read des Twitch-/Chat-Pfads im Worker, Abgleich mit
[Architectur.md](Architectur.md) (Modul A) und [DECISIONS.md](DECISIONS.md) (49 Twitch-betreffende
Einträge), externe Recherche ausschließlich gegen `dev.twitch.tv` (Doku, Changelog-RSS, Product
Lifecycle) plus NuGet/GitHub für den Bibliotheksstand. Alle Doku-Abrufe am **2026-08-01**.
Kennzeichnung wie im 7TV-Dokument: **belegt** (Doku-Zitat mit Link) / **plausibel** (eigene
Ableitung aus belegten Fakten) / **unbestätigt** (keine Primärquelle gefunden).

Kein Produktivcode geändert, kein `DECISIONS.md`-Eintrag — der kommt erst nach der Entscheidung.

---

## 1. Fazit vorab

**Jetzt nicht wechseln.** Die Migration löst das Problem nicht, für das man sie erwägen würde, und
sie kostet genau die Eigenschaft, auf der Modul A heute steht.

Drei Befunde tragen das:

1. **EventSub hat keinen anonymen Lesepfad.** `channel.chat.message` verlangt in *jeder*
   dokumentierten Variante mindestens `user:read:chat` von einem echten, per OAuth autorisierten
   Account; die `condition` enthält ein Pflichtfeld `user_id` („The User ID to read chat as").
   Ein `justinfan`-Äquivalent existiert nicht. **Belegt.**
2. **EventSub-WebSocket löst das JOIN-Limit nicht — es verschärft es.** Twitch wendet die
   IRC-Join-Limits wortgleich auf EventSub mit User-Token an („Joining a chat room occurs **only
   when you subscribe to the Channel Chat Message EventSub subscription, or use the JOIN command in
   IRC**"), *und* obendrauf gilt seit 2024-05-15 ein **Concurrent-Join-Limit von 100 Kanälen pro
   User-Account**. Die verlockenden 3 × 300 = 900 Subscriptions sind damit unerreichbar. **Belegt.**
3. **Es gibt keinen Zeitdruck.** Twitch IRC steht im Product Lifecycle auf **„Active"**, hat kein
   Abschaltdatum und bekam am **2026-07-17** — vor zwei Wochen — noch einen neuen PRIVMSG-Tag.
   Twitch sagt durchgehend „recommended", nie „deprecated". **Belegt.**

Der einzige echte Skalierungs-Unlock ist nicht „EventSub statt IRC", sondern **„App Access Token +
`channel:bot`-Zustimmung des Broadcasters statt anonymem Lesen"** — dann entfallen beide Limits
ersatzlos. Das erzwingt aber Webhook- oder Conduit-Transport (WebSocket ist per Doku auf
User-Token beschränkt), einen Client-Credentials-Flow, den es im Repo an keiner Stelle gibt, und
eine OAuth-Zustimmung pro Kanal.

**Korrektur 2026-09-15 (#125):** Die ersten beiden Hürden aus diesem Absatz sind überholt. Der
Client-Credentials-Flow existiert seit dem 2026-08-03 (`2faba66`: `TwitchAuthClient.cs`,
`TwitchAppTokenProvider.cs`), der Worker nutzt ihn bereits (`TwitchLivePollWorker.cs`,
`TwitchConnectionWatchdog.cs`) — der hier als teuerster Einzelposten gebuchte Blocker ist erledigt
(s. Korrektur zu Abschnitt 4.3). Und „Conduit" bedeutet nicht zwingend Webhook: ein Conduit kann
seine Shards auch per WebSocket betreiben (`Get Conduit Shards` liefert `"method": "websocket"` mit
eigener `session_id`), dann entfällt der öffentliche HTTPS-Endpunkt ganz (s. Korrektur zu
Abschnitt 4.4). Pfad 2 kostet damit weniger, als dieser Absatz nahelegt. Was unverändert bleibt:
die **Zustimmung pro Kanal** — die eigentliche Produktfrage aus Abschnitt 5.3 und 7 — sowie zwei
in #125 offen gelassene Punkte (Prüfzeitpunkt von `user:bot`, `session_reconnect` auf
Conduit-Shards). Die Empfehlung in Abschnitt 7 ändert sich dadurch nicht.

**Was diese Untersuchung trotzdem verändert:** Der Eintrag „Bekannte offene Grenzen" in
[CLAUDE.md](../CLAUDE.md) nennt nur das Rate-Limit (20 JOINs/10 s). Das **Bestandslimit von 100
gleichzeitig gejointen Kanälen pro Account** fehlt dort — es ist die härtere Decke, und die
Vorabmessung vom 2026-07-30 (28 ungedrosselte JOINs in 5 s, 0 Fehler) hat sie gar nicht berührt,
weil sie eine Rate gemessen hat, keinen Bestand. Das gehört unabhängig von jeder Migration
korrigiert.

**Empfehlung mit Zeithorizont:** s. Abschnitt 9. Kurz: Status quo halten, zwei benannte
Auslöser beobachten, und die *eine* billige Vorbereitung jetzt machen (Abschnitt 9.3).

---

## 2. Was wir heute haben

Quelle: Read des kompletten Chat-Pfads. **Belegt** durch Code-Stellen.

### 2.1 Der Transport

`src/EmotePurge.Worker/TwitchChatManager.cs` (483 Zeilen) kapselt genau **eine** `TwitchClient`-
Instanz für die Worker-Lebensdauer.

- **Anonym:** `client.Initialize(new ConnectionCredentials())` — parameterlos
  ([TwitchChatManager.cs:220](../src/EmotePurge.Worker/TwitchChatManager.cs#L220), Kommentar
  `// anonym/read-only`). Twitch vergibt daraufhin eine `justinfanXXXXX`-Identität.
- **Der Worker hat null Twitch-Auth.** Er injiziert weder `ITwitchAuthClient` noch
  `ITwitchHelixClient`, obwohl beide über `AddEmotePurgeInfrastructure` in seinem DI-Container
  registriert *sind*. Seine `appsettings.json` enthält **kein einziges Twitch-Setting**.
- Client-Erzeugung in `CreateClient` mit explizitem `new ClientOptions(new ReconnectionPolicy())` —
  die parameterlose Policy hat `maxAttempts == null`. Der Default (`null`) wäre
  `ReconnectionPolicy(3000, maxAttempts: 10)`, und deren Zähler ist ein *Lebenszeit*-Budget der
  Instanz, nicht pro Reconnect. Das war der Bug „scheitert exakt beim 10. Reconnect".
- Join/Leave kommen als reine Strings `JOIN:`/`LEAVE:`/`RESYNC:` über Redis `channel:bot:commands`
  ([Worker.cs:33-59](../src/EmotePurge.Worker/Worker.cs#L33-L59)).
- **Desired-State ist bereits vorhanden:** `_desiredChannels` ist eine
  `ConcurrentDictionary<string, bool>` — Key = Channel, Value = *ob Twitch den JOIN bestätigt hat*.
  Der Intent wird **vor** dem Versuch aufgezeichnet, `EnsureJoinedAsync` ist das Konvergenznetz,
  minütlich getrieben vom `SevenTvPeriodicResyncWorker`.
- JOIN-Drosselung 600 ms über den `_joinGate`-Semaphor, der alle eigenen Join-Pfade serialisiert.
  TwitchLibs *eigener* Rejoin nach einem Reconnect läuft innerhalb der Library und ist nicht
  drosselbar (~180–200 ms/Channel gemessen).

### 2.2 Die für die Migration entscheidende Stelle

`OnMessageReceived` ([TwitchChatManager.cs:436-461](../src/EmotePurge.Worker/TwitchChatManager.cs#L436-L461))
liest aus der empfangenen Nachricht **exakt drei** Properties:

| Property | Verwendung |
|---|---|
| `e.ChatMessage.Channel` | Cache-Lookup-Key (Channel-**Login**, nicht ID) |
| `e.ChatMessage.Message` | roher Nachrichtentext, wird tokenisiert |
| `e.ChatMessage.Username` | **nur** in einem `LogDebug` |

Sonst nichts. Keine Message-ID, keine User-ID, keine Badges, keine Twitch-Emote-Metadaten, kein
Timestamp (der wird lokal per `DateTime.UtcNow` gesetzt). Zusätzlich wird pro Nachricht
`_lastMessageReceivedUtcTicks` aktualisiert — der Freeze-Detektor des Watchdogs.

Der Match selbst:

```csharp
foreach (var token in e.ChatMessage.Message.Split(' '))
    if (channelEmotes.TryGetValue(token, out var emoteId) && matchedThisMessage.Add(emoteId))
        usageCounter.Increment(emoteId);
```

Split auf `' '`, exakte case-sensitive Wortgleichheit gegen die 7TV-Namen aus **unserer** DB. Twitchs
`emotes`-Tag wird nie angefasst. **Der Minimal-Payload eines Ersatz-Transports ist also
Channel-Login + roher Nachrichtentext** — mehr nicht.

Das ist der stärkste einzelne Befund zugunsten der *Machbarkeit*: Der Vertrag nach innen ist
`IEmoteMatchCache.GetChannelEmotes(login)` + `IEmoteUsageCounter.Increment(emoteId)`, und beide
kennen Twitch nicht. Modul A ist an **keiner** Stelle auf IRC-Spezifika festgelegt außer der
Transportklasse selbst.

---

## 3. Faktenbasis EventSub

### 3.1 `channel.chat.message`

**Version `1`** — eine v2 existiert nicht (Stand 2026-08-01).
Quelle: [eventsub-subscription-types](https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/#channelchatmessage),
[eventsub-reference](https://dev.twitch.tv/docs/eventsub/eventsub-reference/#channel-chat-message-event).
**Belegt.**

**Condition (beide Pflicht):**

| Name | Beschreibung (wörtlich) |
|---|---|
| `broadcaster_user_id` | „The User ID of the channel to receive chat message events for." |
| `user_id` | „The User ID to read chat as." |

Das zweite Feld ist der Angelpunkt des ganzen Auth-Problems: **es gibt immer einen lesenden
Nutzer.**

**Payload, die für uns relevanten Teile:** `broadcaster_user_id/_name/_login`,
`chatter_user_id/_name/_login`, `message_id` (UUID), `message.text` („The chat message in plain
text."), `message.fragments[]`, `message_type`, `badges[]`, `cheer`, `color`, `reply`,
`source_*` (Shared Chat).

Fragment-Typen sind **fünf**, nicht vier: `text | cheermote | emote | mention | gif`. Der Typ `gif`
kam laut [Changelog 2026-07-17](https://dev.twitch.tv/docs/changelog/) hinzu.
`fragments[].emote` liefert `id`, `emote_set_id`, `owner_id`, `format` (`animated`/`static`).

**Für uns:** `broadcaster_user_login` + `message.text` decken den Bedarf aus 2.2 vollständig ab. Die
`fragments`-Struktur ist zwar deutlich besser als IRCs Positions-Offsets im `emotes`-Tag — für
7TV-Emotes aber irrelevant, weil die für Twitch in beiden Transporten schlichter Text sind.

**Was IRC liefert und EventSub nicht** (eigener Vergleich der Feldlisten; Twitch dokumentiert keine
Lückenliste — die Feldlisten selbst sind belegt, die Differenz ist meine Ableitung, insofern
**plausibel**, nicht belegt):

- `tmi-sent-ts` — **kein Zeitstempel der Chat-Nachricht** im Event. Es gibt nur
  `metadata.message_timestamp`, den Sendezeitpunkt der *Notification*.
- `first-msg`, `flags` (AutoMod), `returning-chatter`, `client-nonce` — kein Äquivalent.
- **JOIN/PART-Membership und `/NAMES`** — hier ist Twitch ausdrücklich: „When running a chatbot
  using EventSub or API, you do not get notifications when a user leaves or joins a chatroom."
  Ersatz nur über [Get Chatters](https://dev.twitch.tv/docs/api/reference/#get-chatters) mit
  `moderator:read:chatters`. **Belegt.**

Keiner dieser Punkte betrifft EmotePurge — wir nutzen nichts davon.

Twitch selbst formuliert die Migration zurückhaltend
([irc-migration](https://dev.twitch.tv/docs/chat/irc-migration/)): „Using EventSub, **some**
information from PRIVMSG tags are still provided for Channel Chat Message, however the format is
much different than IRC."

### 3.2 Auth-Modell — der kritischste Punkt

Wörtlich von der Subscription-Types-Seite:

> „Requires `user:read:chat` scope from the chatting user. If app access token used, then
> additionally requires `user:bot` scope from chatting user, and either `channel:bot` scope from
> broadcaster or moderator status."

Und aus [Authenticating and EventSub](https://dev.twitch.tv/docs/chat/authenticating/), der
Satz, der die Architektur festlegt:

> „**You can only subscribe to events over WebSockets transport using a User Access Token.**"
> „**You can only subscribe to events over Webhook transport using an App Access Token.**"

Daraus folgen **genau zwei** nutzbare Kombinationen. **Belegt.**

| | **Pfad 1 — Bot-User-Token + WebSocket** | **Pfad 2 — App-Token + Webhook/Conduit** |
|---|---|---|
| Scopes | `user:read:chat` vom Bot | `user:bot` vom Bot **+** `channel:bot` vom Broadcaster (oder Mod-Status des Bots) |
| Zustimmung nötig von | nur uns selbst (einmalig) | **jedem Broadcaster einzeln** |
| Transport | WebSocket (ausgehend) | Webhook oder Conduit (eingehend) |
| Concurrent-Join-Limit | **100 Kanäle** | entfällt |
| Join-Rate-Limit | 20 / 10 s (wie IRC) | entfällt |
| Neue Infrastruktur | keine | öffentlicher HTTPS-Endpunkt, HMAC, Dedup, Client-Credentials-Flow |

**Korrektur 2026-09-15 (#125):** Die Zeilen „Transport" und „Neue Infrastruktur" für Pfad 2 gelten
nur für den Webhook-Zweig. Ein Conduit braucht dagegen keinen öffentlichen HTTPS-Endpunkt, wenn
seine Shards per WebSocket statt per Webhook laufen (`Get Conduit Shards` liefert dokumentiert
`"method": "websocket"` mit eigener `session_id`, s. Abschnitt 3.3) — die HMAC-Prüfung eingehender
Webhooks entfällt dann ebenso. Der Client-Credentials-Flow, den beide Transporte zur Verwaltung brauchen, existiert
seit dem 2026-08-03 (s. Korrektur zu Abschnitt 4.3). Details: #125.

**Anonymer Lesepfad: existiert nicht.** Belege: die `condition` verlangt zwingend `user_id`; es gibt
keinen dokumentierten Token-Typ ohne Nutzerbezug für diesen Subscription-Type. **Belegt.**

**Zu `justinfan` — ein Nebenbefund, der wichtiger ist als die Migrationsfrage:** Die komplette Seite
[IRC Concepts](https://dev.twitch.tv/docs/chat/irc/) wurde roh durchsucht — die Zeichenkette
„justinfan" und das Wort „anonymous" kommen dort **nicht vor**. Die Seite verlangt im Gegenteil
„you must authenticate with Twitch and get a User Access Token with `chat:read`". Der Pfad, auf dem
Modul A heute vollständig steht, ist also **undokumentiertes, geduldetes Verhalten**. Es gibt keine
Zusage, dass er bleibt — und keine Ankündigung, ihn zu entfernen. **Belegt, dass es undokumentiert
ist; die Folgerung „kein Deprecation-Schutz" ist plausibel, nicht belegt.** Das ist das eigentliche
Architekturrisiko hinter Modul A, und es lässt sich durch keine Menge Sorgfalt in unserem Code
absichern.

**`channel:bot` autorisiert der Broadcaster**, nicht der Bot — es ist ein Scope auf dem User-Token
des Kanaleigentümers. Twitch beschreibt das als „Cloud Chatbot": „the User Access Token needed will
be from the broadcaster who owns the chat room." **Belegt.**

**Mod-Status vs. `channel:bot`:** Die Doku nennt beide als Alternativen, aber nur im
App-Token-Kontext. Zwei dokumentierte Unterschiede: beide befreien vom Concurrent-Join-Limit; für
die Einordnung als Chat Bot in der Chatter-Liste genügt Mod-Status **nicht**. Mod-Status ersetzt
also nur die `channel:bot`-Komponente — `user:bot` + `user:read:chat` vom Bot-Account braucht es
weiterhin, und der App-Token-Pfad braucht weiterhin Webhook/Conduit — Letzteres nicht zwingend mit
öffentlichem Endpunkt, s. Korrektur 2026-09-15 (#125) zur Tabelle oben. **Belegt.**

### 3.3 Transport-Details

**WebSocket** ([handling-websocket-events](https://dev.twitch.tv/docs/eventsub/handling-websocket-events/),
[manage-subscriptions](https://dev.twitch.tv/docs/eventsub/manage-subscriptions/#subscription-limits)),
Limits „per user token (client ID and user ID tuple)":

- max. **3 Connections** mit aktiven Subscriptions; Reconnect per `reconnect_url` zählt nicht mit
- max. **300 enabled Subscriptions** pro Connection (früher 100, laut Changelog 2023-07-17 erhöht)
- `max_total_cost` ist **10** — für uns **nicht bindend**: „There is no cost for subscriptions that
  require a user to authorize your application", und das offizielle Beispiel-Payload für
  `channel.chat.message` zeigt direkt `"cost": 0`
- `wss://eventsub.wss.twitch.tv/ws`, optional `?keepalive_timeout_seconds=N` (10–600)
- `session_welcome` liefert die Session-ID; **10 Sekunden** Zeit für die erste Subscription, sonst
  Close 4003
- `session_keepalive`, wenn innerhalb `keepalive_timeout_seconds` keine Notification kam; Ping/Pong
  ist unabhängig davon und setzt den Timer **nicht** zurück
- `session_reconnect` kommt **30 Sekunden vor** dem Schließen mit einer `reconnect_url`; „**You
  should not close the old connection until you receive a Welcome message on the new connection.**"
- **Keine dokumentierte feste Verbindungs-TTL.** Anders als 7TVs harte ~1-h-Trennung kündigt Twitch
  den Wechsel an. Das ist ein echter Vorteil gegenüber unserem 7TV-Transport.
- **Kein Replay**: „There is no replay of events that are lost". Und schärfer: „If you disconnect
  from a WebSocket session, **all subscriptions associated with that session are automatically
  disabled**."
- Close-Codes: 4000 internal · 4001 inbound traffic · 4002 ping-pong failed · 4003 unused ·
  4004 reconnect grace expired · 4005 network timeout · 4006 network error · 4007 invalid reconnect

Alles **belegt**.

**Webhook** ([handling-webhook-events](https://dev.twitch.tv/docs/eventsub/handling-webhook-events/)):
SSL auf **Port 443**; Challenge-Antwort muss den **rohen** Challenge-Wert mit korrektem
`Content-Length` zurückgeben; HMAC-SHA256 über die Konkatenation von
`Twitch-Eventsub-Message-Id` + `Twitch-Eventsub-Message-Timestamp` + **rohem Request-Body** (in
dieser Reihenfolge), Secret 10–100 Zeichen, Vergleich „time safe"; Antwort „within a few seconds",
Empfehlung „write the event to temporary storage and process after responding with 2XX"; Zustellung
„at least once" mit identischer Message-ID bei Wiederholung; Replay-Schutz: Timestamp nicht älter
als **10 Minuten** und Message-ID-Dedup. **Belegt.**

Nicht beziffert in der Doku: die konkrete **Retry-Anzahl** und das Retry-Zeitfenster. Der Header
`Twitch-Eventsub-Message-Retry` existiert, eine Zahl steht nirgends. **Unbestätigt** — nicht
erfunden. Ebenso: das `"max_total_cost": 10000` in den Webhook-Beispiel-Payloads ist Beispieldatum,
kein zugesichertes Limit.

**Conduits** ([handling-conduit-events](https://dev.twitch.tv/docs/eventsub/handling-conduit-events/)):
„a wrapper that separates your subscriptions from the underlying transport and load balances
notifications across shards". Braucht App Access Token zur Verwaltung; max. **5 Conduits** à max.
**20.000 Shards** („all numbers provided are subject to change"). Der eigentliche Gewinn ist
Resilienz: „if no shard is reactivated **after 72 hours**, EventSub will delete the conduit. This
allows developers to recover from full outages **without needing to recreate every subscription**."
**Belegt.**

Das ist genau die Klasse von Problem, die uns aus dem Worker-Reconnect-Bug und dem
7TV-EventAPI-Transport bekannt ist: Bei reinem WebSocket disabled jeder Abbruch *alle*
Subscriptions.

**Korrektur 2026-09-15 (#125):** Dieses Dokument hat Conduits an mehreren Stellen implizit als
Webhook-Sache behandelt (s. Tabelle in 3.2, Abschnitt 4.4). Das ist zu eng: `Get Conduit Shards`
gibt pro Shard ein `transport`-Objekt zurück, dessen `"method"` auch `"websocket"` sein kann, mit
eigener `session_id` wie bei einer normalen WebSocket-Session. Ein Conduit erzwingt also **keinen**
öffentlichen HTTPS-Endpunkt — nur der Webhook-*Zweig* eines Conduits tut das. Die drei
nginx-Hürden aus 4.4 und die HMAC-Prüfung entfallen damit für den WebSocket-Zweig; ob Nachrichten
dort dedupliziert werden müssen, ist davon nicht berührt. Unbelegt bleibt laut #125, ob Twitch `session_reconnect` auch für Conduit-Shards
ankündigt — auf der Conduit-Doku-Seite kommt der Begriff nicht vor.

### 3.4 Skalierung — die Rechnung

[Chat — Rate Limits](https://dev.twitch.tv/docs/chat/), der Abschnitt mit dem größten
Überraschungswert. Twitch hat die Limits **vereinheitlicht**:

> „Joining a chat room occurs **only when you subscribe to the Channel Chat Message EventSub
> subscription, or use the JOIN command in IRC**."

**Concurrent Join Limit:** „Twitch imposes limits for how many chat rooms you can join from a single
user account. **As of May 15th 2024, the limit is set to 100.**" Nicht mitzählend: Join als
Broadcaster oder Moderator; und Join „after being authorized by the chat room's broadcaster […]
authorizing a Channel Chat Message EventSub subscription with an App Access Token and the
`channel:bot` scope".

**Join Rate Limits:** „When using IRC, **and EventSub or Twitch API using a User Access Token**":
20 Versuche / 10 s (normal), 2.000 / 10 s (verified). Und: „**When using an App Access Token for
Channel Chat Message EventSub subscription, the above limits are not applicable.**"

Alles **belegt**. Die in [CLAUDE.md](../CLAUDE.md) notierte 20/2.000-Angabe ist damit exakt
bestätigt — und sie gilt für EventSub-WebSocket **genauso**.

**Rechnung:**

| Pfad | Theoretisches Maximum | Tatsächlich bindend |
|---|---|---|
| Heute (anonymes IRC) | — | 100 Kanäle (Bestand) + 20/10 s (Rate) |
| EventSub WS + Bot-Token | 3 × 300 = 900 Subs | **100 Kanäle** + 20/10 s — **identisch** |
| EventSub + App-Token + `channel:bot` | 5 Conduits × 20.000 Shards | praktisch unbegrenzt; Zustimmung pro Kanal |

Sharding über N Bot-Accounts ergäbe rechnerisch N × 100, weil die Limits „per user account"
formuliert sind. Twitch dokumentiert das **weder als erlaubt noch als verboten** — **unbestätigt**,
und ich würde es nicht ohne Rückfrage bei Twitch einplanen.

### 3.5 Deprecation-Status von IRC

**Es gibt kein angekündigtes Abschaltdatum. IRC ist offiziell „Active".**

Primärbeleg [Product Lifecycle](https://dev.twitch.tv/docs/product-lifecycle/), Tabelle „Current
Product Status":

| Product | Status | Note |
|---|---|---|
| **Chat (IRC)** | **Active** | „Non-secure websocket connections specifically have been decommissioned in August 2025" |

Der einzige IRC-Eintrag unter „Important Dates" ist 2025-08-15, „Non-secure WebSocket connections
to Twitch IRC servers is decommissioned". **Nur `ws://` ist weg** — `wss://irc-ws.chat.twitch.tv:443`
und beide TCP-Ports (6667/6697) leben. Wir nutzen ohnehin den `wss`-Pfad. **Belegt.**

**Gegenbeleg zur Deprecation-These — IRC wird aktiv weiterentwickelt.** Aus dem vollständigen
[Changelog-RSS](https://dev.twitch.tv/docs/rss/change-log.xml) (352 Einträge):

- **2026-07-17**: „IRC PRIVMSG Tags has been updated to include information about the new `gif` tag"
- **2026-06-18**: „Added `viewermilestone` and `modiversary` message types to IRC's USERNOTICE Tags"
- 2025-04-10: neuer PRIVMSG-Tag `source-only`, zeitgleich mit dem EventSub-Pendant

Ein Protokoll, dem man vor zwei Wochen noch neue Tags hinzufügt, wird nicht abgeschaltet.

**Zum Kontrast, so sieht eine echte Twitch-Abkündigung aus:** PubSub hatte ein explizites
Shutdown-Datum in „Important Dates" (2025-04-14 „Permanent shutdown of legacy PubSub") plus einen
offiziellen Migration Guide. Für IRC existiert nichts Vergleichbares.

**Senden vs. Lesen — die Trennung ist scharf:**

- **Chat-Commands über IRC (Senden/Moderieren) wurden abgeschaltet**, am 2023-02-24: `/ban`,
  `/timeout`, `/slow`, `/emoteonly` etc. zugunsten der Helix-API, mit der Formulierung „the use of
  chat commands over IRC has been deprecated"
  ([Forum-Announcement](https://discuss.dev.twitch.com/t/deprecation-of-chat-commands-through-irc/40486)).
  Das *Datum* stammt aus dem Forum-Thread, nicht aus einer geladenen Doku-Seite — insofern das
  Datum **leicht unbestätigt**, der Sachverhalt klar.
- **Lesen per PRIVMSG ist unangetastet.** Weder Product Lifecycle noch Changelog noch IRC Concepts
  enthalten dazu eine Deprecation. **Belegt.**

**Wortlaut der Empfehlung**, konsequent „recommended", nie „deprecated", nie „will be removed":

- [Chat-Übersicht](https://dev.twitch.tv/docs/chat/): „The **preferred method** of viewing and
  sending chats on Twitch is through EventSub and Twitch API."
- [IRC Concepts](https://dev.twitch.tv/docs/chat/irc/): „Twitch IRC has some limitations versus
  EventSub, and is more complicated to parse, so it is **recommended** that you use EventSub
  subscriptions and API calls instead."
- [irc-migration](https://dev.twitch.tv/docs/chat/irc-migration/): „it is **recommended** to upgrade
  your chatbots".

---

## 4. Passung zur bestehenden Architektur

### 4.1 Was bliebe, was fiele weg, was käme neu

| Baustein | Schicksal |
|---|---|
| `TwitchChatManager` (Transport) | **ersetzt** — WebSocket + Helix statt TwitchLib.Client |
| `_desiredChannels` (Desired State) | **bleibt konzeptionell**, wandert in eine Registry nach 7TV-Vorbild |
| 600-ms-Join-Gate | **bleibt**, wird zum Request-Spacing für `POST /helix/eventsub/subscriptions` (Rate-Limit ist identisch) |
| `ReconnectPolicy` (Reconnect/Recreate/Wait) | **entfällt ersatzlos** — behandelt reine TwitchLib-Objektpathologie |
| `TwitchConnectionWatchdog` | **entfällt weitgehend** — der Keepalive-Timeout ist der Watchdog (wie bei 7TV: Receive-Timeout = `CancellationTokenSource.CancelAfter`) |
| `IEmoteMatchCache`, `IEmoteUsageCounter`, `UsageFlushWorker`, `UsageStat` | **unberührt**. Kein Datenmodell-Änderungsbedarf. |
| `WorkerHealthPublisher` / `worker:health:twitch` | **bleibt**, Semantik von „IRC verbunden" zu „EventSub-Session aktiv + N/M Subs bestätigt" |
| Twitch-Auth im Worker | **komplett neu** — heute existiert dort nichts |
| Backoff | neu, aber praktisch `SevenTvBackoffPolicy` kopiert |

Ein Detail zum Datenmodell: `Channel.TwitchChannelId` ist **bereits für jeden gesyncten Channel
befüllt** (über 7TVs GraphQL-Nutzersuche, nicht über Helix). Die numerische Broadcaster-ID, die
EventSub als `condition.broadcaster_user_id` braucht, liegt also schon in der DB — keine Migration,
kein Backfill.

Aber: EventSub liefert `broadcaster_user_login` im Event mit, und `EmoteMatchCache` ist auf
Channel-**Login** gekeyt. Der Match-Pfad bräuchte also nicht einmal ein ID→Login-Mapping zur
Laufzeit. **Der Eingriff endet exakt an der Klassengrenze `TwitchChatManager`.**

### 4.2 Vergleich mit dem 7TV-EventAPI-Client — das stärkste Argument für die Machbarkeit

Unser 7TV-Transport ist ~1.100 Zeilen, davon ~310 rein und getestet:

| Klasse | Zeilen | Art |
|---|---|---|
| `SevenTvEventClient` | 582 | Transport, ein `ClientWebSocket` |
| `SevenTvDispatchParser` | 189 | pur, statisch, getestet |
| `SevenTvSubscriptionRegistry` | 154 | pur, getestet — Desired State + Ack-Tracking |
| `SevenTvBackoffPolicy` | 88 | pur, getestet |
| `SevenTvEventWorker` | 67 | Hosted Service, dünn |

Die Formen sind bemerkenswert ähnlich — beide sind „ein Socket, ein Willkommens-Frame mit
Session-ID und Keepalive-Intervall, Subscriptions pro Ziel, serverinitiierte Reconnects, kein
Replay":

| 7TV EventAPI | Twitch EventSub WS | Übertragbarkeit |
|---|---|---|
| `Hello` (op 1) mit `heartbeat_interval`, `subscription_limit` | `session_welcome` mit `session_id`, `keepalive_timeout_seconds` | 1:1 |
| Heartbeat-Watchdog als Receive-Timeout (3 × Intervall) | `session_keepalive` | 1:1 |
| `registry.ResetAcknowledgements()` nach jedem Hello | Subscriptions sind nach Disconnect *disabled* → gleiche Notwendigkeit | 1:1 |
| Gap-Filling: REST-Vollsync nach erster Konvergenz | kein Replay → gleiche Notwendigkeit | 1:1 |
| op 4/7 Server-Reconnect | `session_reconnect` **mit `reconnect_url`** | **ein Schritt mehr** |
| Close 4009 `AlreadySubscribed` → 60 s Mindestwartezeit | Close 4000–4007 | analog |
| `SendPumpAsync`: Diff gegen Desired State, Frames über den Socket, 100 ms Abstand | **Subscriptions gehen NICHT über den Socket**, sondern per `POST /helix/eventsub/subscriptions` mit der `session_id` im Transport-Objekt | **Struktureller Unterschied** |

Die zwei Unterschiede im Klartext:

1. **`session_reconnect` verlangt kurzzeitig zwei Sockets.** Unsere aktuelle „ein Socket pro
   Session"-Schleife (`RunSessionAsync` mit `finally`-Abräumen) müsste die neue Verbindung öffnen
   und erst nach deren `session_welcome` die alte schließen. Das ist ein echter, aber gut
   umgrenzter Umbau der Session-Schleife — und Twitch gibt 30 Sekunden dafür.
2. **Die Send-Pump wird zur HTTP-Konvergenzschleife.** Statt Frames zu senden, feuert sie
   Helix-Requests. Das `_syncSignal`-Muster (unbounded `Channel<bool>`, Burst-Kollaps, Diff,
   Frame-Spacing → Request-Spacing) bleibt **unverändert**, und
   `BuildDesiredSubscriptions()`/`ResetAcknowledgements()` sind wortgleich weiterverwendbar. Neu
   ist nur, dass jeder Subscribe ein Token braucht und fehlschlagen kann (401/429).

**Bewertung:** Das Muster überträgt zu vielleicht 80 %. `SevenTvBackoffPolicy` wäre praktisch
kopierbar, die Registry im Vertrag identisch. Das senkt den Aufwand erheblich — und es ist das
stärkste Argument *für* die Machbarkeit. Es ist aber kein Argument für die **Notwendigkeit**; siehe
Abschnitt 3.4 und 3.5.

### 4.3 Schichtentreue und Bibliothek

- **Core:** `ITwitchChatManager` bleibt als Interface bestehen (unveränderter Vertrag nach außen:
  `EnsureJoinedAsync`, `LeaveChannelAsync`, `IsConnected`, `LastMessageReceivedUtc`). Neu: DTOs für
  die EventSub-Payload als reine `record`s in `Core/Twitch/`.
- **Infrastructure:** Erweiterung von `ITwitchHelixClient` um `CreateEventSubSubscriptionAsync` /
  `DeleteEventSubSubscriptionAsync` — passt exakt ins bestehende Muster (typisierter `HttpClient`,
  `Client-Id`-Header schon gesetzt). ~~**Neu und heute nirgends vorhanden: ein
  Client-Credentials-Flow.** Grep über `src/` nach `client_credentials`/`AppAccessToken` liefert
  null Treffer; die einzigen Grant-Types sind `authorization_code` und `refresh_token`.~~ Das ist
  bereits einmal aufgeschlagen: Am 2026-07-27 wurde eine Helix-„Get Streams"-Vorabprüfung im
  Watchdog u. a. genau deshalb verworfen ([DECISIONS.md](DECISIONS.md), Eintrag
  „`TwitchConnectionWatchdog`: Cooldown…").
  **Korrektur 2026-09-15 (#125):** Der durchgestrichene Satz stimmt seit dem 2026-08-03 nicht mehr.
  `ITwitchAuthClient.GetAppAccessTokenAsync` (`2faba66`) implementiert `grant_type=client_credentials`
  in `TwitchAuthClient.cs`, gecacht per Single-Flight in `TwitchAppTokenProvider.cs`; der Worker
  ruft ihn bereits in `TwitchLivePollWorker.cs` und `TwitchConnectionWatchdog.cs` auf. Der 2026-07-27
  verworfene Blocker existiert also nicht mehr — dieser Punkt spricht nicht länger gegen Pfad 2.
- **Worker:** neuer Hosted Service `TwitchEventSubWorker` nach dem Vorbild von `SevenTvEventWorker`,
  plus `TwitchEventSubClient` / `TwitchEventSubRegistry` / `TwitchEventSubBackoffPolicy` unter
  `Worker/Twitch/`. Die reinen Policies bekommen Tests im container-freien
  `tests/EmotePurge.Worker.Tests` (Regel 11), der Transport wird live verifiziert (Regel 16).
- **Bot-Token-Speicherung:** Der Worker müsste einen langlebigen User-Token besitzen. Die
  Verschlüsselung existiert (`AesGcmTokenCipher`, AES-256-GCM), das Refresh-Muster existiert
  (`TwitchUserTokenService` mit Single-Flight und Validate-on-Use) — aber beides hängt heute an
  `User`-Entitäten und läuft in der **Api**. Ein Bot-Token ist kein Nutzer-Token; es bräuchte
  entweder eine eigene Speicherform oder einen Pseudo-User. **Das ist die unangenehmste offene
  Designfrage.**

**Bibliothek:** `TwitchLib.EventSub.Websockets` **0.8.0** (2025-11-22), 141,7K Downloads,
Target-Frameworks .NET 8/9/10, letztes Repo-Update 2026-06-30, 3 offene Issues (alle aus 2024).
`channel.chat.message` wird unterstützt — README zeigt
`CreateEventSubSubscriptionAsync("channel.chat.message", "1", …)` und einen Handler
`OnChannelChatMessage`. **Einschränkung:** Die Paketbeschreibung deklariert das Paket weiterhin als
„open beta", vor 1.0.0 sind Breaking Changes ohne Vorankündigung möglich.

Der Pflegezustand der Organisation ist gesund und der EventSub-Zweig ist der aktive Schwerpunkt
(Updates bis 2026-06-30); `TwitchLib.Client` selbst wurde zuletzt 2025-12-23 aktualisiert, ist also
**nicht verwaist**. `TwitchLib.PubSub` ist archiviert — konsistent mit Twitchs eigener Abkündigung.

Alternativen: `Twitch.EventSub.Websocket` (GimliCZ, 3.0.2 von 2026-03-31) ist aktuell, aber ein
Ein-Personen-Projekt mit 2 GitHub-Stars, und `channel.chat.message`-Support ist im README **nicht
belegt** (dokumentiert nur `ChannelFollow`) — **unbestätigt**. `MiniTwitch` hat laut README
**kein** EventSub. `Twitch.Net.EventSub` steht bei 0.0.2.

**Eigenbau vs. Bibliothek:** Nach der 7TV-Erfahrung spricht mehr für den Eigenbau. Wir haben das
Muster bereits, es ist getestet, und der 7TV-Transport hat gezeigt, dass die schwierigen Teile
(Subscription-Konvergenz, Ack-Tracking, Gap-Filling) genau die sind, die eine generische Bibliothek
*nicht* abnimmt. TwitchLib.EventSub.Websockets würde uns die Frame-Deserialisierung sparen — und
uns dafür wieder an Library-Reconnect-Verhalten binden, das uns 2026-07-26/27 zwei Produktionsausfälle
gekostet hat.

### 4.4 Webhook-Topologie gegen unsere nginx-Konfiguration

**Korrektur 2026-09-15 (#125):** Dieser ganze Abschnitt gilt nur, wenn Conduit-Shards per Webhook
laufen. Sie können stattdessen per WebSocket laufen (s. Korrektur in 3.3) — dann entfällt jede
nginx-Berührung, weil es keinen eingehenden Endpunkt gibt. Als Belastbarkeitsprobe für den
Webhook-Zweig bleibt der Abschnitt trotzdem gültig.

Aus der damaligen `VPS-Reverse-Proxy.md` (inzwischen nach `infra-docs` ausgelagert, s. DECISIONS 2026-09-10): **genau eine** `location /` →
`proxy_pass http://127.0.0.1:4300/`. Ein `POST /api/twitch/eventsub/webhook` wäre also **ohne jede
nginx-Änderung erreichbar**, TLS terminiert, `X-Forwarded-*` gesetzt. Das eingehende
WebSocket-Upgrade fehlt zwar — betrifft aber nur *eingehende* Verbindungen; unsere ausgehenden
(Worker → Twitch/7TV) sieht der Proxy nie.

Drei konkrete Stolpersteine, alle belegbar:

1. **Die geteilte Rate-Zone ist ein echter Blocker.**
   `limit_req_zone $binary_remote_addr zone=general:10m rate=10r/s` mit `burst=20 nodelay` gilt für
   **alles** und ist **mit anderen vHosts geteilt**. Twitchs Zustellung kommt aus wenigen Quell-IPs
   und burstet bei Chat-Nachrichten weit über 10 r/s. Überschreitung liefert bei uns **503** — was
   Twitch als Zustellfehler wertet und nach genügend Fehlversuchen zur Revocation der Subscription
   führt. Es bräuchte zwingend einen eigenen `location = /api/twitch/eventsub/webhook`-Block mit
   eigener Zone oder `limit_req off`. Das ist die einzige *notwendige* nginx-Änderung — aber an
   einer Datei, die nicht im Repo liegt und andere vHosts mitbedient.
2. `client_max_body_size` steht nirgends ⇒ nginx-Default 1 MB. Für Chat-Payloads unkritisch, aber
   ungeprüft.
3. Die HMAC-Prüfung braucht den **rohen** Body ⇒ `EnableBuffering()` vor dem Model-Binding. Eine
   App-seitige Sonderbehandlung, die es im Projekt bisher nirgends gibt.

~~**Der eigentliche Blocker ist aber nicht nginx, sondern der Client-Credentials-Flow aus 4.3.**~~
**Korrektur 2026-09-15 (#125):** Der Client-Credentials-Flow existiert inzwischen (s. Korrektur zu
4.3), und der WebSocket-Zweig eines Conduits braucht diesen ganzen Abschnitt gar nicht erst. Ein
Für den *Webhook*-Zweig bleiben die drei Stolpersteine oben unverändert bestehen.

---

## 5. Bewertung

### 5.1 Vorteile

| | Bewertung |
|---|---|
| **Zukunftssicherheit** | Real, aber diffus. Neue Chat-Funktionalität entsteht nur noch in EventSub. Der konkrete Gewinn für *uns* ist null, weil wir aus der Nachricht nur Text lesen. |
| **Weg vom undokumentierten `justinfan`** | **Der stärkste Vorteil.** Wir würden ein geduldetes, nirgends zugesichertes Verhalten gegen einen dokumentierten Vertrag tauschen. |
| **Kein `session_reconnect`-Blindflug** | Twitch kündigt den Verbindungswechsel 30 s vorher an — 7TV trennt hart. Angenehmer als unser 7TV-Transport. |
| **Sauberere Payload** | Geparste `fragments` statt Positions-Offsets. Für uns **irrelevant** (nur `text` wird gebraucht). |
| **Weg von TwitchLib.Client** | Real: die Reconnect-Pathologie der Library hat uns zwei Produktionsausfälle gekostet (2026-07-26 45+ min, 2026-07-27) und `ReconnectPolicy` existiert nur, um sie zu behandeln. |
| **Skalierung** | **Nur auf Pfad 2** (App-Token + `channel:bot`). Auf Pfad 1: kein Gewinn, s. 3.4. |
| **Conduit-Resilienz** | Real und attraktiv: Subscriptions überleben einen Totalausfall bis 72 h. Aber nur auf Pfad 2. **Korrektur 2026-09-15 (#125):** günstiger als unten in 5.2 dargestellt — der Webhook-Aufwand aus 4.4 entfällt beim WebSocket-Zweig eines Conduits vollständig, s. Korrektur zu 3.3. |

### 5.2 Risiken und Kosten

| | Bewertung |
|---|---|
| **Auth-Zwang** | **Der Killer.** Der anonyme Betrieb endet definitiv. Es gibt keinen Zwischenweg. |
| **Bot-Account-Betrieb** | Neuer Dauerbetriebs-Gegenstand: Account, Token, Refresh, Ausfallmodus „Token revoked" ⇒ *alle* Channels tot. Heute gibt es keinen einzigen Ausfallmodus dieser Art. |
| **Token im Worker** | Der Worker hat heute **keine** Twitch-Konfiguration. Es bräuchte Speicherform, Verschlüsselungsschlüssel und Refresh — die vorhandene Mechanik hängt an `User`-Entitäten in der Api. |
| **Neuer Client-Credentials-Flow** (nur Pfad 2) | ~~Existiert im Repo nirgends; wurde 2026-07-27 schon einmal als zu teuer verworfen.~~ **Korrektur 2026-09-15 (#125):** existiert seit dem 2026-08-03 (`2faba66`, s. Korrektur zu 4.3) und ist bereits im Einsatz — entfällt als Kostenpunkt. |
| **Concurrent-Join-Limit 100** | Auf Pfad 1 **schlechter als heute vermutet**, weil wir es bisher nicht auf dem Schirm hatten. Auf Pfad 2 entfällt es. |
| **Reconnect-Komplexität** | Zwei-Socket-Übergang bei `session_reconnect`; Subscriptions per HTTP statt per Frame ⇒ neue Fehlerklassen (401 mitten in der Konvergenz, 429 auf Helix). |
| **Migrationsaufwand** | ~4 Wellen, s. 7. Nicht klein, aber durch das 7TV-Muster gedeckelt. |
| **Neue Ausfallmodi** | Token-Revocation · Helix-429 während der Konvergenz · Subscription-Revocation durch Zustellfehler (Webhook) · nginx-503 durch geteilte Rate-Zone (Webhook). |
| **Doppelzählung im Parallelbetrieb** | Lösbar, aber nicht durch ein globales Flag — s. 6.2. |
| **Bibliotheks-Beta** | `TwitchLib.EventSub.Websockets` ist erklärtermaßen „open beta". Bei Eigenbau irrelevant. |
| **Broadcaster-Zustimmung** (nur Pfad 2) | Jeder Kanal braucht einen OAuth-Durchlauf des Eigentümers. Für den Zielfall HandOfBlood, wo *Mods* das Werkzeug nutzen wollen, ist das eine echte Hürde — der Broadcaster muss mitmachen. |

### 5.3 Was den Ausschlag gibt

Der Vergleich ist nicht „IRC vs. EventSub", sondern **„anonym vs. autorisiert"**. Alle
substanziellen Vorteile hängen an Pfad 2, und Pfad 2 kostet die Zustimmung jedes einzelnen
Broadcasters. Pfad 1 tauscht einen funktionierenden anonymen Transport gegen einen
auth-pflichtigen mit **identischen** Limits und einem zusätzlichen Betriebsgegenstand.

Der einzige Vorteil, der auch auf Pfad 1 zählt, ist das Wegkommen von `justinfan` und von
TwitchLib.Client. Beides ist real — aber beides ist heute kein *Problem*, sondern ein *Risiko*.

---

## 6. Migrationspfad

### 6.1 Feature-Flag-Parallelbetrieb

**Ja, möglich** — und das Muster steht bereits: `SevenTv:EventApi:Enabled` wird einmal im
Konstruktor gelesen und an genau **einer** Stelle ausgewertet (`SevenTvEventWorker.ExecuteAsync`
loggt und returnt sofort). Der Rest der Pipeline läuft unverändert weiter, die Registry füllt sich
auch bei ausgeschaltetem Flag — es verbindet nur niemand. Ein `Twitch:EventSub:Enabled` würde
genauso funktionieren.

### 6.2 Doppelzählung verhindern — der nicht-triviale Teil

Beide Transporte würden denselben `IEmoteUsageCounter.Increment(emoteId)` aufrufen. Ein globales
Bool-Flag genügt deshalb **nicht** für einen echten Parallelbetrieb, sondern nur für ein
Entweder-Oder.

Drei Optionen, absteigend nach Empfehlung:

1. **Kanalweise Zuordnung (empfohlen).** Das Flag ist keine Bool, sondern eine Kanalliste bzw. ein
   Prozentsatz: `Twitch:EventSub:Channels = ["testchannel"]`. Ein Kanal wird von **genau einem**
   Transport gejoint — IRC überspringt ihn, EventSub abonniert ihn. Doppelzählung ist strukturell
   ausgeschlossen, echter A/B-Betrieb möglich, Rollback pro Kanal. Kostet: `EnsureJoinedAsync` und
   die EventSub-Konvergenz müssen dieselbe Zuordnungsfunktion konsultieren. **Zusätzlicher
   Vorteil:** Man kann exakt vergleichen, ob beide Transporte auf demselben Kanal dieselben Zahlen
   liefern — Kanal einen Tag auf IRC, den nächsten auf EventSub, `UsageStat` gegenüberstellen.
2. **Schattenbetrieb mit getrenntem Zähler.** EventSub läuft auf allen Kanälen mit, schreibt aber in
   einen separaten Zähler, der nur geloggt und nie geflusht wird. Gibt die beste Vergleichsbasis
   (identische Kanäle, identischer Zeitraum), kostet aber doppelte Verbindungen *und* die
   100-Kanal-Decke gilt für den EventSub-Teil sofort.
3. **Hartes Umschalten.** Billigste Implementierung, keine Vergleichsmöglichkeit, riskantester
   Rollback.

### 6.3 Wellen

| Welle | Inhalt | Live-Verifikation nötig (Regel 16) |
|---|---|---|
| **0 — Vorbereitung** | Bot-Account anlegen, Scope `user:read:chat` autorisieren, Token-Speicherform im Worker entscheiden (eigene Entität? `AesGcmTokenCipher` wiederverwenden?), Refresh-Pfad klären | Token-Ausstellung + Refresh gegen `id.twitch.tv` |
| **1 — Transport** | `TwitchEventSubClient` (Session-Schleife, Zwei-Socket-Übergang bei `session_reconnect`), `TwitchEventSubBackoffPolicy` (~kopiert), `TwitchEventSubRegistry` (~kopiert), Tests für die beiden puren Klassen | **`session_reconnect` live** — nicht simulierbar; Close-Code-Verhalten; Keepalive-Timeout |
| **2 — Subscriptions** | Helix-Client um `CreateEventSubSubscription`/`Delete` erweitern, HTTP-Konvergenzschleife mit 600-ms-Spacing, Ack/Fehlerbehandlung (401/429) | **Rate-Limit-Verhalten bei ~20 Kanälen**, 401-Verhalten mitten in der Konvergenz |
| **3 — Umschaltung** | Kanalweise Zuordnung (6.2), `Twitch:EventSub:*`-Config in Worker-`appsettings` + `.env.example`, Health-Semantik anpassen (`worker:health:twitch`), Admin-Anzeige | **Zählvergleich IRC vs. EventSub auf demselben Kanal** über mindestens 24 h |
| **4 — Rückbau** | `TwitchChatManager`, `ReconnectPolicy`, `TwitchConnectionWatchdog`, `TwitchLib.Client`/`.Communication` entfernen; `libgssapi-krb5-2` aus dem Worker-Dockerfile prüfen (wird es ohne `SslStream`-IRC noch gebraucht?) | Vollbetrieb über eine Woche |

Wellen 0–2 sind kein Wegwerf-Aufwand, falls man am Ende doch nicht umschaltet — aber sie sind auch
kein Gewinn ohne Welle 3.

Eine Welle 5 „App-Token + Conduits" wäre nochmals ein eigenständiges Projekt: Client-Credentials,
`channel:bot` im Broadcaster-Login-Scope (⇒ Scope-Drift ⇒ **erzwungener Re-Login für alle
bestehenden Nutzer**, weil `TwitchUserTokenService.ScopesDrifted` das so vorsieht), Conduit-/
Shard-Verwaltung, Webhook-Endpunkt inkl. nginx-Änderung. Das würde ich nicht in einem Zug mit 0–4
denken.

**Korrektur 2026-09-15 (#125):** Zwei der hier gebuchten Posten sind kleiner als dargestellt. Der
Client-Credentials-Flow existiert bereits (s. Korrektur zu 4.3) — er ist kein Aufwand mehr. Und
„Webhook-Endpunkt inkl. nginx-Änderung" ist nur nötig, wenn die Conduit-Shards per Webhook laufen;
per WebSocket (s. Korrektur zu 3.3) entfällt dieser Posten ganz. Offen bleiben aus dieser Liste
weiterhin die Scope-Drift/Re-Login-Frage und die Conduit-/Shard-Verwaltung selbst.

---

## 7. Empfehlung

**Nicht wechseln. Nicht jetzt, und nicht auf Pfad 1 überhaupt.**

Die Migration löst kein bestehendes Problem: Es gibt keine Deadline (IRC ist „Active" und bekam vor
zwei Wochen neue Features), und das Skalierungsproblem wird auf dem naheliegenden Pfad nicht besser
(dieselbe 100-Kanal-Decke, dieselbe Join-Rate — plus ein Bot-Account als neuer Ausfallpunkt).

### 7.1 Auslöser, bei denen neu zu bewerten ist

Konkret und beobachtbar, absteigend nach Wahrscheinlichkeit:

1. **Ab ~60 getrackten Kanälen** — 60 % der 100er-Decke. Ab hier ist der Puffer zu dünn, um auf eine
   Migration zu warten, die 3–4 Wellen dauert. *Das ist der wahrscheinlichste Auslöser.*
   **Wichtig:** Bei diesem Auslöser ist die richtige Antwort mit hoher Wahrscheinlichkeit **nicht**
   EventSub, sondern ein **verifizierter Bot-Account** — das war schon 2026-07-30 die vorgesehene
   Richtung, hebt beide Limits (2.000 JOINs/10 s), und funktioniert **auf IRC genauso**.
2. **Sobald ein Abschaltdatum für IRC in „Important Dates" auftaucht** — Twitch hat mit PubSub
   gezeigt, wie das aussieht (Datum + Migration Guide, Monate Vorlauf). Prüfbar durch
   gelegentliches Nachsehen im
   [Product Lifecycle](https://dev.twitch.tv/docs/product-lifecycle/).
3. **Sobald `justinfan` in Produktion Probleme macht** — gehäufte „Fatal network error" auf frischen
   Verbindungen ohne erklärbare Ursache, oder ein Kanal, auf dem der anonyme Join dauerhaft
   scheitert. Das wäre das Signal, dass die Duldung endet.
4. **Sobald ein Kanal Broadcaster-Zustimmung anbietet** — wenn HandOfBlood (oder ein anderer
   Zielkanal) bereit wäre, `channel:bot` zu erteilen, wird Pfad 2 auf einen Schlag attraktiv, weil
   der Kanal dann aus **beiden** Limits herausfällt. Das ist der einzige Weg, auf dem EventSub
   wirklich etwas gewinnt.

### 7.2 Was jetzt zu tun ist

Zwei Dinge, beide billig:

- **[CLAUDE.md](../CLAUDE.md) korrigieren**, Abschnitt „Bekannte offene Grenzen": Das
  Concurrent-Join-Limit von **100 Kanälen pro Account** (seit 2024-05-15) ergänzen und klarstellen,
  dass die Messung vom 2026-07-30 die *Rate* geprüft hat, nicht den *Bestand*. Ebenso: Das Limit
  gilt für EventSub identisch — die Migration ist also **kein** Ausweg, ein verifizierter Bot-Account
  schon. Das ist unabhängig von jeder Migrationsentscheidung ein Faktenfehler in der Doku.
- **Nichts weiter.** Kein Bot-Account auf Vorrat, keine Vorab-Implementierung. Die Wellen 0–2 sind
  ohne Welle 3 wertlos, und die Bibliotheks-/Doku-Lage ist stabil genug, dass eine Neubewertung in
  ein paar Monaten dieselben Antworten liefern wird — mit dem einen Unterschied, dass wir dann
  wissen, ob wir 20 oder 60 Kanäle tracken.

Falls die Entscheidung anders ausfällt: Die drei wertvollsten Vorarbeiten in dieser Reihenfolge sind
(a) die Token-Speicherform für den Worker klären, (b) `TwitchEventSubRegistry` +
`TwitchEventSubBackoffPolicy` aus den 7TV-Pendants ableiten (billig, testbar, ohne Twitch-Zugang
entwickelbar), (c) den Zwei-Socket-Übergang bei `session_reconnect` live gegen einen Testkanal
verifizieren — das ist der einzige Teil, den man nicht am Schreibtisch absichern kann.

---

## 8. Quellenverzeichnis

Alle abgerufen am **2026-08-01**.

| Thema | Quelle |
|---|---|
| `channel.chat.message` Subscription-Type | https://dev.twitch.tv/docs/eventsub/eventsub-subscription-types/#channelchatmessage |
| Event-Payload-Referenz | https://dev.twitch.tv/docs/eventsub/eventsub-reference/#channel-chat-message-event |
| Auth-Modell (User- vs. App-Token) | https://dev.twitch.tv/docs/chat/authenticating/ |
| Scope-Definitionen | https://dev.twitch.tv/docs/authentication/scopes/ |
| Chat-Übersicht + Rate/Concurrent-Join-Limits | https://dev.twitch.tv/docs/chat/ |
| IRC Concepts (Ports, `wss`, kein „justinfan") | https://dev.twitch.tv/docs/chat/irc/ |
| Migrating from Twitch IRC | https://dev.twitch.tv/docs/chat/irc-migration/ |
| WebSocket-Transport, Close-Codes, Reconnect | https://dev.twitch.tv/docs/eventsub/handling-websocket-events/ |
| Subscription-Limits und Kosten | https://dev.twitch.tv/docs/eventsub/manage-subscriptions/#subscription-limits |
| Webhook-Transport, HMAC, Replay-Fenster | https://dev.twitch.tv/docs/eventsub/handling-webhook-events/ |
| Conduits, Shards, 72-h-Regel | https://dev.twitch.tv/docs/eventsub/handling-conduit-events/ |
| Product Lifecycle („Chat (IRC): Active") | https://dev.twitch.tv/docs/product-lifecycle/ |
| Changelog (IRC-Updates 2026-07-17, 2026-06-18) | https://dev.twitch.tv/docs/changelog/ · https://dev.twitch.tv/docs/rss/change-log.xml |
| Deprecation der IRC-Chat-Commands (2023) | https://discuss.dev.twitch.com/t/deprecation-of-chat-commands-through-irc/40486 |
| `TwitchLib.EventSub.Websockets` 0.8.0 | https://www.nuget.org/packages/TwitchLib.EventSub.Websockets · https://github.com/TwitchLib/TwitchLib.EventSub.Websockets |
